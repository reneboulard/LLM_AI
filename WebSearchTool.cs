using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace LLM_AI
{
    /// <summary>
    /// Outil de recherche web. Backend configurable :
    /// <list type="bullet">
    /// <item><b>SearXNG</b> (auto-hébergé) si <see cref="PluginConfiguration.SearXngUrl"/>
    ///   est renseigné — privilégié (gratuit, sans quota, réponse JSON native).
    ///   Requête <c>{url}/search?q=…&amp;format=json</c>.</item>
    /// <item><b>Ollama Cloud</b> (<c>https://ollama.com/api/web_search</c>) en repli,
    ///   authentifié par bearer (<see cref="PluginConfiguration.OllamaApiKey"/> ou
    ///   variable <c>OLLAMA_API_KEY</c>).</item>
    /// </list>
    /// Lecture seule. Sert au LLM à récupérer des informations récentes non couvertes
    /// par TMDB (actu, sorties à venir, contexte).
    /// </summary>
    public class WebSearchTool : ILlmTool
    {
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        private readonly ILogger _logger;

        public string Name => "web_search";

        public string Description =>
            "Recherche sur le web. Retourne des résultats (titres, extraits, liens) " +
            "et, quand disponibles, des réponses instantanées / résumés (infobox). " +
            "Utilise-le pour des informations récentes non couvertes par tmdb_lookup " +
            "(actu, sorties à venir, contexte).";

        public string ArgumentsSchema => @"{
  ""query"": ""requête de recherche (obligatoire)""
}";

        public WebSearchTool(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
        {
            if (args.ValueKind != JsonValueKind.Object) args = default;
            string query = OptString(args, "query");
            if (string.IsNullOrWhiteSpace(query))
                return Err("paramètre 'query' requis pour web_search");

            // Cache 24h (une info de série ne bouge pas en 24h ; économise quota
            // Ollama cloud et allège l'instance SearXNG sur les requêtes identiques).
            string cacheKey = "search:" + WebResultCache.NormalizeQuery(query);
            if (WebResultCache.TryGet(cacheKey, out var cached))
            {
                _logger?.Info("[LLM_AI] web_search cache hit query={0}", query);
                return cached;
            }

            string searxng = ResolveSearXngUrl();
            try
            {
                string result;
                if (!string.IsNullOrWhiteSpace(searxng))
                    result = await SearchSearXng(searxng, query, ct).ConfigureAwait(false);
                else
                    result = await SearchOllamaCloud(query, ct).ConfigureAwait(false);

                // Ne cache QUE les résultats valids (pas les erreurs).
                bool valid = false;
                try { using (JsonDocument.Parse(result)) { valid = true; } } catch { }
                if (valid && !result.Contains("\"error\""))
                {
                    WebResultCache.Set(cacheKey, result);
                    _logger?.Info("[LLM_AI] web_search query={0} -> {1} (caché 24h)", query, Truncate(result, 200));
                }
                else
                    _logger?.Info("[LLM_AI] web_search query={0} -> {1} (non caché)", query, Truncate(result, 200));
                return result;
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("[LLM_AI] web_search a levé : {0}", ex, ex.Message);
                return Err(ex.Message);
            }
        }

        /// <summary>
        /// Recherche via une instance SearXNG auto-hébergée. L'endpoint
        /// <c>{baseUrl}/search?q=…&amp;format=json</c> renvoie un JSON contenant
        /// <c>results</c> (tableau de {title, content, url, publishedDate, score}),
        /// <c>answers</c> (réponses instantanées) et <c>infoboxes</c> (résumés).
        /// On projette un JSON compact pour le LLM (top 8 résultats par score).
        /// </summary>
        private async Task<string> SearchSearXng(string baseUrl, string query, CancellationToken ct)
        {
            string url = baseUrl.TrimEnd('/') + "/search?q=" + Uri.EscapeDataString(query) + "&format=json";
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
            {
                var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    return Err($"SearXNG HTTP {(int)resp.StatusCode}: {Truncate(text, 300)}");

                using (var doc = JsonDocument.Parse(text))
                {
                    var root = doc.RootElement;

                    // Réponses instantanées (SearXNG "answers").
                    var answers = new List<string>();
                    if (root.TryGetProperty("answers", out var ans) && ans.ValueKind == JsonValueKind.Array)
                        foreach (var a in ans.EnumerateArray())
                            if (a.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(a.GetString()))
                                answers.Add(a.GetString());

                    // Infoboxes : on concatène le contenu textuel du premier.
                    string infobox = "";
                    if (root.TryGetProperty("infoboxes", out var ib) && ib.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var b in ib.EnumerateArray())
                        {
                            string c = b.TryGetProperty("content", out var cEl) && cEl.ValueKind == JsonValueKind.String ? cEl.GetString() : null;
                            if (string.IsNullOrWhiteSpace(c) && b.TryGetProperty("infobox", out var iEl) && iEl.ValueKind == JsonValueKind.String)
                                c = iEl.GetString();
                            if (!string.IsNullOrWhiteSpace(c)) { infobox = c.Trim(); break; }
                        }
                    }

                    // Résultats : top 8 par score (SearXNG les renvoie déjà triés,
                    // mais on sécurise).
                    var results = new List<object>();
                    if (root.TryGetProperty("results", out var resEl) && resEl.ValueKind == JsonValueKind.Array)
                    {
                        var sorted = resEl.EnumerateArray()
                            .Where(r => r.TryGetProperty("url", out _) && r.TryGetProperty("title", out _))
                            .OrderByDescending(r => r.TryGetProperty("score", out var sc) && sc.TryGetDouble(out var d) ? d : 0)
                            .Take(8);
                        foreach (var r in sorted)
                        {
                            results.Add(new
                            {
                                title = r.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : "",
                                content = r.TryGetProperty("content", out var c2) && c2.ValueKind == JsonValueKind.String ? c2.GetString() : "",
                                url = r.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : "",
                                date = r.TryGetProperty("publishedDate", out var d2) && d2.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(d2.GetString()) ? d2.GetString() : null
                            });
                        }
                    }

                    return JsonSerializer.Serialize(new
                    {
                        backend = "searxng",
                        query,
                        answers = answers.Count > 0 ? answers : null,
                        infobox = string.IsNullOrWhiteSpace(infobox) ? null : infobox,
                        results
                    }, s_json);
                }
            }
        }

        /// <summary>Recherche via l'API cloud Ollama (chemin d'origine).</summary>
        private async Task<string> SearchOllamaCloud(string query, CancellationToken ct)
        {
            string key = ResolveApiKey();
            if (string.IsNullOrWhiteSpace(key))
                return Err("web_search non configuré (OLLAMA_API_KEY absente et SearXNG non renseigné).");

            var body = JsonSerializer.Serialize(new { query });
            using (var req = new HttpRequestMessage(HttpMethod.Post, "https://ollama.com/api/web_search"))
            {
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");

                using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        return Err($"Ollama Cloud HTTP {(int)resp.StatusCode}: {Truncate(text, 300)}");

                    // L'API renvoie directement le JSON des résultats. On valide
                    // qu'il est bien formé avant de le réinjecter dans la boucle.
                    try { using (JsonDocument.Parse(text)) { } }
                    catch { return JsonSerializer.Serialize(new { error = "réponse non-JSON", raw = Truncate(text, 500) }, s_json); }
                    return text;
                }
            }
        }

        private static string ResolveSearXngUrl()
        {
            var cfg = Plugin.Instance?.Configuration;
            return cfg?.SearXngUrl;
        }

        private static string ResolveApiKey()
        {
            var cfg = Plugin.Instance?.Configuration;
            return !string.IsNullOrWhiteSpace(cfg?.OllamaApiKey)
                ? cfg.OllamaApiKey
                : Environment.GetEnvironmentVariable("OLLAMA_API_KEY");
        }

        private static readonly JsonSerializerOptions s_json = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private static string OptString(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object) return null;
            return e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        }

        private static string Err(string msg) => JsonSerializer.Serialize(new { error = msg }, s_json);

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s.Substring(0, max) + "…");
    }
}