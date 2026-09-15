# LLM AI external chat — companion app

A standalone client app for the **external chat** of the Emby LLM AI
plugin (endpoints `POST /Plugins/LLMAI/ChatExternal` and
`POST /Plugins/LLMAI/Show`).

A **natural conversation with your Emby server**: talk to it by keyboard
or **by voice** — speech-to-text 🎤 to ask, text-to-speech 🔊 for the
answer, like a living-room assistant over your household media library.

A **single Python file** (`chat_external.py`), standard library only:
nothing to compile, nothing to install with pip, no web server
(nginx/openresty/PHP) required. Works on Linux, macOS and Windows.

> ⚠️ **A local-network app.** Never expose this app directly to the
> Internet: it is designed for the household LAN (see
> [Security](#security)).

## What the app does

- Serves a **chat page** (embedded in the script) on the LAN;
- **Same credentials as Emby**: sign-in goes through
  `AuthenticateByName` — there is **no second password store**. Only
  accounts **with a password** can sign in (a passwordless account is
  rejected by the app);
- **Administrators never go through this chat** (security best
  practice): any account with Emby admin rights is **always rejected**,
  fail-closed — a privileged account should not travel over a user
  channel. Administrators' chat remains the plugin configuration page,
  with its own safeguards;
- **Same rights as Emby**: every answer only cites what the user's policy
  allows — library access (`EnableAllFolders` / `EnabledFolders`),
  parental control (`MaxParentalRating`, blocked unrated items, blocked
  tags). The app never widens access: the plugin enforces the Emby policy
  of the signed-in user;
- Titles cited by the agent are clickable:
  - **📺 Cast to screen** — the title's detail page is displayed
    instantly on an **Emby app open with that user's account** (TV,
    tablet, another PC…), via the `DisplayContent` command; if the client
    does not support it, an on-screen message carries the title
    (`DisplayMessage` fallback);
  - **↗ Web page** — opens the Emby detail page in a new browser tab;
- **Agent-driven control of the active client**: the LLM can, through the
  `client_command` tool, act on the user's active Emby client — project a
  detail page, **start playback of a title**, pause / resume / stop, set
  the **volume**, mute / unmute, return **home**. Non-destructive only,
  and only on THEIR client;
- **Speech-to-text 🎤** and **text-to-speech 🔊** — see
  [Voice conversation](#voice-conversation-🎤-🔊);
- **Built-in anti-spam on the plugin side**: each user is rate-limited
  (5 turns per minute and 150 per day by default, adjustable in the
  plugin configuration page, "External chat" section) — the LLM is never
  saturated, even if the page is open on several devices.

All content stays filtered by the user's **parental policy** on the
plugin side. Server-side, the chat is **read-only** (no action tools over
the media library, no audit): the only possible actions are driving the
user's **active Emby client** (detail-page projection, play / pause /
volume) — never any server modification, never anything on another
device.

## Installation

The script must run **on the same machine as Emby**: the plugin gate only
accepts direct calls from the loopback, without `X-Forwarded-For`.

```bash
# 1. First launch: creates config.json with a generated secret
python3 chat_external.py        # Windows: py chat_external.py

# 2. In Emby: dashboard → Plugins → LLM AI → "External chat" section
#    - tick "Enable external chat"
#    - paste the secret shown at first launch into "Shared secret"
#      (or click "Generate" in Emby and copy the value into config.json)
#    - list the allowed users (one Emby username per line — accounts
#      WITH a password; admins are rejected)

# 3. Restart
python3 chat_external.py
```

Then open `http://<emby-machine>:8070/` from a browser in the household
(tablet, phone, PC) and sign in with your **Emby** credentials (same
username and password as the Emby app).

## Configuration (`config.json`)

| Key | Default | Purpose |
|---|---|---|
| `emby_url` | `http://localhost:8096` | Emby URL — **must stay the loopback** of the Emby machine (plugin gate) |
| `emby_public_url` | (empty) | Emby URL **as seen by the user's browser** — used by "↗ web page" links when chatting from a device other than the server (e.g. `http://192.168.1.20:8096`); empty = falls back to `emby_url` |
| `listen_host` | `0.0.0.0` | `0.0.0.0` = LAN; `127.0.0.1` if you only chat from the server |
| `listen_port` | `8070` | Chat page port |
| `secret` | (generated) | Same value as "External chat → Shared secret" in Emby |
| `timeout_seconds` | `180` | Timeout for calls to Emby / the LLM |
| `session_secret` | (generated) | Key signing the session cookies; regenerating it signs everyone out |
| `ssl_cert` / `ssl_key` | (empty) | Optional HTTPS — **required for speech-to-text 🎤 off localhost** (the microphone is blocked by Chrome over plain HTTP); self-signed accepted |

To revoke access: change the secret **on both sides** (Emby +
`config.json`), or disable the external chat in Emby.

## Voice conversation 🎤 🔊

This is what the app is for: **talking to the server and being answered
out loud**, with no keyboard or screen needed for some household users.

### Speech-to-text 🎤

- **🎤** button in the input bar: press, speak, the transcription is sent
  automatically (native browser speech recognition, Chrome/Edge — in the
  user's browser language, e.g. "fr-CA", "en-US");
- The button is **hidden** on browsers without support (Firefox…);
- ⚠️ Browsers only grant the microphone on a **secure context**: HTTPS,
  or a connection from the server itself (localhost). See the
  [HTTPS section](#https-highly-recommended).

### Text-to-speech 🔊

- Every answer has a **🔊** button to have it read by the browser's
  speech synthesis (works over plain HTTP too — only **dictation**
  requires a secure context);
- The **🔊 Auto** button (header) turns on automatic read-aloud of every
  answer; the setting is remembered per browser.

### Spoken-answer mode

When auto-read is on, the app tells the plugin the delivery channel on
every send (`Tts` field in the `ChatExternal` request); the LLM then
phrases its answer **for the EAR** — short sentences, times spelled out,
no lists/tables/URLs — while keeping exact titles (the projection buttons
still render). Toggling 🔊 mid-conversation cleanly alternates the two
wordings (the signal is per turn). Server-side, every spoken turn logs
"Delivery channel: speech synthesis".

## HTTPS: highly recommended

**Text-to-speech 🔊** works everywhere, but **speech-to-text 🎤** is only
offered by Chrome/Edge on a **secure context** (HTTPS, or a connection
from the server itself). HTTPS is therefore **highly recommended** as
soon as you want a real voice conversation — and it keeps the
conversation from travelling in clear text over the LAN. With a
**valid certificate**, Chrome accepts the connection **without a
murmur**: no warning screen to click on every visit — a real comfort on
household devices (TV, shared tablet).

Two families of setup, your choice:

### A. TLS inside the Python app (`ssl_cert` / `ssl_key` options)

Three variants, from the simplest to the most self-contained:

1. **Copy an existing certificate from the host** (simplest) — if the
   server already holds a valid certificate (Let's Encrypt…), copy the
   **fullchain** certificate (leaf + intermediates) and the key to the
   app machine, then point `ssl_cert`/`ssl_key` at them:

   ```json
   "ssl_cert": "/path/to/fullchain.pem",
   "ssl_key":  "/path/to/privkey.pem"
   ```

   - The name in the URL must be covered by the certificate
     (`https://chat.yourdomain.tld:8070`) — a **wildcard**
     (`*.yourdomain.tld`) is reusable as is;
   - the name must resolve to the server's LAN IP: a local entry in the
     household DNS (router/DNS server local zone, the devices' `hosts`
     file) is enough — nothing public, no open port;
   - if the host certificate renews (90 days for Let's Encrypt), refresh
     the copy (cron or post-renewal hook), otherwise the app will serve
     an expired certificate.

2. **Dedicated Let's Encrypt certificate** (DNS-01 challenge) for
   `chat.yourdomain.tld` — automatic renewal directly on the app machine,
   same local resolution.

3. **Self-signed** (this doc's default): works everywhere but the
   browser shows a one-time warning to accept. Generation:

   ```bash
   openssl req -x509 -newkey rsa:2048 -nodes -days 825 \
     -keyout chat.key -out chat.crt \
     -subj "/CN=chat-local" \
     -addext "subjectAltName=IP:192.168.x.x,DNS:chat-local"
   ```

   (replace `192.168.x.x` with the server's LAN IP; put the two files in
   `ssl_key`/`ssl_cert`).

### B. TLS in a reverse proxy

If the household already runs an HTTPS reverse proxy (nginx, Caddy,
openresty…) holding a certificate **valid for its name**, putting the app
behind it may be the simplest: the proxy terminates TLS (already-valid
certificate, no warning) and relays the app over plain HTTP — in that
case force `listen_host` to `127.0.0.1` in `config.json` so the page only
answers through the proxy, and leave `ssl_cert`/`ssl_key` empty.

Two rules to remember:

- only the **browser → app** leg goes through the proxy; the **app →
  Emby** call must stay **direct** (the plugin gate rejects any proxied
  call to Emby — see [Security](#security));
- if the app becomes reachable from outside the household through that
  proxy, protect it (proxy authentication) — see
  [Security](#security).

## Running the app at startup

### Linux (systemd)

So the chat survives reboots and restarts on its own after a crash, a
small systemd unit is enough:

```ini
# /etc/systemd/system/llmai-chat.service
[Unit]
Description=LLM AI — external chat (Emby companion app)
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

(adapt the two paths to where you put the script and its `config.json`;
the service user must be able to read the SSL key.)

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now llmai-chat.service
journalctl -u llmai-chat -f        # follow the logs
```

The service starts on its own at boot and re-reads `config.json` at every
(re)start — changing the certificate or the secret boils down to a
`systemctl restart llmai-chat`.

### Windows

Two approaches, depending on whether the machine stays signed in:

**a) Task Scheduler (at boot, without opening a session) —
recommended:**

```bat
:: once, in an administrator console:
schtasks /Create /TN "LLMAI Chat" /RU SYSTEM /SC ONSTART /DELAY 0000:30 ^
  /TR "\"C:\chat-external\chat_external.bat\""
```

with a minimal `chat_external.bat` in the same folder:

```bat
@echo off
cd /d C:\chat-external
py chat_external.py >> chat_external.log 2>&1
```

(output is logged to `chat_external.log`, next to the script).

**b) Startup folder** (simpler, but starts only at session sign-in):
`Win+R` → `shell:startup` → copy a shortcut to `chat_external.bat` there
(without `/RU SYSTEM` above, the plain .bat is enough).

Re-reading the configuration after a change means stopping and starting
the task again (`schtasks /End /TN "LLMAI Chat"` then `schtasks /Run /TN
"LLMAI Chat"`) or signing out and back in (Startup folder).

### macOS (launchd)

A per-user `LaunchAgent`, in `~/Library/LaunchAgents/`:

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

## Security

- **Local network only.** Never expose the app **directly** to the
  Internet (no port-forward rule to 8070). For access from outside the
  household, put the app behind **your own** HTTPS reverse proxy (with
  authentication); only the app's calls **toward** Emby must stay direct.
- The call to Emby is **always direct** (no HTTP proxy is consulted): any
  request passing through a reverse proxy carries an `X-Forwarded-For`
  and is rejected by the plugin gate.
- **Emby authentication, no duplicate**: same credentials as the Emby
  app; no password stored by the app. Only accounts with a password can
  sign in; Emby administrators are **always rejected** on the external
  chat (their dedicated chat remains the plugin configuration page).
- **Emby rights honored**: cited or projected content passes the user's
  policy (library access + parental control) as defined in Emby.
- Session cookie signed HMAC-SHA256 (`HttpOnly`, `SameSite=Lax`).
- The shared secret never leaves the script and the page.
- **Rate limiting** on the plugin side: per-user caps (default 5
  turns/min, 150/day — adjustable in the "External chat" section of the
  plugin configuration page) against LLM spam.
- **Read-only** on the plugin side: a leaked secret grants no write
  capability on the server — the only possible actions are driving the
  user's active Emby client (detail-page projection, play / pause /
  volume).
- **The LLM is throttled, by design.** The worst possible scenario is
  that it makes a mistake and **starts the wrong video**, or pauses at
  the wrong moment — on the active client of the user who is speaking,
  and nothing else:
  - **strict allowlist** of commands (`display_item`, `play_item`,
    `go_home`, `pause`, `unpause`, `stop`, `set_volume`, `mute`,
    `unmute`) — anything else is rejected **fail-closed** before any
    effect, and some sends are excluded **forever** (`SendKey`:
    arbitrary keystrokes, `TakeScreenshot`: privacy, `Restart`/`Shutdown`:
    server, `Seek`);
  - **bounded session**: only the signed-in user's own devices are
    targetable — never another user's client, never the server;
  - `display_item` and `play_item` pass the user's **parental policy**
    (fail-closed);
  - **no server writes**: no media-library modification, no action
    tools, no audit.
- The page can be served over HTTPS (see the HTTPS section above —
  recommended, and required for dictation off localhost) or plain HTTP,
  like Emby itself on the LAN.

## Troubleshooting (FAQ)

| Symptom | Likely cause | Fix |
|---|---|---|
| "Invalid token" / 403 from the app | Secret differs between Emby and `config.json` | Use the same value on both sides, restart the app |
| "No active Emby session for this user" (📺 projection) | No Emby app open with that account | Open the Emby app on the target device signed in as that user, then retry |
| 🎤 button missing | Browser without speech recognition, or page served over HTTP off localhost | Use Chrome/Edge and serve the page over HTTPS (HTTPS section) |
| "Microphone blocked" in Chrome | Page over plain HTTP, not localhost | Same fix: HTTPS |
| Projection does nothing on the TV | Client without `DisplayContent` | The plugin shows an on-screen message with the title (automatic fallback) — expected behavior |
| Expired certificate (HTTPS warning is back) | Host certificate copy not refreshed | Re-copy `fullchain.pem`/`privkey.pem` (automate via cron/hook), restart the app |
| User rejected at sign-in | Name missing from the whitelist, admin account, or passwordless account | Add the exact name to "Allowed users" (admins and passwordless accounts are rejected by design) |
| Answers stopping / "limit" reached | Anti-spam cap hit (5/min or 150/day by default) | Wait for the next window, or adjust the caps in the plugin configuration page |

The app's logs are on its console (or in `journalctl -u llmai-chat` under
systemd); the plugin's logs are in the Emby log (lines
`[LLM_AI] [CHAT-EXT]`).