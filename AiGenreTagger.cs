using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;

namespace LLM_AI
{
    /// <summary>
    /// Étiquetage par genre des items Emby pour surface native des
    /// recommandations « À regarder ce soir » (watch bucket : enregistrements
    /// non visionnés + items possédés). Ajoute le genre
    /// <see cref="TonightGenre"/> aux items recommandés pour que l'usager les
    /// retrouve en filtrant sur ce genre dans n'importe quel client Emby.
    /// </summary>
    /// <remarks>
    /// <para>Deux opérations :</para>
    /// <list type="bullet">
    /// <item><see cref="AddAsync"/> : appelée par <c>TonightService</c> après un
    /// run frais (recos watch bucket) — opt-in via
    /// <see cref="PluginConfiguration.TonightGenreTagEnabled"/>.</item>
    /// <item><see cref="RemoveAllAsync"/> : appelée par la tâche planifiée
    /// <c>AiTonightCleanupTask</c> (3 h du matin) pour retirer le genre de tous
    /// les items — tourne toujours, même si l'étiquetage est désactivé, afin de
    /// nettoyer les tags restants.</item>
    /// </list>
    /// <para><b>Scope isolé</b> : le genre <see cref="TonightGenre"/> (« AI
    /// Tonight ») est distinct du genre « AI Suggestion » utilisé par la
    /// bibliothèque <c>.strm</c> (<see cref="StrmLibraryGenerator"/>) — le
    /// nettoyage ne touche donc jamais les cartes <c>.strm</c>.</para>
    /// <para><b>Persistance</b> : modifie les métadonnées réelles des items
    /// (tableau <c>Genres</c>) via <c>BaseItem.UpdateToRepository</c>
    /// (<c>ItemUpdateType.MetadataEdit</c>). Un refresh métadonnées peut
    /// annuler le tag — le prochain run Tonight le réajoutera.</para>
    /// </remarks>
    internal static class AiGenreTagger
    {
        /// <summary>
        /// Genre appliqué aux items du watch bucket de « À regarder ce soir ».
        /// Distinct de « AI Suggestion » (bibliothèque .strm) pour garder les
        /// deux nettoyages indépendants.
        /// </summary>
        public const string TonightGenre = "AI Tonight";

        // ------------------------------------------------------------------
        //  Ajout du genre à une liste d'items
        // ------------------------------------------------------------------

        /// <summary>
        /// Ajoute <paramref name="genre"/> à chaque item Emby dont l'id figure
        /// dans <paramref name="itemIds"/>. Best-effort : un id non résolvable
        /// (cf. <see cref="ItemIdResolver"/>), un item introuvable ou une erreur
        /// de persistance sont logués et n'interrompent pas le reste. Les ids
        /// sont dédupliqués.
        /// </summary>
        internal static Task AddAsync(
            ILibraryManager library, ILogger logger,
            IEnumerable<string> itemIds, string genre, CancellationToken ct)
        {
            if (library == null || string.IsNullOrEmpty(genre) || itemIds == null)
                return Task.CompletedTask;

            int done = 0, skipped = 0;
            foreach (var raw in itemIds)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(raw)) continue;

                BaseItem item;
                try { item = ItemIdResolver.Resolve(library, raw); }
                catch (Exception ex) { logger?.Warn("[LLM_AI] Genre tag : résolution id {0} échouée : {1}", raw, ex.Message); continue; }
                if (item == null) { skipped++; continue; }

                var genres = item.Genres ?? Array.Empty<string>();
                if (genres.Any(g => string.Equals(g, genre, StringComparison.OrdinalIgnoreCase)))
                {
                    done++; // déjà étiqueté : rien à faire, compte comme réussi
                    continue;
                }

                try
                {
                    item.Genres = genres.Concat(new[] { genre }).ToArray();
                    item.UpdateToRepository(ItemUpdateType.MetadataEdit);
                    done++;
                }
                catch (Exception ex)
                {
                    logger?.Warn("[LLM_AI] Genre tag : échec persistance sur « {0} » : {1}", item.Name, ex.Message);
                }
            }

            logger?.Info("[LLM_AI] Genre tag « {0} » : {1} item(s) étiqueté(s), {2} ignoré(s).", genre, done, skipped);
            return Task.CompletedTask;
        }

        // ------------------------------------------------------------------
        //  Retrait du genre sur tous les items (nettoyage)
        // ------------------------------------------------------------------

        /// <summary>
        /// Retire <paramref name="genre"/> de tous les items Emby qui le portent
        /// (requête par filtre genre — <see cref="InternalItemsQuery.Genres"/>,
        /// pas un scan complet). Persistance via
        /// <c>UpdateToRepository(MetadataEdit)</c>. Best-effort par item.
        /// </summary>
        internal static Task RemoveAllAsync(
            ILibraryManager library, ILogger logger, string genre, CancellationToken ct)
        {
            if (library == null || string.IsNullOrEmpty(genre))
                return Task.CompletedTask;

            BaseItem[] items;
            try
            {
                var q = new InternalItemsQuery
                {
                    Genres = new[] { genre },
                    EnableTotalRecordCount = false
                };
                items = library.GetItemList(q) ?? Array.Empty<BaseItem>();
            }
            catch (Exception ex)
            {
                logger?.Warn("[LLM_AI] Genre cleanup : GetItemList échoué : {0}", ex.Message);
                return Task.CompletedTask;
            }

            int removed = 0;
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                if (item == null) continue;
                var genres = item.Genres ?? Array.Empty<string>();
                if (!genres.Any(g => string.Equals(g, genre, StringComparison.OrdinalIgnoreCase)))
                    continue;
                try
                {
                    item.Genres = genres
                        .Where(g => !string.Equals(g, genre, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    item.UpdateToRepository(ItemUpdateType.MetadataEdit);
                    removed++;
                }
                catch (Exception ex)
                {
                    logger?.Warn("[LLM_AI] Genre cleanup : échec sur « {0} » : {1}", item.Name, ex.Message);
                }
            }

            logger?.Info("[LLM_AI] Genre cleanup « {0} » : {1} item(s) nettoyé(s).", genre, removed);
            return Task.CompletedTask;
        }
    }
}