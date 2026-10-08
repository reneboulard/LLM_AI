// Module d'internationalisation (i18n) du plugin LLM_AI — module AMD embarqué,
// servi comme ressource « LLMAII18n » (PluginPageInfo) et chargé comme
// dépendance __plugin/LLMAII18n par config.js et recommendations.js.
//
// Pourquoi un système custom léger plutôt que globalize.register ?
// Le globalize d'Emby est file-based (fetch JSON par locale) et son repli est
// match EXACT de locale normalisée puis en-us. normalizeLocaleName("fr-CA") ->
// "fr-ca", qui ne matche PAS "fr" : un usager Québec retomberait en anglais.
// Ici on détecte la langue via globalize.getCurrentLocale() (source de vérité
// Emby) PUIS on mappe tout préfixe "fr" -> "fr" (repli Québec correct), sinon
// "en". Dictionnaires inline => zéro fetch runtime, repli en-us sur clé absente.
//
// API exposée : { init, t, translateView, getLang }
//   init()         -> Promise<lang> : résout la langue courante (une seule fois).
//   t(key, ...a)   -> traduction ; substitution {0} {1} … ; repli en puis clé.
//   translateView  -> parcourt le DOM et applique data-i18n / data-i18n-html /
//                     data-i18n-ph / data-i18n-label / data-i18n-title.
//
// Overlay communautaire (v1.16.0, plan P3) : init() fait AUSSI un fetch de
// GET /Plugins/LLMAI/I18n (endpoint du plugin, en parallèle de la détection)
// — via ApiClient.ajax (jeton Emby attaché ; un fetch NU y reçoit 401,
// constat terrain 2026-10-04, voir fetchOverlayWeb). Le payload
// { "<lang>": { "web": {…} } } enrichit STRINGS (patch fr/en,
// ajout de langues comme « es ») AVANT la résolution pickLang, qui est
// désormais data-driven (itération Object.keys(STRINGS)). La section web
// n'est PAS validée côté serveur — la référence EN web vit dans CE module,
// pas dans la DLL — la validation est donc ici, par clé, au merge : clé
// inconnue du dictionnaire EN natif, placeholders {n} ou balises HTML
// divergents de l'EN natif → clé sautée + console.warn (miroir exact des
// règles 4-5 du kit/chargeur). Échec fetch (plugin < v1.16.0 → 404, hôte
// injoignable, réponse non-JSON) → null silencieux : natif inchangé
// (fail-open symétrique du chargeur) ; un statut HTTP ≠ 404 est signalé
// console.warn (la panne 401 initiale était totalement invisible).
define([], function () {
    "use strict";

    // ------------------------------------------------------------------
    //  Dictionnaires FR + EN (extensible : ajouter une locale ci-dessous,
    //  ou livrer un overlay communautaire — cf. la section Overlay).
    // ------------------------------------------------------------------
    var STRINGS = {
        fr: {
            // -- Page de configuration : titres / descriptions / labels ----
            "cfg.title": "LLM AI — Configuration",
            "cfg.docs.link": "📖 Documentation complète (GitHub — nouvel onglet)",
            "cfg.backends.h": "Serveurs LLM (repli par priorité)",
            "cfg.backends.desc": "Ajoutez un ou plusieurs serveurs LLM — Ollama (local ou cloud) ou Google Gemini. Chaque LLM a une priorité (1 = essayé en premier) et peut être activé ou non. Si un serveur ne répond pas, la tâche passe automatiquement au LLM activé suivant.",
            "cfg.backends.add": "+ Ajouter un LLM",
            "cfg.embyurl.label": "URL publique Emby",
            "cfg.embyurl.desc": "URL d'accès à Emby (ex. http://localhost:8096), utilisée pour construire les liens d'images transmis au LLM.",
            "cfg.relang.label": "Langue de réponse du LLM",
            "cfg.relang.auto": "Auto (langue de l'interface — défaut)",
            "cfg.relang.desc": "Choisissez la langue pour les recommandations, le rapport d'audit, les cartes .strm et les synopsis TMDB. L'extension privilégie votre langue de choix, puis l'anglais, et enfin une traduction par le LLM si nécessaire. Le réglage « Auto » suit la langue de votre interface Emby. Les titres de films et de séries, les noms de chaînes et les champs techniques restent inchangés pour garantir leur exactitude.",
            "cfg.filters.h": "Filtres de l'analyse du guide TV (tâche recommandations)",
            "cfg.filters.desc": "Ces filtres définissent les programmes du guide TV qui seront analysés pour vos recommandations. Pour les chaînes et les genres, cochez ceux que vous souhaitez conserver ; si rien n'est coché, tout est inclus. Concernant les catégories Enfants, Infos et Sports : si elles sont cochées, ces programmes seront ajoutés aux films et séries. Si elles restent décochées, seuls ces derniers seront pris en compte.",
            "cfg.channels.label": "Chaînes",
            "cfg.channels.search": "Rechercher une chaîne",
            "cfg.channels.ph": "Filtrer la liste…",
            "cfg.channels.loading": "Chargement des chaînes…",
            "cfg.channels.desc": "Chaînes à garder. Vide = toutes les chaînes.",
            "cfg.genres.label": "Genres",
            "cfg.genres.loading": "Chargement des genres…",
            "cfg.genres.desc": "Genres du guide TV (collectés depuis les programmes à venir) à garder. Vide = tous les genres.",
            "cfg.flags.series": "Types de programmes à ajouter — Séries",
            "cfg.flags.series.desc": "Par défaut, les programmes du guide TV marqués Enfants, Infos ou Sports sont ignorés. Cochez une catégorie pour l'inclure dans l'analyse des séries.",
            "cfg.flags.movies": "Types de programmes à ajouter — Films",
            "cfg.flags.movies.desc": "Par défaut, les programmes du guide TV marqués Enfants, Infos ou Sports sont ignorés. Cochez une catégorie pour l'inclure dans l'analyse des films.",
            "cfg.flag.kids": "Enfants",
            "cfg.flag.news": "Infos",
            "cfg.flag.sports": "Sports",
            "cfg.maxseries.label": "Plafond séries (par appel)",
            "cfg.maxseries.desc": "Nombre maximum de séries soumises au LLM (après pré-tri). Défaut 40.",
            "cfg.maxmovies.label": "Plafond films (par appel)",
            "cfg.maxmovies.desc": "Nombre maximum de films soumis au LLM (après pré-tri). Défaut 30.",
            "cfg.apikeys.h": "Clés API — services externes",
            "cfg.apikeys.desc": "Clés des services web auxquels l'extension peut faire appel. Un champ vide désactive le service correspondant : l'LLM ne le voit plus.",
            "cfg.tmdb.label": "Clé API TMDB",
            "cfg.tmdb.ph": "clé themoviedb.org",
            "cfg.tmdb.desc": "Clé de l'API TMDB, utilisée pour enrichir les fiches (synopsis, statut). Laisser vide pour désactiver l'outil.",
            "cfg.tmdblang.label": "Langue TMDB",
            "cfg.tmdblang.desc": "Ne sert que si « Langue de réponse du LLM » est sur Auto : langue utilisée pour interroger TMDB (ex. fr-FR, en-US). Quand une langue de réponse est choisie, c'est elle qui pilote TMDB.",
            "cfg.tvdb.label": "Clé API TheTVDB",
            "cfg.tvdb.ph": "clé api4.thetvdb.com",
            "cfg.tvdb.desc": "Clé de l'API TheTVDB (v4), utilisée pour enrichir les séries (synopsis FR en priorité). À défaut, la variable d'environnement TVDB_API_KEY est utilisée. Vide = outil désactivé.",
            "cfg.ollama.label": "Clé API Ollama (cloud et recherche web)",
            "cfg.ollama.desc": "Clé ollama.com, utilisée pour Ollama cloud et la recherche web. À défaut, la variable d'environnement OLLAMA_API_KEY est utilisée. Vide = Ollama cloud désactivé.",
            "cfg.searxng.label": "URL SearXNG (recherche web — optionnel)",
            "cfg.searxng.desc": "Instance SearXNG auto-hébergée, utilisée en priorité pour la recherche web — gratuit, sans quota. Le format JSON doit être activé dans ses réglages (search.formats). Sans SearXNG, la recherche web passe par Ollama cloud. Vide = désactivé.",
            "cfg.webfetch.label": "Récupération directe des pages web (sans clé)",
            "cfg.webfetch.desc": "Récupère les pages web directement depuis le serveur Emby et en extrait le contenu (titre, texte, tableaux) — sans quota ni clé. En cas de blocage anti-bot, repli automatique sur Ollama cloud si une clé est renseignée. Décochez pour tout passer par Ollama cloud. Activé par défaut.",
            "cfg.gemini.label": "Clé API Google Gemini",
            "cfg.gemini.desc": "Clé Google AI Studio, utilisée pour le serveur Gemini. À défaut, la variable d'environnement GEMINI_API_KEY est utilisée. Vide = Gemini désactivé.",
            "cfg.newreleases.label": "Sources nouveautés web",
            "cfg.newreleases.ph": "https://exemple.com/flux.rss\nhttps://exemple.com/nouveautes :: @showbizz",
            "cfg.newreleases.desc": "Une source par ligne : URL seule = flux RSS/Atom auto-détecté ; « URL :: @showbizz » = extracteur intégré Showbizz.net ; « URL :: regex .NET » = extraction personnalisée (groupe « title » requis, « url » et « date » optionnels). Vide = outil désactivé. La liste est relue sans redémarrage (cache 24 h). Le bouton « Tester les sources » valide chaque ligne sans enregistrer.",
            "cfg.newreleases.test": "Tester les sources",
            "cfg.newreleases.test.title": "Teste chaque ligne sans enregistrer : mode détecté, nombre d'items trouvés, aperçu des titres — ou l'erreur exacte (HTTP, délai dépassé, regex).",
            "cfg.newreleases.testing": "Test des sources…",
            "cfg.newreleases.test.line_ok": "✅ {0} ({1}) : {2} item(s){3}",
            "cfg.newreleases.test.line_zero": "⚠️ {0} ({1}) : 0 item — ligne inadaptée (regex périmée, page sans annonce…) : revalidez-la",
            "cfg.newreleases.test.line_err": "❌ {0} : {1}",
            "cfg.newreleases.test.fail": "❌ {0}",
            "cfg.rag.label": "Prompt système (directives de l'agent)",
            "cfg.rag.desc": "Instructions envoyées au LLM au début de chaque appel.",
            "cfg.debug.label": "Mode debug (journal complet de l'agent)",
            "cfg.debug.desc": "Écrit dans le journal Emby tout ce que voit l'LLM : prompts complets, réponses, résultats d'outils. À activer ponctuellement pour comprendre un comportement. Désactivé par défaut.",
            "cfg.sched.series.label": "Tâche planifiée Séries — planification et prompt",
            "cfg.sched.series.ph": "Daily 03:00 | Recommande des enregistrements de SÉRIES : …",
            "cfg.sched.series.desc": "Format : « planification | prompt de la tâche ». Planification : Daily HH:MM, Hourly, Weekly Jour HH:MM ou Interval heures. Le texte après le « | » est envoyé au LLM pour le run séries (premières S01E01 et nouvelles saisons). La tâche est aussi déclenchable manuellement dans « Tâches planifiées ».",
            "cfg.sched.movies.label": "Tâche planifiée Films — prompt du run films",
            "cfg.sched.movies.ph": "Recommande des enregistrements de FILMS : …",
            "cfg.sched.movies.desc": "Prompt envoyé au LLM pour le run films (films à venir absents de la bibliothèque). Pas de planification ici : elle vient du champ Séries. Le run films est fusionné avec les séries sur la page Recommandations. Vide = pas de run films (séries seulement).",
            "cfg.dropped.label": "Titres à exclure des recommandations",
            "cfg.dropped.ph": "Star Trek\nCastle\nUn titre par ligne",
            "cfg.dropped.desc": "Titres jamais recommandés : la tâche les retire de la liste envoyée au LLM. Un titre par ligne. Le bouton « Oublier » de la page Recommandations y ajoute aussi des titres.",

            // -- Sous-section « Ce soir » (recommandation personnalisée) ---
            "cfg.tonight.h": "À regarder ce soir (recommandation personnalisée par usager)",
            "cfg.tonight.desc": "Section « À regarder ce soir » de la page Recommandations : analyse l'historique de visionnage de l'usager et croise avec le guide TV du soir pour proposer quoi regarder maintenant. Calculée à l'ouverture de la page, avec un cache par usager.",
            "cfg.tonight.enabled": "Activer la section « À regarder ce soir »",
            "cfg.tonight.genretag.flag": "Ajouter l'étiquette « AI Tonight » aux recommandations « à regarder ce soir »",
            "cfg.tonight.genretag.desc": "Ajoute l'étiquette AI Tonight aux items recommandés (enregistrements non visionnés + bibliothèque) : ils se retrouvent en filtrant sur cette étiquette dans n'importe quel client Emby (filtre « Étiquettes »). Une tâche planifiée retire l'étiquette chaque nuit à 3 h ; les exécutions suivantes la réajoutent. L'étiquette modifie les métadonnées des items — un rafraîchissement Emby peut l'effacer (elle sera réajoutée).",
            "cfg.tonight.collection.flag": "Regrouper les recommandations « à regarder ce soir » dans une collection « AI Tonight »",
            "cfg.tonight.collection.desc": "Maintient une collection Emby « AI Tonight » regroupant les items recommandés (enregistrements non visionnés + bibliothèque) — l'usager la parcourt comme n'importe quelle collection. Non destructive : les items sont référencés, jamais copiés ni déplacés ; jouer un membre joue le vrai item. Indépendante de l'étiquette (les deux peuvent cohabiter). La tâche planifiée de 3 h vide la collection chaque nuit ; elle est re-remplie à l'exécution suivante.",
            "cfg.tonight.playlist.flag": "Remplir une playlist « AI Tonight » avec les recommandations « à regarder ce soir »",
            "cfg.tonight.playlist.desc": "Maintient une playlist Emby « AI Tonight » remplie avec les items recommandés (enregistrements non visionnés + bibliothèque) — lecture enchaînée depuis n'importe quel client Emby. Remise à zéro à chaque exécution : la playlist est vidée puis remplie avec les recommandations du jour. Publique (visible par le foyer), liée à l'usager indiqué ci-dessous. Indépendante de l'étiquette et de la collection (les trois peuvent cohabiter). La tâche planifiée de 3 h vide aussi la playlist.",
            "cfg.tonight.favorites.flag": "Mettre les recommandations « à regarder ce soir » en favori (favoris éphémères)",
            "cfg.tonight.favorites.desc": "À chaque nouvelle exécution, les recommandations sont mises en favori (dans « Ma liste ») pour l'usager indiqué ci-dessous. Favoris éphémères : la tâche planifiée de 3 h les retire — uniquement ceux posés par l'extension (traçés dans un fichier d'état), jamais les favoris préexistants. Limite connue : un item re-favori manuellement par l'usager est retiré quand même (indiscernable d'un favori posé par l'extension).",
            "cfg.tonight.user": "Usager des favoris et de la playlist « AI Tonight »",
            "cfg.tonight.user.desc": "Usager propriétaire des favoris éphémères et de la playlist. Vide = premier usager administrateur (sinon, premier usager), hors comptes ignorés ci-dessous. Doit être le nom exact d'un usager Emby existant.",
            "cfg.tonight.ignored": "Comptes ignorés (un nom d'usager Emby par ligne)",
            "cfg.tonight.ignored.desc": "Comptes exclus des mécanismes « foyer » : leur position de visionnage ne compte pas dans la résolution de la playlist publique « AI Tonight » ; le repli du champ ci-dessus (premier admin) les saute ; leur login ne déclenche plus de run « À regarder ce soir ». Jamais appliquée à l'intersection parentale ni aux surfaces par usager (recos, playlist privée — un compte ignoré garde ses surfaces en rafraîchissement manuel). Casse et accents tolérés (« rene » = « René »). Vide = aucun changement.",
            "cfg.tonight.window.start": "Début de fenêtre (HH:mm)",
            "cfg.tonight.window.end": "Fin de fenêtre (HH:mm)",
            "cfg.tonight.window.desc": "Fenêtre du guide TV interrogée (ex. 18:00 → 23:59). Début vide = maintenant ; fin vide = 23:59.",
            "cfg.tonight.prompt": "Prompt « ce soir »",
            "cfg.tonight.prompt.desc": "Gabarit du prompt envoyé au LLM. Le profil de goût de l'usager (historique récent) y est injecté à l'exécution.",
            "cfg.tonight.batch": "Plafond de programmes (par appel)",
            "cfg.tonight.batch.desc": "Nombre maximal de programmes de la soirée soumis au LLM (après pré-tri par pertinence). Défaut 10.",
            "cfg.tonight.cache": "Cache (heures)",
            "cfg.tonight.cache.desc": "Durée de validité du cache par usager. 0 = pas de cache (recalcul à chaque ouverture). Défaut 4.",
            "cfg.tonight.recDays": "Enregistrements (jours)",
            "cfg.tonight.recDays.desc": "Fenêtre de recherche des enregistrements récents non visionnés (candidats « à regarder ce soir »). Défaut 7.",
            "cfg.tonight.minRec": "Min recommandations",
            "cfg.tonight.minRec.desc": "Si le guide TV et les enregistrements donnent moins de recommandations, complète avec des titres non visionnés de la bibliothèque. Défaut 3.",
            "cfg.tonight.binge.flag": "Signaler les séries « prêtes à dévorer » (stock d'épisodes enregistrés non visionnés)",
            "cfg.tonight.binge.desc": "Détecte les séries en cours d'enregistrement dont l'usager accumule les épisodes non visionnés : quand le stock atteint le seuil, la recommandation propose de commencer (« il est temps de regarder X, N épisodes en attente »). Anti-spam : chaque série n'est signalée qu'une seule fois ; la suggestion ne revient qu'après que le compte non visionné repasse sous le seuil. Une série dormante (jamais commencée, gardée « pour un jour de pluie ») n'est jamais signalée : seules les séries encore actives le sont.",
            "cfg.tonight.binge.threshold": "Seuil d'épisodes en attente",
            "cfg.tonight.binge.threshold.desc": "Nombre d'épisodes non visionnés à partir duquel une série est « prête à dévorer ». Défaut 4.",
            "cfg.tonight.binge.days": "Fenêtre d'activité (jours)",
            "cfg.tonight.binge.days.desc": "Au moins un épisode de la série doit avoir été ajouté à la bibliothèque dans ces N derniers jours (signal « enregistrement actif » — distingue une accumulation en cours d'une série dormante). Défaut 14.",
            "cfg.feedback.h": "Boucle de rétroaction des recommandations",
            "cfg.feedback.desc": "Chaque recommandation (« À regarder ce soir », tâche d'enregistrement) et chaque rejet (« Oublier ») est journalisé. Une fois par semaine (dimanche 4 h), une tâche d'analyse rapproche ce journal de ce que l'usager a réellement regardé et fait produire au LLM une directive concise (ce qui a plu, ce qui a été ignoré, ce qui a manqué), réinjectée dans les prompts des exécutions suivantes — les recommandations s'améliorent d'elles-mêmes au fil des semaines. Sans directive, les prompts sont inchangés.",
            "cfg.feedback.flag": "Activer la boucle de rétroaction (analyse hebdomadaire)",
            "cfg.feedback.directive": "Directives actives (par usager, JSON)",
            "cfg.feedback.directive.desc": "Directives produites par la dernière analyse (une par usager). Éditable : l'admin peut corriger ou vider ce JSON « [{\"u\":\"userId\",\"n\":\"nom\",\"d\":\"date\",\"text\":\"…\"}] » — vider le champ (ou un « text ») retire la directive des prompts jusqu'à la prochaine analyse.",
            "cfg.memory.h": "Mémoire réflexive (expérimental)",
            "cfg.memory.desc": "Les données qui alimentent la réflexion du LLM sur lui-même : chaque recommandation émise avec sa raison, la liste des candidats soumis à chaque exécution, et chaque lecture terminée avec le pourcentage visionné. Trois fichiers JSON locaux (rétention 30 jours, volume plafonné), aucune donnée sortante.",
            "cfg.memory.decisions.flag": "Journaliser les décisions du LLM (recommandation, raison, candidats)",
            "cfg.memory.decisions.desc": "À chaque recommandation (« À regarder ce soir », enregistrements) et chaque rejet « Oublier », écrit « decisions.json » (raison, priorité, version de fiche) et « run_pool.json » (candidats écartés). Double aussi l'écriture dans le journal des recommandations, requis par l'analyse hebdomadaire.",
            "cfg.memory.playback.flag": "Télémétrie de lecture (% visionné de chaque lecture)",
            "cfg.memory.playback.desc": "À chaque fin de lecture, écrit « playback.json » (item, usager, durée réelle, fraction lue, source, chaîne, client, appareil). Signal central de la réflexion : rejet immédiat (moins de 5 %), abandon (5 à 50 %), contenu validé (plus de 80 %). Aucun effet sur la lecture elle-même.",
            "cfg.memory.card.flag": "Activer la fiche mémoire du LLM (révision hebdomadaire)",
            "cfg.memory.card.desc": "Tâche hebdomadaire (dimanche 4 h 30) : le LLM réécrit lui-même sa fiche mémoire (~250 mots) à partir des événements de la semaine — ce qu'il sait de l'usager, ses réussites, ses échecs et ses stratégies. Quatre versions sont conservées. La fiche est réinjectée dans les prompts (recommandations, enregistrement, chat) et remplace la directive de la boucle de rétroaction. En cas d'échec, la fiche précédente est conservée.",
            "cfg.memory.cardview.h": "Fiche mémoire actuelle",
            "cfg.memory.cardview.desc": "Lecture et édition admin : vous pouvez corriger la fiche à la main (le LLM la réécrira la semaine suivante à partir de votre version). Version 0 = pas encore rédigée (la première révision a besoin d'une semaine de données).",
            "cfg.memory.cardview.meta": "Version {0}, mise à jour {1} — {2} version(s) antérieure(s) conservée(s).",
            "cfg.memory.cardview.empty": "Pas encore de fiche (v0) — elle sera rédigée à la première révision hebdomadaire.",
            "cfg.memory.cardview.error": "Fiche indisponible : {0}",
            "cfg.memory.cardview.save": "Enregistrer la fiche",
            "cfg.memory.cardview.saving": "Enregistrement…",
            "cfg.memory.cardview.saved": "Fiche enregistrée.",
            "cfg.memory.cardview.savefail": "Échec : {0}",
            "cfg.autoprog.h": "Auto-programmation & popup au login",
            "cfg.autoprog.desc": "Les clients natifs Android / Android TV n'affichent pas les pages HTML de l'extension : les recommandations n'y sont donc pas visibles. L'auto-programmation crée les timers d'enregistrement Emby des recommandations — elles ressortent dans le guide natif (badge d'enregistrement) sur tous les clients. Le popup au login signale ce soir ce que l'usager peut regarder (bibliothèque, enregistrements).",
            "cfg.autoprog.flag": "Auto-programmer les recommandations (créer les timers d'enregistrement)",
            "cfg.autoprog.flag.desc": "Si cochée, après chaque exécution (tâche planifiée et login), les recommandations à enregistrer sont programmées : programmes à venir du guide TV, non déjà possédés, non déjà programmés, hors titres à exclure — SeriesTimer pour une série, timer unique pour un film. Aucune programmation tant que la case est décochée. L'usager peut annuler un timer indésirable directement dans Emby.",
            "cfg.diskgate.threshold": "Seuil disque enregistrements (Go)",
            "cfg.diskgate.threshold.desc": "Espace libre minimum sur le disque des enregistrements. En dessous, aucun nouveau timer n'est créé (les timers existants continuent). 0 = désactivé. Défaut 25.",
            "cfg.diskgate.tag.flag": "Suggérer à supprimer les enregistrements visionnés quand le seuil disque est franchi (étiquette « AI Delete »)",
            "cfg.diskgate.tag.desc": "Quand l'espace libre passe sous le seuil, les enregistrements déjà visionnés reçoivent l'étiquette « AI Delete », du plus ancien au plus récent, jusqu'à ce que leur suppression ramène le disque au-dessus du seuil (avec marge). Pure suggestion — l'extension ne supprime jamais rien : filtrez la bibliothèque des enregistrements par cette étiquette (filtre « Étiquettes »), sélectionnez puis supprimez. Chaque passe retire d'abord les étiquettes précédentes (si vous avez libéré de l'espace, tout disparaît). Les enregistrements non visionnés ne sont jamais étiquetés.",
            "cfg.aibadge.flag": "Badge « AI » sur l'image des suggestions à enregistrer (guide natif)",
            "cfg.ownedbadge.flag": "Badge jaune « déjà possédé » sur l'image des émissions de la bibliothèque (guide natif)",
            "cfg.ownedbadge.desc": "Pastille jaune (sans icône) sur l'image des programmes du guide dont la série ou le film figure déjà dans la bibliothèque — le rapprochement se fait par nom (articles FR/EN et casse ignorés). L'usager sait ainsi qu'il n'a pas intérêt à enregistrer. Même mécanisme non destructif que le badge « AI » : l'image stockée n'est jamais modifiée. NB : une nouvelle saison d'une série possédée porte aussi la pastille jaune (même nom de série).",
            "cfg.aibadge.desc": "Pastille verte avec icône étincelle (sans texte, multilingue) en haut à droite de l'image des programmes suggérés — visible dans le guide natif, sur tous les clients. Non destructif : le badge est ajouté quand l'image est servie, l'image stockée n'est jamais modifiée (l'enregistrement importé garde son image d'origine et le badge disparaît une fois l'émission enregistrée). Chaque exécution remplace la sélection badgée ; les diffusions passées s'auto-expirent.",
            "cfg.loginpopup.flag": "Popup au login (suggestions « à regarder ce soir »)",
            "cfg.loginpopup.seconds": "Durée d'affichage du popup (secondes)",
            "cfg.loginpopup.desc": "Indépendant de l'auto-programmation : le popup liste ce soir ce que l'usager peut regarder (enregistrements non visionnés, bibliothèque). Le message s'affiche sur le client qui se connecte ; si le client ne le supporte pas, une notification persistante (cloche) prend le relais. La cloche reste même si la session ferme avant la fin du calcul (~30 à 60 s).",
            "cfg.strmlib.h": "Bibliothèque .strm des recommandations",
            "cfg.strmlib.desc": "Alternative manuelle à l'auto-programmation : après chaque exécution, l'extension écrit une carte (.strm, .nfo, affiche) par recommandation à enregistrer — programmes à venir du guide TV, non possédés — dans une bibliothèque Emby dédiée. L'usager parcourt la bibliothèque ; lire une carte crée l'enregistrement puis affiche un clip de confirmation. Créez d'abord dans Emby une bibliothèque de type Films (ou Contenu mixte) pointant vers un dossier vide, puis renseignez son nom exact ici.",
            "cfg.strmlib.flag": "Activer la bibliothèque .strm des recommandations",
            "cfg.strmlib.name": "Nom de la bibliothèque Emby dédiée",
            "cfg.strmlib.name.desc": "Nom exact de la bibliothèque Emby où écrire les cartes (casse ignorée). Indépendant de l'auto-programmation : les deux peuvent cohabiter, les doublons de timers sont évités. Un jeton de sécurité est généré automatiquement au premier passage pour protéger l'activation.",
            "cfg.strmlib.perm": "Droits d'accès : lire une carte programme un enregistrement — réservez donc la bibliothèque, dans le tableau de bord Emby (Utilisateurs → autoriser un accès par dossier), aux comptes disposant du droit Enregistrer (EnableLiveTvManagement). L'extension refuse l'activation déclenchée par un compte sans ce droit (message dédié, aucun timer créé), et la page Recommandations masque ses sections d'enregistrement pour ces comptes.",

            // -- Identification des enregistrements orphelins (tâche planifiée 04 h) ---
            "cfg.orphan.h": "Identification des enregistrements orphelins",
            "cfg.orphan.desc": "Passe quotidienne (4 h) qui repère les enregistrements non identifiés (sans id IMDb/TMDB — souvent des titres québécois absents de TMDB/TVDB) et tente de les résoudre en trois étapes : nettoyage du titre et recherche multilingue (S1), id IMDb proposé par le LLM puis validé via TMDB (S2), recherche web SearXNG (S3). L'id, les métadonnées et l'affiche sont écrits en verrouillant le titre du guide (jamais écrasé). Les irrésolus sont marqués pour revue. Modifie des enregistrements.",
            "cfg.orphan.enabled": "Activer la tâche d'identification des orphelins",
            "cfg.orphan.dryrun": "Mode simulation (aucune écriture)",
            "cfg.orphan.dryrun.desc": "Si cochée, la tâche n'écrit rien : elle consigne dans le journal les orphelins trouvés, la résolution proposée et un bilan. Sert à valider la qualité des résolutions avant d'activer l'application automatique. À garder cochée pour les premiers passages.",
            "cfg.orphan.searxng": "Étape S3 : recherche web (SearXNG) pour les titres introuvables",
            "cfg.orphan.searxng.desc": "Si cochée, après S1 et S2 la tâche interroge SearXNG (URL configurée plus haut), extrait les ids IMDb des résultats et les valide via TMDB et le juge de synopsis. Résout les titres paraphrasés qu'aucun catalogue ne connaît (ex. « L'histoire de Jean Seberg » → le film « Seberg », 2019). Sans effet si ni SearXNG ni la clé Ollama ne sont configurés.",
            "cfg.orphan.retry": "Retraiter les items marqués pour revue",
            "cfg.orphan.retry.desc": "Si cochée, les orphelins marqués « needs-review » sont retraités au lieu d'être sautés — l'étape S3 peut ainsi être repassée une fois SearXNG configuré. En cas de résolution, l'étiquette devient « identified ». Les déjà-identifiés restent sautés.",
            "cfg.orphan.firstpass": "Étape S0 : premier tri natif Emby (moteur « Identifier »)",
            "cfg.orphan.firstpass.desc": "Si cochée, les orphelins passent d'abord par la recherche native d'Emby (le même moteur que le dialogue « Identifier », avec les clés configurées côté serveur) avant l'étape S1. Chaque candidat est vérifié sur le titre, l'année et le synopsis — un mauvais match est rejeté et la chaîne S1→S2→S3 se poursuit. Sans effet si la clé TMDB est absente.",
            "cfg.orphan.validate": "Valider les métadonnées à la fin de chaque enregistrement",
            "cfg.orphan.validate.desc": "Si cochée, à la fin de chaque enregistrement l'extension fige les données du guide (titre, synopsis, année) puis audite l'identification posée par les fournisseurs automatiques d'Emby. Si le synopsis concorde : champs verrouillés et étiquette « identified ». Sinon : ids retirés, retour aux données du guide et reprise immédiate de la chaîne S1→S2→S3. Les enregistrements non identifiés passent immédiatement au traitement. Sans cette case, la passe de 4 h reste le seul filet. Activeable sans redémarrage ; respecte le mode simulation.",
            "cfg.orphan.audit": "Auditer les items étiquetés qui ont reçu un id entre-temps",
            "cfg.orphan.audit.desc": "Si cochée, les items marqués « introuvable » ou « à revoir » qui ont reçu un id Emby après coup sont ré-audités : la fiche TMDB de l'id posé est confrontée au titre de l'item. Si tout concorde : champs verrouillés et étiquette « identified ». Sinon (identification Emby fausse, ex. un homonyme) : ids retirés et reprise immédiate de la chaîne S1→S2→S3. Respecte le mode simulation. Modifie des métadonnées.",

            // -- Audit santé (endpoint à la demande, agent system_audit) ---
            "cfg.audit.h": "Audit santé du serveur",
            "cfg.audit.desc": "Un agent LLM inspecte le serveur (sessions, tâches planifiées, transcodage, disques, journaux) et rédige un rapport de santé : constats classés par gravité, actions recommandées. Le dernier rapport est sauvegardé et réaffiché à l'ouverture de la page. Réservé aux administrateurs.",
            "cfg.audit.enabled": "Activer l'audit (/Plugins/LLMAI/Audit)",
            "cfg.audit.remediation": "Autoriser l'agent à agir (arrêter une lecture, déclencher une tâche, envoyer un message)",
            "cfg.audit.remediation.desc": "Décochée par défaut : l'agent ne peut que recommander des actions dans son rapport. Cochée, il peut les exécuter pendant l'audit. Dans tous les cas, il n'agit jamais sans demande explicite.",
            "cfg.audit.prompt": "Prompt de l'audit",
            "cfg.audit.prompt.desc": "Instructions envoyées au modèle pour l'audit. Vous pouvez les adapter librement — le déroulement et les garde-fous sont injectés par l'extension — mais conservez la phrase qui interdit les actions de remédiation.",
            "cfg.audit.mode": "Mode d'exécution de l'audit",
            "cfg.audit.mode.single": "Agent adaptatif (modèle puissant / cloud)",
            "cfg.audit.mode.deterministic": "Déterministe, guidé par l'extension (modèle local)",
            "cfg.audit.mode.desc": "Agent adaptatif : l'agent décide lui-même ce qu'il inspecte et peut creuser un constat. À réserver à un modèle puissant (cloud). Déterministe : l'extension collecte toutes les données, puis le modèle rédige le rapport en courtes étapes — adapté à un modèle local ou modeste. Seul le mode adaptatif permet d'exécuter les actions de remédiation.",
            "cfg.audit.focus": "Focus optionnel de l'audit",
            "cfg.audit.focus.desc": "Restreint l'audit à un domaine ou à une demande précise. Laisser vide pour un audit complet.",
            "cfg.audit.focus.ph": "ex. transcoding, disk, ou « arrête la session XYZ »",
            "cfg.audit.run": "Lancer l'audit",
            "cfg.audit.running": "Audit en cours en arrière-plan…",
            "cfg.audit.running.desc": "L'audit démarre en arrière-plan. Vous pouvez quitter cette page ; le rapport sera sauvegardé automatiquement à la fin, quoi qu'il arrive. Cliquer à nouveau pendant un audit ne le relance pas : cela permet simplement de suivre son avancement.",
            "cfg.audit.failed": "Échec de l'audit — le dernier rapport sauvegardé reste affiché.",
            "cfg.audit.done": "Audit terminé",
            "cfg.audit.disabled": "Audit désactivé dans la configuration.",
            "cfg.audit.last": "Dernier rapport sauvegardé — {0} (mode {1})",
            "cfg.i18n.h": "Langues d'interface",
            "cfg.i18n.desc": "Ajoute ou entretient une langue d'interface de l'extension (pages de configuration, messages serveur, app compagnon) avec le LLM configuré ici. La génération passe par des lots validés (placeholders {n} et balises HTML préservés, valeurs identiques à l'anglais rejetées), puis écrit le fichier LLM_AI_i18n.json dans le dossier de configuration de l'extension. L'atelier manuel décrit dans le README reste possible, et le fichier peut toujours être retouché à la main.",
            "cfg.i18n.lang": "Code de langue",
            "cfg.i18n.lang.desc": "2 ou 3 lettres minuscules (ex. es, de, it, pt). La terminologie officielle Emby de cette langue (les chaînes du serveur) sert de glossaire d'ancrage à chaque lot ; sans fichier local, le glossaire est vide (avertissement au rapport).",
            "cfg.i18n.lang.invalid": "Code de langue invalide — 2-3 lettres attendues (ex. es).",
            "cfg.i18n.loading": "Vérification de la couverture…",
            "cfg.i18n.coverage": "Couverture de « {0} » — web {1}/{2}, serveur {3}/{4}, app {5}/{6} présentes ; {7} manquantes.",
            "cfg.i18n.coverage.err": "Lecture de la couverture impossible (extension trop ancienne ou serveur injoignable).",
            "cfg.i18n.mode": "Mode de génération",
            "cfg.i18n.mode.full": "Générer / refaire la langue complète",
            "cfg.i18n.mode.missing": "Compléter — clés manquantes et sautées seulement",
            "cfg.i18n.mode.skipped": "Re-traduire les clés sautées seulement",
            "cfg.i18n.mode.desc": "« Générer » refait toutes les clés de zéro (l'existant est écrasé, une sauvegarde .bak est conservée). « Compléter » traite seulement les clés absentes ; vos retouches manuelles restent intouchables. « Re-traduire les sautées » reprend seulement les clés refusées par la validation (repli natif en attendant).",
            "cfg.i18n.run": "Lancer la génération",
            "cfg.i18n.running": "Génération en cours… (tâche de fond)",
            "cfg.i18n.running.desc": "La génération s'exécute en tâche de fond : vous pouvez quitter ou recharger la page, elle continue et le rapport est écrit à la fin. Un clic supplémentaire ne relance rien, il reprend la progression.",
            "cfg.i18n.done": "Génération terminée.",
            "cfg.i18n.failed": "Génération échouée.",
            "cfg.i18n.endpoint.missing": "La génération n'est pas disponible sur cette version de l'extension (mise à jour requise).",
            "cfg.i18n.last": "Dernier rapport de génération — {0}",
            "cfg.crosskind.h": "File de régularisation « cross-kind »",
            "cfg.crosskind.desc": "Œuvres enregistrées par Emby sous le mauvais type. Deux sources alimentent la file : les items étiquetés llmai-cross-kind (fiche film sur un item série, ou l'inverse) et les suspects — items étiquetés llmai-not-found dont le dossier vit dans le répertoire des enregistrements (œuvre absente de TMDB : un documentaire importé en série, par exemple). Pour chacune, deux traitements : copier le fichier vidéo vers la bibliothèque de son type, sous le nom « Titre (Année).ts » (copie vérifiée, jamais destructive — l'original reste en place ; supprimez-le ensuite pour qu'Emby retire l'ancien item) ; ou convertir sur place le dossier d'enregistrement : renommage du dossier, de la vidéo, du .nfo et de l'affiche en « Titre (Année) », réécriture du .nfo en racine &lt;movie&gt;, retrait de tvshow.nfo — Emby ré-importe alors l'œuvre sous le bon type, et tout échec restaure l'état initial. « Ignorer » retire une entrée de la file (étiquette llmai-cross-kind-ignored), réaffichable à la demande.",
            "cfg.crosskind.refresh": "Recharger la file",
            "cfg.crosskind.queue.loading": "Lecture de la file… (fiches TMDB relues, quelques secondes)",
            "cfg.crosskind.queue.empty": "Aucune œuvre cross-kind ou suspecte en attente — rien à régulariser.",
            "cfg.crosskind.queue.err": "Lecture de la file échouée (serveur indisponible ?).",
            "cfg.crosskind.status.pending": "à régulariser",
            "cfg.crosskind.status.done": "copie faite — original en place",
            "cfg.crosskind.status.suspect": "suspect — fiche introuvable, dossier d'enregistrement",
            "cfg.crosskind.status.ignored": "ignoré",
            "cfg.crosskind.evidence.base": "Suspect : étiquette not-found + dossier dans le répertoire des enregistrements.",
            "cfg.crosskind.evidence.fiche": "Fiche {0} du type opposé trouvée par titre exact : « {1} » ({2}).",
            "cfg.crosskind.evidence.nofiche": "Aucune fiche TMDB du type opposé (recherche par titre exact).",
            "cfg.crosskind.showignored": "Afficher les entrées ignorées",
            "cfg.crosskind.ignore": "Ignorer",
            "cfg.crosskind.unignore": "Ne plus ignorer",
            "cfg.crosskind.kind.movie": "film",
            "cfg.crosskind.kind.series": "série",
            "cfg.crosskind.cross": "Fiche {0} sur un item {1} —",
            "cfg.crosskind.target": "Cible suggérée : {0} / {1}",
            "cfg.crosskind.regularize": "Régulariser…",
            "cfg.crosskind.dialog.title": "Régulariser « {0} »",
            "cfg.crosskind.dialog.sources": "Fichier(s) source :",
            "cfg.crosskind.dialog.lib": "Bibliothèque cible",
            "cfg.crosskind.dialog.lib.desc": "Bibliothèques de films/séries ou à contenu mixte, filtrées selon le type visé par l'entrée (la bibliothèque de cartes .strm de l'extension n'est jamais proposée ; celle qui contient les enregistrements l'est si elle supporte le type visé — elle reste soumise à la rétention des enregistrements). Choisir une bibliothèque préremplit le dossier de destination avec la cible suggérée « Titre (Année) ».",
            "cfg.crosskind.dialog.folder": "Dossier de destination",
            "cfg.crosskind.dialog.folder.desc": "Dossier cible complet ; le fichier y est copié sous le nom indiqué ci-dessous. Modifiable : vous pouvez pointer directement un dossier « Season 01 » dans le cas d'une fiche série.",
            "cfg.crosskind.dialog.file": "Nom du fichier copié",
            "cfg.crosskind.dialog.file.desc": "Suggestion « Titre (Année).ext » dérivée de la fiche. Pour une fiche série (un épisode d'une série), écrivez le nom complet, ex. « Série (2019) - S01E05.ts ».",
            "cfg.crosskind.dialog.go": "Copier (l'original reste en place)",
            "cfg.crosskind.dialog.cancel": "Annuler",
            "cfg.crosskind.dialog.needfolder": "Renseignez le dossier de destination.",
            "cfg.crosskind.dialog.err": "Échec de la régularisation ({0}).",
            "cfg.crosskind.copying": "Copie en cours…",
            "cfg.crosskind.done": "Terminé : {0} copié(s), {1} déjà en place, {2} échec(s).",
            "cfg.crosskind.finalize": "La copie est faite. Supprimez maintenant les fichiers originaux du répertoire d'enregistrement — Emby retirera l'ancien item au scan suivant.",
            "cfg.crosskind.failedNote": "Échec : rien n'a été copié pour ces fichiers — ne supprimez pas les originaux. Corrigez la cause (permissions du dossier cible, chemin) puis relancez « Copier » : la copie reprend là où elle s'était arrêtée.",
            "cfg.crosskind.convert.title": "Convertir sur place (dossier d'enregistrement)",
            "cfg.crosskind.convert.hint": "Renomme le dossier en « Titre (Année) », la vidéo et le .nfo à l'identique, poster.jpg en « Titre (Année)-poster.jpg », réécrit le .nfo en racine &lt;movie&gt; (sinon l'œuvre serait relue comme un épisode), puis Emby ré-importe l'œuvre sous le bon type au scan suivant. L'image du guide est aussi écrite en poster.jpg dans le dossier converti — l'affiche survit au ré-import. La vidéo n'est jamais supprimée ; tout échec restaure l'état initial.",
            "cfg.crosskind.convert.name": "Nom cible « Titre (Année) »",
            "cfg.crosskind.convert.name.desc": "Suggestion dérivée de la fiche, sinon du nom de l'item et de son année de diffusion. Plusieurs enregistrements : suffixe « (2) », « (3) »…",
            "cfg.crosskind.convert.tvshow": "Supprimer tvshow.nfo (requis pour le basculement de type)",
            "cfg.crosskind.convert.go": "Convertir sur place",
            "cfg.crosskind.convert.busy": "Conversion en cours…",
            "cfg.crosskind.convert.needname": "Renseignez le nom cible « Titre (Année) ».",
            "cfg.crosskind.convert.done": "Terminé : {0} fichier(s) renommé(s), {1} supprimé(s), {2} échec(s).",
            "cfg.crosskind.convert.finalize": "Le dossier est converti — Emby le ré-importe sous le bon type au scan suivant, déclenché automatiquement.",
            "cfg.crosskind.convert.failedNote": "Échec : l'état initial a été restauré — corrigez la cause puis relancez.",
            "cfg.crosskind.convert.err": "Échec de la conversion ({0}).",
            "cfg.chat.h": "Chat avec l'assistant IA",
            "cfg.update.available": "Nouvelle version {0} disponible sur GitHub (installée : {1}).",
            "cfg.update.link": "Voir la release",
            "cfg.update.hint": "Après l'installation, un simple rechargement de la page (F5) charge le nouveau JS.",
            "cfg.gtx.h": "Traduction des genres (IA)",
            "cfg.gtx.desc": "L'extension repère les genres du guide TV qui ne sont pas encore traduits et suggère des équivalents basés sur votre liste de genres. Si un genre est totalement nouveau, le LLM peut vous proposer de le créer et de l'ajouter à votre liste en un clic. Vous gardez le contrôle total en validant chaque proposition. Ces changements sont appliqués immédiatement aux recommandations, mais un redémarrage du serveur est requis pour mettre à jour l'ensemble du système GenreCleaner.",
            "cfg.gtx.analyze": "Analyser les genres non traduits",
            "cfg.gtx.analyzing": "Analyse en cours… (collecte des genres du guide TV et traduction par le LLM, 10 à 60 s environ)",
            "cfg.gtx.counts": "{0} genre(s) non traduit(s) côté films, {1} côté séries. Décochez ce que vous ne voulez pas appliquer.",
            "cfg.gtx.suggest.h": "Genres sans équivalent — nouveaux genres suggérés (ajoutés à AllowedGenres à l'application)",
            "cfg.gtx.newgenre": "nouveau genre",
            "cfg.gtx.orphans": "{0} genre(s) sans équivalent possible (aucune action) : {1}",
            "cfg.gtx.movies": "films",
            "cfg.gtx.series": "séries",
            "cfg.gtx.apply": "Appliquer les traductions sélectionnées",
            "cfg.gtx.applying": "Écriture dans GenreCleaner.xml…",
            "cfg.gtx.noneSelected": "Aucune proposition cochée.",
            "cfg.gtx.applied.restart": "{0} traduction(s) ajoutée(s) à GenreCleaner.xml. Les recommandations les utilisent dès maintenant. Redémarrez le serveur pour que l'extension GenreCleaner les adopte (la bannière « redémarrage requis » s'affiche).",
            "cfg.gtx.applied.norestart": "{0} traduction(s) déjà présente(s) dans GenreCleaner.xml — rien de nouveau à écrire. Les recommandations les utilisent.",
            "cfg.gtx.error": "Échec : {0}",
            "chat.title": "💬 Chat LLM AI",
            "chat.resume.banner": "Conversation du {0} — {1} échange(s) enregistrés",
            "chat.resume.button": "Reprendre",
            "cfg.chat.desc": "Conversation multi-tours avec l'agent LLM, sur sa propre page (menu Serveur → Chat LLM AI). Il dispose de tous les outils de l'extension (guide TV, bibliothèque, TMDB/TVDB, recherche web, nouveautés web, audit système) et suit les priorités de serveurs LLM configurées ci-dessus. L'historique est maintenu par la page ; la directive et la documentation des outils sont envoyées au LLM une seule fois, au début de la conversation. Réservé aux administrateurs.",
            "cfg.chat.enabled": "Activer le chat interactif",
            "cfg.chat.memory.flag": "Mémoire de conversation (reprendre le dernier chat)",
            "cfg.chat.memory.desc": "Chaque conversation est conservée (« chat_memory.json »). Au retour de l'usager, un appel LLM condense la session précédente en note de continuité (goûts exprimés, faits, fil ouvert) ; les signaux de goût partent dans « decisions.json » et la fiche mémoire hebdomadaire les intègre. La page chat affiche un bouton « Reprendre » ; le résumé et les derniers échanges sont injectés dans le prompt. En cas d'échec LLM, la conversation suivante continue sans mémoire.",
            "cfg.chat.actions.desc": "Le chat peut agir sur les mêmes surfaces que l'extension — cartes .strm « AI Suggestions », timers d'enregistrement, étiquette « AI Tonight », collection, playlist — avec les mêmes garde-fous (déjà possédé, déjà visionné, titres à exclure, doublons évités). Le budget plafonne les actions réussies, par question et par conversation ; 0 = chat en lecture seule. L'agent propose puis attend votre confirmation avant d'exécuter ; une action refusée par un garde-fou ne consomme pas le budget.",
            "cfg.chat.actions.budget": "Budget d'actions (par question)",
            "cfg.chat.actions.budget.desc": "Nombre maximal d'actions réussies par question (toutes surfaces confondues : cartes, timers, étiquettes, collection, playlist, lancement de l'analyse « ce soir »). 0 = lecture seule. Défaut 10.",
            "cfg.chat.actions.cap": "Plafond d'actions (par conversation)",
            "cfg.chat.actions.cap.desc": "Plafond cumulé par conversation (compteur en mémoire serveur, remis au redémarrage). Défaut 30.",
            "cfg.chat.prompts": "Autoriser le chat à proposer des modifications des prompts",
            "cfg.extchat.desc": "Pour une app compagnon (script Python autonome fourni avec l'extension, dossier chat-external — aucun serveur web à installer) : « POST /Plugins/LLMAI/ChatExternal » discute avec l'agent au nom d'un usager Emby, « POST /Plugins/LLMAI/Show » projette la fiche d'un item sur le client Emby actif de cet usager. Lecture seule : aucun outil d'action, aucun audit ; tout contenu est filtré par le contrôle parental de l'usager et la navigation ne cible que sa session. Côté sécurité : appels uniquement depuis le serveur lui-même (l'app appelle Emby directement, sans X-Forwarded-For), secret dédié, liste d'usagers — les administrateurs sont toujours refusés sur ce chemin.",
            "cfg.extchat.enabled": "Activer le chat externe (app compagnon)",
            "cfg.extchat.secret": "Secret partagé (copié dans la config de l'app compagnon)",
            "cfg.extchat.secret.desc": "Secret dédié (distinct du secret .strm). Générez-le ici puis copiez-le dans la config de l'app compagnon ; changez-le des deux côtés pour révoquer l'accès.",
            "cfg.extchat.users": "Usagers autorisés (un nom d'usager Emby par ligne)",
            "cfg.extchat.users.desc": "Mêmes noms que dans l'app compagnon. Un usager non listé est rejeté ; un administrateur est toujours rejeté (le chat admin reste sur /Plugins/LLMAI/Chat).",
            "cfg.extchat.perMinute": "Anti-spam (messages/minute)",
            "cfg.extchat.perMinute.desc": "Nombre maximal de messages par usager et par minute (fenêtre glissante) — chaque message déclenche un appel LLM. 0 = illimité. Défaut 5.",
            "cfg.extchat.perDay": "Quota (messages/24 h)",
            "cfg.extchat.perDay.desc": "Nombre maximal de messages par usager sur 24 h (fenêtre glissante). 0 = illimité. Défaut 150 (compteur en mémoire : un redémarrage d'Emby le remet à zéro).",
            "cfg.extchat.rec.enabled": "Enregistrements à la voix (confirmation par code)",
            "cfg.extchat.rec.desc": "Une demande d'enregistrement ne s'exécute jamais du premier coup : elle est mise en réserve et un code à 4 chiffres s'affiche à l'écran de l'app (le LLM ne le voit jamais) ; l'usager donne ensuite ce code dans son message, et seul le code exact crée le timer. 3 codes erronés = outil verrouillé 15 min pour l'usager ; le quota journalier ne compte que les enregistrements réellement créés ; le contrôle parental s'applique toujours. Exige à la fois : la case cochée ici, l'usager listé ci-dessous et le droit Emby d'enregistrer la TV en direct.",
            "cfg.extchat.rec.users": "Usagers autorisés à enregistrer (un nom d'usager Emby par ligne)",
            "cfg.extchat.rec.users.desc": "Vide = personne autorisée. En plus du droit Emby (Gérer l'enregistrement TV) et de la case cochée ci-dessus.",
            "cfg.extchat.rec.perDay": "Quota (enregistrements créés/24 h)",
            "cfg.extchat.rec.perDay.desc": "Nombre maximal d'enregistrements créés par usager sur 24 h (fenêtre glissante) — comptés seulement quand le code est confirmé, jamais sur une réservation expirée ou refusée. 0 = illimité. Défaut 3.",
            "cfg.chat.prompts.desc": "Le chat peut lire les cinq prompts de la configuration et proposer leur réécriture. L'écriture se fait en deux temps : la proposition attend le clic « Approuver » de l'admin sur la page chat (expiration 10 min, une par conversation) — le LLM n'a aucun chemin d'écriture direct. Les modes d'édition du menu déroulant de la page chat restent disponibles sans cette case.",
            "chat.context": "Mode de conversation",
            "chat.context.none": "Aucun (assistant général)",
            // Libellés des modes d'édition (menu déroulant du chat) : le
            // registre serveur (ChatContexts.All) sert le libellé FR de
            // référence ; la page traduit par clé d'id — la langue suit
            // l'interface Emby du client, comme le reste de la page
            // (repli : libellé serveur).
            "chat.ctx.edit_rag": "Éditer — Prompt système",
            "chat.ctx.edit_schedule_series": "Éditer — Tâche séries (enregistrements)",
            "chat.ctx.edit_schedule_movies": "Éditer — Tâche films (enregistrements)",
            "chat.ctx.edit_tonight": "Éditer — Run « À regarder ce soir »",
            "chat.ctx.edit_audit": "Éditer — Prompt d'audit santé",
            // Mode atelier (v1.17.0.2) : le seul mode sans prompt éditable —
            // absent des débuts, il retombait sur le libellé serveur FR dans
            // toute interface (corrigé v1.17.1.2, renommé « Éditer — Atelier
            // de langues »).
            "chat.ctx.i18n_edit": "Éditer — Atelier de langues",
            // Annonce automatique au changement de mode (tour « Vous » du
            // fil — visible par l'usager ET relue par le LLM) + erreurs
            // d'envoi (timeout / connexion) : suivent la langue de la page.
            "chat.mode.selected": "[Admin] J'ai sélectionné le mode « {0} ». Avant toute chose : indique clairement sur quel prompt tu travailles (champ concerné) et affiche le texte actuel que tu vas modifier.",
            "chat.err.timeout": "Requête trop longue — le LLM n'a pas répondu dans le délai imparti. Réessayez.",
            "chat.err.network": "Serveur injoignable (connexion interrompue).",
            // Notes [Admin] poussées dans le fil après un clic
            // Approuver/Refuser (v1.15.0.2 — elles suivent la langue de la
            // page comme l'annonce de sélection ; le LLM les rejoue au tour
            // suivant et peut les citer, d'où la cohérence de voix).
            "chat.note.prompt.approved": "[Admin] J'ai approuvé la modification du prompt « {0} » — elle a été enregistrée dans la configuration.",
            "chat.note.prompt.refused": "[Admin] J'ai refusé la modification du prompt « {0} » — rien n'a été écrit.",
            "chat.note.testhint": " Façon de tester : {0}",
            "chat.note.action.approved": "[Admin] J'ai approuvé l'action « {0} » — {1}",
            "chat.note.action.executed": "elle a été exécutée",
            "chat.note.action.executed.detail": "elle a été exécutée : {0}",
            "chat.note.action.failed": "mais l'exécution n'a pas abouti : {0}",
            "chat.note.action.refused": "[Admin] J'ai refusé l'action « {0} » — rien n'a été exécuté.",
            "chat.empty": "(vide)",
            "chat.pending.title": "Modification de prompt en attente d'approbation",
            "chat.pending.field": "Champ",
            "chat.pending.before": "Avant",
            "chat.pending.after": "Après",
            "chat.pending.approve": "Approuver",
            "chat.pending.refuse": "Refuser",
            "chat.pending.approved": "Approuvé — le prompt a été enregistré dans la configuration. Rechargez la page de configuration pour voir la nouvelle valeur.",
            "chat.pending.refused": "Refusé — rien n'a été écrit.",
            "chat.pending.error": "Échec de l'approbation",
            "chat.pending.stale": "Proposition supplantée ou expirée — non actionnable.",
            "chat.i18n.pending.title": "Proposition i18n en attente d'approbation",
            "chat.i18n.pending.key": "Clé",
            "chat.i18n.pending.lang": "Langue",
            "chat.i18n.pending.before": "Valeur actuelle",
            "chat.i18n.pending.after": "Nouvelle valeur",
            "chat.i18n.pending.absent": "(absente de l'overlay — première écriture)",
            "chat.i18n.pending.approve": "Approuver",
            "chat.i18n.pending.refuse": "Refuser",
            "chat.i18n.pending.approved": "Écrite — le serveur sert désormais : « {0} ».",
            "chat.i18n.pending.refused": "Refusé — rien n'a été écrit.",
            "chat.note.i18n.approved": "Proposition i18n approuvée par l'admin via la carte : clé {0} ({1}/{2}) écrite ; le serveur sert désormais « {3} ».",
            "chat.note.i18n.refused": "Proposition i18n refusée par l'admin via la carte : clé {0} ({1}/{2}) — rien n'a été écrit.",
            "chat.action.pending.title": "Action Emby en attente d'approbation (le LLM ne peut pas exécuter)",
            "chat.action.approve": "Approuver",
            "chat.action.refuse": "Refuser",
            "chat.action.approved": "Approuvé — action exécutée par le serveur.",
            "chat.action.refused": "Refusé — rien n'a été exécuté.",
            "chat.action.error": "Échec de l'approbation",
            "chat.code.copy": "Copier le code",
            "chat.code.save": "Demander l'enregistrement — envoie la demande au LLM ; la carte d'approbation reste l'étape d'autorisation",
            "chat.code.save_request": "Enregistre le texte suivant comme nouveau texte complet du prompt du mode actif (plugin_prompts action=\"set\"), sans le modifier :\n\n{0}",
            "chat.code.save_request_turn": "Enregistre la révision que tu viens de proposer dans ta dernière réponse comme nouveau texte complet du prompt du mode actif (plugin_prompts action=\"set\"), sans la modifier et sans y inclure de commentaire ni de question.",
            "chat.code.reemit_request": "Réémets la révision de ta dernière réponse dans un bloc de code ```text, sans la modifier — le bloc affichera le bouton de sauvegarde.",
            "chat.code.save_turn": "La réponse n'est pas dans un bloc de code — ce bouton fait réémettre la révision en bloc ```text (instruction ponctuelle, ciblage exact) ; la sauvegarde se fait ensuite via le bouton du bloc, la carte d'approbation reste l'autorisation",
            "chat.code.save_turn_short": "Préparer la sauvegarde (bloc de code)",
            "reco.viaChat": "Générée via chat — directives : {0}",
            "reco.viaChat.short": "Générée via chat",
            "cfg.chat.input": "Votre message",
            "cfg.chat.placeholder": "ex. que peux-tu me dire sur ce qui passe ce soir ?",
            "cfg.chat.send": "Envoyer",
            "cfg.chat.clear": "Effacer la conversation",
            "cfg.chat.running": "L'assistant réfléchit… (il peut interroger vos outils, quelques dizaines de secondes)",
            "cfg.chat.disabled": "Chat désactivé en configuration.",
            "cfg.chat.you": "Vous",
            "cfg.chat.assistant": "Assistant IA",
            "cfg.chat.hint": "Posez une question à l'assistant — il interrogera vos données Emby (guide TV, bibliothèque, santé du serveur) pour répondre.",
            "cfg.save": "Enregistrer",

            // -- config.js : chaînes dynamiques ----------------------------
            "cfg.wl.empty": "(aucun élément disponible)",
            "cfg.backend.provider.local": "Ollama local",
            "cfg.backend.provider.cloud": "Ollama cloud",
            "cfg.backend.provider.gemini": "Google Gemini",
            "cfg.backend.provider.label": "Fournisseur",
            "cfg.backend.num": "LLM #{0}",
            "cfg.backend.remove": "Supprimer",
            "cfg.backend.url.label": "URL de base",
            "cfg.backend.model.label": "Modèle",
            "cfg.backend.prio.label": "Priorité",
            "cfg.backend.enabled": "Activé",
            "cfg.backend.test": "Tester",
            "cfg.backend.test.title": "Question-test rapide envoyée à ce serveur (30 s au plus). Utilisable avant enregistrement ; les clés API sont relues depuis la configuration enregistrée.",
            "cfg.backend.testing": "Test…",
            "cfg.backend.test.ok": "✅ OK ({0} ms) — « {1} »",
            "cfg.backend.test.fail": "❌ {0}",
            "cfg.alert.saved": "Configuration enregistrée.",
            "cfg.reset": "Réinitialiser",
            "cfg.reset.title": "Restaure la directive d'origine (dans la langue configurée). Non enregistré tant que vous n'avez pas cliqué « Enregistrer ».",
            "cfg.reset.error": "Impossible de récupérer les directives par défaut : {0}",
            "cfg.sections.collapse": "Replier tout",
            "cfg.sections.expand": "Déplier tout",
            "cfg.alert.saveError": "Erreur lors de l'enregistrement : {0}",

            // -- Page Recommandations --------------------------------------
            "rec.title": "🤖 Recommandations LLM AI",
            "rec.toggleRaw": "Afficher / masquer le JSON brut",
            "rec.prio.high": "⚡ Haute",
            "rec.prio.medium": "🔶 Moyenne",
            "rec.prio.low": "🔵 Basse",
            "rec.btn.program": "✅ Programmer",
            "rec.btn.drop": "🗑️ Oublier",
            "rec.btn.noId": "Aucun Id programme rattaché (titre non matché)",
            "rec.btn.scheduled": "✓ Programmée",
            "rec.btn.already": "Déjà programmée",
            "rec.btn.forgotten": "✓ Oublié",
            "rec.section.series": "Séries",
            "rec.section.movies": "Films",
            "rec.section.empty": "Aucune recommandation dans cette section.",
            "rec.count": "{0} recommandation(s)",
            "rec.lastRun": "Dernière exécution : {0}",
            "rec.noRun": "Aucune exécution enregistrée pour l'instant.",
            "rec.empty": "Aucune recommandation pour l&#39;instant. Lancez la tâche « LLM AI Task » dans Tâches planifiées.",
            "rec.alert.refused": "Programmation refusée par Emby : {0}",
            "rec.alert.refusedShort": "Programmation refusée par Emby.",
            "rec.alert.dropSave": "Impossible d'enregistrer la drop list : {0}",
            "rec.alert.cfgRead": "Impossible de lire la config : {0}",
            "rec.alert.cfgLoad": "Impossible de charger la configuration de l'extension.",

            // -- Section « À regarder ce soir » (endpoint plugin) ----------
            "rec.section.tonight": "À regarder ce soir",
            "rec.tonight.loading": "Analyse de l'EPG de ce soir selon votre historique… (le LLM peut prendre quelques dizaines de secondes)",
            "rec.tonight.refresh": "↻ Rafraîchir",
            "rec.tonight.error": "Impossible de produire la sélection : {0}",
            "rec.tonight.empty": "Rien d'intéressant ce soir dans l'EPG (selon vos filtres).",
            "rec.tonight.fromCache": "depuis cache",
            "rec.tonight.watchLive": "Regarder en direct",
            "rec.tonight.watch": "Regarder",
            "rec.tonight.watchLib": "Regarder (bibli.)",
            // Préfixe de la raison, section « À regarder ce soir » SEULEMENT :
            // les recos d'enregistrement (.strm, sections séries/films) portent
            // l'emoji 🤖 seul.
            "rec.tonight.why": "🤖 Pourquoi ce soir : ",
            "rec.type.upcoming": "À venir",
            "rec.type.recording": "Disponible · Enregistrement",
            "rec.type.library": "Disponible · Bibliothèque",
            "rec.type.aired": "Diffusé",
            "rec.type.watched": "Déjà visionné"
        },

        en: {
            "cfg.title": "LLM AI — Configuration",
            "cfg.docs.link": "📖 Full documentation (GitHub — new tab)",
            "cfg.backends.h": "LLM servers (priority fallback)",
            "cfg.backends.desc": "Add one or more LLM servers — Ollama (local or cloud) or Google Gemini. Each LLM has a priority (1 is tried first) and can be enabled or disabled. If a server does not respond, the task automatically falls back to the next enabled LLM.",
            "cfg.backends.add": "+ Add an LLM",
            "cfg.embyurl.label": "Emby public URL",
            "cfg.embyurl.desc": "Emby access URL (e.g. http://localhost:8096), used to build the image links sent to the LLM.",
            "cfg.relang.label": "LLM response language",
            "cfg.relang.auto": "Auto (interface language — default)",
            "cfg.relang.desc": "Choose the language for recommendations, the audit report, .strm cards and TMDB synopsis. The plugin favors your chosen language, then English, and finally a translation by the LLM if needed. The “Auto” setting follows your Emby interface language. Movie and series titles, channel names and technical fields stay unchanged to guarantee their accuracy.",
            "cfg.filters.h": "TV guide analysis filters (recommendations task)",
            "cfg.filters.desc": "These filters define which TV guide programs are analyzed for your recommendations. For channels and genres, check the ones you want to keep; if nothing is checked, everything is included. For the Kids, News and Sports categories: when checked, these programs are added to movies and series. When left unchecked, only the latter are considered.",
            "cfg.channels.label": "Channels",
            "cfg.channels.search": "Search a channel",
            "cfg.channels.ph": "Filter the list…",
            "cfg.channels.loading": "Loading channels…",
            "cfg.channels.desc": "Channels to keep. Empty = all channels.",
            "cfg.genres.label": "Genres",
            "cfg.genres.loading": "Loading genres…",
            "cfg.genres.desc": "TV guide genres (collected from upcoming programs) to keep. Empty = all genres.",
            "cfg.flags.series": "Program types to add — Series",
            "cfg.flags.series.desc": "By default, TV guide programs tagged Kids, News or Sports are ignored. Check a category to include it in the series analysis.",
            "cfg.flags.movies": "Program types to add — Movies",
            "cfg.flags.movies.desc": "By default, TV guide programs tagged Kids, News or Sports are ignored. Check a category to include it in the movies analysis.",
            "cfg.flag.kids": "Kids",
            "cfg.flag.news": "News",
            "cfg.flag.sports": "Sports",
            "cfg.maxseries.label": "Series cap (per call)",
            "cfg.maxseries.desc": "Max series submitted to the LLM (after pre-sorting). Default 40.",
            "cfg.maxmovies.label": "Movies cap (per call)",
            "cfg.maxmovies.desc": "Max movies submitted to the LLM (after pre-sorting). Default 30.",
            "cfg.apikeys.h": "API keys — external services",
            "cfg.apikeys.desc": "Keys for the web services the plugin can call. An empty field disables the corresponding service: the LLM no longer sees it.",
            "cfg.tmdb.label": "TMDB API key",
            "cfg.tmdb.ph": "themoviedb.org key",
            "cfg.tmdb.desc": "TMDB API key, used to enrich item details (synopsis, status). Leave empty to disable the tool.",
            "cfg.tmdblang.label": "TMDB language",
            "cfg.tmdblang.desc": "Only used when \"LLM response language\" is Auto: the language used to query TMDB (e.g. fr-FR, en-US). When a response language is set, it drives TMDB.",
            "cfg.tvdb.label": "TheTVDB API key",
            "cfg.tvdb.ph": "api4.thetvdb.com key",
            "cfg.tvdb.desc": "TheTVDB (v4) API key, used to enrich series (FR synopsis first). Falls back to the TVDB_API_KEY environment variable. Empty = tool disabled.",
            "cfg.ollama.label": "Ollama API key (cloud and web search)",
            "cfg.ollama.desc": "ollama.com key, used for Ollama cloud and web search. Falls back to the OLLAMA_API_KEY environment variable. Empty = Ollama cloud disabled.",
            "cfg.searxng.label": "SearXNG URL (web search — optional)",
            "cfg.searxng.desc": "Self-hosted SearXNG instance, used first for web search — free, no quota. The JSON format must be enabled in its settings (search.formats). Without SearXNG, web search goes through Ollama cloud. Empty = disabled.",
            "cfg.webfetch.label": "Direct page fetching (no key)",
            "cfg.webfetch.desc": "Fetches web pages directly from the Emby server and extracts their content (title, text, tables) — no quota, no key. On anti-bot blocking, automatic fallback to Ollama cloud if a key is set. Uncheck to route everything through Ollama cloud. Enabled by default.",
            "cfg.gemini.label": "Google Gemini API key",
            "cfg.gemini.desc": "Google AI Studio key, used for the Gemini server. Falls back to the GEMINI_API_KEY environment variable. Empty = Gemini disabled.",
            "cfg.newreleases.label": "Web new-releases sources",
            "cfg.newreleases.ph": "https://example.com/feed.rss\nhttps://example.com/new-releases :: @showbizz",
            "cfg.newreleases.desc": "One source per line: a bare URL = auto-detected RSS/Atom feed; \"URL :: @showbizz\" = built-in Showbizz.net extractor; \"URL :: .NET regex\" = custom extraction (required \"title\" group, optional \"url\"/\"date\"). Empty = tool disabled. The list is re-read without a restart (24h cache). The \"Test sources\" button validates each line without saving.",
            "cfg.newreleases.test": "Test sources",
            "cfg.newreleases.test.title": "Tests each line without saving: detected mode, number of items found, title samples — or the exact error (HTTP, timeout, regex).",
            "cfg.newreleases.testing": "Testing sources…",
            "cfg.newreleases.test.line_ok": "✅ {0} ({1}): {2} item(s){3}",
            "cfg.newreleases.test.line_zero": "⚠️ {0} ({1}): 0 item — unsuitable line (outdated regex, page without announcements…): re-validate it",
            "cfg.newreleases.test.line_err": "❌ {0}: {1}",
            "cfg.newreleases.test.fail": "❌ {0}",
            "cfg.rag.label": "System prompt (agent directives)",
            "cfg.rag.desc": "Instructions sent to the LLM at the start of every call.",
            "cfg.debug.label": "Debug mode (full agent logging)",
            "cfg.debug.desc": "Writes everything the LLM sees to the Emby log: full prompts, responses, tool results. Enable temporarily to understand a behavior. Disabled by default.",
            "cfg.sched.series.label": "Series scheduled task — schedule and prompt",
            "cfg.sched.series.ph": "Daily 03:00 | Recommend SERIES recordings: …",
            "cfg.sched.series.desc": "Format: \"schedule | task prompt\". Schedule: Daily HH:MM, Hourly, Weekly Day HH:MM or Interval hours. The text after \"|\" is sent to the LLM for the series run (first episodes S01E01 and new seasons). The task can also be triggered manually in \"Scheduled Tasks\".",
            "cfg.sched.movies.label": "Movies scheduled task — movies run prompt",
            "cfg.sched.movies.ph": "Recommend MOVIE recordings: …",
            "cfg.sched.movies.desc": "Prompt sent to the LLM for the movies run (upcoming movies absent from the library). No schedule here: it comes from the Series field. The movies run is merged with the series on the Recommendations page. Empty = no movies run (series only).",
            "cfg.dropped.label": "Titles to exclude from recommendations",
            "cfg.dropped.ph": "Star Trek\nCastle\nOne title per line",
            "cfg.dropped.desc": "Titles never recommended: the task removes them from the list sent to the LLM. One title per line. The \"Forget\" button on the Recommendations page also adds titles here.",

            "cfg.tonight.h": "Watch tonight (per-user personalized recommendation)",
            "cfg.tonight.desc": "\"Watch tonight\" section of the Recommendations page: analyzes the user's watch history and crosses it with the evening's TV guide to suggest what to watch now. Computed when the page opens, with a per-user cache.",
            "cfg.tonight.enabled": "Enable the \"Watch tonight\" section",
            "cfg.tonight.genretag.flag": "Add the \"AI Tonight\" tag to \"watch tonight\" recommendations",
            "cfg.tonight.genretag.desc": "Adds the AI Tonight tag to the recommended items (unwatched recordings + library): find them by filtering on this tag in any Emby client (the \"Tags\" filter). A scheduled task removes the tag every night at 3 AM; subsequent runs re-add it. The tag modifies item metadata — an Emby refresh may drop it (it will be re-added).",
            "cfg.tonight.collection.flag": "Group \"watch tonight\" recommendations into an \"AI Tonight\" collection",
            "cfg.tonight.collection.desc": "Maintains an \"AI Tonight\" Emby collection grouping the recommended items (unwatched recordings + library) — browse it like any collection. Non-destructive: items are referenced, never copied or moved; playing a member plays the real item. Independent of the tag (both can coexist). The 3 AM scheduled task empties the collection every night; it is refilled on the next run.",
            "cfg.tonight.playlist.flag": "Fill an \"AI Tonight\" playlist with \"watch tonight\" recommendations",
            "cfg.tonight.playlist.desc": "Maintains an \"AI Tonight\" Emby playlist filled with the recommended items (unwatched recordings + library) — continuous playback from any Emby client. Reset on every run: the playlist is emptied then filled with the day's recommendations. Public (visible to the household), owned by the user below. Independent of the tag and the collection (all three can coexist). The 3 AM scheduled task also empties the playlist.",
            "cfg.tonight.favorites.flag": "Favorite \"watch tonight\" recommendations (ephemeral favorites)",
            "cfg.tonight.favorites.desc": "On every new run, the recommendations are favorited (\"My List\") for the user below. Ephemeral favorites: the 3 AM scheduled task removes them — only those placed by the plugin (tracked in a state file), never pre-existing favorites. Known limit: an item the user manually re-favorites is removed anyway (indistinguishable from a plugin-placed one).",
            "cfg.tonight.user": "User for AI Tonight favorites and playlist",
            "cfg.tonight.user.desc": "Owner of the ephemeral favorites and the playlist. Empty = first admin user (otherwise the first user), skipping ignored accounts below. Must be the exact name of an existing Emby user.",
            "cfg.tonight.ignored": "Ignored accounts (one Emby user name per line)",
            "cfg.tonight.ignored.desc": "Accounts excluded from the household mechanisms: their viewing position doesn't count in the public \"AI Tonight\" playlist resolution; the fallback of the field above (first admin) skips them; their login no longer triggers an \"AI Tonight\" run. Never applied to the parental intersection nor to per-user surfaces (recos, private playlist — an ignored account keeps its surfaces on manual refresh). Case and accents tolerated (\"rene\" = \"René\"). Empty = no change.",
            "cfg.tonight.window.start": "Window start (HH:mm)",
            "cfg.tonight.window.end": "Window end (HH:mm)",
            "cfg.tonight.window.desc": "TV guide time window queried (e.g. 18:00 → 23:59). Empty start = now; empty end = 23:59.",
            "cfg.tonight.prompt": "Tonight prompt",
            "cfg.tonight.prompt.desc": "Prompt template sent to the LLM. The user's taste profile (recent history) is injected at runtime.",
            "cfg.tonight.batch": "Programs cap (per call)",
            "cfg.tonight.batch.desc": "Max evening programs submitted to the LLM (after relevance pre-sort). Default 10.",
            "cfg.tonight.cache": "Cache (hours)",
            "cfg.tonight.cache.desc": "Per-user cache validity duration. 0 = no cache (recomputed on every open). Default 4.",
            "cfg.tonight.recDays": "Recordings (days)",
            "cfg.tonight.recDays.desc": "Lookback window for recent unwatched recordings (candidates for \"watch tonight\"). Default 7.",
            "cfg.tonight.minRec": "Min recommendations",
            "cfg.tonight.minRec.desc": "If the TV guide and recordings yield fewer recommendations, fill with unwatched library titles. Default 3.",
            "cfg.tonight.binge.flag": "Surface \"binge-ready\" series (stockpile of unwatched recorded episodes)",
            "cfg.tonight.binge.desc": "Detects series being recorded whose unwatched episodes the user is stockpiling: when the stockpile reaches the threshold, the tonight run suggests starting it (\"time to start X, N episodes waiting\"). Anti-spam: each series is surfaced only once; the suggestion re-arms only after the unwatched count drops back below the threshold. A dormant series (never started, kept \"for a rainy day\") is never surfaced: only still-active series are.",
            "cfg.tonight.binge.threshold": "Episode stockpile threshold",
            "cfg.tonight.binge.threshold.desc": "Unwatched-episode count at which a series becomes \"binge-ready\". Default 4.",
            "cfg.tonight.binge.days": "Activity window (days)",
            "cfg.tonight.binge.days.desc": "At least one episode of the series must have been added to the library within these last N days (the \"actively recording\" signal — tells an ongoing stockpile from a dormant series). Default 14.",
            "cfg.feedback.h": "Recommendation feedback loop",
            "cfg.feedback.desc": "Every recommendation (\"Watch tonight\", record task) and every rejection (\"Forget\") is logged. Once a week (Sunday 4 AM), an analysis task correlates that log with what the user actually watched and has the LLM produce a concise directive (what worked, what was ignored, what was missed), re-injected into subsequent run prompts — recommendations improve on their own over the weeks. Without a directive, prompts are unchanged.",
            "cfg.feedback.flag": "Enable the feedback loop (weekly analysis)",
            "cfg.feedback.directive": "Active directives (per user, JSON)",
            "cfg.feedback.directive.desc": "Directives produced by the latest analysis (one per user). Editable: the admin can fix or clear this JSON “[{\"u\":\"userId\",\"n\":\"name\",\"d\":\"date\",\"text\":\"…\"}]” — emptying the field (or a \"text\") removes the directive from prompts until the next analysis.",
            "cfg.memory.h": "Reflective memory (experimental)",
            "cfg.memory.desc": "The data that feeds the LLM's self-reflection: every emitted recommendation with its reasoning, the list of candidates submitted on each run, and every finished playback with the percentage watched. Three local JSON files (30-day retention, size-capped), no outbound data.",
            "cfg.memory.decisions.flag": "Log the LLM's decisions (recommendation, reasoning, candidates)",
            "cfg.memory.decisions.desc": "For every recommendation (\"Watch tonight\", recordings) and every \"Forget\" rejection, writes “decisions.json” (reasoning, priority, card version) and “run_pool.json” (discarded candidates). Also dual-writes the recommendations journal, required by the weekly analysis.",
            "cfg.memory.playback.flag": "Playback telemetry (% watched of every playback)",
            "cfg.memory.playback.desc": "At every playback end, writes “playback.json” (item, user, actual duration, fraction watched, source, channel, client, device). The core behavioural signal: instant rejection (under 5%), abandonment (5–50%), validated content (over 80%). No effect on playback itself.",
            "cfg.memory.card.flag": "Enable the LLM's memory card (weekly revision)",
            "cfg.memory.card.desc": "Weekly task (Sunday 4:30 am): the LLM rewrites its own memory card (~250 words) from the week's events — what it knows about the user, its wins, its failures and its strategies. Four versions are kept. The card is re-injected into every prompt (recommendations, recordings, chat) and replaces the feedback-loop directive. On failure, the previous card is kept.",
            "cfg.memory.cardview.h": "Current memory card",
            "cfg.memory.cardview.desc": "Admin read and edit: you can fix the card by hand (the LLM will rewrite from your version next week). Version 0 = not written yet (the first revision needs one week of data).",
            "cfg.memory.cardview.meta": "Version {0}, updated {1} — {2} previous version(s) kept.",
            "cfg.memory.cardview.empty": "No card yet (v0) — it will be written at the first weekly revision.",
            "cfg.memory.cardview.error": "Card unavailable: {0}",
            "cfg.memory.cardview.save": "Save card",
            "cfg.memory.cardview.saving": "Saving…",
            "cfg.memory.cardview.saved": "Card saved.",
            "cfg.memory.cardview.savefail": "Failed: {0}",
            "cfg.autoprog.h": "Auto-programming & login popup",
            "cfg.autoprog.desc": "Native Android / Android TV clients don't render plugin HTML pages: recommendations are only visible on the web page. Auto-programming creates the Emby recording timers for the recommendations — they stand out in the native guide (record badge) on every client. The login popup surfaces what to watch tonight (library, recordings).",
            "cfg.autoprog.flag": "Auto-program recommendations (create recording timers)",
            "cfg.autoprog.flag.desc": "When checked, after each run (scheduled task and login), recommendations to record are programmed: upcoming TV guide programs, not already owned, not already scheduled, outside the excluded titles — a SeriesTimer for a series, a single timer for a movie. No programming while unchecked. The user can cancel an unwanted timer directly in Emby.",
            "cfg.diskgate.threshold": "Recording disk threshold (GB)",
            "cfg.diskgate.threshold.desc": "Minimum free space on the recordings disk. Below it, no new timer is created (existing timers keep recording). 0 = disabled. Default 25.",
            "cfg.diskgate.tag.flag": "Suggest watched recordings for deletion when the disk threshold is crossed (\"AI Delete\" tag)",
            "cfg.diskgate.tag.desc": "When free space drops below the threshold, watched recordings get the \"AI Delete\" tag, oldest first, until deleting them brings the disk back above the threshold (with margin). Pure suggestion — the plugin never deletes anything: filter the recordings library by this tag (the \"Tags\" filter), select then delete. Each pass first removes previous tags (if you freed space, everything clears). Unwatched recordings are never tagged.",
            "cfg.aibadge.flag": "\"AI\" badge on the image of to-record suggestions (native guide)",
            "cfg.ownedbadge.flag": "Yellow \"already owned\" badge on the image of library shows (native guide)",
            "cfg.ownedbadge.desc": "Yellow chip (no icon) on the image of guide programs whose series or movie is already in the library — matching is by name (FR/EN articles and case ignored). The user knows there is no point recording it. Same non-destructive mechanism as the \"AI\" badge: the stored image is never modified. NB: a new season of an owned series also carries the yellow chip (same series name).",
            "cfg.aibadge.desc": "Green chip with a sparkle icon (no text, multilingual) on the top-right corner of the image of suggested programs — visible in the native guide, on every client. Non-destructive: the badge is added when the image is served; the stored image is never modified (the imported recording keeps its original image and the badge disappears once the show is recorded). Each run replaces the badged selection; past airings self-expire.",
            "cfg.loginpopup.flag": "Login popup (\"watch tonight\" suggestions)",
            "cfg.loginpopup.seconds": "Popup display duration (seconds)",
            "cfg.loginpopup.desc": "Independent of auto-programming: the popup lists what to watch tonight (unwatched recordings, library). The message appears on the connecting client; if the client doesn't support it, a persistent notification (bell) takes over. The bell remains even if the session closes before the computation ends (~30–60 s).",
            "cfg.strmlib.h": "Recommendations .strm library",
            "cfg.strmlib.desc": "A manual alternative to auto-programming: after each run, the plugin writes a card (.strm, .nfo, poster) for each recommendation to record — upcoming TV guide programs, not owned — into a dedicated Emby library. The user browses the library; playing a card creates the recording then shows a confirmation clip. First create a Movies (or Mixed content) library in Emby pointing at an empty folder, then enter its exact name here.",
            "cfg.strmlib.flag": "Enable the recommendations .strm library",
            "cfg.strmlib.name": "Dedicated Emby library name",
            "cfg.strmlib.name.desc": "Exact name of the Emby library where cards are written (case-insensitive). Independent of auto-programming: both can coexist, duplicate timers are avoided. A security token is generated automatically on the first pass to protect activation.",
            "cfg.strmlib.perm": "Access rights: playing a card schedules a recording — so reserve the library, in the Emby dashboard (Users → allow access per folder), to accounts holding the Record permission (EnableLiveTvManagement). The plugin refuses activations triggered by an account without that permission (dedicated message, no timer created), and the Recommendations page hides its recording sections for such accounts.",

            // -- Orphan recording identification (scheduled task, 4 AM) ---
            "cfg.orphan.h": "Orphan recording identification",
            "cfg.orphan.desc": "Daily pass (4 AM) that finds unidentified recordings (no IMDb/TMDB id — often Quebec titles missing from TMDB/TVDB) and resolves them in three stages: title cleanup and multi-language search (S1), an IMDb id proposed by the LLM then validated through TMDB (S2), a SearXNG web search (S3). The id, metadata and poster are written while locking the guide title (never overwritten). Unresolved ones are tagged for review. Mutates recordings.",
            "cfg.orphan.enabled": "Enable the orphan identification task",
            "cfg.orphan.dryrun": "Simulation mode (no writes)",
            "cfg.orphan.dryrun.desc": "When checked, the task writes nothing: it only logs the orphans found, the proposed resolution and a summary. Use it to validate resolution quality before switching to automatic application. Keep checked for the first passes.",
            "cfg.orphan.searxng": "S3 stage: web search (SearXNG) for titles that can't be found",
            "cfg.orphan.searxng.desc": "When checked, after S1 and S2 the task queries SearXNG (the URL configured above), extracts IMDb ids from the results, and validates them through TMDB and the synopsis judge. Resolves paraphrased titles no catalog knows (e.g. \"L'histoire de Jean Seberg\" → the film \"Seberg\", 2019). No-op if neither SearXNG nor an Ollama key is configured.",
            "cfg.orphan.retry": "Re-process items marked for review",
            "cfg.orphan.retry.desc": "When checked, orphans tagged \"needs-review\" are reprocessed instead of skipped — the S3 stage can then run on them once SearXNG is configured. On success the tag becomes \"identified\". Already-identified items stay skipped.",
            "cfg.orphan.firstpass": "S0 stage: native Emby first pass (\"Identify\" engine)",
            "cfg.orphan.firstpass.desc": "When checked, orphans go through the native Emby search first (the same engine as the \"Identify\" dialog, with the server-configured keys) before the S1 stage. Every candidate is checked on title, year and synopsis — a bad match is rejected and the S1→S2→S3 chain continues. No-op when the TMDB key is missing.",
            "cfg.orphan.validate": "Validate metadata when each recording finishes",
            "cfg.orphan.validate.desc": "When checked, at the end of every recording the plugin freezes the guide data (title, synopsis, year) then audits the identification written by Emby's automatic providers. If the synopsis matches: fields locked and the \"identified\" tag set. Otherwise: ids removed, back to the guide data, and the S1→S2→S3 chain resumes immediately. Unidentified recordings go through immediately. Without this option the nightly 4 AM pass stays the only safety net. Can be enabled without restart; honors the simulation mode.",
            "cfg.orphan.audit": "Audit tagged items that received an id in the meantime",
            "cfg.orphan.audit.desc": "When checked, items tagged \"not found\" or \"needs review\" that received an Emby id in the meantime are re-audited: the TMDB entry of the written id is checked against the item title. If everything matches: fields locked and the \"identified\" tag set. Otherwise (wrong Emby identification, e.g. a namesake): ids removed and the S1→S2→S3 chain resumes immediately. Honors the simulation mode. Mutates metadata.",

            "cfg.audit.h": "Server health audit",
            "cfg.audit.desc": "An LLM agent inspects the server (sessions, scheduled tasks, transcoding, disks, logs) and writes a health report: findings ranked by severity, recommended actions. The last report is saved and shown again when the page loads. Administrators only.",
            "cfg.audit.enabled": "Enable the audit (/Plugins/LLMAI/Audit)",
            "cfg.audit.remediation": "Allow the agent to act (stop a playback, trigger a task, send a message)",
            "cfg.audit.remediation.desc": "Off by default: the agent can only recommend actions in its report. When checked, it may execute them during the audit. Either way, it never acts without an explicit request.",
            "cfg.audit.prompt": "Audit prompt",
            "cfg.audit.prompt.desc": "Instructions sent to the model for the audit. Feel free to adapt them — the workflow and guardrails are injected by the plugin — but keep the sentence that forbids remediation actions.",
            "cfg.audit.mode": "Audit execution mode",
            "cfg.audit.mode.single": "Adaptive agent (capable / cloud model)",
            "cfg.audit.mode.deterministic": "Deterministic, plugin-guided (local model)",
            "cfg.audit.mode.desc": "Adaptive agent: the agent decides what to inspect and can drill into a finding. Best kept for a capable (cloud) model. Deterministic: the plugin collects all the data, then the model writes the report in short passes — suited to a local or smaller model. Only the adaptive mode can execute remediation actions.",
            "cfg.audit.focus": "Optional audit focus",
            "cfg.audit.focus.desc": "Restricts the audit to one area or one specific request. Leave empty for a full audit.",
            "cfg.audit.focus.ph": "e.g. transcoding, disk, or stop session XYZ",
            "cfg.audit.run": "Run the audit",
            "cfg.audit.running": "Audit running in the background…",
            "cfg.audit.running.desc": "The audit starts in the background. You can leave this page; the report will be saved automatically at the end, no matter what. Clicking again while an audit is running does not restart it: it just lets you follow its progress.",
            "cfg.audit.failed": "Audit failed — the last saved report remains displayed.",
            "cfg.audit.done": "Audit complete",
            "cfg.audit.disabled": "Audit is disabled in the configuration.",
            "cfg.audit.last": "Last saved report — {0} (mode {1})",
            "cfg.i18n.h": "Interface languages",
            "cfg.i18n.desc": "Adds or keeps a plugin interface language (configuration pages, server messages, companion app) with the LLM configured here. Generation goes through validated batches ({n} placeholders and HTML tags preserved, values identical to English rejected), then writes the LLM_AI_i18n.json file into the plugin's configuration folder. The manual workshop described in the README remains possible, and the file can always be edited by hand.",
            "cfg.i18n.lang": "Language code",
            "cfg.i18n.lang.desc": "2 or 3 lowercase letters (e.g. es, de, it, pt). The official Emby terminology for that language (the server strings) anchors the glossary of every batch; without the local file the glossary is empty (warning in the report).",
            "cfg.i18n.lang.invalid": "Invalid language code — expected 2-3 letters (e.g. es).",
            "cfg.i18n.loading": "Checking coverage…",
            "cfg.i18n.coverage": "Coverage of “{0}” — web {1}/{2}, server {3}/{4}, app {5}/{6} present; {7} missing.",
            "cfg.i18n.coverage.err": "Cannot read coverage (plugin too old or server unreachable).",
            "cfg.i18n.mode": "Generation mode",
            "cfg.i18n.mode.full": "Generate / redo the full language",
            "cfg.i18n.mode.missing": "Complete — missing and skipped keys only",
            "cfg.i18n.mode.skipped": "Re-translate skipped keys only",
            "cfg.i18n.mode.desc": "“Generate” redoes every key from scratch (existing content is overwritten, with a .bak file kept). “Complete” only handles absent keys; your manual edits stay untouched. “Re-translate skipped” only takes the keys rejected by validation (native fallback in the meantime).",
            "cfg.i18n.run": "Start generation",
            "cfg.i18n.running": "Generation running… (background task)",
            "cfg.i18n.running.desc": "The generation runs as a background task: you can leave or reload the page, it continues and the report is written at the end. Extra clicks don't restart anything — they resume the progress.",
            "cfg.i18n.done": "Generation complete.",
            "cfg.i18n.failed": "Generation failed.",
            "cfg.i18n.endpoint.missing": "Generation is not available in this plugin version (update required).",
            "cfg.i18n.last": "Last generation report — {0}",
            "cfg.crosskind.h": "“Cross-kind” regularization queue",
            "cfg.crosskind.desc": "Works recorded by Emby under the wrong kind. Two sources feed the queue: items tagged llmai-cross-kind (movie fiche on a series item, or the reverse) and suspects — items tagged llmai-not-found whose folder lives inside the recordings path (work missing from TMDB: a documentary imported as a series, for instance). For each, two remedies: copy the video file to the library of its kind, under the name “Title (Year).ts” (verified copy, never destructive — the original stays in place; delete it afterwards so Emby removes the old item); or convert the recording folder in place: rename the folder, the video, the .nfo and the poster to “Title (Year)”, rewrite the .nfo with a &lt;movie&gt; root, remove tvshow.nfo — Emby then re-imports the work under the right kind, and any failure restores the initial state. “Ignore” removes an entry from the queue (llmai-cross-kind-ignored tag), restorable on demand.",
            "cfg.crosskind.refresh": "Reload the queue",
            "cfg.crosskind.queue.loading": "Reading the queue… (TMDB entries re-read, a few seconds)",
            "cfg.crosskind.queue.empty": "No cross-kind or suspect work pending — nothing to regularize.",
            "cfg.crosskind.queue.err": "Failed to read the queue (server unavailable?).",
            "cfg.crosskind.status.pending": "to regularize",
            "cfg.crosskind.status.done": "copy done — original still in place",
            "cfg.crosskind.status.suspect": "suspect — no fiche, recording folder",
            "cfg.crosskind.status.ignored": "ignored",
            "cfg.crosskind.evidence.base": "Suspect: not-found tag + folder inside the recordings path.",
            "cfg.crosskind.evidence.fiche": "Opposite-kind {0} fiche found by exact title: “{1}” ({2}).",
            "cfg.crosskind.evidence.nofiche": "No TMDB fiche in the opposite kind (exact-title search).",
            "cfg.crosskind.showignored": "Show ignored entries",
            "cfg.crosskind.ignore": "Ignore",
            "cfg.crosskind.unignore": "Stop ignoring",
            "cfg.crosskind.kind.movie": "movie",
            "cfg.crosskind.kind.series": "series",
            "cfg.crosskind.cross": "{1} item carrying a {0} fiche —",
            "cfg.crosskind.target": "Suggested target: {0} / {1}",
            "cfg.crosskind.regularize": "Regularize…",
            "cfg.crosskind.dialog.title": "Regularize “{0}”",
            "cfg.crosskind.dialog.sources": "Source file(s):",
            "cfg.crosskind.dialog.lib": "Target library",
            "cfg.crosskind.dialog.lib.desc": "Movie/TV libraries or mixed-content ones, filtered by the kind targeted by the entry (the plugin's .strm card library is never offered; the one holding the recordings is offered when it supports the targeted kind — it remains subject to recording retention). Picking a library pre-fills the destination folder with the suggested “Title (Year)” target.",
            "cfg.crosskind.dialog.folder": "Destination folder",
            "cfg.crosskind.dialog.folder.desc": "Full target folder; the file is copied into it under the name below. Editable: you can point directly to a “Season 01” folder for a series fiche.",
            "cfg.crosskind.dialog.file": "Copied file name",
            "cfg.crosskind.dialog.file.desc": "Suggested “Title (Year).ext” derived from the fiche. For a series fiche (an episode of a series), write the full name, e.g. “Series (2019) - S01E05.ts”.",
            "cfg.crosskind.dialog.go": "Copy (the original stays in place)",
            "cfg.crosskind.dialog.cancel": "Cancel",
            "cfg.crosskind.dialog.needfolder": "Enter the destination folder.",
            "cfg.crosskind.dialog.err": "Regularization failed ({0}).",
            "cfg.crosskind.copying": "Copying…",
            "cfg.crosskind.done": "Done: {0} copied, {1} already in place, {2} failure(s).",
            "cfg.crosskind.finalize": "Copy is done. Delete the original files from the recording folder — Emby will remove the old item at the next scan.",
            "cfg.crosskind.failedNote": "Failure: nothing was copied for these files — do not delete the originals. Fix the cause (destination folder permissions, path) then click “Copy” again: the copy resumes where it left off.",
            "cfg.crosskind.convert.title": "Convert in place (recording folder)",
            "cfg.crosskind.convert.hint": "Renames the folder to “Title (Year)”, the video and the .nfo alike, poster.jpg to “Title (Year)-poster.jpg”, rewrites the .nfo with a &lt;movie&gt; root (otherwise the work would be read as an episode), then Emby re-imports the work under the right kind at the next scan. The guide image is also written as poster.jpg in the converted folder — the poster survives the re-import. The video is never deleted; any failure restores the initial state.",
            "cfg.crosskind.convert.name": "Target name “Title (Year)”",
            "cfg.crosskind.convert.name.desc": "Suggestion derived from the fiche, else from the item name and its air year. Several recordings: “(2)”, “(3)”… suffixes.",
            "cfg.crosskind.convert.tvshow": "Delete tvshow.nfo (required for the kind switch)",
            "cfg.crosskind.convert.go": "Convert in place",
            "cfg.crosskind.convert.busy": "Converting…",
            "cfg.crosskind.convert.needname": "Enter the target name “Title (Year)”.",
            "cfg.crosskind.convert.done": "Done: {0} file(s) renamed, {1} deleted, {2} failure(s).",
            "cfg.crosskind.convert.finalize": "The folder is converted — Emby re-imports it under the right kind at the next scan, triggered automatically.",
            "cfg.crosskind.convert.failedNote": "Failure: the initial state was restored — fix the cause and retry.",
            "cfg.crosskind.convert.err": "Conversion failed ({0}).",
            "cfg.chat.h": "Chat with the AI assistant",
            "cfg.update.available": "New version {0} available on GitHub (installed: {1}).",
            "cfg.update.link": "View release",
            "cfg.update.hint": "After installing, a simple page reload (F5) loads the new JS.",
            "cfg.gtx.h": "AI genre translation",
            "cfg.gtx.desc": "The plugin spots TV guide genres that are not translated yet and suggests equivalents based on your genre list. If a genre is completely new, the LLM can offer to create it and add it to your list in one click. You stay in full control by reviewing each proposal. These changes apply to recommendations immediately, but a server restart is required to update the GenreCleaner system as a whole.",
            "cfg.gtx.analyze": "Analyze untranslated genres",
            "cfg.gtx.analyzing": "Analyzing… (collecting TV guide genres and LLM translation, ~10–60 s)",
            "cfg.gtx.counts": "{0} untranslated genre(s) on the movie side, {1} on the series side. Uncheck what you don't want to apply.",
            "cfg.gtx.suggest.h": "No equivalent — suggested new genres (added to AllowedGenres when applied)",
            "cfg.gtx.newgenre": "new genre",
            "cfg.gtx.orphans": "{0} genre(s) with no possible equivalent (no action available): {1}",
            "cfg.gtx.movies": "movies",
            "cfg.gtx.series": "series",
            "cfg.gtx.apply": "Apply the selected translations",
            "cfg.gtx.applying": "Writing to GenreCleaner.xml…",
            "cfg.gtx.noneSelected": "No proposal checked.",
            "cfg.gtx.applied.restart": "{0} translation(s) added to GenreCleaner.xml. Recommendations use them right away. Restart the server so the GenreCleaner plugin adopts them (the « restart required » banner is displayed).",
            "cfg.gtx.applied.norestart": "{0} translation(s) already present in GenreCleaner.xml — nothing new to write. Recommendations already use them.",
            "cfg.gtx.error": "Failed: {0}",
            "chat.title": "💬 LLM AI Chat",
            "chat.resume.banner": "Conversation from {0} — {1} exchange(s) saved",
            "chat.resume.button": "Resume",
            "cfg.chat.desc": "Multi-turn conversation with the LLM agent, on its own page (Server menu → LLM AI Chat). It has every tool of the plugin (TV guide, library, TMDB/TVDB, web search, new releases, system audit) and follows the LLM server priorities configured above. The history is kept by the page; the directive and tool documentation are sent to the LLM once, at the start of the conversation. Admin-only.",
            "cfg.chat.enabled": "Enable interactive chat",
            "cfg.chat.memory.flag": "Conversation memory (resume the last chat)",
            "cfg.chat.memory.desc": "Every conversation is persisted (“chat_memory.json”). When the user comes back, one LLM call condenses the previous session into a continuity note (expressed tastes, facts, open thread); the taste signals go into “decisions.json” and the weekly memory card folds them in. The chat page shows a \"Resume\" button; the summary and last turns are injected into the prompt. On LLM failure, the next conversation continues without memory.",
            "cfg.chat.actions.desc": "The chat can act on the same surfaces as the plugin — .strm cards \"AI Suggestions\", recording timers, \"AI Tonight\" tag, collection, playlist — with the same guards (already owned, already watched, excluded titles, duplicates avoided). The budget caps successful actions, per question and per conversation; 0 = read-only chat. The agent proposes then waits for your confirmation before executing; an action refused by a guard does not consume the budget.",
            "cfg.chat.actions.budget": "Action budget (per question)",
            "cfg.chat.actions.budget.desc": "Max successful actions per question (all surfaces combined: cards, timers, tags, collection, playlist, tonight run). 0 = read-only. Default 10.",
            "cfg.chat.actions.cap": "Action cap (per conversation)",
            "cfg.chat.actions.cap.desc": "Cumulative cap per conversation (server-side in-memory counter, reset on restart). Default 30.",
            "cfg.chat.prompts": "Let the chat propose prompt edits",
            "cfg.extchat.desc": "For a companion app (standalone Python script shipped with the plugin, chat-external folder — no web server to install): “POST /Plugins/LLMAI/ChatExternal” talks to the agent on behalf of an Emby user, “POST /Plugins/LLMAI/Show” displays an item's detail page on that user's active Emby client. Read-only: no action tools, no audit; all content is filtered by the user's parental control and navigation only targets their session. Security: requests only from the server itself (the app calls Emby directly, no X-Forwarded-For), a dedicated secret, a user allowlist — administrators are always refused on this path.",
            "cfg.extchat.enabled": "Enable external chat (companion app)",
            "cfg.extchat.secret": "Shared secret (copied into the companion app's config)",
            "cfg.extchat.secret.desc": "Dedicated secret (distinct from the .strm secret). Generate it here then copy it into the companion app's config; change it on both sides to revoke access.",
            "cfg.extchat.users": "Allowed users (one Emby username per line)",
            "cfg.extchat.users.desc": "Same names as in the companion app. Unlisted users are refused; administrators are always refused (admin chat stays on /Plugins/LLMAI/Chat).",
            "cfg.extchat.perMinute": "Anti-spam (messages/minute)",
            "cfg.extchat.perMinute.desc": "Max messages per user per minute (sliding window) — each message triggers an LLM call. 0 = unlimited. Default 5.",
            "cfg.extchat.perDay": "Quota (messages/24 h)",
            "cfg.extchat.perDay.desc": "Max messages per user over 24 h (sliding window). 0 = unlimited. Default 150 (in-memory counter: an Emby restart resets it).",
            "cfg.extchat.rec.enabled": "Voice recordings (PIN confirmation)",
            "cfg.extchat.rec.desc": "A record request never executes on the first try: it is held as a reservation and a 4-digit PIN appears on the app's screen (the LLM never sees it); the user then gives that PIN in their message, and only the exact PIN creates the timer. 3 wrong PINs = tool locked for 15 min for that user; the daily quota only counts recordings actually created; parental control always applies. Requires all of: the checkbox here, the user listed below, and the Emby right to record live TV.",
            "cfg.extchat.rec.users": "Users allowed to record (one Emby username per line)",
            "cfg.extchat.rec.users.desc": "Empty = nobody allowed. On top of the Emby right (Manage Live TV recordings) and the checkbox above.",
            "cfg.extchat.rec.perDay": "Quota (recordings created/24 h)",
            "cfg.extchat.rec.perDay.desc": "Max recordings created per user over 24 h (sliding window) — counted only when the PIN is confirmed, never on an expired or refused reservation. 0 = unlimited. Default 3.",
            "cfg.chat.prompts.desc": "The chat can read the five configuration prompts and propose rewrites. Writing happens in two steps: the proposal waits for the admin's \"Approve\" click on the chat page (expires in 10 min, one per conversation) — the LLM has no direct write path. The chat page's dropdown editing modes remain available without this option.",
            "chat.context": "Conversation mode",
            "chat.context.none": "None (general assistant)",
            // Edit-mode labels (chat dropdown): the server registry
            // (ChatContexts.All) serves the FR reference label; the page
            // translates by id key — the language follows the client's
            // Emby interface, like the rest of the page (fallback: the
            // server label).
            "chat.ctx.edit_rag": "Edit — System prompt",
            "chat.ctx.edit_schedule_series": "Edit — Series task (recordings)",
            "chat.ctx.edit_schedule_movies": "Edit — Movies task (recordings)",
            "chat.ctx.edit_tonight": "Edit — \"Watch tonight\" run",
            "chat.ctx.edit_audit": "Edit — Health audit prompt",
            // Workshop mode (v1.17.0.2): the only mode without an editable
            // prompt — missing at first, it fell back to the FR server label
            // on any interface (fixed v1.17.1.2, renamed "Edit — Language
            // workshop").
            "chat.ctx.i18n_edit": "Edit — Language workshop",
            // Automatic announcement on mode change (the \"You\" turn of
            // the thread — seen by the user AND re-read by the LLM) +
            // send errors (timeout / connection): follow the page language.
            "chat.mode.selected": "[Admin] I selected the mode \"{0}\". First of all: clearly state which prompt you are working on (the field concerned) and show the current text you are about to modify.",
            "chat.err.timeout": "Request took too long — the LLM did not respond within the allotted time. Try again.",
            "chat.err.network": "Server unreachable (connection interrupted).",
            // [Admin] notes pushed into the thread after an Approve/Refuse
            // click (v1.15.0.2 — they follow the page language like the mode
            // announcement; the LLM replays them on the next turn and may
            // quote them, hence the consistent voice).
            "chat.note.prompt.approved": "[Admin] I approved the prompt change \"{0}\" — it has been saved to the configuration.",
            "chat.note.prompt.refused": "[Admin] I rejected the prompt change \"{0}\" — nothing was written.",
            "chat.note.testhint": " How to test it: {0}",
            "chat.note.action.approved": "[Admin] I approved the action \"{0}\" — {1}",
            "chat.note.action.executed": "it was executed",
            "chat.note.action.executed.detail": "it was executed: {0}",
            "chat.note.action.failed": "but the execution did not complete: {0}",
            "chat.note.action.refused": "[Admin] I rejected the action \"{0}\" — nothing was executed.",
            "chat.empty": "(empty)",
            "chat.pending.title": "Prompt edit pending approval",
            "chat.pending.field": "Field",
            "chat.pending.before": "Before",
            "chat.pending.after": "After",
            "chat.pending.approve": "Approve",
            "chat.pending.refuse": "Refuse",
            "chat.pending.approved": "Approved — the prompt was saved to the configuration. Reload the configuration page to see the new value.",
            "chat.pending.refused": "Refused — nothing was written.",
            "chat.pending.error": "Approval failed",
            "chat.pending.stale": "Superseded or expired proposal — no longer actionable.",
            "chat.i18n.pending.title": "Pending i18n proposal",
            "chat.i18n.pending.key": "Key",
            "chat.i18n.pending.lang": "Language",
            "chat.i18n.pending.before": "Current value",
            "chat.i18n.pending.after": "New value",
            "chat.i18n.pending.absent": "(absent from the overlay — first write)",
            "chat.i18n.pending.approve": "Approve",
            "chat.i18n.pending.refuse": "Refuse",
            "chat.i18n.pending.approved": "Written — the server now serves: \"{0}\".",
            "chat.i18n.pending.refused": "Refused — nothing was written.",
            "chat.note.i18n.approved": "i18n proposal approved by the admin via the card: key {0} ({1}/{2}) written; the server now serves \"{3}\".",
            "chat.note.i18n.refused": "i18n proposal refused by the admin via the card: key {0} ({1}/{2}) — nothing was written.",
            "chat.action.pending.title": "Emby action pending approval (the LLM cannot execute)",
            "chat.action.approve": "Approve",
            "chat.action.refuse": "Refuse",
            "chat.action.approved": "Approved — action executed by the server.",
            "chat.action.refused": "Refused — nothing was executed.",
            "chat.action.error": "Approval failed",
            "chat.code.copy": "Copy code",
            "chat.code.save": "Ask to save — sends the request to the LLM; the approval card remains the authorization step",
            "chat.code.save_request": "Save the following text as the complete new text of the active mode's prompt (plugin_prompts action=\"set\"), without modifying it:\n\n{0}",
            "chat.code.save_request_turn": "Save the revision you just proposed in your last reply as the complete new text of the active mode's prompt (plugin_prompts action=\"set\"), without modifying it and without including any comment or question in the saved text.",
            "chat.code.reemit_request": "Re-emit the revision from your last reply in a ```text code block, without modifying it — the block will carry the save button.",
            "chat.code.save_turn": "The reply is not in a code block — this button makes the LLM re-emit the revision in a ```text block (one-shot instruction, exact targeting); saving then happens via the block's button, the approval card remains the authorization step",
            "chat.code.save_turn_short": "Prepare save (code block)",
            "reco.viaChat": "Generated via chat — directives: {0}",
            "reco.viaChat.short": "Generated via chat",
            "cfg.chat.input": "Your message",
            "cfg.chat.placeholder": "e.g. what's on tonight that I might like?",
            "cfg.chat.send": "Send",
            "cfg.chat.clear": "Clear conversation",
            "cfg.chat.running": "The assistant is thinking… (it may query your tools, a few tens of seconds)",
            "cfg.chat.disabled": "Chat is disabled in configuration.",
            "cfg.chat.you": "You",
            "cfg.chat.assistant": "AI assistant",
            "cfg.chat.hint": "Ask the assistant anything — it will query your Emby data (TV guide, library, server health) to answer.",
            "cfg.save": "Save",

            "cfg.wl.empty": "(no items available)",
            "cfg.backend.provider.local": "Ollama local",
            "cfg.backend.provider.cloud": "Ollama cloud",
            "cfg.backend.provider.gemini": "Google Gemini",
            "cfg.backend.provider.label": "Provider",
            "cfg.backend.num": "LLM #{0}",
            "cfg.backend.remove": "Remove",
            "cfg.backend.url.label": "Base URL",
            "cfg.backend.model.label": "Model",
            "cfg.backend.prio.label": "Priority",
            "cfg.backend.enabled": "Enabled",
            "cfg.backend.test": "Test",
            "cfg.backend.test.title": "Quick probe question sent to this server (30 s at most). You can test before saving; API keys are re-read from the saved configuration.",
            "cfg.backend.testing": "Testing…",
            "cfg.backend.test.ok": "✅ OK ({0} ms) — \"{1}\"",
            "cfg.backend.test.fail": "❌ {0}",
            "cfg.alert.saved": "Configuration saved.",
            "cfg.reset": "Reset",
            "cfg.reset.title": "Restores the original directive (in the configured language). Not saved until you click \"Save\".",
            "cfg.reset.error": "Could not fetch the default directives: {0}",
            "cfg.sections.collapse": "Collapse all",
            "cfg.sections.expand": "Expand all",
            "cfg.alert.saveError": "Error saving configuration: {0}",

            "rec.title": "🤖 LLM AI Recommendations",
            "rec.toggleRaw": "Show / hide raw JSON",
            "rec.prio.high": "⚡ High",
            "rec.prio.medium": "🔶 Medium",
            "rec.prio.low": "🔵 Low",
            "rec.btn.program": "✅ Schedule",
            "rec.btn.drop": "🗑️ Forget",
            "rec.btn.noId": "No program Id attached (title not matched)",
            "rec.btn.scheduled": "✓ Scheduled",
            "rec.btn.already": "Already scheduled",
            "rec.btn.forgotten": "✓ Forgotten",
            "rec.section.series": "Series",
            "rec.section.movies": "Movies",
            "rec.section.empty": "No recommendations in this section.",
            "rec.count": "{0} recommendation(s)",
            "rec.lastRun": "Last run: {0}",
            "rec.noRun": "No run recorded yet.",
            "rec.empty": "No recommendations yet. Run the \"LLM AI Task\" in Scheduled Tasks.",
            "rec.alert.refused": "Emby refused the recording: {0}",
            "rec.alert.refusedShort": "Emby refused the recording.",
            "rec.alert.dropSave": "Failed to save the drop list: {0}",
            "rec.alert.cfgRead": "Failed to read configuration: {0}",
            "rec.alert.cfgLoad": "Failed to load the plugin configuration.",

            "rec.section.tonight": "Watch tonight",
            "rec.tonight.loading": "Analyzing tonight's EPG based on your history… (the LLM may take a few tens of seconds)",
            "rec.tonight.refresh": "↻ Refresh",
            "rec.tonight.error": "Failed to produce the selection: {0}",
            "rec.tonight.empty": "Nothing interesting tonight in the EPG (per your filters).",
            "rec.tonight.fromCache": "from cache",
            "rec.tonight.watchLive": "Watch live",
            "rec.tonight.watch": "Watch",
            "rec.tonight.watchLib": "Watch (library)",
            // Prefix of the reason, "Watch tonight" section ONLY: record
            // recommendations (.strm, series/films sections) carry the 🤖
            // emoji alone.
            "rec.tonight.why": "🤖 Why tonight: ",
            "rec.type.upcoming": "Upcoming",
            "rec.type.recording": "Available · Recording",
            "rec.type.library": "Available · Library",
            "rec.type.aired": "Aired",
            "rec.type.watched": "Already watched"
        }
    };

    // ------------------------------------------------------------------
    //  Détection de langue
    // ------------------------------------------------------------------
    var lang = null;

    // Data-driven (v1.16.0, plan P3) : itère les clés de STRINGS (ordre
    // d'insertion : fr, en, puis les langues ajoutées par l'overlay) et rend
    // la première dont le préfixe de locale correspond. « fr-CA » → fr,
    // « es-ES » → es si la langue a été chargée depuis l'overlay (sinon la
    // clé « es » n'existe pas et l'itération retombe sur « en »). Tout ce
    // qui ne correspond à aucune clé -> "en" (repli anglais, inchangé).
    function pickLang(loc) {
        loc = String(loc || "").toLowerCase();
        var keys = Object.keys(STRINGS);
        for (var i = 0; i < keys.length; i++) {
            var k = String(keys[i] || "").toLowerCase();
            if (k && loc.indexOf(k) === 0) return keys[i];
        }
        return "en";
    }

    // ------------------------------------------------------------------
    //  Overlay de traductions communautaires (v1.16.0, plan P3)
    // ------------------------------------------------------------------
    // GET /Plugins/LLMAI/I18n sert les tranches web du fichier administré
    // LLM_AI_i18n.json (chargeur I18nOverlay.cs). La validation est ICI, par
    // clé, au merge (miroir exact des règles 4-5 du kit validate_i18n.js) :
    // multiset des placeholders {n} (trié, joint ',') et multiset des
    // balises HTML (/<\/?[a-zA-Z][^>]*>/g, trié, joint '|') comparés à
    // l'EN NATIF (la référence de contrat — jamais à un patch qui précède).
    function warnOverlay(langKey, key, msg) {
        try { console.warn("[LLM_AI i18n] [" + langKey + "] " + key + " — " + msg); }
        catch (e) { /* la console ne doit jamais casser l'i18n */ }
    }
    function phSig(s) {
        var m = String(s || "").match(/\{\d+\}/g);
        return m ? m.slice().sort().join(",") : "";
    }
    function tagSig(s) {
        var m = String(s || "").match(/<\/?[a-zA-Z][^>]*>/g);
        return m ? m.slice().sort().join("|") : "";
    }
    function copyDict(d) {
        var out = {}, keys = Object.keys(d), i;
        for (i = 0; i < keys.length; i++) out[keys[i]] = d[keys[i]];
        return out;
    }

    // Fetch de l'overlay — API emby authentifiée : chemin principal
    // ApiClient.ajax (global du dashboard ; il attache le jeton X-Emby-Token
    // via setAuthorizationInfoIntoRequest — constat terrain 2026-10-04 : un
    // fetch NU de /Plugins/LLMAI/I18n reçoit 401, l'endpoint exigeant le jeton
    // contrairement aux pages web/ConfigurationPage ; l'overlay ne fusionnait
    // donc JAMAIS dans le navigateur et une langue générée ne s'affichait
    // pas — fail-open silencieux = panne invisible, d'où le console.warn de
    // diagnostic ci-dessous). Sémantiques d'ApiClient (fetchhelper 4.10) :
    // 2xx → Response BRUTE (r.json() à l'appel, pattern des appels ?base /
    // ?missing de config.js), ≥ 400 → REJET de la Response (r.status lu au
    // catch). Cache-buster ?v= par init : la réponse de l'endpoint ne porte
    // AUCUN en-tête de fraîcheur et l'overlay est éditable live (le serveur
    // re-scanne à chaque GET — même garantie que l'ancien no-store).
    // Repli fetch nu si ApiClient est absent (contexte hors dashboard,
    // harnais) — 404 (plugin < v1.16.0, endpoint absent) et hôte injoignable
    // restent silencieux : natif inchangé (fail-open symétrique du chargeur).
    function warnFetch(status) {
        try { console.warn("[LLM_AI i18n] overlay non chargé — HTTP " + status
            + " sur GET /Plugins/LLMAI/I18n (jeton absent ?)"); }
        catch (e) { /* la console ne doit jamais casser l'i18n */ }
    }
    function payloadOrNull(payload) {
        return (payload && typeof payload === "object"
                && !Array.isArray(payload)) ? payload : null;
    }
    function fetchOverlayWeb() {
        try {
            if (typeof ApiClient !== "undefined" && ApiClient
                    && typeof ApiClient.ajax === "function"
                    && typeof ApiClient.getUrl === "function") {
                return ApiClient.ajax({
                    url: ApiClient.getUrl("Plugins/LLMAI/I18n",
                        { v: String(Date.now()) }),
                    type: "GET"
                })
                    .then(function (r) { return r.json(); })
                    .then(payloadOrNull)
                    .catch(function (r) {
                        if (r && r.status && r.status !== 404) warnFetch(r.status);
                        return null;
                    });
            }
        } catch (e) { /* repli fetch nu ci-dessous */ }
        try {
            if (typeof fetch !== "function") return Promise.resolve(null);
            return fetch("/Plugins/LLMAI/I18n", { cache: "no-store" })
                .then(function (r) {
                    if (r && r.ok) return r.json().then(payloadOrNull);
                    if (r && r.status && r.status !== 404) warnFetch(r.status);
                    return null;
                })
                .catch(function () { return null; });
        } catch (e) { return Promise.resolve(null); }
    }

    // Merge des tranches web dans STRINGS : patch fr/en (remplacement par
    // clé) + ajout de langues nouvelles. La référence de validation est la
    // photo de l'EN natif prise au début du merge (un patch "en" qui
    // précéderait un autre langage dans le payload ne redéfinit PAS le
    // contrat — miroir du comportement chargeur C#, où patch-en s'applique
    // à l'étage de repli sans changer les règles). Une langue créée n'est
    // rattachée à STRINGS que si au moins une clé a survécu (un dict vide
    // n'a pas de valeur ni pour lookup ni pour pickLang).
    function mergeOverlayWeb(payload) {
        if (!payload) return;
        var enNative = (STRINGS.en && typeof STRINGS.en === "object")
            ? copyDict(STRINGS.en) : {};
        var langs = Object.keys(payload), li;
        for (li = 0; li < langs.length; li++) {
            var lk = String(langs[li] || "");
            if (!lk) continue;
            var sect = payload[lk];
            var web = (sect && typeof sect === "object" && !Array.isArray(sect))
                ? sect.web : null;
            if (!web || typeof web !== "object" || Array.isArray(web)) continue;

            var dict = (STRINGS[lk] && typeof STRINGS[lk] === "object") ? STRINGS[lk] : null;
            var created = dict === null;
            if (created) dict = {};

            var keys = Object.keys(web), landed = 0, skipped = 0, i;
            for (i = 0; i < keys.length; i++) {
                var k = String(keys[i]), v = web[k];
                if (typeof v !== "string" || v.length === 0) {
                    warnOverlay(lk, k, "valeur non-textuelle ou vide ignorée, clé sautée");
                    skipped++; continue;
                }
                var ref = enNative[k];
                if (typeof ref !== "string") {
                    warnOverlay(lk, k, "clé inconnue du dictionnaire EN natif (glissement ou clé nouvelle ?), clé sautée");
                    skipped++; continue;
                }
                if (phSig(ref) !== phSig(v)) {
                    warnOverlay(lk, k, "placeholders divergents (EN {" + phSig(ref)
                        + "} vs soumis {" + phSig(v) + "}), clé sautée");
                    skipped++; continue;
                }
                if (tagSig(ref) !== tagSig(v)) {
                    warnOverlay(lk, k, "balises HTML divergentes (EN <" + tagSig(ref)
                        + "> vs soumis <" + tagSig(v) + ">), clé sautée");
                    skipped++; continue;
                }
                dict[k] = v;
                landed++;
            }
            if (created && landed === 0) continue;
            if (created) STRINGS[lk] = dict;
            try {
                console.info("[LLM_AI i18n] overlay « " + lk + " » fusionné — "
                    + landed + " clés web" + (skipped ? " (" + skipped + " sautées)" : ""));
            } catch (e) { /* console.info indisponible */ }
        }
    }

    // Détecte la localeEmby/navigateur : promesse TOUJOURS résolue (jamais
    // rejetée — l'i18n ne doit pas casser l'UI). globalize.getCurrentLocale()
    // (source de vérité Emby) via Emby.importModule, repli navigator.language,
    // repli "".
    function detectLocale() {
        return new Promise(function (resolve) {
            try {
                if (typeof Emby !== "undefined" && Emby.importModule) {
                    Emby.importModule("./modules/common/globalize.js").then(function (g) {
                        // Emby.importModule peut résoudre soit le default du
                        // module AMD, soit le namespace {default:…}. On gère les
                        // deux pour que getCurrentLocale() (langue d'affichage
                        // Emby, ex. fr-CA) soit bien trouvée.
                        var gl = g && (g.default || g);
                        var loc = (gl && typeof gl.getCurrentLocale === "function")
                            ? gl.getCurrentLocale() : "";
                        resolve(loc || (navigator.language || ""));
                    }, function () { resolve(navigator.language || ""); });
                } else {
                    resolve(navigator.language || "");
                }
            } catch (e) { resolve(navigator.language || ""); }
        });
    }

    var initPromise = null;

    // Résout la langue courante (une seule fois) : locale + overlay en
    // parallèle, merge AVANT pickLang (les langues ajoutées par l'overlay
    // doivent être visibles du data-driven pickLang), donc translateView —
    // appelé par les pages après init() — voit toujours les chaînes finies.
    function init() {
        if (lang) return Promise.resolve(lang);
        if (initPromise) return initPromise;      // double init concurrent → même promesse
        initPromise = new Promise(function (resolve) {
            var done = function (l) { lang = l; resolve(l); };
            var fallback = function () { done(pickLang(navigator.language || "")); };
            Promise.all([detectLocale(), fetchOverlayWeb()])
                .then(function (results) {
                    mergeOverlayWeb(results[1]);
                    done(pickLang(results[0]));
                }, fallback);
        });
        return initPromise;
    }

    // ------------------------------------------------------------------
    //  Traduction
    // ------------------------------------------------------------------
    function lookup(key) {
        var d = STRINGS[lang] || STRINGS.en;
        if (d && Object.prototype.hasOwnProperty.call(d, key)) return d[key];
        // Repli anglais puis clé brute.
        if (STRINGS.en && Object.prototype.hasOwnProperty.call(STRINGS.en, key)) return STRINGS.en[key];
        return key;
    }

    // t("key") -> texte. t("key", a0, a1) -> substitue {0}, {1}, …
    function t(key) {
        var s = lookup(key);
        if (arguments.length > 1) {
            for (var i = 1; i < arguments.length; i++) {
                s = s.split("{" + (i - 1) + "}").join(arguments[i]);
            }
        }
        return s;
    }

    // ------------------------------------------------------------------
    //  Traduction du DOM
    // ------------------------------------------------------------------
    // Met à jour le label rendu d'un composant emby (emby-input / emby-select /
    // emby-textarea). Ces composants lisent l'attribut « label » à l'upgrade et
    // ne réagissent PAS à un setAttribute tardif (pas d'attributeChangedCallback)
    // — il faut appeler leur setter ou cibler l'élément label qu'ils ont créé.
    function applyLabel(el, text) {
        try {
            // emby-input : méthode label(text) qui fait labelElement.innerHTML = text.
            if (typeof el.label === "function" && el.labelElement) { el.label(text); return; }
            // emby-select : méthode setLabel(text) qui cible .selectLabelText.
            if (typeof el.setLabel === "function") { el.setLabel(text); return; }
        } catch (e) { /* repli ci-dessous */ }
        // emby-textarea : pas de setter ; mettre à jour le labeltext rendu s'il
        // existe (le textarea doit être dans un <label>).
        var lbl = el.closest ? el.closest("label") : null;
        if (lbl) {
            var lt = lbl.querySelector(".emby-textarea-labeltext");
            if (lt) { lt.innerHTML = text; return; }
        }
        // Repli générique : écrire l'attribut (utile si le composant n'est pas
        // encore upgradé — il lira la valeur traduite à l'upgrade).
        el.setAttribute("label", text);
    }

    function translateView(view) {
        if (!view) return;
        var i, el, key, list;

        list = view.querySelectorAll("[data-i18n]");
        for (i = 0; i < list.length; i++) { list[i].textContent = t(list[i].getAttribute("data-i18n")); }

        list = view.querySelectorAll("[data-i18n-html]");
        for (i = 0; i < list.length; i++) { list[i].innerHTML = t(list[i].getAttribute("data-i18n-html")); }

        list = view.querySelectorAll("[data-i18n-ph]");
        for (i = 0; i < list.length; i++) { list[i].setAttribute("placeholder", t(list[i].getAttribute("data-i18n-ph"))); }

        list = view.querySelectorAll("[data-i18n-label]");
        for (i = 0; i < list.length; i++) { applyLabel(list[i], t(list[i].getAttribute("data-i18n-label"))); }

        list = view.querySelectorAll("[data-i18n-title]");
        for (i = 0; i < list.length; i++) { list[i].setAttribute("title", t(list[i].getAttribute("data-i18n-title"))); }
    }

    return {
        init: init,
        t: t,
        translateView: translateView,
        getLang: function () { return lang; }
    };
});