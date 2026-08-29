using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;
using MediaBrowser.Model.System;
using MediaBrowser.Model.Tasks;
using Emby.Notifications;

// TranscodingInfo.CurrentCpuUsage / AverageCpuUsage sont marquées obsolètes
// (Emby recommande ProcessStatistics, plus riche) mais restent fonctionnelles.
// On garde le scalaire — simple à interpréter par le LLM (« CPU transcodage
// élevé ») — plutôt que d'exposer la structure ProcessStatistics entière.
#pragma warning disable CS0618

namespace LLM_AI
{
    /// <summary>
    /// Outil natif « system_audit » exposé au LLM pour auditer la santé du
    /// serveur Emby. Contrairement à <see cref="GetEmbyInfoTool"/> (bibliothèque
    /// / EPG, lecture seule), cet outil interroge les services SystÈME d'Emby
    /// (sessions, tâches planifiées, transcodage, infos serveur) et le système
    /// hôte (process, disques) via la BCL. Douze actions dispatchées sur
    /// <c>action</c> :
    /// <list type="bullet">
    /// <item><b>Inspection (lecture seule, toujours disponibles)</b> :
    ///   <c>server_info</c>, <c>active_sessions</c>, <c>scheduled_tasks</c>,
    ///   <c>list_logs</c>, <c>inspect_log</c>, <c>transcode</c>,
    ///   <c>host_metrics</c>, <c>gpu_transcode</c>, <c>disk_storage</c>,
    ///   <c>processes</c> (orphelins ffmpeg + top processus RAM/CPU),
    ///   <c>library_stats</c>, <c>missing_metadata</c> (bibliothèque, via
    ///   <see cref="ILibraryManager"/> — couche DB, pas FS brut).</item>
    /// <item><b>Remédiation (écriture, GATE par config
    ///   <see cref="PluginConfiguration.AuditRemediationEnabled"/>)</b> :
    ///   <c>stop_session</c>, <c>trigger_task</c>, <c>send_message</c>.
    ///   Quand le flag est off, ces actions renvoient une erreur JSON — le LLM
    ///   doit alors recommander l'action dans son rapport sans l'exécuter.</item>
    /// </list>
    /// Ne lève jamais : une erreur renvoie <c>{"error":"..."}</c> pour ne pas
    /// casser la boucle de l'agent (même contrat que <see cref="GetEmbyInfoTool"/>).
    /// </summary>
    public class SystemAuditTool : ILlmTool
    {
        private readonly IServerApplicationHost _host;
        private readonly ILibraryManager _library;
        private readonly ISessionManager _sessions;
        private readonly ITaskManager _tasks;
        private readonly INotificationManager _notifications;
        private readonly IUserManager _users;
        private readonly ILogger _logger;

        public string Name => "system_audit";

        public string Description =>
            "Audite la santé du serveur Emby, du système hôte ET de la bibliothèque. Retourne du JSON minimal. " +
            "Actions (lecture seule) : server_info, system_config (configuration serveur : HTTPS, ports, " +
            "mode maintenance, cache path, rétention des logs — via IServerConfigurationManager, cross-OS), " +
            "active_sessions, scheduled_tasks, list_logs, inspect_log, transcode, host_metrics, " +
            "gpu_transcode, disk_storage, processes (détection d'orphelins ffmpeg + top processus RAM/CPU + " +
            "compteurs Emby), library_stats (comptes par type + liste des bibliothèques + état du scan), " +
            "missing_metadata (échantillonnage des items sans synopsis/image/genres pour un type). " +
            "Actions de REMÉDIATION (écriture, requièrent AuditRemediationEnabled activé en config) : " +
            "stop_session, trigger_task, send_message.";

        public string ArgumentsSchema => @"{
  ""action"": ""server_info | system_config | active_sessions | scheduled_tasks | list_logs | inspect_log | transcode | host_metrics | gpu_transcode | disk_storage | processes | library_stats | missing_metadata | stop_session | trigger_task | send_message"",
  ""limit"": ""(active_sessions / list_logs) nombre max de résultats (défaut 50)"",
  ""include_hidden"": ""(scheduled_tasks) true pour inclure les tâches cachées (défaut false)"",
  ""top_n"": ""(processes) nombre de processus à lister dans top_by_memory et top_by_cpu (défaut 8)"",
  ""type"": ""(missing_metadata) type d'item Emby à auditer (défaut Movie — ex. Series, Episode, MusicAlbum)"",
  ""sample_limit"": ""(missing_metadata) taille de l'échantillon à examiner (défaut 1000, max 5000) — les comptes sont estimés sur cet échantillon"",
  ""file"": ""(inspect_log) nom du fichier journal (nom seul, pas de chemin) — depuis list_logs"",
  ""tail"": ""(inspect_log, sans grep) nombre de lignes à lire depuis la fin (défaut 200, max 2000)"",
  ""grep"": ""(inspect_log, optionnel) regex .NET pour filtrer — active le mode grep : retourne les lignes matchantes + 'context' lignes autour (déduction), cap 50 matchs"",
  ""context"": ""(inspect_log, grep) lignes de contexte autour de chaque match (défaut 2, 0-10)"",
  ""include_transcode_size"": ""(disk_storage) true pour calculer la taille du dossier de transcodage (défaut false)"",
  ""session_id"": ""(stop_session) identifiant de la session à arrêter"",
  ""task_id"": ""(trigger_task) identifiant (Id) de la tâche planifiée à déclencher"",
  ""task_key"": ""(trigger_task) clé alternative (Key) de la tâche planifiée"",
  ""user_id|user_name"": ""(send_message) identifiant Guid OU nom de l'usager destinataire"",
  ""header"": ""(send_message) titre du message (défaut « Message »)"",
  ""text"": ""(send_message) corps du message — requis"",
  ""delivery"": ""(send_message) notification (défaut, inbox/cloche) | osd (toast à l'écran, requiert une session active)"",
  ""timeout_ms"": ""(send_message, osd) durée d'affichage du toast en ms (défaut 5000)""
}";

        private static readonly JsonSerializerOptions s_json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // Un double NaN/Infinity (ex. division par zéro sur un volume TotalSize==0,
            // ou un CurrentCpuUsage aberrant) ferait lever JsonSerializer. On autorise
            // les litéraux nommés : la sortie de s_json n'est JAMAIS consommée par un
            // parseur JSON strict — c'est du texte injecté dans le prompt (résultats
            // d'outils / digest déterministe). On préfère un « NaN » lisible par le LLM
            // à un plantage de tout l'audit.
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };

        public SystemAuditTool(
            IServerApplicationHost host,
            ILibraryManager library,
            ISessionManager sessions,
            ITaskManager tasks,
            INotificationManager notifications,
            IUserManager users,
            ILogger logger)
        {
            _host = host;
            _library = library;
            _sessions = sessions;
            _tasks = tasks;
            _notifications = notifications;
            _users = users;
            _logger = logger;
        }

        /// <summary>
        /// Exécute l'action demandée. async car plusieurs actions appellent
        /// des API Emby asynchrones (GetSystemInfo, SendPlaystateCommand,
        /// SendMessageCommand). Toute exception est captée → JSON d'erreur
        /// (ne casse jamais la boucle agent).
        /// </summary>
        public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
        {
            if (args.ValueKind != JsonValueKind.Object)
                args = default;

            string action = OptString(args, "action") ?? "server_info";
            try
            {
                string result;
                switch (action.ToLowerInvariant())
                {
                    case "server_info":      result = await ServerInfoAsync(ct).ConfigureAwait(false); break;
                    case "system_config":   result = SystemConfig(); break;
                    case "active_sessions":  result = ActiveSessions(args); break;
                    case "scheduled_tasks":   result = ScheduledTasks(args); break;
                    case "list_logs":         result = await ListLogsAsync(args, ct).ConfigureAwait(false); break;
                    case "inspect_log":       result = await InspectLogAsync(args, ct).ConfigureAwait(false); break;
                    case "transcode":         result = Transcode(); break;
                    case "host_metrics":      result = HostMetrics(); break;
                    case "gpu_transcode":     result = GpuTranscode(); break;
                    case "disk_storage":      result = await DiskStorageAsync(args, ct).ConfigureAwait(false); break;
                    case "processes":         result = Processes(args); break;
                    case "library_stats":    result = LibraryStats(); break;
                    case "missing_metadata": result = MissingMetadata(args); break;
                    case "stop_session":      result = await StopSessionAsync(args, ct).ConfigureAwait(false); break;
                    case "trigger_task":      result = TriggerTask(args); break;
                    case "send_message":      result = await SendMessageAsync(args, ct).ConfigureAwait(false); break;
                    default:
                        result = Err($"action inconnue : {action}");
                        break;
                }
                _logger?.Info("[LLM_AI] system_audit action={0} -> {1}", action, Truncate(result, 200));
                return result;
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("[LLM_AI] system_audit action={0} a levé : {1}", ex, action, ex.Message);
                return Err(ex.Message);
            }
        }

        // ------------------------------------------------------------------
        //  Inspection : télémétrie & configuration
        // ------------------------------------------------------------------

        /// <summary>
        /// Récupère <see cref="SystemInfo"/> (vue complète : version, OS, ports,
        /// chemins, redémarrage en attente, mise à jour). On passe
        /// <see cref="IPAddress.Loopback"/> comme remoteAddress (l'API n'a pas
        /// de surcharge sans adresse ; null peut lever). En cas d'échec, vue
        /// réduite depuis <see cref="IServerApplicationHost.GetPublicSystemInfo"/>
        /// + propriétés de l'hôte.
        /// </summary>
        private async Task<string> ServerInfoAsync(CancellationToken ct)
        {
            var info = await GetSystemInfoAsync(ct).ConfigureAwait(false);
            if (info != null)
            {
                var o = new
                {
                    name = _host.FriendlyName,
                    server_name = info.ServerName,
                    server_id = info.Id,
                    version = info.Version,
                    available_version = _host.AvailableVersion?.ToString(),
                    has_update_available = info.HasUpdateAvailable,
                    has_pending_restart = info.HasPendingRestart,
                    is_shutting_down = info.IsShuttingDown,
                    is_in_maintenance_mode = info.IsInMaintenanceMode,
                    operating_system = info.OperatingSystem,
                    operating_system_display = info.OperatingSystemDisplayName,
                    http_port = info.HttpServerPortNumber,
                    https_port = info.HttpsPortNumber,
                    supports_https = info.SupportsHttps,
                    local_address = info.LocalAddress,
                    wan_address = info.WanAddress,
                    program_data_path = info.ProgramDataPath,
                    log_path = info.LogPath,
                    cache_path = info.CachePath,
                    transcoding_temp_path = info.TranscodingTempPath,
                    internal_metadata_path = info.InternalMetadataPath,
                    items_by_name_path = info.ItemsByNamePath
                };
                return JsonSerializer.Serialize(o, s_json);
            }

            // Repli : GetSystemInfo indisponible — vue réduite depuis
            // GetPublicSystemInfo + chemins déduits des interfaces de chemins
            // (ResolveEmbyPathsAsync fournit program_data/log/transcoding_temp…).
            PublicSystemInfo pub = null;
            try { pub = await _host.GetPublicSystemInfo(ct).ConfigureAwait(false); } catch { }
            var p = await ResolveEmbyPathsAsync(ct).ConfigureAwait(false);
            string P(string k) { string v; p.TryGetValue(k, out v); return v; }
            var fallback = new
            {
                name = _host.FriendlyName,
                server_name = pub?.ServerName,
                server_id = pub?.Id,
                version = pub?.Version,
                available_version = _host.AvailableVersion?.ToString(),
                has_update_available = _host.HasUpdateAvailable,
                has_pending_restart = _host.HasPendingRestart,
                is_shutting_down = false,
                operating_system = Environment.OSVersion.VersionString,
                operating_system_display = RuntimeInformation.OSDescription,
                http_port = _host.HttpPort,
                https_port = _host.HttpsPort,
                supports_https = _host.EnableHttps,
                local_address = pub?.LocalAddress,
                wan_address = pub?.WanAddress,
                program_data_path = P("program_data"),
                log_path = P("log"),
                cache_path = P("cache"),
                transcoding_temp_path = P("transcoding_temp"),
                internal_metadata_path = P("internal_metadata"),
                note = "Repli normal : GetSystemInfo n'est pas disponible sur cette version " +
                       "Emby (lève une NRE connue). Les chemins système (program_data, logs, " +
                       "cache, transcodage, métadonnées) sont résolus via " +
                       "IServerConfigurationManager.ApplicationPaths ; seul le détail des " +
                       "interfaces réseau manque. Ce repli est COUVERT et ATTENDU — ne le " +
                       "signale PAS comme un défaut critique ni comme une action à investiguer."
            };
            return JsonSerializer.Serialize(fallback, s_json);
        }

        /// <summary>
        /// Expose la <b>configuration serveur</b> Emby (HTTPS, ports, mode maintenance,
        /// cache path, rétention des journaux, etc.) — le contenu sérialisé de
        /// <c>system.xml</c>, mais lu <b>in-process</b> via
        /// <see cref="MediaBrowser.Controller.Configuration.IServerConfigurationManager"/>
        /// (résolu depuis le host, cross-OS — aucun parsing XML, aucun chemin codé en
        /// dur). Comble les champs que <see cref="ServerInfoAsync"/> ne peut plus donner
        /// quand <c>GetSystemInfo</c> lève (Emby 4.9.x). Sérialise le DTO
        /// <c>ServerConfiguration</c> ; en repli (si la sérialisation globale échoue
        /// sur un champ complexe), on ne projette que les propriétés simples
        /// (string/bool/numérique) par réflexion. Lecture seule. Ne lève pas.
        /// </summary>
        private string SystemConfig()
        {
            object cfg = null;
            try
            {
                // TryResolve<T> est hérité de IApplicationHost (le host l'implémente).
                // Renvoie null si le service n'est pas enregistré.
                var mgr = _host.TryResolve<MediaBrowser.Controller.Configuration.IServerConfigurationManager>();
                cfg = mgr?.Configuration;
            }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] system_audit system_config résolution : {0}", ex.Message);
            }
            if (cfg == null)
                return Err("ServerConfiguration indisponible (IServerConfigurationManager non résolu).");

            // Sérialisation globale (DTO plat en principe) — si un champ complexe
            // fait planter, on retombe sur une projection des propriétés simples.
            try
            {
                return JsonSerializer.Serialize(cfg, s_json);
            }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] system_audit system_config sérialisation globale échouée " +
                    "(projection simple en repli) : {0}", ex.Message);
            }

            try
            {
                var t = cfg.GetType();
                var simple = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var p in t.GetProperties())
                {
                    if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                    object val;
                    try { val = p.GetValue(cfg, null); } catch { continue; }
                    // On ne garde que les scalaires (string/bool/char/numérique) + leurs
                    // listes de scalaires — on évite les objets complexes/cycliques.
                    if (val == null || IsScalar(val.GetType())
                        || IsScalarArray(val.GetType()))
                    {
                        simple[p.Name] = val;
                    }
                }
                return JsonSerializer.Serialize(simple, s_json);
            }
            catch (Exception ex)
            {
                return Err("system_config : échec de la projection simple : " + ex.Message);
            }
        }

        private static bool IsScalar(Type t) =>
            t.IsPrimitive || t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime)
            || t == typeof(DateTimeOffset) || t == typeof(TimeSpan) || t.IsEnum;

        private static bool IsScalarArray(Type t)
        {
            if (!t.IsArray) return false;
            var el = t.GetElementType();
            return el != null && (IsScalar(el) || el == typeof(object));
        }

        private string ActiveSessions(JsonElement args)
        {
            int limit = Math.Max(1, OptInt(args, "limit", 50));
            var list = (_sessions.Sessions ?? Enumerable.Empty<SessionInfo>()).ToList();
            var proj = list.Take(limit).Select(s => new
            {
                id = s.Id,
                user_name = s.UserName,
                client = s.Client,
                device_name = s.DeviceName,
                device_type = s.DeviceType,
                application_version = s.ApplicationVersion,
                last_activity_date = s.LastActivityDate,
                remote_end_point = s.RemoteEndPoint?.ToString(),
                supports_remote_control = s.SupportsRemoteControl,
                now_playing = s.NowPlayingItem != null
                    ? new { name = s.NowPlayingItem.Name, type = s.NowPlayingItem.Type }
                    : null,
                play_state = s.PlayState != null
                    ? new
                    {
                        is_paused = s.PlayState.IsPaused,
                        is_muted = s.PlayState.IsMuted,
                        position_ticks = s.PlayState.PositionTicks,
                        volume_level = s.PlayState.VolumeLevel,
                        play_method = s.PlayState.PlayMethod?.ToString()
                    }
                    : null,
                transcoding = ProjectTranscoding(s.TranscodingInfo)
            });
            return JsonSerializer.Serialize(new { total = list.Count, results = proj }, s_json);
        }

        private string ScheduledTasks(JsonElement args)
        {
            bool includeHidden = OptBool(args, "include_hidden", false);
            var workers = _tasks.ScheduledTasks ?? Array.Empty<IScheduledTaskWorker>();
            var proj = workers
                .Where(w => includeHidden || !IsHidden(w))
                .Select(w => new
                {
                    id = w.Id,
                    name = w.Name,
                    description = w.Description,
                    category = w.Category,
                    state = w.State.ToString(),
                    current_progress = w.CurrentProgress,
                    last_execution = w.LastExecutionResult != null
                        ? new
                        {
                            start_time_utc = w.LastExecutionResult.StartTimeUtc,
                            end_time_utc = w.LastExecutionResult.EndTimeUtc,
                            status = w.LastExecutionResult.Status.ToString(),
                            error_message = w.LastExecutionResult.ErrorMessage,
                            long_error_message = w.LastExecutionResult.LongErrorMessage
                        }
                        : null,
                    triggers = w.Triggers?.Select(t => new
                    {
                        type = t.Type,
                        time_of_day_ticks = t.TimeOfDayTicks,
                        interval_ticks = t.IntervalTicks,
                        day_of_week = t.DayOfWeek?.ToString()
                    }).ToArray()
                });
            return JsonSerializer.Serialize(new { results = proj }, s_json);
        }

        // ------------------------------------------------------------------
        //  Inspection : logs & flux
        // ------------------------------------------------------------------

        private async Task<string> ListLogsAsync(JsonElement args, CancellationToken ct)
        {
            var paths = await ResolveEmbyPathsAsync(ct).ConfigureAwait(false);
            string logDir;
            paths.TryGetValue("log", out logDir);
            if (string.IsNullOrWhiteSpace(logDir))
                return Err("chemin des journaux introuvable : GetSystemInfo et le repli " +
                    "IServerConfigurationManager.ApplicationPaths ont tous deux échoué.");

            int limit = Math.Max(1, OptInt(args, "limit", 50));
            var dir = new DirectoryInfo(logDir);
            if (!dir.Exists)
                return Err($"dossier de logs introuvable : {logDir}");

            var files = dir.GetFiles("*.txt")
                            .OrderByDescending(f => f.LastWriteTimeUtc)
                            .Take(limit)
                            .Select(f => new { name = f.Name, size = f.Length, date_modified = f.LastWriteTimeUtc })
                            .ToArray();
            return JsonSerializer.Serialize(new { path = logDir, total = files.Length, results = files }, s_json);
        }

        /// <summary>
        /// Lit la fin d'un fichier journal. <c>file</c> est réduit à son nom seul
        /// (<see cref="Path.GetFileName"/>) : tout slash/chemin est rejeté →
        /// traversal-safe (on ne lit que les enfants directs du dossier de logs).
        /// Lecture en flux (File.ReadLines) avec tampon circulaire des
        /// <c>tail</c> dernières lignes matchant <c>grep</c> (mémoire bornée,
        /// gère les gros fichiers). <c>tail</c> plafonné à 2000 lignes.
        /// </summary>
        private async Task<string> InspectLogAsync(JsonElement args, CancellationToken ct)
        {
            var paths = await ResolveEmbyPathsAsync(ct).ConfigureAwait(false);
            string logDir;
            paths.TryGetValue("log", out logDir);
            if (string.IsNullOrWhiteSpace(logDir))
                return Err("chemin des journaux introuvable : GetSystemInfo et le repli " +
                    "IServerConfigurationManager.ApplicationPaths ont tous deux échoué.");

            string file = OptString(args, "file");
            if (string.IsNullOrWhiteSpace(file))
                return Err("paramètre 'file' requis (nom du fichier — voir list_logs).");
            // Réduction anti-traversal : nom seul, sans séparateur.
            file = Path.GetFileName(file);
            if (string.IsNullOrWhiteSpace(file) || file.Contains('/') || file.Contains('\\') || file.Contains(".."))
                return Err("nom de fichier invalide.");

            // Whitelist d'extension : seuls les fichiers journaux sont lisibles.
            // Path.GetFileName confine déjà au répertoire des logs ; ici on
            // restreint en plus le *type* de fichier pour qu'un LLM ne puisse
            // pas lire un .db, .json de config ou autre déchet déposé dans le
            // dossier de logs. C'est la « whitelist FS » pratique : logs, rien d'autre.
            string ext = Path.GetExtension(file);
            if (!".txt".Equals(ext, StringComparison.OrdinalIgnoreCase) &&
                !".log".Equals(ext, StringComparison.OrdinalIgnoreCase))
                return Err($"extension non autorisée (journaux .txt/.log uniquement) : {file}");

            string path = Path.Combine(logDir, file);
            // Confinement canonique (double assurance au-delà de Path.GetFileName) :
            // on résout le chemin final et on vérifie qu'il reste sous LogPath,
            // pour bloquer toute échappatoire résiduelle (nom mal formé, etc.).
            string resolved = Path.GetFullPath(path);
            string logRoot = Path.GetFullPath(logDir);
            string logPrefix = (logRoot.EndsWith(Path.DirectorySeparatorChar) || logRoot.EndsWith(Path.AltDirectorySeparatorChar))
                ? logRoot : logRoot + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(logPrefix, StringComparison.OrdinalIgnoreCase))
                return Err("chemin hors du répertoire des journaux refusé.");

            if (!File.Exists(path))
                return Err($"fichier introuvable : {file}");

            long size = new FileInfo(path).Length;
            string grep = OptString(args, "grep");
            Regex grepRe = null;
            if (!string.IsNullOrWhiteSpace(grep))
            {
                try { grepRe = new Regex(grep, RegexOptions.CultureInvariant | RegexOptions.Compiled); }
                catch (Exception ex) { return Err($"regex 'grep' invalide : {ex.Message}"); }

                // Mode grep : on materialise les lignes (les journaux Emby sont
                // tournants et de taille bornée) pour pouvoir ramener, comme
                // l'app PHP de référence, chaque match + N lignes de contexte
                // (déduction, cap 50 matchs) — le contexte d'une erreur est ce
                // qu'un LLM auditant a besoin de voir, pas la seule ligne match.
                int context = Math.Min(Math.Max(0, OptInt(args, "context", 2)), 10);
                int maxMatches = 50;

                var lines = new List<string>();
                foreach (var line in ReadLogLines(path, ct))
                {
                    lines.Add(line);
                }
                int totalLines = lines.Count;

                var matched = new List<int>();
                for (int i = 0; i < totalLines; i++)
                {
                    if (grepRe.IsMatch(lines[i])) matched.Add(i);
                }
                int totalMatches = matched.Count;
                if (totalMatches == 0)
                {
                    return JsonSerializer.Serialize(new
                    {
                        file,
                        path,
                        size,
                        grep,
                        total_lines = totalLines,
                        matches = 0,
                        message = $"aucune ligne ne matche le filtre '{grep}' dans '{file}'."
                    }, s_json);
                }

                bool truncated = false;
                if (totalMatches > maxMatches)
                {
                    truncated = true;
                    matched = matched.GetRange(0, maxMatches);
                }

                // Fenêtres [idx-context, idx+context] dédoublonnées, ordre croissant.
                var added = new HashSet<int>();
                var results = new List<object>();
                foreach (int idx in matched)
                {
                    int start = Math.Max(0, idx - context);
                    int end = Math.Min(totalLines - 1, idx + context);
                    for (int i = start; i <= end; i++)
                    {
                        if (!added.Add(i)) continue;
                        results.Add(new { line = i + 1, content = lines[i], match = (i == idx) });
                    }
                }
                return JsonSerializer.Serialize(new
                {
                    file,
                    path,
                    size,
                    grep,
                    total_lines = totalLines,
                    matches = totalMatches,
                    showing_matches = matched.Count,
                    truncated,
                    context_lines = context,
                    results
                }, s_json);
            }

            // Mode tail (sans grep) : N dernières lignes, en flux (mémoire O(tail)).
            int tail = Math.Min(Math.Max(1, OptInt(args, "tail", 200)), 2000);
            var buf = new Queue<(int line, string content)>();
            int seen = 0;
            int lineNo = 0;
            foreach (var line in ReadLogLines(path, ct))
            {
                lineNo++;
                buf.Enqueue((lineNo, line));
                seen++;
                if (buf.Count > tail) buf.Dequeue();
            }
            var arr = buf.ToArray();
            return JsonSerializer.Serialize(new
            {
                file,
                path,
                size,
                total_lines = seen,
                showing_last = arr.Length,
                lines = arr.Select(p => new { line = p.line, content = p.content }).ToArray()
            }, s_json);
        }

        /// <summary>
        /// Transcodages ffmpeg actifs : sessions dont <c>TranscodingInfo</c> est
        /// non null. Détail complet (codecs, HW/SW, CPU, bitrate, completion,
        /// raisons de transcodage). C'est le « parse_ffmpeg_transcode ».
        /// </summary>
        private string Transcode()
        {
            var list = (_sessions.Sessions ?? Enumerable.Empty<SessionInfo>()).ToList();
            var proj = list
                .Where(s => s.TranscodingInfo != null)
                .Select(s => new
                {
                    session_id = s.Id,
                    user_name = s.UserName,
                    client = s.Client,
                    device_name = s.DeviceName,
                    now_playing = s.NowPlayingItem?.Name,
                    transcoding = ProjectTranscoding(s.TranscodingInfo)
                })
                .ToArray();
            return JsonSerializer.Serialize(new { total = proj.Length, results = proj }, s_json);
        }

        // ------------------------------------------------------------------
        //  Inspection : matériel & OS
        // ------------------------------------------------------------------

        /// <summary>
        /// Métriques hôte au mieux. Emby n'expose pas de service de métriques
        /// hôte : on utilise la BCL (process courant, GC, runtime, disques via
        /// <see cref="DriveInfo"/>) + l'état Emby (scan bibliothèque, CPU
        /// transcodage agrégé). L'utilisation GPU n'est pas disponible au
        /// niveau hôte — seulement par transcodage (actions transcode /
        /// gpu_transcode). <c>note</c> le précise au LLM.
        /// </summary>
        private string HostMetrics()
        {
            var proc = Process.GetCurrentProcess();
            var sessions = (_sessions.Sessions ?? Enumerable.Empty<SessionInfo>()).ToList();
            double aggCpu = 0;
            int activeTranscodes = 0;
            foreach (var s in sessions)
            {
                if (s.TranscodingInfo?.CurrentCpuUsage.HasValue == true)
                {
                    aggCpu += s.TranscodingInfo.CurrentCpuUsage.Value;
                    activeTranscodes++;
                }
            }

            // Uptime peut lever sur certaines plates-formes — on isole.
            string uptime = null;
            DateTime? startTime = null;
            try { startTime = proc.StartTime; uptime = (DateTime.Now - proc.StartTime).ToString(@"d\.hh\:mm\:ss", CultureInfo.InvariantCulture); }
            catch { }

            var o = new
            {
                machine_name = Environment.MachineName,
                os = Environment.OSVersion.VersionString,
                os_description = RuntimeInformation.OSDescription,
                os_architecture = RuntimeInformation.OSArchitecture.ToString(),
                process_architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                framework = RuntimeInformation.FrameworkDescription,
                processor_count = Environment.ProcessorCount,
                process_working_set_mb = Math.Round(proc.WorkingSet64 / 1048576.0, 1),
                process_private_memory_mb = Math.Round(proc.PrivateMemorySize64 / 1048576.0, 1),
                gc_total_memory_mb = Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1),
                process_cpu_time_seconds = Math.Round(proc.TotalProcessorTime.TotalSeconds, 1),
                process_start_time = startTime,
                process_uptime = uptime,
                library_scan_running = _library.IsScanRunning,
                active_sessions = sessions.Count,
                active_transcodes = activeTranscodes,
                aggregate_transcode_cpu_usage = Math.Round(aggCpu, 1),
                note = "Métriques hôte au mieux : Emby n'expose pas de service de métriques hôte. " +
                       "L'utilisation GPU n'est disponible QUE par transcodage (actions transcode / gpu_transcode)."
            };
            return JsonSerializer.Serialize(o, s_json);
        }

        /// <summary>
        /// État du transcodage matériel (GPU) : sessions utilisant un décodeur
        /// ou encodeur hardware, avec l'accélération (hw_accel) utilisée. Résumé
        /// du nombre de transcodages HW vs SW actifs.
        /// </summary>
        private string GpuTranscode()
        {
            var list = (_sessions.Sessions ?? Enumerable.Empty<SessionInfo>()).ToList();
            var hw = list
                .Where(s => s.TranscodingInfo != null
                            && (s.TranscodingInfo.VideoDecoderIsHardware || s.TranscodingInfo.VideoEncoderIsHardware))
                .Select(s => new
                {
                    session_id = s.Id,
                    user_name = s.UserName,
                    now_playing = s.NowPlayingItem?.Name,
                    decoder = s.TranscodingInfo.VideoDecoder,
                    decoder_hw_accel = s.TranscodingInfo.VideoDecoderHwAccel,
                    decoder_is_hardware = s.TranscodingInfo.VideoDecoderIsHardware,
                    encoder = s.TranscodingInfo.VideoEncoder,
                    encoder_hw_accel = s.TranscodingInfo.VideoEncoderHwAccel,
                    encoder_is_hardware = s.TranscodingInfo.VideoEncoderIsHardware,
                    bitrate = s.TranscodingInfo.Bitrate,
                    completion_percentage = s.TranscodingInfo.CompletionPercentage,
                    current_cpu_usage = s.TranscodingInfo.CurrentCpuUsage
                })
                .ToArray();
            int swCount = list.Count(s => s.TranscodingInfo != null
                                          && !s.TranscodingInfo.VideoDecoderIsHardware
                                          && !s.TranscodingInfo.VideoEncoderIsHardware);
            return JsonSerializer.Serialize(new
            {
                hardware_transcodes = hw.Length,
                software_transcodes = swCount,
                results = hw
            }, s_json);
        }

        // ------------------------------------------------------------------
        //  Processus OS (lecture seule — BCL pure, multiplateforme, pas de
        //  natif : noms/temps CPU/âge seulement, jamais les arguments ni le
        //  contenu → aucune fuite de secret, contournement FS exclu).
        // ------------------------------------------------------------------

        /// <summary>
        /// Diagnostic processus OS : détection d'orphelins <b>ffmpeg</b> +
        /// top processus par RAM/CPU + compteurs étendus du process Emby.
        /// </summary>
        /// <remarks>
        /// <b>Orphelins ffmpeg</b> — Emby lance un <c>ffmpeg</c> par
        /// transcodage ; si une session s'arrête mal (ou qu'Emby redémarre),
        /// des <c>ffmpeg</c> orphelins restent à consommer du CPU. La BCL
        /// n'expose pas <c>ParentProcessId</c> de façon portable, donc on
        /// détecte par <b>corrélation</b> : on compte les <c>ffmpeg</c>/
        /// <c>ffprobe</c> en cours et on croise avec les sessions de
        /// transcodage actives (<see cref="SessionInfo.TranscodingInfo"/>).
        /// Verdict honnête et borné :
        /// <list type="bullet">
        /// <item><c>ffmpeg_en_cours > 0 && transcodages_actifs == 0</c> →
        ///   <c>orphelins_probables</c> (signal net).</item>
        /// <item><c>ffmpeg_en_cours > transcodages_actifs</c> →
        ///   <c>orphelins_possibles</c> (un transcodage peut lancer 2 ffmpeg
        ///   — two-pass — donc le &gt; est un indice, pas une certitude).</item>
        /// <item>sinon → <c>ok</c>.</item>
        /// </list>
        /// On renvoie aussi chaque ffmpeg avec son CPU time et son âge pour
        /// que l'LLM juge la staleness. <see cref="Process.GetProcesses"/>
        /// renvoie noms/temps, pas d'arguments — aucune fuite.
        /// </remarks>
        private string Processes(JsonElement args)
        {
            int topN = Math.Min(Math.Max(1, OptInt(args, "top_n", 8)), 32);

            // Sessions de transcodage actives (pour la corrélation orphelins).
            int activeTranscodes = 0;
            try
            {
                var sessions = _sessions.Sessions ?? Enumerable.Empty<SessionInfo>();
                activeTranscodes = sessions.Count(s => s.TranscodingInfo != null);
            }
            catch { /* sessions indisponibles — corrélation moins fiable */ }

            // Snapshot unique de tous les processus avec métriques. On extrait
            // tout de suite (et on Dispose chaque Process) pour ne garder que
            // des tuples simples — aucune référence Process gardée en vie.
            var snap = new List<(int pid, string name, long ws, double cpu, string started, int threads)>();
            try
            {
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        string nm = p.ProcessName;
                        if (string.IsNullOrEmpty(nm)) continue;
                        long ws = 0; double cpu = 0; int th = 0; string started = null;
                        try { ws = p.WorkingSet64; } catch { }
                        try { cpu = p.TotalProcessorTime.TotalSeconds; } catch { }
                        try { th = p.Threads.Count; } catch { }
                        try { started = p.StartTime.ToString("o", CultureInfo.InvariantCulture); } catch { }
                        snap.Add((p.Id, nm, ws, cpu, started, th));
                    }
                    catch { /* processus inaccessible — on l'ignore */ }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] system_audit processes GetProcesses : {0}", ex.Message);
            }

            // ffmpeg / ffprobe en cours.
            var ffmpeg = snap
                .Where(x => x.name.StartsWith("ffmpeg", StringComparison.OrdinalIgnoreCase)
                         || x.name.StartsWith("ffprobe", StringComparison.OrdinalIgnoreCase))
                .Select(x => new
                {
                    pid = x.pid,
                    name = x.name,
                    cpu_time_seconds = Math.Round(x.cpu, 1),
                    started = x.started,
                    working_set_mb = Math.Round(x.ws / 1048576.0, 1),
                    threads = x.threads
                })
                .ToArray();
            int ffmpegCount = ffmpeg.Length;

            string verdict;
            if (ffmpegCount > 0 && activeTranscodes == 0)
                verdict = "orphelins_probables (ffmpeg en cours, 0 transcodage actif)";
            else if (ffmpegCount > activeTranscodes)
                verdict = "orphelins_possibles (plus de ffmpeg que de transcodages actifs — un transcodage peut en lancer 2)";
            else if (ffmpegCount == 0)
                verdict = "ok (aucun ffmpeg en cours)";
            else
                verdict = "ok";

            // Top processus par RAM puis par CPU (hors bruit système minime).
            var byRam = snap.OrderByDescending(x => x.ws).Take(topN)
                .Select(x => new { pid = x.pid, name = x.name, working_set_mb = Math.Round(x.ws / 1048576.0, 1), threads = x.threads })
                .ToArray();
            var byCpu = snap.OrderByDescending(x => x.cpu).Take(topN)
                .Select(x => new { pid = x.pid, name = x.name, cpu_time_seconds = Math.Round(x.cpu, 1) })
                .ToArray();

            // Compteurs étendus du process Emby (récupérés à part : on veut
            // threads + peak working set, qui ne sont pas dans host_metrics).
            int embyThreads = 0; long embyPeak = 0;
            try
            {
                var emby = Process.GetCurrentProcess();
                try { embyThreads = emby.Threads.Count; } catch { }
                try { embyPeak = emby.PeakWorkingSet64; } catch { }
            }
            catch { }

            return JsonSerializer.Serialize(new
            {
                active_transcode_sessions = activeTranscodes,
                ffmpeg_count = ffmpegCount,
                orphan_verdict = verdict,
                ffmpeg_processes = ffmpeg,
                emby_process = new
                {
                    threads = embyThreads,
                    peak_working_set_mb = Math.Round(embyPeak / 1048576.0, 1)
                },
                top_by_memory = byRam,
                top_by_cpu = byCpu
            }, s_json);
        }

        /// <summary>
        /// Espace disque des volumes montés (<see cref="DriveInfo.GetDrives"/>)
        /// + mapping des chemins Emby (program data, cache, transcodage, logs,
        /// métadonnées) vers leur volume. Option <c>include_transcode_size</c> :
        /// calcule la taille du dossier de transcodage (somme bornée).
        /// </summary>
        private async Task<string> DiskStorageAsync(JsonElement args, CancellationToken ct)
        {
            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .OrderByDescending(d => d.Name.Length)   // match préfixe le plus spécifique
                .Select(d =>
                {
                    // TotalSize peut valoir 0 (volume réseau/ram mal reporté) :
                    // division par zéro → +Infinity → JsonSerializer lève. On garde
                    // 0 dans ce cas plutôt que de planter l'audit entier.
                    long total = d.TotalSize;
                    long free = d.AvailableFreeSpace;
                    return new
                    {
                        name = d.Name,
                        format = d.DriveFormat,
                        type = d.DriveType.ToString(),
                        total_bytes = total,
                        free_bytes = free,
                        used_bytes = total - free,
                        used_pct = total > 0 ? Math.Round((double)(total - free) / total * 100, 1) : 0
                    };
                })
                .ToArray();

            // Mapping chemins Emby → volume (préfixe le plus spécifique).
            // ResolveEmbyPathsAsync : SystemInfo si dispo, sinon repli via les
            // interfaces de chemins (GetSystemInfo lève sur Emby 4.9.x).
            var paths = await ResolveEmbyPathsAsync(ct).ConfigureAwait(false);
            var pathMap = new List<object>();
            if (paths.Count > 0)
            {
                var allDrives = DriveInfo.GetDrives().Where(d => d.IsReady)
                    .OrderByDescending(d => d.Name.Length).ToArray();
                foreach (var kv in paths)
                {
                    if (string.IsNullOrWhiteSpace(kv.Value)) continue;
                    var drive = allDrives.FirstOrDefault(d =>
                        kv.Value.StartsWith(d.Name, StringComparison.OrdinalIgnoreCase));
                    pathMap.Add(new
                    {
                        label = kv.Key,
                        path = kv.Value,
                        drive = drive?.Name,
                        drive_free_bytes = drive?.AvailableFreeSpace
                    });
                }
            }

            long? transcodeSize = null;
            bool sizeTruncated = false;
            string transcodeTemp;
            paths.TryGetValue("transcoding_temp", out transcodeTemp);
            if (OptBool(args, "include_transcode_size", false)
                && !string.IsNullOrWhiteSpace(transcodeTemp))
            {
                var (sz, trunc) = BoundedDirSize(transcodeTemp);
                transcodeSize = sz;
                sizeTruncated = trunc;
            }

            return JsonSerializer.Serialize(new
            {
                drives,
                emby_paths = pathMap,
                transcode_temp_bytes = transcodeSize,
                transcode_temp_size_truncated = sizeTruncated
            }, s_json);
        }

        // ------------------------------------------------------------------
        //  Bibliothèque (lecture seule — couche DB ILibraryManager, pas FS brut)
        // ------------------------------------------------------------------
        // Types de contenu comptés par library_stats. On exclut les types
        // « structure » bruyants (Folder, CollectionFolder, UserView) : on veut
        // les contenus réels que l'usager comprend (films, séries, épisodes…).
        private static readonly string[] s_libTypes =
            { "Movie", "Series", "Episode", "MusicAlbum", "Audio", "Book", "MusicVideo", "Photo" };

        /// <summary>
        /// Vue d'ensemble de la bibliothèque : état du scan, liste des
        /// bibliothèques configurées (nom, type, emplacements) via
        /// <see cref="ILibraryManager.GetVirtualFolders"/>, et comptes globaux
        /// par type de contenu. Tout passe par la couche DB d'Emby — aucun
        /// accès FS brut, le confinement du système de fichier est préservé.
        /// Les comptes par type utilisent <see cref="ILibraryManager.GetItemsResult"/>
        /// avec Limit=1 + EnableTotalRecordCount (fetch léger : renvoie juste
        /// le total, pas les items). Idiom repris de GetEmbyInfoTool.Count.
        /// </summary>
        private string LibraryStats()
        {
            // Bibliothèques configurées (VirtualFolderInfo : Name/CollectionType/Locations).
            var libs = new List<object>();
            try
            {
                var folders = _library.GetVirtualFolders();
                if (folders != null)
                {
                    foreach (var f in folders)
                    {
                        if (f == null) continue;
                        libs.Add(new
                        {
                            name = f.Name,
                            collection_type = f.CollectionType,
                            locations = f.Locations ?? Array.Empty<string>()
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] system_audit library_stats GetVirtualFolders : {0}", ex.Message);
            }

            // Comptes globaux par type (fetch léger via TotalRecordCount).
            var counts = new Dictionary<string, int>();
            foreach (var t in s_libTypes)
                counts[t] = CountByType(t);

            return JsonSerializer.Serialize(new
            {
                scan_running = _library.IsScanRunning,
                libraries = libs,
                type_counts = counts
            }, s_json);
        }

        /// <summary>
        /// Échantillonne les items d'un type et signale ceux qui manquent de
        /// métadonnées clés : synopsis (<c>Overview</c>), image primaire
        /// (<c>PrimaryImagePath</c>) et genres. Indicateur de santé
        /// bibliothèque — ex. « 18 % des films sans synopsis ». Comme
        /// <see cref="InternalItemsQuery"/> n'expose pas de filtre « has
        /// overview », on récupère un échantillon borné (défaut 1000, max
        /// 5000) via <see cref="ILibraryManager.GetItemsResult"/> et on filtre
        /// en C#. Le total réel de la bibliothèque (TotalRecordCount) est
        /// renvoyé pour contextualiser l'échantillon et signaler s'il a été
        /// tronqué. Couche DB — aucun accès FS brut.
        /// </summary>
        private string MissingMetadata(JsonElement args)
        {
            string type = OptString(args, "type") ?? "Movie";
            int sampleLimit = Math.Min(Math.Max(50, OptInt(args, "sample_limit", 1000)), 5000);

            var q = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { type },
                Recursive = true,
                Limit = sampleLimit,
                EnableTotalRecordCount = true
            };

            var res = _library.GetItemsResult(q);
            if (res == null)
                return Err("GetItemsResult a retourné null.");
            var sample = res.Items ?? Array.Empty<BaseItem>();
            var total = res.TotalRecordCount;

            int examined = 0, missingOverview = 0, missingImage = 0, missingGenres = 0;
            var examples = new List<string>();
            foreach (var i in sample)
            {
                if (i == null) continue;
                examined++;
                bool noOverview = string.IsNullOrWhiteSpace(i.Overview);
                bool noImage = string.IsNullOrWhiteSpace(i.PrimaryImagePath);
                bool noGenres = i.Genres == null || i.Genres.Length == 0;
                if (noOverview) missingOverview++;
                if (noImage) missingImage++;
                if (noGenres) missingGenres++;
                if (noOverview && examples.Count < 10)
                    examples.Add(i.Name);
            }

            double pct = examined > 0 ? 100.0 / examined : 0;
            // Tronqué si l'échantillon atteint la limite ET qu'il y a plus
            // d'items en bibliothèque que ce qu'on a examiné. La comparaison
            // est « lifted » : si total est null, total > examined vaut false.
            bool capped = examined >= sampleLimit && total > examined;

            return JsonSerializer.Serialize(new
            {
                type,
                total_in_library = total,
                sampled = examined,
                sample_cap_reached = capped,
                missing_overview = new { count = missingOverview, pct = Math.Round(missingOverview * pct, 1) },
                missing_primary_image = new { count = missingImage, pct = Math.Round(missingImage * pct, 1) },
                missing_genres = new { count = missingGenres, pct = Math.Round(missingGenres * pct, 1) },
                examples_missing_overview = examples
            }, s_json);
        }

        /// <summary>Compte les items d'un type via TotalRecordCount (fetch 1).</summary>
        private int CountByType(string embyType)
        {
            try
            {
                var q = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { embyType },
                    Recursive = true,
                    Limit = 1,
                    EnableTotalRecordCount = true
                };
                return _library.GetItemsResult(q)?.TotalRecordCount ?? 0;
            }
            catch { return 0; }
        }

        // ------------------------------------------------------------------
        //  Rassemblement déterministe (mode AuditMode=deterministic)
        // ------------------------------------------------------------------
        // Arguments vides réutilisables : les actions read-only ont toutes des
        // défauts sensés quand un champ manque (OptInt/OptString retournent
        // null → défaut). Clone() garde le JsonElement vivant après dispose du
        // JsonDocument — pattern recommandé pour un JsonElement statique.
        private static readonly JsonElement s_emptyArgs = CreateEmptyArgs();
        private static JsonElement CreateEmptyArgs()
        {
            using var doc = JsonDocument.Parse("{}");
            return doc.RootElement.Clone();
        }

        /// <summary>
        /// Rassemble de façon déterministe (zéro LLM) l'ensemble des sondes
        /// read-only de l'audit et retourne un <b>digest</b> Markdown où chaque
        /// section <c>## nom</c> contient le JSON brut d'une sonde. Ce digest est
        /// ensuite fourni à un unique passage LLM sans outils (synthèse) pour le
        /// mode <c>AuditMode=deterministic</c> — conçu pour les modèles
        /// locaux/modestes (ex. gemma4) en retirant l'orchestration multi-outils.
        /// Inclut le tail (150 lignes) du journal le plus récent (best-effort,
        /// via <see cref="ListLogsAsync"/> puis <see cref="InspectLogAsync"/>).
        /// Aucune action de remédiation : ce mode est lecture-seule au sens
        /// exécution. Ne lève pas (erreurs capturées en sections d'erreur).
        /// </summary>
        public async System.Threading.Tasks.Task<string> GatherAuditDigestAsync(System.Threading.CancellationToken ct)
        {
            var sb = new StringBuilder();
            // Section locale : titre + JSON brut. On await les sondes async
            // inline (pas de sync-over-async) ; les sync passent leur résultat.
            void Section(string title, string json)
            {
                sb.Append("## ").AppendLine(title);
                sb.AppendLine(json ?? "null");
                sb.AppendLine();
            }

            // Wrap résilient : une sonde qui lève (ex. un double Infinity qui
            // s'échappe malgré s_json tolérant, ou un service Emby indisponible)
            // ne doit JAMAIS faire planter tout l'audit — on l'enregistre comme
            // section d'erreur et on continue les autres sondes. Les sondes sont
            // appelées HORS du try/catch de ExecuteAsync (rassemblement direct),
            // d'où la nécessité de ce garde ici. OperationCanceledException est
            // propagée (annulation = arrêt volontaire, pas une erreur de sonde).
            async System.Threading.Tasks.Task SectionAsync(
                string title, System.Threading.Tasks.Task<string> probe)
            {
                try { Section(title, await probe.ConfigureAwait(false)); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger?.Warn("[LLM_AI] system_audit digest sonde « {0} » a échoué : {1}", title, ex.Message);
                    Section(title, Err("digest: " + title + " a échoué : " + ex.Message));
                }
            }
            void SectionSync(string title, Func<string> probe)
            {
                try { Section(title, probe()); }
                catch (Exception ex)
                {
                    _logger?.Warn("[LLM_AI] system_audit digest sonde « {0} » a échoué : {1}", title, ex.Message);
                    Section(title, Err("digest: " + title + " a échoué : " + ex.Message));
                }
            }

            // Sondes (await inline pour éviter le sync-over-async).
            await SectionAsync("server_info", ServerInfoAsync(ct)).ConfigureAwait(false);
            SectionSync("system_config", () => SystemConfig());
            SectionSync("host_metrics", () => HostMetrics());
            SectionSync("processes", () => Processes(s_emptyArgs));
            await SectionAsync("disk_storage", DiskStorageAsync(s_emptyArgs, ct)).ConfigureAwait(false);
            SectionSync("active_sessions", () => ActiveSessions(s_emptyArgs));
            SectionSync("scheduled_tasks", () => ScheduledTasks(s_emptyArgs));
            SectionSync("transcode", () => Transcode());
            SectionSync("gpu_transcode", () => GpuTranscode());
            SectionSync("library_stats", () => LibraryStats());
            SectionSync("missing_metadata", () => MissingMetadata(s_emptyArgs));

            string logs = null;
            try { logs = await ListLogsAsync(s_emptyArgs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] system_audit digest sonde « list_logs » a échoué : {0}", ex.Message);
                logs = Err("digest: list_logs a échoué : " + ex.Message);
            }
            Section("list_logs", logs);

            // Tail du journal le plus récent (best-effort) : on extrait le 1er
            // nom (list_logs trie par LastWriteTimeUtc desc) puis inspect_log.
            try
            {
                string newest = null;
                using (var doc = JsonDocument.Parse(logs))
                {
                    if (doc.RootElement.TryGetProperty("results", out var arr)
                        && arr.GetArrayLength() > 0
                        && arr[0].TryGetProperty("name", out var nameEl))
                        newest = nameEl.GetString();
                }
                if (!string.IsNullOrWhiteSpace(newest))
                {
                    var inspArgs = JsonSerializer.SerializeToElement(new { file = newest, tail = 150 }, s_json);
                    await SectionAsync("inspect_log (journal le plus récent, tail 150)",
                        InspectLogAsync(inspArgs, ct)).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Section("inspect_log", Err("digest: inspect_log a échoué : " + ex.Message));
            }

            return sb.ToString();
        }

        // ------------------------------------------------------------------
        //  Remédiation (écriture) — GATE : AuditRemediationEnabled
        // ------------------------------------------------------------------

        /// <summary>true si la remédiation est activée en config (gate unique).</summary>
        private static bool RemediationEnabled =>
            Plugin.Instance?.Configuration?.AuditRemediationEnabled == true;

        /// <summary>Message d'erreur standard quand la remédiation est désactivée.</summary>
        private static string RemediationDisabledErr() =>
            JsonSerializer.Serialize(new
            {
                error = "remediation désactivée — activez « AuditRemediationEnabled » dans la config du plugin " +
                        "pour autoriser stop_session / trigger_task / send_message. Recommande l'action dans le rapport au lieu de l'exécuter."
            }, s_json);

        /// <summary>
        /// Arrête la lecture d'une session (envoie un PlaystateCommand Stop).
        /// N'agit pas sur la session elle-même (Emby n'expose pas de fermeture
        /// de session propre en in-process) : arrête le transcodage/lecture en
        /// cours — c'est l'action utile d'un audit (« ce stream consomme trop »).
        /// </summary>
        private async Task<string> StopSessionAsync(JsonElement args, CancellationToken ct)
        {
            if (!RemediationEnabled) return RemediationDisabledErr();
            string sessionId = OptString(args, "session_id");
            if (string.IsNullOrWhiteSpace(sessionId))
                return Err("paramètre 'session_id' requis (voir active_sessions).");

            var session = (_sessions.Sessions ?? Enumerable.Empty<SessionInfo>())
                .FirstOrDefault(s => s.Id == sessionId);
            if (session == null)
                return Err($"session introuvable : {sessionId}");

            await _sessions.SendPlaystateCommand(null, sessionId,
                new PlaystateRequest { Command = PlaystateCommand.Stop }, ct).ConfigureAwait(false);

            return JsonSerializer.Serialize(new
            {
                stopped = true,
                session_id = sessionId,
                user_name = session.UserName,
                now_playing = session.NowPlayingItem?.Name
            }, s_json);
        }

        /// <summary>
        /// Déclenche une tâche planifiée (la met en file d'exécution via
        /// <see cref="ITaskManager.QueueScheduledTask(IScheduledTask, TaskOptions)"/>).
        /// La tâche est repérée par <c>task_id</c> (worker.Id) ou <c>task_key</c>
        /// (ScheduledTask.Key).
        /// </summary>
        private string TriggerTask(JsonElement args)
        {
            if (!RemediationEnabled) return RemediationDisabledErr();
            string taskId = OptString(args, "task_id");
            string taskKey = OptString(args, "task_key");
            if (string.IsNullOrWhiteSpace(taskId) && string.IsNullOrWhiteSpace(taskKey))
                return Err("paramètre 'task_id' ou 'task_key' requis (voir scheduled_tasks).");

            var worker = MatchTask(taskId, taskKey);
            if (worker == null)
                return Err($"tâche introuvable (task_id={taskId}, task_key={taskKey}).");

            _tasks.QueueScheduledTask(worker.ScheduledTask, new TaskOptions());
            return JsonSerializer.Serialize(new
            {
                queued = true,
                task_id = worker.Id,
                name = worker.Name,
                key = worker.ScheduledTask?.Key
            }, s_json);
        }

        /// <summary>
        /// Envoie un message à un usager. Deux modes de livraison :
        /// <list type="bullet">
        /// <item><c>notification</c> (défaut) : notification Emby inbox/cloche via
        ///   <see cref="INotificationManager.SendNotification"/> — fiable, livré
        ///   même sans session active (chemin éprouvé par <c>LlmScheduledTask</c>).</item>
        /// <item><c>osd</c> : toast à l'écran via
        ///   <see cref="ISessionManager.SendMessageCommand"/> sur chaque session
        ///   active de l'usager — requiert une session live.</item>
        /// </list>
        /// L'usager est résolu par Guid (<c>user_id</c>) ou par nom
        /// (<c>user_name</c>).
        /// </summary>
        private async Task<string> SendMessageAsync(JsonElement args, CancellationToken ct)
        {
            if (!RemediationEnabled) return RemediationDisabledErr();
            string recipient = OptString(args, "user_id") ?? OptString(args, "user_name");
            if (string.IsNullOrWhiteSpace(recipient))
                return Err("paramètre 'user_id' ou 'user_name' requis.");

            string header = OptString(args, "header") ?? "Message";
            string text = OptString(args, "text");
            if (string.IsNullOrWhiteSpace(text))
                return Err("paramètre 'text' requis (corps du message).");

            var users = ResolveUsers(recipient);
            if (users.Count == 0)
                return Err($"usager introuvable : {recipient}");

            string delivery = (OptString(args, "delivery") ?? "notification").ToLowerInvariant();
            int timeoutMs = OptInt(args, "timeout_ms", 5000);

            if (delivery == "osd")
            {
                int reached = 0;
                var sessions = (_sessions.Sessions ?? Enumerable.Empty<SessionInfo>()).ToList();
                foreach (var u in users)
                {
                    string uid = u.Id.ToString();
                    foreach (var s in sessions.Where(x => string.Equals(x.UserId, uid, StringComparison.OrdinalIgnoreCase)))
                    {
                        try
                        {
                            await _sessions.SendMessageCommand(null, s.Id,
                                new MessageCommand { Header = header, Text = text, TimeoutMs = timeoutMs },
                                ct).ConfigureAwait(false);
                            reached++;
                        }
                        catch (Exception ex)
                        {
                            _logger?.Warn("[LLM_AI] system_audit send_message(osd) session {0} : {1}", s.Id, ex.Message);
                        }
                    }
                }
                return JsonSerializer.Serialize(new
                {
                    delivery = "osd",
                    recipients = users.Count,
                    sessions_reached = reached,
                    note = reached == 0 ? "Aucune session active — aucun toast envoyé. Utilise delivery=notification pour une livraison persistante." : null
                }, s_json);
            }

            // notification (défaut) — chemin inbox/cloche éprouvé.
            int sent = 0;
            var now = DateTimeOffset.UtcNow;
            foreach (var u in users)
            {
                try
                {
                    var req = new NotificationRequest
                    {
                        Title = header,
                        Description = text,
                        Date = now,
                        Severity = LogSeverity.Info,
                        User = u
                    };
                    _notifications.SendNotification(req);
                    sent++;
                }
                catch (Exception ex)
                {
                    _logger?.Warn("[LLM_AI] system_audit send_message(notification) « {0} » : {1}", u.Name, ex.Message);
                }
            }
            return JsonSerializer.Serialize(new { delivery = "notification", sent, recipients = users.Count }, s_json);
        }

        // ------------------------------------------------------------------
        //  Helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Récupère <see cref="SystemInfo"/> (remoteAddress = Loopback, l'API
        /// n'a pas de surcharge sans adresse). Null si indisponible.
        /// </summary>
        // SystemInfo mis en cache par run : GetSystemInfo lève une NRE sur certaines
        // versions d'Emby (4.9.x observé), et on l'appelle depuis plusieurs sondes
        // (server_info, list_logs, inspect_log, disk_storage). On tente une seule
        // fois — l'échec est définitif pour ce run et les sondes dégradent proprement
        // via les replis ci-dessous. OperationCanceledException n'est PAS cachée.
        private SystemInfo _cachedSystemInfo;
        private bool _systemInfoTried;

        private async Task<SystemInfo> GetSystemInfoAsync(CancellationToken ct)
        {
            if (_systemInfoTried) return _cachedSystemInfo;
            _systemInfoTried = true;
            try
            {
                _cachedSystemInfo = await _host.GetSystemInfo(IPAddress.Loopback, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { _systemInfoTried = false; throw; }
            catch (Exception ex)
            {
                // Repli attendu et COUVERT : GetSystemInfo lève une NRE à
                // l'intérieur d'Emby sur certains hôtes (observé sur Windows 11 —
                // pas sur Linux avec la même version Emby ; la cause est dans le
                // code d'Emby, on ne peut pas la corriger côté plugin). On log en
                // Info (pas Warn) : le repli via IServerConfigurationManager.
                // ApplicationPaths résout tous les chemins système utilisés par
                // les sondes ; seule la liste des interfaces réseau manque, ce
                // qui n'impacte aucun diagnostic. Voir ServerInfoAsync/ResolveEmbyPathsAsync.
                _logger?.Info("[LLM_AI] system_audit GetSystemInfo indisponible (repli couvert " +
                    "via IServerConfigurationManager.ApplicationPaths) : {0}", ex.Message);
                _cachedSystemInfo = null;
            }
            return _cachedSystemInfo;
        }

        /// <summary>
        /// Résout les chemins Emby (log, program_data, cache, transcoding_temp,
        /// internal_metadata, root_folder…) — priorité à <see cref="SystemInfo"/>
        /// (vue complète), puis repli par <b>réflexion par nom</b> sur le type concret
        /// du host quand GetSystemInfo lève. Le host implémente bien une interface de
        /// chemins, mais son namespace/type exact varie selon la version d'Emby (le
        /// cast statique vers <c>IApplicationPaths</c>/<c>IServerApplicationPaths</c>
        /// a échoué sur Emby 4.9.x) : on lit donc les propriétés par leur nom stable
        /// (<c>ProgramDataPath</c>, <c>TranscodingTempPath</c>…) sur le type concret et
        /// ses interfaces. Le chemin des journaux n'est exposé par AUCUNE interface
        /// connue : on le déduit en convention Emby comme
        /// <c>&lt;ProgramDataPath&gt;/logs</c>. Ne lève pas.
        /// </summary>
        private async Task<Dictionary<string, string>> ResolveEmbyPathsAsync(CancellationToken ct)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var info = await GetSystemInfoAsync(ct).ConfigureAwait(false);
            if (info != null)
            {
                map["log"] = info.LogPath;
                map["program_data"] = info.ProgramDataPath;
                map["cache"] = info.CachePath;
                map["transcoding_temp"] = info.TranscodingTempPath;
                map["internal_metadata"] = info.InternalMetadataPath;
                map["items_by_name"] = info.ItemsByNamePath;
                return map;
            }

            // Repli : le host n'expose pas ProgramDataPath en propriété publique directe
            // (implémentation explicite d'interface — GetProperty par nom échoue sur le
            // host, et il n'implémente même pas l'IApplicationPaths qu'on ciblait). En
            // revanche, le gestionnaire de config serveur (résolu depuis le host, comme
            // dans system_config) expose .ApplicationPaths — l'objet dédié des chemins
            // qui, lui, implémente l'interface des chemins. On lit les chemins par
            // réflexion par nom sur CET objet (noms stables cross-version, cross-OS).
            object paths = null;
            try
            {
                var mgr = _host.TryResolve<MediaBrowser.Controller.Configuration.IServerConfigurationManager>();
                paths = mgr?.ApplicationPaths;
            }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] system_audit ResolveEmbyPaths (repli) : {0}", ex.Message);
            }

            string GetPathProp(string name)
            {
                if (paths == null) return null;
                try
                {
                    var t = paths.GetType();
                    var pi = t.GetProperty(name);
                    if (pi == null)
                    {
                        foreach (var it in t.GetInterfaces())
                        {
                            pi = it.GetProperty(name);
                            if (pi != null) break;
                        }
                    }
                    if (pi == null) return null;
                    return pi.GetValue(paths, null) as string;
                }
                catch { return null; }
            }

            map["program_data"] = GetPathProp("ProgramDataPath");
            map["cache"] = GetPathProp("CachePath");
            map["transcoding_temp"] = GetPathProp("TranscodingTempPath");
            map["internal_metadata"] = GetPathProp("InternalMetadataPath");
            map["items_by_name"] = GetPathProp("ItemsByNamePath");
            map["root_folder"] = GetPathProp("RootFolderPath");
            // LogPath n'est pas exposé par les interfaces de chemins : convention Emby.
            string log = GetPathProp("LogPath");
            if (!string.IsNullOrWhiteSpace(log))
                map["log"] = log;
            else if (!string.IsNullOrWhiteSpace(map["program_data"]))
                map["log"] = Path.Combine(map["program_data"], "logs");

            return map;
        }

        /// <summary>Projection du <see cref="TranscodingInfo"/> d'une session.</summary>
        private static object ProjectTranscoding(TranscodingInfo t)
        {
            if (t == null) return null;
            return new
            {
                is_video_direct = t.IsVideoDirect,
                is_audio_direct = t.IsAudioDirect,
                video_codec = t.VideoCodec,
                audio_codec = t.AudioCodec,
                container = t.Container,
                bitrate = t.Bitrate,
                video_bitrate = t.VideoBitrate,
                audio_bitrate = t.AudioBitrate,
                width = t.Width,
                height = t.Height,
                framerate = t.Framerate,
                audio_channels = t.AudioChannels,
                completion_percentage = t.CompletionPercentage,
                current_cpu_usage = t.CurrentCpuUsage,
                average_cpu_usage = t.AverageCpuUsage,
                video_decoder = t.VideoDecoder,
                video_decoder_is_hardware = t.VideoDecoderIsHardware,
                video_decoder_hw_accel = t.VideoDecoderHwAccel,
                video_encoder = t.VideoEncoder,
                video_encoder_is_hardware = t.VideoEncoderIsHardware,
                video_encoder_hw_accel = t.VideoEncoderHwAccel,
                transcode_reasons = t.TranscodeReasons?.Select(r => r.ToString()).ToArray()
            };
        }

        /// <summary>Une tâche planifiée est cachée si elle implémente IConfigurableScheduledTask.IsHidden.</summary>
        private static bool IsHidden(IScheduledTaskWorker w)
        {
            try { return (w.ScheduledTask as IConfigurableScheduledTask)?.IsHidden ?? false; }
            catch { return false; }
        }

        /// <summary>Repère une tâche par Id (worker.Id) ou Key (ScheduledTask.Key).</summary>
        private IScheduledTaskWorker MatchTask(string taskId, string taskKey)
        {
            var workers = _tasks.ScheduledTasks ?? Array.Empty<IScheduledTaskWorker>();
            foreach (var w in workers)
            {
                if (!string.IsNullOrWhiteSpace(taskId)
                    && string.Equals(w.Id, taskId, StringComparison.OrdinalIgnoreCase))
                    return w;
                if (!string.IsNullOrWhiteSpace(taskKey)
                    && string.Equals(w.ScheduledTask?.Key, taskKey, StringComparison.OrdinalIgnoreCase))
                    return w;
            }
            return null;
        }

        /// <summary>
        /// Résout des usagers par identifiant (Guid) OU nom (insensible casse).
        /// On liste puis on match — robuste quel que soit le type de User.Id.
        /// </summary>
        private List<User> ResolveUsers(string recipient)
        {
            var result = new List<User>();
            try
            {
                var all = _users.GetUserList(new UserQuery()) ?? Array.Empty<User>();
                foreach (var u in all)
                {
                    if (u == null) continue;
                    if (string.Equals(u.Name, recipient, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(u.Id.ToString(), recipient, StringComparison.OrdinalIgnoreCase))
                        result.Add(u);
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] system_audit ResolveUsers : {0}", ex.Message);
            }
            return result;
        }

        /// <summary>
        /// Taille bornée d'un dossier (somme récursive des fichiers). Plafond
        /// de 200 000 fichiers pour limiter le coût — renvoie <c>truncated</c>
        /// si le plafond est atteint. Utilisé par disk_storage pour le dossier
        /// de transcodage.
        /// </summary>
        private static (long size, bool truncated) BoundedDirSize(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                return (0, false);
            long size = 0;
            bool truncated = false;
            int count = 0;
            const int MAX_FILES = 200000;
            try
            {
                var stack = new Stack<string>();
                stack.Push(dir);
                while (stack.Count > 0)
                {
                    var current = stack.Pop();
                    string[] subdirs = null;
                    try { subdirs = Directory.GetDirectories(current); } catch { }
                    if (subdirs != null)
                        foreach (var s in subdirs) stack.Push(s);

                    string[] files = null;
                    try { files = Directory.GetFiles(current); } catch { }
                    if (files == null) continue;
                    foreach (var f in files)
                    {
                        try { size += new FileInfo(f).Length; } catch { }
                        if (++count >= MAX_FILES) { truncated = true; return (size, truncated); }
                    }
                }
            }
            catch { }
            return (size, truncated);
        }

        /// <summary>
        /// Énumère les lignes d'un fichier journal en lecture partagée. Sur
        /// Windows, le logger Emby garde le fichier courant (ex. embyserver.txt)
        /// ouvert en écriture exclusive : <see cref="File.ReadLines(string)"/>
        /// échoue alors avec « The process cannot access the file ... because it
        /// is being used by another process » (alors que sur Linux le logger
        /// ouvre en partage de lecture — d'où le comportement divergent).
        /// On ouvre donc en <see cref="FileShare.ReadWrite"/> |
        /// <see cref="FileShare.Delete"/> pour relire le journal actif même sous
        /// la plume du logger (sur Linux ce partage n'est pas requis mais reste
        /// inoffensif). Lecture en flux, mémoire O(tampon lecture) — adaptée
        /// aux journaux de grande taille. Le verrouillage du FS est préservé :
        /// lecture seule, aucun droit d'écriture demandé.
        /// </summary>
        private static IEnumerable<string> ReadLogLines(string path, CancellationToken ct)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                          FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.UTF8, true, 4096))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    ct.ThrowIfCancellationRequested();
                    yield return line;
                }
            }
        }

        private static string Err(string msg) => JsonSerializer.Serialize(new { error = msg }, s_json);

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s.Substring(0, max) + "…");

        // --- Lecture optionnelle des arguments JSON -----------------------

        private static string OptString(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object) return null;
            return e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        }

        private static int OptInt(JsonElement e, string name, int dflt)
        {
            if (e.ValueKind != JsonValueKind.Object) return dflt;
            return e.TryGetProperty(name, out var p) && p.TryGetInt32(out var v) ? v : dflt;
        }

        private static bool OptBool(JsonElement e, string name, bool dflt)
        {
            if (e.ValueKind != JsonValueKind.Object) return dflt;
            if (!e.TryGetProperty(name, out var p)) return dflt;
            if (p.ValueKind == JsonValueKind.True) return true;
            if (p.ValueKind == JsonValueKind.False) return false;
            return dflt;
        }
    }
}

#pragma warning restore CS0618