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
    //  Fiche mémoire réflexive (mémoire réflexive, Phase C) : le LLM gère et
    //  met à jour lui-même sa fiche de stratégie persistante (~250 mots,
    //  Markdown), réinjetée dans ses prochains runs (recommandations + chat).
    //
    //  Une fois par semaine, MemoryTask soumet au LLM la fiche actuelle face
    //  aux événements bruts de la semaine (décisions journalisées ×
    //  télémétrie de lecture × pools de candidats × snapshot EPG) ; il en
    //  déduit ses réussites, ses échecs et réécrit sa fiche. Le C# ne
    //  valide que les garde-fous (plafond de mots, historisation immuable,
    //  fail-open) — le contenu est 100 % écrit par le LLM.
    //
    //  Garde-fous anti-dérive :
    //    - historisation immuable des 4 versions précédentes (un LLM qui
    //      réécrit sa mémoire chaque semaine peut s'auto-convaincre en
    //      boucle ; l'historique est le contrepoids, consultable/éditable
    //      par l'admin) ;
    //    - plafond de taille (le modèle local ne peut pas gonfler sa fiche
    //      au fil des semaines) ;
    //    - fail-open : un échec LLM conserve la fiche précédente.
    // ---------------------------------------------------------------------

    /// <summary>Une version de la fiche mémoire (courante ou historique).</summary>
    internal sealed class MemoryCardData
    {
        public int Version;
        public DateTimeOffset Updated;
        public string Text = "";
    }

    /// <summary>
    /// Store fichier <c>memory_card.json</c> (répertoire de configuration du
    /// plugin) + bloc d'injection dans les prompts. Statique.
    /// </summary>
    internal static class MemoryCard
    {
        /// <summary>Plafond d'une fiche (caractères) — ≈ 300 mots ; borne le
        /// contexte injecté (modèle local).</summary>
        internal const int MaxChars = 2200;

        /// <summary>Versions précédentes conservées (immuables).</summary>
        internal const int HistoryMax = 4;

        // -----------------------------------------------------------------
        //  Store
        // -----------------------------------------------------------------

        /// <summary>Charge (fiche courante, historique). Tolérant.</summary>
        internal static (MemoryCardData current, List<MemoryCardData> history) Load()
        {
            var cur = new MemoryCardData { Version = 0 };
            var hist = new List<MemoryCardData>();
            try
            {
                var arr = JsonNode.Parse(ReadAll()) as JsonObject;
                if (arr != null)
                {
                    if (arr.TryGetPropertyValue("v", out var v) && v is JsonValue jv
                        && jv.TryGetValue<string>(out var vs) && int.TryParse(vs, out var n))
                        cur.Version = n;
                    if (arr.TryGetPropertyValue("up", out var up) && up is JsonValue ju
                        && ju.TryGetValue<string>(out var us)
                        && DateTimeOffset.TryParse(us, CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal, out var u))
                        cur.Updated = u.ToUniversalTime();
                    cur.Text = (arr.TryGetPropertyValue("text", out var tx) && tx is JsonValue jt
                        && jt.TryGetValue<string>(out var ts)) ? ts ?? "" : "";
                    if (arr.TryGetPropertyValue("h", out var hv) && hv is JsonArray ha)
                    {
                        foreach (var hn in ha)
                        {
                            if (!(hn is JsonObject ho)) continue;
                            var h = new MemoryCardData
                            {
                                Version = (OStr(ho, "v") is var hvs && int.TryParse(hvs, out var hn2)) ? hn2 : 0,
                                Text = OStr(ho, "text") ?? ""
                            };
                            if (OStr(ho, "up") is var hup && DateTimeOffset.TryParse(hup,
                                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var hudt))
                                h.Updated = hudt.ToUniversalTime();
                            if (!string.IsNullOrWhiteSpace(h.Text)) hist.Add(h);
                        }
                    }
                }
            }
            catch { /* corrompu : fiche vide (fail-open) */ }
            return (cur, hist);
        }

        /// <summary>Persiste une nouvelle version (historise la précédente).
        /// Tronque au plafond sur une frontière de ligne. Best-effort.</summary>
        internal static void Save(MemoryCardData current, List<MemoryCardData> history, ILogger logger)
        {
            try
            {
                current.Text = Truncate(current.Text ?? "");
                var obj = new JsonObject
                {
                    ["v"] = current.Version.ToString(CultureInfo.InvariantCulture),
                    ["up"] = current.Updated.ToString("o", CultureInfo.InvariantCulture),
                    ["text"] = current.Text
                };
                var ha = new JsonArray();
                foreach (var h in (history ?? new List<MemoryCardData>()).Take(HistoryMax))
                {
                    ha.Add(new JsonObject
                    {
                        ["v"] = h.Version.ToString(CultureInfo.InvariantCulture),
                        ["up"] = h.Updated.ToString("o", CultureInfo.InvariantCulture),
                        ["text"] = Truncate(h.Text ?? "")
                    });
                }
                obj["h"] = ha;
                WriteAll(obj.ToJsonString(), logger);
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Fiche mémoire : échec de persistance : {0}", ex.Message);
            }
        }

        private static string Truncate(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var t = text.Trim();
            if (t.Length <= MaxChars) return t;
            var cut = t.Substring(0, MaxChars);
            var nl = cut.LastIndexOf('\n');
            if (nl > MaxChars / 2) cut = cut.Substring(0, nl);
            return cut.TrimEnd() + " …";
        }

        // -----------------------------------------------------------------
        //  Injection dans les prompts
        // -----------------------------------------------------------------

        /// <summary>
        /// Bloc « MÉMOIRE DE L'ASSISTANT » réinjecté dans les prompts (runs
        /// Tonight, enregistrement, chat). Vide (fail-open) si le flag est
        /// off ou aucune fiche — l'appelant retombe alors sur les directives
        /// de la boucle de rétroaction classique.
        /// </summary>
        internal static string BuildInjectionBlock(PluginConfiguration cfg)
        {
            if (!(cfg?.MemoryCardEnabled ?? false)) return "";
            var (cur, _) = Load();
            if (cur.Version == 0 || string.IsNullOrWhiteSpace(cur.Text)) return "";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("### MÉMOIRE DE L'ASSISTANT (auto-évaluation hebdomadaire)");
            sb.AppendLine("Ta fiche mémoire, mise à jour par toi-même le "
                + (cur.Updated == default ? "?" : cur.Updated.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                + " (v" + cur.Version + "). Tendance générale à respecter, SANS exclure la diversité ni contrevenir aux autres consignes :");
            foreach (var line in cur.Text.Replace("\r", "").Split('\n'))
                if (!string.IsNullOrWhiteSpace(line)) sb.AppendLine(line.Trim());
            sb.AppendLine();
            return sb.ToString();
        }

        // -----------------------------------------------------------------
        //  IO
        // -----------------------------------------------------------------

        private static string CardPath
        {
            get
            {
                try
                {
                    var dir = Plugin.Paths?.PluginConfigurationsPath;
                    if (string.IsNullOrEmpty(dir)) return null;
                    return Path.Combine(dir, "memory_card.json");
                }
                catch { return null; }
            }
        }

        private static string ReadAll()
        {
            var path = CardPath;
            if (path == null || !File.Exists(path)) return null;
            try { return File.ReadAllText(path); }
            catch { return null; }
        }

        private static void WriteAll(string json, ILogger logger)
        {
            var path = CardPath;
            if (path == null) return;
            try { File.WriteAllText(path, json); }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Fiche mémoire : écriture échouée : {0}", ex.Message);
            }
        }

        private static string OStr(JsonObject o, string key)
        {
            if (o.TryGetPropertyValue(key, out var v) && v is JsonValue jv
                && jv.TryGetValue<string>(out var s))
                return s;
            return null;
        }
    }
}