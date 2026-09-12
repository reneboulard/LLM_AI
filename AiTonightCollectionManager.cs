using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;

namespace LLM_AI
{
    /// <summary>
    /// Maintient une <b>collection Emby</b> nommée <see cref="CollectionName"/>
    /// (« AI Tonight ») regroupant les items du <b>watch bucket</b> recommandés
    /// par « À regarder ce soir » (enregistrements non visionnés + items
    /// possédés). Même principe que l'étiquetage par genre
    /// (<see cref="AiTagger"/>) mais présenté comme une collection navigable
    /// plutôt qu'un filtre par genre — et surtout <b>non destructif</b> : les
    /// items sont référencés (regroupés), jamais copiés ni déplacés, et ils
    /// proviennent de bibliothèques potentiellement distinctes (enregistrements
    /// + films/séries possédés), ce qu'un filtre par genre ne permet pas aussi
    /// directement.
    /// </summary>
    /// <remarks>
    /// <para>Deux opérations :</para>
    /// <list type="bullet">
    /// <item><see cref="EnsureAsync"/> : appelée par <c>TonightService</c> après un
    /// run frais (recos watch bucket) — opt-in via
    /// <see cref="PluginConfiguration.TonightCollectionEnabled"/>. Crée la
    /// collection à la première exécution (membres initiaux passés en un appel),
    /// puis la rapproche à chaque run par « tout retirer puis tout réajouter »
    /// (volume faible, ~10 items) — évite une logique de diff et garantit que la
    /// collection reflète exactement les recos courantes.</item>
    /// <item><see cref="ClearAsync"/> : appelée par la tâche planifiée
    /// <c>AiTonightCleanupTask</c> (3 h du matin) pour <b>vider</b> la collection
    /// (retirer tous les membres ; la coquille BoxSet reste pour être re-remplie
    /// au prochain run) — tourne toujours, même si la feature est désactivée,
    /// afin de nettoyer les membres restants.</item>
    /// </list>
    /// <para><b>Scope isolé</b> du genre <c>AI Tonight</c> (étiquetage) et du
    /// genre <c>AI Suggestion</c> (bibliothèque <c>.strm</c>) : cette collection
    /// n'interfère ni avec l'un ni avec l'autre — les trois nettoyages sont
    /// indépendants. Indépendante aussi du flag
    /// <see cref="PluginConfiguration.TonightGenreTagEnabled"/> (les deux peuvent
    /// cohabiter).</para>
    /// <para><b>API Emby utilisées</b> (vérifiées sur cet hôte, Emby 4.9.5.0) :
    /// <see cref="ICollectionManager.CreateCollection"/> (crée un
    /// <see cref="BoxSet"/> avec <c>ItemIdList</c> = membres initiaux),
    /// <see cref="ICollectionManager.AddToCollection"/> (ajoute par
    /// <c>InternalId</c> long) et <see cref="ICollectionManager.RemoveFromCollection"/>
    /// (retire par <c>InternalId</c> long — n'efface jamais l'item référencé).
    /// Les ids du watch bucket sont des chaînes (InternalId, ou Guid hérité) :
    /// résolus en <see cref="BaseItem"/> via <see cref="ItemIdResolver"/>, puis
    /// en <see cref="BaseItem.InternalId"/> (long) pour le gestionnaire de
    /// collections. Les membres courants de la collection sont lus via
    /// <see cref="Folder.GetChildrenIds"/> (les membres d'un <see cref="BoxSet"/>
    /// sont ses enfants).</para>
    /// </remarks>
    internal static class AiTonightCollectionManager
    {
        /// <summary>
        /// Nom de la collection Emby maintenue pour « À regarder ce soir ».
        /// Volontairement identique au tag <see cref="AiTagger.TonightTag"/>
        /// (« AI Tonight ») pour une cohérence d'interface, mais c'est un artefact
        /// distinct (une collection, pas un genre) — les deux mécanismes sont
        /// indépendants.
        /// </summary>
        public const string CollectionName = "AI Tonight";

        // ------------------------------------------------------------------
        //  Maintien de la collection (création + rapprochement)
        // ------------------------------------------------------------------

        /// <summary>
        /// Garantit que la collection <see cref="CollectionName"/> contient les
        /// items Emby dont l'id (chaîne, cf. <see cref="ItemIdResolver"/>) figure
        /// dans <paramref name="itemGuidIds"/>. <b>Cumul (v1.13.18.0)</b> : la
        /// collection est le mur du foyer du jour — chaque run <b>AJOUTE</b> ses
        /// recommandations (dédup), il ne remplace plus le contenu du run
        /// précédent (plus de course de remplissage entre comptes, cf. playlist
        /// v1.13.16.0) ; la remise à zéro est quotidienne, par la tâche de
        /// nettoyage 3 h (coquille conservée). Emby filtre nativement le listing
        /// du BoxSet par le contrôle parental de chaque compte (validé
        /// 2026-09-12), le cumul multi-comptes est donc sûr à l'affichage.
        /// Best-effort : un id non résolvable, un item introuvable ou un échec
        /// d'API collection sont logués et n'interrompent pas le reste — la
        /// collection reste exploitable.
        /// </summary>
        internal static async Task EnsureAsync(
            ICollectionManager collections, ILibraryManager library, ILogger logger,
            IServerApplicationHost host, IEnumerable<string> itemGuidIds, CancellationToken ct)
        {
            if (collections == null || library == null || itemGuidIds == null)
                return;

            // 1) Résoudre les ids du watch bucket (InternalId, ou Guid hérité —
            //    cf. ItemIdResolver) -> InternalId (long) du gestionnaire de
            //    collections. Best-effort par id (déjà étiqueté par AiTagger
            //    avec la même logique de résolution).
            var freshLongIds = new List<long>();
            int skipped = 0;
            foreach (var raw in itemGuidIds)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(raw)) continue;

                BaseItem item;
                try { item = ItemIdResolver.Resolve(library, raw); }
                catch (Exception ex) { logger?.Warn("[LLM_AI] Collection : résolution id {0} échouée : {1}", raw, ex.Message); continue; }
                if (item == null) { skipped++; continue; }

                freshLongIds.Add(item.InternalId);
            }

            if (freshLongIds.Count == 0)
            {
                // Aucun membre à ajouter (run 0-reco) : en cumul, on NE VIDE PAS
                // la collection — le run d'un compte ne doit pas effacer le
                // cumul du jour des autres (la remise à zéro est le travail de
                // la tâche de nettoyage 3 h).
                logger?.Info("[LLM_AI] Collection « {0} » : rien à ajouter ({1} ignoré(s)) — cumul conservé, reset à 3 h.", CollectionName, skipped);
                return;
            }

            // 2) Chercher une collection existante du même nom (scan BoxSet par
            //    nom — le nombre de collections est faible, inutile de persister
            //    l'id en config).
            BoxSet boxSet = FindCollection(library);
            long[] freshArr = freshLongIds.ToArray();

            if (boxSet == null)
            {
                // 3a) Création avec membres initiaux en un seul appel. ParentId
                //     laissé à 0 : Emby place le BoxSet sous le dossier
                //     Collections par défaut. (Vérifié post-déploiement que la
                //     collection apparaît bien dans l'UI.)
                //     IsLocked = true : sinon Emby attache un provider id TMDB
                //     au nom « AI Tonight » (collection réelle 891174) puis
                //     remplace le collage de pochettes par l'affiche TMDB au
                //     prochain refresh metadata. On garde notre présentation.
                try
                {
                    var opts = new CollectionCreationOptions
                    {
                        Name = CollectionName,
                        ItemIdList = freshArr,
                        IsLocked = true
                    };
                    await collections.CreateCollection(opts).ConfigureAwait(false);
                    logger?.Info("[LLM_AI] Collection « {0} » : créée avec {1} membre(s).", CollectionName, freshArr.Length);

                    // CreateCollection ne retourne pas le BoxSet : on le ré-resout pour poser
                    // l'image par défaut (idempotent — ne s'applique qu'à la création).
                    try
                    {
                        var fresh = FindCollection(library);
                        if (fresh != null)
                            await DefaultImageApplier.ApplyPrimaryIfMissingAsync(fresh, host, library, logger, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) { logger?.Warn("[LLM_AI] Collection « {0} » : image par défaut échouée : {1}", CollectionName, ex.Message); }
                }
                catch (Exception ex)
                {
                    logger?.Warn("[LLM_AI] Collection « {0} » : échec CreateCollection : {1}", CollectionName, ex.Message);
                }
                return;
            }

            // 3b) CUMUL (v1.13.18.0) : ajout des membres manquants SEULEMENT —
            //     le contenu du run précédent est conservé (mur du foyer du
            //     jour, remis à zéro à 3 h par la tâche de nettoyage).
            //     RemoveFromCollection n'est plus appelé ici.
            //
            //     Auto-réparation : si la collection existe d'une version
            //     antérieure (créée sans IsLocked), on la verrouille rétro-
            //     activement et on retire le provider id TMDB qu'Emby a pu
            //     attacher au nom « AI Tonight » — sinon un refresh metadata
            //     écraserait notre collage de pochettes par l'affiche TMDB.
            EnsureLocked(boxSet, logger);

            // Image par défaut (idempotent) : couvre aussi une collection créée par une
            // version antérieure (avant cette feature) — au 1er run post-update, elle
            // reçoit le poster standard si elle n'en a pas déjà un.
            try
            {
                await DefaultImageApplier.ApplyPrimaryIfMissingAsync(boxSet, host, library, logger, ct).ConfigureAwait(false);
            }
            catch (Exception ex) { logger?.Warn("[LLM_AI] Collection « {0} » : image par défaut échouée : {1}", CollectionName, ex.Message); }

            try
            {
                long[] current = GetCurrentMemberIds(library, boxSet);
                var existing = new HashSet<long>(current);
                long[] missing = freshArr.Where(id => !existing.Contains(id)).ToArray();
                if (missing.Length == 0)
                {
                    logger?.Info("[LLM_AI] Collection « {0} » : cumul — tous les {1} item(s) déjà présent(s).", CollectionName, freshArr.Length);
                    return;
                }
                await collections.AddToCollection(boxSet.InternalId, missing).ConfigureAwait(false);
                logger?.Info("[LLM_AI] Collection « {0} » : cumul — {1} membre(s) ajouté(s) ({2} déjà présents).",
                    CollectionName, missing.Length, freshArr.Length - missing.Length);
            }
            catch (Exception ex) { logger?.Warn("[LLM_AI] Collection « {0} » : échec AddToCollection : {1}", CollectionName, ex.Message); }
        }

        // ------------------------------------------------------------------
        //  Ajout additif (chat, v1.13) — SANS rapprochement
        // ------------------------------------------------------------------

        /// <summary>
        /// Ajoute des items à la collection <see cref="CollectionName"/>
        /// SANS rapprochement (additif pur) — contrairement à
        /// <see cref="EnsureAsync"/> qui remplace tout le contenu. Crée la
        /// collection (membres initiaux, <c>IsLocked=true</c>, image par
        /// défaut) si absente ; sinon <c>AddToCollection</c> sur la coquille
        /// existante. Retourne le nombre d'items réellement ajoutés.
        /// Best-effort : un échec d'API est logué sans lever.
        /// Utilisé par la couche d'action du chat (<c>ChatActions</c>).
        /// </summary>
        internal static async Task<int> AddItemsAsync(
            ICollectionManager collections, ILibraryManager library, ILogger logger,
            IServerApplicationHost host, IEnumerable<string> itemIds, CancellationToken ct)
        {
            if (collections == null || library == null || itemIds == null)
                return 0;

            var longIds = new List<long>();
            foreach (var raw in itemIds)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(raw)) continue;
                BaseItem item;
                try { item = ItemIdResolver.Resolve(library, raw); }
                catch (Exception ex) { logger?.Warn("[LLM_AI] Collection : résolution id {0} échouée : {1}", raw, ex.Message); continue; }
                if (item == null) continue;
                if (!longIds.Contains(item.InternalId)) longIds.Add(item.InternalId);
            }
            if (longIds.Count == 0) return 0;

            var arr = longIds.ToArray();
            BoxSet boxSet = FindCollection(library);
            if (boxSet == null)
            {
                // Création avec membres initiaux — même options que
                // EnsureAsync (IsLocked=true : Emby n'attache pas de provider
                // id TMDB au nom « AI Tonight » et garde notre pochette).
                try
                {
                    var opts = new CollectionCreationOptions
                    {
                        Name = CollectionName,
                        ItemIdList = arr,
                        IsLocked = true
                    };
                    await collections.CreateCollection(opts).ConfigureAwait(false);
                    logger?.Info("[LLM_AI] Collection « {0} » : créée (chat) avec {1} membre(s).", CollectionName, arr.Length);
                }
                catch (Exception ex)
                {
                    logger?.Warn("[LLM_AI] Collection « {0} » : échec CreateCollection (chat) : {1}", CollectionName, ex.Message);
                    return 0;
                }
                try
                {
                    var fresh = FindCollection(library);
                    if (fresh != null)
                        await DefaultImageApplier.ApplyPrimaryIfMissingAsync(fresh, host, library, logger, ct).ConfigureAwait(false);
                }
                catch (Exception ex) { logger?.Warn("[LLM_AI] Collection « {0} » : image par défaut échouée : {1}", CollectionName, ex.Message); }
                return arr.Length;
            }

            EnsureLocked(boxSet, logger);
            try
            {
                await collections.AddToCollection(boxSet.InternalId, arr).ConfigureAwait(false);
                logger?.Info("[LLM_AI] Collection « {0} » : {1} item(s) ajouté(s) (chat).", CollectionName, arr.Length);
                return arr.Length;
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Collection « {0} » : échec AddToCollection (chat) : {1}", CollectionName, ex.Message);
                return 0;
            }
        }

        /// <summary>
        /// Retire des items de la collection <see cref="CollectionName"/>
        /// (<c>RemoveFromCollection</c> ne retire QUE le lien, jamais l'item).
        /// Retourne le nombre d'ids réellement retirés. Best-effort, ne lève
        /// jamais. Utilisé par la couche d'action du chat — l'appelant
        /// (ChatActions) filtre en amont pour n'autoriser que les items que
        /// le chat a lui-même ajoutés dans la conversation.
        /// </summary>
        internal static Task<int> RemoveItemsAsync(
            ICollectionManager collections, ILibraryManager library, ILogger logger,
            IEnumerable<string> itemIds, CancellationToken ct)
        {
            if (collections == null || library == null || itemIds == null)
                return Task.FromResult(0);

            var longIds = new List<long>();
            foreach (var raw in itemIds)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(raw)) continue;
                BaseItem item;
                try { item = ItemIdResolver.Resolve(library, raw); }
                catch (Exception ex) { logger?.Warn("[LLM_AI] Collection : résolution id {0} échouée : {1}", raw, ex.Message); continue; }
                if (item == null) continue;
                if (!longIds.Contains(item.InternalId)) longIds.Add(item.InternalId);
            }
            if (longIds.Count == 0) return Task.FromResult(0);

            try
            {
                BoxSet boxSet = FindCollection(library);
                if (boxSet == null) return Task.FromResult(0);
                collections.RemoveFromCollection(boxSet, longIds.ToArray());
                logger?.Info("[LLM_AI] Collection « {0} » : {1} item(s) retiré(s) (chat).", CollectionName, longIds.Count);
                return Task.FromResult(longIds.Count);
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Collection « {0} » : échec RemoveFromCollection (chat) : {1}", CollectionName, ex.Message);
                return Task.FromResult(0);
            }
        }

        // ------------------------------------------------------------------
        //  Nettoyage (vidage de la collection)
        // ------------------------------------------------------------------

        /// <summary>
        /// <b>Vide</b> la collection <see cref="CollectionName"/> (retire tous
        /// ses membres) sans supprimer la coquille <see cref="BoxSet"/> — celle-ci
        /// sera re-remplie au prochain run Tonight. No-op si la collection
        /// n'existe pas. Best-effort : un échec d'API est logué sans lever.
        /// </summary>
        internal static Task ClearAsync(
            ICollectionManager collections, ILibraryManager library, ILogger logger, CancellationToken ct)
        {
            if (collections == null || library == null)
                return Task.CompletedTask;

            try
            {
                BoxSet boxSet = FindCollection(library);
                if (boxSet == null)
                    return Task.CompletedTask;

                long[] current = GetCurrentMemberIds(library, boxSet);
                if (current.Length == 0)
                {
                    logger?.Info("[LLM_AI] Collection cleanup « {0} » : déjà vide.", CollectionName);
                    return Task.CompletedTask;
                }

                collections.RemoveFromCollection(boxSet, current);
                logger?.Info("[LLM_AI] Collection cleanup « {0} » : {1} membre(s) retiré(s) (coquille conservée).", CollectionName, current.Length);
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Collection cleanup « {0} » : {1}", CollectionName, ex.Message);
            }
            return Task.CompletedTask;
        }

        // ------------------------------------------------------------------
        //  Helpers internes
        // ------------------------------------------------------------------

        /// <summary>
        /// Recherche la collection <see cref="CollectionName"/> parmi les
        /// <see cref="BoxSet"/> de la bibliothèque (filtre par type + nom exact).
        /// Retourne null si introuvable.
        /// </summary>
        internal static BoxSet FindCollection(ILibraryManager library)
        {
            try
            {
                var q = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "BoxSet" },
                    Name = CollectionName,
                    EnableTotalRecordCount = false
                };
                var items = library.GetItemList(q) ?? Array.Empty<BaseItem>();
                // GetItemList(Name=…) est censé filtrer par nom, mais on vérifie
                // quand même (casse / correspondance exacte) par sécurité.
                return items.OfType<BoxSet>().FirstOrDefault(
                    b => string.Equals(b.Name, CollectionName, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Auto-réparation d'une collection existante créée par une version
        /// antérieure (avant <c>IsLocked=true</c> à la création). Verrouille le
        /// <see cref="BoxSet"/> et retire le provider id TMDB qu'Emby a pu
        /// attacher au nom « AI Tonight » (collection TMDB réelle 891174) :
        /// sinon un refresh metadata remplacerait notre collage de pochettes
        /// par l'affiche TMDB. Best-effort, ne lève jamais.
        /// </summary>
        private static void EnsureLocked(BoxSet boxSet, ILogger logger)
        {
            if (boxSet == null) return;
            try
            {
                bool changed = false;

                if (!boxSet.IsLocked)
                {
                    boxSet.IsLocked = true;
                    changed = true;
                }

                // ProviderIds = Dictionary<string,string> sur BaseItem. On retire
                // les clés de providers externes connues ; on laisse intactes
                // les éventuelles autres clés (aucune attendue ici).
                var pids = boxSet.ProviderIds;
                if (pids != null && pids.Count > 0)
                {
                    string[] external =
                    {
                        "Tmdb", "TmdbCollection", "Tvdb", "Imdb",
                        "MusicBrainzAlbum", "MusicBrainzReleaseGroup", "TheMovieDb"
                    };
                    bool removed = false;
                    foreach (var k in external)
                        if (pids.Remove(k)) removed = true;
                    if (removed) changed = true;
                }

                if (changed)
                {
                    boxSet.UpdateToRepository(ItemUpdateType.MetadataEdit);
                    logger?.Info("[LLM_AI] Collection « {0} » : verrouillée et provider id externe nettoyé.", CollectionName);
                }
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Collection « {0} » : échec verrouillage/nettoyage : {1}", CollectionName, ex.Message);
            }
        }

        /// <summary>
        /// Récupère les <c>InternalId</c> (long) des membres actuels d'un
        /// <see cref="BoxSet"/>. Les membres d'une collection Emby sont portés
        /// par les items enfants eux-mêmes (lien de collection), et le
        /// <see cref="BoxSet"/> les résout en interrogeant les items dont
        /// <c>CollectionIds</c> contient son <see cref="BaseItem.InternalId"/>.
        /// </summary>
        /// <remarks>
        /// <b>On interroge la bibliothèque directement avec
        /// <see cref="InternalItemsQuery.CollectionIds"/></b>, et NON via
        /// <c>boxSet.GetChildrenIds(...)</c> : <see cref="Folder.GetChildrenIds"/>
        /// force <c>query.ForceOriginalFolders = true</c> dès que la requête n'a
        /// pas de <c>User</c> (notre cas — pas d'utilisateur en contexte de
        /// nettoyage), or <see cref="BoxSet"/>'s <c>GetItemIdsInternal</c>
        /// <b>retourne un tableau vide</b> lorsque
        /// <c>ForceOriginalFolders</c> est vrai. <c>GetChildrenIds</c> aurait
        /// donc toujours renvoyé <c>[]</c> ici, rendant le vidage muet
        /// (log « déjà vide » émis alors que la collection est pleine) — c'était
        /// le bug : la tâche de nettoyage ne vidait jamais la collection.
        /// </remarks>
        private static long[] GetCurrentMemberIds(ILibraryManager library, BoxSet boxSet)
        {
            if (boxSet == null || library == null) return Array.Empty<long>();
            try
            {
                var q = new InternalItemsQuery
                {
                    CollectionIds = new[] { boxSet.InternalId },
                    EnableTotalRecordCount = false
                };
                var items = library.GetItemList(q);
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