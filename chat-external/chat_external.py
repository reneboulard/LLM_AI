#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Chat externe LLM AI — app compagnon autonome.

Un SEUL fichier, bibliothèque standard Python 3.7+ uniquement : aucun pip,
aucun serveur web à installer. Fonctionne sous Linux, macOS et Windows
(`py chat_external.py`).

Rôle :
  - sert une page de chat (embarquée ci-dessous) sur le LAN ;
  - authentifie ses usagers AUPRÈS D'EMBY (AuthenticateByName) — aucun
    deuxième stockage de mots de passe ;
  - relaie chaque tour vers l'endpoint plugin `POST /Plugins/LLMAI/ChatExternal`
    (secret partagé) et la navigation vers `POST /Plugins/LLMAI/Show` ;
  - détient l'historique de conversation CÔTÉ PAGE (l'endpoint est
    stateless) — le serveur Python ne stocke rien.

Sécurité :
  - l'appel vers Emby est TOUJOURS direct (aucun proxy) : la gate du plugin
    n'accepte que le loopback sans X-Forwarded-For, donc `emby_url` doit
    pointer sur `http://localhost:8096` (ou 127.0.0.1) de la machine Emby ;
  - cookie de session signé HMAC-SHA256 (HttpOnly, SameSite=Lax) ;
  - le secret partagé ne quitte jamais ce script et la page ;
  - lecture seule : aucun tool d'action de plugin n'est exposé ici, et
    tout contenu reste filtré par la policy parentale de l'usager (côté
    plugin).

Configuration : `config.json` (même dossier que le script), créé au premier
lancement avec un secret généré — à copier dans la page de config du plugin
(« Chat externe » → Secret partagé).
"""

import base64
import hashlib
import hmac
import json
import os
import secrets
import sys
import time
import urllib.error
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

BASE_DIR = os.path.dirname(os.path.abspath(__file__))
CONFIG_PATH = os.path.join(BASE_DIR, "config.json")
COOKIE_NAME = "llmai_ext_chat"
SESSION_TTL = 7 * 24 * 3600          # cookie valable 7 jours
MAX_BODY = 256 * 1024                # borne anti-abus sur les requêtes
MAX_HISTORY_TURNS = 40               # borné aussi côté plugin (re-filtrage)

# ---------------------------------------------------------------------------
# Configuration
# ---------------------------------------------------------------------------

def default_config():
    return {
        # URL Emby — DOIT être le loopback de la machine qui fait tourner
        # ce script ET Emby (gate du plugin : loopback, sans X-Forwarded-For).
        "emby_url": "http://localhost:8096",
        # URL Emby vue par le NAVIGATEUR de l'usager — sert uniquement à
        # construire les liens « ↗ fiche web » (l'usager est en général sur
        # un autre appareil que le serveur : localhost ne marcherait pas).
        # Vide = repli sur emby_url (bon uniquement si on chat depuis le
        # serveur lui-même). Exemple : "http://192.168.1.20:8096".
        # La gate du plugin n'utilise PAS cette valeur (emby_url reste le
        # loopback) ; elle ne fait que s'afficher dans le navigateur.
        "emby_public_url": "",
        # Port d'écoute de la page de chat (0.0.0.0 = LAN ; mettez 127.0.0.1
        # si vous ne discutez que depuis le serveur lui-même).
        "listen_host": "0.0.0.0",
        "listen_port": 8070,
        # Secret partagé — MÊME valeur que « Chat externe → Secret partagé »
        # dans la page de configuration du plugin LLM AI.
        "secret": "",
        # Délai d'attente des appels vers Emby / le LLM (secondes).
        "timeout_seconds": 180,
        # HTTPS optionnel (requis pour la dictée vocale 🎤 hors localhost) :
        # chemins d'un certificat/clé PEM. Vide = HTTP simple. Génération
        # d'un certificat auto-signé (une fois) :
        #   openssl req -x509 -newkey rsa:2048 -nodes -days 825 \
        #     -keyout key.pem -out cert.pem -subj "/CN=chat-emby"
        # Chrome affichera un avertissement certificat à la première
        # ouverture — accepter (« Procéder ») ; le micro marchera ensuite.
        "ssl_cert": "",
        "ssl_key": "",
        # Clé de signature des cookies de session (générée au premier
        # lancement ; régénérer déconnecte tout le monde).
        "session_secret": "",
    }


def load_config():
    """Lit config.json ; au premier lancement, crée un template avec un
    secret généré et s'arrête avec les instructions."""
    cfg = default_config()
    if not os.path.exists(CONFIG_PATH):
        cfg["secret"] = secrets.token_hex(16)
        cfg["session_secret"] = secrets.token_hex(32)
        with open(CONFIG_PATH, "w", encoding="utf-8") as f:
            json.dump(cfg, f, indent=2, ensure_ascii=False)
        print(__doc__ or "")
        print("=" * 72)
        print("Premier lancement : configuration créée dans")
        print("  " + CONFIG_PATH)
        print()
        print("1) Ouvrez la page de configuration du plugin LLM AI dans Emby,")
        print("   section « Chat externe » :")
        print("   - cochez « Activer le chat externe » ;")
        print("   - collez ce secret dans « Secret partagé » :")
        print("       " + cfg["secret"])
        print("   - listez les usagers autorisés (un nom d'usager Emby par")
        print("     ligne ; les administrateurs sont toujours refusés).")
        print("2) Relancez ce script.")
        print("=" * 72)
        sys.exit(0)

    with open(CONFIG_PATH, "r", encoding="utf-8") as f:
        try:
            user_cfg = json.load(f)
        except ValueError as e:
            print("config.json invalide : %s" % e)
            sys.exit(1)
    cfg.update(user_cfg)

    if not cfg.get("secret"):
        print("config.json : « secret » est vide — fail-closed. Collez le")
        print("secret généré par la page de configuration du plugin (section")
        print("« Chat externe ») ou régénérez config.json.")
        sys.exit(1)
    if not cfg.get("session_secret"):
        cfg["session_secret"] = secrets.token_hex(32)
        save_config(cfg)

    host = (cfg.get("emby_url") or "").split("//")[-1].split(":")[0]
    if host not in ("localhost", "127.0.0.1", "::1"):
        print("AVERTISSEMENT : emby_url (%s) n'est pas le loopback — la gate du"
              % cfg["emby_url"])
        print("plugin n'accepte que des appels directs depuis la machine Emby.")
        print("Ce script doit tourner SUR la machine Emby et appeler")
        print("http://localhost:8096.")
    return cfg


def save_config(cfg):
    with open(CONFIG_PATH, "w", encoding="utf-8") as f:
        json.dump(cfg, f, indent=2, ensure_ascii=False)


CFG = load_config()


# ---------------------------------------------------------------------------
# Appels Emby (TOUJOURS directs — aucun proxy, la gate l'exige)
# ---------------------------------------------------------------------------

_OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def emby_post(path, payload, extra_headers=None):
    """POST JSON vers Emby ; retourne (status, dict) — ne lève pas sur 4xx/5xx."""
    url = CFG["emby_url"].rstrip("/") + path
    data = json.dumps(payload).encode("utf-8")
    headers = {
        "Content-Type": "application/json",
        "Accept": "application/json",
    }
    if extra_headers:
        headers.update(extra_headers)
    req = urllib.request.Request(url, data=data, headers=headers, method="POST")
    try:
        with _OPENER.open(req, timeout=CFG["timeout_seconds"]) as resp:
            body = resp.read().decode("utf-8", "replace")
            return resp.status, json.loads(body) if body else {}
    except urllib.error.HTTPError as e:
        try:
            body = e.read().decode("utf-8", "replace")
        except Exception:
            body = ""
        return e.code, {}
    except (urllib.error.URLError, OSError, TimeoutError) as e:
        return 0, {"__network__": str(e)}


AUTH_HEADER = ("MediaBrowser Client=\"LLM AI Chat\", Device=\"Chat externe\", "
               "DeviceId=\"llmai-external-chat\", Version=\"1.0\"")


def emby_authenticate(username, password):
    """Authentifie auprès d'Emby ; retourne (nom_canonique, None) ou (None, msg)."""
    status, resp = emby_post(
        "/Users/AuthenticateByName",
        {"Username": username, "Pw": password},
        {"X-Emby-Authorization": AUTH_HEADER},
    )
    if status == 0:
        return None, "Emby injoignable (%s)." % resp.get("__network__", "?")
    if status != 200:
        return None, None  # identifiants refusés (pas de détail au client)
    name = (resp.get("User") or {}).get("Name") or ""
    if not name.strip():
        return None, "Réponse Emby inattendue."
    return name.strip(), None


def plugin_chat(user, message, history, session, tts=False):
    return emby_post("/Plugins/LLMAI/ChatExternal", {
        "Token": CFG["secret"],
        "User": user,
        "Message": message,
        "History": history,
        "Session": session or "",
        "Tts": bool(tts),
    })


def plugin_show(user, item_id):
    return emby_post("/Plugins/LLMAI/Show", {
        "Token": CFG["secret"],
        "User": user,
        "ItemId": item_id,
    })


# ---------------------------------------------------------------------------
# Cookies de session signés (HMAC-SHA256)
# ---------------------------------------------------------------------------

def make_cookie(username):
    payload = "%s|%d" % (username, int(time.time()) + SESSION_TTL)
    token = base64.urlsafe_b64encode(payload.encode("utf-8")).decode("ascii")
    sig = hmac.new(CFG["session_secret"].encode("utf-8"),
                   token.encode("ascii"), hashlib.sha256).hexdigest()
    return "%s=%s.%s; Path=/; HttpOnly; SameSite=Lax; Max-Age=%d" % (
        COOKIE_NAME, token, sig, SESSION_TTL)


def read_cookie(handler):
    """Retourne le nom d'usager si le cookie est valide et non expiré, sinon None."""
    raw = handler.headers.get("Cookie") or ""
    for part in raw.split(";"):
        part = part.strip()
        if not part.startswith(COOKIE_NAME + "="):
            continue
        value = part[len(COOKIE_NAME) + 1:]
        token, _, sig = value.rpartition(".")
        if not token or not sig:
            return None
        expected = hmac.new(CFG["session_secret"].encode("utf-8"),
                            token.encode("ascii"), hashlib.sha256).hexdigest()
        if not hmac.compare_digest(sig, expected):
            return None
        try:
            payload = base64.urlsafe_b64decode(token.encode("ascii")).decode("utf-8")
            username, _, expires = payload.rpartition("|")
            if not username or int(expires) < time.time():
                return None
            return username
        except Exception:
            return None
    return None


# ---------------------------------------------------------------------------
# Serveur HTTP
# ---------------------------------------------------------------------------

class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    server_version = "LLMAIExternalChat/1.0"

    # -- utilitaires --------------------------------------------------------

    def send_json(self, obj, status=200, extra_headers=None):
        body = json.dumps(obj).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        for k, v in (extra_headers or {}).items():
            self.send_header(k, v)
        self.end_headers()
        self.wfile.write(body)

    def read_body(self):
        """Lit et draine TOUJOURS le corps (keep-alive HTTP/1.1), puis
        décode le JSON — None si absent/trop gros/invalide."""
        try:
            length = int(self.headers.get("Content-Length") or 0)
        except ValueError:
            length = 0
        if length > MAX_BODY:
            self.close_connection = True  # corps non lu : pas de keep-alive
            return None
        raw = self.rfile.read(length) if length > 0 else b""
        if length <= 0:
            return None
        try:
            return json.loads(raw.decode("utf-8"))
        except (ValueError, UnicodeDecodeError):
            return None

    def log_message(self, fmt, *args):
        sys.stderr.write("[%s] %s\n" % (self.log_date_time_string(),
                                        fmt % args))

    # -- routes -------------------------------------------------------------

    def do_GET(self):
        if self.path == "/":
            body = PAGE_HTML.encode("utf-8")
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(body)
        elif self.path == "/api/whoami":
            user = read_cookie(self)
            if user:
                self.send_json({"user": user})
            else:
                self.send_json({"error": "non connecté"}, 401)
        else:
            self.send_json({"error": "introuvable"}, 404)

    def do_POST(self):
        # Drainer TOUJOURS le corps avant de répondre (même 401/404) :
        # sans quoi les octets non lus sont reinterprétés comme une nouvelle
        # requête sur la connexion keep-alive (désalignement 400).
        body = self.read_body()
        if self.path == "/api/login":
            self.handle_login(body)
        elif self.path == "/api/logout":
            self.send_json({"ok": True}, extra_headers={
                "Set-Cookie": "%s=; Path=/; HttpOnly; Max-Age=0" % COOKIE_NAME})
        elif self.path == "/api/chat":
            self.handle_chat(body)
        elif self.path == "/api/show":
            self.handle_show(body)
        else:
            self.send_json({"error": "introuvable"}, 404)

    # -- handlers -----------------------------------------------------------

    def handle_login(self, body):
        if not body:
            return self.send_json({"error": "Requête invalide."}, 400)
        username = (body.get("username") or "").strip()
        password = body.get("password") or ""
        if not username or not password:
            return self.send_json({"error": "Nom d'usager et mot de passe requis."}, 400)
        name, err = emby_authenticate(username, password)
        if err:
            return self.send_json({"error": err}, 502)
        if name is None:
            # Identifiants refusés — même message que l'identité soit
            # mauvaise ou que l'usager soit inconnu (pas d'indice).
            return self.send_json({"error": "Connexion refusée (identifiants Emby invalides)."}, 401)
        self.send_json({"ok": True, "user": name},
                       extra_headers={"Set-Cookie": make_cookie(name)})

    def handle_chat(self, body):
        user = read_cookie(self)
        if not user:
            return self.send_json({"error": "non connecté"}, 401)
        if not body:
            return self.send_json({"error": "Requête invalide."}, 400)

        message = (body.get("message") or "").strip()
        if not message:
            return self.send_json({"error": "Message vide."}, 400)
        if len(message) > 2000:  # même borne que le plugin
            return self.send_json({"error": "Message trop long (2000 caractères maximum)."}, 400)

        # Défense en profondeur : seuls user/assistant partent, plafonnés.
        history = []
        raw = body.get("history")
        if isinstance(raw, list):
            for t in raw[-MAX_HISTORY_TURNS:]:
                if not isinstance(t, dict):
                    continue
                role = t.get("role")
                content = t.get("content")
                if role in ("user", "assistant") and isinstance(content, str) and content.strip():
                    history.append({"role": role, "content": content[:2000]})
        session = body.get("session") or ""
        # Lecture automatique active côté page ? → le plugin formule la
        # réponse pour la synthèse vocale (bloc « canal de livraison »).
        tts = bool(body.get("tts"))

        status, resp = plugin_chat(user, message, history, session, tts)
        if status == 0:
            return self.send_json({"error": "Emby injoignable (%s)."
                                   % resp.get("__network__", "?")}, 502)
        # La gate répond toujours en JSON (200) avec un champ Error.
        self.send_json({
            "reply": resp.get("Reply"),
            "session": resp.get("Session") or "",
            "error": resp.get("Error"),
            # 🔑 Code de confirmation (enregistrements, human-in-the-loop) :
            # canal HORS BANDE — le plugin ne le met JAMAIS dans Reply, il
            # ne transite que par ce champ dédié (le LLM ne le voit pas) ;
            # affiché à l'écran par la page, jamais lu par le TTS.
            "confirm_code": resp.get("ConfirmCode"),
            # ⚠️ Notice VÉRIDIQUE du tour (refus de confirmation) : jointe
            # HORS BANDE par le plugin (ne passe jamais par le LLM) ; la
            # page l'affiche dans un encadré distinct, même si le modèle
            # embellit sa réponse. Jamais lu par le TTS.
            "notice": resp.get("Notice"),
        })

    def handle_show(self, body):
        user = read_cookie(self)
        if not user:
            return self.send_json({"error": "non connecté"}, 401)
        if not body:
            return self.send_json({"error": "Requête invalide."}, 400)
        item_id = (body.get("item_id") or "").strip()
        if not item_id:
            return self.send_json({"error": "Item manquant."}, 400)

        status, resp = plugin_show(user, item_id)
        if status == 0:
            return self.send_json({"error": "Emby injoignable (%s)."
                                   % resp.get("__network__", "?")}, 502)
        self.send_json({
            "ok": resp.get("Ok", False),
            "device": resp.get("Device"),
            "command": resp.get("Command"),
            "error": resp.get("Error"),
        })


# ---------------------------------------------------------------------------
# Page (HTML + CSS + JS embarqués — aucune ressource externe)
# ---------------------------------------------------------------------------

PAGE_HTML = r"""<!doctype html>
<html lang="fr">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Chat Emby</title>
<style>
  :root {
    --bg: #10141a; --panel: #1a2029; --panel2: #222a36; --border: #2e3846;
    --text: #e8ecf2; --muted: #8b98a8; --accent: #52b54b; --accent2: #3d8f38;
    --err: #e05c5c; --user: #2a4a6b;
  }
  * { box-sizing: border-box; }
  body {
    margin: 0; background: var(--bg); color: var(--text);
    font: 15px/1.5 -apple-system, "Segoe UI", Roboto, sans-serif;
    height: 100vh; display: flex; flex-direction: column;
  }
  header {
    display: flex; align-items: center; gap: 12px; padding: 10px 16px;
    background: var(--panel); border-bottom: 1px solid var(--border);
  }
  header h1 { font-size: 16px; margin: 0; flex: 1; }
  header .user { color: var(--muted); font-size: 13px; }
  button {
    background: var(--panel2); color: var(--text); border: 1px solid var(--border);
    border-radius: 6px; padding: 6px 12px; cursor: pointer; font-size: 13px;
  }
  button:hover { border-color: var(--accent); }
  button.primary { background: var(--accent2); border-color: var(--accent2); color: #fff; }
  button.primary:disabled { opacity: .5; cursor: default; }
  button.active { border-color: var(--accent); background: var(--accent2); color: #fff; }
  #login {
    margin: auto; background: var(--panel); border: 1px solid var(--border);
    border-radius: 10px; padding: 28px; width: min(340px, 90vw);
    display: flex; flex-direction: column; gap: 12px;
  }
  #login h2 { margin: 0; font-size: 18px; text-align: center; }
  #login input {
    background: var(--panel2); border: 1px solid var(--border); color: var(--text);
    border-radius: 6px; padding: 10px; font-size: 15px; width: 100%;
  }
  #login .err { color: var(--err); font-size: 13px; min-height: 18px; }
  #chat { display: none; flex: 1; flex-direction: column; min-height: 0; }
  #msgs { flex: 1; overflow-y: auto; padding: 16px; display: flex;
          flex-direction: column; gap: 10px; }
  .msg { max-width: 78%; padding: 9px 13px; border-radius: 12px;
         white-space: pre-wrap; word-wrap: break-word; }
  .msg.user { align-self: flex-end; background: var(--user); border-bottom-right-radius: 3px; }
  .msg.bot { align-self: flex-start; background: var(--panel); border: 1px solid var(--border);
             border-bottom-left-radius: 3px; }
  .msg.bot.err { border-color: var(--err); color: var(--err); }
  .msg code { background: var(--panel2); border-radius: 4px; padding: 1px 5px;
              font-family: ui-monospace, Consolas, monospace; font-size: 13px; }
  .msg pre { background: var(--panel2); border-radius: 6px; padding: 10px;
             overflow-x: auto; margin: 6px 0; }
  .msg pre code { background: none; padding: 0; }
  .showbtn {
    display: inline-flex; align-items: center; gap: 6px; margin: 4px 6px 0 0;
    background: var(--accent2); border-color: var(--accent2); color: #fff;
  }
  .weblink { color: var(--muted); text-decoration: none; font-size: 12px; }
  .speakbtn {
    display: block; margin-top: 6px; padding: 2px 8px; font-size: 12px; opacity: .55;
  }
  .speakbtn:hover { opacity: 1; }
  form { display: flex; gap: 8px; padding: 12px 16px;
         background: var(--panel); border-top: 1px solid var(--border); }
  form input {
    flex: 1; background: var(--panel2); border: 1px solid var(--border);
    color: var(--text); border-radius: 8px; padding: 11px 14px; font-size: 15px;
  }
  #mic.rec { background: #a33; border-color: #a33; animation: pulse 1.2s infinite; }
  @keyframes pulse { 50% { opacity: .55; } }
  #toast {
    position: fixed; bottom: 78px; left: 50%; transform: translateX(-50%);
    background: var(--panel2); border: 1px solid var(--accent); color: var(--text);
    border-radius: 8px; padding: 10px 18px; font-size: 14px; display: none;
    max-width: 90vw; z-index: 10;
  }
  #toast.err { border-color: var(--err); }
  /* 🔑 Code de confirmation (enregistrements) — brique VISUELLE distincte :
     grande, encadrée, jamais passée au TTS (le TTS ne lit que reply). */
  .confirmcode {
    display: inline-block; margin-top: 8px; padding: 10px 16px;
    background: var(--panel2); border: 1px dashed var(--accent);
    border-radius: 8px; font-size: 18px; font-weight: 700;
    letter-spacing: 4px; color: var(--text);
  }
  /* ⚠️ Notice VÉRIDIQUE du serveur (refus de confirmation) : encadré
     distinct, visible même si le modèle embellit sa réponse. */
  .sysnotice {
    display: block; margin-top: 8px; padding: 10px 14px;
    background: var(--panel2); border: 1px solid var(--err);
    border-radius: 8px; font-size: 14px; color: var(--text);
  }
</style>
</head>
<body>

<div id="login">
  <h2>🤖 Chat Emby</h2>
  <input id="u" type="text" placeholder="Nom d'usager Emby" autocomplete="username">
  <input id="p" type="password" placeholder="Mot de passe Emby" autocomplete="current-password">
  <div class="err" id="lerr"></div>
  <button class="primary" id="lbtn" onclick="login()">Se connecter</button>
</div>

<div id="chat">
  <header>
    <h1>🤖 Chat Emby</h1>
    <span class="user" id="who"></span>
    <button id="autotts" onclick="toggleAutoTts()" title="Lecture automatique des réponses">🔊 Auto</button>
    <button onclick="newSession()">➕ Nouvelle</button>
    <button onclick="logout()">Quitter</button>
  </header>
  <div id="msgs"></div>
  <form onsubmit="return send(event)">
    <input id="in" type="text" maxlength="2000" placeholder="Votre message…"
           autocomplete="off">
    <button type="button" id="mic" onclick="toggleMic()" title="Dictée vocale">🎤</button>
    <button class="primary" id="sbtn" type="submit">Envoyer</button>
  </form>
</div>
<div id="toast"></div>

<script>
"use strict";
var user = "", session = "", chatHistory = [], busy = false;

// -- utilitaires ------------------------------------------------------------
function $(id) { return document.getElementById(id); }
// Langue de la voix (dictée 🎤 + synthèse 🔊) : celle du navigateur de
// l'usager (normalement alignée avec SA langue), repli fr-FR. Ex.
// « fr-CA », « en-US » ; le navigateur ne reconnaît/parle que dans une
// langue disponible sur l'appareil.
var SPEECH_LANG = (navigator.languages && navigator.languages[0])
                  || navigator.language || "fr-FR";
function esc(s) {
  return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
          .replace(/"/g, "&quot;");
}
var toastTimer = null;
function toast(msg, isErr) {
  var t = $("toast");
  t.textContent = msg;
  t.className = isErr ? "err" : "";
  t.style.display = "block";
  clearTimeout(toastTimer);
  toastTimer = setTimeout(function () { t.style.display = "none"; }, 4000);
}
function storageKey() { return "llmai_ext_chat_" + user; }
function persist() {
  try { localStorage.setItem(storageKey(),
        JSON.stringify({ session: session, chatHistory: chatHistory.slice(-60) })); }
  catch (e) { /* stockage indisponible : OK en mémoire */ }
}
function restore() {
  session = ""; chatHistory = [];
  try {
    var raw = localStorage.getItem(storageKey());
    if (!raw) return;
    var d = JSON.parse(raw);
    if (d && Array.isArray(d.chatHistory)) {
      chatHistory = d.chatHistory.filter(function (t) {
        return t && (t.role === "user" || t.role === "assistant") && t.content; });
      session = d.session || "";
    }
  } catch (e) { session = ""; chatHistory = []; }
}
function scrollDown() { var m = $("msgs"); m.scrollTop = m.scrollHeight; }

// -- rendu Markdown minimal + liens profonds ---------------------------------
// Le plugin émet [Titre](/web/index.html#!/item?id=ID&serverId=…). Ici, un
// clic sur le bouton projette la fiche sur le client Emby actif de l'usager
// (POST /Plugins/LLMAI/Show via /api/show) ; ↗ ouvre la fiche web.
var SHOW_RE = /\[([^\]]+)\]\(\/web\/index\.html#!\/item\?id=([A-Za-z0-9\-]+)(?:&(?:amp;)?serverId=[A-Za-z0-9\-]+)?(?:&(?:amp;)?asSeries=true)?\)/g;

function renderMd(src) {
  var placeholders = [];
  var text = src.replace(SHOW_RE, function (_, title, id) {
    placeholders.push({ title: title, id: id });
    return "@@SHOW" + (placeholders.length - 1) + "@@";
  });
  var html = esc(text);
  var fences = html.split(/```[a-z]*\n?/);
  html = fences.map(function (chunk, i) {
    return (i % 2) ? "<pre><code>" + chunk.replace(/\n$/, "") + "</code></pre>"
                   : chunk;
  }).join("");
  html = html.replace(/`([^`\n]+)`/g, "<code>$1</code>");
  html = html.replace(/\*\*([^*\n]+)\*\*/g, "<b>$1</b>");
  html = html.replace(/(^|\s)\*([^*\n]+)\*(?=\s|$|[.,!?;:)])/g, "$1<i>$2</i>");
  html = html.replace(/(^|\s)(https?:\/\/[^\s<]+[^\s<.,!?;:)])/g,
                      '$1<a href="$2" target="_blank" rel="noopener noreferrer">$2</a>');
  html = html.replace(/@@SHOW(\d+)@@/g, function (_, idx) {
    var ph = placeholders[+idx];
    return "<button class=\"showbtn\" onclick=\"showItem('" + ph.id +
           "')\">📺 " + esc(ph.title) + "</button>" +
           "<a class=\"weblink\" href=\"" + esc(serverBase) +
           "/web/index.html#!/item?id=" + ph.id +
           "\" target=\"_blank\" rel=\"noopener noreferrer\">↗ fiche web</a>";
  });
  return html;
}

function addBubble(role, text, isErr) {
  var div = document.createElement("div");
  div.className = "msg " + (role === "user" ? "user" : "bot" + (isErr ? " err" : ""));
  if (role === "user") { div.textContent = text; }
  else {
    div.innerHTML = renderMd(text || "");
    // 🔊 lecture à voix haute (speechSynthesis — marche aussi hors HTTPS ;
    // seul le contenu texte est lu, les boutons de projection sont écartés)
    if (!isErr && text && window.speechSynthesis) {
      var sb = document.createElement("button");
      sb.className = "speakbtn"; sb.textContent = "🔊"; sb.dataset.label = "🔊";
      sb.title = "Lire à voix haute";
      sb.addEventListener("click", function () { speakText(text, sb); });
      div.appendChild(sb);
    }
  }
  $("msgs").appendChild(div);
  scrollDown();
  return div;
}

// -- lecture à voix haute (Web Speech Synthesis) ------------------------------
var speakingBtn = null;
var autoTts = false;
function stopSpeak() {
  try { window.speechSynthesis.cancel(); } catch (e) {}
  if (speakingBtn) {
    speakingBtn.textContent = speakingBtn.dataset.label || "🔊";
    speakingBtn = null;
  }
}
function ttsKey() { return storageKey() + "_tts"; }
function updateAutoTtsBtn() {
  $("autotts").className = autoTts ? "active" : "";
}
function toggleAutoTts() {
  // Pendant une lecture auto en cours : un clic = arrêt (et pas re-démarrage).
  if (speakingBtn === $("autotts")) { stopSpeak(); return; }
  autoTts = !autoTts;
  try { localStorage.setItem(ttsKey(), autoTts ? "1" : "0"); } catch (e) {}
  updateAutoTtsBtn();
  toast(autoTts ? "🔊 Lecture automatique activée." : "Lecture automatique désactivée.");
}
// Markdown → texte oral : les liens [Titre](url) ne gardent que « Titre »,
// les blocs de code et URL restantes sont écartés (pas de charabia lu).
function cleanSpeech(src) {
  return src
    .replace(/```[a-z]*[\s\S]*?```/g, " ")
    .replace(SHOW_RE, function (_, title) { return title; })
    .replace(/`([^`\n]+)`/g, "$1")
    .replace(/\*\*([^*\n]+)\*\*/g, "$1")
    .replace(/(^|\s)\*([^*\n]+)\*(?=\s|$|[.,!?;:)])/g, "$1$2")
    .replace(/https?:\/\/\S+/g, " ")
    .replace(/^#+\s*/gm, " ")
    .replace(/^[-*•]\s*/gm, " ")
    .replace(/[ \t]+/g, " ")
    .replace(/\n{2,}/g, " ")
    .trim();
}
// Chrome coupe les énoncés très longs : lecture par tranches de phrases.
function chunkSpeech(text, maxLen) {
  var parts = [], cur = "";
  (text.match(/[^.!?;\n]+[.!?;]*\s*/g) || [text]).forEach(function (s) {
    if (cur.length + s.length > maxLen && cur) { parts.push(cur.trim()); cur = ""; }
    cur += s;
  });
  if (cur.trim()) parts.push(cur.trim());
  return parts;
}
function speakText(raw, btn) {
  if (!window.speechSynthesis) return;
  if (speakingBtn === btn) { stopSpeak(); return; }
  stopSpeak();
  var text = cleanSpeech(raw);
  if (!text) { toast("⚠️ Rien à lire dans cette réponse.", true); return; }
  speakingBtn = btn;
  btn.textContent = "⏹";
  var parts = chunkSpeech(text, 180), i = 0;
  function next() {
    if (speakingBtn !== btn) return;            // annulé entre-temps
    if (i >= parts.length) { stopSpeak(); return; }
    var u = new SpeechSynthesisUtterance(parts[i++]);
    u.lang = SPEECH_LANG; u.rate = 1.0; u.pitch = 1.0;
    u.onend = next;
    u.onerror = function () { stopSpeak(); };
    window.speechSynthesis.speak(u);
  }
  next();
}

// -- API ---------------------------------------------------------------------
var serverBase = "";   // rempli par boot() (origine de la page → Emby absolu)

function api(path, body, cb) {
  var xhr = new XMLHttpRequest();
  xhr.open("POST", path, true);
  xhr.setRequestHeader("Content-Type", "application/json");
  xhr.onload = function () {
    var data = {};
    try { data = JSON.parse(xhr.responseText); } catch (e) {}
    if (xhr.status === 401 && path !== "/api/login") { showLogin(); return; }
    cb(data, xhr.status);
  };
  xhr.onerror = function () { cb({ error: "Serveur injoignable." }, 0); };
  xhr.send(JSON.stringify(body || {}));
}

function login() {
  var u = $("u").value.trim(), p = $("p").value;
  if (!u || !p) { $("lerr").textContent = "Nom et mot de passe requis."; return; }
  $("lbtn").disabled = true;
  api("/api/login", { username: u, password: p }, function (d, st) {
    $("lbtn").disabled = false;
    if (st === 200 && d.ok) { enterChat(d.user); }
    else { $("lerr").textContent = d.error || "Connexion refusée."; }
  });
}

function logout() {
  api("/api/logout", {}, function () { showLogin(); });
}

function enterChat(name) {
  user = name;
  $("login").style.display = "none";
  $("chat").style.display = "flex";
  $("who").textContent = user;
  try { autoTts = localStorage.getItem(ttsKey()) === "1"; } catch (e) {}
  updateAutoTtsBtn();
  restore();
  $("msgs").innerHTML = "";
  chatHistory.forEach(function (t) { addBubble(t.role, t.content); });
  if (!chatHistory.length) {
    addBubble("assistant",
              "Bonjour " + user + " ! Posez vos questions sur la médiathèque, "
              + "ce qui passe ce soir, ou demandez-moi une suggestion. 🎬");
  }
  scrollDown();
  $("in").focus();
}

function showLogin() {
  user = ""; chatHistory = []; session = "";
  $("chat").style.display = "none";
  $("login").style.display = "flex";
  $("p").value = "";
  $("lerr").textContent = "";
}

function newSession() {
  session = ""; chatHistory = [];
  try { localStorage.removeItem(storageKey()); } catch (e) {}
  $("msgs").innerHTML = "";
  addBubble("assistant", "Nouvelle conversation. Comment puis-je aider ? 🎬");
  $("in").focus();
}

function send(ev) {
  if (ev && ev.preventDefault) { ev.preventDefault(); }
  if (busy) return false;
  var text = $("in").value.trim();
  if (!text) return false;
  busy = true;
  stopSpeak();
  $("sbtn").disabled = true;
  $("in").value = "";
  addBubble("user", text);
  var outgoing = chatHistory.concat([{ role: "user", content: text }]);
  api("/api/chat", { message: text, history: chatHistory, session: session,
      tts: autoTts },
      function (d) {
        busy = false;
        $("sbtn").disabled = false;
        if (d.error) {
          addBubble("assistant", "⚠️ " + d.error, true);
        } else {
          chatHistory = outgoing.concat([{ role: "assistant", content: d.reply || "" }]);
          session = d.session || session;
          persist();
          var bubble = addBubble("assistant", d.reply || "(réponse vide)");
          // 🔑 Code de confirmation (enregistrements, human-in-the-loop) :
          // canal HORS BANDE — le code n'apparaît jamais dans le texte du
          // LLM (il ne le connaît pas), il est affiché ICI et l'usager le
          // fournit ensuite dans son message. Ne jamais le lire à voix
          // haute automatiquement (le TTS ne lit que d.reply).
          if (d.confirm_code) {
            var chip = document.createElement("div");
            chip.className = "confirmcode";
            chip.textContent = "🔑 Code de confirmation : " + d.confirm_code;
            bubble.appendChild(chip);
          }
          // ⚠️ Notice VÉRIDIQUE (refus de confirmation) — canal déterministe
          // du serveur : affichée TELLE QUELLE, même si le texte du LLM dit
          // autre chose. Jamais passée au TTS.
          if (d.notice) {
            var box = document.createElement("div");
            box.className = "sysnotice";
            box.textContent = d.notice;
            bubble.appendChild(box);
          }
          if (autoTts) { speakText(d.reply || "", $("autotts")); }
        }
        scrollDown();
        $("in").focus();
      });
  return false;
}

function showItem(itemId) {
  api("/api/show", { item_id: itemId }, function (d) {
    if (d.error) { toast("⚠️ " + d.error, true); return; }
    toast("📺 Projeté sur « " + (d.device || "?") + " » (" + d.command + ")");
  });
}

// -- dictée vocale (Web Speech API — Chrome/Edge ; bouton masqué sinon) ------
var recog = null;
function setupVoice() {
  // Contexte sécurisé requis : la reconnaissance vocale de Chrome est
  // bloquée sur http://<ip-LAN> (seuls localhost/HTTPS la permettent).
  if (!window.isSecureContext) {
    $("mic").style.display = "none";
    return;
  }
  var SR = window.SpeechRecognition || window.webkitSpeechRecognition;
  if (!SR) { $("mic").style.display = "none"; return; }
  recog = new SR();
  recog.lang = SPEECH_LANG;
  recog.interimResults = false;
  recog.maxAlternatives = 1;
  recog.onresult = function (ev) {
    var text = (ev.results[0][0].transcript || "").trim();
    if (text) { $("in").value = text; send(); }  // envoi auto (choix usager)
  };
  recog.onend = function () { $("mic").classList.remove("rec"); };
  recog.onerror = function (ev) {
    $("mic").classList.remove("rec");
    if (ev && ev.error === "not-allowed") {
      toast("🎤 Micro refusé — autorisez-le dans la barre d'adresse.", true);
    } else if (ev && ev.error && ev.error !== "aborted" && ev.error !== "no-speech") {
      toast("🎤 Dictée indisponible (" + ev.error + ").", true);
    }
  };
}
function toggleMic() {
  if (!recog) return;
  if ($("mic").classList.contains("rec")) { recog.stop(); return; }
  $("mic").classList.add("rec");
  try { recog.start(); } catch (e) {
    $("mic").classList.remove("rec");
    toast("🎤 Dictée indisponible ici (HTTPS requis pour le micro).", true);
  }
}

$("in").addEventListener("keydown", function (ev) {
  if (ev.key === "Enter" && !ev.shiftKey) { ev.preventDefault(); send(ev); }
});
$("p").addEventListener("keydown", function (ev) {
  if (ev.key === "Enter") { ev.preventDefault(); login(); }
});

// -- démarrage ----------------------------------------------------------------
(function boot() {
  // URL Emby absolue pour « ↗ fiche web » : fournie par le serveur via la
  // balise meta ci-dessous (emby_public_url, repli emby_url — config.json).
  var meta = document.querySelector('meta[name="emby-base"]');
  if (meta) serverBase = meta.getAttribute("content");
  setupVoice();
  var xhr = new XMLHttpRequest();
  xhr.open("GET", "/api/whoami", true);
  xhr.onload = function () {
    if (xhr.status === 200) {
      try { enterChat(JSON.parse(xhr.responseText).user); }
      catch (e) { showLogin(); }
    } else { showLogin(); }
  };
  xhr.onerror = function () { showLogin(); };
  xhr.send();
})();
</script>
</body>
</html>
"""

# URL Emby injectée dans la page (pour le lien « ↗ fiche web »).
PAGE_HTML = PAGE_HTML.replace(
    "</head>",
    '<meta name="emby-base" content="%s"></head>' % (CFG.get("emby_public_url") or CFG["emby_url"]).rstrip("/"))


# ---------------------------------------------------------------------------
# Démarrage
# ---------------------------------------------------------------------------

def main():
    host = CFG.get("listen_host", "0.0.0.0")
    port = int(CFG.get("listen_port", 8070))
    server = ThreadingHTTPServer((host, port), Handler)
    server.daemon_threads = True

    # HTTPS optionnel — requis pour la dictée vocale (micro) hors localhost.
    scheme = "http"
    cert = CFG.get("ssl_cert") or ""
    key = CFG.get("ssl_key") or ""
    if cert and key:
        import ssl
        ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        ctx.load_cert_chain(cert, key)
        server.socket = ctx.wrap_socket(server.socket, server_side=True)
        scheme = "https"
    elif cert or key:
        print("AVERTISSEMENT : ssl_cert et ssl_key doivent être définis "
              "ensemble — HTTPS désactivé.")

    shown = host if host != "0.0.0.0" else "0.0.0.0 (LAN)"
    print("=" * 72)
    print("Chat externe LLM AI — app compagnon")
    print("  Page de chat : %s://<cette-machine>:%d/  (écoute : %s)"
          % (scheme, port, shown))
    print("  Emby (gate)  : %s" % CFG["emby_url"])
    if CFG.get("emby_public_url"):
        print("  Emby (liens) : %s" % CFG["emby_public_url"])
    else:
        print("  Liens fiche web : emby_url (localhost) — mettez emby_public_url")
        print("  dans config.json si les usagers ouvrent le chat depuis un autre")
        print("  appareil que le serveur.")
    print("  Config       : %s" % CONFIG_PATH)
    print("  Ctrl+C pour arrêter.")
    print("=" * 72)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\nArrêté.")
    finally:
        server.server_close()


if __name__ == "__main__":
    main()