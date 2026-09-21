using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Controller.Playlists;
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
        private readonly ICollectionManager _collections;
        private readonly IPlaylistManager _playlists;

        public ChatApiService(ISessionManager sessions, ITaskManager tasks,
            INotificationManager notifications, IJsonSerializer json,
            ILiveTvManager liveTv, ICollectionManager collections,
            IPlaylistManager playlists)
        {
            _sessions = sessions;
            _tasks = tasks;
            _notifications = notifications;
            _json = json;
            _liveTv = liveTv;
            _collections = collections;
            _playlists = playlists;
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
        /// à chaque appel (serveur stateless). <c>Session</c> : identifiant
        /// de session de mémoire de conversation (retourné par la première
        /// réponse puis rejoué ; vide = nouvelle conversation). <c>Context</c>
        /// : identifiant du contexte déroulant choisi dans la page (v1.13.8,
        /// liste blanche <see cref="ChatContexts"/> ; vide = aucun).
        /// </summary>
        [Route("/Plugins/LLMAI/Chat", "POST")]
        public class ChatRequest : IReturn<object>
        {
            public string Message { get; set; }
            public List<ChatTurn> History { get; set; }
            public string Session { get; set; }
            public string Context { get; set; }
        }

        /// <summary>
        /// Réponse renvoyée au navigateur. <c>Reply</c> : réponse Markdown de
        /// l'agent (rendue côté config.js via le mini-convertisseur Markdown).
        /// <c>Date</c> : date/heure (UTC ISO) de production. <c>Session</c> :
        /// identifiant de session de la mémoire de conversation (à rejouer
        /// aux tours suivants). <c>Enabled</c> : false si le chat est
        /// désactivé en config. <c>Error</c> : message (ex. accès non-admin,
        /// message vide).
        /// </summary>
        public class ChatResponse
        {
            public bool Enabled { get; set; }
            public string Reply { get; set; }
            public string Date { get; set; }
            public string Session { get; set; }
            public string Error { get; set; }
            /// <summary>Libellés des actions réussies du tour (v1.13.4) —
            /// affichés par la page de chat sous la réponse (le toast Emby
            /// n'est pas rendu sur la page de config). Null si aucune.</summary>
            public List<string> Actions { get; set; }
            /// <summary>Proposition de modification de prompt en attente
            /// d'approbation créée par le tool <c>plugin_prompts</c> pendant
            /// ce tour (v1.13.8) — la page rend la carte de diff
            /// Approuver/Refuser. Null si aucune.</summary>
            public PendingApprovalInfo Pending { get; set; }
            /// <summary>Actions Emby déposées par les tools d'action pendant
            /// ce tour (v1.13.29, deux phases) — la page rend une carte
            /// « Approuver / Refuser » par proposition ; l'exécution passe
            /// par les endpoints ChatAction, jamais par le LLM. Null si
            /// aucune.</summary>
            public List<ChatPendingActionInfo> PendingActions { get; set; }
        }

        /// <summary>
        /// Descriptif d'une action Emby en attente d'approbation (deux
        /// phases v1.13.29) : le clic n'envoie QUE <see cref="ActionId"/> —
        /// l'outil et ses arguments restent figés côté serveur
        /// (<see cref="ChatActionStore"/>, expiration 10 min, liaison
        /// usager+session à l'approbation).
        /// </summary>
        public class ChatPendingActionInfo
        {
            public string ActionId { get; set; }
            public string Tool { get; set; }
            /// <summary>Libellé français de la proposition (ton des toasts
            /// de traçabilité).</summary>
            public string Label { get; set; }
        }

        /// <summary>
        /// Descriptif d'une écriture de prompt en attente d'approbation
        /// (two-phase) : la page reçoit l'ancien et le nouveau texte pour
        /// rendre la carte de diff ; le clic n'envoie QUE
        /// <see cref="ActionId"/> — les paramètres de l'écriture restent
        /// côté serveur (<see cref="ChatPromptStore"/>, expiration 10 min).
        /// </summary>
        public class PendingApprovalInfo
        {
            public string ActionId { get; set; }
            public string Field { get; set; }
            public string Label { get; set; }
            public string OldText { get; set; }
            public string NewText { get; set; }
            /// <summary>Avertissement de divergence (null si aucun) : le
            /// nouveau texte recouvre peu le texte courant — la carte de
            /// diff l'affiche en bandeau (v1.13.9).</summary>
            public string Warning { get; set; }
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

            // Contexte déroulant (v1.13.8) : résolu contre le registre
            // statique (liste blanche) — un id inconnu est simplement ignoré
            // (fail-open : aucun bloc injecté, jamais d'erreur de tour).
            string contextId = (req?.Context ?? string.Empty).Trim();
            string contextBlock = ChatContexts.BuildBlock(cfg, contextId, ApplicationHost);
            if (contextBlock.Length > 0)
                Logger.Info("[LLM_AI] [CHAT] Contexte « {0} » actif ({1} caractères injectés).",
                    contextId, contextBlock.Length);

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
            var userId = admin.Id.ToString();

            // Mémoire de conversation (opt-in ChatMemoryEnabled) : résolution
            // de session. Session connue → continuation ; inconnue/vide →
            // nouvelle conversation (les sessions passées jamais résumées
            // sont condensées paresseusement, en tâche de fond).
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
                if (memoryBlock.Length > 0)
                    Logger.Info("[LLM_AI] [CHAT] Mémoire de conversation injectée ({0} caractères).",
                        memoryBlock.Length);
            }

            // LlmRunner construit avec les services de la base + liveTv,
            // exactement comme sur le path d'audit.
            var runner = new LlmRunner(Logger, _json, LibraryManager, UserManager, _liveTv, ApplicationHost);

            // Couche d'action du chat (v1.13) : budget > 0 → outils d'action
            // (mêmes primitives que le plugin) + bloc de workflow annonçant
            // budget et étiquette de confirmation. 0 = lecture seule
            // (strictement le comportement pré-v1.13).
            // Le tour (un message = un tour) ouvre le compteur de budget.
            ChatActions.BeginTurn(sessionId, Logger);
            List<ILlmTool> actionTools = null;
            string actionsWorkflow = null;
            if (cfg.ChatActionBudget > 0)
            {
                actionTools = ChatActions.BuildTools(cfg, sessionId, admin,
                    LibraryManager, _liveTv, _collections, _playlists,
                    UserManager, ApplicationHost, Logger, _json, _sessions);
                actionsWorkflow = ChatActions.BuildWorkflowBlock(cfg);
                Logger.Info("[LLM_AI] [CHAT] Couche d'action active : {0} outil(s), budget {1}/tour.",
                    actionTools.Count, cfg.ChatActionBudget);
            }

            // Édition de prompts par le chat (v1.13.8, opt-in) : le tool
            // plugin_prompts (list/get/set two-phase) — l'écriture passe par
            // l'approbation, jamais par le LLM. Indépendant du budget
            // d'actions (une approbation n'est pas une action Emby).
            if (cfg.ChatPromptsEnabled)
            {
                actionTools ??= new List<ILlmTool>();
                actionTools.Add(new ChatPromptsTool(cfg, sessionId, userId, contextId, Logger));
                Logger.Info("[LLM_AI] [CHAT] Édition de prompts active (plugin_prompts).");
            }

            string reply;
            try
            {
                reply = await runner.RunChatAsync(cfg, "CHAT", history, message,
                    _sessions, _tasks, _notifications, ct, memoryBlock,
                    actionTools, actionsWorkflow, contextBlock).ConfigureAwait(false);
            }
            // Requête avortée (déconnexion client, timeout page) : répondre un
            // JSON propre au lieu de laisser l'OperationCanceledException
            // remonter en 500 ServiceStack — vécu 2026-09-05 : l'ajax d'Emby
            // rejette alors la Response brute et la page affichait
            // « [object Response] ». (Si le client a vraiment fermé la
            // connexion, la réponse est perdue — sans conséquence.)
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Annulation DÉPASSANT la requête HTTP (timeout HttpClient LLM) :
                // le client est encore là → on lui répond.
                Logger.Info("[LLM_AI] [CHAT] Requête annulée (délai backend LLM) — réponse d'erreur envoyée.");
                return new ChatResponse { Enabled = true, Error = "Le LLM n'a pas répondu à temps (délai dépassé). Réessayez." };
            }
            catch (OperationCanceledException)
            {
                // Déconnexion client : ni réponse ni 500 — juste le log.
                Logger.Info("[LLM_AI] [CHAT] Requête annulée (client déconnecté).");
                throw;
            }

            // ------------------------------------------------------------------
            //  Filet structurel anti-différence (v1.13.9.11) : en mode d'édition,
            //  certains modèles (vécu gemma4:26b) répondent en posant une
            //  question de validation (« Souhaitez-vous que je prépare le
            //  texte ? ») au lieu de livrer le texte révisé — malgré les
            //  règles injectées (CommonRules + description du tool). Un rappel
            //  PONCTUEL suffit (l'instruction ponctuelle est suivie là où la
            //  règle de fond est ignorée — constaté côté page v1.13.9.8) :
            //  on le rejoue donc automatiquement ici, une seule fois, pour que
            //  l'usager n'ait JAMAIS à rappeler le format. Déclencheur :
            //  réponse sans AUCUNE clôture ET qui se termine par une question
            //  (le pattern exact de la déférence — une vraie réponse Q&A ou
            //  une annonce « voici le texte actuel » ne se terminent pas par
            //  une question posée à l'usager). Un set déjà soumis (proposition
            //  en attente) ou une réponse d'échec ne déclenchent pas le filet.
            //  ------------------------------------------------------------------
            if (contextBlock.Length > 0 && cfg.ChatPromptsEnabled
                && !string.IsNullOrWhiteSpace(reply)
                && !reply.StartsWith("Échec du chat", StringComparison.Ordinal)
                && ChatPromptStore.PeekPagePending(sessionId) == null
                && reply.IndexOf("```", StringComparison.Ordinal) < 0
                && reply.TrimEnd().EndsWith("?", StringComparison.Ordinal))
            {
                Logger.Info("[LLM_AI] [CHAT] Filet prose : réponse-question sans clôture en mode " +
                    "d'édition — nudge automatique (une seule fois).");
                var nudge = "[Admin] Ne demandez pas de validation : livrez MAINTENANT, dans votre " +
                    "prochaine réponse, le texte COMPLET du prompt révisé dans un bloc de code clôturé " +
                    "```text … ``` (une seule clôture, en tout dernier du message). L'approbation existe " +
                    "déjà : la carte de diff Approuver/Refuser de la page porte ce bloc.";
                var nudgedHistory = new List<LlmClient.ChatMessage>(history)
                {
                    new LlmClient.ChatMessage { Role = "user", Content = message },
                    new LlmClient.ChatMessage { Role = "assistant", Content = reply },
                    new LlmClient.ChatMessage { Role = "user", Content = nudge }
                };
                try
                {
                    string nudged = await runner.RunChatAsync(cfg, "CHAT-NUDGE", nudgedHistory,
                        nudge, _sessions, _tasks, _notifications, ct, memoryBlock,
                        actionTools, actionsWorkflow, contextBlock).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(nudged)
                        && !nudged.StartsWith("Échec du chat", StringComparison.Ordinal))
                    {
                        reply = nudged;
                        Logger.Info("[LLM_AI] [CHAT] Filet prose : réponse corrigée fournie.");
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Timeout backend LLM sur le nudge : on rend la réponse
                    // d'origine (déférente) plutôt qu'une erreur — l'usager a
                    // le bouton de secours de la page en dernier ressort.
                    Logger.Info("[LLM_AI] [CHAT] Filet prose : nudge annulé (délai backend) — réponse d'origine rendue.");
                }
                catch (OperationCanceledException) { throw; }
            }

            // Journalise le tour (usager + assistant) dans la session —
            // seulement en cas de SUCCÈS : un tour raté n'est pas rejoué par
            // la page (retry possible) et ne doit pas polluer la mémoire.
            string savedSession = sessionId;
            if (cfg.ChatMemoryEnabled && !string.IsNullOrWhiteSpace(reply)
                && !reply.StartsWith("Échec du chat", StringComparison.Ordinal))
            {
                savedSession = ChatMemoryStore.RecordTurn(cfg, userId, existingSession != null ? sessionId : "",
                    fromUser: true, text: message, logger: Logger);
                ChatMemoryStore.RecordTurn(cfg, userId, savedSession,
                    fromUser: false, text: reply, logger: Logger);
            }

            // Libellés des actions réussies du tour (succès seulement) :
            // affichés sous la réponse par la page de chat — le toast Emby
            // DisplayMessage n'est pas rendu par le client web sur cette
            // page de configuration (constat 2026-09-06).
            var turnActions = ChatActions.TakeActions(sessionId);

            // Proposition de prompt en attente créée pendant le tour
            // (plugin_prompts set) : la page rend la carte de diff.
            var pendingInfo = (PendingApprovalInfo)null;
            var pending = ChatPromptStore.TakePagePending(sessionId);
            if (pending != null)
            {
                pendingInfo = new PendingApprovalInfo
                {
                    ActionId = pending.ActionId,
                    Field = pending.Field,
                    Label = pending.Label,
                    OldText = pending.OldText,
                    NewText = pending.NewText,
                    Warning = pending.Warn
                };
            }

            // Actions Emby déposées pendant le tour (v1.13.29, deux phases) :
            // la page rend une carte Approuver/Refuser par proposition —
            // l'exécution passe par les endpoints ChatAction, jamais par le
            // LLM (la page ne parse jamais le texte du LLM pour les
            // découvrir).
            var pendingCards = ChatActionStore.TakePagePending(sessionId);
            var pendingActions = pendingCards.Count > 0
                ? pendingCards.Select(a => new ChatPendingActionInfo
                    {
                        ActionId = a.ActionId,
                        Tool = a.Tool,
                        Label = a.Label
                    }).ToList()
                : null;

            return new ChatResponse
            {
                Enabled = true,
                Reply = reply,
                Date = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Session = savedSession,
                Actions = turnActions.Count > 0 ? turnActions : null,
                Pending = pendingInfo,
                PendingActions = pendingActions
            };
        }

        // ------------------------------------------------------------------
        //  Mémoire de conversation : condensation paresseuse (déléguée à
        //  <see cref="ChatSummarizer"/> depuis v1.13.21 — partagée avec le
        //  chat externe) + endpoints
        // ------------------------------------------------------------------

        /// <summary>
        /// <c>GET /Plugins/LLMAI/ChatMemory</c> — la session la plus récente
        /// de l'admin appelant (id, date, résumé, derniers tours) pour le
        /// bouton « Reprendre la conversation » de la page chat. Déclenche
        /// aussi la condensation paresseuse des sessions jamais résumées.
        /// Réservé aux administrateurs.
        /// </summary>
        [Route("/Plugins/LLMAI/ChatMemory", "GET")]
        public class ChatMemoryGetRequest : IReturn<object>
        {
        }

        public class ChatMemoryInfo
        {
            public string Id { get; set; }
            public string Date { get; set; }
            public int Turns { get; set; }
            public bool HasSummary { get; set; }
            public string Summary { get; set; }
            public List<ChatTurn> Last { get; set; }
        }

        public class ChatMemoryGetResponse
        {
            public bool Enabled { get; set; }
            public string Error { get; set; }
            public ChatMemoryInfo Current { get; set; }
        }

        public object Get(ChatMemoryGetRequest req)
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null)
                return new ChatMemoryGetResponse { Error = "Configuration du plugin indisponible." };

            var admin = ResolveAdmin();
            bool isAdmin = admin?.Policy?.IsAdministrator ?? false;
            if (!isAdmin)
                return new ChatMemoryGetResponse { Error = "Réservé aux administrateurs." };

            if (!cfg.ChatMemoryEnabled)
                return new ChatMemoryGetResponse { Enabled = false };

            var userId = admin.Id.ToString();
            ChatSummarizer.KickSummarizeStale(cfg, userId, Logger, _json,
                LibraryManager, UserManager, _liveTv, ApplicationHost);

            var latest = ChatMemoryStore.ListSessions(cfg, userId).FirstOrDefault(s => s != null && s.Turns > 0);
            if (latest == null)
                return new ChatMemoryGetResponse { Enabled = true, Current = null };

            var last = new List<ChatTurn>();
            foreach (var t in latest.Messages.TakeLast(ChatMemoryStore.KeepTurnsAfterSummary))
            {
                if (t == null || string.IsNullOrWhiteSpace(t.T)) continue;
                last.Add(new ChatTurn
                {
                    Role = t.R == "u" ? "user" : "assistant",
                    Content = t.T
                });
            }
            return new ChatMemoryGetResponse
            {
                Enabled = true,
                Current = new ChatMemoryInfo
                {
                    Id = latest.Id,
                    Date = latest.Date.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                    Turns = latest.Turns,
                    HasSummary = !string.IsNullOrWhiteSpace(latest.Summary),
                    Summary = latest.Summary ?? "",
                    Last = last
                }
            };
        }

        /// <summary>
        /// <c>POST /Plugins/LLMAI/ChatMemory/Forget</c> — oublie les sessions
        /// de l'admin appelant (toutes, ou <c>Session</c> précisée). Réservé
        /// aux administrateurs.
        /// </summary>
        [Route("/Plugins/LLMAI/ChatMemory/Forget", "POST")]
        public class ChatMemoryForgetRequest : IReturn<object>
        {
            public string Session { get; set; }
        }

        public object Post(ChatMemoryForgetRequest req)
        {
            var admin = ResolveAdmin();
            bool isAdmin = admin?.Policy?.IsAdministrator ?? false;
            if (!isAdmin)
                return new ChatMemoryGetResponse { Error = "Réservé aux administrateurs." };

            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null || !cfg.ChatMemoryEnabled)
                return new ChatMemoryGetResponse { Enabled = false };

            var userId = admin.Id.ToString();
            var session = (req?.Session ?? string.Empty).Trim();
            if (session.Length > 0)
            {
                ChatSummarizer.ForgetActive(session);
            }
            ChatMemoryStore.Forget(cfg, userId, session, Logger);
            Logger.Info("[LLM_AI] [CHAT] Mémoire de conversation oubliée ({0}).",
                session.Length > 0 ? session : "toutes les sessions");
            return new ChatMemoryGetResponse { Enabled = true };
        }

        // ------------------------------------------------------------------
        //  Contextes déroulants + approbation des prompts (v1.13.8)
        // ------------------------------------------------------------------

        /// <summary>
        /// <c>GET /Plugins/LLMAI/ChatContexts</c> — la liste des contextes
        /// prédéfinis disponibles pour le menu déroulant de la page chat
        /// (registre statique <see cref="ChatContexts"/>). Réservé aux
        /// administrateurs.
        /// </summary>
        [Route("/Plugins/LLMAI/ChatContexts", "GET")]
        public class ChatContextsRequest : IReturn<object>
        {
        }

        public class ChatContextInfo
        {
            public string Id { get; set; }
            public string Label { get; set; }
        }

        public class ChatContextsResponse
        {
            public bool Enabled { get; set; }
            public string Error { get; set; }
            public List<ChatContextInfo> Contexts { get; set; }
        }

        public object Get(ChatContextsRequest req)
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null || !cfg.ChatEnabled)
                return new ChatContextsResponse { Enabled = false };

            var admin = ResolveAdmin();
            bool isAdmin = admin?.Policy?.IsAdministrator ?? false;
            if (!isAdmin)
                return new ChatContextsResponse { Enabled = true, Error = "Réservé aux administrateurs." };

            var list = new List<ChatContextInfo>();
            foreach (var c in ChatContexts.All)
                list.Add(new ChatContextInfo { Id = c.Id, Label = c.Label });
            return new ChatContextsResponse { Enabled = true, Contexts = list };
        }

        /// <summary>
        /// <c>POST /Plugins/LLMAI/ChatPrompt/Approve</c> — approbation par
        /// l'admin d'une modification de prompt proposée par le chat. Le
        /// navigateur n'envoie QUE <c>ActionId</c> : le champ, l'ancien et
        /// le nouveau texte viennent du store serveur
        /// (<see cref="ChatPromptStore"/>, expiration 10 min, action liée à
        /// la session ET à l'usager approbateur) — anti-détournement.
        /// L'écriture est exécutée en C# déterministe
        /// (<see cref="ChatPromptsTool.SetPrompt"/> +
        /// <c>SaveConfiguration</c>) ; le LLM n'a aucun rôle dans
        /// l'exécution.
        /// </summary>
        [Route("/Plugins/LLMAI/ChatPrompt/Approve", "POST")]
        public class ChatPromptApproveRequest : IReturn<object>
        {
            public string ActionId { get; set; }
        }

        public class ChatPromptDecisionResponse
        {
            public bool Ok { get; set; }
            public string Field { get; set; }
            public string Label { get; set; }
            /// <summary>Comment tester la nouvelle directive, selon le champ
            /// (chemin réel quand il existe, simulation sinon) — affiché par
            /// la page dans la carte et poussé dans le fil pour le LLM.</summary>
            public string TestHint { get; set; }
            public string Error { get; set; }
        }

        public object Post(ChatPromptApproveRequest req)
        {
            var admin = ResolveAdmin();
            bool isAdmin = admin?.Policy?.IsAdministrator ?? false;
            if (!isAdmin)
                return new ChatPromptDecisionResponse { Error = "Réservé aux administrateurs." };

            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null)
                return new ChatPromptDecisionResponse { Error = "Configuration du plugin indisponible." };

            var action = ChatPromptStore.Consume(req?.ActionId, RequestSessionHint(),
                admin.Id.ToString(), Logger);
            if (action == null)
                return new ChatPromptDecisionResponse { Error =
                    "Action introuvable ou expirée (attente valable 10 minutes) — demandez à nouveau la sauvegarde dans la conversation." };

            string text = (action.NewText ?? string.Empty).Trim();
            if (!ChatPromptsTool.IsKnownField(action.Field) || text.Length == 0 ||
                text.Length > ChatPromptsTool.MaxPromptChars)
                return new ChatPromptDecisionResponse { Error = "Proposition invalide — rien n'a été écrit." };

            ChatPromptsTool.SetPrompt(cfg, action.Field, text);
            Plugin.Instance.SaveConfiguration();
            Logger.Info("[LLM_AI] Chat prompts : champ « {0} » écrasé par approbation de l'admin " +
                "(action_id={1}, {2} caractères).", action.Field, action.ActionId, text.Length);

            return new ChatPromptDecisionResponse
            {
                Ok = true,
                Field = action.Field,
                Label = action.Label ?? ChatPromptsTool.LabelOf(action.Field),
                TestHint = ChatPromptsTool.TestHintFor(action.Field)
            };
        }

        /// <summary>
        /// <c>POST /Plugins/LLMAI/ChatPrompt/Refuse</c> — retire l'action en
        /// attente (la conversation peut continuer, une nouvelle proposition
        /// créera un nouveau pending). Le refus n'écrit rien.
        /// </summary>
        [Route("/Plugins/LLMAI/ChatPrompt/Refuse", "POST")]
        public class ChatPromptRefuseRequest : IReturn<object>
        {
            public string ActionId { get; set; }
        }

        public object Post(ChatPromptRefuseRequest req)
        {
            var admin = ResolveAdmin();
            bool isAdmin = admin?.Policy?.IsAdministrator ?? false;
            if (!isAdmin)
                return new ChatPromptDecisionResponse { Error = "Réservé aux administrateurs." };

            ChatPromptStore.Discard(req?.ActionId, Logger);
            return new ChatPromptDecisionResponse { Ok = true };
        }

        // ------------------------------------------------------------------
        //  Approbation des ACTIONS EMBY du chat (v1.13.29, deux phases)
        // ------------------------------------------------------------------

        /// <summary>
        /// <c>POST /Plugins/LLMAI/ChatAction/Approve</c> — exécute l'action
        /// Emby déposée en attente par un tool d'action (v1.13.29). Le LLM
        /// n'a AUCUN rôle dans cette exécution : le pending figé porte
        /// l'outil ET ses arguments, la consommation vérifie usager + session
        /// (anti-rejeu single-use, TTL 10 min), puis
        /// <see cref="ChatActions.BuildToolByName"/> reconstruit l'outil et
        /// <see cref="IChatActionTool.ExecuteCoreAsync"/> exécute en code
        /// serveur déterministe — budget consommé À CE MOMENT (pas au dépôt),
        /// garde-fous métier inchangés, toast de traçabilité habituel.
        /// Réservé aux administrateurs.
        /// </summary>
        [Route("/Plugins/LLMAI/ChatAction/Approve", "POST")]
        public class ChatActionApproveRequest : IReturn<object>
        {
            public string ActionId { get; set; }
        }

        public class ChatActionDecisionResponse
        {
            public bool Ok { get; set; }
            public string Label { get; set; }
            /// <summary>Détail du résultat d'exécution (affiché sur la carte
            /// et poussé dans le fil pour le LLM).</summary>
            public string Detail { get; set; }
            public string Error { get; set; }
        }

        public async Task<object> Post(ChatActionApproveRequest req)
        {
            var admin = ResolveAdmin();
            bool isAdmin = admin?.Policy?.IsAdministrator ?? false;
            if (!isAdmin)
                return new ChatActionDecisionResponse { Error = "Réservé aux administrateurs." };

            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null)
                return new ChatActionDecisionResponse { Error = "Configuration du plugin indisponible." };

            var cfgBudget = Math.Max(0, cfg.ChatActionBudget);
            if (cfgBudget <= 0)
                return new ChatActionDecisionResponse { Error =
                    "La couche d'action du chat est désactivée (budget 0) — rien n'a été exécuté." };

            var action = ChatActionStore.Consume(req?.ActionId, RequestSessionHint(),
                admin.Id.ToString());
            if (action == null)
                return new ChatActionDecisionResponse { Error =
                    "Action introuvable ou expirée (attente valable 10 minutes) — demandez à nouveau l'action dans la conversation." };

            var tool = ChatActions.BuildToolByName(action.Tool, cfg, action.Session, admin,
                admin.Id.ToString(), LibraryManager, _liveTv, _collections, _playlists,
                UserManager, ApplicationHost, Logger, _json);
            if (tool == null)
                return new ChatActionDecisionResponse { Error =
                    "Outil d'action indisponible ou inconnu — proposition invalide." };

            JsonElement args = default;
            try
            {
                using var doc = JsonDocument.Parse(action.ArgsJson ?? "{}");
                args = doc.RootElement.Clone();
            }
            catch { args = default; }

            string result;
            try
            {
                result = await tool.ExecuteCoreAsync(args,
                    Request?.CancellationToken ?? CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new ChatActionDecisionResponse { Error = "Exécution annulée." };
            }
            catch (Exception ex)
            {
                Logger.ErrorException("[LLM_AI] Chat action : échec d'exécution approuvée (action_id={0}, tool={1}, usager={2}) : {3}",
                    ex, action.ActionId, action.Tool, admin.Name, ex.Message);
                result = JsonSerializer.Serialize(new { status = "failed", detail = ex.Message });
            }

            // Le core retourne le JSON canonique {status, detail} — extrait
            // pour la carte (le LLM le recevra via la note [Admin] de la page).
            string status = "failed";
            string detail = null;
            try
            {
                using var doc2 = JsonDocument.Parse(result ?? "{}");
                var root = doc2.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String)
                        status = st.GetString() ?? "failed";
                    if (root.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String)
                        detail = d.GetString();
                }
            }
            catch { detail = result; }

            Logger.Info("[LLM_AI] Chat action : approbation admin (action_id={0}, tool={1}, usager={2}) → {3}.",
                action.ActionId, action.Tool, admin.Name, status);

            bool ok = string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase);
            return new ChatActionDecisionResponse
            {
                Ok = ok,
                Label = action.Label,
                Detail = ok ? (detail ?? "Action exécutée.") : null,
                Error = ok ? null : (detail ?? "Échec de l'exécution — consultez le journal du serveur.")
            };
        }

        /// <summary>
        /// <c>POST /Plugins/LLMAI/ChatAction/Refuse</c> — retire l'action en
        /// attente sans l'exécuter (nettoyage légitime par tout admin, comme
        /// le refus des prompts ; n'exige pas la session).
        /// </summary>
        [Route("/Plugins/LLMAI/ChatAction/Refuse", "POST")]
        public class ChatActionRefuseRequest : IReturn<object>
        {
            public string ActionId { get; set; }
        }

        public object Post(ChatActionRefuseRequest req)
        {
            var admin = ResolveAdmin();
            bool isAdmin = admin?.Policy?.IsAdministrator ?? false;
            if (!isAdmin)
                return new ChatActionDecisionResponse { Error = "Réservé aux administrateurs." };

            ChatActionStore.Discard(req?.ActionId);
            return new ChatActionDecisionResponse { Ok = true };
        }

        /// <summary>Indice de session pour la consommation d'un pending :
        /// la page rejoue son id de session à chaque tour — l'endpoint
        /// d'approbation accepte le même champ optionnel (query
        /// <c>session</c>) pour lier le clic à la session émettrice (vide =
        /// indicateur absent, la vérification usager reste). Lecture par
        /// énumération défensive : l'indexeur de QueryParamCollection sur
        /// clé absente n'est pas contractuellement null-safe.</summary>
        private string RequestSessionHint()
        {
            try
            {
                var qs = Request?.QueryString;
                if (qs == null) return "";
                foreach (var nv in qs)
                {
                    if (nv == null || !string.Equals(nv.Name, "session", StringComparison.OrdinalIgnoreCase))
                        continue;
                    return (nv.Value ?? "").Trim();
                }
                return "";
            }
            catch { return ""; }
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