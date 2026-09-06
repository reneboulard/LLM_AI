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
    /// Étiquetage par <b>tag</b> des items Emby pour surfaces natives
    /// (watch bucket « À regarder ce soir », suggestions de suppression
    /// disque). Ajoute le tag <see cref="TonightTag"/> aux items recommandés
    /// pour que l'usager les retrouve en filtrant sur ce tag dans n'importe
    /// quel client Emby. Un tag est préféré à un genre : « AI Tonight » /
    /// « AI Delete » sont des marqueurs d'admin, pas des genres de contenu —
    /// ils n'encombrent ni la navigation par genres ni les vocabulaires cibles
    /// du genre cleaner.
    /// </summary>
    /// <remarks>
    /// <para>Deux opérations :</para>
    /// <list type="bullet">
    /// <item><see cref="AddAsync"/> : appelée par <c>TonightService</c> après un
    /// run frais (recos watch bucket) — opt-in via
    /// <see cref="PluginConfiguration.TonightGenreTagEnabled"/> — et par le
    /// tool chat <c>tag_ai_tonight</c> ; aussi par
    /// <c>RecordingDiskManager</c> pour le tag « AI Delete ».</item>
    /// <item><see cref="RemoveAllAsync"/> : appelée par la tâche planifiée
    /// <c>AiTonightCleanupTask</c> (3 h du matin) pour retirer le tag « AI
    /// Tonight » de tous les items, et par <c>RecordingDiskManager</c> (clear-
    /// first de chaque passe disque). Tourne toujours, même si l'étiquetage
    /// est désactivé, afin de nettoyer les restes.</item>
    /// </list>
    /// <para><b>Migration genres → tags (v1.13.3)</b> : les marqueurs ont
    /// longtemps été posés comme <i>genres</i> (v1.4-v1.13.2).
    /// <see cref="RemoveAllAsync"/> interroge donc À LA FOIS le filtre
    /// <c>Tags</c> et le filtre <c>Genres</c> du même nom, et retire les deux
    /// de chaque item trouvé — le nettoyage de 3 h et le clear-first disque
    /// migrent ainsi les restes du genre hérité au fil de leurs passages,
    /// sans action manuelle.</para>
    /// <para><b>Scope isolé</b> : les tags <see cref="TonightTag"/> (« AI
    /// Tonight ») et <see cref="DeleteTag"/> (« AI Delete ») sont distincts du
    /// genre « AI Suggestion » utilisé par la bibliothèque <c>.strm</c> —
    /// les nettoyages ne se marchent jamais l'un sur l'autre.</para>
    /// <para><b>Persistance</b> : modifie les métadonnées réelles des items
    /// (tableaux <c>Tags</c> / <c>Genres</c> pour le migration) via
    /// <c>BaseItem.UpdateToRepository</c> (<c>ItemUpdateType.MetadataEdit</c>).
    /// Un refresh métadonnées peut annuler le tag — le prochain run Tonight
    /// le réajoutera.</para>
    /// </remarks>
    internal static class AiTagger
    {
        /// <summary>
        /// Tag appliqué aux items du watch bucket de « À regarder ce soir ».
        /// Ex-genre « AI Tonight » (migré par <see cref="RemoveAllAsync"/>).
        /// Distinct de « AI Suggestion » (bibliothèque .strm) pour garder les
        /// nettoyages indépendants.
        /// </summary>
        public const string TonightTag = "AI Tonight";

        /// <summary>
        /// Tag appliqué aux enregistrements suggérés à la suppression par la
        /// passe disque (ex-genre, migré par <see cref="RemoveAllAsync"/>).
        /// </summary>
        public const string DeleteTag = "AI Delete";

        // ------------------------------------------------------------------
        //  Ajout du tag à une liste d'items
        // ------------------------------------------------------------------

        /// <summary>
        /// Ajoute <paramref name="tag"/> à chaque item Emby dont l'id figure
        /// dans <paramref name="itemIds"/>. Best-effort : un id non résolvable
        /// (cf. <see cref="ItemIdResolver"/>), un item introuvable ou une
        /// erreur de persistance sont logués et n'interrompent pas le reste.
        /// Les ids sont dédupliqués.
        /// </summary>
        internal static Task AddAsync(
            ILibraryManager library, ILogger logger,
            IEnumerable<string> itemIds, string tag, CancellationToken ct)
        {
            if (library == null || string.IsNullOrEmpty(tag) || itemIds == null)
                return Task.CompletedTask;

            int done = 0, skipped = 0;
            foreach (var raw in itemIds)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(raw)) continue;

                BaseItem item;
                try { item = ItemIdResolver.Resolve(library, raw); }
                catch (Exception ex) { logger?.Warn("[LLM_AI] Tag : résolution id {0} échouée : {1}", raw, ex.Message); continue; }
                if (item == null) { skipped++; continue; }

                var tags = item.Tags ?? Array.Empty<string>();
                if (tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
                {
                    done++; // déjà étiqueté : rien à faire, compte comme réussi
                    continue;
                }

                try
                {
                    var list = new List<string>(tags) { tag };
                    item.Tags = list.ToArray();
                    item.UpdateToRepository(ItemUpdateType.MetadataEdit);
                    done++;
                }
                catch (Exception ex)
                {
                    logger?.Warn("[LLM_AI] Tag : échec persistance sur « {0} » : {1}", item.Name, ex.Message);
                }
            }

            logger?.Info("[LLM_AI] Tag « {0} » : {1} item(s) étiqueté(s), {2} ignoré(s).", tag, done, skipped);
            return Task.CompletedTask;
        }

        // ------------------------------------------------------------------
        //  Retrait du tag sur tous les items (+ migration du genre hérité)
        // ------------------------------------------------------------------

        /// <summary>
        /// Retire <paramref name="tag"/> de tous les items Emby qui le portent
        /// (requêtes par filtre — <see cref="InternalItemsQuery.Tags"/> et,
        /// pour la migration v1.13.3, <see cref="InternalItemsQuery.Genres"/>
        /// du même nom — pas un scan complet). Chaque item trouvé perd le tag
        /// ET l'éventuel genre hérité du même nom, en une seule persistance.
        /// Best-effort par item.
        /// </summary>
        internal static Task RemoveAllAsync(
            ILibraryManager library, ILogger logger, string tag, CancellationToken ct)
        {
            if (library == null || string.IsNullOrEmpty(tag))
                return Task.CompletedTask;

            var items = new Dictionary<Guid, BaseItem>();
            foreach (var filter in new[] { "Tags", "Genres" })
            {
                BaseItem[] found;
                try
                {
                    var q = new InternalItemsQuery
                    {
                        Tags = filter == "Tags" ? new[] { tag } : null,
                        Genres = filter == "Genres" ? new[] { tag } : null,
                        EnableTotalRecordCount = false
                    };
                    found = library.GetItemList(q) ?? Array.Empty<BaseItem>();
                }
                catch (Exception ex)
                {
                    logger?.Warn("[LLM_AI] Tag cleanup : GetItemList ({0}) échoué : {1}", filter, ex.Message);
                    continue;
                }
                foreach (var it in found)
                    if (it != null && it.Id != Guid.Empty && !items.ContainsKey(it.Id))
                        items[it.Id] = it;
            }

            int removed = 0, migrated = 0;
            foreach (var item in items.Values)
            {
                ct.ThrowIfCancellationRequested();

                var tags = item.Tags ?? Array.Empty<string>();
                var hasTag = tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
                var genres = item.Genres ?? Array.Empty<string>();
                var hasLegacyGenre = genres.Any(g => string.Equals(g, tag, StringComparison.OrdinalIgnoreCase));
                if (!hasTag && !hasLegacyGenre) continue;

                try
                {
                    if (hasTag)
                        item.Tags = tags
                            .Where(t => !string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))
                            .ToArray();
                    if (hasLegacyGenre)
                    {
                        item.Genres = genres
                            .Where(g => !string.Equals(g, tag, StringComparison.OrdinalIgnoreCase))
                            .ToArray();
                        migrated++;
                    }
                    item.UpdateToRepository(ItemUpdateType.MetadataEdit);
                    removed++;
                }
                catch (Exception ex)
                {
                    logger?.Warn("[LLM_AI] Tag cleanup : échec sur « {0} » : {1}", item.Name, ex.Message);
                }
            }

            logger?.Info("[LLM_AI] Tag cleanup « {0} » : {1} item(s) nettoyé(s) ({2} genre(s) hérité(s) migré(s)).",
                tag, removed, migrated);
            return Task.CompletedTask;
        }
    }
}