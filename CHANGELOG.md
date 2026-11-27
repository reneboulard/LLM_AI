# Changelog

Tous les changements notables de ce plugin sont documentés ici.
Le format s'inspire de [Keep a Changelog](https://keepachangelog.com/),
et ce projet adhère au [Semantic Versioning](https://semver.org/lang/fr/).

All notable changes to this plugin are documented here.
Format based on [Keep a Changelog](https://keepachangelog.com/),
and this project adheres to [Semantic Versioning](https://semver.org/).

---

## [2.25.0] — 2026-11-27

### Ajouté
- **Choix de l'IA dans le chat.** Le chat en direct propose un menu « IA utilisée pour
  ce chat » : l'IA choisie est essayée en premier pour vos prochains messages, les
  autres restent en réserve dans l'ordre de la configuration. Au rechargement de la
  page, le menu revient à « Défaut » (ordre de la configuration).

- **Serveurs compatibles OpenAI.** Dans « Serveurs LLM », le nouveau fournisseur
  « Serveur compatible OpenAI » permet d'utiliser llama.cpp, LM Studio, vLLM ou tout
  serveur du même type sur votre réseau, avec une clé facultative.

### Modifié
- **Constat « doublons » plus prudent.** Quand plusieurs dossiers partagent la même
  fiche, l'assistant demande d'abord de vérifier qu'il s'agit bien de la même œuvre.
  Si ce sont deux œuvres différentes (une série et sa dérivée, par exemple), il
  recommande de réidentifier le dossier fautif au lieu de le supprimer ou de le
  fusionner.

## [2.25.0] — 2026-11-27

### Added
- **Choose the AI in the chat.** The live chat has an "AI used for this chat" menu:
  the chosen AI is tried first for your next messages, the others stay as backups in
  the configuration order. When the page is reloaded, the menu goes back to "Default"
  (configuration order).

- **OpenAI-compatible servers.** In "LLM servers", the new "OpenAI-compatible server"
  provider lets you use llama.cpp, LM Studio, vLLM or any similar server on your
  network, with an optional key.

### Changed
- **More careful « duplicates » finding.** When several folders share the same
  card, the assistant first asks you to check that they really are the same work.
  If they are two different works (a series and its spin-off, for example), it
  recommends re-identifying the faulty folder instead of deleting or merging it.
