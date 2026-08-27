using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LLM_AI
{
    /// <summary>
    /// Outil exposé au LLM via le protocole de tool-calling (tableau JSON).
    /// Chaque outil décrit ses arguments (schéma) et exécute une action
    /// retournant un résultat JSON minimal (seulement les champs utiles).
    /// </summary>
    public interface ILlmTool
    {
        /// <summary>Nom appelé par le LLM (ex. « get_emby_info »).</summary>
        string Name { get; }

        /// <summary>Description courte injectée dans le system prompt.</summary>
        string Description { get; }

        /// <summary>Schéma JSON des arguments (bloc « AVAILABLE TOOLS »).</summary>
        string ArgumentsSchema { get; }

        /// <summary>
        /// Exécute l'outil. Ne lève jamais : une erreur renvoie
        /// <c>{"error":"..."}</c> pour ne pas casser la boucle de l'agent.
        /// </summary>
        Task<string> ExecuteAsync(JsonElement args, CancellationToken ct);
    }

    /// <summary>
    /// Appel d'outil émis par le LLM, désérialisé depuis le tableau JSON
    /// de la réponse : <c>[{"tool":"...","arguments":{...}}, ...]</c>.
    /// </summary>
    public sealed class ToolCall
    {
        public string Tool { get; set; }
        public JsonElement Arguments { get; set; }
    }
}