using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.TV;
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
    /// <para>Comportement (v1.13.2) : <b>détruite puis recréée à chaque
    /// run</b> — elle reflète exactement les recommandations du jour, sans
    /// accumulation. Deux quirks de l'API playlist Emby 4.9.5.0 rendent
    /// l'ancien « retirer tout + ré-ajouter » inopérant (vécu 2026-09-06 :
    /// 403 entrées, 73 titres dupliqués ×6) :
    /// <list type="bullet">
    /// <item><c>RemoveFromPlaylist</c> ne retire RIEN (no-op silencieux en
    /// interne ; via REST, <c>DELETE /Playlists/{id}/Items?Ids=…</c> → HTTP
    /// 500 SQLiteException, <c>EntryIds=…</c> → 204 sans effet).</item>
    /// <item>Une <b>série</b> ou saison ajoutée à une playlist (création ou
    /// ajout) est développée en TOUS ses épisodes (vérifié : un id série →
    /// 52 entrées).</item>
    /// </list>
    /// D'où : suppression de la coquille (<c>ILibraryManager.DeleteItem</c> +
    /// <c>DeleteFileLocation</c>, le .m3u aussi) + recréation, et
    /// normalisation des recos en <b>feuilles</b> (série/saison → épisode
    /// « next up » non vu, cf. <see cref="ResolveLeafIds"/>).</para>
    /// <para><b>API Emby utilisées</b> (signatures vérifiées par réflexion sur
    /// <c>MediaBrowser.Controller.dll</c> de cet hôte) :
    /// <see cref="IPlaylistManager.CreatePlaylist"/>
    /// (<c>PlaylistCreationRequest { Name, ItemIdList (long[]), MediaType,
    /// User, IsPublic }</c> → <c>PlaylistCreationResult</c>),
    /// <see cref="IPlaylistManager.AddToPlaylist"/>,
    /// <c>ITVSeriesManager.GetNextUp</c> (next up par série, usager),
    /// <c>ILibraryManager.DeleteItem</c>. Le listing des entrées passe par
    /// <c>Playlist.GetItemList</c> (chemin de l'API REST
    /// <c>/Playlists/{id}/Items</c>).</para>
    /// <para><b>Scope isolé</b> : distinct du tag « AI Tonight »
    /// (<see cref="AiTagger"/>) et de la collection « AI Tonight »
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
        /// Garantit que la playlist <see cref="PlaylistName"/> contient
        /// EXACTEMENT les recos fraîches du run : <b>détruite puis recréée</b>
        /// à chaque appel. <paramref name="itemGuidIds"/> (ids du watch
        /// bucket, cf. <see cref="ItemIdResolver"/>) est d'abord normalisé
        /// en <b>feuilles</b> via <see cref="ResolveLeafIds"/> : une reco
        /// série/saison devient son épisode « next up » non vu (usager
        /// Tonight), pas la série entière (Emby développerait la série en
        /// TOUS ses épisodes à l'ajout — cf. remarque de classe).
        /// <paramref name="user"/> est l'usager propriétaire. Best-effort :
        /// un id non résolvable ou un échec d'API est logué sans lever.
        /// </summary>
        internal static async Task EnsureAsync(
            IPlaylistManager playlists, ILibraryManager library, ILogger logger,
            IEnumerable<string> itemGuidIds, User user, IServerApplicationHost host, CancellationToken ct)
        {
            if (playlists == null || library == null || itemGuidIds == null)
                return;

            var freshLongIds = ResolveLeafIds(library, host, itemGuidIds, user, logger, ct);

            // 1) Destruction systématique de la playlist existante :
            //    RemoveFromPlaylist est INOPÉRANT sur ce build Emby (4.9.5.0 —
            //    SQLiteException en REST, no-op en interne ; vécu 2026-09-06 :
            //    403 entrées dupliquées ×6). Détruire + recréer est le seul
            //    reset fiable. La coquille change d'id à chaque run : sans
            //    importance (retrouvée par nom, cf. FindPlaylist).
            DestroyPlaylist(library, logger);

            if (freshLongIds.Count == 0)
            {
                // Aucun membre frais : rester à zéro (ne jamais recréer vide).
                logger?.Info("[LLM_AI] Playlist « {0} » : aucun membre frais — playlist absente/supprimée.", PlaylistName);
                return;
            }

            // 2) Création avec les membres fraîchs (feuilles uniquement).
            try
            {
                var request = new PlaylistCreationRequest
                {
                    Name = PlaylistName,
                    ItemIdList = freshLongIds.ToArray(),
                    MediaType = "Video",
                    IsPublic = true,
                    User = user
                };
                var result = await playlists.CreatePlaylist(request).ConfigureAwait(false);
                logger?.Info("[LLM_AI] Playlist « {0} » : recréée (id={1}) avec {2} entrée(s).",
                    PlaylistName, result?.Id, result?.ItemAddedCount ?? freshLongIds.Count);
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Playlist « {0} » : échec CreatePlaylist : {1}", PlaylistName, ex.Message);
            }
        }

        // ------------------------------------------------------------------
        //  Ajout additif (chat, v1.13) — SANS reset
        // ------------------------------------------------------------------

        /// <summary>
        /// Ajoute des items à la playlist <see cref="PlaylistName"/> SANS
        /// reset (additif pur) — contrairement à <see cref="EnsureAsync"/> qui
        /// remplace tout le contenu. Hygiène (v1.13.2) : les ids sont d'abord
        /// normalisés en <b>feuilles</b> via <see cref="ResolveLeafIds"/>
        /// (série/saison → épisode next up), puis <b>dédupliqués</b> contre
        /// les entrées courantes (un item déjà présent n'est pas re-ajouté).
        /// Crée la playlist (mêmes options : publique, <paramref name="user"/>
        /// propriétaire) si absente ; sinon <c>AddToPlaylist</c> sur la
        /// coquille existante. Retourne les ids <b>réellement ajoutés</b>
        /// (vérifiés par re-listing des entrées après l'appel). Best-effort :
        /// un échec d'API est logué sans lever. Utilisé par la couche d'action
        /// du chat (<c>ChatActions</c>).
        /// </summary>
        internal static async Task<List<long>> AddItemsAsync(
            IPlaylistManager playlists, ILibraryManager library, ILogger logger,
            IEnumerable<string> itemIds, User user, IServerApplicationHost host, CancellationToken ct)
        {
            if (playlists == null || library == null || itemIds == null)
                return new List<long>();

            var freshLongIds = ResolveLeafIds(library, host, itemIds, user, logger, ct);
            if (freshLongIds.Count == 0) return new List<long>();

            Playlist playlist = FindPlaylist(library);
            if (playlist != null)
            {
                // Dédup : ne soumettre que ce qui n'est PAS déjà une entrée
                // (une reco re-proposée par plusieurs runs ne duplique plus).
                var existing = new HashSet<long>(GetEntryIds(library, playlist));
                freshLongIds = freshLongIds.Where(id => !existing.Contains(id)).ToList();
                if (freshLongIds.Count == 0)
                {
                    logger?.Info("[LLM_AI] Playlist « {0} » : ajout (chat) — tous les items déjà présents.", PlaylistName);
                    return new List<long>();
                }
            }

            var arr = freshLongIds.ToArray();
            try
            {
                if (playlist == null)
                {
                    var request = new PlaylistCreationRequest
                    {
                        Name = PlaylistName,
                        ItemIdList = arr,
                        MediaType = "Video",
                        IsPublic = true,
                        User = user
                    };
                    var result = await playlists.CreatePlaylist(request).ConfigureAwait(false);
                    logger?.Info("[LLM_AI] Playlist « {0} » : créée (chat, id={1}) avec {2} item(s).",
                        PlaylistName, result?.Id, result?.ItemAddedCount ?? arr.Length);
                }
                else
                {
                    await playlists.AddToPlaylist(playlist, arr, false, user, ct).ConfigureAwait(false);
                    logger?.Info("[LLM_AI] Playlist « {0} » : {1} item(s) ajouté(s) (chat).", PlaylistName, arr.Length);
                }

                // Comptage honnête : re-listing des entrées après l'appel
                // (un id non appliqué par Emby ne sera pas compté).
                playlist = FindPlaylist(library) ?? playlist;
                var after = new HashSet<long>(GetEntryIds(library, playlist));
                return arr.Where(id => after.Contains(id)).ToList();
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Playlist « {0} » : échec ajout (chat) : {1}", PlaylistName, ex.Message);
                return new List<long>();
            }
        }

        /// <summary>
        /// Retire des items de la playlist <see cref="PlaylistName"/> —
        /// <b>par InternalId d'item</b> : les ids demandés sont vérifiés
        /// contre le listing des entrées courantes et SEULS les ids présents
        /// dans ce listing sont passés à <c>RemoveFromPlaylist</c>. Le
        /// résultat est <b>vérifié par re-listing</b> après l'appel :
        /// <c>RemoveFromPlaylist</c> est INOPÉRANT sur ce build Emby
        /// (4.9.5.0 — cf. remarque de classe), le retour ne compte donc que
        /// les ids réellement disparus du listing (0 sur ce build). Retourne
        /// la liste des ids (normalisés) réellement retirés. Best-effort, ne
        /// lève jamais. Utilisé par la couche d'action du chat.
        /// </summary>
        internal static async Task<List<long>> RemoveItemsAsync(
            IPlaylistManager playlists, ILibraryManager library, ILogger logger,
            IEnumerable<string> itemIds, CancellationToken ct)
        {
            if (playlists == null || library == null || itemIds == null)
                return new List<long>();

            Playlist playlist = FindPlaylist(library);
            if (playlist == null) return new List<long>();

            long[] entryIds = GetEntryIds(library, playlist);
            if (entryIds.Length == 0) return new List<long>();

            var wanted = new List<long>();
            foreach (var raw in itemIds)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(raw)) continue;
                BaseItem item;
                try { item = ItemIdResolver.Resolve(library, raw); }
                catch (Exception ex) { logger?.Warn("[LLM_AI] Playlist : résolution id {0} échouée : {1}", raw, ex.Message); continue; }
                if (item == null) continue;
                if (entryIds.Contains(item.InternalId) && !wanted.Contains(item.InternalId))
                    wanted.Add(item.InternalId);
            }
            if (wanted.Count == 0) return new List<long>();

            try
            {
                await playlists.RemoveFromPlaylist(playlist, wanted.ToArray()).ConfigureAwait(false);
                // Vérification : seuls les ids absents du re-listing comptent
                // comme retirés (sur ce build Emby, l'appel ne retire rien —
                // le retour honnête est 0).
                var after = new HashSet<long>(GetEntryIds(library, playlist));
                var removed = wanted.Where(id => !after.Contains(id)).ToList();
                logger?.Info("[LLM_AI] Playlist « {0} » : {1}/{2} entrée(s) réellement retirée(s) (chat).",
                    PlaylistName, removed.Count, wanted.Count);
                return removed;
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Playlist « {0} » : échec RemoveFromPlaylist (chat) : {1}", PlaylistName, ex.Message);
                return new List<long>();
            }
        }

        // ------------------------------------------------------------------
        //  Nettoyage (vidage de la playlist)
        // ------------------------------------------------------------------

        /// <summary>
        /// <b>Supprime</b> la playlist <see cref="PlaylistName"/> (item +
        /// fichier .m3u sous-jacent) — elle est recréée au prochain run
        /// Tonight. Remplace l'ancien « vidage par RemoveFromPlaylist »,
        /// inopérant sur ce build Emby (cf. remarque de classe). No-op si la
        /// playlist n'existe pas. Best-effort : un échec d'API est logué sans
        /// lever.
        /// </summary>
        internal static Task ClearAsync(
            IPlaylistManager playlists, ILibraryManager library, ILogger logger, CancellationToken ct)
        {
            if (library == null)
                return Task.CompletedTask;

            DestroyPlaylist(library, logger);
            return Task.CompletedTask;
        }

        // ------------------------------------------------------------------
        //  Helpers internes
        // ------------------------------------------------------------------

        /// <summary>
        /// Supprime l'item <see cref="Playlist"/> (et son fichier .m3u
        /// sous-jacent via <c>DeleteOptions.DeleteFileLocation = true</c>) —
        /// seul reset fiable sur ce build, <c>RemoveFromPlaylist</c> y étant
        /// inopérant (no-op silencieux en interne, SQLiteException via REST ;
        /// vécu 2026-09-06 : 403 entrées dupliquées ×6). La coquille change
        /// d'id à chaque appel : sans importance (retrouvée par nom).
        /// </summary>
        private static void DestroyPlaylist(ILibraryManager library, ILogger logger)
        {
            try
            {
                Playlist playlist = FindPlaylist(library);
                if (playlist == null) return;

                library.DeleteItem(playlist, new DeleteOptions { DeleteFileLocation = true });
                logger?.Info("[LLM_AI] Playlist « {0} » : coquille supprimée (id={1}) — recréation au prochain remplissage.",
                    PlaylistName, playlist.InternalId);
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Playlist : échec suppression coquille : {0}", ex.Message);
            }
        }

        /// <summary>
        /// Normalise les ids du watch bucket en <b>feuilles jouables</b> :
        /// une reco <see cref="MediaBrowser.Controller.Entities.TV.Series"/> /
        /// <c>Season</c> devient son épisode « next up » non vu pour l'usager
        /// Tonight (<c>ITVSeriesManager.GetNextUp</c> — le même « à suivre »
        /// qu'Emby propose en fin de lecture ; aucun next up → série sautée,
        /// tout est déjà vu), les autres items (film, épisode) passent tels
        /// quels. Déduplique. <b>Pourquoi</b> : Emby développe une série ou
        /// saison ajoutée à une playlist en TOUS ses épisodes (vérifié
        /// 2026-09-06 : un id série → 52 entrées) — sans cette normalisation,
        /// chaque run gonflait la playlist de ~50 entrées par reco série.
        /// </summary>
        private static List<long> ResolveLeafIds(
            ILibraryManager library, IServerApplicationHost host,
            IEnumerable<string> itemIds, User user, ILogger logger, CancellationToken ct)
        {
            var leaves = new List<long>();
            if (itemIds == null) return leaves;

            ITVSeriesManager tv = null;
            try { tv = host?.TryResolve<ITVSeriesManager>(); }
            catch { /* résolution impossible → séries sautées (log ci-dessous) */ }

            foreach (var raw in itemIds)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(raw)) continue;

                BaseItem item;
                try { item = ItemIdResolver.Resolve(library, raw); }
                catch (Exception ex) { logger?.Warn("[LLM_AI] Playlist : résolution id {0} échouée : {1}", raw, ex.Message); continue; }
                if (item == null) continue;

                // Série/saison → épisode next up (une feuille par reco).
                var seriesItem = item as MediaBrowser.Controller.Entities.TV.Series;
                var seasonItem = item as MediaBrowser.Controller.Entities.TV.Season;
                if (seriesItem != null || seasonItem != null)
                {
                    var series = seriesItem ?? seasonItem.Series;
                    if (series == null)
                    {
                        logger?.Warn("[LLM_AI] Playlist : série introuvable pour la saison « {0} » — sautée.", item.Name);
                        continue;
                    }
                    if (tv == null)
                    {
                        logger?.Warn("[LLM_AI] Playlist : ITVSeriesManager indisponible — série « {0} » sautée.", series.Name);
                        continue;
                    }

                    try
                    {
                        var next = tv.GetNextUp(
                            new NextUpQuery
                            {
                                SeriesId = series.InternalId,
                                UserId = user.InternalId,
                                Limit = 1,
                                EnableTotalRecordCount = false
                            },
                            user,
                            new DtoOptions());
                        var ep = next?.Items != null && next.Items.Length > 0 ? next.Items[0] : null;
                        if (ep == null)
                        {
                            logger?.Info("[LLM_AI] Playlist : série « {0} » sans épisode next up (tout vu ?) — sautée.", series.Name);
                            continue;
                        }
                        logger?.Info("[LLM_AI] Playlist : série « {0} » → épisode next up « {1} » (id={2}).",
                            series.Name, ep.Name, ep.InternalId);
                        if (!leaves.Contains(ep.InternalId)) leaves.Add(ep.InternalId);
                    }
                    catch (Exception ex)
                    {
                        logger?.Warn("[LLM_AI] Playlist : next up échoué pour « {0} » : {1} — série sautée.", series.Name, ex.Message);
                    }
                    continue;
                }

                // Feuille (film, épisode, vidéo) : telle quelle.
                if (!leaves.Contains(item.InternalId)) leaves.Add(item.InternalId);
            }
            return leaves;
        }

        /// <summary>
        /// Recherche la playlist <see cref="PlaylistName"/> parmi les
        /// <see cref="Playlist"/> de la bibliothèque (filtre par type + nom
        /// exact, comme <c>AiTonightCollectionManager.FindCollection</c>).
        /// Retourne null si introuvable.
        /// </summary>
        internal static Playlist FindPlaylist(ILibraryManager library)
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