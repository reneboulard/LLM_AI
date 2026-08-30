using System;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace LLM_AI
{
    /// <summary>
    /// Résolution d'un id d'item Emby reçu en chaîne (payload de reco, tool
    /// result) vers le <see cref="BaseItem"/> correspondant.
    /// </summary>
    /// <remarks>
    /// <para><b>Devise canonique du plugin</b> : l'<c>InternalId</c>
    /// (<c>long</c>) sérialisé en chaîne. C'est la seule forme comprise par
    /// toute la couche REST/UI d'Emby (<c>/emby/Items/{id}/...</c>, filtre
    /// <c>Ids=</c>, lecture <c>playbackManager</c>) ET par
    /// <see cref="InternalItemsQuery.ItemIds"/> (<c>long[]</c>). Le
    /// <see cref="BaseItem.Id"/> (<c>Guid</c>), lui, est REJETÉ par la couche
    /// REST (400/500, filtre silencieusement ignoré) — ne jamais l'émettre vers
    /// le LLM ou l'UI ; passer toujours par <c>item.InternalId</c>.</para>
    /// <para>Les <c>Guid</c> restent acceptés en <b>entrée</b> uniquement
    /// (payloads hérités en cache, ids recopiés/déformés par le LLM) : ils
    /// sont résolus via <c>GetItemById(Guid)</c>, puis l'appelant normalise en
    /// InternalId avant toute nouvelle émission.</para>
    /// </remarks>
    internal static class ItemIdResolver
    {
        /// <summary>
        /// Résout <paramref name="raw"/> vers l'item : InternalId (long) en
        /// chaîne d'abord, puis repli Guid hérité. Retourne null si
        /// vide/introuvable — ne lève jamais (erreurs de lookup avalées).
        /// </summary>
        internal static BaseItem Resolve(ILibraryManager library, string raw)
        {
            if (library == null || string.IsNullOrWhiteSpace(raw)) return null;
            raw = raw.Trim();
            try
            {
                if (long.TryParse(raw, out var internalId) && internalId > 0)
                    return library.GetItemById(internalId);
            }
            catch { /* id numérique invalide → repli Guid */ }
            try
            {
                if (Guid.TryParse(raw, out var guid))
                    return library.GetItemById(guid);
            }
            catch { }
            return null;
        }
    }
}