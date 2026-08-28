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
7. [Auto-programming & login popup](#auto-programming--login-popup)
8. [Native recommendation surfaces](#native-recommendation-surfaces)
9. [HTTP API](#http-api)
10. [i18n (FR / EN)](#i18n-fr--en)
11. [Troubleshooting](#troubleshooting)
12. [Changelog](#changelog)

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

### Auto-programming & login popup

Native **Android / Android TV** clients don't render plugin HTML pages:
recommendations are only visible on the web page. Two levers make recos
**discoverable on the TV**:

- `AutoProgram` (bool, **default `false` — explicit opt-in**): when checked,
  after each run (scheduled task **and** login), the **record bucket** (upcoming
  EPG programs not already owned, not already scheduled, outside `DroppedTitles`)
  is **automatically scheduled as recordings** (SeriesTimer for a series, single
  Timer for a movie). They then show up in the **native EPG guide** with a record
  badge on every client, including TV. **No scheduling while unchecked.**
- `LoginPopup` (bool, default `true` — independent of `AutoProgram`): on user
  login, a **toast** surfaces what to watch tonight (unwatched recordings,
  library), plus a persistent **bell notification** (deep-link) as fallback.
  `LoginPopupSeconds` (default 8) sets the toast duration.

> ⚠️ Auto-programming occupies tuners/disk: it's an opt-in action. The user can
> cancel an unwanted timer in Emby. The login popup shows even without
> auto-programming (watch suggestions only).

### Native surfaces (.strm library, genre, collection)

Three **opt-in** levers (default `false`) that expose recos directly in Emby
beyond the web page. All are generated by the **scheduled task** and detailed in
[Native recommendation surfaces](#native-recommendation-surfaces).

- `StrmLibraryEnabled` (bool, default `false`) — writes a
  `.strm`+`.nfo`+poster card per **record bucket** reco into the Emby library
  named `StrmLibraryName`. Manual alternative to `AutoProgram` (both coexist, dedup
  prevents duplicate timers).
- `StrmLibraryName` (string, default `""`) — **exact name** of the dedicated Emby
  library (case-insensitive). The user must first create in Emby a **Movies**
  (or Mixed content) library pointing at an empty folder.
- `StrmSecret` (string, auto-generated) — capability token verified by the
  `/Plugins/LLMAI/Activate` endpoint. Auto-generated on first run, never entered.
- `TonightGenreTagEnabled` (bool, default `false`) — adds the `AI Tonight` genre
  to the **watch bucket** Emby items (mutates real metadata). Isolated scope from
  the `.strm` library's `AI Suggestion` genre.
- `TonightCollectionEnabled` (bool, default `false`) — maintains an `AI Tonight`
  collection (BoxSet) of the watch bucket items. **Non-destructive** (items
  referenced, never copied). Independent of the genre (both coexist).

> 📌 `StrmLibraryName` must match the **exact name** shown in the Emby dashboard
> (the UserView is slugified with hyphens; a name with `_` may not match). If you
> get "library not found", copy the name from the dashboard.

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
| `TonightApiService.cs` | `TonightApiService : BaseApiService` | **Per-user, on-demand** HTTP endpoint `GET /Plugins/LLMAI/Tonight`. Thin HTTP layer: resolves the user then delegates to `TonightService`. |
| `TonightService.cs` | `TonightService` (internal) | **Shared generation** for "Watch tonight": taste profile, unwatched recordings, library reserve, LLM run, enrichment, **per-user cache** (static, shared by endpoint + login). Used by `TonightApiService` and `TonightLoginService`. |
| `AutoProgrammer.cs` | `AutoProgrammer` (internal) | Auto-programming: creates the Emby timers (SeriesTimer / single Timer) for the **record bucket** — recos to record not owned / not already scheduled / outside drop list. Server-side port of the "Schedule" logic from `recommendations.js`. `ProgramOneAsync(Reco, …)` (returns `OneOutcome`) shared with the Activate endpoint. |
| `StrmLibraryGenerator.cs` | `StrmLibraryGenerator` (internal) | `.strm` library: writes a `.strm`+`.nfo`+poster card per record-bucket reco, `.llmai_reco` cleanup, TMDB poster download. |
| `ActivateApiService.cs` | `ActivateApiService : BaseApiService` | `GET /Plugins/LLMAI/Activate` endpoint (`[Unauthenticated]` DTO): programs a single reco then streams `recording_activated.mp4`. Gated by `StrmSecret`. |
| `AiGenreTagger.cs` | `AiGenreTagger` (static) | `AI Tonight` genre tagging: `AddAsync` / `RemoveAllAsync` via `UpdateToRepository`. |
| `AiTonightCollectionManager.cs` | `AiTonightCollectionManager` (static) | `AI Tonight` collection: `EnsureAsync` (find-or-create BoxSet, reconcile) + `ClearAsync` via `ICollectionManager`. |
| `AiTonightCleanupTask.cs` | `AiTonightCleanupTask : IScheduledTask` | Daily 03:00 cleanup: removes the `AI Tonight` genre + empties the collection (always active). |
| `TonightLoginService.cs` | `TonightLoginService : IServerEntryPoint` | Login trigger: hooks `ISessionManager.SessionStarted`, runs `TonightService` (cache-aware), auto-programs (if `AutoProgram`), sends a **toast** (`SendMessageCommand`, gated `DisplayMessage`) + persistent **bell** (deep-link). `Emby.ComSkipper` pattern. |
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

## Auto-programming & login popup

LLM_AI recommendations show by default only on the **web** page
`recommendations.html`. Native clients (Android / Android TV) don't render
plugin HTML pages — the reco isn't "discoverable" there. Two levers, both
**configurable** (see [Configuration](#auto-programming--login-popup)):

### Record bucket → native EPG badge (auto-programming)

If `AutoProgram` is checked, after each run the **to-record** recommendations
(upcoming EPG programs, not already owned, not already scheduled, outside the
drop list) are scheduled as recordings:

- **Series** → `SeriesTimerInfo` (RecordNewOnly, SkipEpisodesInLibrary) via
  `ILiveTvManager.CreateSeriesTimer`.
- **Movie / one-off** → `TimerInfoDto` via `ILiveTvManager.CreateTimer`.

Default values (Start/End/Channel/paddings) are derived from the program via
`GetNewTimerDefaults(programId)` — a minimal timer is never posted (missing
required fields: the server then creates nothing). Dedup via
`GetTimers`/`GetSeriesTimers` (ProgramId + normalized name, with article
removal — consistent with the EPG exclusion in `get_emby_info`).

Result: the recos carry the **record badge** in the **native** EPG guide —
the only reliable highlight across all TV clients. The user rarely watches
live (records + skips ads): scheduling is the right action; they can cancel
a timer if needed.

### Watch bucket → login popup

On user login, `TonightLoginService` (`IServerEntryPoint`,
`Emby.ComSkipper` pattern) hooks `SessionManager.SessionStarted`:

1. **Fresh cache** → immediate toast (no LLM run).
2. **Cold cache** → run `TonightService` (~30–60 s), then toast + bell.
3. **In-flight** guard: a single run per user even across several devices
   logged in at once (shared endpoint + login cache).

The **toast** (`SendMessageCommand`, gated `DisplayMessage` in
`SupportedCommands`) lists the watch-bucket titles (unwatched recordings /
library). The persistent **bell** (`INotificationManager`) deep-links to
the Recommendations page and survives if the session closes before the run
finishes. `LoginPopup` is **independent** of `AutoProgram`: watch
suggestions show at login even without auto-programming.

> **Gating `AutoProgram` (absolute rule)**: no timer is created while
> `cfg.AutoProgram == false`. The flag is checked in both paths
> (`LlmScheduledTask`, `TonightLoginService`) before any call to
> `AutoProgrammer.Program`. The popup (`LoginPopup`) is **not** gated by
> `AutoProgram`.

---

## Native recommendation surfaces

Beyond the `recommendations.html` web page and auto-programming, three **opt-in**
levers expose recos directly in Emby (all generated by the scheduled task). See
[Configuration](#native-surfaces-strm-library-genre-collection).

### `.strm` library (record bucket)

`StrmLibraryGenerator` writes, after each run, a **`.strm`+`.nfo`+poster** card
per recommendation **to record** (upcoming EPG programs not owned) into a dedicated
Emby library (option `StrmLibraryEnabled`, name `StrmLibraryName`). A `.llmai_reco`
marker file per folder drives stale-card cleanup on the next run (`CleanPrevious`).

Playing a card triggers **`GET /Plugins/LLMAI/Activate?programId=&kind=&t=`**:
1. `AutoProgrammer.ProgramOneAsync` creates the recording timer (a single reco),
   with dedup by ProgramId;
2. the endpoint streams the embedded `recording_activated.mp4` clip (10 s, 640×360).

The endpoint is **`[Unauthenticated]`** (media players / `ffprobe` carry no Emby
token); the **`StrmSecret` token** (`t=`) is the sole gate. Emby only probes the
`.strm` at **play time** (not at scan), so scheduling fires on the user's click.
The played clip marks the Emby card **watched** (green flag) — enable "hide watched"
on the library to auto-hide activated cards.

> ⚠️ **Required Emby setup**: first create a **Movies** (or Mixed content) library
> pointing at an empty folder, then enter its **exact name** in `StrmLibraryName`.
> A `localhost` base URL works for the web client (server-side transcode);
> `EmbyPublicUrl` is only needed for direct-play clients (TV, phones). Emby auth
> 401s requests without a token — hence the `[Unauthenticated]` + `StrmSecret`.

### `AI Tonight` genre (watch bucket)

`AiGenreTagger` tags, on **fresh** Tonight runs (not the cache), the real Emby
items of the **watch bucket** (unwatched recordings + owned items) with the
**`AI Tonight`** genre (option `TonightGenreTagEnabled`). The user finds the recos
by **filtering on that genre** in any Emby client.

- Mutate: `item.Genres = …; item.UpdateToRepository(ItemUpdateType.MetadataEdit)`.
- **Mutates real metadata** (`Genres` array) — a refresh may drop it, re-added on
  the next fresh run.
- **Isolated** scope from the `AI Suggestion` genre used by the `.strm` library
  (separate cleanup).

### `AI Tonight` collection (watch bucket)

`AiTonightCollectionManager` maintains an **`AI Tonight`** collection (BoxSet)
grouping the watch-bucket items (option `TonightCollectionEnabled`). The user
browses it like any collection in any client.

- **Non-destructive**: items are **referenced** (grouped), never copied or moved;
  playing a member plays the real item.
- **Aggregates cross-library items** (recordings + owned movies/series), which a
  genre filter can't do as directly.
- Populated on fresh runs (reconcile remove-all-then-add-all), **independent** of
  the genre (both can coexist). Verified: `CreateCollection(ParentId=0)` shows up
  in the Collections list.

### Cleanup (`AiTonightCleanupTask`)

**Daily 03:00** scheduled task, **always active** (not gated):

1. removes the `AI Tonight` genre from all items (`AiGenreTagger.RemoveAllAsync`);
2. **empties** the `AI Tonight` collection (shell kept, refilled on the next fresh
   run) — best-effort.

Subsequent tonight runs re-add the genre / refill the collection on still-relevant
recos.

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

```
GET /Plugins/LLMAI/Activate?programId=<id>&kind=<series|movie>&t=<StrmSecret>
```

**`.strm` card activation**: called by the media player when a card from the `.strm`
library is played. Creates the recording (`AutoProgrammer.ProgramOneAsync`, dedup
by `programId`) then streams `recording_activated.mp4` (10 s). Supports `Range`
(206 + `Content-Range`).

**Authentication:** **none** (`[Unauthenticated]` DTO) — players / `ffprobe` carry
no Emby token. The sole gate is the **`StrmSecret`** token (`t=`), auto-generated
and compared in constant time. An invalid `t` → 403.

**Test:**
```bash
curl "http://localhost:8096/emby/Plugins/LLMAI/Activate?programId=<id>&kind=movie&t=<StrmSecret>" -o clip.mp4
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

**`.strm` library: "library not found":**
`StrmLibraryName` must match the **exact name** shown in the Emby dashboard. The
UserView is slugified with **hyphens** (`ai-suggestions`) while the CollectionFolder
on disk often keeps the typed name (`ai_suggestions`). On mismatch, copy the name
from the dashboard. First create a **Movies** (or Mixed content) library pointing
at an empty folder before enabling `StrmLibraryEnabled`.

**`.strm` card: "No compatible streams" / ffprobe "Input/output error":**
Players / `ffprobe` carry no Emby token → 401 before the endpoint's `Get()`. The DTO
is `[Unauthenticated]` (gated by `StrmSecret`). Verify the `t=` in the `.strm` matches
the current `StrmSecret` (constant-time compare; invalid → 403). For `Range: bytes=0-`
(EOF), the endpoint serves the full body + `Content-Range: 0-<len-1>/<len>`.

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