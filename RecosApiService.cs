using System;
using System.Collections.Generic;
using System.Text.Json;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace LLM_AI
{
    /// <summary>
    /// Endpoint HTTP plugin « Recommandations » : expose les données de la page
    /// de recommandations aux usagers <b>non-admin</b>. La page
    /// <c>recommendations.js</c> est servie dans le menu utilisateur
    /// (<c>EnableInUserMenu</c>), mais elle lisait ses données via
    /// <c>GET /Plugins/{id}/Configuration</c> — endpoint hôte réservé ManageServer
    /// (admin) : un usager ordinaire recevait 403 et la page ne rendait rien.
    /// Ce service expose les mêmes données en lecture via une route plugin
    /// authentifiée standard (aucune élévation de privilège : on ne sert que
    /// les champs Recommendations / RecommendationsDate, jamais la config
    /// complète — pas de clés API, chemins ni prompts).
    /// </summary>
    /// <remarks>
    /// Trois routes :
    /// <list type="bullet">
    /// <item><c>GET /Plugins/LLMAI/Recos</c> — dernières recommandations de la
    /// tâche planifiée (payload brut + date), pour tout usager authentifié ;</item>
    /// <item><c>POST /Plugins/LLMAI/Forget {Title}</c> — bouton « Oublier » :
    /// ajoute le titre à la drop list persistante <c>DroppedTitles</c>. Était
    /// fait côté page par un round-trip config admin (get/update), donc 403
    /// pour un non-admin ; l'écriture se fait maintenant serveur-side via
    /// <see cref="MediaBrowser.Common.Plugins.BasePlugin.SaveConfiguration"/>.</item>
    /// <item><c>GET /Plugins/LLMAI/Version</c> — version courante du plugin,
    /// handshake de cache-busting du module généré <c>asset_version.js</c>.</item>
    /// </list>
    /// Service ServiceStack découvert par scanning d'assembly : hérite
    /// <see cref="BaseApiService"/> (auth Emby standard par token, aucune
    /// route [Unauthenticated]). Les routes sont portées par les DTO requête
    /// via <see cref="RouteAttribute"/>.
    /// </remarks>
    public class RecosApiService : BaseApiService
    {
        // ------------------------------------------------------------------
        //  DTO requête / réponse
        // ------------------------------------------------------------------

        /// <summary>
        /// Requête GET <c>/Plugins/LLMAI/Recos</c> (aucun paramètre : les
        /// recommandations de la tâche planifiée sont globales, partagées par
        /// tous les usagers — la section « ce soir », elle, reste par usager
        /// via <c>/Plugins/LLMAI/Tonight</c>).
        /// </summary>
        [Route("/Plugins/LLMAI/Recos", "GET")]
        public class RecosRequest : IReturn<object>
        {
        }

        /// <summary>
        /// Réponse GET : <c>Items</c> est le payload JSON (tableau de
        /// recommandations) en chaîne — le JS le <c>JSON.parse</c> comme il le
        /// faisait pour <c>cfg.Recommendations</c>. <c>Date</c> : date du
        /// dernier run (champ config <c>RecommendationsDate</c>, ISO).
        /// </summary>
        public class RecosResponse
        {
            public string Items { get; set; }
            public string Date { get; set; }
            public string Error { get; set; }
        }

        /// <summary>
        /// Requête POST <c>/Plugins/LLMAI/Forget</c> : corps
        /// <c>{"Title":"..."}</c> — titre à ajouter à la drop list.
        /// </summary>
        [Route("/Plugins/LLMAI/Forget", "POST")]
        public class ForgetRequest : IReturn<object>
        {
            public string Title { get; set; }
        }

        /// <summary>
        /// Réponse POST : <c>Added</c> true si le titre a été ajouté (false =
        /// déjà présent — l'appelant ferme la carte sans réécrire la config,
        /// même sémantique que l'ancien dedup côté page).
        /// </summary>
        public class ForgetResponse
        {
            public bool Added { get; set; }
            public string Error { get; set; }
        }

        /// <summary>
        /// Requête GET <c>/Plugins/LLMAI/Version</c> (aucun paramètre) :
        /// version courante du plugin (assembly, pilotée par <c>&lt;Version&gt;</c>
        /// du csproj). Sert au cache-busting client — le module généré
        /// <c>asset_version.js</c> compare la version du build QUI L'A SERVI
        /// à cette réponse ; en cas d'écart, le JS en cache disque est
        /// périmé → la page rafraîchit les entrées de cache HTTP puis se
        /// recharge (voir asset_version_template.js).
        /// </summary>
        [Route("/Plugins/LLMAI/Version", "GET")]
        public class VersionRequest : IReturn<object>
        {
        }

        /// <summary>
        /// Réponse GET : <c>Version</c> en chaîne (ex. « 1.1.0.0 »), vide si
        /// l'instance n'est pas résolue (le client n'agit pas sur vide).
        /// </summary>
        public class VersionResponse
        {
            public string Version { get; set; }
        }

        // ------------------------------------------------------------------
        //  Handler GET : dernières recommandations
        // ------------------------------------------------------------------

        public object Get(RecosRequest req)
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null)
                return new RecosResponse { Error = "Configuration du plugin indisponible." };

            // Lecture seule des deux champs consommés par la page — rien de la
            // config complète (clés API, prompts, chemins) ne traverse cette
            // route, contrairement à l'endpoint hôte /Configuration.
            return new RecosResponse
            {
                Items = cfg.Recommendations ?? "",
                Date = cfg.RecommendationsDate ?? ""
            };
        }

        // ------------------------------------------------------------------
        //  Handler GET : version du plugin (cache-busting client)
        // ------------------------------------------------------------------

        /// <summary>
        /// Version courante du plugin pour le handshake de cache-busting
        /// (<c>asset_version.js</c>). Lecture seule, aucune donnée
        /// sensible — un usager authentifié quelconque peut l'appeler (les
        /// pages servies dans le menu utilisateur en ont besoin).
        /// </summary>
        public object Get(VersionRequest req)
        {
            // BasePlugin.Version : sealed, renseignée par l'hôte depuis
            // <AssemblyVersion> du csproj au chargement du plugin.
            return new VersionResponse { Version = Plugin.Instance?.Version?.ToString() ?? "" };
        }

        // ------------------------------------------------------------------
        //  Handler POST : « Oublier » (drop list)
        // ------------------------------------------------------------------

        public object Post(ForgetRequest req)
        {
            var plugin = Plugin.Instance;
            var cfg = plugin?.Configuration;
            if (cfg == null)
                return new ForgetResponse { Error = "Configuration du plugin indisponible." };

            // Tout usager authentifié peut oublier un titre : le bouton est
            // affiché sur une page du menu utilisateur et la drop list est
            // globale (sémantique inchangée — admin et non-admin peuvent
            // désormais tous deux l'utiliser, au lieu du seul admin).
            // Un appel sans identité résolue (token invalide) est rejeté.
            var user = ResolveCaller();
            if (user == null)
                return new ForgetResponse { Error = "Utilisateur non authentifié." };

            string title = (req?.Title ?? "").Trim();
            if (title.Length == 0)
                return new ForgetResponse { Error = "Titre vide." };

            // Drop list : JSON array de chaînes (même format que le champ
            // éditable de la page de config et que l'ancien round-trip page).
            var arr = new List<string>();
            bool already = false;
            try
            {
                if (!string.IsNullOrWhiteSpace(cfg.DroppedTitles))
                {
                    using (var doc = JsonDocument.Parse(cfg.DroppedTitles))
                    {
                        if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var el in doc.RootElement.EnumerateArray())
                            {
                                if (el.ValueKind != JsonValueKind.String) continue;
                                var s = el.GetString() ?? "";
                                if (s.Length == 0) continue;
                                arr.Add(s);
                            }
                        }
                    }
                }
            }
            catch { /* JSON invalide : on repart d'une liste vide */ }

            // Dedup par la normalisation partagée (celle qu'utilise
            // DroppedTitlesSet pour l'exclusion EPG) : « Le Suspect » et
            // « le suspect » ne font qu'un.
            string normNew = GetEmbyInfoTool.Norm(title);
            foreach (var s in arr)
            {
                if (string.Equals(GetEmbyInfoTool.Norm(s), normNew,
                        StringComparison.OrdinalIgnoreCase))
                {
                    already = true;
                    break;
                }
            }

            if (!already)
            {
                arr.Add(title);
                cfg.DroppedTitles = JsonSerializer.Serialize(arr);
                // Best-effort, comme les autres écritures serveur-side
                // (LlmScheduledTask, StrmLibraryGenerator) : la config Emby
                // est un fichier unique réécrit en entier.
                try { plugin.SaveConfiguration(); }
                catch (Exception ex)
                {
                    Logger?.Error("[LLM_AI] Forget : échec de sauvegarde de la config : {0}", ex.Message);
                    return new ForgetResponse { Error = "Échec de sauvegarde." };
                }
            }

            return new ForgetResponse { Added = !already };
        }

        // ------------------------------------------------------------------
        //  Auth : résolution de l'usager appelant
        // ------------------------------------------------------------------

        /// <summary>
        /// Résout l'usager à partir du token d'authentification. Calqué sur
        /// <see cref="AuditApiService"/> : priorité au User du token, puis au
        /// UserId (Int64) du token. Retourne null si non authentifié.
        /// </summary>
        private User ResolveCaller()
        {
            try
            {
                var auth = AuthorizationContext?.GetAuthorizationInfo(Request);
                var user = auth?.User;
                if (user == null && auth != null && auth.UserId != 0)
                    user = UserManager.GetUserById(auth.UserId);
                return user;
            }
            catch { return null; }
        }
    }
}