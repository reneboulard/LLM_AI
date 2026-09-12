<!-- ⚠️ NE PAS RENOMMER le titre H1 ci-dessous sans répercuter le changement :
     l'ancre GitHub #llm_ai--plugin-emby-de-recommandations-par-llm est générée
     à partir de ce titre et est référencée par le lien « Documentation
     complète » de la page de config du plugin (config.html, href à mettre à
     jour en cas de renommage). -->
# LLM_AI — Plugin Emby de recommandations par LLM

**Version :** 1.13.10.1 · **Id :** `e7d3dee6-ef19-46a9-985f-06318b682e60` · **Cible :** Emby (net8.0)

> Version anglaise : voir [README-EN.md](README-EN.md).

## En clair : l'assistant qui connaît votre télé

### Le problème

Vous avez un serveur Emby qui enregistre la télé. Chaque soir, la même question :
*« qu'est-ce que je pourrais regarder ou enregistrer ce soir ? »* Répondre soi-même
demande de feuilleter le guide TV (des centaines d'émissions), de se souvenir de ce
qu'on a déjà vu, de ce qu'on a déjà enregistré, de vérifier si le film en vaut la
peine… C'est un travail de tri fastidieux — et c'est exactement le genre de chose
qu'une IA sait faire.

### L'idée

**LLM_AI** est un assistant intégré à Emby qui **lit le programme TV à votre place**
et vous dit quoi regarder ou enregistrer. Ce n'est pas un robot qui fait n'importe
quoi : il pose ses questions à votre serveur (qu'y a-t-il ce soir ? qu'est-ce que
j'aime déjà ? qu'est-ce que j'ai déjà ?), complète avec des sources externes
(fiches de films, web, listes de nouveautés), puis **réfléchit et propose**, toujours
avec une explication en bon français.

### Ce qu'il fait

- **Une page « Recommandations »** : des séries et des films à enregistrer bientôt,
  chacun avec sa raison d'être.
- **« À regarder ce soir »** : une sélection personnalisée par personne du foyer —
  vos goûts, ce que vous avez commencé sans finir, les enregistrements qui
  s'accumulent (« tu as 6 épisodes de cette série : temps de commencer »).
- **La remise des suggestions, à votre goût** : playlist, collection, tag, favoris,
  cartes dans une bibliothèque ou enregistrement programmé directement — chaque
  option se coche et se décoche indépendamment, et tout se nettoie tout seul chaque
  nuit. **Le plugin ne supprime jamais rien lui-même.**

### Il apprend avec le temps

- **Il apprend de ses erreurs** : une fois par semaine, il compare ce qu'il a
  recommandé avec ce que vous avez réellement regardé, et ajuste ses suggestions.
- **Il tient un carnet** : recommandations, visionnages, abandons sont notés, et il
  réécrit régulièrement une « fiche mémoire » de sa stratégie qu'il relit avant de
  proposer.
- **Vous gardez la main** : chaque suggestion peut être rejetée (« Oublier »), et les
  actions sensibles sont désactivées par défaut — il faut les activer volontairement.

### Les petits plus

- **Un chat avec l'assistant** : posez vos questions en langage naturel
  (« qu'est-ce qu'il y a comme documentaire cette semaine ? »).
- **Un rapport de santé du serveur** : à la demande, il inspecte Emby (disque,
  journaux, performances) et rédige un bilan avec des conseils — conservé d'une
  relecture à l'autre, sans relancer l'audit.
- **Les fiches incomplètes réparées** : quand une émission enregistrée reste sans
  fiche (titre introuvable dans les catalogues), il la retrouve sur le web et remplit
  les informations manquantes, comme le ferait une recherche manuelle.

### Ce dont il a besoin : des données propres

L'assistant **ne peut évaluer que ce qu'on lui montre**. Or le guide TV et votre
vidéothèque ne parlent pas les mêmes mots : le guide écrit « Kids », votre
bibliothèque dit « Enfant » ; l'émission est notée « PG-13 » ou « 13+ » d'un côté,
« 14A » de l'autre. Pour un assistant qui compare des listes, ce sont des mots
différents — et des comparaisons moins justes.

Deux plugins font ce ménage en amont et sont **fortement recommandés** :

- **GenreCleaner** — un vocabulaire unique pour les genres dans toute la
  vidéothèque ;
- **Classification Mapper** — une seule liste pour les classifications d'âge.

Quand ils sont installés, LLM_AI lit automatiquement leur travail, traduit lui-même
le guide TV dans le même vocabulaire, et peut même proposer les traductions
manquantes que vous validez d'un clic. **Des métadonnées propres en amont, des
recommandations pertinentes en aval** — l'assistant ne remplace pas le ménage, il le
prolonge.

### Accessible à tous, sans ordinateur de course

On imagine souvent que l'IA exige une grosse machine. Pas nécessairement :

- **Un modèle en ligne gratuit** — avec un simple **compte gratuit chez Ollama**,
  vous utilisez leurs modèles de base hébergés sur leurs serveurs. C'est le chemin
  le plus simple : aucun logiciel à installer, aucune configuration technique, et
  c'est suffisant pour tout ce que fait le plugin. C'est aussi l'option des petites
  machines : même sur un **Raspberry Pi 4**, l'ordinateur n'a pas à faire le travail
  de réflexion — il le confie au cloud.
- **Un modèle local, en option** — faire tourner l'IA **chez vous** est la variante
  toute privée (rien ne quitte votre réseau), mais elle demande du matériel : **un
  ordinateur avec une carte graphique d'au moins 8 Go de mémoire**. En dessous, le
  modèle tourne, mais ses réponses prennent une éternité. Sur une petite machine,
  laissez ce travail au cloud.
- **Les deux à la fois** — le plugin accepte plusieurs services en parallèle et
  choisit lui-même : votre modèle local quand il suffit, le modèle en ligne en
  renfort, et il **bascule automatiquement** si l'un est indisponible.

En résumé : le coût peut être **zéro dollar**, et selon votre matériel, la réflexion
se fait chez vous (machine avec GPU 8 Go+) ou gratuitement dans le cloud Ollama —
dans les deux cas, le plugin s'adapte.

### En une phrase

**C'est un majordome pour votre télé : il surveille le guide, connaît vos goûts,
vous dit quoi regarder ou enregistrer, et range les informations — mais il ne jette
jamais rien et ne décide rien de sensible sans votre accord.**

---

## 📡 Une recommandation, plusieurs sorties

Le plugin affiche ses recommandations dans une **page web** (Recommandations,
après login) et notifie à l'ouverture — cette page ne modifie rien dans Emby.
En option, les mêmes recommandations peuvent être livrées **à l'intérieur
d'Emby**, là où vous les regardez : chaque sortie est **indépendante**
(cochez ce qui vous sert, seule) et **réversible** (nettoyée chaque nuit à
3 h, ou recréée au run suivant) — tout se nettoie tout seul, le plugin ne
supprime jamais rien lui-même.

| Je veux… | Cochez (config) | Ce que ça crée dans Emby |
|---|---|---|
| **Enchaîner ma soirée ce soir** | Playlist « AI Tonight » | Playlist jouable, refaite à chaque run |
| **Regrouper / parcourir librement** | Collection « AI Tonight » | BoxSet navigable |
| **Retrouver les recos par un filtre** | Tag « AI Tonight » | Tag Emby (filtre « Tags ») |
| **Marquer les recos ❤️** | Favoris « AI Tonight » | Favoris de l'usager choisi |
| **Enregistrer automatiquement** ce qui n'est pas possédé | Timers auto (DVR) | Timers d'enregistrement Emby |
| **Enregistrer en un clic, à la demande** | Bibliothèque .strm « AI Suggestions » | Cartes .strm jouables |
| **Être suggéré quoi supprimer** quand le disque est plein | Tag « AI Delete » | Tag Emby (aucune suppression auto) |

Détail de chaque option dans la section
[Surfaces natives des recommandations](#surfaces-natives-des-recommandations).

Plugin Emby qui utilise un grand modèle de langage (LLM — [Ollama](https://ollama.com) local, Ollama Cloud ou
Google Gemini) pour produire des **recommandations de séries et de films à enregistrer**
(planifiées au niveau serveur) et une section personnalisée **« À regarder ce soir »**
par usager. Le LLM dispose d'outils pour interroger la bibliothèque Emby, l'EPG, TMDB,
TVDB, le web et des sources « nouveautés » configurables (flux RSS/Atom ou page HTML
+ regex — outil `new_releases`) ; il décide lui-même des appels
d'outils à effectuer (boucle d'agent / tool-calling).

Il expose aussi un **audit santé du serveur** à la demande (`GET /Plugins/LLMAI/Audit`,
admin) : un agent LLM interroge l'outil `system_audit` (sessions, tâches planifiées,
transcodage, disques, journaux, métriques hôte, bibliothèque) et produit un **rapport
Markdown** de santé (constats tagués par gravité + actions recommandées). Le **dernier
rapport réussi est persisté** et affiché par défaut au chargement de la page de config —
sa relecture ne coûte aucun LLM. La **remédiation** (arrêter une session, déclencher une
tâche, notifier un usager) est désactivée par défaut (opt-in). Voir [Audit santé](#audit-santé).

Le plugin comprend enfin deux ponts avec **GenreCleaner** (plugin de nettoyage des
genres du catalogue officiel Emby) : une **traduction IA des genres EPG** qui fait proposer par le
LLM, pour chaque genre EPG non couvert, un équivalent de votre vocabulaire curaté —
voire un **nouveau genre** à ajouter — puis écrit les mappages acceptés dans
`GenreCleaner.xml` (voir [Traduction IA des genres](#traduction-ia-des-genres-epg-genrecleaner)),
et un **chat LLM admin** (page « LLM_AI Chat », tous les outils de l'agent à
conversation, voir [Chat LLM](#chat-llm-admin)).

Depuis v1.7, le plugin embarque une **mémoire réflexive** (opt-in) : chaque
recommandation émise est journalisée avec sa raison, chaque lecture terminée avec son
pourcentage visionné, et une tâche hebdomadaire fait **réécrire par le LLM sa propre
fiche mémoire** (~250 mots) à partir des événements bruts — fiche ensuite réinjectée
dans ses prompts (recommandations, enregistrement, chat). Le chat dispose en outre
d'une **mémoire de conversation** (bouton « Reprendre », résumé de session, signaux de
goût). Voir [Mémoire réflexive](#mémoire-réflexive).

---

## Table des matières

1. [Aperçu](#aperçu)
2. [Installation](#installation)
3. [Configuration](#configuration)
4. [Composants](#composants)
5. [Outils LLM](#outils-llm)
6. [« À regarder ce soir »](#à-regarder-ce-soir)
7. [Boucle de rétroaction des recommandations](#boucle-de-rétroaction-des-recommandations)
8. [Auto-programmation & popup au login](#auto-programmation--popup-au-login)
9. [Surfaces natives des recommandations](#surfaces-natives-des-recommandations)
10. [Audit santé](#audit-santé)
11. [Identification des enregistrements orphelins](#identification-des-enregistrements-orphelins)
12. [Traduction IA des genres EPG (GenreCleaner)](#traduction-ia-des-genres-epg-genrecleaner)
13. [Classifications officielles (Classification Mapper)](#classifications-officielles-classification-mapper)
14. [Chat LLM (admin)](#chat-llm-admin)
15. [Mémoire réflexive](#mémoire-réflexive)
16. [API HTTP](#api-http)
17. [i18n (FR / EN)](#i18n-fr--en)
18. [Dépannage](#dépannage)
19. [Changelog](#changelog)

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
   et le service (`emby-server`), supprime l'ancienne DLL du plugin, copie la DLL et
   redémarre Emby. Variables d'env optionnelles : `EMBY_PLUGINS_DIR`, `EMBY_SERVICE`.
4. Dans Emby : **Plugins** → **LLM_AI** → configurer (voir [Configuration](#configuration)).
5. Recharger simplement la page (**F5**) après le redémarrage d'Emby — **une
   nouvelle session navigateur suffit**. Le buste de cache intégré (v1.13.4.3)
   garantit la lecture réseau des ressources plugin à chaque session (paramètre
   `?v=` par session), et le module de version réécrit les caches puis recharge
   la page quand il détecte un changement de version serveur. Un hard-reload
   (Ctrl+Shift+R) ne reste utile qu'en cas de cache intermédiaire (proxy) hors
   de portée du navigateur.

### Depuis les sources (développeur)

Prérequis : Emby Server (build net8.0), .NET SDK 8.

- `bash deploy.sh` depuis la racine du projet : compile en `Release net8.0`, copie
  `LLM_AI.dll` dans `/var/lib/emby/plugins/`, supprime l'ancienne DLL du plugin,
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
| `Url` | URL de l'API (ex. `http://localhost:11434` pour [Ollama](https://ollama.com) local) |
| `Model` | Nom du modèle (ex. `llama3.1`, `gemini-1.5-flash`) |
| `Enabled` | Activer ce backend |
| `Priority` | Ordre de préférence (plus haut = primaire) |

Champs hérités `LlmUrl` / `ModelName` restent supportés (repli legacy : un `LlmUrl` non
vide est traité comme un backend local).

### Aides de la page de configuration

- **Bouton « Tester » par backend** (`POST /Plugins/LLMAI/TestLlm`, admin-only) : appel
  rapide au backend **tel qu'édité** (une question-sonde dans la langue configurée,
  timeout 30 s) — testable avant enregistrement. Les clés API ne sont pas postées par
  la page : le serveur les relit depuis la config enregistrée. Résultat inline sous
  l'en-tête de la ligne : OK + latence ou message d'échec.
- **Bouton « Réinitialiser » sur les cinq prompts éditables** (Directives RAG, tâche
  Séries, tâche Films, prompt « ce soir », audit santé) : restaure la version propre
  dans la langue de réponse (`?Lang=` forcé, sinon `ResponseLanguage` si renseignée,
  sinon langue d'affichage Emby). Une installation neuve installe le français ; un
  usager anglophone clique Réinitialiser et obtient la directive en anglais. Le bouton
  remplit le textarea sans enregistrer. Source unique : `DefaultPrompts.cs` (FR + EN),
  aussi utilisée par les valeurs par défaut des champs pour une nouvelle installation.
- **Sections repliables** : chaque titre de section replie son contenu (chevron,
  clavier Enter/Espace) ; bouton global « Replier tout / Déplier tout » ; l'état par
  section est mémorisé par navigateur. Le bouton « Enregistrer » reste toujours visible.

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
- `TonightBingeEnabled` (bool, **défaut `false` — opt-in explicite**) — signale les séries
  « prêtes à dévorer » : l'usager enregistre une série et attend d'avoir plusieurs épisodes
  avant de commencer ; quand le stock non visionné atteint le seuil, le run « ce soir »
  propose de commencer (une seule fois par cycle, voir
  [À regarder ce soir](#à-regarder-ce-soir)).
- `TonightBingeThreshold` (défaut 4) — nombre d'épisodes non visionnés à partir duquel la
  suggestion « temps de commencer » se déclenche.
- `TonightBingeActiveDays` (défaut 14) — fenêtre d'activité : au moins un épisode de la
  série doit être arrivé dans ces N derniers jours (signal « enregistrement actif » qui
  distingue une accumulation en cours d'une série dormante jamais commencée).

### Boucle de rétroaction des recommandations

- `RecoFeedbackEnabled` (bool, **défaut `false` — opt-in explicite**) : active la
  boucle de rétroaction — le plugin apprend de ses recommandations passées (voir
  [Boucle de rétroaction](#boucle-de-rétroaction-des-recommandations)).
- `PromptDirectives` (JSON `[{"u":"userId","n":"nom","d":"date","text":"…"}]`) —
  directives produites par l'analyse hebdo, réinjectées dans les prompts des runs.
  **Affichées et éditables** dans la page de config : l'admin peut corriger ou
  vider le JSON pour retirer une directive des prompts.
- `RecoLog` (interne) — journal roulant des recos/rejets (30 jours, plafond 500
  entrées), maintenu côté serveur (carry-forward par la page de config).

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
  connexion d’un usager, une **séquence de popups** — une par suggestion
  « À regarder ce soir » (enregistrements non visionnés, bibliothèque **et**
  programmes EPG live) — défile sur le client connecté : « 🤖 À regarder ce
  soir (1/3) — Titre (chaîne · heure · type) ». Le rythme est **adapté au
  client** : 4 s sur le client web (son toast ignore `TimeoutMs` et se fond
  sur une animation fixe ~3 s), sinon `LoginPopupSeconds` (défaut 8 s),
  honoré par l’app Android TV. Une **notification Emby** accompagne la
  séquence — livrée via les notifiers configurés (ex. courriel SMTP, voir
  [Watch bucket → popup au login](#watch-bucket--popup-au-login)).

> ⚠️ L’auto-programmation occupe des tuners/disque : c’est une action opt-in.
> L’utilisateur peut annuler un timer indésirable dans Emby. Le popup au login
> s’affiche même sans auto-programmation (suggestions à regarder seulement).

#### Seuil disque du dossier d’enregistrements (v1.11)

Deux garde-fous **non destructifs** autour du disque qui reçoit les
enregistrements Live TV (`RecordingDiskManager.cs`) :

- `RecordingDiskThresholdGb` (int, défaut 25, `0` = désactivé) : quand l’espace
  libre du volume d’enregistrements passe sous ce seuil, **aucun nouveau timer**
  n’est créé (tâche planifiée, popup au login et endpoint Activate) — les timers
  déjà créés continuent (Emby les possède). Une notification Emby signale le
  blocage. Le gate **échoue ouvert** : chemin d’enregistrements inconnu ou
  volume illisible ne bloque jamais.
- `RecordingTaggingEnabled` (bool, **opt-in**, défaut `false`) : quand le seuil
  est franchi, les enregistrements **visionnés** (par n’importe quel usager) sont
  tagués **« AI Delete »** (tag), du plus ancien au plus récent (avec leur
  taille réelle), jusqu’à ce que leur suppression ramène l’espace libre au-dessus
  du seuil (×1.2 de marge). **Pure suggestion — le plugin ne supprime jamais de
  fichier** : l’usager filtre sa bibliothèque d’enregistrements par ce tag
  (filtre « Tags ») et supprime lui-même. Chaque passe retire **d’abord** les tags précédents (reset) :
  les tags reflètent toujours la dernière évaluation, et si l’espace s’est
  libéré, tout disparaît. Une sonde quotidienne (3 h, via la tâche de nettoyage
  nocturne) fait le reset même sans événement auto-program. Les enregistrements
  non visionnés ne sont jamais tagués.

#### Retour des cartes .strm (v1.12)

Lire une carte « AI Suggestions » n'est plus un geste silencieux
(`ActivateFeedback.cs`) :

- **Toast Emby** sur le client qui lit la carte — « 🤖 Enregistrement programmé :
  Titre » (succès), « Déjà programmé » (un timer existant couvre déjà le
  programme), « Échec de l'enregistrement » ou « Disque d'enregistrements plein —
  programmation suspendue » (gate v1.11). Session retrouvée par le chemin du
  `.strm` en cours de lecture ; **pas de notification cloche** — l'usager a
  cliqué la carte, le toast suffit.
- **Suppression de la carte en cas de succès** : ~60 s après l'activation (fin de
  la lecture du clip), le plugin demande à **Emby** de supprimer l'item de la
  carte (`FindByPath` → `DeleteItem`, `DeleteFileLocation`) — l'item ET le
  fichier `.strm` quittent la bibliothèque, et le plugin ne supprime **jamais de
  fichier** lui-même (même philosophie que le seuil disque). Échec ou disque
  plein → la carte reste, réessayable. Constasté sur ce serveur : Emby retire
  **le dossier de carte entier** (`.strm` + `.nfo` + marker + poster), plus
  aucune trace à nettoyer.
- Une même lecture génère plusieurs appels à Activate (sonde ffmpeg, requêtes
  Range) : un cache TTL 5 min fait que seul le premier déclenche le feedback.
  Les cartes d'avant v1.12 (sans param `card`) restent jouables, sans toast ni
  suppression.

#### Gate de droits d'enregistrement (v1.13.11.0)

Programmer un enregistrement est une décision **foyer**, pilotée par le droit
d'enregistrement natif `EnableLiveTvManagement` ; l'intérêt de visionnement
(Watch Tonight) reste, lui, par usager. Un compte sans ce droit :

- **ne peut pas déclencher d'enregistrement en jouant une carte .strm** — les
  requêtes `.strm` ne portant pas l'auth Emby et la session lecteur n'étant
  visible qu'après l'ouverture du flux, le contrôle est asynchrone (même
  mécanique que le toast) : une fois le lecteur identifié, les timers créés par
  sa lecture sont **annulés** (seulement ceux de cette activation — un timer
  antérieur légitime n'est jamais touché) et un toast dédié l'informe (« 🤖
  Enregistrement non autorisé pour ce compte : Titre ») ; la carte reste en
  bibliothèque. Compte non identifiable (sonde serveur, api_key) → comportement
  inchangé, journalisé (`PermissionGate.cs`).
- **ne voit pas les sections d'enregistrement de la page Recommandations** —
  `/Plugins/LLMAI/Recos` répond `CanRecord` (policy lue à chaud) et la page
  masque Séries/Films sans ce droit ; la section « À regarder ce soir » reste
  visible. Le bouton « Programmer » est de toute façon protégé nativement par
  l'API LiveTv d'Emby.

Recommandation d'installation : réserver la bibliothèque « AI Suggestions »
(accès par dossier du tableau de bord) aux comptes disposant du droit
d'enregistrement — le dashboard reste maître des accès, le plugin ne modifie
jamais les comptes.

#### Reco visionnage conforme aux droits (v1.13.12.0)

L'intérêt de visionnement étant **par usager**, la recommandation « Watch
Tonight » respecte désormais les droits de l'usager demandeur (policy lue à
chaud, jamais en cache) :

- **TV en direct** (`EnableLiveTvAccess`) : **l'EPG n'est consulté que si
  l'usager porte ce droit**. Sans le droit, les tools EPG du run
  (`epg_tonight`/`epg_series`/`epg_movies` et la jambe EPG de `find`) renvoient
  un résultat **vide et légitime** (note explicite, le LLM réoriente), le
  snapshot EPG de validation est sauté et les recos live pures sont écartées
  (conservées seulement si enrichies d'un `library_id`, donc watchables depuis
  la bibliothèque). Avec le droit, le comportement est inchangé.
- **Accès médiathèque** (`EnableAllFolders`/`EnabledFolders`) : la réserve
  bibliothèque et les séries « prêtes à dévorer » ne proposent que des items
  des bibliothèques accessibles à l'usager. No-op si non restrictif ;
  fail-open si la résolution échoue (une indisponibilité ne vide jamais les
  recos). Le bucket « enregistrements » (décision foyer) et le profil de goût
  (historique déjà visionné) ne sont pas filtrés.
- **Page Recommandations** : `/Plugins/LLMAI/Recos` et `/Plugins/LLMAI/Tonight`
  portent les droits de l'usager (`CanRecord`, `CanLiveTv`) — sans droit TV en
  direct, le bouton « Regarder en direct » est masqué ; sans droit
  d'enregistrement, le bouton « Programmer » des cartes tonight l'est aussi
  (le refus natif Emby reste le filet).
- La suppression de médias n'a aucun impact : le plugin ne supprime jamais de
  médias.

#### Contrôle parental dans « Watch Tonight » (v1.13.15.0)

Les candidats des recos « Watch Tonight » respectent désormais la section
**Contrôle parental** de la policy de l'usager demandeur (pattern v1.13.12.0 :
dit au LLM en amont, imposé mécaniquement en aval) :

- **Limite parentale** (`MaxParentalRating`) : la réserve bibliothèque, les
  séries « prêtes à dévorer » et les recos `recording`/`library` sont filtrées
  sur la cote héritée native de l'item ; les recos EPG sont vérifiées sur la
  cote textuelle du programme, normalisée d'abord par le plugin Classification
  Mapper (si installé) puis par la table parentale du serveur — la même liste
  que le menu de limite du dashboard.
- **Non coté** (`BlockUnratedItems`) : un item sans cote (ou marqué NR) est
  écarté seulement si son type figure dans la liste de la policy — comme Emby
  natif. Les cotes non reconnues par la table serveur (pas de score natif)
  ne sont **jamais** bloquées (pas de décision forcée) : conservées et
  comptées à part dans le journal — l'audit `ratings_check` signale ce désordre.
- **Tags** (« Exclure le tag » / « Exclure tous sauf le tag ») : les deux
  modes de la page Contrôle parental sont impliqués — liste noire
  (`BlockedTags`, un tag porté → écarté) et liste blanche
  (`IsTagBlockingModeInclusive`, seul un tag listé est visible), cette
  dernière avec les deux sous-modes `AllowTagOrRating` (tag autorisé contourne
  ou non la limite). Pour un épisode, les tags de la série porteuse sont
  consultés. Cas dégénéré assumé : une liste blanche stricte peut ne rien
  laisser de recommandable — le run produit alors moins de recos **avec une
  explication affichée à l'usager** (`Warning` de la réponse Tonight) au lieu
  d'une liste courte muette.
- Le filtrage parental est **no-op** pour un usager sans règle, fail-open par
  item illisible, et les surfaces natives (tag/collection/playlist/favoris)
  n'héritent que des recos déjà validées — elles ne contiennent jamais d'item
  que la policy cache.

#### Playlists « AI Tonight » conformes aux droits (v1.13.16.0)

Le contrôle parental Emby est **listing-only** (validé empiriquement sur
4.10 : limite et tags filtrent les listings, mais la lecture d'un item visible
— y compris depuis une playlist — n'est PAS bloquée). La playlist publique
unique était donc un **contourne-ment** : un item au-dessus de la limite d'un
compte restreint, rempli par le run d'un usager sans limite, y restait lisible
par ce compte. Deux surfaces remplacent l'ancienne playlist unique :

- **Playlist privée par usager** (« AI Tonight · {usager} », `IsPublic=false`)
  : chaque run rafraîchit la playlist du run avec SES recos (déjà filtrées par
  sa policy) — plus de course de remplissage entre comptes.
- **Playlist publique foyer** (« AI Tonight », `IsPublic=true`) : remplie
  uniquement par les runs de l'usager « Tonight », avec **intersection
  parentale** — un item est écarté si un seul usager actif porte une règle qui
  l'interdit (même verdict que le gate des recos). La surface foyer ne peut
  plus exposer ce qu'un compte ne peut pas déjà lire.
- **Audit aligné (volet sécurité de `system_audit`)** : le check des surfaces
  couvre publique + privées, avec le verdict parental COMPLET du gate (limite,
  tags noirs/blancs, non cotés, tags de série — l'ancien check ne voyait que
  la limite) et un libellé honnête : « il peut les LIRE depuis la playlist ».
- Modèle de visibilité des playlists Emby documenté dans
  [Surfaces natives](#playlists-ai-tonight-privée-par-usager--publique-foyer-watch-bucket).

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
- `TonightGenreTagEnabled` (bool, défaut `false`) — ajoute le **tag** `AI Tonight`
  aux items Emby du **watch bucket** (modifie les métadonnées réelles ; ex-genre,
  migré en v1.13.3). Scope isolé du genre `AI Suggestion` de la bibliothèque
  `.strm`.
- `TonightCollectionEnabled` (bool, défaut `false`) — maintient une collection
  (BoxSet) `AI Tonight` des items du watch bucket. **Non destructive** (items
  référencés, jamais copiés). Indépendante du genre (les deux cohabitent).

> 📌 `StrmLibraryName` doit correspondre au **nom exact** affiché dans le dashboard
> Emby (l'UserView est slugifié avec des tirets ; un nom avec `_` peut ne pas
> matcher). En cas de « bibliothèque introuvable », recopier le nom du dashboard.

### Divers

`TmdbLanguage`, `SearXngUrl` (recherche web auto-hébergée — [SearXNG](https://docs.searxng.org/)), `WebFetchDirect`,
`NewReleaseSources` (sources de l'outil `new_releases`, une par ligne — migré depuis
l'ancienne paire `ShowbizzUrl` / `ShowbizzPattern`), `RagDirectives` (directives additionnelles injectées
dans le prompt — installée par défaut avec la directive de base localisée, réinitialisable
en un clic, voir [Aides de la page de configuration](#aides-de-la-page-de-configuration)),
`ResponseLanguage` (langue de sortie du LLM — voir ci-dessous),
`ScheduleTask` / `ScheduleTaskMovies` (cron de la tâche planifiée),
`DebugVerbose`.

### Langue de réponse du LLM

`ResponseLanguage` force la langue du **texte en prose** de l'LLM — les **raisons des
recommandations** (champ `reason` des cartes) **et** le **rapport d'audit**. Vide / `Auto`
= aucune directive (l'LLM suit la langue du prompt, ici le français — comportement par
défaut). Toute autre valeur (ex. `English`, `Español`, `Deutsch`…) injecte une directive
en fin de system prompt : l'LLM rédige alors dans cette langue. Les titres de films/séries
et les noms de chaînes ne se **traduisent jamais** : la directive demande de recopier le
`title` **exactement tel qu'il figure dans les résultats de `get_emby_info`** (même si
`tmdb_lookup` renvoie le titre dans une autre langue — un titre modifié casse le
rattachement au programme EPG). Les noms de champs JSON techniques restent inchangés.
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
- **Surfaces foyer (v1.13.13.0, verdicts complets v1.13.16.0)** — le volet
  sécurité de l'audit vérifie aussi la cohérence d'accès des surfaces du
  plugin : playlists **« AI Tonight »** (publique foyer ET privées par usager —
  items hors des bibliothèques partagées avec l'usager, ou ne passant pas son
  contrôle parental complet → ⚠️, avec le libellé « il peut les LIRE depuis
  la playlist ») et bibliothèque **.strm** (accès sans droit d'enregistrement
  → ⚠️, droit sans accès → ℹ️). L'admin décide ensuite « qui a accès à quoi »
  dans le dashboard ; le plugin ne modifie jamais les comptes.
- **Contrôle parental : collection et règles de tags (v1.13.17.0)** — suite à la
  validation empirique du BoxSet (le listing de ses membres est filtré
  **nativement** par Emby pour un compte restreint ; le container reste
  toujours visible même quand sa cote agrégée dépasse la limite — la
  collection n'est **pas** un contournement, contrairement à la playlist) :
  membres de la collection **« AI Tonight »** au-dessus de la limite d'un
  compte restreint → ℹ️ info (masqués de son listing, « accessibles par accès
  direct à l'id — comportement Emby natif ») ; `BlockedTags`/`IncludeTags`
  ne matchant **aucun** item de la bibliothèque (coquille de frappe = règle
  aveugle : règle noire sans protection, liste blanche sur-bloquante) → ⚠️ ;
  usager « Tonight » lui-même restreint → ℹ️ (l'intersection de la publique
  vaut sa propre policy). Résumé ✅ quand les règles sont opérantes.
- **Hygiène des cotes (v1.13.14.0)** — l'action `ratings_check` de l'audit compare
  les cotes (`OfficialRating`) des films/séries et de l'EPG à la table parentale
  intégrée du serveur : des cotes non reconnues rendent la limite parentale
  **aveugle** sur ces items → ⚠️ avec la liste des valeurs fautives et un conseil
  de normalisation (ex. plugin Classification Mapper). Marqueurs « non coté »
  (NR…) comptés à part ; EPG en ℹ️ info (cotes brutes du guide, jamais
  normalisées).
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

Le **dernier rapport réussi** est persisté (`audit_report.json`, voir
[Audit santé](#audit-santé)) et affiché par défaut au chargement de la page —
`GET /Plugins/LLMAI/Audit?Last=true` le renvoie sans exécuter d'audit (zéro LLM).

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

### Traduction IA des genres EPG (GenreCleaner)

Section **admin** de la page de config (bouton **Analyser** → propositions →
**Appliquer**). Aucun flag dédié : l'analyse et l'écriture sont réservées aux
administrateurs (elles consomment des tokens LLM et modifient la config d'un autre
plugin). Détail complet : [Traduction IA des genres EPG (GenreCleaner)](#traduction-ia-des-genres-epg-genrecleaner).

### Chat LLM (admin)

- `ChatEnabled` (bool, défaut `true` — opt-out) — active la page **LLM_AI Chat**
  (menu admin, section « Serveur ») et l'endpoint `POST /Plugins/LLMAI/Chat`.
  Voir [Chat LLM (admin)](#chat-llm-admin).
- `ChatMemoryEnabled` (bool, défaut `false` — opt-in) — mémoire de conversation du
  chat : sessions persistées, résumé paresseux, bouton « Reprendre ». Voir
  [Mémoire de conversation](#mémoire-de-conversation).
- `ChatActionBudget` (int, défaut `10`) — budget d'actions du chat **par tour**
  (toutes surfaces confondues : cartes, timers, tags, collection, playlist, run).
  `0` = chat en lecture seule. `ChatActionConversationCap` (int, défaut `30`) —
  borne cumulative par conversation. `ChatTonightRunEnabled` (bool, défaut
  `false` — opt-in) — autorise le tool `run_tonight_run`. Voir
  [Couche d'action du chat](#couche-daction-du-chat).
- `ChatPromptsEnabled` (bool, défaut `false` — opt-in) — autorise le tool de chat
  `plugin_prompts` (lecture + proposition d'écriture des cinq prompts de la
  configuration). L'écriture est **two-phase** : proposition sérialisée puis application
  au clic « Approuver » de l'admin sur la carte de diff. Les contextes d'édition (modes
  déroulants) restent en lecture sans ce flag. Voir
  [Édition des prompts par le chat](#édition-des-prompts-par-le-chat).

### Mémoire réflexive (expérimental)

Trois flags opt-in (voir [Mémoire réflexive](#mémoire-réflexive)) :

- `DecisionLogEnabled` (bool, défaut `false`) — journalise chaque reco émise
  avec sa **raison** LLM et le **menu de candidats** du run (`decisions.json` +
  `run_pool.json`), et fige les métadonnées EPG éphémères (`epg_snapshot.json`).
  Double écriture du journal RecoLog de la boucle classique (qui le lit encore).
- `PlaybackTelemetryEnabled` (bool, défaut `false`) — journalise chaque lecture
  terminée avec le pourcentage visionné (`playback.json`) : rejet immédiat
  (< 5 %), abandon (5–50 %), contenu validé (> 80 %).
- `MemoryCardEnabled` (bool, défaut `false`) — tâche hebdomadaire (dimanche
  4 h 30) : le LLM réécrit sa **fiche mémoire** (~250 mots, `memory_card.json`,
  versionnée, 4 versions conservées) ; quand active, la fiche REMPLACE la
  directive de la boucle de rétroaction dans les prompts. Édition admin possible
  sur la page de configuration.

---

## Composants

| Fichier | Classe | Rôle |
|---|---|---|
| `Plugin.cs` | `Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage` | Point d'entrée. Nom `LLM_AI`, Id `e7d3…2e60`. Enregistre les pages web (config, recommandations, i18n). Version pilotée par `<AssemblyVersion>` du `.csproj`. |
| `PluginConfiguration.cs` | `PluginConfiguration` (+ `LlmBackend`, `LlmProvider`) | Toute la config persistée + backends multi-source. |
| `LlmScheduledTask.cs` | `LlmScheduledTask : IScheduledTask, IConfigurableScheduledTask` | Tâche planifiée globale (admin) : produit les recos **Séries / Films** en parcourant l'EPG, applique le garde-fou « déjà possédé » (`EnrichWithLibrary` sur le payload fusionné), stocke dans `Recommendations`, envoie les notifications. Délègue l'orchestration à `LlmRunner`. |
| `TonightApiService.cs` | `TonightApiService : BaseApiService` | Endpoint HTTP **par usager à la demande** `GET /Plugins/LLMAI/Tonight`. Couche HTTP fine : résout l’usager puis délègue à `TonightService`. |
| `TonightService.cs` | `TonightService` (interne) | **Génération partagée** « À regarder ce soir » : profil de goût, enregistrements non visionnés, réserve bibliothèque, séries « prêtes à dévorer » (opt-in, gate anti-spam `BingeNotified`), run LLM, enrichissement, watched-guard (marque `watched=true` les rediffusions déjà visionnées — index per-usager `BuildWatchedIndex`), **cache par usager** (statique, partagé endpoint + login). Utilisé par `TonightApiService`, `TonightLoginService` et le tool chat `run_tonight_run` (directives de session éphémères + origin chat, v1.13). |
| `AutoProgrammer.cs` | `AutoProgrammer` (interne) | Auto-programmation : crée les timers Emby (SeriesTimer / Timer unique) du **record bucket** — recos à enregistrer non possédées/non déjà programmées/hors drop list. Portage serveur de la logique « Programmer » de `recommendations.js`. `ProgramOneAsync(Reco, …)` (retour `OneOutcome`) partagé avec l'endpoint Activate. |
| `StrmLibraryGenerator.cs` | `StrmLibraryGenerator` (interne) | Bibliothèque `.strm` : écrit une carte `.strm`+`.nfo`+poster par reco du record bucket, nettoyage `.llmai_reco`, téléchargement poster TMDB (retry sans suffixe « on <chaîne> » si le titre complet n'a pas de match). Repli poster : **copie l'affiche Primary du programme EPG** si elle est un fichier local (cache disque Emby) ; une URL distante est demandée à **l'endpoint image d'Emby** (l'hôte distant de l'affiche n'est jamais contacté) — poster par défaut embarqué posé à la place. Raison loguée à chaque garde. Le `<plot>` du `.nfo` commence par le **synopsis EPG natif** (langue d'origine) puis l'enrichissement dans la langue de l'usager ; ajoute les **External IDs** `<tmdbid>`/`<imdbid>`/`<tvdbid>` quand disponibles (liens profonds TMDB/IMDb/TVDB). |
| `ActivateApiService.cs` | `ActivateApiService : BaseApiService` | Endpoint `GET /Plugins/LLMAI/Activate` (DTO `[Unauthenticated]`) : programme une reco unique, notifie par toast + fait supprimer la carte par Emby en cas de succès (v1.12), puis stream `recording_activated.mp4`. Gated par `StrmSecret` + **gate de permission asynchrone** (v1.13.11.0 : lecteur identifié via sa session sans `EnableLiveTvManagement` → timers de cette activation annulés + toast dédié). |
| `ActivateFeedback.cs` | `ActivateFeedback` (statique) | Retour visuel des cartes .strm (v1.12) : toast Emby à la session qui lit la carte (session retrouvée par chemin .strm, `DisplayMessage`), suppression de la carte PAR EMBY (`FindByPath` → `DeleteItem`, différée ~60 s, succès seulement), cache anti-doublon (TTL 5 min) pour les GET multiples d'une même lecture. |
| `PermissionGate.cs` | `PermissionGate` (statique) | Gate de droits usager (v1.13.11.0) : résolution de l'usager (token de la requête ou session de lecture) et lecture de sa policy Emby **à chaud** (jamais en cache, pas de comptes propres au plugin). `CanRecordLive` = `EnableLiveTvManagement` ; `CanWatchLive` = `EnableLiveTvAccess` (v1.13.12.0). Filtrage des candidats bibliothèque par accès médiathèque (`FilterAccessible`, v1.13.12.0) et par contrôle parental (`FilterParental`/`IsParentallyAllowed`/`IsEpgAllowed` — limite `MaxParentalRating`, `BlockUnratedItems`, tags noirs/blancs, v1.13.15.0). Annulation ciblée des timers créés par une activation (`CancelCreatedTimers` — seulement les ids absents de la capture préexistante). |
| `AiTagger.cs` | `AiTagger` (statique) | Étiquetage **tags** `AI Tonight` / `AI Delete` : `AddAsync` / `RemoveAllAsync` via `UpdateToRepository` (retire aussi le genre hérité du même nom — migration v1.13.3). |
| `AiTonightCollectionManager.cs` | `AiTonightCollectionManager` (statique) | Collection `AI Tonight` : `EnsureAsync` (find-or-create BoxSet, reconcile) + `ClearAsync` via `ICollectionManager`. |
| `AiTonightCleanupTask.cs` | `AiTonightCleanupTask : IScheduledTask` | Nettoyage quotidien 03:00 : retire le tag `AI Tonight` (+ genre hérité, migration) + vide la collection (toujours actif). Porte aussi la sonde disque quotidienne (passe tag « AI Delete », opt-in). |
| `RecordingDiskManager.cs` | `RecordingDiskManager` (statique) | Seuil disque du dossier d'enregistrements : résolution chemin/volume, gate « sous le seuil » (fail-open), passe d'étiquetage « AI Delete » (clear-first, visionnés uniquement, plus ancien d'abord, objectif ×1.2) + notification. |
| `RecoAnalysisTask.cs` | `RecoAnalysisTask : IScheduledTask` | Analyse hebdo (dimanche 04:00, opt-in `RecoFeedbackEnabled`) de la boucle de rétroaction : rapproche le journal des recos/rejets des visionnages réels (C# + `IUserDataManager`), fait produire au LLM (`RunSynthesisAsync`, sans outils) une directive par usager persistée dans `PromptDirectives`. Voir [Boucle de rétroaction](#boucle-de-rétroaction-des-recommandations). |
| `RecoFeedback.cs` | `RecoFeedback` / `RecoLogEntry` / `RecoDirective` (internes) | Helpers de la boucle de rétroaction : journal roulant `RecoLog` (parse/persist/prune), directives `PromptDirectives` (parse/persist/troncature), blocs de prompt réinjectés (par usager pour Tonight, fusionnés pour la tâche d'enregistrement). |
| `DecisionStore.cs` | `DecisionStore` / `DecisionEntry` / `RunPool` / `PlaybackEntry` (statique interne) | Stores de la mémoire réflexive : `decisions.json` (une reco émise par entrée, avec sa raison LLM et la version de fiche en vigueur `mv`), `run_pool.json` (le « menu » de candidats soumis à chaque run — distingue une mauvaise reco d'une erreur de classement), `playback.json` (télémétrie `PlaybackWatcher`). Contexte de run statique (`BeginRun`/`EndRun`/`ActiveRunId`) reliant le tool global `get_emby_info` au run en cours ; lectures `ParseAllDecisions/Pools/Playback`. Rétention 30 j, plafonné, fail-open. |
| `PlaybackWatcher.cs` | `PlaybackWatcher : IServerEntryPoint` | Observateur de télémétrie : branche `ISessionManager.PlaybackStopped`, écrit une entrée `playback.json` par session terminée (item, usager, durée réelle, fraction lue, source bibliothèque/.strm/direct, chaîne, client, appareil). Opt-in `PlaybackTelemetryEnabled` vérifié à chaque événement ; le % du direct est dérivé à l'analyse via `EpgSnapshotStore`. Best-effort, n'affecte jamais la lecture. |
| `EpgSnapshotStore.cs` | `EpgSnapshotStore` / `EpgSnapshotEntry` (statique interne) | Snapshot EPG (`epg_snapshot.json`, rétention 90 j) : fige les métadonnées éphémères dès l'émission au LLM (titre, synopsis ≤ 300, chaîne, **durée de diffusion** = dénominateur du % du direct, genres normalisés GenreCleanerMap, année, flags série/film) et à la création de timer (`AutoProgrammer` → `MarkTimer`). Un programme diffusé disparaît d'Emby : sans snapshot, tout ce qu'on en savait est perdu. |
| `MemoryCard.cs` | `MemoryCard` / `MemoryCardData` (statique interne) | Fiche mémoire réflexive (`memory_card.json`) : version courante + **historique immuable des 4 versions précédentes** (contrepoids anti-dérive), plafond ~250 mots, fail-open (échec LLM → fiche précédente). `BuildInjectionBlock` : le bloc « MÉMOIRE DE L'ASSISTANT » réinjecté dans les prompts quand `MemoryCardEnabled` — **remplace** la directive de la boucle classique (repli transparent sinon). |
| `MemoryTask.cs` | `MemoryTask : IScheduledTask` | Révision hebdomadaire (dimanche 4 h 30, opt-in `MemoryCardEnabled`) : jointure **100 % C#** des événements de la semaine (décisions × télémétrie avec % du direct via snapshot × **calibration des versions de fiche** `mv` × candidats écartés des pools × vu-sans-recommandation × créneaux de lecture), puis **un appel LLM sans outils** réécrit la fiche (reprise de l'actuelle, sections imposées, nuance signal faible/fort, ≤ 250 mots). Voir [Mémoire réflexive](#mémoire-réflexive). |
| `ChatMemoryStore.cs` | `ChatMemoryStore` / `ChatMemorySession` (statique interne) | Mémoire de conversation du chat (`chat_memory.json`, par usager, 5 sessions / 30 j) : tours verbatim (compressés aux 6 derniers après résumé), résumé de session (≤ 1500 car.). `BuildInjectionBlock` : résumé de la session précédente + derniers échanges, accolé au workflow de chat (jetable — le résumé suivant le remplace). Opt-in `ChatMemoryEnabled`. Voir [Mémoire de conversation](#mémoire-de-conversation). |
| `ChatActions.cs` | `ChatActions` (statique interne) | **Couche d'action du chat** (v1.13) : 8 tools (`record_program`, `create_card`, `tag_ai_tonight`, `collection_add`/`_remove`, `playlist_add`/`_remove`, `run_tonight_run` opt-in) réutilisant les primitives du plugin ; budget par tour + par conversation (consommation au succès, lots all-or-nothing), gate « un run chat à la fois », trace des items ajoutés (seuls retirables), bloc de workflow (budget + étiquette de confirmation), trace visuelle des actions réussies (toast Emby + libellé `TurnActions` renvoyé à la page — v1.13.1/v1.13.4). Voir [Couche d'action du chat](#couche-daction-du-chat). |
| `ChatContexts.cs` | `ChatContexts` / `ChatContextDef` (statique interne) | **Contextes d'édition du chat** (v1.13.8, portage du pattern « contextes » de llm_core) : cinq modes déroulants (un par prompt éditable). `BuildBlock` injecte à CHAQUE tour : guide d'édition (rôle, invariants, conventions), TEXTE COURANT du prompt (source de vérité read-modify-write, relu de la config) et langue cible résolue serveur. Porte aussi les `CommonRules` (canal de livraison ```text, read-modify-write, conventions de rédaction, langues, sauvegarde, mode exclusif) appendées en fin de bloc. La liste servie à la page et la validation `context_id` dérivent du registre `All` (une entrée = un mode). Voir [Édition des prompts par le chat](#édition-des-prompts-par-le-chat). |
| `ChatPromptStore.cs` | `ChatPromptStore` / `ChatPendingAction` (statique interne) | Store des propositions de modification en attente (`chat_pending.json`, expiration 10 min, une par conversation, par usager). `TakePagePending` (relève la carte de diff pour le tour), `PeekPagePending` (consulte sans consommer — filet nudge), `Consume` (approbation : retire l'action si elle existe, n'a pas expiré, appartient à cet usager ET à cette session). |
| `ChatPromptsTool.cs` | `ChatPromptsTool : ILlmTool` | Tool de chat `plugin_prompts` (v1.13.8, opt-in `ChatPromptsEnabled`) : `list`/`get` (lecture des cinq champs) et `set` — **two-phase** : valide (liste blanche, plafond 8000 car., texte non vide, champ = mode actif) puis sérialise la proposition dans `ChatPromptStore` ; l'écriture n'a lieu qu'au clic « Approuver » (endpoint `POST /Plugins/LLMAI/ChatPrompt/Approve`, C# déterministe) — le LLM n'a AUCUN chemin d'écriture direct. Avertissement de divergence (recouvrement lexical < 25 %) porté par la carte de diff. Voir [Édition des prompts par le chat](#édition-des-prompts-par-le-chat). |
| `OrphanIdentifyTask.cs` | `OrphanIdentifyTask : IScheduledTask` | Identification quotidienne 04:00 des items bibliothèque orphelins (sans id IMDb/TMDB/TVDB — enregistrements DVR terminés importés en bibliothèque) : découverte via `ILibraryManager.GetItemList` (Movie/Series) → S1 (nettoyage titre + recherche TMDB multilingue) → S2 (LLM propose un id validé via TMDB `/find`) → S3 (recherche web SearXNG → id IMDb, même porte d'acceptation), écrit ids+Overview+Genres+poster si vides, **verrouille `Name`**, tags `llmai-identified`/`llmai-needs-review`, retry needs-review, dry-run. Voir [Identification des orphelins](#identification-des-enregistrements-orphelins). |
| `DefaultImageApplier.cs` | `DefaultImageApplier` (statique) | Pose un poster par défaut standardisé (`default_poster.jpg`, ressource embedded) sur la collection `AI Tonight` (BoxSet) et la racine de la bibliothèque `.strm` (CollectionFolder). Idempotent (seulement si pas d'image `Primary`). |
| `AiBadgeEnhancer.cs` | `AiBadgeEnhancer : IImageEnhancer` | Badges **au moment du service** sur les images EPG (overlay — l'artwork stocké n'est jamais modifié) : puce **verte + étincelle** pour les suggestions IA du record bucket, puce **jaune sans icône** pour le **déjà possédé** — film par nom, épisode de série **au niveau de l'épisode** (n° saison/épisode, puis titre d'épisode ; posséder la série ne badge pas toutes ses diffusions, repli conservateur au niveau série quand l'EPG n'a pas de numérotation). Matching `Norm` réutilisé, index noms + clés d'épisodes biblio (cache 10 min). Dessin SkiaSharp (livré avec Emby), **clé de cache par état ET par item** (les épisodes partagent la pochette du guide de leur série — le badge d'un épisode ne doit pas fuiter sur les autres), repli copie de l'original, ne lève jamais. Auto-découvert par le scan d'assembly. |
| `AiBadgeRegistry.cs` | `AiBadgeRegistry` (statique) | Registre des programmes suggérés par la tâche nocturne : remplacé à chaque run (`ApplyRecos`, filtres record bucket), persisté `AiBadgeProgramIds`, rechargement paresseux au 1er `Supports` (le constructeur du plugin ne touche jamais `Configuration` — `AssemblyFilePath` n'est posé qu'après construction). |
| `I18n.cs` | `I18n` (statique) | i18n côté serveur (C#) : dictionnaires inline FR/EN + résolution de langue (`ResolveMetaLangKey` métadonnées / `ResolveDisplayLangKey` interface) + `ToTmdbLang`/`ToLangName`. Localise les tâches planifiées. |
| `TonightLoginService.cs` | `TonightLoginService : IServerEntryPoint` | Déclencheur de login : branche `ISessionManager.SessionStarted`, lance `TonightService` (cache-aware), auto-programme (si `AutoProgram`), envoie un **toast** (`SendMessageCommand`, gated `DisplayMessage`) + **cloche** persistante (deep-link). Pattern `Emby.ComSkipper`. |
| `AuditApiService.cs` | `AuditApiService : BaseApiService` | Endpoint HTTP **à la demande admin** `GET /Plugins/LLMAI/Audit` : résout l'admin appelant, construit le prompt d'audit (template `AuditPrompt` + `Focus` optionnel) puis délègue le run agent à `LlmRunner.RunAuditAsync`. Retourne le rapport Markdown brut ; persiste chaque rapport réussi (`AuditReportStore`) et sert `?Last=true` (lecture seule du dernier rapport, zéro LLM). |
| `AuditReportStore.cs` | `AuditReportStore` / `LastAuditReport` (statique interne) | Persistance du **dernier rapport d'audit** (`audit_report.json`, dossier de configuration du plugin, convention `ChatMemoryStore`) : date, mode, focus, rapport Markdown. Best-effort fail-open ; un seul enregistrement écrasé à chaque run réussi. |
| `ChatApiService.cs` | `ChatApiService : BaseApiService` | Endpoint HTTP **chat interactif admin** `POST /Plugins/LLMAI/Chat` : corps `{Message, History:[{role,content}], Session}` (la page garde l'historique ; `Session` = identifiant de mémoire de conversation), filtre les rôles user/assistant, délègue le tour à `LlmRunner.RunChatAsync` (tous les outils existants, priorités LLM usager, bloc mémoire accolé). Le system prompt (doc outils + directives) est construit serveur-side, une fois par conversation. Porte aussi la **mémoire de conversation** : résolution de session, journalisation des tours (`ChatMemoryStore`), condensation paresseuse des sessions passées (un appel LLM en tâche de fond, note de continuité + ligne `SIGNALS:` → décisions `kind="chat"`), et les endpoints `GET /Plugins/LLMAI/ChatMemory` / `POST /Plugins/LLMAI/ChatMemory/Forget`. |
| `ConfigApiService.cs` | `ConfigApiService : BaseApiService` | Endpoints utilitaires **admin** de la page de config : `POST /Plugins/LLMAI/TestLlm` (test d'un backend **tel qu'édité** — provider/url/modèle postés, clés API relues côté serveur depuis la config, réponse OK/échec + latence + extrait, timeout 30 s), `GET /Plugins/LLMAI/DefaultPrompts` (les cinq prompts par défaut dans la langue résolue : `?Lang=` → `ResponseLanguage` → langue d'affichage Emby — volontairement PAS la cascade métadonnées/TmdbLanguage) et `GET`/`POST /Plugins/LLMAI/MemoryCard` (consultation / édition admin de la fiche mémoire — version et historique inchangés). Voir [Aides de la page de configuration](#aides-de-la-page-de-configuration). |
| `DefaultPrompts.cs` | `DefaultPrompts` (statique interne) | **Source unique** des cinq prompts/directives par défaut (FR + EN) : baseline `RagDirectives` (outils avant d'affirmer, jamais un titre possédé/programmé, préférence légère productions récentes sans pénaliser l'année absente), `ScheduleTask`, `ScheduleTaskMovies`, `TonightPrompt`, `AuditPrompt`. Sert à la fois d'initialiseurs de `PluginConfiguration` (nouvelles installations) et de contenu du bouton « Réinitialiser ». |
| `GenreApiService.cs` | `GenreApiService : BaseApiService` | Endpoints **traduction IA des genres** (admin) : `GET /Plugins/LLMAI/GenreProposals` (collecte les genres EPG des programmes **à venir** non couverts par GenreCleaner, par section films/séries, plafonnés à 60/section, puis un appel LLM one-shot via `ChatWithFallbackAsync` propose pour chacun une cible du vocabulaire curaté, un nouveau genre, ou rien) et `POST /Plugins/LLMAI/GenreApply` (re-valide puis écrit dans `GenreCleaner.xml` via `GenreCleanerMap`, enregistre dans `GenreAliasApplied`, déclenche `NotifyPendingRestart`). Langue des suggestions = cascade `ResolveMetaLangKey` (`ResponseLanguage`). Voir [Traduction IA des genres](#traduction-ia-des-genres-epg-genrecleaner). |
| `GenreCleanerMap.cs` | `GenreCleanerMap` (statique interne) | **Pont GenreCleaner.xml** : lecture (`Allowed`/`IsMapped`/`IsCovered` — un genre est couvert s'il est mappé OU présent tel quel dans AllowedGenres), écriture idempotente (`AddMappings` — dedup par clé normalisée, ajout AllowedGenres pour les entrées `new`, rejet des mappages identité `Action→Action` sauf nouveaux genres) et **auto-guérison** (`HealApplied` : ré-écrit dans le XML les mappages enregistrés dans `GenreAliasApplied` qui manqueraient — `new:true` restaure aussi l'entrée AllowedGenres). |
| `ClassificationMap.cs` | `ClassificationMap` (statique interne) | **Pont Classification Mapper** (lecture seule) : lecteur paresseux de `classification_mapper_config.json` (dossier de configuration du **serveur**, pas des plugins ; re-stat mtime throttlé 30 s — les mappings édités dans l'UI de Classification Mapper sont suivis sans redémarrage) ; normalise les classifications officielles hétérogènes (« PG-13 », « TV-14 », « 13+ »…) vers les valeurs canoniques de l'UI (« CA-G », « CA-14A »…). Neutre si le plugin est absent (passthrough en casse). Utilisé par l'action `find` de `get_emby_info`. Voir [Classifications officielles](#classifications-officielles-classification-mapper). |
| `RecosApiService.cs` | `RecosApiService : BaseApiService` | Endpoints **usager** de la page Recommandations : `GET /Plugins/LLMAI/Recos` (dernières recommandations de la tâche planifiée + date, tout usager authentifié — la page ne lit plus la config plugin via l'endpoint hôte admin `/Configuration`, qui renvoyait 403 aux non-admin) et `POST /Plugins/LLMAI/Forget {Title}` (bouton **Oublier** : ajoute à `DroppedTitles` serveur-side via `SaveConfiguration`). Répond en plus `CanRecord`/`CanLiveTv` (v1.13.12.0 : droits de l'appelant, policy lue à chaud). Ne sert **que** ces champs — jamais la config complète (clés API, prompts). |
| `UpdateApiService.cs` | `UpdateApiService : BaseApiService` | Endpoint `GET /Plugins/LLMAI/Update` : compare le tag de la dernière release GitHub (`releases/latest`, workflow `release.yml`) à la version d'assembly installée → bannière de mise à jour sur la page de config. Lecture seule (aucun téléchargement), cache 1 h sous verrou (limite API GitHub), `Force=1` pour bypasser, ne lève jamais (`Error` → pas de bannière). |
| `SystemAuditTool.cs` | `SystemAuditTool : ILlmTool` | Outil `system_audit` (voir [Outils](#outils-llm)) — 12 actions d'audit système (sessions, tâches, transcodage, disques, journaux, métriques hôte, processus, bibliothèque) + 3 actions de remédiation gated par `AuditRemediationEnabled`. Confinement FS des journaux (nom seul + whitelist extension + containment canonique). |
| `LlmRunner.cs` | `LlmRunner` (classe interne) | **Orchestration partagée** : `ResolveBackends`, `RunAsync` (boucle d'agent + tool-calling), `EnrichRecommendations` (match titre → id/chaîne/poster/note), `EnrichWithLibrary` (rapprochement bibliothèque : titre exact/flou, **repli par id IMDb** via `AnyProviderIdEquals` — reco possédée → `library_id`, exclue du record bucket), `FindLibraryItem`, `MergeJsonArrays`, `ExtractJsonPayload`, `NormTitle` (pliage d'accents partagé `FoldAscii` : « leçons » ≡ « lecons »), résolution des clés via env. Path d'audit dédié : `BuildAuditTools`, `RunAuditAsync` (boucle agent ou mode déterministe), `ChatWithFallbackAsync` (synthèse sans outils). Filet de formatage `SanitizeReport` (flèches LaTeX → « → », balises HTML dénudées) appliqué aux sorties audit et chat. Path chat : `RunChatAsync` (multi-tours, tous les outils existants, priorités LLM usager). Appels one-shot : `TranslateTextAsync` (tier-3 cascade TMDB), `ResolveIdsAsync` (proposition d'ids pour la tâche orphelins — toujours validée par TMDB). Utilisé par `LlmScheduledTask`, `TonightApiService`, `AuditApiService`, `ChatApiService` **et** `OrphanIdentifyTask`. |
| `ItemIdResolver.cs` | `ItemIdResolver` (statique interne) | Résolution bilingue des ids Emby : longs (InternalId — forme canonique du plugin, la seule que la couche REST/UI accepte) **et** Guids historiques (input legacy seulement, jamais émis). Corriger la devise d'ids qui faisait échouer toutes les validations Tonight. |
| `LlmAgentService.cs` | `LlmAgentService` | Boucle d'agent : envoie le prompt au LLM, exécute les tool-calls, reboucle jusqu'à la réponse finale. Deux paramètres optionnels (`roleIntro`, `formatSection`) permettent de surcharger l'intro du rôle et le bloc de format de sortie pour les paths audit et chat (sans toucher aux appelants recommandation). `RunChatAsync` : entrée multi-tours qui rejoue l'historique (user/assistant, borné) entre le system prompt et le nouveau message — même boucle partagée (`RunLoopAsync`). |
| `LlmClient.cs` | `LlmClient` (statique) | Appels HTTP bruts vers Ollama / Gemini (sans clé en clair dans les journaux). |
| `GetEmbyInfoTool.cs` | `GetEmbyInfoTool` | Outil `get_emby_info` (voir [Outils](#outils-llm)). Expose en outre le pliage d'accents partagé `FoldAscii` (FormD + marques combinantes + map manuel œ/æ/ø/đ/ł/ß/ð/þ → « leçons » ≡ « lecons »), utilisé par `Norm` (exclusion biblio `epg_series`/`epg_movies`, drop list) et `LlmRunner.NormTitle`. |
| `TmdbLookupTool.cs` / `TvdbSearchTool.cs` / `WebSearchTool.cs` / `WebFetchTool.cs` / `NewReleasesTool.cs` | … | Outils LLM spécialisés (voir [Outils](#outils-llm)). `TmdbLookupTool` expose en outre `LookupMetaAsync`/`LookupMetaMultiLangAsync` (recherche, S1), `FindByExternalIdAsync` (`/find`, valide un id proposé), `LookupMetaByIdAsync` (détail par id), `CleanEpgTitle` — réutilisés par `StrmLibraryGenerator` et `OrphanIdentifyTask`. |
| `config.html` / `config.js` | — | Page de configuration (saisie des champs ci-dessus). |
| `recommendations.html` / `recommendations.js` | — | Page Recommandations (rendu des 3 sections, cartes, boutons). |
| `chat.html` / `chat.js` | — | Page « Chat LLM AI » (menu admin, section Serveur) : conversation plein cadre avec l'agent LLM — logique multi-tours portée de la config vers sa propre page, historique par visite, rendu Markdown partagé, bannière « Reprendre » (mémoire de conversation, opt-in `ChatMemoryEnabled`). Contextes d'édition (v1.13.8) : liste déroulante des modes, annonce `[Admin]` automatique au changement, rendu des blocs clôturés en conteneur avec boutons Copier / Sauvegarder (message autoporteur), bouton de secours au niveau du tour si la réponse arrive en prose. Liens profonds (v1.13.9.12) : rendu des liens Markdown `[texte](url)` vers les fiches Emby, même origine uniquement, nouvel onglet — voir [Liens profonds vers les fiches Emby](#liens-profonds-vers-les-fiches-emby-v113912). |
| `i18n.js` | — | Chaînes localisées FR/EN + endpoint `web/ConfigurationPage?name=LLMAII18n`. |
| `deploy.sh` | — | Build + déploiement + redémarrage (voir [Installation](#installation)). |

### Flux de données

**Tâche planifiée (Séries/Films) :**
1. Cron `ScheduleTask`/`ScheduleTaskMovies` → `LlmScheduledTask.Execute`.
2. `LlmRunner.ResolveBackends` choisit le backend primaire.
3. Prompt + outils → `LlmAgentService` boucle d'agent (le LLM appelle `get_emby_info`
   `epg_series`/`epg_movies`, `tmdb_lookup`, `web_search`/`web_fetch`, `new_releases…`).
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
| `get_emby_info` | **Interrogation Emby** — actions : `summary` (résumé bibliothèque), `library` (items), `global_search`, `item_details`, `item_persons`, `person`, `epg_series` (EPG séries à venir), `epg_movies` (EPG films à venir), `epg_tonight` (EPG dans la fenêtre « ce soir », `HasAired=false`, marque `is_scheduled`, déduplication par titre « meilleure diffusion » — l'inédit l'emporte sur la rediffusion), `scheduled` / `planning` (timers programmés), `find` (recherche unifiée bibliothèque + EPG — terme libre, types, personne, genres, **classification** normalisée, vus/favoris par usager, `source` `library|epg|both` avec dédup titre — la bibliothèque gagne). Applique whitelists, flags, drop list. |
| `tmdb_lookup` | Recherche / détails TMDB (note, poster, résumé, casting) via `TmdbApiKey`. |
| `tvdb_search` | Recherche TVDB (séries) via `TvdbApiKey`. |
| `web_search` | Recherche web ([SearXNG](https://docs.searxng.org/) `SearXngUrl` ou fournisseur intégré). |
| `web_fetch` | Récupération/lecture d'une page web (`WebFetchDirect` pour lecture brute). |
| `new_releases` | Nouveautés TV depuis les sources web de `NewReleaseSources` (une par ligne) : URL seule = flux RSS/Atom auto-détecté ; `URL :: @showbizz` = extracteur Showbizz.net intégré (blocs « Saison 1 ») ; `URL :: regex .NET` = extraction personnalisée (groupe `title` requis, `url`/`date` optionnels). Alias `showbizz_new_releases` (prompts existants). Cache 24h invalidé par tout changement de sources (sans redémarrage). |
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

**Séries « prêtes à dévorer » (opt-in)** — `TonightBingeEnabled` : l'usager qui enregistre
une série et attend d'avoir plusieurs épisodes avant de commencer reçoit un signal
opportuniste quand le stock est suffisant. Détection **déterministe C#** (`BuildBingeReadySeries`,
pas de tool LLM) : épisodes non visionnés agrégés par série ; une série qualifie quand
(1) son stock atteint `TonightBingeThreshold` (défaut 4) **et** (2) au moins un épisode est
arrivé récemment (`TonightBingeActiveDays`, défaut 14) — le signal « enregistrement actif »
qui distingue une accumulation en cours d'une série **dormante** jamais commencée (une
série conservée « pour un jour de pluie » n'est **jamais** signalée). La série franchissant
le seuil est injectée dans le prompt (AU PLUS UNE recommandation par run,
`source="recording"` si elle figure aussi dans les enregistrements non visionnés, sinon
`source="library"` ; id du **premier épisode à regarder**, ordre saison/épisode) avec
mention du nombre d'épisodes en attente dans la raison.

> 📌 **Anti-spam** : le gate persistant `BingeNotified` (par usager et par série) ne
> signale chaque série qu'**une seule fois par cycle d'accumulation**. La suggestion se
> ré-arme quand le compte non visionné repasse sous le seuil — c'est-à-dire quand
> l'usager commence à regarder ; le cycle suivant d'accumulation re-déclenchera la
> suggestion. La bibliothèque `.strm` est exclue (garde anti-circulaire) et la détection
> est fail-open (une erreur ne casse jamais le run).

**Watched-guard (rediffusions)** — Une rediffusion EPG d'un épisode/ film que l'usager a
**déjà visionné** n'est plus recommandée comme du contenu neuf. Deux garde-fous
déterministes C# :

- **Déduplication « meilleure diffusion »** dans `epg_tonight` : pour chaque titre, la
  diffusion au contenu le plus récent gagne (n° saison/épisode le plus haut, repli sur
  l'heure la plus tardive) — la rediffusion de 19 h ne masque plus l'épisode inédit de
  21 h, seul visible du LLM.
- **Marquage `watched=true`** (`BuildWatchedIndex` + `ValidateAndFilter`) : index
  per-usager des épisodes joués (clés « s{S}e{E} », repli nom d'épisode) et des films
  joués ; toute reco live correspondante est **marquée, pas droppée** (le minimum de
  recos reste garanti) : badge « Déjà visionné », actions Programmer / Regarder en
  direct masquées, **aucun timer créé**, exclusion des popups et de la cloche.
  Fail-open (index indisponible → recos non marquées, jamais vidées) ; bibliothèque
  `.strm` exclue de l'index (anti-circulaire).

**Champ `source`** de chaque recommandation (drive les boutons de la carte) :

| `source` | Sens | Boutons |
|---|---|---|
| `live` | Programme EPG de ce soir | Programmer · Regarder en direct (si déjà commencé) · Regarder (bibli.) si possédé · Oublier — actions masquées si déjà diffusé (`aired`) ou déjà visionné (`watched`) |
| `recording` | Enregistrement récent non visionné | Regarder · Oublier |
| `library` | Réserve bibliothèque (fallback) | Regarder · Oublier |

**Mode compact (local)** — Si le backend primaire est `OllamaLocal`, `TonightApiService`
passe en **mode compact** : les plafonds d'items injectés (profil, enregistrements,
réserve) et la troncature des résumés EPG sont réduits, pour éviter de surcharger un
modèle local (souvent plus lent / contexte limité). Les backends cloud reçoivent le
contexte complet.

**Cache par usager** — `Dictionary<userId, CacheEntry>` + verrou, TTL
`TonightCacheHours`. `Refresh=1` force un nouveau run (bouton **Rafraîchir**).

## Boucle de rétroaction des recommandations

**Opt-in** (`RecoFeedbackEnabled`, défaut off) — le plugin apprend de ses
recommandations passées : une fois par semaine, il analyse l'écart entre *ce
qu'il a recommandé* et *ce que l'usager a réellement regardé*, en tire une
directive, et l'injecte dans les prompts des runs suivants.

**1. Journalisation** — chaque reco (« À regarder ce soir » : par usager ;
tâche planifiée d'enregistrement : globale au foyer) et chaque rejet explicite
(bouton **Oublier**, le signal négatif le plus fort) est ajouté au journal
`RecoLog` (fenêtre roulante 30 jours, plafond 500 entrées).

**2. Analyse hebdo** (`RecoAnalysisTask`, dimanche 4 h) — pour chaque usager
ayant du signal, un tableau de corrélation est construit **en C# déterministe**
(zéro LLM pour le rassemblement) : recos **REGARDÉES** (l'item/la série a été
joué après la reco — date exacte via `IUserDataManager`) vs **IGNORÉES**,
rejets explicites, et **visionnages SANS recommandation** (opportunités
manquées, avec genres). Puis un **seul appel LLM sans outils**
(`LlmRunner.RunSynthesisAsync`, repli multi-backend) produit une **directive
concise** (≤ 1200 caractères, puces actionnables, repart de la directive
précédente, contrainte explicite de **préserver la diversité**).

**3. Réinjection** — la directive persistée (`PromptDirectives`) est injectée
dans le prompt : **par usager** pour « À regarder ce soir », **fusionnée
(étiquetée par usager)** pour la tâche planifiée d'enregistrement.

**Garde-fous** : directives **visibles et éditables** (JSON) dans la page de
config — les vider les retire des prompts ; fail-open total (sans directive,
prompts inchangés ; échec d'analyse → directive précédente conservée ; usager
sans signal sauté) ; bibliothèque `.strm` exclue de l'historique analysé.

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
`SupportedCommands`) est une **séquence de popups** — une par suggestion « À
regarder ce soir » (enregistrements non visionnés, bibliothèque **et**
programmes EPG live, plafond 5), au format « 🤖 À regarder ce soir (i/n) —
Titre (chaîne · heure · type) ». Comportements constatés (tests 2026-09-02) :

- Le champ `Header` de `MessageCommand` **n’est pas rendu** par les clients
  web / Android — tout (🤖, libellé, compteur) vit donc dans le `Text`.
- Le client **web** ignore `TimeoutMs` : son toast se fond sur une animation
  CSS fixe (~3 s). La séquence envoie donc le popup suivant à **4 s** sur le
  web (pas de temps mort), et à `LoginPopupSeconds` (défaut 8 s) ailleurs.
- L’app **Android TV** honore `TimeoutMs` : chaque popup y vit la durée
  configurée.

Une **notification Emby** (`INotificationManager`) accompagne toujours la
séquence, avec une description **multi-lignes** (une suggestion par ligne +
raison 🤖, retours à la ligne préservés dans le courriel). ⚠️ Les clients
standard Emby n’ont **pas de boîte de réception intégrée** : la notification
n’est visible que si elle est **activée dans le plugin de notifications**.

**Recevoir les notifications (ex. courriel SMTP)** :

1. Installer un notifier (ex. plugin SMTP `MediaBrowser.Plugins.SmtpNotifications`).
2. Dans les paramètres de l’usager → **Notifications**, configurer le service
   (serveur SMTP, destinataire…).
3. **Activer le type « External notification via emby API »** pour cet usager —
   c’est le type sous lequel les notifications envoyées par LLM_AI (popup au
   login, recommandations de la tâche planifiée, échecs de tâche) sont livrées.

`LoginPopup` est **indépendant** de `AutoProgram` : les suggestions à regarder
s’affichent au login même sans auto-programmation.

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

Lire une carte déclenche l'endpoint **`GET /Plugins/LLMAI/Activate?programId=&kind=&card=&t=`** :
1. `AutoProgrammer.ProgramOneAsync` crée le timer d'enregistrement (une reco
   unique), avec dedup par ProgramId ;
2. l'endpoint notifie l'usager par **toast Emby** (succès / déjà programmé /
   échec / disque plein) et, en cas de succès, fait supprimer la carte par Emby
   après ~60 s (voir [Retour des cartes .strm (v1.12)](#retour-des-cartes-strm-v112)) ;
3. l'endpoint stream le clip embarqué `recording_activated.mp4` (8 s, 1280×720, sans texte ni audio — universel).

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

### Tag `AI Tonight` (watch bucket)

`AiTagger` étiquette, sur les **runs frais** de Tonight (pas le cache), les
items Emby réels du **watch bucket** (enregistrements non visionnés + items
possédés) avec le **tag** `AI Tonight` (option `TonightGenreTagEnabled`).
L'usager retrouve les recos en **filtrant sur ce tag** dans n'importe quel
client Emby. Un tag est préféré au genre (v1.13.3) : « AI Tonight » est un
marqueur d'admin, pas un genre de contenu — il n'encombre ni la navigation par
genres ni le vocabulaire du genre cleaner.

- Mutate : `item.Tags = …; item.UpdateToRepository(ItemUpdateType.MetadataEdit)`.
- **Modifie les métadonnées réelles** (tableau `Tags`) — un refresh peut
  l'effacer, réajouté au prochain run frais.
- **Migration v1.13.3** : le nettoyage de 3 h retire aussi l'ancien *genre* du
  même nom des items qui le portent encore.
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

### Playlists « AI Tonight » : privée par usager + publique foyer (watch bucket)

`AiTonightPlaylistManager` maintient **deux familles de playlists jouables**
(option `TonightPlaylistEnabled`), détruites puis **recréées à chaque run
frais** de « À regarder ce soir » — la liste exacte des recommandations du
jour, sans accumulation — pour un enchaînement direct depuis n'importe quel
client Emby (miroir de la collection, même nettoyage de 3 h) :

- **Privée par usager** (v1.13.16.0) : « **AI Tonight · {usager}** »,
  `IsPublic=false` (invisible des autres comptes — défaut d'une playlist Emby
  créée sans MakePublic). Chaque run rafraîchit la playlist **du run** avec
  SES recos (déjà filtrées par sa policy parentale — v1.13.15.0) : un usager
  ne peut plus vider ni reconstruire la playlist d'un autre (la course de
  remplissage de l'ancien modèle unique disparaît).
- **Publique foyer** : « **AI Tonight** » (`IsPublic=true`, visible de tous —
  TV salon, invités), reconstruite **uniquement par les runs de l'usager
  « Tonight »** de la config, avec l'**intersection parentale** : un item du
  watch bucket est écarté si **un seul** usager actif porte une règle
  parentale qui l'interdit (même verdict que le gate des recos,
  `PermissionGate.IsParentallyAllowed`). Pourquoi : le contrôle parental Emby
  est **listing-only** (validé 2026-09-12 — un item visible dans une playlist
  est **lisible** par un compte restreint, la lecture n'est pas bloquée) ;
  l'intersection rend la surface foyer incapable d'exposer ce qu'un compte ne
  peut pas déjà voir. Le chat (`playlist_add`/`playlist_remove`) vise la
  publique foyer.

- **Une feuille jouable par reco** : une reco **série ou saison** n'est jamais
  ajoutée telle quelle (Emby développe une série ajoutée à une playlist en
  TOUS ses épisodes — vérifié : un id série → 52 entrées) mais résolue en **un
  épisode « next up » non vu** pour l'usager du run ; les films/épisodes
  passent tels quels.
- **Repli next up (v1.13.10.1)** : sur ce build Emby, `GetNextUp` retourne
  **vide pour une série jamais commencée** — le repli prend le **premier
  épisode non vu** en ordre saison/épisode (donc S1E1 pour une série neuve),
  et aussi quand le next up retourné est déjà vu. La série n'est sautée que si
  **tout est vu**. S'applique aussi au `playlist_add` du chat.
- **Hygiène (v1.13.2)** : sur ce build Emby, `RemoveFromPlaylist` est inopérant
  — le reset passe par destruction + recréation ; le `playlist_remove` du chat
  signale honnêtement l'inopérance (les items tracés du tour
  restent retirables via l'interface Emby).
- **Modèle de visibilité Emby (validé 2026-09-12)** : playlist créée par un
  usager = **privée** (le propriétaire seul la voit), `POST /Items/{id}/
  MakePublic` = visible de tous, admin = voit tout ; la vue `?UserId=` + clé
  admin est **incohérente pour les playlists** — toute vérification passe par
  des tokens réels. Le nom (« AI Tonight » vs « AI Tonight · {usager} ») est
  le discrimineur in-process (l'entité Playlist n'expose pas de champ owner).

### Nettoyage (tâche `AiTonightCleanupTask`)

Tâche planifiée **quotidienne 03:00**, **toujours active** (non gatingée) :

1. retire le tag `AI Tonight` de tous les items (+ l'ancien genre hérité du même
   nom, migration v1.13.3) via `AiTagger.RemoveAllAsync` ;
2. **vide** la collection `AI Tonight` (coquille conservée, re-remplie au prochain
   run frais) — best-effort ;
3. **détruit** les playlists `AI Tonight` (publique foyer et privées par
   usager, v1.13.16.0) — best-effort.

Les runs « ce soir » suivants réajoutent le tag / re-remplissent la collection /
recréent les playlists sur les recos toujours pertinentes.

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

### Persistance du dernier rapport (v1.13.10)

Le rapport n'existait que dans le DOM de la page : quitter la page le perdait, et toute
relecture exigeait de relancer un audit (coût LLM inutile). Depuis v1.13.10, le
**dernier rapport réussi** est persisté dans `audit_report.json` (dossier de
configuration du plugin, écriture best-effort — `AuditReportStore.cs`), avec sa date,
son mode et son focus. Un seul enregistrement, écrasé à chaque run : la relecture ne
coûte **aucun LLM**.

- **Écriture à chaque run réussi** : les messages d'échec de `RunAuditAsync`
  (« Aucun backend configuré… », « Échec de l'audit… ») ne peuvent **jamais** écraser
  le dernier vrai rapport.
- **`GET /Plugins/LLMAI/Audit?Last=true`** : lecture seule du dernier rapport persisté,
  **sans exécuter d'audit** (zéro LLM), admin-only. La réponse porte
  `LastReport` / `LastGeneratedAt` / `LastMode`.
- **Page de config** : au chargement, la zone rapport affiche par défaut le dernier
  rapport persisté (« Dernier rapport persisté — [date] (mode …) ») ; le bouton
  « Lancer l'audit » régénère et écrase. Toute réponse admin porte aussi les champs
  `Last*` (jamais peuplés pour un non-admin — le rapport expose l'état du serveur).

### Qualité du rapport (v1.13.9.13)

Deux garde-fous sur la restitution du rapport (et de la réponse de chat) :

- **Filet `LlmRunner.SanitizeReport`** : les petits modèles émettent parfois de la
  notation math LaTeX (`$\rightarrow$`) et du HTML cru (`<code>`) que la restitution
  Markdown rend illisible. Le filet remplace les flèches LaTeX par leur glyphe texte
  (« → »), retire les dollars de math-mode résiduels et dénude les balises
  d'habillage — best-effort, appliqué aux sorties audit (les deux modes) **et** chat.
- **Règle « Markdown pur »** injectée dans les prompts d'audit (boucle agent +
  synthèse déterministe, FR + EN) : jamais de LaTeX, jamais de balises HTML. Et la
  **ligne UPnP** figure toujours dans les constats : aucun mapping trouvé = constat
  ✅ explicite (la sonde `upnp_check` ne peut plus passer sous silence).

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
   (champ `SearXngUrl`, déjà utilisé par l'outil `web_search` du LLM —
   [SearXNG](https://docs.searxng.org/) ; repli Ollama
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

## Traduction IA des genres EPG (GenreCleaner)

**GenreCleaner** (plugin officiel du catalogue Emby) normalise les genres : à chaque
scan/refresh, il remappe les genres bruts des items vers un **vocabulaire curaté**
(`AllowedGenres` — ex. votre liste française « Comédie », « Documentaire »,
« Sport »…) via une table `GenreMappings` (`brut → curaté`), et retire ce qui n'y
figure pas. Les deux plugins forment une **boucle fermée** :

- **GenreCleaner agit** — il écrit les genres de la bibliothèque et de l'EPG ;
- **LLM_AI cure** — il détecte les genres que GenreCleaner ne connaît pas encore et
  fait proposer par le LLM les mappages manquants, que l'admin valide avant écriture
  directement dans `GenreCleaner.xml`.

Sans ce pont, maintenir la table `GenreMappings` à la main est un travail sans fin :
l'EPG émet des centaines de variantes brutes (`Bus./financial`,
`Track/field`, `Political News Satire & Talk`…), et un seul canal thématique peut
introduire dix genres inconnus en une mise à jour EPG.

### Genres curatés dans les outils EPG

Le pont ne s'arrête pas à l'écriture des mappages : les outils EPG du LLM
(`epg_series`, `epg_movies`, `epg_tonight`) **émettent eux-mêmes des genres curatés** —
`GenreCleanerMap.MapGenres` applique la table de sa section (films/séries) aux genres
bruts avant de les injecter dans les résultats. Le LLM voit donc le **même vocabulaire**
que le profil de goût de l'usager (bibliothèque, déjà curatée par GenreCleaner) :
« Comédie », « Drame », « Sport »… au lieu de `Sitcom`, `Dark comedy`, `Track/field`.

Les **whitelists et exclusions de genres** (`GenreWhitelist`, exclusions
`documentary`/`news` en mode premieres_only) matchent la clé **brute ET mappée**
(`GenreCleanerMap.GenreKeys`) : une whitelist saisie dans le vocabulaire EPG brut
continue de matcher après activation du mapping — et inversement. Les exclusions par
défaut sont bilingues (`documentary`/`news` + `documentaire`/`nouvelles`).

### Analyse (bouton « Analyser »)

`GET /Plugins/LLMAI/GenreProposals` (admin) :

1. **Collecte** — `CollectUnmapped` requête les programmes EPG **à venir**
   (`IncludeItemTypes=Program`, `HasAired=false` — ce sont eux que les
   recommandations émettent), séparément pour la classe films (`IsMovie=true`) et
   séries (`IsSeries=true`), et retient chaque genre **non couvert** : ni mappé dans
   la table de sa section, ni présent tel quel dans son `AllowedGenres`.
   Requête library calquée sur `BuildGenreMap` (les DTO de `GetPrograms` ne portent
   pas `Genres` sur ce build). Plafond : 60 genres non mappés par section.
2. **Prompt LLM** — un appel one-shot (`ChatWithFallbackAsync`, mêmes
   backends/priorités que le reste du plugin, repli multi-backend) reçoit les deux
   listes **et** les deux vocabulaires curatés. La langue des propositions suit la
   cascade de langue du plugin (`ResponseLanguage` → langue d'affichage Emby →
   `TmdbLanguage`).
3. **Réponse à trois niveaux** :

| Niveau | Affichage (page de config) | Action « Appliquer » |
|---|---|---|
| **Proposition** — le LLM a trouvé un équivalent dans le vocabulaire existant | case `Genre → Cible` (`Movies · Series` selon les sections où le genre apparaît) | ajoute le mappage `Genre → Cible` dans `GenreMappings` |
| **Suggestion de nouveau genre** — aucun équivalent ; le LLM propose un nom court et général (ex. un cluster de genres techniques → « Technologie »), étiqueté *nouveau genre* | case `Genre → NouveauNom` + badge *nouveau genre* | ajoute `NouveauNom` à `AllowedGenres` **et** le mappage `Genre → NouveauNom` — en un clic |
| **Orphelin** — le LLM juge le genre trop spécifique/intraduisible | ligne d'information « sans équivalent possible (aucune action) » | rien (information seulement) |

L'admin **coche/décoche** chaque ligne avant d'appliquer — rien n'est écrit sans
validation explicite.

### Application, redémarrage, auto-guérison

`POST /Plugins/LLMAI/GenreApply` re-valide côté serveur (cible dans le vocabulaire
sauf nouveaux genres, rejet des mappages identité type `Action → Action`), écrit dans
`GenreCleaner.xml` de façon **idempotente** (dedup par clé normalisée — appliquer deux
fois ne crée rien), enregistre chaque mappage appliqué dans
`PluginConfiguration.GenreAliasApplied` et déclenche `NotifyPendingRestart()` — la
bannière Emby « redémarrage requis ».

> ⚠️ **Ordre d'adoption** : les recommandations LLM_AI lisent `GenreCleaner.xml` **en
> direct** et utilisent aussitôt les mappages fraîchement écrits ; mais GenreCleaner,
> lui, a chargé sa config **au démarrage** — le redémarrage signalé par la bannière
> est requis pour que GenreCleaner lui-même adopte le fichier réécrit.

**Auto-guérison** — si le XML revient à une version antérieure (restauration,
ré-écriture depuis la page de config de GenreCleaner, qui sérialise sa copie mémoire),
`GenreCleanerMap.HealApplied` (au GET analyse et à chaque run de la tâche planifiée)
ré-écrit les mappages enregistrés dans `GenreAliasApplied` qui manquent ; les entrées
`new:true` restaurent **aussi** l'entrée `AllowedGenres` correspondante. Rien ne se
perd.

### Pourquoi des genres curatés améliorent les recommandations

Le bénéfice dépasse l'esthétique de la fiche — **toute la chaîne de recommandation
travaille sur des genres propres** :

1. **Profil de goût non fragmenté.** `BuildTasteProfile` agrège les genres des items
   joués par l'usager pour décrire ses goûts au LLM. Avec des genres bruts, « Comédie »
   éclate en `Sitcom`, `Talk`, `Variety`, `Dark comedy`, `Musical comedy`… : le profil
   compte quinze micro-genres d'un item chacun et le LLM ne voit aucune dominante.
   Avec le vocabulaire curaté, tous convergent vers « Comédie » — le profil révèle
   **ce que l'usager regarde vraiment**.
2. **Filtres qui filtre(nt) vraiment.** `GenreWhitelist` de LLM_AI (et tout filtre
   genre côté Emby) ne matche que si le genre brut est déjà le vôtre ; normalisés en
   amont, un seul terme de la whitelist couvre toutes ses variantes EPG.
3. **Prompt LLM plus net.** Les listes EPG injectées dans les prompts montrent des
   genres cohérents et peu nombreux — moins de bruit de token, moins de confusion
   (surtout pour un modèle local), de meilleures justifications.
4. **Surfaces Emby cohérentes.** Cartes `.strm` (genres du `.nfo`), badges, filtres
   par genre dans n'importe quel client : l'usager parcourt « Documentaire » et retrouve
   aussi bien `Nature` que `How-to` que `Consumer` — au lieu de trente étiquettes
   redondantes dispersées dans l'interface.
5. **La boucle se referme.** Mieux recommandé → mieux regardé → profil encore plus
   précis. Et quand un nouveau canal introduit des genres inconnus, l'analyse les
   détecte au prochain passage — la maintenance du vocabulaire devient un clic au lieu
   d'une session d'édition manuelle du XML.

---

## Classifications officielles (Classification Mapper)

**Classification Mapper** (plugin compagnon) réécrit les classifications
officielles (`OfficialRating`) de la **bibliothèque** vers un vocabulaire
canonique maintenu dans son UI (« CA-G », « CA-PG », « CA-14A », « CA-18A »,
« CA-R », « CA-A », « NR »…). Mais l'**EPG émet des classifications
brutes hétérogènes** (« PG-13 », « TV-14 », « 13+ », « 14A »…) : sans pont, les
classifications vues par le LLM viennent de **deux vocabulaires différents**, et
un filtre demandé « 13+ » raterait à la fois « PG-13 » (EPG) et « CA-14A »
(biblio réécrite). `ClassificationMap.cs` referme cet écart, en **miroir du
pont [GenreCleaner](#traduction-ia-des-genres-epg-genrecleaner)** (qui lui fait
la même chose pour les genres) :

- **Normalisation des deux côtés** — `ClassificationMap.Normalize` convertit
  chaque classification officielle vers sa valeur canonique Classification
  Mapper (« 13+ » → « CA-14A », « PG-13 » → « CA-14A »). Appliquée aux
  résultats **et au filtre** de l'action `find` de `get_emby_info` : une
  classification demandée dans n'importe quelle forme brute matche la
  bibliothèque **et** l'EPG, et le champ `classification` émis au LLM est
  toujours canonique.
- **Lecture paresseuse de `classification_mapper_config.json`** — le fichier
  que Classification Mapper dépose dans le dossier de configuration du
  **serveur** (`ConfigurationDirectoryPath`, ex. `/var/lib/emby/config` — pas
  le dossier des configurations de plugins). Rechargement dès que le fichier
  change (mtime, re-stat throttlé à 30 s) : les mappings édités dans l'UI de
  Classification Mapper sont suivis **sans redémarrage** d'Emby.
- **Index inverse brut → canonique** : le canonique est lui-même indexé (les
  items déjà réécrits par Classification Mapper ressortent inchangés). Le
  pliage de clé est trim + minuscules + suppression des espaces internes
  (« PG 13 » ≡ « PG-13 ») ; la casse canonique d'affichage du JSON est
  préservée dans la sortie (« CA-14A », pas « ca-14a ») — les classifications
  émises au LLM restent lisibles.
- **Neutre si Classification Mapper n'est pas installé** : fichier absent ou
  JSON illisible → table vide, chaque classification ressort simplement
  normalisée en casse (uppercase) — le comportement d'un serveur sans le
  plugin. Le lecteur ne lève jamais (fail-open).

Contrairement à la traduction IA des genres (qui **écrit** dans
`GenreCleaner.xml`), ce pont n'a **ni flag de config ni endpoint** : lecture
seule de la config de l'autre plugin, jamais une écriture.

---

## Chat LLM (admin)

Page **« LLM_AI Chat »** (menu admin, section « Serveur », `chat.html`/`chat.js`) :
conversation multi-tours **plein cadre** avec l'agent LLM — les mêmes outils que la
tâche planifiée (`get_emby_info`, `tmdb_lookup`, `web_search`, `new_releases`…,
**pas** les actions de remédiation d'audit), les backends/priorités du plugin.

- **Endpoint :** `POST /Plugins/LLMAI/Chat`, corps
  `{Message, History:[{role,content}], Session}` — la page garde l'historique
  (historique propre à chaque visite de page), les rôles autres que
  user/assistant sont filtrés, le system prompt (doc outils + directives) est construit
  serveur-side. `Session` : identifiant de la mémoire de conversation (retourné par la
  première réponse puis rejoué ; vide = nouvelle conversation).
- **Gating :** `ChatEnabled` (défaut `true`, opt-out). **Admin uniquement** —
  l'outil n'est pas publié dans le menu utilisateur (`EnableInUserMenu` absent) : il
  expose l'introspection de la bibliothèque et de l'EPG.
- Usage : explorer la bibliothèque en langage naturel, préparer/évaluer une soirée,
  questionner l'agent sur ce qu'il peut recommander — sans consommer un run complet.

### Liens profonds vers les fiches Emby (v1.13.9.12)

Quand une réponse cite un item (film, série, épisode, enregistrement, programme EPG)
dont un outil a fourni l'identifiant, le titre est rendu **cliquable vers sa fiche
Emby**, ouverte dans un nouvel onglet — de là, mettre en favori ou enregistrer selon
le cas.

- **Côté serveur** : un bloc `### LIENS PROFONDS EMBY` est injecté dans le workflow du
  chat (`LlmRunner.cs`) avec le gabarit exact `[Titre](/web/index.html#!/item?id=ID&serverId=…)`
  (`&asSeries=true` pour la vue groupée des séries). Le `serverId` est obtenu une
  seule fois via `GetPublicSystemInfo` et mis en cache (bloc omis, fail-open, si
  indisponible). Règle explicite : sans id connu, le titre est cité **sans** lien —
  jamais d'id inventé.
- **Côté page** (`chat.js`, `inline()`) : rendu des liens Markdown avec deux
  garde-fous — **même origine uniquement** (l'URL doit commencer par `/` ; tout lien
  absolu proposé par le LLM reste du texte brut, pas de redirection contrôlée par le
  modèle) et `target="_blank" rel="noopener noreferrer"`.
- Les projections des outils émettent déjà un `id` partout — bibliothèque
  (`i.InternalId.ToString()`) comme EPG (`p.Id` des DTO de `GetPrograms`, la même
  forme que les liens `EpgLink` des NFO .strm) ; aucune modification de
  `GetEmbyInfoTool.cs`.

### Mémoire de conversation

Opt-in `ChatMemoryEnabled` (défaut off) — reprendre le dernier chat et raffiner les
goûts de l'usager :

- **Persistance** : chaque session est journalisée par usager dans
  `chat_memory.json` (5 sessions, 30 jours) — tours verbatim, total, résumé.
- **Condensation paresseuse** : la session précédente est résumée (UN appel LLM
  sans outils) en **tâche de fond** au retour de l'usager (ouverture de la page,
  nouvelle conversation) — jamais pendant la conversation (zéro coût par tour).
  Note de continuité « Goûts exprimés / Faits utiles / Fil ouvert », terminée par
  une ligne `SIGNALS:` (tableau JSON des signaux de goût, parsing tolérant :
  absent = ignoré, fail-open).
- **Pont réflexif** : chaque signal de goût (titre, raison, +/−) devient une
  décision `kind="chat"` dans `decisions.json` (dédoublonnée 7 jours) — la
  révision hebdo de la fiche mémoire les intègre.
- **Injection** : résumé de la session précédente + ses 6 derniers échanges
  verbatim, accolés au workflow de chat (après la fiche mémoire) ; jetable (le
  résumé suivant le remplace).
- **UI** : bannière « Conversation du {date} — {n} échanges » + bouton
  **« Reprendre »** (restaure les derniers échanges et la session) ;
  « Effacer la conversation » oublie aussi la session serveur.
- **Endpoints admin** : `GET /Plugins/LLMAI/ChatMemory` (session la plus
  récente), `POST /Plugins/LLMAI/ChatMemory/Forget` (oubli de session(s)).

### Couche d'action du chat

Le chat agit sur les **mêmes surfaces que le plugin** — cartes .strm « AI
Suggestions », timers d'enregistrement, tag « AI Tonight », collection,
playlist, run Tonight — en réutilisant les primitives existantes, sous limites
strictes (`ChatActions.cs`) :

- **Admin-only + human in the middle** : pas d'autorisation supplémentaire ;
  l'étiquette (annoncée dans le system prompt) est « proposer dans le texte,
  exécuter après confirmation explicite de l'admin ». Les garde-fous DURS sont
  le budget et le gating admin.
- **Budget d'actions** : `ChatActionBudget` par tour (défaut 10, toutes
  surfaces confondues) + `ChatActionConversationCap` par conversation (défaut
  30, compteur en mémoire serveur — remis au redémarrage). `0` = lecture seule.
  Consommation **au succès** : une action refusée par un garde-fou (déjà
  possédé, déjà visionné, drop list, doublon) ne consomme rien ; un lot ne
  couvrant pas le budget restant est refusé en bloc (all-or-nothing).
- **8 tools** : `record_program` (timer via `AutoProgrammer.ProgramOneAsync`),
  `create_card` (carte .strm unique, éphémère par construction — le marker
  `.llmai_reco` la fait nettoyer par Emby à la prochaine génération
  planifiée), `tag_ai_tonight` (tag, v1.13.3), `collection_add`/`collection_remove`,
  `playlist_add`/`playlist_remove` (primitives **additives** — contrairement à
  `EnsureAsync` qui rapproche tout le contenu ; le retrait n'accepte que les
  items que le chat a ajoutés lui-même dans la conversation) et
  `run_tonight_run` (opt-in).
- **`run_tonight_run(directives?)`** (`ChatTonightRunEnabled`, défaut false) :
  déclenche le run Tonight sur le chemin exact de la tâche planifiée et du
  login. Directives de session **éphémères** (≤ 500 caractères, valables pour
  ce run uniquement, jamais persistées) ; un seul run chat à la fois, 2 par
  conversation ; runId préfixé `c` (diagnostic) ; badge discret « générée via
  chat — directives : … » sur la page Recommandations (la métadonnée survit
  au cache par usager). Les résultats sont livrés par les surfaces habituelles
  (page, tag, collection, playlist selon la config).
- **Trace visuelle des actions** : chaque action réussie émet un libellé vers
  deux sorties (v1.13.1/v1.13.4) — (1) un **toast Emby** 🤖 vers toutes les
  sessions admin (visible sur les pages normales d'Emby, autre appareil… ; le
  client web ne le rend PAS sur la page de configuration, constat 2026-09-06),
  et (2) une **ligne discrète dans la page de chat** sous la réponse du LLM
  (« 🤖 1 item(s) tagué(s) « AI Tonight » » — champ `actions` de la réponse
  HTTP, non rejoué par l'historique). Succès seulement : rien pour les refus
  de garde-fous.
- **Hygiène playlist (v1.13.2)** : sur ce build Emby, `RemoveFromPlaylist`
  est inopérant (no-op interne / HTTP 500) et une série ajoutée est
  développée en TOUS ses épisodes. Les ajouts (tools chat compris) passent
  donc par une **normalisation en feuilles** (série/season → épisode *next
  up*, skip si tout vu) et le comptage honnête par re-listing (les items
  déjà présents ne consomment pas de budget). Le reset complet reste le
  destroy+recréate du run Tonight ; `playlist_remove` signale l'inopérance
  du retrait à l'usager.

### Édition des prompts par le chat

Un cas d'usage de bout en bout : *« ajoute la règle X à ma directive films »* dit dans
le chat, approuvé sur la page — sans jamais ouvrir la page de configuration à la main.

- **Contextes d'édition (v1.13.8)** : une liste déroulante de la page chat sélectionne
  un mode parmi cinq (un par prompt éditable — Directives RAG, Tâche séries, Tâche
  films, Run « ce soir », Audit santé). À CHAQUE tour, le serveur réinjecte un bloc
  système portant le **guide d'édition** du mode (rôle du prompt, invariants à
  préserver), son **texte courant** relu de la config (règle read-modify-write — des
  règles de l'usager peuvent n'exister que dans sa config live) et la **langue cible**
  (résolue serveur : `ResponseLanguage`, sinon langue d'affichage Emby). Changer de
  mode n'exige jamais de réinitialiser la conversation ; au changement, la page envoie
  une note `[Admin]` automatique qui ancre le texte courant.
- **Tool `plugin_prompts` (two-phase, opt-in `ChatPromptsEnabled`)** : `list`/`get`
  lisent les cinq champs ; `set` ne fait que **proposer** — la proposition est
  sérialisée dans `chat_pending.json` (expiration 10 min, une par conversation) et la
  page affiche une **carte de diff** Approuver/Refuser. L'écriture n'a lieu qu'au clic
  « Approuver », en C# déterministe (endpoint `ChatPrompt/Approve`) : le LLM n'a
  AUCUN chemin d'écriture direct, l'approbation n'est pas contournable par prompt.
  Garde-fous : liste blanche des champs, plafond 8000 caractères, **validation
  croisée champ ↔ mode** (un set ne peut viser que le prompt du mode actif) et
  **avertissement de divergence** (recouvrement lexical < 25 % avec le texte courant
  → bandeau sur la carte).
- **Le bloc ```text comme canal de livraison (v1.13.9.11)** : toute révision proposée
  se termine par le texte COMPLET du prompt révisé dans un bloc clôturé ```text — le
  seul canal par lequel le texte parvient à l'interface, qui active le bouton de
  sauvegarde 💾 (message autoporteur : le clic vise SON bloc). La confirmation porte
  sur l'exécution du bloc déjà livré (carte de diff), jamais sur sa production — le
  modèle ne doit jamais demander « voulez-vous que je prépare le texte ? » avant de
  livrer. Les règles sont injectées à chaque tour (`CommonRules`) ET portées par la
  description du tool.
- **Filet structurel** : en mode d'édition, une réponse sans aucune clôture et qui se
  termine par une question (pattern de déférence, vécu gemma4:26b) déclenche côté
  serveur UN nudge automatique (« livrez maintenant le texte complet en bloc
  ```text ») — seule la réponse corrigée part à la page. Un seul par tour, réponse
  d'origine rendue en cas d'échec ; un bouton de secours au niveau du tour reste
  disponible.
- **Après approbation** : le LLM rappelle de recharger la page de configuration — un
  enregistrement ultérieur de la page avec des valeurs affichées périmées écraserait
  la modification.

---

## Mémoire réflexive

**Opt-in, expérimental** — le LLM observe ses propres résultats et tient à jour sa
stratégie. Cycle complet : **données → réflexion → réinjection**.

### Les stores (Phase A/B, `DecisionStore` / `PlaybackWatcher` / `EpgSnapshotStore`)

Tous fichiers JSON dans le répertoire de configuration du plugin, opt-in, plafonnés,
rétention bornée, fail-open (fichier absent/corrompu → vide, jamais d'exception) :

| Fichier | Contenu | Rétention |
|---|---|---|
| `decisions.json` | Chaque reco émise (Tonight / enregistrement / rejet « Oublier » / **signal de goût du chat**) avec sa **raison LLM**, sa priorité, et la **version de fiche en vigueur** (`mv` — clé de la calibration) | 30 j |
| `run_pool.json` | Le « menu » de candidats soumis à chaque run (EPG capté par `get_emby_info`, réserve bibliothèque, enregistrements non visionnés) — distingue une **mauvaise reco** d'une **erreur de classement** | 30 j |
| `playback.json` | Chaque lecture terminée : item, usager, durée réelle, **fraction lue**, source (bibliothèque/.strm/direct), chaîne, client, appareil | 30 j |
| `epg_snapshot.json` | Métadonnées EPG **figées** à l'émission au LLM / la création de timer (titre, synopsis, chaîne, **durée de diffusion** — dénominateur du % du direct, genres normalisés) | 90 j |

Catégorisation comportementale dérivée : **rejet immédiat** < 5 %, **abandon**
5–50 %, **partiel** 50–80 %, **validé** > 80 % (le % du direct = durée lue ÷ durée
de diffusion du snapshot, joint par chaîne + fenêtre de diffusion).

### La fiche mémoire (Phase C, `MemoryTask` / `MemoryCard`)

Tâche hebdomadaire (dimanche 4 h 30 — après l'analyse classique de 4 h, opt-in
`MemoryCardEnabled`) :

1. **Jointure 100 % C#** (zéro LLM pour les données) : décisions × télémétrie ×
   **calibration des versions de fiche** (les recos émises sous v3 ont-elles mieux
   marché que sous v4 ?) × candidats écartés des pools × vu-sans-recommandation
   (titres résolus côté C#) × créneaux de lecture.
2. **Un appel LLM sans outils** réécrit la fiche : reprise de l'actuelle obligatoire,
   sections imposées (« Ce que je sais de l'usager / Ce qui a marché / Ce qui a
   échoué et pourquoi / Stratégies / Zones d'incertitude »), auto-évaluation de la
   *croyance* ratée (pas seulement du titre), nuance signal faible (1-2 cas) vs fort
   (3+), ≤ 250 mots.
3. **Historisation immuable** : version++, 4 versions précédentes conservées — le
   contrepoids anti-dérive. Échec LLM → fiche précédente conservée (fail-open).

**Injection (3 sites)** : quand la fiche est active et renseignée, elle **remplace**
la directive de la boucle de rétroaction classique — prompts Tonight, prompts
d'enregistrement, workflow de chat. Fiche vide/absente → repli transparent sur la
directive classique.

**Consultation / édition admin** : section « Fiche mémoire actuelle » de la page de
configuration (version/date/nb de versions conservées, texte éditable pour un
correctif manuel que le LLM reprendra la semaine suivante) — endpoints
`GET`/`POST /Plugins/LLMAI/MemoryCard` (admin).

### Reset

Pas de bouton : renommer/supprimer les fichiers JSON ci-dessus suffit (les stores
les recréent au premier écrit, sans redémarrage). Piège : supprimer
`decisions.json` seul déclenche la re-migration de l'ancien journal RecoLog — vider
aussi le champ RecoLog de la config pour un zéro réel. Renommer (backup) vaut
supprimer (inversible).

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
GET /Plugins/LLMAI/Audit?Last=true
```

**Audit santé à la demande** : lance un run agent dédié (outil `system_audit`) et
renvoie un **rapport Markdown** de santé du serveur. Voir [Audit santé](#audit-santé).
`?Last=true` : lecture seule du **dernier rapport réussi persisté**, sans exécuter
d'audit (zéro LLM) — c'est l'appel du chargement de la page de config.

**Réponse :** `{ Enabled, Report, Date, Error, LastReport, LastGeneratedAt, LastMode }` —
`Report` est le rapport Markdown brut (rendu côté config.js via un mini-convertisseur
Markdown→HTML sûr) ; les champs `Last*` portent le dernier rapport persisté (`Last=true`
ou run réussi). `Enabled=false` si `AuditEnabled` est off ; `Error="Réservé aux
administrateurs."` si l'appelant n'est pas admin.

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

```
GET /Plugins/LLMAI/Recos
```

**Recommandations pour la page usager** : dernières recommandations de la tâche
planifiée (payload JSON brut + date du run). Route créée pour les **usagers
non-admin** — la page `recommendations.js` lisait auparavant la config plugin via
l'endpoint hôte `/Plugins/{id}/Configuration` (réservé ManageServer → 403).

**Réponse :** `{ Items, Date, Error }` — `Items` est la chaîne JSON des recommandations
(même forme que le champ `Recommendations` de la config).

**Authentification :** tout usager authentifié (token de session ou clé API). La route
ne sert **que** `Recommendations`/`RecommendationsDate` — jamais la config complète
(clés API, prompts, chemins).

```
POST /Plugins/LLMAI/Forget        corps : {"Title":"..."}
```

**Bouton « Oublier » serveur-side** : ajoute le titre à la drop list persistante
`DroppedTitles` (via `SaveConfiguration`). Était fait côté page par un round-trip
config admin, donc 403 pour un non-admin.

**Réponse :** `{ Added, Error }` — `Added=false` si déjà présent (idempotent).

**Authentification :** tout usager authentifié.

```
POST /Plugins/LLMAI/Chat          corps : {Message, History:[{role,content}], Session}
```

**Chat LLM admin** : un tour de conversation avec l'agent (tous les outils, backends/
priorités du plugin). `History` est renvoyé par la page, filtré aux rôles user/assistant ;
`Session` (optionnel) active la mémoire de conversation — l'identifiant est retourné
dans chaque réponse et rejoué par la page. Voir
[Chat LLM (admin)](#chat-llm-admin) et [Mémoire de conversation](#mémoire-de-conversation).

**Réponse :** `{ Reply, Enabled, Error, Session }`.

**Authentification :** admin uniquement.

```
GET  /Plugins/LLMAI/MemoryCard
POST /Plugins/LLMAI/MemoryCard    corps : {"Text":"..."}
```

**Fiche mémoire réflexive** (admin) : `GET` renvoie `{Version, Updated, Text,
HistoryCount, Error}` (fiche actuelle + nombre de versions conservées) ; `POST`
enregistre un correctif manuel du texte (version et historique inchangés — le LLM
reprendra ce texte à la prochaine révision hebdo). Voir [Mémoire réflexive](#mémoire-réflexive).

**Réponse :** `{Version, Updated, Text, HistoryCount, Error}`.

**Authentification :** admin uniquement.

**Test :**
```bash
curl -H "X-Emby-Token: VOTRE_CLE_API" \
  "http://localhost:8096/emby/Plugins/LLMAI/MemoryCard"
```

```
GET  /Plugins/LLMAI/ChatMemory
POST /Plugins/LLMAI/ChatMemory/Forget    corps : {"Session":"c..."} (optionnel)
```

**Mémoire de conversation** (admin) : `GET` déclenche la condensation paresseuse des
sessions passées (tâche de fond) et renvoie `{Current:{Id, Date, Turns, HasSummary,
Summary, Last:[{role,content}]}, Enabled, Error}` — la session la plus récente avec
des tours (utilisée par la bannière « Reprendre » de la page chat). `POST …/Forget`
supprime la session indiquée (sans `Session` : toutes les sessions de l'admin courant).

**Réponse Forget :** `{Forgotten, Error}`.

**Authentification :** admin uniquement.

**Test :**
```bash
curl -H "X-Emby-Token: VOTRE_CLE_API" \
  "http://localhost:8096/emby/Plugins/LLMAI/ChatMemory"
```

```
GET /Plugins/LLMAI/GenreProposals
```

**Analyse des genres EPG** (admin) : collecte les genres des programmes **à venir**
non couverts par GenreCleaner (par section films/séries) et retourne les propositions
LLM à trois niveaux (cibles existantes / nouveaux genres suggérés / orphelins).
Aucune écriture à ce stade. Voir
[Traduction IA des genres](#traduction-ia-des-genres-epg-genrecleaner).

**Réponse :** `{ Proposals:[{Genre, Movies, Series, New, InMovies, InSeries}],
UnmappedMovies, UnmappedSeries, Orphans:[], Message, Error }`.

```
POST /Plugins/LLMAI/GenreApply    corps : {Mappings:[{Name, Value, Section, NewGenre}]}
```

**Écriture des mappages validés** (admin) : re-validation serveur (vocabulaire, rejet
des mappages identité), écriture idempotente dans `GenreCleaner.xml` (AllowedGenres +
GenreMappings), enregistrement dans `GenreAliasApplied` (auto-guérison),
`NotifyPendingRestart`.

**Réponse :** `{ Applied, RestartRequired, Error }`.

**Authentification :** admin uniquement (GenreProposals et GenreApply).

```
GET /Plugins/LLMAI/Update
```

**Vérification de mise à jour** : compare le tag de la dernière release GitHub du
plugin (`reneboulard/LLM_AI`, workflow `release.yml`) à la version d'assembly
installée. **Lecture seule** — aucun téléchargement ni installation ; la page de config
affiche un bandeau avec le lien de la release. Cache 1 h sous verrou (limite API
GitHub) ; `?Force=1` pour bypasser (debug).

**Réponse :** `{ Current, Latest, Available, ReleaseUrl, ZipUrl, CheckedAt, Error }`.
Erreur réseau → `Error` (pas de bannière, pas de bruit).

**Test :**
```bash
curl -H "X-Emby-Token: <token>" \
  "http://localhost:8096/emby/Plugins/LLMAI/Update?Force=1"
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

**Analyse des genres : « tout est déjà couvert » alors que des genres semblent manquer :**
L'analyse ne couvre que les programmes EPG **à venir** (`HasAired=false`) — c'est le
périmètre des recommandations ; un genre présent uniquement sur des programmes déjà
diffusés n'est pas proposé. Vérifier aussi que GenreCleaner a bien **redémarré** depuis
la dernière écriture de mappages (bannière « redémarrage requis ») : sans redémarrage,
GenreCleaner travaille sur sa copie mémoire et peut réécrire le XML sans vos mappages —
l'auto-guérison les restaure au passage suivant.

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