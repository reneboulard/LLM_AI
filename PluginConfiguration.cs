using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace LLM_AI
{
    /// <summary>
    /// Un backend LLM (serveur Ollama + modèle) avec sa priorité de tentative.
    /// La tâche planifiée essaye les backends <see cref="Enabled"/> par
    /// <see cref="Priority"/> croissante (1 = le plus prioritaire, essayé en
    /// premier) et bascule sur le suivant si le serveur est indisponible.
    /// </summary>
    public class LlmBackend
    {
        /// <summary>
        /// Type de provider : <c>"ollama_local"</c> (serveur Ollama local,
        /// <see cref="Url"/> = hôte:port), <c>"ollama_cloud"</c> (Ollama cloud
        /// ollama.com, clé <see cref="PluginConfiguration.OllamaApiKey"/>) ou
        /// <c>"gemini"</c> (Google Gemini, clé
        /// <see cref="PluginConfiguration.GeminiApiKey"/>). Défaut
        /// <c>"ollama_local"</c>. Porte la même dynamique que l'app de
        /// référence /var/www/llm_core (3 providers, routage par type).
        /// </summary>
        public string Provider { get; set; } = "ollama_local";

        /// <summary>
        /// URL de base : Ollama local (ex. http://192.168.11.2:11434),
        /// Ollama cloud (https://ollama.com), Gemini
        /// (https://generativelanguage.googleapis.com/v1beta). Vide = défaut
        /// selon <see cref="Provider"/>.
        /// </summary>
        public string Url { get; set; } = "";

        /// <summary>
        /// Nom du modèle. Pour ollama_local/ollama_cloud : ex. gemma4:26b.
        /// Pour gemini : ex. gemini-2.0-flash.
        /// </summary>
        public string Model { get; set; } = "";

        /// <summary>
        /// true = ce backend participe au repli. false = ignoré (serveur
        /// temporairement désactivé sans perdre la config).
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Priorité de tentative : 1 = la plus haute (essayée en premier),
        /// 2, 3… en ordre croissant. Tri ascendant, puis ordre de liste.
        /// </summary>
        public int Priority { get; set; } = 1;

        /// <summary>
        /// Type de provider normalisé (enum) parsé depuis <see cref="Provider"/>
        /// (tolérant casse/préfixe). Valeur inconnue → <see cref="LlmProvider.OllamaLocal"/>.
        /// </summary>
        public LlmProvider ProviderType =>
            LlmProviderHelper.Parse(Provider);
    }

    /// <summary>
    /// Type de backend LLM (portage des 3 providers de /var/www/llm_core).
    /// </summary>
    public enum LlmProvider { OllamaLocal, OllamaCloud, Gemini }

    public static class LlmProviderHelper
    {
        public static LlmProvider Parse(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return LlmProvider.OllamaLocal;
            switch (s.Trim().ToLowerInvariant())
            {
                case "ollama_cloud":
                case "cloud":
                    return LlmProvider.OllamaCloud;
                case "gemini":
                    return LlmProvider.Gemini;
                default:
                    return LlmProvider.OllamaLocal;
            }
        }

        /// <summary>URL de base par défaut selon le provider.</summary>
        public static string DefaultUrl(LlmProvider p)
        {
            switch (p)
            {
                case LlmProvider.OllamaCloud: return "https://ollama.com";
                case LlmProvider.Gemini:       return "https://generativelanguage.googleapis.com/v1beta";
                default:                       return "http://192.168.11.2:11434";
            }
        }
    }

    /// <summary>
    /// Configuration sérialisée du plugin LLM_AI (XML, gérée par l'hôte Emby).
    /// Éditable via la page de configuration du plugin dans le dashboard Emby.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// Liste des backends LLM configurés, essayés par ordre de priorité avec
        /// repli si un serveur est indisponible. Éditée par la page de config.
        /// </summary>
        public List<LlmBackend> LlmBackends { get; set; } = new List<LlmBackend>();

        /// <summary>
        /// URL de l'API Ollama (legacy, mono-serveur). Conservée pour migrer
        /// les configs existantes : si <see cref="LlmBackends"/> est vide, la
        /// tâche planifiée construit un backend unique depuis ce champ. La
        /// page de config ne l'édite plus (elle gère <see cref="LlmBackends"/>).
        /// </summary>
        public string LlmUrl { get; set; } = "http://192.168.11.2:11434";

        /// <summary>
        /// URL publique d'Emby (ex. http://192.168.11.2:8096) utilisée pour
        /// construire les <c>image_url</c> retournés au LLM (affichage uniquement).
        /// </summary>
        public string EmbyPublicUrl { get; set; } = "http://192.168.11.2:8096";

        /// <summary>
        /// Nom du modèle Ollama (legacy, mono-serveur). Conservé pour la
        /// migration au même titre que <see cref="LlmUrl"/>.
        /// </summary>
        public string ModelName { get; set; } = "gemma4:26b";

        /// <summary>
        /// Clé API TMDB (themoviedb.org) utilisée par l'outil <c>tmdb_lookup</c>
        /// pour enrichir les synopsis/statuts quand l'EPG est vide. Laisser vide
        /// pour désactiver l'outil (il ne sera pas exposé au LLM).
        /// </summary>
        public string TmdbApiKey { get; set; } = "";

        /// <summary>Langue TMDB (ex. fr-FR, en-US). Défaut fr-FR.</summary>
        public string TmdbLanguage { get; set; } = "fr-FR";

        /// <summary>
        /// Clé API TheTVDB.com (v4) pour l'outil <c>tvdb_search</c> (enrichissement
        /// synopsis/statut des séries — portage de l'outil PHP qui fonctionne avec
        /// le LLM). Repli sur la variable d'environnement <c>TVDB_API_KEY</c> si
        /// vide. Laisser les deux vides pour désactiver l'outil. À renseigner via
        /// la page de configuration (persisté dans le fichier XML), pas en dur.
        /// </summary>
        public string TvdbApiKey { get; set; } = "";

        /// <summary>
        /// Clé pour l'API cloud Ollama (web_search / web_fetch). Repli sur la
        /// variable d'environnement <c>OLLAMA_API_KEY</c> si vide. Laisser les
        /// deux vides pour désactiver ces outils. À renseigner via la page de
        /// configuration du plugin (persisté dans le fichier XML de config),
        /// pas en dur dans le source.
        /// </summary>
        public string OllamaApiKey { get; set; } = "";

        /// <summary>
        /// URL de base d'une instance SearXNG auto-hébergée (ex.
        /// <c>http://192.168.11.24</c> ou <c>http://searxng.local:8888</c>),
        /// utilisée comme backend alternatif pour l'outil <c>web_search</c>.
        /// L'outil interroge <c>{url}/search?q=…&amp;format=json</c>. Si renseigné,
        /// SearXNG est privilégié (gratuit, sans quota, résultats JSON) ; sinon
        /// repli sur l'API cloud Ollama (<see cref="OllamaApiKey"/>). Optionnel :
        /// l'utilisateur de la communauté n'a pas besoin d'installer SearXNG —
        /// il lui suffit d'une clé Ollama. Laisser vide pour désactiver SearXNG.
        /// </summary>
        public string SearXngUrl { get; set; } = "";

        /// <summary>
        /// Active le backend <b>direct auto-hébergé</b> pour l'outil
        /// <c>web_fetch</c> : récupération de la page par <c>HttpClient</c> côté
        /// plugin + extraction locale (titre, métadonnées og:/twitter,
        /// JSON-LD schema.org, texte, titres, tableaux) plutôt que le simple
        /// relai du contenu brut d'Ollama cloud. Aucune clé requise — fonctionne
        /// pour tout utilisateur de la communauté dès l'installation. Si la
        /// récupération directe échoue (anti-bot, 403, page trop courte) et
        /// qu'une clé Ollama cloud est présente, l'outil repli
        /// automatiquement sur <c>https://ollama.com/api/web_fetch</c>. Défaut
        /// <c>true</c> (communauté). <c>false</c> = utiliser uniquement Ollama
        /// cloud (nécessite <see cref="OllamaApiKey"/>). Portage C# de la logique
        /// d'extraction de <c>/var/www/llm_core/tools/fetch_web_page.php</c>
        /// (sans ses dépendances curl-impersonate/Redis, non portables).
        /// </summary>
        public bool WebFetchDirect { get; set; } = true;

        /// <summary>
        /// Clé pour l'API Google Gemini (provider <c>gemini</c> des backends
        /// LLM). Repli sur la variable d'environnement <c>GEMINI_API_KEY</c>
        /// si vide. Laisser les deux vides pour désactiver le provider Gemini.
        /// </summary>
        public string GeminiApiKey { get; set; } = "";

        /// <summary>
        /// URL de la page « nouveautés » de Showbizz.net à scraper via l'outil
        /// <c>showbizz_new_releases</c>. Laisser vide pour désactiver l'outil.
        /// </summary>
        public string ShowbizzUrl { get; set; } = "";

        /// <summary>
        /// Regex .NET appliqué au HTML de <see cref="ShowbizzUrl"/> pour extraire
        /// les titres (groupe nommé « title » ; groupe « url » optionnel). Laisser
        /// vide pour un repli générique (texte des balises &lt;a&gt;). À régler
        /// selon la structure réelle de la page.
        /// </summary>
        public string ShowbizzPattern { get; set; } = "";

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
        public string ScheduleTask { get; set; } = "Daily 03:00 | Recommande des enregistrements de SÉRIES : 1) les nouvelles séries (S01E01) à venir dans l'EPG mais absentes de ma bibliothèque (get_emby_info action=epg_series premieres_only=true), enrichis-les via tmdb_lookup/tvdb_search quand le synopsis EPG est vide, et croise avec showbizz_new_releases ; 2) les nouvelles saisons à venir des séries que je possède déjà mais qui ne sont pas dans mes enregistrements planifiés (get_emby_info action=epg_series new_seasons=true). Les filtres chaines/genres et les flags Kids/News/Sports s'appliquent. Recommande les drames, thrillers, comédies de fiction et scifi dignes d'être enregistrés. Retourne un tableau JSON [{title, kind, reason, priority, channel, start, showbizz_match}] où kind vaut \"series\".";

        /// <summary>
        /// Prompt de la tâche FILMS (sans partie planification — la
        /// planification vient de <see cref="ScheduleTask"/>). Exécuté comme un
        /// second run agent indépendant (contexte séparé du run séries) puis
        /// fusionné avec celui-ci. Vide = pas de run films (séries seulement).
        /// </summary>
        public string ScheduleTaskMovies { get; set; } = "Recommande des enregistrements de FILMS : les films à venir dans l'EPG mais absents de la bibliothèque (get_emby_info action=epg_movies), enrichis via tmdb_lookup quand le synopsis EPG est vide. Les filtres chaines/genres et les flags Kids/News/Sports s'appliquent. Recommande les drames, thrillers, comédies de fiction et scifi dignes d'être enregistrés. Retourne un tableau JSON [{title, kind, reason, priority, channel, start, showbizz_match}] où kind vaut \"movie\".";

        /// <summary>
        /// Nombre max de séries soumises au LLM par appel epg_series (plafond
        /// dur côté serveur, appliqué APRÈS filtrage et PRÉ-TRI par pertinence :
        /// on garde les <see cref="MaxSeriesBatch"/> meilleures, pas les
        /// premières par date). Protège le contexte du modèle local.
        /// </summary>
        public int MaxSeriesBatch { get; set; } = 40;

        /// <summary>
        /// Nombre max de films soumis au LLM par appel epg_movies (plafond dur
        /// côté serveur, APRÈS filtrage et pré-tri par pertinence).
        /// </summary>
        public int MaxMovieBatch { get; set; } = 30;

        /// <summary>
        /// Active la journalisation verbeuse de l'agent dans le journal Emby :
        /// system prompt complet, prompt utilisateur complet, réponse complète
        /// de chaque itération et chaque résultat d'outil réinjecté. Désactivé
        /// par défaut (évite de gonfler le journal) — à activer ponctuellement
        /// pour comprendre ce que voit le LLM.
        /// </summary>
        public bool DebugVerbose { get; set; } = false;

        /// <summary>
        /// Dernière liste de recommandations produite par l'agent (JSON tel que
        /// renvoyé par le LLM, p. ex. <c>[{title,kind,reason,channel,start,showbizz_match}]</c>).
        /// Persistance <b>plugin-side</b> (pas éditable dans la page de config) :
        /// la tâche planifiée y écrit la réponse finale via
        /// <see cref="MediaBrowser.Common.Plugins.BasePlugin.SaveConfiguration"/>,
        /// et la page dashboard « Recommandations LLM AI » la relit via
        /// <c>getPluginConfiguration</c> pour l'afficher. Vide tant que la tâche
        /// n'a pas produit de réponse.
        /// </summary>
        public string Recommendations { get; set; } = "";

        /// <summary>
        /// Date/heure (UTC, format ISO 8601) à laquelle <see cref="Recommendations"/>
        /// a été produite — affichée en en-tête de la page de recommandations.
        /// </summary>
        public string RecommendationsDate { get; set; } = "";

        /// <summary>
        /// Drop list persistante : tableau JSON de titres (ex.
        /// <c>["Star Trek","Castle"]</c>) à **exclure des recommandations**. Lu par
        /// <c>get_emby_info</c> (epg_series/epg_movies) qui retire ces titres de la
        /// liste envoyée au LLM (épuration en amont) — le LLM ne les recommande
        /// donc plus. Alimenté par le bouton « Oublier » de la page de
        /// recommandations ET éditable manuellement dans la page de config
        /// (textarea, un titre par ligne ; le JS convertit array↔multiligne).
        /// </summary>
        public string DroppedTitles { get; set; } = "";

        /// <summary>
        /// Whitelist de chaines (tableau JSON de noms, ex.
        /// <c>["TF1","Arte"]</c>) : seuls les programmes EPG diffusés sur ces
        /// chaines sont renvoyés à l'agent par <c>get_emby_info</c>
        /// (epg_series/epg_movies). Vide = toutes les chaines (pas de filtre).
        /// Spécifique à la tâche de recommandation. Peuplée par la page de
        /// config (cases à cocher depuis <c>LiveTv/Channels</c>).
        /// </summary>
        public string ChannelWhitelist { get; set; } = "";

        /// <summary>
        /// Whitelist de genres (tableau JSON, ex. <c>["Drama","Crime"]</c>) :
        /// seuls les programmes dont au moins un genre figure dans la liste
        /// sont renvoyés à l'agent. Vide = tous les genres. Peuplée par la
        /// page de config (cases depuis l'API <c>Genres</c>).
        /// </summary>
        public string GenreWhitelist { get; set; } = "";

        /// <summary>
        /// Flags orthogonaux à INCLURE pour les SÉRIES, en plus de la fiction :
        /// tableau JSON subset de <c>["kids","news","sports"]</c>. Vide = séries
        /// de fiction seulement (les programmes <c>IsKids</c>/<c>IsNews</c>/
        /// <c>IsSports</c> sont exclus). Cocher un flag l'ajoute (modèle opt-in,
        /// fiction par défaut). <c>series</c>/<c>films</c> ne figurent pas ici :
        /// la catégorie est garantie par l'appel outil (<c>epg_series</c> vs
        /// <c>epg_movies</c>), pas par un flag. Voir <see cref="MovieFlags"/>.
        /// </summary>
        public string SeriesFlags { get; set; } = "";

        /// <summary>
        /// Flags orthogonaux à INCLURE pour les FILMS, en plus de la fiction :
        /// tableau JSON subset de <c>["kids","news","sports"]</c>. Vide = films
        /// de fiction seulement. Même sémantique opt-in que
        /// <see cref="SeriesFlags"/>.
        /// </summary>
        public string MovieFlags { get; set; } = "";

        // ------------------------------------------------------------------
        //  Section « À regarder ce soir » (recommandation personnalisée par
        //  usager, à la demande — endpoint TonightApiService). Indépendante de
        //  la tâche planifiée globale (admin) : croise l'historique de
        //  visionnage de l'usager avec les programmes EPG de la soirée.
        // ------------------------------------------------------------------

        /// <summary>
        /// Active la section « À regarder ce soir » (page Recommandations) et
        /// l'endpoint <c>/Plugins/LLMAI/Tonight</c>. <c>false</c> = la section
        /// n'est pas rendue et l'endpoint renvoie une réponse désactivée (pas
        /// de run LLM). Défaut <c>true</c>.
        /// </summary>
        public bool TonightEnabled { get; set; } = true;

        /// <summary>
        /// Borne de début de la fenêtre temporelle « ce soir » pour l'action
        /// <c>epg_tonight</c> (format <c>HH:mm</c>, ex. <c>"18:00"</c>). Vide =
        /// « maintenant » (l'EPG est interrogé à partir de l'heure courante).
        /// </summary>
        public string TonightWindowStart { get; set; } = "";

        /// <summary>
        /// Borne de fin de la fenêtre temporelle « ce soir » (format <c>HH:mm</c>,
        /// ex. <c>"23:59"</c>). Vide = <c>"23:59"</c> (fin de la journée). Un
        /// programme est retenu si son <c>StartDate</c> tombe dans
        /// [<c>TonightWindowStart</c> ; <c>TonightWindowEnd</c>].
        /// </summary>
        public string TonightWindowEnd { get; set; } = "23:59";

        /// <summary>
        /// Template du prompt envoyé au LLM pour la section « ce soir ». Le
        /// profil de goût (historique récent de l'usager) et la consigne
        /// d'appel à <c>epg_tonight</c> sont injectés à l'exécution par
        /// <c>TonightApiService</c> ; ce champ porte le squelette /
        /// l'orientation éditoriale (ex. « privilégie la fiction, croise avec
        /// mes goûts, recommande à regarder en direct ou à enregistrer »).
        /// </summary>
        public string TonightPrompt { get; set; } =
            "À partir de l'historique de visionnage de l'usager (profil de goût fourni), des " +
            "programmes de l'EPG pour ce soir (appelle get_emby_info avec action=\"epg_tonight\") " +
            "ET des enregistrements récents non visionnés listés dans le message (films/épisodes " +
            "enregistrés ces derniers jours mais pas encore regardés), recommande ce qui pourrait " +
            "lui plaire À REGARDER CE SOIR. Croise les genres/titres de l'historique avec l'EPG du " +
            "soir ET avec les enregistrements disponibles : si l'usager suit une série et qu'un " +
            "nouvel épisode enregistré de cette série est non visionné, c'est un candidat de choix. " +
            "Pour chaque recommandation, précise kind=\"series\" ou kind=\"movie\" et " +
            "priority high/medium/low, et source=\"live\" (programme EPG du soir : à regarder en " +
            "direct ou à enregistrer) ou source=\"recording\" (enregistrement disponible : à " +
            "regarder maintenant). Reprends title/channel/start tels quels depuis epg_tonight pour " +
            "le source=\"live\" ; pour source=\"recording\", reprends id tel quel depuis la liste " +
            "des enregistrements. Tu peux enrichir via tmdb_lookup/web_search si utile, mais reste " +
            "pratique et rapide : l'objectif est une courte sélection personnalisée pour ce soir, " +
            "pas un audit exhaustif.";

        /// <summary>
        /// Nombre max de programmes soumis au LLM par appel <c>epg_tonight</c>
        /// (plafond dur côté serveur, APRÈS filtrage whitelists/flags/drop et
        /// pré-tri par pertinence). Protège le contexte du modèle. Défaut 10
        /// (sélection courte pour « ce soir »).
        /// </summary>
        public int MaxTonightBatch { get; set; } = 10;

        /// <summary>
        /// Durée de validité (heures) du cache par usager de la section « ce
        /// soir » : un second appel dans cette fenêtre renvoie le résultat
        /// précédent sans relancer le LLM. <c>0</c> = pas de cache (run à
        /// chaque ouverture). Défaut 4. Le bouton « Rafraîchir » de la page
        /// bypass le cache (force un nouveau run).
        /// </summary>
        public int TonightCacheHours { get; set; } = 4;

        /// <summary>
        /// Fenêtre de recherche (en jours) pour les enregistrements récents NON
        /// visionnés injectés dans le prompt « ce soir » : l'usager peut avoir
        /// fait enregistrer un film/épisode ces N derniers jours sans l'avoir
        /// regardé — ces enregistrements disponibles sont des candidats de choix
        /// immédiat (« à regarder ce soir »), au même titre que l'EPG live. Croisé
        /// avec le profil de goût, ça permet de remonter un nouvel épisode enregistré
        /// d'une série que l'usager suit. Défaut 7.
        /// </summary>
        public int TonightRecordingsDays { get; set; } = 7;

        /// <summary>
        /// Nombre minimal de recommandations attendu pour la section « ce soir ».
        /// Si l'EPG du soir + les enregistrements récents non visionnés produisent
        /// moins de <c>TonightMinRecommendations</c> pistes, le LLM complète avec
        /// des titres de la bibliothèque de l'usager non encore visionnés (réserve
        /// pré-fetchée et injectée dans le prompt, <c>source="library"</c>).
        /// Garantit une sélection exploitable même quand l'EPG est vide. Défaut 3.
        /// </summary>
        public int TonightMinRecommendations { get; set; } = 3;
    }
}