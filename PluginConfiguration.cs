using MediaBrowser.Model.Plugins;

namespace LLM_AI
{
    /// <summary>
    /// Configuration sérialisée du plugin LLM_AI (XML, gérée par l'hôte Emby).
    /// Éditable via la page de configuration du plugin dans le dashboard Emby.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>URL de l'API Ollama (ex. http://192.168.11.2:11434).</summary>
        public string LlmUrl { get; set; } = "http://192.168.11.2:11434";

        /// <summary>Nom du modèle Ollama (ex. gemma4:latest).</summary>
        public string ModelName { get; set; } = "gemma4:latest";

        /// <summary>
        /// Directives RAG : prompt système envoyé au LLM (role: system)
        /// à chaque appel de la tâche planifiée.
        /// </summary>
        public string RagDirectives { get; set; } = "";

        /// <summary>
        /// Tâche planifiée combinée : « &lt;planification&gt; | &lt;prompt de tâche&gt; ».
        /// Ex. « Daily 03:00 | Résume les nouveaux médias ajoutés cette semaine ».
        /// La planification (gauche du '|') fixe les triggers par défaut ;
        /// le prompt (droite du '|') est envoyé au LLM comme message utilisateur.
        /// </summary>
        public string ScheduleTask { get; set; } = "";
    }
}