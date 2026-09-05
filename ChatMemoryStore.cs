using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using MediaBrowser.Model.Logging;

namespace LLM_AI
{
    // ---------------------------------------------------------------------
    //  Mémoire de conversation du chat (stockage + injection).
    //
    //  Le chat est stateless (la page re-poste son historique) — un
    //  rechargement de page perdait tout. Ce store persiste PAR USAGER les
    //  sessions dans chat_memory.json (répertoire de configuration du
    //  plugin, pattern DecisionStore) :
    //    - une entrée PAR SESSION : les tours verbatim (bornés) pendant la
    //      session ; APRÈS résumé, seuls les derniers tours sont gardés ;
    //    - le RÉSUMÉ de session (note de continuité ≤ ~1500 caractères,
    //      produite paresseusement par un appel LLM sans outils au moment
    //      où la session devient passée — voir ChatApiService) ;
    //    - les SIGNAUX DE GOÛT extraits du résumé ne sont PAS stockés ici :
    //      ils partent dans decisions.json (kind="chat", ChatApiService) —
    //      la révision hebdo de la fiche mémoire (MemoryTask) les voit déjà.
    //
    //  Injection (BuildInjectionBlock) : résumé de la session précédente +
    //  derniers échanges verbatim, accolés au workflow de chat. La fiche
    //  mémoire (MemoryCard, hebdo, stratégie) reste au-dessus : ce bloc ne
    //  porte que le contexte conversationnel, il est jetable (le résumé
    //  suivant le remplace — pas d'historisation).
    //
    //  Opt-in ChatMemoryEnabled ; fail-open (store absent/corrompu → chat
    //  sans mémoire, inchangé) ; best-effort ; rétention 30 jours.
    // ---------------------------------------------------------------------

    /// <summary>Un tour de conversation (r = "u" usager / "a" assistant).</summary>
    internal sealed class ChatMemoryTurn
    {
        public string R;
        public string T;
    }

    /// <summary>Une session de conversation (une « conversation » du chat).</summary>
    internal sealed class ChatMemorySession
    {
        public string Id;
        public string User;
        /// <summary>Date du dernier tour (UTC).</summary>
        public DateTimeOffset Date;
        /// <summary>Nombre total de tours de la session.</summary>
        public int Turns;
        /// <summary>Note de continuité (vide tant que non résumée).</summary>
        public string Summary = "";
        public List<ChatMemoryTurn> Messages = new List<ChatMemoryTurn>();
    }

    /// <summary>Store fichier <c>chat_memory.json</c>. Statique.</summary>
    internal static class ChatMemoryStore
    {
        /// <summary>Sessions conservées par usager (les plus récentes).</summary>
        internal const int MaxSessions = 5;

        /// <summary>Plafond de tours stockés d'une session non résumée
        /// (le résumé n'en aura qu'une fenêtre — le reste est perdu, accepté).</summary>
        internal const int MaxTurnsPerSession = 60;

        /// <summary>Plafond d'un tour stocké (les réponses d'outils/Markdown
        /// peuvent être longues — on garde le début, suffisant au résumé).</summary>
        internal const int MaxTurnChars = 2000;

        /// <summary>Plafond d'un résumé (caractères) — borne le bloc injecté.</summary>
        internal const int MaxSummaryChars = 1500;

        /// <summary>Après résumé, tours verbatim conservés (vérité terrain de
        /// la continuité immédiate ; le résumé couvre le reste).</summary>
        internal const int KeepTurnsAfterSummary = 6;

        /// <summary>Rétention (jours) — une conversation ancienne n'a plus
        /// de valeur de continuité.</summary>
        internal const int RetentionDays = 30;

        private static readonly object _lock = new object();

        // -----------------------------------------------------------------
        //  Lectures
        // -----------------------------------------------------------------

        /// <summary>Sessions d'un usager, la plus récente en premier.</summary>
        internal static List<ChatMemorySession> ListSessions(PluginConfiguration cfg, string userId)
        {
            try
            {
                lock (_lock)
                {
                    return LoadAll()
                        .Where(s => s != null
                                    && string.Equals(s.User ?? "", userId ?? "", StringComparison.Ordinal))
                        .OrderByDescending(s => s.Date)
                        .ToList();
                }
            }
            catch { return new List<ChatMemorySession>(); }
        }

        /// <summary>La session la plus récente d'un usager, ou null.</summary>
        internal static ChatMemorySession GetLatest(PluginConfiguration cfg, string userId)
            => ListSessions(cfg, userId).FirstOrDefault();

        /// <summary>Cherche une session par identifiant (cet usager).</summary>
        internal static ChatMemorySession Find(PluginConfiguration cfg, string userId, string sessionId)
            => ListSessions(cfg, userId).FirstOrDefault(s => string.Equals(s.Id ?? "", sessionId ?? "", StringComparison.Ordinal));

        // -----------------------------------------------------------------
        //  Écritures
        // -----------------------------------------------------------------

        /// <summary>
        /// Enregistre un tour : ajoute à la session portée par
        /// <paramref name="sessionId"/> (l'assure : inconnue → nouvelle
        /// session d'identité <paramref name="sessionId"/>) puis persiste.
        /// Retourne l'identifiant de session utilisé. Best-effort : "" en
        /// cas d'échec (le chat continue sans mémoire).
        /// </summary>
        internal static string RecordTurn(PluginConfiguration cfg, string userId, string sessionId,
            bool fromUser, string text, ILogger logger)
        {
            if (cfg == null || !cfg.ChatMemoryEnabled) return sessionId ?? "";
            var t = (text ?? "").Trim();
            if (t.Length == 0) return sessionId ?? "";
            try
            {
                lock (_lock)
                {
                    var all = LoadAll();
                    var sid = (sessionId ?? "").Trim();
                    if (sid.Length == 0)
                        sid = "c" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                            + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                    var s = all.FirstOrDefault(x => string.Equals(x.Id ?? "", sid, StringComparison.Ordinal)
                                                    && string.Equals(x.User ?? "", userId ?? "", StringComparison.Ordinal));
                    if (s == null)
                    {
                        s = new ChatMemorySession
                        {
                            Id = sid,
                            User = userId ?? "",
                            Date = DateTimeOffset.UtcNow
                        };
                        all.Add(s);
                    }
                    s.Messages.Add(new ChatMemoryTurn
                    {
                        R = fromUser ? "u" : "a",
                        T = t.Length <= MaxTurnChars ? t : t.Substring(0, MaxTurnChars) + " …"
                    });
                    s.Turns++;
                    s.Date = DateTimeOffset.UtcNow;
                    Save(all, logger);
                    return sid;
                }
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Mémoire de chat : tour non journalisé : {0}", ex.Message);
                return sessionId ?? "";
            }
        }

        /// <summary>
        /// Pose le résumé d'une session (et comprime ses tours verbatim aux
        /// derniers <see cref="KeepTurnsAfterSummary"/>). Best-effort.
        /// </summary>
        internal static void SetSummary(PluginConfiguration cfg, string userId, string sessionId,
            string summary, ILogger logger)
        {
            if (cfg == null || string.IsNullOrWhiteSpace(sessionId)) return;
            try
            {
                lock (_lock)
                {
                    var all = LoadAll();
                    var s = all.FirstOrDefault(x => string.Equals(x.Id ?? "", sessionId, StringComparison.Ordinal)
                                                    && string.Equals(x.User ?? "", userId ?? "", StringComparison.Ordinal));
                    if (s == null) return;
                    s.Summary = ClampSummary(summary);
                    if (s.Messages.Count > KeepTurnsAfterSummary)
                        s.Messages = s.Messages.Skip(s.Messages.Count - KeepTurnsAfterSummary).ToList();
                    Save(all, logger);
                }
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Mémoire de chat : résumé non persisté : {0}", ex.Message);
            }
        }

        /// <summary>Oublie TOUTES les sessions d'un usager, ou une seule
        /// (sessionId non vide). Best-effort.</summary>
        internal static void Forget(PluginConfiguration cfg, string userId, string sessionId, ILogger logger)
        {
            if (cfg == null) return;
            try
            {
                lock (_lock)
                {
                    var all = LoadAll();
                    // Garde les sessions des AUTRES usagers ; pour l'usager
                    // visé : tout (sessionId vide) ou tout SAUF cette session.
                    var kept = all.Where(s =>
                    {
                        if (s == null) return false;
                        if (!string.Equals(s.User ?? "", userId ?? "", StringComparison.Ordinal)) return true;
                        return !string.IsNullOrWhiteSpace(sessionId)
                            && !string.Equals(s.Id ?? "", sessionId, StringComparison.Ordinal);
                    }).ToList();
                    Save(kept, logger);
                }
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Mémoire de chat : oubli échoué : {0}", ex.Message);
            }
        }

        // -----------------------------------------------------------------
        //  Injection dans le prompt de chat
        // -----------------------------------------------------------------

        /// <summary>
        /// Bloc « MÉMOIRE DE CONVERSATION » : le résumé de la session
        /// précédente + ses derniers échanges verbatim. La session courante
        /// (<paramref name="excludeSessionId"/>) est exclue — son contenu
        /// arrive verbatim dans l'historique rejoué par la page. Vide
        /// (fail-open) si le flag est off ou aucune session antérieure.
        /// </summary>
        internal static string BuildInjectionBlock(PluginConfiguration cfg, string userId, string excludeSessionId)
        {
            if (cfg == null || !cfg.ChatMemoryEnabled) return "";
            var prev = ListSessions(cfg, userId)
                .FirstOrDefault(s => !string.Equals(s.Id ?? "", excludeSessionId ?? "", StringComparison.Ordinal)
                                     && s.Turns > 0
                                     && (!string.IsNullOrWhiteSpace(s.Summary) || s.Messages.Count > 0));
            if (prev == null) return "";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("### MÉMOIRE DE CONVERSATION (session précédente)");
            sb.AppendLine("Contexte de ta conversation précédente avec cet usager ("
                + prev.Date.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                + ", " + prev.Turns.ToString(CultureInfo.InvariantCulture) + " échanges) — pour la continuité, PAS une directive :");
            if (!string.IsNullOrWhiteSpace(prev.Summary))
                sb.AppendLine(prev.Summary.Trim());
            // Derniers échanges verbatim (même résumée — la fin du fil reste utile).
            var tail = prev.Messages.TakeLast(KeepTurnsAfterSummary).ToList();
            if (tail.Count > 0)
            {
                sb.AppendLine("[Derniers échanges]");
                foreach (var t in tail)
                {
                    if (t == null || string.IsNullOrWhiteSpace(t.T)) continue;
                    sb.Append(t.R == "u" ? "Usager : " : "Assistant : ");
                    sb.AppendLine(t.T.Trim().Replace("\r", "").Replace("\n", " "));
                }
            }
            sb.AppendLine();
            return sb.ToString();
        }

        // -----------------------------------------------------------------
        //  IO
        // -----------------------------------------------------------------

        private static string PathOf()
        {
            try
            {
                var dir = Plugin.Paths?.PluginConfigurationsPath;
                if (string.IsNullOrEmpty(dir)) return null;
                return Path.Combine(dir, "chat_memory.json");
            }
            catch { return null; }
        }

        /// <summary>Charge toutes les sessions (tous usagers). Tolérant.</summary>
        private static List<ChatMemorySession> LoadAll()
        {
            var list = new List<ChatMemorySession>();
            var path = PathOf();
            if (path == null || !File.Exists(path)) return list;
            string json;
            try { json = File.ReadAllText(path); }
            catch { return list; }
            try
            {
                if (!(JsonNode.Parse(json) is JsonObject root)) return list;
                if (root.TryGetPropertyValue("sessions", out var sv) && sv is JsonArray sa)
                {
                    foreach (var sn in sa)
                        list.Add(ParseSession(sn as JsonObject));
                    list.RemoveAll(x => x == null);
                }
            }
            catch { /* corrompu : vide */ }
            return list;
        }

        private static ChatMemorySession ParseSession(JsonObject o)
        {
            if (o == null) return null;
            var s = new ChatMemorySession
            {
                Id = OStr(o, "id") ?? "",
                User = OStr(o, "u") ?? "",
                Summary = OStr(o, "s") ?? ""
            };
            if (int.TryParse(OStr(o, "n") ?? "", out var n)) s.Turns = Math.Max(0, n);
            if (DateTimeOffset.TryParse(OStr(o, "d") ?? "", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var d))
                s.Date = d.ToUniversalTime();
            if (o.TryGetPropertyValue("m", out var mv) && mv is JsonArray ma)
            {
                foreach (var tn in ma)
                {
                    if (!(tn is JsonObject to)) continue;
                    var turn = new ChatMemoryTurn
                    {
                        R = OStr(to, "r") ?? "u",
                        T = OStr(to, "t") ?? ""
                    };
                    if (!string.IsNullOrWhiteSpace(turn.T)) s.Messages.Add(turn);
                }
            }
            if (string.IsNullOrWhiteSpace(s.Id)) return null;
            return s;
        }

        private static void Save(List<ChatMemorySession> sessions, ILogger logger)
        {
            var path = PathOf();
            if (path == null) return;
            try
            {
                var cutoff = DateTimeOffset.UtcNow.AddDays(-RetentionDays);
                var kept = (sessions ?? new List<ChatMemorySession>())
                    .Where(s => s != null && !string.IsNullOrWhiteSpace(s.Id) && s.Date >= cutoff)
                    .GroupBy(s => s.User ?? "", StringComparer.Ordinal)
                    .SelectMany(g => g.OrderByDescending(s => s.Date).Take(MaxSessions))
                    .OrderBy(s => s.Date)
                    .ToList();
                var arr = new JsonArray();
                foreach (var s in kept)
                {
                    var turns = new JsonArray();
                    foreach (var t in (s.Messages ?? new List<ChatMemoryTurn>()).Take(MaxTurnsPerSession))
                    {
                        if (t == null || string.IsNullOrWhiteSpace(t.T)) continue;
                        turns.Add(new JsonObject { ["r"] = t.R ?? "u", ["t"] = t.T ?? "" });
                    }
                    arr.Add(new JsonObject
                    {
                        ["id"] = s.Id,
                        ["u"] = s.User ?? "",
                        ["d"] = s.Date.ToString("o", CultureInfo.InvariantCulture),
                        ["n"] = Math.Max(0, s.Turns),
                        ["s"] = ClampSummary(s.Summary),
                        ["m"] = turns
                    });
                }
                var root = new JsonObject { ["sessions"] = arr };
                File.WriteAllText(path, root.ToJsonString());
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Mémoire de chat : écriture échouée : {0}", ex.Message);
            }
        }

        private static string ClampSummary(string summary)
        {
            if (string.IsNullOrWhiteSpace(summary)) return "";
            var t = summary.Trim();
            return t.Length <= MaxSummaryChars ? t : t.Substring(0, MaxSummaryChars) + " …";
        }

        private static string OStr(JsonObject o, string key)
        {
            if (o == null) return null;
            if (o.TryGetPropertyValue(key, out var v) && v is JsonValue jv
                && jv.TryGetValue<string>(out var s))
                return s;
            return null;
        }
    }
}