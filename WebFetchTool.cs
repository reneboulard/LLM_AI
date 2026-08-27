using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace LLM_AI
{
    /// <summary>
    /// Outil de récupération du contenu d'une URL. Backend configurable :
    /// <list type="bullet">
    /// <item><b>Direct auto-hébergé</b> (défaut, <see cref="PluginConfiguration.WebFetchDirect"/>)
    ///   : <c>HttpClient</c> côté plugin + extraction locale de la page en JSON
    ///   structuré (titre, métadonnées og:/twitter, <b>JSON-LD schema.org</b>,
    ///   texte, titres h1–h6 en markdown, tableaux en markdown). Aucune clé
    ///   requise — fonctionne pour la communauté dès l'installation. Portage C#
    ///   de la logique d'extraction de
    ///   <c>/var/www/llm_core/tools/fetch_web_page.php</c> (sans ses dépendances
    ///   curl-impersonate / Redis, non portables).</item>
    /// <item><b>Ollama Cloud</b> (<c>https://ollama.com/api/web_fetch</c>) en
    ///   repli si la récupération directe échoue (anti-bot, 403, page trop
    ///   courte) et qu'une clé est présente (<see cref="PluginConfiguration.OllamaApiKey"/>
    ///   ou <c>OLLAMA_API_KEY</c>).</item>
    /// </list>
    /// Lecture seule. Sert au LLM à lire une page identifiée par
    /// <see cref="WebSearchTool"/> ou un lien fourni.
    /// </summary>
    /// <remarks>
    /// <b>SSRF</b> : l'URL provient du LLM, on valide donc côté client (comme le
    /// PHP) avant tout fetch — refus des hôtes IP littéraux et des noms qui
    /// résolvent vers une adresse privée/réservée/boucle locale. La garde
    /// s'applique aux deux backends (direct et cloud).
    /// </remarks>
    public class WebFetchTool : ILlmTool
    {
        // Client dédié au fetch direct : User-Agent de navigateur pour limiter
        // les blocages anti-bot basiques (beaucoup de sites refusent le UA par
        // défaut de HttpClient). Le PHP utilise curl-impersonate (fingerprint
        // TLS Chrome) — non portable en .NET ; ce UA est le meilleur équivalent
        // zéro-dépendance. Repli Ollama cloud sur les sites qui bloquent quand
        // même.
        private static readonly HttpClient _direct = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        static WebFetchTool()
        {
            _direct.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
            _direct.DefaultRequestHeaders.Accept.ParseAdd(
                "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            _direct.DefaultRequestHeaders.AcceptLanguage.ParseAdd("fr,en;q=0.9");
        }

        // Client pour le repli Ollama cloud (timeout plus long, pas de UA).
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        private readonly ILogger _logger;

        public string Name => "web_fetch";

        public string Description =>
            "Récupère le contenu d'une URL et l'extrait en JSON structuré " +
            "(titre, métadonnées, JSON-LD schema.org, texte, titres, tableaux). " +
            "Backend direct auto-hébergé par défaut (sans clé) ; repli sur " +
            "l'API cloud Ollama si configuré. L'URL doit être un domaine public " +
            "(pas d'IP, pas de réseau local). Paramètre detail_level : \"full\" " +
            "(défaut, contenu complet 15000 car.) ou \"preview\" (extrait 500 car.). " +
            "Utilise-le pour lire une page identifiée par web_search.";

        public string ArgumentsSchema => @"{
  ""url"": ""URL absolue à récupérer (obligatoire)"",
  ""detail_level"": ""full (défaut) ou preview""
}";

        public WebFetchTool(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
        {
            if (args.ValueKind != JsonValueKind.Object) args = default;
            string url = OptString(args, "url");
            if (string.IsNullOrWhiteSpace(url))
                return Err("paramètre 'url' requis pour web_fetch");

            string detail = OptString(args, "detail_level");
            if (string.IsNullOrWhiteSpace(detail)) detail = "full";
            detail = detail.Trim().ToLowerInvariant();
            if (detail != "preview" && detail != "full") detail = "full";

            // --- SSRF : validation côté client avant tout fetch ---
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) == false
                || (uri.Scheme != "http" && uri.Scheme != "https"))
                return Err("URL invalide (doit être http(s) absolu).");

            string host = uri.Host;
            if (string.IsNullOrEmpty(host))
                return Err("URL sans hôte.");
            if (IPAddress.TryParse(host, out _))
                return Err("Requêtes vers une IP littérale bloquées (fournir un nom de domaine).");

            try
            {
                var addrs = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
                if (addrs == null || addrs.Length == 0)
                    return Err($"hôte introuvable : {host}");
                foreach (var a in addrs)
                    if (IsPrivateOrReserved(a))
                        return Err("Requêtes vers des adresses privées/locales bloquées.");
            }
            catch (Exception ex)
            {
                return Err($"résolution DNS échouée pour {host} : {ex.Message}");
            }

            // Cache 24h (après validation SSRF). La clé intègre detail_level pour
            // cacher séparément preview (court) et full (long) d'une même URL.
            string cacheKey = "fetch:" + detail + ":" + WebResultCache.NormalizeUrl(url);
            if (WebResultCache.TryGet(cacheKey, out var cached))
            {
                _logger?.Info("[LLM_AI] web_fetch cache hit url={0}", url);
                return cached;
            }

            bool direct = Plugin.Instance?.Configuration?.WebFetchDirect ?? true;
            string cloudKey = ResolveApiKey();

            try
            {
                string result;
                bool valid;

                if (direct)
                {
                    var (html, fetchErr) = await FetchDirect(url, ct).ConfigureAwait(false);
                    if (html != null && !LooksBlocked(html))
                    {
                        result = BuildStructured(url, html, detail);
                        valid = true;
                        _logger?.Info("[LLM_AI] web_fetch direct url={0} -> {1} (caché 24h)",
                            url, Truncate(result, 200));
                    }
                    else
                    {
                        // Récupération directe échouée ou anti-bot : repli cloud
                        // si une clé est présente, sinon on signale l'échec.
                        string why = html != null ? "anti-bot/blocage" : (fetchErr ?? "échec fetch");
                        if (!string.IsNullOrWhiteSpace(cloudKey))
                        {
                            _logger?.Info("[LLM_AI] web_fetch direct KO ({0}) url={1} -> repli Ollama cloud", why, url);
                            result = await FetchCloud(url, cloudKey, ct).ConfigureAwait(false);
                            valid = !IsErrorResult(result);
                        }
                        else
                        {
                            return Err($"récupération directe impossible ({why}) et aucune clé Ollama cloud pour le repli. Configurez OllamaApiKey ou désactivez WebFetchDirect.");
                        }
                    }
                }
                else
                {
                    // Backend direct désactivé : Ollama cloud uniquement.
                    if (string.IsNullOrWhiteSpace(cloudKey))
                        return Err("web_fetch non configuré (WebFetchDirect désactivé et OLLAMA_API_KEY / clé absente).");
                    result = await FetchCloud(url, cloudKey, ct).ConfigureAwait(false);
                    valid = !IsErrorResult(result);
                    _logger?.Info("[LLM_AI] web_fetch cloud url={0} -> {1}",
                        url, Truncate(result, 200));
                }

                // Ne cache QUE les résultats valides (pas les erreurs).
                if (valid)
                    WebResultCache.Set(cacheKey, result);
                return result;
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("[LLM_AI] web_fetch a levé : {0}", ex, ex.Message);
                return Err(ex.Message);
            }
        }

        // ------------------------------------------------------------------
        //  Backend direct : HttpClient + extraction locale
        // ------------------------------------------------------------------

        /// <summary>
        /// Récupère le HTML via HttpClient (UA navigateur). Retourne
        /// (html, erreur) : html non null si la requête a abouti (HTTP 2xx).
        /// </summary>
        private async Task<(string html, string error)> FetchDirect(string url, CancellationToken ct)
        {
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                using (var resp = await _direct.SendAsync(req, ct).ConfigureAwait(false))
                {
                    var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        return (null, $"HTTP {(int)resp.StatusCode}");
                    if (string.IsNullOrWhiteSpace(text))
                        return (null, "corps vide");
                    return (text, null);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return (null, ex.Message);
            }
        }

        /// <summary>
        /// Heuristique anti-bot : page trop courte ou page d'attente
        /// Cloudflare / challenge JS. Si vrai, on tente le repli cloud.
        /// </summary>
        private static bool LooksBlocked(string html)
        {
            if (html == null) return true;
            if (html.Length < 80) return true;
            // Indices classiques de challenge anti-bot.
            if (html.IndexOf("Just a moment", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (html.IndexOf("Enable JavaScript and cookies to continue", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (html.IndexOf("cf-browser-verification", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>
        /// Construit le JSON structuré à partir du HTML, fidèle à la sortie de
        /// fetch_web_page.php : {url, backend, title, meta, json_ld,
        /// detail_level, content|content_preview, headings, tables}. Les liens
        /// et images du PHP sont omis (bruit pour le LLM ; économie de tokens).
        /// </summary>
        private static string BuildStructured(string url, string html, string detail)
        {
            string title = ExtractTitle(html);
            var meta = ExtractMeta(html);
            var jsonLd = ExtractJsonLd(html);
            string text = ExtractText(html);

            var obj = new JsonObject
            {
                ["backend"] = "direct",
                ["url"] = url,
                ["title"] = title,
                ["detail_level"] = detail
            };

            var metaObj = new JsonObject();
            foreach (var kv in meta) metaObj[kv.Key] = kv.Value;
            obj["meta"] = metaObj;

            obj["json_ld"] = jsonLd;

            if (detail == "full")
            {
                string content = text;
                const int max = 15000;
                if (content.Length > max)
                    content = content.Substring(0, max) + "... [Contenu tronqué]";
                obj["content"] = content;
                obj["headings"] = ExtractHeadings(html);
                obj["tables"] = ExtractTables(html);
            }
            else
            {
                const int preview = 500;
                string p = text.Length <= preview ? text : text.Substring(0, preview) + "...";
                obj["content_preview"] = p;
            }

            return obj.ToJsonString(s_json);
        }

        // ------------------------------------------------------------------
        //  Extraction HTML (regex, zéro dépendance — pas de HtmlAgilityPack)
        // ------------------------------------------------------------------

        // Doit être déclaré AVANT les Regex qui l'utilisent : l'initialiseur
        // statique des champs s'exécute dans l'ordre textuel, donc ce champ
        // doit déjà valoir 5s quand les Regex ci-dessous sont construits
        // (sinon TimeSpan.Zero → ArgumentOutOfRangeException dans .cctor).
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);

        private static readonly Regex s_rxTitle = new Regex(
            @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline,
            RegexTimeout);
        private static readonly Regex s_rxMeta = new Regex(
            @"<meta\b[^>]*>", RegexOptions.IgnoreCase);
        private static readonly Regex s_rxJsonLd = new Regex(
            @"<script\b[^>]*type\s*=\s*[""']application/ld\+json[""'][^>]*>(.*?)</script>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);
        private static readonly Regex s_rxHeadings = new Regex(
            @"<h([1-6])[^>]*>(.*?)</h\1>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);
        private static readonly Regex s_rxTable = new Regex(
            @"<table\b[^>]*>(.*?)</table>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);
        private static readonly Regex s_rxRow = new Regex(
            @"<tr\b[^>]*>(.*?)</tr>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);
        private static readonly Regex s_rxCell = new Regex(
            @"<(th|td)[^>]*>(.*?)</\1>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);
        private static readonly Regex s_rxTag = new Regex(@"<[^>]+>");
        private static readonly Regex s_rxWs = new Regex(@"\s+");

        private static string ExtractTitle(string html)
        {
            var m = s_rxTitle.Match(html);
            if (!m.Success) return "";
            return CleanText(m.Groups[1].Value);
        }

        private static readonly HashSet<string> s_usefulMeta = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "og:title", "og:description", "og:image", "og:type", "og:url",
            "description", "author", "article:published_time", "article:modified_time",
            "twitter:title", "twitter:description", "twitter:image"
        };

        private static Dictionary<string, string> ExtractMeta(string html)
        {
            var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match mm in s_rxMeta.Matches(html))
            {
                string tag = mm.Value;
                string key = GetAttr(tag, "property");
                if (string.IsNullOrEmpty(key)) key = GetAttr(tag, "name");
                if (string.IsNullOrEmpty(key)) continue;
                if (!s_usefulMeta.Contains(key)) continue;
                string val = GetAttr(tag, "content");
                if (string.IsNullOrWhiteSpace(val)) continue;
                meta[key] = WebUtility.HtmlDecode(val).Trim();
            }
            return meta;
        }

        private static JsonArray ExtractJsonLd(string html)
        {
            var arr = new JsonArray();
            foreach (Match m in s_rxJsonLd.Matches(html))
            {
                string inner = m.Groups[1].Value;
                if (string.IsNullOrWhiteSpace(inner)) continue;
                try
                {
                    var node = JsonNode.Parse(inner);
                    if (node != null) arr.Add(node);
                }
                catch { /* JSON-LD malformé : on l'ignore */ }
            }
            return arr;
        }

        private static JsonArray ExtractHeadings(string html)
        {
            var arr = new JsonArray();
            foreach (Match m in s_rxHeadings.Matches(html))
            {
                int level = int.Parse(m.Groups[1].Value);
                string text = CleanText(m.Groups[2].Value);
                if (!string.IsNullOrEmpty(text))
                    arr.Add(new string('#', level) + " " + text);
            }
            return arr;
        }

        private static JsonArray ExtractTables(string html)
        {
            var arr = new JsonArray();
            foreach (Match tm in s_rxTable.Matches(html))
            {
                var sb = new StringBuilder();
                foreach (Match rm in s_rxRow.Matches(tm.Groups[1].Value))
                {
                    var cells = new List<string>();
                    foreach (Match cm in s_rxCell.Matches(rm.Groups[1].Value))
                        cells.Add(CleanText(cm.Groups[2].Value));
                    if (cells.Count > 0)
                        sb.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
                }
                if (sb.Length > 0) arr.Add(sb.ToString().TrimEnd('\n'));
            }
            return arr;
        }

        private static string ExtractText(string html)
        {
            string s = html;
            // Retire script/style/noscript avant de stripper les balises.
            s = Regex.Replace(s, @"<script\b[^>]*>.*?</script>", " ",
                RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);
            s = Regex.Replace(s, @"<style\b[^>]*>.*?</style>", " ",
                RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);
            s = Regex.Replace(s, @"<noscript\b[^>]*>.*?</noscript>", " ",
                RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);
            // Strip toutes les balises restantes.
            s = s_rxTag.Replace(s, " ");
            s = WebUtility.HtmlDecode(s);
            s = s_rxWs.Replace(s, " ").Trim();
            return s;
        }

        /// <summary>Nettoie un fragment HTML : strip balises, décode entités,
        /// collapse whitespace.</summary>
        private static string CleanText(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s_rxTag.Replace(s, " ");
            s = WebUtility.HtmlDecode(s);
            s = s_rxWs.Replace(s, " ").Trim();
            return s;
        }

        /// <summary>
        /// Extrait la valeur d'un attribut d'une balise (ex. content, property,
        /// name). Gère les quotes doubles, simples et la forme sans quotes.
        /// </summary>
        private static string GetAttr(string tag, string attr)
        {
            var m = Regex.Match(tag,
                @"\s" + Regex.Escape(attr) + @"\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))",
                RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            if (m.Groups[1].Success) return m.Groups[1].Value;
            if (m.Groups[2].Success) return m.Groups[2].Value;
            return m.Groups[3].Value;
        }

        // ------------------------------------------------------------------
        //  Backend repli : Ollama cloud (chemin d'origine)
        // ------------------------------------------------------------------

        private async Task<string> FetchCloud(string url, string key, CancellationToken ct)
        {
            var body = JsonSerializer.Serialize(new { url });
            using (var req = new HttpRequestMessage(HttpMethod.Post, "https://ollama.com/api/web_fetch"))
            {
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");

                using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        return Err($"Ollama Cloud HTTP {(int)resp.StatusCode}: {Truncate(text, 300)}");

                    // L'API renvoie directement le contenu. On valide le JSON
                    // avant de le réinjecter dans la boucle agent.
                    try { using (JsonDocument.Parse(text)) { } }
                    catch { return JsonSerializer.Serialize(new { error = "réponse non-JSON", raw = Truncate(text, 500) }, s_json); }
                    return text;
                }
            }
        }

        /// <summary>Détecte un résultat d'erreur (JSON contenant un champ
        /// "error") pour ne pas le cacher.</summary>
        private static bool IsErrorResult(string result)
        {
            if (string.IsNullOrWhiteSpace(result)) return true;
            try { using (var doc = JsonDocument.Parse(result)) return doc.RootElement.TryGetProperty("error", out _); }
            catch { return true; }
        }

        // ------------------------------------------------------------------
        //  SSRF : détection des adresses privées / réservées / boucle locale
        // ------------------------------------------------------------------

        private static bool IsPrivateOrReserved(IPAddress ip)
        {
            if (IPAddress.IsLoopback(ip)) return true;
            if (ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv6SiteLocal) return true;

            var b = ip.GetAddressBytes();
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                if (b[0] == 0) return true;                              // 0.0.0.0/8
                if (b[0] == 10) return true;                             // 10.0.0.0/8
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true; // 172.16.0.0/12
                if (b[0] == 192 && b[1] == 168) return true;             // 192.168.0.0/16
                if (b[0] == 169 && b[1] == 254) return true;             // 169.254.0.0/16
                if (b[0] >= 224) return true;                           // 224.0.0.0/4 multicast + réservé
                return false;
            }
            // IPv6
            if (b[0] == 0xfe && (b[1] & 0xc0) == 0x80) return true;      // fe80::/10 link-local
            if (b[0] == 0xfc || b[0] == 0xfd) return true;               // fc00::/7 unique-local
            if (b[0] == 0xff) return true;                               // multicast
            // :: et ::1 déjà couverts par IsLoopback / adresse non précisée :
            if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;
            return false;
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