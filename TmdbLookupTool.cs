using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace LLM_AI
{
    /// <summary>
    /// Outil d'enrichissement internet via TMDB (themoviedb.org) : recherche un
    /// titre (série ou film) et renvoie synopsis, statut, note, genres et poster.
    /// Sert au LLM quand le synopsis EPG est vide ou insuffisant. Lecture seule.
    /// Clé API lue dans <see cref="PluginConfiguration.TmdbApiKey"/> via
    /// <see cref="Plugin.Instance"/>. Si la clé est absente, l'outil renvoie
    /// une erreur JSON (il ne doit pas casser la boucle de l'agent).
    /// </summary>
    public class TmdbLookupTool : ILlmTool
    {
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        private readonly ILogger _logger;

        public string Name => "tmdb_lookup";

        public string Description =>
            "Recherche un titre sur TMDB (themoviedb.org) et renvoie synopsis, statut, " +
            "note, genres et poster. Utilise-le quand le synopsis EPG est vide ou pour " +
            "confirmer le statut d'une série (en cours / terminée).";

        public string ArgumentsSchema => @"{
  ""query"": ""titre à rechercher (obligatoire)"",
  ""kind"": ""series | movie (défaut: series)"",
  ""year"": ""année de diffusion/sortie (optionnel, affine la recherche)""
}";

        public TmdbLookupTool(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
        {
            var cfg = Plugin.Instance?.Configuration;
            if (string.IsNullOrWhiteSpace(cfg?.TmdbApiKey))
                return Err("TMDB non configuré (clé API absente).");

            if (args.ValueKind != JsonValueKind.Object) args = default;
            string query = OptString(args, "query");
            if (string.IsNullOrWhiteSpace(query))
                return Err("paramètre 'query' requis pour tmdb_lookup");

            string kind = (OptString(args, "kind") ?? "series").ToLowerInvariant();
            int? year = OptInt(args, "year");

            // Langue TMDB = langue des métadonnées de l'usager (ResponseLanguage →
            // langue d'affichage Emby → legacy TmdbLanguage → en-US). host null :
            // « Auto » retombe sur TmdbLanguage/en (ResolveMetaLangKey gère la
            // précédence). L'agent LLM adaptera lui-même la langue dans son
            // raisonnement — on ne traduit pas ici.
            string userTmdb = I18n.ToTmdbLang(I18n.ResolveMetaLangKey(cfg, null));

            try
            {
                string json = await FetchWithCacheAsync(query, kind, year, userTmdb, ct).ConfigureAwait(false);

                // Tier 2 : si le synopsis est absent dans la langue de l'usager
                // et que celle-ci n'est pas l'anglais, on retente en en-US (repli
                // synopsis). Best-effort : on renvoie le JSON en-US s'il a un
                // synopsis, sinon le JSON original (éventuellement un {error}).
                if (!string.Equals(userTmdb, "en-US", StringComparison.OrdinalIgnoreCase))
                {
                    var meta = ParseMeta(json);
                    if (meta != null && string.IsNullOrWhiteSpace(meta.Overview))
                    {
                        string jsonEn = await FetchWithCacheAsync(query, kind, year, "en-US", ct).ConfigureAwait(false);
                        var metaEn = ParseMeta(jsonEn);
                        if (metaEn != null && !string.IsNullOrWhiteSpace(metaEn.Overview))
                            return jsonEn;
                    }
                }
                return json;
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("[LLM_AI] tmdb_lookup a levé : {0}", ex, ex.Message);
                return Err(ex.Message);
            }
        }

        /// <summary>
        /// Recherche TMDB avec cache 24h (WebResultCache) <b>partagé avec l'agent
        /// LLM</b> : si le titre a déjà été cherché pendant le run (par l'agent
        /// via <see cref="ExecuteAsync"/> ou par la génération .strm via
        /// <see cref="LookupMetaAsync"/>), on sert le résultat mis en cache — un
        /// seul appel TMDB par titre et par fenêtre de 24h. Retourne le JSON
        /// contractuel (kind/title/overview/year/rating/status/genres/poster_url)
        /// ou un JSON d'erreur. Ne lève pas (erreurs renvoyées en JSON).
        /// <para><paramref name="lang"/> = code langue TMDB demandé par l'appelant
        /// (ex. « fr-FR », « en-US »), résolu depuis <see cref="I18n.ResolveMetaLangKey"/>.
        /// Vide/null → repli legacy sur <see cref="PluginConfiguration.TmdbLanguage"/>,
        /// puis « en-US ». La clé de cache inclut la langue : deux langues distinctes
        /// pour un même titre sont mises en cache séparément.</para>
        /// </summary>
        internal async Task<string> FetchWithCacheAsync(string query, string kind, int? year, string lang, CancellationToken ct)
        {
            var cfg = Plugin.Instance?.Configuration;
            if (string.IsNullOrWhiteSpace(cfg?.TmdbApiKey))
                return Err("TMDB non configuré (clé API absente).");

            // Langue demandée par l'appelant (résolue depuis ResponseLanguage /
            // langue d'affichage). Repli legacy sur cfg.TmdbLanguage, puis en-US.
            if (string.IsNullOrWhiteSpace(lang))
                lang = string.IsNullOrWhiteSpace(cfg.TmdbLanguage) ? "en-US" : cfg.TmdbLanguage;
            string key = cfg.TmdbApiKey;

            // La clé inclut kind/lang/year : une même requête avec un kind ou une
            // année différents renvoie des résultats différents.
            string cacheKey = "tmdb:" + kind + ":" + lang + ":" +
                              WebResultCache.NormalizeQuery(query) + ":" + (year?.ToString() ?? "");
            if (WebResultCache.TryGet(cacheKey, out var cached))
            {
                _logger?.Info("[LLM_AI] tmdb_lookup cache hit query={0} kind={1}", query, kind);
                return cached;
            }

            string result = kind == "movie"
                ? await LookupMovieAsync(key, lang, query, year, ct).ConfigureAwait(false)
                : await LookupSeriesAsync(key, lang, query, year, ct).ConfigureAwait(false);

            // Ne cache QUE les résultats valides (pas les erreurs / « aucune
            // série »), comme web_search.
            bool valid = false;
            try { using (JsonDocument.Parse(result)) { valid = true; } } catch { }
            if (valid && !result.Contains("\"error\""))
            {
                WebResultCache.Set(cacheKey, result);
                _logger?.Info("[LLM_AI] tmdb_lookup query={0} kind={1} -> {2} (caché 24h)",
                    query, kind, Truncate(result, 200));
            }
            else
                _logger?.Info("[LLM_AI] tmdb_lookup query={0} kind={1} -> {2} (non caché)",
                    query, kind, Truncate(result, 200));
            return result;
        }

        /// <summary>
        /// Recherche TMDB <b>structurée</b> pour la génération de cartes .strm
        /// (<see cref="StrmLibraryGenerator"/>) : retourne un <see cref="TmdbMeta"/>
        /// (overview/note/genres/année/poster) ou <c>null</c> si pas de match ou
        /// erreur. Réutilise le cache 24h de <see cref="FetchWithCacheAsync"/>
        /// (donc le LLM et le générateur .strm partagent leurs lookups).
        /// <para><paramref name="lang"/> = code langue TMDB demandé (résolu par
        /// l'appelant via <see cref="I18n.ToTmdbLang"/>). Vide/null → repli legacy
        /// <see cref="PluginConfiguration.TmdbLanguage"/> / en-US.</para>
        /// </summary>
        internal async Task<TmdbMeta> LookupMetaAsync(string query, string kind, int? year, string lang, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(query)) return null;
            string json = await FetchWithCacheAsync(query, kind ?? "series", year, lang, ct).ConfigureAwait(false);
            return ParseMeta(json);
        }

        /// <summary>
        /// Analyse le JSON contractuel de <see cref="FetchWithCacheAsync"/> en un
        /// <see cref="TmdbMeta"/>. Null si JSON vide, non-objet, ou portant un
        /// champ <c>error</c> (pas de match TMDB). Tolérant aux champs absents.
        /// </summary>
        internal static TmdbMeta ParseMeta(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    var r = doc.RootElement;
                    if (r.ValueKind != JsonValueKind.Object) return null;
                    if (r.TryGetProperty("error", out _)) return null;
                    var m = new TmdbMeta
                    {
                        Kind = Str(r, "kind"),
                        Title = Str(r, "title"),
                        Overview = Str(r, "overview"),
                        Year = IntN(r, "year"),
                        Rating = Num(r, "rating"),
                        Status = Str(r, "status"),
                        Genres = GenreArr(r),
                        PosterUrl = Str(r, "poster_url")
                    };
                    if (r.TryGetProperty("seasons", out var s) && s.TryGetInt32(out var sv)) m.Seasons = sv;
                    if (r.TryGetProperty("runtime", out var rt) && rt.TryGetInt32(out var rtv)) m.Runtime = rtv;
                    if (r.TryGetProperty("tmdb_id", out var tid) && tid.TryGetInt32(out var tidv)) m.TmdbId = tidv;
                    m.ImdbId = Str(r, "imdb_id");
                    m.TvdbId = Str(r, "tvdb_id");
                    return m;
                }
            }
            catch { return null; }
        }

        /// <summary>
        /// Extrait le tableau <c>genres</c> du JSON (tableau de chaînes sérialisé
        /// depuis <see cref="TmdbMeta.Genres"/>). Null si absent ou vide.
        /// </summary>
        private static string[] GenreArr(JsonElement e)
        {
            if (!e.TryGetProperty("genres", out var g) || g.ValueKind != JsonValueKind.Array) return null;
            var list = new System.Collections.Generic.List<string>();
            foreach (var x in g.EnumerateArray())
            {
                string n = x.ValueKind == JsonValueKind.String ? x.GetString() : null;
                if (!string.IsNullOrEmpty(n)) list.Add(n);
            }
            return list.Count == 0 ? null : list.ToArray();
        }

        // ------------------------------------------------------------------
        //  Recherche série : /search/tv puis /tv/{id} (détails + statut)
        // ------------------------------------------------------------------

        private async Task<string> LookupSeriesAsync(string key, string lang, string query, int? year, CancellationToken ct)
        {
            var sb = new StringBuilder("https://api.themoviedb.org/3/search/tv?api_key=").Append(UrlEnc(key));
            sb.Append("&language=").Append(UrlEnc(lang));
            sb.Append("&query=").Append(UrlEnc(query));
            if (year.HasValue) sb.Append("&first_air_date_year=").Append(year.Value);

            var search = await GetJsonAsync(sb.ToString(), ct).ConfigureAwait(false);
            var first = PickFirstResult(search, year, "first_air_date");
            if (first == null) return Err($"aucune série TMDB pour : {query}");

            int id = first.Value.TryGetProperty("id", out var idp) ? idp.GetInt32() : 0;
            if (id == 0) return Err("réponse TMDB sans id");

            return await FetchDetailAsync(key, id, "series", lang, ct).ConfigureAwait(false);
        }

        // ------------------------------------------------------------------
        //  Recherche film : /search/movie puis /movie/{id} (genres)
        // ------------------------------------------------------------------

        private async Task<string> LookupMovieAsync(string key, string lang, string query, int? year, CancellationToken ct)
        {
            var sb = new StringBuilder("https://api.themoviedb.org/3/search/movie?api_key=").Append(UrlEnc(key));
            sb.Append("&language=").Append(UrlEnc(lang));
            sb.Append("&query=").Append(UrlEnc(query));
            if (year.HasValue) sb.Append("&primary_release_year=").Append(year.Value);

            var search = await GetJsonAsync(sb.ToString(), ct).ConfigureAwait(false);
            var first = PickFirstResult(search, year, "release_date");
            if (first == null) return Err($"aucun film TMDB pour : {query}");

            int id = first.Value.TryGetProperty("id", out var idp) ? idp.GetInt32() : 0;
            if (id == 0) return Err("réponse TMDB sans id");

            return await FetchDetailAsync(key, id, "movie", lang, ct).ConfigureAwait(false);
        }

        // ------------------------------------------------------------------
        //  Détail par id TMDB (/movie|tv/{id} + external_ids) — facteur commun
        //  de la recherche par titre (LookupMovie/SeriesAsync) et de la
        //  résolution par id externe (FindByExternalIdAsync). Non mis en cache
        //  (le cache est géré par FetchWithCacheAsync au niveau recherche).
        // ------------------------------------------------------------------

        private async Task<string> FetchDetailAsync(string key, int id, string kind, string lang, CancellationToken ct)
        {
            string path = kind == "movie" ? "movie" : "tv";
            var det = await GetJsonAsync(
                $"https://api.themoviedb.org/3/{path}/{id}?api_key={UrlEnc(key)}&language={UrlEnc(lang)}&append_to_response=external_ids", ct)
                .ConfigureAwait(false);
            var d = det.RootElement;

            if (kind == "movie")
            {
                return JsonSerializer.Serialize(new
                {
                    kind = "movie",
                    tmdb_id = id,
                    imdb_id = ExtId(d, "imdb_id"),
                    title = Str(d, "title"),
                    overview = Str(d, "overview"),
                    year = YearOf(Str(d, "release_date")),
                    rating = Num(d, "vote_average"),
                    status = Str(d, "status"),
                    runtime = IntN(d, "runtime"),
                    genres = GenreNames(d),
                    poster_url = Poster(Str(d, "poster_path"))
                }, s_json);
            }
            return JsonSerializer.Serialize(new
            {
                kind = "series",
                tmdb_id = id,
                imdb_id = ExtId(d, "imdb_id"),
                tvdb_id = ExtId(d, "tvdb_id"),
                title = Str(d, "name"),
                overview = Str(d, "overview"),
                year = YearOf(Str(d, "first_air_date")),
                rating = Num(d, "vote_average"),
                status = Str(d, "status"),
                seasons = IntN(d, "number_of_seasons"),
                genres = GenreNames(d),
                poster_url = Poster(Str(d, "poster_path"))
            }, s_json);
        }

        // ------------------------------------------------------------------
        //  Résolution par id externe : /find/{externalId}?external_source=…
        //  Sert au S2 de la tâche d'identification d'orphelins : l'id IMDb/TMDB
        //  proposé par le LLM est validé ici (TMDB est la source de vérité — un
        //  id halluciné renvoie null). Retourne le TmdbMeta du 1er résultat, ou
        //  null si aucun. Ne lève pas (erreurs loguées + null).
        // ------------------------------------------------------------------

        internal async Task<TmdbMeta> FindByExternalIdAsync(string externalId, string source, string kind, string lang, CancellationToken ct)
        {
            var cfg = Plugin.Instance?.Configuration;
            if (string.IsNullOrWhiteSpace(cfg?.TmdbApiKey) || string.IsNullOrWhiteSpace(externalId) || string.IsNullOrWhiteSpace(source))
                return null;
            string key = cfg.TmdbApiKey;
            string resultsField = kind == "movie" ? "movie_results" : "tv_results";

            JsonDocument doc;
            try
            {
                doc = await GetJsonAsync(
                    $"https://api.themoviedb.org/3/find/{UrlEnc(externalId)}?api_key={UrlEnc(key)}" +
                    $"&external_source={UrlEnc(source)}&language={UrlEnc(lang)}", ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger?.Info("[LLM_AI] tmdb /find {0}={1} échoué ({2}).", source, externalId, ex.Message);
                return null;
            }

            if (doc.RootElement.TryGetProperty(resultsField, out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in arr.EnumerateArray())
                {
                    if (x.TryGetProperty("id", out var idp) && idp.TryGetInt32(out int id) && id > 0)
                    {
                        try
                        {
                            string json = await FetchDetailAsync(key, id, kind, lang, ct).ConfigureAwait(false);
                            var meta = ParseMeta(json);
                            if (meta != null) return meta;
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception ex) { _logger?.Info("[LLM_AI] tmdb /find détail {0} échoué ({1}).", id, ex.Message); }
                        break; // 1er résultat seulement
                    }
                }
            }
            return null;
        }

        // ------------------------------------------------------------------
        //  Recherche multi-langue (S1 de la tâche orphelins) : essaie plusieurs
        //  codes langue pour un même titre (les titres québécois peuvent matcher
        //  en en-US — titre original — ou en fr-FR — titre France). Réutilise le
        //  cache 24h de FetchWithCacheAsync. Retourne le 1er match non-null, en
        //  privilégiant celui qui a un synopsis. Null si aucun match.
        // ------------------------------------------------------------------

        internal async Task<TmdbMeta> LookupMetaMultiLangAsync(string query, string kind, int? year, IEnumerable<string> langs, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(query)) return null;
            TmdbMeta best = null;
            foreach (var lang in (langs ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                TmdbMeta m = null;
                try { m = await LookupMetaAsync(query, kind, year, lang, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { _logger?.Info("[LLM_AI] tmdb multi-lang « {0} » ({1}) échoué ({2}).", query, lang, ex.Message); }

                if (m == null) continue;
                if (best == null) best = m;
                else if (string.IsNullOrWhiteSpace(best.Overview) && !string.IsNullOrWhiteSpace(m.Overview)) best = m;
                if (!string.IsNullOrWhiteSpace(best.Overview)) return best;
            }
            return best;
        }

        // ------------------------------------------------------------------
        //  Détail par id TMDB (S2 de la tâche orphelins) : quand le LLM propose
        //  un tmdb_id, on le valide en relisant la fiche TMDB (TMDB est la
        //  source de vérité — un id halluciné renvoie null). Réutilise
        //  FetchDetailAsync (non mis en cache au niveau détail). Null si échec
        //  ou id invalide. Ne lève pas.
        // ------------------------------------------------------------------

        internal async Task<TmdbMeta> LookupMetaByIdAsync(int tmdbId, string kind, string lang, CancellationToken ct)
        {
            if (tmdbId <= 0) return null;
            var cfg = Plugin.Instance?.Configuration;
            if (string.IsNullOrWhiteSpace(cfg?.TmdbApiKey)) return null;
            try
            {
                string json = await FetchDetailAsync(cfg.TmdbApiKey, tmdbId, kind, lang, ct).ConfigureAwait(false);
                return ParseMeta(json);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger?.Info("[LLM_AI] tmdb détail by-id {0} échoué ({1}).", tmdbId, ex.Message); return null; }
        }

        // ------------------------------------------------------------------
        //  Nettoyage d'un titre EPG bruité (S1) : retire les marqueurs HD/VO/
        //  VOSTFR/Rediff/Inédit, les numéros de saison/épisode, et les
        //  parenthèses, puis collapse les espaces. Conservateur — ne retire que
        //  le bruit typique des guides TV. Le titre original de l'item n'est
        //  jamais modifié (ceci ne sert qu'à la requête de recherche).
        // ------------------------------------------------------------------

        private static readonly Regex s_epgNoise = new Regex(
            @"(?i)\b(?:HD|HDTV|VOSTFR|VF|VO|V\.O\.|V\.F\.|REDIFF|REDIFFUSION|INÉDIT|INEDIT|REDIF)\b" +
            @"|\bS\d{1,2}\s?E\d{1,3}\b|\bSaisons?\s+\d+\b|\b[ÉE]pisodes?\s+\d+\b" +
            @"|[\(\[][^\)\]]*[\)\]]",
            RegexOptions.Compiled);

        internal static string CleanEpgTitle(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return s;
            var t = s_epgNoise.Replace(s, " ");
            t = Regex.Replace(t, @"\s{2,}", " ").Trim(' ', '-', ':', '|', '.');
            return t;
        }

        // ------------------------------------------------------------------
        //  Helpers JSON / HTTP
        // ------------------------------------------------------------------

        private static readonly JsonSerializerOptions s_json = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        throw new InvalidOperationException($"TMDB HTTP {(int)resp.StatusCode}: {Truncate(text, 200)}");
                    return JsonDocument.Parse(text);
                }
            }
        }

        /// <summary>Prend le 1er résultat ; affine par année si fournie.</summary>
        private static JsonElement? PickFirstResult(JsonDocument search, int? year, string dateField)
        {
            if (search.RootElement.TryGetProperty("results", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                if (year.HasValue)
                {
                    foreach (var r in arr.EnumerateArray())
                        if (YearOf(Str(r, dateField)) == year.Value) return r;
                }
                foreach (var r in arr.EnumerateArray())
                    return r;
            }
            return null;
        }

        private static string[] GenreNames(JsonElement det)
        {
            if (!det.TryGetProperty("genres", out var g) || g.ValueKind != JsonValueKind.Array) return null;
            var list = new System.Collections.Generic.List<string>();
            foreach (var x in g.EnumerateArray())
            {
                var n = Str(x, "name");
                if (!string.IsNullOrEmpty(n)) list.Add(n);
            }
            return list.Count == 0 ? null : list.ToArray();
        }

        private static string Poster(string posterPath) =>
            string.IsNullOrEmpty(posterPath) ? null : "https://image.tmdb.org/t/p/w500" + posterPath;

        private static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

        /// <summary>
        /// Lit un champ <paramref name="name"/> dans le sous-objet
        /// <c>external_ids</c> (fourni par <c>append_to_response=external_ids</c>
        /// sur l'appel détail TMDB — ids IMDb/TVDB, indépendants de la langue).
        /// Retourne null si absent.
        /// </summary>
        private static string ExtId(JsonElement det, string name)
        {
            if (det.TryGetProperty("external_ids", out var ext)
                && ext.ValueKind == JsonValueKind.Object
                && ext.TryGetProperty(name, out var p)
                && p.ValueKind == JsonValueKind.String)
                return p.GetString();
            return null;
        }

        private static double? Num(JsonElement e, string name) =>
            e.TryGetProperty(name, out var p) && (p.ValueKind == JsonValueKind.Number) ? p.GetDouble() : (double?)null;

        private static int? IntN(JsonElement e, string name) =>
            e.TryGetProperty(name, out var p) && p.TryGetInt32(out var v) ? v : (int?)null;

        private static int? YearOf(string isoDate) =>
            !string.IsNullOrEmpty(isoDate) && isoDate.Length >= 4 && int.TryParse(isoDate.Substring(0, 4), out var y) ? y : (int?)null;

        private static string OptString(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object) return null;
            return e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        }

        private static int? OptInt(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object) return null;
            return e.TryGetProperty(name, out var p) && p.TryGetInt32(out var v) ? v : (int?)null;
        }

        private static string UrlEnc(string s) => Uri.EscapeDataString(s ?? string.Empty);

        private static string Err(string msg) => JsonSerializer.Serialize(new { error = msg }, s_json);

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s.Substring(0, max) + "…");
    }

    /// <summary>
    /// Résultat structuré d'un lookup TMDB (série ou film), partagé entre l'outil
    /// LLM <see cref="TmdbLookupTool"/> (sérialisé en JSON pour l'agent) et la
    /// génération de cartes .strm (<see cref="StrmLibraryGenerator"/> : synopsis
    /// + note + genres + année pour enrichir le <c>.nfo</c>, et URL du poster).
    /// Tous les champs sont optionnels (null/absents selon le type et la
    /// disponibilité TMDB).
    /// </summary>
    internal sealed class TmdbMeta
    {
        /// <summary>"series" ou "movie".</summary>
        public string Kind;
        /// <summary>Titre TMDB (peut différer du titre EPG).</summary>
        public string Title;
        /// <summary>Synopsis / overview TMDB.</summary>
        public string Overview;
        /// <summary>Année de sortie / première diffusion.</summary>
        public int? Year;
        /// <summary>Note moyenne TMDB (vote_average, /10).</summary>
        public double? Rating;
        /// <summary>Statut ("Returning Series", "Ended", "Released"…).</summary>
        public string Status;
        /// <summary>Nb de saisons (série uniquement).</summary>
        public int? Seasons;
        /// <summary>Durée en minutes (film uniquement).</summary>
        public int? Runtime;
        /// <summary>Genres TMDB (noms).</summary>
        public string[] Genres;
        /// <summary>URL complète du poster (image.tmdb.org/…/w500…).</summary>
        public string PosterUrl;
        /// <summary>Id TMDB (clé provider Emby « tmdb »). 0 si inconnu.</summary>
        public int TmdbId;
        /// <summary>Id IMDb (clé provider « imdb »), si exposé par external_ids.</summary>
        public string ImdbId;
        /// <summary>Id TVDB (clé provider « tvdb »), séries uniquement.</summary>
        public string TvdbId;
    }
}