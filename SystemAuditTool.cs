using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
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
using MediaBrowser.Controller.Playlists;
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
            "security_check (sécurité : mots de passe des comptes/admins, accès distant et HTTPS, " +
            "UPnP, en-têtes proxy, preuves d'accès externe — sessions actives ET historique des appareils " +
            "avec IP publique ; si un accès externe est observé, les avertissements sont rehaussés critique " +
            "— constats severity critique/avertissement/ok avec correctif), " +
            "upnp_check (interroge le routeur en UPnP : passerelle détectée ? IP WAN externe ? table de " +
            "redirection de ports — UN MAPPING VERS LE PORT EMBY 8096/8920 EST CRITIQUE ; lecture seule, " +
            "n'ajoute/supprime JAMAIS de mapping ; attention : les redirections manuelles du routeur sont " +
            "invisibles pour l'UPnP, seul un test externe les voit), " +
            "active_sessions, scheduled_tasks, list_logs, inspect_log, transcode, host_metrics, " +
            "gpu_transcode, disk_storage, processes (détection d'orphelins ffmpeg + top processus RAM/CPU + " +
            "compteurs Emby), library_stats (comptes par type + liste des bibliothèques + état du scan), " +
            "missing_metadata (échantillonnage des items sans synopsis/image/genres pour un type), " +
            "ratings_check (hygiène des cotes : OfficialRating des films/séries et de l'EPG comparés à la " +
            "table parentale intégrée du serveur — cotes non reconnues = limite parentale aveugle sur ces " +
            "items, avertissement + conseil de normalisation ; marqueurs « non coté » comptés à part). " +
            "Actions de REMÉDIATION (écriture, requièrent AuditRemediationEnabled activé en config) : " +
            "stop_session, trigger_task, send_message.";

        public string ArgumentsSchema => @"{
  ""action"": ""server_info | system_config | security_check | upnp_check | active_sessions | scheduled_tasks | list_logs | inspect_log | transcode | host_metrics | gpu_transcode | disk_storage | processes | library_stats | missing_metadata | ratings_check | stop_session | trigger_task | send_message"",
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
                    case "security_check":   result = SecurityCheck(); break;
                    case "upnp_check":       result = await UpnpCheckAsync(ct).ConfigureAwait(false); break;
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
                    case "ratings_check":    result = RatingsCheck(); break;
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

        /// <summary>
        /// Hygiène des cotes (action <c>ratings_check</c>, lecture seule) :
        /// compare les cotes <c>OfficialRating</c> des films/séries de la
        /// bibliothèque ET des programmes EPG à la table parentale intégrée
        /// du serveur (<c>ILocalizationManager.GetParentalRatings()</c> — la
        /// même liste que le menu de limite parentale du dashboard).
        /// <para><b>Pourquoi</b> : une cote non reconnue rend la limite
        /// parentale (<c>MaxParentalRating</c>) aveugle sur cet item — le
        /// filtrage natif compare des scores numériques, une cote hors table
        /// n'a pas de score. Les fournisseurs (TMDB/TVDB, guide EPG) livrent
        /// des formats nationaux hétérogènes : sans normalisation, la cote
        /// est « n'importe quoi ». Bibliothèque non alignée → avertissement
        /// + conseil de normalisation (ex. Classification Mapper). Les
        /// marqueurs « non coté » (NR, Unrated…) sont comptés à part :
        /// légitimes, couverts par la policy <c>BlockUnratedItems</c>.
        /// L'EPG n'est JAMAIS passé à la normalisation de la bibliothèque
        /// (programmes transitoires, refetchés au guide) : census séparé en
        /// info, la comparaison y est indicative.</para>
        /// <para>Fail-open : table ou bibliothèque illisible → JSON d'erreur,
        /// jamais une exception (ne casse pas la boucle agent).</para>
        /// </summary>
        private string RatingsCheck()
        {
            // Table parentale du serveur (même source que le menu du dashboard).
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var loc = _host?.TryResolve<MediaBrowser.Model.Globalization.ILocalizationManager>();
                foreach (var r in loc?.GetParentalRatings() ?? Array.Empty<MediaBrowser.Model.Entities.ParentalRating>())
                    if (!string.IsNullOrWhiteSpace(r?.Name)) known.Add(r.Name.Trim());
            }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] ratings_check : table parentale illisible : {0}", ex.Message);
                return Err("ratings_check : table parentale du serveur illisible : " + ex.Message);
            }
            if (known.Count == 0)
                return Err("ratings_check : la table parentale du serveur est vide.");

            // Marqueurs « non coté » légitimes : hors table, mais pas du
            // désordre (pas de score à leur donner — BlockUnratedItems couvre).
            var unratedMarkers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "NR", "Not Rated", "Unrated", "N/A" };

            // --- Bibliothèque (films + séries) --------------------------------
            int libTotal = 0, libRecognized = 0, libUnrated = 0;
            var unrecognizedCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var unrecognizedExamples = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var items = _library.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Movie", "Series" },
                    Recursive = true,
                    EnableTotalRecordCount = false
                }) ?? Array.Empty<BaseItem>();
                libTotal = items.Length;
                foreach (var i in items)
                {
                    var rating = i?.OfficialRating;
                    if (string.IsNullOrWhiteSpace(rating)) { libUnrated++; continue; }
                    var r = rating.Trim();
                    if (unratedMarkers.Contains(r)) { libUnrated++; continue; }
                    if (known.Contains(r)) { libRecognized++; continue; }
                    unrecognizedCounts[r] = unrecognizedCounts.TryGetValue(r, out var c) ? c + 1 : 1;
                    if (!unrecognizedExamples.ContainsKey(r)) unrecognizedExamples[r] = i?.Name ?? "?";
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] ratings_check : lecture bibliothèque impossible : {0}", ex.Message);
                return Err("ratings_check : lecture bibliothèque impossible : " + ex.Message);
            }

            int libUnrecognized = unrecognizedCounts.Values.Sum();
            string libSeverity = libUnrecognized == 0 ? "ok" : "avertissement";
            string libAdvice = libUnrecognized == 0
                ? null
                : "Utilisez un outil de normalisation des cotes (ex. plugin Classification Mapper) pour " +
                  "aligner les cotes de la bibliothèque sur la table parentale du serveur " +
                  "(tableau de bord → contrôle parental) — sans cela, toute limite parentale est " +
                  "aveugle sur ces items.";

            // Top valeurs non reconnues (détail + 1er exemple d'item).
            var topUnrecognized = unrecognizedCounts
                .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .Select(kv => new { value = kv.Key, count = kv.Value, example = unrecognizedExamples.TryGetValue(kv.Key, out var n) ? n : "?" })
                .ToList();

            // --- EPG (cotes brutes du fournisseur de guide) -------------------
            object epg = null;
            try
            {
                var liveTv = _host?.TryResolve<MediaBrowser.Controller.LiveTv.ILiveTvManager>();
                var programs = liveTv?.GetPrograms(new InternalItemsQuery
                {
                    EnableTotalRecordCount = false
                })?.Items ?? Array.Empty<BaseItemDto>();
                int epgTotal = 0, epgRecognized = 0, epgUnrated = 0;
                var epgUnrecognized = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in programs)
                {
                    var rating = p?.OfficialRating;
                    if (string.IsNullOrWhiteSpace(rating)) { epgUnrated++; continue; }
                    var r = rating.Trim();
                    epgTotal++;
                    if (unratedMarkers.Contains(r)) { epgUnrated++; continue; }
                    if (known.Contains(r)) { epgRecognized++; continue; }
                    epgUnrecognized[r] = epgUnrecognized.TryGetValue(r, out var c) ? c + 1 : 1;
                }
                epg = new
                {
                    total_programs = epgTotal,
                    recognized = epgRecognized,
                    unrecognized = epgUnrecognized.Values.Sum(),
                    unrecognized_values = epgUnrecognized
                        .OrderByDescending(kv => kv.Value).Select(kv => kv.Key).Take(15).ToList(),
                    unrated = epgUnrated,
                    note = "Les cotes EPG viennent BRUTES du fournisseur de guide (formats nationaux, " +
                           "jamais passés à la normalisation de la bibliothèque) — attendu ; la comparaison " +
                           "y est indicative. Appliquer une limite parentale au contenu en direct exige une " +
                           "carte EPG → table serveur."
                };
            }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] ratings_check : lecture EPG impossible : {0}", ex.Message);
                epg = new { note = "EPG non vérifiable : " + ex.Message };
            }

            return JsonSerializer.Serialize(new
            {
                server_table_size = known.Count,
                library = new
                {
                    total = libTotal,
                    recognized = libRecognized,
                    unrecognized = libUnrecognized,
                    unrecognized_top = topUnrecognized,
                    unrated = libUnrated,
                    severity = libSeverity,
                    advice = libAdvice
                },
                epg
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
        //  Sécurité : comptes & exposition réseau (lecture seule)
        // ------------------------------------------------------------------

        /// <summary>
        /// Suggestion de test externe transmise au LLM : la sonde locale ne
        /// peut jamais confirmer la joignabilité WAN (redirections manuelles
        /// invisibles à l'UPnP, port ouvert jamais scanné). GRC ShieldsUP!!
        /// est le test externe de référence, gratuit, sans installation —
        /// le plugin ne l'appelle JAMAIS lui-même (aucune requête sortante
        /// vers un tiers) : c'est l'usager qui clique.
        /// </summary>
        private const string ShieldsUpHint =
            "Test externe suggéré : GRC ShieldsUP!! (https://www.grc.com/shieldsup) — " +
            "gratuit, sans installation. Lancer « Custom Port Scanner » sur les ports 8096 et 8920 : " +
            "« Stealth » ou « Closed » = port non joignable depuis Internet ; « Open » = exposé " +
            "(corriger immédiatement : retirer la redirection de port du routeur, activer HTTPS, " +
            "verrouiller les comptes sans mot de passe).";

        /// <summary>Condition d'inclusion du test externe : une surface distante existe.</summary>
        private static bool ShouldSuggestExternalTest(bool remoteAccess, bool publicAccessObserved) =>
            remoteAccess || publicAccessObserved;

        /// <summary>
        /// Validation de sécurité consolidée (action <c>security_check</c>) :
        /// <list type="bullet">
        /// <item><b>Comptes</b> : mots de passe manquants — un admin sans mot
        ///   de passe est 🔴 critique (n'importe qui sur le réseau prend le
        ///   rôle), un simple usager ⚠️. Comptes désactivés comptés à part.
        ///   L'entité <c>User</c> n'a pas de HasPassword (le DTO REST l'ajoute)
        ///   : on teste <c>Password</c>/<c>Salt</c> vides, sans jamais exposer
        ///   le hash.</item>
        /// <item><b>Réseau</b> : accès distant sans HTTPS, HTTPS activé sans
        ///   certificat, UPnP (ouverture de ports automatique),
        ///   <c>ProxyHeaderMode != None</c> sans reverse proxy (en-têtes
        ///   X-Forwarded-*/X-Real-Ip forgables), absence de filtre IP.</item>
        /// <item><b>Exposition observée</b> : sessions actives dont
        ///   <c>RemoteEndPoint</c> est une IP publique (preuve directe, temps
        ///   réel) ET historique des appareils <c>IDeviceManager.GetDevices</c>
        ///   (table Devices2 de authentication.db — preuve durable : tout
        ///   appareil jamais connecté avec une IP publique).</item>
        /// <item><b>Surfaces plugin</b> (v1.13.13.0) : les playlists
        ///   « AI Tonight » (publique foyer + privées par usager,
        ///   v1.13.16.0 — verdict parental COMPLET du gate) et la bibliothèque
        ///   .strm sont des surfaces foyer — vérifie que chaque usager actif
        ///   ne voit pas d'items hors des bibliothèques partagées avec lui
        ///   (FilterAccessible, fail-open) ni au-dessus de son contrôle
        ///   parental complet, et que l'accès aux cartes .strm s'accompagne
        ///   du droit d'enregistrement (règle v1.13.11.0 — inverse en info).
        ///   Un échec de lecture → constat « info non vérifiable », jamais
        ///   une erreur.</item>
        /// <item><b>Contrôle parental : collection et règles de tags</b>
        ///   (v1.13.17.0) : la collection « AI Tonight » (BoxSet) — le listing
        ///   de ses membres est filtré NATIVEMENT par Emby (validé) et le
        ///   container reste visible — est couverte en INFO (pas un
        ///   contournement, contrairement à la playlist) ; les règles de
        ///   tags <c>BlockedTags</c>/<c>IncludeTags</c> ne matchant aucun
        ///   item (règles AVEUGLES) → AVERTISSEMENT ; usager « Tonight »
        ///   restreint → INFO.</item>
        /// </list>
        /// <b>Escalade de sévérité</b> : si un accès externe est observé
        /// (session ou appareil historique avec IP publique), tout constat
        /// ⚠️ avertissement est rehaussé 🔴 critique — un défaut de config
        /// combiné à une exposition RÉELLE n'est plus un avertissement.
        /// Chaque constat porte une <c>severity</c> (critique / avertissement /
        /// ok) et un <c>fix</c> d'une ligne avec le chemin exact du dashboard —
        /// le LLM les reprend tels quels dans le rapport. La sonde reste
        /// honnête : l'ABSENCE de visite ne prouve pas la non-exposition
        /// (port ouvert jamais scanné) ; le <c>note</c> le rappelle. Ne lève pas.
        /// </summary>
        private string SecurityCheck()
        {
            // Constats stockés en tuples mutables : la passe d'escalade (accès
            // externe observé → avertissement rehaussé critique) réécrit la
            // sévérité AVANT sérialisation et comptage.
            var findings = new List<(string severity, string title, string detail, string fix)>();
            void Add(string severity, string title, string detail, string fix) =>
                findings.Add((severity, title, detail ?? "", fix ?? ""));

            // ---- Comptes & mots de passe --------------------------------
            int totalUsers = 0, disabledUsers = 0;
            var adminsNoPassword = new List<string>();
            var usersNoPassword = new List<string>();
            try
            {
                var users = _users.GetUserList(new UserQuery()) ?? Array.Empty<User>();
                foreach (var u in users)
                {
                    if (u == null) continue;
                    totalUsers++;
                    if (u.Policy?.IsDisabled == true) { disabledUsers++; continue; }
                    // L'entité User n'expose pas HasPassword (le DTO REST l'ajoute) :
                    // un compte sans mot de passe a Password/Salt vides. On ne sort
                    // JAMAIS le hash lui-même — seulement le booléen.
                    if (!string.IsNullOrEmpty(u.Password) || !string.IsNullOrEmpty(u.Salt))
                        continue;
                    usersNoPassword.Add(u.Name ?? u.Id.ToString());
                    if (u.Policy?.IsAdministrator == true)
                        adminsNoPassword.Add(u.Name ?? u.Id.ToString());
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] system_audit security_check usagers : {0}", ex.Message);
                Add("info", "Comptes usagers non vérifiables",
                    "IUserManager indisponible : " + ex.Message, null);
            }
            if (totalUsers > 0)
            {
                if (adminsNoPassword.Count > 0)
                    Add("critique", "Administrateur sans mot de passe",
                        string.Join(", ", adminsNoPassword) +
                        " — n'importe qui sur le réseau (ou Internet si exposé) obtient le rôle admin sans credential.",
                        "Dashboard → Utilisateurs → sélectionner le compte → « Définir un mot de passe ».");
                else
                    Add("ok", "Tous les administrateurs ont un mot de passe", null, null);
                var nonAdminNoPw = usersNoPassword.Where(n => !adminsNoPassword.Contains(n)).ToList();
                if (nonAdminNoPw.Count > 0)
                    Add("avertissement", "Usagers sans mot de passe",
                        string.Join(", ", nonAdminNoPw) +
                        " — secret vide = aucune barrière si le compte a un accès (même local).",
                        "Dashboard → Utilisateurs → sélectionner chaque compte → « Définir un mot de passe ».");
            }

            // ---- Configuration réseau -----------------------------------
            bool remote = false, https = false, behindProxy = false, upnp = false;
            bool certConfigured = false, httpsRead = false, requireHttps = false;
            string proxyHeaderMode = null;
            int ipFilterCount = 0;
            bool ipFilterBlacklist = false;
            try
            {
                var mgr = _host.TryResolve<MediaBrowser.Controller.Configuration.IServerConfigurationManager>();
                var c = mgr?.Configuration;
                if (c != null)
                {
                    httpsRead = true;
                    remote = c.EnableRemoteAccess;
                    https = c.EnableHttps || c.RequireHttps;
                    requireHttps = c.RequireHttps;
                    behindProxy = c.IsBehindProxy;
                    upnp = c.EnableUPnP;
                    certConfigured = !string.IsNullOrWhiteSpace(c.CertificatePath);
                    ipFilterCount = c.RemoteIPFilter?.Length ?? 0;
                    ipFilterBlacklist = c.IsRemoteIPFilterBlacklist;
                    // Type de ProxyHeaderMode variable selon la version (enum ou
                    // string) : lecture par réflexion → nom lisible dans les deux cas.
                    proxyHeaderMode = c.GetType().GetProperty("ProxyHeaderMode")
                        ?.GetValue(c, null)?.ToString();
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] system_audit security_check réseau : {0}", ex.Message);
            }

            if (!httpsRead)
            {
                Add("info", "Configuration réseau non vérifiable",
                    "IServerConfigurationManager non résolu — aucun réglage réseau lu.", null);
            }
            else if (!remote)
            {
                Add("ok", "Accès distant désactivé",
                    "Le serveur n'est prévu que pour le réseau local.", null);
            }
            else if (behindProxy)
            {
                Add("info", "Accès distant derrière un reverse proxy",
                    "Emby délègue le transport au proxy : vérifier que lui seul termine TLS (certificat valide).",
                    null);
            }
            else if (https && certConfigured)
            {
                Add("ok", "Accès distant sécurisé (HTTPS)",
                    "RequireHttps=" + requireHttps + ", certificat configuré.", null);
            }
            else if (https && !certConfigured)
            {
                Add("avertissement", "HTTPS activé sans certificat",
                    "EnableHttps/RequireHttps actifs mais CertificatePath vide : le serveur HTTPS ne peut pas démarrer — les clients retombent en HTTP clair.",
                    "Dashboard → Réseau → « Chemin du certificat SSL » ou repasser le mode de connexion sécurisée sur « Désactivé » (et assumer HTTP).");
            }
            else
            {
                Add("critique", "Accès distant sans HTTPS",
                    "EnableRemoteAccess=true, EnableHttps=false : si le port est joignable depuis Internet (redirection de port, UPnP routeur), tout circule en clair — mots de passe, tokens, flux.",
                    "Dashboard → Réseau → « Mode de connexion sécurisée » → « Requis (HTTPS) » + renseigner un certificat ; ou désactiver l'accès distant ; ou placer un reverse proxy avec TLS.");
            }

            if (httpsRead && remote && !behindProxy
                && !string.IsNullOrEmpty(proxyHeaderMode)
                && !proxyHeaderMode.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                Add("avertissement", "En-têtes proxy lus depuis toutes les adresses",
                    "ProxyHeaderMode=" + proxyHeaderMode +
                    " sans reverse proxy : un client distant peut forger X-Real-Ip / X-Forwarded-For pour mentir sur son adresse.",
                    "Dashboard → Réseau → « Read proxy header » → « Non » (None) tant qu'aucun reverse proxy n'est en place.");
            }
            if (upnp)
            {
                Add("avertissement", "UPnP activé",
                    "Le serveur ouvre automatiquement les ports du routeur — exposition WAN possible sans action consciente de l'admin.",
                    "Dashboard → Réseau → décocher « Activer la mise en correspondance de ports UPnP ».");
            }
            if (httpsRead && remote && ipFilterCount == 0)
            {
                Add("info", "Aucun filtre d'adresses IP distant",
                    "Toute adresse peut tenter de se connecter (whitelist RemoteIPFilter vide).",
                    "Optionnel : Dashboard → Réseau → « Filtre d'adresses externes » en mode whitelist.");
            }

            // ---- Surfaces plugin : cohérence d'accès (v1.13.13.0,
            //      playlists per-usager + verdicts complets v1.13.16.0) ---
            // Les playlists « AI Tonight » (publique foyer + privées par
            // usager) et la bibliothèque .strm exposent les cartes
            // d'enregistrement. Un usager peut donc voir ces surfaces sans
            // avoir accès aux bibliothèques qui portent leur contenu (il
            // voit la reco — titre, poster — hébergée ailleurs), ou accéder
            // aux cartes .strm sans porter le droit d'enregistrement (règle
            // v1.13.11.0). Le contrôle parental Emby étant LISTING-ONLY,
            // un item visible dans une playlist est LISIBLE — l'audit
            // signale ces décalages à l'admin ; le dashboard reste maître
            // des accès, le plugin ne modifie jamais les comptes.
            int surfacesFindings = findings.Count;
            bool playlistChecked = false, strmChecked = false;
            string strmName = null;
            try
            {
                var cfg = Plugin.Instance?.Configuration;
                strmName = cfg?.StrmLibraryName;
                string strmRoot = string.IsNullOrWhiteSpace(strmName)
                    ? null : StrmLibraryGenerator.ResolveLibraryRoot(_library, strmName, _logger);

                // Usagers actifs (les désactivés ne voient aucune surface).
                var activeUsers = new List<User>();
                try
                {
                    foreach (var u in _users.GetUserList(new UserQuery()) ?? Array.Empty<User>())
                        if (u != null && u.Policy?.IsDisabled != true) activeUsers.Add(u);
                }
                catch (Exception ex)
                {
                    _logger?.Warn("[LLM_AI] system_audit security_check surfaces (usagers) : {0}", ex.Message);
                }

                // --- A. Playlists « AI Tonight » (publique + privées) ------
                // v1.13.16.0 : la playlist PUBLIQUE foyer (« AI Tonight »)
                // coexiste avec les playlists PRIVÉES par usager
                // (« AI Tonight · {usager} », remplie par les runs de chacun).
                // Le contrôle parental Emby est LISTING-ONLY (validé
                // 2026-09-12 : un item visible dans une playlist est LISIBLE
                // par le compte — ni la limite ni les tags ne bloquent la
                // lecture). L'audit vérifie donc ce que chaque surface expose
                // contre les droits de chaque compte concerné, avec le VERDICT
                // COMPLET du gate (PermissionGate.IsParentallyAllowed — limite
                // de cote, tags noirs/blancs, non cotés, tags de série), le
                // même code de décision que les recos de « Watch Tonight ».
                var pluginPlaylists = new List<(Playlist pl, User owner)>();
                try
                {
                    foreach (var p in _library.GetItemList(new InternalItemsQuery
                    {
                        IncludeItemTypes = new[] { "Playlist" },
                        EnableTotalRecordCount = false
                    }) ?? Array.Empty<BaseItem>())
                    {
                        var pl = p as Playlist;
                        if (pl == null || string.IsNullOrWhiteSpace(pl.Name)) continue;
                        if (string.Equals(pl.Name, AiTonightPlaylistManager.PlaylistName, StringComparison.OrdinalIgnoreCase))
                            pluginPlaylists.Add((pl, null));   // publique foyer
                        else if (pl.Name.StartsWith(AiTonightPlaylistManager.PlaylistName + " · ", StringComparison.OrdinalIgnoreCase))
                        {
                            string suffix = pl.Name.Substring(AiTonightPlaylistManager.PlaylistName.Length + 3).Trim();
                            User owner = null;
                            try { owner = _users.GetUserByName(suffix); } catch { }
                            owner = owner ?? activeUsers.FirstOrDefault(u =>
                                string.Equals(u.Name, suffix, StringComparison.OrdinalIgnoreCase));
                            pluginPlaylists.Add((pl, owner));
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Warn("[LLM_AI] system_audit security_check surfaces (playlists) : {0}", ex.Message);
                    Add("info", "Playlists « AI Tonight » non vérifiables",
                        "Listing des playlists impossible : " + ex.Message, null);
                }

                foreach (var entry in pluginPlaylists)
                {
                    var members = new List<BaseItem>();
                    try
                    {
                        foreach (var it in entry.pl.GetItemList(new InternalItemsQuery
                        {
                            EnableTotalRecordCount = false
                        }) ?? Array.Empty<BaseItem>())
                            if (it != null) members.Add(it);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Warn("[LLM_AI] system_audit security_check surfaces (playlist) : {0}", ex.Message);
                        continue;
                    }
                    if (members.Count == 0) continue;
                    playlistChecked = true;

                    bool isPublic = entry.owner == null;
                    string surface = isPublic
                        ? "Playlist publique « " + entry.pl.Name + " »"
                        : "Playlist privée « " + entry.pl.Name + " »";
                    // Usagers concernés : le propriétaire pour une privée,
                    // tous les actifs pour la publique (surface foyer).
                    var usersToCheck = isPublic
                        ? activeUsers
                        : (entry.owner != null ? new List<User> { entry.owner } : new List<User>());

                    foreach (var u in usersToCheck)
                    {
                        if (u == null) continue;

                        // Mismatch bibliothèque : même mécanique que le gate des
                        // recos (v1.13.12.0) — les items de la playlist doivent
                        // rester dans les bibliothèques accessibles à l'usager.
                        if (!PermissionGate.HasUnrestrictedFolders(u))
                        {
                            var accessible = PermissionGate.FilterAccessible(u, _library, _logger, members);
                            if (accessible != null)   // null = fail-open : résolution échouée
                            {
                                int hidden = members.Count - accessible.Count;
                                if (hidden > 0)
                                    Add("avertissement", surface + " : items hors des bibliothèques accessibles à " + (u.Name ?? "?"),
                                        hidden + " item(s) sur " + members.Count + " sont hébergés dans des bibliothèques non partagées avec ce compte — il voit l'item dans la playlist alors que sa bibliothèque ne lui est pas partagée.",
                                        "Dashboard → Utilisateurs → " + (u.Name ?? "?") + " → « Accès aux médias » : donner la bibliothèque concernée, ou retirer l'item du watch bucket (page Recommandations).");
                            }
                        }

                        // Verdict parental COMPLET (v1.13.16.0) : remplace
                        // l'ancien check « MaxParentalRating seul » — le
                        // contrôle parental Emby ne bloque PAS la lecture
                        // depuis une playlist (validé 2026-09-12), donc un
                        // item interdit visible y est LISIBLE.
                        int blocked = members.Count(m =>
                            PermissionGate.IsParentallyAllowed(u, m) != PermissionGate.ParentalVerdict.Allowed);
                        if (blocked > 0)
                            Add("avertissement", surface + " : contenu au-dessus du contrôle parental de " + (u.Name ?? "?"),
                                blocked + " item(s) sur " + members.Count + " ne passent pas le contrôle parental de ce compte (limite de cote, tags ou non cotés bloqués) — il peut les LIRE depuis la playlist, le contrôle parental Emby ne bloque pas la lecture.",
                                isPublic
                                    ? "La playlist publique est reconstruite à l'intersection parentale par le prochain run « Watch Tonight » de l'usager « Tonight » — ou ajuster la policy du compte (Dashboard → Utilisateurs → contrôle parental)."
                                    : "La playlist privée a été remplie avant un changement de policy — relancer « Watch Tonight » sous ce compte la reconstruit filtrée, ou ajuster la policy.");
                    }
                }

                // --- B. Bibliothèque .strm --------------------------------
                if (!string.IsNullOrWhiteSpace(strmRoot))
                {
                    strmChecked = true;
                    foreach (var u in activeUsers)
                    {
                        // Accès .strm : EnableAllFolders, ou une bibliothèque de
                        // EnabledFolders résolue dont le chemin couvre la racine
                        // .strm (heuristique : CollectionFolder.Path = location —
                        // comparaison dans les deux sens, la forme exacte des ids
                        // du dashboard varie selon le build).
                        bool hasStrm = u.Policy?.EnableAllFolders == true;
                        if (!hasStrm && u.Policy?.EnabledFolders != null)
                        {
                            foreach (var raw in u.Policy.EnabledFolders)
                            {
                                var lib = ItemIdResolver.Resolve(_library, raw);
                                if (lib == null || string.IsNullOrWhiteSpace(lib.Path)) continue;
                                if (TonightService.IsUnderPath(lib.Path, strmRoot)
                                    || TonightService.IsUnderPath(strmRoot, lib.Path))
                                {
                                    hasStrm = true;
                                    break;
                                }
                            }
                        }

                        if (hasStrm && !PermissionGate.CanRecordLive(u))
                            Add("avertissement", "Cartes d'enregistrement visibles par un compte sans droit d'enregistrer",
                                (u.Name ?? "?") + " accède à la bibliothèque .strm mais ne porte pas EnableLiveTvManagement — la lecture d'une carte déclencherait une activation immédiatement refusée (gate d'enregistrement).",
                                "Dashboard → Utilisateurs → " + (u.Name ?? "?") + " : retirer la bibliothèque .strm de son accès aux médias, ou lui donner le droit d'enregistrement.");
                        else if (!hasStrm && PermissionGate.CanRecordLive(u))
                            Add("info", "Cartes .strm invisibles pour un compte autorisé à enregistrer",
                                (u.Name ?? "?") + " porte le droit d'enregistrement mais n'a pas accès à la bibliothèque .strm — les cartes de recommandation sont invisibles pour ce compte.",
                                "Optionnel : Dashboard → Utilisateurs → " + (u.Name ?? "?") + " → ajouter la bibliothèque .strm à son accès aux médias.");
                    }
                }

                // --- C. Contrôle parental : collection et règles de tags ----
                // (v1.13.17.0) Deux volets complémentaires aux playlists :
                //
                // • Collection « AI Tonight » (BoxSet) : le listing de ses
                //   membres est filtré NATIVEMENT par Emby pour un compte
                //   restreint (validé 2026-09-12 : un item CA-14A ajouté au
                //   BoxSet est invisible dans le listing du compte, les items
                //   sous la limite restent visibles) et le container lui-même
                //   reste toujours visible, même quand sa cote agrégée dépasse
                //   la limite. Ce n'est donc pas un contournement (contraire-
                //   ment à la playlist, corrigée en v1.13.16.0) — INFO de
                //   transparence, pas alerte. L'accès direct par id reste
                //   possible (comportement Emby natif, hors du plugin).
                // • Règles de TAGS inopérantes : un BlockedTags / IncludeTags
                //   dont la valeur ne matche AUCUN item de la bibliothèque
                //   (coquille de frappe, accent) est une règle AVEUGLE — le
                //   compte croit être protégé sans l'être (règle noire) ou ne
                //   voit plus rien (liste blanche). Seul trou parental restant
                //   côté plugin → AVERTISSEMENT.
                var restrictedUsers = new List<User>();
                int parentalFindings = findings.Count;
                foreach (var u in activeUsers)
                    if (u != null && PermissionGate.HasParentalRestrictions(u))
                        restrictedUsers.Add(u);

                if (restrictedUsers.Count > 0)
                {
                    // INFO : l'usager « Tonight » porte lui-même des règles —
                    // l'intersection parentale de la playlist publique vaut
                    // exactement sa propre policy (la surface foyer ne montre
                    // jamais plus que ce qu'il voit lui-même).
                    string tonightName = cfg?.TonightUserName;
                    if (!string.IsNullOrWhiteSpace(tonightName) && restrictedUsers.Any(u =>
                            string.Equals(u.Name, tonightName.Trim(), StringComparison.OrdinalIgnoreCase)))
                        Add("info", "L'usager « Tonight » est sous contrôle parental",
                            "L'intersection parentale de la playlist publique « " + AiTonightPlaylistManager.PlaylistName +
                            " » vaut alors exactement la policy de cet usager — la surface foyer ne montre jamais plus que ce qu'il voit lui-même.", null);

                    // INFO de transparence sur la collection (listing filtré
                    // nativement, pas un contournement).
                    BaseItem tonightCollection = null;
                    try
                    {
                        foreach (var b in _library.GetItemList(new InternalItemsQuery
                        {
                            IncludeItemTypes = new[] { "BoxSet" },
                            EnableTotalRecordCount = false
                        }) ?? Array.Empty<BaseItem>())
                            if (string.Equals(b.Name, AiTonightCollectionManager.CollectionName, StringComparison.OrdinalIgnoreCase))
                            {
                                tonightCollection = b;
                                break;
                            }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Warn("[LLM_AI] system_audit security_check parental (collection) : {0}", ex.Message);
                    }
                    if (tonightCollection != null)
                    {
                        var members = new List<BaseItem>();
                        try
                        {
                            // Membres d'un BoxSet : via InternalItemsQuery
                            // .CollectionIds (ParentId n'existe pas sur cette
                            // build — voir AiTonightCollectionManager).
                            foreach (var m in _library.GetItemList(new InternalItemsQuery
                            {
                                CollectionIds = new[] { tonightCollection.InternalId },
                                EnableTotalRecordCount = false
                            }) ?? Array.Empty<BaseItem>())
                                if (m != null) members.Add(m);
                        }
                        catch (Exception ex)
                        {
                            _logger?.Warn("[LLM_AI] system_audit security_check parental (membres collection) : {0}", ex.Message);
                        }
                        if (members.Count > 0)
                        {
                            foreach (var u in restrictedUsers)
                            {
                                int blocked = members.Count(m =>
                                    PermissionGate.IsParentallyAllowed(u, m) != PermissionGate.ParentalVerdict.Allowed);
                                if (blocked > 0)
                                    Add("info", "Collection « AI Tonight » : contenu au-dessus du contrôle parental de " + (u.Name ?? "?"),
                                        blocked + " item(s) sur " + members.Count + " ne passent pas le contrôle parental de ce compte. Sa vue listing les MASQUE (filtrage natif du BoxSet, validé) — ils restent accessibles par accès direct à l'id (comportement Emby natif, hors du plugin). La collection est remplie par le run « Watch Tonight » de l'usager « Tonight », sans intersection parentale (contrairement à la playlist publique).",
                                        "Optionnel : ajuster la policy du compte (Dashboard → Utilisateurs → contrôle parental), ou garder les recos « Tonight » sous la limite la plus basse du foyer.");
                            }
                        }
                    }

                    // AVERTISSEMENT : règles de tags inopérantes (règle
                    // aveugle). Existence testée par une requête Limit=1
                    // (même filtre que AiTagger).
                    foreach (var u in restrictedUsers)
                    {
                        var pol = u.Policy;
                        if (pol == null) continue;

                        foreach (var tag in pol.BlockedTags ?? Array.Empty<string>())
                        {
                            if (string.IsNullOrWhiteSpace(tag)) continue;
                            if (!TagMatchesAnyItem(tag))
                                Add("avertissement", "Tag bloqué inopérant pour " + (u.Name ?? "?") + " : « " + tag + " »",
                                    "Aucun item de la bibliothèque ne porte ce tag — la règle noire de contrôle parental ne bloque donc RIEN (règle aveugle : le compte croit être protégé sans l'être).",
                                    "Corriger la valeur dans Dashboard → Utilisateurs → " + (u.Name ?? "?") + " → contrôle parental (orthographe et accents exacts).");
                        }

                        if (pol.IsTagBlockingModeInclusive)
                            foreach (var tag in pol.IncludeTags ?? Array.Empty<string>())
                            {
                                if (string.IsNullOrWhiteSpace(tag)) continue;
                                if (!TagMatchesAnyItem(tag))
                                    Add("avertissement", "Tag de liste blanche inopérant pour " + (u.Name ?? "?") + " : « " + tag + " »",
                                        "Mode « Exclure tous sauf le tag » : aucun item ne porte ce tag — le compte ne voit pratiquement aucun contenu (sur-blocage involontaire, effet miroir de la règle aveugle).",
                                        "Corriger la valeur dans Dashboard → Utilisateurs → " + (u.Name ?? "?") + " → contrôle parental (orthographe et accents exacts).");
                            }
                    }

                    if (findings.Count == parentalFindings)
                        Add("ok", "Contrôle parental : règles et collection cohérentes",
                            "Les règles de tags des comptes restreints matchent des items de la bibliothèque" +
                            (tonightCollection != null
                                ? ", et la collection « AI Tonight » n'expose rien au-delà des limites dans les listings (filtrage natif du BoxSet, validé)."
                                : ") — la collection « AI Tonight » n'existe pas (aucun run « Watch Tonight » encore, ou option désactivée)."), null);
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] system_audit security_check surfaces : {0}", ex.Message);
                Add("info", "Surfaces plugin non vérifiables",
                    "Lecture playlist / bibliothèque .strm impossible : " + ex.Message, null);
            }
            if (!playlistChecked && !strmChecked)
            {
                Add("info", "Surfaces plugin absentes ou vides",
                    "Aucune playlist « AI Tonight » remplie (publique ou privée), et bibliothèque .strm non configurée — rien à vérifier pour l'instant (les surfaces apparaissent à la prochaine génération de recommandations).", null);
            }
            else if (findings.Count == surfacesFindings)
            {
                Add("ok", "Surfaces plugin : accès cohérents",
                    "Playlists « AI Tonight » et bibliothèque .strm" + (string.IsNullOrWhiteSpace(strmName) ? " (non configurée)" : " (« " + strmName + " »)") +
                    " : aucun décalage entre les surfaces visibles et les droits des usagers.", null);
            }

            // ---- Exposition observée (preuves directes) ------------------
            // Temps réel : sessions actives ; durable : appareils historiques
            // (IDeviceManager → table Devices2 de authentication.db, chaque
            // appareil jamais connecté avec sa dernière IP rapportée).
            var publicPeers = new List<string>();
            try
            {
                foreach (var s in _sessions.Sessions ?? Enumerable.Empty<SessionInfo>())
                {
                    string ep = s.RemoteEndPoint?.ToString();
                    if (IsPublicEndPoint(ep))
                        publicPeers.Add((s.UserName ?? "?") + "@" + ep);
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] system_audit security_check sessions : {0}", ex.Message);
            }
            if (publicPeers.Count > 0)
            {
                Add("avertissement", "Connexions depuis des IP publiques",
                    string.Join(", ", publicPeers.Distinct()) +
                    " — le serveur est EFFECTIVEMENT joignable depuis Internet (session active).",
                    "Vérifier la redirection de ports du routeur et la légitimité de ces adresses.");
            }

            int devicesTotal = 0;
            var publicDevices = new List<string>();
            try
            {
                // Résolution souple (pas d'injection constructeur) : IDeviceManager
                // n'est pas disponible sur toutes les versions/registres DI — un
                // échec ne fait que dégrader la preuve « historique », la sonde
                // continue (même contrat que system_config).
                var devices = _host.TryResolve<MediaBrowser.Controller.Devices.IDeviceManager>()
                    ?.GetDevices(new MediaBrowser.Model.Devices.DeviceQuery())?.Items;
                if (devices != null)
                {
                    foreach (var d in devices)
                    {
                        if (d == null) continue;
                        devicesTotal++;
                        // DeviceInfo.IpAddress est un IPAddress (pas une string) ;
                        // 0.0.0.0 = l'appareil n'a jamais rapporté d'IP utilisable.
                        string ip = d.IpAddress?.ToString();
                        if (!IsPublicEndPoint(ip)) continue;
                        string activity = d.DateLastActivity != default
                            ? ", dernière activité " + d.DateLastActivity.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                            : "";
                        publicDevices.Add(string.Equals(ip, "0.0.0.0", StringComparison.Ordinal)
                            ? (d.Name ?? d.ReportedDeviceId ?? "?") + " (IP non rapportée)"
                            : (d.Name ?? "?") + " [" + (d.LastUserName ?? "?") + "] @ " + ip
                              + activity);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] system_audit security_check devices : {0}", ex.Message);
            }
            if (publicDevices.Count > 0)
            {
                Add("avertissement", "Appareils historiques avec IP publique",
                    string.Join(" ; ", publicDevices) +
                    " — des accès DEPUIS Internet ont déjà eu lieu (historique d'appareils).",
                    "Vérifier la redirection de ports du routeur et la légitimité de ces appareils.");
            }
            bool publicAccessObserved = publicPeers.Count > 0 || publicDevices.Count > 0;

            // ---- Escalade de sévérité ------------------------------------
            // Règle de l'usager : accès externe observé + mauvaise config ⇒ le
            // constat n'est plus un avertissement. Tout ⚠️ est rehaussé 🔴
            // (y compris le constat d'exposition lui-même : une preuve
            // d'accès public sur un serveur sans HTTPS n'est pas un
            // avertissement). Les constats ok/info ne bougent pas.
            if (publicAccessObserved)
            {
                for (int i = 0; i < findings.Count; i++)
                {
                    if (findings[i].severity != "avertissement") continue;
                    var f = findings[i];
                    findings[i] = ("critique", f.title,
                        f.detail + (f.detail.Length > 0 ? " " : "") +
                        "[Sévérité rehaussée en critique : accès depuis Internet observé.]",
                        f.fix);
                }
            }

            int crit = findings.Count(f => f.severity == "critique");
            int warn = findings.Count(f => f.severity == "avertissement");
            int ok = findings.Count(f => f.severity == "ok");

            return JsonSerializer.Serialize(new
            {
                findings = findings.Select(f => new
                {
                    severity = f.severity,
                    title = f.title,
                    detail = f.detail,
                    fix = f.fix
                }),
                summary = new { critique = crit, avertissement = warn, ok },
                external_access_observed = publicAccessObserved,
                users = new
                {
                    total = totalUsers,
                    disabled = disabledUsers,
                    no_password = usersNoPassword,
                    admins_no_password = adminsNoPassword
                },
                network = new
                {
                    enable_remote_access = remote,
                    enable_https_or_require = https,
                    is_behind_proxy = behindProxy,
                    proxy_header_mode = proxyHeaderMode,
                    enable_upnp = upnp,
                    certificate_configured = certConfigured,
                    remote_ip_filter_count = ipFilterCount,
                    remote_ip_filter_blacklist = ipFilterBlacklist
                },
                devices = new
                {
                    total_known = devicesTotal,
                    public_ip = publicDevices
                },
                note = "Preuves d'accès externe : sessions actives (temps réel) et historique des appareils " +
                       "(dernière IP rapportée par appareil — pas un historique exhaustif des connexions). " +
                       "L'ABSENCE de visite ne prouve pas la non-exposition : un port ouvert jamais scanné est " +
                       "invisible ; le champ external_test fournit la recette de confirmation (GRC ShieldsUP!!) — " +
                       "inclus-la dans les « Actions recommandées » dès qu'une surface distante existe. Quand " +
                       "external_access_observed=true, les avertissements sont déjà rehaussés critique. " +
                       "Reprends les constats (severity + fix) tels quels dans le rapport.",
                external_test = ShouldSuggestExternalTest(remote, publicAccessObserved) ? ShieldsUpHint : null
            }, s_json);
        }

        /// <summary>
        /// Vrai si <paramref name="endPoint"/> (format « ip:port » / « [v6]:port »)
        /// parse vers une adresse IP publique : ni loopback, ni privée
        /// (RFC1918), ni link-local, ni CGNAT, ni unique-local v6. Retourne
        /// false pour toute valeur non parsable (hôte résolu, vide…).
        /// </summary>
        private static bool IsPublicEndPoint(string endPoint)
        {
            if (string.IsNullOrWhiteSpace(endPoint)) return false;
            string host = endPoint.Trim();
            // IPv6 littéral entre crochets, sinon coupe le port v4 (un seul ':').
            if (host.StartsWith("[")) host = host.Trim('[', ']');
            else
            {
                int colon = host.IndexOf(':');
                if (colon >= 0 && host.IndexOf(':', colon + 1) < 0)
                    host = host.Substring(0, colon);
            }
            if (!System.Net.IPAddress.TryParse(host, out var ip)) return false;
            if (System.Net.IPAddress.IsLoopback(ip)) return false;

            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                if (b[0] == 10) return false;                                       // 10/8
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;          // 172.16/12
                if (b[0] == 192 && b[1] == 168) return false;                       // 192.168/16
                if (b[0] == 169 && b[1] == 254) return false;                       // link-local
                if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false;         // CGNAT 100.64/10
                return true;
            }
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return false;
                var b = ip.GetAddressBytes();
                if ((b[0] & 0xFE) == 0xFC) return false;                            // fc00::/7 ULA
                return true;
            }
            return false;
        }

        // ------------------------------------------------------------------
        //  UPnP : sonde de la passerelle (lecture seule)
        // ------------------------------------------------------------------

        /// <summary>
        /// Sonde UPnP du routeur (action <c>upnp_check</c>) — répond à la
        /// question « le port Emby est-il ouvert sur le WAN via UPnP ? » :
        /// <list type="bullet">
        /// <item><b>Découverte</b> : M-SEARCH SSDP multicast
        ///   (239.255.255.250:1900) pour InternetGatewayDevice / WANIPConnection
        ///   / WANPPPConnection, écoute ~3 s. Aucune réponse = UPnP désactivé
        ///   (ou muet) au routeur — un verdict <c>ok</c> pour la fuite.</item>
        /// <item><b>Description</b> : GET du XML LOCATION → URL de contrôle du
        ///   service WANIPConnection/WANPPPConnection.</item>
        /// <item><b>SOAP</b> : <c>GetExternalIPAddress</c> (IP WAN réelle) puis
        ///   boucle <c>GetGenericPortMappingEntry</c> (index 0→39, stop au
        ///   premier refus) pour énumérer la table de redirection. Un mapping
        ///   dont le port interne est 8096/8920 (ou pointant vers l'hôte Emby)
        ///   est un constat <b>critique</b> : exposition WAN effective.</item>
        /// </list>
        /// <b>Strictement lecture seule</b> : seules des actions Get* sont
        /// envoyées — jamais Add/DeletePortMapping. Limite honnête rappelée
        /// dans le <c>note</c> : la table UPnP ne contient que les redirections
        /// créées VIA UPnP ; une redirection manuelle (UI du routeur) est
        /// invisible — seul un test externe la voit. Budget temps borné
        /// (~3 s découverte + ~4 s HTTP + boucle mappages plafonnée).
        /// Ne lève pas (erreurs capturées → <c>{"error":…}</c>).
        /// </summary>
        private async Task<string> UpnpCheckAsync(CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();

            // ---- 1) Découverte SSDP ------------------------------------
            // M-SEARCH multicast sur les ST de passerelle, puis écoute ~3 s.
            // On dédoublonne par LOCATION (une passerelle répond à plusieurs ST).
            var locations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var udp = new System.Net.Sockets.UdpClient();
                udp.Client.ReceiveTimeout = 500;
                foreach (var st in new[]
                {
                    "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
                    "urn:schemas-upnp-org:service:WANIPConnection:1",
                    "urn:schemas-upnp-org:service:WANIPConnection:2",
                    "urn:schemas-upnp-org:service:WANPPPConnection:1"
                })
                {
                    ct.ThrowIfCancellationRequested();
                    var msg = Encoding.ASCII.GetBytes(
                        "M-SEARCH * HTTP/1.1\r\n" +
                        "HOST: 239.255.255.250:1900\r\n" +
                        "MAN: \"ssdp:discover\"\r\n" +
                        "MX: 2\r\n" +
                        "ST: " + st + "\r\n\r\n");
                    udp.Send(msg, msg.Length, "239.255.255.250", 1900);
                }
                var deadline = DateTime.UtcNow.AddSeconds(3);
                var anyEp = new IPEndPoint(IPAddress.Any, 0);
                while (DateTime.UtcNow < deadline && locations.Count < 8)
                {
                    try
                    {
                        var data = udp.Receive(ref anyEp);
                        var head = Encoding.UTF8.GetString(data);
                        var loc = Regex.Match(head, "LOCATION:\\s*(\\S+)", RegexOptions.IgnoreCase);
                        if (!loc.Success) continue;
                        var stM = Regex.Match(head, "ST:\\s*(\\S+)", RegexOptions.IgnoreCase);
                        locations[loc.Groups[1].Value] = stM.Success ? stM.Groups[1].Value : "?";
                    }
                    catch (System.Net.Sockets.SocketException) { /* ReceiveTimeout — on boucle */ }
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] system_audit upnp_check découverte SSDP : {0}", ex.Message);
            }

            if (locations.Count == 0)
            {
                return JsonSerializer.Serialize(new
                {
                    upnp_available = false,
                    verdict = "ok",
                    checked_seconds = Math.Round(sw.Elapsed.TotalSeconds, 1),
                    note = "Aucune passerelle UPnP n'a répondu au SSDP (UPnP désactivé ou muet au routeur) : " +
                           "aucune redirection de port ne peut être créée à l'insu de l'admin. Ne conclus " +
                           "PAS pour autant que le port est fermé : les redirections MANUELLES de l'UI du " +
                           "routeur sont invisibles ici — le champ external_test donne le test de confirmation. " +
                           "Croiser avec security_check (preuves d'accès externe).",
                    external_test = ShieldsUpHint
                }, s_json);
            }

            // ---- 2) Description de la passerelle → URL de contrôle ------
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            string controlUrl = null, controlServiceType = null, friendlyName = null;
            foreach (var loc in locations.Keys)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    string xml = await http.GetStringAsync(loc).ConfigureAwait(false);
                    if (friendlyName == null)
                    {
                        var fn = Regex.Match(xml, "<friendlyName>\\s*([^<]+)");
                        if (fn.Success) friendlyName = fn.Groups[1].Value.Trim();
                    }
                    foreach (Match svc in Regex.Matches(xml,
                        "<service>.*?</service>", RegexOptions.Singleline))
                    {
                        var stTxt = Regex.Match(svc.Value, "<serviceType>\\s*([^<]+)").Groups[1].Value.Trim();
                        if (!stTxt.Contains("WANIPConnection", StringComparison.OrdinalIgnoreCase)
                            && !stTxt.Contains("WANPPPConnection", StringComparison.OrdinalIgnoreCase))
                            continue;
                        var cu = Regex.Match(svc.Value, "<controlURL>\\s*([^<]+)").Groups[1].Value.Trim();
                        if (string.IsNullOrEmpty(cu)) continue;
                        controlUrl = new Uri(new Uri(loc), cu).AbsoluteUri;
                        controlServiceType = stTxt;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Warn("[LLM_AI] system_audit upnp_check description {0} : {1}", loc, ex.Message);
                }
                if (controlUrl != null) break;
            }

            if (controlUrl == null)
            {
                return JsonSerializer.Serialize(new
                {
                    upnp_available = true,
                    gateway = friendlyName,
                    discovered_locations = locations.Keys.ToArray(),
                    verdict = "info",
                    checked_seconds = Math.Round(sw.Elapsed.TotalSeconds, 1),
                    note = "Une passerelle UPnP répond mais aucun service WANIPConnection/WANPPPConnection " +
                           "n'expose d'URL de contrôle (UPnP limité au DLNA ?). Impossible d'énumérer la " +
                           "table de redirection — traiter comme « passerelle présente, état des ports " +
                           "inconnu » et rappeler la limite des redirections manuelles.",
                    external_test = ShieldsUpHint
                }, s_json);
            }

            // ---- 3) SOAP : IP WAN + table de redirection ----------------
            // POST SOAP minimal. Retourne le corps de la réponse (réponse ou
            // fault SOAP — l'appelant décide), jamais d'exception.
            async Task<string> SoapAsync(string action, string body)
            {
                try
                {
                    var env = "<?xml version=\"1.0\"?>" +
                        "<SOAP-ENV:Envelope xmlns:SOAP-ENV=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
                        "SOAP-ENV:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                        "<SOAP-ENV:Body><u:" + action + " xmlns:u=\"" + controlServiceType + "\">" +
                        body + "</u:" + action + "></SOAP-ENV:Body></SOAP-ENV:Envelope>";
                    using var req = new HttpRequestMessage(HttpMethod.Post, controlUrl);
                    req.Content = new StringContent(env, Encoding.UTF8, "text/xml");
                    req.Headers.TryAddWithoutValidation("SOAPACTION",
                        "\"" + controlServiceType + "#" + action + "\"");
                    using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                    return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.Warn("[LLM_AI] system_audit upnp_check SOAP {0} : {1}", action, ex.Message);
                    return null;
                }
            }
            static string Tag(string xml, string tag)
            {
                var m = Regex.Match(xml, "<" + tag + ">\\s*([^<]+)");
                return m.Success ? m.Groups[1].Value.Trim() : null;
            }

            string externalIp = null;
            try
            {
                var ext = await SoapAsync("GetExternalIPAddress", "").ConfigureAwait(false);
                if (ext != null) externalIp = Tag(ext, "NewExternalIPAddress");
            }
            catch (OperationCanceledException) { throw; }
            catch { /* IP WAN indisponible — non bloquant */ }

            // IP locales de l'hôte (pour repérer les mappings qui pointent ici).
            var localIps = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            localIps.Add(ua.Address.ToString());
            }
            catch { }

            var mappings = new List<object>();
            bool mappedToEmby = false;
            for (int i = 0; i < 40 && sw.Elapsed.TotalSeconds < 25; i++)
            {
                ct.ThrowIfCancellationRequested();
                var resp = await SoapAsync("GetGenericPortMappingEntry",
                    "<NewPortMappingIndex>" + i + "</NewPortMappingIndex>").ConfigureAwait(false);
                if (resp == null) break;
                // La fin de table est une fault SOAP (SpecifiedArrayIndexInvalid…) :
                // si le champ attendu est absent, on s'arrête.
                string internalClient = Tag(resp, "NewInternalClient");
                if (internalClient == null) break;
                string internalPortStr = Tag(resp, "NewInternalPort");
                string proto = (Tag(resp, "NewProtocol") ?? "").ToUpperInvariant();
                string desc = Tag(resp, "NewPortMappingDescription") ?? "";
                int.TryParse(internalPortStr, out int internalPort);
                bool targetsEmby = internalPort == 8096 || internalPort == 8920;
                bool targetsHost = localIps.Contains(internalClient) || internalClient == "0.0.0.0";
                if (targetsEmby) mappedToEmby = true;
                mappings.Add(new
                {
                    external_port = Tag(resp, "NewExternalPort"),
                    protocol = proto,
                    internal_client = internalClient,
                    internal_port = internalPort,
                    targets_emby_port = targetsEmby,
                    targets_this_host = targetsHost,
                    description = desc,
                    lease_seconds = Tag(resp, "NewLeaseDuration")
                });
            }

            string verdict = mappedToEmby ? "critique" : (mappings.Count > 0 ? "info" : "ok");
            return JsonSerializer.Serialize(new
            {
                upnp_available = true,
                gateway = friendlyName,
                external_ip = externalIp,
                verdict,
                mapping_count = mappings.Count,
                mappings,
                checked_seconds = Math.Round(sw.Elapsed.TotalSeconds, 1),
                note = mappedToEmby
                    ? "UN MAPPING UPnP EXPOSE LE PORT EMBY SUR LE WAN — constat CRITIQUE : le port 8096/8920 " +
                      "est joignable depuis Internet. Reproduis ce constat tel quel, avec la ligne du mapping. " +
                      "L'admin peut supprimer le mapping dans l'UI du routeur et désactiver son UPnP."
                    : "Table de redirection UPnP énumérée en lecture seule (aucune action Add/Delete envoyée). " +
                      "Limite : la table ne contient que les redirections créées VIA UPnP — les redirections " +
                      "MANUELLES de l'UI du routeur sont invisibles ici. Croiser avec security_check.",
                external_test = mappedToEmby
                    ? null // exposition déjà confirmée — pas besoin de test externe
                    : ShieldsUpHint,
                external_ip_note = externalIp == null
                    ? null
                    : (IsPublicEndPoint(externalIp + ":0")
                        ? "IP WAN publique de la passerelle — utile au test externe."
                        : "IP WAN non publique (CGNAT du fournisseur ?) : sans redirection explicite, le serveur " +
                          "n'est PAS joignable depuis Internet quelle que soit la config locale.")
            }, s_json);
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
            SectionSync("ratings_check", () => RatingsCheck());
            SectionSync("security_check", () => SecurityCheck());
            await SectionAsync("upnp_check", UpnpCheckAsync(ct)).ConfigureAwait(false);

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

        /// <summary>
        /// Un tag de règle parentale (BlockedTags / IncludeTags) matche-t-il au
        /// moins un item de la bibliothèque ? Requête <c>Limit=1</c> avec le
        /// même filtre que AiTagger (<see cref="InternalItemsQuery.Tags"/>) —
        /// une coquille de frappe (accent, casse gérée par Emby, mais pas les
        /// accents) rend la règle AVEUGLE : aucun constat de règle noire
        /// inopérante ne doit passer inaperçu. Fail-open : erreur de requête =
        /// on assume que le tag matche (pas de fausse alerte).
        /// </summary>
        private bool TagMatchesAnyItem(string tag)
        {
            try
            {
                return _library.GetItemList(new InternalItemsQuery
                {
                    Tags = new[] { tag },
                    Limit = 1,
                    EnableTotalRecordCount = false
                })?.Length > 0;
            }
            catch (Exception ex)
            {
                _logger?.Warn("[LLM_AI] system_audit security_check parental (match tag) : {0}", ex.Message);
                return true;
            }
        }

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