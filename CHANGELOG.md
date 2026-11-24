# Changelog

Tous les changements notables de ce plugin sont documentés ici.
Le format s'inspire de [Keep a Changelog](https://keepachangelog.com/),
et ce projet adhère au [Semantic Versioning](https://semver.org/lang/fr/).

All notable changes to this plugin are documented here.
Format based on [Keep a Changelog](https://keepachangelog.com/),
and this project adheres to [Semantic Versioning](https://semver.org/).


---

## [2.23.3] — 2026-11-24

Version de fiabilité : aucune nouvelle fonction, mais plusieurs situations rares qui
pouvaient bloquer le plugin ou abîmer ses données sont corrigées.

### Corrigé
- **Les analyses IA ne se bloquent plus.** Dans de rares cas, annuler une tâche au
  mauvais moment empêchait toutes les analyses suivantes de démarrer, jusqu'au
  prochain redémarrage d'Emby.
- **Annuler, c'est vraiment annuler.** Quand vous interrompez le chat ou une tâche,
  l'assistant s'arrête tout de suite et n'exécute plus aucune action ensuite. Si une
  recherche web prend trop de temps, l'assistant reçoit un message clair
  (« délai dépassé ») au lieu d'une erreur technique.
- **Vos données du plugin sont mieux protégées.** Décisions, mémoire de l'assistant,
  favoris « AI Tonight », rapports d'audit : un arrêt du serveur en pleine
  sauvegarde n'abîme plus ces fichiers. Si un fichier de validation des
  enregistrements est endommagé, il est mis de côté (extension `.corrupt-…`) plutôt
  qu'écrasé.
- **« Période de grande écoute » : plus de réglage ignoré.** Des écritures comme
  `9:00` ou `17:00-24:00` (ou avec un tiret typographique) étaient refusées, et le
  plugin travaillait alors 24 h sur 24 sans prévenir. Elles sont maintenant acceptées.
- **Petits modèles d'IA : réponses mieux récupérées.** Une réponse mal formée d'un
  petit modèle local est réparée dans un cas de plus, au lieu d'être perdue.
- **Quota Gemini dépassé :** le message « quota atteint » (erreur 429) n'est plus
  masqué quand Google renvoie une valeur de limite démesurée.
- **Espace disque des enregistrements :** le plugin ne confond plus deux dossiers dont
  les noms commencent pareil (par exemple `/mnt/rec` et `/mnt/record`). Le marquage
  « AI Delete » vise aussi toujours le bon élément.
- **Tâches lancées depuis le chat ou l'audit :** si plusieurs tâches planifiées portent
  le même nom, l'assistant n'en lance plus une au hasard ; il n'en lance aucune et
  vous demande de préciser.

## [2.23.3] — 2026-11-24

Reliability release: no new feature, but several rare situations that could freeze
the plugin or damage its data are fixed.

### Fixed
- **AI analyses no longer get stuck.** In rare cases, cancelling a task at the wrong
  moment stopped every later analysis from starting until Emby was restarted.
- **Cancel really cancels.** When you interrupt the chat or a task, the assistant
  stops right away and runs no further action. If a web search takes too long, the
  assistant now gets a clear message ("timed out") instead of a technical error.
- **Your plugin data is better protected.** Decisions, assistant memory, "AI Tonight"
  favorites, audit reports: a server shutdown in the middle of a save no longer
  damages these files. If a recordings-validation file is damaged, it is set aside
  (`.corrupt-…` extension) instead of being overwritten.
- **"Prime-time window": no more ignored setting.** Entries such as `9:00` or
  `17:00-24:00` (or with a typographic dash) used to be rejected, and the plugin then
  silently worked 24/7. They are now accepted.
- **Small AI models: better recovery of replies.** A badly formed reply from a small
  local model is repaired in one more case instead of being lost.
- **Gemini quota exceeded:** the "quota reached" message (error 429) is no longer
  hidden when Google returns an oversized limit value.
- **Recordings disk space:** the plugin no longer mixes up two folders whose names
  start the same way (for example `/mnt/rec` and `/mnt/record`). The "AI Delete"
  marking also always targets the right item.
- **Tasks started from the chat or the audit:** if several scheduled tasks share the
  same name, the assistant no longer starts one at random; it starts none and asks
  you to be more specific.
