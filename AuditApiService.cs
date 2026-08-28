using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Services;
using MediaBrowser.Model.Tasks;

namespace LLM_AI
{
    /// <summary>
    /// Endpoint HTTP plugin « Audit santé » : expose
    /// <c>GET /Plugins/LLMAI/Audit</c> à la page de config pour produire un
    /// rapport de santé du serveur Emby à la demande. Couche HTTP fine,
    /// calquée sur <see cref="TonightApiService"/> : résout l'usager appelant
    /// (admin uniquement), construit le prompt d'audit (template config +
    /// focus optionnel) puis délègue le run agent à
    /// <see cref="LlmRunner.RunAuditAsync"/> (backends LLM partagés, outil
    /// <c>system_audit</c> dédié). Retourne le rapport Markdown brut.
    /// </summary>
    /// <remarks>
    /// Service ServiceStack découvert par scanning d'assembly : hérite
    /// <see cref="BaseApiService"/> (propriétés DI peuplées par l'hôte :
    /// Logger, UserManager, LibraryManager, ApplicationHost,
    /// AuthorizationContext, Request) et injecte via constructeur les
    /// services d'audit non exposés par la base :
    /// <see cref="ISessionManager"/>, <see cref="ITaskManager"/>,
    /// <see cref="INotificationManager"/>, <see cref="IJsonSerializer"/> et
    /// <see cref="ILiveTvManager"/> (ce dernier uniquement pour construire
    /// <see cref="LlmRunner"/> proprement — inutilisé sur le path d'audit).
    /// La route est portée par le DTO requête <see cref="AuditRequest"/> via
    /// <see cref="RouteAttribute"/>.
    /// </remarks>
    public class AuditApiService : BaseApiService
    {
        private readonly ISessionManager _sessions;
        private readonly ITaskManager _tasks;
        private readonly INotificationManager _notifications;
        private readonly IJsonSerializer _json;
        private readonly ILiveTvManager _liveTv;

        public AuditApiService(ISessionManager sessions, ITaskManager tasks,
            INotificationManager notifications, IJsonSerializer json,
            ILiveTvManager liveTv)
        {
            _sessions = sessions;
            _tasks = tasks;
            _notifications = notifications;
            _json = json;
            _liveTv = liveTv;
        }

        // ------------------------------------------------------------------
        //  DTO requête / réponse
        // ------------------------------------------------------------------

        /// <summary>
        /// Requête GET <c>/Plugins/LLMAI/Audit</c>.
        /// <c>Focus</c> : texte libre optionnel pour orienter l'audit (ex.
        /// « transcoding », « disk », ou une demande explicite de remédiation
        /// comme « arrête la session XYZ »). Appendé au template de prompt
        /// <see cref="PluginConfiguration.AuditPrompt"/>.
        /// </summary>
        [Route("/Plugins/LLMAI/Audit", "GET")]
        public class AuditRequest : IReturn<object>
        {
            public string Focus { get; set; }
        }

        /// <summary>
        /// Réponse renvoyée au navigateur. <c>Report</c> est le rapport
        /// Markdown brut produit par l'agent (rendu côté config.js via un
        /// mini-convertisseur Markdown→HTML sûr). <c>Date</c> : date/heure
        /// (UTC ISO) de production. <c>Enabled</c> : false si l'audit est
        /// désactivé en config. <c>Error</c> : message (ex. accès non-admin).
        /// </summary>
        public class AuditResponse
        {
            public bool Enabled { get; set; }
            public string Report { get; set; }
            public string Date { get; set; }
            public string Error { get; set; }
        }

        // ------------------------------------------------------------------
        //  Handler GET
        // ------------------------------------------------------------------

        public async Task<object> Get(AuditRequest req)
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null)
                return new AuditResponse { Enabled = false, Error = "Configuration du plugin indisponible." };

            if (!cfg.AuditEnabled)
                return new AuditResponse { Enabled = false };

            // Réservé aux administrateurs : un audit santé expose l'état du
            // serveur (sessions, chemins, disques) et peut exécuter des actions
            // de remédiation — on ne laisse pas un usager ordinaire l'invoquer.
            var admin = ResolveAdmin();
            bool isAdmin = admin?.Policy?.IsAdministrator ?? false;
            if (!isAdmin)
                return new AuditResponse { Enabled = true, Error = "Réservé aux administrateurs." };

            // Prompt = template config + focus optionnel (l'orientation ou la
            // demande explicite de remédiation de l'usager).
            string prompt = cfg.AuditPrompt ?? string.Empty;
            string focus = req?.Focus;
            if (!string.IsNullOrWhiteSpace(focus))
                prompt += "\n\n### Focus demandé\n" + focus.Trim();

            var ct = Request?.CancellationToken ?? CancellationToken.None;
            // LlmRunner construit avec les services de la base + liveTv (pour
            // satisfaire le constructeur ; inutilisé sur le path d'audit).
            var runner = new LlmRunner(Logger, _json, LibraryManager, UserManager, _liveTv, ApplicationHost);
            string report = await runner.RunAuditAsync(cfg, "AUDIT", prompt, _sessions, _tasks, _notifications, ct)
                .ConfigureAwait(false);

            return new AuditResponse
            {
                Enabled = true,
                Report = report,
                Date = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            };
        }

        // ------------------------------------------------------------------
        //  Auth : résolution de l'administrateur appelant
        // ------------------------------------------------------------------

        /// <summary>
        /// Résout l'usager à partir du token d'authentification. Calqué sur
        /// <c>TonightApiService.ResolveUser</c> : priorité au User du token,
        /// puis au UserId (Int64) du token. Retourne null si non authentifié.
        /// L'appelant vérifie ensuite <see cref="User.Policy"/>'s IsAdministrator.
        /// </summary>
        private User ResolveAdmin()
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