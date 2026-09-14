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