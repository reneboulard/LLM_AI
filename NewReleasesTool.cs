using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using MediaBrowser.Model.Logging;

namespace LLM_AI
{
    /// <summary>
    /// Outil de scraping HTTP générique « nouveautés TV » : liste les
    /// nouvelles séries/saisons annoncées par les sources web configurées
    /// (héritier du scraper Showbizz.net, porté de
    /// <c>/var/www/llm_core/tools/showbizz_scraper.php</c>).
    /// <list type="bullet">
    /// <item>Sources = <see cref="PluginConfiguration.NewReleaseSources"/>, une
    /// par ligne : « URL » (flux RSS/Atom auto-détecté), « URL :: @showbizz »
    /// (extracteur intégré : blocs <c>/emissions/</c> + « Saison 1 ») ou
    /// « URL :: regex .NET » (groupe nommé « title » requis, « url »/« date »
    /// optionnels).</item>
    /// <item>Retourne <c>[{title, date, url, source}]</c> — enveloppe stable,
    ///     aussi consommée par <c>LlmRunner.EnrichRecommendations</c>.</item>
    /// <item>User-Agent Chrome (certains sites bloquent les UA non-navigateur)
    ///     + cache 24h en mémoire, clé fondée sur le SHA256 des sources :
    ///     modifier la config re-scrappe sans redémarrer.</item>
    /// </list>
    /// Alias compatibilité : <see cref="AliasTool"/> réexpose l'ancien nom
    /// <c>showbizz_new_releases</c> pour ne pas casser les prompts sauvegardés.
    /// Ne lève jamais (erreur source → on saute ; erreur globale → JSON).
    /// </summary>
    public class NewReleasesTool : ILlmTool
    {
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        // UA Chrome complet (identique au PHP) — certains sites bloquent
        // les UA non-navigateur.
        private const string ChromeUA =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        // Cache 24h en mémoire (équivalent du cache fichier PHP). La clé est
        // le hash des sources effectives : un changement de config invalide
        // le cache sans redémarrage.
        private static List<object> s_cacheList;
        private static DateTime s_cacheAt = DateTime.MinValue;
        private static string s_cacheKey;
        private static readonly object s_cacheLock = new object();
        private static readonly TimeSpan s_cacheDuration = TimeSpan.FromHours(24);

        private readonly ILogger _logger;
        private readonly List<SourceSpec> _sources;
        private readonly string _description;

        // Sources Showbizz.net historiques (portage de showbizz_scraper.php)
        // — utilisées par la migration de PluginConfiguration.NewReleaseSources.
        internal static readonly string[] LegacyDefaultSources = new[]
        {
            "https://www.showbizz.net",
            "https://www.showbizz.net/nos-listes/a-ne-pas-manquer-cet-automne-a-la-tele"
        };

        /// <summary>Une ligne de la config : URL + mode d'extraction
        /// ("" = auto RSS/Atom, "@showbizz" = extracteur intégré, sinon regex).</summary>
        public sealed class SourceSpec
        {
            public string Url;
            public string Extractor;

            public SourceSpec(string url, string extractor)
            {
                Url = url;
                Extractor = extractor;
            }
        }

        /// <summary>
        /// Analyse <see cref="PluginConfiguration.NewReleaseSources"/> : une
        /// source par ligne, séparateur « :: » entre l'URL et le mode
        /// d'extraction. Ignore les lignes vides ; dédoublonne les URL.
        /// </summary>
        public static List<SourceSpec> ParseSources(string text)
        {
            var list = new List<SourceSpec>();
            if (string.IsNullOrWhiteSpace(text)) return list;
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;

                string url = line, extractor = "";
                int sep = line.IndexOf(" :: ", StringComparison.Ordinal);
                if (sep >= 0)
                {
                    url = line.Substring(0, sep).Trim();
                    extractor = line.Substring(sep + 4).Trim();
                }
                if (url.Length == 0) continue;
                if (list.Any(s => string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase)))
                    continue;
                list.Add(new SourceSpec(url, extractor));
            }
            return list;
        }

        public string Name => "new_releases";

        public string Description => _description;

        public string ArgumentsSchema => @"{
  ""limit"": ""nombre max de titres (défaut 50)""
}";

        public NewReleasesTool(List<SourceSpec> sources, ILogger logger)
        {
            _logger = logger;
            _sources = sources ?? new List<SourceSpec>();
            _description = BuildDescription(_sources);
        }

        /// <summary>Description dynamique (première du plugin) : générique,
        /// indépendante du site — le LLM n'a pas à savoir d'où viennent les
        /// données, juste ce que l'outil retourne. Liste les hôtes pour
        /// l'aider à citer ses sources.</summary>
        private static string BuildDescription(List<SourceSpec> sources)
        {
            var hosts = new List<string>();
            foreach (var s in sources)
            {
                try
                {
                    string h = new Uri(s.Url).Host;
                    if (!hosts.Contains(h, StringComparer.OrdinalIgnoreCase)) hosts.Add(h);
                }
                catch { /* URL malformée : ignorée dans la description */ }
            }
            string src = hosts.Count > 0 ? " Sources : " + string.Join(", ", hosts) + "." : "";
            return "Liste les nouveautés TV (nouvelles séries/saisons) annoncées par " +
                   sources.Count + " source(s) web configurée(s) (flux RSS/Atom ou page " +
                   "HTML + regex). Retourne un tableau [{title, date, url, source}]. " +
                   "Sert à mettre en avant les nouvelles séries et à croiser avec " +
                   "l'EPG pour identifier les premiers épisodes à recommander." + src;
        }

        public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
        {
            int limit = 50;
            if (args.ValueKind == JsonValueKind.Object &&
                args.TryGetProperty("limit", out var lp) && lp.TryGetInt32(out var l))
                limit = Math.Max(1, l);

            // 1) Cache 24h : renvoie la liste cached tronquée à `limit`, à
            //    condition que les sources n'aient pas changé depuis.
            string key = ComputeCacheKey(_sources);
            List<object> cached;
            lock (s_cacheLock)
            {
                if (s_cacheList != null && DateTime.UtcNow - s_cacheAt < s_cacheDuration &&
                    string.Equals(s_cacheKey, key, StringComparison.Ordinal))
                    cached = s_cacheList;
                else
                    cached = null;
            }
            if (cached != null)
            {
                _logger?.Info("[LLM_AI] new_releases -> cache ({0} nouveauté(s))", cached.Count);
                return Serialize(cached, limit);
            }

            // 2) Scrappe chaque source (tolérant : une source injoignable est
            //    sautée ; une regex invalide est ignorée avec un Warn).
            var shows = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            int okSources = 0;
            foreach (var spec in _sources)
            {
                string html;
                try { html = await FetchAsync(spec.Url, ct).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    _logger?.Warn("[LLM_AI] new_releases source {0} injoignable : {1}",
                        spec.Url, ex.Message);
                    continue;
                }
                if (string.IsNullOrEmpty(html)) continue;
                okSources++;

                if (spec.Extractor == "@showbizz")
                {
                    ExtractEmissions(html, shows, limit);
                }
                else if (!string.IsNullOrEmpty(spec.Extractor))
                {
                    Regex rx;
                    try
                    {
                        rx = new Regex(spec.Extractor,
                            RegexOptions.IgnoreCase | RegexOptions.Singleline,
                            TimeSpan.FromSeconds(2));
                    }
                    catch (ArgumentException ex)
                    {
                        _logger?.Warn("[LLM_AI] new_releases regex invalide pour {0} : {1} — source ignorée",
                            spec.Url, ex.Message);
                        continue;
                    }
                    ExtractWithPattern(html, rx, shows, limit, spec.Url);
                }
                else if (ExtractFeed(html, shows, limit, spec.Url))
                {
                    // Flux RSS/Atom auto-détecté — rien à faire de plus.
                }
                else
                {
                    _logger?.Warn("[LLM_AI] new_releases source {0} : HTML sans motif " +
                        "d'extraction — ajoutez « :: @showbizz » ou « :: <regex> » sur la ligne",
                        spec.Url);
                }
            }

            var list = shows.Values.ToList();

            // 3) Publie dans le cache (avec la clé des sources).
            lock (s_cacheLock)
            {
                s_cacheList = list;
                s_cacheAt = DateTime.UtcNow;
                s_cacheKey = key;
            }

            _logger?.Info("[LLM_AI] new_releases -> {0} nouveauté(s) depuis {1} source(s)",
                list.Count, okSources);
            return Serialize(list, limit);
        }

        /// <summary>Clé de cache = SHA256 des lignes « URL\textractor » —
        /// tout changement de config re-scrappe sans redémarrage.</summary>
        private static string ComputeCacheKey(List<SourceSpec> sources)
        {
            string joined = string.Join("\n",
                sources.Select(s => s.Url + "\t" + s.Extractor));
            using (var sha = SHA256.Create())
                return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(joined)));
        }

        // ------------------------------------------------------------------
        //  Extraction « emissions » (portage du PHP, extracteur intégré)
        // ------------------------------------------------------------------

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
        //  Extraction par regex utilisateur (une par source)
        // ------------------------------------------------------------------

        private void ExtractWithPattern(string html, Regex rx,
            Dictionary<string, object> shows, int limit, string sourceUrl)
        {
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
                    url = string.IsNullOrEmpty(url) ? null : ResolveUrl(url, sourceUrl),
                    source = sourceUrl
                };
            }
        }

        // ------------------------------------------------------------------
        //  Flux RSS 2.0 / Atom (auto-détecté quand aucune extraction n'est
        //  donnée sur la ligne) — XDocument, aucun paquet externe.
        // ------------------------------------------------------------------

        /// <summary>Tente de lire le contenu comme un flux RSS/Atom.
        /// Retourne false (sans lever) si ce n'est pas un flux valide.</summary>
        private bool ExtractFeed(string html, Dictionary<string, object> shows,
            int limit, string sourceUrl)
        {
            XDocument doc;
            try { doc = XDocument.Parse(html); }
            catch { return false; }

            var root = doc.Root;
            if (root == null) return false;

            IEnumerable<XElement> items;
            if (root.Name.LocalName == "rss")
            {
                var channel = root.Element(root.Name.Namespace + "channel");
                items = channel == null
                    ? root.Descendants(root.Name.Namespace + "item")
                    : channel.Elements(root.Name.Namespace + "item");
            }
            else if (root.Name.LocalName == "feed")
                items = root.Elements(root.Name.Namespace + "entry");
            else
                return false;

            foreach (var item in items)
            {
                if (shows.Count >= limit) break;
                var ns = item.Name.Namespace;
                var titleEl = item.Element(ns + "title");
                string title = titleEl == null ? null : Clean(StripTags(titleEl.Value));
                if (string.IsNullOrWhiteSpace(title)) continue;
                string key = title.ToLowerInvariant();
                if (shows.ContainsKey(key)) continue;

                // Lien : RSS <link>texte</link> ; Atom <link href="..."/>.
                string url = (string)item.Element(ns + "link");
                if (url == null)
                {
                    var linkEl = item.Elements(ns + "link").FirstOrDefault();
                    url = linkEl == null ? null : (string)linkEl.Attribute("href");
                }

                // Date : RSS <pubDate> ; Atom <published>/<updated>.
                var dateEl = item.Element(ns + "pubDate");
                if (dateEl == null) dateEl = item.Element(ns + "published");
                if (dateEl == null) dateEl = item.Element(ns + "updated");
                string date = dateEl == null ? null : Clean(dateEl.Value);

                shows[key] = new
                {
                    title,
                    date = string.IsNullOrEmpty(date) ? null : date,
                    url = string.IsNullOrEmpty(url) ? null : ResolveUrl(url, sourceUrl),
                    source = sourceUrl
                };
            }
            return true;
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

        /// <summary>Décode les entités HTML (toutes, via HtmlDecode) et trim.</summary>
        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return WebUtility.HtmlDecode(s).Trim().Replace("’", "'");
        }

        /// <summary>Résout un lien relatif (« /x ») contre son hôte source.</summary>
        private static string ResolveUrl(string url, string sourceUrl)
        {
            if (!url.StartsWith("/", StringComparison.Ordinal)) return url;
            try { return new Uri(new Uri(sourceUrl), url).ToString(); }
            catch { return url; }
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
                            $"new_releases HTTP {(int)resp.StatusCode}: {Truncate(text, 200)}");
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

        /// <summary>
        /// Alias compatibilité : réexpose l'ancien nom
        /// <c>showbizz_new_releases</c> (prompts sauvegardés, tâche planifiée
        /// d'utilisateurs existants) en transférant tout au nouvel outil.
        /// </summary>
        public sealed class AliasTool : ILlmTool
        {
            private readonly NewReleasesTool _inner;

            public string Name => "showbizz_new_releases";

            public string Description =>
                "Alias obsolète de new_releases — même outil, préférez new_releases.";

            public string ArgumentsSchema => _inner.ArgumentsSchema;

            public AliasTool(NewReleasesTool inner) { _inner = inner; }

            public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) =>
                _inner.ExecuteAsync(args, ct);
        }
    }
}