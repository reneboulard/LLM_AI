# Changelog

Tous les changements notables de ce plugin sont documentés ici.
Le format s'inspire de [Keep a Changelog](https://keepachangelog.com/),
et ce projet adhère au [Semantic Versioning](https://semver.org/lang/fr/).

All notable changes to this plugin are documented here.
Format based on [Keep a Changelog](https://keepachangelog.com/),
and this project adheres to [Semantic Versioning](https://semver.org/).

---

## [2.23.3] — 2026-11-24

### Fixed (FR)
- **Porte de concurrence des tâches LLM** : une annulation survenant au moment exact de la libération d'un passage ne bloque plus définitivement les runs LLM suivants (jusqu'au redémarrage du serveur).
- **`web_search` / `web_fetch` / outils de l'agent** : une vraie annulation est propagée (le lot d'outils s'arrête, plus d'action exécutée après l'annulation) ; un délai dépassé renvoie « délai dépassé » au modèle au lieu de l'erreur .NET brute.
- **États JSON** (décisions, snapshots EPG, favoris Tonight, mémoire/prompts du chat, rapports d'audit et de revue, validation des liens) : écriture atomique (fichier temporaire puis renommage) — un arrêt en pleine écriture ne tronque plus le fichier. Le store de validation des enregistrements met un fichier corrompu de côté (`.corrupt-…`) et n'écrit plus rien si sa lecture échoue, au lieu d'écraser la vérité EPG figée.
- **Fenêtre de grande écoute Tonight** : `9:00`, `17:00-24:00` et les tirets typographiques sont acceptés (ils étaient refusés, ce qui repassait la production en 24 h/24).
- **Réparation JSON des petits modèles** : un saut de ligne brut après une barre oblique inverse produit maintenant un JSON valide.
- **Limite de quota Gemini (429)** : une valeur de limite démesurée ne masque plus le 429.
- **Disque des enregistrements** : un volume ne préfixe plus un chemin voisin (`/mnt/rec` vs `/mnt/record`) ; id canonique (`InternalId`) pour le marquage « AI Delete ».
- **Déclenchement de tâches planifiées depuis le chat/l'audit** : priorité Id > clé > nom respectée ; un nom partagé par plusieurs tâches n'en déclenche plus aucune (au lieu de la première).

### Changed (FR)
- **Notes de release** : la release GitHub reprend le contenu de ce fichier, qui ne couvre que les changements depuis le dernier tag publié. Le workflow refuse un tag qui ne correspond pas à `<Version>`.
- **Tests** : un projet xUnit (`tests/`) couvre les fonctions pures (fenêtre Tonight, gardes de titre/année des orphelins, réparation JSON, écriture atomique, porte LLM, actions du chat) ; exécuté avant le package de release. La DLL du plugin reste sans dépendance NuGet.
- Journaux d'erreur : `ex.ToString()` (pile complète) pour les échecs de requête de bibliothèque ; code mort retiré ; exemples de chemins et d'adresses neutralisés dans la documentation.

### Fixed (EN)
- **LLM task concurrency gate**: a cancellation landing at the exact moment a slot is released no longer blocks all following LLM runs for good (until server restart).
- **`web_search` / `web_fetch` / agent tools**: a real cancellation is propagated (the tool batch stops, no action runs after the cancellation); a timeout returns "timed out" to the model instead of the raw .NET error.
- **JSON state files** (decisions, EPG snapshots, Tonight favorites, chat memory/prompts, audit and review reports, link validation): atomic write (temp file then rename) — a stop mid-write no longer truncates the file. The recording-validation store sets a corrupt file aside (`.corrupt-…`) and writes nothing when its read fails, instead of overwriting the frozen EPG truth.
- **Tonight prime-time window**: `9:00`, `17:00-24:00` and typographic dashes are accepted (they were rejected, which silently put production back to 24/7).
- **Small-model JSON repair**: a raw newline right after a backslash now yields valid JSON.
- **Gemini quota limit (429)**: an oversized limit value no longer masks the 429.
- **Recording disk**: a volume no longer prefixes a sibling path (`/mnt/rec` vs `/mnt/record`); canonical id (`InternalId`) for the "AI Delete" tagging.
- **Triggering scheduled tasks from chat/audit**: Id > key > name priority respected; a name shared by several tasks now triggers none (instead of the first one).

### Changed (EN)
- **Release notes**: the GitHub release uses the content of this file, which only covers changes since the last published tag. The workflow rejects a tag that does not match `<Version>`.
- **Tests**: an xUnit project (`tests/`) covers the pure functions (Tonight window, orphan title/year guards, JSON repair, atomic write, LLM gate, chat actions); run before the release package. The plugin DLL still has no NuGet dependency.
- Error logs: `ex.ToString()` (full stack) for library query failures; dead code removed; example paths and addresses neutralized in the documentation.
