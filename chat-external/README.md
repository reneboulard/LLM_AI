# Chat externe LLM AI — app compagnon

App cliente autonome pour le **chat externe** du plugin Emby LLM AI
(endpoints `POST /Plugins/LLMAI/ChatExternal` et `POST /Plugins/LLMAI/Show`).

Une **conversation naturelle avec votre serveur Emby** : parlez-lui au clavier
ou **par la voix** — dictée 🎤 pour poser la question, lecture à voix haute 🔊
pour la réponse, comme un assistant de salon posé sur le médiathèque du foyer.

Un **seul fichier Python** (`chat_external.py`), bibliothèque standard
uniquement : rien à compiler, rien à installer avec pip, aucun serveur web
(nginx/openresty/PHP) requis. Fonctionne sous Linux, macOS et Windows.

> ⚠️ **App de réseau local.** N'exposez jamais cette app directement sur
> Internet : elle est conçue pour le LAN du foyer (voir
> [Sécurité](#sécurité)).

## Ce que fait l'app

- Sert une **page de chat** (embarquée dans le script) sur le LAN ;
- **Mêmes identifiants qu'Emby** : la connexion passe par
  `AuthenticateByName`, il n'y a **aucun deuxième stockage de mots de
  passe**. Seuls les comptes **disposant d'un mot de passe** peuvent se
  connecter (un compte sans mot de passe est refusé par l'app) ;
- **Les administrateurs ne passent jamais par ce chat** (best pratique de
  sécurité) : tout compte avec les droits d'admin Emby est **toujours
  refusé**, fail-closed — un compte à privilèges ne doit pas circuler sur
  un canal usager. Le chat des administrateurs reste la page de
  configuration du plugin, avec ses propres garde-fous ;
- **Mêmes droits qu'Emby** : chaque réponse ne cite que ce que la policy de
  l'usager autorise — accès aux bibliothèques (`EnableAllFolders` /
  `EnabledFolders`), contrôle parental (`MaxParentalRating`, éléments non
  cotés bloqués, tags bloqués). L'app n'élargit jamais l'accès : c'est le
  plugin qui applique la policy Emby de l'usager connecté ;
- Les titres cités par l'agent sont cliquables :
  - **📺 Projeter** — la fiche du titre s'affiche instantanément sur une
    **app Emby ouverte avec le compte de l'usager** (téléviseur, tablette,
    autre PC…), via la commande `DisplayContent` ; si le client ne la gère
    pas, un message à l'écran reprend le titre (repli `DisplayMessage`) ;
  - **↗ Fiche web** — ouvre la fiche Emby dans un nouvel onglet du
    navigateur ;
- **Pilotage du client actif par l'agent** : le LLM peut, via le tool
  `client_command`, agir sur le client Emby actif de l'usager — projeter la
  fiche, **lancer la lecture d'un titre**, mettre en pause / reprendre /
  arrêter, régler le **volume**, couper / rétablir le **son**, revenir à
  l'**accueil**, lire **l'état de lecture** (`playback_status` : position,
  durée restante, pistes audio et sous-titres actives), **sauter en avant
  ou en arrière dans la vidéo** (`seek` — jusqu'à ±30 min, borné au
  programme en cours), **activer ou couper les sous-titres** et **changer
  de piste sonore** (`set_subtitle_track` / `set_audio_track` — « off »,
  langue 2-3 lettres ou n° de piste). Uniquement non destructeur,
  uniquement sur SON client ;
- **Contrôle du visionnement en cours** : pendant une lecture, l'agent peut
  aussi lire **où en est la lecture** (« où en sommes-nous ? » — position,
  durée restante, sous-titre et piste audio actives), **sauter dans le
  temps** (« avance de 30 secondes », « recule de 10 secondes », « reprends
  au début ») et **basculer les pistes** (« mets les sous-titres en
  français », « coupe les sous-titres », « passe en version originale »).
  Les sauts sont **approximatifs** : la position connue du serveur a
  quelques secondes de retard. Un **toast court** s'affiche à l'écran quand
  une piste est basculée ; pour les sauts et la pause, l'écran lui-même
  fait office de retour ;
- **Enregistrements à la voix (opt-in)** : si l'admin active la fonction et
  liste l'usager (en plus du droit natif Emby d'enregistrer la TV en
  direct), l'agent peut programmer un enregistrement DVR — avec une
  **confirmation humaine obligatoire à deux phases** :
  1. « enregistre ce programme » → l'agent **réserve** ; un **code à
     4 chiffres** s'affiche dans un encadré du chat (🔑) — le LLM ne le
     connaît pas et ne peut pas se confirmer lui-même. Tant que la
     réservation est en attente, l'encadré porte aussi le **contenu du
     bucket** (« ⏳ À confirmer : « titre » (série|film) — expire à HH:mm »,
     ligne composée par le serveur dans la langue de l'usager : ce que
     l'usager voit à l'écran EST la réservation déposée — mais jamais lue
     par la synthèse vocale) ;
  2. l'usager **tape le code** dans son message → seul ce code exact crée
     l'enregistrement (visible dans la DVR Emby, avec un toast à l'écran).
  Une réservation expire après **5 minutes** ; **3 codes erronés
  verrouillent l'outil 15 minutes** pour cet usager ; un quota de
  **créations par jour** (défaut 3, réglable) est compté seulement quand
  un enregistrement est réellement créé. Le contrôle parental de la
  policy s'applique aussi au programme demandé.
  **La confirmation est traitée par le serveur lui-même** : quand une
  réservation existe et que le message contient un code à 4 chiffres,
  l'endpoint intercepte et exécute la confirmation directement — le
  modèle n'a aucun rôle dans cette transaction (il ne peut ni se
  confirmer lui-même, ni présenter un refus comme un succès ; validé en
  test). Un **refus s'affiche toujours** dans un encadré distinct
  (compteur d'essais, verrou, quota) quel que soit le texte du modèle.
  L'agent peut aussi répondre aux questions d'état via `status`
  (lecture seule : réservation en attente, heure d'expiration, essais
  ratés — **jamais le code, jamais le quota** ; une réservation en
  attente n'est pas un enregistrement créé), et lister les
  enregistrements DVR **complétés** visibles à l'usager (sous-action
  `recordings` de `get_emby_info` — droit natif + présence dans la
  liste dédiée tous deux requis, v1.13.23).
- **Dictée vocale 🎤** et **lecture à voix haute 🔊** — voir
  [Conversation par la voix](#conversation-par-la-voix-🎤-🔊) ;
- **Anti-spam intégré côté plugin** : chaque usager est plafonné (5 tours
  par minute et 150 par jour par défaut, réglables dans la page de
  configuration du plugin, section « Chat externe ») — le LLM n'est jamais
  saturé, même si la page est ouverte sur plusieurs appareils.

Tout le contenu reste filtré par la **policy parentale** de l'usager côté
plugin. Côté serveur, le chat reste en **lecture seule** sur la
médiathèque (aucun tool d'action sur celle-ci, aucun audit) : les seules
actions possibles sont piloter le **client Emby actif de l'usager**
(projection de fiche, lecture / pause / volume, sauts et pistes) et,
**seulement si l'admin active l'opt-in**, programmer un enregistrement
DVR — derrière sa confirmation à code (voir ci-dessus) — jamais de
modification de la médiathèque, jamais rien sur un autre appareil.

## Installation

Le script doit tourner **sur la même machine qu'Emby** : la gate du plugin
n'accepte que des appels directs depuis le loopback, sans
`X-Forwarded-For`.

```bash
# 1. Premier lancement : crée config.json avec un secret généré
python3 chat_external.py        # Windows : py chat_external.py

# 2. Dans Emby : tableau de bord → Plugins → LLM AI → section « Chat externe »
#    - cochez « Activer le chat externe »
#    - collez le secret affiché au premier lancement dans « Secret partagé »
#      (ou cliquez « Générer » dans Emby et reportez la valeur dans config.json)
#    - listez les usagers autorisés (un nom d'usager Emby par ligne —
#      des comptes AVEC mot de passe ; les admins sont refusés)

# 3. Relancez
python3 chat_external.py
```

Ouvrez ensuite `http://<machine-emby>:8070/` depuis un navigateur du foyer
(tablette, téléphone, PC) et connectez-vous avec vos identifiants **Emby**
(mêmes nom d'usager et mot de passe que l'app Emby).

## Configuration (`config.json`)

| Clé | Défaut | Rôle |
|---|---|---|
| `emby_url` | `http://localhost:8096` | URL Emby — **doit rester le loopback** de la machine Emby (gate du plugin) |
| `emby_public_url` | (vide) | URL Emby **vue par le navigateur de l'usager** — sert aux liens « ↗ fiche web » quand on chatte depuis un autre appareil que le serveur (ex. `http://192.168.1.20:8096`) ; vide = repli sur `emby_url` |
| `listen_host` | `0.0.0.0` | `0.0.0.0` = LAN ; `127.0.0.1` si vous ne discutez que depuis le serveur |
| `listen_port` | `8070` | Port de la page de chat |
| `secret` | (généré) | Même valeur que « Chat externe → Secret partagé » dans Emby |
| `timeout_seconds` | `180` | Délai d'attente des appels vers Emby / le LLM |
| `session_secret` | (généré) | Clé de signature des cookies de session ; la régénérer déconnecte tout le monde |
| `ssl_cert` / `ssl_key` | (vides) | HTTPS optionnel — **requis pour la dictée vocale 🎤 hors localhost** (le micro est bloqué par Chrome sur du HTTP) ; auto-signé accepté |

Pour révoquer l'accès : changez le secret **des deux côtés** (Emby +
`config.json`), ou désactivez le chat externe dans Emby.

## Conversation par la voix 🎤 🔊

C'est la raison d'être de l'app : **parler au serveur et se faire répondre
à voix haute**, sans clavier ni écran pour certains usagers du foyer.

### Dictée 🎤 (parole → texte)

- Bouton **🎤** dans la barre de saisie : appuyez, parlez, la transcription
  part automatiquement (reconnaissance vocale native du navigateur,
  Chrome/Edge — dans la langue du navigateur de l'usager, ex. « fr-CA »,
  « en-US ») ;
- Le bouton est **masqué** sur les navigateurs sans support (Firefox…) ;
- ⚠️ Le navigateur n'offre le micro que sur un **contexte sécurisé** :
  HTTPS, ou connexion depuis le serveur lui-même (localhost). Voir la
  section [HTTPS](#https-grandement-recommandé).

### Lecture à voix haute 🔊 (texte → parole)

- Chaque réponse a un bouton **🔊** pour la faire lire par la synthèse
  vocale du navigateur (fonctionne aussi hors HTTPS — seule la **dictée**
  exige un contexte sécurisé) ;
- Le bouton **🔊 Auto** (en-tête) active la lecture automatique de chaque
  réponse ; l'état est mémorisé par navigateur.

### Mode réponse parlée

Quand l'auto-lecture est active, l'app signale le canal de livraison au
plugin à chaque envoi (`Tts` dans la requête `ChatExternal`) ; le LLM
formule alors sa réponse **pour l'ORAL** — phrases courtes, heures en
toutes lettres, pas de listes/tableaux/URL — tout en gardant les titres
exacts (les boutons de projection restent rendus). Basculer 🔊 en cours de
conversation alterne proprement les deux formulations (le signal est par
tour). Côté serveur, chaque tour parlé journalise « Canal de livraison :
synthèse vocale ».

## HTTPS : grandement recommandé

La **lecture à voix haute 🔊** fonctionne partout, mais la **dictée 🎤**
n'est offerte par Chrome/Edge que sur un **contexte sécurisé** (HTTPS, ou
connexion depuis le serveur lui-même). HTTPS est donc
**grandement recommandé** dès qu'on veut une vraie conversation vocale —
et il évite que la conversation circule en clair sur le LAN. Avec un
**certificat valide**, Chrome accepte la connexion **sans broncher** :
pas d'écran d'avertissement à cliquer à chaque connexion — un vrai
confort sur les appareils du foyer (téléviseur, tablette partagée).

Deux familles de mise en œuvre, au choix :

### A. TLS dans l'app Python (option `ssl_cert` / `ssl_key`)

Trois variantes, de la plus simple à la plus autonome :

1. **Copier un certificat existant du host** (le plus simple) — si le
   serveur a déjà un certificat valide (Let's Encrypt…), copiez le
   certificat **fullchain** (feuille + intermédiaires) et la clé sur la
   machine de l'app, puis pointez `ssl_cert`/`ssl_key` dessus :

   ```json
   "ssl_cert": "/chemin/vers/fullchain.pem",
   "ssl_key":  "/chemin/vers/privkey.pem"
   ```

   - Le nom dans l'URL doit être couvert par le certificat
     (`https://chat.votredomaine.tld:8070`) — un **wildcard**
     (`*.votredomaine.tld`) se réutilise tel quel ;
   - le nom doit résoudre vers l'IP LAN du serveur : un
     enregistrement local du DNS du foyer suffit (zone locale du
     routeur/serveur DNS, fichier `hosts` des appareils) — rien de public,
     aucun port ouvert ;
   - si le certificat du host se renouvelle (90 j pour Let's Encrypt),
     rafraîchissez la copie (cron ou hook post-renewal), sinon l'app
     servira un certificat expiré.

2. **Certificat Let's Encrypt dédié** (challenge DNS-01) pour
   `chat.votredomaine.tld` — renouvellement automatique directement sur la
   machine de l'app, même résolution locale.

3. **Auto-signé** (défaut de cette doc) : fonctionne partout mais le
   navigateur affiche un avertissement à accepter une fois. Génération :

   ```bash
   openssl req -x509 -newkey rsa:2048 -nodes -days 825 \
     -keyout chat.key -out chat.crt \
     -subj "/CN=chat-local" \
     -addext "subjectAltName=IP:192.168.x.x,DNS:chat-local"
   ```

   (remplacez `192.168.x.x` par l'IP LAN du serveur ; mettez les deux
   fichiers dans `ssl_key`/`ssl_cert`).

### B. TLS dans un reverse proxy

Si le foyer a déjà un reverse proxy HTTPS (nginx, Caddy, openresty…)
avec un certificat **valide pour son nom**, le plus simple peut être de
placer l'app derrière : le proxy termine le TLS (certificat déjà valide,
aucun avertissement) et relaie l'app en HTTP clair — dans ce cas, forcez
`listen_host` à `127.0.0.1` dans `config.json` pour que la page ne
réponde que depuis le proxy, et laissez `ssl_cert`/`ssl_key` vides.

Deux règles à retenir :

- seule la jambe **navigateur → app** passe par le proxy ; l'appel
  **app → Emby** doit rester **direct** (la gate du plugin rejette tout
  appel à Emby passant par un proxy — voir [Sécurité](#sécurité)) ;
- si l'app est joignable hors du foyer via ce proxy, protégez-la
  (authentification du proxy) — voir [Sécurité](#sécurité).

## Lancer l'app au démarrage

### Linux (systemd)

Pour que le chat survive aux redémarrages et se relance seul après un
crash, une petite unité systemd suffit :

```ini
# /etc/systemd/system/llmai-chat.service
[Unit]
Description=LLM AI — chat externe (app compagnon Emby)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
ExecStart=/usr/bin/python3 /opt/chat-external/chat_external.py
WorkingDirectory=/opt/chat-external
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
```

(adaptez les deux chemins à l'endroit où vous avez mis le script et son
`config.json` ; l'usager du service doit pouvoir lire la clé SSL.)

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now llmai-chat.service
journalctl -u llmai-chat -f        # suivre les logs
```

Le service démarre tout seul au boot de la machine et relit
`config.json` à chaque (re)démarrage — changer le certificat ou le
secret se résume à un `systemctl restart llmai-chat`.

### Windows

Deux approches, selon que la machine reste connectée ou non :

**a) Planificateur de tâches (au boot, sans ouvrir de session) —
recommandé :**

```bat
:: une fois, dans une console administrateur :
schtasks /Create /TN "LLMAI Chat" /RU SYSTEM /SC ONSTART /DELAY 0000:30 ^
  /TR "\"C:\chat-external\chat_external.bat\""
```

avec un `chat_external.bat` minimal dans le même dossier :

```bat
@echo off
cd /d C:\chat-external
py chat_external.py >> chat_external.log 2>&1
```

(les sorties sont journalisées dans `chat_external.log`, à côté du
script).

**b) Dossier Démarrage** (plus simple, mais démarre seulement à
l'ouverture de session) : `Win+R` → `shell:startup` → copiez-y un
raccourci vers `chat_external.bat` (sans `/RU SYSTEM` ci-dessus, le .bat
seul suffit).

Relire la configuration après un changement se résume à arrêter et
relancer la tâche (`schtasks /End /TN "LLMAI Chat"` puis `schtasks
/Run /TN "LLMAI Chat"`) ou à se déconnecter/reconnecter (dossier
Démarrage).

### macOS (launchd)

Un `LaunchAgent` par utilisateur connecté, dans
`~/Library/LaunchAgents/` :

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
  "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>Label</key><string>llmai-chat</string>
  <key>ProgramArguments</key><array>
    <string>/usr/bin/python3</string>
    <string>/opt/chat-external/chat_external.py</string>
  </array>
  <key>WorkingDirectory</key><string>/opt/chat-external</string>
  <key>KeepAlive</key><true/>
</dict></plist>
```

```bash
launchctl load ~/Library/LaunchAgents/llmai-chat.plist
```

## Sécurité

- **App de réseau local uniquement.** N'exposez **jamais** l'app
  directement sur Internet (aucune règle de redirection de port vers
  8070). Pour un accès hors du foyer, placez l'app derrière **votre
  propre** reverse proxy HTTPS (avec authentification) ; seuls les appels
  de l'app **vers** Emby doivent rester directs.
- L'appel vers Emby est **toujours direct** (aucun proxy HTTP n'est
  consulté) : toute requête passée par un reverse proxy porte un
  `X-Forwarded-For` et est rejetée par la gate du plugin.
- **Authentification Emby, pas de doublon** : mêmes identifiants que
  l'app Emby ; aucun mot de passe stocké par l'app. Seuls les comptes
  avec mot de passe peuvent se connecter ; les administrateurs Emby sont
  **toujours refusés** sur le chat externe (leur chat dédié reste la page
  de configuration du plugin).
- **Droits Emby respectés** : le contenu cité ou projeté passe la policy
  de l'usager (accès bibliothèques + contrôle parental) telle que définie
  dans Emby.
- Cookie de session signé HMAC-SHA256 (`HttpOnly`, `SameSite=Lax`).
- Le secret partagé ne quitte jamais le script et la page.
- **Rate limiting** côté plugin : plafonds par usager (défaut 5 tours/min,
  150/jour — réglables dans la section « Chat externe » de la page de
  configuration du plugin) contre le spam du LLM.
- **Lecture seule côté plugin** : une fuite du secret ne donne aucune
  capacité d'écriture sur la médiathèque — les seules actions possibles
  sont de piloter le client Emby actif de l'usager (projection de fiche,
  lecture / pause / volume, sauts et pistes) et, **seulement si l'admin
  active l'opt-in enregistrements**, de programmer un timer DVR —
  derrière la confirmation à code à deux phases (le code n'est jamais
  visible du LLM ni de la page sans login).
- **Le LLM est bridé, par conception.** Le pire scénario possible est
  qu'il se trompe et **lance la mauvaise vidéo**, mette en pause au
  mauvais moment ou **saute au mauvais endroit** — sur le client actif
  de l'usager qui parle, et rien d'autre (les enregistrements restent
  derrière la confirmation à code) :
  - **allowlist stricte** des commandes (`playback_status`, `seek`,
    `set_subtitle_track`, `set_audio_track`, `display_item`, `play_item`,
    `go_home`, `pause`, `unpause`, `stop`, `set_volume`, `mute`,
    `unmute`) — toute autre commande est rejetée **fail-closed** avant
    tout effet, et certains envois sont exclus **pour toujours**
    (`SendKey` : poids arbitraire, `TakeScreenshot` : intimité,
    `Restart`/`Shutdown`/`Identify` : serveur) ;
  - **session bornée** : seuls les appareils de l'usager connecté sont
    ciblables — jamais le client d'un autre usager, jamais le serveur ;
  - `display_item` et `play_item` passent la **policy parentale** de
    l'usager (fail-closed) ;
  - **aucune écriture** sur la médiathèque : pas de modification
    d'items, pas de tool d'action, pas d'audit. La seule écriture
    possible est l'opt-in enregistrements (timer DVR), toujours derrière
    la confirmation à code traitée par le serveur.
- La page peut être servie en HTTPS (voir la section HTTPS ci-dessus —
  recommandé, et requis pour la dictée hors localhost) ou en HTTP simple,
  comme Emby lui-même sur le LAN.

## Dépannage (FAQ)

| Symptôme | Cause probable | Correctif |
|---|---|---|
| « Jeton invalide » / 403 côté app | Secret différent entre Emby et `config.json` | Reportez la même valeur des deux côtés, relancez l'app |
| « Aucune session Emby active pour cet usager » (projection 📺) | Aucune app Emby ouverte avec ce compte | Ouvrez l'app Emby sur l'appareil visé et connectez-vous avec le compte de l'usager, puis réessayez |
| Bouton 🎤 absent | Navigateur sans reconnaissance vocale, ou page servie en HTTP hors localhost | Utilisez Chrome/Edge et servez la page en HTTPS (section HTTPS) |
| « Micro bloqué » dans Chrome | Page en HTTP non localhost | Même correctif : HTTPS |
| La projection ne fait rien sur la TV | Client sans `DisplayContent` | Le plugin affiche un message à l'écran avec le titre (repli automatique) — comportement attendu |
| Certificat expiré (warning HTTPS revenu) | Copie du certificat du host non rafraîchie | Recopiez `fullchain.pem`/`privkey.pem` (automatisez par cron/hook), relancez l'app |
| L'usager est refusé à la connexion | Nom absent de la liste blanche, compte admin, ou compte sans mot de passe | Ajoutez le nom exact dans « Usagers autorisés » (les admins et les comptes sans mot de passe sont refusés par design) |
| Réponses qui s'arrêtent / « limite » | Plafond anti-spam atteint (5/min ou 150/jour par défaut) | Attendez la fenêtre suivante, ou ajustez les plafonds dans la page de configuration du plugin |

Les logs de l'app sont sur sa console (ou dans `journalctl -u llmai-chat`
sous systemd) ; les logs du plugin sont dans le journal Emby (lignes
`[LLM_AI] [CHAT-EXT]`).