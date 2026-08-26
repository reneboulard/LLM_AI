using System;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace MonPlugin
{
    /// <summary>
    /// Point d'entrée principal du plugin.
    /// Hérite de BasePlugin&lt;TConfigurationType&gt; pour bénéficier de la
    /// persistance automatique de la configuration par l'hôte Emby.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>
    {
        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
        }

        /// <summary>Nom affiché du plugin dans l'interface Emby.</summary>
        public override string Name => "LLM_AI";

        /// <summary>Description courte du plugin.</summary>
        public override string Description => "Un plugin d'exemple pour Emby.";

        /// <summary>Identifiant unique et stable du plugin.</summary>
        public override Guid Id => new Guid("e7d3dee6-ef19-46a9-985f-06318b682e60");

        // Note : Version n'est PAS surchargée. Dans cette build d'Emby,
        // BasePlugin.Version est sealed et renvoie un champ renseigné par
        // l'hôte au chargement à partir de la version de l'assembly.
        // -> la version effective est pilotée par <AssemblyVersion> dans le .csproj.
    }
}