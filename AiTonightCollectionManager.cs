using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    /// (<see cref="AiGenreTagger"/>) mais présenté comme une collection navigable
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
    /// Les ids du watch bucket sont des Guid (chaînes) : résolus en
    /// <see cref="BaseItem"/> via <see cref="ILibraryManager.GetItemById(Guid)"/>,
    /// puis en <see cref="BaseItem.InternalId"/> (long) pour le gestionnaire de
    /// collections. Les membres courants de la collection sont lus via
    /// <see cref="Folder.GetChildrenIds"/> (les membres d'un <see cref="BoxSet"/>
    /// sont ses enfants).</para>
    /// </remarks>
    internal static class AiTonightCollectionManager
    {
        /// <summary>
        /// Nom de la collection Emby maintenue pour « À regarder ce soir ».
        /// Volontairement identique au genre <see cref="AiGenreTagger.TonightGenre"/>
        /// (« AI Tonight ») pour une cohérence d'interface, mais c'est un artefact
        /// distinct (une collection, pas un genre) — les deux mécanismes sont
        /// indépendants.
        /// </summary>
        public const string CollectionName = "AI Tonight";

        // ------------------------------------------------------------------
        //  Maintien de la collection (création + rapprochement)
        // ------------------------------------------------------------------

        /// <summary>
        /// Garantit que la collection <see cref="CollectionName"/> existe et
        /// contient exactement les items Emby dont l'id (Guid, chaîne) figure
        /// dans <paramref name="itemGuidIds"/>. Crée la collection (avec ses
        /// membres initiaux) si elle n'existe pas ; sinon rapproche l'appartenance
        /// par « tout retirer puis tout réajouter ». Best-effort : un id non-Guid,
        /// un item introuvable ou un échec d'API collection sont logués et
        /// n'interrompent pas le reste — la collection reste exploitable.
        /// </summary>
        internal static async Task EnsureAsync(
            ICollectionManager collections, ILibraryManager library, ILogger logger,
            IEnumerable<string> itemGuidIds, CancellationToken ct)
        {
            if (collections == null || library == null || itemGuidIds == null)
                return;

            // 1) Résoudre les ids Guid du watch bucket -> InternalId (long) du
            //    gestionnaire de collections. Best-effort par id (déjà étiqueté
            //    par AiGenreTagger avec la même logique de résolution).
            var freshLongIds = new List<long>();
            int skipped = 0;
            foreach (var raw in itemGuidIds)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(raw)) continue;
                if (!Guid.TryParse(raw, out var guid)) { skipped++; continue; }

                BaseItem item;
                try { item = library.GetItemById(guid); }
                catch (Exception ex) { logger?.Warn("[LLM_AI] Collection : GetItemById({0}) échoué : {1}", raw, ex.Message); continue; }
                if (item == null) { skipped++; continue; }

                freshLongIds.Add(item.InternalId);
            }

            if (freshLongIds.Count == 0)
            {
                // Aucun membre à mettre : on ne crée pas une collection vide.
                // Si une collection existe déjà, on la vide (rapprochement vers
                // zéro) pour ne pas laisser d'anciens membres.
                logger?.Info("[LLM_AI] Collection « {0} » : aucun membre à mettre ({1} ignoré(s)) — vide si existante.", CollectionName, skipped);
                await ClearAsync(collections, library, logger, ct).ConfigureAwait(false);
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
                }
                catch (Exception ex)
                {
                    logger?.Warn("[LLM_AI] Collection « {0} » : échec CreateCollection : {1}", CollectionName, ex.Message);
                }
                return;
            }

            // 3b) Rapprochement : retirer tous les membres courants puis
            //     réajouter les frais. Évite une logique de diff (volume faible).
            //     RemoveFromCollection n'efface que le lien, jamais l'item.
            //
            //     Auto-réparation : si la collection existe d'une version
            //     antérieure (créée sans IsLocked), on la verrouille rétro-
            //     activement et on retire le provider id TMDB qu'Emby a pu
            //     attacher au nom « AI Tonight » — sinon un refresh metadata
            //     écraserait notre collage de pochettes par l'affiche TMDB.
            EnsureLocked(boxSet, logger);

            try
            {
                long[] current = GetCurrentMemberIds(boxSet);
                if (current.Length > 0)
                {
                    collections.RemoveFromCollection(boxSet, current);
                    logger?.Info("[LLM_AI] Collection « {0} » : {1} ancien(s) membre(s) retiré(s).", CollectionName, current.Length);
                }
            }
            catch (Exception ex) { logger?.Warn("[LLM_AI] Collection « {0} » : échec retrait anciens membres : {1}", CollectionName, ex.Message); }

            try
            {
                await collections.AddToCollection(boxSet.InternalId, freshArr).ConfigureAwait(false);
                logger?.Info("[LLM_AI] Collection « {0} » : {1} membre(s) (ré)ajouté(s).", CollectionName, freshArr.Length);
            }
            catch (Exception ex) { logger?.Warn("[LLM_AI] Collection « {0} » : échec AddToCollection : {1}", CollectionName, ex.Message); }
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

                long[] current = GetCurrentMemberIds(boxSet);
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
        private static BoxSet FindCollection(ILibraryManager library)
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
        /// <see cref="BoxSet"/> — les membres d'une collection sont ses enfants
        /// (<see cref="Folder.GetChildrenIds"/>). Retourne un tableau vide en
        /// cas d'erreur ou de collection vide.
        /// </summary>
        private static long[] GetCurrentMemberIds(BoxSet boxSet)
        {
            if (boxSet == null) return Array.Empty<long>();
            try
            {
                var ids = boxSet.GetChildrenIds(new InternalItemsQuery());
                return ids ?? Array.Empty<long>();
            }
            catch (Exception)
            {
                return Array.Empty<long>();
            }
        }
    }
}