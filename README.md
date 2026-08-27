# LLM_AI — Plugin Emby de recommandations par LLM

**Version :** 1.0.0.0 · **Id :** `e7d3dee6-ef19-46a9-985f-06318b682e60` · **Cible :** Emby (net8.0)

> Version anglaise : voir [README-EN.md](README-EN.md).

Plugin Emby qui utilise un grand modèle de langage (LLM — Ollama local, Ollama Cloud ou
Google Gemini) pour produire des **recommandations de séries et de films à enregistrer**
(planifiées au niveau serveur) et une section personnalisée **« À regarder ce soir »**
par usager. Le LLM dispose d'outils pour interroger la bibliothèque Emby, l'EPG, TMDB,
TVDB, le web et une source « Showbizz » de nouveautés ; il décide lui-même des appels
d'outils à effectuer (boucle d'agent / tool-calling).

---

## Table des matières

1. [Aperçu](#aperçu)
2. [Installation](#installation)
3. [Configuration](#configuration)
4. [Composants](#composants)
5. [Outils LLM](#outils-llm)
6. [« À regarder ce soir »](#à-regarder-ce-soir)
7. [Auto-programmation & popup au login](#auto-programmation--popup-au-login)
8. [API HTTP](#api-http)
9. [i18n (FR / EN)](#i18n-fr--en)
10. [Dépannage](#dépannage)
11. [Changelog](#changelog)

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

### Divers

`TmdbLanguage`, `SearXngUrl` (recherche web auto-hébergée), `WebFetchDirect`,
`ShowbizzUrl` / `ShowbizzPattern`, `RagDirectives` (directives additionnelles injectées
dans le prompt), `ScheduleTask` / `ScheduleTaskMovies` (cron de la tâche planifiée),
`DebugVerbose`.

---

## Composants

| Fichier | Classe | Rôle |
|---|---|---|
| `Plugin.cs` | `Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage` | Point d'entrée. Nom `LLM_AI`, Id `e7d3…2e60`. Enregistre les pages web (config, recommandations, i18n). Version pilotée par `<AssemblyVersion>` du `.csproj`. |
| `PluginConfiguration.cs` | `PluginConfiguration` (+ `LlmBackend`, `LlmProvider`) | Toute la config persistée + backends multi-source. |
| `LlmScheduledTask.cs` | `LlmScheduledTask : IScheduledTask, IConfigurableScheduledTask` | Tâche planifiée globale (admin) : produit les recos **Séries / Films** en parcourant l'EPG, stocke dans `Recommendations`, envoie des notifications. Délègue l'orchestration à `LlmRunner`. |
| `TonightApiService.cs` | `TonightApiService : BaseApiService` | Endpoint HTTP **par usager à la demande** `GET /Plugins/LLMAI/Tonight`. Couche HTTP fine : résout l’usager puis délègue à `TonightService`. |
| `TonightService.cs` | `TonightService` (interne) | **Génération partagée** « À regarder ce soir » : profil de goût, enregistrements non visionnés, réserve bibliothèque, run LLM, enrichissement, **cache par usager** (statique, partagé endpoint + login). Utilisé par `TonightApiService` et `TonightLoginService`. |
| `AutoProgrammer.cs` | `AutoProgrammer` (interne) | Auto-programmation : crée les timers Emby (SeriesTimer / Timer unique) du **record bucket** — recos à enregistrer non possédées/non déjà programmées/hors drop list. Portage serveur de la logique « Programmer » de `recommendations.js`. |
| `TonightLoginService.cs` | `TonightLoginService : IServerEntryPoint` | Déclencheur de login : branche `ISessionManager.SessionStarted`, lance `TonightService` (cache-aware), auto-programme (si `AutoProgram`), envoie un **toast** (`SendMessageCommand`, gated `DisplayMessage`) + **cloche** persistante (deep-link). Pattern `Emby.ComSkipper`. |
| `LlmRunner.cs` | `LlmRunner` (classe interne) | **Orchestration partagée** : `ResolveBackends`, `RunAsync` (boucle d'agent + tool-calling), `EnrichRecommendations` (match titre → id/chaîne/poster/note), `EnrichWithLibrary`, `FindLibraryItem`, `MergeJsonArrays`, `ExtractJsonPayload`, `NormTitle`, résolution des clés via env. Utilisé par `LlmScheduledTask` **et** `TonightApiService`. |
| `LlmAgentService.cs` | `LlmAgentService` | Boucle d'agent : envoie le prompt au LLM, exécute les tool-calls, reboucle jusqu'à la réponse finale. |
| `LlmClient.cs` | `LlmClient` (statique) | Appels HTTP bruts vers Ollama / Gemini (sans clé en clair dans les journaux). |
| `GetEmbyInfoTool.cs` | `GetEmbyInfoTool` | Outil `get_emby_info` (voir [Outils](#outils-llm)). |
| `TmdbLookupTool.cs` / `TvdbSearchTool.cs` / `WebSearchTool.cs` / `WebFetchTool.cs` / `ShowbizzTool.cs` | … | Outils LLM spécialisés (voir [Outils](#outils-llm)). |
| `config.html` / `config.js` | — | Page de configuration (saisie des champs ci-dessus). |
| `recommendations.html` / `recommendations.js` | — | Page Recommandations (rendu des 3 sections, cartes, boutons). |
| `i18n.js` | — | Chaînes localisées FR/EN + endpoint `web/ConfigurationPage?name=LLMAII18n`. |
| `deploy.sh` | — | Build + déploiement + redémarrage (voir [Installation](#installation)). |

### Flux de données

**Tâche planifiée (Séries/Films) :**
1. Cron `ScheduleTask`/`ScheduleTaskMovies` → `LlmScheduledTask.Execute`.
2. `LlmRunner.ResolveBackends` choisit le backend primaire.
3. Prompt + outils → `LlmAgentService` boucle d'agent (le LLM appelle `get_emby_info`
   `epg_series`/`epg_movies`, `tmdb_lookup`, `web_search`/`web_fetch`, `showbizz…`).
4. `EnrichRecommendations` → posters/notes/id/chaîne. Stockage dans `Recommendations`.
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

---

## i18n (FR / EN)

Système maison (`i18n.js`) : dictionnaire `STRINGS { fr, en }`, fonction `t(key, …args)`.
Chargé côté navigateur via `require([ApiClient.getUrl("web/ConfigurationPage",
{name:"LLMAII18n"})])`. Toutes les étiquettes visibles (sections, boutons, champs de
config, messages d'erreur/vide/loading) passent par `t(...)`. Pour ajouter une langue,
ajouter une branche dans `STRINGS` et un sélecteur de langue côté page.

---

## Dépannage

**Le LLM local « choke » sur « À regarder ce soir » :**
Le contexte injecté est trop volumineux pour un modèle local. Le mode compact s'active
automatiquement si le primaire est `OllamaLocal`. Ajuster : réduire `MaxTonightBatch`,
vérifier que le backend local a la plus haute `Priority`, ou utiliser un backend cloud.

**L'EPG renvoie 0 programmes :**
Vérifier `TonightWindowStart`/`TonightWindowEnd` (format `HH:mm`) et que l'EPG est peuplé.
Le fallback « réserve bibliothèque » garantit quand même `TonightMinRecommendations` recos.

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