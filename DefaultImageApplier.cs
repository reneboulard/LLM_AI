using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;

namespace LLM_AI
{
    /// <summary>
    /// Pose une <b>image par défaut standardisée</b> (poster <see cref="ImageType.Primary"/>)
    /// sur un <see cref="BaseItem"/> — la collection « AI Tonight » (BoxSet), la racine de la
    /// bibliothèque <c>.strm</c> (CollectionFolder) et les playlists « AI Tonight » (publique
    /// foyer + privées par usager, recréées sans image à chaque run). Deux ressources embarquées
    /// selon la cible :
    /// <see cref="ResourceName"/> (400×600 portrait, BoxSet + playlists) et
    /// <see cref="LibraryResourceName"/>
    /// (640×360 16:9, bibliothèque) — aucun fichier externe à livrer, présentation identique
    /// partout. <b>Idempotent</b> : ne pose l'image que si l'item n'en a pas déjà une
    /// (<see cref="BaseItem.HasImage"/> == false) — une attribution manuelle ultérieure dans
    /// « Edit Images » est respectée (jamais écrasée au run suivant).
    /// </summary>
    /// <remarks>
    /// <para><b>API Emby</b> : <see cref="IProviderManager.SaveImage(BaseItem, LibraryOptions,
    /// Stream, ReadOnlyMemory{char}, ImageType, Nullable{int}, long[], IDirectoryService,
    /// bool, CancellationToken)"/> (surcharge acceptant un <see cref="System.IO.Stream"/> —
    /// c'est le chemin qu'emprunte l'upload depuis « Edit Images ») sauve le fichier image au
    /// bon endroit, l'attache à l'item et met en cache ; puis
    /// <see cref="BaseItem.UpdateToRepository(ItemUpdateType.ImageUpdate)"/> persiste.</para>
    /// <para><b>Services résolus</b> via <see cref="IServerApplicationHost.TryResolve{T}"/>
    /// (même pattern que <c>SystemAuditTool</c> pour <c>IServerConfigurationManager</c>) :
    /// <see cref="IProviderManager"/> et <see cref="IFileSystem"/> (ce dernier pour construire
    /// un <see cref="DirectoryService"/> éphémère).</para>
    /// <para><b>Best-effort</b> : ne lève jamais. Un service indisponible, une ressource
    /// manquante ou un échec d'API Emby sont logués en <c>Warn</c> et n'interrompent pas
    /// l'appelant — la cible reste exploitable, simplement sans image par défaut.</para>
    /// </remarks>
    internal static class DefaultImageApplier
    {
        /// <summary>
        /// Nom logique de la ressource embedded contenant le poster par défaut.
        /// Convention <c>RootNamespace.fichier</c> (= <c>LLM_AI.default_poster.jpg</c>),
        /// identique à <c>LLM_AI.thumb.png</c> dans <c>Plugin.cs</c>.
        /// </summary>
        private const string ResourceName = "LLM_AI.default_poster.jpg";

        /// <summary>
        /// Ressource embedded pour la racine de la bibliothèque <c>.strm</c>
        /// (CollectionFolder) : 640×360 16:9 — format tuile de bibliothèque
        /// (le poster BoxSet portrait ne conviendrait pas à cette cible).
        /// </summary>
        internal const string LibraryResourceName = "LLM_AI.default_library.jpg";

        /// <summary>
        /// Type MIME du poster embarqué (JPEG). <see cref="SaveImage"/> attend un
        /// <see cref="ReadOnlyMemory{T}"/> de caractères — <see cref="MemoryExtensions.AsMemory"/>
        /// fait la conversion.
        /// </summary>
        private const string MimeType = "image/jpeg";

        /// <summary>
        /// Pose le poster <see cref="ImageType.Primary"/> par défaut sur
        /// <paramref name="item"/> si celui-ci n'a pas déjà d'image Primary. Best-effort,
        /// ne lève jamais. Renvoie <see cref="Task.CompletedTask"/> si un argument requis
        /// est nul ou si l'image est déjà présente.
        /// </summary>
        /// <param name="item">Cible (BoxSet de la collection ou CollectionFolder de la
        /// bibliothèque <c>.strm</c>). Null → no-op.</param>
        /// <param name="host">Hôte Emby pour résoudre <see cref="IProviderManager"/> et
        /// <see cref="IFileSystem"/> via <see cref="IServerApplicationHost.TryResolve{T}"/>.
        /// Null → no-op.</param>
        /// <param name="library"><see cref="ILibraryManager"/> pour
        /// <see cref="ILibraryManager.GetLibraryOptions(BaseItem)"/> (passé à
        /// <see cref="IProviderManager.SaveImage"/> ; un null éventuel est
        /// remplacé par un <see cref="LibraryOptions"/> vierge — le paramètre
        /// est déréférencé sans garde par Emby).</param>
        /// <param name="resourceName">Ressource embedded à poser — null (défaut)
        /// → poster BoxSet (<see cref="ResourceName"/>) ;
        /// <see cref="LibraryResourceName"/> pour la racine de bibliothèque.</param>
        internal static async Task ApplyPrimaryIfMissingAsync(
            BaseItem item, IServerApplicationHost host, ILibraryManager library,
            ILogger logger, CancellationToken ct, string resourceName = null)
        {
            if (item == null || host == null || library == null) return;
            string resName = string.IsNullOrEmpty(resourceName) ? ResourceName : resourceName;

            try
            {
                // Idempotent : si une image Primary existe déjà (posée manuellement ou par
                // un run précédent), on ne touche pas — respecte la personnalisation de l'usager.
                if (item.HasImage(ImageType.Primary, 0)) return;

                var providers = host.TryResolve<IProviderManager>();
                var fs = host.TryResolve<IFileSystem>();
                if (providers == null || fs == null)
                {
                    logger?.Warn("[LLM_AI] DefaultImage : IProviderManager/IFileSystem indispo → image par défaut ignorée pour « {0} ».", item.Name);
                    return;
                }

                // Ressource embedded : un Stream frais à chaque appel. N'arrive qu'à la
                // création d'une cible sans image (rare) → pas de mise en cache des octets.
                using var stream = typeof(DefaultImageApplier).Assembly.GetManifestResourceStream(resName);
                if (stream == null)
                {
                    logger?.Warn("[LLM_AI] DefaultImage : ressource « {0} » introuvable dans l'assembly → image par défaut ignorée.", resName);
                    return;
                }

                // DirectoryService éphémère (lecture FS cache par opération).
                // Deux pièges Emby 4.10 (décompilé Emby.Providers/ImageSaver
                // 4.10.0.40 ; NRE terrain aux runs 09:24/09:28) :
                // - generatedFromItemIds est déréférencé SANS garde
                //   (« generatedFromItemIds.Length », ImageSaver.SaveImage) :
                //   null y est fatal → Array.Empty<long>() (neutre : pas de
                //   suffixe auto_poster_ dans le nom de fichier).
                // - libraryOptions est déréférencé sans garde au premier appel
                //   (BaseItem.IsSaveLocalImagesEnabled →
                //   libraryOptions.SaveLocalMetadata) : garde null →
                //   LibraryOptions vierge (4.10 fabrique des défauts même pour
                //   un BoxSet, mais on ne parie pas sur les builds voisins).
                var dirSvc = new DirectoryService(fs);
                var libOpts = library.GetLibraryOptions(item)
                    ?? new MediaBrowser.Model.Configuration.LibraryOptions();

                await providers.SaveImage(item, libOpts, stream, MimeType.AsMemory(),
                    ImageType.Primary, null, Array.Empty<long>(), dirSvc, true, ct).ConfigureAwait(false);

                item.UpdateToRepository(ItemUpdateType.ImageUpdate);
                logger?.Info("[LLM_AI] DefaultImage : poster Primary posé sur « {0} ».", item.Name);
            }
            catch (Exception ex)
            {
                // Stack complète (ex.ToString) : le Message seul a coûté une
                // séance de décompilation pour diagnostiquer la NRE SaveImage
                // du 2026-09-28 — ne pas régresser là-dessus.
                logger?.Warn("[LLM_AI] DefaultImage : échec sur « {0} » : {1}", item?.Name, ex.ToString());
            }
        }
    }
}