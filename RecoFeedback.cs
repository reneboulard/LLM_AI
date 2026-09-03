using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using MediaBrowser.Model.Logging;

namespace LLM_AI
{
    // ---------------------------------------------------------------------
    //  Boucle de rétroaction des recommandations (feedback loop)
    //
    //  Chaque reco émise (run « À regarder ce soir », tâche planifiée
    //  d'enregistrement) et chaque rejet explicite (bouton « Oublier »)
    //  est journalisé dans RecoLog. Une fois par semaine, RecoAnalysisTask
    //  rapproche ce journal des visionnages réels de l'usager (hits/misses/
    //  rejets/vu-sans-reco) et fait produire au LLM une DIRECTIVE concise
    //  persistée dans PromptDirectives — réinjectée dans les prompts des
    //  runs suivants. Tout est opt-in (RecoFeedbackEnabled) et fail-open :
    //  sans directive, les prompts sont inchangés.
    // ---------------------------------------------------------------------

    /// <summary>Une entrée du journal des recommandations (RecoLog).</summary>
    internal sealed class RecoLogEntry
    {
        /// <summary>Id de l'usager concerné ; "" = reco globale de la tâche
        /// planifiée (record bucket, valable pour tout le foyer).</summary>
        public string User;

        /// <summary>"tonight" (run « ce soir »), "record" (tâche planifiée),
        /// "drop" (bouton « Oublier »).</summary>
        public string Kind;

        public string Title;

        /// <summary>source="live|recording|library" (tonight) ou
        /// kind="series|movie" (record) ; "" pour un drop.</summary>
        public string Source;

        /// <summary>Id d'item (InternalId) ou ProgramId EPG ; peut être vide.</summary>
        public string Id;

        /// <summary>Moment de la reco / du rejet (UTC).</summary>
        public DateTimeOffset Date;
    }

    /// <summary>Directive de recommandation persistée par usager.</summary>
    internal sealed class RecoDirective
    {
        public string User;
        /// <summary>Nom d'affichage de l'usager (repère pour l'admin).</summary>
        public string Name;
        /// <summary>Date de génération (yyyy-MM-dd, information).</summary>
        public string Date;
        /// <summary>Texte de la directive (≤ <see cref="RecoFeedback.DirectiveMaxChars"/>).</summary>
        public string Text;
    }

    /// <summary>
    /// Helpers partagés de la boucle de rétroaction : parse/persist du
    /// journal (<see cref="PluginConfiguration.RecoLog"/>) et des directives
    /// (<see cref="PluginConfiguration.PromptDirectives"/>), et construction
    /// des blocs de prompt réinjectés dans les runs. Les appelsants passent
    /// la config courante — la persistance passe par
    /// <c>Plugin.Instance.SaveConfiguration()</c> (best-effort).
    /// </summary>
    internal static class RecoFeedback
    {
        /// <summary>Fenêtre de rétention du journal (jours).</summary>
        internal const int LogRetentionDays = 30;

        /// <summary>Plafond du journal (entrées les plus récentes gardées).</summary>
        internal const int LogMaxEntries = 500;

        /// <summary>Taille max d'une directive (caractères) — borne le
        /// contexte injecté dans les prompts (modèle local).</summary>
        internal const int DirectiveMaxChars = 1200;

        // -----------------------------------------------------------------
        //  Journal (RecoLog)
        // -----------------------------------------------------------------

        /// <summary>Parse le journal (tolérant : JSON invalide/vide → vide).</summary>
        internal static List<RecoLogEntry> ParseLog(PluginConfiguration cfg)
        {
            var list = new List<RecoLogEntry>();
            var raw = cfg?.RecoLog;
            if (string.IsNullOrWhiteSpace(raw)) return list;
            try
            {
                if (JsonNode.Parse(raw) is JsonArray arr)
                {
                    foreach (var n in arr)
                    {
                        if (!(n is JsonObject o)) continue;
                        var e = new RecoLogEntry
                        {
                            User = ObjStr(o, "u") ?? "",
                            Kind = ObjStr(o, "k") ?? "",
                            Title = ObjStr(o, "t") ?? "",
                            Source = ObjStr(o, "s") ?? "",
                            Id = ObjStr(o, "i") ?? ""
                        };
                        if (string.IsNullOrWhiteSpace(e.Title)) continue;
                        if (DateTimeOffset.TryParse(ObjStr(o, "d"),
                                CultureInfo.InvariantCulture,
                                DateTimeStyles.AssumeUniversal, out var d))
                            e.Date = d.ToUniversalTime();
                        list.Add(e);
                    }
                }
            }
            catch { /* JSON invalide : repart d'un journal vide */ }
            return list;
        }

        /// <summary>
        /// Ajoute des entrées au journal, puis persiste (prune >
        /// <see cref="LogRetentionDays"/> jours, plafond
        /// <see cref="LogMaxEntries"/> entrées les plus récentes). Best-effort.
        /// </summary>
        internal static void AppendLog(PluginConfiguration cfg,
            IEnumerable<RecoLogEntry> entries, ILogger logger)
        {
            if (cfg == null) return;
            var log = ParseLog(cfg);
            log.AddRange(entries.Where(e => e != null && !string.IsNullOrWhiteSpace(e.Title)));
            SaveLog(cfg, log, logger);
        }

        /// <summary>Persiste le journal complet (prune + plafond + tri
        /// chronologique). Best-effort : n'échoue jamais sur erreur d'écriture.</summary>
        internal static void SaveLog(PluginConfiguration cfg,
            List<RecoLogEntry> log, ILogger logger)
        {
            if (cfg == null) return;
            try
            {
                var cutoff = DateTimeOffset.UtcNow.AddDays(-LogRetentionDays);
                var kept = (log ?? new List<RecoLogEntry>())
                    .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Title) && e.Date >= cutoff)
                    .OrderBy(e => e.Date)
                    .ToList();
                if (kept.Count > LogMaxEntries)
                    kept = kept.Skip(kept.Count - LogMaxEntries).ToList();
                var arr = new JsonArray();
                foreach (var e in kept)
                {
                    arr.Add(new JsonObject
                    {
                        ["u"] = e.User ?? "",
                        ["k"] = e.Kind ?? "",
                        ["d"] = e.Date.ToString("o", CultureInfo.InvariantCulture),
                        ["t"] = e.Title,
                        ["s"] = e.Source ?? "",
                        ["i"] = e.Id ?? ""
                    });
                }
                cfg.RecoLog = arr.ToJsonString();
                Save(cfg, logger);
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Reco log : échec de persistance : {0}", ex.Message);
            }
        }

        // -----------------------------------------------------------------
        //  Directives (PromptDirectives)
        // -----------------------------------------------------------------

        /// <summary>Parse les directives persistées (tolérant).</summary>
        internal static List<RecoDirective> ParseDirectives(PluginConfiguration cfg)
        {
            var list = new List<RecoDirective>();
            var raw = cfg?.PromptDirectives;
            if (string.IsNullOrWhiteSpace(raw)) return list;
            try
            {
                if (JsonNode.Parse(raw) is JsonArray arr)
                {
                    foreach (var n in arr)
                    {
                        if (!(n is JsonObject o)) continue;
                        var d = new RecoDirective
                        {
                            User = ObjStr(o, "u") ?? "",
                            Name = ObjStr(o, "n") ?? "",
                            Date = ObjStr(o, "d") ?? "",
                            Text = ObjStr(o, "text") ?? ""
                        };
                        if (string.IsNullOrEmpty(d.User) || string.IsNullOrWhiteSpace(d.Text)) continue;
                        list.Add(d);
                    }
                }
            }
            catch { /* JSON invalide : directives vides (fail-open) */ }
            return list;
        }

        /// <summary>Persiste la liste de directives (tri par usager).
        /// Best-effort.</summary>
        internal static void SaveDirectives(PluginConfiguration cfg,
            List<RecoDirective> directives, ILogger logger)
        {
            if (cfg == null) return;
            try
            {
                var arr = new JsonArray();
                foreach (var d in (directives ?? new List<RecoDirective>())
                    .Where(d => d != null && !string.IsNullOrEmpty(d.User) && !string.IsNullOrWhiteSpace(d.Text))
                    .OrderBy(d => d.User, StringComparer.OrdinalIgnoreCase))
                {
                    arr.Add(new JsonObject
                    {
                        ["u"] = d.User,
                        ["n"] = d.Name ?? "",
                        ["d"] = d.Date ?? "",
                        ["text"] = TruncateDirective(d.Text)
                    });
                }
                cfg.PromptDirectives = arr.ToJsonString();
                Save(cfg, logger);
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Directives : échec de persistance : {0}", ex.Message);
            }
        }

        /// <summary>Directive de l'usager, ou null si aucune.</summary>
        internal static RecoDirective DirectiveFor(PluginConfiguration cfg, string userId)
        {
            if (string.IsNullOrEmpty(userId)) return null;
            return ParseDirectives(cfg).FirstOrDefault(d =>
                string.Equals(d.User, userId, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Tronque une directive au plafond, sur une frontière de
        /// ligne si possible (garde un texte propre pour le prompt).</summary>
        internal static string TruncateDirective(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var t = text.Trim();
            if (t.Length <= DirectiveMaxChars) return t;
            var cut = t.Substring(0, DirectiveMaxChars);
            var nl = cut.LastIndexOf('\n');
            if (nl > DirectiveMaxChars / 2) cut = cut.Substring(0, nl);
            return cut.TrimEnd() + " …";
        }

        // -----------------------------------------------------------------
        //  Blocs de prompt réinjectés dans les runs
        // -----------------------------------------------------------------

        /// <summary>
        /// Bloc de prompt « RETOUR SUR LES RECOMMANDATIONS » pour le run
        /// « À regarder ce soir » d'un usager. Vide si pas de directive.
        /// </summary>
        internal static string BuildTonightBlock(PluginConfiguration cfg, string userId)
        {
            var d = DirectiveFor(cfg, userId);
            if (d == null) return "";
            return BuildBlockCore(new[] { d });
        }

        /// <summary>
        /// Bloc de prompt fusionnant les directives de TOUS les usagers pour
        /// la tâche planifiée d'enregistrement (reco globale du foyer, la
        /// directive n'est pas par-usager) — chaque directive est étiquetée
        /// du nom de l'usager. Vide si aucune directive.
        /// </summary>
        internal static string BuildRecordBlock(PluginConfiguration cfg)
        {
            var all = ParseDirectives(cfg);
            if (all.Count == 0) return "";
            return BuildBlockCore(all);
        }

        private static string BuildBlockCore(IEnumerable<RecoDirective> directives)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("### RETOUR SUR LES RECOMMANDATIONS (analyse hebdomadaire)");
            sb.AppendLine("Directives dérivées de l'analyse des recommandations passées vs les visionnages réels. " +
                          "Tendance à respecter, SANS exclure la diversité ni contrevenir aux autres consignes :");
            foreach (var d in directives)
            {
                if (!string.IsNullOrWhiteSpace(d.Name))
                    sb.AppendLine("Directive « usager " + d.Name + " » :");
                else
                    sb.AppendLine("Directive :");
                foreach (var line in (d.Text ?? "").Replace("\r", "").Split('\n'))
                    if (!string.IsNullOrWhiteSpace(line)) sb.AppendLine(line.Trim());
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd() + "\n\n";
        }

        // -----------------------------------------------------------------
        //  Divers
        // -----------------------------------------------------------------

        /// <summary>Sauvegarde best-effort de la config (fichier unique
        /// réécrit en entier — même pattern que les autres écritures
        /// serveur-side du plugin).</summary>
        internal static void Save(PluginConfiguration cfg, ILogger logger)
        {
            try
            {
                Plugin.Instance?.SaveConfiguration();
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Rétroaction : échec de sauvegarde de la config : {0}", ex.Message);
            }
        }

        private static string ObjStr(JsonObject o, string key)
        {
            if (o.TryGetPropertyValue(key, out var v) && v is JsonValue jv
                && jv.TryGetValue<string>(out var s))
                return s;
            return null;
        }
    }
}