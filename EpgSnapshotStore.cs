using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Logging;

namespace LLM_AI
{
    // ---------------------------------------------------------------------
    //  EpgSnapshot (mémoire réflexive, Phase B) : figer les métadonnées EPG
    //  éphémères dès l'émission au LLM ou la programmation d'un timer.
    //
    //  Un programme EPG disparaît de la base Emby une fois diffusé : sans
    //  snapshot, tout ce qu'on en savait (titre, description, genres,
    //  durée de diffusion) est perdu — et l'analyse ne peut plus rattacher
    //  une lecture du direct à son programme (le % du direct = durée lue ÷
    //  durée DE DIFFUSION du programme, ici figée). Les entrées sont écrites
    //  :
    //    1. à l'émission au LLM (captures epg_tonight / epg_series /
    //       epg_movies — mêmes sites que le pool de candidats) ;
    //    2. à la création d'un timer d'enregistrement (AutoProgrammer).
    //
    //  Le croisement avec la bibliothèque (providerIds, itemId de l'item
    //  enregistré) N'EST PAS résolu en temps réel : il l'est de façon
    //  déterministe À L'ANALYSE (Phase C) par ItemIdResolver / providerIds —
    //  les enregistrements complétés deviennent des items bibliothèque que
    //  Emby résout lui-même ; un rapprochement à l'analyse suffit et
    //  n'introduit aucune dépendance au runtime.
    //
    //  Opt-in DecisionLogEnabled (même store opt-in que les décisions) ;
    //  fail-open ; best-effort ; rétention 90 jours.
    // ---------------------------------------------------------------------

    /// <summary>Une entrée du snapshot EPG (un programme figé).</summary>
    internal sealed class EpgSnapshotEntry
    {
        /// <summary>Id du programme EPG (Guid de BaseItemDto, chaîne).</summary>
        public string ProgramId;
        /// <summary>Titre de diffusion (séries : SeriesName, sinon Name).</summary>
        public string Title;
        public string EpisodeTitle;
        /// <summary>Synopsis figé (≤ 300 caractères).</summary>
        public string Overview;
        public string Channel;
        /// <summary>Début de diffusion.</summary>
        public DateTimeOffset Start;
        /// <summary>Durée DE DIFFUSION (minutes) — sert au % du direct.</summary>
        public int? RuntimeMin;
        /// <summary>Genres NORMALISÉS (GenreCleanerMap — nomenclature commune
        /// avec la bibliothèque).</summary>
        public List<string> Genres = new List<string>();
        public int? Year;
        public bool IsSeries;
        public bool IsMovie;
        /// <summary>Un timer d'enregistrement a été créé sur ce programme.</summary>
        public bool Timer;
        public DateTimeOffset Updated;
    }

    /// <summary>
    /// Store fichier <c>epg_snapshot.json</c> (répertoire de configuration du
    /// plugin, pattern <see cref="DecisionStore"/>). Statique : écrit par
    /// <see cref="GetEmbyInfoTool"/> et <see cref="AutoProgrammer"/>, lu par
    /// l'analyse hebdo (Phase C). Les lectures tolèrent un JSON invalide.
    /// </summary>
    internal static class EpgSnapshotStore
    {
        /// <summary>Rétention (jours) — les programmes (et leur contexte de
        /// diffusion) d'une saison doivent rester joignables.</summary>
        internal const int RetentionDays = 90;

        internal const int MaxEntries = 3000;

        private static readonly object _lock = new object();

        // -----------------------------------------------------------------
        //  Écriture
        // -----------------------------------------------------------------

        /// <summary>
        /// Fige (upsert) un programme EPG au moment de son émission au LLM.
        /// Les genres passés doivent déjà être normalisés (GenreCleanerMap —
        /// c'est le vocabulaire commun). No-op si le flag est off. Best-effort.
        /// </summary>
        internal static void Upsert(BaseItemDto program, IEnumerable<string> mappedGenres,
            ILogger logger)
        {
            if (program == null) return;
            try
            {
                var cfg = Plugin.Instance?.Configuration;
                if (!(cfg?.DecisionLogEnabled ?? false)) return;
                var pid = program.Id?.ToString();
                if (string.IsNullOrEmpty(pid)) return;
                var title = !string.IsNullOrEmpty(program.SeriesName) ? program.SeriesName : program.Name;
                if (string.IsNullOrWhiteSpace(title)) return;
                var entry = new EpgSnapshotEntry
                {
                    ProgramId = pid,
                    Title = title.Trim(),
                    EpisodeTitle = program.EpisodeTitle ?? "",
                    Overview = Clamp(program.Overview, 300),
                    Channel = program.ChannelName ?? "",
                    Start = program.StartDate ?? DateTimeOffset.MinValue,
                    RuntimeMin = program.RunTimeTicks.HasValue
                        ? (int)Math.Max(0, program.RunTimeTicks.Value / TimeSpan.TicksPerMinute)
                        : (int?)null,
                    Genres = (mappedGenres ?? Enumerable.Empty<string>())
                        .Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).ToList(),
                    Year = program.ProductionYear,
                    IsSeries = program.IsSeries == true,
                    IsMovie = program.IsMovie == true,
                    Updated = DateTimeOffset.UtcNow
                };
                UpsertCore(entry, logger);
            }
            catch (Exception ex)
            {
                logger?.Debug("[LLM_AI] Snapshot EPG : upsert échoué : {0}", ex.Message);
            }
        }

        /// <summary>
        /// Marque le programme comme programmé (timer d'enregistrement créé
        /// par <see cref="AutoProgrammer"/>). Crée une entrée minimale si le
        /// programme n'a pas été capturé à l'émission (ex. reco enrichie via
        /// tmdb_lookup, jamais passée par un outil EPG). Best-effort.
        /// </summary>
        internal static void MarkTimer(string programId, string title,
            string channel, DateTimeOffset? start, ILogger logger)
        {
            if (string.IsNullOrEmpty(programId)) return;
            try
            {
                var cfg = Plugin.Instance?.Configuration;
                if (!(cfg?.DecisionLogEnabled ?? false)) return;
                lock (_lock)
                {
                    var list = Parse();
                    var e = list.FirstOrDefault(x => string.Equals(x.ProgramId, programId, StringComparison.Ordinal));
                    if (e == null)
                    {
                        e = new EpgSnapshotEntry
                        {
                            ProgramId = programId,
                            Title = (title ?? "").Trim(),
                            Channel = channel ?? "",
                            Start = start ?? DateTimeOffset.MinValue
                        };
                        list.Add(e);
                    }
                    e.Timer = true;
                    e.Updated = DateTimeOffset.UtcNow;
                    Save(list, logger);
                }
            }
            catch (Exception ex)
            {
                logger?.Debug("[LLM_AI] Snapshot EPG : MarkTimer échoué : {0}", ex.Message);
            }
        }

        private static void UpsertCore(EpgSnapshotEntry entry, ILogger logger)
        {
            lock (_lock)
            {
                var list = Parse();
                var e = list.FirstOrDefault(x => string.Equals(x.ProgramId, entry.ProgramId, StringComparison.Ordinal));
                if (e == null)
                {
                    list.Add(entry);
                }
                else
                {
                    // Rafraîchissement : la diffusion la plus récente du même
                    // programme gagne (les rediffusions re-décrivent parfois).
                    e.Title = entry.Title;
                    e.EpisodeTitle = entry.EpisodeTitle;
                    e.Overview = entry.Overview;
                    e.Channel = entry.Channel;
                    e.Start = entry.Start;
                    e.RuntimeMin = entry.RuntimeMin;
                    e.Genres = entry.Genres;
                    e.Year = entry.Year;
                    e.IsSeries = entry.IsSeries;
                    e.IsMovie = entry.IsMovie;
                    e.Updated = entry.Updated;
                }
                Save(list, logger);
            }
        }

        // -----------------------------------------------------------------
        //  Lecture (analyse hebdo, Phase C)
        // -----------------------------------------------------------------

        /// <summary>Liste les entrées (tolérant : absent/corrompu → vide).</summary>
        internal static List<EpgSnapshotEntry> Parse()
        {
            var list = new List<EpgSnapshotEntry>();
            foreach (var n in ReadArray())
            {
                if (!(n is JsonObject o)) continue;
                var e = new EpgSnapshotEntry
                {
                    ProgramId = OStr(o, "pid") ?? "",
                    Title = OStr(o, "t") ?? "",
                    EpisodeTitle = OStr(o, "et") ?? "",
                    Overview = OStr(o, "ov") ?? "",
                    Channel = OStr(o, "ch") ?? "",
                    RuntimeMin = OStr(o, "rt") is var rs && int.TryParse(rs, out var rt) ? rt : (int?)null,
                    Year = OStr(o, "y") is var ys && int.TryParse(ys, out var y) ? y : (int?)null,
                    IsSeries = (OStr(o, "is") ?? "") == "1",
                    IsMovie = (OStr(o, "im") ?? "") == "1",
                    Timer = (OStr(o, "tm") ?? "") == "1"
                };
                if (o.TryGetPropertyValue("g", out var gv) && gv is JsonArray ga)
                    foreach (var gn in ga)
                        if (gn is JsonValue jv && jv.TryGetValue<string>(out var gs))
                            e.Genres.Add(gs);
                if (OStr(o, "st") is var st && DateTimeOffset.TryParse(st, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var sdt))
                    e.Start = sdt.ToUniversalTime();
                if (OStr(o, "up") is var up && DateTimeOffset.TryParse(up, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var udt))
                    e.Updated = udt.ToUniversalTime();
                if (string.IsNullOrWhiteSpace(e.ProgramId) || string.IsNullOrWhiteSpace(e.Title)) continue;
                list.Add(e);
            }
            return list;
        }

        // -----------------------------------------------------------------
        //  Persistance
        // -----------------------------------------------------------------

        private static void Save(List<EpgSnapshotEntry> list, ILogger logger)
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-RetentionDays);
            var kept = (list ?? new List<EpgSnapshotEntry>())
                .Where(e => e != null && !string.IsNullOrWhiteSpace(e.ProgramId)
                            && !string.IsNullOrWhiteSpace(e.Title)
                            && (e.Updated >= cutoff || e.Timer))
                .OrderBy(e => e.Updated)
                .ToList();
            if (kept.Count > MaxEntries)
                kept = kept.Skip(kept.Count - MaxEntries).ToList();
            var arr = new JsonArray();
            foreach (var e in kept)
            {
                var ga = new JsonArray();
                foreach (var g in e.Genres.Take(8))
                    if (!string.IsNullOrWhiteSpace(g)) ga.Add(g);
                arr.Add(new JsonObject
                {
                    ["pid"] = e.ProgramId,
                    ["t"] = e.Title,
                    ["et"] = e.EpisodeTitle ?? "",
                    ["ov"] = e.Overview ?? "",
                    ["ch"] = e.Channel ?? "",
                    ["st"] = e.Start.ToString("o", CultureInfo.InvariantCulture),
                    ["rt"] = e.RuntimeMin?.ToString(CultureInfo.InvariantCulture) ?? "",
                    ["g"] = ga,
                    ["y"] = e.Year?.ToString(CultureInfo.InvariantCulture) ?? "",
                    ["is"] = e.IsSeries ? "1" : "0",
                    ["im"] = e.IsMovie ? "1" : "0",
                    ["tm"] = e.Timer ? "1" : "0",
                    ["up"] = e.Updated.ToString("o", CultureInfo.InvariantCulture)
                });
            }
            WriteAll(arr.ToJsonString(), logger);
        }

        private static string Clamp(string text, int max)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var t = text.Trim();
            return t.Length <= max ? t : t.Substring(0, max) + " …";
        }

        private static string SnapshotPath
        {
            get
            {
                try
                {
                    var dir = Plugin.Paths?.PluginConfigurationsPath;
                    if (string.IsNullOrEmpty(dir)) return null;
                    return Path.Combine(dir, "epg_snapshot.json");
                }
                catch { return null; }
            }
        }

        private static System.Collections.Generic.IEnumerable<JsonNode> ReadArray()
        {
            var path = SnapshotPath;
            if (path == null || !File.Exists(path)) yield break;
            string json;
            try { json = File.ReadAllText(path); }
            catch { yield break; }
            JsonArray arr = null;
            try { arr = JsonNode.Parse(json) as JsonArray; }
            catch { /* corrompu : vide */ }
            if (arr == null) yield break;
            foreach (var n in arr) yield return n;
        }

        private static void WriteAll(string json, ILogger logger)
        {
            var path = SnapshotPath;
            if (path == null) return;
            try
            {
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Snapshot EPG : écriture échouée : {0}", ex.Message);
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