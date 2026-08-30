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
define([], function () {
    "use strict";

    // ------------------------------------------------------------------
    //  Dictionnaires FR + EN (extensible : ajouter une locale ci-dessous et
    //  un préfixe dans pickLang).
    // ------------------------------------------------------------------
    var STRINGS = {
        fr: {
            // -- Page de configuration : titres / descriptions / labels ----
            "cfg.title": "LLM AI — Configuration",
            "cfg.backends.h": "Serveurs LLM (repli par priorité)",
            "cfg.backends.desc": "Ajoutez un ou plusieurs serveurs Ollama. Chaque LLM a une <b>priorité</b> (1 = la plus haute, essayée en premier) et un drapeau <b>activé</b>. Si un serveur est indisponible, la tâche bascule automatiquement sur le prochain LLM activé selon la priorité.",
            "cfg.backends.add": "+ Ajouter un LLM",
            "cfg.embyurl.label": "URL publique Emby",
            "cfg.embyurl.desc": "URL d'accès Emby (ex. http://192.168.11.2:8096) — sert à construire les liens d'images retournés au LLM.",
            "cfg.relang.label": "Langue de réponse du LLM",
            "cfg.relang.auto": "Auto (langue de l'interface — défaut)",
            "cfg.relang.desc": "Langue des <b>métadonnées</b> et du texte en prose de l'LLM : <b>raisons des recommandations</b>, <b>rapport d'audit</b>, scaffolding des cartes .strm et <b>synopsis TMDB</b> (demandé dans cette langue, repli en-US, traduction LLM en dernier recours). « Auto » = suit la langue d'affichage Emby. Les titres de films/séries, noms de chaînes et champs JSON techniques restent inchangés.",
            "cfg.filters.h": "Filtres de l'analyse EPG (tâche recommandations)",
            "cfg.filters.desc": "Filtres appliqués aux programmes EPG avant envoi au LLM (get_emby_info epg_series/epg_movies). <b>Chaines</b> et <b>Genres</b> sont partagés par les deux tâches (séries et films) ; case vide = pas de filtre sur cette dimension. Les <b>flags Kids/News/Sports</b> sont <i>opt-in</i> par catégorie : vide = fiction seulement ; cochez un flag pour AJOUTER ces programmes. Spécifique à la tâche de recommandation.",
            "cfg.channels.label": "Chaines",
            "cfg.channels.search": "Rechercher une chaine",
            "cfg.channels.ph": "Filtrer la liste…",
            "cfg.channels.loading": "Chargement des chaines…",
            "cfg.channels.desc": "Chaines à garder. Vide = toutes les chaines.",
            "cfg.genres.label": "Genres",
            "cfg.genres.loading": "Chargement des genres…",
            "cfg.genres.desc": "Genres de l'EPG (collectés depuis les programmes à venir) à garder. Vide = tous les genres.",
            "cfg.flags.series": "Flags Séries (opt-in)",
            "cfg.flags.series.desc": "Flags à AJOUTER aux séries de fiction. Vide = fiction seulement (kids/news/sports exclus). Cochez Kids/News/Sports pour les inclure.",
            "cfg.flags.movies": "Flags Films (opt-in)",
            "cfg.flags.movies.desc": "Flags à AJOUTER aux films de fiction. Vide = fiction seulement. Cochez Kids/News/Sports pour les inclure.",
            "cfg.maxseries.label": "Plafond séries (par appel)",
            "cfg.maxseries.desc": "Max de séries soumises au LLM (après pré-tri). Défaut 40.",
            "cfg.maxmovies.label": "Plafond films (par appel)",
            "cfg.maxmovies.desc": "Max de films soumis au LLM (après pré-tri). Défaut 30.",
            "cfg.apikeys.h": "Clés API — services externes",
            "cfg.apikeys.desc": "Clés pour les outils d'enrichissement internet. Un champ vide désactive l'outil correspondant (il n'est pas exposé au LLM).",
            "cfg.tmdb.label": "Clé API TMDB",
            "cfg.tmdb.ph": "clé themoviedb.org",
            "cfg.tmdb.desc": "Clé API TMDB pour l'outil tmdb_lookup (enrichissement synopsis/statut). Laisser vide pour désactiver l'outil.",
            "cfg.tmdblang.label": "Langue TMDB",
            "cfg.tmdblang.desc": "Repli legacy pour la langue TMDB quand « Langue de réponse du LLM » est sur Auto et que la langue d'affichage Emby est illisible. Quand une langue de réponse est choisie, c'est elle qui pilote TMDB (repli en-US, traduction LLM en dernier recours). Ex. fr-FR, en-US.",
            "cfg.tvdb.label": "Clé API TheTVDB (tvdb_search)",
            "cfg.tvdb.ph": "clé api4.thetvdb.com",
            "cfg.tvdb.desc": "Clé API TheTVDB.com (v4) pour l'outil tvdb_search (enrichissement séries, synopsis FR en priorité). Repli sur la variable d'environnement TVDB_API_KEY. Vide = désactivé.",
            "cfg.ollama.label": "Clé API Ollama (cloud / web_search / web_fetch)",
            "cfg.ollama.desc": "Clé ollama.com — sert pour le backend <b>Ollama cloud</b> ET les outils web_search/web_fetch. Repli sur la variable d'environnement OLLAMA_API_KEY. Vide = Ollama cloud désactivé.",
            "cfg.searxng.label": "URL SearXNG (recherche web — optionnel)",
            "cfg.searxng.desc": "Instance SearXNG auto-hébergée (méta-moteur). Si renseigné, l'outil <b>web_search</b> l'utilise en priorité (gratuit, sans quota, résultats JSON). L'outil interroge <c>{url}/search?q=…&amp;format=json</c> — le format JSON doit être activé dans les réglages SearXNG (<c>search.formats</c>). Optionnel : un utilisateur de la communauté n'a pas besoin d'installer SearXNG (repli automatique sur Ollama cloud). Vide = SearXNG désactivé.",
            "cfg.webfetch.label": "web_fetch direct (auto-hébergé, sans clé)",
            "cfg.webfetch.desc": "Récupère les pages web directement côté plugin (HttpClient) et extrait localement le contenu structuré (titre, métadonnées, <b>JSON-LD schema.org</b>, texte, titres, tableaux) — aucun quota cloud, aucune clé requise. Repli automatique sur Ollama cloud en cas de blocage anti-bot (si une clé est renseignée). Décochez pour utiliser uniquement Ollama cloud (clé requise). Activé par défaut — fonctionne pour tout utilisateur de la communauté.",
            "cfg.gemini.label": "Clé API Google Gemini",
            "cfg.gemini.desc": "Clé Google AI Studio pour le backend <b>Gemini</b> (generativelanguage.googleapis.com). Repli sur la variable d'environnement GEMINI_API_KEY. Vide = Gemini désactivé.",
            "cfg.showbizz.label": "URL nouveautés Showbizz",
            "cfg.showbizz.desc": "Active l'outil showbizz_new_releases. La page d'accueil + la liste saisonnière sont scrapées par défaut ; cette URL (si différente) est ajoutée comme source supplémentaire. Laisser vide désactive l'outil.",
            "cfg.showbizz.regex.label": "Regex extraction Showbizz (optionnel)",
            "cfg.showbizz.regex.ph": "Vide = extraction auto (blocs emissions / Saison 1)",
            "cfg.showbizz.regex.desc": "Vide = extraction automatique des nouveautés « Saison 1 » (portage du scraper showbizz_scraper.php : blocs &lt;a href=\"/emissions/...\"&gt; + titre &lt;h3&gt;/&lt;h4&gt; + date « Dès le »). Renseigné = regex .NET override (groupes « title », « url »/« date » optionnels).",
            "cfg.rag.label": "RAG Directives (system prompt)",
            "cfg.rag.desc": "Prompt système envoyé au LLM (role: system) à chaque appel.",
            "cfg.debug.label": "Debug verbose (journalisation intégrale de l'agent)",
            "cfg.debug.desc": "Quand activé, le journal Emby contient le system prompt complet, le prompt utilisateur, la réponse complète de chaque itération et chaque résultat d'outil. À activer ponctuellement pour comprendre ce que voit le LLM (désactivé par défaut).",
            "cfg.sched.series.label": "Schedule Task (Séries) — planification + prompt",
            "cfg.sched.series.ph": "Daily 03:00 | Recommande des enregistrements de SÉRIES : …",
            "cfg.sched.series.desc": "Format : « &lt;planification&gt; | &lt;prompt de tâche SÉRIES&gt; ». Planification : Daily HH:MM, Hourly, Weekly &lt;Day&gt; HH:MM, Interval &lt;heures&gt;. La partie après le « | » est le prompt utilisateur du <b>run SÉRIES</b> (étapes 1 + 2 : premieres S01E01 + nouvelles saisons). Exécuté comme un premier run agent indépendant. Déclenchable manuellement dans « Tâches planifiées ».",
            "cfg.sched.movies.label": "Schedule Task (Films) — prompt du run films",
            "cfg.sched.movies.ph": "Recommande des enregistrements de FILMS : …",
            "cfg.sched.movies.desc": "Prompt du <b>run FILMS</b> (étape 3 : films à venir absents de la biblio, via get_emby_info action=epg_movies). Pas de planification ici — elle vient du champ ci-dessus. Exécuté comme un second run agent indépendant, puis fusionné avec les séries (la page Recommandations affiche deux sections). Vide = pas de run films (séries seulement).",
            "cfg.dropped.label": "Drop list — titres à exclure des recommandations",
            "cfg.dropped.ph": "Star Trek\nCastle\nUn titre par ligne",
            "cfg.dropped.desc": "Titres exclus des prochaines exécutions : la tâche les retire de la liste envoyée au LLM (épuration en amont) — ils ne seront plus recommandés. Un titre par ligne. Alimenté aussi par le bouton « Oublier » de la page Recommandations.",

            // -- Sous-section « Ce soir » (recommandation personnalisée) ---
            "cfg.tonight.h": "À regarder ce soir (recommandation personnalisée par usager)",
            "cfg.tonight.desc": "Section « À regarder ce soir » de la page Recommandations : analyse l'historique de visionnage de l'usager et croise avec l'EPG du soir pour recommander quoi regarder maintenant. Appel à la demande (endpoint plugin), cache par usager.",
            "cfg.tonight.enabled": "Activer la section « À regarder ce soir »",
            "cfg.tonight.genretag.flag": "Étiqueter les recos « à regarder ce soir » avec le genre « AI Tonight »",
            "cfg.tonight.genretag.desc": "Option opt-in. Ajoute le genre AI Tonight aux items Emby recommandés (enregistrements non visionnés + bibliothèque) pour les retrouver en filtrant sur ce genre dans n'importe quel client Emby. Une tâche planifiée (Nettoyage genre AI Tonight, 3 h du matin) retire le genre chaque jour ; les runs « ce soir » suivants le réajoutent sur les recos pertinentes. Modifie les métadonnées réelles des items (tableau Genres) — un refresh peut l'effacer (réajouté au prochain run). Indépendant de la bibliothèque .strm (genre AI Suggestion, nettoyage séparé).",
            "cfg.tonight.collection.flag": "Regrouper les recos « à regarder ce soir » dans une collection « AI Tonight »",
            "cfg.tonight.collection.desc": "Option opt-in, indépendante du genre ci-dessus (les deux peuvent cohabiter). Maintient une collection Emby « AI Tonight » regroupant les items recommandés (enregistrements non visionnés + bibliothèque) — l'usager la parcourt comme n'importe quelle collection dans n'importe quel client Emby. Non destructive : les items sont référencés (regroupés), jamais copiés ni déplacés ; lire un membre joue le vrai item. La collection agrège des items inter-bibliothèques (enregistrements + films/séries possédés). La tâche planifiée (Nettoyage genre AI Tonight, 3 h du matin) vide aussi la collection chaque jour (coquille conservée, re-remplie au prochain run).",
            "cfg.tonight.window.start": "Début de fenêtre (HH:mm)",
            "cfg.tonight.window.end": "Fin de fenêtre (HH:mm)",
            "cfg.tonight.window.desc": "Fenêtre temporelle de l'EPG interrogée (ex. 18:00 → 23:59). Début vide = maintenant ; fin vide = 23:59.",
            "cfg.tonight.prompt": "Prompt « ce soir »",
            "cfg.tonight.prompt.desc": "Template du prompt envoyé au LLM. Le profil de goût (historique récent de l'usager) y est injecté à l'exécution.",
            "cfg.tonight.batch": "Plafond programmes (par appel)",
            "cfg.tonight.batch.desc": "Max de programmes de la soirée soumis au LLM (après pré-tri par pertinence). Défaut 10.",
            "cfg.tonight.cache": "Cache (heures)",
            "cfg.tonight.cache.desc": "Durée de validité du cache par usager. 0 = pas de cache (run à chaque ouverture). Défaut 4.",
            "cfg.tonight.recDays": "Enregistrements (jours)",
            "cfg.tonight.recDays.desc": "Fenêtre de recherche des enregistrements récents non visionnés (candidats « à regarder ce soir »). Défaut 7.",
            "cfg.tonight.minRec": "Min recommandations",
            "cfg.tonight.minRec.desc": "Si l'EPG + les enregistrements donnent moins de recommandations, complète avec des titres non visionnés de la bibliothèque. Défaut 3.",
            "cfg.autoprog.h": "Auto-programmation & popup au login",
            "cfg.autoprog.desc": "Les clients natifs Android / Android TV ne rendent pas les pages plugin HTML : les recommandations ne sont visibles que sur la page web. L'auto-programmation crée les timers Emby des recos à enregistrer → elles ressortent dans le guide EPG natif (badge d'enregistrement) sur tous les clients. Le popup au login signale ce soir ce que l'usager peut regarder (bibliothèque / enregistrements).",
            "cfg.autoprog.flag": "Auto-programmer les recommandations (créer les timers d'enregistrement)",
            "cfg.autoprog.flag.desc": "Option opt-in (décochée par défaut). Si cochée, après chaque run (tâche planifiée ET login), les recommandations à enregistrer (programmes EPG à venir, non déjà possédées, non déjà programmées, hors drop list) sont programmées en enregistrement (SeriesTimer pour une série, Timer unique pour un film). Aucune programmation tant que décochée. L'utilisateur peut annuler un timer indésirable dans Emby.",
            "cfg.aibadge.flag": "Badge « AI » sur les images EPG des suggestions à enregistrer",
            "cfg.ownedbadge.flag": "Badge jaune « déjà possédé » sur les images EPG des émissions de la bibliothèque",
            "cfg.ownedbadge.desc": "Pastille jaune (sans icône) sur l'image des programmes EPG dont la série ou le film figure déjà dans la bibliothèque — même rapprochement par nom que l'exclusion biblio des outils EPG (articles FR/EN retirés, casse ignorée). L'usager sait dans le guide qu'il n'a pas intérêt à enregistrer. Même mécanisme non destructif que le badge « AI » (overlay au service, artwork jamais modifié) ; clé de cache distincte. NB : une nouvelle saison d'une série possédée porte aussi le jaune (c'est le même nom de série).",
            "cfg.aibadge.desc": "Pastille verte + icône étincelle (sans texte, multilingue) en haut à droite de l'image des programmes suggérés par la tâche planifiée — visible dans le guide EPG natif, sur tous les clients. Non destructif : overlay au moment du service, l'artwork stocké n'est jamais modifié (l'enregistrement importé garde l'image d'origine — le badge disparaît une fois l'émission enregistrée) et un refresh EPG est sans effet. Chaque run remplace la sélection badgée ; les diffusions passées s'auto-expirent.",
            "cfg.loginpopup.flag": "Popup au login (suggestions « à regarder ce soir »)",
            "cfg.loginpopup.seconds": "Durée du toast (secondes)",
            "cfg.loginpopup.desc": "Indépendant de l'auto-programmation : le popup liste ce soir ce que l'usager peut regarder (enregistrements non visionnés, bibliothèque). Toast sur le client qui se connecte (si DisplayMessage supporté) + notification cloche persistante (deep-link) en repli. La cloche reste même si la session ferme avant la fin du run LLM (~30–60 s) ou si le client ne supporte pas le toast.",
            "cfg.strmlib.h": "Bibliothèque .strm des recommandations",
            "cfg.strmlib.desc": "Alternative manuelle à l'auto-programmation : après chaque run, le plugin écrit une carte .strm+.nfo+poster par recommandation à enregistrer (programmes EPG à venir, non possédés) dans une bibliothèque Emby dédiée. L'usager parcourt la bibliothèque ; lire une carte crée l'enregistrement puis affiche un clip de confirmation. Créez d'abord dans Emby une bibliothèque de type Films (ou Contenu mixte) pointant vers un dossier vide, puis renseignez son nom exact ici.",
            "cfg.strmlib.flag": "Activer la bibliothèque .strm des recommandations",
            "cfg.strmlib.name": "Nom de la bibliothèque Emby dédiée",
            "cfg.strmlib.name.desc": "Nom exact (casse ignorée) de la bibliothèque Emby où écrire les cartes. Indépendant de l'auto-programmation (les deux peuvent cohabiter : le dedup évite les timers en double). Un jeton de sécurité est auto-généré au premier run pour protéger l'endpoint d'activation.",

            // -- Identification des enregistrements orphelins (tâche planifiée 04 h) ---
            "cfg.orphan.h": "Identification des enregistrements orphelins",
            "cfg.orphan.desc": "Passe quotidienne (04 h) qui repère les enregistrements DVR <b>non identifiés</b> (sans id IMDb/TMDB — souvent des titres québécois absents du catalogue TMDB/TVDB), tente de les résoudre par <b>nettoyage du titre + recherche multilingue</b> (S1), puis par <b>proposition LLM d'un id IMDb validé via TMDB /find</b> (S2), puis par <b>recherche web SearXNG → id IMDb</b> (S3), et écrit l'id + métadonnées + affiche en <b>verrouillant le titre EPG</b> (jamais écrasé). Les irrésolus sont marqués pour revue. <b>Opt-in</b> : modifie des enregistrements.",
            "cfg.orphan.enabled": "Activer la tâche d'identification des orphelins",
            "cfg.orphan.dryrun": "Mode simulation (dry-run — aucune écriture)",
            "cfg.orphan.dryrun.desc": "Si cochée, la tâche <b>n'écrit rien</b> : elle logue seulement les orphelins trouvés et la résolution proposée (S1/S2/S3) + un bilan. Sert à valider la qualité des résolutions avant de basculer en application automatique. À garder cochée pour les premiers runs.",
            "cfg.orphan.searxng": "Étape S3 : recherche web (SearXNG) pour les titres introuvables",
            "cfg.orphan.searxng.desc": "Si cochée, après S1/S2 la tâche interroge <b>SearXNG</b> (cf. URL ci-dessus), extrait les ids <b>IMDb</b> des résultats, valide via TMDB + juge de synopsis. Résout les titres québécois paraphrasés qu'aucun catalogue ne connaît (ex. « L'histoire de Jean Seberg » → film « Seberg » 2019). Inopérant si ni SearXNG ni clé Ollama ne sont configurés.",
            "cfg.orphan.retry": "Retraiter les besoins-revues (retry)",
            "cfg.orphan.retry.desc": "Si cochée, les orphelins marqués <b>needs-review</b> sont retraités (au lieu d'être sautés) — pour y repasser S3 une fois SearXNG configuré. En cas de résolution, le tag devient <b>identified</b>. Les déjà-identifiés restent sautés.",

            // -- Audit santé (endpoint à la demande, agent system_audit) ---
            "cfg.audit.h": "Audit santé du serveur",
            "cfg.audit.desc": "Lance un agent LLM qui interroge l'outil <b>system_audit</b> (sessions, tâches planifiées, transcodage, disques, journaux, métriques hôte) et produit un <b>rapport Markdown</b> de santé : constats tagués par gravité + actions recommandées. Réservé aux administrateurs. La <b>remédiation</b> (arrêter une session, déclencher une tâche, notifier un usager) est désactivée par défaut.",
            "cfg.audit.enabled": "Activer l'endpoint d'audit (/Plugins/LLMAI/Audit)",
            "cfg.audit.remediation": "Autoriser la remédiation (stop_session, trigger_task, send_message)",
            "cfg.audit.remediation.desc": "Option <b>opt-in</b> (décochée par défaut). Si cochée, le LLM peut <b>exécuter</b> des actions de remédiation pendant l'audit (arrêter une lecture, déclencher une tâche, envoyer un message). Tant que décochée, ces actions renvoient une erreur et le LLM se contente de les <b>recommander</b> dans le rapport. Le prompt d'audit demande de toute façon au LLM de ne jamais agir sans demande explicite.",
            "cfg.audit.mode": "Mode d'exécution de l'audit",
            "cfg.audit.mode.single": "Boucle agent (adaptatif — modèle costaud / cloud)",
            "cfg.audit.mode.deterministic": "Déterministe + synthèse (modèle local / gemma4)",
            "cfg.audit.mode.desc": "<b>single</b> : l'LLM appelle lui-même l'outil system_audit de façon adaptative (peut creuser un journal suite à un constat). Convient à un modèle puissant (cloud). <b>deterministic</b> : le plugin rassemble lui-même toutes les sondes (zéro LLM) puis un seul passage LLM <b>sans outils</b> synthétise le rapport — conçu pour un modèle local/modeste (ex. gemma4), qui synthétise bien du texte fourni mais gère mal l'orchestration multi-outils. Note : la <b>remédiation exécutable</b> n'est possible qu'en mode <b>single</b> (avec le flag de remédiation activé) ; en mode déterministe elle reste rapport-only.",
            "cfg.audit.focus": "Focus optionnel de l'audit",
            "cfg.audit.focus.desc": "Orientation libre de l'audit (ex. un domaine à inspecter, ou une demande explicite de remédiation). Laisser vide pour un audit complet.",
            "cfg.audit.run": "Lancer l'audit santé",
            "cfg.audit.running": "Audit en cours… (l'agent interroge le serveur, ~30–60 s)",
            "cfg.audit.done": "Audit terminé",
            "cfg.audit.disabled": "Audit désactivé en configuration.",
            "cfg.chat.h": "Chat avec l'assistant IA",
            "cfg.chat.desc": "Conversation interactive multi-tours avec l'agent LLM. Celui-ci dispose de <b>tous les outils déjà disponibles</b> (guide TV, bibliothèque, TMDB/TVDB, recherche web, Showbizz, audit système) et utilise les <b>priorités de backends LLM configurées</b> ci-dessus. L'historique est maintenu par la page ; la directive et la documentation des outils sont envoyées au LLM une seule fois, au début de la conversation. Réservé aux administrateurs.",
            "cfg.chat.enabled": "Activer le chat interactif (/Plugins/LLMAI/Chat)",
            "cfg.chat.input": "Votre message",
            "cfg.chat.placeholder": "ex. que peux-tu me dire sur ce qui passe ce soir ?",
            "cfg.chat.send": "Envoyer",
            "cfg.chat.clear": "Effacer la conversation",
            "cfg.chat.running": "L'assistant réfléchit… (il peut interroger vos outils, quelques dizaines de secondes)",
            "cfg.chat.disabled": "Chat désactivé en configuration.",
            "cfg.chat.you": "Vous",
            "cfg.chat.assistant": "Assistant IA",
            "cfg.chat.hint": "Posez une question à l'assistant — il interrogera vos données Emby (EPG, bibliothèque, santé du serveur) pour répondre.",
            "cfg.save": "Enregistrer",

            // -- config.js : chaînes dynamiques ----------------------------
            "cfg.wl.empty": "(aucun élément disponible)",
            "cfg.backend.provider.local": "Ollama local",
            "cfg.backend.provider.cloud": "Ollama cloud",
            "cfg.backend.provider.gemini": "Google Gemini",
            "cfg.backend.provider.label": "Provider",
            "cfg.backend.num": "LLM #{0}",
            "cfg.backend.remove": "Supprimer",
            "cfg.backend.url.label": "URL de base",
            "cfg.backend.model.label": "Modèle",
            "cfg.backend.prio.label": "Priorité",
            "cfg.backend.enabled": "Activé",
            "cfg.alert.saved": "Configuration enregistrée.",
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
            "rec.alert.cfgLoad": "Impossible de charger la configuration du plugin.",

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
            "rec.type.upcoming": "À venir",
            "rec.type.recording": "Disponible · Enregistrement",
            "rec.type.library": "Disponible · Bibliothèque",
            "rec.type.aired": "Diffusé"
        },

        en: {
            "cfg.title": "LLM AI — Configuration",
            "cfg.backends.h": "LLM servers (priority fallback)",
            "cfg.backends.desc": "Add one or more Ollama servers. Each LLM has a <b>priority</b> (1 = highest, tried first) and an <b>enabled</b> flag. If a server is unavailable, the task automatically falls back to the next enabled LLM by priority.",
            "cfg.backends.add": "+ Add an LLM",
            "cfg.embyurl.label": "Emby public URL",
            "cfg.embyurl.desc": "Emby access URL (e.g. http://192.168.11.2:8096) — used to build the image links returned to the LLM.",
            "cfg.relang.label": "LLM response language",
            "cfg.relang.auto": "Auto (interface language — default)",
            "cfg.relang.desc": "Language of <b>metadata</b> and the LLM's prose: <b>recommendation reasons</b>, <b>audit report</b>, .strm card scaffolding and <b>TMDB synopsis</b> (requested in this language, en-US fallback, LLM translation as last resort). “Auto” = follows the Emby display language. Movie/series titles, channel names and technical JSON fields stay unchanged.",
            "cfg.filters.h": "EPG analysis filters (recommendations task)",
            "cfg.filters.desc": "Filters applied to EPG programs before sending them to the LLM (get_emby_info epg_series/epg_movies). <b>Channels</b> and <b>Genres</b> are shared by both tasks (series and movies); an empty box = no filter on that dimension. The <b>Kids/News/Sports flags</b> are <i>opt-in</i> per category: empty = fiction only; check a flag to ADD these programs. Specific to the recommendation task.",
            "cfg.channels.label": "Channels",
            "cfg.channels.search": "Search a channel",
            "cfg.channels.ph": "Filter the list…",
            "cfg.channels.loading": "Loading channels…",
            "cfg.channels.desc": "Channels to keep. Empty = all channels.",
            "cfg.genres.label": "Genres",
            "cfg.genres.loading": "Loading genres…",
            "cfg.genres.desc": "EPG genres (collected from upcoming programs) to keep. Empty = all genres.",
            "cfg.flags.series": "Series flags (opt-in)",
            "cfg.flags.series.desc": "Flags to ADD to fiction series. Empty = fiction only (kids/news/sports excluded). Check Kids/News/Sports to include them.",
            "cfg.flags.movies": "Movie flags (opt-in)",
            "cfg.flags.movies.desc": "Flags to ADD to fiction movies. Empty = fiction only. Check Kids/News/Sports to include them.",
            "cfg.maxseries.label": "Series cap (per call)",
            "cfg.maxseries.desc": "Max series submitted to the LLM (after pre-sorting). Default 40.",
            "cfg.maxmovies.label": "Movies cap (per call)",
            "cfg.maxmovies.desc": "Max movies submitted to the LLM (after pre-sorting). Default 30.",
            "cfg.apikeys.h": "API keys — external services",
            "cfg.apikeys.desc": "Keys for the internet enrichment tools. An empty field disables the corresponding tool (it is not exposed to the LLM).",
            "cfg.tmdb.label": "TMDB API key",
            "cfg.tmdb.ph": "themoviedb.org key",
            "cfg.tmdb.desc": "TMDB API key for the tmdb_lookup tool (synopsis/status enrichment). Leave empty to disable the tool.",
            "cfg.tmdblang.label": "TMDB language",
            "cfg.tmdblang.desc": "Legacy fallback for the TMDB language when \"LLM response language\" is Auto and the Emby display language can't be read. When a response language is set, it drives TMDB (en-US fallback, LLM translation as last resort). E.g. fr-FR, en-US.",
            "cfg.tvdb.label": "TheTVDB API key (tvdb_search)",
            "cfg.tvdb.ph": "api4.thetvdb.com key",
            "cfg.tvdb.desc": "TheTVDB.com (v4) API key for the tvdb_search tool (series enrichment, FR synopsis first). Falls back to the TVDB_API_KEY environment variable. Empty = disabled.",
            "cfg.ollama.label": "Ollama API key (cloud / web_search / web_fetch)",
            "cfg.ollama.desc": "ollama.com key — used for the <b>Ollama cloud</b> backend AND the web_search/web_fetch tools. Falls back to the OLLAMA_API_KEY environment variable. Empty = Ollama cloud disabled.",
            "cfg.searxng.label": "SearXNG URL (web search — optional)",
            "cfg.searxng.desc": "Self-hosted SearXNG instance (meta-engine). If set, the <b>web_search</b> tool uses it first (free, no quota, JSON results). The tool queries <c>{url}/search?q=…&amp;format=json</c> — the JSON format must be enabled in the SearXNG settings (<c>search.formats</c>). Optional: a community user does not need to install SearXNG (automatic fallback to Ollama cloud). Empty = SearXNG disabled.",
            "cfg.webfetch.label": "Direct web_fetch (self-hosted, no key)",
            "cfg.webfetch.desc": "Fetches web pages directly on the plugin side (HttpClient) and extracts structured content locally (title, metadata, <b>JSON-LD schema.org</b>, text, headings, tables) — no cloud quota, no key required. Automatic fallback to Ollama cloud on anti-bot blocking (if a key is set). Uncheck to use only Ollama cloud (key required). Enabled by default — works for any community user.",
            "cfg.gemini.label": "Google Gemini API key",
            "cfg.gemini.desc": "Google AI Studio key for the <b>Gemini</b> backend (generativelanguage.googleapis.com). Falls back to the GEMINI_API_KEY environment variable. Empty = Gemini disabled.",
            "cfg.showbizz.label": "Showbizz new releases URL",
            "cfg.showbizz.desc": "Enables the showbizz_new_releases tool. The home page + the seasonal list are scraped by default; this URL (if different) is added as an extra source. Leave empty to disable the tool.",
            "cfg.showbizz.regex.label": "Showbizz extraction regex (optional)",
            "cfg.showbizz.regex.ph": "Empty = auto extraction (emissions blocks / Saison 1)",
            "cfg.showbizz.regex.desc": "Empty = automatic extraction of \"Saison 1\" new releases (port of the showbizz_scraper.php scraper: &lt;a href=\"/emissions/...\"&gt; blocks + &lt;h3&gt;/&lt;h4&gt; title + \"Dès le\" date). Set = .NET regex override (\"title\", optional \"url\"/\"date\" groups).",
            "cfg.rag.label": "RAG Directives (system prompt)",
            "cfg.rag.desc": "System prompt sent to the LLM (role: system) on every call.",
            "cfg.debug.label": "Verbose debug (full agent logging)",
            "cfg.debug.desc": "When enabled, the Emby log contains the full system prompt, the user prompt, the complete response of each iteration and each tool result. Enable temporarily to understand what the LLM sees (disabled by default).",
            "cfg.sched.series.label": "Schedule Task (Series) — schedule + prompt",
            "cfg.sched.series.ph": "Daily 03:00 | Recommend SERIES recordings: …",
            "cfg.sched.series.desc": "Format: \"&lt;schedule&gt; | &lt;SERIES task prompt&gt;\". Schedule: Daily HH:MM, Hourly, Weekly &lt;Day&gt; HH:MM, Interval &lt;hours&gt;. The part after \"|\" is the user prompt of the <b>SERIES run</b> (steps 1 + 2: first S01E01 + new seasons). Run as an independent first agent run. Can be triggered manually in \"Scheduled Tasks\".",
            "cfg.sched.movies.label": "Schedule Task (Movies) — movies run prompt",
            "cfg.sched.movies.ph": "Recommend MOVIE recordings: …",
            "cfg.sched.movies.desc": "Prompt of the <b>MOVIES run</b> (step 3: upcoming movies absent from the library, via get_emby_info action=epg_movies). No schedule here — it comes from the field above. Run as a second independent agent run, then merged with the series (the Recommendations page shows two sections). Empty = no movies run (series only).",
            "cfg.dropped.label": "Drop list — titles to exclude from recommendations",
            "cfg.dropped.ph": "Star Trek\nCastle\nOne title per line",
            "cfg.dropped.desc": "Titles excluded from future runs: the task removes them from the list sent to the LLM (upstream cleanup) — they will no longer be recommended. One title per line. Also fed by the \"Forget\" button on the Recommendations page.",

            "cfg.tonight.h": "Watch tonight (per-user personalized recommendation)",
            "cfg.tonight.desc": "\"Watch tonight\" section of the Recommendations page: analyzes the user's watch history and crosses it with the evening's EPG to recommend what to watch now. On-demand call (plugin endpoint), per-user cache.",
            "cfg.tonight.enabled": "Enable the \"Watch tonight\" section",
            "cfg.tonight.genretag.flag": "Tag \"watch tonight\" recommendations with the \"AI Tonight\" genre",
            "cfg.tonight.genretag.desc": "Opt-in. Adds the AI Tonight genre to recommended Emby items (unwatched recordings + library) so you can find them by filtering on that genre in any Emby client. A scheduled task (AI Tonight genre cleanup, 3 AM) removes the genre daily; subsequent tonight runs re-add it on still-relevant recos. Mutates real item metadata (Genres array) — a refresh may drop it (re-added on the next run). Independent of the .strm library (AI Suggestion genre, separate cleanup).",
            "cfg.tonight.collection.flag": "Group \"watch tonight\" recommendations into an \"AI Tonight\" collection",
            "cfg.tonight.collection.desc": "Opt-in, independent of the genre above (both can coexist). Maintains an \"AI Tonight\" Emby collection grouping the recommended items (unwatched recordings + library) — browse it like any collection in any Emby client. Non-destructive: items are referenced (grouped), never copied or moved; playing a member plays the real item. The collection aggregates cross-library items (recordings + owned movies/series). The scheduled task (AI Tonight genre cleanup, 3 AM) also empties the collection daily (shell kept, refilled on the next run).",
            "cfg.tonight.window.start": "Window start (HH:mm)",
            "cfg.tonight.window.end": "Window end (HH:mm)",
            "cfg.tonight.window.desc": "EPG time window queried (e.g. 18:00 → 23:59). Empty start = now; empty end = 23:59.",
            "cfg.tonight.prompt": "Tonight prompt",
            "cfg.tonight.prompt.desc": "Prompt template sent to the LLM. The taste profile (user's recent history) is injected at runtime.",
            "cfg.tonight.batch": "Programs cap (per call)",
            "cfg.tonight.batch.desc": "Max evening programs submitted to the LLM (after relevance pre-sort). Default 10.",
            "cfg.tonight.cache": "Cache (hours)",
            "cfg.tonight.cache.desc": "Per-user cache validity duration. 0 = no cache (run on every open). Default 4.",
            "cfg.tonight.recDays": "Recordings (days)",
            "cfg.tonight.recDays.desc": "Lookback window for recent unwatched recordings (candidates for \"watch tonight\"). Default 7.",
            "cfg.tonight.minRec": "Min recommendations",
            "cfg.tonight.minRec.desc": "If EPG + recordings yield fewer recommendations, fill with unwatched library titles. Default 3.",
            "cfg.autoprog.h": "Auto-programming & login popup",
            "cfg.autoprog.desc": "Native Android / Android TV clients don't render plugin HTML pages: recommendations are only visible on the web page. Auto-programming creates the Emby timers for recommendations to record → they stand out in the native EPG guide (record badge) on every client. The login popup surfaces what to watch tonight (library / recordings).",
            "cfg.autoprog.flag": "Auto-program recommendations (create recording timers)",
            "cfg.autoprog.flag.desc": "Opt-in (off by default). When checked, after each run (scheduled task AND login), recommendations to record (upcoming EPG programs not already owned, not already scheduled, outside the drop list) are programmed as recordings (SeriesTimer for a series, single Timer for a movie). No programming while unchecked. The user can cancel an unwanted timer in Emby.",
            "cfg.aibadge.flag": "\"AI\" badge on the EPG images of to-record suggestions",
            "cfg.ownedbadge.flag": "Yellow \"already owned\" badge on the EPG images of library shows",
            "cfg.ownedbadge.desc": "Yellow chip (no icon) on the image of EPG programs whose series or movie is already in the library — same name matching as the EPG tools' library exclusion (FR/EN articles stripped, case-insensitive). The user knows from the guide that recording it is pointless. Same non-destructive mechanism as the \"AI\" badge (serve-time overlay, artwork never modified); separate cache key. NB: a new season of an owned series also carries the yellow chip (same series name).",
            "cfg.aibadge.desc": "Green chip + sparkle icon (no text, multilingual) on the top-right corner of the image of programs suggested by the scheduled task — visible in the native EPG guide, on every client. Non-destructive: a serve-time overlay, the stored artwork is never modified (the imported recording keeps the original image — the badge disappears once the show is recorded) and an EPG refresh has no effect. Each run replaces the badged selection; past airings self-expire.",
            "cfg.loginpopup.flag": "Login popup (\"watch tonight\" suggestions)",
            "cfg.loginpopup.seconds": "Toast duration (seconds)",
            "cfg.loginpopup.desc": "Independent of auto-programming: the popup lists what to watch tonight (unwatched recordings, library). Toast on the connecting client (if DisplayMessage is supported) + persistent bell notification (deep-link) as fallback. The bell survives even if the session closes before the LLM run finishes (~30–60 s) or the client doesn't support toasts.",
            "cfg.strmlib.h": "Recommendations .strm library",
            "cfg.strmlib.desc": "A manual alternative to auto-programming: after each run, the plugin writes a .strm+.nfo+poster card for each recommendation to record (upcoming EPG programs, not owned) into a dedicated Emby library. The user browses the library; playing a card creates the recording then shows a confirmation clip. First create a Movies (or Mixed content) library in Emby pointing at an empty folder, then enter its exact name here.",
            "cfg.strmlib.flag": "Enable the recommendations .strm library",
            "cfg.strmlib.name": "Dedicated Emby library name",
            "cfg.strmlib.name.desc": "Exact name (case-insensitive) of the Emby library where cards are written. Independent of auto-programming (both can coexist: dedup prevents duplicate timers). A security token is auto-generated on the first run to protect the activation endpoint.",

            // -- Orphan recording identification (scheduled task, 4 AM) ---
            "cfg.orphan.h": "Orphan recording identification",
            "cfg.orphan.desc": "Daily pass (4 AM) that finds <b>unidentified</b> DVR recordings (no IMDb/TMDB id — often Quebec titles missing from TMDB/TVDB), resolves them via <b>title cleanup + multi-language search</b> (S1), then an <b>LLM-proposed IMDb id validated through TMDB /find</b> (S2), then a <b>SearXNG web search → IMDb id</b> (S3), and writes the id + metadata + poster while <b>locking the EPG title</b> (never overwritten). Unresolved ones are tagged for review. <b>Opt-in</b>: mutates recordings.",
            "cfg.orphan.enabled": "Enable the orphan identification task",
            "cfg.orphan.dryrun": "Simulation mode (dry-run — no writes)",
            "cfg.orphan.dryrun.desc": "When checked, the task <b>writes nothing</b>: it only logs the orphans found and the proposed resolution (S1/S2/S3) + a summary. Use it to validate resolution quality before switching to automatic application. Keep checked for the first runs.",
            "cfg.orphan.searxng": "S3 stage: web search (SearXNG) for titles that can't be found",
            "cfg.orphan.searxng.desc": "When checked, after S1/S2 the task queries <b>SearXNG</b> (see URL above), extracts <b>IMDb</b> ids from the results, and validates them through TMDB + synopsis judge. Resolves paraphrased Quebec titles no catalog knows (e.g. \"L'histoire de Jean Seberg\" → film \"Seberg\" 2019). No-op if neither SearXNG nor an Ollama key is configured.",
            "cfg.orphan.retry": "Re-process needs-review items (retry)",
            "cfg.orphan.retry.desc": "When checked, orphans tagged <b>needs-review</b> are reprocessed (instead of skipped) — to run S3 on them once SearXNG is configured. On success the tag becomes <b>identified</b>. Already-identified items stay skipped.",

            "cfg.audit.h": "Server health audit",
            "cfg.audit.desc": "Launches an LLM agent that queries the <b>system_audit</b> tool (sessions, scheduled tasks, transcoding, disks, logs, host metrics) and produces a Markdown health report: severity-tagged findings + recommended actions. Admin-only. <b>Remediation</b> (stop a session, trigger a task, notify a user) is disabled by default.",
            "cfg.audit.enabled": "Enable the audit endpoint (/Plugins/LLMAI/Audit)",
            "cfg.audit.remediation": "Allow remediation (stop_session, trigger_task, send_message)",
            "cfg.audit.remediation.desc": "Opt-in (off by default). When checked, the LLM can <b>execute</b> remediation actions during the audit (stop a playback, trigger a task, send a message). While unchecked, these actions return an error and the LLM only <b>recommends</b> them in the report. The audit prompt also instructs the LLM never to act without an explicit request.",
            "cfg.audit.mode": "Audit execution mode",
            "cfg.audit.mode.single": "Agent loop (adaptive — capable / cloud model)",
            "cfg.audit.mode.deterministic": "Deterministic + synthesis (local model / gemma4)",
            "cfg.audit.mode.desc": "<b>single</b>: the LLM calls the system_audit tool itself, adaptively (can drill into a log after a finding). Suited to a capable (cloud) model. <b>deterministic</b>: the plugin gathers all probes itself (zero LLM) then a single <b>tool-free</b> LLM pass synthesizes the report — designed for a local/smaller model (e.g. gemma4) that synthesizes provided text well but handles multi-tool orchestration poorly. Note: <b>executable remediation</b> is only possible in <b>single</b> mode (with the remediation flag on); in deterministic mode it stays report-only.",
            "cfg.audit.focus": "Optional audit focus",
            "cfg.audit.focus.desc": "Free-form audit orientation (e.g. a domain to inspect, or an explicit remediation request). Leave empty for a full audit.",
            "cfg.audit.run": "Run health audit",
            "cfg.audit.running": "Audit running… (the agent is querying the server, ~30–60 s)",
            "cfg.audit.done": "Audit complete",
            "cfg.audit.disabled": "Audit is disabled in configuration.",
            "cfg.chat.h": "Chat with the AI assistant",
            "cfg.chat.desc": "Interactive multi-turn conversation with the LLM agent. It has <b>all the tools already available</b> (TV guide, library, TMDB/TVDB, web search, Showbizz, system audit) and uses the <b>LLM backend priorities</b> configured above. The history is kept by the page; the directive and tool documentation are sent to the LLM once, at the start of the conversation. Admin-only.",
            "cfg.chat.enabled": "Enable interactive chat (/Plugins/LLMAI/Chat)",
            "cfg.chat.input": "Your message",
            "cfg.chat.placeholder": "e.g. what's on tonight that I might like?",
            "cfg.chat.send": "Send",
            "cfg.chat.clear": "Clear conversation",
            "cfg.chat.running": "The assistant is thinking… (it may query your tools, a few tens of seconds)",
            "cfg.chat.disabled": "Chat is disabled in configuration.",
            "cfg.chat.you": "You",
            "cfg.chat.assistant": "AI assistant",
            "cfg.chat.hint": "Ask the assistant anything — it will query your Emby data (EPG, library, server health) to answer.",
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
            "cfg.alert.saved": "Configuration saved.",
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
            "rec.type.upcoming": "Upcoming",
            "rec.type.recording": "Available · Recording",
            "rec.type.library": "Available · Library",
            "rec.type.aired": "Aired"
        }
    };

    // ------------------------------------------------------------------
    //  Détection de langue
    // ------------------------------------------------------------------
    var lang = null;

    // Mappe une locale Emby/navigateur (ex. "fr-CA", "fr-FR", "en-US") vers un
    // code de dictionnaire. Extensible : ajouter un préfixe pour une nouvelle
    // langue (et un dictionnaire ci-dessus). Tout ce qui n'est pas reconnu ->
    // "en" (repli anglais).
    function pickLang(loc) {
        loc = String(loc || "").toLowerCase();
        if (loc.indexOf("fr") === 0) return "fr";
        // (extensible : if (loc.indexOf("es") === 0) return "es"; ...)
        return "en";
    }

    // Détecte la langue : globalize.getCurrentLocale() (source de vérité Emby)
    // via Emby.importModule, repli navigator.language, repli "en". Toujours
    // résolue (jamais rejetée) — l'i18n ne doit pas casser l'UI.
    function init() {
        if (lang) return Promise.resolve(lang);
        return new Promise(function (resolve) {
            var done = function (l) { lang = l; resolve(l); };
            try {
                if (typeof Emby !== "undefined" && Emby.importModule) {
                    Emby.importModule("./modules/common/globalize.js").then(function (g) {
                        // Emby.importModule peut résoudre soit le default du
                        // module AMD, soit le namespace {default:…}. On gère les
                        // deux pour que getCurrentLocale() (langue d'affichage
                        // Emby, ex. fr-CA) soit bien trouvée.
                        var gl = g && (g.default || g);
                        var loc = (gl && typeof gl.getCurrentLocale === "function") ? gl.getCurrentLocale() : "";
                        done(pickLang(loc || (navigator.language || "")));
                    }, function () { done(pickLang(navigator.language || "")); });
                } else {
                    done(pickLang(navigator.language || ""));
                }
            } catch (e) { done(pickLang(navigator.language || "")); }
        });
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