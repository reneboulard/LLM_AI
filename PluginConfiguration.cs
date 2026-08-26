using MediaBrowser.Model.Plugins;

namespace MonPlugin
{
    /// <summary>
    /// Configuration simple et sérialisable du plugin.
    /// Hérite de BasePluginConfiguration pour être chargée/sauvegardée
    /// automatiquement par Emby au format XML.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>Exemple d'option texte.</summary>
        public string OptionExemple { get; set; } = "valeur par défaut";

        /// <summary>Active ou désactive la fonctionnalité principale du plugin.</summary>
        public bool ActiverFonctionnalite { get; set; } = true;
    }
}