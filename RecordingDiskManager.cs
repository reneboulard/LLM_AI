using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Model.Logging;
using Emby.Notifications;
using MediaBrowser.Model.Querying;

namespace LLM_AI
{
    /// <summary>
    /// Gestion du seuil disque du dossier d'enregistrements Live TV.
    /// <para><b>1. Gate</b> : <see cref="IsBelowThreshold"/> indique si l'espace
    /// libre du volume hébergeant le dossier d'enregistrements est sous
    /// <see cref="PluginConfiguration.RecordingDiskThresholdGb"/> — les
    /// appelants (<c>AutoProgrammer.Program</c>, endpoint Activate) suspendent
    /// alors la création de nouveaux timers. Le gate échoue OUVERT : chemin
    /// introuvable ou volume illisible ne bloque jamais l'enregistrement.</para>
    /// <para><b>2. Passe d'étiquetage</b> (suggestion, jamais destructive) :
    /// quand le seuil est franchi, <see cref="RunTagPassAsync"/> retire
    /// D'ABORD tous les tags « AI Delete » précédents (reset — chaque run
    /// repart de zéro), puis tag les enregistrements <b>visionnés</b> (par
    /// n'importe quel usager), du plus ancien au plus récent, jusqu'à ce que
    /// <c>libre + Σ tailles taguées ≥ seuil × marge</c> — c'est-à-dire que la
    /// suppression de tout ce qui est tagué ramènerait le volume au-dessus du
    /// seuil avec une marge de sécurité. Le plugin ne supprime JAMAIS de
    /// fichier : le genre <see cref="DeleteGenre"/> est une suggestion que
    /// l'usager concrétise lui-même (filtre bibliothèque par genre,
    /// multi-sélection, suppression). Une passe déclenchée alors que l'espace
    /// s'est libéré ne fait donc que retirer les tags obsolètes.</para>
    /// </summary>
    /// <remarks>
    /// <para><b>Scope</b> : seuls les items dont le fichier est sous le chemin
    /// d'enregistrements résolu (<c>LiveTvOptions.RecordingPath</c>, repli
    /// MovieRecordingPath / SeriesRecordingPath, repli défaut Emby
    /// <c>&lt;ProgramData&gt;/data/livetv/recordings</c>) sont candidats au tag.</para>
    /// <para><b>Persistance des tags</b> : réutilise
    /// <see cref="AiGenreTagger.AddAsync"/>/<see cref="AiGenreTagger.RemoveAllAsync"/>
    /// (même mécanique que le genre « AI Tonight » — le cleanup nocturne
    /// <c>AiTonightCleanupTask</c> ne touche pas à ce genre).</para>
    /// </remarks>
    internal static class RecordingDiskManager
    {
        /// <summary>
        /// Genre appliqué aux enregistrements suggérés à la suppression.
        /// Distinct de « AI Tonight » / « AI Suggestion » pour garder tous les
        /// nettoyages indépendants.
        /// </summary>
        public const string DeleteGenre = "AI Delete";

        /// <summary>
        /// Marge de sécurité : l'objectif de la passe est
        /// <c>libre + Σ tailles taguées ≥ seuil × <see cref="MarginFactor"/></c>
        /// — l'usager peut garder une partie des enregistrements tagués sans
        /// repasser sous le seuil immédiatement.
        /// </summary>
        private const double MarginFactor = 1.2;

        private static readonly long GiB = 1024L * 1024L * 1024L;

        // ------------------------------------------------------------------
        //  Chemin d'enregistrements + volume
        // ------------------------------------------------------------------

        /// <summary>
        /// Résout le chemin d'enregistrements effectif :
        /// <c>LiveTvOptions.RecordingPath</c>, repli <c>MovieRecordingPath</c>,
        /// repli <c>SeriesRecordingPath</c>, repli <b>défaut Emby</b>
        /// <c>&lt;ProgramData&gt;/data/livetv/recordings</c> (le dossier que
        /// Emby utilise quand aucun chemin n'est configuré — constaté sur ce
        /// serveur). Vide/inconnu → faux (les appelants traitent cela comme
        /// « gate non applicable »).
        /// <para><b>Accès Emby</b> : les options LiveTV ne sont PAS sur
        /// <c>ServerConfiguration</c> — c'est une configuration <b>nommée</b>,
        /// clé <c>livetv</c> (confirmé par réflexion du serveur live :
        /// <c>LiveTvConfigurationFactory → ConfigurationStore Key="livetv"</c>),
        /// lue via <c>IConfigurationManager.GetConfiguration("livetv")</c>.</para>
        /// </summary>
        internal static bool TryResolveRecordingPath(
            IServerApplicationHost host, ILogger logger, out string path)
        {
            path = null;
            try
            {
                var cm = host?.TryResolve<MediaBrowser.Common.Configuration.IConfigurationManager>();
                var opts = cm?.GetConfiguration("livetv") as MediaBrowser.Model.LiveTv.LiveTvOptions;
                path = FirstNonEmpty(opts?.RecordingPath, opts?.MovieRecordingPath, opts?.SeriesRecordingPath);
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Disque enregistrements : lecture LiveTvOptions (config nommée « livetv ») échouée : {0}", ex.Message);
                return false;
            }
            if (string.IsNullOrWhiteSpace(path))
            {
                // Défaut Emby quand aucun chemin n'est configuré :
                // <ProgramData>/data/livetv/recordings (constaté sur ce serveur).
                var appPaths = host?.TryResolve<MediaBrowser.Common.Configuration.IApplicationPaths>()
                    ?? host as MediaBrowser.Common.Configuration.IApplicationPaths;
                string dataRoot = null;
                try { dataRoot = appPaths?.DataPath; }
                catch (Exception ex) { logger?.Warn("[LLM_AI] Disque enregistrements : DataPath illisible : {0}", ex.Message); }
                if (!string.IsNullOrWhiteSpace(dataRoot))
                {
                    path = Path.Combine(dataRoot, "livetv", "recordings");
                    logger?.Info("[LLM_AI] Disque enregistrements : aucun chemin configuré — repli sur le défaut Emby « {0} ».", path);
                }
            }
            if (string.IsNullOrWhiteSpace(path))
            {
                logger?.Warn("[LLM_AI] Disque enregistrements : chemin d'enregistrements indéterminable — gate disque et passe de tag inapplicables.");
                return false;
            }
            return true;
        }

        /// <summary>Premier chemin non vide/blanc de la liste, sinon null.</summary>
        private static string FirstNonEmpty(params string[] paths)
        {
            foreach (var p in paths ?? Array.Empty<string>())
                if (!string.IsNullOrWhiteSpace(p)) return p;
            return null;
        }

        /// <summary>
        /// Indique si l'espace libre du volume hébergeant <paramref name="path"/>
        /// est sous <c>cfg.RecordingDiskThresholdGb</c>. Échoue OUVERT : chemin
        /// nul, volume introuvable ou lecture DriveInfo en échec → « non sous
        /// le seuil » (on ne bloque jamais l'enregistrement sur une
        /// indétermination). <paramref name="freeBytes"/> = -1 si indéterminé.
        /// </summary>
        internal static bool IsBelowThreshold(
            PluginConfiguration cfg, string path, ILogger logger,
            out long freeBytes, out long thresholdBytes)
        {
            freeBytes = -1;
            thresholdBytes = 0;
            int gb = cfg?.RecordingDiskThresholdGb ?? 0;
            if (gb <= 0 || string.IsNullOrWhiteSpace(path)) return false;

            thresholdBytes = (long)gb * GiB;
            try
            {
                var drive = DriveInfo.GetDrives()
                    .Where(d => d.IsReady)
                    .OrderByDescending(d => d.Name.Length)  // préfixe le plus spécifique
                    .FirstOrDefault(d => path.StartsWith(d.Name, StringComparison.OrdinalIgnoreCase));
                if (drive == null)
                {
                    logger?.Warn("[LLM_AI] Disque enregistrements : aucun volume local ne préfixe « {0} » — gate disque inapplicable (échoue ouvert).", path);
                    return false;
                }
                freeBytes = drive.AvailableFreeSpace;
                return freeBytes < thresholdBytes;
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Disque enregistrements : lecture du volume de « {0} » échouée : {1} — gate disque inapplicable (échoue ouvert).", path, ex.Message);
                return false;
            }
        }

        // ------------------------------------------------------------------
        //  Passe d'étiquetage (clear-first + suggestion de suppression)
        // ------------------------------------------------------------------

        /// <summary>
        /// Exécute la passe complète (voir doc de classe) :
        /// (1) retire tous les tags <see cref="DeleteGenre"/> précédents,
        /// (2) si l'espace libre est sous le seuil et que le tagging est activé,
        /// re-tag les enregistrements visionnés, du plus ancien au plus récent,
        /// jusqu'à l'objectif <c>libre + Σ ≥ seuil × marge</c>, puis notifie.
        /// Best-effort de bout en bout : ne lève jamais vers l'appelant.
        /// </summary>
        /// <param name="recPath">Chemin d'enregistrements résolu (
        /// <see cref="TryResolveRecordingPath"/>) — peut être nul : la passe
        /// ne fait alors rien.</param>
        internal static async Task RunTagPassAsync(
            ILibraryManager library, string recPath,
            IUserManager users, INotificationManager notifications,
            IServerApplicationHost host, ILogger logger,
            PluginConfiguration cfg, CancellationToken ct)
        {
            try
            {
                if (library == null || string.IsNullOrWhiteSpace(recPath))
                {
                    logger?.Info("[LLM_AI] Passe tag disque : chemin d'enregistrements non résolu — ignorée.");
                    return;
                }

                // (1) Clear-first : chaque passe repart de zéro. Les tags
                //     reflètent toujours la DERNIÈRE évaluation — si l'usager
                //     a libéré de l'espace, tout disparaît ici.
                await AiGenreTagger.RemoveAllAsync(library, logger, DeleteGenre, ct).ConfigureAwait(false);

                // (2) Re-tag seulement si le seuil est réellement franchi et
                //     que la suggestion est activée.
                if (cfg?.RecordingTaggingEnabled != true)
                {
                    logger?.Info("[LLM_AI] Passe tag disque : suggestion désactivée — tags réinitialisés, rien re-tagué.");
                    return;
                }
                if (!IsBelowThreshold(cfg, recPath, logger, out long freeBytes, out long thresholdBytes))
                {
                    logger?.Info("[LLM_AI] Passe tag disque : au-dessus du seuil ({0} Go libre) — tags réinitialisés, rien re-tagué.",
                        freeBytes >= 0 ? (freeBytes / (double)GiB).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) : "?");
                    return;
                }

                // Candidats : enregistrements VUS (par n'importe quel usager)
                // sous le chemin d'enregistrements. IsPlayed est par usager →
                // union des requêtes jouées par usager.
                var played = new Dictionary<Guid, BaseItem>();
                List<MediaBrowser.Controller.Entities.User> userList;
                try { userList = users?.GetUserList(new UserQuery()).ToList(); }
                catch (Exception ex)
                {
                    logger?.Warn("[LLM_AI] Passe tag disque : lecture des usagers échouée : {0}", ex.Message);
                    return;
                }
                foreach (var u in userList ?? new List<MediaBrowser.Controller.Entities.User>())
                {
                    if (u == null) continue;
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var q = new InternalItemsQuery
                        {
                            User = u,
                            IsPlayed = true,
                            PathStartsWith = recPath,
                            Recursive = true,
                            EnableTotalRecordCount = false
                        };
                        foreach (var it in library.GetItemList(q) ?? Array.Empty<BaseItem>())
                        {
                            if (it == null || it.Id == Guid.Empty || played.ContainsKey(it.Id)) continue;
                            played[it.Id] = it;
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.Warn("[LLM_AI] Passe tag disque : requête joués (usager {0}) échouée : {1}", u.Name, ex.Message);
                    }
                }

                if (played.Count == 0)
                {
                    logger?.Warn("[LLM_AI] Passe tag disque : {0} Go libre sous le seuil ({1} Go) mais AUCUN enregistrement visionné à suggérer — action manuelle requise (supprimer des enregistrements, réduire les timers).",
                        (freeBytes / (double)GiB).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture), (thresholdBytes / (double)GiB).ToString("0", System.Globalization.CultureInfo.InvariantCulture));
                    return;
                }

                // Du plus ancien au plus récent ; taille réelle du fichier
                // (un item sans fichier lisible est ignoré — on ne sait pas
                // l'estimer). Arrêt dès que l'objectif est atteint : la liste
                // taguée reste courte et délibérée.
                long target = (long)(thresholdBytes * MarginFactor);
                var picked = new List<BaseItem>();
                long reclaimed = 0;
                int unreadable = 0;
                foreach (var it in played.Values.OrderBy(i => i.DateCreated))
                {
                    long size = FileSizeOrNull(it.Path, logger);
                    if (size <= 0) { unreadable++; continue; }
                    picked.Add(it);
                    reclaimed += size;
                    if (freeBytes + reclaimed >= target) break;
                }

                var ids = picked.Select(i => i.Id.ToString()).ToList();
                await AiGenreTagger.AddAsync(library, logger, ids, DeleteGenre, ct).ConfigureAwait(false);

                bool covered = freeBytes + reclaimed >= target;
                string summary = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "[LLM_AI] Passe tag disque : {0} enregistrement(s) visionné(s) tagué(s) « {1} » (~{2} Go récupérables) — libre {3} Go / seuil {4} Go, objectif {5}.",
                    picked.Count, DeleteGenre,
                    (reclaimed / (double)GiB).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture),
                    (freeBytes / (double)GiB).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture),
                    (thresholdBytes / (double)GiB).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture),
                    covered ? "atteint" : "NON atteint (action manuelle requise)");
                if (unreadable > 0)
                    summary += string.Format(System.Globalization.CultureInfo.InvariantCulture, " {0} item(s) sans fichier lisible ignoré(s).", unreadable);
                logger?.Info(summary);

                NotifyTagPass(notifications, users, cfg, host, logger, picked.Count, reclaimed, covered);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger?.ErrorException("[LLM_AI] Passe tag disque : échec (ignoré) : {0}", ex, ex.Message);
            }
        }

        /// <summary>Taille du fichier si lisible, sinon 0 (best-effort).</summary>
        private static long FileSizeOrNull(string path, ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(path)) return 0;
            try
            {
                var fi = new FileInfo(path);
                return fi.Exists ? fi.Length : 0;
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Passe tag disque : taille de « {0} » illisible : {1}", path, ex.Message);
                return 0;
            }
        }

        // ------------------------------------------------------------------
        //  Notification (même pattern que LlmScheduledTask.SendFailureNotification)
        // ------------------------------------------------------------------

        /// <summary>
        /// Notifie tous les usagers : N enregistrements tagués « AI Delete »
        /// (~X Go récupérables) — filtrer la bibliothèque par ce genre pour
        /// supprimer à la main. <paramref name="covered"/> = false → mentionne
        /// que l'objectif n'est pas couvert par les seuls visionnés.
        /// </summary>
        private static void NotifyTagPass(
            INotificationManager notifications, IUserManager users,
            PluginConfiguration cfg, IServerApplicationHost host, ILogger logger,
            int count, long reclaimedBytes, bool covered)
        {
            if (notifications == null || count <= 0) return;

            string langKey = I18n.ResolveDisplayLangKey(host);
            string title = I18n.S("disktag.notif.title", langKey);
            string desc = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                I18n.S(covered ? "disktag.notif.desc" : "disktag.notif.desc.uncovered", langKey),
                count, DeleteGenre, reclaimedBytes / (double)GiB);
            string url = (cfg?.EmbyPublicUrl ?? string.Empty).Trim();

            List<MediaBrowser.Controller.Entities.User> list;
            try { list = users?.GetUserList(new UserQuery()).ToList(); }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Passe tag disque : lecture des usagers pour notification échouée : {0}", ex.Message);
                return;
            }

            int sent = 0;
            foreach (var u in list ?? new List<MediaBrowser.Controller.Entities.User>())
            {
                if (u == null) continue;
                try
                {
                    notifications.SendNotification(new NotificationRequest
                    {
                        Title = title,
                        Description = desc,
                        Url = url,
                        Date = DateTimeOffset.UtcNow,
                        Severity = LogSeverity.Warn,
                        User = u
                    });
                    sent++;
                }
                catch (Exception ex)
                {
                    logger?.Warn("[LLM_AI] Passe tag disque : notification à « {0} » échouée : {1}", u.Name, ex.Message);
                }
            }
            if (sent > 0)
                logger?.Info("[LLM_AI] Passe tag disque : notification envoyée à {0}/{1} usager(s).", sent, list?.Count ?? 0);
        }
    }
}