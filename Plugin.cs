using System;
using System.Collections.Generic;
using System.IO;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace LLM_AI
{
    /// <summary>
    /// Point d'entrée principal du plugin.
    /// Hérite de BasePlugin&lt;TConfigurationType&gt; pour bénéficier de la
    /// persistance automatique de la configuration par l'hôte Emby.
    /// </summary>
    /// <remarks>
    /// Implémente <see cref="IHasWebPages"/> : la page de configuration est
    /// servie comme deux ressources embarquées (HTML + module JS AMD), le JS
    /// étant lié au HTML via <c>data-controller="__plugin/LLMAIConfigPageJS"</c>.
    /// C'est le mécanisme du dashboard moderne : un &lt;script&gt; inline injecté
    /// par innerHTML n'est jamais exécuté, seul le module déclaré dans
    /// <c>data-controller</c> est chargé via require(). Voir Emby.ComSkipper.
    /// </remarks>
    /// <remarks>
    /// Implémente <see cref="IHasThumbImage"/> : fournit l'image affichée dans la
    /// pastille du plugin (liste Dashboard → Plugins). L'hôte Emby sert cette
    /// image sur <c>/Plugins/{Id}/Thumb</c> et ne peuple <c>ImageTag</c> (donc ne
    /// remplace l'icône puzzle par défaut) que si le plugin implémente cette
    /// interface. La ressource embarquée <c>LLM_AI.thumb.png</c> (800x450, 16:9)
    /// est retournée comme flux PNG.
    /// </remarks>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
    {
        /// <summary>
        /// Singleton exposant le plugin à la page de configuration et à la
        /// tâche planifiée (qui n'ont pas accès à l'instance via DI).
        /// </summary>
        public static Plugin Instance { get; private set; }

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            // NOTE — ne pas toucher <see cref="Configuration"/> ici : au moment
            // du ctor, l'hôte n'a pas encore renseigné AssemblyFilePath (posé
            // APRÈS la construction) et ConfigurationFilePath en dépend — un
            // accès anticipé lève ArgumentNullException et fait échouer la
            // création du plugin (« Error creating LLM_AI.Plugin », Instance
            // cassée pour tout le serveur). Le registre du badge se charge donc
            // paresseusement à la première consultation (AiBadgeRegistry.EnsureLoaded).
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

        /// <summary>
        /// Image de la pastille du plugin (liste Dashboard → Plugins), servie par
        /// l'hôte sur <c>/Plugins/{Id}/Thumb</c>. Ressource embarquée 16:9.
        /// </summary>
        public Stream GetThumbImage()
            => GetType().Assembly.GetManifestResourceStream("LLM_AI.thumb.png");

        /// <summary>Format de l'image de pastille retournée par <see cref="GetThumbImage"/>.</summary>
        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        /// <summary>
        /// Pages web embarquées servies par le dashboard Emby.
        /// 1) la page HTML de config (lien « ConfigPageUrl » depuis la liste des plugins),
        /// 2) le module JS AMD de config chargé via <c>data-controller="__plugin/..."</c>,
        /// 3) la page « Recommandations LLM AI » (menu principal du dashboard,
        ///    <see cref="PluginPageInfo.EnableInMainMenu"/>) qui affiche la dernière
        ///    réponse de l'agent,
        /// 4) le module JS AMD de cette page.
        /// 5) le module i18n (LLMAII18n) : dictionnaires FR/EN + walker DOM,
        ///    chargé comme dépendance __plugin/LLMAII18n par config.js et
        ///    recommendations.js. Sans PluginPageInfo, le module ne serait pas
        ///    servi et la dépendance AMD ne se résoudrait pas.
        /// L'ordre compte pour la page de config : le HTML doit venir en premier
        /// (c'est elle que le dashboard lie au plugin dans la liste).
        /// </summary>
        public IEnumerable<PluginPageInfo> GetPages() => new[]
        {
            new PluginPageInfo
            {
                Name = "LLMAIConfigPage",
                EmbeddedResourcePath = "LLM_AI.config.html"
            },
            new PluginPageInfo
            {
                Name = "LLMAIConfigPageJS",
                EmbeddedResourcePath = "LLM_AI.config.js"
            },
            new PluginPageInfo
            {
                Name = "LLMAII18n",
                EmbeddedResourcePath = "LLM_AI.i18n.js"
            },
            new PluginPageInfo
            {
                Name = "LLMAIRecommendationsPage",
                EmbeddedResourcePath = "LLM_AI.recommendations.html",
                // Menu admin (dashboard) : section « Serveur ».
                EnableInMainMenu = true,
                // Menu utilisateur (le drawer principal qu'un utilisateur voit à
                // sa connexion) — rend la page visible hors de la zone admin,
                // directement dans le menu de l'app. Le drawer charge les pages
                // via getConfigurationPages({EnableInUserMenu:true,UserId}).
                EnableInUserMenu = true,
                // MenuSection doit être une des sections connues du dashboard
                // Emby ("server" | "devices" | "advanced" | null) — le navdrawer
                // (addPluginPagesToMainMenu) n'ajoute la page au menu admin que
                // si sa MenuSection correspond à la section rendue. Une valeur
                // libre comme "LLM AI" rend la page invisible côté admin.
                MenuSection = "server",
                DisplayName = "Recommandations LLM AI"
            },
            new PluginPageInfo
            {
                Name = "LLMAIRecommendationsPageJS",
                EmbeddedResourcePath = "LLM_AI.recommendations.js"
            },
            new PluginPageInfo
            {
                // Image de fond de la page Recommandations : module AMD exportant
                // une data URI (ASCII), servi sur web/ConfigurationPage?name=LLMAIBg
                // et chargé par recommendations.js via require(). L'endpoint sert les
                // ressources en UTF-8 : un binaire serait corrompu, d'où la data URI.
                Name = "LLMAIBg",
                EmbeddedResourcePath = "LLM_AI.bg.js"
            }
        };
    }
}