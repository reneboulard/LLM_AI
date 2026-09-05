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
    //  Stores de la mémoire réflexive (Phase A) : décisions, pool de
    //  candidats, télémétrie de lecture.
    //
    //  Trois fichiers JSON dans le répertoire de configuration du plugin
    //  (pattern AiTonightFavoritesManager) :
    //    - decisions.json : une entrée PAR RECO ÉMISE (tonight/record/chat/
    //      drop) avec la raison du LLM (champ « reason » de son tableau JSON,
    //      déjà produit mais perdu avant cette phase) et la version de la
    //      fiche mémoire en vigueur (mv, 0 tant que la fiche n'existe pas) ;
    //    - run_pool.json   : une entrée PAR RUN — le « menu » soumis au LLM
    //      (candidats EPG capturés par get_emby_info + réserve bibliothèque +
    //      enregistrements non visionnés), qui permet de distinguer une
    //      mauvaise reco d'une erreur de CLASSEMENT (le gagnant ignoré alors
    //      qu'un écarté aurait été regardé) ;
    //    - playback.json   : une entrée PAR SESSION DE LECTURE terminée
    //      (PlaybackStopped) avec % lu, durée, contexte (app/appareil/chaîne).
    //
    //  Jointures du tableau hebdo (Phase C) : decisions.item_id /
    //  program_id → playback.item_id ; decisions.run_id → run_pool.run_id.
    //
    //  Tout est opt-in (DecisionLogEnabled / PlaybackTelemetryEnabled),
    //  fail-open (fichier absent/corrompu → vide, jamais d'exception) et
    //  best-effort (un échec de persistance ne casse jamais l'appelant).
    // ---------------------------------------------------------------------

    /// <summary>Une entrée du journal de décisions (une reco émise).</summary>
    internal sealed class DecisionEntry
    {
        public string RunId;
        /// <summary>"tonight" | "record" | "chat" | "drop".</summary>
        public string Kind;
        /// <summary>Id d'usager ; "" = reco globale (record du foyer).</summary>
        public string User;
        public DateTimeOffset Date;
        public string Title;
        /// <summary>ItemId Emby (InternalId) pour recording/library ; "" pour un EPG pur.</summary>
        public string ItemId;
        /// <summary>Id de programme EPG pour source=live ; "" sinon.</summary>
        public string ProgramId;
        /// <summary>live | recording | library | series | movie | "" (drop).</summary>
        public string Source;
        /// <summary>La raison du LLM (argument de la reco) — l'objet de la
        /// calibration : on révise une croyance, pas un titre.</summary>
        public string Reason;
        /// <summary>Priority émise (high/medium/low) ou "".</summary>
        public string Priority;
        /// <summary>Version de la fiche mémoire en vigueur au moment de la
        /// décision (0 = pas encore de fiche).</summary>
        public int Mv;
    }

    /// <summary>Un candidat du « menu » soumis au LLM pour un run.</summary>
    internal sealed class CandidateEntry
    {
        public string Id;
        public string Title;
        /// <summary>epg | epg_series | epg_movies | recording | library.</summary>
        public string Source;
        public string Channel;
        /// <summary>Début de diffusion (EPG) ou "" (bibliothèque/enregistrement).</summary>
        public string Start;
        /// <summary>Genres joints par «, » (normalisés à la source).</summary>
        public string Genres;
    }

    /// <summary>Le pool de candidats d'un run (sérialisé dans run_pool.json).</summary>
    internal sealed class RunPool
    {
        public string RunId;
        public string User;
        public DateTimeOffset Date;
        public int Mv;
        public List<CandidateEntry> Candidates = new List<CandidateEntry>();
    }

    /// <summary>Une session de lecture terminée (télémétrie).</summary>
    internal sealed class PlaybackEntry
    {
        public string ItemId;
        public string User;
        /// <summary>Début de la lecture (UTC).</summary>
        public DateTimeOffset Date;
        /// <summary>Secondes réellement lues.</summary>
        public int DurSec;
        /// <summary>Fraction lue (0-1) ; null si la durée d'œuvre est inconnue
        /// (direct : connue seulement via l'EpgSnapshot, Phase B).</summary>
        public double? Pct;
        /// <summary>Durée d'œuvre connue au moment de la lecture (s) ou null.</summary>
        public int? RtSec;
        /// <summary>library | strm | livetv.</summary>
        public string Src;
        public string Channel;
        public string App;
        public string Device;
        public bool Completed;
    }

    /// <summary>
    /// Stores fichiers de la mémoire réflexive. Statique : le journal est
    /// transverse (écrit par TonightService, LlmScheduledTask, RecosApiService,
    /// GetEmbyInfoTool, PlaybackWatcher ; lu par la future tâche Mémoire).
    /// Tous les appelsants passent la config courante ; l'activation est
    /// contrôlée par <see cref="PluginConfiguration.DecisionLogEnabled"/> (et
    /// <see cref="PluginConfiguration.PlaybackTelemetryEnabled"/> pour la
    /// télémétrie). Les lectures tolèrent un JSON invalide (repart de vide).
    /// </summary>
    internal static class DecisionStore
    {
        /// <summary>Fenêtre de rétention (jours) des trois stores.</summary>
        internal const int RetentionDays = 30;

        /// <summary>Plafonds (entrées les plus récentes gardées).</summary>
        internal const int MaxDecisions = 1000;
        internal const int MaxPools = 200;
        internal const int MaxPlayback = 2000;

        /// <summary>Plafond de candidats stockés par run (le menu complet est
        /// déjà capé côté LLM ; garde-fou anti-dérapage).</summary>
        internal const int MaxCandidatesPerRun = 60;

        /// <summary>Plafond d'une raison stockée (le champ LLM peut être long).</summary>
        internal const int MaxReasonChars = 300;

        // -----------------------------------------------------------------
        //  Run actif : contexte statique reliant get_emby_info (sans usager,
        //  sans orchestrateur) au run en cours. Posé par TonightService /
        //  LlmScheduledTask autour du run agent, lu par GetEmbyInfoTool au
        //  moment de l'émission des candidats EPG. Deux runs simultanés
        //  (deux usagers) peuvent entremêler leurs captures — accepté : le
        //  pool est un contexte heuristique, pas un registre comptable.
        // -----------------------------------------------------------------

        private static readonly object _lock = new object();
        private static string _activeRunId;
        private static readonly List<KeyValuePair<string, CandidateEntry>> _capture =
            new List<KeyValuePair<string, CandidateEntry>>();

        /// <summary>runId du run agent en cours, ou null.</summary>
        internal static string ActiveRunId
        {
            get { lock (_lock) return _activeRunId; }
        }

        /// <summary>Ouvre un run : pose le runId et purge ses captures résiduelles.</summary>
        internal static void BeginRun(string runId)
        {
            if (string.IsNullOrEmpty(runId)) return;
            lock (_lock)
            {
                _activeRunId = runId;
                _capture.RemoveAll(kv => string.Equals(kv.Key, runId, StringComparison.Ordinal));
            }
        }

        /// <summary>
        /// Ferme un run : retourne les candidats capturés pendant celui-ci
        /// (dédupliqués, plafonnés) et retire le runId actif. Best-effort :
        /// jamais d'exception.
        /// </summary>
        internal static List<CandidateEntry> EndRun(string runId)
        {
            try
            {
                lock (_lock)
                {
                    if (string.Equals(_activeRunId, runId, StringComparison.Ordinal))
                        _activeRunId = null;
                    var list = new List<CandidateEntry>();
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var kv in _capture)
                    {
                        if (!string.Equals(kv.Key, runId, StringComparison.Ordinal)) continue;
                        var c = kv.Value;
                        if (c == null || string.IsNullOrWhiteSpace(c.Title)) continue;
                        var key = (c.Id ?? "") + "|" + c.Title.Trim().ToLowerInvariant();
                        if (!seen.Add(key)) continue;
                        list.Add(c);
                        if (list.Count >= MaxCandidatesPerRun) break;
                    }
                    _capture.RemoveAll(kv => string.Equals(kv.Key, runId, StringComparison.Ordinal));
                    return list;
                }
            }
            catch { return new List<CandidateEntry>(); }
        }

        /// <summary>
        /// Capture un candidat du run actif (EPG émis par get_emby_info,
        /// réserve bibliothèque, enregistrements). No-op hors run ou flag off.
        /// </summary>
        internal static void CaptureCandidate(string source, string id, string title,
            string channel, DateTimeOffset? start, IEnumerable<string> genres)
        {
            try
            {
                string runId;
                bool enabled;
                lock (_lock) { runId = _activeRunId; }
                if (string.IsNullOrEmpty(runId)) return;
                var cfg = Plugin.Instance?.Configuration;
                enabled = cfg?.DecisionLogEnabled ?? false;
                if (!enabled) return;
                var c = new CandidateEntry
                {
                    Source = source ?? "",
                    Id = id ?? "",
                    Title = (title ?? "").Trim(),
                    Channel = channel ?? "",
                    Start = start.HasValue
                        ? start.Value.ToString("o", CultureInfo.InvariantCulture)
                        : "",
                    Genres = string.Join(", ", genres ?? Enumerable.Empty<string>())
                };
                lock (_lock) _capture.Add(new KeyValuePair<string, CandidateEntry>(runId, c));
            }
            catch { /* best-effort */ }
        }

        // -----------------------------------------------------------------
        //  Décisions
        // -----------------------------------------------------------------

        /// <summary>Ajoute des entrées au journal de décisions, puis persiste.
        /// Best-effort. Déclenche la migration du journal RecoLog au premier
        /// usage.</summary>
        internal static void AppendDecisions(PluginConfiguration cfg,
            IEnumerable<DecisionEntry> entries, ILogger logger)
        {
            if (cfg == null || entries == null) return;
            try
            {
                lock (_lock)
                {
                    var list = ParseDecisions();
                    EnsureMigrated(cfg, list);
                    list.AddRange(entries.Where(e => e != null && !string.IsNullOrWhiteSpace(e.Title)));
                    SaveDecisions(list, logger);
                }
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Décisions : échec de persistance : {0}", ex.Message);
            }
        }

        private static List<DecisionEntry> ParseDecisions()
        {
            var list = new List<DecisionEntry>();
            foreach (var n in ReadArray(DecisionsPath))
            {
                if (!(n is JsonObject o)) continue;
                var e = new DecisionEntry
                {
                    RunId = OStr(o, "run") ?? "",
                    Kind = OStr(o, "k") ?? "",
                    User = OStr(o, "u") ?? "",
                    Title = OStr(o, "t") ?? "",
                    ItemId = OStr(o, "i") ?? "",
                    ProgramId = OStr(o, "pid") ?? "",
                    Source = OStr(o, "s") ?? "",
                    Reason = OStr(o, "r") ?? "",
                    Priority = OStr(o, "p") ?? ""
                };
                if (long.TryParse(OStr(o, "mv") ?? "", out var mv)) e.Mv = (int)Math.Max(0, mv);
                if (OTry(o, "d", out var d)) e.Date = d.ToUniversalTime();
                if (string.IsNullOrWhiteSpace(e.Title)) continue;
                list.Add(e);
            }
            return list;
        }

        private static void SaveDecisions(List<DecisionEntry> list, ILogger logger)
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-RetentionDays);
            var kept = (list ?? new List<DecisionEntry>())
                .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Title) && e.Date >= cutoff)
                .OrderBy(e => e.Date)
                .ToList();
            if (kept.Count > MaxDecisions)
                kept = kept.Skip(kept.Count - MaxDecisions).ToList();
            var arr = new JsonArray();
            foreach (var e in kept)
            {
                var o = new JsonObject
                {
                    ["run"] = e.RunId ?? "",
                    ["k"] = e.Kind ?? "",
                    ["u"] = e.User ?? "",
                    ["d"] = e.Date.ToString("o", CultureInfo.InvariantCulture),
                    ["t"] = e.Title,
                    ["i"] = e.ItemId ?? "",
                    ["pid"] = e.ProgramId ?? "",
                    ["s"] = e.Source ?? "",
                    ["r"] = ClampReason(e.Reason),
                    ["p"] = e.Priority ?? "",
                    ["mv"] = Math.Max(0, e.Mv)
                };
                arr.Add(o);
            }
            WriteAll(DecisionsPath, arr.ToJsonString(), logger);
        }

        private static string ClampReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return "";
            var t = reason.Trim();
            return t.Length <= MaxReasonChars ? t : t.Substring(0, MaxReasonChars) + " …";
        }

        /// <summary>
        /// Carry-forward : au premier usage du store (fichier absent), les
        /// entrées du journal RecoLog (config XML, 30 jours) sont converties
        /// en décisions (runId/raison/mv inconnus). RecoLog reste actif en
        /// double écriture jusqu'à la Phase C (l'analyse hebdo actuelle le
        /// lit encore).
        /// </summary>
        private static void EnsureMigrated(PluginConfiguration cfg, List<DecisionEntry> list)
        {
            if (DecisionsPath == null || File.Exists(DecisionsPath)) return;
            if (list.Count > 0) return; // le fichier existait : rien à migrer
            foreach (var e in RecoFeedback.ParseLog(cfg))
            {
                // Dans l'ancien journal, Id est l'identifiant EPG pour les
                // recos « live » ET pour les recos d'enregistrement (leur
                // Source porte le kind series/movie, pas la provenance).
                var src = e.Source ?? "";
                var isEpg = string.Equals(e.Kind, "record", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(src, "live", StringComparison.OrdinalIgnoreCase);
                list.Add(new DecisionEntry
                {
                    RunId = "",
                    Kind = e.Kind ?? "",
                    User = e.User ?? "",
                    Date = e.Date,
                    Title = e.Title ?? "",
                    ItemId = isEpg ? "" : (e.Id ?? ""),
                    ProgramId = isEpg ? (e.Id ?? "") : "",
                    Source = src,
                    Reason = "",
                    Priority = "",
                    Mv = 0
                });
            }
        }

        // -----------------------------------------------------------------
        //  Pools de candidats
        // -----------------------------------------------------------------

        /// <summary>Persiste le pool de candidats d'un run (best-effort).</summary>
        internal static void SavePool(PluginConfiguration cfg, RunPool pool, ILogger logger)
        {
            if (cfg == null || pool == null || string.IsNullOrEmpty(pool.RunId)) return;
            try
            {
                lock (_lock)
                {
                    var pools = ParsePools();
                    pools.RemoveAll(p => string.Equals(p.RunId, pool.RunId, StringComparison.Ordinal));
                    pools.Add(pool);
                    var cutoff = DateTimeOffset.UtcNow.AddDays(-RetentionDays);
                    var kept = pools
                        .Where(p => p != null && !string.IsNullOrEmpty(p.RunId) && p.Date >= cutoff)
                        .OrderBy(p => p.Date)
                        .ToList();
                    if (kept.Count > MaxPools)
                        kept = kept.Skip(kept.Count - MaxPools).ToList();
                    var arr = new JsonArray();
                    foreach (var p in kept)
                    {
                        var cands = new JsonArray();
                        foreach (var c in (p.Candidates ?? new List<CandidateEntry>()).Take(MaxCandidatesPerRun))
                        {
                            if (c == null || string.IsNullOrWhiteSpace(c.Title)) continue;
                            cands.Add(new JsonObject
                            {
                                ["i"] = c.Id ?? "",
                                ["t"] = c.Title,
                                ["s"] = c.Source ?? "",
                                ["ch"] = c.Channel ?? "",
                                ["st"] = c.Start ?? "",
                                ["g"] = c.Genres ?? ""
                            });
                        }
                        arr.Add(new JsonObject
                        {
                            ["run"] = p.RunId,
                            ["u"] = p.User ?? "",
                            ["d"] = p.Date.ToString("o", CultureInfo.InvariantCulture),
                            ["mv"] = Math.Max(0, p.Mv),
                            ["c"] = cands
                        });
                    }
                    WriteAll(PoolsPath, arr.ToJsonString(), logger);
                }
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Pool : échec de persistance : {0}", ex.Message);
            }
        }

        private static List<RunPool> ParsePools()
        {
            var pools = new List<RunPool>();
            foreach (var n in ReadArray(PoolsPath))
            {
                if (!(n is JsonObject o)) continue;
                var p = new RunPool
                {
                    RunId = OStr(o, "run") ?? "",
                    User = OStr(o, "u") ?? "",
                    Mv = int.TryParse(OStr(o, "mv") ?? "", out var mv) ? Math.Max(0, mv) : 0
                };
                if (OTry(o, "d", out var d)) p.Date = d.ToUniversalTime();
                if (o.TryGetPropertyValue("c", out var cv) && cv is JsonArray ca)
                {
                    foreach (var cn in ca)
                    {
                        if (!(cn is JsonObject co)) continue;
                        p.Candidates.Add(new CandidateEntry
                        {
                            Id = OStr(co, "i") ?? "",
                            Title = OStr(co, "t") ?? "",
                            Source = OStr(co, "s") ?? "",
                            Channel = OStr(co, "ch") ?? "",
                            Start = OStr(co, "st") ?? "",
                            Genres = OStr(co, "g") ?? ""
                        });
                    }
                }
                if (string.IsNullOrEmpty(p.RunId)) continue;
                pools.Add(p);
            }
            return pools;
        }

        // -----------------------------------------------------------------
        //  Télémétrie de lecture
        // -----------------------------------------------------------------

        /// <summary>Ajoute une session de lecture au journal de télémétrie.
        /// Best-effort ; no-op si le flag est off.</summary>
        internal static void AppendPlayback(PluginConfiguration cfg,
            PlaybackEntry entry, ILogger logger)
        {
            if (cfg == null || entry == null) return;
            if (!(cfg.PlaybackTelemetryEnabled)) return;
            if (string.IsNullOrEmpty(entry.ItemId) && string.IsNullOrWhiteSpace(entry.Channel)) return;
            try
            {
                lock (_lock)
                {
                    var list = ParsePlayback();
                    list.Add(entry);
                    var cutoff = DateTimeOffset.UtcNow.AddDays(-RetentionDays);
                    var kept = list
                        .Where(e => e != null && e.Date >= cutoff)
                        .OrderBy(e => e.Date)
                        .ToList();
                    if (kept.Count > MaxPlayback)
                        kept = kept.Skip(kept.Count - MaxPlayback).ToList();
                    var arr = new JsonArray();
                    foreach (var e in kept)
                    {
                        var o = new JsonObject
                        {
                            ["i"] = e.ItemId ?? "",
                            ["u"] = e.User ?? "",
                            ["d"] = e.Date.ToString("o", CultureInfo.InvariantCulture),
                            ["dur"] = Math.Max(0, e.DurSec),
                            ["src"] = e.Src ?? "",
                            ["ch"] = e.Channel ?? "",
                            ["app"] = e.App ?? "",
                            ["dev"] = e.Device ?? "",
                            ["comp"] = e.Completed
                        };
                        if (e.Pct.HasValue) o["pct"] = Math.Round(e.Pct.Value, 3);
                        if (e.RtSec.HasValue) o["rt"] = Math.Max(0, e.RtSec.Value);
                        arr.Add(o);
                    }
                    WriteAll(PlaybackPath, arr.ToJsonString(), logger);
                }
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Télémétrie : échec de persistance : {0}", ex.Message);
            }
        }

        private static List<PlaybackEntry> ParsePlayback()
        {
            var list = new List<PlaybackEntry>();
            foreach (var n in ReadArray(PlaybackPath))
            {
                if (!(n is JsonObject o)) continue;
                var e = new PlaybackEntry
                {
                    ItemId = OStr(o, "i") ?? "",
                    User = OStr(o, "u") ?? "",
                    DurSec = int.TryParse(OStr(o, "dur") ?? "", out var dur) ? Math.Max(0, dur) : 0,
                    Src = OStr(o, "src") ?? "",
                    Channel = OStr(o, "ch") ?? "",
                    App = OStr(o, "app") ?? "",
                    Device = OStr(o, "dev") ?? "",
                    Completed = (OStr(o, "comp") ?? "") == "1" || (OStr(o, "comp") ?? "") == "true",
                    Pct = OStr(o, "pct") is var ps && double.TryParse(ps, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var p) ? p : (double?)null,
                    RtSec = OStr(o, "rt") is var rs && int.TryParse(rs, out var rt) ? rt : (int?)null
                };
                if (OTry(o, "d", out var d)) e.Date = d.ToUniversalTime();
                list.Add(e);
            }
            return list;
        }

        // -----------------------------------------------------------------
        //  IO communs
        // -----------------------------------------------------------------

        internal static string DecisionsPath => PathOf("decisions.json");
        internal static string PoolsPath => PathOf("run_pool.json");
        internal static string PlaybackPath => PathOf("playback.json");

        private static string PathOf(string fileName)
        {
            try
            {
                var dir = Plugin.Paths?.PluginConfigurationsPath;
                if (string.IsNullOrEmpty(dir)) return null;
                return Path.Combine(dir, fileName);
            }
            catch { return null; }
        }

        private static System.Collections.Generic.IEnumerable<JsonNode> ReadArray(string path)
        {
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

        private static void WriteAll(string path, string json, ILogger logger)
        {
            if (path == null) return;
            try
            {
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Stores mémoire : écriture {0} échouée : {1}", System.IO.Path.GetFileName(path), ex.Message);
            }
        }

        private static string OStr(JsonObject o, string key)
        {
            if (o.TryGetPropertyValue(key, out var v) && v is JsonValue jv
                && jv.TryGetValue<string>(out var s))
                return s;
            return null;
        }

        private static bool OTry(JsonObject o, string key, out DateTimeOffset value)
        {
            value = DateTimeOffset.MinValue;
            var s = OStr(o, key);
            if (s == null) return false;
            return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out value);
        }
    }
}