# Changelog

Tous les changements notables de ce plugin sont documentés ici.
Le format s'inspire de [Keep a Changelog](https://keepachangelog.com/),
et ce projet adhère au [Semantic Versioning](https://semver.org/lang/fr/).

All notable changes to this plugin are documented here.
Format based on [Keep a Changelog](https://keepachangelog.com/),
and this project adheres to [Semantic Versioning](https://semver.org/).

---

## [1.13.16.0] — 2026-09-12

### Added — Playlists « AI Tonight » conformes aux droits (privée par usager + publique foyer à intersection parentale)

- **Constat validé empiriquement (Emby 4.10)** : le contrôle parental est
  **listing-only** — la limite (`MaxParentalRating`) et les tags
  (`BlockedTags`) filtrent les listings de l'usager, mais la **lecture** d'un
  item visible (y compris depuis une playlist publique) n'est PAS bloquée
  (`PlaybackInfo` 200, stream 200 sous token du compte restreint, item
  CA-14A au-dessus d'une limite CA-PG). L'ancienne playlist publique unique
  était donc un contourne-ment : le contenu rempli par le run d'un usager
  sans limite restait lisible par un compte restreint.
- **Playlist privée par usager** (`AiTonightPlaylistManager.EnsureUserAsync`) :
  « **AI Tonight · {usager}** », `IsPublic=false` (invisible des autres
  comptes — défaut d'une playlist Emby sans MakePublic, validé 2026-09-12).
  Chaque run rafraîchit la playlist **du run** avec SES recos (déjà filtrées
  par sa policy dans `ValidateAndFilter`), plus un filet parental sur les
  feuilles résolues (`FilterParental`). Un usager ne peut plus vider ni
  reconstruire la playlist d'un autre (la course de remplissage disparaît —
  un run 0-reco ne vide plus la playlist de tous).
- **Playlist publique foyer** (`EnsurePublicAsync`) : « **AI Tonight** »
  (`IsPublic=true`), reconstruite uniquement par les runs de l'usager
  « Tonight » de la config, avec **intersection parentale** — un item du
  watch bucket est écarté si un seul usager actif porte une règle qui
  l'interdit (verdict `PermissionGate.IsParentallyAllowed`, compteur au
  journal). Aucun membre acceptable = playlist absente (jamais recréée vide).
- **Le nom est le discrimineur in-process** : « AI Tonight » (publique) vs
  « AI Tonight · {usager} » (privées) — l'entité `Playlist` n'expose pas de
  champ owner et la vue `?UserId=`+clé admin est incohérente pour les
  playlists (validé). Le chat (`playlist_add`/`playlist_remove`) vise la
  publique foyer.
- **Audit aligné** (`SystemAuditTool`, volet sécurité) : le check des surfaces
  couvre la publique ET les privées (propriétaire déduit du nom), avec le
  **verdict parental complet** du gate (limite, tags noirs/blancs, non cotés,
  tags de série — l'ancien check ne voyait que `MaxParentalRating`) et un
  libellé honnête : « il peut les LIRE depuis la playlist » (l'ancien texte
  « ne peut pas la lire » était faux pour le cas cote).
- **Nettoyage 3 h** (`AiTonightCleanupTask`) : détruit la publique et toutes
  les privées (« AI Tonight* »).

## [1.13.15.0] — 2026-09-12

### Added — Contrôle parental dans « Watch Tonight »

- **Principe** : les candidats des recos « Watch Tonight » respectent la
  section « Contrôle parental » de la policy de l'usager demandeur (pattern
  v1.13.12.0 : contrainte dite au LLM en amont, filtrage mécanique en aval) —
  un usager limité ne reçoit plus de reco qu'Emby cacherait dans son UI.
- **Nouvelle couche parentale de `PermissionGate`** (lecture de policy à
  chaud, no-op si aucune règle, fail-open par item illisible) :
  - **Bibliothèque** (réserve + binge + recos `recording`/`library`) : cote
    héritée native (`GetInheritedParentalRatingValue`) vs `MaxParentalRating` ;
    item sans cote ou « NR » → règle `BlockUnratedItems` du type (Movie,
    Series…) ; cote présente mais hors table serveur (pas de score natif) →
    **jamais bloquée** (pas de décision forcée — l'audit `ratings_check`
    signale ces cotes). Pour un épisode, les tags de la série porteuse sont
    consultés.
  - **EPG** : cote textuelle du programme normalisée par le plugin
    Classification Mapper (si installé) puis par la table parentale du
    serveur ; non reconnue → conservée et comptée à part (native-blind).
  - **Tags** : les deux modes de la page Contrôle parental — liste noire
    (`BlockedTags` + tag porté → écarté) et liste blanche
    (`IsTagBlockingModeInclusive` + « exclure tous sauf le tag »), avec les
    deux sous-modes `AllowTagOrRating` (le tag autorisé contourne ou non la
    limite de cote).
- **Points d'insertion** : réserve bibliothèque et pool binge (avant
  présentation au LLM) ; `ValidateAndFilter` (toutes les jambes : live,
  recording, library, live-but-owned via `library_id`) avec compteur dédié
  dans le journal de validation.
- **Note « trop restrictif »** : policy parentale active et moins de recos que
  le minimum demandé → le champ `Warning` de la réponse Tonight explique
  pourquoi à l'usager (le cas dégénéré « liste blanche stricte » peut ne rien
  laisser de recommandable — validé empiriquement 2026-09-12 : 3782 films → 1,
  guide EPG → 0) au lieu d'une liste courte muette.

## [1.13.14.0] — 2026-09-11

### Added — Audit : hygiène des cotes (`ratings_check`)

- **Principe** : une cote (`OfficialRating`) non reconnue par la table
  parentale intégrée du serveur n'a pas de score numérique — la limite
  parentale (`MaxParentalRating`) est **aveugle** sur cet item. Les
  fournisseurs (TMDB/TVDB, guide EPG) livrent des formats nationaux
  hétérogènes : sans normalisation, la cote est « n'importe quoi ».
- **Nouvelle action `ratings_check` de `system_audit`** (lecture seule,
  aussi dans le digest déterministe) :
  - **Bibliothèque** (films + séries) : census des cotes vs
    `ILocalizationManager.GetParentalRatings()` — la même liste que le menu
    de limite parentale du dashboard. Cotes non reconnues → constat
    **⚠️ avertissement** avec les valeurs les plus fréquentes (détail +
    exemple d'item) et le **conseil de normalisation** (ex. plugin
    Classification Mapper) ; aucune → ✅. Les marqueurs « non coté » (NR,
    Unrated…) sont comptés à part : légitimes, couverts par
    `BlockUnratedItems`.
  - **EPG** : census séparé en ℹ️ info — les cotes du guide viennent
    **brutes** du fournisseur (jamais passées à la normalisation de la
    bibliothèque, programmes transitoires) ; la comparaison y est
    indicative, appliquer la limite au contenu en direct exigerait une
    carte EPG → table serveur.
  - Fail-open : table ou bibliothèque illisible → JSON d'erreur, jamais une
    exception.

## [1.13.13.2] — 2026-09-11

### Fixed — Création des timers séries compatible 4.10 (activation .strm d'une série)

- **Problème** : `SeriesTimerInfo` (type interne Controller) n'a plus la
  propriété `ChannelId` sur Emby 4.10 (il ne reste que `ChannelIds`, présent
  sur toutes les builds). L'initialiseur compilé
  `new SeriesTimerInfo { ChannelId = … }` cassait au JIT
  (`MissingMethodException: set_ChannelId(System.String)`) — l'activation d'une
  carte .strm **série** échouait systématiquement à la création du timer
  (chaîne .strm observée en prod : « Bienvenue à Kingston-Falls »).
- **Correctif** : ne passer plus que par `ChannelIds` (formé du DTO, repli sur
  `ChannelId` de la base commune si vide) — les champs restants de
  l'initialiseur ont été vérifiés présents sur les deux builds, et le chemin
  film (`TimerInfoDto`, Model) est inchangé entre les builds.

## [1.13.13.1] — 2026-09-11

### Fixed — Lecture de `LiveTvOptions` compatible 4.10 (échecs d'activation des cartes .strm)

- **Problème** : `LiveTvOptions` n'a plus les propriétés string
  `RecordingPath`/`MovieRecordingPath`/`SeriesRecordingPath` sur Emby 4.10
  (remplacées par les ids de dossiers `RecordingFolderId`/…). L'accès **compilé**
  à une propriété absente casse le JIT (`MissingMethodException`) —
  `RecordingDiskManager.TryResolveRecordingPath` plantait, bloquant **à la fois**
  le gate disque du `Activate` (cartes .strm : échec création timer) et
  l'auto-programmeur. Le DVR natif Emby, sans référence à ces propriétés,
  enregistrait normalement.
- **Correctif** : lecture des propriétés **par réflexion** (absente → null,
  jamais de `MissingMethodException`) ; repli sur les **ids de dossiers** 4.10
  (`RecordingFolderId`/`MovieRecordingFolderId`/`SeriesRecordingFolderId`
  résolus dans la bibliothèque → `BaseItem.Path`) ; repli **défaut Emby**
  `<ProgramData>/data/livetv/recordings` inchangé (le dossier effectivement
  utilisé quand rien n'est configuré). Insensible à la build hôte.

## [1.13.13.0] — 2026-09-11

### Added — Audit : cohérence d'accès des surfaces foyer (playlist + bibliothèque .strm)

- **Principe** : la playlist **« AI Tonight »** et la bibliothèque **.strm**
  sont des surfaces foyer, remplies par le watch bucket du run — un compte
  peut donc y voir une reco hébergée dans une bibliothèque qui ne lui est pas
  partagée (titre + poster visibles, lecture refusée par Emby), ou au-dessus
  de sa limite parentale. L'audit signale ces décalages à l'admin — le
  dashboard reste maître des accès, le plugin ne modifie jamais les comptes.
- **Nouveau volet « Surfaces plugin » de l'action `security_check`
  (`SystemAuditTool.SecurityCheck`)**, après la section réseau (les
  avertissements participent à l'escalade existante : exposition externe
  observée → critique) :
  - **Playlist** : pour chaque usager actif (désactivés exclus) — items hors
    des bibliothèques accessibles détectés par le même mécanisme que les recos
    (`PermissionGate.FilterAccessible`, requête `ItemIds`+`AncestorIds`,
    fail-open) → ⚠️ *« … il voit la reco mais ne peut pas la lire »* ;
    contenu au-dessus de sa limite parentale (`MaxParentalRating` vs
    `GetInheritedParentalRatingValue`) → ⚠️ distinct.
  - **Bibliothèque .strm** : accès détecté (`EnableAllFolders` ou une
    bibliothèque de `EnabledFolders` résolue sous la racine .strm) sans le
    droit d'enregistrement → ⚠️ (règle v1.13.11.0 : réserver les cartes aux
    comptes à droit d'enregistrer) ; inverse — droit d'enregistrer sans accès
    .strm — → ℹ️ info (les cartes sont invisibles pour ce compte).
  - **Fallbacks honnêtes** : playlist absente/vide ET bibliothèque .strm non
    configurée → ℹ️ info (rien à vérifier encore) ; échec de lecture →
    ℹ️ « non vérifiable », jamais une erreur ; tout propre → ✅ « accès
    cohérents ».

## [1.13.12.0] — 2026-09-11

### Added — Reco visionnage « Watch Tonight » conforme aux droits de l'usager

- **Principe** : l'intérêt de visionnement est **par usager** — la recommandation
  « Watch Tonight » respecte désormais les droits de l'usager demandeur :
  **TV en direct** (`EnableLiveTvAccess`), **accès médiathèque**
  (`EnableAllFolders`/`EnabledFolders`). Le droit d'enregistrement (v1.13.11.0)
  complète le tableau ; la suppression de médias n'a aucun impact (le plugin ne
  supprime jamais de médias).
- **EPG consulté seulement avec le droit TV en direct** :
  - **Tools `get_emby_info`** (`GetEmbyInfoTool.RunUser`, posé par
    `LlmRunner.BuildTools` pour les runs per-usager) : sans
    `EnableLiveTvAccess`, les actions `epg_tonight`/`epg_series`/`epg_movies`
    et la jambe EPG de `find` renvoient un résultat **vide ET légitime**
    (`{total: 0, note: …}`) — pas une erreur, le LLM réoriente vers les
    enregistrements et la réserve bibliothèque. Null (tâche planifiée, chat)
    → comportement global inchangé.
  - **Prompt** (`TonightService`) : ligne « CONTRAINTE D'ACCÈS » injectée
    quand le droit manque (filet double avec la note des tools).
  - **Validation** (`ValidateAndFilter`) : sans le droit, PAS de snapshot EPG
    (donnée à laquelle l'usager n'a pas droit) et les recos `source="live"`
    pures sont **droppées** (compteur `liveNotPermitted`) — conservées
    seulement si enrichies d'un `library_id` (watchables depuis la
    bibliothèque). Avec le droit, la logique fail-open existante est inchangée.
- **Candidats bibliothèque filtrés** (`PermissionGate.FilterAccessible`) : la
  réserve bibliothèque et les séries « prêtes à dévorer » (binge) restent dans
  les bibliothèques accessibles à l'usager (`EnableAllFolders`/`EnabledFolders`,
  policy lue à chaud). No-op si non restrictif ; fail-open si la résolution des
  bibliothèques échoue (une indisponibilité ne vide jamais les recos). Le
  bucket « enregistrements » (tier foyer) et le profil de goût (historique déjà
  visionné) ne sont pas filtrés.
- **Page Recommandations** : `CanLiveTv` servi par `/Plugins/LLMAI/Recos` **et**
  `/Plugins/LLMAI/Tonight` (les cartes tonight passent par cette dernière) —
  sans le droit TV en direct, le bouton « Regarder en direct » est masqué ;
  sans le droit d'enregistrement, le bouton « Programmer » des cartes tonight
  est également masqué (le refus natif Emby reste le filet).
- Version → 1.13.12.0.

## [1.13.11.0] — 2026-09-11

### Added — Gate de droits d'enregistrement (facettes .strm et page Recommandations)

- **Principe** : programmer un enregistrement est une décision **foyer** pilotée
  par le droit d'enregistrement Emby (`EnableLiveTvManagement`) ; l'intérêt de
  visionnement (Watch Tonight) reste par usager. Un usager sans ce droit ne doit
  ni déclencher d'enregistrement ni voir les recommandations d'enregistrement.
- **`PermissionGate.cs`** (nouveau) : résolution de l'usager (token de la
  requête, ou session de lecture) et lecture de sa policy **à chaud** — jamais
  mise en cache, jamais de comptes propres au plugin (la policy Emby est la
  source de vérité).
- **Gate .strm** (`ActivateApiService`) : les requêtes `.strm` ne portent pas
  l'auth Emby et la session lecteur n'est visible qu'après l'ouverture du flux —
  le contrôle est donc **asynchrone** (même finder que le toast v1.12) : une
  fois le lecteur identifié, un usager sans `EnableLiveTvManagement` voit les
  timers créés par sa lecture **annulés** (seulement ceux de cette activation —
  capture des ids préexistants) et reçoit un toast dédié (« Enregistrement non
  autorisé pour ce compte : … ») ; la carte reste en bibliothèque. Usager non
  identifiable (sonde serveur, api_key, cartes d'avant v1.12) → comportement
  inchangé (fail-open, logué). La fraîcheur d'activation (anti-doublon des GET
  répétés) est décidée une fois par lecture : la création de timer n'a plus lieu
  que sur le premier GET.
- **Page Recommandations** (`RecosApiService` + `recommendations.js`) : la
  réponse `/Plugins/LLMAI/Recos` porte désormais `CanRecord` (policy de l'appelant) ;
  sans ce droit, les sections d'enregistrement (Séries/Films) **ne sont pas
  rendues** — la section « À regarder ce soir » reste visible. Le bouton
  « Programmer » était déjà protégé nativement (l'API LiveTv d'Emby refuse un
  usager sans le droit).
- **Config** : note dans la section « Bibliothèque .strm des recommandations » —
  réserver la bibliothèque (accès par dossier du dashboard) aux comptes disposant
  du droit d'enregistrement.
- Version → 1.13.11.0.

## [1.13.10.1] — 2026-09-10

### Fixed — Playlist « AI Tonight » : séries jamais commencées (repli next up)

- **Repli « next up »** (`AiTonightPlaylistManager.ResolveLeafIds`) : sur ce build
  Emby (4.10.0.40), `ITVSeriesManager.GetNextUp` retourne **vide pour une série
  jamais commencée** (0 épisode vu — vérifié aussi via le REST natif
  `/Shows/NextUp` pour 2 usagers) : il ne « commence » pas la série. Une reco
  série non vue était donc **sautée** — le run du 2026-09-10 (08:29) a produit
  2 recos watch bucket (série + film) mais la playlist n'en contenait qu'**une**
  (journal : « série … sans épisode next up (tout vu ?) — sautée »).
- Le repli calcule le « prochain » à la main : **premier épisode non vu** en
  ordre saison/épisode (`Folder.GetItemList` + `IUserDataManager.GetUserData` —
  même pattern que le stock du binge de `TonightService`). Déclenché aussi quand
  le next up retourné est **déjà vu** (le build peut le renvoyer).
- La série n'est sautée que si **tout est vu** (message de journal explicite).
  S'applique aux deux chemins : run Tonight (`EnsureAsync`) **et** ajout par le
  chat (`AddItemsAsync`). Toujours **une feuille par reco série** — jamais
  l'expansion en tous les épisodes (quirks playlist 4.9.5.0).
- Version → 1.13.10.1.

## [1.13.10.0] — 2026-09-10

### Added — Persistance du dernier rapport d'audit, affiché par défaut dans la page de config

- **`AuditReportStore.cs`** (nouveau) : le dernier rapport d'audit **réussi** est
  persisté dans `audit_report.json` (dossier de configuration du plugin, convention
  `ChatMemoryStore` — JSON `System.Text.Json.Nodes`, écriture best-effort fail-open).
  Un seul enregistrement, écrasé à chaque run : la **relecture ne coûte aucun LLM**,
  une rétention N rapports serait une évolution distincte.
- **Endpoint** (`AuditApiService.cs`) : `GET /Plugins/LLMAI/Audit?Last=true` — lecture
  seule du dernier rapport persisté **sans exécuter d'audit** (zéro LLM), toujours
  admin-only. Un run réussi persiste le rapport **avec son mode, son focus et sa
  date** ; les messages d'échec de `RunAuditAsync` (« Aucun backend configuré… »,
  « Échec de l'audit… ») ne peuvent **jamais** écraser le dernier vrai rapport.
- **Page de config** (`config.js`, `i18n.js`) : au chargement de la page, la zone
  rapport affiche par défaut le dernier rapport persisté avec la méta
  « Dernier rapport persisté — [date locale] (mode …) » ; le bouton « Lancer l'audit »
  régénère et écrase. La date du run est désormais formatée localement
  (`toLocaleString`) au lieu de l'ISO brut. Description de la section mise à jour
  (FR + EN).
- Version → 1.13.10.0.

## [1.13.9.13] — 2026-09-10

### Fixed — Rapports d'audit : Markdown pur exigé, filet de formatage, ligne UPnP toujours présente

- **Artéfacts de restitution corrigés** : le LLM émettait de la notation math LaTeX
  (`$\rightarrow$` — le `\r` avalé comme retour chariot produisait
  « Dashboard $ ightarrow$ Réseau ») et du HTML cru (`809<code>96</code>`). Le filet
  **`LlmRunner.SanitizeReport`** remplace les flèches LaTeX (`\rightarrow`, `\to`,
  `\Rightarrow`, `\leftarrow`) par leur glyphe texte, retire les dollars de math-mode
  résiduels, dénude les balises d'habillage (`code`/`b`/`strong`/`i`/`em`) et convertit
  les `<br>` — appliqué aux sorties **audit** (boucle agent + mode déterministe) **et
  chat**. Best-effort, n'élève jamais, insensible aux montants en dollars.
- **Règle « Markdown pur » dans les prompts** (`LlmRunner.cs` — `AUDIT_WORKFLOW` §7 et
  `AUDIT_SYNTHESIS_WORKFLOW` ; `DefaultPrompts.cs` — `auditPrompt` FR + EN) : jamais de
  notation math/LaTeX, jamais de balises HTML, flèches en « → » texte simple.
- **Ligne UPnP toujours présente** : le workflow exige désormais un constat ✅ explicite
  (« UPnP désactivé / aucun mapping routeur ») quand `upnp_check` ne trouve aucun
  mapping — la sonde ne peut plus passer sous silence dans le rapport.
- Version → 1.13.9.13.

## [1.13.9.12] — 2026-09-09

### Added — Liens profonds Emby dans le chat : titres cliquables vers la fiche

- **Injection côté serveur** (`LlmRunner.cs`) : un bloc `### LIENS PROFONDS
  EMBY` est ajouté au workflow du chat. Il donne au LLM le gabarit exact
  `[Titre](/web/index.html#!/item?id=ID&serverId=…)` — le `serverId` est
  obtenu une seule fois via `GetPublicSystemInfo` et mis en cache (bloc omis,
  fail-open, si l'info est indisponible) — et ajoute `&asSeries=true` pour la
  vue groupée des séries. Règle explicite : sans id connu, le titre est cité
  SANS lien — jamais d'id inventé.
- **Rendu côté page** (`chat.js`) : la fonction `inline()` rend les liens
  Markdown `[texte](url)` avec deux garde-fous — même origine uniquement
  (l'URL doit commencer par `/` ; tout lien absolu proposé par le LLM reste
  du texte brut, pas de redirection contrôlée par le modèle) et
  `target="_blank" rel="noopener noreferrer"` (nouvel onglet, demande
  explicite de l'usager).
- **Données : aucune modification** — les projections des outils émettaient
  déjà un `id` partout, bibliothèque (`i.InternalId.ToString()`) comme EPG
  (`p.Id` des DTO de `GetPrograms`) ; la forme EPG est la même que celle
  validée en production par `EpgLink` (liens des NFO de la bibliothèque
  .strm). Le clic ouvre la fiche Emby où l'usager peut mettre en favori ou
  enregistrer selon le cas.
- Version → 1.13.9.12 (bust du cache client : `chat.js` a changé).

## [1.13.9.11] — 2026-09-09

### Fixed — La révision proposée par le chat arrive en bloc ```text, sans rappel

- **Règles de révision réellement injectées** : le bloc de règles communes
  (`ChatContexts.CommonRules` — livraison du texte complet, read-modify-write,
  conventions de rédaction, langues, sauvegarde, mode exclusif) était défini
  mais jamais injecté dans le system prompt ; le LLM ne l'avait jamais vu et
  l'usager devait lui rappeler le format à chaque tour. Il est maintenant
  appendé en tout dernier du bloc contexte (effet de récence).
- **Cadrage « canal de livraison »** (pattern repris des directives admin de
  llm_core) : le bloc clôturé ```text n'est plus présenté comme une préférence
  de format mais comme le SEUL canal par lequel le texte révisé parvient à
  l'interface (même mécanique qu'une commande bash proposée dans un bloc) ;
  la confirmation porte sur l'EXÉCUTION du bloc déjà livré (carte de diff
  Approuver/Refuser), jamais sur sa production — le modèle ne demande plus
  « voulez-vous que je prépare le texte ? » avant de livrer.
- **Filet structurel côté serveur** : en mode d'édition, une réponse sans
  AUCUNE clôture ET qui se termine par une question (pattern exact de la
  déférence) déclenche UN nudge automatique rejoué au LLM (« livrez
  MAINTENANT le texte complet en bloc ```text ») — seule la réponse corrigée
  part à la page, l'usager ne voit rien. Un seul nudge par tour ; réponse
  d'origine rendue si le nudge échoue (le bouton de secours de la page reste
  le dernier ressort). `ChatPromptStore.PeekPagePending` ajouté pour
  consulter la proposition en attente sans la consommer.
- Description du tool `plugin_prompts` alignée sur le même cadrage (double
  ancrage : les descriptions d'outils sont fortement pondérées par les petits
  modèles).

## [1.13.9.0] — 2026-09-09

### Changed — Édition de prompts par le chat : garde-fous et ergonomie (1.13.9.0 → 1.13.9.8)

- **Validation croisée champ ↔ mode** : la sauvegarde (`plugin_prompts` set)
  ne peut viser que le prompt du mode d'édition actif de la liste déroulante —
  la confusion vécue (texte « tâche séries » soumis pour le champ RAG sans que
  rien ne le signale) devient impossible côté serveur.
- **Avertissement de divergence** sur la carte de diff : recouvrement lexical
  mot à mot < 25 % avec le texte courant → bandeau « diffère fortement ».
- **Durcissement du tool-calling** (vécu qwen2.5:14b) : le system prompt
  montre le formulaire d'appel enveloppé ` [{"tool":"…","arguments":{…}}]` par
  outil et un contre-exemple explicite (un objet JSON nu n'est JAMAIS une
  réponse valide).
- **Page chat** : les blocs clôturés ``` sont rendus en conteneur avec boutons
  Copier / Sauvegarder ; le clic 💾 embarque le texte de SON bloc dans le
  message (autoporteur, plusieurs propositions sans ambiguïté) ; si la
  réponse arrive en prose, un bouton de secours au niveau du tour demande la
  réémission en bloc ; l'annonce automatique au changement de mode ([Admin])
  ancre le read-modify-write dès l'entrée.

## [1.13.8.0] — 2026-09-09

### Added — Édition des prompts du plugin par le chat (contextes d'édition)

- **Cinq contextes déroulants** (`ChatContexts.cs`, portage du pattern
  « contextes » de llm_core) : un mode par prompt éditable — Directives RAG,
  Tâche séries, Tâche films, Run « ce soir », Audit santé. Le bloc injecté à
  CHAQUE tour porte le guide d'édition (rôle, invariants, conventions), le
  TEXTE COURANT du prompt (source de vérité read-modify-write) et la langue
  cible résolue côté serveur. Changer de mode n'exige jamais de réinitialiser
  la conversation.
- **Tool `plugin_prompts`** (list/get/set) : écriture **two-phase** — le set
  ne fait que sérialiser la proposition (`ChatPromptStore`, expiration 10 min,
  une par conversation) ; elle n'est appliquée qu'au clic « Approuver » de
  l'admin sur la carte de diff de la page, en C# déterministe. Le LLM n'a
  AUCUN chemin d'écriture direct. Opt-in explicite (`ChatPromptsEnabled`,
  défaut false) ; les modes d'édition restent en lecture sans ce flag.
- **Cinq prompts éditables dans la page de configuration** (textarea) avec
  bouton « Réinitialiser » : l'endpoint `/Plugins/LLMAI/DefaultPrompts`
  installe la version propre dans la langue de réponse configurée (repli
  langue d'affichage) — source de vérité unique FR/EN (`DefaultPrompts.cs`).
- Nouveaux fichiers : `ChatContexts.cs`, `ChatPromptStore.cs`,
  `ChatPromptsTool.cs`.

## [1.13.7.0] — 2026-09-08

### Added — Audit sécurité et diagnostics étendus (system_audit)

- **`security_check`** : validation consolidée — mots de passe des comptes
  (admins surtout), accès distant et HTTPS, ouverture de ports automatique
  (UPnP), en-têtes proxy (X-Forwarded-For), IP publiques ; constats gravés
  avec gravité et correctifs, repris tels quels dans le rapport.
- **`upnp_check`** : sonde SOAP du routeur (lecture seule) — confirme l'état
  réel de l'ouverture automatique des ports.
- Nouvelles actions de diagnostic : `system_config`, `processes`,
  `library_stats`, `missing_metadata`.
- **Prompt d'audit éditable** (`AuditPrompt`) : injectable/orientable par la
  page de configuration ; les garde-fous de remédiation ne dépendent PAS de
  ce texte — ils vivent dans le workflow d'audit (system prompt) et la gate
  `AuditRemediationEnabled`.

## [1.13.6.0] — 2026-09-08

### Added — Repli poster EPG via l'endpoint image d'Emby (toute source de guide)

- Le repli poster de la bibliothèque `.strm` récupère à nouveau les affiches
  EPG sans fichier local — mais **uniquement via l'endpoint image d'Emby**
  (`/emby/Items/{id}/Images/Primary`), le même chemin que l'affichage EPG.
  Le plugin ne contacte **que l'origine Emby locale** : jamais l'hôte distant
  de l'affiche (les requêtes directes au fournisseur d'images de guide sont
  facturées — demande de son éditeur). Aucun domaine n'apparaît dans le code.
- Fonctionne pour toute source de guide : fichier local copié comme avant ;
  sinon Emby sert la variante redimensionnée depuis son cache disque ou la
  récupère lui-même (testé avec une source XMLTV, affiches servies par le CDN
  du tuner).
- Volume borné : `poster.jpg` déjà présent → retour immédiat (une requête
  réseau au maximum par programme sur la vie du disque) et un cap de
  10 récupérations par génération. Échec → poster par défaut embarqué.
- `StrmLibraryGenerator.TryCopyProgramPoster` devient
  `TryWriteProgramPosterAsync` (async, annulation propagée).

## [1.13.5.0] — 2026-09-08

### Fixed — Repli poster EPG : plus aucun appel direct à l'hôte distant

- Les URL distantes des affiches EPG pointent vers le CDN d'un fournisseur
  d'images de guide (requêtes facturées) : le plugin ne les appelle jamais
  directement, sur demande de l'éditeur d'Emby.
- `StrmLibraryGenerator.TryCopyProgramPoster` ne télécharge **plus jamais**
  d'URL distante : les affiches EPG distantes (URL distante dans le champ
  Path de l'image Primary) sont ignorées ; le **poster par défaut embarqué**
  (même ressource que `DefaultImageApplier`) est posé à la place pour que la
  carte ne reste pas sans image. Les affiches en **fichier local** (cache
  disque Emby) restent copiées — aucun réseau.
- Concerne la génération planifiée **et** le tool chat `create_card`
  (chemin commun `WriteCardWithMetaAsync`). Aucun autre composant du plugin
  ne sollicite le fournisseur d'images (vérifié par recherche de code).

## [1.13.4.4] — 2026-09-06

### Changed — Lien documentation avec ancre GitHub

- Le lien de la page de config ouvre maintenant
  `https://github.com/reneboulard/LLM_AI#llm_ai--plugin-emby-de-recommandations-par-llm`
  — la page GitHub s'affiche avec le bloc « Une recommandation, plusieurs
  sorties » (les 7 sorties natives) visible d'emblée.
- Diagnostic confirmé (Firefox vs Chrome) : le serveur sert le bon contenu ;
  le symptôme « clé brute » venait du cache HTTP de Chrome. À débloquer une
  seule fois : DevTools (F12) → clic droit sur le bouton recharger →
  « Vider le cache et effectuer un rechargement physique ». Ensuite le bust
  par session (v1.13.4.3) évite toute récurrence.

## [1.13.4.3] — 2026-09-06

### Fixed — Cache navigateur : bust par session des ressources plugin

- Constat : les ressources `web/ConfigurationPage` sont servies avec
  `Cache-Control: public`, sans `max-age` ni `Last-Modified`, et un ETag **non
  dérivé du contenu** (même ETag pour deux ressources de contenus différents —
  vérifié par md5). La revalidation peut donc répondre `304 Not Modified` sur un
  contenu périmé : le navigateur peut garder un `i18n.js` d'avant-déploiement
  indéfiniment (symptôme : clé brute « cfg.docs.link » affichée sur la page de
  config), et même survivre à un hard-reset selon le contexte.
- **1re couche** : `config.js` / `chat.js` / `recommendations.js` chargent les
  ressources dont ils ont le contrôle (`LLMAII18n`, `LLMAIAssetVersion`,
  `LLMAIBg`) avec un paramètre `?v=` de bust **par session navigateur** (jeton
  `sessionStorage`) → lecture réseau garantie à chaque nouvelle session, sans
  dépendre de l'ETag.
- **Couche de recours** : le self-heal de `asset_version.js` détecte désormais
  un déploiement par **version serveur mémorisée en sessionStorage** (l'ancien
  critère « VERSION gravée ≠ serveur » ne fonctionnait plus une fois le module
  lui-même busté) ; bandeau « reload dur » retiré (mort avec le bust).
- Conséquence pratique : après chaque déploiement, **une nouvelle session
  navigateur suffit** (rechargement de l'onglet ou fermeture/ouverture) — le
  Ctrl+Shift+R n'est plus nécessaire.

## [1.13.4.2] — 2026-09-06

### Fixed — Libellé du lien documentation FR/EN inversé

- Les valeurs FR/EN de `cfg.docs.link` étaient échangées entre les deux blocs de
  langue (libellé anglais affiché en français et réciproquement).
- Rappel diagnostic : si la page affiche la clé brute « cfg.docs.link », c'est un
  `i18n.js` en cache (précédente version) — hard-reset de la page après déploiement.

## [1.13.4.1] — 2026-09-06

### Added — Lien documentation + intro « une recommandation, plusieurs sorties »

- La page de configuration affiche un lien **« 📖 Documentation complète »** vers
  `https://github.com/reneboulard/LLM_AI` (nouvel onglet) sous le titre — la doc
  détaillée vit dans le repo GitHub (README FR/EN, CHANGELOG), la page n'embarque
  qu'un lien (une ligne, pas de logique).
- Le README (FR/EN) s'ouvre désormais sur le bloc **« Une recommandation, plusieurs
  sorties »** : tableau des 7 sorties opt-in par intention (regarder / regrouper /
  filtrer / marquer / enregistrer / supprimer), pour choisir en config.
- README : version d'en-tête rafraîchie (1.13.4.1).

## [1.13.4.0] — 2026-09-06

### Added — Libellés d'actions dans la page de chat

- Les toasts Emby (`DisplayMessage`) **ne sont pas rendus par le client web** sur
  la page de configuration (constat 2026-09-06 : livraison OK, affichage
  seulement après avoir quitté la page). L'admin n'avait donc pas de retour
  visuel immédiat quand une action de chat réussissait.
- Correction : le serveur retourne désormais dans la réponse HTTP un champ
  `actions` (libellés des actions **réussies** du tour, mêmes textes que les
  toasts), et la page de chat affiche sous la réponse une ligne discrète par
  action — ex. « 🤖 1 item(s) tagué(s) « AI Tonight » ».
- Success-only (identique au toast) ; pas rejoué par l'historique (l'info reste
  dans le texte du LLM). Indépendant du client Emby → visible instantanément.
- Le toast serveur reste actif (utile hors chat : autre onglet, autre appareil).
- Implémentation : `ChatActions.TurnActions` (collecteur par tour, vidé à
  `BeginTurn`, thread-safe) rempli par `ToastActionAsync` ; `ChatResponse.Actions` ;
  rendu `chat.js` + style `chat.html`.

## [1.13.3.0] — 2026-09-06

### Changed — Marqueurs « AI Tonight » / « AI Delete » : genres → tags

- Les deux marqueurs d'admin du plugin sont désormais des **tags** Emby et non plus des
  genres : « AI Tonight » (recos watch bucket) et « AI Delete » (suggestions de suppression
  disque). Un tag est plus adapté à un marqueur : il n'encombre ni la navigation par genres,
  ni les filtres de genre des clients, ni le vocabulaire du genre cleaner — et il reste
  filtrable dans tous les clients (filtre « Tags »).
- Nouveau `AiTagger` (remplace `AiGenreTagger`) : ajout/retrait via `item.Tags`
  (`InternalItemsQuery.Tags` pour les requêtes). Le tool chat `tag_ai_tonight` pose le tag
  (texte inchangé : « tagué »).
- **Migration automatique** : `AiTagger.RemoveAllAsync` interroge le filtre `Tags` ET le
  filtre `Genres` du même nom, et retire les deux en une persistance — le nettoyage de 3 h
  (`AiTonightCleanupTask`) et le clear-first de chaque passe disque migrent les items encore
  étiquetés par l'ancien genre au fil de leurs passages, sans action manuelle.
- Libellés mis à jour (config page FR/EN, descriptions de tâche planifiée, notification
  disque, tool chat) : « genre » → « tag ». Les noms de propriétés de configuration sont
  inchangés (`TonightGenreTagEnabled`, `RecordingTaggingEnabled`) — aucune perte de valeur
  sauvegardée.
- Correctif de câblage v1.13.2 : `PlaylistAddTool` reçoit bien `IServerApplicationHost`
  (nécessaire à la normalisation next-up de `AiTonightPlaylistManager.AddItemsAsync`).

## [1.13.2.0] — 2026-09-06

### Corrigé / Fixed

- **Playlist « AI Tonight » : accumulation de doublons (403 entrées, 73
  titres ×6) éliminée**. Deux quirks de l'API playlist Emby 4.9.5.0
  rendaient le « reset » de `EnsureAsync` inopérant (vérifiés
  empiriquement 2026-09-06) : `RemoveFromPlaylist` ne retire **rien** (no-op
  interne, HTTP 500 SQLiteException via REST `Ids=…`, 204 sans effet via
  `EntryIds=…`) ; une série/saison ajoutée à une playlist est **développée
  en tous ses épisodes** (un id série → 52 entrées). Chaque run ajoutait
  donc ~50 entrées et n'en retirait jamais.
  - `EnsureAsync` → **déstruction + recréation** de la playlist
    (`ILibraryManager.DeleteItem` + `DeleteFileLocation` — le .m3u aussi)
    + `CreatePlaylist` avec les recos fraîches : contenu exact garanti,
    id de coquille changé à chaque run (sans importance, retrouvée par
    nom). `ClearAsync` (cleanup 3 h) → suppression de la coquille.
  - Recos normalisées en **feuilles** : une reco série/saison devient son
    **épisode « next up » non vu** (usager Tonight) via
    `ITVSeriesManager.GetNextUp` — cohérent avec Emby qui propose le
    prochain épisode en fin de lecture ; aucun next up (tout vu) → série
    sautée.
- **Tools chat `playlist_add`/`playlist_remove`** — même hygiène :
  déduplication contre les entrées courantes (un item déjà présent ne
  consomme pas le budget), comptage **honnête** par re-listing après
  l'appel (un id non appliqué par Emby n'est ni compté ni retracé),
  `playlist_remove` rapporte l'échec du retrait sur ce build et ne
  réessaye pas.

---

## [1.13.1.0] — 2026-09-06

### Ajouté / Added

- **Toast de traçabilité des actions du chat** (`ChatActions.ToastActionAsync`)
  — chaque action de chat réussie (timer, carte .strm, tag, collection,
  playlist, run Tonight) émet un toast Emby discret (préfixe 🤖, timeout
  8 s) vers **toutes les sessions admin** (`SendMessageToAdminSessions` /
  DisplayMessage). Succès seulement : pas de toast pour les refus de
  garde-fous (le texte du chat les explique) — le nombre de toasts reflète
  donc le budget consommé. Best-effort, jamais bloquant.
- Test v1.13 en conditions réelles (chat admin navigateur) : propose →
  confirmer → exécuter vérifié de bout en bout sur `record_program` (timer
  série créé puis nettoyé) et `create_card` (.strm + .nfo + poster + marker
  `.llmai_reco` vérifiés sur disque), `collection_add`/`collection_remove`
  (10 ajoutés, 10 retirés ; garde de retrait : ids non ajoutés par le chat
  exclus silencieusement, auto-refus du modèle pour un item jamais ajouté).

---

## [1.13.0.0] — 2026-09-06

### Ajouté / Added

- **Couche d'action du chat** (`ChatActions.cs`, nouveau) — le chat admin
  obtient des outils d'action qui réutilisent les **mêmes primitives que le
  plugin** (aucune nouvelle logique métier) : `record_program` (timer via
  `AutoProgrammer.ProgramOneAsync` — garde-fous owned/watched/drop list/dedup
  inclus), `create_card` (carte .strm unique via le chemin commun
  `WriteCardWithMetaAsync` de `StrmLibraryGenerator` — éphémère par
  construction : le marker `.llmai_reco` la fait nettoyer par Emby à la
  prochaine génération planifiée), `tag_ai_tonight` (genre via
  `AiGenreTagger`), `collection_add`/`collection_remove` et
  `playlist_add`/`playlist_remove` (primitives **additives** nouvelles dans
  les managers — contrairement à `EnsureAsync` qui rapproche tout le
  contenu ; le retrait n'accepte que les items que le chat a ajoutés
  lui-même dans la conversation).
- **Budget d'actions** : config `ChatActionBudget` (défaut 10, par tour,
  toutes surfaces confondues) + `ChatActionConversationCap` (défaut 30, par
  conversation, compteur en mémoire serveur). `0` = chat en lecture seule
  (comportement pré-v1.13). Consommation **au succès** : une action refusée
  par un garde-fou ne consomme rien ; un lot refusé en bloc ne s'exécute pas
  partiellement. Refus typé renvoyé au modèle.
- **Étiquette de confirmation** (niveau prompt, bloc « ACTIONS EMBY
  DISPONIBLES » du system prompt) : l'agent propose dans son texte et
  attend une confirmation explicite de l'admin avant d'exécuter — l'admin
  reste le maillon humain ; le budget + le gating admin sont les garde-fous
  durs.
- **`run_tonight_run(directives?)`** (opt-in `ChatTonightRunEnabled`, défaut
  false) : le chat déclenche le run « À regarder ce soir » sur le chemin
  exact de la tâche planifiée et du login
  (`TonightService.GenerateTonightAsync`) — cache, profil, EPG, surfaces
  configurées (genre/collection/playlist/favoris), décisions. Directives de
  session **éphémères** (≤ 500 caractères, injectées dans le prompt de ce
  run uniquement, jamais persistées). Limites : un seul run chat à la fois,
  2 par conversation.
- **Origin « chat »** : runId préfixé `c` (decisions.json diagnosable) et
  badge discret « générée via chat — directives : … » sur la page
  Recommandations (la métadonnée survit au cache par usager).

### Modifié / Changed

- `TonightService.GenerateTonightAsync` : paramètres optionnels
  `sessionDirectives`/`fromChat` (injection one-shot dans le prompt, runId
  `c…`, `TonightResult.ViaChat`/`ChatDirectives` portés par le cache) ;
  `ResolveTonightUser` devient `internal static`.
- `TonightApiService` : cache consulté via `TryGetCachedResult` (badge
  via_chat servi aussi depuis le cache).
- `LlmRunner.RunChatAsync` : paramètres optionnels `extraTools`/`extraWorkflow`
  (aucun autre chemin affecté).
- `ChatApiService` : DI `ICollectionManager`/`IPlaylistManager`, ouverture du
  tour (`ChatActions.BeginTurn`), construction des outils d'action si budget
  &gt; 0.
- Page config (section Chat) : budget par tour, plafond par conversation,
  opt-in run Tonight ; i18n FR/EN ; badge Recommandations.

---

## [1.12.0.0] — 2026-09-06

### Ajouté / Added

- **Retour visuel des cartes .strm** (`ActivateFeedback.cs`, nouveau) — lire
  une carte de la bibliothèque « AI Suggestions » ne se termine plus en
  silence :
  - **Toast Emby** au client qui lit la carte (mécanique
    `DisplayMessage`/`SendMessageCommand` éprouvée de TonightLoginService ;
    session retrouvée par le chemin du `.strm` en cours de lecture ;
    **aucune notification cloche**) — succès (« Enregistrement programmé »),
    déjà programmé, échec, ou disque d'enregistrements plein (gate v1.11),
    textes FR/EN via I18n ;
  - **Suppression de la carte en cas de succès** : après ~60 s (fin de la
    lecture du clip), le plugin demande à **Emby** de supprimer l'item carte
    par son chemin (`FindByPath` → `DeleteItem` avec `DeleteFileLocation`) —
    l'item ET le fichier `.strm` disparaissent de la bibliothèque ; le plugin
    ne supprime JAMAIS de fichier lui-même. Échec ou disque plein → carte
    conservée (réessayable). Constaté sur ce serveur : Emby retire le **dossier
    de carte entier** (`.strm` + `.nfo` + marker + poster) — rien à nettoyer.
- L'URL `.strm` embarque désormais l'identité de la carte (`card=<dossier>`)
  — les cartes générées avant v1.12 (sans `card`) fonctionnent toujours,
  sans toast ni suppression. Anti-doublon : une même lecture génère plusieurs
  GET à Activate (sonde ffmpeg, requêtes Range) — seul le premier déclenche
  le feedback (cache TTL 5 min).

## [1.11.0.1] — 2026-09-06

### Corrigé / Fixed

- **Chemin d'enregistrements par défaut** : quand aucun chemin n'est configuré
  dans Emby (`RecordingPath`/`MovieRecordingPath`/`SeriesRecordingPath` vides),
  le gate disque et la passe de tag replient sur le défaut Emby constaté
  `<ProgramData>/data/livetv/recordings` (ex. `/var/lib/emby/data/livetv/recordings`)
  au lieu d'être inertes. (Les options LiveTV restent lues via la configuration
  nommée `livetv` — pas sur `ServerConfiguration`.)

## [1.11.0.0] — 2026-09-06

### Ajouté / Added

- **Seuil disque du dossier d'enregistrements** (`RecordingDiskManager.cs`,
  nouveau) — deux garde-fous non destructifs :
  - `RecordingDiskThresholdGb` (int, défaut 25, `0` = off) : sous le seuil
    d'espace libre du volume d'enregistrements, la création de **nouveaux**
    timers est suspendue (`AutoProgrammer.Program` + endpoint Activate) —
    timers existants inchangés, notification Emby, gate **fail-open** ;
  - `RecordingTaggingEnabled` (opt-in) : au franchissement du seuil, les
    enregistrements **visionnés** sont tagués « AI Delete », du plus ancien au
    plus récent (tailles réelles), jusqu'à couvrir le déficit (×1.2 de marge).
    **Le plugin ne supprime jamais de fichier** — suggestion que l'usager
    concrétise (filtre genre + multi-sélection + suppression). Clear-first :
    chaque passe retire les tags précédents ; sonde quotidienne à 3 h (tâche
    de nettoyage nocturne) pour le reset sans événement auto-program.
- Page config : champ « Seuil disque enregistrements (Go) » + case
  « Suggérer à supprimer… » (FR/EN) dans la section auto-programmation.

## [1.10.0.0] — 2026-09-05

### Ajouté / Added

- **Mémoire de conversation du chat** (opt-in `ChatMemoryEnabled`, défaut
  off) — reprendre le dernier chat, et raffiner les goûts de l'usager :
  - `ChatMemoryStore.cs` (nouveau) — store `chat_memory.json` : par usager,
    les 5 dernières sessions (rétention 30 jours), chacune avec ses tours
    verbatim (plafonnés), un total, et le **résumé de session** (≤ 1500
    caractères) ;
  - **Condensation paresseuse** — la session précédente est résumée
    (UN appel LLM sans outils) en tâche de fond au retour de l'usager
    (ouverture de la page chat ou première conversation), jamais pendant la
    conversation (zéro coût par tour) : note de continuité « Goûts exprimés
    / Faits utiles / Fil ouvert » + une ligne `SIGNALS:` (tableau JSON des
    signaux de goût, parsing tolérant — absent = ignoré, fail-open) ;
  - **Signaux de goût → boucle réflexive** : chaque signal (titre, raison,
    +/−) est journalisé comme décision `kind="chat"` dans `decisions.json`
    (dédoublonné 7 jours, gated `DecisionLogEnabled`) — la révision hebdo
    de la fiche mémoire (`MemoryTask`) les voit déjà ; plus grand trou de
    traçabilité de la v1.9 comblé ;
  - **Injection** : résumé de la session précédente + derniers échanges
    verbatim accolés au workflow de chat (après la fiche mémoire ; la
    session courante est exclue — son contenu arrive via l'historique
    rejoué par la page) ; jetable (le résumé suivant le remplace) ;
  - **Page chat** : bannière « Conversation du {date} — {n} échanges » +
    bouton **« Reprendre »** (restaure les derniers échanges et la session) ;
    le bouton « Effacer la conversation » oublie aussi la session
    (`POST /Plugins/LLMAI/ChatMemory/Forget`, best-effort) ;
  - **Endpoints admin** : `GET /Plugins/LLMAI/ChatMemory` (session la plus
    récente) et `POST /Plugins/LLMAI/ChatMemory/Forget` ; le `SessionId`
    voyage dans `ChatRequest`/`ChatResponse` ;
  - opt-in, fail-open partout : store absent → chat sans mémoire, inchangé.

## [1.9.0.0] — 2026-09-05

### Ajouté / Added

- **Mémoire réflexive — Phase 3 : la fiche mémoire du LLM** (le concept est
  désormais complet : données → réflexion → réinjection) :
  - `MemoryCard.cs` (nouveau) — store `memory_card.json` (répertoire de
    config du plugin) : fiche courante (version, date, texte Markdown
    plafonné ≈ 250 mots) + **historique immuable des 4 versions
    précédentes** (le contrepoids anti-dérive : un LLM qui réécrit sa
    mémoire chaque semaine peut s'auto-convaincre en boucle) ;
  - `MemoryTask.cs` (nouvelle tâche planifiée, dimanche 4 h 30 — après
    l'analyse hebdo classique) : joint en C# (zéro LLM pour la jointure) les
    événements bruts de la semaine — décisions × télémétrie de lecture
    (rejet immédiat / abandon / partiel / validé, % du direct via la durée
    de diffusion du snapshot EPG) × **calibration des versions de fiche**
    (les recos émises sous v3 ont-elles mieux marché que celles sous v4 ?) ×
    candidats écartés des pools (mauvaise reco vs erreur de classement) ×
    vu-sans-recommandation (titres résolus côté C#) × créneaux de lecture —
    puis **un seul appel LLM sans outils** réécrit la fiche (reprise de
    l'actuelle obligatoire, sections imposées, auto-évaluation des croyances
    ratées, nuance signal faible/fort, ≤ 250 mots) ; version++ à la
    sauvegarde, fail-open (échec LLM → fiche précédente conservée) ;
  - **Injection** (3 sites) : quand la fiche est active et renseignée, elle
    **remplace** la directive de la boucle de rétroaction classique —
    prompts Tonight (`TonightService`), prompts d'enregistrement
    (`LlmScheduledTask`), workflow de chat (`LlmRunner.RunChatAsync`) ;
    fiche vide/absente → repli transparent sur la directive classique ;
  - **Opt-in** `MemoryCardEnabled` (défaut off ; requiert les stores
    décision/télémétrie — sans données, pas de révision) ;
  - **Consultation / édition admin** : `GET`/`POST
    /Plugins/LLMAI/MemoryCard` (réservés admin) + zone sur la page de
    configuration (version/date/nb de versions conservées, texte éditable
    pour un correctif manuel, bouton « Enregistrer la fiche » — version et
    historique inchangés côté serveur) ;
  - lectures des stores exposées (`DecisionStore.ParseAllDecisions /
    ParseAllPools / ParseAllPlayback`) pour la jointure de la tâche.

## [1.8.0.0] — 2026-09-05

### Ajouté / Added

- **Mémoire réflexive — Phase 2 : snapshot EPG** — `EpgSnapshotStore.cs`
  (nouveau, fichier `epg_snapshot.json` dans le répertoire de config du
  plugin) : les métadonnées EPG éphémères sont **figées** dès l'émission au
  LLM ou la programmation d'un timer — un programme diffusé disparaît de la
  base Emby, sans snapshot tout ce qu'on en savait est perdu :
  - écrit aux sites de capture existants (`epg_tonight` / `epg_series` /
    `epg_movies`) : titre, titre d'épisode, synopsis (≤ 300), chaîne, début de
    diffusion, **durée de diffusion** (minutes — c'est le dénominateur du % du
    direct), genres **normalisés** (GenreCleanerMap — nomenclature commune
    bibliothèque), année, flags série/film ;
  - `AutoProgrammer` marque les programmes programmés (`timer=true`, entrée
    minimale créée si le programme n'a jamais passé un outil EPG) ;
  - le croisement avec la bibliothèque (providerIds, item enregistré) est
    résolu de façon **déterministe à l'analyse** (Phase 3) plutôt qu'en
    temps réel — aucun hook runtime, aucun coût caché ;
  - opt-in `DecisionLogEnabled` (même store opt-in que les décisions) ;
    rétention 90 jours (une saison reste joignable), plafond 3000, fail-open.

### Corrigé / Fixed

- `PlaybackWatcher` (v1.7.0.0) : using manquant
  (`MediaBrowser.Controller.Session`) — la DLL v1.7.0.0 ne compilait pas
  réellement (le build incrémental l'avait masqué) ; rebuild propre vérifié.

---

## [1.7.0.0] — 2026-09-05

### Ajouté / Added

- **Mémoire réflexive — Phase 1 : les données de la réflexion** — deux options
  opt-in dans une nouvelle section de config « Mémoire réflexive
  (expérimental) ». Ce sont les stores qui alimenteront l'auto-évaluation du
  LLM (la fiche mémoire rédigée par le LLM arrive en Phase 3) :
  - **Journal de décisions** (`DecisionLogEnabled`) — `DecisionStore.cs` :
    - `decisions.json` : chaque reco émise (kind `tonight` / `record` / `drop`)
      avec la **raison** du LLM (champ `reason` de son tableau JSON — produit
      depuis toujours, jusqu'ici perdu), la priorité, l'itemId Emby ou le
      programId EPG, l'usager, et la **version de la fiche mémoire** en
      vigueur au moment de la décision (`mv`, 0 tant que la fiche n'existe
      pas) — la clé de la calibration (réviser une croyance, pas un titre) ;
    - `run_pool.json` : le **menu de candidats** soumis au LLM à chaque run
      (EPG émis par `get_emby_info` — capture dans les actions `epg_tonight` /
      `epg_series` / `epg_movies` —, réserve bibliothèque, enregistrements non
      visionnés) — permet à l'analyse de distinguer une mauvaise reco d'une
      **erreur de classement** (le gagnant ignoré, un écarté regardé) ;
    - carry-forward : au premier usage, les entrées du journal `RecoLog`
      (config XML) sont migrées dans `decisions.json` ; `RecoLog` reste en
      **double écriture** (l'analyse hebdo `RecoAnalysisTask` le lit encore —
      la Phase 3 le retirera).
  - **Télémétrie de lecture** (`PlaybackTelemetryEnabled`) —
    `PlaybackWatcher.cs` (nouveau, pattern `IServerEntryPoint`) branché sur
    `ISessionManager.PlaybackStopped` : `playback.json` — item, usager,
    durée réelle, **fraction lue** (signal comportemental : rejet immédiat
    &lt; 5 %, abandon 5–50 %, validé &gt; 80 %), source
    bibliothèque/.strm/direct, chaîne, client, appareil. Le % du direct
    (pas de durée d'œuvre naturelle) reste à dériver via le snapshot EPG
    (Phase 2). % calculé depuis `BaseItem.RunTimeTicks` /
    `MediaSourceInfo` — vérifié par réflexion : `UserItemData` n'expose
    **pas** `PlayedPercentage` sur cette build.
  - Points d'ancrage : `TonightService` (runId + pool + décisions tonight),
    `LlmScheduledTask` (décisions record), `RecosApiService` (drop),
    `GetEmbyInfoTool` (captures EPG via `DecisionStore.ActiveRunId` — le
    tool, global et sans usager, est relié au run par un contexte statique).
  - Rétention 30 jours, plafonds (1000 décisions / 200 pools / 2000 lectures /
    60 candidats par run) ; fail-open de bout en bout.

### Technique / Technical

- `TonightService.IsUnderPath` passe en `internal` (partagé avec
  `PlaybackWatcher` pour l'étiquetage .strm).

---

## [1.6.0.0] — 2026-09-05

### Ajouté / Added

- **Surfaces « personnelles » du watch bucket « À regarder ce soir »** — deux
  nouvelles options opt-in dans la section Tonight, parallèles au genre et à la
  collection existants (les quatre coexistent, chacune indépendante) :
  - **Playlist « AI Tonight »** (`TonightPlaylistEnabled`) — maintenue via
    `IPlaylistManager` : créée avec les recos du watch bucket au premier run,
    puis **reset à chaque run frais** (toutes les entrées retirées, recos du
    jour ajoutées — la playlist reflète exactement les recommandations du
    jour). Publique (visible par le foyer), liée à l'usager configuré. Signatures
    `IPlaylistManager` vérifiées par réflexion sur `MediaBrowser.Controller.dll`
    de cet hôte : `CreatePlaylist(PlaylistCreationRequest)`,
    `AddToPlaylist(Playlist, long[], skipDuplicates, User, ct)`,
    `RemoveFromPlaylist(playlist, entryIds long[])` — les **entryIds** sont les
    enfants de la playlist, listés via `playlist.GetItemList(...)` (l'override
    `GetItemsInternal` lit le fichier playlist).
  - **Favoris éphémères** (`TonightFavoritesEnabled`) — les recos du watch
    bucket mises en favori (`IUserDataManager.SaveUserData`, raison
    `UpdateUserRating`) pour l'usager configuré : « Ma liste » les montre au
    cours de la soirée. Le set exact posé par le plugin est tracé dans
    `tonight_favorites_state.json` (dossier de configuration du plugin) ; le
    nettoyage nocturne ne revert **que ce set** — un item déjà favori avant le
    run est ignoré (jamais modifié, jamais suivi). Limite documentée : un
    item re-favorisé manuellement par l'usager après notre pose est retourné
    au nettoyage (indistinguable d'un nôtre).
- **Option « usager des surfaces Tonight »** (`TonightUserName`) — usager
  propriétaire des favoris éphémères et de la playlist ; vide = premier usager
  admin (sinon premier usager).
- **Nettoyage nocturne 3 h étendu** (`AiTonightCleanupTask`, toujours exécuté
  même si les flags sont désactivés) : (1) revert des favoris posés par le
  plugin (set du fichier d'état), (2) retrait du genre « AI Tonight »,
  (3) vidage de la collection (coquille conservée), (4) vidage de la playlist
  (entrées retirées, coquille conservée).
- Page de configuration : deux nouvelles cases à cocher + champ usager dans la
  section « À regarder ce soir » (i18n FR/EN).

### Changé / Changed

- `TonightService` reçoit `IPlaylistManager` + `IUserDataManager` (ripple sur
  `TonightApiService` et `TonightLoginService`, seuls sites de construction).

---

## [1.5.1.0] — 2026-09-05

### Corrigé / Fixed

- **Chat : « [object Response] » sur échec de requête** — l'ajax d'Emby
  (`ApiClient.fetch`, non-GET) rejette la **Response brute** pour tout statut
  ≥ 400, et la page chat la stringifyait telle quelle. Vécu : une question
  « Bruce Willis » avortée à 124 s (LLM local lent) → réponse d'erreur
  ServiceStack → « [object Response ] ». La page lit maintenant le statut
  HTTP et le corps d'erreur (JSON ServiceStack ou texte) et affiche
  « HTTP 500 — message » ; `AbortError` (timeout client, borné à 4 min) et
  `TypeError` (connexion coupée) ont des messages dédiés.
- **Chat : requête avortée → 500 ServiceStack** — `ChatApiService.Post`
  attrape désormais l'`OperationCanceledException` : réponse JSON propre
  (« Le LLM n'a pas répondu à temps… ») si le client est encore connecté
  (timeout backend LLM), log simple en cas de déconnexion client.
- **`find` : le tri `recent`/`date_played` (défaut du paramètre `source`) est
  maintenant `library`** — ces tris sont des concepts bibliothèque
  (DateCreated/DatePlayed) ; l'ancien défaut `both` mélangeait les programmes
  EPG dans « vos derniers ajouts » (196 programmes TV aux côtés des 10 films
  récents, le LLM devant se corriger en rappelant `find` avec
  `source=library`). Une demande explicite `source=both` reste honorée.
- **Classifications émises en casse canonique** — le pont Classification
  Mapper renvoyait la clé pliée minuscule (« ca-g ») au lieu du libellé
  canonique du JSON (« CA-G ») ; la valeur d'affichage conserve désormais la
  casse du fichier de configuration.

---

## [1.5.0.0] — 2026-09-05

### Ajouté / Added

- **Action `find` de `get_emby_info` : recherche unifiée bibliothèque + EPG**
  (le LLM fait UNE requête, l'outil fait les appels Emby appropriés) : terme
  libre, types, personne (acteur/réalisateur, résolue via
  `InternalItemsQuery.PersonIds`), genres (vocabulaire harmonisé par le pont
  GenreCleaner — FR curaté et EN brut matchent dans les deux sens),
  classification (normalisée via Classification Mapper), `source`
  `library|epg|both` avec dédup par titre (l'item bibliothèque gagne ;
  en source=epg seul, `owned=true` informatif), filtres `watched`/`favorites`
  per-user (`IsPlayed`/`IsFavorite` sur la query — sans
  `IUserDataManager`, aucun ripple de constructeur), tris récents/année/
  note/nom côté C# et `date_played` côté SQL (`OrderBy` sur la query,
  précédent RecoAnalysisTask). / **`get_emby_info` `find` action: unified
  library + EPG search** (the LLM asks once, the tool makes the right Emby
  calls): free-text term, types, person (resolved via
  `InternalItemsQuery.PersonIds`), genres (harmonized through the
  GenreCleaner bridge — curated French and raw English match both ways),
  classification (normalized via Classification Mapper), `source`
  `library|epg|both` with title dedup (library wins; in epg-only mode
  `owned=true` is informational), per-user `watched`/`favorites` filters
  (`IsPlayed`/`IsFavorite` on the query — no `IUserDataManager`, no
  constructor ripple), recent/year/rating/name C# sorts and `date_played`
  SQL-side (`OrderBy` on the query, following RecoAnalysisTask).

- **Pont `ClassificationMap.cs` (Classification Mapper)** : lecteur paresseux
  de `classification_mapper_config.json` (dossier config du serveur,
  `ConfigurationDirectoryPath`, re-stat mtime 30 s, neutre si le plugin est
  absent) — normalise les classifications officielles hétérogènes
  (« PG-13 », « TV-14 », « 13+ »…) vers les valeurs canoniques maintenues
  dans l'UI Classification Mapper (« CA-14A »…), en miroir du pont
  GenreCleaner pour les genres. / **`ClassificationMap.cs` bridge
  (Classification Mapper)**: lazy reader of
  `classification_mapper_config.json` (server config directory,
  `ConfigurationDirectoryPath`, 30 s mtime re-stat, neutral when the plugin
  is absent) — normalizes heterogeneous official ratings ("PG-13", "TV-14",
  "13+"…) to the canonical values maintained in the Classification Mapper UI
  ("CA-14A"…), mirroring the GenreCleaner bridge for genres.

- **Action `genre_stats` de `get_emby_info`** : profil de goûts —
  occurrences de genres (vocabulaire curaté) dans le vu, les favoris vus et
  les favoris non vus (top 15 par bucket, totaux ; tous les usagers agrégés
  avec dédup par item si `user` absent). / **`get_emby_info` `genre_stats`
  action**: taste profile — genre occurrences (curated vocabulary) among
  watched, favorites watched and favorites unwatched (top 15 per bucket,
  item totals; all users aggregated with per-item dedup when `user` is
  absent).

## [Non publié / Unreleased]

### Ajouté / Added

- **Test d'un backend LLM depuis la page de config** (bouton « Tester » sur
  chaque ligne de serveur LLM, endpoint admin-only
  `POST /Plugins/LLMAI/TestLlm`) : appel rapide (une question-sonde, timeout
  30 s, prompt dans la langue configurée) au backend **tel qu'édité** —
  testable avant enregistrement. Les clés API ne sont pas postées par la
  page : le serveur les relit depuis la config enregistrée (repli variable
  d'environnement). Résultat inline sous l'en-tête de la ligne : OK + latence
  ou message d'échec. / **LLM backend test from the config page** ("Test"
  button on each LLM server row, admin-only endpoint
  `POST /Plugins/LLMAI/TestLlm`): quick call (one probe question, 30 s
  timeout, prompt in the configured language) to the backend **as edited** —
  testable before saving. API keys are not posted by the page: the server
  re-reads them from the saved configuration (environment-variable
  fallback). Inline result under the row header: OK + latency or failure
  message.

- **Bouton « Réinitialiser » pour les directives/prompts de la config**
  (endpoint admin-only `GET /Plugins/LLMAI/DefaultPrompts` +
  `DefaultPrompts.cs`) : restaure la version propre des quatre prompts
  éditables (Directives RAG, tâche Séries, tâche Films, prompt « ce soir »)
  dans la **langue de l'interface** (`?Lang=` forcé, sinon `ResponseLanguage`
  si renseignée, sinon langue d'affichage Emby — volontairement PAS la
  cascade métadonnées qui retomberait sur le legacy `TmdbLanguage`) — une
  installation neuve installe le français, un usager anglophone clique
  Réinitialiser et obtient la directive en anglais. Le « propre » des
  Directives RAG est une **directive de base réelle** (vérifier via les
  outils avant d'affirmer, ne jamais recommander un titre possédé/programmé,
  guidage année de production, explications concrètes) — désormais aussi
  la valeur installée sur les NOUVELLES installations (les installs
  existantes ne changent pas tant que l'admin n'a pas cliqué
  Réinitialiser). Valeur par défaut unique : les initializers de
  `PluginConfiguration` pointent sur `DefaultPrompts.Fr` (plus de texte
  dupliqué). Le bouton remplit le textarea sans enregistrer — l'admin
  clique « Enregistrer » pour appliquer. / **"Reset" button for the config
  directives/prompts** (admin-only endpoint `GET /Plugins/LLMAI/DefaultPrompts`
  + `DefaultPrompts.cs`): restores the clean version of the four editable
  prompts (RAG directives, Series task, Movies task, "tonight" prompt) in
  the **interface language** (forced `?Lang=`, else `ResponseLanguage` if
  set, else Emby display language — deliberately NOT the metadata cascade,
  which would fall back to legacy `TmdbLanguage`) — a fresh install ships
  French, an English user clicks Reset and gets the English directive. The
  clean RAG directives are a **real baseline directive** (verify with the
  tools before asserting, never recommend an owned/scheduled title,
  production-year guidance, concrete explanations) — now also the value
  shipped to NEW installations (existing installs are unchanged until the
  admin clicks Reset). Single source of defaults: the `PluginConfiguration`
  initializers now point at `DefaultPrompts.Fr` (no more duplicated text).
  The button fills the textarea without saving — the admin clicks "Save"
  to apply.

- **Sections repliables de la page de config** : les ~11 titres de section
  deviennent des interrupteurs (chevron, clavier Enter/Espace, aria-expanded)
  qui replient leur contenu ; bouton global « Replier tout / Déplier tout »
  en haut de page ; l'état de chaque section est mémorisé par navigateur
  (localStorage, accès gardé). Le bouton « Enregistrer » reste toujours
  visible (hors sections). / **Collapsible config page sections**: the ~11
  section titles become toggles (chevron, Enter/Space keyboard support,
  aria-expanded) that fold their content; global "Collapse all / Expand all"
  button at the top; each section's state is remembered per browser
  (localStorage, guarded access). The "Save" button stays always visible
  (outside the sections).

- **Boucle de rétroaction des recommandations** (`RecoAnalysisTask` +
  `RecoFeedback` + `LlmRunner.RunSynthesisAsync`, opt-in `RecoFeedbackEnabled`,
  défaut off) : le plugin apprend de ses recommandations passées. Chaque reco
  (« À regarder ce soir », tâche planifiée d'enregistrement) et chaque rejet
  (« Oublier ») est journalisé (`RecoLog`, fenêtre roulante 30 jours / plafond
  500 entrées). La nouvelle tâche planifiée hebdomadaire **« Analyse des
  recommandations »** (dimanche 4 h) rapproche en C# déterministe ce journal des
  visionnages réels de chaque usager — via `IUserDataManager` (regardé / ignoré /
  rejeté / vu-sans-recommandation) — puis fait produire au LLM (un seul appel
  sans outils, repli multi-backend) une **directive concise** persistée
  (`PromptDirectives`, une par usager, ≤ 1200 caractères) et réinjectée dans le
  prompt des runs suivants : par usager pour « À regarder ce soir », fusionnée
  pour la tâche d'enregistrement. **L'admin garde le contrôle** : les directives
  sont affichées et éditables (JSON) dans la page de config ; les vider les
  retire des prompts. **Fail-open** : sans directive ou boucle désactivée, les
  prompts sont inchangés ; un échec d'analyse conserve la directive précédente
  et ne casse jamais un run ; usager sans signal (aucune reco/rejet) sauté.
  **Recommendation feedback loop** (`RecoAnalysisTask` + `RecoFeedback` +
  `LlmRunner.RunSynthesisAsync`, opt-in `RecoFeedbackEnabled`, off by default):
  the plugin learns from its past recommendations. Every reco ("Watch tonight",
  scheduled record task) and every rejection ("Forget") is logged (`RecoLog`,
  30-day rolling window / 500-entry cap). The new weekly scheduled task
  **"Recommendation analysis"** (Sunday 4 AM) deterministically correlates that
  log in C# against each user's actual watch history — via `IUserDataManager`
  (watched / ignored / rejected / watched-without-reco) — then has the LLM
  produce (single tool-less call, multi-backend fallback) a **concise
  directive** that is persisted (`PromptDirectives`, one per user, ≤ 1200
  chars) and re-injected into subsequent run prompts: per-user for "Watch
  tonight", merged for the record task. **The admin stays in control**:
  directives are displayed and editable (JSON) on the config page; clearing
  them removes them from prompts. **Fail-open**: without a directive or with
  the loop disabled, prompts are unchanged; an analysis failure keeps the
  previous directive and never breaks a run; users without signal (no
  reco/rejection) are skipped.

- **Watched-guard « À regarder ce soir »** (`TonightService.BuildWatchedIndex` +
  `ValidateAndFilter` + `GetEmbyInfoTool.EpgTonight`) : une rediffusion EPG d'un
  épisode/ film que l'usager a **déjà visionné** n'est plus recommandée comme du
  contenu neuf (vécu : reco d'un épisode déjà vu, la rediffusion de 19 h masquant
  l'épisode inédit de 21 h). Deux garde-fous déterministes C# :
  **(C) déduplication par titre « meilleure diffusion »** dans `epg_tonight` —
  pour chaque titre, on garde la diffusion au contenu le plus récent (n°
  saison/épisode le plus haut, repli sur la diffusion la plus tardive) au lieu de
  la première heure : la rediffusion ne masque plus l'inédit, seul visible du LLM ;
  **(A) marquage `watched=true`** par la validation du run — index per-usager des
  épisodes joués (clés « s{S}e{E} » + repli nom d'épisode) et des films joués ;
  toute reco live correspondante est **marquée, pas droppée** (le minimum de recos
  reste garanti) : badge « Déjà visionné », actions Programmer/Regarder en direct
  masquées, aucun timer créé (AutoProgrammer), exclue des popups et de la cloche.
  Fail-open total (index indisponible → recos non marquées, jamais vidées) ;
  bibliothèque `.strm` exclue de l'index (anti-circulaire).
  **"Watch tonight" watched-guard** (`TonightService.BuildWatchedIndex` +
  `ValidateAndFilter` + `GetEmbyInfoTool.EpgTonight`): an EPG rerun of an episode/
  movie the user **already watched** is no longer recommended as fresh content
  (experienced: a reco for an already-seen episode, the 7 pm rerun hiding the 9 pm
  premiere). Two deterministic C# guards:
  **(C) "best airing" per-title dedup** in `epg_tonight` — for each title the
  freshest-content airing wins (highest season/episode number, fallback to latest
  start) instead of the earliest: a rerun no longer crowds out the premiere, the
  only one the LLM could see;
  **(A) `watched=true` marking** at run validation — per-user index of played
  episodes ("s{S}e{E}" keys + episode-name fallback) and played movies; any
  matching live reco is **marked, not dropped** (the minimum reco count still
  holds): "Already watched" badge, Schedule/Watch-live actions hidden, no timer
  created (AutoProgrammer), excluded from popups and the bell notification.
  Fully fail-open (index unavailable → recos unmarked, never emptied); `.strm`
  library excluded from the index (anti-circular).

- **Séries « prêtes à dévorer » (binge-ready)** (`TonightService.BuildBingeReadySeries`,
  opt-in `TonightBingeEnabled`, défaut off) : le run « À regarder ce soir » détecte les
  séries dont l'usager accumule les épisodes enregistrés non visionnés — il attend d'en
  avoir plusieurs avant de commencer — et lui signale que c'est le moment (« il est temps
  de regarder X, N épisodes en attente »). Détection déterministe C# : épisodes non
  visionnés agrégés par série ; une série qualifie quand son stock atteint
  `TonightBingeThreshold` (défaut 4) **et** qu'au moins un épisode est arrivé dans les
  `TonightBingeActiveDays` derniers jours (défaut 14 — le signal « enregistrement actif »
  qui distingue une accumulation en cours d'une série dormante jamais commencée : une
  série conservée « pour un jour de pluie » ne déclenche **jamais** la suggestion).
  Injectée dans le prompt « ce soir » (AU PLUS UNE série recommandée par run,
  `source="recording"` si la série figure aussi dans les enregistrements non visionnés,
  sinon `source="library"` ; id du premier épisode à regarder, ordre saison/épisode).
  **Anti-spam** : le gate persistant `BingeNotified` (par usager et par série, carry-forward
  par la page de config) ne signale chaque série qu'**une seule fois par cycle
  d'accumulation** — la suggestion se ré-arme quand le compte non visionné repasse sous
  le seuil, c'est-à-dire quand l'usager commence à regarder. Bibliothèque `.strm` exclue
  (garde anti-circulaire), fail-open (aucune erreur de la détection ne casse le run).
  **Binge-ready series ("time to start watching")** (`TonightService.BuildBingeReadySeries`,
  opt-in `TonightBingeEnabled`, off by default): the tonight run detects series whose
  episodes the user is stockpiling unwatched while recording — they wait for several before
  starting — and surfaces that it's time ("time to start X, N episodes waiting").
  Deterministic C# detection: unwatched episodes aggregated per series; a series qualifies
  when its stockpile reaches `TonightBingeThreshold` (default 4) **and** at least one
  episode arrived within the last `TonightBingeActiveDays` days (default 14 — the
  "actively recording" signal that tells an ongoing stockpile from a dormant, never-started
  series: a series kept "for a rainy day" **never** triggers the suggestion). Injected
  into the tonight prompt (AT MOST ONE series recommended per run,
  `source="recording"` if the series also appears in the unwatched recordings, else
  `source="library"`; id of the first episode to watch, season/episode order).
  **Anti-spam**: the persistent `BingeNotified` gate (per user and per series, carried
  forward by the config page) surfaces each series **only once per accumulation cycle** —
  the suggestion re-arms when the unwatched count drops back below the threshold, i.e.
  when the user starts watching. `.strm` library excluded (anti-circular guard), fail-open
  (no detection error ever breaks the run).

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

- **Année de production dans les recommandations** : ajoutée à tous les étages —
  schéma de prompt (`year` facultatif, repris de `epg_series`/`epg_movies`/`epg_tonight`
  ou de `tmdb_lookup`, jamais inventé), injection déterministe lors du rapprochement
  EPG (`EnrichRecommendations`, 3 clés de traçage) puis repli bibliothèque
  (`ProductionYear`), carte UI « 🎬 1995 », `<year>` du NFO `.strm` (année de
  PRODUCTION, distincte de `<premiered>` = date de diffusion, omise si inconnue).
  **Production year in recommendations**: added at every layer — prompt schema
  (optional `year`, copied from the EPG/TMDB tool results, never invented),
  deterministic injection at EPG-match time (3 tracing keys) then library fallback,
  "🎬 1995" on the UI card, `.strm` NFO `<year>` (PRODUCTION year, distinct from
  `<premiered>` = air date, omitted when unknown).

- **Auto-récupération du cache JS client (cache-busting)** : `asset_version.js`
  **généré au build** (cible MSBuild `GenerateAssetVersionJs` : template
  `asset_version_template.js` + `<Version>` du csproj — la version embarquée ne peut
  jamais dériver de celle de la DLL) + endpoint `GET /Plugins/LLMAI/Version` + un
  contrôle au chargement des 3 pages JS : version du module servi ≠ serveur →
  `fetch(cache:'reload')` des 9 modules + `location.reload()` (garde sessionStorage,
  une fois par version). Un module PÉRIMÉ se détecte lui-même (version gravée au
  build) → banner de secours demandant un Ctrl+Shift+R si le reload ne suffit pas.
  **Client JS cache self-healing**: `asset_version.js` **generated at build time**
  (MSBuild target stamping the csproj `<Version>` into the template — the embedded
  version can never drift from the DLL's) + `GET /Plugins/LLMAI/Version` endpoint +
  a load-time check in all 3 page modules: served module version ≠ server →
  `fetch(cache:'reload')` of the 9 modules + `location.reload()` (sessionStorage
  once-per-version guard). A STALE module detects itself (build-time baked version)
  → fallback banner asking for Ctrl+Shift+R.

- **Popup au login : une popup PAR suggestion, en séquence** (au lieu d'un toast
  unique dense) : « 🤖 À regarder ce soir (i/n) — Titre (chaîne · heure · type) »,
  plafond 5, bilan « N programmé(s) » sur la dernière. La séquence couvre TOUTES
  les suggestions (enregistrements, bibliothèque **et** programmes EPG live — vécu :
  un run 100 % live donnait le toast générique « Suggestions prêtes »). Comportements
  clients constatés (tests 2026-09-02) : `MessageCommand.Header` **non rendu** par
  web/Android (tout dans le `Text`) ; le client **web ignore `TimeoutMs`** (fondu
  CSS fixe ~3 s) → rythme **adapté** : 4 s sur web, `LoginPopupSeconds` (défaut 8 s)
  ailleurs (Android TV honore `TimeoutMs`).
  **Login popup: one popup PER suggestion, in sequence** (instead of one dense
  toast): "🤖 À regarder ce soir (i/n) — Title (channel · time · type)", capped at 5,
  "N scheduled" summary on the last one. The sequence covers ALL suggestions
  (recordings, library **and** live EPG programs). Observed client behavior
  (2026-09-02 tests): `MessageCommand.Header` **not rendered** by web/Android
  (everything in the `Text`); the **web** client ignores `TimeoutMs` (fixed ~3 s CSS
  fade) → **adaptive pacing**: 4 s on web, `LoginPopupSeconds` (default 8 s)
  elsewhere (Android TV honors `TimeoutMs`).

- **Notifications multi-lignes via les notifiers Emby (ex. courriel SMTP)** : la
  notification qui accompagne le popup porte maintenant une description **une
  suggestion par ligne + sa raison 🤖** (les retours à la ligne passent tels quels
  dans le courriel — testé). ⚠️ Les clients standard Emby n'ont **pas de boîte de
  réception intégrée** : pour recevoir les notifications de LLM_AI, activer un
  notifier (plugin SMTP) ET le type **« External notification via emby API »**
  dans les paramètres Notifications de l'usager — c'est le type sous lequel les
  notifications du plugin sont livrées (popup login, recos de la tâche planifiée,
  échecs de tâche).
  **Multi-line notifications via Emby notifiers (e.g. SMTP email)**: the
  notification accompanying the popup now carries a **one-suggestion-per-line +
  its 🤖 reason** description (line breaks pass through in the email — tested).
  ⚠️ Stock Emby clients have **no built-in inbox**: to receive LLM_AI's
  notifications, enable a notifier (SMTP plugin) AND the **"External notification
  via emby API"** type in the user's Notifications settings — that is the type
  under which the plugin's notifications (login popup, scheduled-task recos, task
  failures) are delivered.

- **Tagline NFO `.strm`** : la raison LLM va dans le champ `<tagline>` (mise en
  évidence par Emby sur la fiche), préfixée du seul emoji 🤖 — le libellé complet
  « 🤖 Pourquoi ce soir / Why tonight : » est réservé aux cartes de la section
  « À regarder ce soir » de la page (clé i18n `rec.tonight.why`) ; les recos
  d'enregistrement (sections séries/films, `.strm`) portent l'emoji seul.
  **`.strm` NFO tagline**: the LLM reason goes into the `<tagline>` field
  (highlighted by Emby on the item page), prefixed with the 🤖 emoji alone — the
  full "🤖 Pourquoi ce soir / Why tonight:" label is reserved for the "Watch
  tonight" section cards (i18n key `rec.tonight.why`); record recommendations
  (series/movies sections, `.strm`) carry the emoji only.

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

- **Tâche planifiée : un run LLM vide n'efface plus les recommandations**
  (vécu 2026-09-02 03:01 : LLM local étouffé — SÉRIES a répondu
  `[]`, FILMS un appel d'outil malformé `[{"action":"epg_movies"}]` passé comme
  réponse finale → fusion vide persistée → recos de la veille effacées, et les
  consommateurs « tout remplacer » (badges, `CleanPrevious` des cartes `.strm`)
  auraient tout balayé). Trois garde-fous :
  **Scheduled task: an empty LLM run no longer wipes the recommendations**
  (seen 2026-09-02 03:01, main server: choked local LLM — SERIES answered `[]`,
  MOVIES emitted a malformed tool call `[{"action":"epg_movies"}]` that passed as a
  final answer → empty merge persisted → previous day's recos wiped, and the
  replace-all consumers (badges, `.strm` `CleanPrevious`) would have swept
  everything). Three safeguards:
  - la **réparation finale** est étendue : en mode recommandation, un tableau non
    vide dont AUCUN item ne porte `title` (écho d'appel d'outil) déclenche une
    demande de renvoi (un `[]` honnête reste accepté) / the **final-answer repair**
    is extended: in recommendation mode, a non-empty array where NO item has a
    `title` (tool-call echo) triggers a bounded re-emit (an honest `[]` stays
    accepted);
  - **garde anti-effacement** dans `LlmScheduledTask` : run à 0 reco alors que le
    payload précédent en a → l'ancien est conservé, tous les consommateurs aval
    (persistance, badges, `.strm`, auto-program) sont sautés, log Warn /
    **anti-wipe guard** in `LlmScheduledTask`: zero-reco run while the previous
    payload has some → old payload kept, all downstream consumers (persist, badges,
    `.strm`, auto-program) skipped, Warn log;
  - **notification d'échec** (via notifier configuré) dans les deux cas : run vide
    (« recommandations précédentes conservées ») et exception (les deux runs ont
    jeté) / **failure notification** (via configured notifier) in both cases: empty
    run ("previous recommendations kept") and exception (both runs threw).

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
    les épisodes EPG partagent la même pochette de guide au niveau série, mais le
    rapprochement et la clé de cache sont désormais par épisode). Repli conservateur :
    un programme EPG sans numérotation dont le titre ne matche aucun épisode possédé
    retombe sur le niveau série (comportement historique — on ne peut pas prouver que
    l'épisode est absent). Réutilise la correspondance par nom normalisé
    (`GetEmbyInfoTool.Norm`) de l'exclusion epg_series/epg_movies ; index noms +
    clés d'épisodes biblio caché 10 min (jamais par requête). Le vert gagne en cas
    de conflit.
  - **Clé de cache par état ET par item** (`ownedbadge-v2`/`aibadge-v2` + suffixe
    `InternalId`) : les épisodes d'une même série partagent la même pochette du guide
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
    series-level guide artwork, but both the match and the cache key are now
    per-episode). Conservative fallback: an EPG program with no episode numbering whose
    title matches no owned episode falls back to series level (historical behavior —
    the episode's absence cannot be proven). Reuses the normalized name matching
    (`GetEmbyInfoTool.Norm`) from the epg_series/epg_movies exclusion; library names +
    episode keys cached 10 min (never per request). Green wins on conflict.
  - **Cache key per state AND per item** (`ownedbadge-v2`/`aibadge-v2` + `InternalId`
    suffix): episodes of the same series share the same guide artwork (one
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
  gardait le caractère accentué — « leçons » ≠ « lecons » aussi. Or l'EPG porte le titre accentué tandis que l'item bibliothèque porte
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
  kept the accented character — "leçons" ≠ "lecons" too. Since the guide's
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
  `TryCopyProgramPoster` ne gérait que les fichiers locaux — mais les programmes EPG des
  guides sous abonnement référencent presque toujours une **URL distante**
  dans le champ Path de leur image Primary. Le garde
  `File.Exists(URL)` échouait donc silencieusement (`return false` sans log) et la
  carte restait sans affiche quand le lookup TMDB échouait aussi (titres suffixés
  du type « … on Masterpiece » introuvables sur TMDB). Le repli téléchargeait
  dès lors les URL http(s) via le `HttpClient` partagé (les fichiers locaux
  restent copiés) et **chaque garde logue sa raison** (programme introuvable,
  sans image, chemin absent, dossier absent…) — plus de `return false` muet.
  Complément : si le lookup TMDB échoue sur le titre complet, le générateur
  retente **une fois** sans le suffixe de chaîne « on … » (convention de titrage du
  guide : « Moonflower Murders on Masterpiece » → entrée TMDB « Moonflower
  Murders ») — la forme complète est toujours essayée d'abord, un titre
  légitime contenant « on » n'est donc tronqué qu'après échec ; le titre de la
  carte (dossier/.nfo) reste inchangé, seule la requête est nettoyée.
  *(Comportement de téléchargement supersédé en 1.13.5.0 : les URL distantes
  ne sont plus jamais téléchargées — poster par défaut embarqué à la place,
  cf. entrée 1.13.5.0 ci-dessus.)*
  **STRM cards with no poster while the EPG shows one** (fixed 2026-08-30,
  "Moonflower Murders on Masterpiece" case): the poster fallback
  `TryCopyProgramPoster` only handled local files — but subscription-guide EPG
  programs almost always reference a **remote URL** in their Primary image's
  Path field. The `File.Exists(URL)` guard failed
  silently (`return false`, no log) and the card was left posterless whenever
  the TMDB lookup also failed (suffixed titles like "… on Masterpiece" have no
  TMDB match). The fallback then downloaded http(s) URLs via the shared
  `HttpClient` (local files are still copied) and **every guard logs its
  reason** (missing program, no image, missing path, missing folder…) — no more
  silent `return false`. Complement: if the TMDB lookup fails on the full
  title, the generator retries **once** with the "on …" channel suffix
  stripped (guide title convention: "Moonflower Murders on Masterpiece" →
  TMDB entry "Moonflower Murders") — the full form is always tried first, so a
  legitimate title containing "on" is only stripped after a failed lookup; the
  card title (folder/.nfo) is unchanged, only the query is cleaned.
  *(Download behavior superseded in 1.13.5.0: remote URLs are no longer
  fetched at all — embedded default poster applied instead, see the 1.13.5.0
  entry above.)*
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