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

### Modifié / Changed
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