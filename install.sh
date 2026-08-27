#!/usr/bin/env bash
#
# install.sh — Installation du plugin Emby LLM_AI pour les utilisateurs finaux.
# À utiliser après avoir décompressé la release (LLM_AI-<version>.zip).
#
#   bash install.sh
#
# Ce script :
#   1. localise LLM_AI.dll à côté de lui,
#   2. détecte le dossier des plugins Emby (défaut /var/lib/emby/plugins),
#   3. supprime l'ancien mon-plugin.dll (nom précédent) s'il est présent,
#   4. copie la DLL, redémarre le service Emby.
#
# Lancé en root (ou via sudo) : le dossier plugins et le service ne sont
# accessibles qu'à l'administrateur.
#
set -euo pipefail

PLUGIN_NAME="LLM_AI"
PLUGIN_DLL="${PLUGIN_NAME}.dll"
PREVIOUS_DLL="mon-plugin.dll"   # ancien nom de DLL avant renommage

# Couleurs (désactivées si sortie non interactive).
if [ -t 1 ]; then
    C_BLUE='\033[1;34m'; C_GREEN='\033[1;32m'; C_RED='\033[1;31m'; C_RST='\033[0m'
else
    C_BLUE=''; C_GREEN=''; C_RED=''; C_RST=''
fi
log()  { printf "${C_BLUE}>> %s${C_RST}\n" "$*"; }
ok()   { printf "${C_GREEN}>> OK — %s${C_RST}\n" "$*"; }
die()  { printf "${C_RED}>> ERREUR — %s${C_RST}\n" "$*" >&2; exit 1; }

# Répertoire de ce script (= contenu du zip : install.sh + LLM_AI.dll + README*).
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC_DLL="${SCRIPT_DIR}/${PLUGIN_DLL}"

[ -f "${SRC_DLL}" ] || die "${PLUGIN_DLL} introuvable à côté de install.sh (${SCRIPT_DIR})."

# Droits root.
[ "$(id -u)" -eq 0 ] || die "Lance ce script en root (sudo bash install.sh)."

# --- Dossier des plugins Emby -------------------------------------------
EMBY_PLUGINS="${EMBY_PLUGINS_DIR:-/var/lib/emby/plugins}"
[ -d "${EMBY_PLUGINS}" ] || die "Dossier plugins Emby introuvable : ${EMBY_PLUGINS} (var EMBY_PLUGINS_DIR pour le surcharger)."

# --- Service Emby -------------------------------------------------------
SERVICE="${EMBY_SERVICE:-}"
if [ -z "${SERVICE}" ]; then
    if systemctl list-unit-files 2>/dev/null | grep -q '^emby-server\.service'; then
        SERVICE="emby-server"
    elif systemctl list-unit-files 2>/dev/null | grep -q '^emby\.service'; then
        SERVICE="emby"
    else
        die "Service Emby introuvable (var EMBY_SERVICE pour le surcharger)."
    fi
fi

log "Installation de ${PLUGIN_DLL} vers ${EMBY_PLUGINS}"
log "Service Emby : ${SERVICE}"

# Ancien nom de DLL : à retirer pour éviter un doublon au chargement.
if [ -f "${EMBY_PLUGINS}/${PREVIOUS_DLL}" ]; then
    log "Suppression de l'ancienne ${PREVIOUS_DLL}…"
    rm -f "${EMBY_PLUGINS}/${PREVIOUS_DLL}"
fi

# Copie (atomic-ish : on écrase).
install -m 0644 "${SRC_DLL}" "${EMBY_PLUGINS}/${PLUGIN_DLL}"
ok "DLL installée."

# Redémarrage.
log "Redémarrage de ${SERVICE}…"
systemctl restart "${SERVICE}"
ok "Service redémarré."

cat <<EOF

${C_GREEN}Installation terminée.${C_RST}
Ouvre Emby → Tableau de bord → Plugins → LLM_AI pour le configurer
(backends LLM, clés API, section « Ce soir »).

Voir README-FR.md / README-EN.md pour la documentation complète.
EOF