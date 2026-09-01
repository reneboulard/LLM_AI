# Changelog

Tous les changements notables de ce plugin sont documentés ici.
Le format s'inspire de [Keep a Changelog](https://keepachangelog.com/),
et ce projet adhère au [Semantic Versioning](https://semver.org/lang/fr/).

All notable changes to this plugin are documented here.
Format based on [Keep a Changelog](https://keepachangelog.com/),
and this project adheres to [Semantic Versioning](https://semver.org/).

---

## [Non publié / Unreleased]

### Ajouté / Added

- **Traduction IA des genres EPG — pont GenreCleaner** (`GenreApiService` +
  `GenreCleanerMap` + section « Traduction des genres (IA) » de la page de config) :
  LLM_AI devient le **curateur** du plugin GenreCleaner (catalogue officiel Emby) —
  il détecte les genres EPG que la table `GenreMappings`/`AllowedGenres` de
  GenreCleaner ne couvre pas encore et fait proposer par le LLM les mappages
  manquants, que l'admin valide avant écriture directement dans `GenreCleaner.xml`.
  Admin-only (tokens LLM + config d'un autre plugin).
  **AI translation of EPG genres — GenreCleaner bridge** (`GenreApiService` +
  `GenreCleanerMap` + "AI genre translation" config-page section): LLM_AI becomes the
  **curator** of the GenreCleaner plugin (official Emby catalog) — it detects the EPG genres that
  GenreCleaner's `GenreMappings`/`AllowedGenres` doesn't cover yet and has the LLM
  propose the missing mappings, which the admin validates before they are written
  straight into `GenreCleaner.xml`. Admin-only (LLM tokens + another plugin's config).
  - **`GET /Plugins/LLMAI/GenreProposals`** — collecte les genres des programmes EPG
    **à venir** (`HasAired=false`, requête library calquée sur `BuildGenreMap` — les
    DTO de `GetPrograms` ne portent pas `Genres` sur ce build) non couverts (mappés OU
    présents dans AllowedGenres), séparément films/séries (plafond 60/section), puis
    un appel LLM one-shot (`ChatWithFallbackAsync`, repli multi-backend) propose pour
    chacun : une cible du vocabulaire curaté, un **nouveau genre** (nom court, général,
    dans la langue de réponse du plugin — cascade `ResolveMetaLangKey` :
    `ResponseLanguage` → langue d'affichage Emby → `TmdbLanguage`), ou rien (orphelin).
    Réponse à trois niveaux : propositions / suggestions de nouveaux genres (ajout
    AllowedGenres **et** mappage en un clic) / orphelins (information seulement).
    **`GET /Plugins/LLMAI/GenreProposals`** — collects the genres of **upcoming** EPG
    programs (`HasAired=false`, library query modeled on `BuildGenreMap` — this
    build's `GetPrograms` DTOs carry no `Genres`) not covered (mapped OR present in
    AllowedGenres), separately for movies/series (60/section cap), then a one-shot LLM
    call (`ChatWithFallbackAsync`, multi-backend fallback) proposes for each: a
    curated-vocabulary target, a **new genre** (short, general name, in the plugin's
    response language — `ResolveMetaLangKey` cascade: `ResponseLanguage` → Emby display
    language → `TmdbLanguage`), or nothing (orphan). Three-tier response: proposals /
    new-genre suggestions (AllowedGenres add **and** mapping in one click) / orphans
    (information only).
  - **`POST /Plugins/LLMAI/GenreApply`** — re-validation serveur (cible dans le
    vocabulaire sauf nouveaux genres, rejet des mappages identité `Action → Action`),
    écriture **idempotente** dans `GenreCleaner.xml` (dedup par clé normalisée),
    enregistrement de chaque mappage appliqué dans `PluginConfiguration.GenreAliasApplied`
    et déclenchement de `NotifyPendingRestart()` (bannière Emby « redémarrage requis » —
    les recommandations LLM_AI adoptent les mappages sans redémarrage, GenreCleaner lui
    ne les adopte qu'au redémarrage).
    **`POST /Plugins/LLMAI/GenreApply`** — server-side re-validation (target in
    vocabulary except new genres, identity mappings `Action → Action` rejected),
    **idempotent** write into `GenreCleaner.xml` (normalized-key dedup), every applied
    mapping recorded in `PluginConfiguration.GenreAliasApplied`, and
    `NotifyPendingRestart()` triggered (Emby's "restart required" banner — LLM_AI
    recommendations adopt the mappings without a restart; GenreCleaner only adopts them
    on restart).
  - **Auto-guérison** — si le XML revient à une version antérieure (restauration, ou
    sauvegarde depuis la page de config de GenreCleaner qui sérialise sa copie mémoire),
    `GenreCleanerMap.HealApplied` (au GET analyse et à chaque run de la tâche planifiée)
    ré-écrit les mappages enregistrés qui manquent ; les entrées `new:true` restaurent
    **aussi** l'entrée `AllowedGenres` correspondante. Rien ne se perd.
    **Self-healing** — if the XML reverts to an older version (a restore, or a save from
    GenreCleaner's own config page which serializes its in-memory copy),
    `GenreCleanerMap.HealApplied` (on the analysis GET and every scheduled-task run)
    re-writes the recorded mappings that went missing; `new:true` entries also restore
    the matching `AllowedGenres` entry. Nothing is lost.
  - **Genres curatés dans les outils EPG** — `epg_series`/`epg_movies`/`epg_tonight`
    émettent des genres curatés (`GenreCleanerMap.MapGenres`, table de la section
    films/séries du programme) : le LLM voit le même vocabulaire que le profil de goût
    de l'usager (bibliothèque déjà curatée). Whitelists et exclusions de genres matchent
    la clé **brute ET mappée** (`GenreCleanerMap.GenreKeys`) — une whitelist saisie en
    vocabulaire EPG brut continue de matcher après activation du mapping ; exclusions
    par défaut bilingues (`documentary`/`news` + `documentaire`/`nouvelles`).
    **Curated genres inside the EPG tools** — `epg_series`/`epg_movies`/`epg_tonight`
    emit curated genres (`GenreCleanerMap.MapGenres`, the program's movie/series
    section table): the LLM sees the same vocabulary as the user's taste profile
    (library already curated). Genre whitelists and exclusions match the **raw AND
    mapped** keys (`GenreCleanerMap.GenreKeys`) — a whitelist typed in the raw EPG
    vocabulary keeps matching once the mapping is enabled; default exclusions are
    bilingual (`documentary`/`news` + `documentaire`/`nouvelles`).

- **Routes API usager pour la page Recommandations** (`RecosApiService`) :
  `GET /Plugins/LLMAI/Recos` (dernières recommandations de la tâche planifiée + date)
  et `POST /Plugins/LLMAI/Forget {Title}` (bouton « Oublier » → `DroppedTitles`,
  écriture serveur-side via `SaveConfiguration`). La page `recommendations.js` lisait
  auparavant la config plugin via l'endpoint hôte `/Plugins/{id}/Configuration` —
  réservé ManageServer : un usager non-admin recevait **403** et la page ne rendait
  rien. Les routes ne servent **que** `Recommendations`/`RecommendationsDate` —
  jamais la config complète (clés API, prompts, chemins).
  **User API routes for the Recommendations page** (`RecosApiService`):
  `GET /Plugins/LLMAI/Recos` (latest scheduled-task recommendations + date) and
  `POST /Plugins/LLMAI/Forget {Title}` (**Forget** button → `DroppedTitles`, written
  server-side via `SaveConfiguration`). The `recommendations.js` page previously read
  plugin config through the host endpoint `/Plugins/{id}/Configuration` —
  ManageServer-only: a non-admin user got **403** and the page rendered nothing. The
  routes serve **only** `Recommendations`/`RecommendationsDate` — never the full
  config (API keys, prompts, paths).

- **Bannière de mise à jour GitHub** (`UpdateApiService`, `GET /Plugins/LLMAI/Update`) :
  compare le tag de la dernière release GitHub (`reneboulard/LLM_AI`, workflow
  `release.yml` sur tag `v*`) à la version d'assembly installée → bandeau sur la page
  de config avec le lien de la release. **Lecture seule** (aucun téléchargement ni
  installation — Emby n'auto-met à jour que les plugins de son catalogue officiel),
  cache 1 h sous verrou (limite API GitHub non authentifiée), `Force=1` pour bypasser,
  ne lève jamais (erreur réseau → pas de bandeau).
  **GitHub update banner** (`UpdateApiService`, `GET /Plugins/LLMAI/Update`): compares
  the latest GitHub release tag (`reneboulard/LLM_AI`, `release.yml` workflow on `v*`
  tags) with the installed assembly version → config-page banner linking to the
  release. **Read-only** (no download or install — Emby only auto-updates official
  catalog plugins), 1 h lock-guarded cache (unauthenticated GitHub API limit),
  `Force=1` bypass, never throws (network error → no banner).

### Corrigé / Fixed

- **`epg_tonight` : fenêtre « ce soir » vide sur ce build d'Emby** — `GetPrograms`
  n'honore pas `MinStartDate`/`MaxStartDate` (0 programme alors que l'EPG en contient
  ~200 par soirée) : repli en mémoire — relance sans fenêtre (`HasAired=false`) puis
  filtre C# par `StartDate` sur la fenêtre `TonightWindowStart`→`TonightWindowEnd`,
  plafonné au pool. Ajout d'un **recensement des genres** dans le log (genres émis au
  LLM post-mapping GenreCleaner) qui rend visible d'un coup d'œil que le pont est
  actif.
  **`epg_tonight`: empty "tonight" window on this Emby build** — `GetPrograms`
  ignores `MinStartDate`/`MaxStartDate` (0 programs although the EPG holds ~200 per
  evening): in-memory fallback — re-query without a window (`HasAired=false`) then
  C#-filter by `StartDate` over the `TonightWindowStart`→`TonightWindowEnd` window,
  capped to the pool. Added a **genre census** log line (genres emitted to the LLM
  post-GenreCleaner-mapping) that makes bridge activity visible at a glance.

- **« À regarder ce soir » : toutes les recos `live` supprimées par la validation** —
  le snapshot EPG de `ValidateAndFilter` (TonightService) reposait sur la même requête
  fenêtrée `GetPrograms` qui retourne 0 sur ce build : dictionnaire VIDE → chaque reco
  `live` était droppée « hors-snapshot » (vécu : 4/4 enrichies puis 4/4 supprimées →
  erreur « toutes les recommandations pointaient vers des items introuvables »). Même
  repli en mémoire que `epg_tonight` (sans `HasAired=false` : le snapshot couvre aussi
  les 24 dernières heures pour la détection « Diffusé »), + log de la taille du snapshot.
  **"Watch tonight": all `live` recos dropped by validation** — the EPG snapshot in
  `ValidateAndFilter` (TonightService) relied on the same windowed `GetPrograms` query
  that returns 0 on this build: EMPTY dictionary → every `live` reco was dropped as
  "out-of-snapshot" (seen live: 4/4 enriched then 4/4 deleted → "all recommendations
  pointed to unfindable items" error). Same in-memory fallback as `epg_tonight`
  (without `HasAired=false`: the snapshot also covers the last 24 hours for the
  "Aired" detection), + snapshot-size log line.

- **Réponses finales LLM non-JSON tolérées jusqu'à l'affichage brut** — vécu avec
  glm-5.3:cloud : prose autour du tableau fenced, guillemets internes non échappés
  (`S07E06 "The Truck Stops Here"`), ou écho du format demandé — le parse aval
  échouait silencieusement (enrichissement + validation ignorés, markdown brut servi
  sur la page). Deux garde-fous : `ExtractJsonPayload` retire les balises ``` même
  précédées de prose, et la boucle agent (mode recommandation) vérifie que la réponse
  finale est un tableau JSON parseable — sinon **réparation bornée** (2 tentatives) :
  le message d'erreur est réinjecté au modèle, même mécanisme que le renvoi des
  appels d'outils malformés.
  **Non-JSON final LLM answers tolerated all the way to raw display** — seen with
  glm-5.3:cloud: prose around the fenced array, unescaped inner quotes
  (`S07E06 "The Truck Stops Here"`), or an echo of the requested format — downstream
  parsing failed silently (enrichment + validation skipped, raw markdown served to
  the page). Two safeguards: `ExtractJsonPayload` strips ``` fences even when preceded
  by prose, and the agent loop (recommendation mode) checks that the final answer is
  a parseable JSON array — otherwise a **bounded repair** (2 attempts) re-injects the
  error to the model, same mechanism as the malformed-tool-call resend.

## [1.1.0.0] — 2026-08-31

### Ajouté / Added
- **Audit santé du serveur** (`SystemAuditTool` + `AuditApiService`, endpoint à la
  demande `GET /Plugins/LLMAI/Audit`) : un agent LLM interroge l'outil `system_audit`
  et produit un **rapport Markdown** de santé (constats tagués 🔴/⚠️/✅ + actions
  recommandées). Indépendant de la recommandation (run agent dédié). Admin-only.
  **Server health audit** (`SystemAuditTool` + `AuditApiService`, on-demand endpoint
  `GET /Plugins/LLMAI/Audit`): an LLM agent queries the `system_audit` tool and produces
  a **Markdown health report** (severity-tagged findings + recommended actions).
  Independent from recommendations (dedicated agent run). Admin-only.
  - **Outil `system_audit`** — 15 actions sur `action` : inspection (lecture seule,
    toujours disponibles) `server_info`, `system_config` (configuration serveur via
    `IServerConfigurationManager.Configuration` — cross-OS, lu en cours de processus),
    `active_sessions`, `scheduled_tasks`, `list_logs`, `inspect_log` (tail ou grep +
    contexte, **confiné au dossier des journaux** : nom seul + whitelist extension
    `.txt`/`.log` + containment canonique), `transcode`, `gpu_transcode`, `host_metrics`
    (BCL : process/GC/runtime/uptime/scan + CPU transcodage agrégé ; GPU uniquement par
    transcodage), `disk_storage` (`DriveInfo` + mapping chemins Emby), `processes`
    (détection d'**orphelins ffmpeg** par corrélation + top RAM/CPU + compteurs Emby,
    BCL pure — aucun argument de processus lu), `library_stats` (comptes par type +
    bibliothèques + état du scan, via `ILibraryManager` — couche DB, pas FS brut),
    `missing_metadata` (échantillonnage des items sans synopsis/image/genres) ;
    remédiation (gate `AuditRemediationEnabled`) `stop_session`, `trigger_task`,
    `send_message`. Ne lève jamais (erreur → JSON, préserve la boucle agent).
    **`system_audit` tool** — 15 actions on `action`: inspection (read-only, always
    available) `server_info`, `system_config` (server configuration via
    `IServerConfigurationManager.Configuration` — cross-OS, read in-process),
    `active_sessions`, `scheduled_tasks`, `list_logs`, `inspect_log` (tail or grep +
    context, **confined to the log folder**: name-only + `.txt`/`.log` extension
    whitelist + canonical containment), `transcode`, `gpu_transcode`, `host_metrics`
    (BCL: process/GC/runtime/uptime/scan + aggregate transcode CPU; GPU only per
    transcode), `disk_storage` (`DriveInfo` + Emby path mapping), `processes`
    (ffmpeg-**orphan** detection by correlation + top RAM/CPU + Emby counters, pure BCL
    — no process arguments read), `library_stats` (per-type counts + libraries + scan
    state, via `ILibraryManager` — DB layer, no raw FS), `missing_metadata` (sampling
    of items missing overview/image/genres); remediation (gate `AuditRemediationEnabled`)
    `stop_session`, `trigger_task`, `send_message`. Never throws (error → JSON,
    preserves the agent loop).
  - **Deux modes d'exécution** (`AuditMode`) : `single` (défaut, boucle agent
    adaptative — modèle costaud/cloud, seul mode avec remédiation exécutable) et
    `deterministic` (rassemblement C# de toutes les sondes en un digest, zéro LLM,
    puis un seul passage LLM **sans outils** synthétise le rapport — conçu pour un
    modèle local/modeste comme gemma4, retire l'orchestration multi-outils pour ne
    garder que la synthèse de texte fourni ; remédiation report-only).
    **Two execution modes** (`AuditMode`): `single` (default, adaptive agent loop —
    capable/cloud model, the only mode with executable remediation) and `deterministic`
    (C# gathers all probes into a digest, zero LLM, then a single **tool-free** LLM pass
    synthesizes the report — designed for a local/smaller model like gemma4, removes
    multi-tool orchestration to keep only synthesis of provided text; remediation is
    report-only).
  - **Sécurité** : endpoint admin-only (`Policy.IsAdministrator`) ; pas d'outil générique
    de lecture de fichier (le LLM ne peut pas vaguer dans `/`) ; remédiation gated par
    config (défaut off) + consigne du prompt « n'agis jamais sans demande explicite ».
    **Security**: admin-only endpoint (`Policy.IsAdministrator`); no generic file-read
    tool (the LLM cannot wander into `/`); remediation gated by config (default off) +
    prompt instruction "never act without an explicit request".
  - **Config** : `AuditEnabled` (défaut `true`), `AuditRemediationEnabled` (défaut
    `false`, opt-in), `AuditMode` (`single`/`deterministic`), `AuditPrompt` (template +
    `Focus` optionnel). Page de config : section « Audit santé » avec bouton
    « Lancer l'audit » + panneau de rendu Markdown + mini-convertisseur Markdown→HTML sûr.
    **Config**: `AuditEnabled` (default `true`), `AuditRemediationEnabled` (default
    `false`, opt-in), `AuditMode` (`single`/`deterministic`), `AuditPrompt` (template +
    optional `Focus`). Config page: "Health audit" section with a "Run health audit"
    button + Markdown render panel + safe minimal Markdown→HTML converter.
- **Langue de réponse du LLM** (`ResponseLanguage`) : force la langue du texte en prose de
  l'LLM — les **raisons des recommandations** (champ `reason`) **et** le **rapport d'audit**.
  Vide / `Auto` (défaut) = aucune directive (l'LLM suit la langue du prompt, ici le
  français). Toute autre valeur (ex. `English`, `Español`…) injecte une directive en fin de
  system prompt ; les titres de films/séries, noms de chaînes et champs JSON techniques
  restent inchangés. Select sur la page de config (`Auto`, `Français`, `English`, `Español`,
  `Deutsch`, `Italiano`, `Português`). S'applique aux deux paths (recommandation + audit,
  modes single et déterministe) via un paramètre optionnel rétro-compatible du
  `LlmAgentService` et un append au system prompt du mode synthèse déterministe.
  **LLM response language** (`ResponseLanguage`): forces the language of the LLM's prose —
  the **recommendation reasons** (`reason` field) **and** the **audit report**. Empty /
  `Auto` (default) = no directive (the LLM follows the prompt's language, here French). Any
  other value (e.g. `English`, `Español`…) injects a directive at the end of the system
  prompt; movie/series titles, channel names and technical JSON fields stay unchanged.
  Config-page select (`Auto`, `Français`, `English`, `Español`, `Deutsch`, `Italiano`,
  `Português`). Applies to both paths (recommendations + audit, single and deterministic
  modes) via a backward-compatible optional `LlmAgentService` parameter and an append to the
  deterministic-synthesis system prompt.
- **i18n côté serveur (C#)** (`I18n.cs`) : dictionnaires inline FR/EN + résolution de
  langue. **Deux buckets** : métadonnées (`ResolveMetaLangKey` — `<plot>` du `.nfo`,
  synopsis TMDB, prose LLM → `ResponseLanguage` puis langue d'affichage puis legacy
  `TmdbLanguage` puis en-US) et interface (`ResolveDisplayLangKey` — nom/description
  des tâches planifiées → langue d'affichage Emby `UICulture`). Helpers `ToTmdbLang`
  (clé 2 lettres → `fr-FR`/`en-US`…) et `ToLangName` (→ `French`/`English`… pour la
  cible de traduction LLM). Localise les tâches planifiées (`task.*.name/desc`).
  Extensible par la donnée (ajouter une entrée `s_res`).
  **Server-side i18n (C#)** (`I18n.cs`): inline FR/EN dictionaries + language
  resolution. **Two buckets**: metadata (`ResolveMetaLangKey` — `.nfo` `<plot>`, TMDB
  overview, LLM prose → `ResponseLanguage` then display language then legacy
  `TmdbLanguage` then en-US) and UI (`ResolveDisplayLangKey` — scheduled-task
  name/description → Emby display language `UICulture`). Helpers `ToTmdbLang`
  (2-letter key → `fr-FR`/`en-US`…) and `ToLangName` (→ `French`/`English`… for the LLM
  translation target). Localizes scheduled tasks (`task.*.name/desc`). Data-driven
  extensibility (add an `s_res` entry).
- **Poster par défaut standardisé** (`DefaultImageApplier`) : pose un poster
  `default_poster.jpg` (ressource embedded, JPEG) en `ImageType.Primary` sur la
  collection `AI Tonight` (BoxSet) **et** la racine de la bibliothèque `.strm`
  (CollectionFolder). **Idempotent** : ne pose l'image que si l'item n'en a pas déjà
  une (`HasImage` false) — respecte une attribution manuelle ultérieure (« Edit
  Images »). Best-effort (ne lève jamais) via `IProviderManager.SaveImage` +
  `UpdateToRepository(ImageUpdate)`.
  **Standardized default poster** (`DefaultImageApplier`): sets a `default_poster.jpg`
  (embedded resource, JPEG) as `ImageType.Primary` on the `AI Tonight` collection
  (BoxSet) **and** the `.strm` library root (CollectionFolder). **Idempotent**: only
  sets the image if the item has none yet (`HasImage` false) — respects a later manual
  assignment ("Edit Images"). Best-effort (never throws) via
  `IProviderManager.SaveImage` + `UpdateToRepository(ImageUpdate)`.
- **Identification des enregistrements orphelins** (`OrphanIdentifyTask`, tâche
  planifiée quotidienne **04:00**) : repère les **items de bibliothèque non
  identifiés** (films/séries issus d'enregistrements DVR terminés — une fois
  l'enregistrement terminé, Emby importe l'item dans une bibliothèque ; aucun id
  IMDb/TMDB/TVDB = identification échouée, souvent des titres québécois absents du
  catalogue TMDB/TVDB) et tente de les résoudre en trois stages :
  - **S1 — nettoyage + recherche multilingue** : le titre EPG est débarrassé de son
    bruit (`CleanEpgTitle` : HD, VOSTFR, « Rediff. », marqueurs saison/épisode,
    parenthèses) puis recherché sur TMDB en plusieurs langues (en-US = titre original,
    fr-FR = titre France, + langue de l'usager). Garde-fou de correspondance (titre
    normalisé + année). **S1 n'est lancé que si l'année `ProductionYear` est connue** :
    sans année fiable, la recherche TMDB est large et la garde lexicale (sans juge)
    pourrait accepter un faux film homonyme — les orphelins sans année sont laissés à
    S2/S3.
  - **S2 — proposition LLM validée par TMDB** : le LLM propose un id IMDb/TMDB à partir
    du titre + overview + chaîne ; la proposition est **validée** via TMDB `/find`
    (`FindByExternalIdAsync`) ou détail par id (`LookupMetaByIdAsync`) — TMDB est la
    source de vérité, un id halluciné renvoie null. **Porte d'acceptation sémantique** :
    chaque candidat doit passer un **juge LLM de synopsis** (`LlmRunner.JudgeSynopsisMatchAsync`)
    qui compare le synopsis EPG au synopsis TMDB pour confirmer qu'ils décrivent la
    *même œuvre* (un id qui existe mais qui correspond à un film homonyme d'une autre
    époque — ex. « Le guérisseur » 1953 vs 2017 — est rejeté). Reproduit la méthode
    manuelle de l'usager (comparaison synopsis + date ; on continue de chercher si
    différent). Garde-fou année en plus. Skippé quand l'EPG n'a pas de synopsis
    (retour à année + titre).
  - **S3 — recherche web (SearXNG) → id IMDb** : si S1 et S2 échouent, la tâche
    interroge l'instance **SearXNG** auto-hébergée (champ `SearXngUrl`, déjà utilisé
    par l'outil `web_search` ; repli Ollama cloud), extrait les **ids IMDb** des URLs
    de résultats (regex `imdb.com/.../title/tt…`), puis valide chaque id via TMDB
    `/find` + la **même porte d'acceptation** (année + juge synopsis). Reproduit
    exactement la méthode manuelle de l'usager (web-search du titre → id IMDb → Emby
    tire TMDB → comparaison synopsis+date) et résout les **titres paraphrasés
    québécois** qu'aucun catalogue ne connaît (ex. « L'histoire de Jean Seberg » →
    film « Seberg » 2019 → tt1780967). Accepté sans synopsis à comparer → logué
    « à confirmer visuellement ».
  - **Correction année** : l'année de référence est désormais `ProductionYear`
    **uniquement** (avant : `PremiereDate`/`DateCreated` en repli — or pour un
    enregistrement DVR ce sont des dates de **diffusion/enregistrement**, pas de
    sortie ; utilisées comme `primary_release_year` elles filtraient TMDB à tort et
    rataient des films existants).
  - **Application non destructive** : ne remplit que les ids provider absents, un
    `Overview` vide, des `Genres` vides, un poster `Primary` manquant. **Le `Name` EPG
    n'est jamais modifié** — il est **verrouillé** (`MetadataFields.Name`) pour
    préserver le titre d'origine (réutilisé plus tard pour scanner l'EPG). Les champs
    remplis sont aussi verrouillés (add-only — aucun verrou existant n'est retiré),
    reflétant la pratique manuelle de l'usager.
  - **Idempotence** via tags `llmai-identified` (résolu) / `llmai-needs-review`
    (irrésolu — marqué pour revue). **Dry-run** (`OrphanIdentifyDryRun`) : aucune
    écriture, log détaillé de la résolution proposée + bilan. Best-effort : un item en
    erreur n'interrompt jamais le passage. Scope : items de bibliothèque Movie/Series
  (enregistrements DVR terminés), pas les cartes `.strm` (découverte via
  `ILibraryManager.GetItemList`, `IncludeItemTypes=Movie,Series`).
  **Orphan recording identification** (`OrphanIdentifyTask`, daily scheduled task
  **4 AM**): finds **unidentified library items** (movies/series from completed DVR
  recordings — once recording completes, Emby imports the item into a library; no
  IMDb/TMDB/TVDB id = failed identification, often Quebec titles missing from
  TMDB/TVDB) and tries to resolve them in three stages:
  - **S1 — cleanup + multi-language search**: the EPG title is stripped of noise
    (`CleanEpgTitle`: HD, VOSTFR, "Rediff.", season/episode markers, parentheses) then
    searched on TMDB in several languages (en-US = original title, fr-FR = France
    title, + user language). Match guard (normalized title + year). **S1 only runs
    when the `ProductionYear` is known**: without a reliable year, TMDB search is broad
    and the lexical guard (no judge) could accept a wrong same-titled film — orphans
    with no year are left to S2/S3.
  - **S2 — LLM proposal validated by TMDB**: the LLM proposes an IMDb/TMDB id from the
    title + overview + channel; the proposal is **validated** via TMDB `/find`
    (`FindByExternalIdAsync`) or detail-by-id (`LookupMetaByIdAsync`) — TMDB is the
    source of truth, a hallucinated id returns null. **Semantic acceptance gate**: each
    candidate must pass an **LLM synopsis judge** (`LlmRunner.JudgeSynopsisMatchAsync`)
    that compares the EPG synopsis to the TMDB synopsis to confirm they describe the
    *same work* (an id that exists but is a same-titled film from a different era — e.g.
    "Le guérisseur" 1953 vs 2017 — is rejected). Mirrors the user's manual method
    (compare synopsis + date; keep searching if different). Year guard on top. Skipped
    when the EPG has no synopsis (falls back to year + title).
  - **S3 — web search (SearXNG) → IMDb id**: if S1 and S2 fail, the task queries the
    self-hosted **SearXNG** instance (`SearXngUrl` field, already used by the
    `web_search` tool; Ollama cloud fallback), extracts **IMDb ids** from result URLs
    (regex `imdb.com/.../title/tt…`), then validates each id via TMDB `/find` + the
    **same acceptance gate** (year + synopsis judge). Mirrors the user's manual method
    exactly (web-search the title → IMDb id → Emby pulls TMDB → compare synopsis+date)
    and resolves **paraphrased Quebec titles** no catalog knows (e.g. "L'histoire de
    Jean Seberg" → film "Seberg" 2019 → tt1780967). Accepted with no synopsis to
    compare → logged "to confirm visually".
  - **Year fix**: the reference year is now `ProductionYear` **only** (previously
    `PremiereDate`/`DateCreated` as fallback — but for a DVR recording those are
    **broadcast/recording** dates, not release dates; used as `primary_release_year`
    they filtered TMDB wrongly and missed existing films).
  - **Non-destructive apply**: only fills missing provider ids, an empty `Overview`,
    empty `Genres`, a missing `Primary` poster. **The EPG `Name` is never changed** —
    it is **locked** (`MetadataFields.Name`) to preserve the original title (reused
    later to scan the EPG). Filled fields are also locked (add-only — no existing lock
    is removed), mirroring the user's manual practice.
  - **Idempotent** via `llmai-identified` (resolved) / `llmai-needs-review` (unresolved
    — tagged for review) tags. **Dry-run** (`OrphanIdentifyDryRun`): no writes, detailed
    log of the proposed resolution + summary. Best-effort: a failing item never aborts
    the pass. Scope: library Movie/Series items (completed DVR recordings), not
    `.strm` cards (discovered via `ILibraryManager.GetItemList`,
    `IncludeItemTypes=Movie,Series`).
  - **Config** : `OrphanIdentifyEnabled` (défaut `false`, opt-in — modifie des
    enregistrements), `OrphanIdentifyDryRun` (défaut `false`),
    `OrphanSearXngEnabled` (défaut `true` — étape S3 ; inopérant sans SearXNG/clé
    Ollama), `OrphanRetryNeedsReview` (défaut `false` — retraite les besoins-revues,
    utile pour y repasser S3 une fois SearXNG configuré ; en cas de résolution le tag
    `needs-review` devient `identified`). Page de config : section « Identification
    des enregistrements orphelins ».
    **Config**: `OrphanIdentifyEnabled` (default `false`, opt-in — mutates recordings),
    `OrphanIdentifyDryRun` (default `false`), `OrphanSearXngEnabled` (default `true` —
    S3 stage; no-op without SearXNG/Ollama key), `OrphanRetryNeedsReview` (default
    `false` — re-processes needs-review items, useful to run S3 on them once SearXNG is
    configured; on success the `needs-review` tag becomes `identified`). Config page:
    "Orphan recording identification" section.
- **Badges IA sur les images EPG** (`AiBadgeEnhancer` + `AiBadgeRegistry`,
  auto-découverts par le scan d'assembly d'Emby) : deux badges dessinés **au moment du
  service** (overlay `IImageEnhancer` — l'artwork stocké n'est JAMAIS modifié, donc le
  badge disparaît gratuitement quand l'enregistrement est importé et l'image originale
  est préservée) :
  - **Badge « suggestion IA »** — puce verte `#21963F` + étincelle blanche à 4 branches,
    coin haut droit, sur les programmes du **record bucket** de la tâche nocturne
    (registre `AiBadgeRegistry`, remplacé à chaque run, persisté `AiBadgeProgramIds` ;
    garde `EndDate > now` → auto-expiration des suggestions passées).
  - **Badge « déjà possédé »** — puce jaune `#FBC02D` SANS étincelle : pour un film,
    le film (`Name`) figure dans la bibliothèque ; pour un épisode de série,
    **cet épisode précis** doit y figurer (n° saison/épisode `s{S}e{E}` d'abord, puis
    titre d'épisode normalisé) — posséder la série ne badge **pas** toutes ses
    diffusions, seuls les épisodes réellement possédés le sont (vérifié empiriquement :
    les épisodes EPG partagent la même pochette Gracenote au niveau série, mais le
    rapprochement et la clé de cache sont désormais par épisode). Repli conservateur :
    un programme EPG sans numérotation dont le titre ne matche aucun épisode possédé
    retombe sur le niveau série (comportement historique — on ne peut pas prouver que
    l'épisode est absent). Réutilise la correspondance par nom normalisé
    (`GetEmbyInfoTool.Norm`) de l'exclusion epg_series/epg_movies ; index noms +
    clés d'épisodes biblio caché 10 min (jamais par requête). Le vert gagne en cas
    de conflit.
  - **Clé de cache par état ET par item** (`ownedbadge-v2`/`aibadge-v2` + suffixe
    `InternalId`) : les épisodes d'une même série partagent la même pochette Gracenote
    (URL unique au niveau série) — sans suffixe par item, le badge du premier épisode
    servi serait resservi à tous les épisodes partageant l'artwork, faisant fuiter le
    badge d'un épisode (ou d'une suggestion AI d'un programme) sur les autres. Le
    suffixe par item sépare les entrées de cache ; les transitions d'état régénèrent
    l'image. Dessin via **SkiaSharp** livré avec Emby (référencé `libs/SkiaSharp.dll`,
    aucun changement de déploiement), repli = copie de l'original sur toute erreur
    (l'enhancer ne lève jamais dans le pipeline d'images).
  Config : `AiBadgeEnabled` + `AiOwnedBadgeEnabled` (défaut `true`, opt-out), cases à
  cocher sur la page de config (FR/EN).
  **AI badges on EPG images** (`AiBadgeEnhancer` + `AiBadgeRegistry`, auto-discovered by
  Emby's assembly scan): two badges drawn **serve-time** (an `IImageEnhancer` overlay —
  stored artwork is NEVER modified, so the badge disappears for free once the recording
  is imported and the original image is preserved):
  - **"AI suggestion" badge** — green chip `#21963F` + white 4-point sparkle, top-right,
    on the nightly task's **record bucket** programs (`AiBadgeRegistry`, replaced on each
    run, persisted `AiBadgeProgramIds`; `EndDate > now` guard → past suggestions
    self-expire).
  - **"Already owned" badge** — yellow chip `#FBC02D` WITHOUT the sparkle: for a movie,
    the movie (`Name`) exists in the library; for a series episode, **that specific
    episode** must exist there (season/episode number `s{S}e{E}` first, then normalized
    episode title) — owning a series does **not** badge all its airings, only the
    actually-owned episodes get the chip (empirically verified: EPG episodes share the
    series-level Gracenote artwork, but both the match and the cache key are now
    per-episode). Conservative fallback: an EPG program with no episode numbering whose
    title matches no owned episode falls back to series level (historical behavior —
    the episode's absence cannot be proven). Reuses the normalized name matching
    (`GetEmbyInfoTool.Norm`) from the epg_series/epg_movies exclusion; library names +
    episode keys cached 10 min (never per request). Green wins on conflict.
  - **Cache key per state AND per item** (`ownedbadge-v2`/`aibadge-v2` + `InternalId`
    suffix): episodes of the same series share the same Gracenote artwork (one
    series-level URL) — without a per-item suffix, the first served episode's badge
    would be re-served to every episode sharing the artwork, leaking one episode's
    badge (or one program's AI suggestion) onto the others. The per-item suffix
    separates the cache entries; state transitions regenerate the image. Drawn with
    **SkiaSharp** bundled with Emby (referenced from `libs/SkiaSharp.dll`, zero deploy
    changes), fallback = copy of the original on any error (the enhancer never throws
    into the image pipeline).
  Config: `AiBadgeEnabled` + `AiOwnedBadgeEnabled` (default `true`, opt-out), config-page
  checkboxes (FR/EN).
- **Chat interactif avec l'assistant IA** (`ChatApiService` +
  `LlmAgentService.RunChatAsync` + `LlmRunner.RunChatAsync`) : conversation multi-tours
  avec l'agent LLM directement sur la page de config (`POST /Plugins/LLMAI/Chat`,
  admin-only). Réutilise **tous les outils existants** (guide TV, bibliothèque,
  TMDB/TVDB, web, Showbizz, `system_audit` — la remédiation reste gated par
  `AuditRemediationEnabled`) et les **priorités de backends LLM configurées** — aucun
  nouvel outil, aucun changement de backend. Le serveur est stateless : la page garde
  l'historique (tours user/assistant uniquement, bornés à 40) et le re-poste à chaque
  tour ; le system prompt — documentation complète des outils + directives RAG — est
  construit côté serveur, injecté **une seule fois** par conversation et jamais renvoyé
  par le client (un « system » forgé dans le corps est ignoré). Les réponses sont du
  Markdown brut, rendu par le mini-convertisseur existant. La boucle agent partagée
  (`RunLoopAsync`) est inchangée pour les chemins recommandation/audit ; le message de
  réparation JSON est désormais adapté au mode (Markdown hors recommandation).
  **Page dédiée** « LLM_AI Chat » (`chat.html`/`chat.js`) dans le menu admin (dashboard,
  section « Serveur ») — chat plein cadre, historique propre à chaque visite de la page ;
  la page de config ne garde que le flag `ChatEnabled`. Outil admin : PAS
  d'`EnableInUserMenu` (la sécurité est celle d'Emby — seuls les admins voient la section
  Serveur ; l'endpoint re-vérifie de toute façon `IsAdministrator`). Piège de nommage :
  le serveur trie toutes les pages de plugins par DisplayName et le dashboard lie le
  plugin de la liste à sa première page triée — un DisplayName « Chat LLM AI » triait
  AVANT «LLM_AI» (la page de config) et volait le clic du plugin ; « LLM_AI Chat »
  (règle du préfixe) rétablit la page de config comme page liée.
  Config : `ChatEnabled` (défaut `true`, opt-out).
  **Interactive chat with the AI assistant** (`ChatApiService` +
  `LlmAgentService.RunChatAsync` + `LlmRunner.RunChatAsync`): multi-turn conversation
  with the LLM agent right on the config page (`POST /Plugins/LLMAI/Chat`, admin-only).
  Reuses **all existing tools** (TV guide, library, TMDB/TVDB, web, Showbizz,
  `system_audit` — remediation stays gated by `AuditRemediationEnabled`) and the
  **configured LLM backend priorities** — no new tool, no backend change. The server is
  stateless: the page keeps the history (user/assistant turns only, capped at 40) and
  re-posts it each turn; the system prompt — full tool documentation + RAG directives —
  is built server-side, injected **once** per conversation, and never sent back by the
  client (a forged "system" in the body is ignored). Replies are raw Markdown, rendered
  by the existing mini converter. The shared agent loop (`RunLoopAsync`) is unchanged
  for the recommendation/audit paths; the JSON repair message is now mode-aware
  (Markdown outside recommendations).
  **Dedicated page** "LLM_AI Chat" (`chat.html`/`chat.js`) in the admin menu (dashboard,
  "Server" section) — full-frame chat, history scoped to each page visit; the config
  page only keeps the `ChatEnabled` flag. Admin tool: NO `EnableInUserMenu` (security is
  Emby's — only admins see the Server section; the endpoint still re-checks
  `IsAdministrator`). Naming gotcha: the server sorts all plugin pages by DisplayName and
  the dashboard links a plugin in the list to its first sorted page — a "Chat LLM AI"
  DisplayName sorted BEFORE "LLM_AI" (the config page) and stole the plugin click;
  "LLM_AI Chat" (prefix rule) restores the config page as the linked page.
  Config: `ChatEnabled` (default `true`, opt-out).

### Modifié / Changed
- **Outil `new_releases` : généralisation multi-sources de `showbizz_new_releases`**
  (fichier `ShowbizzTool.cs` renommé `NewReleasesTool.cs`). Le scraper « nouveautés »
  n'est plus lié à Showbizz.net ni au Québec : toute source web devient utilisable.
  **`new_releases` tool: multi-source generalization of `showbizz_new_releases`**
  (`ShowbizzTool.cs` renamed to `NewReleasesTool.cs`). The new-releases scraper is no
  longer tied to Showbizz.net or Québec: any web source works.
  - **Config `NewReleaseSources`** (une source par ligne, remplace la paire
    `ShowbizzUrl`/`ShowbizzPattern`) : URL seule = flux **RSS 2.0/Atom
    auto-détecté** (XDocument, aucun paquet externe) ; `URL :: @showbizz` =
    extracteur Showbizz.net intégré (blocs « Saison 1 », inchangé) ; `URL :: regex .NET`
    = extraction personnalisée **par source** (groupe `title` requis, `url`/`date`
    optionnels). Regex invalide → Warn + source ignorée (l'outil ne lève jamais).
    Vide = outil désactivé.
    **`NewReleaseSources` config** (one source per line, replaces the
    `ShowbizzUrl`/`ShowbizzPattern` pair): bare URL = auto-detected **RSS 2.0/Atom
    feed** (XDocument, no external package); `URL :: @showbizz` = built-in
    Showbizz.net extractor ("Saison 1" blocks, unchanged); `URL :: .NET regex` =
    custom extraction **per source** (required `title` group, optional
    `url`/`date`). Invalid regex → Warn + source skipped (the tool never throws).
    Empty = tool disabled.
  - **Migration transparente** : tant que `NewReleaseSources` n'a jamais été
    sauvegardé, son getter reconstruit la liste équivalente depuis l'ancienne paire
    (`ShowbizzUrl` + sources Showbizz.net par défaut, regex globale appliquée à
    toutes les sources si elle existait) — comportement strictement préservé, et la
    page de config affiche/sauvegarde la liste migrée.
    **Transparent migration**: until `NewReleaseSources` has ever been saved, its
    getter rebuilds the equivalent list from the legacy pair (`ShowbizzUrl` + the
    default Showbizz.net sources, global regex applied to all sources if it
    existed) — behavior strictly preserved, and the config page displays/saves the
    migrated list.
  - **Alias `showbizz_new_releases`** : l'ancien nom reste enregistré (transfert
    vers le nouvel outil) pour ne pas casser les prompts sauvegardés ; les défauts
    `ScheduleTask`/`ScheduleTaskMovies` citent désormais `new_releases`. Le champ
    de sortie `showbizz_match` garde son nom (contrat JSON persisté).
    **`showbizz_new_releases` alias**: the old name stays registered (forwards to
    the new tool) so saved prompts keep working; the `ScheduleTask`/
    `ScheduleTaskMovies` defaults now mention `new_releases`. The `showbizz_match`
    output field keeps its name (persisted JSON contract).
  - **Cache 24h invalidé par la config** : la clé de cache est le SHA256 des
    sources effectives — modifier la liste re-scrappe **sans redémarrer Emby**
    (supprime le piège « restart pour re-tester »).
    **Config-invalidated 24h cache**: the cache key is the SHA256 of the effective
    sources — editing the list re-scrapes **without restarting Emby** (removes the
    "restart to re-test" gotcha).
  - **Description dynamique** (première du plugin) : générique, sans mention de
    Showbizz/S01E01, liste les hôtes configurés — le LLM sait ce que l'outil
    retourne, d'où qu'il soit installé. Entités HTML décodées via
    `WebUtility.HtmlDecode` (corrige `&#039;` des titres Wikipedia).
    **Dynamic description** (the plugin's first): generic, no Showbizz/S01E01
    wording, lists the configured hosts — the LLM knows what the tool returns
    wherever it is installed. HTML entities decoded via
    `WebUtility.HtmlDecode` (fixes Wikipedia titles' `&#039;`).
- **Surfaces natives des recommandations** — trois leviers opt-in (générés par la
  tâche planifiée) pour exposer les recos directement dans Emby, au-delà de la
  page web `recommendations.html` :
  - **Bibliothèque `.strm`** (`StrmLibraryGenerator`, options `StrmLibraryEnabled` /
    `StrmLibraryName`, jeton auto-généré `StrmSecret`) : écrit une carte
    `.strm`+`.nfo`+poster par reco du **record bucket** dans une bibliothèque Emby
    dédiée. Lire une carte déclenche `GET /Plugins/LLMAI/Activate`, crée
    l'enregistrement (`AutoProgrammer.ProgramOneAsync`) puis stream un clip de
    confirmation `recording_activated.mp4`. Endpoint `[Unauthenticated]` (les lecteurs
    n'ont pas de token Emby), gated par `StrmSecret`. Alternative manuelle à
    l'auto-programmation (les deux cohabitent, dedup anti-timers en double).
  - **Genre `AI Tonight`** (`AiGenreTagger`, option `TonightGenreTagEnabled`) :
    étiquette les items Emby du **watch bucket** (enregistrements non visionnés +
    bibliothèque) avec le genre `AI Tonight` → l'usager filtre sur ce genre dans
    n'importe quel client. Modifie les métadonnées réelles (`Genres`), réajouté au
    prochain run si un refresh l'efface. Scope isolé du genre `AI Suggestion` de
    la bibliothèque `.strm`.
  - **Collection `AI Tonight`** (`AiTonightCollectionManager`, option
    `TonightCollectionEnabled`) : maintient un BoxSet `AI Tonight` (non
    destructif : items référencés, jamais copiés/déplacés) agrégeant les recos
    inter-bibliothèques. Peuplé sur les runs frais, vidé chaque nuit par la tâche de
    nettoyage. Indépendant du genre (les deux cohabitent). Vérifié :
    `CreateCollection(ParentId=0)` ressort bien dans la liste des Collections.
  - **Tâche de nettoyage** (`AiTonightCleanupTask`, quotidienne 03:00) : retire le
    genre `AI Tonight` de tous les items **et** vide la collection chaque jour
    (toujours active, non gatingée — balaie les restes).
  **Native recommendation surfaces** — three opt-in levers (scheduled-task driven)
  exposing recos directly in Emby beyond the `recommendations.html` web page:
  - **`.strm` library** (`StrmLibraryGenerator`, `StrmLibraryEnabled` /
    `StrmLibraryName`, auto-generated `StrmSecret` token): writes a
    `.strm`+`.nfo`+poster card per **record bucket** reco into a dedicated Emby
    library. Playing a card hits `GET /Plugins/LLMAI/Activate`, creates the
    recording (`AutoProgrammer.ProgramOneAsync`) then streams the
    `recording_activated.mp4` confirmation clip. `[Unauthenticated]` endpoint
    (players carry no Emby token), gated by `StrmSecret`. Manual alternative to
    auto-programming (both coexist, dedup prevents duplicate timers).
  - **`AI Tonight` genre** (`AiGenreTagger`, `TonightGenreTagEnabled`): tags the
    **watch bucket** Emby items (unwatched recordings + library) with the
    `AI Tonight` genre → filter on it in any client. Mutates real metadata
    (`Genres`), re-added on the next run if a refresh drops it. Isolated from the
    `.strm` library's `AI Suggestion` genre.
  - **`AI Tonight` collection** (`AiTonightCollectionManager`,
    `TonightCollectionEnabled`): maintains an `AI Tonight` BoxSet (non-destructive:
    items referenced, never copied/moved) aggregating cross-library recos.
    Populated on fresh runs, emptied nightly by the cleanup task. Independent of
    the genre (both coexist). Verified: `CreateCollection(ParentId=0)` shows up
    correctly in the Collections list.
  - **Cleanup task** (`AiTonightCleanupTask`, daily 03:00): removes the `AI Tonight`
    genre from all items **and** empties the collection daily (always active, not
    gated — sweeps leftovers).
- **Bibliothèque `.strm` — enrichissement du `.nfo`** (`StrmLibraryGenerator`) :
  - le `<plot>` commence désormais par le **synopsis natif de l'EPG** (langue d'origine
    du programme, lu sur le `BaseItem` EPG sous-jacent), suivi de l'enrichissement
    (synopsis TMDB + raison LLM + méta + diffusion à venir + lien fiche EPG) dans la
    **langue de l'usager** (`ResponseLanguage`) — « best of both worlds » : l'usager lit
    l'enrichissement dans sa langue tout en gardant le synopsis EPG d'origine. Aucune
    déduction de la langue du programme nécessaire.
  - ajout des **External IDs** `<tmdbid>` / `<imdbid>` / `<tvdbid>` au `.nfo` quand
    ils sont disponibles (récupérés via `append_to_response=external_ids` de TMDB) →
    Emby génère les **liens profonds** TMDB/IMDb/TVDB sur la fiche de la carte.
  **`.strm` library — `.nfo` enrichment** (`StrmLibraryGenerator`):
  - the `<plot>` now starts with the **EPG-native overview** (the program's original
    language, read from the underlying EPG `BaseItem`), followed by the enrichment
    (TMDB overview + LLM reason + meta + upcoming airings + EPG page link) in the
    **user's language** (`ResponseLanguage`) — "best of both worlds": the user reads
    the enrichment in their language while keeping the original EPG overview. No need
    to deduce the program's language.
  - added **External IDs** `<tmdbid>` / `<imdbid>` / `<tvdbid>` to the `.nfo` when
    available (fetched via TMDB's `append_to_response=external_ids`) → Emby generates
    the TMDB/IMDb/TVDB **deep links** on the card's detail page.
- **`TmdbLookupTool`** — refactor + nouveaux points d'entrée pour la résolution
  d'orphelins : extraction de `FetchDetailAsync` (détail `/movie|tv/{id}` +
  `external_ids`, facteur commun recherche/`/find`), `LookupMetaMultiLangAsync`
  (recherche multi-langue, S1), `FindByExternalIdAsync` (`/find/{id}` par
  `imdb_id`/`tvdb_id`, valide un id proposé), `LookupMetaByIdAsync` (détail par id
  TMDB, valide un `tmdb_id` proposé), `CleanEpgTitle` (regex de nettoyage de titre
  EPG bruité). `TmdbMeta` gagne `TmdbId` / `ImdbId` / `TvdbId`.
  **`TmdbLookupTool`** — refactor + new entry points for orphan resolution: extracted
  `FetchDetailAsync` (detail `/movie|tv/{id}` + `external_ids`, shared by search/`/find`),
  `LookupMetaMultiLangAsync` (multi-language search, S1), `FindByExternalIdAsync`
  (`/find/{id}` by `imdb_id`/`tvdb_id`, validates a proposed id), `LookupMetaByIdAsync`
  (detail by TMDB id, validates a proposed `tmdb_id`), `CleanEpgTitle` (regex cleanup of
  noisy EPG titles). `TmdbMeta` gains `TmdbId` / `ImdbId` / `TvdbId`.
- **`LlmRunner.ResolveIdsAsync`** : appel LLM one-shot (sans outils, multi-backend
  avec repli) qui propose un id IMDb/TMDB + titre original + année + niveau de
  confiance à partir d'un titre EPG + overview + chaîne. Calqué sur
  `TranslateTextAsync`. Best-effort (retourne un `IdGuess` vide en cas d'échec). La
  proposition est **toujours validée côté `OrphanIdentifyTask`** via TMDB — jamais
  appliquée telle quelle.
  **`LlmRunner.ResolveIdsAsync`**: one-shot LLM call (no tools, multi-backend with
  fallback) proposing an IMDb/TMDB id + original title + year + confidence level from
  an EPG title + overview + channel. Modeled on `TranslateTextAsync`. Best-effort
  (returns an empty `IdGuess` on failure). The proposal is **always validated by
  `OrphanIdentifyTask`** via TMDB — never applied as-is.
- **Auto-programmation** (`AutoProgrammer`, option `AutoProgram` — défaut `false`, opt-in
  explicite) : après chaque run (tâche planifiée **et** login), les recommandations du
  **record bucket** (programmes EPG à venir non possédés, non déjà programmés, hors
  `DroppedTitles`) sont automatiquement programmées en enregistrement (SeriesTimer pour
  une série, Timer unique pour un film). Elles ressortent dans le **guide EPG natif** avec
  un badge d'enregistrement — le seul highlight fiable sur tous les clients TV.
  **Auto-programming** (`AutoProgrammer`, `AutoProgram` option — default `false`, explicit
  opt-in): after each run (scheduled task **and** login), the **record bucket** (upcoming
  EPG programs not owned, not already scheduled, outside `DroppedTitles`) is auto-scheduled
  as recordings (SeriesTimer for series, single Timer for movies). They surface in the
  **native EPG guide** with a record badge — the only reliable highlight across TV clients.
- **Popup au login** (`TonightLoginService : IServerEntryPoint`, option `LoginPopup` —
  défaut `true`, **indépendant** de `AutoProgram`) : à la connexion d'un usager, un **toast**
  (`SendMessageCommand`, gated `DisplayMessage`) signale ce qu'il peut regarder ce soir
  (enregistrements non visionnés / bibliothèque), + une **cloche** persistante (deep-link)
  en repli. Pattern `Emby.ComSkipper`. Garde-fou **in-flight** : un seul run par usager
  même sur plusieurs appareils.
  **Login popup** (`TonightLoginService : IServerEntryPoint`, `LoginPopup` option — default
  `true`, **independent** of `AutoProgram`): on user login, a **toast** (`SendMessageCommand`,
  gated `DisplayMessage`) surfaces tonight's watch-bucket (unwatched recordings / library),
  + a persistent **bell** (deep-link) fallback. `Emby.ComSkipper` pattern. **In-flight**
  guard: a single run per user even across multiple devices.
  - `LoginPopupSeconds` (défaut 8) règle la durée du toast. / Sets the toast duration.
- **Gating `AutoProgram` (règle absolue)** : aucun timer n'est créé tant que
  `cfg.AutoProgram == false` (vérifié dans les deux chemins avant tout appel à
  `AutoProgrammer.Program`). / No timer is created while `AutoProgram == false` (checked in
  both paths before any `AutoProgrammer.Program` call).
- **Clip de confirmation universel** : `recording_activated.mp4` (embarqué, streamé par
  `ActivateApiService` à la lecture d'une carte `.strm`) remplacé par une version **sans
  texte ni audio** (8 s, 1280×720, ~545 Ko) — appropriée à toutes les langues, cohérente
  avec la nouvelle option `ResponseLanguage`. L'ancienne version (10 s, 1080p, ~931 Ko,
  texte français, audio quasi-silencieux à 2 kbps) reste récupérable via
  `git show HEAD:recording_activated.mp4`. Aucun changement de code (le clip est embarqué
  par son nom) ; commentaires de code et docs mis à jour (durée/résolution).
  **Universal confirmation clip**: `recording_activated.mp4` (embedded, streamed by
  `ActivateApiService` on `.strm` card play) replaced with a **no-text, no-audio** version
  (8 s, 1280×720, ~545 KB) — suitable for all languages, consistent with the new
  `ResponseLanguage` option. The old version (10 s, 1080p, ~931 KB, French text,
  near-silent 2 kbps audio) remains recoverable via
  `git show HEAD:recording_activated.mp4`. No code change (the clip is embedded by name);
  code comments and docs updated (duration/resolution).

### Modifié / Changed
- `LlmAgentService` : deux paramètres optionnels (`roleIntro`, `formatSection`) au
  constructeur pour surcharger l'intro du rôle et le bloc de format de sortie (le path
  d'audit passe une intro d'audit et supprime le bloc « FORMAT DES RECOMMANDATIONS »).
  Les appelants recommandation existants ne passent rien → comportement inchangé.
  `LlmAgentService`: two optional constructor params (`roleIntro`, `formatSection`) to
  override the role intro and the output-format block (the audit path passes an audit
  intro and suppresses the "FORMAT DES RECOMMANDATIONS" block). Existing recommendation
  call sites pass nothing → behavior unchanged.
- `LlmRunner` : path d'audit dédié ajouté (`BuildAuditTools`, `RunAuditAsync`,
  `RunAuditDeterministicAsync`, `ChatWithFallbackAsync`) sans modifier le constructeur
  ni le path recommandation (zéro impact sur `LlmScheduledTask` / `TonightApiService`).
  `LlmRunner`: dedicated audit path added (`BuildAuditTools`, `RunAuditAsync`,
  `RunAuditDeterministicAsync`, `ChatWithFallbackAsync`) without changing the
  constructor or the recommendation path (zero impact on `LlmScheduledTask` /
  `TonightApiService`).
- `AutoProgrammer` : logique par-reco extraite en `internal ProgramOneAsync(Reco,
  HashSet, HashSet, ct)` (retourne `OneOutcome`) — réutilisée par la boucle de la
  tâche planifiée **et** l'endpoint `/Plugins/LLMAI/Activate` (reco unique déclenchée
  à la lecture d'une carte `.strm`). `Reco` porte désormais `Reason`/`Channel`/
  `Start` pour la génération NFO.
  `AutoProgrammer`: per-reco logic extracted into `internal ProgramOneAsync(Reco,
  HashSet, HashSet, ct)` (returns `OneOutcome`) — shared by the scheduled-task loop
  **and** the `/Plugins/LLMAI/Activate` endpoint (single reco fired on `.strm` card
  play). `Reco` now carries `Reason`/`Channel`/`Start` for NFO generation.
- `ICollectionManager` injecté dans `TonightService` (et ses appelants
  `TonightApiService` / `TonightLoginService`) ainsi que dans `AiTonightCleanupTask`.
  `ICollectionManager` injected into `TonightService` (and its callers
  `TonightApiService` / `TonightLoginService`) and into `AiTonightCleanupTask`.
- Extraction de la génération « À regarder ce soir » dans `TonightService` (interne),
  partagée par `TonightApiService` (endpoint HTTP) et `TonightLoginService` (déclencheur
  login), avec cache par usager statique commun. `TonightApiService` devient une couche HTTP
  fine.
  "Watch tonight" generation extracted into `TonightService` (internal), shared by
  `TonightApiService` (HTTP endpoint) and `TonightLoginService` (login trigger), with a
  shared static per-user cache. `TonightApiService` becomes a thin HTTP layer.
- `GetEmbyInfoTool.DroppedTitlesSet` / `Norm` élargis à `internal` pour réutilisation par
  `AutoProgrammer` (matching de déduplication cohérent avec l'exclusion EPG).
  `GetEmbyInfoTool.DroppedTitlesSet` / `Norm` widened to `internal` for reuse by
  `AutoProgrammer` (dedup matching consistent with the EPG exclusion).

### Corrigé / Fixed
- **Recos FILMS sans id EPG (run 3am 2026-08-31 — 0/6 matchées)** : avec
  `ResponseLanguage=English`, le LLM (gemma4:26b local) émettait les **titres TMDB
  anglais** (« Big Night ») au lieu des titres EPG français (« À table! ») dans sa
  réponse finale — l'ancienne directive disait que les titres « restent dans leur
  langue d'origine », ce que le modèle interprétait comme « utiliser le titre en
  langue d'origine de l'œuvre » (juste après avoir vu les titres anglais dans les
  résultats `tmdb_lookup`). L'enrichissement (`EnrichRecommendations`), qui
  rapproche par titre normalisé, matchait donc 0/6 → recos sans `id` : pas de
  poster, programmation impossible, exclues du record bucket (pas de carte
  `.strm`, pas de timer auto-program, pas de badge). Les séries du même run
  matchaient 9/9 par coïncidence (chaînes anglophones — titres EPG déjà anglais).
  Triple correctif :
  (a) **directive reformulée sans ambiguïté** (`BuildLanguageDirective`) : les
  titres et noms de chaînes ne se traduisent JAMAIS — recopier le `title`
  EXACTEMENT tel qu'il figure dans les résultats de `get_emby_info`, même si
  `tmdb_lookup` renvoie le titre dans une autre langue ;
  (b) **repli chaîne+heure** dans `EnrichRecommendations` (`FindByChannelStart`) :
  quand le titre ne matche pas, la reco est rattachée au programme EPG diffusé sur
  la même chaîne (nom normalisé) à la même heure (±10 min, la plus proche gagne) —
  une chaîne ne diffuse qu'un programme à une heure donnée, et `channel`/`start`
  sont des champs obligatoires que le LLM recopie correctement ; chaque
  rattachement est logué (Info) avec le titre EPG retrouvé ;
  (c) **log Warn quand 0 reco matchée** (avant : Info silencieux) avec la cause
  probable et l'impact (pas de programmation, ni carte `.strm`, ni badge) ;
  (d) **porte de validation stricte** dans `EnrichRecommendations` : une reco
  *intracable* au pool EPG que le plugin a lui-même fourni au LLM — ni par
  titre, ni par chaîne+heure, ni par id de programme recopié du pool — est
  **écartée du payload** avec un Warn listant les titres écartés. On ne
  publie que ce qui peut être rattaché à un programme EPG réellement envoyé
  au LLM : l'intracable est soit une hallucination (programme jamais dans le
  pool), soit une reco entièrement reformulée — dans les deux cas elle n'est
  pas programmable et ne passerait de toute façon aucun garde-fou du record
  bucket ; autant l'écarter plutôt que d'afficher une carte morte. La ligne
  de bilan compte maintenant les écartées. Une reco purement « Showbizz »
  (nouveauté sans entrée EPG) est écartée aussi — délibéré : sans programme
  EPG, elle ne peut pas être enregistrée.
  **MOVIE recos with no EPG id (3am run 2026-08-31 — 0/6 matched)**: with
  `ResponseLanguage=English`, the LLM (local gemma4:26b) emitted the **English TMDB
  titles** ("Big Night") instead of the French EPG titles ("À table!") in its final
  reply — the old directive said titles "stay in their original language", which
  the model read as "use the work's original-language title" (right after seeing
  the English titles in the `tmdb_lookup` results). Enrichment
  (`EnrichRecommendations`), which matches by normalized title, therefore matched
  0/6 → recos with no `id`: no poster, no possible scheduling, excluded from the
  record bucket (no `.strm` card, no auto-program timer, no badge). The same run's
  series matched 9/9 by coincidence (English-language channels — EPG titles were
  already English). Triple fix:
  (a) **unambiguous directive wording** (`BuildLanguageDirective`): titles and
  channel names are NEVER translated — copy the `title` EXACTLY as it appears in
  the `get_emby_info` results, even when `tmdb_lookup` returns the title in
  another language;
  (b) **channel+time fallback** in `EnrichRecommendations`
  (`FindByChannelStart`): when the title doesn't match, the reco is attached to
  the EPG program airing on the same channel (normalized name) at the same time
  (±10 min, nearest wins) — a channel broadcasts only one program at a given time,
  and `channel`/`start` are mandatory fields the LLM copies correctly; each
  attachment is logged (Info) with the recovered EPG title;
  (c) **Warn log when 0 recos match** (previously a silent Info) with the likely
  cause and impact (no scheduling, no .strm card, no badge);
  (d) **strict validation gate** in `EnrichRecommendations`: a reco *untraceable*
  to the EPG pool the plugin itself fed the LLM — not by title, not by
  channel+time, not by a program id copied from the pool — is **dropped from the
  payload** with a Warn listing the dropped titles. Only what can be tied back
  to an EPG program actually sent to the LLM gets published: untraceable means
  either a hallucination (a program never in the pool) or a fully rewritten
  reco — in both cases it cannot be scheduled and would fail every record-bucket
  guard anyway; better dropped than a dead card. The summary line now counts the
  dropped recos. A purely "Showbizz" reco (new release with no EPG entry) is
  dropped too — deliberate: without an EPG program it cannot be recorded.
- **« ç » et accents : les titres accentués ne matchaient jamais leurs
  variantes non accentuées** (corrigé 2026-08-30, suspect n°1 du cas
  « Comment tuer son mari en 10 leçons ») : les deux normalisateurs de titres
  du plugin pliaient mal les diacritiques, chacun dans son sens :
  (a) `GetEmbyInfoTool.Norm` (exclusion biblio de `epg_series`/`epg_movies`,
  drop list, dédup) SUPPRIMAIT les diacritiques au lieu de les
  translittérer — « leçons » → « le**ons** » ≠ « lecons » ; (b)
  `LlmRunner.NormTitle` (rapprochement EPG↔reco et reco↔bibliothèque)
  gardait le caractère accentué — « leçons » ≠ « lecons » aussi. Or l'EPG
  Gracenote porte le titre accentué tandis que l'item bibliothèque porte
  souvent la variante sans accents (nom de fichier, métadonnées du provider) :
  l'exclusion « déjà possédé » ratait donc systématiquement ces titres.
  Nouveau pliage partagé `GetEmbyInfoTool.FoldAscii` (décomposition Unicode
  FormD + retrait des marques combinantes : é/è/ê→e, ç→c, à→a… MAJUSCULES
  comprises, tout accent latin décomposable) + map manuel des lettres NON
  décomposables (œ→oe, æ→ae, ø→o, đ→d, ł→l, ß→ss, ð→d, þ→th), appliqué dans
  `Norm` ET `NormTitle` — « Comment tuer son mari en 10 leçons » ≡ « … en 10
  lecons », vérifié empiriquement sur accents multiples, ligatures,
  nordiques/germaniques, apostrophes et titres identiques. Complément du
  garde-fou IMDb id (voir entrée précédente) : le titre rapproche maintenant
  aussi les variantes accentuées.
  **"ç" and accents: accented titles never matched their unaccented
  variants** (fixed 2026-08-30, prime suspect of the "Comment tuer son mari
  en 10 leçons" case): the plugin's two title normalizers folded diacritics
  badly, each in its own direction: (a) `GetEmbyInfoTool.Norm` (library
  exclusion of `epg_series`/`epg_movies`, drop list, dedup) DELETED
  diacritics instead of transliterating them — "leçons" → "le**ons**" ≠
  "lecons"; (b) `LlmRunner.NormTitle` (EPG↔reco and reco↔library matching)
  kept the accented character — "leçons" ≠ "lecons" too. Since Gracenote's
  EPG carries the accented title while the library item often carries the
  unaccented variant (filename, provider metadata), the already-owned
  exclusion systematically missed those titles. New shared folding
  `GetEmbyInfoTool.FoldAscii` (Unicode FormD decomposition + combining-mark
  stripping: é/è/ê→e, ç→c, à→a… + œ→oe, æ→ae ligatures), applied in both
  `Norm` and `NormTitle` — "Comment tuer son mari en 10 leçons" ≡ "… en 10
  lecons", empirically verified across multiple accents, ligatures,
  apostrophes and identical titles. Complements the IMDb-id guard (previous
  entry): title matching now also matches accented variants. The manual map
  covers the letters WITHOUT a FormD decomposition (œ→oe, æ→ae, ø→o, đ→d,
  ł→l, ß→ss, ð→d, þ→th).
- **Reco d'enregistrer un titre déjà possédé malgré l'id IMDb trouvé par le LLM**
  (corrigé 2026-08-30, cas « Comment tuer son mari en 10 leçons ») : le LLM
  établissait l'id IMDb du contenu via ses outils (tt22335046) mais (1) le format
  de reco n'avait **pas de champ imdb_id** — l'id était perdu dans la réponse
  finale, et (2) la tâche planifiée ne rapprochait **jamais** les recos de la
  bibliothèque (`EnrichWithLibrary` n'était appelé que par la page Tonight) —
  donc `library_id` restait vide et les garde-fous du record bucket
  (.strm/Auto-program/badges), qui reposent sur `library_id`, laissaient passer
  le film déjà possédé. Triple correctif :
  (a) le format des recos demande désormais un champ **`imdb_id`** facultatif
  (uniquement si un outil l'a établi) ;
  (b) `EnrichWithLibrary` gagne un repli **par id IMDb** : si le titre ne matche
  pas, l'item bibliothèque est résolu via `InternalItemsQuery.AnyProviderIdEquals`
  (clé Provider « Imdb », films ET séries) — rapprochement indépendant du titre,
  le plus fiable possible ; l'id est normalisé (accepte « 1234567 » → « tt1234567 »,
  minuscules, 7–8 chiffres — protège des ids hallucinés) ;
  (c) la tâche planifiée appelle `EnrichWithLibrary` sur le payload fusionné
  avant persistance : une reco possédée reçoit `library_id` → **exclue du
  record bucket** et affichée avec le bouton « Regarder (bibli.) » (déjà géré
  par recommendations.js). La détection du « déjà possédé » ne repose plus
  uniquement sur la classification `source` du LLM.
  **Reco to record an already-owned title despite the LLM finding its IMDb id**
  (fixed 2026-08-30, "Comment tuer son mari en 10 leçons" case): the LLM
  established the content's IMDb id via its tools (tt22335046) but (1) the
  reco format had **no imdb_id field** — the id was lost in the final reply,
  and (2) the scheduled task **never** matched recos against the library
  (`EnrichWithLibrary` was only called by the Tonight page) — so `library_id`
  stayed empty and the record-bucket guards (.strm/Auto-program/badges), which
  rely on `library_id`, let the already-owned film through. Triple fix:
  (a) the reco format now asks for an optional **`imdb_id`** field (only when
  established by a tool);
  (b) `EnrichWithLibrary` gains an **IMDb-id fallback**: when the title
  doesn't match, the library item is resolved via
  `InternalItemsQuery.AnyProviderIdEquals` (Provider key "Imdb", movies AND
  series) — a title-independent match, the most reliable possible; the id is
  normalized (accepts "1234567" → "tt1234567", lowercase, 7–8 digits — guards
  against hallucinated ids);
  (c) the scheduled task runs `EnrichWithLibrary` on the merged payload before
  persisting: an owned reco gets `library_id` → **excluded from the record
  bucket** and rendered with the "Watch (library)" button (already handled by
  recommendations.js). Owned-detection no longer relies solely on the LLM's
  `source` classification.
- **Cartes .strm sans poster alors que l'EPG en affiche une** (corrigé 2026-08-30,
  cas « Moonflower Murders on Masterpiece ») : le repli poster
  `TryCopyProgramPoster` ne gérait que les fichiers locaux — mais les programmes
  EPG Gracenote/TMS référencent presque toujours une **URL distante**
  (`ebyl.tmsimg.com/…`) dans le champ Path de leur image Primary. Le garde
  `File.Exists(URL)` échouait donc silencieusement (`return false` sans log) et la
  carte restait sans affiche quand le lookup TMDB échouait aussi (titres suffixés
  du type « … on Masterpiece » introuvables sur TMDB). Le repli télécharge
  désormais les URL http(s) via le `HttpClient` partagé (les fichiers locaux
  restent copiés) et **chaque garde logue sa raison** (programme introuvable,
  sans image, chemin absent, dossier absent…) — plus de `return false` muet.
  Complément : si le lookup TMDB échoue sur le titre complet, le générateur
  retente **une fois** sans le suffixe de chaîne « on … » (convention Gracenote/
  PBS : « Moonflower Murders on Masterpiece » → entrée TMDB « Moonflower
  Murders ») — la forme complète est toujours essayée d'abord, un titre
  légitime contenant « on » n'est donc tronqué qu'après échec ; le titre de la
  carte (dossier/.nfo) reste inchangé, seule la requête est nettoyée.
  **STRM cards with no poster while the EPG shows one** (fixed 2026-08-30,
  "Moonflower Murders on Masterpiece" case): the poster fallback
  `TryCopyProgramPoster` only handled local files — but Gracenote/TMS EPG
  programs almost always reference a **remote URL** (`ebyl.tmsimg.com/…`) in
  their Primary image's Path field. The `File.Exists(URL)` guard failed
  silently (`return false`, no log) and the card was left posterless whenever
  the TMDB lookup also failed (suffixed titles like "… on Masterpiece" have no
  TMDB match). The fallback now downloads http(s) URLs via the shared
  `HttpClient` (local files are still copied) and **every guard logs its
  reason** (missing program, no image, missing path, missing folder…) — no more
  silent `return false`. Complement: if the TMDB lookup fails on the full
  title, the generator retries **once** with the "on …" channel suffix
  stripped (Gracenote/PBS convention: "Moonflower Murders on Masterpiece" →
  TMDB entry "Moonflower Murders") — the full form is always tried first, so a
  legitimate title containing "on" is only stripped after a failed lookup; the
  card title (folder/.nfo) is unchanged, only the query is cleaned.
- **Crash NaN/Infinity en JSON** : `disk_storage` divisait par `TotalSize == 0` (volumes
  tels `/var/snap/lxd`, `/sys/…`), produisant `NaN`/`∞` que `System.Text.Json` refusait de
  sérialiser — l'exception escapait le digest déterministe et faisait échouer tout l'audit.
  Triple garde : (1) `s_json` avec `JsonNumberHandling.AllowNamedFloatingPointLiterals`
  (émet des littéraux au lieu de lancer), (2) `used_pct` gardé `total > 0` (sinon 0),
  (3) chaque sonde du digest enveloppée dans un `try/catch` résilient (`SectionAsync`/
  `SectionSync`) — une sonde défaillante ne tue plus les autres (`OperationCanceledException`
  reste relancé).
  **NaN/Infinity JSON crash**: `disk_storage` divided by `TotalSize == 0` (volumes like
  `/var/snap/lxd`, `/sys/…`), yielding `NaN`/`∞` that `System.Text.Json` refused to
  serialize — the exception escaped the deterministic digest and failed the whole audit.
  Triple guard: (1) `s_json` with `JsonNumberHandling.AllowNamedFloatingPointLiterals`
  (emits literals instead of throwing), (2) `used_pct` guarded `total > 0` (else 0),
  (3) every digest probe wrapped in a resilient `try/catch` (`SectionAsync`/`SectionSync`)
  — a failing probe no longer takes down the rest (`OperationCanceledException` rethrown).
- **Repli chemins Emby (GetSystemInfo NRE)** : sur certaines versions Emby
  (p.ex. 4.9.5.0), `GetSystemInfo(IPAddress.Loopback, ct)` lève une `NullReferenceException`,
  rendant les chemins système (et donc les logs) inaccessibles. Les chemins Emby sont
  désormais résolus via `IServerConfigurationManager` (résolu par le host) puis lecture de
  `.ApplicationPaths` par réflexion sur le nom (program data, cache, transcode temp,
  métadonnées, items par nom, dossier racine) ; le chemin des journaux est déduit par
  convention (`<ProgramDataPath>/logs`). Couverture complète, seules les interfaces
  réseau manquent (signalé honnêtement dans le rapport). `SystemInfo` est mis en cache
  (une seule tentative par run). Ajout de l'action **`system_config`** exposant
  `IServerConfigurationManager.Configuration` (la `ServerConfiguration` entière —
  cross-OS, lu en cours de processus, pas d'analyse XML de `system.xml`).
  **Emby path fallback (GetSystemInfo NRE)**: on some Emby versions (e.g. 4.9.5.0),
  `GetSystemInfo(IPAddress.Loopback, ct)` throws a `NullReferenceException`, making system
  paths (and thus logs) unreachable. Emby paths are now resolved via
  `IServerConfigurationManager` (resolved through the host) then reading
  `.ApplicationPaths` by name reflection (program data, cache, transcode temp, metadata,
  items by name, root folder); the log path is derived by convention
  (`<ProgramDataPath>/logs`). Full coverage, only network interfaces are missing (honestly
  noted in the report). `SystemInfo` is cached (one attempt per run). Added the
  **`system_config`** action exposing `IServerConfigurationManager.Configuration` (the full
  `ServerConfiguration` — cross-OS, read in-process, no `system.xml` XML parsing).
- **Cadrage du repli GetSystemInfo dans le rapport** : le repli (GetSystemInfo lève une
  NRE sur certaines versions Emby) était signalé par l'LLM comme une exception critique
  à investiguer en priorité haute — faux, puisque les chemins/logs/config sont couverts
  par le repli `IServerConfigurationManager.ApplicationPaths`. Le `note` du repli précise
  désormais « COUVERT et ATTENDU — ne pas signaler comme défaut critique », et les prompts
  d'audit (single + déterministe) ajoutent une section « REPLI GetSystemInfo (À CONNAÎTRE) »
  demandant au LLM de le traiter au plus comme un ✅/ℹ️ info, jamais en « Priorité Haute ».
  **GetSystemInfo fallback framing in the report**: the fallback (GetSystemInfo throws an
  NRE on some Emby versions) was reported by the LLM as a critical exception to investigate
  at high priority — wrong, since paths/logs/config are covered by the
  `IServerConfigurationManager.ApplicationPaths` fallback. The fallback `note` now reads
  "COUVERT et ATTENDU — do not flag as a critical defect", and the audit prompts (single +
  deterministic) add a "REPLI GetSystemInfo (À CONNAÎTRE)" section instructing the LLM to
  treat it at most as a ✅/ℹ️ info, never as "High Priority".
- **Id Emby : Guid vs InternalId** (corrigé 2026-08-30, toutes les validations Tonight
  échouaient) : `BaseItem.Id` est un Guid que la couche REST/UI d'Emby refuse
  (`/emby/Items/{guid}/…` → 400/500 ; les ids acceptés sont les `InternalId` longs).
  Le plugin émettait des Guids vers l'LLM et l'UI dans plusieurs chemins — les recos
  issues du pool de repli bibliothèque ne passaient donc jamais la validation Tonight
  (« items bibli. introuvables : 3 », toutes supprimées, sauf le run où l'LLM omettait
  le champ `source`). Standardisation sur les chaînes **InternalId** à toutes les
  bornes LLM/UI + nouveau `ItemIdResolver.Resolve` (bilingue : accepte les longs ET
  les Guids historiques que le LLM peut échoer, jamais l'inverse). Touchés :
  `TonightService` (pool de repli `BuildLibraryFallbackPool`, validation
  `ValidateAndFilter` + normalisation pré-passe des ids résolubles), `LlmRunner`
  (`FindLibraryItemId` → enrichissement `library_id`/`image_url`), `GetEmbyInfoTool`
  (projections library/search/person/item + `ItemDetails`/`ItemPersons` bilingues),
  `AiGenreTagger`, `AiTonightCollectionManager`.
  **Emby ids: Guid vs InternalId** (fixed 2026-08-30, every Tonight validation was
  failing): `BaseItem.Id` is a Guid that Emby's REST/UI layer rejects
  (`/emby/Items/{guid}/…` → 400/500; accepted ids are the long `InternalId`s). The
  plugin emitted Guids to the LLM and UI in several paths — so library-fallback-pool
  recos never passed Tonight validation ("items bibli. introuvables : 3", all dropped,
  except runs where the LLM omitted the `source` field). Standardized on **InternalId**
  strings at all LLM/UI boundaries + new `ItemIdResolver.Resolve` (bilingual: accepts
  longs AND legacy Guids the LLM may echo back, never the reverse). Touched:
  `TonightService` (`BuildLibraryFallbackPool` fallback pool, `ValidateAndFilter`
  validation + pre-pass normalizing resolvable ids), `LlmRunner` (`FindLibraryItemId` →
  `library_id`/`image_url` enrichment), `GetEmbyInfoTool` (library/search/person/item
  projections + bilingual `ItemDetails`/`ItemPersons`), `AiGenreTagger`,
  `AiTonightCollectionManager`.

---

## [1.0.0.0] — 2026-08-27

### Ajouté / Added
- **Tâche planifiée globale** (`LlmScheduledTask`) : recommandations de **séries** et de
  **films à enregistrer**, basées sur l'EPG à venir, croisées avec la bibliothèque et les
  whitelists. Résultats stockés au niveau serveur + notifications Emby.
  Global **scheduled task** for series/movie recording recommendations from the upcoming
  EPG, cross-referenced with the library and whitelists; server-stored results + Emby
  notifications.
- **Section « À regarder ce soir »** (`TonightApiService`, endpoint
  `GET /Plugins/LLMAI/Tonight`) : recommandations **par usager à la demande**, croisant
  l'historique de visionnage, l'EPG du soir et les enregistrements récents non visionnés.
  Per-user on-demand "Watch tonight" recommendations combining watch history, tonight's
  EPG and recent unwatched recordings.
  - 3 sources : profil de goût, `epg_tonight`, enregistrements récents non visionnés.
  - **Réserve bibliothèque** (fallback) garantissant au moins `TonightMinRecommendations`
    recommandations même si l'EPG est vide.
  - Champ `source` (`live` / `recording` / `library`) pilotant les boutons des cartes
    (Programmer / Regarder en direct / Regarder / Regarder (bibli.) / Oublier).
  - **Mode compact** automatique pour les backends `OllamaLocal` (contexte réduit).
  - **Cache par usager** (TTL `TonightCacheHours`) + bouton Rafraîchir.
- **Backends LLM multi-source** : `OllamaLocal`, `OllamaCloud`, `Gemini`, avec priorité.
  Multi-source LLM backends with priority.
- **Outils LLM** : `get_emby_info` (10 actions dont `epg_tonight`), `tmdb_lookup`,
  `tvdb_search`, `web_search`, `web_fetch`, `showbizz_new_releases`.
- **i18n FR/EN** maison (`i18n.js`) : étiquettes de page, boutons, config.
- **Lecture** via le module AMD `playbackManager` d'Emby (Regarder / Regarder en direct).
- **Empaquetage & release** : `package.sh` (build + zip auto-suffisant), `install.sh`
  (installation utilisateur final), workflow GitHub Actions de release sur tag `v*`.

### Modifié / Changed
- Refactor de l'orchestration LLM dans une classe partagée `LlmRunner` (utilisée par la
  tâche planifiée et l'endpoint tonight) pour éviter la duplication.
  LLM orchestration extracted into shared `LlmRunner`.

[1.0.0.0]: ../../releases/tag/v1.0.0
[1.1.0.0]: ../../releases/tag/v1.1.0.0