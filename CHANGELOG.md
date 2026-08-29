# Changelog

Tous les changements notables de ce plugin sont documentés ici.
Le format s'inspire de [Keep a Changelog](https://keepachangelog.com/),
et ce projet adhère au [Semantic Versioning](https://semver.org/lang/fr/).

All notable changes to this plugin are documented here.
Format based on [Keep a Changelog](https://keepachangelog.com/),
and this project adheres to [Semantic Versioning](https://semver.org/).

---

## [Unreleased]

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
  planifiée quotidienne **04:00**) : repère les enregistrements DVR **non identifiés**
  (aucun id IMDb/TMDB/TVDB — souvent des titres québécois absents du catalogue
  TMDB/TVDB) et tente de les résoudre en deux stages :
  - **S1 — nettoyage + recherche multilingue** : le titre EPG est débarrassé de son
    bruit (`CleanEpgTitle` : HD, VOSTFR, « Rediff. », marqueurs saison/épisode,
    parenthèses) puis recherché sur TMDB en plusieurs langues (en-US = titre original,
    fr-FR = titre France, + langue de l'usager). Garde-fou de correspondance (titre
    normalisé + année).
  - **S2 — proposition LLM validée par TMDB** : le LLM propose un id IMDb/TMDB à partir
    du titre + overview + chaîne ; la proposition est **validée** via TMDB `/find`
    (`FindByExternalIdAsync`) ou détail par id (`LookupMetaByIdAsync`) — TMDB est la
    source de vérité, un id halluciné renvoie null.
  - **Application non destructive** : ne remplit que les ids provider absents, un
    `Overview` vide, des `Genres` vides, un poster `Primary` manquant. **Le `Name` EPG
    n'est jamais modifié** — il est **verrouillé** (`MetadataFields.Name`) pour
    préserver le titre d'origine (réutilisé plus tard pour scanner l'EPG). Les champs
    remplis sont aussi verrouillés (add-only — aucun verrou existant n'est retiré),
    reflétant la pratique manuelle de l'usager.
  - **Idempotence** via tags `llmai-identified` (résolu) / `llmai-needs-review`
    (irrésolu — marqué pour revue). **Dry-run** (`OrphanIdentifyDryRun`) : aucune
    écriture, log détaillé de la résolution proposée + bilan. Best-effort : un item en
    erreur n'interrompt jamais le passage. Scope : enregistrements DVR uniquement.
  **Orphan recording identification** (`OrphanIdentifyTask`, daily scheduled task
  **4 AM**): finds **unidentified** DVR recordings (no IMDb/TMDB/TVDB id — often Quebec
  titles missing from TMDB/TVDB) and tries to resolve them in two stages:
  - **S1 — cleanup + multi-language search**: the EPG title is stripped of noise
    (`CleanEpgTitle`: HD, VOSTFR, "Rediff.", season/episode markers, parentheses) then
    searched on TMDB in several languages (en-US = original title, fr-FR = France
    title, + user language). Match guard (normalized title + year).
  - **S2 — LLM proposal validated by TMDB**: the LLM proposes an IMDb/TMDB id from the
    title + overview + channel; the proposal is **validated** via TMDB `/find`
    (`FindByExternalIdAsync`) or detail-by-id (`LookupMetaByIdAsync`) — TMDB is the
    source of truth, a hallucinated id returns null.
  - **Non-destructive apply**: only fills missing provider ids, an empty `Overview`,
    empty `Genres`, a missing `Primary` poster. **The EPG `Name` is never changed** —
    it is **locked** (`MetadataFields.Name`) to preserve the original title (reused
    later to scan the EPG). Filled fields are also locked (add-only — no existing lock
    is removed), mirroring the user's manual practice.
  - **Idempotent** via `llmai-identified` (resolved) / `llmai-needs-review` (unresolved
    — tagged for review) tags. **Dry-run** (`OrphanIdentifyDryRun`): no writes, detailed
    log of the proposed resolution + summary. Best-effort: a failing item never aborts
    the pass. Scope: DVR recordings only.
  - **Config** : `OrphanIdentifyEnabled` (défaut `false`, opt-in — modifie des
    enregistrements), `OrphanIdentifyDryRun` (défaut `false`). Page de config : section
    « Identification des enregistrements orphelins ».
    **Config**: `OrphanIdentifyEnabled` (default `false`, opt-in — mutates recordings),
    `OrphanIdentifyDryRun` (default `false`). Config page: "Orphan recording
    identification" section.

### Modifié / Changed
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