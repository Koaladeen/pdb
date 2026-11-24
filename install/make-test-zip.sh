#!/usr/bin/env bash
# Builds the local package: dist/PDB-Local/ (folder) and dist/PDB-Local.zip - PDB for one Windows PC
# with its own database (Start-PDB.cmd), the PDB Helper and the Mastercam bridge.
# No company data: no database, pictures or import scripts; only the neutral demo data option.
# Needs a helper release first (helper/release.sh -> helper/release/out).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/dist"; STAGE="$OUT/PDB-Local"; REL="$ROOT/helper/release/out"
[ -f "$REL/version.json" ] || { echo "no helper release in $REL - run helper/release.sh first"; exit 1; }
rm -rf "$STAGE" "$OUT/PDB-Local.zip" "$OUT/PDB-Demo" "$OUT/PDB-Demo.zip" "$OUT/PDB-test.zip"
mkdir -p "$STAGE/server/scripts" "$STAGE/install" "$STAGE/helper"
cp -r "$ROOT/server/app" "$ROOT/server/deploy" "$ROOT/server/requirements.txt" "$STAGE/server/"
cp "$ROOT/server/scripts/demo_sample.py" "$STAGE/server/scripts/"
find "$STAGE" -name __pycache__ -type d -prune -exec rm -rf {} +
cp "$ROOT/install/"{pdb-local.ps1,install-server-windows.ps1,PDB-Server-einrichten.cmd,README.md} "$STAGE/install/"
cp "$ROOT/install/zip-root/"* "$STAGE/"
# helper + bridge: PdbHelper.exe to run, versioned files + version.json for the local server's downloads
cp "$REL"/*.dll "$REL/version.json" "$STAGE/helper/"
cp "$REL"/PdbHelper-*.exe "$STAGE/helper/PdbHelper.exe"  # same version as version.json: never downloaded again
for f in "$STAGE"/*.cmd "$STAGE"/*.txt; do sed -i 's/\r$//; s/$/\r/' "$f"; done   # Windows line endings
(cd "$OUT" && powershell -NoProfile -Command "Compress-Archive -Path PDB-Local -DestinationPath PDB-Local.zip")
echo "== $STAGE"
echo "== $OUT/PDB-Local.zip ($(du -k "$OUT/PDB-Local.zip" | cut -f1) KB)"
