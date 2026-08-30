using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
    /// Endpoint HTTP plugin « Chat interactif » : expose
    /// <c>POST /Plugins/LLMAI/Chat</c> pour que la page de config envoie un
    /// message à l'LLM et reçoive sa réponse, en conversation multi-tours.
    /// Couche HTTP fine, calquée sur <see cref="AuditApiService"/> : résout
    /// l'usager appelant (admin uniquement), convertit l'historique JSON du
    /// corps en messages LLM, puis délègue le tour à
    /// <see cref="LlmRunner.RunChatAsync"/> (backends LLM partagés selon les
    /// priorités usager, TOUS les outils existants — aucun outil nouveau).
    /// </summary>
    /// <remarks>
    /// Le serveur est <b>stateless</b> : la page garde l'historique de la
    /// conversation en JS et re-poste à chaque tour
    /// <c>{Message, History:[{role,content}... ]}</c>. Le system prompt —
    /// documentation complète des outils + directives RAG — est construit
    /// serveur-side, injecté UNE fois en tête de conversation et jamais
    /// renvoyé par le client ; seuls les rôles user/assistant du corps sont
    /// rejoués (voir <see cref="LlmAgentService.RunChatAsync"/>).
    /// Service ServiceStack découvert par scanning d'assembly, hérite
    /// <see cref="BaseApiService"/> ; la route est portée par le DTO
    /// <see cref="ChatRequest"/> via <see cref="RouteAttribute"/>.
    /// </remarks>
    public class ChatApiService : BaseApiService
    {
        private readonly ISessionManager _sessions;
        private readonly ITaskManager _tasks;
        private readonly INotificationManager _notifications;
        private readonly IJsonSerializer _json;
        private readonly ILiveTvManager _liveTv;

        public ChatApiService(ISessionManager sessions, ITaskManager tasks,
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
        /// Un tour rejoué de la conversation (côté page JS). Seuls
        /// <c>role="user"</c> et <c>role="assistant"</c> sont acceptés et
        /// rejoués — tout autre rôle est ignoré serveur-side.
        /// </summary>
        public class ChatTurn
        {
            public string Role { get; set; }
            public string Content { get; set; }
        }

        /// <summary>
        /// Requête POST <c>/Plugins/LLMAI/Chat</c>. <c>Message</c> : nouveau
        /// message de l'usager. <c>History</c> : tours précédents de la
        /// conversation (user/assistant), maintenus par la page et re-postés
        /// à chaque appel (serveur stateless).
        /// </summary>
        [Route("/Plugins/LLMAI/Chat", "POST")]
        public class ChatRequest : IReturn<object>
        {
            public string Message { get; set; }
            public List<ChatTurn> History { get; set; }
        }

        /// <summary>
        /// Réponse renvoyée au navigateur. <c>Reply</c> : réponse Markdown de
        /// l'agent (rendue côté config.js via le mini-convertisseur Markdown).
        /// <c>Date</c> : date/heure (UTC ISO) de production.
        /// <c>Enabled</c> : false si le chat est désactivé en config.
        /// <c>Error</c> : message (ex. accès non-admin, message vide).
        /// </summary>
        public class ChatResponse
        {
            public bool Enabled { get; set; }
            public string Reply { get; set; }
            public string Date { get; set; }
            public string Error { get; set; }
        }

        // ------------------------------------------------------------------
        //  Handler POST
        // ------------------------------------------------------------------

        public async Task<object> Post(ChatRequest req)
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null)
                return new ChatResponse { Enabled = false, Error = "Configuration du plugin indisponible." };

            if (!cfg.ChatEnabled)
                return new ChatResponse { Enabled = false };

            // Réservé aux administrateurs : le chat expose l'état du serveur
            // (system_audit : sessions, chemins, disques, journaux) et
            // consomme des tokens LLM — pas pour un usager ordinaire.
            var admin = ResolveAdmin();
            bool isAdmin = admin?.Policy?.IsAdministrator ?? false;
            if (!isAdmin)
                return new ChatResponse { Enabled = true, Error = "Réservé aux administrateurs." };

            string message = (req?.Message ?? string.Empty).Trim();
            if (message.Length == 0)
                return new ChatResponse { Enabled = true, Error = "Message vide." };

            // Historique re-posté par la page → messages LLM. La page ne
            // stocke que les tours user/assistant (textes finaux) ; on
            // re-filtre par rôle par défense en profondeur (RunChatAsync
            // borne de nouveau et re-valide).
            var history = new List<LlmClient.ChatMessage>();
            if (req.History != null)
            {
                foreach (var t in req.History)
                {
                    if (t == null || string.IsNullOrWhiteSpace(t.Content)) continue;
                    if (!string.Equals(t.Role, "user", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(t.Role, "assistant", StringComparison.OrdinalIgnoreCase)) continue;
                    history.Add(new LlmClient.ChatMessage { Role = t.Role, Content = t.Content });
                }
            }

            var ct = Request?.CancellationToken ?? CancellationToken.None;
            // LlmRunner construit avec les services de la base + liveTv,
            // exactement comme sur le path d'audit.
            var runner = new LlmRunner(Logger, _json, LibraryManager, UserManager, _liveTv, ApplicationHost);
            string reply = await runner.RunChatAsync(cfg, "CHAT", history, message,
                _sessions, _tasks, _notifications, ct).ConfigureAwait(false);

            return new ChatResponse
            {
                Enabled = true,
                Reply = reply,
                Date = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            };
        }

        // ------------------------------------------------------------------
        //  Auth : résolution de l'administrateur appelant
        // ------------------------------------------------------------------

        /// <summary>
        /// Résout l'usager à partir du token d'authentification. Calqué sur
        /// <see cref="AuditApiService"/> : priorité au User du token, puis au
        /// UserId (Int64) du token. Retourne null si non authentifié.
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