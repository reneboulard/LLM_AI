#!/usr/bin/env bash
#
# package.sh — Compile le plugin en Release et produit une archive ZIP
# auto-suffisante prête à publier (GitHub Release, partage manuel, etc.).
#
#   bash package.sh
#
# Sortie : dist/LLM_AI-<version>.zip contenant
#   - LLM_AI.dll                 (la DLL, toutes les ressources web embarquées)
#   - install.sh                 (script d'installation pour l'utilisateur final)
#   - README.md / README-EN.md (documentation FR/EN)
#
# La version est lue depuis <AssemblyVersion> dans LLM_AI.csproj.
#
set -euo pipefail

PLUGIN_NAME="LLM_AI"
CSPROJ="${PLUGIN_NAME}.csproj"
CONFIGURATION="Release"
TFM="net8.0"
BUILD_OUT="bin/${CONFIGURATION}/${TFM}"

if [ -t 1 ]; then
    C_BLUE='\033[1;34m'; C_GREEN='\033[1;32m'; C_RED='\033[1;31m'; C_RST='\033[0m'
else
    C_BLUE=''; C_GREEN=''; C_RED=''; C_RST=''
fi
log()  { printf "${C_BLUE}>> %s${C_RST}\n" "$*"; }
ok()   { printf "${C_GREEN}>> OK — %s${C_RST}\n" "$*"; }
die()  { printf "${C_RED}>> ERREUR — %s${C_RST}\n" "$*" >&2; exit 1; }

cd "$(dirname "$0")"

[ -f "${CSPROJ}" ] || die "${CSPROJ} introuvable."

# --- Version depuis le .csproj ------------------------------------------
VERSION="$(grep -oP '<AssemblyVersion>\K[^<]+' "${CSPROJ}" | head -1)"
[ -n "${VERSION}" ] || die "Version introuvable dans ${CSPROJ} (<AssemblyVersion>)."

# --- Build --------------------------------------------------------------
log "Build ${CONFIGURATION}/${TFM} (version ${VERSION})…"
dotnet build -c "${CONFIGURATION}" -f "${TFM}" "${CSPROJ}"

DLL="${BUILD_OUT}/${PLUGIN_NAME}.dll"
[ -f "${DLL}" ] || die "Build terminé mais ${DLL} manquant."

# --- Stage --------------------------------------------------------------
DIST="dist"
STAGE="${DIST}/${PLUGIN_NAME}-${VERSION}"
rm -rf "${STAGE}"
mkdir -p "${STAGE}"

install -m 0644 "${DLL}"        "${STAGE}/${PLUGIN_NAME}.dll"
install -m 0755 install.sh      "${STAGE}/install.sh"
install -m 0644 README.md       "${STAGE}/README.md"
install -m 0644 README-EN.md    "${STAGE}/README-EN.md"
install -m 0644 LICENSE         "${STAGE}/LICENSE"
install -m 0644 CHANGELOG.md    "${STAGE}/CHANGELOG.md"

# --- Zip ----------------------------------------------------------------
ZIP="${DIST}/${PLUGIN_NAME}-${VERSION}.zip"
rm -f "${ZIP}"
STAGE_NAME="$(basename "${STAGE}")"
ZIP_NAME="$(basename "${ZIP}")"
if command -v zip >/dev/null 2>&1; then
    ( cd "${DIST}" && zip -qr "${ZIP_NAME}" "${STAGE_NAME}" )
elif command -v python3 >/dev/null 2>&1; then
    # Repli : Python stdlib (present sur la plupart des systèmes).
    python3 - "$DIST" "$ZIP_NAME" "$STAGE_NAME" <<'PY'
import os, sys, zipfile
dist, zipname, stage = sys.argv[1:4]
root = os.path.join(dist, stage)
with zipfile.ZipFile(os.path.join(dist, zipname), 'w', zipfile.ZIP_DEFLATED) as z:
    for base, _, files in os.walk(root):
        for f in files:
            full = os.path.join(base, f)
            arc = os.path.relpath(full, dist)
            z.write(full, arc)
PY
else
    die "Aucun outil de zip trouvé (installe 'zip' ou utilise python3)."
fi

ok "Archive : ${ZIP}"
log "Somme de contrôle :"
( cd "${DIST}" && sha256sum "$(basename "${ZIP}")" )

cat <<EOF

Contenu de l'archive (${STAGE}/):
  ${PLUGIN_NAME}.dll   — plugin auto-suffisant (HTML/JS/i18n embarqués)
  install.sh           — installation pour l'utilisateur final (sudo bash install.sh)
  README.md            — documentation française (principal, affiché sur GitHub)
  README-EN.md         — documentation anglaise
  LICENSE              — licence MIT
  CHANGELOG.md         — journal des versions

Pour publier : glisse ${ZIP} dans une GitHub Release (ou voir le workflow
.github/workflows/release.yml qui le fait automatiquement sur un tag v*).
EOF