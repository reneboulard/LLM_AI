using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace LLM_AI
{
    /// <summary>
    /// Endpoint HTTP plugin « Vérification de mise à jour » : expose
    /// <c>GET /Plugins/LLMAI/Update</c> à la page de config pour signaler
    /// qu'une nouvelle version du plugin est publiée sur GitHub
    /// (<c>reneboulard/LLM_AI</c>). Interroge l'API publique
    /// <c>releases/latest</c> (celle qu'alimente le workflow
    /// <c>.github/workflows/release.yml</c> sur un tag <c>v*</c>) et compare le
    /// tag à la version de l'assembly installée.
    /// </summary>
    /// <remarks>
    /// Emby ne met à jour automatiquement que les plugins de son catalogue
    /// officiel — un dépôt GitHub auto-hébergé doit vérifier lui-même. La
    /// comparaison est en lecture seule (aucun téléchargement, aucune
    /// installation) : la page affiche un bandeau avec le lien de la release ;
    /// l'installation reste manuelle (<c>install.sh</c> de l'archive).
    /// Le résultat est mis en cache 1 h (partagé entre appels, sous verrou) :
    /// l'API GitHub non authentifiée est limitée à 60 req/h par IP — le
    /// bandeau de la page de config n'a pas besoin de plus d'un refresh par
    /// heure. Ne lève jamais : une erreur réseau renvoie <c>{Error}</c> (le
    /// JS n'affiche alors simplement pas de bandeau — pas de bruit).
    /// </remarks>
    public class UpdateApiService : BaseApiService
    {
        /// <summary>Dépôt GitHub du plugin (owner/repo), servi par le workflow release.yml.</summary>
        private const string RepoSlug = "reneboulard/LLM_AI";

        /// <summary>API GitHub : dernière release (tag + assets zip).</summary>
        private const string ReleasesLatestUrl =
            "https://api.github.com/repos/" + RepoSlug + "/releases/latest";

        /// <summary>GitHub exige un User-Agent sur ses appels API (403 sinon).</summary>
        private const string UserAgent = "LLM_AI-Emby-Plugin";

        /// <summary>Timeout court : le bandeau n'est pas critique, pas de requête qui traîne.</summary>
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        /// <summary>TTL du cache (résultat partagé, une ouverture de page par heure suffit).</summary>
        private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

        // Cache statique partagé entre requêtes (sous verrou) : une entrée
        // récente (erreur comprise) est renvoyée telle quelle jusqu'au TTL —
        // un GitHub indisponible 5 minutes ne doit pas déclencher un appel
        // API par ouverture de page. Null + _cachedAt ancien = premier appel.
        private static readonly object _lock = new object();
        private static DateTimeOffset _cachedAt = DateTimeOffset.MinValue;
        private static UpdateResponse _cached;

        // ------------------------------------------------------------------
        //  DTO requête / réponse
        // ------------------------------------------------------------------

        /// <summary>
        /// Requête GET <c>/Plugins/LLMAI/Update</c>.
        /// <c>Force</c> : "1"/"true" = bypass le cache d'une heure (debug).
        /// Chaîne (pas bool) car le binder DTO d'Emby rejette "0"/"1" sur un
        /// bool (même convention que <see cref="TonightApiService"/>).
        /// </summary>
        [Route("/Plugins/LLMAI/Update", "GET")]
        public class UpdateRequest : IReturn<object>
        {
            public string Force { get; set; }
        }

        /// <summary>
        /// Réponse renvoyée au navigateur. <c>Current</c> : version installée
        /// (version de l'assembly — la même que celle qu'Emby affiche).
        /// <c>Latest</c> : tag de la dernière release GitHub (sans le « v »).
        /// <c>Available</c> : true si <c>Latest &gt; Current</c>.
        /// <c>ReleaseUrl</c> : page humaine de la release (lien du bandeau) ;
        /// <c>ZipUrl</c> : asset <c>LLM_AI-&lt;version&gt;.zip</c> (usage
        /// manuel — le plugin ne télécharge rien). <c>CheckedAt</c> : date
        /// (UTC ISO) de la vérification GitHub (pas celle du cache lecture).
        /// </summary>
        public class UpdateResponse
        {
            public string Current { get; set; }
            public string Latest { get; set; }
            public bool Available { get; set; }
            public string ReleaseUrl { get; set; }
            public string ZipUrl { get; set; }
            public string CheckedAt { get; set; }
            public string Error { get; set; }
        }

        // ------------------------------------------------------------------
        //  Handler GET
        // ------------------------------------------------------------------

        public async Task<object> Get(UpdateRequest req)
        {
            // Version installée : version de l'assembly (pilotée par
            // <AssemblyVersion> du .csproj — BasePlugin.Version, sealed, est
            // renseignée par l'hôte depuis cette même valeur).
            string current = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "";

            bool force = IsTrue(req?.Force);
            lock (_lock)
            {
                if (!force && _cached != null && DateTimeOffset.UtcNow - _cachedAt < CacheTtl)
                    return CloneFor(_cached, current);
            }

            var res = await FetchLatestAsync().ConfigureAwait(false);
            res.Current = current;

            // Disponibilité : comparaison de versions (Latest > Current), pas
            // d'égalité de chaînes (le tag peut être 3 segments « 1.2.0 »
            // contre un assembly 4 segments « 1.2.0.0 »).
            res.Available = IsNewer(res.Latest, current);

            lock (_lock)
            {
                _cached = res;
                _cachedAt = DateTimeOffset.UtcNow;
            }
            return CloneFor(res, current);
        }

        /// <summary>
        /// Appelle l'API GitHub <c>releases/latest</c> et extrait tag, lien de
        /// release et asset zip. Ne lève jamais : toute erreur (réseau, JSON,
        /// 404 si aucune release) → <c>{Error}</c> et champs vides.
        /// </summary>
        private static async Task<UpdateResponse> FetchLatestAsync()
        {
            try
            {
                using (var reqMsg = new HttpRequestMessage(HttpMethod.Get, ReleasesLatestUrl))
                {
                    // GitHub rejette (403) les appels API sans User-Agent.
                    reqMsg.Headers.UserAgent.ParseAdd(UserAgent);
                    reqMsg.Headers.Accept.ParseAdd("application/vnd.github+json");

                    using (var resp = await _http.SendAsync(reqMsg).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode)
                            return new UpdateResponse
                            {
                                Error = "GitHub a répondu " + (int)resp.StatusCode + "."
                            };

                        using (var doc = await JsonDocument
                            .ParseAsync(await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            .ConfigureAwait(false))
                        {
                            var root = doc.RootElement;
                            string tag = root.TryGetProperty("tag_name", out var tg) && tg.ValueKind == JsonValueKind.String
                                ? tg.GetString() : null;
                            string releaseUrl = root.TryGetProperty("html_url", out var hu) && hu.ValueKind == JsonValueKind.String
                                ? hu.GetString() : null;

                            // Asset zip : LLM_AI-<version>.zip (le workflow
                            // n'en joint qu'un, mais on filtre par extension).
                            string zipUrl = null;
                            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var a in assets.EnumerateArray())
                                {
                                    if (a.TryGetProperty("browser_download_url", out var du)
                                        && du.ValueKind == JsonValueKind.String)
                                    {
                                        var u = du.GetString();
                                        if (!string.IsNullOrEmpty(u) && u.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                                        {
                                            zipUrl = u;
                                            break;
                                        }
                                    }
                                }
                            }

                            if (string.IsNullOrWhiteSpace(tag))
                                return new UpdateResponse { Error = "Réponse GitHub sans tag de version." };

                            return new UpdateResponse
                            {
                                Latest = tag.Trim().TrimStart('v', 'V'),
                                ReleaseUrl = releaseUrl ?? ("https://github.com/" + RepoSlug + "/releases/latest"),
                                ZipUrl = zipUrl ?? "",
                                CheckedAt = DateTimeOffset.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture)
                            };
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return new UpdateResponse { Error = "Vérification GitHub impossible : " + ex.Message };
            }
        }

        /// <summary>
        /// Latest &gt; Current ? Compare des <see cref="Version"/> (2 à 4
        /// segments) ; un tag non parsable n'est jamais « plus récent » (on
        /// ne veut pas d'un faux positif sur un tag exotique).
        /// </summary>
        private static bool IsNewer(string latest, string current)
        {
            if (string.IsNullOrWhiteSpace(latest) || string.IsNullOrWhiteSpace(current)) return false;
            Version l, c;
            if (!Version.TryParse(latest.Trim().TrimStart('v', 'V'), out l)) return false;
            if (!Version.TryParse(current.Trim(), out c)) return false;
            return l > c;
        }

        /// <summary>
        /// Réponse sérialisable retournée à l'appelant : le cache porte une
        /// instance partagée — on en renvoie une copie avec la version
        /// courante recalculée (l'assembly ne change pas sans redémarrage,
        /// mais on ne renvoie jamais l'objet verrouillé tel quel).
        /// </summary>
        private static UpdateResponse CloneFor(UpdateResponse src, string current)
        {
            return new UpdateResponse
            {
                Current = current,
                Latest = src.Latest,
                Available = IsNewer(src.Latest, current),
                ReleaseUrl = src.ReleaseUrl,
                ZipUrl = src.ZipUrl,
                CheckedAt = src.CheckedAt,
                Error = src.Error
            };
        }

        /// <summary>
        /// Interprète un flag query-string permissivement ("1"/"true"/"yes"/"on")
        /// — même convention que <see cref="TonightApiService"/>.
        /// </summary>
        private static bool IsTrue(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            switch (s.Trim().ToLowerInvariant())
            {
                case "1":
                case "true":
                case "yes":
                case "on":
                    return true;
                default:
                    return false;
            }
        }
    }
}