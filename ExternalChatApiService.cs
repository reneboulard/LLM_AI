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
using MediaBrowser.Controller.Tasks;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Services;
using MediaBrowser.Model.Tasks;

namespace LLM_AI
{
    /// <summary>
    /// Endpoints HTTP du <b>chat externe</b> (v1.13.21) : l'app compagnon
    /// (script Python autonome fourni dans <c>chat-external/</c>, sur le
    /// MÊME hôte, derrière son propre login)
    /// parle à l'agent LLM au nom d'un usager Emby résolu, et peut projeter
    /// la fiche d'un item sur le client Emby actif de CET usager.
    /// <list type="bullet">
    /// <item><c>POST /Plugins/LLMAI/ChatExternal</c> — un tour de chat
    ///   ({Token, User, Message, History[], Session}). Stateless comme le
    ///   chat admin : l'app maintient l'historique user/assistant et le
    ///   re-poste à chaque tour.</item>
    /// <item><c>POST /Plugins/LLMAI/Show</c> — navigation
    ///   ({Token, User, ItemId}) : la session active de l'usager reçoit
    ///   <c>DisplayContent</c> (repli <c>DisplayMessage</c> si le client ne
    ///   la déclare pas — cf. validé live 2026-09-13 sur « Emby for
    ///   Android » : route REST <c>POST /Sessions/{Id}/Command</c>, le
    ///   plugin, lui, passe par <c>ISessionManager.SendGeneralCommand</c>).</item>
    /// </list>
    /// Sécurité (design validé 2026-09-13) :
    /// <list type="bullet">
    /// <item><c>[Unauthenticated]</c> (pas d'auth Emby sur la route) — la
    ///   gate EST le service : opt-in <see cref="PluginConfiguration.ExternalChatEnabled"/>,
    ///   secret dédié comparé à temps constant (pattern
    ///   <see cref="StrmSecret"/> de <see cref="ActivateApiService"/>, jamais
    ///   le même secret qu'un autre canal), <b>requête loopback uniquement
    ///   SANS X-Forwarded-For</b> (le seul appel légitime part de l'app
    ///   compagnon locale vers Emby directement — toute requête passée par le reverse
    ///   proxy porte un XFF et est rejetée, même si elle arrive du
    ///   loopback), usager Emby résolu par son nom ET présent dans la liste
    ///   blanche <see cref="PluginConfiguration.ExternalChatUsers"/> — les
    ///   administrateurs sont TOUJOURS rejetés sur ce chemin.</item>
    /// <item><b>Lecture seule</b> : aucun tool d'action, aucun
    ///   <c>system_audit</c>, aucun <c>plugin_prompts</c> — une fuite du
    ///   secret ne donne aucune capacité d'écriture sur le serveur.</item>
    /// <item><b>Parental</b> : tout item vu par le LLM passe la policy
    ///   parentale de l'usager résolu (<see cref="PermissionGate"/> — get_emby_info
    ///   reçoit <c>ParentalUser</c>, bloc de contrainte injecté dans le
    ///   prompt) ; la navigation refuse un item non autorisé (fail-closed,
    ///   ne révèle rien).</item>
    /// <item><b>Session bornée</b> : la navigation ne cible que la session
    ///   active DONT l'usager est celui de la requête — un usager listé ne
    ///   peut ni agir sur la session d'un autre ni lui montrer un contenu.</item>
    /// </list>
    /// Service ServiceStack découvert par scanning d'assembly, hérite
    /// <see cref="BaseApiService"/> ; calqué sur <see cref="ChatApiService"/>
    /// (historique, mémoire de conversation via <see cref="ChatMemoryStore"/>,
    /// condensation paresseuse via <see cref="ChatSummarizer"/>).
    /// </summary>
    public class ExternalChatApiService : BaseApiService
    {
        private readonly ISessionManager _sessions;
        private readonly ITaskManager _tasks;
        private readonly INotificationManager _notifications;
        private readonly IJsonSerializer _json;
        private readonly ILiveTvManager _liveTv;

        public ExternalChatApiService(ISessionManager sessions, ITaskManager tasks,
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
        //  Plafonds
        // ------------------------------------------------------------------

        /// <summary>Longueur maximale d'un message externe (caractères) —
        /// borne le coût par appel (l'app compagnon est la seule source, la
        /// borne protège d'une boucle déréglée côté app).</summary>
        internal const int MaxMessageChars = 2000;

        /// <summary>Bloc de workflow INJECTÉ UNIQUEMENT dans le chat externe
        /// (paramètre extraWorkflow de RunChatAsync — le chat admin est
        /// inchangé). Sans donner au LLM le moindre tool d'action : le
        /// bouton de projection appartient à l'app compagnon, qui appelle
        /// /Plugins/LLMAI/Show (gate + policy parentale serveur) quand
        /// l'usager clique le lien profond émis par le LLM. Le LLM apprend
        /// seulement À QUOI sert le bouton, pour orienter l'usager au lieu
        /// de répondre « je ne peux rien afficher ».</summary>
        internal const string ExternalWorkflowBlock =
            "\n### BOUTON DE PROJECTION ET COMMANDES CLIENT (CHAT EXTERNE)\n" +
            "Cette conversation s'affiche dans l'app compagnon du foyer : chaque " +
            "lien fiche que tu émets y est rendu comme un bouton de projection. " +
            "Quand l'usager demande d'afficher un contenu sur son client Emby " +
            "(« mets-le à l'écran », « montre-moi sur la TV »), émets le lien " +
            "profond du titre et indique-lui de cliquer sur le bouton à côté du " +
            "titre : c'est LUI qui projette la fiche sur son client Emby actif. " +
            "Tu peux AUSSI envoyer toi-même des commandes non destructives à " +
            "SON client actif avec l'outil client_command (fiche, lecture, " +
            "pause, volume) — mais jamais rien de destructeur ni sur un autre " +
            "appareil. N'affirme jamais que tu affiches toi-même la fiche via " +
            "un lien : le lien ouvre la fiche dans la page, le bouton ou " +
            "client_command fait la projection. Si aucun client Emby de " +
            "l'usager n'est actif, signale-le simplement.";

        // ------------------------------------------------------------------
        //  DTO requête / réponse — ChatExternal
        // ------------------------------------------------------------------

        // ------------------------------------------------------------------
        //  DTO requête / réponse — ChatExternal
        // ------------------------------------------------------------------

        /// <summary>Un tour rejoué de la conversation (maintenu par l'app
        /// compagnon). Seuls user/assistant sont acceptés et rejoués.</summary>
        public class ExtChatTurn
        {
            public string Role { get; set; }
            public string Content { get; set; }
        }

        /// <summary>
        /// Requête POST <c>/Plugins/LLMAI/ChatExternal</c>. <c>Token</c> :
        /// secret partagé (<see cref="PluginConfiguration.ExternalChatSecret"/>).
        /// <c>User</c> : nom d'usager Emby (mêmes noms dans les deux apps ;
        /// doit figurer dans <see cref="PluginConfiguration.ExternalChatUsers"/>).
        /// <c>Message</c> : nouveau message. <c>History</c> : tours
        /// précédents (stateless). <c>Session</c> : id de session de mémoire
        /// de conversation (retourné puis rejoué ; vide = nouvelle).
        /// </summary>
        [Route("/Plugins/LLMAI/ChatExternal", "POST")]
        [Unauthenticated]
        public class ChatExternalRequest : IReturn<object>
        {
            public string Token { get; set; }
            public string User { get; set; }
            public string Message { get; set; }
            public List<ExtChatTurn> History { get; set; }
            public string Session { get; set; }
        }

        /// <summary>Réponse du chat externe. <c>Reply</c> : réponse Markdown
        /// de l'agent. <c>Date</c> : UTC ISO. <c>Session</c> : id de session
        /// de mémoire à rejouer (si mémoire active). <c>Error</c> : message
        /// (gate, usager, etc.).</summary>
        public class ChatExternalResponse
        {
            public bool Enabled { get; set; }
            public string Reply { get; set; }
            public string Date { get; set; }
            public string Session { get; set; }
            public string Error { get; set; }
        }

        // ------------------------------------------------------------------
        //  DTO requête / réponse — Show (navigation)
        // ------------------------------------------------------------------

        /// <summary>
        /// Requête POST <c>/Plugins/LLMAI/Show</c> — projette la fiche d'un
        /// item sur le client Emby actif de l'usager (le LLM, en chat
        /// externe, émet l'id de chaque item qu'il projette — cf. couche de
        /// deep links v1.13.9). Réponses : <c>Ok</c>, <c>Device</c> (nom du
        /// client ciblé), <c>Command</c> (DisplayContent |
        /// DisplayMessage), <c>Error</c>.
        /// </summary>
        [Route("/Plugins/LLMAI/Show", "POST")]
        [Unauthenticated]
        public class ExternalShowRequest : IReturn<object>
        {
            public string Token { get; set; }
            public string User { get; set; }
            public string ItemId { get; set; }
        }

        public class ExternalShowResponse
        {
            public bool Ok { get; set; }
            public string Device { get; set; }
            public string Command { get; set; }
            public string Error { get; set; }
        }

        // ------------------------------------------------------------------
        //  Handler — tour de chat externe
        // ------------------------------------------------------------------

        public async Task<object> Post(ChatExternalRequest req)
        {
            var ct = Request?.CancellationToken ?? CancellationToken.None;
            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null)
                return new ChatExternalResponse { Error = "Configuration du plugin indisponible." };

            if (!cfg.ExternalChatEnabled)
                return new ChatExternalResponse { Enabled = false, Error = "Chat externe désactivé." };

            // Gate commune (origine + secret + usager). Toute erreur est une
            // réponse JSON — jamais de 401/403 ServiceStack (l'app compagnon lit le
            // champ Error, et un scan n'apprend rien du serveur).
            string gateError = GateError(cfg, req?.Token);
            if (gateError != null)
                return new ChatExternalResponse { Enabled = true, Error = gateError };

            var user = ResolveAllowedUser(cfg, req?.User);
            if (user == null)
                return new ChatExternalResponse { Enabled = true, Error = "Usager non autorisé pour le chat externe." };

            string message = (req?.Message ?? string.Empty).Trim();
            if (message.Length == 0)
                return new ChatExternalResponse { Enabled = true, Error = "Message vide." };
            if (message.Length > MaxMessageChars)
                return new ChatExternalResponse { Enabled = true, Error =
                    "Message trop long (" + MaxMessageChars + " caractères maximum)." };

            string userId = user.Id.ToString();

            // Historique re-posté par l'app → messages LLM (défense en
            // profondeur : re-filtrage des rôles, comme le chat admin).
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

            // Mémoire de conversation (même store que le chat admin, clé
            // d'usager = l'usager résolu — mémoire par personne).
            string sessionId = (req?.Session ?? string.Empty).Trim();
            var existingSession = cfg.ChatMemoryEnabled && sessionId.Length > 0
                ? ChatMemoryStore.Find(cfg, userId, sessionId)
                : null;
            string memoryBlock = string.Empty;
            if (cfg.ChatMemoryEnabled)
            {
                if (existingSession == null)
                    ChatSummarizer.KickSummarizeStale(cfg, userId, Logger, _json,
                        LibraryManager, UserManager, _liveTv, ApplicationHost);
                memoryBlock = ChatMemoryStore.BuildInjectionBlock(cfg, userId,
                    existingSession != null ? sessionId : string.Empty);
            }

            var runner = new LlmRunner(Logger, _json, LibraryManager, UserManager,
                _liveTv, ApplicationHost);

            // Contrainte parentale ANNONCÉE (pattern Watch Tonight v1.13.12 :
            // le dire en amont, l'imposer en aval — get_emby_info reçoit le
            // ParentalUser qui l'impose mécaniquement de toute façon).
            var parentalNote = PermissionGate.DescribeForPrompt(user) ?? "";

            string reply;
            try
            {
                reply = await runner.RunChatAsync(cfg, "CHAT-EXT", history, message,
                    _sessions, _tasks, _notifications, ct, memoryBlock,
                    null, ExternalWorkflowBlock, parentalNote, user, false).ConfigureAwait(false);
            }
            // Même sémantique que le chat admin : annulation DÉPASSANT la
            // requête (timeout backend LLM) → JSON propre ; déconnexion du
            // client (l'app compagnon) → log seul, pas de 500.
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Logger.Info("[LLM_AI] [CHAT-EXT] Requête annulée (délai backend LLM) — usager {0}.",
                    user.Name);
                return new ChatExternalResponse { Enabled = true, Error =
                    "Le LLM n'a pas répondu à temps (délai dépassé). Réessayez." };
            }
            catch (OperationCanceledException)
            {
                Logger.Info("[LLM_AI] [CHAT-EXT] Requête annulée (client déconnecté) — usager {0}.",
                    user.Name);
                throw;
            }

            // Journalise le tour (succès seulement — même règle que le chat
            // admin : un tour raté n'est pas rejoué).
            string savedSession = sessionId;
            if (cfg.ChatMemoryEnabled && !string.IsNullOrWhiteSpace(reply)
                && !reply.StartsWith("Échec du chat", StringComparison.Ordinal))
            {
                savedSession = ChatMemoryStore.RecordTurn(cfg, userId,
                    existingSession != null ? sessionId : "",
                    fromUser: true, text: message, logger: Logger);
                ChatMemoryStore.RecordTurn(cfg, userId, savedSession,
                    fromUser: false, text: reply, logger: Logger);
            }

            Logger.Info("[LLM_AI] [CHAT-EXT] Tour externe — usager {0}, session {1}, {2} caractères.",
                user.Name, savedSession, reply?.Length ?? 0);

            return new ChatExternalResponse
            {
                Enabled = true,
                Reply = reply,
                Date = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Session = savedSession
            };
        }

        // ------------------------------------------------------------------
        //  Handler — Show (navigation d'un client Emby)
        // ------------------------------------------------------------------

        public async Task<object> Post(ExternalShowRequest req)
        {
            var ct = Request?.CancellationToken ?? CancellationToken.None;
            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null)
                return new ExternalShowResponse { Error = "Configuration du plugin indisponible." };

            if (!cfg.ExternalChatEnabled)
                return new ExternalShowResponse { Error = "Chat externe désactivé." };

            string gateError = GateError(cfg, req?.Token);
            if (gateError != null)
                return new ExternalShowResponse { Error = gateError };

            var user = ResolveAllowedUser(cfg, req?.User);
            if (user == null)
                return new ExternalShowResponse { Error = "Usager non autorisé pour le chat externe." };

            // Item : forme REST (InternalId long) ou Guid hérité — résolution
            // tolérante commune (ItemIdResolver).
            var item = ItemIdResolver.Resolve(LibraryManager, (req?.ItemId ?? "").Trim());
            if (item == null)
                return new ExternalShowResponse { Error = "Item introuvable." };

            // Double porte : la policy parentale de l'usager jugé est la loi
            // — un item refusé n'est projeté nulle part, et la raison n'est
            // pas révélée (cote, tag, unrated : aucun indice renvoyé).
            if (PermissionGate.IsParentallyAllowed(user, item) != PermissionGate.ParentalVerdict.Allowed)
            {
                Logger.Info("[LLM_AI] [CHAT-EXT] Show refusé (parental) — usager {0}, item {1}.",
                    user.Name, item.Name);
                return new ExternalShowResponse { Error = "Cet item n'est pas autorisé pour cet usager." };
            }

            // Session bornée : la SEULE session ciblable est celle DONT
            // l'usager est celui de la requête (la session de l'admin sur la
            // TV du foyer n'est jamais touchable par une requête qui prétend
            // être un autre usager). La plus récente gagne s'il y en a
            // plusieurs (même appareil multi-fenêtres).
            var candidates = (_sessions.Sessions ?? Enumerable.Empty<SessionInfo>())
                .Where(s => s != null && SessionUserMatches(s, user))
                .OrderByDescending(s => s.LastActivityDate)
                .ToList();
            var session = candidates.FirstOrDefault();
            if (session == null)
                return new ExternalShowResponse { Error =
                    "Aucune session Emby active pour cet usager (ouvrir l'app Emby sur l'appareil)." };

            string itemIdArg = item.InternalId.ToString();
            bool hasDisplayContent = (session.SupportedCommands ?? Array.Empty<string>())
                .Any(c => string.Equals(c, "DisplayContent", StringComparison.OrdinalIgnoreCase));

            try
            {
                if (hasDisplayContent)
                {
                    var cmd = new MediaBrowser.Model.Session.GeneralCommand
                    {
                        Name = "DisplayContent",
                        Arguments = new Dictionary<string, string>
                        {
                            { "ItemId", itemIdArg }
                        }
                    };
                    await _sessions.SendGeneralCommand(null, session.Id, cmd, ct).ConfigureAwait(false);
                }
                else
                {
                    // Repli (client qui n'expose pas DisplayContent) : le
                    // toast DisplayMessage avec le titre — le mécanisme déjà
                    // éprouvé de TonightLoginService/ActivateFeedback (tout
                    // le texte dans Text, le Header n'est pas rendu).
                    var msg = new MediaBrowser.Model.Session.MessageCommand
                    {
                        Header = string.Empty,
                        Text = "🤖 " + (item.Name ?? "Fiche demandée"),
                        TimeoutMs = 8000,
                    };
                    await _sessions.SendMessageCommand(null, session.Id, msg, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Logger.ErrorException("[LLM_AI] [CHAT-EXT] Show : commande non livrée (session {0}, client {1}) : {2}",
                    ex, session.Id, session.DeviceName, ex.Message);
                return new ExternalShowResponse { Error = "Commande non livrée au client : " + ex.Message };
            }

            string command = hasDisplayContent ? "DisplayContent" : "DisplayMessage";
            Logger.Info("[LLM_AI] [CHAT-EXT] Show — usager {0}, client « {1} », item « {2} » ({3}).",
                user.Name, session.DeviceName, item.Name, command);

            return new ExternalShowResponse
            {
                Ok = true,
                Device = session.DeviceName,
                Command = command
            };
        }

        // ------------------------------------------------------------------
        //  Gate partagée
        // ------------------------------------------------------------------

        /// <summary>
        /// Gate commune aux deux endpoints, dans l'ordre : opt-in (déjà
        /// vérifié par l'appelant), <b>origine</b> (loopback — <c>IsLocal</c>
        /// ou <c>RemoteIp.IsLoopback</c> — et SANS X-Forwarded-For : le seul
        /// appel légitime part de l'app compagnon locale directement vers Emby, toute
        /// requête passée par le reverse proxy porte un XFF et est rejetée
        /// même si elle arrive du loopback), puis <b>secret</b> comparé à
        /// temps constant (secret vide = fail-closed même opt-in).
        /// Retourne null si la gate passe, sinon le message d'erreur.
        /// </summary>
        private string GateError(PluginConfiguration cfg, string token)
        {
            // Origine : loopback uniquement.
            bool local = false;
            try
            {
                if (Request != null)
                    local = Request.IsLocal
                        || (Request.RemoteIp != null && System.Net.IPAddress.IsLoopback(Request.RemoteIp));
            }
            catch { local = false; }
            if (!local)
            {
                Logger.Warn("[LLM_AI] [CHAT-EXT] Requête non locale rejetée (origine : {0}).",
                    SafeRemoteAddress());
                return "Requête non locale rejetée.";
            }

            // Toute requête passée par un reverse proxy porte un
            // X-Forwarded-For — le seul chemin légitime (app compagnon → Emby
            // direct) n'en porte jamais.
            try
            {
                var xff = Request?.XForwardedFor;
                if (!string.IsNullOrWhiteSpace(xff))
                {
                    Logger.Warn("[LLM_AI] [CHAT-EXT] Requête avec X-Forwarded-For rejetée (proxy).");
                    return "Requête non locale rejetée.";
                }
            }
            catch { /* lecture tolérante */ }

            // Secret partagé dédié (à temps constant — pattern Activate).
            if (string.IsNullOrWhiteSpace(cfg.ExternalChatSecret) ||
                !ConstantTimeEquals(token, cfg.ExternalChatSecret))
                return "Jeton invalide.";

            return null;
        }

        /// <summary>
        /// Résout l'usager Emby du chat externe : nom exact (insensible à la
        /// casse) présent dans la liste blanche
        /// <see cref="PluginConfiguration.ExternalChatUsers"/> — et JAMAIS
        /// un administrateur (le chat admin reste /Plugins/LLMAI/Chat).
        /// </summary>
        private User ResolveAllowedUser(PluginConfiguration cfg, string name)
        {
            var wanted = (name ?? "").Trim();
            if (wanted.Length == 0 || cfg?.ExternalChatUsers == null
                || cfg.ExternalChatUsers.Count == 0) return null;
            if (!cfg.ExternalChatUsers
                    .Any(u => string.Equals((u ?? "").Trim(), wanted, StringComparison.OrdinalIgnoreCase)))
                return null;
            try
            {
                var user = UserManager.GetUserList(new MediaBrowser.Model.Querying.UserQuery())
                    .FirstOrDefault(u => u != null
                        && string.Equals(u.Name, wanted, StringComparison.OrdinalIgnoreCase));
                if (user == null) return null;
                if (user.Policy?.IsAdministrator ?? false)
                {
                    Logger.Warn("[LLM_AI] [CHAT-EXT] Usager admin « {0} » refusé (chat externe).", user.Name);
                    return null;
                }
                return user;
            }
            catch { return null; }
        }

        /// <summary>La session appartient-elle à cet usager ? Comparaison
        /// tolérante sur les deux formes d'id (Guid « N » sans tirets, forme
        /// canonique, InternalId) — cf. <see cref="ItemIdResolver"/>.</summary>
        private static bool SessionUserMatches(SessionInfo s, User user)
        {
            var sid = s.UserId;
            if (string.IsNullOrWhiteSpace(sid)) return false;
            return string.Equals(sid, user.Id.ToString("N"), StringComparison.OrdinalIgnoreCase)
                || string.Equals(sid, user.Id.ToString(), StringComparison.OrdinalIgnoreCase)
                || string.Equals(sid, user.InternalId.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        private string SafeRemoteAddress()
        {
            try { return Request?.RemoteIp?.ToString() ?? "?"; }
            catch { return "?"; }
        }

        /// <summary>
        /// Comparaison à temps constant (anti-orchestration de timing) —
        /// même implémentation que <see cref="ActivateApiService"/>.
        /// </summary>
        private static bool ConstantTimeEquals(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}