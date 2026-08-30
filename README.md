# LLM_AI — Plugin Emby de recommandations par LLM

**Version :** 1.0.0.0 · **Id :** `e7d3dee6-ef19-46a9-985f-06318b682e60` · **Cible :** Emby (net8.0)

> Version anglaise : voir [README-EN.md](README-EN.md).

Plugin Emby qui utilise un grand modèle de langage (LLM — Ollama local, Ollama Cloud ou
Google Gemini) pour produire des **recommandations de séries et de films à enregistrer**
(planifiées au niveau serveur) et une section personnalisée **« À regarder ce soir »**
par usager. Le LLM dispose d'outils pour interroger la bibliothèque Emby, l'EPG, TMDB,
TVDB, le web et une source « Showbizz » de nouveautés ; il décide lui-même des appels
d'outils à effectuer (boucle d'agent / tool-calling).

Il expose aussi un **audit santé du serveur** à la demande (`GET /Plugins/LLMAI/Audit`,
admin) : un agent LLM interroge l'outil `system_audit` (sessions, tâches planifiées,
transcodage, disques, journaux, métriques hôte, bibliothèque) et produit un **rapport
Markdown** de santé (constats tagués par gravité + actions recommandées). La
**remédiation** (arrêter une session, déclencher une tâche, notifier un usager) est
désactivée par défaut (opt-in). Voir [Audit santé](#audit-santé).

---

## Table des matières

1. [Aperçu](#aperçu)
2. [Installation](#installation)
3. [Configuration](#configuration)
4. [Composants](#composants)
5. [Outils LLM](#outils-llm)
6. [« À regarder ce soir »](#à-regarder-ce-soir)
7. [Auto-programmation & popup au login](#auto-programmation--popup-au-login)
8. [Surfaces natives des recommandations](#surfaces-natives-des-recommandations)
9. [Audit santé](#audit-santé)
10. [Identification des enregistrements orphelins](#identification-des-enregistrements-orphelins)
11. [API HTTP](#api-http)
12. [i18n (FR / EN)](#i18n-fr--en)
13. [Dépannage](#dépannage)
14. [Changelog](#changelog)

Voir aussi : [LICENSE](LICENSE) (MIT) · [CHANGELOG.md](CHANGELOG.md).

---

## Aperçu

Le plugin ajoute à Emby une page **Recommandations** (menu principal) qui présente :

- **Séries à enregistrer** et **Films à enregistrer** — produites par une **tâche
  planifiée** (`LlmScheduledTask`) tournant au niveau serveur (admin). Le LLM parcourt
  l'EPG à venir, croise avec la bibliothèque et les whitelists, et recommande quoi
  programmer. Les résultats sont stockés dans `PluginConfiguration.Recommendations` et
  affichés à tous les usagers. Des notifications Emby peuvent être envoyées.

- **À regarder ce soir / aujourd'hui** — section **par usager et à la demande** calculée
  par un endpoint plugin (`TonightApiService`). Le LLM croise l'historique de visionnage
  de l'usager, l'EPG de ce soir et les enregistrements récents non visionnés pour
  recommander quoi regarder *maintenant*. Un cache par usager évite de relancer le LLM à
  chaque ouverture de page.

Les boutons des cartes permettent de **Programmer** (SeriesTimer pour une série, Timer
unique pour un film), de **Regarder** / **Regarder en direct** / **Regarder (bibli.)**,
et d'**Oublier** (ajoute le titre à la liste de rejet `DroppedTitles`).

---

## Installation

> **Le plugin est auto-suffisant dans la DLL** : tous les fichiers web (HTML/JS/i18n/icône)
> sont embarqués comme ressources — un seul fichier `LLM_AI.dll` suffit.

### Depuis une release (utilisateur final)

1. Télécharger la dernière archive `LLM_AI-<version>.zip` sur la page
   **[Releases][releases]** du dépôt GitHub.
2. Décompresser l'archive.
3. Lancer l'installateur (en root) :
   ```bash
   sudo bash install.sh
   ```
   `install.sh` détecte le dossier des plugins Emby (`/var/lib/emby/plugins` par défaut)
   et le service (`emby-server`), supprime l'ancien `mon-plugin.dll`, copie la DLL et
   redémarre Emby. Variables d'env optionnelles : `EMBY_PLUGINS_DIR`, `EMBY_SERVICE`.
4. Dans Emby : **Plugins** → **LLM_AI** → configurer (voir [Configuration](#configuration)).
5. Vider le cache du navigateur / recharger la page (les fichiers JS du plugin sont
   servis par Emby et mis en cache agressivement).

### Depuis les sources (développeur)

Prérequis : Emby Server (build net8.0), .NET SDK 8.

- `bash deploy.sh` depuis la racine du projet : compile en `Release net8.0`, copie
  `LLM_AI.dll` dans `/var/lib/emby/plugins/`, supprime l'ancien `mon-plugin.dll`,
  redémarre `emby-server` et affiche la fin du journal.
- `bash package.sh` : compile + produit `dist/LLM_AI-<version>.zip` (release
  auto-suffisante, voir ci-dessus).

[releases]: ../../releases

---

## Configuration

La page de config (`config.html` / `config.js`, localisée via `i18n.js`) expose :

### Backends LLM

Plusieurs backends peuvent être activés simultanément avec une **priorité**. Le backend
activé de plus haute priorité est le backend **primaire**. Chaque backend :

| Champ | Rôle |
|---|---|
| `Provider` | `OllamaLocal`, `OllamaCloud` ou `Gemini` |
| `Url` | URL de l'API (ex. `http://localhost:11434` pour Ollama local) |
| `Model` | Nom du modèle (ex. `llama3.1`, `gemini-1.5-flash`) |
| `Enabled` | Activer ce backend |
| `Priority` | Ordre de préférence (plus haut = primaire) |

Champs hérités `LlmUrl` / `ModelName` restent supportés (repli legacy : un `LlmUrl` non
vide est traité comme un backend local).

### Clés API

Les clés API sont stockées dans la config **OU** lues dans des variables d'environnement
(si le champ de config est vide) :

- `OllamaApiKey` ← `OLLAMA_API_KEY` (Ollama Cloud)
- `GeminiApiKey` ← `GEMINI_API_KEY` (Google Gemini)
- `TmdbApiKey` ← `TMDB_API_KEY`, `TvdbApiKey` ← `TVDB_API_KEY`
- `EmbyPublicUrl` — URL Emby exposée au LLM (pour `item_details` / posters).

> ⚠️ Les clés ne sont jamais lues ni affichées en clair par l'assistant ; elles transitent
> directement du champ de config (ou de l'env) vers l'appel d'API.

### Filtres et listes

- `ChannelWhitelist` (chaînes à considérer, vide = toutes), `GenreWhitelist` (idem genres).
- `SeriesFlags` / `MovieFlags` — drapeaux Kids / News / Sports (inclusion).
- `DroppedTitles` — titres exclus (alimenté par le bouton **Oublier**).
- `MaxSeriesBatch` / `MaxMovieBatch` — plafonds de recommandations par tâche planifiée.

### Section « Ce soir »

- `TonightEnabled` (bool, défaut `true`) — active la section + l'endpoint.
- `TonightWindowStart` / `TonightWindowEnd` (HH:mm, défauts `""` = maintenant / `23:59`) —
  fenêtre temporelle de l'EPG pour `epg_tonight`.
- `TonightPrompt` — template du prompt (l'historique/EPG/enregistrements sont injectés à
  l'exécution, pas dans ce champ).
- `MaxTonightBatch` (défaut 10) — plafond de recommandations.
- `TonightCacheHours` (défaut 4) — TTL du cache par usager.
- `TonightRecordingsDays` (défaut 7) — fenêtre « enregistrés il y a moins de N jours ».
- `TonightMinRecommendations` (défaut 3) — minimum garanti (voir [À regarder ce soir](#à-regarder-ce-soir)).

### Auto-programmation & popup au login

Les clients natifs **Android / Android TV** ne rendent pas les pages plugin
HTML : les recommandations ne sont visibles que sur la page web. Deux leviers
rendent les recos **discoverables sur la TV** :

- `AutoProgram` (bool, **défaut `false` — opt-in explicite**) : si coché, après
  chaque run (tâche planifiée **et** login), les recommandations du **record
  bucket** (programmes EPG à venir, non déjà possédés, non déjà programmés,
  hors `DroppedTitles`) sont **automatiquement programmées en enregistrement**
  (SeriesTimer pour une série, Timer unique pour un film). Elles ressortent
  alors dans le **guide EPG natif** (badge d’enregistrement) sur tous les
  clients, TV comprise. **Aucune programmation tant que décoché.**
- `LoginPopup` (bool, défaut `true` — indépendant de `AutoProgram`) : à la
  connexion d’un usager, un **toast** signale ce soir ce qu’il peut regarder
  (enregistrements non visionnés, bibliothèque), + une **notification cloche**
  persistante (deep-link) en repli. `LoginPopupSeconds` (défaut 8) règle la
  durée du toast.

> ⚠️ L’auto-programmation occupe des tuners/disque : c’est une action opt-in.
> L’utilisateur peut annuler un timer indésirable dans Emby. Le popup au login
> s’affiche même sans auto-programmation (suggestions à regarder seulement).

### Surfaces natives (bibliothèque .strm, genre, collection)

Trois leviers **opt-in** (défaut `false`) qui exposent les recos directement dans
Emby, au-delà de la page web. Tous sont générés par la **tâche planifiée** et
détaillés dans [Surfaces natives des recommandations](#surfaces-natives-des-recommandations).

- `StrmLibraryEnabled` (bool, défaut `false`) — écrit une carte
  `.strm`+`.nfo`+poster par reco du **record bucket** dans la bibliothèque Emby
  nommée `StrmLibraryName`. Alternative manuelle à `AutoProgram` (les deux
  cohabitent, le dedup évite les timers en double).
- `StrmLibraryName` (chaîne, défaut `""`) — **nom exact** de la bibliothèque Emby
  dédiée (casse ignorée). L'utilisateur doit d'abord créer dans Emby une
  bibliothèque de type **Films** (ou Contenu mixte) pointant vers un dossier vide.
- `StrmSecret` (chaîne, auto-générée) — jeton de capacité vérifié par l'endpoint
  `/Plugins/LLMAI/Activate`. Auto-généré au premier run, jamais à saisir.
- `TonightGenreTagEnabled` (bool, défaut `false`) — ajoute le genre `AI Tonight`
  aux items Emby du **watch bucket** (modifie les métadonnées réelles). Scope
  isolé du genre `AI Suggestion` de la bibliothèque `.strm`.
- `TonightCollectionEnabled` (bool, défaut `false`) — maintient une collection
  (BoxSet) `AI Tonight` des items du watch bucket. **Non destructive** (items
  référencés, jamais copiés). Indépendante du genre (les deux cohabitent).

> 📌 `StrmLibraryName` doit correspondre au **nom exact** affiché dans le dashboard
> Emby (l'UserView est slugifié avec des tirets ; un nom avec `_` peut ne pas
> matcher). En cas de « bibliothèque introuvable », recopier le nom du dashboard.

### Divers

`TmdbLanguage`, `SearXngUrl` (recherche web auto-hébergée), `WebFetchDirect`,
`ShowbizzUrl` / `ShowbizzPattern`, `RagDirectives` (directives additionnelles injectées
dans le prompt), `ResponseLanguage` (langue de sortie du LLM — voir ci-dessous),
`ScheduleTask` / `ScheduleTaskMovies` (cron de la tâche planifiée),
`DebugVerbose`.

### Langue de réponse du LLM

`ResponseLanguage` force la langue du **texte en prose** de l'LLM — les **raisons des
recommandations** (champ `reason` des cartes) **et** le **rapport d'audit**. Vide / `Auto`
= aucune directive (l'LLM suit la langue du prompt, ici le français — comportement par
défaut). Toute autre valeur (ex. `English`, `Español`, `Deutsch`…) injecte une directive
en fin de system prompt : l'LLM rédige alors dans cette langue. Les titres de films/séries,
noms de chaînes et noms de champs JSON techniques restent inchangés (langue d'origine).
Select sur la page de config : `Auto`, `Français`, `English`, `Español`, `Deutsch`,
`Italiano`, `Português`. S'applique aux deux paths (recommandation + audit, modes single
et déterministe).

### Audit santé

Endpoint **à la demande** (admin uniquement) `GET /Plugins/LLMAI/Audit` qui produit un
rapport de santé du serveur. Indépendant de la recommandation (run agent dédié, outil
`system_audit`). Voir [Audit santé](#audit-santé).

- `AuditEnabled` (bool, défaut `true`) — active l'endpoint et le bouton « Lancer l'audit ».
  `false` = l'endpoint renvoie une réponse désactivée (pas de run LLM). L'endpoint reste
  réservé aux administrateurs.
- `AuditRemediationEnabled` (bool, **défaut `false` — opt-in explicite**) — si coché, le
  LLM peut **exécuter** les trois actions de remédiation (`stop_session`,
  `trigger_task`, `send_message`) pendant l'audit. Tant que décoché, ces actions
  renvoient une erreur et le LLM se contente de les **recommander** dans le rapport.
  Double contrôle : le prompt d'audit demande de toute façon au LLM de ne JAMAIS agir
  sans demande explicite — ce flag n'ouvre que la *capacité*, pas l'autonomie.
- `AuditMode` (`single` | `deterministic`, défaut `single`) — stratégie d'exécution :
  - `single` — une boucle agent : le LLM appelle lui-même `system_audit` de façon
    adaptative (peut creuser un journal suite à un constat). Convient à un modèle
    costaud / cloud. **Seul mode où la remédiation peut être exécutée** (si le flag est
    activé).
  - `deterministic` — le C# rassemble toutes les sondes read-only (zéro appel LLM
    pour le rassemblement), puis un seul passage LLM **sans outils** synthétise le
    rapport à partir du digest. Conçu pour un modèle local/modeste (ex. gemma4) : on
    retire du LLM l'orchestration multi-outils pour ne garder que la synthèse de texte
    fourni. Remédiation report-only (l'LLM n'a pas d'outil pour l'exécuter).
- `AuditPrompt` — template du prompt envoyé au LLM (message user). L'éventuel
  paramètre `Focus` de l'endpoint est appendé à l'exécution pour orienter l'audit.

### Identification des enregistrements orphelins

Tâche planifiée **quotidienne 04:00** qui identifie les **items de bibliothèque non
identifiés** (films/séries issus d'enregistrements DVR **terminés** — une fois
l'enregistrement terminé, Emby importe l'item dans une bibliothèque où il vit comme
un `Movie`/`Series` normal ; aucun id IMDb/TMDB/TVDB = identification échouée, souvent
des titres québécois absents du catalogue TMDB/TVDB). Voir [Identification des orphelins](#identification-des-enregistrements-orphelins).

- `OrphanIdentifyEnabled` (bool, **défaut `false` — opt-in explicite**) — active la
  tâche. `false` = la tâche est inactive (no-op). Modifie des métadonnées
  d'enregistrements — d'où l'opt-in.
- `OrphanIdentifyDryRun` (bool, défaut `false`) — si coché, la tâche **n'écrit rien** :
  elle logue seulement les orphelins trouvés et la résolution proposée (S1/S2/S3) + un
  bilan. Sert à valider la qualité des résolutions avant de basculer en application
  automatique. À garder cochée pour les premiers runs.
- `OrphanSearXngEnabled` (bool, défaut `true`) — active l'étape **S3** (recherche web
  SearXNG → id IMDb → validation TMDB + juge synopsis) pour les titres que S1 et S2 ne
  résolvent pas. Inopérant si ni SearXNG ni clé Ollama ne sont configurés.
- `OrphanRetryNeedsReview` (bool, défaut `false`) — si coché, retraite les items
  `llmai-needs-review` (au lieu de les ignorer) pour y repasser S3 ; en cas de
  résolution, le tag devient `llmai-identified`. Les déjà-identifiés restent ignorés.

---

## Composants

| Fichier | Classe | Rôle |
|---|---|---|
| `Plugin.cs` | `Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage` | Point d'entrée. Nom `LLM_AI`, Id `e7d3…2e60`. Enregistre les pages web (config, recommandations, i18n). Version pilotée par `<AssemblyVersion>` du `.csproj`. |
| `PluginConfiguration.cs` | `PluginConfiguration` (+ `LlmBackend`, `LlmProvider`) | Toute la config persistée + backends multi-source. |
| `LlmScheduledTask.cs` | `LlmScheduledTask : IScheduledTask, IConfigurableScheduledTask` | Tâche planifiée globale (admin) : produit les recos **Séries / Films** en parcourant l'EPG, applique le garde-fou « déjà possédé » (`EnrichWithLibrary` sur le payload fusionné), stocke dans `Recommendations`, envoie les notifications. Délègue l'orchestration à `LlmRunner`. |
| `TonightApiService.cs` | `TonightApiService : BaseApiService` | Endpoint HTTP **par usager à la demande** `GET /Plugins/LLMAI/Tonight`. Couche HTTP fine : résout l’usager puis délègue à `TonightService`. |
| `TonightService.cs` | `TonightService` (interne) | **Génération partagée** « À regarder ce soir » : profil de goût, enregistrements non visionnés, réserve bibliothèque, run LLM, enrichissement, **cache par usager** (statique, partagé endpoint + login). Utilisé par `TonightApiService` et `TonightLoginService`. |
| `AutoProgrammer.cs` | `AutoProgrammer` (interne) | Auto-programmation : crée les timers Emby (SeriesTimer / Timer unique) du **record bucket** — recos à enregistrer non possédées/non déjà programmées/hors drop list. Portage serveur de la logique « Programmer » de `recommendations.js`. `ProgramOneAsync(Reco, …)` (retour `OneOutcome`) partagé avec l'endpoint Activate. |
| `StrmLibraryGenerator.cs` | `StrmLibraryGenerator` (interne) | Bibliothèque `.strm` : écrit une carte `.strm`+`.nfo`+poster par reco du record bucket, nettoyage `.llmai_reco`, téléchargement poster TMDB (retry sans suffixe « on <chaîne> » si le titre complet n'a pas de match). Repli poster : **télécharge l'affiche Primary du programme EPG** — fichier local OU URL distante Gracenote/TMS (`tmsimg.com`) — avec raison loguée à chaque garde. Le `<plot>` du `.nfo` commence par le **synopsis EPG natif** (langue d'origine) puis l'enrichissement dans la langue de l'usager ; ajoute les **External IDs** `<tmdbid>`/`<imdbid>`/`<tvdbid>` quand disponibles (liens profonds TMDB/IMDb/TVDB). |
| `ActivateApiService.cs` | `ActivateApiService : BaseApiService` | Endpoint `GET /Plugins/LLMAI/Activate` (DTO `[Unauthenticated]`) : programme une reco unique puis stream `recording_activated.mp4`. Gated par `StrmSecret`. |
| `AiGenreTagger.cs` | `AiGenreTagger` (statique) | Étiquetage genre `AI Tonight` : `AddAsync` / `RemoveAllAsync` via `UpdateToRepository`. |
| `AiTonightCollectionManager.cs` | `AiTonightCollectionManager` (statique) | Collection `AI Tonight` : `EnsureAsync` (find-or-create BoxSet, reconcile) + `ClearAsync` via `ICollectionManager`. |
| `AiTonightCleanupTask.cs` | `AiTonightCleanupTask : IScheduledTask` | Nettoyage quotidien 03:00 : retire le genre `AI Tonight` + vide la collection (toujours actif). |
| `OrphanIdentifyTask.cs` | `OrphanIdentifyTask : IScheduledTask` | Identification quotidienne 04:00 des items bibliothèque orphelins (sans id IMDb/TMDB/TVDB — enregistrements DVR terminés importés en bibliothèque) : découverte via `ILibraryManager.GetItemList` (Movie/Series) → S1 (nettoyage titre + recherche TMDB multilingue) → S2 (LLM propose un id validé via TMDB `/find`) → S3 (recherche web SearXNG → id IMDb, même porte d'acceptation), écrit ids+Overview+Genres+poster si vides, **verrouille `Name`**, tags `llmai-identified`/`llmai-needs-review`, retry needs-review, dry-run. Voir [Identification des orphelins](#identification-des-enregistrements-orphelins). |
| `DefaultImageApplier.cs` | `DefaultImageApplier` (statique) | Pose un poster par défaut standardisé (`default_poster.jpg`, ressource embedded) sur la collection `AI Tonight` (BoxSet) et la racine de la bibliothèque `.strm` (CollectionFolder). Idempotent (seulement si pas d'image `Primary`). |
| `AiBadgeEnhancer.cs` | `AiBadgeEnhancer : IImageEnhancer` | Badges **au moment du service** sur les images EPG (overlay — l'artwork stocké n'est jamais modifié) : puce **verte + étincelle** pour les suggestions IA du record bucket, puce **jaune sans icône** pour les programmes déjà en bibliothèque (matching `Norm` réutilisé). Dessin SkiaSharp (livré avec Emby), clé de cache par type de badge, repli copie de l'original, ne lève jamais. Auto-découvert par le scan d'assembly. |
| `AiBadgeRegistry.cs` | `AiBadgeRegistry` (statique) | Registre des programmes suggérés par la tâche nocturne : remplacé à chaque run (`ApplyRecos`, filtres record bucket), persisté `AiBadgeProgramIds`, rechargement paresseux au 1er `Supports` (le constructeur du plugin ne touche jamais `Configuration` — `AssemblyFilePath` n'est posé qu'après construction). |
| `I18n.cs` | `I18n` (statique) | i18n côté serveur (C#) : dictionnaires inline FR/EN + résolution de langue (`ResolveMetaLangKey` métadonnées / `ResolveDisplayLangKey` interface) + `ToTmdbLang`/`ToLangName`. Localise les tâches planifiées. |
| `TonightLoginService.cs` | `TonightLoginService : IServerEntryPoint` | Déclencheur de login : branche `ISessionManager.SessionStarted`, lance `TonightService` (cache-aware), auto-programme (si `AutoProgram`), envoie un **toast** (`SendMessageCommand`, gated `DisplayMessage`) + **cloche** persistante (deep-link). Pattern `Emby.ComSkipper`. |
| `AuditApiService.cs` | `AuditApiService : BaseApiService` | Endpoint HTTP **à la demande admin** `GET /Plugins/LLMAI/Audit` : résout l'admin appelant, construit le prompt d'audit (template `AuditPrompt` + `Focus` optionnel) puis délègue le run agent à `LlmRunner.RunAuditAsync`. Retourne le rapport Markdown brut. |
| `ChatApiService.cs` | `ChatApiService : BaseApiService` | Endpoint HTTP **chat interactif admin** `POST /Plugins/LLMAI/Chat` : corps `{Message, History:[{role,content}]}` (serveur stateless — la page garde l'historique), filtre les rôles user/assistant, délègue le tour à `LlmRunner.RunChatAsync` (tous les outils existants, priorités LLM usager). Le system prompt (doc outils + directives) est construit serveur-side, une fois par conversation. |
| `SystemAuditTool.cs` | `SystemAuditTool : ILlmTool` | Outil `system_audit` (voir [Outils](#outils-llm)) — 12 actions d'audit système (sessions, tâches, transcodage, disques, journaux, métriques hôte, processus, bibliothèque) + 3 actions de remédiation gated par `AuditRemediationEnabled`. Confinement FS des journaux (nom seul + whitelist extension + containment canonique). |
| `LlmRunner.cs` | `LlmRunner` (classe interne) | **Orchestration partagée** : `ResolveBackends`, `RunAsync` (boucle d'agent + tool-calling), `EnrichRecommendations` (match titre → id/chaîne/poster/note), `EnrichWithLibrary` (rapprochement bibliothèque : titre exact/flou, **repli par id IMDb** via `AnyProviderIdEquals` — reco possédée → `library_id`, exclue du record bucket), `FindLibraryItem`, `MergeJsonArrays`, `ExtractJsonPayload`, `NormTitle` (pliage d'accents partagé `FoldAscii` : « leçons » ≡ « lecons »), résolution des clés via env. Path d'audit dédié : `BuildAuditTools`, `RunAuditAsync` (boucle agent ou mode déterministe), `ChatWithFallbackAsync` (synthèse sans outils). Path chat : `RunChatAsync` (multi-tours, tous les outils existants, priorités LLM usager). Appels one-shot : `TranslateTextAsync` (tier-3 cascade TMDB), `ResolveIdsAsync` (proposition d'ids pour la tâche orphelins — toujours validée par TMDB). Utilisé par `LlmScheduledTask`, `TonightApiService`, `AuditApiService`, `ChatApiService` **et** `OrphanIdentifyTask`. |
| `ItemIdResolver.cs` | `ItemIdResolver` (statique interne) | Résolution bilingue des ids Emby : longs (InternalId — forme canonique du plugin, la seule que la couche REST/UI accepte) **et** Guids historiques (input legacy seulement, jamais émis). Corriger la devise d'ids qui faisait échouer toutes les validations Tonight. |
| `LlmAgentService.cs` | `LlmAgentService` | Boucle d'agent : envoie le prompt au LLM, exécute les tool-calls, reboucle jusqu'à la réponse finale. Deux paramètres optionnels (`roleIntro`, `formatSection`) permettent de surcharger l'intro du rôle et le bloc de format de sortie pour les paths audit et chat (sans toucher aux appelants recommandation). `RunChatAsync` : entrée multi-tours qui rejoue l'historique (user/assistant, borné) entre le system prompt et le nouveau message — même boucle partagée (`RunLoopAsync`). |
| `LlmClient.cs` | `LlmClient` (statique) | Appels HTTP bruts vers Ollama / Gemini (sans clé en clair dans les journaux). |
| `GetEmbyInfoTool.cs` | `GetEmbyInfoTool` | Outil `get_emby_info` (voir [Outils](#outils-llm)). |
| `TmdbLookupTool.cs` / `TvdbSearchTool.cs` / `WebSearchTool.cs` / `WebFetchTool.cs` / `ShowbizzTool.cs` | … | Outils LLM spécialisés (voir [Outils](#outils-llm)). `TmdbLookupTool` expose en outre `LookupMetaAsync`/`LookupMetaMultiLangAsync` (recherche, S1), `FindByExternalIdAsync` (`/find`, valide un id proposé), `LookupMetaByIdAsync` (détail par id), `CleanEpgTitle` — réutilisés par `StrmLibraryGenerator` et `OrphanIdentifyTask`. |
| `config.html` / `config.js` | — | Page de configuration (saisie des champs ci-dessus). |
| `recommendations.html` / `recommendations.js` | — | Page Recommandations (rendu des 3 sections, cartes, boutons). |
| `chat.html` / `chat.js` | — | Page « Chat LLM AI » (menu admin, section Serveur) : conversation plein cadre avec l'agent LLM — logique multi-tours portée de la config vers sa propre page, historique par visite (serveur stateless), rendu Markdown partagé. |
| `i18n.js` | — | Chaînes localisées FR/EN + endpoint `web/ConfigurationPage?name=LLMAII18n`. |
| `deploy.sh` | — | Build + déploiement + redémarrage (voir [Installation](#installation)). |

### Flux de données

**Tâche planifiée (Séries/Films) :**
1. Cron `ScheduleTask`/`ScheduleTaskMovies` → `LlmScheduledTask.Execute`.
2. `LlmRunner.ResolveBackends` choisit le backend primaire.
3. Prompt + outils → `LlmAgentService` boucle d'agent (le LLM appelle `get_emby_info`
   `epg_series`/`epg_movies`, `tmdb_lookup`, `web_search`/`web_fetch`, `showbizz…`).
4. `EnrichRecommendations` → posters/notes/id/chaîne.
5. `EnrichWithLibrary` → garde-fou « déjà possédé » déterministe (titre, puis
   id IMDb si le LLM l'a établi) : reco possédée → `library_id` (exclue du
   record bucket, bouton « Regarder (bibli.) »). Stockage dans `Recommendations`.
5. Notifications Emby (si activées). La page lit `Recommendations` au `viewshow`.

**À regarder ce soir :** voir section dédiée ci-dessous.

---

## Outils LLM

Le LLM choisit lui-même les outils à appeler. Chaque outil implémente `ILlmTool`
(`Name`, `RunAsync(args)`).

| `Name` | Action(s) / Description |
|---|---|
| `get_emby_info` | **Interrogation Emby** — actions : `summary` (résumé bibliothèque), `library` (items), `global_search`, `item_details`, `item_persons`, `person`, `epg_series` (EPG séries à venir), `epg_movies` (EPG films à venir), `epg_tonight` (EPG dans la fenêtre « ce soir », `HasAired=false`, marque `is_scheduled`), `scheduled` / `planning` (timers programmés). Applique whitelists, flags, drop list, déduplication par titre. |
| `tmdb_lookup` | Recherche / détails TMDB (note, poster, résumé, casting) via `TmdbApiKey`. |
| `tvdb_search` | Recherche TVDB (séries) via `TvdbApiKey`. |
| `web_search` | Recherche web (SearXng `SearXngUrl` ou fournisseur intégré). |
| `web_fetch` | Récupération/lecture d'une page web (`WebFetchDirect` pour lecture brute). |
| `showbizz_new_releases` | Nouveautés depuis une source « Showbizz » (`ShowbizzUrl` + `ShowbizzPattern`). |
| `system_audit` | **Audit santé** (voir [Audit santé](#audit-santé)) — 15 actions sur `action` : **inspection** `server_info`, `system_config` (configuration serveur via `IServerConfigurationManager`), `active_sessions`, `scheduled_tasks`, `list_logs`, `inspect_log` (grep + contexte, confiné au dossier des journaux), `transcode`, `gpu_transcode`, `host_metrics`, `disk_storage`, `processes` (orphelins ffmpeg + top RAM/CPU), `library_stats`, `missing_metadata` ; **remédiation** (gate `AuditRemediationEnabled`) `stop_session`, `trigger_task`, `send_message`. Ne lève jamais (erreur → JSON). |

---

## À regarder ce soir

Section **personnalisée par usager**, calculée à l'ouverture de la page (on-demand) par
l'endpoint `TonightApiService`. Le LLM reçoit **trois sources** :

1. **Profil de goût** — `BuildTasteProfile` : items joués récemment par l'usager (tri
   `DatePlayed` desc), titres/séries/genres préférés.
2. **EPG de ce soir** — via le tool-call `get_emby_info action=epg_tonight` (fenêtre
   `TonightWindowStart`→`TonightWindowEnd`, `HasAired=false`).
3. **Enregistrements récents non visionnés** — `BuildUnwatchedRecordings` : items
   enregistrés dans les `TonightRecordingsDays` derniers jours mais non lus
   (`IsPlayed=false`). Permet la règle : *si l'usager regarde la série X et qu'un nouvel
   enregistrement de X est disponible non visionné → le recommander.*

**Réserve bibliothèque (fallback)** — `BuildLibraryFallbackPool` : items non visionnés
pré-fetchés, injectés comme **réserve**, à utiliser **seulement si** le LLM produit
**moins de `TonightMinRecommendations`** recommandations. Garantit au moins N recos même
si l'EPG est vide.

**Champ `source`** de chaque recommandation (drive les boutons de la carte) :

| `source` | Sens | Boutons |
|---|---|---|
| `live` | Programme EPG de ce soir | Programmer · Regarder en direct (si déjà commencé) · Regarder (bibli.) si possédé · Oublier |
| `recording` | Enregistrement récent non visionné | Regarder · Oublier |
| `library` | Réserve bibliothèque (fallback) | Regarder · Oublier |

**Mode compact (local)** — Si le backend primaire est `OllamaLocal`, `TonightApiService`
passe en **mode compact** : les plafonds d'items injectés (profil, enregistrements,
réserve) et la troncature des résumés EPG sont réduits, pour éviter de surcharger un
modèle local (souvent plus lent / contexte limité). Les backends cloud reçoivent le
contexte complet.

**Cache par usager** — `Dictionary<userId, CacheEntry>` + verrou, TTL
`TonightCacheHours`. `Refresh=1` force un nouveau run (bouton **Rafraîchir**).

## Auto-programmation & popup au login

Les recommandations LLM_AI ne s’affichent par défaut que sur la **page web**
`recommendations.html`. Les clients natifs (Android / Android TV) ne rendent
pas les pages plugin HTML — la reco n’y est pas « discoverable ». Deux leviers,
tous deux **configurables** (voir [Configuration](#auto-programmation--popup-au-login)) :

### Record bucket → badge EPG natif (auto-programmation)

Si `AutoProgram` est coché, après chaque run les recommandations **à enregistrer**
(programmes EPG à venir, non déjà possédées, non déjà programmées, hors drop
list) sont programmées en enregistrement :

- **Série** → `SeriesTimerInfo` (RecordNewOnly, SkipEpisodesInLibrary) via
  `ILiveTvManager.CreateSeriesTimer`.
- **Film / one-off** → `TimerInfoDto` via `ILiveTvManager.CreateTimer`.

Les valeurs par défaut (Start/End/Channel/paddings) sont dérivées du programme
via `GetNewTimerDefaults(programId)` — on ne poste jamais un timer minimal
(champs requis manquants : le serveur ne crée alors rien). Déduplication par
`GetTimers`/`GetSeriesTimers` (ProgramId + nom normalisé, avec retrait
d’article — cohérent avec l’exclusion EPG de `get_emby_info`).

Résultat : les recos portent le **badge d’enregistrement** dans le guide EPG
**natif** — le seul highlight fiable sur tous les clients TV. L’utilisateur
regarre rarement en direct (il enregistre + zappe les pubs) : programmer est
l’action juste ; il peut annuler un timer au besoin.

### Watch bucket → popup au login

À la connexion d’un usager, `TonightLoginService` (`IServerEntryPoint`,
pattern `Emby.ComSkipper`) branche `SessionManager.SessionStarted` :

1. **Cache frais** → toast immédiat (pas de run LLM).
2. **Cache froid** → run `TonightService` (~30–60 s), puis toast + cloche.
3. Garde-fou **in-flight** : un seul run par usager même sur plusieurs appareils
   connectés à la fois (cache partagé endpoint + login).

Le **toast** (`SendMessageCommand`, gated `DisplayMessage` dans
`SupportedCommands`) liste les titres du watch bucket (enregistrements non
visionnés / bibliothèque). La **cloche** persistante (`INotificationManager`)
deep-link vers la page Recommandations et survive si la session ferme avant la
fin du run. `LoginPopup` est **indépendant** de `AutoProgram` : les suggestions
à regarder s’affichent au login même sans auto-programmation.

> **Gating `AutoProgram` (règle absolue)** : aucun timer n’est créé tant que
> `cfg.AutoProgram == false`. Le flag est vérifié dans les deux chemins
> (`LlmScheduledTask`, `TonightLoginService`) avant tout appel à
> `AutoProgrammer.Program`. Le popup (`LoginPopup`) n’est pas gatingé par
> `AutoProgram`.

---

## Surfaces natives des recommandations

Outre la page web `recommendations.html` et l'auto-programmation, trois leviers
**opt-in** exposent les recos directement dans Emby (tous générés par la tâche
planifiée). Voir [Configuration](#surfaces-natives-bibliothèque-strm-genre-collection).

### Bibliothèque `.strm` (record bucket)

`StrmLibraryGenerator` écrit, après chaque run, une carte **`.strm`+`.nfo`+poster**
par recommandation **à enregistrer** (programmes EPG à venir non possédés) dans une
bibliothèque Emby dédiée (option `StrmLibraryEnabled`, nom `StrmLibraryName`). Un
fichier marqueur `.llmai_reco` par dossier pilote le nettoyage des cartes périmées
au run suivant (`CleanPrevious`).

Le `.nfo` de chaque carte contient :

- un `<plot>` qui commence par le **synopsis natif de l'EPG** (langue d'origine du
  programme, lu sur le `BaseItem` EPG sous-jacent), suivi de l'enrichissement
  (synopsis TMDB + raison LLM + méta + diffusion à venir + lien fiche EPG) dans la
  **langue de l'usager** (`ResponseLanguage`). L'usager lit l'enrichissement dans sa
  langue tout en gardant le synopsis EPG d'origine — aucune déduction de la langue du
  programme nécessaire ;
- les **External IDs** `<tmdbid>` / `<imdbid>` / `<tvdbid>` quand ils sont disponibles
  (récupérés via `append_to_response=external_ids` de TMDB) → Emby génère les **liens
  profonds** TMDB / IMDb / TVDB sur la fiche de la carte.

Lire une carte déclenche l'endpoint **`GET /Plugins/LLMAI/Activate?programId=&kind=&t=`** :
1. `AutoProgrammer.ProgramOneAsync` crée le timer d'enregistrement (une reco
   unique), avec dedup par ProgramId ;
2. l'endpoint stream le clip embarqué `recording_activated.mp4` (8 s, 1280×720, sans texte ni audio — universel).

L'endpoint est **`[Unauthenticated]`** (les lecteurs média / `ffprobe` n'ont pas de
token Emby) ; le **jeton `StrmSecret`** (`t=`) est l'unique garde. Emby ne probe le
`.strm` qu'à la **lecture** (pas au scan), donc la programmation se déclenche au
clic de l'usager. Le clip joué marque la carte Emby **comme visionnée** (drapeau
vert) — activer « masquer les éléments visionnés » sur la bibliothèque auto-cache
les cartes activées.

> ⚠️ **Gestes requis côté Emby** : créer d'abord une bibliothèque **Films** (ou
> Contenu mixte) pointant vers un dossier vide, puis renseigner son **nom exact**
> dans `StrmLibraryName`. Un `localhost` comme URL de base fonctionne pour le
> client web (transcodage serveur) ; `EmbyPublicUrl` n'est requis que pour les
> clients en direct-play (TV, téléphones). L'authentification Emby 401 les
> requêtes sans token — d'où le `[Unauthenticated]` + `StrmSecret`.

### Genre `AI Tonight` (watch bucket)

`AiGenreTagger` étiquette, sur les **runs frais** de Tonight (pas le cache), les
items Emby réels du **watch bucket** (enregistrements non visionnés + items
possédés) avec le genre **`AI Tonight`** (option `TonightGenreTagEnabled`).
L'usager retrouve les recos en **filtrant sur ce genre** dans n'importe quel
client Emby.

- Mutate : `item.Genres = …; item.UpdateToRepository(ItemUpdateType.MetadataEdit)`.
- **Modifie les métadonnées réelles** (tableau `Genres`) — un refresh peut
  l'effacer, réajouté au prochain run frais.
- Scope **isolé** du genre `AI Suggestion` utilisé par la bibliothèque `.strm`
  (nettoyage séparé).

### Collection `AI Tonight` (watch bucket)

`AiTonightCollectionManager` maintient une **collection** (BoxSet) **`AI Tonight`**
regroupant les items du watch bucket (option `TonightCollectionEnabled`). L'usager
la parcourt comme n'importe quelle collection dans n'importe quel client.

- **Non destructive** : les items sont **référencés** (regroupés), jamais copiés ni
  déplacés ; lire un membre joue le vrai item.
- **Agrège des items inter-bibliothèques** (enregistrements + films/séries
  possédés), ce qu'un filtre par genre ne permet pas aussi directement.
- Peuplée sur les runs frais (reconcile remove-all-then-add-all), **indépendante**
  du genre (les deux peuvent cohabiter). Vérifié : `CreateCollection(ParentId=0)`
  ressort dans la liste des Collections.

### Nettoyage (tâche `AiTonightCleanupTask`)

Tâche planifiée **quotidienne 03:00**, **toujours active** (non gatingée) :

1. retire le genre `AI Tonight` de tous les items (`AiGenreTagger.RemoveAllAsync`) ;
2. **vide** la collection `AI Tonight` (coquille conservée, re-remplie au prochain
   run frais) — best-effort.

Les runs « ce soir » suivants réajoutent le genre / re-remplissent la collection sur
les recos toujours pertinentes.

---

## Audit santé

L'audit santé est **indépendant de la recommandation** : un run agent dédié interroge
l'outil `system_audit` (télémétrie système, journaux, transcodage, matériel/OS, disque,
bibliothèque) puis produit un **rapport Markdown** (constats tagués par gravité
🔴/⚠️/✅ + section « Actions recommandées »). Il se déclenche **à la demande** depuis la
page de config (bouton « Lancer l'audit santé ») ou l'endpoint `GET /Plugins/LLMAI/Audit`.

### Deux modes d'exécution (`AuditMode`)

- **`single` (boucle agent, défaut)** — l'LLM appelle lui-même `system_audit` de façon
  adaptative (il peut creuser un journal suite à un constat, enchaîner les actions dans
  l'ordre qui lui semble utile). Convient à un modèle costaud / cloud. **C'est le seul
  mode où la remédiation peut être exécutée** (si `AuditRemediationEnabled` est activé).
- **`deterministic` (rassemblement C# + synthèse)** — le C# rassemble **toutes** les
  sondes read-only lui-même (`GatherAuditDigestAsync`, zéro appel LLM pour le
  rassemblement) dans un digest Markdown, puis **un seul passage LLM sans outils**
  synthétise le rapport à partir du digest. Conçu pour un modèle local/modeste
  (ex. gemma4) : on retire au LLM l'orchestration multi-outils (son point faible) pour
  ne lui laisser que la synthèse de texte fourni (son point fort). La remédiation y est
  **report-only** (l'LLM n'a pas d'outil pour l'exécuter).

### Actions de l'outil `system_audit`

| Famille | Actions (lecture seule, toujours disponibles) |
|---|---|
| Télémétrie & config | `server_info` (version, ports, chemins, redémarrage en attente, mise à jour, maintenance), `system_config` (configuration serveur complète via `IServerConfigurationManager.Configuration`), `active_sessions`, `scheduled_tasks` |
| Logs & flux | `list_logs` (dossier `LogPath`, `*.txt`), `inspect_log` (tail ou **grep + contexte**, confiné au dossier des journaux), `transcode`, `gpu_transcode` |
| Matériel & OS | `host_metrics` (BCL : process, GC, runtime, uptime, scan en cours, CPU transcodage agrégé — GPU uniquement par transcodage), `disk_storage` (`DriveInfo` + mapping chemins Emby), `processes` (détection d'**orphelins ffmpeg** par corrélation + top RAM/CPU + compteurs Emby) |
| Bibliothèque | `library_stats` (comptes par type + bibliothèques configurées + état du scan, via `ILibraryManager` — couche DB, pas FS brut), `missing_metadata` (échantillonnage des items sans synopsis/image/genres) |

| Famille | Actions de **remédiation** (gate `AuditRemediationEnabled`) |
|---|---|
| Contrôle | `stop_session` (PlaystateCommand Stop), `trigger_task` (`QueueScheduledTask`), `send_message` (notification inbox **ou** toast OSD) |

Quand `AuditRemediationEnabled` est décoché, les actions de remédiation renvoient une
erreur JSON — le LLM doit alors **recommander** l'action dans son rapport sans
l'exécuter.

### Sécurité

- **Admin-only** : l'endpoint résout l'usager appelant et vérifie `Policy.IsAdministrator`.
  Un non-admin reçoit `{Enabled:true, Error:"Réservé aux administrateurs."}`.
- **Confinement du système de fichiers** : il n'y a **pas d'outil générique de lecture
  de fichier**. `inspect_log` est épinglé au dossier des journaux avec trois gardes :
  `Path.GetFileName` (rejette tout slash/`..`), **whitelist d'extension**
  (`.txt`/`.log` uniquement) et **containment canonique** (`Path.GetFullPath` sous le
  dossier des journaux). Le LLM ne peut pas vaguer dans `/`.
- **Résolution des chemins Emby** : `server_info`/`list_logs`/`inspect_log`/`disk_storage`
  obtiennent les chemins Emby (program data, cache, transcode temp, métadonnées, **logs**)
  en résolvant `IServerConfigurationManager` via le host puis en lisant `.ApplicationPaths`
  par réflexion sur le nom. `system_config` expose `IServerConfigurationManager.Configuration`
  (la `ServerConfiguration` entière — cross-OS, lu en cours de processus, pas d'analyse XML).
  Repli : si l'appel `GetSystemInfo` échoue (sur certaines versions Emby il lève une
  `NullReferenceException`), les chemins proviennent quand même d'`ApplicationPaths` et le
  chemin des journaux est déduit par convention (`<ProgramDataPath>/logs`) — la couverture
  reste complète, seules les interfaces réseau manquent (signalé honnêtement dans le
  rapport).
- **Bibliothèque via DB** : `library_stats` / `missing_metadata` passent par
  `ILibraryManager` (couche DB) — aucun accès FS brut aux dossiers de la bibliothèque.
- **Remédiation gated** : `stop_session` / `trigger_task` / `send_message` vérifient
  `Plugin.Instance.Configuration.AuditRemediationEnabled` avant d'agir (défaut off).
  Le prompt d'audit demande en plus au LLM de ne **jamais** exécuter de remédiation sans
  demande explicite de l'usager (défense en profondeur).
- **Processus : BCL pure** — `Process.GetProcesses()` n'expose que noms/temps CPU/âge,
  **jamais** les arguments ni le contenu : aucune fuite de secret.

### Paramètre `Focus`

L'endpoint accepte un `Focus` libre (champ de la page de config) appendé au template
`AuditPrompt` pour orienter l'audit (ex. `disk`, `transcoding`) ou formuler une demande
explicite de remédiation (ex. « arrête la session XYZ » — qui n'aboutira que si la
remédiation est activée **et** le mode est `single`).

---

## Identification des enregistrements orphelins

Quand Emby termine un enregistrement DVR, il l'**importe dans une bibliothèque**
(Movies/Series) et tente de l'identifier, puis écrit les métadonnées dans un `.nfo`.
Pour les **titres québécois**, le lookup TMDB/TVDB échoue souvent (le catalogue utilise
les titres de France ou originaux) : l'item finit **sans id IMDb/TMDB** — un
**orphelin**. L'usager corrige alors à la main (recherche web → id IMDb) puis
**verrouille** les champs. La tâche planifiée **`OrphanIdentifyTask`** (quotidienne
**04:00**, juste après le nettoyage 03:00) automatise cette démarche.

> ℹ️ **Découverte** : la tâche **scanne les items `Movie`/`Series` de la bibliothèque**
> (`ILibraryManager.GetItemList`, `Recursive=true`) et retient ceux sans id
> IMDb/TMDB/TVDB. Elle n'utilise **pas** `ILiveTvManager.GetRecordings`, qui ne retourne
> que les enregistrements **actifs/à venir** — les enregistrements **terminés** vivent
> en bibliothèque comme des items normaux. Les cartes `.strm` sont exclues
> (extension `.strm`).

### Flux (trois stages)

1. **S1 — nettoyage + recherche multilingue.** Le titre EPG est débarrassé de son
   bruit par `CleanEpgTitle` (marqueurs `HD`/`VOSTFR`/`VF`/`VO`, « Rediff. »/« Inédit »,
   `S##E##` / `Saison \d` / `Épisode \d`, parenthèses) puis recherché sur TMDB en
   plusieurs langues : `en-US` (titre original), `fr-FR` (titre France), + la langue de
   l'usager. Un candidat est retenu si le **titre normalisé** correspond (garde-fou
   contre un mauvais match ambigu), avec contrôle de l'année. **S1 n'est lancé que si
   `ProductionYear` est connu** : sans année fiable, la recherche TMDB est large et la
   garde lexicale (sans juge) pourrait accepter un faux film homonyme — les orphelins
   sans année vont directement à S2/S3.
2. **S2 — proposition LLM validée par TMDB** (si S1 échoue). `LlmRunner.ResolveIdsAsync`
   demande au LLM un id IMDb/TMDB à partir du titre EPG + overview + chaîne (appel
   one-shot, multi-backend avec repli). La proposition n'est **jamais appliquée telle
   quelle** : elle est validée via `FindByExternalIdAsync` (TMDB `/find` par `imdb_id`)
   ou `LookupMetaByIdAsync` (détail par `tmdb_id`) — **TMDB est la source de vérité**, un
   id halluciné renvoie null. À défaut, le titre original proposé est passé à S1.
   Chaque candidat doit ensuite passer une **porte d'acceptation sémantique** :
   - **garde-fou année** (`YearCompatible`, ±1 an) ;
   - **juge LLM de synopsis** (`LlmRunner.JudgeSynopsisMatchAsync`) qui compare le
     synopsis EPG au synopsis TMDB et confirme qu'ils décrivent la *même œuvre* — un id
     qui existe mais qui pointe vers un film homonyme d'une autre époque (ex. « Le
     guérisseur » 1953 vs 2017) est **rejeté**, et on continue de chercher. Reproduit la
     méthode manuelle de l'usager (comparaison synopsis + date). Skippé quand l'EPG n'a
     pas de synopsis (retour à année + titre). Le verdict + la justification sont logués.
3. **S3 — recherche web (SearXNG) → id IMDb** (si S1 et S2 échouent, et
   `OrphanSearXngEnabled`). La tâche interroge l'instance **SearXNG** auto-hébergée
   (champ `SearXngUrl`, déjà utilisé par l'outil `web_search` du LLM ; repli Ollama
   cloud), extrait les **ids IMDb** des URLs de résultats (regex
   `imdb.com/.../title/tt…`, ordre d'apparition = pertinence SearXNG), puis valide
   chaque id via `FindByExternalIdAsync` + la **même porte d'acceptation** (année + juge
   synopsis). Reproduit **exactement** la méthode manuelle de l'usager (web-search du
   titre → id IMDb → Emby tire TMDB → comparaison synopsis+date) et résout les **titres
   paraphrasés québécois** qu'aucun catalogue ne connaît (ex. « L'histoire de Jean
   Seberg » → film « Seberg » 2019 → tt1780967). Un candidat accepté **sans synopsis à
   comparer** est logué « à confirmer visuellement » (on fait confiance au classement
   SearXNG, comme l'usager le ferait avant de valider à la main).

### Application non destructive + verrouillage

Quand un candidat est validé (et hors dry-run), `OrphanIdentifyTask` :

- remplit les **ids provider** absents (`SetProviderId` `tmdb`/`imdb`/`tvdb`) ;
- remplit un `Overview` **vide**, des `Genres` **vides**, un poster `Primary`
  **manquant** (téléchargé depuis TMDB, `IProviderManager.SaveImage`) — jamais n'écrase
  une valeur existante ;
- **verrouille `MetadataFields.Name`** (le titre EPG n'est **jamais modifié** — préservé
  pour scanner l'EPG plus tard à la recherche de nouveaux programmes) ainsi que les
  champs remplis (`Overview`/`Genres`) — **add-only** : aucun verrou existant n'est
  retiré, reflétant la pratique manuelle de l'usager ;
- ajoute le tag **`llmai-identified`** et persiste (`UpdateToRepository`).

Les orphelins qu'aucun stage ne résout sont tagués **`llmai-needs-review`** (à revérifier
à la main) — aucun id n'est écrit.

### Idempotence & dry-run

Les items déjà tagués `llmai-identified` sont **ignorés** au passage suivant
(idempotence par tags). Avec **`OrphanRetryNeedsReview`**, les items `llmai-needs-review`
sont **retraités** (au lieu d'être ignorés) — pour y repasser S3 une fois SearXNG
configuré ; en cas de résolution, le tag `needs-review` est **remplacé** par
`identified`. Avec **`OrphanIdentifyDryRun`**, la tâche n'écrit
rien : elle logue chaque orphelin + la résolution proposée (S1/S2/S3) et un bilan
(résolus / needs-review / ignorés / erreurs) — pour valider la qualité des résolutions
avant de basculer en application. Best-effort : un item en erreur n'interrompt jamais le
passage (per-item try/catch). Scope : **items de bibliothèque `Movie`/`Series`**
(enregistrements DVR terminés importés en bibliothèque), pas les cartes `.strm`.

> ⚠️ **Année de référence** : l'année utilisée par S1 (filtre `primary_release_year`) et
> par la garde-fou année de S2/S3 est `ProductionYear` **uniquement**. Pour un
> enregistrement DVR, `PremiereDate`/`DateCreated` sont des dates de **diffusion** ou
> d'enregistrement (ex. 2024), pas l'année de sortie du film — les utiliser filtrait
> TMDB à tort et ratait des films existants. Les orphelins sans `ProductionYear`
> sautent S1 et s'appuient sur le juge synopsis (S2/S3) pour éviter un faux match.

> 📌 **Vérification recommandée** : activer `OrphanIdentifyEnabled` **avec**
> `OrphanIdentifyDryRun` coché, déclencher la tâche manuellement (Dashboard ▶ Tâches
> planifiées) et inspecter les lignes `[LLM_AI] OrphanIdentify` du journal avant de
> décocher le dry-run pour une vraie application.

---

## API HTTP

```
GET /Plugins/LLMAI/Tonight?userId=<id>&refresh=<0|1>
```

**Réponse :** `{ Enabled, Items, Date, FromCache, Error }` — `Items` est une chaîne JSON
(tableau de recommandations `{title, kind, reason, priority, source, channel, start, id,
showbizz_match, image_url, library_id, …}`).

**Authentification :** token de session (`X-Emby-Token`) **ou** clé API. Avec une clé API,
le contexte d'auth renvoie un usager `null` / `UserId=0` ; le plugin résout alors l'usager
via le paramètre `userId`, à défaut par le **premier usager** (usage domestique / clé API).
Un usager normal ne peut pas consulter l'historique d'un autre : `userId` est vérifié
contre l'usager authentifié (sauf admin).

**Test :**
```bash
curl -H "X-Emby-Token: <token>" \
  "http://localhost:8096/emby/Plugins/LLMAI/Tonight?userId=<id>&refresh=1"
```

```
GET /Plugins/LLMAI/Activate?programId=<id>&kind=<series|movie>&t=<StrmSecret>
```

**Activation d'une carte `.strm`** : appelé par le lecteur média à la lecture d'une
carte de la bibliothèque `.strm`. Crée l'enregistrement (`AutoProgrammer.ProgramOneAsync`,
dedup par `programId`) puis stream `recording_activated.mp4` (8 s, 1280×720). Supporte le
`Range` (206 + `Content-Range`).

**Authentification :** **aucune** (DTO `[Unauthenticated]`) — les lecteurs / `ffprobe`
n'ont pas de token Emby. L'unique garde est le jeton **`StrmSecret`** (`t=`),
auto-généré et comparé en temps constant. Un `t` invalide → 403.

**Test :**
```bash
curl "http://localhost:8096/emby/Plugins/LLMAI/Activate?programId=<id>&kind=movie&t=<StrmSecret>" -o clip.mp4
```

```
GET /Plugins/LLMAI/Audit?focus=<texte optionnel>
```

**Audit santé à la demande** : lance un run agent dédié (outil `system_audit`) et
renvoie un **rapport Markdown** de santé du serveur. Voir [Audit santé](#audit-santé).

**Réponse :** `{ Enabled, Report, Date, Error }` — `Report` est le rapport Markdown brut
(rendu côté config.js via un mini-convertisseur Markdown→HTML sûr). `Enabled=false` si
`AuditEnabled` est off ; `Error="Réservé aux administrateurs."` si l'appelant n'est pas
admin.

**Paramètre `focus` :** orientation libre de l'audit (un domaine à inspecter, ou une
demande explicite de remédiation). Appendé au template `AuditPrompt`. Laisser vide pour
un audit complet.

**Authentification :** **admin uniquement**. Résolution de l'usager via le token de
session (`X-Emby-Token`) ou la clé API, puis vérification `Policy.IsAdministrator`. Un
non-admin reçoit `Error` (pas de run LLM).

**Test :**
```bash
curl -H "X-Emby-Token: <token-admin>" \
  "http://localhost:8096/emby/Plugins/LLMAI/Audit?focus=transcoding"
```

---

## i18n (FR / EN)

**Côté navigateur** (`i18n.js`) : dictionnaire `STRINGS { fr, en }`, fonction
`t(key, …args)`. Chargé via `require([ApiClient.getUrl("web/ConfigurationPage",
{name:"LLMAII18n"})])`. Toutes les étiquettes visibles (sections, boutons, champs de
config, messages d'erreur/vide/loading) passent par `t(...)`. Pour ajouter une langue,
ajouter une branche dans `STRINGS` et un sélecteur de langue côté page.

**Côté serveur** (`I18n.cs`) : dictionnaires inline FR/EN (`s_res`) + résolution de langue.
**Deux buckets** distincts :

- **métadonnées** (`ResolveMetaLangKey`) — `<plot>` du `.nfo`, synopsis TMDB, prose LLM :
  précédence `ResponseLanguage` → langue d'affichage Emby → legacy `TmdbLanguage` →
  anglais ;
- **interface** (`ResolveDisplayLangKey`) — nom/description des tâches planifiées :
  langue d'affichage Emby (`UICulture`), repli anglais.

Helpers `ToTmdbLang` (clé 2 lettres → code TMDB `fr-FR`/`en-US`…) et `ToLangName` (→ nom
humain pour la cible de traduction LLM). Extensible par la donnée : ajouter une entrée
`I18n.s_res` (les langues sans dictionnaire retombent sur l'anglais pour les courts
libellés ; le synopsis TMDB et la prose LLM restent dans la langue de l'usager via la
cascade TMDB + traduction LLM en dernier recours).

---

## Dépannage

**Le LLM local « choke » sur « À regarder ce soir » :**
Le contexte injecté est trop volumineux pour un modèle local. Le mode compact s'active
automatiquement si le primaire est `OllamaLocal`. Ajuster : réduire `MaxTonightBatch`,
vérifier que le backend local a la plus haute `Priority`, ou utiliser un backend cloud.

**L'EPG renvoie 0 programmes :**
Vérifier `TonightWindowStart`/`TonightWindowEnd` (format `HH:mm`) et que l'EPG est peuplé.
Le fallback « réserve bibliothèque » garantit quand même `TonightMinRecommendations` recos.

**Bibliothèque `.strm` : « bibliothèque introuvable » :**
`StrmLibraryName` doit correspondre au **nom exact** affiché dans le dashboard Emby.
L'UserView est slugifié avec des **tirets** (`ai-suggestions`) tandis que le dossier
CollectionFolder porte souvent le nom typé (`ai_suggestions`). En cas de mismatch,
recopier le nom du dashboard. Créer d'abord une bibliothèque **Films** (ou Contenu mixte)
pointant vers un dossier vide avant d'activer `StrmLibraryEnabled`.

**Carte `.strm` : « No compatible streams » / ffprobe « Input/output error » :**
Les lecteurs / `ffprobe` n'ont pas de token Emby → 401 avant le `Get()` de l'endpoint.
Le DTO est `[Unauthenticated]` (gated par `StrmSecret`). Vérifier que le `t=` dans le
`.strm` correspond au `StrmSecret` courant (comparaison temps constant ; invalide → 403).
Pour `Range: bytes=0-` (EOF), l'endpoint sert le corps complet + `Content-Range: 0-<len-1>/<len>`.

**Seul le bouton « Oublier » s'affiche :**
La lecture utilise `playbackManager` (module AMD), pas `ApiClient.play`. Recharger le JS
du plugin (cache navigateur). Pour les recos `library` sans `id`, l'enrichissement
`EnrichWithLibrary` remplit l'id rétroactivement — vérifier que le titre correspond à un
item bibliothèque.

**Clé API : « Utilisateur non authentifié » :**
Une clé API authentifie mais ne fournit pas d'usager (`User=null`). Le plugin retombe sur
le paramètre `userId` puis sur le premier usager. Passer `?userId=<id>` explicite.

**Boutons/labels non traduits :**
Hard-reload (cache JS Emby). Vérifier que la clé `i18n` existe dans `STRINGS.fr` **et**
`STRINGS.en`.

**Build : 0 warning / 0 erreur attendu :** `bash deploy.sh` doit terminer sans erreur ni
warning. Les JS sont validés par `node --check` avant déploiement.

---

## Changelog

Les changements notables sont consignés dans [CHANGELOG.md](CHANGELOG.md). Licence :
[MIT](LICENSE).