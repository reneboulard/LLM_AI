# LLM_AI — Emby LLM recommendations plugin

**Version:** 1.0.0.0 · **Id:** `e7d3dee6-ef19-46a9-985f-06318b682e60` · **Target:** Emby (net8.0)

> French version: see [README.md](README.md).

An Emby plugin that uses a large language model (LLM — local Ollama, Ollama Cloud, or
Google Gemini) to produce **series and movie recording recommendations** (scheduled at the
server level) and a per-user personalized **"À regarder ce soir" (Watch tonight)** section.
The LLM has tools to query the Emby library, the EPG, TMDB, TVDB, the web, and a "Showbizz"
new-releases source; it decides which tool calls to make on its own (agent / tool-calling
loop).

---

## Table of contents

1. [Overview](#overview)
2. [Installation](#installation)
3. [Configuration](#configuration)
4. [Components](#components)
5. [LLM tools](#llm-tools)
6. [Watch tonight](#watch-tonight)
7. [HTTP API](#http-api)
8. [i18n (FR / EN)](#i18n-fr--en)
9. [Troubleshooting](#troubleshooting)
10. [Changelog](#changelog)

See also: [LICENSE](LICENSE) (MIT) · [CHANGELOG.md](CHANGELOG.md).

---

## Overview

The plugin adds a **Recommendations** page to Emby (main menu) showing:

- **Series to record** and **Movies to record** — produced by a **scheduled task**
  (`LlmScheduledTask`) running at the server (admin) level. The LLM scans the upcoming
  EPG, cross-references the library and whitelists, and recommends what to schedule.
  Results are stored in `PluginConfiguration.Recommendations` and shown to all users. Emby
  notifications can be sent.

- **À regarder ce soir / today** — a **per-user, on-demand** section computed by a plugin
  endpoint (`TonightApiService`). The LLM cross-references the user's watch history,
  tonight's EPG, and recent unwatched recordings to recommend what to watch *now*. A
  per-user cache avoids re-running the LLM on every page open.

Card buttons let you **Schedule** (SeriesTimer for a series, single Timer for a movie),
**Watch** / **Watch live** / **Watch (library)**, and **Forget** (adds the title to the
`DroppedTitles` drop list).

---

## Installation

> **The plugin is self-contained in the DLL**: all web files (HTML/JS/i18n/icon) are
> embedded as resources — a single `LLM_AI.dll` file is enough.

### From a release (end user)

1. Download the latest `LLM_AI-<version>.zip` archive from the repository's
   **[Releases][releases]** page on GitHub.
2. Unzip the archive.
3. Run the installer (as root):
   ```bash
   sudo bash install.sh
   ```
   `install.sh` detects the Emby plugins folder (`/var/lib/emby/plugins` by default) and
   service (`emby-server`), removes the old `mon-plugin.dll`, copies the DLL, and
   restarts Emby. Optional env vars: `EMBY_PLUGINS_DIR`, `EMBY_SERVICE`.
4. In Emby: **Plugins** → **LLM_AI** → configure (see [Configuration](#configuration)).
5. Clear the browser cache / hard-reload the page (the plugin's JS files are served by
   Emby and cached aggressively).

### From source (developer)

Prerequisites: Emby Server (net8.0 build), .NET SDK 8.

- `bash deploy.sh` from the project root: builds `Release net8.0`, copies `LLM_AI.dll`
  to `/var/lib/emby/plugins/`, removes the old `mon-plugin.dll`, restarts `emby-server`,
  and tails the log.
- `bash package.sh`: builds + produces `dist/LLM_AI-<version>.zip` (self-contained
  release, see above).

[releases]: ../../releases

---

## Configuration

The config page (`config.html` / `config.js`, localized via `i18n.js`) exposes:

### LLM backends

Multiple backends can be enabled at once, each with a **priority**. The highest-priority
enabled backend is the **primary**. Each backend:

| Field | Role |
|---|---|
| `Provider` | `OllamaLocal`, `OllamaCloud`, or `Gemini` |
| `Url` | API URL (e.g. `http://localhost:11434` for local Ollama) |
| `Model` | Model name (e.g. `llama3.1`, `gemini-1.5-flash`) |
| `Enabled` | Enable this backend |
| `Priority` | Preference order (higher = primary) |

Legacy fields `LlmUrl` / `ModelName` are still supported (legacy fallback: a non-empty
`LlmUrl` is treated as a local backend).

### API keys

API keys are stored in config **OR** read from environment variables (if the config field
is empty):

- `OllamaApiKey` ← `OLLAMA_API_KEY` (Ollama Cloud)
- `GeminiApiKey` ← `GEMINI_API_KEY` (Google Gemini)
- `TmdbApiKey` ← `TMDB_API_KEY`, `TvdbApiKey` ← `TVDB_API_KEY`
- `EmbyPublicUrl` — Emby URL exposed to the LLM (for `item_details` / posters).

> ⚠️ Keys are never read or printed in the clear by the assistant; they flow directly from
> the config field (or env) into the API call.

### Filters and lists

- `ChannelWhitelist` (channels to consider, empty = all), `GenreWhitelist` (same for genres).
- `SeriesFlags` / `MovieFlags` — Kids / News / Sports flags (inclusion).
- `DroppedTitles` — excluded titles (populated by the **Forget** button).
- `MaxSeriesBatch` / `MaxMovieBatch` — recommendation caps per scheduled task.

### "Tonight" section

- `TonightEnabled` (bool, default `true`) — enables the section + the endpoint.
- `TonightWindowStart` / `TonightWindowEnd` (HH:mm, defaults `""` = now / `23:59`) — EPG
  time window for `epg_tonight`.
- `TonightPrompt` — prompt template (history/EPG/recordings are injected at runtime, not in
  this field).
- `MaxTonightBatch` (default 10) — recommendation cap.
- `TonightCacheHours` (default 4) — per-user cache TTL.
- `TonightRecordingsDays` (default 7) — "recorded within the last N days" window.
- `TonightMinRecommendations` (default 3) — guaranteed minimum (see [Watch tonight](#watch-tonight)).

### Misc

`TmdbLanguage`, `SearXngUrl` (self-hosted web search), `WebFetchDirect`,
`ShowbizzUrl` / `ShowbizzPattern`, `RagDirectives` (extra directives injected into the
prompt), `ScheduleTask` / `ScheduleTaskMovies` (scheduled-task cron), `DebugVerbose`.

---

## Components

| File | Class | Role |
|---|---|---|
| `Plugin.cs` | `Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage` | Entry point. Name `LLM_AI`, Id `e7d3…2e60`. Registers web pages (config, recommendations, i18n). Version driven by `<AssemblyVersion>` in the `.csproj`. |
| `PluginConfiguration.cs` | `PluginConfiguration` (+ `LlmBackend`, `LlmProvider`) | All persisted config + multi-source backends. |
| `LlmScheduledTask.cs` | `LlmScheduledTask : IScheduledTask, IConfigurableScheduledTask` | Global (admin) scheduled task: produces **Series / Movies** recos by scanning the EPG, stores them in `Recommendations`, sends notifications. Delegates orchestration to `LlmRunner`. |
| `TonightApiService.cs` | `TonightApiService : BaseApiService` | **Per-user, on-demand** HTTP endpoint `GET /Plugins/LLMAI/Tonight`. Builds the taste profile, unwatched recordings, library reserve, runs the LLM, enriches, caches. |
| `LlmRunner.cs` | `LlmRunner` (internal class) | **Shared orchestration**: `ResolveBackends`, `RunAsync` (agent loop + tool-calling), `EnrichRecommendations` (title match → id/channel/poster/rating), `EnrichWithLibrary`, `FindLibraryItem`, `MergeJsonArrays`, `ExtractJsonPayload`, `NormTitle`, env-based key resolution. Used by both `LlmScheduledTask` and `TonightApiService`. |
| `LlmAgentService.cs` | `LlmAgentService` | Agent loop: sends the prompt to the LLM, executes tool-calls, loops until the final answer. |
| `LlmClient.cs` | `LlmClient` (static) | Raw HTTP calls to Ollama / Gemini (no keys logged in the clear). |
| `GetEmbyInfoTool.cs` | `GetEmbyInfoTool` | The `get_emby_info` tool (see [LLM tools](#llm-tools)). |
| `TmdbLookupTool.cs` / `TvdbSearchTool.cs` / `WebSearchTool.cs` / `WebFetchTool.cs` / `ShowbizzTool.cs` | … | Specialized LLM tools (see [LLM tools](#llm-tools)). |
| `config.html` / `config.js` | — | Configuration page (entry of the fields above). |
| `recommendations.html` / `recommendations.js` | — | Recommendations page (renders the 3 sections, cards, buttons). |
| `i18n.js` | — | Localized FR/EN strings + `web/ConfigurationPage?name=LLMAII18n` endpoint. |
| `deploy.sh` | — | Build + deploy + restart (see [Installation](#installation)). |

### Data flow

**Scheduled task (Series/Movies):**
1. Cron `ScheduleTask`/`ScheduleTaskMovies` → `LlmScheduledTask.Execute`.
2. `LlmRunner.ResolveBackends` picks the primary backend.
3. Prompt + tools → `LlmAgentService` agent loop (the LLM calls `get_emby_info`
   `epg_series`/`epg_movies`, `tmdb_lookup`, `web_search`/`web_fetch`, `showbizz…`).
4. `EnrichRecommendations` → posters/ratings/id/channel. Stored in `Recommendations`.
5. Emby notifications (if enabled). The page reads `Recommendations` on `viewshow`.

**Watch tonight:** see the dedicated section below.

---

## LLM tools

The LLM chooses which tools to call on its own. Each tool implements `ILlmTool`
(`Name`, `RunAsync(args)`).

| `Name` | Action(s) / Description |
|---|---|
| `get_emby_info` | **Emby queries** — actions: `summary` (library summary), `library` (items), `global_search`, `item_details`, `item_persons`, `person`, `epg_series` (upcoming series EPG), `epg_movies` (upcoming movies EPG), `epg_tonight` (EPG within the "tonight" window, `HasAired=false`, marks `is_scheduled`), `scheduled` / `planning` (programmed timers). Applies whitelists, flags, drop list, title deduplication. |
| `tmdb_lookup` | TMDB search / details (rating, poster, overview, cast) via `TmdbApiKey`. |
| `tvdb_search` | TVDB search (series) via `TvdbApiKey`. |
| `web_search` | Web search (SearXng `SearXngUrl` or built-in provider). |
| `web_fetch` | Fetch/read a web page (`WebFetchDirect` for raw read). |
| `showbizz_new_releases` | New releases from a "Showbizz" source (`ShowbizzUrl` + `ShowbizzPattern`). |

---

## Watch tonight

A **per-user** section computed on page open (on-demand) by the `TonightApiService`
endpoint. The LLM receives **three sources**:

1. **Taste profile** — `BuildTasteProfile`: the user's recently played items
   (`DatePlayed` desc), favorite titles/series/genres.
2. **Tonight's EPG** — via the `get_emby_info action=epg_tonight` tool-call
   (`TonightWindowStart`→`TonightWindowEnd`, `HasAired=false`).
3. **Recent unwatched recordings** — `BuildUnwatchedRecordings`: items recorded within the
   last `TonightRecordingsDays` days but not watched (`IsPlayed=false`). Enables the rule:
   *if the user watches series X and a new unwatched recording of X is available →
   recommend it.*

**Library reserve (fallback)** — `BuildLibraryFallbackPool`: pre-fetched unwatched items,
injected as a **reserve**, used **only if** the LLM produces **fewer than
`TonightMinRecommendations`** recommendations. Guarantees at least N recos even when the
EPG is empty.

**`source` field** per recommendation (drives card buttons):

| `source` | Meaning | Buttons |
|---|---|---|
| `live` | Tonight's EPG program | Schedule · Watch live (if started) · Watch (library) if owned · Forget |
| `recording` | Recent unwatched recording | Watch · Forget |
| `library` | Library reserve (fallback) | Watch · Forget |

**Compact mode (local)** — If the primary backend is `OllamaLocal`, `TonightApiService`
switches to **compact mode**: injected item caps (profile, recordings, reserve) and EPG
overview truncation are reduced, to avoid overwhelming a local model (often slower /
limited context). Cloud backends receive the full context.

**Per-user cache** — `Dictionary<userId, CacheEntry>` + lock, TTL `TonightCacheHours`.
`Refresh=1` forces a fresh run (**Refresh** button).

**Playback** — The **Watch** button plays via Emby's AMD `playbackManager` module
(`require(["playbackManager"], pm => pm.play({ids:[id], serverId: ApiClient.serverId()}))`),
not via `ApiClient.play` (which doesn't exist).

---

## HTTP API

```
GET /Plugins/LLMAI/Tonight?userId=<id>&refresh=<0|1>
```

**Response:** `{ Enabled, Items, Date, FromCache, Error }` — `Items` is a JSON string
(array of recommendations `{title, kind, reason, priority, source, channel, start, id,
showbizz_match, image_url, library_id, …}`).

**Authentication:** session token (`X-Emby-Token`) **or** API key. With an API key, the
auth context returns a `null` user / `UserId=0`; the plugin then resolves the user from
the `userId` parameter, or falls back to the **first user** (domestic / API-key use). A
normal user cannot read another user's history: `userId` is checked against the
authenticated user (except admins).

**Test:**
```bash
curl -H "X-Emby-Token: <token>" \
  "http://localhost:8096/emby/Plugins/LLMAI/Tonight?userId=<id>&refresh=1"
```

---

## i18n (FR / EN)

Custom system (`i18n.js`): a `STRINGS { fr, en }` dictionary and a `t(key, …args)`
function. Loaded client-side via `require([ApiClient.getUrl("web/ConfigurationPage",
{name:"LLMAII18n"})])`. All visible labels (sections, buttons, config fields,
error/empty/loading messages) go through `t(...)`. To add a language, add a branch in
`STRINGS` and a language selector on the page.

---

## Troubleshooting

**Local LLM "chokes" on Watch tonight:**
The injected context is too large for a local model. Compact mode activates automatically
when the primary backend is `OllamaLocal`. Adjust: lower `MaxTonightBatch`, make sure the
local backend has the highest `Priority`, or use a cloud backend.

**EPG returns 0 programs:**
Check `TonightWindowStart`/`TonightWindowEnd` (`HH:mm` format) and that the EPG is
populated. The "library reserve" fallback still guarantees `TonightMinRecommendations`
recos.

**Only the "Forget" button shows:**
Playback uses `playbackManager` (an AMD module), not `ApiClient.play`. Reload the plugin's
JS (browser cache). For `library` recos without an `id`, the `EnrichWithLibrary` step
backfills the id — check that the title matches a library item.

**API key: "User not authenticated":**
An API key authenticates but provides no user (`User=null`). The plugin falls back to the
`userId` parameter, then to the first user. Pass an explicit `?userId=<id>`.

**Untranslated buttons/labels:**
Hard-reload (Emby JS cache). Check that the `i18n` key exists in both `STRINGS.fr` and
`STRINGS.en`.

**Build: 0 warnings / 0 errors expected:** `bash deploy.sh` must finish with no errors or
warnings. JS files are validated with `node --check` before deployment.

---

## Changelog

Notable changes are recorded in [CHANGELOG.md](CHANGELOG.md). License: [MIT](LICENSE).