# Chat externe LLM AI — app compagnon

App cliente autonome pour le **chat externe** du plugin Emby LLM AI
(endpoints `POST /Plugins/LLMAI/ChatExternal` et `POST /Plugins/LLMAI/Show`).

Un **seul fichier Python** (`chat_external.py`), bibliothèque standard
uniquement : rien à compiler, rien à installer avec pip. Fonctionne sous
Linux, macOS et Windows. Aucun serveur web (nginx/openresty/PHP) requis.

## Ce que fait l'app

- Sert une **page de chat** (embarquée dans le script) sur le LAN ;
- Authentifie ses usagers **auprès d'Emby** (`AuthenticateByName`) — aucun
  deuxième stockage de mots de passe ;
- Relaie chaque tour vers le plugin (secret partagé) ; l'historique de
  conversation est détenu **par la page** (l'endpoint est stateless) ;
- Les titres cités par l'agent sont cliquables : **📺 projette la fiche sur
  le client Emby actif de l'usager** (`Show` → `DisplayContent`), **↗**
  ouvre la fiche web dans un nouvel onglet ;
- **Dictée vocale 🎤** (Chrome/Edge — reconnaissance native du navigateur,
  fr-FR) : la transcription est envoyée automatiquement ; le bouton est
  masqué sur les navigateurs sans support.

Tout le contenu reste filtré par la **policy parentale** de l'usager côté
plugin ; le chat est en lecture seule (aucun tool d'action, aucun audit).

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
#    - listez les usagers autorisés (un nom d'usager Emby par ligne)

# 3. Relancez
python3 chat_external.py
```

Ouvrez ensuite `http://<machine-emby>:8070/` depuis un navigateur du foyer
(tablette, téléphone, PC) et connectez-vous avec des identifiants **Emby**.

## Configuration (`config.json`)

| Clé | Défaut | Rôle |
|---|---|---|
| `emby_url` | `http://localhost:8096` | URL Emby — **doit rester le loopback** de la machine Emby |
| `listen_host` | `0.0.0.0` | `0.0.0.0` = LAN ; `127.0.0.1` si vous ne discutez que depuis le serveur |
| `listen_port` | `8070` | Port de la page de chat |
| `secret` | (généré) | Même valeur que « Chat externe → Secret partagé » dans Emby |
| `timeout_seconds` | `180` | Délai d'attente des appels vers Emby / le LLM |
| `session_secret` | (généré) | Clé de signature des cookies de session ; la régénérer déconnecte tout le monde |
| `ssl_cert` / `ssl_key` | (vides) | HTTPS optionnel — **requis pour la dictée vocale 🎤 hors localhost** (le micro est bloqué par Chrome sur du HTTP) ; auto-signé accepté |

Pour révoquer l'accès : changez le secret **des deux côtés** (Emby +
`config.json`), ou désactivez le chat externe dans Emby.

## HTTPS : trois façons de se connecter sans warning

La dictée vocale 🎤 exige un **contexte sécurisé** (HTTPS ou localhost).
Trois variantes, de la plus simple à la plus autonome :

1. **Copier un certificat existant du host** (le plus simple) — si le
   serveur (ou votre pare-feu/routeur) a déjà un certificat valide
   (Let's Encrypt…), copiez le certificat **fullchain** (feuille +
   intermédiaires) et la clé sur la machine de l'app, puis pointez
   `ssl_cert`/`ssl_key` dessus :

   ```json
   "ssl_cert": "/chemin/vers/fullchain.pem",
   "ssl_key":  "/chemin/vers/privkey.pem"
   ```

   - Le nom dans l'URL doit être couvert par le certificat
     (`https://chat.votredomaine.tld:8070`) — un **wildcard**
     (`*.votredomaine.tld`) se réutilise tel quel ;
   - le nom doit résoudre vers l'IP LAN du serveur : un
     enregistrement local du DNS du foyer suffit (ex. *Host Override*
     pfSense/Unbound, fichier `hosts` des appareils) — rien de public,
     aucun port ouvert ;
   - si le certificat du host se renouvelle (90 j pour Let's Encrypt),
     rafraîchissez la copie (cron ou hook post-renewal), sinon l'app
     servira un certificat expiré.

2. **Certificat Let's Encrypt dédié** (challenge DNS-01, ex. paquet ACME
   de pfSense) pour `chat.votredomaine.tld` — renouvellement automatique
   directement sur la machine de l'app, même résolution locale.

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

## Sécurité

- L'appel vers Emby est **toujours direct** (aucun proxy HTTP n'est
  consulté) : toute requête passée par un reverse proxy porte un
  `X-Forwarded-For` et est rejetée par la gate du plugin.
- Cookie de session signé HMAC-SHA256 (`HttpOnly`, `SameSite=Lax`).
- Le secret partagé ne quitte jamais le script et la page.
- Les administrateurs Emby sont **toujours refusés** sur le chat externe
  (leur chat dédié reste la page de configuration du plugin).
- La page est servie en HTTP simple — comme Emby lui-même sur le LAN. Pour
  un accès hors du foyer, placez l'app derrière votre propre reverse proxy
  HTTPS ; seuls les appels de l'app **vers** Emby doivent rester directs.
- Lecture seule côté plugin : une fuite du secret ne donne aucune capacité
  d'écriture sur le serveur.