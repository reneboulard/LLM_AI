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
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.IO;
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
            /// <summary>Proposition d'écriture i18n en attente d'approbation
            /// (atelier de langues, two phases v1.17.0.2) — la page rend la
            /// carte Approuver/Refuser de l'atelier ; l'écriture passe par les
            /// endpoints I18nKey, jamais par le LLM. Null si aucune.</summary>
            public PendingI18nInfo PendingI18n { get; set; }
            /// <summary>v2.20.11 : vrai quand la réponse du tour, en mode
            /// d'édition de prompt, ressemble à une RÉVISION du texte
            /// actuel (recouvrement lexical suffisant avec le prompt courant)
            /// sans contenir de bloc clôturé — la page affiche alors le
            /// bouton de secours « Préparer la sauvegarde » sous la réponse.
            /// Faux sinon (annonce, question, prose sans rapport) : aucun
            /// bouton, pour ne pas suggérer une sauvegarde qui n'existe pas.
            /// Null hors mode d'édition.</summary>
            public bool? LooksLikeRevision { get; set; }
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
            /// <summary>Diff visuel optionnel (identification / annulation) —
            /// null pour les autres actions.</summary>
            public ChatActionDiffInfo Diff { get; set; }
        }

        public class ChatActionDiffInfo
        {
            public string Kind { get; set; }
            public bool CrossKind { get; set; }
            public string ItemId { get; set; }
            public string ItemName { get; set; }
            public string ItemPath { get; set; }
            public string ItemImageUrl { get; set; }
            public string ItemLink { get; set; }
            public string CandidateTitle { get; set; }
            public string CandidateOriginalTitle { get; set; }
            public int? CandidateYear { get; set; }
            public string CandidateOverview { get; set; }
            public string CandidatePosterUrl { get; set; }
            public string CandidateTmdbLink { get; set; }
            public string ProofLevel { get; set; }
            public string ProofSource { get; set; }
            public string ProofEvidence { get; set; }
            public string TruthSource { get; set; }
            public List<string> Changes { get; set; }
            public List<string> Hints { get; set; }
            public List<string> Rejected { get; set; }
            public string Warning { get; set; }
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

        /// <summary>
        /// Descriptif d'une proposition d'écriture i18n en attente
        /// d'approbation (atelier de langues, two phases v1.17.0.2) : la page
        /// rend la carte Avant/Après Approuver/Refuser ; le clic n'envoie QUE
        /// <see cref="ActionId"/> — clé, langue et valeur restent côté serveur
        /// (<see cref="ChatI18nStore"/>, expiration 10 min).
        /// </summary>
        public class PendingI18nInfo
        {
            public string ActionId { get; set; }
            public string Key { get; set; }
            public string Lang { get; set; }
            public string Family { get; set; }
            /// <summary>Valeur au moment du dépôt (null si première écriture
            /// de cette langue — la carte l'affiche comme telle).</summary>
            public string OldValue { get; set; }
            public string NewValue { get; set; }
            /// <summary>Avertissements du dépôt (identique-EN, langue native
            /// de code…) — null si aucun.</summary>
            public string Warning { get; set; }
        }

        // ------------------------------------------------------------------
        //  Handler POST
        // ------------------------------------------------------------------

        public async Task<object> Post(ChatRequest req)
        {
            // Langue d'affichage de ce tour (v1.15.0.2) : toutes les chaînes
            // serveur affichées par la page (erreurs, libellés des cartes)
            // suivent la langue de l'interface — même bucket que les noms
            // de tâches. Résolu une fois par requête.
            string lang = I18n.ResolveDisplayLangKey(ApplicationHost);

            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null)
                return new ChatResponse { Enabled = false, Error = I18n.S("err.noconfig", lang) };

            // v2.9.0 : plus de flag ChatEnabled — le chat vit dans l'onglet
            // « Chat en direct » de la page de config (admin-only comme la
            // page). Seule la gate admin reste.

            // Réservé aux administrateurs : le chat expose l'état du serveur
            // (system_audit : sessions, chemins, disques, journaux) et
            // consomme des tokens LLM — pas pour un usager ordinaire.
            var admin = ResolveAdmin();
            bool isAdmin = admin?.Policy?.IsAdministrator ?? false;
            if (!isAdmin)
                return new ChatResponse { Enabled = true, Error = I18n.S("err.admin", lang) };

            string message = (req?.Message ?? string.Empty).Trim();
            if (message.Length == 0)
                return new ChatResponse { Enabled = true, Error = I18n.S("err.emptymsg", lang) };

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
            // v1.13.30.2 : allouer l'id de session DÈS LE DÉBUT du tour quand
            // la page n'en porte pas (premier tour d'une conversation). Sinon
            // les dépôts du tour (cartes prompt + action, budget) partent sous
            // la clé « default » tandis que la page approuve avec l'id généré
            // par RecordTurn en fin de tour — Consume rejette alors le clic
            // (« Action introuvable ou expirée ») sans autre symptôme (constat
            // terrain 2026-09-21 : carte rendue, clic 200, exécution jamais
            // atteinte). RecordTurn « assure » une session inconnue : l'id
            // alloué ici est repris tel quel par la mémoire et la réponse.
            if (sessionId.Length == 0)
                sessionId = ChatMemoryStore.NewSessionId();
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
            // v2.9.1 : en mode « Éditer — … » (contexte d'édition résolu),
            // la couche d'action est COUPÉE : aucun outil d'action n'est
            // enregistré et le bloc « ACTIONS EMBY DISPONIBLES » n'est pas
            // injecté. La révision d'un prompt ne se valide PAS par une
            // action Emby — le dépôt est deux phases (carte Approuver/
            // Refuser, écriture en C# déterministe) et le test du prompt
            // modifié se fait HORS mode édition (chat normal, ex.
            // run_tonight_run). Vécu 2026-11-02 07:00 (mode edit_rag,
            // ministral-3:14b) : voyant record_program & cie., le modèle a
            // déposé un enregistrement EPG au lieu de livrer le prompt
            // révisé — les outils d'action visibles en mode édition sont
            // une invitation à la dérive. La consultation reste : get_emby_info
            // et l'audit sont fournis par la base du runner (lecture seule).
            bool editMode = ChatContexts.Find(contextId) != null;
            // v2.20.16 : les mécanismes du canal bloc ```text (nudge
            // d'itération, hint de réparation, filet prose, extraction
            // serveur-side) ne concernent QUE les modes d'édition À CHAMP
            // de prompt — le mode atelier de langues (GuideOnly, FieldFor
            // vide) livre par l'OUTIL i18n_set_key. Vécu prod 2026-11-14
            // (gemma4:31b cloud) : EditMode englobait l'atelier → le nudge
            // v2.20.12 exigeait un bloc ```text dans un mode dont ce bloc
            // n'est PAS le canal de livraison — le modèle lisait l'exigence
            // comme une demande hors mode et REFUSAIT après un travail
            // correct (bannière d'entrée, réponse i18n_get, résumé
            // post-dépôt : 5 rappels tirés, 3 refus confus servis comme
            // réponse FINALE — l'état complet de chat.mode.selected et le
            // résumé du dépôt f40568db ont été REMPLACÉS par un refus).
            bool promptEditMode = editMode && ChatContexts.FieldFor(contextId).Length > 0;
            ChatActions.BeginTurn(sessionId, Logger);
            List<ILlmTool> actionTools = null;
            string actionsWorkflow = null;
            if (cfg.ChatActionBudget > 0 && !editMode)
            {
                actionTools = ChatActions.BuildTools(cfg, sessionId, admin,
                    LibraryManager, _liveTv, _collections, _playlists,
                    UserManager, ApplicationHost, Logger, _json, _sessions,
                    _tasks, _notifications);
                actionsWorkflow = ChatActions.BuildWorkflowBlock(cfg);
                Logger.Info("[LLM_AI] [CHAT] Couche d'action active : {0} outil(s), budget {1}/tour.",
                    actionTools.Count, cfg.ChatActionBudget);
            }
            else if (editMode)
            {
                Logger.Info("[LLM_AI] [CHAT] Mode édition « {0} » : couche d'action Emby coupée " +
                    "(édition = dépôt deux phases, aucune action Emby dans ce mode).", contextId);
            }

            // Outils d'identification assistée (lecture seule : identify_list,
            // identify_resolve) — disponibles hors mode édition même si le
            // budget d'actions est à 0 ; les écritures (identify_propose,
            // identify_undo) vivent dans ChatActions et exigent le budget.
            if (!editMode)
            {
                actionTools ??= new List<ILlmTool>();
                actionTools.AddRange(ChatIdentifyTools.BuildReadTools(cfg, sessionId,
                    LibraryManager, UserManager, _liveTv, ApplicationHost, _json, Logger));
                actionsWorkflow = (actionsWorkflow ?? string.Empty)
                    + ChatIdentifyTools.BuildWorkflowBlock(cfg.ChatActionBudget > 0);
            }

            // Édition de prompts par le chat (v1.13.8, opt-in) : le tool
            // plugin_prompts (list/get/set two-phase) — l'écriture passe par
            // l'approbation, jamais par le LLM. Indépendant du budget
            // d'actions (une approbation n'est pas une action Emby).
            // v2.20.12 : le tool est AUSSI enregistré dès qu'un mode
            // « Éditer — … » est actif, même si ChatPromptsEnabled est
            // false — la sélection du mode dans le dropdown EST l'opt-in
            // explicite de ce tour (vécu prod 2026-11-14 : checkbox
            // décochée, aucun tool, le LLM bouclait sans jamais livrer).
            // Le dépôt final reste de toute façon deux phases + serveur-side
            // (bloc ```text extrait côté serveur, voir plus bas).
            if (cfg.ChatPromptsEnabled || editMode)
            {
                actionTools ??= new List<ILlmTool>();
                actionTools.Add(new ChatPromptsTool(cfg, sessionId, userId, contextId, lang, Logger));
                Logger.Info("[LLM_AI] [CHAT] Édition de prompts active (plugin_prompts){0}.",
                    editMode && !cfg.ChatPromptsEnabled ? " (mode actif, opt-in du dropdown)" : "");
            }

            // Atelier de langues (v1.17.0 T1d ; mode déroulant v1.17.0.2) :
            // revue/correction des langues générées — i18n_search (recherche
            // PAR TEXTE), i18n_get (lecture complète) et i18n_set_key
            // (DÉPÔT deux phases : la carte « Approuver / Refuser » porte
            // l'écriture, les endpoints I18nKey exécutent en C# déterministe).
            // Enregistrés quand le protocole tool-calling est déjà actif
            // (budget/prompts) OU quand le mode « Éditer — Atelier de
            // langues » est sélectionné (opt-in explicite du dropdown — protocole initié
            // POUR lui). Hors mode, i18n_set_key refuse le dépôt avec la
            // consigne : la modification des chaînes est exclue sans le mode
            // (décision usager 2026-10-04).
            bool i18nMode = string.Equals(contextId, ChatContexts.I18nEditModeId, StringComparison.Ordinal);
            if (actionTools != null || i18nMode)
            {
                actionTools ??= new List<ILlmTool>();
                actionTools.Add(new I18nSearchTool(Logger));
                actionTools.Add(new I18nGetTool(Logger));
                actionTools.Add(new I18nSetKeyTool(sessionId, userId, contextId, Logger));
                Logger.Info("[LLM_AI] [CHAT] Atelier de langues actif{0} (i18n_search, i18n_get, i18n_set_key — dépôt).",
                    i18nMode ? " en mode « Éditer — Atelier de langues »" : "");
            }

            string reply;
            try
            {
                reply = await runner.RunChatAsync(cfg, "CHAT", history, message,
                    _sessions, _tasks, _notifications, ct, memoryBlock,
                    actionTools, actionsWorkflow, contextBlock,
                    editMode: editMode, promptEditMode: promptEditMode).ConfigureAwait(false);
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
                return new ChatResponse { Enabled = true, Error = I18n.S("err.llmtimeout", lang) };
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
            if (promptEditMode
                && cfg.ChatPromptsEnabled
                && !string.IsNullOrWhiteSpace(reply)
                && !reply.StartsWith(I18n.S("err.chatfail", lang), StringComparison.Ordinal)
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
                        actionTools, actionsWorkflow, contextBlock,
                        editMode: editMode, promptEditMode: promptEditMode).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(nudged)
                        && !nudged.StartsWith(I18n.S("err.chatfail", lang), StringComparison.Ordinal))
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
                && !reply.StartsWith(I18n.S("err.chatfail", lang), StringComparison.Ordinal))
            {
                savedSession = ChatMemoryStore.RecordTurn(cfg, userId, sessionId,
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

            // v2.20.12 — dépôt DÉTERMINISTE côté serveur en mode édition :
            // un petit LLM local (vécu prod 2026-11-14, gemma4:latest) ne
            // parvient pas à appeler plugin_prompts set (boucle de
            // réparations, aucun appel émis). Le serveur extrait donc
            // LUI-MÊME le bloc clôturé ```text … ``` de la réponse finale
            // (le SEUL canal de livraison documenté dans CommonRules) et
            // dépose la proposition dans ChatPromptStore — comme le ferait
            // le tool set, avec les mêmes validations (liste blanche,
            // plafond, champ = mode actif, avertissement de divergence).
            // Aucune écriture : le store n'est consommé que par le clic
            // « Approuver » (endpoint dédié, C# déterministe).
            if (pending == null && promptEditMode && !string.IsNullOrWhiteSpace(reply)
                && reply.IndexOf("```", StringComparison.Ordinal) >= 0)
            {
                string field = ChatContexts.FieldFor(contextId);
                string blockText = ExtractLastTextCodeBlock(reply);
                if (field.Length > 0 && !string.IsNullOrWhiteSpace(blockText)
                    && blockText.Trim().Length <= ChatPromptsTool.MaxPromptChars)
                {
                    string current = ChatContexts.Find(contextId)?.GetPrompt(cfg) ?? "";
                    if (ChatPromptsTool.IsMateriallyIdentical(blockText.Trim(), current))
                    {
                        // v2.20.15 — pas de diff, pas de carte (question usager
                        // 2026-11-14) : approuver un texte identique au courant
                        // serait une écriture no-op. Ligne greppable : un bloc
                        // livré sans carte doit toujours s'expliquer au log.
                        Logger.Info("[LLM_AI] [CHAT] Bloc ```text identique au texte courant — aucun dépôt (champ {0}).", field);
                    }
                    else
                    {
                        string warn = null;
                        if (current.Trim().Length > 0)
                        {
                            double overlap = ChatPromptsTool.WordOverlapPublic(blockText, current);
                            if (overlap < ChatPromptsTool.WarnOverlapThreshold)
                                warn = string.Format(CultureInfo.InvariantCulture,
                                    I18n.S("chat.warn.divergence", lang),
                                    Math.Round(overlap * 100),
                                    ChatPromptsTool.LabelOfLocalized(field, lang));
                        }
                        pending = ChatPromptStore.Create(cfg, sessionId, userId, field,
                            blockText.Trim(), ChatPromptsTool.LabelOfLocalized(field, lang), warn, Logger);
                        if (pending != null)
                        {
                            // v2.20.14 — Create publie aussi la carte au canal page
                            // (_pageNotified) ; ici on la sert par la valeur de
                            // retour. Sans consommer le canal dans CE tour, le tour
                            // SUIVANT relèverait cette carte périmée (jamais
                            // approuvée) : re-service en doublon ET fermeture
                            // silencieuse de ce dépôt (pending != null) — le bloc
                            // ```text du tour suivant serait jeté sans un log
                            // (vécu prod 2026-11-14 08:17-08:25 : dépôts un tour
                            // sur deux). Symétrique du chemin tool : le canal est
                            // relevé dans le tour même où il est posé.
                            ChatPromptStore.TakePagePending(sessionId);
                            Logger.Info("[LLM_AI] [CHAT] Bloc ```text extrait serveur-side en mode édition : " +
                                "proposition déposée (action_id={0}, champ={1}, {2} caractères).",
                                pending.ActionId, field, pending.NewText?.Length ?? 0);
                        }
                    }
                }
            }

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
                        Label = a.Label,
                        Diff = a.Diff
                    }).ToList()
                : null;

            // Proposition i18n déposée pendant le tour (i18n_set_key, deux
            // phases) : la page rend la carte Approuver/Refuser de l'atelier
            // de langues — comme pour les cartes de prompt et d'action, la
            // page ne parse JAMAIS le texte du LLM pour la découvrir.
            var i18nInfo = (PendingI18nInfo)null;
            var i18nPending = ChatI18nStore.TakePagePending(sessionId);
            if (i18nPending != null)
                i18nInfo = new PendingI18nInfo
                {
                    ActionId = i18nPending.ActionId,
                    Key = i18nPending.Key,
                    Lang = i18nPending.Lang,
                    Family = i18nPending.Sec,
                    OldValue = i18nPending.OldValue,
                    NewValue = i18nPending.NewValue,
                    Warning = i18nPending.Warn
                };

            // v2.20.11 : le bouton de secours « Préparer la sauvegarde » (page)
            // n'a de sens que si la réponse PROPOSE quelque chose : en mode
            // d'édition, on n'active le flag que si la réponse recouvre
            // lexicalement le prompt courant (c'est une révision en prose,
            // pas une annonce/question). Sans bloc clôturé ni ressemblance,
            // aucun bouton — vécu prod 2026-11-14 (gemma4:latest) : annonce
            // « Je suis en mode Éditer — Prompt système » + bouton 💾 affiché
            // sans aucune proposition.
            bool? looksLikeRevision = null;
            if (editMode && pending == null && !string.IsNullOrWhiteSpace(reply)
                && reply.IndexOf("```", StringComparison.Ordinal) < 0)
            {
                string field = ChatContexts.FieldFor(contextId);
                string current = field.Length > 0 ? (ChatContexts.Find(contextId)?.GetPrompt(cfg) ?? "") : "";
                if (current.Trim().Length > 0)
                    looksLikeRevision = ChatPromptsTool.WordOverlapPublic(reply, current)
                        >= ChatPromptsTool.WarnOverlapThreshold;
                else
                    looksLikeRevision = false; // prompt vide : rien à réviser en prose
            }

            return new ChatResponse
            {
                Enabled = true,
                Reply = reply,
                Date = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Session = savedSession,
                Actions = turnActions.Count > 0 ? turnActions : null,
                Pending = pendingInfo,
                PendingActions = pendingActions,
                PendingI18n = i18nInfo,
                LooksLikeRevision = looksLikeRevision
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
                return new ChatMemoryGetResponse { Error = I18n.SDisplay("err.noconfig", ApplicationHost) };

            var admin = ResolveAdmin();
            bool isAdmin = admin?.Policy?.IsAdministrator ?? false;
            if (!isAdmin)
                return new ChatMemoryGetResponse { Error = I18n.SDisplay("err.admin", ApplicationHost) };

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
                return new ChatMemoryGetResponse { Error = I18n.SDisplay("err.admin", ApplicationHost) };

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
            /// <summary>Texte actuel du prompt couvert par le mode (v2.20.11)
            /// : la page l'affiche à l'écran dès la sélection du mode, pour
            /// que l'admin voie CE qu'il va modifier sans attendre la
            /// réponse du LLM. Null pour un mode sans prompt (atelier de
            /// langues) et pour un prompt vide — la page affiche alors une
            /// note « (vide) ».</summary>
            public string CurrentText { get; set; }
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
            if (cfg == null)
                return new ChatContextsResponse { Enabled = false };

            var admin = ResolveAdmin();
            bool isAdmin = admin?.Policy?.IsAdministrator ?? false;
            if (!isAdmin)
                return new ChatContextsResponse { Enabled = true, Error = I18n.SDisplay("err.admin", ApplicationHost) };

            var list = new List<ChatContextInfo>();
            foreach (var c in ChatContexts.All)
                list.Add(new ChatContextInfo
                {
                    Id = c.Id,
                    Label = c.Label,
                    // v2.20.11 : texte actuel servi avec la liste — la page
                    // l'affiche à l'écran à la sélection du mode (un prompt
                    // vide reste visible comme « (vide) », jamais absent).
                    CurrentText = c.GuideOnly ? null : (c.GetPrompt(cfg) ?? "")
                });
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
                return new ChatPromptDecisionResponse { Error = I18n.SDisplay("err.admin", ApplicationHost) };

            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null)
                return new ChatPromptDecisionResponse { Error = I18n.SDisplay("err.noconfig", ApplicationHost) };

            var action = ChatPromptStore.Consume(req?.ActionId, RequestSessionHint(),
                admin.Id.ToString(), Logger);
            if (action == null)
            {
                // Branche sinon muette : mismatch session dépôt/clic ou TTL —
                // loguer le hint (même diagnostic que les cartes d'action).
                Logger.Info("[LLM_AI] Chat prompt Approve : pending introuvable/expiré " +
                    "(action_id={0}, session={1}, usager={2}).",
                    req?.ActionId, RequestSessionHint(), admin.Name);
                return new ChatPromptDecisionResponse { Error =
                    I18n.SDisplay("err.pending.expired.save", ApplicationHost) };
            }

            string text = (action.NewText ?? string.Empty).Trim();
            if (!ChatPromptsTool.IsKnownField(action.Field) || text.Length == 0 ||
                text.Length > ChatPromptsTool.MaxPromptChars)
                return new ChatPromptDecisionResponse { Error = I18n.SDisplay("err.proposal.invalid", ApplicationHost) };

            ChatPromptsTool.SetPrompt(cfg, action.Field, text);
            Plugin.Instance.SaveConfiguration();
            Logger.Info("[LLM_AI] Chat prompts : champ « {0} » écrasé par approbation de l'admin " +
                "(action_id={1}, {2} caractères).", action.Field, action.ActionId, text.Length);

            string lang = I18n.ResolveDisplayLangKey(ApplicationHost);
            return new ChatPromptDecisionResponse
            {
                Ok = true,
                Field = action.Field,
                Label = action.Label ?? ChatPromptsTool.LabelOfLocalized(action.Field, lang),
                TestHint = ChatPromptsTool.TestHintFor(action.Field, lang)
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
                return new ChatPromptDecisionResponse { Error = I18n.SDisplay("err.admin", ApplicationHost) };

            ChatPromptStore.Discard(req?.ActionId, Logger);
            return new ChatPromptDecisionResponse { Ok = true };
        }

        /// <summary>
        /// <c>POST /Plugins/LLMAI/I18nKey/Approve</c> — approbation par
        /// l'admin d'une correction d'overlay i18n déposée par le chat
        /// (atelier de langues, two phases v1.17.0.2). Le navigateur n'envoie
        /// QUE <c>ActionId</c> : la clé, la langue et la valeur viennent du
        /// store serveur (<see cref="ChatI18nStore"/>, expiration 10 min,
        /// proposition liée à la session ET à l'usager approbateur) —
        /// anti-détournement. Les gates structurelles sont RE-courues à
        /// l'exécution contre la native EN fraîche (elle peut avoir changé
        /// entre le dépôt et le clic), puis écriture déterministe
        /// (<see cref="I18nGenerator.WriteOverlayKey"/>, .bak + re-scan,
        /// trace SecurityMonitor) — le LLM n'a aucun rôle dans l'exécution.
        /// </summary>
        [Route("/Plugins/LLMAI/I18nKey/Approve", "POST")]
        public class I18nKeyApproveRequest : IReturn<object>
        {
            public string ActionId { get; set; }
        }

        public class I18nKeyDecisionResponse
        {
            public bool Ok { get; set; }
            public string Key { get; set; }
            public string Lang { get; set; }
            /// <summary>Ce que le serveur SERVIT après re-scan (preuve
            /// d'effet, affichée sur la carte) — null si erreur.</summary>
            public string Served { get; set; }
            public string Error { get; set; }
        }

        public object Post(I18nKeyApproveRequest req)
        {
            string lang = I18n.ResolveDisplayLangKey(ApplicationHost);
            var admin = ResolveAdmin();
            bool isAdmin = admin?.Policy?.IsAdministrator ?? false;
            if (!isAdmin)
                return new I18nKeyDecisionResponse { Error = I18n.SDisplay("err.admin", ApplicationHost) };

            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null)
                return new I18nKeyDecisionResponse { Error = I18n.SDisplay("err.noconfig", ApplicationHost) };

            var action = ChatI18nStore.Consume(req?.ActionId, RequestSessionHint(),
                admin.Id.ToString(), Logger);
            if (action == null)
            {
                // Branche sinon muette : mismatch session dépôt/clic ou TTL —
                // même diagnostic que les cartes de prompt et d'action.
                Logger.Info("[LLM_AI] Chat i18n Approve : pending introuvable/expiré " +
                    "(action_id={0}, session={1}, usager={2}).",
                    req?.ActionId, RequestSessionHint(), admin.Name);
                return new I18nKeyDecisionResponse { Error =
                    I18n.SDisplay("err.pending.expired.save", ApplicationHost) };
            }

            // Gates RE-courues à l'exécution (jamais confiance au payload
            // déposé en dernier ressort) : la clé doit toujours exister dans
            // les natives avec la MÊME famille, et la valeur passer la
            // validation structurelle (la native EN peut avoir changé entre
            // dépôt et approbation).
            var (sec, en, _) = I18nChatKeys.ResolveNative(action.Key, Logger);
            if (sec == null || en == null ||
                !string.Equals(sec, action.Sec, StringComparison.Ordinal) ||
                (action.NewValue ?? string.Empty).Length == 0 ||
                (action.NewValue ?? string.Empty).Length > I18nSetKeyTool.MaxValueChars ||
                I18nDoses.Validate(en, action.NewValue) != I18nDoses.Verdict.Ok)
            {
                Logger.Warn("[LLM_AI] Chat i18n Approve : proposition invalide à l'exécution " +
                    "(action_id={0}, clé={1}) — native ou gates divergentes.", action.ActionId, action.Key);
                return new I18nKeyDecisionResponse { Error =
                    I18n.SDisplay("err.proposal.invalid", ApplicationHost) };
            }

            I18nGenerator.WriteOverlayKey(action.Lang, sec, action.Key, action.NewValue);
            Logger.Info("[LLM_AI] Chat i18n : clé {0} écrite par approbation de l'admin " +
                "({1}/{2}, {3} caractères, action_id={4}).",
                action.Key, action.Lang, sec, action.NewValue.Length, action.ActionId);
            SecurityMonitor.Record("I18N_CLE_MODIFIEE",
                action.Lang + "/" + sec + " " + action.Key + " (approbation chat, "
                + action.NewValue.Length + " caractères)");

            return new I18nKeyDecisionResponse
            {
                Ok = true,
                Key = action.Key,
                Lang = action.Lang,
                // Ce que le serveur SERVIT après re-scan : la preuve d'effet,
                // affichée sur la carte et poussée dans le fil.
                Served = I18n.S(action.Key, action.Lang) ?? action.NewValue
            };
        }

        /// <summary>
        /// <c>POST /Plugins/LLMAI/I18nKey/Refuse</c> — retire la proposition
        /// i18n en attente (la conversation peut continuer, une nouvelle
        /// proposition créera un nouveau pending). Le refus n'écrit rien.
        /// </summary>
        [Route("/Plugins/LLMAI/I18nKey/Refuse", "POST")]
        public class I18nKeyRefuseRequest : IReturn<object>
        {
            public string ActionId { get; set; }
        }

        public object Post(I18nKeyRefuseRequest req)
        {
            var admin = ResolveAdmin();
            bool isAdmin = admin?.Policy?.IsAdministrator ?? false;
            if (!isAdmin)
                return new I18nKeyDecisionResponse { Error = I18n.SDisplay("err.admin", ApplicationHost) };

            ChatI18nStore.Discard(req?.ActionId, Logger);
            return new I18nKeyDecisionResponse { Ok = true };
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
            string lang = I18n.ResolveDisplayLangKey(ApplicationHost);
            var admin = ResolveAdmin();
            bool isAdmin = admin?.Policy?.IsAdministrator ?? false;
            if (!isAdmin)
                return new ChatActionDecisionResponse { Error = I18n.S("err.admin", lang) };

            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null)
                return new ChatActionDecisionResponse { Error = I18n.S("err.noconfig", lang) };

            var cfgBudget = Math.Max(0, cfg.ChatActionBudget);
            if (cfgBudget <= 0)
                return new ChatActionDecisionResponse { Error = I18n.S("err.actionlayer.disabled", lang) };

            var action = ChatActionStore.Consume(req?.ActionId, RequestSessionHint(),
                admin.Id.ToString());
            if (action == null)
            {
                // Branche sinon muette (constat v1.13.30.1 : rejet de session
                // sans aucune trace côté serveur) — loguer le hint pour
                // diagnostiquer un mismatch dépôt/clic.
                Logger.Info("[LLM_AI] Chat action Approve : pending introuvable/expiré " +
                    "(action_id={0}, session={1}, usager={2}).",
                    req?.ActionId, RequestSessionHint(), admin.Name);
                return new ChatActionDecisionResponse { Error =
                    I18n.S("err.pending.expired.action", lang) };
            }

            var tool = ChatActions.BuildToolByName(action.Tool, cfg, action.Session, admin,
                admin.Id.ToString(), LibraryManager, _liveTv, _collections, _playlists,
                UserManager, ApplicationHost, Logger, _json, _sessions,
                _tasks, _notifications);
            if (tool == null)
            {
                Logger.Info("[LLM_AI] Chat action Approve : outil inconnu (tool={0}) — proposition invalide.",
                    action.Tool);
                return new ChatActionDecisionResponse { Error = I18n.S("err.tool.unknown", lang) };
            }

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
                return new ChatActionDecisionResponse { Error = I18n.S("err.exec.cancelled", lang) };
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
                Detail = ok ? (detail ?? I18n.S("detail.executed", lang)) : null,
                Error = ok ? null : (detail ?? I18n.S("err.exec.failed", lang))
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
                return new ChatActionDecisionResponse { Error = I18n.SDisplay("err.admin", ApplicationHost) };

            ChatActionStore.Discard(req?.ActionId);
            return new ChatActionDecisionResponse { Ok = true };
        }

        /// <summary>
        /// <c>POST /Plugins/LLMAI/ChatAction/RefreshMetadata</c> — met en file
        /// une actualisation des métadonnées manquantes d'un item tout en
        /// excluant les images (<c>MetadataRefreshMode.FullRefresh</c> +
        /// <c>ReplaceAllMetadata=false</c>, <c>ImageRefreshMode.ValidationOnly</c>,
        /// <c>ReplaceAllImages=false</c>). Déclenchée manuellement par le bouton
        /// post-approbation de la carte d'identification (hors fiches croisées).
        /// Réservé aux administrateurs.
        /// </summary>
        [Route("/Plugins/LLMAI/ChatAction/RefreshMetadata", "POST")]
        public class ChatActionRefreshRequest : IReturn<object>
        {
            public string ItemId { get; set; }
        }

        public object Post(ChatActionRefreshRequest req)
        {
            string lang = I18n.ResolveDisplayLangKey(ApplicationHost);
            var admin = ResolveAdmin();
            bool isAdmin = admin?.Policy?.IsAdministrator ?? false;
            if (!isAdmin)
                return new ChatActionDecisionResponse { Error = I18n.S("err.admin", lang) };

            var item = ItemIdResolver.Resolve(LibraryManager, req?.ItemId);
            if (item == null || OrphanResolver.IsStrmCard(item))
                return new ChatActionDecisionResponse { Error = I18n.S("act.ref.refresh.notfound", lang) };

            if (OrphanResolver.HasTag(item, OrphanIdentifyTask.TagCrossKind))
                return new ChatActionDecisionResponse { Error = I18n.S("act.ref.refresh.crosskind", lang) };

            var providers = ApplicationHost?.TryResolve<IProviderManager>();
            var fs = ApplicationHost?.TryResolve<IFileSystem>();
            if (providers == null || fs == null)
                return new ChatActionDecisionResponse { Error = I18n.S("act.ref.refresh.unavailable", lang) };

            try
            {
                var dirSvc = new DirectoryService(fs);
                var opts = new MetadataRefreshOptions(dirSvc)
                {
                    MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                    ReplaceAllMetadata = false,
                    ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
                    ReplaceAllImages = false,
                    ReplaceThumbnailImages = false,
                    Recursive = true
                };
                providers.QueueRefresh(item.InternalId, opts, RefreshPriority.High);
                Logger.Info("[LLM_AI] Chat action RefreshMetadata : « {0} » (id={1}) mis en file (métadonnées manquantes, sans images ; usager={2}).",
                    item.Name, item.InternalId, admin.Name);
                return new ChatActionDecisionResponse
                {
                    Ok = true,
                    Detail = string.Format(CultureInfo.InvariantCulture, I18n.S("act.ok.refresh_meta", lang), item.Name ?? "?")
                };
            }
            catch (Exception ex)
            {
                Logger.Warn("[LLM_AI] Chat action RefreshMetadata : échec sur « {0} » ({1}).", item.Name, ex.Message);
                return new ChatActionDecisionResponse { Error = ex.Message };
            }
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

        /// <summary>
        /// v2.20.12 : extrait le contenu du DERNIER bloc de code d'une
        /// réponse (```text … ``` ou ``` brut) — le canal de livraison
        /// unique d'une révision de prompt en mode édition (CommonRules).
        /// v2.20.13 : les lignes de fence ``` (début de ligne, indent
        /// ≤ 3 espaces toléré) s'alternent ouvrante/clôture ; le DERNIER
        /// bloc = entre l'avant-dernière et la dernière fence (paire
        /// complète) ou après la dernière fence seule (bloc non fermé =
        /// réponse tronquée, accepté). La première version prenait la
        /// dernière fence comme ouvrante — un bloc CLÔTÉ (le cas normal)
        /// donnait un corps vide et le dépôt était silently ignoré.
        /// Null si aucune fence.
        /// </summary>
        private static string ExtractLastTextCodeBlock(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            var fences = new System.Collections.Generic.List<int>();
            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i].TrimStart();
                if (t.StartsWith("```", StringComparison.Ordinal)) fences.Add(i);
            }
            if (fences.Count == 0) return null;

            int open, close; // indexes de lignes
            if (fences.Count % 2 == 1)
            {
                // bloc non fermé : tout ce qui suit la dernière fence
                open = fences[fences.Count - 1];
                close = lines.Length; // sentinelle : jusqu'à la fin
            }
            else
            {
                open = fences[fences.Count - 2];
                close = fences[fences.Count - 1];
            }
            var sb = new System.Text.StringBuilder();
            for (int i = open + 1; i < close; i++)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(lines[i]);
            }
            return sb.ToString().Trim('\n');
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