using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace LLM_AI
{
    /// <summary>
    /// Outil de scraping HTTP : liste les nouveautés « Saison 1 » de
    /// Showbizz.net. Portage C# fidèle de
    /// <c>/var/www/llm_core/tools/showbizz_scraper.php</c> :
    /// <list type="bullet">
    /// <item>Scrappe la page d'accueil + la liste saisonnière (2 sources),
    /// plus l'<see cref="PluginConfiguration.ShowbizzUrl"/> si elle est
    /// renseignée et différente.</item>
    /// <item>Cherche les blocs <c>&lt;a href="/emissions/..."&gt;…&lt;/a&gt;</c>
    /// et ne garde que ceux contenant « Saison 1 ».</item>
    /// <item>Extrait le titre (<c>&lt;h3&gt;</c>/<c>&lt;h4&gt;</c>) et la date
    /// (« Dès le … »).</item>
    /// <item>Retourne <c>[{title, date, url, source}]</c>.</item>
    /// <item>User-Agent Chrome (certains sites bloquent les UA non-navigateur)
    /// + cache 24h en mémoire pour ne pas spammer le site.</item>
    /// </list>
    /// Repli avancé : si <see cref="PluginConfiguration.ShowbizzPattern"/> est
    /// renseignée, elle remplace l'extraction « emissions » (regex avec groupe
    /// nommé « title », « url »/« date » optionnels) — pour cas spécifique.
    /// Ne lève jamais (erreur source → on saute ; erreur globale → JSON).
    /// </summary>
    public class ShowbizzTool : ILlmTool
    {
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        // UA Chrome complet (identique au PHP) — showbizz.net peut bloquer
        // les UA non-navigateur.
        private const string ChromeUA =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        // Cache 24h en mémoire (équivalent du cache fichier PHP).
        private static List<object> s_cacheList;
        private static DateTime s_cacheAt = DateTime.MinValue;
        private static readonly object s_cacheLock = new object();
        private static readonly TimeSpan s_cacheDuration = TimeSpan.FromHours(24);

        private readonly ILogger _logger;

        public string Name => "showbizz_new_releases";

        public string Description =>
            "Liste les nouveautés (Saison 1) selon Showbizz.net (page d'accueil + " +
            "liste saisonnière). Retourne un tableau [{title, date, url, source}]. " +
            "Sert à mettre en avant les nouvelles séries annoncées et à croiser " +
            "avec l'EPG pour identifier les S01E01 à recommander.";

        public string ArgumentsSchema => @"{
  ""limit"": ""nombre max de titres (défaut 50)""
}";

        // Sources par défaut (portage de showbizz_scraper.php).
        private static readonly string[] DefaultSources = new[]
        {
            "https://www.showbizz.net",
            "https://www.showbizz.net/nos-listes/a-ne-pas-manquer-cet-automne-a-la-tele"
        };

        // Bloc <a href="/emissions/..."> ... </a> (multiline).
        private static readonly Regex AnchorRx = new Regex(
            @"<a[^>]*href=""(?<slug>/emissions/[a-z0-9-]+)""[^>]*>(?<inner>.*?)</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(2));

        // Titre dans <h3> ou <h4>.
        private static readonly Regex TitleRx = new Regex(
            @"<h[34][^>]*>(?<title>.*?)</h[34]>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(1));

        // Date « Dès le ... » (jusqu'au prochain '<').
        private static readonly Regex DateRx = new Regex(
            @"Dès le\s*(?<date>.*?)<",
            RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(1));

        public ShowbizzTool(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
        {
            int limit = 50;
            if (args.ValueKind == JsonValueKind.Object &&
                args.TryGetProperty("limit", out var lp) && lp.TryGetInt32(out var l))
                limit = Math.Max(1, l);

            // 1) Cache 24h : renvoie la liste cached tronquée à `limit`.
            List<object> cached;
            lock (s_cacheLock)
            {
                if (s_cacheList != null && DateTime.UtcNow - s_cacheAt < s_cacheDuration)
                    cached = s_cacheList;
                else
                    cached = null;
            }
            if (cached != null)
            {
                _logger?.Info("[LLM_AI] showbizz_new_releases -> cache ({0} nouveauté(s))",
                    cached.Count);
                return Serialize(cached, limit);
            }

            // 2) Construit la liste des sources : défauts + URL config (si différente).
            var cfg = Plugin.Instance?.Configuration;
            var sources = new List<string>(DefaultSources);
            if (!string.IsNullOrWhiteSpace(cfg?.ShowbizzUrl))
            {
                var u = cfg.ShowbizzUrl.Trim();
                if (!sources.Contains(u, StringComparer.OrdinalIgnoreCase))
                    sources.Add(u);
            }

            // 3) Scrappe chaque source (tolérant : une source injoignable est sautée).
            var shows = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            string pattern = cfg?.ShowbizzPattern;
            bool usePattern = !string.IsNullOrWhiteSpace(pattern);

            foreach (var url in sources)
            {
                string html;
                try { html = await FetchAsync(url, ct).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    _logger?.Warn("[LLM_AI] showbizz source {0} injoignable : {1}", url, ex.Message);
                    continue;
                }
                if (string.IsNullOrEmpty(html)) continue;

                if (usePattern)
                    ExtractWithPattern(html, pattern, shows, limit, url);
                else
                    ExtractEmissions(html, shows, limit);
            }

            var list = shows.Values.ToList();

            // 4) Publie dans le cache.
            lock (s_cacheLock)
            {
                s_cacheList = list;
                s_cacheAt = DateTime.UtcNow;
            }

            _logger?.Info("[LLM_AI] showbizz_new_releases -> {0} nouveauté(s) (Saison 1) depuis {1} source(s)",
                list.Count, sources.Count);
            return Serialize(list, limit);
        }

        // ------------------------------------------------------------------
        //  Extraction « emissions » (portage du PHP)
        // ------------------------------------------------------------------

        private void ExtractEmissions(string html, Dictionary<string, object> shows, int limit)
        {
            foreach (Match m in AnchorRx.Matches(html))
            {
                if (shows.Count >= limit) break;
                string inner = m.Groups["inner"].Value;
                // On ne garde que les blocs contenant « Saison 1 ».
                if (inner.IndexOf("Saison 1", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                string slug = m.Groups["slug"].Value;
                string title = "";
                var tm = TitleRx.Match(inner);
                if (tm.Success) title = Clean(StripTags(tm.Groups["title"].Value));

                string date = "";
                var dm = DateRx.Match(inner);
                if (dm.Success) date = Clean(StripTags(dm.Groups["date"].Value));

                if (string.IsNullOrWhiteSpace(title)) continue;
                string key = title.ToLowerInvariant();
                if (shows.ContainsKey(key)) continue; // dedup (premier vu gagne)

                shows[key] = new
                {
                    title,
                    date = string.IsNullOrEmpty(date) ? null : date,
                    url = "https://www.showbizz.net" + slug,
                    source = "Showbizz.net"
                };
            }
        }

        // ------------------------------------------------------------------
        //  Extraction par regex utilisateur (repli avancé)
        // ------------------------------------------------------------------

        private void ExtractWithPattern(string html, string pattern,
            Dictionary<string, object> shows, int limit, string sourceUrl)
        {
            var rx = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline,
                TimeSpan.FromSeconds(2));
            foreach (Match m in rx.Matches(html ?? string.Empty))
            {
                if (shows.Count >= limit) break;
                string title = m.Groups["title"].Success ? Clean(m.Groups["title"].Value) : null;
                if (string.IsNullOrWhiteSpace(title)) continue;
                string key = title.ToLowerInvariant();
                if (shows.ContainsKey(key)) continue;

                string url = m.Groups["url"].Success ? m.Groups["url"].Value : null;
                string date = m.Groups["date"].Success ? Clean(m.Groups["date"].Value) : null;
                shows[key] = new
                {
                    title,
                    date = string.IsNullOrEmpty(date) ? null : date,
                    url = string.IsNullOrEmpty(url) ? null : url,
                    source = sourceUrl
                };
            }
        }

        // ------------------------------------------------------------------
        //  Helpers
        // ------------------------------------------------------------------

        /// <summary>Retire les balises HTML d'un fragment.</summary>
        private static string StripTags(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return Regex.Replace(s, @"<[^>]+>", " ", RegexOptions.None, TimeSpan.FromSeconds(1))
                       .Replace("  ", " ").Trim();
        }

        /// <summary>Décode les entités HTML courantes et trim.</summary>
        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Trim()
                    .Replace("&amp;", "&")
                    .Replace("&#39;", "'")
                    .Replace("&apos;", "'")
                    .Replace("&quot;", "\"")
                    .Replace("&nbsp;", " ")
                    .Replace("’", "'");
        }

        private static string Serialize(List<object> list, int limit)
        {
            var trimmed = limit > 0 && list.Count > limit ? list.Take(limit).ToList() : list;
            return JsonSerializer.Serialize(new { total = trimmed.Count, results = trimmed }, s_json);
        }

        // ------------------------------------------------------------------
        //  HTTP
        // ------------------------------------------------------------------

        private static async Task<string> FetchAsync(string url, CancellationToken ct)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.UserAgent.ParseAdd(ChromeUA);
                using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        throw new InvalidOperationException(
                            $"Showbizz HTTP {(int)resp.StatusCode}: {Truncate(text, 200)}");
                    return text;
                }
            }
        }

        private static readonly JsonSerializerOptions s_json = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s.Substring(0, max) + "…");
    }
}