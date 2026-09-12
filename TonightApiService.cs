using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Services;

namespace LLM_AI
{
    /// <summary>
    /// Endpoint HTTP plugin « À regarder ce soir » : expose
    /// <c>GET /Plugins/LLMAI/Tonight</c> à la page Recommandations pour produire
    /// une recommandation personnalisée par usager, à la demande. Couche HTTP
    /// fine : résout l'usager appelant puis délègue la génération à
    /// <see cref="TonightService"/> (logique partagée avec le déclencheur de
    /// login <c>TonightLoginService</c> — même cache par usager).
    /// </summary>
    /// <remarks>
    /// Service ServiceStack découvert par scanning d'assembly : hérite
    /// <see cref="BaseApiService"/> (propriétés DI peuplées par l'hôte :
    /// Logger, UserManager, LibraryManager, ApplicationHost, AuthorizationContext,
    /// Request…) et injecte via constructeur les services non exposés par la
    /// base : <see cref="ILiveTvManager"/> (EPG/timers) et
    /// <see cref="IJsonSerializer"/> (run LLM). La route est portée par le DTO
    /// requête <see cref="TonightRequest"/> via <see cref="RouteAttribute"/>.
    /// </remarks>
    public class TonightApiService : BaseApiService
    {
        private readonly ILiveTvManager _liveTv;
        private readonly IJsonSerializer _json;
        private readonly ICollectionManager _collections;
        private readonly IPlaylistManager _playlists;
        private readonly IUserDataManager _userData;

        public TonightApiService(ILiveTvManager liveTv, IJsonSerializer json,
            ICollectionManager collections, IPlaylistManager playlists,
            IUserDataManager userData)
        {
            _liveTv = liveTv;
            _json = json;
            _collections = collections;
            _playlists = playlists;
            _userData = userData;
        }

        // ------------------------------------------------------------------
        //  DTO requête
        // ------------------------------------------------------------------

        /// <summary>
        /// Requête GET <c>/Plugins/LLMAI/Tonight</c>.
        /// <c>UserId</c> : optionnel — l'usager pour lequel produire la reco
        /// (défaut : l'usager authentifié par le token). Un usager non-admin ne
        /// peut consulter que son propre historique. <c>Refresh</c> : "1"/"true"
        /// = bypass le cache et force un nouveau run LLM. Chaîne (pas bool) car
        /// le binder DTO d'Emby appelle <c>Boolean.Parse</c> qui rejette "0"/"1"
        /// (n'accepte que "True"/"False") — on parse manuellement.
        /// </summary>
        [Route("/Plugins/LLMAI/Tonight", "GET")]
        public class TonightRequest : IReturn<object>
        {
            public string UserId { get; set; }
            public string Refresh { get; set; }
        }

        /// <summary>
        /// Réponse renvoyée au navigateur. <c>Items</c> est le payload JSON
        /// (tableau de recommandations) en chaîne — le JS le <c>JSON.parse</c>
        /// comme il le fait déjà pour <c>Recommendations</c>. <c>Date</c> :
        /// date/heure (UTC ISO) de production. <c>FromCache</c> : true si
        /// servi depuis le cache. <c>Enabled</c> : false si la section est
        /// désactivée en config (le JS n'affiche alors pas la section).
        /// <c>CanRecord</c>/<c>CanLiveTv</c> (v1.13.12.0) : droits du run
        /// (policy de l'usager résolu, lue à chaud) — la page masque le bouton
        /// « Programmer » sans le droit d'enregistrement et « Regarder en
        /// direct » sans le droit TV en direct (les cartes tonight sont servies
        /// par CETTE route, pas par /Recos).
        /// </summary>
        public class TonightResponse
        {
            public bool Enabled { get; set; }
            public string Items { get; set; }
            public string Date { get; set; }
            public bool FromCache { get; set; }
            public string Error { get; set; }
            /// <summary>Origin « chat » : ce payload a été produit par un run
            /// déclenché depuis le chat (tool run_tonight_run) — la page
            /// affiche un badge discret « générée via chat ».</summary>
            public bool ViaChat { get; set; }
            /// <summary>Directives de session du run chat (informatif, badge).
            /// Vide pour un run normal.</summary>
            public string ChatDirectives { get; set; }
            /// <summary>Droit d'enregistrement de l'usager (policy
            /// EnableLiveTvManagement, fail-closed).</summary>
            public bool CanRecord { get; set; }
            /// <summary>Droit TV en direct de l'usager (policy
            /// EnableLiveTvAccess, fail-closed).</summary>
            public bool CanLiveTv { get; set; }
            /// <summary>Note non fatale (v1.13.15.0) : policy parentale
            /// restrictive (limite de cote / tags / liste blanche) et moins de
            /// recos que le minimum — affichée à l'usager pour expliquer une
            /// liste courte. Null si aucune note.</summary>
            public string Warning { get; set; }
        }

        // ------------------------------------------------------------------
        //  Handler GET
        // ------------------------------------------------------------------

        public async Task<object> Get(TonightRequest req)
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null)
                return new TonightResponse { Enabled = false, Error = "Configuration du plugin indisponible." };

            if (!cfg.TonightEnabled)
                return new TonightResponse { Enabled = false };

            bool refresh = IsTrue(req.Refresh);

            // Résolution de l'usager : priorité au token (AuthorizationContext),
            // puis au UserId passé en param — en vérifiant qu'un usager non-admin
            // ne consulted que son propre historique.
            User user = ResolveUser(req);
            if (user == null)
                return new TonightResponse { Enabled = true, Error = "Utilisateur non authentifié." };

            // Génération partagée (cache par usager, builders, run LLM, enrich).
            // Le CancellationToken vient de la requête HTTP.
            var ct = Request?.CancellationToken ?? CancellationToken.None;
            var svc = new TonightService(UserManager, LibraryManager, _liveTv, _json, ApplicationHost, Logger, _collections, _playlists, _userData);

            // Cache complet (méta origin chat) sauf Refresh : TryGetCachedResult
            // renvoie le TonightResult caché (payload + badge via_chat) ; sinon
            // on relance le pipeline comme avant.
            var cached = refresh ? null : TonightService.TryGetCachedResult(user.Id.ToString());
            if (cached.HasValue)
                return new TonightResponse
                {
                    Enabled = true,
                    Items = cached.Value.Payload,
                    Date = cached.Value.Date,
                    FromCache = true,
                    ViaChat = cached.Value.ViaChat,
                    ChatDirectives = cached.Value.ChatDirectives,
                    CanRecord = PermissionGate.CanRecordLive(user),
                    CanLiveTv = PermissionGate.CanWatchLive(user),
                    Warning = cached.Value.Warning
                };

            var res = await svc.GenerateTonightAsync(user, cfg, refresh, ct).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(res.Error))
                return new TonightResponse { Enabled = true, Error = res.Error, Date = res.Date, FromCache = res.FromCache };

            return new TonightResponse
            {
                Enabled = true,
                Items = res.Payload,
                Date = res.Date,
                FromCache = res.FromCache,
                ViaChat = res.ViaChat,
                ChatDirectives = res.ChatDirectives,
                CanRecord = PermissionGate.CanRecordLive(user),
                CanLiveTv = PermissionGate.CanWatchLive(user),
                Warning = res.Warning
            };
        }

        /// <summary>
        /// Interprète un flag query-string permissivement : "1", "true", "yes",
        /// "on" (casse insensible) → true ; tout le reste (null, "0", "false",
        /// "") → false. Évite le binder bool d'Emby qui rejette "0"/"1".
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

        // ------------------------------------------------------------------
        //  Auth : résolution de l'usager appelant
        // ------------------------------------------------------------------

        /// <summary>
        /// Résout l'usager à partir du token d'authentification (prioritaire) et
        /// du <c>UserId</c> éventuel. Garantit qu'un usager non-administrateur ne
        /// peut consulter que son propre historique : si <c>UserId</c> désigne un
        /// autre usager sans droit admin, on refuse (renvoie null).
        /// </summary>
        private User ResolveUser(TonightRequest req)
        {
            User authUser = null;
            try
            {
                var auth = AuthorizationContext?.GetAuthorizationInfo(Request);
                authUser = auth?.User;
                if (authUser == null && auth != null && auth.UserId != 0)
                    authUser = UserManager.GetUserById(auth.UserId);
            }
            catch { /* tolérant : on repli sur UserId */ }

            if (string.IsNullOrWhiteSpace(req.UserId))
            {
                // Token sans usager (clé API : auth=ok mais User=NULL, UserId=0).
                // Repli sur le premier usager — usage domestique : une clé API est
                // créée par l'admin et vaut accès complet ; le premier usager est
                // l'usager principal du foyer. Le navigateur, lui, passe toujours
                // userId=usager courant (résolution explicite ci-dessous).
                if (authUser != null) return authUser;
                try
                {
                    var users = UserManager.GetUserList(new UserQuery());
                    if (users != null && users.Length > 0)
                    {
                        Logger?.Info("[LLM_AI] Tonight : token sans usager (clé API ?) — repli sur le premier usager « {0} ».", users[0].Name);
                        return users[0];
                    }
                }
                catch { }
                return null;
            }

            User requested = null;
            try { requested = UserManager.GetUserById(req.UserId); }
            catch { /* id invalide */ }
            if (requested == null) return authUser;

            if (authUser == null)
                return requested; // pas de token : on trust UserId (auth Emby globale couvre)

            if (requested.Id == authUser.Id)
                return requested;

            // Un autre usager : autorisé seulement si l'appelant est admin.
            bool isAdmin = false;
            try { isAdmin = authUser.Policy?.IsAdministrator ?? false; } catch { }
            return isAdmin ? requested : null;
        }
    }
}