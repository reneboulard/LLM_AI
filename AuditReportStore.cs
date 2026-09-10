using System;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using MediaBrowser.Model.Logging;

namespace LLM_AI
{
    // ---------------------------------------------------------------------
    //  AuditReportStore : persistance du DERNIER rapport d'audit (v1.13.10).
    //  Le rapport n'existait que dans le DOM de la page de config — quitter
    //  la page le perdait, et toute relecture exigeait de relancer un audit
    //  (coût LLM inutile). Un unique fichier audit_report.json est écrit à
    //  chaque audit RÉUSSI (un rapport d'erreur type « aucun backend »
    //  n'écrase jamais le dernier vrai rapport) et renvoyé par
    //  l'endpoint pour affichage par défaut dans la page.
    //
    //  Convention ChatMemoryStore : JSON System.Text.Json.Nodes, chemin
    //  Plugin.Paths.PluginConfigurationsPath, best-effort fail-open — un
    //  échec IO n'interrompt JAMAIS l'audit ni la page. Pas d'historique :
    //  un seul enregistrement écrasé à chaque run (une rétention N rapports
    //  serait une évolution distincte).
    // ---------------------------------------------------------------------

    /// <summary>Le dernier rapport d'audit persisté (un seul enregistrement).</summary>
    internal sealed class LastAuditReport
    {
        /// <summary>Date/heure (UTC ISO) de production du rapport.</summary>
        public DateTimeOffset GeneratedAt;
        /// <summary>Mode d'exécution : single (boucle agent) ou deterministic.</summary>
        public string Mode;
        /// <summary>Focus optionnel demandé par l'usager (chaîne libre).</summary>
        public string Focus;
        /// <summary>Rapport Markdown brut (rendu côté config.js).</summary>
        public string Report;
    }

    /// <summary>IO best-effort du store du dernier rapport d'audit.</summary>
    internal static class AuditReportStore
    {
        private static string PathOf()
        {
            try
            {
                var dir = Plugin.Paths?.PluginConfigurationsPath;
                if (string.IsNullOrEmpty(dir)) return null;
                return Path.Combine(dir, "audit_report.json");
            }
            catch { return null; }
        }

        /// <summary>Charge le dernier rapport, ou null (absent/corrompu). Tolérant.</summary>
        public static LastAuditReport Load()
        {
            var path = PathOf();
            if (path == null || !System.IO.File.Exists(path)) return null;
            string json;
            try { json = System.IO.File.ReadAllText(path); }
            catch { return null; }
            try
            {
                if (!(JsonNode.Parse(json) is JsonObject root)) return null;
                var r = new LastAuditReport
                {
                    Mode = OStr(root, "mode"),
                    Focus = OStr(root, "focus"),
                    Report = OStr(root, "report")
                };
                if (DateTimeOffset.TryParse(OStr(root, "generated_at") ?? "", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var d))
                    r.GeneratedAt = d.ToUniversalTime();
                if (string.IsNullOrWhiteSpace(r.Report)) return null;
                return r;
            }
            catch { return null; }
        }

        /// <summary>
        /// Écrit le dernier rapport (best-effort : n'élève jamais — un échec
        /// de disque ne doit pas faire échouer un audit par ailleurs réussi).
        /// </summary>
        public static void Save(LastAuditReport report, ILogger logger)
        {
            if (report == null || string.IsNullOrWhiteSpace(report.Report)) return;
            var path = PathOf();
            if (path == null) return;
            try
            {
                var root = new JsonObject
                {
                    ["generated_at"] = report.GeneratedAt.ToString("o", CultureInfo.InvariantCulture),
                    ["mode"] = report.Mode ?? "",
                    ["focus"] = report.Focus ?? "",
                    ["report"] = report.Report
                };
                System.IO.File.WriteAllText(path, root.ToJsonString());
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Persistance du rapport d'audit : écriture échouée : {0}", ex.Message);
            }
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