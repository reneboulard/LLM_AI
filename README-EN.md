# LLM_AI — Emby LLM recommendations plugin

**Version:** 1.0.0.0 · **Id:** `e7d3dee6-ef19-46a9-985f-06318b682e60` · **Target:** Emby (net8.0)

> French version: see [README.md](README.md).

An Emby plugin that uses a large language model (LLM — local Ollama, Ollama Cloud, or
Google Gemini) to produce **series and movie recording recommendations** (scheduled at the
server level) and a per-user personalized **"À regarder ce soir" (Watch tonight)** section.
The LLM has tools to query the Emby library, the EPG, TMDB, TVDB, the web, and a "Showbizz"
new-releases source; it decides which tool calls to make on its own (agent / tool-calling
loop).

It also exposes an **on-demand server health audit** (`GET /Plugins/LLMAI/Audit`,
admin-only): an LLM agent queries the `system_audit` tool (sessions, scheduled tasks,
transcoding, disks, logs, host metrics, library) and produces a **Markdown health
report** (severity-tagged findings + recommended actions). **Remediation** (stop a
session, trigger a task, notify a user) is disabled by default (opt-in). See
[Server health audit](#server-health-audit).

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
9. [Server health audit](#server-health-audit)
10. [Orphan recording identification](#orphan-recording-identification)
11. [HTTP API](#http-api)
12. [i18n (FR / EN)](#i18n-fr--en)
13. [Troubleshooting](#troubleshooting)
14. [Changelog](#changelog)

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
prompt), `ResponseLanguage` (LLM output language — see below),
`ScheduleTask` / `ScheduleTaskMovies` (scheduled-task cron), `DebugVerbose`.

### LLM response language

`ResponseLanguage` forces the language of the LLM's **prose** — the **recommendation
reasons** (card `reason` field) **and** the **audit report**. Empty / `Auto` = no directive
(the LLM follows the prompt's language, here French — default behavior). Any other value
(e.g. `English`, `Español`, `Deutsch`…) injects a directive at the end of the system
prompt: the LLM then writes in that language. Movie/series titles, channel names and
technical JSON field names stay unchanged (original language). Config-page select:
`Auto`, `Français`, `English`, `Español`, `Deutsch`, `Italiano`, `Português`. Applies to
both paths (recommendations + audit, single and deterministic modes).

### Health audit

**On-demand** (admin-only) endpoint `GET /Plugins/LLMAI/Audit` that produces a server
health report. Independent from recommendations (dedicated agent run, `system_audit`
tool). See [Server health audit](#server-health-audit).

- `AuditEnabled` (bool, default `true`) — enables the endpoint and the "Run health audit"
  button. `false` = the endpoint returns a disabled response (no LLM run). The endpoint
  stays admin-only.
- `AuditRemediationEnabled` (bool, **default `false` — explicit opt-in**) — when checked,
  the LLM can **execute** the three remediation actions (`stop_session`, `trigger_task`,
  `send_message`) during the audit. While unchecked, these actions return an error and
  the LLM only **recommends** them in the report. Double control: the audit prompt also
  instructs the LLM never to act without an explicit request — this flag only opens the
  *capability*, not autonomy.
- `AuditMode` (`single` | `deterministic`, default `single`) — execution strategy:
  - `single` — a single agent loop: the LLM calls `system_audit` itself, adaptively (can
    drill into a log after a finding). Suited to a capable / cloud model. **The only
    mode where remediation can be executed** (if the flag is on).
  - `deterministic` — the C# gathers all read-only probes itself (zero LLM calls for
    the gathering), then a single **tool-free** LLM pass synthesizes the report from the
    digest. Designed for a local/smaller model (e.g. gemma4): multi-tool orchestration
    (its weak point) is removed, leaving only synthesis of provided text. Remediation is
    report-only (the LLM has no tool to execute it).
- `AuditPrompt` — prompt template sent to the LLM (user message). The optional `Focus`
  parameter of the endpoint is appended at runtime to orient the audit.

### Orphan recording identification

Daily **04:00** scheduled task that identifies **unidentified library items**
(movies/series from **completed** DVR recordings — once recording completes, Emby
imports the item into a library where it lives as a normal `Movie`/`Series`; no
IMDb/TMDB/TVDB id = failed identification, often Quebec titles missing from the
TMDB/TVDB catalog). See [Orphan identification](#orphan-recording-identification).

- `OrphanIdentifyEnabled` (bool, **default `false` — explicit opt-in**) — enables the
  task. `false` = the task is inactive (no-op). Mutates recording metadata — hence the
  opt-in.
- `OrphanIdentifyDryRun` (bool, default `false`) — when checked, the task **writes
  nothing**: it only logs the orphans found and the proposed resolution (S1/S2/S3) + a
  summary. Use it to validate resolution quality before switching to automatic
  application. Keep checked for the first runs.
- `OrphanSearXngEnabled` (bool, default `true`) — enables the **S3** stage (SearXNG web
  search → IMDb id → TMDB validation + synopsis judge) for titles S1 and S2 can't
  resolve. No-op if neither SearXNG nor an Ollama key is configured.
- `OrphanRetryNeedsReview` (bool, default `false`) — when checked, re-processes
  `llmai-needs-review` items (instead of skipping them) to run S3 on them; on success
  the tag becomes `llmai-identified`. Already-identified items stay skipped.

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
| `StrmLibraryGenerator.cs` | `StrmLibraryGenerator` (internal) | `.strm` library: writes a `.strm`+`.nfo`+poster card per record-bucket reco, `.llmai_reco` cleanup, TMDB poster download. The `.nfo` `<plot>` starts with the **native EPG overview** (original language) then the enrichment in the user's language; adds **External IDs** `<tmdbid>`/`<imdbid>`/`<tvdbid>` when available (TMDB/IMDb/TVDB deep links). |
| `ActivateApiService.cs` | `ActivateApiService : BaseApiService` | `GET /Plugins/LLMAI/Activate` endpoint (`[Unauthenticated]` DTO): programs a single reco then streams `recording_activated.mp4`. Gated by `StrmSecret`. |
| `AiGenreTagger.cs` | `AiGenreTagger` (static) | `AI Tonight` genre tagging: `AddAsync` / `RemoveAllAsync` via `UpdateToRepository`. |
| `AiTonightCollectionManager.cs` | `AiTonightCollectionManager` (static) | `AI Tonight` collection: `EnsureAsync` (find-or-create BoxSet, reconcile) + `ClearAsync` via `ICollectionManager`. |
| `AiTonightCleanupTask.cs` | `AiTonightCleanupTask : IScheduledTask` | Daily 03:00 cleanup: removes the `AI Tonight` genre + empties the collection (always active). |
| `OrphanIdentifyTask.cs` | `OrphanIdentifyTask : IScheduledTask` | Daily 04:00 identification of orphan library items (no IMDb/TMDB/TVDB id — completed DVR recordings imported into a library): discovered via `ILibraryManager.GetItemList` (Movie/Series) → S1 (title cleanup + multi-language TMDB search) → S2 (LLM-proposed id validated via TMDB `/find`) → S3 (SearXNG web search → IMDb id, same acceptance gate), writes ids+Overview+Genres+poster if empty, **locks `Name`**, tags `llmai-identified`/`llmai-needs-review`, retry needs-review, dry-run. See [Orphan identification](#orphan-recording-identification). |
| `DefaultImageApplier.cs` | `DefaultImageApplier` (static) | Sets a standardized default poster (`default_poster.jpg`, embedded resource) on the `AI Tonight` collection (BoxSet) and the `.strm` library root (CollectionFolder). Idempotent (only if no `Primary` image yet). |
| `AiBadgeEnhancer.cs` | `AiBadgeEnhancer : IImageEnhancer` | **Serve-time** badges on EPG images (overlay — stored artwork is never modified): **green chip + sparkle** for AI suggestions from the record bucket, **yellow chip without icon** for programs already in the library (reuses the `Norm` name matching). Drawn with SkiaSharp (bundled with Emby), per-badge-kind cache key, copy-of-original fallback, never throws. Auto-discovered by Emby's assembly scan. |
| `AiBadgeRegistry.cs` | `AiBadgeRegistry` (static) | Registry of programs suggested by the nightly task: replaced on each run (`ApplyRecos`, record-bucket filters), persisted as `AiBadgeProgramIds`, lazy reload on first `Supports` (the plugin ctor never touches `Configuration` — `AssemblyFilePath` is only set after construction). |
| `I18n.cs` | `I18n` (static) | Server-side i18n (C#): inline FR/EN dictionaries + language resolution (`ResolveMetaLangKey` metadata / `ResolveDisplayLangKey` UI) + `ToTmdbLang`/`ToLangName`. Localizes scheduled tasks. |
| `TonightLoginService.cs` | `TonightLoginService : IServerEntryPoint` | Login trigger: hooks `ISessionManager.SessionStarted`, runs `TonightService` (cache-aware), auto-programs (if `AutoProgram`), sends a **toast** (`SendMessageCommand`, gated `DisplayMessage`) + persistent **bell** (deep-link). `Emby.ComSkipper` pattern. |
| `AuditApiService.cs` | `AuditApiService : BaseApiService` | **On-demand admin** HTTP endpoint `GET /Plugins/LLMAI/Audit`: resolves the calling admin, builds the audit prompt (template `AuditPrompt` + optional `Focus`) then delegates the agent run to `LlmRunner.RunAuditAsync`. Returns the raw Markdown report. |
| `ChatApiService.cs` | `ChatApiService : BaseApiService` | **Interactive admin chat** HTTP endpoint `POST /Plugins/LLMAI/Chat`: body `{Message, History:[{role,content}]}` (stateless server — the page keeps the history), filters user/assistant roles, delegates the turn to `LlmRunner.RunChatAsync` (all existing tools, user-configured LLM priorities). The system prompt (tool docs + directives) is built server-side, once per conversation. |
| `SystemAuditTool.cs` | `SystemAuditTool : ILlmTool` | The `system_audit` tool (see [LLM tools](#llm-tools)) — 12 system-audit actions (sessions, tasks, transcoding, disks, logs, host metrics, processes, library) + 3 remediation actions gated by `AuditRemediationEnabled`. Log FS confinement (name-only + extension whitelist + canonical containment). |
| `LlmRunner.cs` | `LlmRunner` (internal class) | **Shared orchestration**: `ResolveBackends`, `RunAsync` (agent loop + tool-calling), `EnrichRecommendations` (title match → id/channel/poster/rating), `EnrichWithLibrary`, `FindLibraryItem`, `MergeJsonArrays`, `ExtractJsonPayload`, `NormTitle`, env-based key resolution. Dedicated audit path: `BuildAuditTools`, `RunAuditAsync` (agent loop or deterministic mode), `ChatWithFallbackAsync` (tool-free synthesis). Chat path: `RunChatAsync` (multi-turn, all existing tools, user-configured LLM priorities). One-shot calls: `TranslateTextAsync` (TMDB cascade tier-3), `ResolveIdsAsync` (id proposal for the orphan task — always validated by TMDB). Used by `LlmScheduledTask`, `TonightApiService`, `AuditApiService`, `ChatApiService`, **and** `OrphanIdentifyTask`. |
| `ItemIdResolver.cs` | `ItemIdResolver` (internal static) | Bilingual Emby id resolution: longs (InternalId — the plugin's canonical form, the only one Emby's REST/UI layer accepts) **and** legacy Guids (input only, never emitted). Fixes the id-currency mismatch that failed every Tonight validation. |
| `LlmAgentService.cs` | `LlmAgentService` | Agent loop: sends the prompt to the LLM, executes tool-calls, loops until the final answer. Two optional params (`roleIntro`, `formatSection`) override the role intro and the output-format block for the audit and chat paths (recommendation call sites unchanged). `RunChatAsync`: multi-turn entry that replays history (user/assistant, capped) between the system prompt and the new message — same shared loop (`RunLoopAsync`). |
| `LlmClient.cs` | `LlmClient` (static) | Raw HTTP calls to Ollama / Gemini (no keys logged in the clear). |
| `GetEmbyInfoTool.cs` | `GetEmbyInfoTool` | The `get_emby_info` tool (see [LLM tools](#llm-tools)). |
| `TmdbLookupTool.cs` / `TvdbSearchTool.cs` / `WebSearchTool.cs` / `WebFetchTool.cs` / `ShowbizzTool.cs` | … | Specialized LLM tools (see [LLM tools](#llm-tools)). `TmdbLookupTool` additionally exposes `LookupMetaAsync`/`LookupMetaMultiLangAsync` (search, S1), `FindByExternalIdAsync` (`/find`, validates a proposed id), `LookupMetaByIdAsync` (detail by id), `CleanEpgTitle` — reused by `StrmLibraryGenerator` and `OrphanIdentifyTask`. |
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
| `system_audit` | **Health audit** (see [Server health audit](#server-health-audit)) — 15 actions on `action`: **inspection** `server_info`, `system_config` (server configuration via `IServerConfigurationManager`), `active_sessions`, `scheduled_tasks`, `list_logs`, `inspect_log` (grep + context, confined to the log folder), `transcode`, `gpu_transcode`, `host_metrics`, `disk_storage`, `processes` (ffmpeg orphans + top RAM/CPU), `library_stats`, `missing_metadata`; **remediation** (gate `AuditRemediationEnabled`) `stop_session`, `trigger_task`, `send_message`. Never throws (error → JSON). |

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

Each card's `.nfo` contains:

- a `<plot>` that starts with the **native EPG overview** (the program's original
  language, read from the underlying EPG `BaseItem`), followed by the enrichment
  (TMDB overview + LLM reason + meta + upcoming airings + EPG page link) in the
  **user's language** (`ResponseLanguage`). The user reads the enrichment in their
  language while keeping the original EPG overview — no need to deduce the program's
  language;
- the **External IDs** `<tmdbid>` / `<imdbid>` / `<tvdbid>` when available (fetched via
  TMDB's `append_to_response=external_ids`) → Emby generates the TMDB / IMDb / TVDB
  **deep links** on the card's detail page.

Playing a card triggers **`GET /Plugins/LLMAI/Activate?programId=&kind=&t=`**:
1. `AutoProgrammer.ProgramOneAsync` creates the recording timer (a single reco),
   with dedup by ProgramId;
2. the endpoint streams the embedded `recording_activated.mp4` clip (8 s, 1280×720, no text or audio — universal).

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

## Server health audit

The health audit is **independent from recommendations**: a dedicated agent run queries
the `system_audit` tool (system telemetry, logs, transcoding, hardware/OS, disk, library)
then produces a **Markdown report** (severity-tagged findings 🔴/⚠️/✅ + a "Recommended
actions" section). It is triggered **on demand** from the config page ("Run health audit"
button) or the `GET /Plugins/LLMAI/Audit` endpoint.

### Two execution modes (`AuditMode`)

- **`single` (agent loop, default)** — the LLM calls `system_audit` itself, adaptively
  (it can drill into a log after a finding, chain actions in the order it finds useful).
  Suited to a capable / cloud model. **The only mode where remediation can be executed**
  (if `AuditRemediationEnabled` is on).
- **`deterministic` (C# gathering + synthesis)** — the C# gathers **all** read-only
  probes itself (`GatherAuditDigestAsync`, zero LLM calls for gathering) into a Markdown
  digest, then a **single tool-free LLM pass** synthesizes the report from the digest.
  Designed for a local/smaller model (e.g. gemma4): multi-tool orchestration (its weak
  point) is removed, leaving only synthesis of provided text (its strength).
  Remediation is **report-only** (the LLM has no tool to execute it).

### `system_audit` tool actions

| Family | Actions (read-only, always available) |
|---|---|
| Telemetry & config | `server_info` (version, ports, paths, pending restart, update, maintenance), `system_config` (full server configuration via `IServerConfigurationManager.Configuration`), `active_sessions`, `scheduled_tasks` |
| Logs & streams | `list_logs` (`LogPath` folder, `*.txt`), `inspect_log` (tail or **grep + context**, confined to the log folder), `transcode`, `gpu_transcode` |
| Hardware & OS | `host_metrics` (BCL: process, GC, runtime, uptime, scan running, aggregate transcode CPU — GPU only per transcode), `disk_storage` (`DriveInfo` + Emby path mapping), `processes` (ffmpeg-**orphan** detection by correlation + top RAM/CPU + Emby counters) |
| Library | `library_stats` (per-type counts + configured libraries + scan state, via `ILibraryManager` — DB layer, no raw FS), `missing_metadata` (sampling of items missing overview/image/genres) |

| Family | **Remediation** actions (gate `AuditRemediationEnabled`) |
|---|---|
| Control | `stop_session` (PlaystateCommand Stop), `trigger_task` (`QueueScheduledTask`), `send_message` (inbox notification **or** OSD toast) |

When `AuditRemediationEnabled` is off, remediation actions return a JSON error — the LLM
must then **recommend** the action in its report instead of executing it.

### Security

- **Admin-only**: the endpoint resolves the calling user and checks
  `Policy.IsAdministrator`. A non-admin receives `{Enabled:true, Error:"Réservé aux administrateurs."}`.
- **Filesystem confinement**: there is **no generic file-read tool**. `inspect_log` is
  pinned to the log folder with three guards: `Path.GetFileName` (rejects any slash/`..`),
  an **extension whitelist** (`.txt`/`.log` only), and **canonical containment**
  (`Path.GetFullPath` under the log folder). The LLM cannot wander into `/`.
- **Emby path resolution**: `server_info`/`list_logs`/`inspect_log`/`disk_storage` obtain
  the Emby paths (program data, cache, transcode temp, metadata, **logs**) by resolving
  `IServerConfigurationManager` through the host then reading `.ApplicationPaths` by name
  reflection. `system_config` exposes `IServerConfigurationManager.Configuration` (the full
  `ServerConfiguration` — cross-OS, read in-process, no XML parsing). Fallback: if the
  `GetSystemInfo` call fails (on some Emby versions it throws a `NullReferenceException`),
  paths still come from `ApplicationPaths` and the log path is derived by convention
  (`<ProgramDataPath>/logs`) — coverage stays complete, only the network interfaces are
  missing (honestly noted in the report).
- **Library via DB**: `library_stats` / `missing_metadata` go through `ILibraryManager`
  (DB layer) — no raw FS access to library folders.
- **Gated remediation**: `stop_session` / `trigger_task` / `send_message` check
  `Plugin.Instance.Configuration.AuditRemediationEnabled` before acting (default off). The
  audit prompt also instructs the LLM to **never** execute remediation without an explicit
  request (defense in depth).
- **Processes: pure BCL** — `Process.GetProcesses()` exposes only names/CPU time/age,
  **never** arguments or content: no secret leakage.

### `Focus` parameter

The endpoint accepts a free-form `Focus` (config-page field) appended to the `AuditPrompt`
template to orient the audit (e.g. `disk`, `transcoding`) or to make an explicit
remediation request (e.g. "stop session XYZ" — which only succeeds if remediation is
enabled **and** the mode is `single`).

---

## Orphan recording identification

When Emby finishes a DVR recording, it **imports it into a library** (Movies/Series)
and tries to identify it, then writes metadata to a `.nfo`. For **Quebec titles**, the
TMDB/TVDB lookup often fails (the catalog uses France or original titles): the item ends
up **without an IMDb/TMDB id** — an **orphan**. The user then fixes it by hand (web
search → IMDb id) and **locks** the fields. The **`OrphanIdentifyTask`** scheduled task
(daily **4 AM**, right after the 03:00 cleanup) automates this.

> ℹ️ **Discovery**: the task **scans library `Movie`/`Series` items**
> (`ILibraryManager.GetItemList`, `Recursive=true`) and keeps those without an
> IMDb/TMDB/TVDB id. It does **not** use `ILiveTvManager.GetRecordings`, which returns
> only **active/upcoming** recordings — **completed** recordings live in the library as
> normal items. `.strm` cards are excluded (`.strm` extension).

### Flow (three stages)

1. **S1 — cleanup + multi-language search.** The EPG title is stripped of noise by
   `CleanEpgTitle` (`HD`/`VOSTFR`/`VF`/`VO` markers, "Rediff."/"Inédit", `S##E##` /
   `Saison \d` / `Épisode \d`, parentheses) then searched on TMDB in several languages:
   `en-US` (original title), `fr-FR` (France title), + the user's language. A candidate
   is accepted if the **normalized title** matches (guard against an ambiguous wrong
   match), with a year check. **S1 only runs when `ProductionYear` is known**: without a
   reliable year, TMDB search is broad and the lexical guard (no judge) could accept a
   wrong same-titled film — orphans with no year go straight to S2/S3.
2. **S2 — LLM proposal validated by TMDB** (if S1 fails). `LlmRunner.ResolveIdsAsync`
   asks the LLM for an IMDb/TMDB id from the EPG title + overview + channel (one-shot
   call, multi-backend with fallback). The proposal is **never applied as-is**: it is
   validated via `FindByExternalIdAsync` (TMDB `/find` by `imdb_id`) or
   `LookupMetaByIdAsync` (detail by `tmdb_id`) — **TMDB is the source of truth**, a
   hallucinated id returns null. Failing that, the proposed original title is fed to S1.
   Each candidate must then pass a **semantic acceptance gate**:
   - **year guard** (`YearCompatible`, ±1 year);
   - **LLM synopsis judge** (`LlmRunner.JudgeSynopsisMatchAsync`) compares the EPG
     synopsis to the TMDB synopsis and confirms they describe the *same work* — an id
     that exists but points to a same-titled film from a different era (e.g. "Le
     guérisseur" 1953 vs 2017) is **rejected**, and the search continues. Mirrors the
     user's manual method (compare synopsis + date). Skipped when the EPG has no
     synopsis (falls back to year + title). The verdict + reasoning are logged.
3. **S3 — web search (SearXNG) → IMDb id** (if S1 and S2 fail, and
   `OrphanSearXngEnabled`). The task queries the self-hosted **SearXNG** instance
   (`SearXngUrl` field, already used by the LLM's `web_search` tool; Ollama cloud
   fallback), extracts **IMDb ids** from result URLs (regex
   `imdb.com/.../title/tt…`, appearance order = SearXNG relevance), then validates each
   id via `FindByExternalIdAsync` + the **same acceptance gate** (year + synopsis judge).
   Mirrors **exactly** the user's manual method (web-search the title → IMDb id → Emby
   pulls TMDB → compare synopsis+date) and resolves **paraphrased Quebec titles** no
   catalog knows (e.g. "L'histoire de Jean Seberg" → film "Seberg" 2019 → tt1780967). A
   candidate accepted **with no synopsis to compare** is logged "to confirm visually"
   (trust SearXNG ranking, as the user would before validating by hand).

### Non-destructive apply + locking

When a candidate is validated (and not in dry-run), `OrphanIdentifyTask`:

- fills **missing provider ids** (`SetProviderId` `tmdb`/`imdb`/`tvdb`);
- fills an **empty** `Overview`, **empty** `Genres`, a **missing** `Primary` poster
  (downloaded from TMDB, `IProviderManager.SaveImage`) — never overwrites an existing
  value;
- **locks `MetadataFields.Name`** (the EPG title is **never changed** — preserved to
  scan the EPG later for new programs) as well as the filled fields
  (`Overview`/`Genres`) — **add-only**: no existing lock is removed, mirroring the
  user's manual practice;
- adds the **`llmai-identified`** tag and persists (`UpdateToRepository`).

Orphans that no stage can resolve are tagged **`llmai-needs-review`** (to recheck by
hand) — no id is written.

### Idempotency & dry-run

Items already tagged `llmai-identified` are **skipped** on the next pass (tag-based
idempotency). With **`OrphanRetryNeedsReview`**, `llmai-needs-review` items are
**re-processed** (instead of skipped) — to run S3 on them once SearXNG is configured; on
success the `needs-review` tag is **replaced** with `identified`. With
**`OrphanIdentifyDryRun`**, the task writes nothing: it logs each orphan + the proposed
resolution (S1/S2/S3) and a summary (resolved / needs-review / skipped / errors) — to
validate resolution quality before switching to application. Best-effort: a failing item
never aborts the pass (per-item try/catch). Scope: **library `Movie`/`Series` items**
(completed DVR recordings imported into a library), not `.strm` cards.

> ⚠️ **Reference year**: the year used by S1 (`primary_release_year` filter) and by the
> S2/S3 year guard is `ProductionYear` **only**. For a DVR recording,
> `PremiereDate`/`DateCreated` are **broadcast** or recording dates (e.g. 2024), not the
> film's release year — using them filtered TMDB wrongly and missed existing films.
> Orphans with no `ProductionYear` skip S1 and rely on the synopsis judge (S2/S3) to
> avoid a wrong match.

> 📌 **Recommended verification**: enable `OrphanIdentifyEnabled` **with**
> `OrphanIdentifyDryRun` checked, trigger the task manually (Dashboard ▶ Scheduled
> Tasks) and inspect the `[LLM_AI] OrphanIdentify` log lines before unchecking dry-run
> for a real apply.

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
by `programId`) then streams `recording_activated.mp4` (8 s, 1280×720). Supports `Range`
(206 + `Content-Range`).

**Authentication:** **none** (`[Unauthenticated]` DTO) — players / `ffprobe` carry
no Emby token. The sole gate is the **`StrmSecret`** token (`t=`), auto-generated
and compared in constant time. An invalid `t` → 403.

**Test:**
```bash
curl "http://localhost:8096/emby/Plugins/LLMAI/Activate?programId=<id>&kind=movie&t=<StrmSecret>" -o clip.mp4
```

```
GET /Plugins/LLMAI/Audit?focus=<optional text>
```

**On-demand health audit**: launches a dedicated agent run (`system_audit` tool) and
returns a **Markdown** server health report. See [Server health audit](#server-health-audit).

**Response:** `{ Enabled, Report, Date, Error }` — `Report` is the raw Markdown report
(rendered client-side by config.js via a safe minimal Markdown→HTML converter).
`Enabled=false` if `AuditEnabled` is off; `Error="Réservé aux administrateurs."` if the
caller is not an admin.

**`focus` parameter:** free-form audit orientation (a domain to inspect, or an explicit
remediation request). Appended to the `AuditPrompt` template. Leave empty for a full audit.

**Authentication:** **admin only**. The user is resolved from the session token
(`X-Emby-Token`) or API key, then `Policy.IsAdministrator` is checked. A non-admin
receives `Error` (no LLM run).

**Test:**
```bash
curl -H "X-Emby-Token: <admin-token>" \
  "http://localhost:8096/emby/Plugins/LLMAI/Audit?focus=transcoding"
```

---

## i18n (FR / EN)

**Client-side** (`i18n.js`): a `STRINGS { fr, en }` dictionary and a `t(key, …args)`
function. Loaded via `require([ApiClient.getUrl("web/ConfigurationPage",
{name:"LLMAII18n"})])`. All visible labels (sections, buttons, config fields,
error/empty/loading messages) go through `t(...)`. To add a language, add a branch in
`STRINGS` and a language selector on the page.

**Server-side** (`I18n.cs`): inline FR/EN dictionaries (`s_res`) + language resolution.
Two distinct **buckets**:

- **metadata** (`ResolveMetaLangKey`) — `.nfo` `<plot>`, TMDB overview, LLM prose:
  precedence `ResponseLanguage` → Emby display language → legacy `TmdbLanguage` →
  English;
- **UI** (`ResolveDisplayLangKey`) — scheduled-task name/description: Emby display
  language (`UICulture`), English fallback.

Helpers `ToTmdbLang` (2-letter key → TMDB code `fr-FR`/`en-US`…) and `ToLangName` (→
human name for the LLM translation target). Data-driven extensibility: add an `I18n.s_res`
entry (languages without a dictionary fall back to English for short labels; the TMDB
overview and LLM prose stay in the user's language via the TMDB cascade + LLM translation
as a last resort).

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