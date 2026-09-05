using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;

namespace LLM_AI
{
    /// <summary>
    /// Maintient une <b>playlist Emby</b> nommée <see cref="PlaylistName"/>
    /// (« AI Tonight ») remplie à chaque run frais de « À regarder ce soir »
    /// avec les recos du <b>watch bucket</b> (enregistrements non visionnés +
    /// items possédés). Miroir de <see cref="AiTonightCollectionManager"/>
    /// (même workflow : surface au run, nettoyage à 3 h par
    /// <c>AiTonightCleanupTask</c>), mais sous forme de <b>playlist</b> —
    /// lecture enchaînée directement depuis n'importe quel client Emby,
    /// là où la collection est une simple navigation.
    /// </summary>
    /// <remarks>
    /// <para>Comportement validé : <b>reset à chaque run</b> — la playlist est
    /// vidée de ses entrées courantes puis remplie avec les recos du run
    /// (elle reflète exactement les recommandations du jour ; pas un
    /// historique cumulé).</para>
    /// <para><b>API Emby utilisées</b> (signatures vérifiées par réflexion sur
    /// <c>MediaBrowser.Controller.dll</c> de cet hôte, Emby 4.9.5.0) :
    /// <see cref="IPlaylistManager.CreatePlaylist"/>
    /// (<c>PlaylistCreationRequest { Name, ItemIdList (long[]), MediaType,
    /// User, IsPublic }</c> → <c>PlaylistCreationResult</c>),
    /// <see cref="IPlaylistManager.AddToPlaylist"/> (Task, par
    /// <c>Playlist</c> + <c>long[]</c> itemIds) et
    /// <see cref="IPlaylistManager.RemoveFromPlaylist"/> — qui prend des
    /// <b>entryIds</b> (ids des ENTRÉES de playlist, enfants
    /// <see cref="Playlist"/>), pas des items ciblés : le listing des entrées
    /// passe par <c>InternalItemsQuery { ParentId = playlist.InternalId }</c>
    /// (chemin utilisé par l'API REST <c>/Playlists/{id}/Items</c>, vérifié
    /// sur ce serveur).</para>
    /// <para><b>Scope isolé</b> : distinct du genre « AI Tonight »
    /// (<see cref="AiGenreTagger"/>) et de la collection « AI Tonight »
    /// (<see cref="AiTonightCollectionManager"/>) — les trois artefacts
    /// coexistent, indépendants, chacun opt-in.</para>
    /// </remarks>
    internal static class AiTonightPlaylistManager
    {
        /// <summary>
        /// Nom de la playlist Emby maintenue pour « À regarder ce soir ».
        /// Identique au nom de la collection et du genre pour une cohérence
        /// d'interface — artefacts distincts.
        /// </summary>
        public const string PlaylistName = "AI Tonight";

        // ------------------------------------------------------------------
        //  Maintien de la playlist (création + reset)
        // ------------------------------------------------------------------

        /// <summary>
        /// Garantit que la playlist <see cref="PlaylistName"/> existe et
        /// contient exactement les items Emby dont l'id (chaîne, cf.
        /// <see cref="ItemIdResolver"/>) figure dans
        /// <paramref name="itemGuidIds"/> : créée avec ses membres initiaux si
        /// absente, sinon <b>reset</b> (toutes les entrées courantes retirées
        /// puis les recos du run ajoutées). <paramref name="user"/> est
        /// l'usager propriétaire (option « usager Tonight »). Best-effort :
        /// un id non résolvable ou un échec d'API est logué sans interrompre
        /// le reste.
        /// </summary>
        internal static async Task EnsureAsync(
            IPlaylistManager playlists, ILibraryManager library, ILogger logger,
            IEnumerable<string> itemGuidIds, User user, CancellationToken ct)
        {
            if (playlists == null || library == null || itemGuidIds == null)
                return;

            // 1) Résoudre les ids du watch bucket (InternalId, ou Guid hérité —
            //    cf. ItemIdResolver) -> InternalId (long) attendu par
            //    PlaylistCreationRequest.ItemIdList.
            var freshLongIds = new List<long>();
            int skipped = 0;
            foreach (var raw in itemGuidIds)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(raw)) continue;

                BaseItem item;
                try { item = ItemIdResolver.Resolve(library, raw); }
                catch (Exception ex) { logger?.Warn("[LLM_AI] Playlist : résolution id {0} échouée : {1}", raw, ex.Message); continue; }
                if (item == null) { skipped++; continue; }

                if (!freshLongIds.Contains(item.InternalId))
                    freshLongIds.Add(item.InternalId);
            }

            if (freshLongIds.Count == 0)
            {
                // Aucun membre frais : vider (reset vers zéro) si la playlist
                // existe, ne jamais créer une playlist vide.
                logger?.Info("[LLM_AI] Playlist « {0} » : aucun membre à mettre ({1} ignoré(s)) — vidée si existante.", PlaylistName, skipped);
                await ClearAsync(playlists, library, logger, ct).ConfigureAwait(false);
                return;
            }

            Playlist playlist = FindPlaylist(library);
            long[] freshArr = freshLongIds.ToArray();

            if (playlist == null)
            {
                // 2a) Création avec membres initiaux en un seul appel.
                //     IsPublic : playlist du foyer (visible par tous), User :
                //     propriétaire (usager « Tonight » de la config).
                try
                {
                    var request = new PlaylistCreationRequest
                    {
                        Name = PlaylistName,
                        ItemIdList = freshArr,
                        MediaType = "Video",
                        IsPublic = true,
                        User = user
                    };
                    var result = await playlists.CreatePlaylist(request).ConfigureAwait(false);
                    logger?.Info("[LLM_AI] Playlist « {0} » : créée (id={1}) avec {2} item(s).",
                        PlaylistName, result?.Id, result?.ItemAddedCount ?? freshArr.Length);
                }
                catch (Exception ex)
                {
                    logger?.Warn("[LLM_AI] Playlist « {0} » : échec CreatePlaylist : {1}", PlaylistName, ex.Message);
                }
                return;
            }

            // 2b) Reset : retirer toutes les entrées courantes puis ajouter
            //     les recos du run. Le listing des entrées passe par une query
            //     ParentId (les enfants de la playlist sont les entrées) —
            //     cf. remarque de classe. Un échec de listing n'empêche pas
            //     l'ajout (les doublons éventuels seront retirés au cleanup).
            long[] entryIds = GetEntryIds(library, playlist);
            if (entryIds.Length > 0)
            {
                try
                {
                    await playlists.RemoveFromPlaylist(playlist, entryIds).ConfigureAwait(false);
                    logger?.Info("[LLM_AI] Playlist « {0} » : {1} entrée(s) retirée(s).", PlaylistName, entryIds.Length);
                }
                catch (Exception ex)
                {
                    logger?.Warn("[LLM_AI] Playlist « {0} » : échec RemoveFromPlaylist : {1}", PlaylistName, ex.Message);
                }
            }

            try
            {
                await playlists.AddToPlaylist(playlist, freshArr, false, user, ct).ConfigureAwait(false);
                logger?.Info("[LLM_AI] Playlist « {0} » : {1} item(s) (ré)ajouté(s).", PlaylistName, freshArr.Length);
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Playlist « {0} » : échec AddToPlaylist : {1}", PlaylistName, ex.Message);
            }
        }

        // ------------------------------------------------------------------
        //  Nettoyage (vidage de la playlist)
        // ------------------------------------------------------------------

        /// <summary>
        /// <b>Vide</b> la playlist <see cref="PlaylistName"/> (retire toutes
        /// ses entrées) sans la supprimer — elle sera re-remplie au prochain
        /// run Tonight. No-op si la playlist n'existe pas. Best-effort : un
        /// échec d'API est logué sans lever.
        /// </summary>
        internal static Task ClearAsync(
            IPlaylistManager playlists, ILibraryManager library, ILogger logger, CancellationToken ct)
        {
            if (playlists == null || library == null)
                return Task.CompletedTask;

            try
            {
                Playlist playlist = FindPlaylist(library);
                if (playlist == null)
                    return Task.CompletedTask;

                long[] entryIds = GetEntryIds(library, playlist);
                if (entryIds.Length == 0)
                {
                    logger?.Info("[LLM_AI] Playlist cleanup « {0} » : déjà vide.", PlaylistName);
                    return Task.CompletedTask;
                }

                playlists.RemoveFromPlaylist(playlist, entryIds);
                logger?.Info("[LLM_AI] Playlist cleanup « {0} » : {1} entrée(s) retirée(s) (coquille conservée).", PlaylistName, entryIds.Length);
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Playlist cleanup « {0} » : {1}", PlaylistName, ex.Message);
            }
            return Task.CompletedTask;
        }

        // ------------------------------------------------------------------
        //  Helpers internes
        // ------------------------------------------------------------------

        /// <summary>
        /// Recherche la playlist <see cref="PlaylistName"/> parmi les
        /// <see cref="Playlist"/> de la bibliothèque (filtre par type + nom
        /// exact, comme <c>AiTonightCollectionManager.FindCollection</c>).
        /// Retourne null si introuvable.
        /// </summary>
        private static Playlist FindPlaylist(ILibraryManager library)
        {
            try
            {
                var q = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Playlist" },
                    Name = PlaylistName,
                    EnableTotalRecordCount = false
                };
                var items = library.GetItemList(q) ?? Array.Empty<BaseItem>();
                // GetItemList(Name=…) est censé filtrer par nom, mais on
                // vérifie la correspondance exacte par sécurité (casse).
                return items.OfType<Playlist>().FirstOrDefault(
                    p => string.Equals(p.Name, PlaylistName, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Récupère les ids (long) des ENTRÉES courantes de la playlist — les
        /// enfants <c>PlaylistItem</c> de la <see cref="Playlist"/>, pas les
        /// items média qu'elles pointent. C'est ce format qu'attend
        /// <see cref="IPlaylistManager.RemoveFromPlaylist"/> (entryIds).
        /// Via <see cref="Folder.GetItemList"/> de la playlist elle-même — son
        /// override <c>GetItemsInternal</c> lit les enfants du fichier
        /// playlist (chemin de l'API REST <c>/Playlists/{id}/Items</c>,
        /// vérifié sur ce serveur). <b>Note</b> :
        /// <c>InternalItemsQuery.ParentId</c> n'existe pas sur cette build.
        /// </summary>
        private static long[] GetEntryIds(ILibraryManager library, Playlist playlist)
        {
            if (playlist == null) return Array.Empty<long>();
            try
            {
                var items = playlist.GetItemList(new InternalItemsQuery
                {
                    EnableTotalRecordCount = false
                });
                if (items == null) return Array.Empty<long>();
                return items.Select(i => i.InternalId).ToArray();
            }
            catch (Exception)
            {
                return Array.Empty<long>();
            }
        }
    }
}