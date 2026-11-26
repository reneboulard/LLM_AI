using System;
using System.Collections.Generic;
using System.Linq;

namespace LLM_AI
{
    /// <summary>Surcharge d'ordre des backends LLM pour UN tour de chat.</summary>
    internal static class BackendOrder
    {
        /// <summary>
        /// Place en tête le backend dont le modèle est <paramref name="model"/>
        /// (nom tel que saisi en config, casse ignorée) ; les autres gardent
        /// leur ordre relatif — le repli multi-backend reste donc celui de la
        /// config. Aucun backend n'est ajouté : seuls ceux de
        /// <paramref name="sorted"/> (activés, déjà triés) sont réordonnés.
        /// <paramref name="model"/> vide ou introuvable : liste inchangée,
        /// <paramref name="found"/> vaut false seulement pour l'introuvable.
        /// Si plusieurs backends portent ce modèle, le mieux classé gagne.
        /// </summary>
        internal static List<LlmBackend> MoveToFront(
            IReadOnlyList<LlmBackend> sorted, string model, out bool found)
        {
            found = true;
            var result = new List<LlmBackend>(sorted);
            if (string.IsNullOrWhiteSpace(model)) return result;

            string wanted = model.Trim();
            int idx = result.FindIndex(b =>
                string.Equals((b.Model ?? "").Trim(), wanted, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) { found = false; return result; }
            if (idx == 0) return result;

            var chosen = result[idx];
            result.RemoveAt(idx);
            result.Insert(0, chosen);
            return result;
        }
    }
}
