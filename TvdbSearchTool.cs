using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace LLM_AI
{
    /// <summary>
    /// Outil d'enrichissement internet via TheTVDB.com (v4) : recherche une
    /// série et renvoie nom, synopsis (français en priorité), statut, année,
    /// note, etc. Portage C# in-process de l'outil PHP <c>tvdb_search</c>
    /// (qui fonctionne avec le LLM de l'utilisateur) — remplace la dépendance
    /// Redis par un cache de token en mémoire statique, et refera le login
    /// + réessayera sur 401 (le PHP se contentait d'effacer le token).
    /// Clé API lue dans <see cref="PluginConfiguration.TvdbApiKey"/> avec
    /// repli sur la variable d'environnement <c>TVDB_API_KEY</c> (comme le PHP).
    /// <para><b>Anti-spam TVDB :</b> deux caches statiques en mémoire — le
    /// token Bearer (TTL 23h) et les résultats de recherche par requête
    /// normalisée (TTL 23h, seules les réponses utiles sont cachées). Aucun
    /// appel réseau tant qu'une entrée valide existe en cache.</para>
    /// </summary>
    public class TvdbSearchTool : ILlmTool
    {
        private const string BaseUrl = "https://api4.thetvdb.com/v4";
        // TVDB : token valide ~24h ; on le cache 23h comme le PHP (82800s).
        private static readonly TimeSpan TokenTtl = TimeSpan.FromHours(23);

        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        // Cache du token Bearer (partagé entre appels ; la tâche planifiée
        // est ponctuelle, mais on protège quand même l'accès par un verrou).
        private static string s_token;
        private static DateTime s_tokenExpiry;
        private static readonly object s_tokenLock = new object();

        // Cache des RÉSULTATS de recherche (clé = requête normalisée) pour ne pas
        // re-spammer TVDB quand le LLM redemande la même série (au cours d'une
        // même exécution de la tâche, ou lors d'une relance manuelle rapprochée).
        // Seules les réponses utiles sont cachées (pas les erreurs transitoires).
        // TTL identique au token : les métadonnées d'une série changent rarement,
        // mais on finit par rafraîchir (statut, score).
        private static readonly TimeSpan SearchTtl = TimeSpan.FromHours(23);
        private static readonly Dictionary<string, (string result, DateTime expiry)> s_searchCache =
            new Dictionary<string, (string, DateTime)>();
        private static readonly object s_searchCacheLock = new object();

        private readonly ILogger _logger;

        public string Name => "tvdb_search";

        public string Description =>
            "Recherche une série sur TheTVDB.com et renvoie nom, synopsis (français en priorité), " +
            "statut, année, note TVDB, pays, réseau et image. Utilise-le quand le synopsis EPG est " +
            "vide ou pour confirmer le statut d'une série (en cours / terminée). Séries uniquement.";

        public string ArgumentsSchema => @"{
  ""query"": ""nom de la série à rechercher (obligatoire)""
}";

        public TvdbSearchTool(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
        {
            string key = ResolveApiKey();
            if (string.IsNullOrWhiteSpace(key))
                return Err("tvdb_search non configuré (TVDB_API_KEY / clé absente).");

            if (args.ValueKind != JsonValueKind.Object) args = default;
            string query = OptString(args, "query");
            if (string.IsNullOrWhiteSpace(query))
                return Err("paramètre 'query' requis pour tvdb_search");

            // Déjà vu ? On sert le cache sans aucun appel réseau vers TVDB.
            string cacheKey = NormKey(query);
            string cached = TryGetCached(cacheKey);
            if (cached != null)
            {
                _logger?.Info("[LLM_AI] tvdb_search (cache) query={0} -> {1}", query, Truncate(cached, 200));
                return cached;
            }

            try
            {
                string token = await GetTokenAsync(key, forceRefresh: false, ct).ConfigureAwait(false);
                string result = await SearchSeriesAsync(key, token, query, allowRetry: true, ct).ConfigureAwait(false);
                // On ne cache QUE les réponses utiles (les erreurs / « aucune série »
                // restent non cachées pour permettre une vraie retry au prochain appel).
                if (!result.Contains("\"error\":"))
                    TrySetCached(cacheKey, result);
                _logger?.Info("[LLM_AI] tvdb_search query={0} -> {1}", query, Truncate(result, 200));
                return result;
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("[LLM_AI] tvdb_search a levé : {0}", ex, ex.Message);
                return Err(ex.Message);
            }
        }

        // ------------------------------------------------------------------
        //  Authentification : POST /v4/login {apikey} -> data.token
        // ------------------------------------------------------------------

        private static async Task<string> GetTokenAsync(string key, bool forceRefresh, CancellationToken ct)
        {
            lock (s_tokenLock)
            {
                if (!forceRefresh && !string.IsNullOrEmpty(s_token) && DateTime.UtcNow < s_tokenExpiry)
                    return s_token;
            }

            var body = JsonSerializer.Serialize(new { apikey = key });
            using (var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/login"))
            {
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        throw new InvalidOperationException($"TVDB login HTTP {(int)resp.StatusCode}: {Truncate(text, 200)}");
                    using (var doc = JsonDocument.Parse(text))
                    {
                        string token = null;
                        if (doc.RootElement.TryGetProperty("data", out var data) &&
                            data.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String)
                            token = t.GetString();
                        if (string.IsNullOrWhiteSpace(token))
                            throw new InvalidOperationException("TVDB login : token non reçu");
                        lock (s_tokenLock)
                        {
                            s_token = token;
                            s_tokenExpiry = DateTime.UtcNow + TokenTtl;
                        }
                        return token;
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        //  Recherche : GET /v4/search?query=...&type=series&limit=5
        //                + GET /v4/series/{tvdb_id} (score + averageRuntime)
        // ------------------------------------------------------------------

        private async Task<string> SearchSeriesAsync(string key, string token, string query, bool allowRetry, CancellationToken ct)
        {
            var url = $"{BaseUrl}/search?query={UrlEnc(query)}&type=series&limit=5";
            var (status, text) = await GetAsync(url, token, ct).ConfigureAwait(false);

            if (status == System.Net.HttpStatusCode.Unauthorized)
            {
                // Token expiré : on efface, on re-login et on réessaie une fois.
                ClearToken();
                if (allowRetry)
                {
                    var fresh = await GetTokenAsync(key, forceRefresh: true, ct).ConfigureAwait(false);
                    return await SearchSeriesAsync(key, fresh, query, allowRetry: false, ct).ConfigureAwait(false);
                }
                return Err("Token TVDB expiré (réessayez).");
            }
            if (status != System.Net.HttpStatusCode.OK)
            {
                return Err($"TVDB search HTTP {(int)status}: {Truncate(text, 200)}");
            }

            JsonDocument search;
            try { search = JsonDocument.Parse(text); }
            catch { return Err("réponse TVDB non-JSON"); }

            if (!search.RootElement.TryGetProperty("data", out var dataArr) || dataArr.ValueKind != JsonValueKind.Array || dataArr.GetArrayLength() == 0)
                return Err($"aucune série TVDB pour : {query}");

            JsonElement best = default;
            int i = 0;
            foreach (var r in dataArr.EnumerateArray()) { if (i == 0) best = r; i++; }
            int totalResults = i;

            // Détail : /series/{tvdb_id} pour score + averageRuntime.
            double? tvdbScore = null;
            int? averageRuntime = null;
            long tvdbId = Int64N(best, "tvdb_id") ?? 0;
            if (tvdbId != 0)
            {
                var (s2, t2) = await GetAsync($"{BaseUrl}/series/{tvdbId}", token, ct).ConfigureAwait(false);
                if (s2 == System.Net.HttpStatusCode.Unauthorized)
                    ClearToken(); // le prochain appel refera le login
                else if (s2 == System.Net.HttpStatusCode.OK)
                {
                    try
                    {
                        using (var d = JsonDocument.Parse(t2))
                        {
                            if (d.RootElement.TryGetProperty("data", out var rec))
                            {
                                tvdbScore = Num(rec, "score");
                                averageRuntime = IntN(rec, "averageRuntime");
                            }
                        }
                    }
                    catch { /* détail optionnel : on continue sans */ }
                }
            }

            var overview = PickOverview(best);
            var output = new
            {
                name = Str(best, "name") ?? query,
                overview = overview,
                status = Str(best, "status") ?? "Unknown",
                year = IntN(best, "year"),
                first_air_time = Str(best, "first_air_time"),
                tvdb_id = tvdbId != 0 ? tvdbId : (long?)null,
                country = Str(best, "country"),
                network = Str(best, "network"),
                image_url = Str(best, "image_url"),
                slug = Str(best, "slug"),
                tvdb_score = tvdbScore,
                average_runtime = averageRuntime,
                total_results = totalResults
            };
            return JsonSerializer.Serialize(output, s_json);
        }

        /// <summary>
        /// Synopsis en français si disponible, sinon anglais, sinon le champ
        /// <c>overview</c> par défaut. TVDB v4 peut renvoyer <c>overviews</c>
        /// soit comme une map {lang: texte}, soit comme un tableau
        /// [{language, overview}] — on gère les deux formes (le PHP ne couvrait
        /// que la map).
        /// </summary>
        private static string PickOverview(JsonElement best)
        {
            if (best.TryGetProperty("overviews", out var ov))
            {
                if (ov.ValueKind == JsonValueKind.Object)
                {
                    if (ov.TryGetProperty("fra", out var f) && f.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(f.GetString())) return f.GetString();
                    if (ov.TryGetProperty("eng", out var e) && e.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(e.GetString())) return e.GetString();
                }
                else if (ov.ValueKind == JsonValueKind.Array)
                {
                    string fra = null, eng = null;
                    foreach (var o in ov.EnumerateArray())
                    {
                        var lang = Str(o, "language");
                        var txt = Str(o, "overview");
                        if (string.IsNullOrEmpty(txt)) continue;
                        if (lang == "fra" && fra == null) fra = txt;
                        if (lang == "eng" && eng == null) eng = txt;
                    }
                    if (fra != null) return fra;
                    if (eng != null) return eng;
                }
            }
            var def = Str(best, "overview");
            return string.IsNullOrEmpty(def) ? "N/A" : def;
        }

        // ------------------------------------------------------------------
        //  Helpers HTTP / JSON
        // ------------------------------------------------------------------

        private static async Task<(System.Net.HttpStatusCode status, string text)> GetAsync(string url, string token, CancellationToken ct)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return (resp.StatusCode, text);
                }
            }
        }

        private static void ClearToken()
        {
            lock (s_tokenLock) { s_token = null; s_tokenExpiry = DateTime.MinValue; }
        }

        // --- Cache des résultats de recherche ---------------------------------

        private static string TryGetCached(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            lock (s_searchCacheLock)
            {
                if (s_searchCache.TryGetValue(key, out var e))
                {
                    if (DateTime.UtcNow < e.expiry) return e.result;
                    s_searchCache.Remove(key); // expiré : on refera l'appel
                }
                return null;
            }
        }

        private static void TrySetCached(string key, string result)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(result)) return;
            lock (s_searchCacheLock)
            {
                s_searchCache[key] = (result, DateTime.UtcNow + SearchTtl);
            }
        }

        private static string NormKey(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            // Normalise espacements + casse : "Star  trek" et "Star Trek" → même clé.
            return string.Join(" ", s.Trim().ToLowerInvariant().Split(new[] { ' ', '\t', '\n', '\r' },
                StringSplitOptions.RemoveEmptyEntries));
        }

        private static string ResolveApiKey()
        {
            var cfg = Plugin.Instance?.Configuration;
            return !string.IsNullOrWhiteSpace(cfg?.TvdbApiKey)
                ? cfg.TvdbApiKey
                : Environment.GetEnvironmentVariable("TVDB_API_KEY");
        }

        private static readonly JsonSerializerOptions s_json = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private static string Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

        private static double? Num(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : (double?)null;

        private static int? IntN(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.TryGetInt32(out var v) ? v : (int?)null;

        private static long? Int64N(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var p)) return null;
            if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var v)) return v;
            if (p.ValueKind == JsonValueKind.String && long.TryParse(p.GetString(), out var vs)) return vs;
            return null;
        }

        private static string OptString(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object) return null;
            return e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        }

        private static string UrlEnc(string s) => Uri.EscapeDataString(s ?? string.Empty);

        private static string Err(string msg) => JsonSerializer.Serialize(new { error = msg }, s_json);

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s.Substring(0, max) + "…");
    }
}