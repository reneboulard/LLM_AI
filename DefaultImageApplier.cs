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
    /// sur un <see cref="BaseItem"/> — la collection « AI Tonight » (BoxSet) et la racine de la
    /// bibliothèque <c>.strm</c> (CollectionFolder). L'image est embarquée comme ressource
    /// (<see cref="ResourceName"/>) : aucune fichier externe à livrer, présentation identique
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
        /// <see cref="IProviderManager.SaveImage"/> ; peut être null pour un BoxSet).</param>
        internal static async Task ApplyPrimaryIfMissingAsync(
            BaseItem item, IServerApplicationHost host, ILibraryManager library,
            ILogger logger, CancellationToken ct)
        {
            if (item == null || host == null || library == null) return;

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
                using var stream = typeof(DefaultImageApplier).Assembly.GetManifestResourceStream(ResourceName);
                if (stream == null)
                {
                    logger?.Warn("[LLM_AI] DefaultImage : ressource « {0} » introuvable dans l'assembly → image par défaut ignorée.", ResourceName);
                    return;
                }

                // DirectoryService éphémère (lecture FS cache par opération) ; GetLibraryOptions
                // peut renvoyer null (BoxSet non rattaché à une bibliothèque typée) — SaveImage
                // le tolère côté Emby.
                var dirSvc = new DirectoryService(fs);
                var libOpts = library.GetLibraryOptions(item);

                await providers.SaveImage(item, libOpts, stream, MimeType.AsMemory(),
                    ImageType.Primary, null, null, dirSvc, true, ct).ConfigureAwait(false);

                item.UpdateToRepository(ItemUpdateType.ImageUpdate);
                logger?.Info("[LLM_AI] DefaultImage : poster Primary posé sur « {0} ».", item.Name);
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] DefaultImage : échec sur « {0} » : {1}", item?.Name, ex.Message);
            }
        }
    }
}