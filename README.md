<!-- ⚠️ NE PAS RENOMMER le titre H1 ci-dessous sans répercuter le changement :
     l'ancre GitHub #llm_ai--plugin-emby-de-recommandations-par-llm est générée
     à partir de ce titre et est référencée par le lien « Documentation
     complète » de la page de config du plugin (config.html, href à mettre à
     jour en cas de renommage). -->
# LLM_AI — Plugin Emby de recommandations par LLM

**Version :** 2.23.3 · **Compatible :** Emby Server 4.9 et 4.10

> English version: see [README-EN.md](README-EN.md).

## En clair : l'assistant qui connaît votre télé

### Le problème

Vous avez un serveur Emby qui enregistre la télé. Chaque soir, la même question :
*« qu'est-ce que je pourrais regarder ou enregistrer ce soir ? »* Y répondre soi-même
demande de feuilleter le guide TV (des centaines d'émissions), de se souvenir de ce
qu'on a déjà vu ou déjà enregistré, de vérifier si le film en vaut la peine… C'est un
travail de tri fastidieux, et c'est exactement ce qu'une IA fait bien.

### L'idée

**LLM_AI** est un assistant intégré à Emby qui **lit le programme TV à votre place** et
vous dit quoi regarder ou enregistrer. Il ne devine pas : il pose ses questions à votre
serveur (qu'y a-t-il ce soir ? qu'est-ce que j'aime déjà ? qu'est-ce que j'ai déjà ?),
consulte des sources externes (fiches de films, web, listes de nouveautés), puis
**réfléchit et propose**, toujours avec une explication en français simple.

### Ce qu'il fait

- **Une page « Recommandations »** : des séries et des films à enregistrer bientôt,
  chacun avec sa raison d'être.
- **« À regarder ce soir »** : une sélection pour chaque personne du foyer, selon ses
  goûts, ce qu'elle a commencé sans finir et les enregistrements qui s'accumulent
  (« vous avez 6 épisodes de cette série : il est temps de commencer »).
- **Une remise des suggestions à votre goût** : playlist, collection, étiquette,
  favoris, cartes dans une bibliothèque, ou enregistrement programmé directement.
  Chaque option se coche et se décoche séparément, et tout se nettoie tout seul chaque
  nuit. **L'IA ne supprime jamais rien de son propre chef : c'est vous qui décidez.**

### Il apprend avec le temps

- **Il apprend de ses erreurs** : chaque semaine, il compare ce qu'il a recommandé avec
  ce que vous avez réellement regardé, et ajuste ses suggestions.
- **Il tient un carnet** : recommandations, visionnages et abandons sont notés, et il
  réécrit régulièrement une courte « fiche mémoire » de sa stratégie qu'il relit avant
  de proposer.
- **Vous gardez la main** : chaque suggestion peut être écartée (bouton « Oublier »),
  et les actions sensibles sont désactivées tant que vous ne les activez pas.

### Les petits plus

- **Un chat avec l'assistant** : posez vos questions en langage naturel
  (« qu'est-ce qu'il y a comme documentaire cette semaine ? »). Il peut aussi vérifier
  et corriger les fiches de vos films et séries : vous voyez la preuve et la
  proposition côte à côte avant d'approuver.
- **Un bilan de santé du serveur** : à la demande, il inspecte Emby (disque, journaux,
  performances) et rédige un rapport avec des conseils.
- **Les enregistrements sans fiche réparés** : quand une émission enregistrée reste
  sans affiche ni description, il la retrouve sur le web et remplit les informations
  manquantes, comme le ferait une recherche manuelle.

### Ce dont il a besoin : des données propres

L'assistant **ne peut évaluer que ce qu'on lui montre**. Or le guide TV et votre
vidéothèque ne parlent pas toujours les mêmes mots : le guide écrit « Kids », votre
bibliothèque dit « Enfant » ; une émission est notée « PG-13 » d'un côté, « 13+ » de
l'autre. Pour un assistant qui compare des listes, ce sont des mots différents, donc des
comparaisons moins justes.

Deux plugins font ce ménage en amont et sont **fortement recommandés** (ils se trouvent
dans le catalogue de plugins d'Emby) :

- **GenreCleaner** : un vocabulaire unique pour les genres dans toute la vidéothèque ;
- **Classification Mapper** : une seule liste pour les classifications d'âge.

Quand ils sont installés, LLM_AI s'en sert automatiquement et traduit lui-même le guide
TV dans le même vocabulaire. **Des informations propres en amont, des recommandations
pertinentes en aval.**

### Accessible à tous, sans ordinateur de course

On imagine souvent que l'IA exige une grosse machine. Pas nécessairement :

- **Un modèle en ligne gratuit** : avec un simple **compte gratuit chez Ollama**, vous
  utilisez leurs modèles hébergés sur leurs serveurs. C'est le chemin le plus simple :
  rien à installer, et c'est suffisant pour tout ce que fait le plugin. C'est aussi la
  solution des petites machines : même sur un **Raspberry Pi 4**, c'est le cloud qui fait
  le travail de réflexion.
- **Un modèle chez vous, en option** : c'est la variante la plus privée (rien ne quitte
  votre réseau), mais elle demande **un ordinateur avec une carte graphique d'au moins
  8 Go de mémoire**. En dessous, le modèle tourne mais répond très lentement.
- **Les deux à la fois** : le plugin accepte plusieurs services, choisit lui-même et
  **bascule automatiquement** si l'un est indisponible. Google Gemini est aussi pris en
  charge.

Le coût peut donc être **zéro dollar**.

### En une phrase

**C'est un majordome pour votre télé : il surveille le guide, connaît vos goûts, vous dit
quoi regarder ou enregistrer et range les informations. Il ne jette rien et ne
décide rien de sensible sans votre accord.**

---

## Table des matières

1. [Ce qu'il vous faut](#ce-quil-vous-faut)
2. [Installation](#installation)
3. [Premiers réglages](#premiers-réglages)
4. [Pour de meilleurs résultats](#pour-de-meilleurs-résultats)
5. [Au quotidien](#au-quotidien)
6. [Où apparaissent les suggestions](#où-apparaissent-les-suggestions)
7. [Vie privée et sécurité](#vie-privée-et-sécurité)
8. [Problèmes courants](#problèmes-courants)
9. [Aller plus loin](#aller-plus-loin)
10. [Licence](#licence)

---

## Ce qu'il vous faut

- **Emby Server** version 4.9 ou 4.10, avec un compte administrateur.
- Pour recommander des *enregistrements* : la **TV en direct (Live TV)** configurée
  dans Emby, avec un guide TV (EPG) et un enregistreur (DVR).
- **Un service d'IA** (voir « Accessible à tous » plus haut) : un compte Ollama gratuit,
  une clé Google Gemini, ou un modèle local sur votre réseau.
- **Facultatif, mais utile :** une clé **TMDB** gratuite (fiches de films et séries plus
  complètes). D'autres clés sont possibles (TVDB, un moteur de recherche web) ; la
  page de configuration a un bouton **Tester** à côté de chaque service.

---

## Installation

> Le plugin tient en un seul fichier : **`LLM_AI.dll`**.

1. Téléchargez la dernière archive `LLM_AI-<version>.zip` sur la page
   **[Releases](https://github.com/reneboulard/LLM_AI/releases)** du projet, puis
   décompressez-la.
2. **Linux** : ouvrez un terminal dans le dossier décompressé et lancez
   `sudo bash install.sh`. Le script copie le plugin au bon endroit et redémarre Emby.
   **Windows ou autre** : copiez `LLM_AI.dll` dans le dossier `plugins` du dossier de
   données d'Emby, puis redémarrez Emby.
3. Rechargez la page web d'Emby (touche **F5**).
4. Ouvrez **Tableau de bord → Plugins → LLM_AI** : c'est la page de configuration.

**Mise à jour :** même geste (nouvelle DLL, redémarrage d'Emby). Vos réglages sont
conservés.

---

## Premiers réglages

Dans la page de configuration, trois étapes suffisent pour commencer :

1. **Ajoutez un service d'IA** (section « Serveurs LLM ») : choisissez Ollama (en ligne ou
   chez vous) ou Gemini, collez votre clé si le service en demande une, puis cliquez sur
   **Tester**. Vous pouvez en ajouter plusieurs : celui de plus haute priorité est utilisé
   en premier, les autres servent de secours.
2. **(Facultatif) Ajoutez votre clé TMDB** dans « Clés API », puis **Tester**.
3. **Enregistrez**, puis ouvrez la page **Recommandations** dans le menu d'Emby. Les
   premières suggestions arrivent après le premier passage de la tâche planifiée, ou
   tout de suite avec le bouton de rafraîchissement.

Le reste est optionnel et se règle à votre rythme : chaque section de la page a ses
explications, et les actions sensibles sont **désactivées par défaut**.

---

## Pour de meilleurs résultats

Le plugin fonctionne dès qu'il a un service d'IA. Mais plus il a de sources, mieux il
identifie vos enregistrements et mieux il recommande. Chaque ajout est facultatif ; le
bouton **Tester** de la page de configuration vérifie chacun.

| Ajout | Ce que ça améliore | Difficulté |
|---|---|---|
| **Clé TMDB** (gratuite) | Fiches complètes, affiches, descriptions, identification plus sûre | Facile |
| **SearXNG** (moteur de recherche web à installer chez soi) | Retrouve les émissions dont le titre du guide ne correspond à aucun catalogue : c'est ce qui permet une excellente identification | Technique |
| **Clé TVDB** | Meilleure identification des séries | Facile |
| **Un deuxième service d'IA** | Bascule automatique si le premier est en panne ou à court de quota | Facile |
| **GenreCleaner et Classification Mapper** | Genres et classifications d'âge cohérents entre le guide et votre bibliothèque | Facile |

Le plugin **complète** Emby, il ne le remplace pas : Emby continue d'identifier et de décrire
vos enregistrements avec ses propres sources, et le plugin intervient pour réparer ce qu'Emby n'a
pas su identifier.

Les réglages détaillés (SearXNG, moteurs, clés, priorités) sont décrits dans la
[référence technique](docs/reference.md#optimiser-le-plugin).

---

## Au quotidien

### La page « Recommandations »
Les séries et films à enregistrer, produits chaque jour par une tâche planifiée. Chaque
carte a une raison et des boutons : **Programmer** (enregistrer), **Regarder**,
**Oublier** (ne plus jamais me le proposer).

### « À regarder ce soir »
Une sélection **par personne**, calculée à l'ouverture de la page, selon l'historique, le
guide du soir et les enregistrements pas encore vus. À chaque connexion, une courte suite
de messages à l'écran peut vous présenter les suggestions du soir (réglable).

Vous pouvez limiter ce travail aux heures où l'on regarde vraiment la télé avec le réglage
**« période de grande écoute »** (par exemple `17:00-23:00`) : en dehors, le plugin
n'appelle pas l'IA et montre simplement la dernière sélection. Laissez vide pour que ce
soit toujours actif.

### Le chat
L'onglet **Chat en direct** de la page de configuration (réservé aux administrateurs)
permet de discuter avec l'assistant : explorer la bibliothèque, préparer une soirée,
demander pourquoi il a suggéré un titre. Pour toute action qui modifie quelque chose, il
vous montre une carte à approuver.

Une **application compagnon** optionnelle offre le même chat aux autres membres du foyer,
au clavier ou à la voix, sur le réseau local (voir
[`chat-external/`](chat-external/README.md)).

### Le bilan de santé
Un rapport sur l'état du serveur (disque, tâches, journaux, sécurité de base), avec des
conseils classés par gravité. Le dernier rapport est conservé : le relire ne coûte rien.
Les actions correctives (arrêter une lecture, lancer une tâche, envoyer une notification)
sont **désactivées par défaut**.

### Les enregistrements sans fiche
Après chaque enregistrement, le plugin vérifie que la fiche est la bonne ; si elle manque
ou est douteuse, il cherche le bon titre (TMDB, web), ajoute affiche, description et
genres, puis **verrouille** la fiche pour qu'Emby ne l'écrase pas. Un passage de
rattrapage a lieu chaque nuit à 4 h. Un champ « Titres exclus » permet d'ignorer certaines
émissions.

### La gestion de bibliothèque
L'onglet **Gestion bibliothèque** classe vos films et séries en trois groupes : à
supprimer, peut être supprimé, à conserver, avec la raison et l'espace libéré. **Rien
n'est supprimé sans votre clic** sur « Effacer » ; c'est alors Emby qui supprime.
« Conserver » vous évite de revoir le titre.

### Plusieurs langues
L'interface est disponible en français et en anglais. D'autres langues peuvent être
ajoutées depuis la page de configuration (l'IA traduit, vous relisez).

---

## Où apparaissent les suggestions

La page Recommandations ne modifie rien dans Emby. Si vous le souhaitez, les mêmes
suggestions peuvent aussi apparaître **directement dans Emby**, là où vous regardez.
Chaque option est **indépendante** (cochez ce qui vous sert) et **réversible**
(nettoyée chaque nuit à 3 h ou recréée au passage suivant).

| Je veux… | Option à cocher | Ce qui apparaît dans Emby |
|---|---|---|
| Enchaîner ma soirée | Playlist « AI Tonight » | Une playlist par personne, plus une playlist commune adaptée aux contrôles parentaux |
| Parcourir librement | Collection « AI Tonight » | Une collection navigable, cumulée sur la journée |
| Retrouver les suggestions par un filtre | Étiquette « AI Tonight » | Une étiquette (tag) Emby |
| Marquer mes suggestions ❤️ | Favoris « AI Tonight » | Les titres sont ajoutés aux favoris de la personne choisie |
| Enregistrer automatiquement ce que je n'ai pas | Auto-programmation | Des enregistrements programmés dans Emby |
| Enregistrer en un clic, à la demande | Bibliothèque « AI Suggestions » | Des cartes jouables dans une bibliothèque, qui lancent l'enregistrement |
| Savoir quoi supprimer quand le disque est plein | Étiquette « AI Delete » | Une étiquette seulement : **vous décidez** ce qu'on supprime |

Les contrôles parentaux d'Emby sont respectés pour les suggestions personnelles.

---

## Vie privée et sécurité

- **L'IA ne supprime rien de son propre chef.** Elle peut *suggérer* de supprimer un titre (par exemple pour libérer de l'espace), mais c'est toujours vous qui décidez, d'un clic.
- **L'IA propose, le plugin vérifie.** Rien de ce que l'IA avance n'est cru sur parole : le plugin contrôle les titres et les fiches proposés avant de s'en servir, et écarte ce qui ne tient pas. Les actions qui modifient quelque chose demandent votre approbation, sauf celles que vous choisissez d'automatiser.
- **Vous activez, il n'agit pas seul.** Auto-programmation, actions correctives du bilan
  de santé, accès au chat : tout est désactivé tant que vous ne l'activez pas.
  L'auto-programmation est encadrée : nombre de titres limité à chaque passage, espace
  disque vérifié avant de programmer, et vous pouvez annuler un enregistrement à tout
  moment dans Emby. Le reste de ce que le plugin ajoute (étiquettes, cartes,
  playlists, collection, favoris) est temporaire et nettoyé chaque nuit.
- **Ce qui part vers l'IA.** Pour répondre, l'IA reçoit des titres, des genres, des
  extraits de votre historique de visionnage et du guide TV. Avec un modèle **local**,
  rien ne quitte votre réseau ; avec un service en ligne (Ollama Cloud, Gemini), ces
  informations sont envoyées à ce service. Choisissez selon votre degré de confort.
- **Vos clés restent chez vous.** Elles sont enregistrées dans la configuration d'Emby
  et ne sont jamais montrées par l'assistant.
- **Le chat de la page de configuration est réservé aux administrateurs.**

---

## Problèmes courants

**Le plugin n'apparaît pas dans Emby.**
Vérifiez que `LLM_AI.dll` est bien dans le dossier `plugins` d'Emby et que vous avez
**redémarré Emby**.

**Des textes sont en anglais ou des boutons manquent après une mise à jour.**
Rechargez la page avec **Ctrl+F5** : le navigateur a gardé l'ancienne version.

**Aucune recommandation n'apparaît.**
Cliquez sur **Tester** à côté de votre service d'IA. Vérifiez ensuite que le guide TV
(EPG) contient des émissions à venir. Si vous avez réglé une « période de grande écoute »,
hors de cette plage le plugin affiche seulement la dernière sélection.

**L'assistant est très lent avec mon modèle local.**
Votre carte graphique manque probablement de mémoire. Ajoutez un service en ligne comme
service principal ou de secours.

**Gemini répond « quota atteint ».**
La version gratuite de Google limite le nombre de demandes par minute. Le plugin attend
et réessaie, ou bascule sur votre autre service d'IA.

**La bibliothèque « AI Suggestions » est introuvable.**
Créez d'abord une bibliothèque de films pointant vers un dossier vide, puis saisissez son
nom **exactement** comme dans le tableau de bord d'Emby.

**Je veux faire le point sur ce qui s'est passé.**
Le journal d'Emby (Tableau de bord → Journaux) contient les lignes marquées `[LLM_AI]`.

---

## Aller plus loin

- **[Référence technique](docs/reference.md)** : toutes les options de configuration,
  les outils de l'IA, l'API HTTP, les détails de chaque fonction.
- **[Application compagnon du chat](chat-external/README.md)** : le chat pour le foyer.
- **[Page des versions](https://github.com/reneboulard/LLM_AI/releases)** : téléchargements
  et nouveautés de chaque version.

---

## Licence

[MIT](LICENSE).
