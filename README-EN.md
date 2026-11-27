<!-- ⚠️ Do NOT rename the H1 title below without updating the dependent anchor:
     the French README.md title generates the GitHub anchor
     #llm_ai--plugin-emby-de-recommandations-par-llm referenced by the
     "Full documentation" link on the plugin config page (config.html). -->
# LLM_AI — Emby LLM recommendations plugin

**Version:** 2.25.0 · **Compatible with:** Emby Server 4.9 and 4.10

> Version française : voir [README.md](README.md).

## In plain words: the assistant that knows your TV

### The problem

You have an Emby server that records TV. Every night, the same question: *"what should
I watch or record tonight?"* Answering it yourself means leafing through the TV guide
(hundreds of shows), remembering what you have already seen or recorded, checking
whether the movie is worth it… It is tedious sorting work, and exactly what an AI does
well.

### The idea

**LLM_AI** is an assistant built into Emby that **reads the TV guide for you** and tells
you what to watch or record. It does not guess: it asks your server questions (what is
on tonight? what do I already like? what do I already have?), checks outside sources
(movie databases, the web, new-release lists), then **thinks and proposes**, always with
an explanation in plain language.

### What it does

- **A "Recommendations" page**: series and movies worth recording soon, each with its
  reason.
- **"Watch tonight"**: a selection for each person in the household, based on their
  tastes, what they started but did not finish, and the recordings piling up ("you have
  6 episodes of this show: time to start").
- **Suggestions delivered the way you like**: playlist, collection, tag, favorites,
  cards in a library, or a recording scheduled directly. Each option is switched on or
  off separately, and everything cleans itself up every night. **The AI never deletes
  anything on its own: you decide.**

### It learns over time

- **It learns from its mistakes**: every week it compares what it recommended with what
  you actually watched, and adjusts its suggestions.
- **It keeps a notebook**: recommendations, viewings and abandoned shows are noted, and
  it regularly rewrites a short "memory note" about its strategy that it rereads before
  proposing.
- **You stay in control**: any suggestion can be dismissed ("Forget"), and sensitive
  actions stay off until you turn them on.

### The extras

- **A chat with the assistant**: ask in natural language ("what documentaries are on
  this week?"). It can also check and fix the entries of your movies and series: you see
  the evidence and the proposal side by side before approving.
- **A server health report**: on demand, it inspects Emby (disk, logs, performance) and
  writes a report with advice.
- **Recordings without an entry get repaired**: when a recorded show is left without
  poster or description, it finds it on the web and fills in the missing information,
  as a manual search would.

### What it needs: clean data

The assistant **can only judge what it is shown**. But your TV guide and your library do
not always use the same words: the guide says "Kids", your library says "Children"; a
show is rated "PG-13" on one side and "13+" on the other. For an assistant comparing
lists, those are different words, so the comparisons are less accurate.

Two plugins do this cleanup upstream and are **strongly recommended** (both are in
Emby's plugin catalog):

- **GenreCleaner**: one single vocabulary for genres across the whole library;
- **Classification Mapper**: one single list for age ratings.

When they are installed, LLM_AI uses them automatically and translates the TV guide into
the same vocabulary itself. **Clean information in, relevant recommendations out.**

### Accessible to everyone, no powerhouse required

People often assume AI needs a big machine. Not necessarily:

- **A free online model**: with a simple **free Ollama account**, you use their models
  hosted on their servers. It is the easiest path: nothing to install, and it is enough
  for everything the plugin does. It also suits small machines: even on a
  **Raspberry Pi 4**, the cloud does the thinking.
- **A model at home, optional**: the most private variant (nothing leaves your network),
  but it needs **a computer with a graphics card of at least 8 GB of memory**. Below
  that, the model runs but answers very slowly.
- **Both at once**: the plugin accepts several services, picks by itself and **switches
  automatically** if one is unavailable. Google Gemini is also supported, as are OpenAI-compatible servers on your network
  (llama.cpp, LM Studio, vLLM).

The cost can therefore be **zero dollars**.

### In one sentence

**It is a butler for your TV: it watches the guide, knows your tastes, tells you what to
watch or record, and tidies up the information. It throws nothing away and
decides nothing sensitive without your approval.**

---

## Table of contents

1. [What you need](#what-you-need)
2. [Installation](#installation)
3. [First settings](#first-settings)
4. [Getting the best results](#getting-the-best-results)
5. [Day to day](#day-to-day)
6. [Where suggestions show up](#where-suggestions-show-up)
7. [Privacy and safety](#privacy-and-safety)
8. [Common problems](#common-problems)
9. [Going further](#going-further)
10. [License](#license)

---

## What you need

- **Emby Server** 4.9 or 4.10, with an administrator account.
- To recommend *recordings*: **Live TV** set up in Emby, with a TV guide (EPG) and a
  recorder (DVR).
- **An AI service** (see "Accessible to everyone" above): a free Ollama account, a Google
  Gemini key, or a model on your own network.
- **Optional but useful:** a free **TMDB** key (richer movie and series entries). Other
  keys are possible (TVDB, a web search engine); the configuration page has a **Test**
  button next to each service.

---

## Installation

> The plugin is a single file: **`LLM_AI.dll`**.

1. Download the latest `LLM_AI-<version>.zip` archive from the project's
   **[Releases](https://github.com/reneboulard/LLM_AI/releases)** page and unzip it.
2. **Linux**: open a terminal in the unzipped folder and run `sudo bash install.sh`. The
   script copies the plugin to the right place and restarts Emby.
   **Windows or other**: copy `LLM_AI.dll` into the `plugins` folder of Emby's data
   folder, then restart Emby.
3. Reload the Emby web page (**F5**).
4. Open **Dashboard → Plugins → LLM_AI**: this is the configuration page.

**Updating:** same steps (new DLL, restart Emby). Your settings are kept.

---

## First settings

On the configuration page, three steps are enough to get started:

1. **Add an AI service** ("LLM servers" section): choose Ollama (online or at home) or
   Gemini, paste your key if the service needs one, then click **Test**. You can add
   several: the highest priority is used first, the others act as backups.
2. **(Optional) Add your TMDB key** under "API keys", then **Test**.
3. **Save**, then open the **Recommendations** page from Emby's menu. The first
   suggestions arrive after the scheduled task runs for the first time, or right away
   with the refresh button.

Everything else is optional and can be set at your own pace: each section of the page has
its own explanations, and sensitive actions are **off by default**.

---

## Getting the best results

The plugin works as soon as it has an AI service. But the more sources it has, the better
it identifies your recordings and the better it recommends. Each addition is optional; the
**Test** button on the configuration page checks each one.

| Addition | What it improves | Difficulty |
|---|---|---|
| **TMDB key** (free) | Complete entries, posters, descriptions, more reliable identification | Easy |
| **SearXNG** (a web search engine you install yourself) | Finds shows whose guide title matches no catalog: this is what enables excellent identification | Technical |
| **TVDB key** | Better series identification | Easy |
| **A second AI service** | Automatic switch if the first is down or out of quota | Easy |
| **GenreCleaner and Classification Mapper** | Genres and age ratings consistent between the guide and your library | Easy |

The plugin **adds to** Emby, it does not replace it: Emby keeps identifying and describing
your recordings with its own sources, and the plugin steps in to fix what Emby could not identify.

Detailed settings (SearXNG, engines, keys, priorities) are described in the
[technical reference](docs/reference-EN.md#optimizing-the-plugin).

---

## Day to day

### The "Recommendations" page
Series and movies to record, produced every day by a scheduled task. Each card has a
reason and buttons: **Schedule** (record), **Watch**, **Forget** (never suggest it
again).

### "Watch tonight"
A selection **per person**, computed when the page opens, from viewing history, tonight's
guide and recordings not yet watched. On each login, a short series of on-screen messages
can present tonight's suggestions (adjustable).

You can limit this work to the hours when people actually watch TV with the **"Prime
viewing period"** setting (for example `17:00-23:00`): outside it, the plugin does not
call the AI and simply shows the last selection. Leave it empty to keep it always on.

### The chat
The **Live chat** tab of the configuration page (administrators only) lets you talk with
the assistant: explore the library, plan an evening, ask why it suggested a title. For
any action that changes something, it shows you a card to approve. A menu lets you try one specific AI from your configuration first,
for the length of the conversation; otherwise the configuration order applies.

An optional **companion app** gives the same chat to other household members, by keyboard
or by voice, on the local network (see [`chat-external/`](chat-external/README-EN.md)).

### The health report
A report on the state of the server (disk, tasks, logs, basic security), with advice
sorted by severity. The last report is kept: rereading it costs nothing. Corrective
actions (stopping a playback, starting a task, sending a notification) are **off by
default**.

### Recordings without an entry
After each recording, the plugin checks that the entry is the right one; if it is missing
or doubtful, it looks up the right title (TMDB, web), adds poster, description and genres,
then **locks** the entry so Emby does not overwrite it. A catch-up pass runs every night at
4 am. An "Excluded titles" field lets you ignore some shows.

### Library management
The **Library management** tab sorts your movies and series into three groups: to delete,
could be deleted, to keep, with the reason and the space freed. **Nothing is deleted
without your click** on "Delete"; Emby then does the deletion. "Keep" stops the title from
coming back.

### Several languages
The interface is available in French and English. Other languages can be added from the
configuration page (the AI translates, you review).

---

## Where suggestions show up

The Recommendations page changes nothing in Emby. If you wish, the same suggestions can
also appear **directly inside Emby**, where you watch. Each option is **independent**
(tick only what is useful to you) and **reversible** (cleaned every night at 3 am or
recreated on the next run).

| I want to… | Option to tick | What appears in Emby |
|---|---|---|
| Line up my evening | "AI Tonight" playlist | One playlist per person, plus a shared playlist that respects parental controls |
| Browse freely | "AI Tonight" collection | A browsable collection, accumulated over the day |
| Find suggestions with a filter | "AI Tonight" tag | An Emby tag |
| Mark my suggestions ❤️ | "AI Tonight" favorites | Titles are added to the chosen person's favorites |
| Record automatically what I do not have | Auto-programming | Recordings scheduled in Emby |
| Record in one click, on demand | "AI Suggestions" library | Playable cards in a library that start the recording |
| Know what to delete when the disk is full | "AI Delete" tag | A tag only: **you decide** what gets deleted |

Emby's parental controls are respected for personal suggestions.

---

## Privacy and safety

- **The AI never deletes anything on its own.** It can *suggest* deleting a title (for example to free up space), but you always make the decision, with a click.
- **The AI proposes, the plugin checks.** Nothing the AI says is taken on trust: the plugin checks the proposed titles and entries before using them, and discards what does not hold up. Actions that change something ask for your approval, except those you choose to automate.
- **You switch things on; it does not act alone.** Auto-programming, corrective actions
  of the health report, chat access: everything is off until you turn it on.
  Auto-programming is framed: a limited number of titles per run, disk space checked
  before scheduling, and you can cancel a recording at any time in Emby. Everything else
  the plugin adds (tags, cards, playlists, collection, favorites) is temporary and cleaned up every
  night.
- **What goes to the AI.** To answer, the AI receives titles, genres, extracts of your
  viewing history and of the TV guide. With a **local** model, nothing leaves your
  network; with an online service (Ollama Cloud, Gemini), that information is sent to the
  service. Choose according to your comfort level.
- **Your keys stay with you.** They are stored in Emby's configuration and are never
  shown by the assistant.
- **The chat in the configuration page is for administrators only.**

---

## Common problems

**The plugin does not show up in Emby.**
Check that `LLM_AI.dll` is in Emby's `plugins` folder and that you **restarted Emby**.

**Some text is in the wrong language or buttons are missing after an update.**
Reload the page with **Ctrl+F5**: the browser kept the old version.

**No recommendations appear.**
Click **Test** next to your AI service. Then check that the TV guide (EPG) has upcoming
shows. If you set a "Prime viewing period", outside that range the plugin only shows the
last selection.

**The assistant is very slow with my local model.**
Your graphics card probably lacks memory. Add an online service as primary or backup.

**Gemini answers "quota reached".**
Google's free tier limits the number of requests per minute. The plugin waits and retries,
or switches to your other AI service.

**The "AI Suggestions" library is not found.**
First create a movie library pointing to an empty folder, then enter its name **exactly**
as shown in the Emby dashboard.

**I want to see what happened.**
Emby's log (Dashboard → Logs) contains lines marked `[LLM_AI]`.

---

## Going further

- **[Technical reference](docs/reference-EN.md)**: every configuration option, the AI
  tools, the HTTP API, the details of each feature.
- **[Chat companion app](chat-external/README-EN.md)**: the chat for the household.
- **[Releases page](https://github.com/reneboulard/LLM_AI/releases)**: downloads and
  what is new in each version.

---

## License

[MIT](LICENSE).
