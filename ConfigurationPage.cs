using System.IO;
using System.Reflection;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Common.Plugins;

namespace LLM_AI
{
    /// <summary>
    /// Page de configuration du plugin, servie par l'hôte Emby.
    /// L'hôte instancie cette classe par réflexion (Activator.CreateInstance)
    /// → constructeur sans paramètre obligatoire. La référence au plugin se
    /// récupère via le singleton <see cref="Plugin.Instance"/>.
    /// </summary>
    public class ConfigurationPage : IPluginConfigurationPage
    {
        public string Name => "LLM AI";

        public ConfigurationPageType ConfigurationPageType => ConfigurationPageType.PluginConfiguration;

        public IPlugin Plugin => LLM_AI.Plugin.Instance;

        public Stream GetHtmlStream()
        {
            return Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("LLM_AI.config.html");
        }
    }
}