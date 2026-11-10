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
    /// fichier : le tag <see cref="DeleteTag"/> est une suggestion que
    /// l'usager concrétise lui-même (filtre bibliothèque par tag,
    /// multi-sélection, suppression). Une passe déclenchée alors que l'espace
    /// s'est libéré ne fait donc que retirer les tags obsolètes.</para>
    /// </summary>
    /// <remarks>
    /// <para><b>Scope</b> : seuls les items dont le fichier est sous l'une des
    /// racines d'enregistrements résolues (<c>LiveTvOptions</c> — chemins
    /// string 4.9.x, ids de dossiers 4.10+, repli défaut Emby
    /// <c>&lt;ProgramData&gt;/data/livetv/recordings</c> — voir
    /// <see cref="TryResolveRecordingPaths"/>) sont candidats au tag
    /// (v2.18.0 : une install peut avoir des racines films ET séries
    /// distinctes — l'ancienne forme singulière n'en voyait que la
    /// première).</para>
    /// <para><b>Persistance des tags</b> : réutilise
    /// <see cref="AiTagger.AddAsync"/>/<see cref="AiTagger.RemoveAllAsync"/>
    /// (même mécanique que le tag « AI Tonight » — le cleanup nocturne
    /// <c>AiTonightCleanupTask</c> ne touche pas à ce tag).</para>
    /// </remarks>
    internal static class RecordingDiskManager
    {
        /// <summary>
        /// Tag appliqué aux enregistrements suggérés à la suppression
        /// (ex-genre, migré par <c>AiTagger.RemoveAllAsync</c>).
        /// Distinct de « AI Tonight » / « AI Suggestion » pour garder tous les
        /// nettoyages indépendants.
        /// </summary>
        public const string DeleteTag = "AI Delete";

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
        /// Résout TOUTES les racines d'enregistrements DVR — chemins 4.9.x
        /// (<c>RecordingPath</c>/<c>MovieRecordingPath</c>/
        /// <c>SeriesRecordingPath</c>) ET ids de dossiers 4.10+
        /// (<c>RecordingFolderId</c>/<c>MovieRecordingFolderId</c>/
        /// <c>SeriesRecordingFolderId</c>), dédoublonnées.
        /// <para><b>Accès Emby</b> : les options LiveTV ne sont PAS sur
        /// <c>ServerConfiguration</c> — c'est une configuration <b>nommée</b>,
        /// clé <c>livetv</c> (confirmé par réflexion du serveur live :
        /// <c>LiveTvConfigurationFactory → ConfigurationStore Key="livetv"</c>),
        /// lue via <c>IConfigurationManager.GetConfiguration("livetv")</c>.</para>
        /// <para><b>Lecture par réflexion</b> : la forme de
        /// <c>LiveTvOptions</c> varie selon la build hôte — un accès compilé à
        /// une propriété absente (ex. <c>RecordingPath</c> retirée en 4.10)
        /// casse le JIT (MissingMethodException) et bloque toute la chaîne
        /// appelante (Activate, auto-program, passe de tag). Les propriétés
        /// sont donc lues par réflexion, et un id de dossier est résolu via
        /// <see cref="ItemIdResolver.Resolve"/> → <c>BaseItem.Path</c>.</para>
        /// <para><b>La forme PLURIELLE est la seule légitime</b> : la forme
        /// singulière historique ne renvoyait que la PREMIÈRE racine — une
        /// install avec dossiers films et séries distincts (constaté en
        /// production : <c>MovieRecordingPath</c> et
        /// <c>SeriesRecordingPath</c> tous deux définis) n'en exposait qu'une,
        /// et tout test d'appartenance « sous une racine DVR » ratait
        /// l'autre moitié des enregistrements. Défauts vécus de cette
        /// classe : médiathèque d'enregistrements TV non exclue d'office du
        /// sélecteur LibReview (v2.16.2) ; filet d'import du
        /// RecordingWatcher n'auditant jamais les enregistrements séries
        /// (cas « De souche », v2.18.0) ; passe de tag AI Delete, surface
        /// chat « recordings », page cross-kind et recoupement des stats
        /// d'audit ne voyant que la première racine (v2.18.0). La forme
        /// singulière est SUPPRIMÉE depuis v2.18.0 : plus aucun appelant ne
        /// doit renaître au singulier.</para>
        /// Repli : si AUCUNE racine n'est configurée, le défaut Emby
        /// « &lt;DataPath&gt;/livetv/recordings » (le dossier qu'Emby utilise
        /// quand aucun chemin n'est configuré — constaté sur ce serveur).
        /// Vide/inconnu → faux (les appelants traitent cela comme « non
        /// applicable »).</summary>
        internal static bool TryResolveRecordingPaths(
            IServerApplicationHost host, ILogger logger, out List<string> paths)
        {
            paths = new List<string>();
            try
            {
                var cm = host?.TryResolve<MediaBrowser.Common.Configuration.IConfigurationManager>();
                var opts = cm?.GetConfiguration("livetv") as MediaBrowser.Model.LiveTv.LiveTvOptions;
                if (opts != null)
                {
                    // Chemins string (4.9.x — conservés par les upgrades : la
                    // config XML n'est pas migrée vers les ids).
                    AddPath(paths, OptString(opts, "RecordingPath"));
                    AddPath(paths, OptString(opts, "MovieRecordingPath"));
                    AddPath(paths, OptString(opts, "SeriesRecordingPath"));
                    // Ids de dossiers de bibliothèque (4.10+).
                    var lib = host?.TryResolve<ILibraryManager>();
                    foreach (var idProp in new[] { "RecordingFolderId", "MovieRecordingFolderId", "SeriesRecordingFolderId" })
                    {
                        var raw = OptString(opts, idProp);
                        if (string.IsNullOrWhiteSpace(raw) || raw == Guid.Empty.ToString()) continue;
                        var folderPath = ItemIdResolver.Resolve(lib, raw)?.Path;
                        if (!string.IsNullOrWhiteSpace(folderPath)) AddPath(paths, folderPath);
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Disque enregistrements : lecture LiveTvOptions (config nommée « livetv ») échouée : {0}", ex.Message);
                return false;
            }
            if (paths.Count == 0)
            {
                // Défaut Emby quand aucun chemin n'est configuré — repli
                // historique de la forme singulière d'origine.
                var appPaths = host?.TryResolve<MediaBrowser.Common.Configuration.IApplicationPaths>()
                    ?? host as MediaBrowser.Common.Configuration.IApplicationPaths;
                string dataRoot = null;
                try { dataRoot = appPaths?.DataPath; }
                catch (Exception ex) { logger?.Warn("[LLM_AI] Disque enregistrements : DataPath illisible : {0}", ex.Message); }
                if (!string.IsNullOrWhiteSpace(dataRoot))
                {
                    var def = Path.Combine(dataRoot, "livetv", "recordings");
                    paths.Add(def);
                    logger?.Info("[LLM_AI] Disque enregistrements : aucun chemin configuré — repli sur le défaut Emby « {0} ».", def);
                }
            }
            if (paths.Count == 0)
            {
                logger?.Warn("[LLM_AI] Disque enregistrements : chemin d'enregistrements indéterminable — racines DVR inapplicables.");
                return false;
            }
            return true;
        }

        /// <summary>Ajoute un chemin non vide, dédoublonné (casse indifférente).</summary>
        private static void AddPath(List<string> paths, string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            var t = p.Trim();
            if (!paths.Contains(t, StringComparer.OrdinalIgnoreCase)) paths.Add(t);
        }

        /// <summary>
        /// Lecture d'une propriété par réflexion, tolérante aux builds :
        /// renvoie null si la propriété n'existe pas (forme de
        /// <c>LiveTvOptions</c> différente sur une autre build hôte) — jamais
        /// de MissingMethodException, le JIT n'ayant aucune référence compilée.
        /// Gère string (chemins 4.9.x) et types-valeur (ids Guid 4.10+).
        /// </summary>
        private static string OptString(object obj, string propertyName)
        {
            try
            {
                var prop = obj?.GetType().GetProperty(propertyName);
                if (prop == null || !prop.CanRead) return null;
                return Convert.ToString(prop.GetValue(obj, null));
            }
            catch { return null; }
        }

        /// <summary>
        /// Gate disque MULTI-RACINES (v2.18.0) : mesure l'espace libre du volume
        /// de CHAQUE racine (dédoublonné par volume — sur une install, les
        /// racines films et séries vivent souvent sur le MÊME disque, inutile
        /// de le mesurer deux fois) et renvoie true si N'IMPORTE QUEL volume
        /// est sous le seuil ; <paramref name="freeBytes"/> rapporte le volume
        /// le PLUS CONTRAINT. L'ancienne forme singulière ne mesurait que la
        /// première racine : sur une install à racines sur des volumes
        /// DISTINCTS, un volume plein passait inaperçu. Échoue OUVERT : racine
        /// nulle/volume introuvable/lecture en échec → racine ignorée une à
        /// une ; plus aucune racine mesurable → « non sous le seuil » (on ne
        /// bloque jamais l'enregistrement sur une indétermination).
        /// <paramref name="freeBytes"/> = -1 si indéterminé.
        /// </summary>
        internal static bool IsBelowThreshold(
            PluginConfiguration cfg, IEnumerable<string> paths, ILogger logger,
            out long freeBytes, out long thresholdBytes)
        {
            freeBytes = -1;
            thresholdBytes = 0;
            int gb = cfg?.RecordingDiskThresholdGb ?? 0;
            if (gb <= 0 || paths == null) return false;

            thresholdBytes = (long)gb * GiB;
            var seenVolumes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool measured = false;
            bool below = false;
            long worst = long.MaxValue;
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                try
                {
                    var drive = DriveInfo.GetDrives()
                        .Where(d => d.IsReady)
                        .OrderByDescending(d => d.Name.Length)  // préfixe le plus spécifique
                        .FirstOrDefault(d => path.StartsWith(d.Name, StringComparison.OrdinalIgnoreCase));
                    if (drive == null)
                    {
                        logger?.Warn("[LLM_AI] Disque enregistrements : aucun volume local ne préfixe « {0} » — racine ignorée (échoue ouvert).", path);
                        continue;
                    }
                    if (!seenVolumes.Add(drive.Name)) continue; // même volume : déjà mesuré
                    measured = true;
                    if (drive.AvailableFreeSpace < worst) worst = drive.AvailableFreeSpace;
                    if (drive.AvailableFreeSpace < thresholdBytes) below = true;
                }
                catch (Exception ex)
                {
                    logger?.Warn("[LLM_AI] Disque enregistrements : lecture du volume de « {0} » échouée : {1} — racine ignorée (échoue ouvert).", path, ex.Message);
                }
            }
            if (!measured) return false;
            freeBytes = worst;
            return below;
        }

        // ------------------------------------------------------------------
        //  Passe d'étiquetage (clear-first + suggestion de suppression)
        // ------------------------------------------------------------------

        /// <summary>
        /// Exécute la passe complète (voir doc de classe) :
        /// (1) retire tous les tags <see cref="DeleteTag"/> précédents,
        /// (2) si l'espace libre est sous le seuil et que le tagging est activé,
        /// re-tag les enregistrements visionnés, du plus ancien au plus récent,
        /// jusqu'à l'objectif <c>libre + Σ ≥ seuil × marge</c>, puis notifie.
        /// Best-effort de bout en bout : ne lève jamais vers l'appelant.
        /// </summary>
        /// <param name="recPaths">Racines d'enregistrements résolues (
        /// <see cref="TryResolveRecordingPaths"/>) — liste vide/nulle : la
        /// passe ne fait alors rien (le clear-first reste global).</param>
        internal static async Task RunTagPassAsync(
            ILibraryManager library, IEnumerable<string> recPaths,
            IUserManager users, INotificationManager notifications,
            IServerApplicationHost host, ILogger logger,
            PluginConfiguration cfg, CancellationToken ct)
        {
            try
            {
                var roots = (recPaths ?? Array.Empty<string>())
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => p.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (library == null || roots.Count == 0)
                {
                    logger?.Info("[LLM_AI] Passe tag disque : chemin d'enregistrements non résolu — ignorée.");
                    return;
                }

                // (1) Clear-first : chaque passe repart de zéro. Les tags
                //     reflètent toujours la DERNIÈRE évaluation — si l'usager
                //     a libéré de l'espace, tout disparaît ici.
                await AiTagger.RemoveAllAsync(library, logger, DeleteTag, ct).ConfigureAwait(false);

                // (2) Re-tag seulement si le seuil est réellement franchi et
                //     que la suggestion est activée.
                if (cfg?.RecordingTaggingEnabled != true)
                {
                    logger?.Info("[LLM_AI] Passe tag disque : suggestion désactivée — tags réinitialisés, rien re-tagué.");
                    return;
                }
                if (!IsBelowThreshold(cfg, roots, logger, out long freeBytes, out long thresholdBytes))
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
                    // Multi-racines (v2.18.0) : une requête par usager PAR
                    // racine — les enregistrements des racines films ET séries
                    // sont tous candidats (PathStartsWith n'accepte qu'un
                    // préfixe). Le dictionnaire dédoublonne les items (un item
                    // ne vit que sous une racine, mais deux préfixes peuvent
                    // se chevaucher en config tordue).
                    foreach (var root in roots)
                    {
                        if (string.IsNullOrWhiteSpace(root)) continue;
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            var q = new InternalItemsQuery
                            {
                                User = u,
                                IsPlayed = true,
                                PathStartsWith = root,
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
                            logger?.Warn("[LLM_AI] Passe tag disque : requête joués (usager {0}, racine {1}) échouée : {2}", u.Name, root, ex.Message);
                        }
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
                await AiTagger.AddAsync(library, logger, ids, DeleteTag, ct).ConfigureAwait(false);

                bool covered = freeBytes + reclaimed >= target;
                string summary = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "[LLM_AI] Passe tag disque : {0} enregistrement(s) visionné(s) tagué(s) « {1} » (~{2} Go récupérables) — libre {3} Go / seuil {4} Go, objectif {5}.",
                    picked.Count, DeleteTag,
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
        /// Notifie les administrateurs : N enregistrements tagués « AI Delete »
        /// (~X Go récupérables) — filtrer la bibliothèque par ce tag pour
        /// supprimer à la main (action admin — même destinataire que la
        /// cloche cross-kind). <paramref name="covered"/> = false → mentionne
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
                count, DeleteTag, reclaimedBytes / (double)GiB);
            string url = (cfg?.EmbyPublicUrl ?? string.Empty).Trim();

            // Lien aussi en description (v2.4.5 : les canaux de lecture —
            // webhooks, courriel — ignorent le champ Url d'Emby).
            if (!string.IsNullOrWhiteSpace(url))
                desc = desc + "\n" + url;

            // Administrateurs uniquement : le guidage (filtrer la
            // bibliothèque d'enregistrements par le tag puis supprimer à la
            // main) est une action admin — même motif que la cloche
            // cross-kind (OrphanResolver.NotifyCrossKindAsync).
            List<MediaBrowser.Controller.Entities.User> list;
            try
            {
                list = (users?.GetUserList(new UserQuery())
                        ?? Enumerable.Empty<MediaBrowser.Controller.Entities.User>())
                    .Where(u => u != null && (u.Policy?.IsAdministrator ?? false))
                    .ToList();
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Passe tag disque : lecture des usagers pour notification échouée : {0}", ex.Message);
                return;
            }

            int sent = 0;
            foreach (var u in list)
            {
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
                logger?.Info("[LLM_AI] Passe tag disque : notification envoyée à {0}/{1} administrateur(s).", sent, list.Count);
            else
                logger?.Warn("[LLM_AI] Passe tag disque : notification NON envoyée — aucun administrateur.");
        }
    }
}