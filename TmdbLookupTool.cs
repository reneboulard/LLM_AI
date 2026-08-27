using System;
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
            string lang = string.IsNullOrWhiteSpace(cfg.TmdbLanguage) ? "fr-FR" : cfg.TmdbLanguage;
            string key = cfg.TmdbApiKey;

            try
            {
                string result = kind == "movie"
                    ? await LookupMovieAsync(key, lang, query, year, ct).ConfigureAwait(false)
                    : await LookupSeriesAsync(key, lang, query, year, ct).ConfigureAwait(false);
                _logger?.Info("[LLM_AI] tmdb_lookup query={0} kind={1} -> {2}", query, kind, Truncate(result, 200));
                return result;
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("[LLM_AI] tmdb_lookup a levé : {0}", ex, ex.Message);
                return Err(ex.Message);
            }
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

            var det = await GetJsonAsync(
                $"https://api.themoviedb.org/3/tv/{id}?api_key={UrlEnc(key)}&language={UrlEnc(lang)}", ct)
                .ConfigureAwait(false);
            var d = det.RootElement;

            return JsonSerializer.Serialize(new
            {
                kind = "series",
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

            var det = await GetJsonAsync(
                $"https://api.themoviedb.org/3/movie/{id}?api_key={UrlEnc(key)}&language={UrlEnc(lang)}", ct)
                .ConfigureAwait(false);
            var d = det.RootElement;

            return JsonSerializer.Serialize(new
            {
                kind = "movie",
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
}