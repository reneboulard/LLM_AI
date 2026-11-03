using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using MediaBrowser.Model.Logging;

namespace LLM_AI
{
    // ---------------------------------------------------------------------
    //  LibReviewReportStore : persistance du DERNIER rapport d'évaluation
    //  « Gestion bibliothèque » (v2.10.0). Même convention que
    //  AuditReportStore : un unique fichier libreview_report.json écrit à
    //  chaque run RÉUSSI dans Plugin.Paths.PluginConfigurationsPath,
    //  best-effort fail-open (un échec IO n'interrompt jamais le run).
    //
    //  Le rapport brut ne porte QUE id + section + reason (contrat LLM) + méta :
    //  l'enrichissement en DTO affichable (titre, type, taille, poster,
    //  nombre d'épisodes…) est refait à CHAQUE lecture (LibReviewApiService)
    //  en re-résolvant les ids — ainsi un item effacé entre-temps (via Emby,
    //  la routine matinale, ou le bouton « Effacer ») ne réapparaît JAMAIS,
    //  et les ids « Conserver » (LibReviewKeptIds) sont exclus aussi.
    // ---------------------------------------------------------------------

    /// <summary>Une proposition brute persistée (contrat LLM + méta).</summary>
    internal sealed class LibReviewEntry
    {
        /// <summary>InternalId de l'item (chaîne) — devise canonique du plugin.</summary>
        public string Id;
        /// <summary>Kind du run : « movie » ou « series ».</summary>
        public string Kind;
        /// <summary>Justificatif du LLM (affiché tel quel sur la carte).</summary>
        public string Reason;
        /// <summary>
        /// Verdict v2.12.0 : « delete » | « can_delete » | « keep ». Null/absent
        /// = rapport d'une version &lt; 2.12.0 (proposition de suppression)
        /// → traité « delete » en lecture (rétro-compat du stockage).
        /// </summary>
        public string Section;
    }

    /// <summary>Le dernier rapport d'évaluation persisté (un seul enregistrement).</summary>
    internal sealed class LastLibReviewReport
    {
        /// <summary>Date/heure (UTC ISO) de production du rapport.</summary>
        public DateTimeOffset GeneratedAt;
        /// <summary>Consigne (brief) qui a orienté le run — chaîne libre.</summary>
        public string Brief;
        /// <summary>Nombre de candidats envoyés au LLM par run (films + séries).</summary>
        public int Candidates;
        /// <summary>Propositions brutes (id + reason + kind).</summary>
        public List<LibReviewEntry> Items = new List<LibReviewEntry>();
    }

    /// <summary>IO best-effort du store du dernier rapport « Gestion bibliothèque ».</summary>
    internal static class LibReviewReportStore
    {
        private static string PathOf()
        {
            try
            {
                var dir = Plugin.Paths?.PluginConfigurationsPath;
                if (string.IsNullOrEmpty(dir)) return null;
                return Path.Combine(dir, "libreview_report.json");
            }
            catch { return null; }
        }

        /// <summary>Charge le dernier rapport, ou null (absent/corrompu). Tolérant.</summary>
        public static LastLibReviewReport Load()
        {
            var path = PathOf();
            if (path == null || !File.Exists(path)) return null;
            string json;
            try { json = File.ReadAllText(path); }
            catch { return null; }
            try
            {
                if (!(JsonNode.Parse(json) is JsonObject root)) return null;
                var r = new LastLibReviewReport
                {
                    Brief = OStr(root, "brief"),
                    Candidates = OInt(root, "candidates")
                };
                if (DateTimeOffset.TryParse(OStr(root, "generated_at") ?? "", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var d))
                    r.GeneratedAt = d.ToUniversalTime();
                if (root.TryGetPropertyValue("items", out var itemsNode) && itemsNode is JsonArray arr)
                {
                    foreach (var n in arr)
                    {
                        if (n is not JsonObject o) continue;
                        var id = OStr(o, "id");
                        if (string.IsNullOrWhiteSpace(id)) continue;
                        // Rétro-compat v2.12.0 : sans « section » = rapport
                        // ancien format (propositions de suppression) → « delete ».
                        string section = OStr(o, "section");
                        r.Items.Add(new LibReviewEntry
                        {
                            Id = id.Trim(),
                            Kind = OStr(o, "kind") ?? "",
                            Reason = OStr(o, "reason") ?? "",
                            Section = (section == "delete" || section == "can_delete" || section == "keep")
                                ? section : "delete"
                        });
                    }
                }
                return r;
            }
            catch { return null; }
        }

        /// <summary>
        /// Écrit le dernier rapport (best-effort : n'élève jamais — un échec
        /// de disque ne doit pas faire échouer un run par ailleurs réussi).
        /// </summary>
        public static void Save(LastLibReviewReport report, ILogger logger)
        {
            if (report == null) return;
            var path = PathOf();
            if (path == null) return;
            try
            {
                var arr = new JsonArray();
                foreach (var it in report.Items)
                {
                    if (it == null || string.IsNullOrWhiteSpace(it.Id)) continue;
                    arr.Add(new JsonObject
                    {
                        ["id"] = it.Id,
                        ["kind"] = it.Kind ?? "",
                        ["reason"] = it.Reason ?? "",
                        ["section"] = string.IsNullOrEmpty(it.Section) ? "delete" : it.Section
                    });
                }
                var root = new JsonObject
                {
                    ["generated_at"] = report.GeneratedAt.ToString("o", CultureInfo.InvariantCulture),
                    ["brief"] = report.Brief ?? "",
                    ["candidates"] = report.Candidates,
                    ["items"] = arr
                };
                File.WriteAllText(path, root.ToJsonString());
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Persistance du rapport « Gestion bibliothèque » : écriture échouée : {0}", ex.Message);
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

        private static int OInt(JsonObject o, string key)
        {
            if (o != null && o.TryGetPropertyValue(key, out var v) && v is JsonValue jv
                && jv.TryGetValue<int>(out var n))
                return n;
            return 0;
        }
    }
}
