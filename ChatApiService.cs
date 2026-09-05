using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
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
        /// à chaque appel (serveur stateless). <c>Session</c> : identifiant
        /// de session de mémoire de conversation (retourné par la première
        /// réponse puis rejoué ; vide = nouvelle conversation).
        /// </summary>
        [Route("/Plugins/LLMAI/Chat", "POST")]
        public class ChatRequest : IReturn<object>
        {
            public string Message { get; set; }
            public List<ChatTurn> History { get; set; }
            public string Session { get; set; }
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
                    KickSummarizeStale(cfg, userId);
                memoryBlock = ChatMemoryStore.BuildInjectionBlock(cfg, userId,
                    existingSession != null ? sessionId : string.Empty);
                if (memoryBlock.Length > 0)
                    Logger.Info("[LLM_AI] [CHAT] Mémoire de conversation injectée ({0} caractères).",
                        memoryBlock.Length);
            }

            // LlmRunner construit avec les services de la base + liveTv,
            // exactement comme sur le path d'audit.
            var runner = new LlmRunner(Logger, _json, LibraryManager, UserManager, _liveTv, ApplicationHost);
            string reply;
            try
            {
                reply = await runner.RunChatAsync(cfg, "CHAT", history, message,
                    _sessions, _tasks, _notifications, ct, memoryBlock).ConfigureAwait(false);
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

            return new ChatResponse
            {
                Enabled = true,
                Reply = reply,
                Date = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Session = savedSession
            };
        }

        // ------------------------------------------------------------------
        //  Mémoire de conversation : condensation paresseuse + endpoints
        // ------------------------------------------------------------------

        // La note de continuité est produite UN appel LLM sans outils, au
        // moment où la session devient passée (retour de l'usager) — jamais
        // pendant la conversation (coût par tour). Tâche de fond
        // (fire-and-forget) : au premier tour ou à l'ouverture de la page,
        // l'usager n'attend rien ; au clic « Reprendre », le résumé est
        // généralement prêt (sinon : les derniers tours verbatim suffisent).
        internal const string SUMMARY_ROLE =
            "Tu es l'assistant de recommandations TV/cinéma d'un serveur Emby. " +
            "Un usager vient de terminer une conversation avec toi. Tu produis une NOTE DE " +
            "CONTINUITÉ : ce que TU-MÊME dois retenir pour reprendre ce fil plus tard.";

        internal const string SUMMARY_RULES =
            "### TA TÂCHE\n" +
            "Écris une note Markdown (maximum ~1500 caractères) avec ces sections exactes :\n" +
            "- ## Goûts exprimés — ce que l'usager a affirmé aimer/détester, avec les titres " +
            "  cités et SA réaction (a accepté / a rejeté / a ignoré tes suggestions).\n" +
            "- ## Faits utiles — contraintes, habitudes, chaînes, appareils mentionnés.\n" +
            "- ## Fil ouvert — ce qu'on cherchait et qui n'a pas été résolu (reprendre ici).\n" +
            "### RÈGLES\n" +
            "- N'invente rien : uniquement ce qui est dit dans la transcription.\n" +
            "- Puces courtes ; pas de préambule ; pas de JSON dans la note.\n" +
            "### FIN OBLIGATOIRE\n" +
            "Termine par UNE dernière ligne :\n" +
            "SIGNALS: [{\"t\":\"titre\",\"w\":\"pourquoi\",\"s\":\"+\"}] — le tableau JSON des " +
            "signaux de goût extraits (s = \"+\" goût affirmé, \"-\" rejet ; [] si aucun). " +
            "Rien après cette ligne.";

        /// <summary>Plafonds de la transcription soumise au résumé (modèle
        /// local : la fenêtre doit rester raisonnable).</summary>
        internal const int SummaryMaxTurns = 30;
        internal const int SummaryMaxTurnChars = 800;

        /// <summary>Résumés en cours (anti-doublon des tâches de fond).</summary>
        private static readonly object _summarizeLock = new object();
        private static readonly HashSet<string> _summarizing = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Déclenche (tâche de fond, fire-and-forget, best-effort) la
        /// condensation des sessions passées jamais résumées de cet usager.
        /// </summary>
        private void KickSummarizeStale(PluginConfiguration cfg, string userId)
        {
            if (cfg == null || !cfg.ChatMemoryEnabled) return;
            List<ChatMemorySession> stale;
            try
            {
                stale = ChatMemoryStore.ListSessions(cfg, userId)
                    .Where(s => s != null && s.Turns > 0 && string.IsNullOrWhiteSpace(s.Summary))
                    .ToList();
            }
            catch { return; }
            foreach (var s in stale)
            {
                var sid = s.Id;
                lock (_summarizeLock)
                {
                    if (!_summarizing.Add(sid)) continue;
                }
                var capturedCfg = cfg;
                var capturedUser = userId;
                System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        // Recharge la session (les tours ont pu grossir).
                        var session = ChatMemoryStore.ListSessions(capturedCfg, capturedUser)
                            .FirstOrDefault(x => string.Equals(x.Id, sid, StringComparison.Ordinal));
                        if (session == null || session.Turns == 0) return;

                        var runner = new LlmRunner(Logger, _json, LibraryManager, UserManager,
                            _liveTv, ApplicationHost);
                        var (note, signals) = await SummarizeSessionAsync(
                            capturedCfg, runner, session, capturedUser,
                            System.Threading.CancellationToken.None).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(note))
                        {
                            ChatMemoryStore.SetSummary(capturedCfg, capturedUser, sid, note, Logger);
                            Logger.Info("[LLM_AI] [CHAT] Session {0} condensée ({1} caractères).",
                                sid, note.Length);
                            AppendChatSignals(capturedCfg, capturedUser, signals);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Fail-open : pas de résumé, la conversation suivante
                        // continue sans.
                        Logger.Debug("[LLM_AI] [CHAT] Condensation de session échouée : {0}", ex.Message);
                    }
                    finally
                    {
                        lock (_summarizeLock) _summarizing.Remove(sid);
                    }
                });
            }
        }

        /// <summary>
        /// Condense une session : transcription (derniers tours, bornés) →
        /// note de continuité + ligne SIGNALS (signaux de goût). Retourne
        /// (note "", signaux vide) si le LLM ne répond pas ou si la réponse
        /// est inutilisable.
        /// </summary>
        private async System.Threading.Tasks.Task<(string note, List<(string title, string why, bool like)> signals)>
            SummarizeSessionAsync(PluginConfiguration cfg, LlmRunner runner, ChatMemorySession session,
                string userId, System.Threading.CancellationToken ct)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var t in session.Messages.TakeLast(SummaryMaxTurns))
            {
                if (t == null || string.IsNullOrWhiteSpace(t.T)) continue;
                var text = t.T.Length <= SummaryMaxTurnChars
                    ? t.T
                    : t.T.Substring(0, SummaryMaxTurnChars) + " …";
                sb.Append(t.R == "u" ? "Usager : " : "Assistant : ");
                sb.AppendLine(text.Replace("\r", "").Replace("\n", " "));
            }
            var transcript = sb.ToString();
            if (string.IsNullOrWhiteSpace(transcript)) return ("", new List<(string, string, bool)>());

            var system = SUMMARY_ROLE + "\n\n" + SUMMARY_RULES;
            var (reply, ok) = await runner.RunSynthesisAsync(cfg, "CHAT-MÉMOIRE", system, transcript, ct)
                .ConfigureAwait(false);
            if (!ok || string.IsNullOrWhiteSpace(reply)) return ("", new List<(string, string, bool)>());

            // Sépare la note de la ligne SIGNALS (dernière occurrence).
            var text2 = reply.Trim();
            var idx = text2.LastIndexOf("SIGNALS:", StringComparison.OrdinalIgnoreCase);
            var note = idx >= 0 ? text2.Substring(0, idx).Trim() : text2;
            if (note.StartsWith("```", StringComparison.Ordinal))
            {
                var firstNl = note.IndexOf('\n');
                if (firstNl > 0) note = note.Substring(firstNl + 1);
                var fence = note.LastIndexOf("```", StringComparison.Ordinal);
                if (fence >= 0) note = note.Substring(0, fence);
                note = note.Trim();
            }

            var signals = new List<(string title, string why, bool like)>();
            if (idx >= 0)
            {
                var rest = text2.Substring(idx + "SIGNALS:".Length).Trim();
                try
                {
                    if (JsonNode.Parse(rest) is JsonArray arr)
                    {
                        foreach (var n in arr)
                        {
                            if (!(n is JsonObject o)) continue;
                            var title = o.TryGetPropertyValue("t", out var tv) ? tv?.ToString() : null;
                            var why = o.TryGetPropertyValue("w", out var wv) ? wv?.ToString() : null;
                            var sign = o.TryGetPropertyValue("s", out var sv) ? sv?.ToString() : null;
                            if (string.IsNullOrWhiteSpace(title)) continue;
                            signals.Add((title.Trim(), why?.Trim() ?? "",
                                !string.Equals(sign ?? "", "-", StringComparison.Ordinal)));
                            if (signals.Count >= 15) break;
                        }
                    }
                }
                catch { /* SIGNALS absent/invalide : signaux ignorés (fail-open) */ }
            }
            return (note, signals);
        }

        /// <summary>
        /// Persiste les signaux de goût extraits d'un résumé comme décisions
        /// kind="chat" (dans decisions.json) — la révision hebdo de la fiche
        /// mémoire les voit déjà (MemoryTask). Dédoublonné contre les signaux
        /// identiques des 7 derniers jours ; gated par DecisionLogEnabled
        /// (données de décision). Best-effort.
        /// </summary>
        private void AppendChatSignals(PluginConfiguration cfg, string userId,
            List<(string title, string why, bool like)> signals)
        {
            if (cfg == null || !cfg.DecisionLogEnabled || signals == null || signals.Count == 0) return;
            try
            {
                var (card, _) = MemoryCard.Load();
                var existing = DecisionStore.ParseAllDecisions()
                    .Where(d => d.Kind == "chat" && d.Date >= DateTimeOffset.UtcNow.AddDays(-7))
                    .Select(d => LlmRunner.NormTitle(d.Title))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var entries = new List<DecisionEntry>();
                foreach (var (title, why, like) in signals)
                {
                    var key = LlmRunner.NormTitle(title);
                    if (key.Length == 0 || !existing.Add(key)) continue;
                    entries.Add(new DecisionEntry
                    {
                        RunId = "",
                        Kind = "chat",
                        User = userId,
                        Date = DateTimeOffset.UtcNow,
                        Title = title,
                        ItemId = "",
                        ProgramId = "",
                        Source = "chat",
                        Reason = (like ? "goût affirmé" : "goût rejeté")
                            + (string.IsNullOrWhiteSpace(why) ? "" : " : " + why),
                        Priority = "",
                        Mv = card.Version
                    });
                }
                if (entries.Count > 0)
                {
                    DecisionStore.AppendDecisions(cfg, entries, Logger);
                    Logger.Info("[LLM_AI] [CHAT] {0} signal(s) de goût journalisé(s) (decisions.json).",
                        entries.Count);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug("[LLM_AI] [CHAT] Signaux de goût non journalisés : {0}", ex.Message);
            }
        }

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
            KickSummarizeStale(cfg, userId);

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
                lock (_summarizeLock) _summarizing.Remove(session);
            }
            ChatMemoryStore.Forget(cfg, userId, session, Logger);
            Logger.Info("[LLM_AI] [CHAT] Mémoire de conversation oubliée ({0}).",
                session.Length > 0 ? session : "toutes les sessions");
            return new ChatMemoryGetResponse { Enabled = true };
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