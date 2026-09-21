using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Session;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Users;

namespace LLM_AI
{
    /// <summary>
    /// Contrat des outils d'action deux-phases du chat (v1.13.29).
    /// <see cref="ExecuteAsync"/> (hérité d'<see cref="ILlmTool"/>) DÉPOSE
    /// une proposition dans <see cref="ChatActionStore"/> sans exécuter ;
    /// <see cref="ExecuteCoreAsync"/> est l'exécution réelle, appelée
    /// UNIQUEMENT par l'endpoint <c>/Plugins/LLMAI/ChatAction/Approve</c>
    /// après consommation du pending — jamais par la boucle LLM. La boucle
    /// de l'agent ne voit donc que le dépôt : un texte injecté dans un
    /// synopsis peut empiler des propositions, jamais d'exécutions.
    /// </summary>
    internal interface IChatActionTool
    {
        /// <summary>Clé d'usine (nom d'outil exposé au LLM).</summary>
        string ToolKey { get; }

        /// <summary>Exécution réelle de l'action (code métier inchangé,
        /// budget consommé ici — à l'approbation, pas au dépôt).</summary>
        Task<string> ExecuteCoreAsync(JsonElement args, CancellationToken ct);
    }

    /// <summary>
    /// Couche d'action du chat admin (v1.13) : le LLM du chat obtient des
    /// outils qui agissent sur les MÊMES surfaces Emby que le plugin (cartes
    /// .strm, timers d'enregistrement, tag « AI Tonight », collection,
    /// playlist, déclenchement du run Tonight) — en réutilisant les
    /// primitives existantes (<see cref="AutoProgrammer.ProgramOneAsync"/>,
    /// <see cref="AiTagger.AddAsync"/>, managers additifs,
    /// <see cref="StrmLibraryGenerator.WriteSingleCardAsync"/>,
    /// <see cref="TonightService.GenerateTonightAsync"/>), pas de nouvelle
    /// logique métier.
    /// </summary>
    /// <remarks>
    /// <para><b>Admin-only + confirmation mécanique hors-LLM</b> (v1.13.29) :
    /// le chat reste réservé aux administrateurs (vérifié côté endpoint).
    /// La confirmation n'est PLUS une étiquette de prompt (jugée par le
    /// LLM, donc falsifiable par une injection dans un synopsis) : chaque
    /// outil d'action est <b>deux-phases</b> — l'appel du LLM dépose une
    /// proposition (aucun effet) dans <see cref="ChatActionStore"/>, la
    /// page rend une carte « Approuver / Refuser », et l'endpoint
    /// <c>/Plugins/LLMAI/ChatAction/Approve</c> exécute en code serveur
    /// déterministe (le LLM n'a aucun rôle, même philosophie que le PIN de
    /// <see cref="RecordingChatTool"/> et l'approbation de
    /// <see cref="ChatPromptsTool"/>). Les garde-fous DURS restent en
    /// place à l'exécution : budget d'actions
    /// (<see cref="PluginConfiguration.ChatActionBudget"/>,
    /// <see cref="PluginConfiguration.ChatActionConversationCap"/>) et
    /// garde-fous métier inchangés (owned-guard, watched-guard, drop list,
    /// dedup — déjà dans les primitives réutilisées).</para>
    /// <para><b>Budget</b> : consommation AU SUCCÈS seulement — une action
    /// refusée par un garde-fou ne consomme rien (réservation puis rembourse-
    /// ment du différentiel). Un lot dont le budget ne peut pas tout couvrir
    /// est refusé en bloc (all-or-nothing : pas d'exécution partielle
    /// gratuite).</para>
    /// <para><b>État en mémoire serveur</b> (compteurs de budget, gate de
    /// run, ids ajoutés à retracer) : best-effort, remis à zéro au
    /// redémarrage — jamais persisté, la perte n'a aucun effet destructeur
    /// (un plafonnage « oublie » simplement la conversation).</para>
    /// </remarks>
    internal static class ChatActions
    {
        // ------------------------------------------------------------------
        //  État par session (budgets, gate de run, ids ajoutés)
        // ------------------------------------------------------------------

        private class SessionState
        {
            /// <summary>Actions consommées pendant le tour courant.</summary>
            public int TurnUsed;
            /// <summary>Actions consommées depuis le début de la conversation.</summary>
            public int ConvUsed;
            /// <summary>Runs Tonight déclenchés (≤ 2 par conversation).</summary>
            public int RunCount;
            public DateTimeOffset LastSeen;
            /// <summary>Ids items (InternalId normalisés) ajoutés par le chat
            /// à la playlist/collection durant cette conversation — seuls
            /// retirables par <c>playlist_remove</c>/<c>collection_remove</c>.</summary>
            public readonly HashSet<string> PlaylistAdded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> CollectionAdded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            /// <summary>Textes des actions RÉUSSIES pendant le tour courant
            /// (même libellé que le toast) — renvoyés à la page de chat
            /// (<c>ChatResponse.Actions</c>) pour l'affichage sous la réponse,
            /// le toast Emby n'étant pas rendu sur la page de config (constat
            /// 2026-09-06). Revidé à chaque <c>BeginTurn</c>.</summary>
            public readonly List<string> TurnActions = new List<string>();
        }

        private static readonly ConcurrentDictionary<string, SessionState> _states =
            new ConcurrentDictionary<string, SessionState>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Gate « un seul run Tonight déclenché par le chat à la
        /// fois » (les runs planifiés/login ne passent pas par ici).</summary>
        private static int _runGate;

        /// <summary>TTL d'inactivité d'une session de chat : au-delà, l'état
        /// (budgets de conversation, ids ajoutés) est élagué — une nouvelle
        /// conversation repart de zéro.</summary>
        private static readonly TimeSpan SessionTtl = TimeSpan.FromHours(24);

        private static SessionState For(string sessionId)
        {
            var key = string.IsNullOrWhiteSpace(sessionId) ? "default" : sessionId.Trim();
            return _states.GetOrAdd(key, _ => new SessionState());
        }

        /// <summary>
        /// Ouvre un tour de chat : remet le compteur de tour à zéro (une
        /// question = un tour) et élage les sessions inactives. Appelé par
        /// l'endpoint de chat AVANT le run LLM.
        /// </summary>
        public static void BeginTurn(string sessionId, ILogger logger)
        {
            var st = For(sessionId);
            st.TurnUsed = 0;
            st.TurnActions.Clear();
            st.LastSeen = DateTimeOffset.UtcNow;

            // Élagage opportuniste des sessions inactives (best-effort).
            var now = DateTimeOffset.UtcNow;
            foreach (var kv in _states)
            {
                if (now - kv.Value.LastSeen > SessionTtl)
                    _states.TryRemove(kv.Key, out _);
            }
        }

        private static int Budget(PluginConfiguration cfg) => Math.Max(0, cfg?.ChatActionBudget ?? 0);
        private static int Cap(PluginConfiguration cfg) => Math.Max(Budget(cfg), cfg?.ChatActionConversationCap ?? 0);

        /// <summary>
        /// Résultat d'une réservation de budget : <see cref="Ok"/> consomme
        /// immédiatement <paramref name="n"/> actions (tour + conversation) ;
        /// <see cref="TurnExhausted"/>/<see cref="ConvExhausted"/> refusent
        /// sans rien consommer.
        /// </summary>
        public enum BudgetVerdict { Ok, TurnExhausted, ConvExhausted }

        /// <summary>Réserve <paramref name="n"/> actions (all-or-nothing).
        /// Le différentiel en cas d'échec partiel se rembourse via
        /// <see cref="Refund"/>.</summary>
        public static BudgetVerdict Reserve(string sessionId, int n, PluginConfiguration cfg)
        {
            if (n <= 0) return BudgetVerdict.Ok;
            var st = For(sessionId);
            int budget = Budget(cfg);
            int cap = Cap(cfg);
            if (st.TurnUsed + n > budget) return BudgetVerdict.TurnExhausted;
            if (st.ConvUsed + n > cap) return BudgetVerdict.ConvExhausted;
            st.TurnUsed += n;
            st.ConvUsed += n;
            st.LastSeen = DateTimeOffset.UtcNow;
            return BudgetVerdict.Ok;
        }

        /// <summary>Rembourse <paramref name="n"/> actions non réalisées
        /// (garde-fous, échecs partiels d'un lot) — borné à zéro.</summary>
        public static void Refund(string sessionId, int n)
        {
            if (n <= 0) return;
            var st = For(sessionId);
            st.TurnUsed = Math.Max(0, st.TurnUsed - n);
            st.ConvUsed = Math.Max(0, st.ConvUsed - n);
        }

        /// <summary>Refus typé renvoyé au modèle quand le budget est
        /// épuisé (tour ou conversation) — le tool s'arrête proprement.</summary>
        public static string BudgetRefusal(string sessionId, BudgetVerdict verdict, PluginConfiguration cfg)
        {
            if (verdict == BudgetVerdict.ConvExhausted)
                return Json(new { status = "refused",
                    detail = string.Format(CultureInfo.InvariantCulture,
                        "Budget d'actions de la conversation épuisé ({0}). Poursuivez en lecture seule.",
                        Cap(cfg)) });
            return Json(new { status = "refused",
                detail = string.Format(CultureInfo.InvariantCulture,
                    "Budget d'actions atteint pour ce tour ({0}). Finissez votre proposition ou réformez-la — n'insistez pas.",
                    Budget(cfg)) });
        }

        public static bool TryBeginRun()
            => Interlocked.CompareExchange(ref _runGate, 1, 0) == 0;

        public static void EndRun()
            => Interlocked.Exchange(ref _runGate, 0);

        // ------------------------------------------------------------------
        //  Deux phases (v1.13.29) : dépôt de proposition + exécution à
        //  l'approbation. Le LLM ne peut JAMAIS exécuter directement —
        //  l'approbation est un clic admin hors de sa portée.
        // ------------------------------------------------------------------

        /// <summary>Dépose une proposition d'action (aucun effet) et retourne
        /// la réponse JSON pour le LLM (<c>status="awaiting_approval"</c>).
        /// Le pending fige l'outil ET ses arguments : l'approbation porte
        /// exactement sur ce dépôt (anti-TOCTOU). <paramref name="label"/>
        /// est le texte de la carte côté page (français, ton des toasts).</summary>
        internal static string CreatePending(string sessionId, string userId,
            string toolKey, JsonElement args, string label)
        {
            string argsJson;
            try { argsJson = JsonSerializer.Serialize(args); }
            catch { argsJson = "{}"; }
            var a = ChatActionStore.Create(sessionId, userId, toolKey, argsJson, label);
            if (a == null)
                return Json(new { status = "refused",
                    detail = "Trop de propositions en attente (10 maximum) — l'admin doit d'abord trancher les cartes existantes." });
            return Json(new { status = "awaiting_approval", action_id = a.ActionId,
                detail = "Proposition déposée (aucune exécution). L'admin approuve en cliquant la carte " +
                         "« Approuver » de la page — le résultat d'exécution arrivera ensuite dans la " +
                         "conversation comme note [Admin]. N'annoncez JAMAIS l'exécution avant cette note " +
                         "et ne redéposez pas une proposition identique tant que l'admin n'a pas tranché." });
        }

        /// <summary>
        /// Fabrique d'outil d'action par clé (endpoint d'approbation) :
        /// reconstruit l'instance avec les services de l'endpoint pour
        /// exécuter le pending consommé. Retourne null pour une clé inconnue
        /// ou pour <c>run_tonight_run</c> quand l'opt-in
        /// <see cref="PluginConfiguration.ChatTonightRunEnabled"/> a été
        /// retiré depuis le dépôt (fail-closed).
        /// </summary>
        public static IChatActionTool BuildToolByName(string toolName, PluginConfiguration cfg,
            string sessionId, User adminUser, string adminUserId,
            ILibraryManager library, ILiveTvManager liveTv, ICollectionManager collections,
            IPlaylistManager playlists, IUserManager users, IServerApplicationHost host,
            ILogger logger, IJsonSerializer json)
        {
            switch ((toolName ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "record_program":
                    return new RecordProgramTool(cfg, sessionId, adminUserId, liveTv, library, host, logger);
                case "create_card":
                    return new CreateCardTool(cfg, sessionId, adminUserId, library, liveTv, host, logger);
                case "tag_ai_tonight":
                    return new TagTonightTool(cfg, sessionId, adminUserId, library, logger);
                case "collection_add":
                    return new CollectionAddTool(cfg, sessionId, adminUserId, collections, library, host, logger);
                case "collection_remove":
                    return new CollectionRemoveTool(cfg, sessionId, adminUserId, collections, library, logger);
                case "playlist_add":
                    return new PlaylistAddTool(cfg, sessionId, adminUserId, playlists, library, adminUser, host, logger);
                case "playlist_remove":
                    return new PlaylistRemoveTool(cfg, sessionId, adminUserId, playlists, library, adminUser, logger);
                case "run_tonight_run":
                    if (cfg == null || !cfg.ChatTonightRunEnabled) return null;
                    return new RunTonightTool(cfg, sessionId, adminUser, adminUserId, users, json, library, liveTv, host, logger);
                default:
                    return null;
            }
        }

        // ------------------------------------------------------------------
        //  Bloc de workflow injecté dans le system prompt du chat
        // ------------------------------------------------------------------

        /// <summary>
        /// Section « ACTIONS EMBY DISPONIBLES » du system prompt de chat :
        /// annonce les surfaces, le budget, l'étiquette de confirmation et la
        /// règle de retrait. Injecté SEULEMENT quand le budget &gt; 0
        /// (lecture seule sinon — aucun outil d'action, aucune annonce).
        /// </summary>
        public static string BuildWorkflowBlock(PluginConfiguration cfg)
        {
            var b = new System.Text.StringBuilder();
            b.Append("\n\n### ACTIONS EMBY DISPONIBLES\n");
            b.Append("Vous pouvez agir sur les surfaces Emby du plugin via des outils d'action ");
            b.Append("(record_program, create_card, tag_ai_tonight, collection_add, collection_remove, ");
            b.Append("playlist_add, playlist_remove");
            if (cfg != null && cfg.ChatTonightRunEnabled) b.Append(", run_tonight_run");
            b.Append(").\n");
            b.Append("Budget d'actions : ").Append(Budget(cfg)).Append(" par tour, ")
              .Append(Cap(cfg)).Append(" pour toute la conversation — au-delà, les outils refuseront. ");
            b.Append("Une action refusée par un garde-fou (déjà possédé, déjà visionné, drop list, doublon) ne consomme pas le budget.\n");
            b.Append("PROTOCOLE À DEUX PHASES (mécanique, v1.13.29) : chaque appel d'outil d'action DÉPOSE une ");
            b.Append("proposition (status \"awaiting_approval\") sans exécuter quoi que ce soit. L'admin approuve ");
            b.Append("en cliquant la carte « Approuver » de la page — le résultat d'exécution lui arrive ensuite ");
            b.Append("dans la conversation comme note [Admin]. N'annoncez JAMAIS une exécution avant cette note, ");
            b.Append("ne redéposez pas une proposition identique tant que l'admin n'a pas tranché, et ne demandez ");
            b.Append("JAMAIS à l'admin d'écrire du code ou un id de proposition. ");
            b.Append("PRIORITÉ DE L'ORDRE EXPLICITE : quand l'admin demande une action précise (programmer un ");
            b.Append("enregistrement, taguer, ajouter à une collection ou playlist…), recherchez l'item et DÉPOSEZ ");
            b.Append("immédiatement — l'admin tranche à la carte. Votre profil de goûts oriente vos suggestions ");
            b.Append("SPONTANÉES, jamais un ordre de l'admin : ne le contredisez pas, ne le dissuadez pas et ne ");
            b.Append("re-proposez pas autre chose à sa place (un programme d'actualité ou hors de vos genres ");
            b.Append("préférés se programme aussi, sur demande).\n");
            b.Append("Ne retirez jamais (playlist_remove, ");
            b.Append("collection_remove) ce que vous n'avez pas ajouté vous-même dans cette conversation.\n");
            return b.ToString();
        }

        // ------------------------------------------------------------------
        //  Construction des outils
        // ------------------------------------------------------------------

        /// <summary>
        /// Construit les outils d'action du chat. Appelé par l'endpoint de
        /// chat seulement si <c>cfg.ChatActionBudget &gt; 0</c>
        /// (<c>run_tonight_run</c> requiert en plus
        /// <c>cfg.ChatTonightRunEnabled</c>).
        /// </summary>
        public static List<ILlmTool> BuildTools(PluginConfiguration cfg, string sessionId, User adminUser,
            ILibraryManager library, ILiveTvManager liveTv, ICollectionManager collections,
            IPlaylistManager playlists, IUserManager users, IServerApplicationHost host, ILogger logger,
            IJsonSerializer json, ISessionManager sessions)
        {
            // Singleton Emby, re-posé à chaque tour (les outils y lisent le
            // gestionnaire pour les toasts de traçabilité — cf. ci-dessous).
            s_sessions = sessions;
            // Liaison d'usager des pendings (deux phases v1.13.29) : la
            // proposition est émise par CET admin — un autre compte ne peut
            // pas approuver (ChatActionStore.Consume vérifie).
            string userId = adminUser?.Id.ToString() ?? "";
            var tools = new List<ILlmTool>
            {
                new RecordProgramTool(cfg, sessionId, userId, liveTv, library, host, logger),
                new CreateCardTool(cfg, sessionId, userId, library, liveTv, host, logger),
                new TagTonightTool(cfg, sessionId, userId, library, logger),
                new CollectionAddTool(cfg, sessionId, userId, collections, library, host, logger),
                new CollectionRemoveTool(cfg, sessionId, userId, collections, library, logger),
                new PlaylistAddTool(cfg, sessionId, userId, playlists, library, adminUser, host, logger),
                new PlaylistRemoveTool(cfg, sessionId, userId, playlists, library, adminUser, logger),
            };
            if (cfg != null && cfg.ChatTonightRunEnabled)
                tools.Add(new RunTonightTool(cfg, sessionId, adminUser, userId, users, json, library, liveTv, host, logger));
            return tools;
        }

        // ------------------------------------------------------------------
        //  Toast de traçabilité (v1.13.1) : un DisplayMessage par action
        //  réussie du chat
        // ------------------------------------------------------------------

        // Singleton Emby (posé par <see cref="BuildTools"/>) : les outils y
        // lisent le gestionnaire de sessions pour les toasts.
        private static ISessionManager s_sessions;

        /// <summary>
        /// Toast « traçage d'action » : quand une action du chat réussit
        /// (timer, carte, tag, collection, playlist, run), le libellé est
        /// (1) enregistré pour la page de chat (<c>ChatResponse.Actions</c> —
        /// le client web Emby ne rend PAS les DisplayMessage sur la page de
        /// config, constat 2026-09-06) puis (2) envoyé en DisplayMessage
        /// discret (préfixe 🤖, timeout 8 s) vers TOUTES les sessions admin —
        /// l'admin voit la preuve de l'action, même si la réponse du LLM
        /// n'évoque pas le détail. Succès seulement : rien pour les refus de
        /// garde-fous (le texte du chat les explique). Best-effort, ne lève
        /// jamais.
        /// </summary>
        internal static void RecordAction(string sessionId, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var st = For(sessionId);
            lock (st) st.TurnActions.Add(text);
        }

        /// <summary>Copie des libellés d'actions réussies du tour courant
        /// (lecture thread-safe, la liste reste en place jusqu'au prochain
        /// <c>BeginTurn</c>).</summary>
        internal static List<string> TakeActions(string sessionId)
        {
            var st = For(sessionId);
            lock (st) return new List<string>(st.TurnActions);
        }

        internal static async Task ToastActionAsync(string sessionId, string text, ILogger logger)
        {
            RecordAction(sessionId, text);
            try
            {
                var sessions = s_sessions;
                if (sessions == null || string.IsNullOrWhiteSpace(text)) return;
                if (text.Length > 120) text = text.Substring(0, 120);
                var msg = new MessageCommand
                {
                    Header = string.Empty,   // non rendu par web/Android — tout dans Text
                    Text = "🤖 " + text,
                    TimeoutMs = 8000,
                };
                await sessions.SendMessageToAdminSessions("DisplayMessage", msg, CancellationToken.None)
                    .ConfigureAwait(false);
                logger?.Info("[LLM_AI] Toast action chat envoyé : {0}", text);
            }
            catch (Exception ex)
            {
                logger?.Info("[LLM_AI] Toast action chat échoué (best-effort) : {0}", ex.Message);
            }
        }

        // ------------------------------------------------------------------
        //  Helpers JSON (arguments, réponses)
        // ------------------------------------------------------------------

        private static string ArgString(JsonElement args, string name)
        {
            try
            {
                if (args.ValueKind != JsonValueKind.Object) return null;
                if (!args.TryGetProperty(name, out var v)) return null;
                return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            }
            catch { return null; }
        }

        /// <summary>Liste d'ids : tableau de chaînes, ou chaîne unique
        /// (« id1,id2 » tolérée). Null si absent ; vide → null aussi
        /// (rien à faire).</summary>
        private static List<string> ArgIdList(JsonElement args, string name)
        {
            try
            {
                if (args.ValueKind != JsonValueKind.Object) return null;
                if (!args.TryGetProperty(name, out var v)) return null;
                var list = new List<string>();
                if (v.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in v.EnumerateArray())
                        if (e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString()))
                            list.Add(e.GetString().Trim());
                }
                else if (v.ValueKind == JsonValueKind.String)
                {
                    foreach (var s in v.GetString().Split(','))
                        if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
                }
                return list;
            }
            catch { return null; }
        }

        private static string Json(object o)
        {
            try { return JsonSerializer.Serialize(o); }
            catch { return "{\"status\":\"failed\",\"detail\":\"sérialisation\"}"; }
        }

        /// <summary>Résolve un id (InternalId ou Guid — cf. ItemIdResolver)
        /// vers la chaîne InternalId normalisée (clé de tracking).</summary>
        private static string NormId(ILibraryManager library, string raw)
        {
            try
            {
                var item = ItemIdResolver.Resolve(library, raw);
                return item == null ? null : item.InternalId.ToString(CultureInfo.InvariantCulture);
            }
            catch { return null; }
        }

        private static long[] ToLongIds(IEnumerable<string> normIds)
            => normIds
                .Where(s => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                .Select(s => long.Parse(s, CultureInfo.InvariantCulture))
                .Distinct()
                .ToArray();

        // ------------------------------------------------------------------
        //  Tool : record_program (timer d'enregistrement)
        // ------------------------------------------------------------------

        private class RecordProgramTool : ILlmTool, IChatActionTool
        {
            private readonly PluginConfiguration _cfg;
            private readonly string _sessionId;
            private readonly string _adminUserId;
            private readonly ILiveTvManager _liveTv;
            private readonly ILibraryManager _library;
            private readonly IServerApplicationHost _host;
            private readonly ILogger _logger;

            public RecordProgramTool(PluginConfiguration cfg, string sessionId, string adminUserId,
                ILiveTvManager liveTv, ILibraryManager library,
                IServerApplicationHost host, ILogger logger)
            {
                _cfg = cfg; _sessionId = sessionId; _adminUserId = adminUserId; _liveTv = liveTv;
                _library = library; _host = host; _logger = logger;
            }

            public string ToolKey => "record_program";
            public string Name => "record_program";
            public string Description =>
                "Programme l'enregistrement d'un programme EPG (timer Emby : SeriesTimer pour une série, Timer pour un film). " +
                "DEUX PHASES : l'appel DÉPOSE une proposition (status \"awaiting_approval\") sans exécuter — l'admin " +
                "approuve en cliquant la carte « Approuver » de la page, le résultat d'exécution arrive ensuite comme " +
                "note [Admin]. N'annoncez JAMAIS l'exécution avant cette note. Les garde-fous du plugin s'appliquent " +
                "à l'exécution (déjà possédé, déjà visionné, drop list, doublon — refus sans consommer le budget).";
            public string ArgumentsSchema =>
                "{\"type\":\"object\",\"properties\":{" +
                "\"title\":{\"type\":\"string\",\"description\":\"Titre du programme\"}," +
                "\"program_id\":{\"type\":\"string\",\"description\":\"Id du programme EPG (source=live)\"}," +
                "\"kind\":{\"type\":\"string\",\"enum\":[\"movie\",\"series\"],\"description\":\"Type de contenu\"}}," +
                "\"required\":[\"title\",\"program_id\"]}";

            /// <summary>Phase 1 (boucle LLM) : dépôt de la proposition,
            /// aucun effet — l'exécution passe par l'approbation admin.</summary>
            public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
            {
                string title = ArgString(args, "title");
                string programId = ArgString(args, "program_id");
                string kind = ArgString(args, "kind") ?? "movie";
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(programId))
                    return Task.FromResult(Json(new { status = "refused", detail = "title et program_id sont requis." }));
                return Task.FromResult(CreatePending(_sessionId, _adminUserId, ToolKey, args,
                    "Enregistrement « " + title.Trim() + " » (DVR, " +
                    (string.Equals(kind, "series", StringComparison.OrdinalIgnoreCase) ? "série" : "film") + ")"));
            }

            /// <summary>Phase 2 : exécution réelle — appelée UNIQUEMENT par
            /// l'endpoint d'approbation (budget consommé ici).</summary>
            public async Task<string> ExecuteCoreAsync(JsonElement args, CancellationToken ct)
            {
                try
                {
                    string title = ArgString(args, "title");
                    string programId = ArgString(args, "program_id");
                    string kind = ArgString(args, "kind") ?? "movie";
                    if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(programId))
                        return Json(new { status = "refused", detail = "title et program_id sont requis." });

                    var verdict = Reserve(_sessionId, 1, _cfg);
                    if (verdict != BudgetVerdict.Ok)
                        return BudgetRefusal(_sessionId, verdict, _cfg);

                    var programIds = new HashSet<string>();
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var ap = new AutoProgrammer(_liveTv, _library, _logger, _host);
                    ap.BuildExistingTimerSets(programIds, names);

                    var reco = new AutoProgrammer.Reco
                    {
                        Title = title.Trim(),
                        Id = programId.Trim(),
                        Kind = kind,
                        Source = "live",
                        Reason = "chat"
                    };
                    var outcome = await ap.ProgramOneAsync(reco, programIds, names, ct).ConfigureAwait(false);

                    if (outcome != AutoProgrammer.OneOutcome.Created)
                    {
                        Refund(_sessionId, 1); // garde-fou : pas une action réelle
                        return Json(new { status = "refused",
                            detail = "Enregistrement non programmé (" + outcome + "). " +
                                     "Rapportez le motif à l'admin, ne réessayez pas à l'identique." });
                    }

                    _logger?.Info("[LLM_AI] Chat action : timer créé pour « {0} » (programId={1}).", title, programId);
                    await ToastActionAsync(_sessionId, "Chat : timer programmé pour « " + title + " »", _logger).ConfigureAwait(false);
                    return Json(new { status = "ok", detail = "Enregistrement programmé : " + title });
                }
                catch (OperationCanceledException) { return Json(new { status = "failed", detail = "annulé" }); }
                catch (Exception ex)
                {
                    Refund(_sessionId, 1);
                    _logger?.Warn("[LLM_AI] Chat action record_program : {0}", ex.Message);
                    return Json(new { status = "failed", detail = ex.Message });
                }
            }
        }

        // ------------------------------------------------------------------
        //  Tool : create_card (carte .strm)
        // ------------------------------------------------------------------

        private class CreateCardTool : ILlmTool, IChatActionTool
        {
            private readonly PluginConfiguration _cfg;
            private readonly string _sessionId;
            private readonly string _adminUserId;
            private readonly ILibraryManager _library;
            private readonly ILiveTvManager _liveTv;
            private readonly IServerApplicationHost _host;
            private readonly ILogger _logger;

            public CreateCardTool(PluginConfiguration cfg, string sessionId, string adminUserId,
                ILibraryManager library, ILiveTvManager liveTv,
                IServerApplicationHost host, ILogger logger)
            {
                _cfg = cfg; _sessionId = sessionId; _adminUserId = adminUserId; _library = library;
                _liveTv = liveTv; _host = host; _logger = logger;
            }

            public string ToolKey => "create_card";
            public string Name => "create_card";
            public string Description =>
                "Crée une carte .strm dans la bibliothèque « AI Suggestions » (proposition d'enregistrement cliquable, " +
                "programme EPG à venir). DEUX PHASES : l'appel DÉPOSE une proposition (status \"awaiting_approval\") sans " +
                "exécuter — l'admin approuve en cliquant la carte « Approuver » de la page, le résultat d'exécution arrive " +
                "ensuite comme note [Admin]. N'annoncez JAMAIS l'exécution avant cette note. La carte est éphémère : " +
                "nettoyée par Emby à la prochaine génération planifiée.";
            public string ArgumentsSchema =>
                "{\"type\":\"object\",\"properties\":{" +
                "\"title\":{\"type\":\"string\"}," +
                "\"kind\":{\"type\":\"string\",\"enum\":[\"movie\",\"series\"]}," +
                "\"program_id\":{\"type\":\"string\",\"description\":\"Id du programme EPG (obligatoire)\"}," +
                "\"reason\":{\"type\":\"string\",\"description\":\"Pourquoi cette reco (1 phrase)\"}}," +
                "\"required\":[\"title\",\"program_id\"]}";

            /// <summary>Phase 1 : dépôt, aucun effet.</summary>
            public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
            {
                string title = ArgString(args, "title");
                string programId = ArgString(args, "program_id");
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(programId))
                    return Task.FromResult(Json(new { status = "refused",
                        detail = "title et program_id sont requis (une carte pointe un programme EPG à venir)." }));
                string reason = ArgString(args, "reason") ?? "";
                return Task.FromResult(CreatePending(_sessionId, _adminUserId, ToolKey, args,
                    "Carte .strm « " + title.Trim() + " »" +
                    (string.IsNullOrWhiteSpace(reason) ? "" : " — " + reason.Trim())));
            }

            /// <summary>Phase 2 : exécution réelle (approbation uniquement).</summary>
            public async Task<string> ExecuteCoreAsync(JsonElement args, CancellationToken ct)
            {
                try
                {
                    string title = ArgString(args, "title");
                    string kind = ArgString(args, "kind") ?? "movie";
                    string programId = ArgString(args, "program_id");
                    string reason = ArgString(args, "reason") ?? "";
                    if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(programId))
                        return Json(new { status = "refused",
                            detail = "title et program_id sont requis (une carte pointe un programme EPG à venir)." });

                    var verdict = Reserve(_sessionId, 1, _cfg);
                    if (verdict != BudgetVerdict.Ok)
                        return BudgetRefusal(_sessionId, verdict, _cfg);

                    var reco = new AutoProgrammer.Reco
                    {
                        Title = title.Trim(),
                        Kind = kind,
                        Id = programId.Trim(),
                        Source = "live",
                        Reason = string.IsNullOrWhiteSpace(reason) ? "chat" : reason.Trim()
                    };
                    var gen = new StrmLibraryGenerator(_library, _liveTv, _host, _logger);
                    bool ok = await gen.WriteSingleCardAsync(reco, _cfg, ct).ConfigureAwait(false);
                    if (!ok)
                    {
                        Refund(_sessionId, 1);
                        return Json(new { status = "failed",
                            detail = "Écriture de la carte impossible (bibliothèque .strm absente ou erreur)." });
                    }

                    _logger?.Info("[LLM_AI] Chat action : carte .strm créée pour « {0} » (source={1}).", title, reco.Source);
                    await ToastActionAsync(_sessionId, "Chat : carte .strm « " + title + " » créée", _logger).ConfigureAwait(false);
                    return Json(new { status = "ok", detail = "Carte créée dans la bibliothèque « AI Suggestions » : " + title });
                }
                catch (OperationCanceledException) { return Json(new { status = "failed", detail = "annulé" }); }
                catch (Exception ex)
                {
                    Refund(_sessionId, 1);
                    _logger?.Warn("[LLM_AI] Chat action create_card : {0}", ex.Message);
                    return Json(new { status = "failed", detail = ex.Message });
                }
            }
        }

        // ------------------------------------------------------------------
        //  Tool : tag_ai_tonight (tag « AI Tonight »)
        // ------------------------------------------------------------------

        private class TagTonightTool : ILlmTool, IChatActionTool
        {
            private readonly PluginConfiguration _cfg;
            private readonly string _sessionId;
            private readonly string _adminUserId;
            private readonly ILibraryManager _library;
            private readonly ILogger _logger;

            public TagTonightTool(PluginConfiguration cfg, string sessionId, string adminUserId,
                ILibraryManager library, ILogger logger)
            {
                _cfg = cfg; _sessionId = sessionId; _adminUserId = adminUserId; _library = library; _logger = logger;
            }

            public string ToolKey => "tag_ai_tonight";
            public string Name => "tag_ai_tonight";
            public string Description =>
                "Étiquette des items de la bibliothèque avec le tag « AI Tonight » (filtre par tag dans Emby). " +
                "DEUX PHASES : l'appel DÉPOSE une proposition (status \"awaiting_approval\") sans exécuter — l'admin " +
                "approuve en cliquant la carte « Approuver » de la page, le résultat d'exécution arrive ensuite comme " +
                "note [Admin]. N'annoncez JAMAIS l'exécution avant cette note. 1 à 10 ids.";
            public string ArgumentsSchema =>
                "{\"type\":\"object\",\"properties\":{" +
                "\"item_ids\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"Ids items (1-10)\"}}," +
                "\"required\":[\"item_ids\"]}";

            /// <summary>Phase 1 : dépôt, aucun effet.</summary>
            public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
            {
                var ids = ArgIdList(args, "item_ids");
                if (ids == null || ids.Count == 0)
                    return Task.FromResult(Json(new { status = "refused", detail = "item_ids requis (1-10 ids)." }));
                if (ids.Count > 10)
                    return Task.FromResult(Json(new { status = "refused", detail = "10 ids maximum par appel." }));
                return Task.FromResult(CreatePending(_sessionId, _adminUserId, ToolKey, args,
                    "Tag « AI Tonight » : " + ids.Count + " item(s)"));
            }

            /// <summary>Phase 2 : exécution réelle (approbation uniquement).</summary>
            public async Task<string> ExecuteCoreAsync(JsonElement args, CancellationToken ct)
            {
                try
                {
                    var ids = ArgIdList(args, "item_ids");
                    if (ids == null || ids.Count == 0)
                        return Json(new { status = "refused", detail = "item_ids requis (1-10 ids)." });
                    if (ids.Count > 10)
                        return Json(new { status = "refused", detail = "10 ids maximum par appel." });

                    var verdict = Reserve(_sessionId, ids.Count, _cfg);
                    if (verdict != BudgetVerdict.Ok)
                        return BudgetRefusal(_sessionId, verdict, _cfg);

                    // AddAsync est best-effort par id (un id non résolvable est
                    // logué et sauté) : le lot est consommé tel quel.
                    await AiTagger.AddAsync(_library, _logger, ids, AiTagger.TonightTag, ct)
                        .ConfigureAwait(false);

                    _logger?.Info("[LLM_AI] Chat action : {0} item(s) taggé(s) « {1} ».", ids.Count, AiTagger.TonightTag);
                    await ToastActionAsync(_sessionId, "Chat : " + ids.Count + " item(s) tagué(s) « " + AiTagger.TonightTag + " »",
                        _logger).ConfigureAwait(false);
                    return Json(new { status = "ok", detail = ids.Count + " item(s) étiqueté(s) « " + AiTagger.TonightTag + " »." });
                }
                catch (OperationCanceledException) { return Json(new { status = "failed", detail = "annulé" }); }
                catch (Exception ex)
                {
                    _logger?.Warn("[LLM_AI] Chat action tag_ai_tonight : {0}", ex.Message);
                    return Json(new { status = "failed", detail = ex.Message });
                }
            }
        }

        // ------------------------------------------------------------------
        //  Tools : collection (additif + retrait tracé)
        // ------------------------------------------------------------------

        private class CollectionAddTool : ILlmTool, IChatActionTool
        {
            private readonly PluginConfiguration _cfg;
            private readonly string _sessionId;
            private readonly string _adminUserId;
            private readonly ICollectionManager _collections;
            private readonly ILibraryManager _library;
            private readonly IServerApplicationHost _host;
            private readonly ILogger _logger;

            public CollectionAddTool(PluginConfiguration cfg, string sessionId, string adminUserId,
                ICollectionManager collections, ILibraryManager library,
                IServerApplicationHost host, ILogger logger)
            {
                _cfg = cfg; _sessionId = sessionId; _adminUserId = adminUserId; _collections = collections;
                _library = library; _host = host; _logger = logger;
            }

            public string ToolKey => "collection_add";
            public string Name => "collection_add";
            public string Description =>
                "Ajoute des items à la collection Emby « AI Tonight » (additif — ne touche pas aux membres existants). " +
                "DEUX PHASES : l'appel DÉPOSE une proposition (status \"awaiting_approval\") sans exécuter — l'admin " +
                "approuve en cliquant la carte « Approuver » de la page, le résultat d'exécution arrive ensuite comme " +
                "note [Admin]. N'annoncez JAMAIS l'exécution avant cette note. 1 à 10 ids.";
            public string ArgumentsSchema =>
                "{\"type\":\"object\",\"properties\":{" +
                "\"item_ids\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"Ids items (1-10)\"}}," +
                "\"required\":[\"item_ids\"]}";

            /// <summary>Phase 1 : dépôt, aucun effet.</summary>
            public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
            {
                var ids = ArgIdList(args, "item_ids");
                if (ids == null || ids.Count == 0)
                    return Task.FromResult(Json(new { status = "refused", detail = "item_ids requis (1-10 ids)." }));
                if (ids.Count > 10)
                    return Task.FromResult(Json(new { status = "refused", detail = "10 ids maximum par appel." }));
                return Task.FromResult(CreatePending(_sessionId, _adminUserId, ToolKey, args,
                    "Ajout à la collection « AI Tonight » : " + ids.Count + " item(s)"));
            }

            /// <summary>Phase 2 : exécution réelle (approbation uniquement).</summary>
            public async Task<string> ExecuteCoreAsync(JsonElement args, CancellationToken ct)
            {
                try
                {
                    var ids = ArgIdList(args, "item_ids");
                    if (ids == null || ids.Count == 0)
                        return Json(new { status = "refused", detail = "item_ids requis (1-10 ids)." });
                    if (ids.Count > 10)
                        return Json(new { status = "refused", detail = "10 ids maximum par appel." });

                    var norm = ids.Select(s => NormId(_library, s)).Where(s => s != null).Distinct().ToList();
                    if (norm.Count == 0)
                        return Json(new { status = "refused", detail = "aucun id résolvable dans la bibliothèque." });

                    var verdict = Reserve(_sessionId, norm.Count, _cfg);
                    if (verdict != BudgetVerdict.Ok)
                        return BudgetRefusal(_sessionId, verdict, _cfg);

                    int added = await AiTonightCollectionManager.AddItemsAsync(
                        _collections, _library, _logger, _host, norm, ct).ConfigureAwait(false);
                    if (added <= 0)
                    {
                        Refund(_sessionId, norm.Count);
                        return Json(new { status = "failed", detail = "ajout impossible (API collection)." });
                    }

                    var st = ChatActions.For(_sessionId);
                    lock (st) { foreach (var n in norm) st.CollectionAdded.Add(n); }
                    if (added < norm.Count) Refund(_sessionId, norm.Count - added);

                    _logger?.Info("[LLM_AI] Chat action : {0} item(s) ajouté(s) à la collection « {1} ».",
                        added, AiTonightCollectionManager.CollectionName);
                    await ToastActionAsync(_sessionId, "Chat : " + added + " item(s) ajouté(s) à la collection « "
                        + AiTonightCollectionManager.CollectionName + " »", _logger).ConfigureAwait(false);
                    return Json(new { status = "ok", detail = added + " item(s) ajouté(s) à la collection « "
                        + AiTonightCollectionManager.CollectionName + " »." });
                }
                catch (OperationCanceledException) { return Json(new { status = "failed", detail = "annulé" }); }
                catch (Exception ex)
                {
                    _logger?.Warn("[LLM_AI] Chat action collection_add : {0}", ex.Message);
                    return Json(new { status = "failed", detail = ex.Message });
                }
            }
        }

        private class CollectionRemoveTool : ILlmTool, IChatActionTool
        {
            private readonly PluginConfiguration _cfg;
            private readonly string _sessionId;
            private readonly string _adminUserId;
            private readonly ICollectionManager _collections;
            private readonly ILibraryManager _library;
            private readonly ILogger _logger;

            public CollectionRemoveTool(PluginConfiguration cfg, string sessionId, string adminUserId,
                ICollectionManager collections, ILibraryManager library, ILogger logger)
            {
                _cfg = cfg; _sessionId = sessionId; _adminUserId = adminUserId; _collections = collections;
                _library = library; _logger = logger;
            }

            public string ToolKey => "collection_remove";
            public string Name => "collection_remove";
            public string Description =>
                "Retire des items de la collection « AI Tonight » — SEULEMENT des items que vous avez ajoutés " +
                "vous-même dans cette conversation (collection_add). Toute autre demande est refusée. " +
                "DEUX PHASES : l'appel DÉPOSE une proposition (status \"awaiting_approval\") sans exécuter — " +
                "l'admin approuve en cliquant la carte « Approuver » de la page, le résultat d'exécution arrive " +
                "ensuite comme note [Admin]. N'annoncez JAMAIS l'exécution avant cette note.";
            public string ArgumentsSchema =>
                "{\"type\":\"object\",\"properties\":{" +
                "\"item_ids\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}}," +
                "\"required\":[\"item_ids\"]}";

            /// <summary>Phase 1 : dépôt, aucun effet.</summary>
            public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
            {
                var ids = ArgIdList(args, "item_ids");
                if (ids == null || ids.Count == 0)
                    return Task.FromResult(Json(new { status = "refused", detail = "item_ids requis." }));
                return Task.FromResult(CreatePending(_sessionId, _adminUserId, ToolKey, args,
                    "Retrait de la collection « AI Tonight » : " + ids.Count + " item(s)"));
            }

            /// <summary>Phase 2 : exécution réelle (approbation uniquement).</summary>
            public async Task<string> ExecuteCoreAsync(JsonElement args, CancellationToken ct)
            {
                try
                {
                    var ids = ArgIdList(args, "item_ids");
                    if (ids == null || ids.Count == 0)
                        return Json(new { status = "refused", detail = "item_ids requis." });

                    var st = ChatActions.For(_sessionId);
                    List<string> eligible;
                    lock (st) eligible = ids.Select(s => NormId(_library, s)).Where(s => s != null && st.CollectionAdded.Contains(s)).ToList();
                    if (eligible.Count == 0)
                        return Json(new { status = "refused",
                            detail = "Aucun de ces ids n'a été ajouté par vous dans cette conversation — retrait refusé." });

                    var verdict = Reserve(_sessionId, eligible.Count, _cfg);
                    if (verdict != BudgetVerdict.Ok)
                        return BudgetRefusal(_sessionId, verdict, _cfg);

                    int removed = await AiTonightCollectionManager.RemoveItemsAsync(
                        _collections, _library, _logger, eligible, ct).ConfigureAwait(false);
                    if (removed < eligible.Count) Refund(_sessionId, eligible.Count - removed);

                    // Retrait de la trace de tracking pour TOUT l'ensemble
                    // demandé (réalisé ou échoué — on ne retente pas un id
                    // qui a déjà échoué dans cette conversation).
                    lock (st)
                    {
                        foreach (var n in eligible) st.CollectionAdded.Remove(n);
                    }

                    _logger?.Info("[LLM_AI] Chat action : {0} item(s) retiré(s) de la collection « {1} ».",
                        removed, AiTonightCollectionManager.CollectionName);
                    await ToastActionAsync(_sessionId, "Chat : " + removed + " item(s) retiré(s) de la collection « "
                        + AiTonightCollectionManager.CollectionName + " »", _logger).ConfigureAwait(false);
                    return Json(new { status = "ok", detail = removed + " item(s) retiré(s) de la collection." });
                }
                catch (OperationCanceledException) { return Json(new { status = "failed", detail = "annulé" }); }
                catch (Exception ex)
                {
                    _logger?.Warn("[LLM_AI] Chat action collection_remove : {0}", ex.Message);
                    return Json(new { status = "failed", detail = ex.Message });
                }
            }
        }

        // ------------------------------------------------------------------
        //  Tools : playlist (additif + retrait tracé)
        // ------------------------------------------------------------------

        private class PlaylistAddTool : ILlmTool, IChatActionTool
        {
            private readonly PluginConfiguration _cfg;
            private readonly string _sessionId;
            private readonly string _adminUserId;
            private readonly IPlaylistManager _playlists;
            private readonly ILibraryManager _library;
            private readonly User _adminUser;
            private readonly IServerApplicationHost _host;
            private readonly ILogger _logger;

            public PlaylistAddTool(PluginConfiguration cfg, string sessionId, string adminUserId,
                IPlaylistManager playlists, ILibraryManager library,
                User adminUser, IServerApplicationHost host, ILogger logger)
            {
                _cfg = cfg; _sessionId = sessionId; _adminUserId = adminUserId; _playlists = playlists;
                _library = library; _adminUser = adminUser; _host = host; _logger = logger;
            }

            public string ToolKey => "playlist_add";
            public string Name => "playlist_add";
            public string Description =>
                "Ajoute des items à la playlist privée du compte admin (« AI Tonight · {admin} », v1.13.18.0 — " +
                "la playlist publique foyer « AI Tonight » reste remplie uniquement par le run « Watch Tonight » " +
                "avec intersection parentale). Additif — ne touche pas aux entrées existantes. " +
                "DEUX PHASES : l'appel DÉPOSE une proposition (status \"awaiting_approval\") sans exécuter — l'admin " +
                "approuve en cliquant la carte « Approuver » de la page, le résultat d'exécution arrive ensuite comme " +
                "note [Admin]. N'annoncez JAMAIS l'exécution avant cette note. 1 à 10 ids.";
            public string ArgumentsSchema =>
                "{\"type\":\"object\",\"properties\":{" +
                "\"item_ids\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"Ids items (1-10)\"}}," +
                "\"required\":[\"item_ids\"]}";

            /// <summary>Phase 1 : dépôt, aucun effet.</summary>
            public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
            {
                var ids = ArgIdList(args, "item_ids");
                if (ids == null || ids.Count == 0)
                    return Task.FromResult(Json(new { status = "refused", detail = "item_ids requis (1-10 ids)." }));
                if (ids.Count > 10)
                    return Task.FromResult(Json(new { status = "refused", detail = "10 ids maximum par appel." }));
                return Task.FromResult(CreatePending(_sessionId, _adminUserId, ToolKey, args,
                    "Ajout à la playlist privée de l'admin : " + ids.Count + " item(s)"));
            }

            /// <summary>Phase 2 : exécution réelle (approbation uniquement).</summary>
            public async Task<string> ExecuteCoreAsync(JsonElement args, CancellationToken ct)
            {
                try
                {
                    var ids = ArgIdList(args, "item_ids");
                    if (ids == null || ids.Count == 0)
                        return Json(new { status = "refused", detail = "item_ids requis (1-10 ids)." });
                    if (ids.Count > 10)
                        return Json(new { status = "refused", detail = "10 ids maximum par appel." });

                    var norm = ids.Select(s => NormId(_library, s)).Where(s => s != null).Distinct().ToList();
                    if (norm.Count == 0)
                        return Json(new { status = "refused", detail = "aucun id résolvable dans la bibliothèque." });

                    // Hygiène v1.13.2 : AddItemsAsync normalise en feuilles
                    // (série → épisode next up) et déduplique — le budget est
                    // réservé sur les ids soumis puis remboursé au différentiel
                    // (les items déjà présents ne consomment rien).
                    var verdict = Reserve(_sessionId, norm.Count, _cfg);
                    if (verdict != BudgetVerdict.Ok)
                        return BudgetRefusal(_sessionId, verdict, _cfg);

                    // v1.13.18.0 : cible = playlist PRIVÉE du compte admin
                    // (le chat est admin-only). Un item ajouté au chat n'a pas
                    // traversé l'intersection parentale du run « Tonight » —
                    // le poser dans la privée de l'admin referme le
                    // contournement de la publique foyer.
                    if (_adminUser == null)
                    {
                        Refund(_sessionId, norm.Count);
                        return Json(new { status = "refused", detail = "aucun usager admin résolvable pour la playlist." });
                    }
                    string targetName = AiTonightPlaylistManager.UserPlaylistName(_adminUser);

                    var addedIds = await AiTonightPlaylistManager.AddItemsAsync(
                        _playlists, _library, _logger, norm, _adminUser, _host, ct).ConfigureAwait(false);
                    int added = addedIds.Count;
                    if (added <= 0)
                    {
                        Refund(_sessionId, norm.Count);
                        return Json(new { status = "refused",
                            detail = "aucun ajout effectué (items déjà présents dans la playlist, ou échec API)." });
                    }

                    var st = ChatActions.For(_sessionId);
                    lock (st) { foreach (var n in addedIds) st.PlaylistAdded.Add(n.ToString(CultureInfo.InvariantCulture)); }
                    if (added < norm.Count) Refund(_sessionId, norm.Count - added);

                    _logger?.Info("[LLM_AI] Chat action : {0} item(s) ajouté(s) à la playlist « {1} ».",
                        added, targetName);
                    await ToastActionAsync(_sessionId, "Chat : " + added + " item(s) ajouté(s) à la playlist « "
                        + targetName + " »", _logger).ConfigureAwait(false);
                    return Json(new { status = "ok", detail = added + " item(s) ajouté(s) à la playlist « "
                        + targetName + " » (privée, compte admin)." });
                }
                catch (OperationCanceledException) { return Json(new { status = "failed", detail = "annulé" }); }
                catch (Exception ex)
                {
                    _logger?.Warn("[LLM_AI] Chat action playlist_add : {0}", ex.Message);
                    return Json(new { status = "failed", detail = ex.Message });
                }
            }
        }

        private class PlaylistRemoveTool : ILlmTool, IChatActionTool
        {
            private readonly PluginConfiguration _cfg;
            private readonly string _sessionId;
            private readonly string _adminUserId;
            private readonly IPlaylistManager _playlists;
            private readonly ILibraryManager _library;
            private readonly User _adminUser;
            private readonly ILogger _logger;

            public PlaylistRemoveTool(PluginConfiguration cfg, string sessionId, string adminUserId,
                IPlaylistManager playlists, ILibraryManager library, User adminUser, ILogger logger)
            {
                _cfg = cfg; _sessionId = sessionId; _adminUserId = adminUserId; _playlists = playlists;
                _library = library; _adminUser = adminUser; _logger = logger;
            }

            public string ToolKey => "playlist_remove";
            public string Name => "playlist_remove";
            public string Description =>
                "Retire des items de la playlist privée du compte admin (« AI Tonight · {admin} », v1.13.18.0 — " +
                "symétrique de playlist_add) — SEULEMENT des items que vous avez ajoutés " +
                "vous-même dans cette conversation (les InternalId items sont les ids d'entrée de la playlist). " +
                "Toute autre demande est refusée. DEUX PHASES : l'appel DÉPOSE une proposition " +
                "(status \"awaiting_approval\") sans exécuter — l'admin approuve en cliquant la carte " +
                "« Approuver » de la page, le résultat d'exécution arrive ensuite comme note [Admin]. " +
                "N'annoncez JAMAIS l'exécution avant cette note.";
            public string ArgumentsSchema =>
                "{\"type\":\"object\",\"properties\":{" +
                "\"item_ids\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}}," +
                "\"required\":[\"item_ids\"]}";

            /// <summary>Phase 1 : dépôt, aucun effet.</summary>
            public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
            {
                var ids = ArgIdList(args, "item_ids");
                if (ids == null || ids.Count == 0)
                    return Task.FromResult(Json(new { status = "refused", detail = "item_ids requis." }));
                return Task.FromResult(CreatePending(_sessionId, _adminUserId, ToolKey, args,
                    "Retrait de la playlist privée de l'admin : " + ids.Count + " item(s)"));
            }

            /// <summary>Phase 2 : exécution réelle (approbation uniquement).</summary>
            public async Task<string> ExecuteCoreAsync(JsonElement args, CancellationToken ct)
            {
                try
                {
                    var ids = ArgIdList(args, "item_ids");
                    if (ids == null || ids.Count == 0)
                        return Json(new { status = "refused", detail = "item_ids requis." });

                    var st = ChatActions.For(_sessionId);
                    List<string> eligible;
                    lock (st) eligible = ids.Select(s => NormId(_library, s)).Where(s => s != null && st.PlaylistAdded.Contains(s)).ToList();
                    if (eligible.Count == 0)
                        return Json(new { status = "refused",
                            detail = "Aucun de ces ids n'a été ajouté par vous dans cette conversation — retrait refusé." });

                    var verdict = Reserve(_sessionId, eligible.Count, _cfg);
                    if (verdict != BudgetVerdict.Ok)
                        return BudgetRefusal(_sessionId, verdict, _cfg);

                    // Comptage honnête : RemoveFromPlaylist est inopérant sur
                    // ce build Emby (v1.13.2) — seuls les ids réellement
                    // disparus du listing comptent (0 sur ce build), le reste
                    // est remboursé et reste traçable.
                    var removedIds = await AiTonightPlaylistManager.RemoveItemsAsync(
                        _playlists, _library, _logger, eligible, _adminUser, ct).ConfigureAwait(false);
                    var removed = removedIds.Select(i => i.ToString(CultureInfo.InvariantCulture)).ToList();
                    if (removed.Count < eligible.Count) Refund(_sessionId, eligible.Count - removed.Count);

                    lock (st)
                    {
                        foreach (var n in removed) st.PlaylistAdded.Remove(n);
                    }

                    if (removed.Count == 0)
                    {
                        _logger?.Info("[LLM_AI] Chat action : retrait playlist sans effet (RemoveFromPlaylist inopérant sur ce build).");
                        return Json(new { status = "failed",
                            detail = "Le retrait n'a PAS été appliqué : RemoveFromPlaylist est inopérant sur ce build Emby. "
                                + "Ne réessayez pas — le prochain run Tonight recrée la playlist de toute façon." });
                    }

                    string targetName = _adminUser != null
                        ? AiTonightPlaylistManager.UserPlaylistName(_adminUser)
                        : AiTonightPlaylistManager.PlaylistName;
                    _logger?.Info("[LLM_AI] Chat action : {0} entrée(s) retirée(s) de la playlist « {1} ».",
                        removed.Count, targetName);
                    await ToastActionAsync(_sessionId, "Chat : " + removed.Count + " item(s) retiré(s) de la playlist « "
                        + targetName + " »", _logger).ConfigureAwait(false);
                    return Json(new { status = "ok", detail = removed.Count + " item(s) retiré(s) de la playlist privée du compte admin." });
                }
                catch (OperationCanceledException) { return Json(new { status = "failed", detail = "annulé" }); }
                catch (Exception ex)
                {
                    _logger?.Warn("[LLM_AI] Chat action playlist_remove : {0}", ex.Message);
                    return Json(new { status = "failed", detail = ex.Message });
                }
            }
        }

        // ------------------------------------------------------------------
        //  Tool : run_tonight_run (déclenchement du run « ce soir »)
        // ------------------------------------------------------------------

        private class RunTonightTool : ILlmTool, IChatActionTool
        {
            private readonly PluginConfiguration _cfg;
            private readonly string _sessionId;
            private readonly string _adminUserId;
            private readonly User _adminUser;
            private readonly IUserManager _users;
            private readonly IJsonSerializer _json;
            private readonly ILibraryManager _library;
            private readonly ILiveTvManager _liveTv;
            private readonly IServerApplicationHost _host;
            private readonly ILogger _logger;

            public RunTonightTool(PluginConfiguration cfg, string sessionId, User adminUser, string adminUserId,
                IUserManager users, IJsonSerializer json, ILibraryManager library,
                ILiveTvManager liveTv, IServerApplicationHost host, ILogger logger)
            {
                _cfg = cfg; _sessionId = sessionId; _adminUser = adminUser; _adminUserId = adminUserId;
                _users = users; _json = json; _library = library; _liveTv = liveTv; _host = host; _logger = logger;
            }

            public string ToolKey => "run_tonight_run";
            public string Name => "run_tonight_run";
            public string Description =>
                "Déclenche le run « À regarder ce soir » (même code path que la tâche planifiée et le login : " +
                "cache, profil, EPG, surfaces configurées — genre/collection/playlist). Les directives de session " +
                "(optionnelles, 500 caractères max) ne valent QUE pour ce run. Un seul run à la fois, 2 maximum " +
                "par conversation. DEUX PHASES : l'appel DÉPOSE une proposition (status \"awaiting_approval\") " +
                "sans exécuter — l'admin approuve en cliquant la carte « Approuver » de la page, le résultat " +
                "d'exécution arrive ensuite comme note [Admin]. N'annoncez JAMAIS l'exécution avant cette note.";
            public string ArgumentsSchema =>
                "{\"type\":\"object\",\"properties\":{" +
                "\"directives\":{\"type\":\"string\",\"description\":\"Directives one-shot pour ce run (max 500 caractères)\"}}}";

            /// <summary>Phase 1 : dépôt, aucun effet.</summary>
            public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
            {
                string directives = (ArgString(args, "directives") ?? "").Trim();
                if (directives.Length > 500) directives = directives.Substring(0, 500);
                return Task.FromResult(CreatePending(_sessionId, _adminUserId, ToolKey, args,
                    "Run « À regarder ce soir »" +
                    (directives.Length > 0 ? " (directives : " +
                        (directives.Length > 80 ? directives.Substring(0, 80) + "…" : directives) + ")" : "")));
            }

            /// <summary>Phase 2 : exécution réelle (approbation uniquement).</summary>
            public async Task<string> ExecuteCoreAsync(JsonElement args, CancellationToken ct)
            {
                try
                {
                    if (!_cfg.TonightEnabled)
                        return Json(new { status = "refused", detail = "Le module « À regarder ce soir » est désactivé dans la config." });

                    if (!TryBeginRun())
                        return Json(new { status = "refused", detail = "Un run « ce soir » déclenché par le chat est déjà en cours — réessayez plus tard." });
                    try
                    {
                        var st = ChatActions.For(_sessionId);
                        lock (st)
                        {
                            if (st.RunCount >= 2)
                                return Json(new { status = "refused",
                                    detail = "Limite de 2 runs par conversation atteinte." });
                        }

                        var verdict = Reserve(_sessionId, 1, _cfg);
                        if (verdict != BudgetVerdict.Ok)
                            return BudgetRefusal(_sessionId, verdict, _cfg);

                        // Directives one-shot : éphémères, plafonnées, JAMAIS
                        // persistées (injectées dans le prompt de CE run
                        // uniquement, cf. TonightService).
                        string directives = (ArgString(args, "directives") ?? "").Trim();
                        if (directives.Length > 500)
                        {
                            directives = directives.Substring(0, 500);
                            _logger?.Info("[LLM_AI] Chat action : directives de session tronquées à 500 caractères.");
                        }

                        User user = TonightService.ResolveTonightUser(_users, _cfg) ?? _adminUser;
                        if (user == null)
                        {
                            Refund(_sessionId, 1);
                            return Json(new { status = "refused", detail = "aucun usager résolvable pour le run." });
                        }

                        lock (st) st.RunCount++;
                        _logger?.Info("[LLM_AI] Chat action : run Tonight déclenché par le chat (directives={0} caractères).",
                            directives.Length);

                        var svc = new TonightService(_users, _library, _liveTv, _json, _host, _logger,
                            null, null, null);
                        var res = await svc.GenerateTonightAsync(user, _cfg, refresh: true, ct,
                            sessionDirectives: directives, fromChat: true).ConfigureAwait(false);

                        if (!string.IsNullOrEmpty(res.Error))
                        {
                            Refund(_sessionId, 1); // run raté : pas une action réalisée
                            lock (st) st.RunCount--;
                            return Json(new { status = "failed", detail = res.Error });
                        }

                        var titles = new List<string>();
                        try
                        {
                            using (var doc = JsonDocument.Parse(res.Payload))
                            {
                                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                                    foreach (var el in doc.RootElement.EnumerateArray())
                                    {
                                        if (el.ValueKind != JsonValueKind.Object) continue;
                                        if (el.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String)
                                            titles.Add(t.GetString());
                                    }
                            }
                        }
                        catch { /* payload non parsable : rapport sans titres */ }

                        await ToastActionAsync(_sessionId, "Chat : run « ce soir » lancé (" + titles.Count + " reco(s))",
                            _logger).ConfigureAwait(false);
                        return Json(new
                        {
                            status = "ok",
                            detail = (titles.Count + " recommandation(s) générée(s) et livrée(s) via les surfaces "
                                + "habituelles (page Recommandations, genre/collection/playlist selon la config) : ")
                                + string.Join(", ", titles)
                        });
                    }
                    finally { EndRun(); }
                }
                catch (OperationCanceledException) { return Json(new { status = "failed", detail = "annulé" }); }
                catch (Exception ex)
                {
                    _logger?.Warn("[LLM_AI] Chat action run_tonight_run : {0}", ex.Message);
                    return Json(new { status = "failed", detail = ex.Message });
                }
            }
        }
    }
}