using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace LLM_AI
{
    /// <summary>
    /// Condensation paresseuse des sessions de mémoire de conversation
    /// (extraite de <see cref="ChatApiService"/> v1.13.21 pour être partagée
    /// avec le chat externe — même mécanique, un appelant par surface).
    /// La note de continuité est produite UN appel LLM sans outils, au
    /// moment où la session devient passée (retour de l'usager) — jamais
    /// pendant la conversation (coût par tour). Tâche de fond
    /// (fire-and-forget) : au premier tour ou à l'ouverture de la page,
    /// l'usager n'attend rien ; au clic « Reprendre », le résumé est
    /// généralement prêt (sinon : les derniers tours verbatim suffisent).
    /// Les signaux de goût extraits du résumé partent dans
    /// <c>decisions.json</c> (kind="chat") — la révision hebdo de la fiche
    /// mémoire les voit déjà (MemoryTask).
    /// </summary>
    internal static class ChatSummarizer
    {
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
        /// Retire un session id de l'anti-doublon (oubli de session pendant
        /// qu'une condensation est en cours) — exécuté par le Forget du chat.
        /// </summary>
        internal static void ForgetActive(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return;
            lock (_summarizeLock) _summarizing.Remove(sessionId);
        }

        /// <summary>
        /// Déclenche (tâche de fond, fire-and-forget, best-effort) la
        /// condensation des sessions passées jamais résumées de cet usager.
        /// Les services Emby nécessaires au runner de synthèse sont passés
        /// par l'appelant (endpoint DI) — mêmes instances que sur le path
        /// chat de <see cref="ChatApiService"/>.
        /// </summary>
        internal static void KickSummarizeStale(PluginConfiguration cfg, string userId, ILogger logger,
            IJsonSerializer json, ILibraryManager library, IUserManager users,
            ILiveTvManager liveTv, IServerApplicationHost host)
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

                        var runner = new LlmRunner(logger, json, library, users, liveTv, host);
                        var (note, signals) = await SummarizeSessionAsync(
                            capturedCfg, runner, session, capturedUser,
                            System.Threading.CancellationToken.None).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(note))
                        {
                            ChatMemoryStore.SetSummary(capturedCfg, capturedUser, sid, note, logger);
                            logger.Info("[LLM_AI] [CHAT] Session {0} condensée ({1} caractères).",
                                sid, note.Length);
                            AppendChatSignals(capturedCfg, capturedUser, signals, logger);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Fail-open : pas de résumé, la conversation suivante
                        // continue sans.
                        logger.Debug("[LLM_AI] [CHAT] Condensation de session échouée : {0}", ex.Message);
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
        private static async System.Threading.Tasks.Task<(string note, List<(string title, string why, bool like)> signals)>
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
        private static void AppendChatSignals(PluginConfiguration cfg, string userId,
            List<(string title, string why, bool like)> signals, ILogger logger)
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
                    DecisionStore.AppendDecisions(cfg, entries, logger);
                    logger.Info("[LLM_AI] [CHAT] {0} signal(s) de goût journalisé(s) (decisions.json).",
                        entries.Count);
                }
            }
            catch (Exception ex)
            {
                logger.Debug("[LLM_AI] [CHAT] Signaux de goût non journalisés : {0}", ex.Message);
            }
        }
    }
}