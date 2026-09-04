using System;

namespace LLM_AI
{
    /// <summary>
    /// Les quatre prompts/directives éditables de la page de configuration,
    /// dans leur version « propre » d'origine — FR et EN. Sert de source de
    /// vérité unique : les valeurs par défaut des propriétés de
    /// <see cref="PluginConfiguration"/> (nouvelle installation) ET le bouton
    /// « Réinitialiser » de la page de config (endpoint
    /// <c>/Plugins/LLMAI/DefaultPrompts</c>) lisent ici.
    /// </summary>
    /// <remarks>
    /// Raison d'être du bouton de reset : une installation neuve installe les
    /// directives en français ; un usager anglophone (ou dont la langue
    /// d'affichage diffère) clique « Réinitialiser » et obtient la directive
    /// propre dans SA langue (langue de réponse configurée, sinon langue
    /// d'affichage Emby — voir <c>ConfigApiService.DefaultPromptsRequest</c>).
    /// </remarks>
    internal sealed class PromptDefaults
    {
        public string RagDirectives { get; }
        public string ScheduleTask { get; }
        public string ScheduleTaskMovies { get; }
        public string TonightPrompt { get; }

        public PromptDefaults(string ragDirectives, string scheduleTask,
            string scheduleTaskMovies, string tonightPrompt)
        {
            RagDirectives = ragDirectives ?? "";
            ScheduleTask = scheduleTask ?? "";
            ScheduleTaskMovies = scheduleTaskMovies ?? "";
            TonightPrompt = tonightPrompt ?? "";
        }
    }

    /// <summary>
    /// Accès aux jeux de prompts par défaut. <see cref="For"/> résout une clé
    /// de langue <see cref="I18n"/> (« fr », « en »…) avec repli anglais.
    /// </summary>
    internal static class DefaultPrompts
    {
        /// <summary>
        /// Jeu de prompts par défaut pour la clé de langue donnée.
        /// « fr » → français ; toute autre valeur → anglais (repli universel,
        /// même sémantique que <see cref="I18n.S"/>).
        /// </summary>
        internal static PromptDefaults For(string langKey) =>
            string.Equals(langKey, I18n.Fr, StringComparison.Ordinal) ? Fr : En;

        /// <summary>Version française — installée par défaut à la création de la config.</summary>
        internal static readonly PromptDefaults Fr = new PromptDefaults(
            ragDirectives:
                "Vérifie toujours via les outils avant d'affirmer : ne recommande jamais un titre " +
                "déjà possédé en bibliothèque ou déjà programmé en enregistrement, et ne devine jamais " +
                "une donnée absente (synopsis, année, id) — dis simplement qu'elle est inconnue.\n" +
                "ANNÉE DE PRODUCTION : quand l'année de production d'un candidat est disponible " +
                "(champ year), tiens-en compte dans ton choix — donne une légère préférence aux " +
                "productions récentes. Ne pénalise JAMAIS un titre dont l'année est absente ou " +
                "inconnue : cela signifie seulement que l'EPG ne la fournit pas, pas que la " +
                "production est vieille. L'année est un critère secondaire : la correspondance " +
                "avec les goûts de l'usager (genres, note) reste prioritaire.\n" +
                "Explique chaque recommandation en une ou deux phrases concrètes, reliées aux goûts " +
                "de l'usager (genres, historique, notes), et ne recommande pas deux fois le même " +
                "titre dans une même réponse.",
            scheduleTask: "Daily 03:00 | Recommande des enregistrements de SÉRIES : 1) les nouvelles séries (S01E01) à venir dans l'EPG mais absentes de ma bibliothèque (get_emby_info action=epg_series premieres_only=true), enrichis-les via tmdb_lookup/tvdb_search quand le synopsis EPG est vide, et croise avec new_releases ; 2) les nouvelles saisons à venir des séries que je possède déjà mais qui ne sont pas dans mes enregistrements planifiés (get_emby_info action=epg_series new_seasons=true). Les filtres chaines/genres et les flags Kids/News/Sports s'appliquent. Recommande les drames, thrillers, comédies de fiction et scifi dignes d'être enregistrés. Retourne un tableau JSON [{title, kind, reason, priority, channel, start, showbizz_match}] où kind vaut \"series\".",
            scheduleTaskMovies: "Recommande des enregistrements de FILMS : les films à venir dans l'EPG mais absents de la bibliothèque (get_emby_info action=epg_movies), enrichis via tmdb_lookup quand le synopsis EPG est vide. Les filtres chaines/genres et les flags Kids/News/Sports s'appliquent. Recommande les drames, thrillers, comédies de fiction et scifi dignes d'être enregistrés. Retourne un tableau JSON [{title, kind, reason, priority, channel, start, showbizz_match}] où kind vaut \"movie\".",
            tonightPrompt:
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
                "pas un audit exhaustif.");

        /// <summary>
        /// Version anglaise — jamais installée automatiquement : elle sert au
        /// bouton « Réinitialiser » quand la langue configurée est l'anglais
        /// (ou ne résout pas vers le français).
        /// </summary>
        internal static readonly PromptDefaults En = new PromptDefaults(
            ragDirectives:
                "Always verify with the tools before asserting: never recommend a title already " +
                "owned in the library or already scheduled for recording, and never guess missing " +
                "data (synopsis, year, id) — simply say it is unknown.\n" +
                "PRODUCTION YEAR: when a candidate's production year is available (year field), " +
                "take it into account — give a slight preference to recent productions. NEVER " +
                "penalize a title whose year is missing or unknown: it only means the EPG does " +
                "not provide it, not that the production is old. The year is a secondary " +
                "criterion: matching the user's tastes (genres, rating) remains the priority.\n" +
                "Explain each recommendation in one or two concrete sentences, tied to the user's " +
                "tastes (genres, history, ratings), and do not recommend the same title twice in " +
                "a single answer.",
            scheduleTask: "Daily 03:00 | Recommend SERIES recordings: 1) new series (S01E01) upcoming in the EPG but missing from my library (get_emby_info action=epg_series premieres_only=true), enrich them via tmdb_lookup/tvdb_search when the EPG synopsis is empty, and cross-check with new_releases; 2) upcoming new seasons of series I already own that are not in my scheduled recordings (get_emby_info action=epg_series new_seasons=true). Channel/genre filters and Kids/News/Sports flags apply. Recommend dramas, thrillers, fiction comedies and sci-fi worth recording. Return a JSON array [{title, kind, reason, priority, channel, start, showbizz_match}] where kind is \"series\".",
            scheduleTaskMovies: "Recommend MOVIE recordings: movies upcoming in the EPG but missing from the library (get_emby_info action=epg_movies), enrich via tmdb_lookup when the EPG synopsis is empty. Channel/genre filters and Kids/News/Sports flags apply. Recommend dramas, thrillers, fiction comedies and sci-fi worth recording. Return a JSON array [{title, kind, reason, priority, channel, start, showbizz_match}] where kind is \"movie\".",
            tonightPrompt:
                "Based on the user's viewing history (taste profile provided), tonight's EPG programs " +
                "(call get_emby_info with action=\"epg_tonight\") AND the recent unwatched recordings " +
                "listed in the message (movies/episodes recorded over the last few days but not yet " +
                "watched), recommend what they might enjoy WATCHING TONIGHT. Cross-reference the " +
                "genres/titles from the history with tonight's EPG AND with the available recordings: " +
                "if the user follows a series and a new recorded episode of that series is unwatched, " +
                "that is a prime candidate. For each recommendation, specify kind=\"series\" or " +
                "kind=\"movie\" and priority high/medium/low, and source=\"live\" (tonight's EPG " +
                "program: to watch live or to record) or source=\"recording\" (available recording: " +
                "to watch now). Copy title/channel/start verbatim from epg_tonight for source=\"live\"; " +
                "for source=\"recording\", copy id verbatim from the recordings list. You may enrich " +
                "via tmdb_lookup/web_search if useful, but stay practical and fast: the goal is a " +
                "short personalized selection for tonight, not an exhaustive audit.");
    }
}