#!/usr/bin/env bash
#
# deploy.sh — Compile en Release, déploie la DLL du plugin Emby, redémarre
# le service et affiche les dernières lignes du journal pour vérification.
#
set -euo pipefail

# ---------------------------------------------------------------------------
# Configuration
# ---------------------------------------------------------------------------
PLUGIN_NAME="mon-plugin"
PLUGIN_DLL="${PLUGIN_NAME}.dll"
CONFIGURATION="Release"
TARGETFramework="netstandard2.0"
BUILD_OUT="bin/${CONFIGURATION}/${TARGETFramework}"

DEPLOY_DIR="/var/lib/emby/plugins"
SERVICE="emby-server"
LOG_FILE="/var/lib/emby/logs/embyserver.txt"

# Couleurs (désactivées si sortie non interactive)
if [ -t 1 ]; then
    C_BLUE='\033[1;34m'; C_GREEN='\033[1;32m'; C_RED='\033[1;31m'; C_RST='\033[0m'
else
    C_BLUE=''; C_GREEN=''; C_RED=''; C_RST=''
fi

log()  { printf "${C_BLUE}>> %s${C_RST}\n" "$*"; }
ok()   { printf "${C_GREEN}>> OK — %s${C_RST}\n" "$*"; }
die()  { printf "${C_RED}>> ERREUR — %s${C_RST}\n" "$*" >&2; exit 1; }

# ---------------------------------------------------------------------------
# Pré-requis
# ---------------------------------------------------------------------------
# Exécution depuis la racine du projet (où se trouve le .csproj).
cd "$(dirname "$0")"

[ -f "${PLUGIN_NAME}.csproj" ] || die "Aucun ${PLUGIN_NAME}.csproj trouvé dans $(pwd)"

command -v dotnet >/dev/null 2>&1 || die "dotnet n'est pas dans le PATH"
command -v systemctl >/dev/null 2>&1 || die "systemctl n'est pas disponible"

# Besoin de root pour écrire dans /var/lib/emby et piloter systemd.
if [ "$(id -u)" -ne 0 ]; then
    log "Relance en root via sudo…"
    exec sudo -E "$0" "$@"
fi

# ---------------------------------------------------------------------------
# 1. Compilation Release
# ---------------------------------------------------------------------------
log "Compilation en ${CONFIGURATION}…"
dotnet build -c "${CONFIGURATION}" /clp:ErrorsOnly
BUILT="${BUILD_OUT}/${PLUGIN_DLL}"
[ -f "${BUILT}" ] || die "La DLL générée est introuvable : ${BUILT}"
ok "Build réussi : ${BUILT}"

# ---------------------------------------------------------------------------
# 2. Déploiement de la DLL du plugin uniquement
#    (on ne copie PAS les DLL Emby — déjà présentes dans /opt/emby-server/system,
#     les dupliquer causerait des conflits de version au chargement).
# ---------------------------------------------------------------------------
log "Copie de ${PLUGIN_DLL} vers ${DEPLOY_DIR}/"
install -m 0644 -o emby -g emby "${BUILT}" "${DEPLOY_DIR}/${PLUGIN_DLL}"
ok "DLL déployée"

# ---------------------------------------------------------------------------
# 3. Redémarrage du service Emby
# ---------------------------------------------------------------------------
log "Redémarrage de ${SERVICE}…"
systemctl restart "${SERVICE}"

# On attend que le service repasse actif (max ~30s).
for _ in $(seq 1 30); do
    state="$(systemctl is-active "${SERVICE}" 2>/dev/null || true)"
    [ "${state}" = "active" ] && break
    sleep 1
done

if [ "${state:-}" != "active" ]; then
    die "Le service ${SERVICE} n'est pas repassé en état actif (état: ${state:-inconnu})."
fi
ok "Service ${SERVICE} actif"

# ---------------------------------------------------------------------------
# 4. Vérification du journal Emby (20 dernières lignes)
# ---------------------------------------------------------------------------
# Laisse à Emby le temps d'écrire ses premières lignes après le démarrage.
for _ in $(seq 1 10); do
    [ -f "${LOG_FILE}" ] && break
    sleep 1
done

if [ ! -f "${LOG_FILE}" ]; then
    die "Journal introuvable : ${LOG_FILE}"
fi

log "20 dernières lignes de ${LOG_FILE} :"
echo "------------------------------------------------------------------------"
tail -n 20 "${LOG_FILE}"
echo "------------------------------------------------------------------------"

# Détection basique d'erreurs dans le segment affiché.
if tail -n 20 "${LOG_FILE}" | grep -qiE 'error|exception|fail'; then
    printf "${C_RED}>> Des lignes contenant « error|exception|fail » apparaissent dans le journal.${C_RST}\n"
    printf "${C_RED}>> Vérifie la sortie ci-dessus avant de considérer le déploiement valide.${C_RST}\n"
    exit 2
fi

ok "Aucune erreur détectée dans les dernières lignes du journal."