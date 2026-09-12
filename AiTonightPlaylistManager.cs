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
    /// Maintient les <b>playlists Emby</b> de « À regarder ce soir » — depuis
    /// la v1.13.16.0, <b>une privée par usager</b> (« AI Tonight · {usager} »,
    /// remplie par les runs de chacun avec ses recos déjà filtrées par sa
    /// policy parentale) et <b>une publique foyer</b> (<see cref="PlaylistName"/>,
    /// « AI Tonight », remplie par les runs de l'usager « Tonight » avec
    /// l'<b>intersection parentale</b> : seulement ce que tout compte actif
    /// peut lire). Le contenu vient du <b>watch bucket</b> du run
    /// (enregistrements non visionnés + items possédés). Miroir de
    /// <see cref="AiTonightCollectionManager"/> (même workflow : surface au
    /// run, nettoyage à 3 h par <c>AiTonightCleanupTask</c>), mais sous forme
    /// de <b>playlist</b> — lecture enchaînée directement depuis n'importe
    /// quel client Emby, là où la collection est une simple navigation.
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

        /// <summary>
        /// Nom de la playlist PRIVÉE d'un usager : « AI Tonight · {usager} ».
        /// Le nom EST le discrimineur de propriétaire (v1.13.16.0) — l'entité
        /// <see cref="Playlist"/> n'expose pas de champ owner in-process et la
        /// sémantique de visibilité des requêtes in-process n'est pas fiable
        /// pour les playlists (validé 2026-09-12 : la vue <c>?UserId=</c>+
        /// clé admin donne des résultats incohérents) ; le suffixe rend le
        /// find déterministe sans dépendre de l'une ni de l'autre.
        /// </summary>
        public static string UserPlaylistName(User user)
        {
            return (user == null || string.IsNullOrWhiteSpace(user.Name))
                ? PlaylistName + " · ?"
                : PlaylistName + " · " + user.Name.Trim();
        }

        // ------------------------------------------------------------------
        //  Maintien des playlists (création + reset)
        // ------------------------------------------------------------------

        /// <summary>
        /// Playlist PRIVÉE du run (v1.13.16.0) : chaque usager a la sienne
        /// (« AI Tonight · {usager} », <c>IsPublic=false</c> — invisible des
        /// autres comptes, comportement par défaut d'une playlist Emby créée
        /// sans MakePublic, validé 2026-09-12). Détruite puis recréée à
        /// chaque appel avec les recos fraîches du run de CET usager — un
        /// run ne touche plus jamais la playlist d'un autre (la course du
        /// remplissage disparaît). Les ids du watch bucket sont normalisés
        /// en <b>feuilles</b> via <see cref="ResolveLeafItems"/> (série/saison
        /// → épisode next up), puis re-passés par le <b>filet parental</b>
        /// (<see cref="PermissionGate.FilterParental"/>) : les recos sont
        /// déjà filtrées dans <c>ValidateAndFilter</c>, mais une feuille
        /// résolue (épisode héritant d'une cote au-dessus de la limite)
        /// ne doit pas entrer dans la playlist. Best-effort : un échec
        /// d'API est logué sans lever.
        /// </summary>
        internal static async Task EnsureUserAsync(
            IPlaylistManager playlists, ILibraryManager library, ILogger logger,
            IEnumerable<string> itemGuidIds, User user, IServerApplicationHost host, CancellationToken ct)
        {
            if (playlists == null || library == null || itemGuidIds == null || user == null)
                return;

            string name = UserPlaylistName(user);
            var leaves = ResolveLeafItems(library, host, itemGuidIds, user, logger, ct);
            leaves = PermissionGate.FilterParental(user, leaves, logger) ?? leaves;

            // 1) Destruction systématique de la playlist de l'usager :
            //    RemoveFromPlaylist est INOPÉRANT sur ce build Emby (cf.
            //    remarque de classe). Détruire + recréer est le seul reset
            //    fiable. La coquille change d'id à chaque run : sans
            //    importance (retrouvée par nom).
            DestroyPlaylist(library, logger, name, false);

            if (leaves.Count == 0)
            {
                // Aucun membre frais : rester à zéro (ne jamais recréer vide).
                logger?.Info("[LLM_AI] Playlist « {0} » : aucun membre frais — playlist absente/supprimée.", name);
                return;
            }

            // 2) Création privée avec les membres frais (feuilles uniquement).
            try
            {
                var request = new PlaylistCreationRequest
                {
                    Name = name,
                    ItemIdList = leaves.Select(i => i.InternalId).ToArray(),
                    MediaType = "Video",
                    // IsPublic non posé = false (playlist privée Emby : le
                    // seul chemin public est le POST explicite MakePublic,
                    // jamais appelé ici).
                    User = user
                };
                var result = await playlists.CreatePlaylist(request).ConfigureAwait(false);
                logger?.Info("[LLM_AI] Playlist « {0} » : recréée (id={1}, privée) avec {2} entrée(s).",
                    name, result?.Id, result?.ItemAddedCount ?? leaves.Count);
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Playlist « {0} » : échec CreatePlaylist : {1}", name, ex.Message);
            }
        }

        /// <summary>
        /// Playlist PUBLIQUE foyer (v1.13.16.0) : « AI Tonight »
        /// (<c>IsPublic=true</c>, visible de tous), reconstruite par les runs
        /// de l'usager « Tonight » uniquement. Contenu = <b>intersection
        /// parentale</b> : un item est écarté si UN SEUL usager actif
        /// restreint (<paramref name="restrictedUsers"/>) ne peut pas le
        /// lire — verdict <see cref="PermissionGate.IsParentallyAllowed"/>,
        /// exactement la décision du gate des recos v1.13.15.0. Pourquoi :
        /// le contrôle parental Emby est LISTING-ONLY (validé 2026-09-12) —
        /// un item visible dans une playlist publique est LISIBLE par un
        /// compte restreint, et la playlist publique est une surface foyer ;
        /// l'intersection la rend incapable d'exposer ce qu'un compte ne
        /// peut pas déjà voir. Détruite puis recréée à chaque appel (aucun
        /// membre acceptable = playlist absente, jamais recréée vide).
        /// Best-effort : un échec d'API est logué sans lever.
        /// </summary>
        internal static async Task EnsurePublicAsync(
            IPlaylistManager playlists, ILibraryManager library, ILogger logger,
            IEnumerable<string> itemGuidIds, User owner, List<User> restrictedUsers,
            IServerApplicationHost host, CancellationToken ct)
        {
            if (playlists == null || library == null || itemGuidIds == null || owner == null)
                return;

            var leaves = ResolveLeafItems(library, host, itemGuidIds, owner, logger, ct);

            var kept = new List<BaseItem>(leaves.Count);
            int blocked = 0;
            foreach (var it in leaves)
            {
                bool allowed = true;
                foreach (var u in restrictedUsers ?? new List<User>())
                {
                    if (u == null) continue;
                    if (PermissionGate.IsParentallyAllowed(u, it) != PermissionGate.ParentalVerdict.Allowed)
                    {
                        allowed = false;
                        blocked++;
                        break;
                    }
                }
                if (allowed) kept.Add(it);
            }
            if (blocked > 0)
                logger?.Info("[LLM_AI] Playlist « {0} » : {1} item(s) écarté(s) par l'intersection parentale ({2} usager(s) actif(s) restreint(s)).",
                    PlaylistName, blocked, restrictedUsers?.Count ?? 0);

            DestroyPlaylist(library, logger, PlaylistName, true);

            if (kept.Count == 0)
            {
                logger?.Info("[LLM_AI] Playlist « {0} » : aucun membre acceptable pour tout le foyer — playlist absente/supprimée.", PlaylistName);
                return;
            }

            try
            {
                var request = new PlaylistCreationRequest
                {
                    Name = PlaylistName,
                    ItemIdList = kept.Select(i => i.InternalId).ToArray(),
                    MediaType = "Video",
                    IsPublic = true,
                    User = owner
                };
                var result = await playlists.CreatePlaylist(request).ConfigureAwait(false);
                logger?.Info("[LLM_AI] Playlist « {0} » : recréée (id={1}, publique, foyer) avec {2} entrée(s).",
                    PlaylistName, result?.Id, result?.ItemAddedCount ?? kept.Count);
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
        /// Ajoute des items à la playlist PRIVÉE du compte demandeur
        /// (« <see cref="PlaylistName"/> · {usager} », cf.
        /// <see cref="UserPlaylistName"/>) SANS reset (additif pur).
        /// <b>v1.13.18.0</b> : le chat (surface admin) n'ajoute PLUS dans la
        /// publique foyer — un item ajouté au chat n'a pas traversé
        /// l'intersection parentale du run « Tonight » ; le poser dans la
        /// playlist privée de l'admin referme ce contournement (la publique
        /// reste remplie exclusivement par <see cref="EnsurePublicAsync"/>).
        /// Hygiène (v1.13.2) : les ids sont d'abord normalisés en
        /// <b>feuilles</b> via <see cref="ResolveLeafItems"/> (série/saison →
        /// épisode next up), puis <b>dédupliqués</b> contre les entrées
        /// courantes (un item déjà présent n'est pas re-ajouté). Crée la
        /// playlist privée (IsPublic=false, <paramref name="user"/>
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
            if (playlists == null || library == null || itemIds == null || user == null)
                return new List<long>();

            string name = UserPlaylistName(user);
            var freshLongIds = ResolveLeafIds(library, host, itemIds, user, logger, ct);
            if (freshLongIds.Count == 0) return new List<long>();

            Playlist playlist = FindPlaylist(library, name, false);
            if (playlist != null)
            {
                // Dédup : ne soumettre que ce qui n'est PAS déjà une entrée
                // (une reco re-proposée par plusieurs runs ne duplique plus).
                var existing = new HashSet<long>(GetEntryIds(library, playlist));
                freshLongIds = freshLongIds.Where(id => !existing.Contains(id)).ToList();
                if (freshLongIds.Count == 0)
                {
                    logger?.Info("[LLM_AI] Playlist « {0} » : ajout (chat) — tous les items déjà présents.", name);
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
                        Name = name,
                        ItemIdList = arr,
                        MediaType = "Video",
                        IsPublic = false,   // privée — visible du seul compte demandeur
                        User = user
                    };
                    var result = await playlists.CreatePlaylist(request).ConfigureAwait(false);
                    logger?.Info("[LLM_AI] Playlist « {0} » : créée (chat, id={1}, privée) avec {2} item(s).",
                        name, result?.Id, result?.ItemAddedCount ?? arr.Length);
                }
                else
                {
                    await playlists.AddToPlaylist(playlist, arr, false, user, ct).ConfigureAwait(false);
                    logger?.Info("[LLM_AI] Playlist « {0} » : {1} item(s) ajouté(s) (chat).", name, arr.Length);
                }

                // Comptage honnête : re-listing des entrées après l'appel
                // (un id non appliqué par Emby ne sera pas compté).
                playlist = FindPlaylist(library, name, false) ?? playlist;
                var after = new HashSet<long>(GetEntryIds(library, playlist));
                return arr.Where(id => after.Contains(id)).ToList();
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Playlist « {0} » : échec ajout (chat) : {1}", name, ex.Message);
                return new List<long>();
            }
        }

        /// <summary>
        /// Retire des items de la playlist PRIVÉE du compte demandeur
        /// (cf. <see cref="UserPlaylistName"/>, v1.13.18.0 — symétrie avec
        /// <see cref="AddItemsAsync"/>) — <b>par InternalId d'item</b> : les
        /// ids demandés sont vérifiés contre le listing des entrées courantes
        /// et SEULS les ids présents dans ce listing sont passés à
        /// <c>RemoveFromPlaylist</c>. Le résultat est <b>vérifié par
        /// re-listing</b> après l'appel : <c>RemoveFromPlaylist</c> est
        /// INOPÉRANT sur ce build Emby (4.9.5.0 — cf. remarque de classe), le
        /// retour ne compte donc que les ids réellement disparus du listing
        /// (0 sur ce build). Retourne la liste des ids (normalisés) réellement
        /// retirés. Best-effort, ne lève jamais. Utilisé par la couche d'action
        /// du chat.
        /// </summary>
        internal static async Task<List<long>> RemoveItemsAsync(
            IPlaylistManager playlists, ILibraryManager library, ILogger logger,
            IEnumerable<string> itemIds, User user, CancellationToken ct)
        {
            if (playlists == null || library == null || itemIds == null || user == null)
                return new List<long>();

            string name = UserPlaylistName(user);
            Playlist playlist = FindPlaylist(library, name, false);
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
                    name, removed.Count, wanted.Count);
                return removed;
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Playlist « {0} » : échec RemoveFromPlaylist (chat) : {1}", name, ex.Message);
                return new List<long>();
            }
        }

        // ------------------------------------------------------------------
        //  Nettoyage (vidage de la playlist)
        // ------------------------------------------------------------------

        /// <summary>
        /// <b>Supprime</b> les playlists du plugin (item + fichier .m3u
        /// sous-jacent) : la publique foyer <see cref="PlaylistName"/> ET
        /// toutes les privées par usager (« AI Tonight · … ») — elles sont
        /// recréées aux prochains runs Tonight. Remplace l'ancien « vidage
        /// par RemoveFromPlaylist », inopérant sur ce build Emby (cf.
        /// remarque de classe). No-op si aucune n'existe. Best-effort : un
        /// échec d'API est logué sans lever.
        /// </summary>
        internal static Task ClearAsync(
            IPlaylistManager playlists, ILibraryManager library, ILogger logger, CancellationToken ct)
        {
            if (library == null)
                return Task.CompletedTask;

            try
            {
                var all = library.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Playlist" },
                    EnableTotalRecordCount = false
                }) ?? Array.Empty<BaseItem>();
                foreach (var p in all.OfType<Playlist>())
                {
                    if (p == null || string.IsNullOrWhiteSpace(p.Name)) continue;
                    bool ours = string.Equals(p.Name, PlaylistName, StringComparison.OrdinalIgnoreCase)
                        || p.Name.StartsWith(PlaylistName + " · ", StringComparison.OrdinalIgnoreCase);
                    if (!ours) continue;
                    try
                    {
                        library.DeleteItem(p, new DeleteOptions { DeleteFileLocation = true });
                        logger?.Info("[LLM_AI] Playlist « {0} » : coquille supprimée (id={1}) — recréation au prochain remplissage.",
                            p.Name, p.InternalId);
                    }
                    catch (Exception ex)
                    {
                        logger?.Warn("[LLM_AI] Playlist « {0} » : échec suppression coquille : {1}", p.Name, ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Playlist : échec nettoyage des coquilles : {0}", ex.Message);
            }
            return Task.CompletedTask;
        }

        // ------------------------------------------------------------------
        //  Helpers internes
        // ------------------------------------------------------------------

        /// <summary>
        /// Supprime l'item <see cref="Playlist"/> ciblé (et son fichier .m3u
        /// sous-jacent via <c>DeleteOptions.DeleteFileLocation = true</c>) —
        /// seul reset fiable sur ce build, <c>RemoveFromPlaylist</c> y étant
        /// inopérant (no-op silencieux en interne, SQLiteException via REST ;
        /// vécu 2026-09-06 : 403 entrées dupliquées ×6). La coquille change
        /// d'id à chaque appel : sans importance (retrouvée par nom).
        /// <paramref name="publicOnly"/> : vrai = ne viser que la coquille
        /// PUBLIQUE du nom (la privée d'un usager homonyme est préservée).
        /// </summary>
        private static void DestroyPlaylist(ILibraryManager library, ILogger logger, string name, bool publicOnly)
        {
            try
            {
                Playlist playlist = FindPlaylist(library, name, publicOnly);
                if (playlist == null) return;

                library.DeleteItem(playlist, new DeleteOptions { DeleteFileLocation = true });
                logger?.Info("[LLM_AI] Playlist « {0} » : coquille supprimée (id={1}) — recréation au prochain remplissage.",
                    name, playlist.InternalId);
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Playlist « {0} » : échec suppression coquille : {1}", name, ex.Message);
            }
        }

        /// <summary>
        /// Normalise les ids du watch bucket en <b>feuilles jouables</b> :
        /// une reco <see cref="MediaBrowser.Controller.Entities.TV.Series"/> /
        /// <c>Season</c> devient son épisode « next up » non vu pour l'usager
        /// Tonight, les autres items (film, épisode) passent tels quels.
        /// Déduplique. <b>Pourquoi</b> : Emby développe une série ou saison
        /// ajoutée à une playlist en TOUS ses épisodes (vérifié 2026-09-06 :
        /// un id série → 52 entrées) — sans cette normalisation, chaque run
        /// gonflait la playlist de ~50 entrées par reco série.
        /// <para><b>Next up + repli (v1.13.10.1)</b> : sur ce build Emby
        /// (4.10.0.40), <c>GetNextUp</c> retourne VIDE pour une série
        /// <b>jamais commencée</b> (aucun épisode vu — vérifié aussi via le
        /// REST natif <c>/Shows/NextUp</c>) et peut renvoyer un épisode DÉJÀ
        /// VU. Le repli calcule le « prochain » à la main
        /// (<see cref="FirstUnwatchedEpisode"/>) : premier épisode non vu en
        /// ordre saison/épisode — tout vu = série sautée.</para>
        /// </summary>
        private static List<long> ResolveLeafIds(
            ILibraryManager library, IServerApplicationHost host,
            IEnumerable<string> itemIds, User user, ILogger logger, CancellationToken ct)
        {
            return ResolveLeafItems(library, host, itemIds, user, logger, ct)
                .Select(i => i.InternalId).ToList();
        }

        /// <summary>
        /// Variante <b>items</b> de <see cref="ResolveLeafIds"/> (v1.13.16.0) :
        /// retourne les feuilles elles-mêmes (et pas seulement leurs ids) —
        /// le filet parental (<see cref="PermissionGate.FilterParental"/>) et
        /// l'intersection parentale de la playlist publique ont besoin des
        /// <see cref="BaseItem"/> pour évaluer la policy. Dédup par InternalId.
        /// </summary>
        private static List<BaseItem> ResolveLeafItems(
            ILibraryManager library, IServerApplicationHost host,
            IEnumerable<string> itemIds, User user, ILogger logger, CancellationToken ct)
        {
            var leaves = new List<BaseItem>();
            var seen = new HashSet<long>();
            if (itemIds == null) return leaves;

            ITVSeriesManager tv = null;
            MediaBrowser.Controller.Library.IUserDataManager userData = null;
            try
            {
                tv = host?.TryResolve<ITVSeriesManager>();
                userData = host?.TryResolve<MediaBrowser.Controller.Library.IUserDataManager>();
            }
            catch { /* résolution impossible → replis limités (logs ci-dessous) */ }

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

                    BaseItem ep = null;
                    if (tv != null)
                    {
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
                            ep = next?.Items != null && next.Items.Length > 0 ? next.Items[0] : null;
                        }
                        catch (Exception ex)
                        {
                            logger?.Warn("[LLM_AI] Playlist : next up échoué pour « {0} » : {1} — repli.", series.Name, ex.Message);
                        }
                        // Un next up DÉJÀ VU ne vaut pas mieux que vide (le
                        // build peut renvoyer un épisode visionné) : repli.
                        if (ep != null && IsPlayedForUser(userData, user, ep))
                        {
                            logger?.Info("[LLM_AI] Playlist : next up « {0} » de « {1} » déjà vu — repli.",
                                ep.Name, series.Name);
                            ep = null;
                        }
                    }
                    else
                    {
                        logger?.Warn("[LLM_AI] Playlist : ITVSeriesManager indisponible — repli pour « {0} ».", series.Name);
                    }

                    // Repli (série jamais commencée : GetNextUp est VIDE sur ce
                    // build) : premier épisode NON VU en ordre saison/épisode.
                    if (ep == null)
                        ep = FirstUnwatchedEpisode(library, userData, user, series, logger);

                    if (ep == null)
                    {
                        logger?.Info("[LLM_AI] Playlist : série « {0} » sans épisode non vu (tout vu ?) — sautée.", series.Name);
                        continue;
                    }
                    logger?.Info("[LLM_AI] Playlist : série « {0} » → épisode « {1} » (id={2}).",
                        series.Name, ep.Name, ep.InternalId);
                    if (seen.Add(ep.InternalId)) leaves.Add(ep);
                    continue;
                }

                // Feuille (film, épisode, vidéo) : telle quelle.
                if (seen.Add(item.InternalId)) leaves.Add(item);
            }
            return leaves;
        }

        /// <summary>État « vu » de l'item pour CET usager (<c>IUserDataManager</c> —
        /// <c>BaseItem</c> ne porte pas de UserData). userData indisponible =
        /// pas vu (fail-open : l'épisode next up est retenu tel quel).</summary>
        private static bool IsPlayedForUser(
            MediaBrowser.Controller.Library.IUserDataManager userData, User user, BaseItem item)
        {
            if (userData == null || user == null || item == null) return false;
            try { return userData.GetUserData(user, item)?.Played ?? false; }
            catch { return false; }
        }

        /// <summary>
        /// Repli « next up » : premier épisode <b>non vu</b> (pour l'usager) de
        /// la série, en ordre saison/épisode — le même choix que le début du
        /// stock du binge (TonightService). Requête par le dossier de la série
        /// (<c>Folder.GetItemList</c>, chemin validé par le listing playlist) ;
        /// l'état « vu » est relu par usager via <c>IUserDataManager</c> en C#
        /// (pas de filtre de requête). userData indisponible ou tout vu =
        /// null (série sautée). Best-effort, ne lève jamais.
        /// </summary>
        private static BaseItem FirstUnwatchedEpisode(
            ILibraryManager library, MediaBrowser.Controller.Library.IUserDataManager userData,
            User user, BaseItem seriesItem, ILogger logger)
        {
            if (userData == null || user == null || seriesItem == null) return null;
            try
            {
                var items = (seriesItem as Folder)?.GetItemList(new InternalItemsQuery
                {
                    Recursive = true,
                    IncludeItemTypes = new[] { "Episode" },
                    EnableTotalRecordCount = false
                });
                if (items == null) return null;
                BaseItem best = null;
                int bestSeason = int.MaxValue, bestNumber = int.MaxValue;
                foreach (var it in items)
                {
                    if (it == null) continue;
                    if (userData.GetUserData(user, it)?.Played ?? false) continue;
                    int season = it.ParentIndexNumber ?? int.MaxValue;
                    int number = it.IndexNumber ?? int.MaxValue;
                    if (best == null || season < bestSeason
                        || (season == bestSeason && number < bestNumber))
                    {
                        best = it; bestSeason = season; bestNumber = number;
                    }
                }
                if (best != null)
                    logger?.Info("[LLM_AI] Playlist : repli next up — premier épisode non vu « {0} » de « {1} ».",
                        best.Name, seriesItem.Name);
                return best;
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Playlist : repli next up échoué pour « {0} » : {1}", seriesItem.Name, ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Recherche la playlist <paramref name="name"/> parmi les
        /// <see cref="Playlist"/> de la bibliothèque (filtre par type + nom
        /// exact, comme <c>AiTonightCollectionManager.FindCollection</c> ;
        /// <paramref name="publicOnly"/> exige en plus
        /// <c>Playlist.IsPublic</c> — le discrimineur public/privé in-process,
        /// l'entité n'exposant pas de champ owner). Retourne null si
        /// introuvable.
        /// </summary>
        internal static Playlist FindPlaylist(ILibraryManager library, string name, bool publicOnly)
        {
            try
            {
                var q = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Playlist" },
                    Name = name,
                    EnableTotalRecordCount = false
                };
                var items = library.GetItemList(q) ?? Array.Empty<BaseItem>();
                // GetItemList(Name=…) est censé filtrer par nom, mais on
                // vérifie la correspondance exacte par sécurité (casse).
                return items.OfType<Playlist>().FirstOrDefault(p =>
                    p != null && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
                    && (!publicOnly || p.IsPublic));
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