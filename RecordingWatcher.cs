using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

using MediaBrowser.Controller.Providers;

using MediaBrowser.Model.Events;

namespace LLM_AI
{
    /// <summary>
    /// Observateur de fin d'enregistrement DVR (contrôle à la source) :
    /// branché sur <see cref="ILiveTvManager.RecordingEnded"/> et
    /// <see cref="ILibraryManager.ItemAdded"/> (pattern
    /// <see cref="PlaybackWatcher"/> : <see cref="IServerEntryPoint"/> découvert
    /// par scan d'assembly, abonnement dans <see cref="Run"/>, désabonnement
    /// dans <see cref="Dispose"/>).
    /// </summary>
    /// <remarks>
    /// <para><b>Motivation</b> : à l'import d'un enregistrement, les providers
    /// distants d'Emby identifient automatiquement l'œuvre. En cas de mauvais
    /// match, un id <b>faux</b> est écrit ET le synopsis EPG (source de vérité)
    /// est écrasé — l'item n'est plus orphelin et échappe à la passe 04 h.
    /// Le watcher fige la vérité EPG (programme du guide, encore dans la
    /// fenêtre EPG au moment de la diffusion) <b>avant</b> que l'identification
    /// n'ait pu l'écraser, puis confie l'item à <see cref="OrphanResolver"/> :
    /// audit de l'id posé par Emby (juge synopsis), identification immédiate
    /// sinon.</para>
    /// <para><b>Decouplage</b> : les événements se contentent d'upserter la
    /// vérité dans <c>recording_validate.json</c> (<see cref="RecordingValidateStore"/>,
    /// pattern <see cref="EpgSnapshotStore"/>) — jamais de travail lourd dans
    /// le thread d'événement. Une boucle d'arrière-plan unique traite les
    /// entrées âgées de plus de quelques minutes (laisser Emby finir son
    /// identification), une par tick, et les retire du store après résolution
    /// (ou abandons au bout de <see cref="MaxAttempts"/>).</para>
    /// <para>Opt-in <c>OrphanValidateOnRecordingEnd</c> (défaut false), vérifié
    /// à CHAQUE événement (pas seulement à Run) — le flag peut être activé sans
    /// redémarrage. Dry-run <c>OrphanIdentifyDryRun</c> respecté. Best-effort :
    /// un échec n'affecte jamais l'enregistrement ni le serveur.</para>
    /// </remarks>
    public class RecordingWatcher : IServerEntryPoint
    {
        private readonly ILiveTvManager _liveTv;
        private readonly ILibraryManager _library;
        private readonly IServerApplicationHost _host;
        private readonly IProviderManager _providers;
        private readonly IJsonSerializer _json;
        private readonly ILogger _logger;

        private CancellationTokenSource _cts;
        private Task _loop;
        private OrphanResolver _resolver;
        private bool _warnedNoKey;

        /// <summary>Âge minimal d'une entrée avant traitement : laisser Emby
        /// finir son identification (providers) après l'import.</summary>
        private static readonly TimeSpan s_processDelay = TimeSpan.FromMinutes(3);

        /// <summary>Intervalle de la boucle d'arrière-plan.</summary>
        private static readonly TimeSpan s_pollInterval = TimeSpan.FromSeconds(60);

        public RecordingWatcher(
            ILiveTvManager liveTv, ILibraryManager library,
            IServerApplicationHost host, IProviderManager providers,
            IJsonSerializer json, ILogger logger)
        {
            _liveTv = liveTv;
            _library = library;
            _host = host;
            _providers = providers;
            _json = json;
            _logger = logger;
        }

        public void Run()
        {
            _liveTv.RecordingEnded += OnRecordingEnded;
            _library.ItemAdded += OnItemAdded;
            _cts = new CancellationTokenSource();
            _loop = Task.Run(ProcessLoopAsync, _cts.Token);
        }

        public void Dispose()
        {
            _liveTv.RecordingEnded -= OnRecordingEnded;
            _library.ItemAdded -= OnItemAdded;
            try { _cts?.Cancel(); } catch { /* best-effort */ }
        }

        // ------------------------------------------------------------------
        //  Événements : figer la vérité EPG (rapide, jamais bloquant)
        // ------------------------------------------------------------------

        private void OnRecordingEnded(object sender, GenericEventArgs<ActiveRecordingInfo> e)
        {
            try
            {
                var cfg = Plugin.Instance?.Configuration;
                if (cfg == null || !cfg.OrphanValidateOnRecordingEnd) return;
                var rec = e?.Argument;
                if (rec == null || string.IsNullOrWhiteSpace(rec.Path)) return;

                var prog = rec.Program;
                RecordingValidateStore.Upsert(new RecordingValidateStore.Entry
                {
                    Path = rec.Path,
                    Title = Truncate(prog?.Name, 300),
                    Overview = Truncate(prog?.Overview, 800),
                    Year = prog?.ProductionYear,
                    Channel = Truncate(rec.Channel?.Name, 120),
                    Updated = DateTimeOffset.UtcNow
                });
                _logger?.Info("[LLM_AI] Recording : fin d'enregistrement « {0} » ({1}) — vérité EPG figée{2}.",
                    Truncate(prog?.Name ?? rec.Path, 80), rec.Channel?.Name ?? "—",
                    cfg.OrphanIdentifyDryRun ? " (DRY-RUN global)" : "");
            }
            catch (OperationCanceledException) { /* jamais */ }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] Recording : échec snapshot EPG ({0}) — ignoré.", ex.Message);
            }
        }

        private void OnItemAdded(object sender, ItemChangeEventArgs e)
        {
            try
            {
                var cfg = Plugin.Instance?.Configuration;
                if (cfg == null || !cfg.OrphanValidateOnRecordingEnd) return;
                var item = e?.Item;
                if (item == null || string.IsNullOrWhiteSpace(item.Path)) return;
                if (OrphanResolver.IsStrmCard(item)) return;
                var typeName = item.GetType().Name;
                if (typeName.IndexOf("Movie", StringComparison.OrdinalIgnoreCase) < 0 &&
                    typeName.IndexOf("Series", StringComparison.OrdinalIgnoreCase) < 0)
                    return;
                if (!IsUnderRecordingPath(item.Path)) return;

                // Filet de l'événement RecordingEnded : si la vérité EPG est déjà
                // figée pour ce chemin, Upsert ne l'écrase pas (complète seulement).
                RecordingValidateStore.Upsert(new RecordingValidateStore.Entry
                {
                    Path = item.Path,
                    Title = Truncate(item.Name, 300),
                    Overview = Truncate(item.Overview, 800),
                    Year = item.ProductionYear,
                    Channel = null,
                    Updated = DateTimeOffset.UtcNow
                });
                _logger?.Info("[LLM_AI] Recording : item DVR importé en bibliothèque « {0} » — filet d'import posé.", item.Name);
            }
            catch (OperationCanceledException) { /* jamais */ }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] Recording : échec filet d'import ({0}) — ignoré.", ex.Message);
            }
        }

        // ------------------------------------------------------------------
        //  Boucle d'arrière-plan : traiter les entrées mûres
        // ------------------------------------------------------------------

        private async Task ProcessLoopAsync()
        {
            var ct = _cts.Token;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await ProcessPendingAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    _logger?.Warn("[LLM_AI] Recording : cycle de validation échoué ({0}).", ex.Message);
                }
                try { await Task.Delay(s_pollInterval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        private async Task ProcessPendingAsync(CancellationToken ct)
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null || !cfg.OrphanValidateOnRecordingEnd) return;
            if (string.IsNullOrWhiteSpace(cfg.TmdbApiKey))
            {
                if (!_warnedNoKey)
                {
                    _warnedNoKey = true;
                    _logger?.Warn("[LLM_AI] Recording : clé API TMDB absente — validation des enregistrements inactive.");
                }
                return;
            }

            var pending = RecordingValidateStore.Parse()
                .Where(e => e.Attempts < RecordingValidateStore.MaxAttempts)
                .Where(e => (DateTimeOffset.UtcNow - e.Updated) >= s_processDelay)
                .ToList();
            if (pending.Count == 0) return;

            if (_resolver == null)
                _resolver = new OrphanResolver(_logger, _json, _library,
                    _host.TryResolve<IUserManager>(), _host.TryResolve<ILiveTvManager>(),
                    _host, _providers);

            string userTmdb = I18n.ToTmdbLang(I18n.ResolveMetaLangKey(cfg, _host));
            bool dry = cfg.OrphanIdentifyDryRun;
            bool verbose = cfg.DebugVerbose;

            // Une seule requête bibliothèque par cycle, filtrage par chemin en mémoire.
            BaseItem[] items = null;
            try
            {
                items = _library.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Movie", "Series", "Episode" },
                    Recursive = true,
                    EnableTotalRecordCount = false
                }) ?? Array.Empty<BaseItem>();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] Recording : scan bibliothèque échoué ({0}) — cycle ignoré.", ex.Message);
                return;
            }

            foreach (var entry in pending)
            {
                ct.ThrowIfCancellationRequested();

                var item = items.FirstOrDefault(i =>
                    string.Equals(i?.Path, entry.Path, StringComparison.OrdinalIgnoreCase));
                if (item == null)
                {
                    // Emby importe l'enregistrement quelques instants après la
                    // fin : on réessaie, jusqu'à abandon (les enregistrements
                    // non importés — échecs, annulations — finissent par sortir).
                    entry.Attempts++;
                    if (entry.Attempts >= RecordingValidateStore.MaxAttempts)
                    {
                        _logger?.Info("[LLM_AI] Recording : « {0} » absent de la bibliothèque après {1} essais — abandon (item peut-être non importé).",
                            Path.GetFileName(entry.Path), entry.Attempts);
                        RecordingValidateStore.Remove(entry.Path);
                    }
                    else RecordingValidateStore.Upsert(entry);
                    continue;
                }

                // Un enregistrement de série numérotée importe comme épisode :
                // les métadonnées d'un épisode dérivent de l'identification de
                // SA série (les providers remplissent l'épisode à partir de
                // l'id de la série) — chercher la série par son titre EPG (un
                // titre d'épisode) donnerait un faux négatif garanti. L'épisode
                // est donc couvert par l'audit de sa série (entrées Movie/Series
                // du filet d'import) : l'entrée est gelée — la vérité EPG de
                // l'épisode reste dans le store jusqu'à la rétention, sans
                // re-traitement.
                if (item.GetType().Name.IndexOf("Episode", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var series = TryResolveParentSeries(item);
                    entry.Attempts = RecordingValidateStore.MaxAttempts; // gel : purgé par la rétention
                    RecordingValidateStore.Upsert(entry);
                    if (series != null)
                        _logger?.Info("[LLM_AI] Recording : épisode « {0} » couvert par l'audit de sa série « {1} » ({2}).",
                            item.Name, series.Name, IsSeriesAudited(series)
                                ? "déjà auditée"
                                : "audit de la série en attente / à la passe 04 h");
                    else
                        _logger?.Info("[LLM_AI] Recording : épisode « {0} » sans série parente résoluble — entrée gelée (audit par la série impossible).",
                            item.Name);
                    continue;
                }

                OrphanResolver.Status status;
                try
                {
                    var truth = new OrphanResolver.EpgTruth
                    {
                        Title = entry.Title,
                        Overview = entry.Overview,
                        Year = entry.Year,
                        Channel = entry.Channel
                    };
                    status = await _resolver.ResolveItemAsync(item, truth, cfg, dry, userTmdb, verbose, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    status = OrphanResolver.Status.Skipped;
                    _logger?.Warn("[LLM_AI] Recording : erreur sur « {0} » ({1}) — abandon de l'entrée.", item?.Name, ex.Message);
                }

                // Résolu ou abandonné : l'entrée sort du store (la passe 04 h
                // reste le filet pour les needs-review si le retry est activé).
                RecordingValidateStore.Remove(entry.Path);
                _logger?.Info("[LLM_AI] Recording : « {0} » traité → {1} (path={2}).",
                    item?.Name ?? Path.GetFileName(entry.Path), status, Path.GetFileName(entry.Path));
            }
        }

        // ------------------------------------------------------------------
        //  Helpers
        // ------------------------------------------------------------------

        /// <summary>Série parente d'un épisode DVR (<see cref="Episode.Series"/>,
        /// repli <c>GetItemById(SeriesId)</c>) — null si non résoluble.</summary>
        private BaseItem TryResolveParentSeries(BaseItem item)
        {
            try
            {
                if (!(item is Episode ep)) return null;
                var series = ep.Series;
                if (series == null && ep.SeriesId != 0)
                    series = _library.GetItemById(ep.SeriesId) as Series;
                return series;
            }
            catch { return null; }
        }

        /// <summary>La série a-t-elle déjà été auditée (tag du plugin ou ids
        /// provider posés) — un épisode en hérite, pas d'audit séparé.</summary>
        private static bool IsSeriesAudited(BaseItem series)
        {
            if (series == null) return false;
            try
            {
                var tags = series.Tags ?? Array.Empty<string>();
                if (Array.IndexOf(tags, OrphanIdentifyTask.TagIdentified) >= 0
                    || Array.IndexOf(tags, OrphanIdentifyTask.TagNeedsReview) >= 0) return true;
                return OrphanResolver.HasItemProviderId(series, "tmdb")
                    || OrphanResolver.HasItemProviderId(series, "tvdb")
                    || OrphanResolver.HasItemProviderId(series, "imdb");
            }
            catch { return false; }
        }

        /// <summary>L'item vit-il sous le dossier d'enregistrements DVR ?</summary>
        private bool IsUnderRecordingPath(string itemPath)
        {
            if (string.IsNullOrWhiteSpace(itemPath)) return false;
            if (!RecordingDiskManager.TryResolveRecordingPath(_host, _logger, out var root)
                || string.IsNullOrWhiteSpace(root)) return false;

            if (itemPath.Length <= root.Length) return false;
            if (!itemPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
            var sep = itemPath[root.Length];
            return sep == Path.DirectorySeparatorChar || sep == Path.AltDirectorySeparatorChar;
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return null;
            s = s.Trim();
            if (s.Length == 0) return null;
            return s.Length <= max ? s : s.Substring(0, max);
        }
    }

    /// <summary>
    /// Store <c>recording_validate.json</c> (répertoire de configuration du
    /// plugin, pattern <see cref="EpgSnapshotStore"/>) : vérité EPG des
    /// enregistrements DVR en attente de validation, écrite à l'événement de
    /// fin (rapide) et lue par la boucle d'arrière-plan du
    /// <see cref="RecordingWatcher"/>. Statique, thread-safe (lock), fail-open,
    /// rétention courte — les entrées sont retirées après traitement.
    /// </summary>
    internal static class RecordingValidateStore
    {
        internal sealed class Entry
        {
            /// <summary>Chemin du fichier d'enregistrement (clé).</summary>
            public string Path;
            public string Title;
            public string Overview;
            public int? Year;
            public string Channel;
            public DateTimeOffset Updated;
            /// <summary>Nb de cycles sans trouver l'item en bibliothèque.</summary>
            public int Attempts;
        }

        internal const int RetentionDays = 7;
        internal const int MaxEntries = 500;
        /// <summary>Cycles max sans trouver l'item avant abandon (60 s / cycle).</summary>
        internal const int MaxAttempts = 15;

        private static readonly object _lock = new object();

        /// <summary>
        /// Fige (upsert) une entrée, clé <see cref="Entry.Path"/>. Une entrée
        /// existante ne perd jamais une vérité déjà figée (le snapshot
        /// RecordingEnded — programme du guide — prime sur les champs d'import,
        /// potentiellement écrasés par l'identification d'Emby) : seuls les
        /// champs manquants sont complétés, <see cref="Entry.Updated"/> n'est
        /// pas repoussé. Best-effort : n'échoue jamais l'appelant.
        /// </summary>
        internal static void Upsert(Entry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Path)) return;
            try
            {
                lock (_lock)
                {
                    var list = Parse();
                    var e = list.FirstOrDefault(x =>
                        string.Equals(x.Path, entry.Path, StringComparison.OrdinalIgnoreCase));
                    if (e == null)
                    {
                        list.Add(entry);
                    }
                    else
                    {
                        if (string.IsNullOrWhiteSpace(e.Title)) e.Title = entry.Title;
                        if (string.IsNullOrWhiteSpace(e.Overview)) e.Overview = entry.Overview;
                        e.Year = e.Year ?? entry.Year;
                        if (string.IsNullOrWhiteSpace(e.Channel)) e.Channel = entry.Channel;
                        e.Attempts = entry.Attempts;
                    }
                    Save(list);
                }
            }
            catch { /* fail-open */ }
        }

        /// <summary>Retire l'entrée d'un chemin (traitée ou abandonnée).</summary>
        internal static void Remove(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                lock (_lock)
                {
                    var list = Parse();
                    list.RemoveAll(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
                    Save(list);
                }
            }
            catch { /* fail-open */ }
        }

        /// <summary>Liste les entrées (tolérant : absent/corrompu → vide).</summary>
        internal static List<Entry> Parse()
        {
            var list = new List<Entry>();
            foreach (var n in ReadArray())
            {
                if (!(n is JsonObject o)) continue;
                var e = new Entry
                {
                    Path = OStr(o, "p") ?? "",
                    Title = OStr(o, "t"),
                    Overview = OStr(o, "ov"),
                    Channel = OStr(o, "ch"),
                    Updated = DateTimeOffset.TryParse(OStr(o, "up"), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var u) ? u.ToUniversalTime() : DateTimeOffset.UtcNow,
                    Attempts = OStr(o, "at") is var at && int.TryParse(at, out var atv) ? atv : 0
                };
                if (OStr(o, "y") is var ys && int.TryParse(ys, out var y)) e.Year = y;
                if (!string.IsNullOrWhiteSpace(e.Path)) list.Add(e);
            }
            return list;
        }

        private static void Save(List<Entry> list)
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-RetentionDays);
            var kept = (list ?? new List<Entry>())
                .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Path) && e.Updated >= cutoff)
                .OrderBy(e => e.Updated)
                .ToList();
            if (kept.Count > MaxEntries)
                kept = kept.Skip(kept.Count - MaxEntries).ToList();
            var arr = new JsonArray();
            foreach (var e in kept)
            {
                arr.Add(new JsonObject
                {
                    ["p"] = e.Path,
                    ["t"] = e.Title ?? "",
                    ["ov"] = e.Overview ?? "",
                    ["ch"] = e.Channel ?? "",
                    ["y"] = e.Year?.ToString(CultureInfo.InvariantCulture) ?? "",
                    ["up"] = e.Updated.ToString("o", CultureInfo.InvariantCulture),
                    ["at"] = e.Attempts.ToString(CultureInfo.InvariantCulture)
                });
            }
            WriteAll(arr.ToJsonString());
        }

        private static string StorePath
        {
            get
            {
                try
                {
                    var dir = Plugin.Paths?.PluginConfigurationsPath;
                    if (string.IsNullOrEmpty(dir)) return null;
                    return Path.Combine(dir, "recording_validate.json");
                }
                catch { return null; }
            }
        }

        private static IEnumerable<JsonNode> ReadArray()
        {
            var path = StorePath;
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

        private static void WriteAll(string json)
        {
            var path = StorePath;
            if (path == null) return;
            try { File.WriteAllText(path, json); }
            catch { /* fail-open */ }
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