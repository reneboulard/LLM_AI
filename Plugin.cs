using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
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
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
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
        /// Pages web embarquées servies par le dashboard Emby.
        /// 1) la page HTML de config (lien « ConfigPageUrl » depuis la liste des plugins),
        /// 2) le module JS AMD de config chargé via <c>data-controller="__plugin/..."</c>,
        /// 3) la page « Recommandations LLM AI » (menu principal du dashboard,
        ///    <see cref="PluginPageInfo.EnableInMainMenu"/>) qui affiche la dernière
        ///    réponse de l'agent,
        /// 4) le module JS AMD de cette page.
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
            }
        };
    }
}