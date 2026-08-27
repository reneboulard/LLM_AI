# Changelog

Tous les changements notables de ce plugin sont documentés ici.
Le format s'inspire de [Keep a Changelog](https://keepachangelog.com/),
et ce projet adhère au [Semantic Versioning](https://semver.org/lang/fr/).

All notable changes to this plugin are documented here.
Format based on [Keep a Changelog](https://keepachangelog.com/),
and this project adheres to [Semantic Versioning](https://semver.org/).

---

## [1.0.0.0] — 2026-08-27

### Ajouté / Added
- **Tâche planifiée globale** (`LlmScheduledTask`) : recommandations de **séries** et de
  **films à enregistrer**, basées sur l'EPG à venir, croisées avec la bibliothèque et les
  whitelists. Résultats stockés au niveau serveur + notifications Emby.
  Global **scheduled task** for series/movie recording recommendations from the upcoming
  EPG, cross-referenced with the library and whitelists; server-stored results + Emby
  notifications.
- **Section « À regarder ce soir »** (`TonightApiService`, endpoint
  `GET /Plugins/LLMAI/Tonight`) : recommandations **par usager à la demande**, croisant
  l'historique de visionnage, l'EPG du soir et les enregistrements récents non visionnés.
  Per-user on-demand "Watch tonight" recommendations combining watch history, tonight's
  EPG and recent unwatched recordings.
  - 3 sources : profil de goût, `epg_tonight`, enregistrements récents non visionnés.
  - **Réserve bibliothèque** (fallback) garantissant au moins `TonightMinRecommendations`
    recommandations même si l'EPG est vide.
  - Champ `source` (`live` / `recording` / `library`) pilotant les boutons des cartes
    (Programmer / Regarder en direct / Regarder / Regarder (bibli.) / Oublier).
  - **Mode compact** automatique pour les backends `OllamaLocal` (contexte réduit).
  - **Cache par usager** (TTL `TonightCacheHours`) + bouton Rafraîchir.
- **Backends LLM multi-source** : `OllamaLocal`, `OllamaCloud`, `Gemini`, avec priorité.
  Multi-source LLM backends with priority.
- **Outils LLM** : `get_emby_info` (10 actions dont `epg_tonight`), `tmdb_lookup`,
  `tvdb_search`, `web_search`, `web_fetch`, `showbizz_new_releases`.
- **i18n FR/EN** maison (`i18n.js`) : étiquettes de page, boutons, config.
- **Lecture** via le module AMD `playbackManager` d'Emby (Regarder / Regarder en direct).
- **Empaquetage & release** : `package.sh` (build + zip auto-suffisant), `install.sh`
  (installation utilisateur final), workflow GitHub Actions de release sur tag `v*`.

### Modifié / Changed
- Refactor de l'orchestration LLM dans une classe partagée `LlmRunner` (utilisée par la
  tâche planifiée et l'endpoint tonight) pour éviter la duplication.
  LLM orchestration extracted into shared `LlmRunner`.

[1.0.0.0]: ../../releases/tag/v1.0.0