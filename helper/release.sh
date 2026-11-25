#!/usr/bin/env bash
# Builds PDB Helper and the Mastercam bridge and publishes them on the PDB server, where the
# helpers on all PCs pick them up (update check every 10 min / at start).
#
#   PDB_HOST=user@server bash helper/release.sh   # upload to that PDB server (ssh)
#   PDB_HOST=none bash helper/release.sh          # only build (helper/release/out)
# Your server can also go into helper/release.local (not in git):  PDB_HOST=user@server
#
# Before a release: raise <Version> in PdbHelper/PdbHelper.csproj (and PdbBridge/PdbBridge.csproj
# when the bridge changed) - the helper only updates to a HIGHER version.
set -euo pipefail
cd "$(dirname "$0")"

GIVEN_HOST="${PDB_HOST:-}"   # set on the command line: wins over release.local
[ -f release.local ] && . ./release.local
HOST="${GIVEN_HOST:-${PDB_HOST:-none}}"
REMOTE_DIR="${PDB_DOWNLOADS:-.local/share/pdb/files/downloads}"   # PDB_DATA_DIR/downloads on the Pi

version_of() { sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$1" | head -1; }
HV=$(version_of PdbHelper/PdbHelper.csproj)
BV=$(version_of PdbBridge/PdbBridge.csproj)
OUT=release/out
rm -rf "$OUT" && mkdir -p "$OUT"

echo "== tests"
PDB_FLOW_TEST=${PDB_FLOW_TEST:-0} dotnet test PdbHelper.Tests -v q -nologo | tail -1

echo "== build bridge $BV"
dotnet build PdbBridge/PdbBridge.csproj -c Release -v q -nologo | grep -E " error |Fehler" || true
cp PdbBridge/bin/Release/net48/PdbBridge.dll "$OUT/PdbBridge-net48-$BV.dll"
cp PdbBridge/bin/Release/net10.0-windows/PdbBridge.dll "$OUT/PdbBridge-net10-$BV.dll"

echo "== publish helper $HV"
dotnet publish PdbHelper/PdbHelper.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -o release/publish -v q -nologo | grep -E " error |Fehler" || true
cp release/publish/PdbHelper.exe "$OUT/PdbHelper-$HV.exe"
cp release/publish/PdbHelper.exe dist/PdbHelper.exe 2>/dev/null || { cp release/publish/PdbHelper.exe "dist/PdbHelper-$HV.exe"; echo "   (dist/PdbHelper.exe in use - new one is dist/PdbHelper-$HV.exe)"; }

sha() { sha256sum "$1" | cut -d' ' -f1 | tr 'a-f' 'A-F'; }
cat > "$OUT/version.json" <<EOF
{
  "helper": {"version": "$HV", "file": "PdbHelper-$HV.exe", "sha256": "$(sha "$OUT/PdbHelper-$HV.exe")"},
  "bridge": {
    "net48": {"version": "$BV", "file": "PdbBridge-net48-$BV.dll", "sha256": "$(sha "$OUT/PdbBridge-net48-$BV.dll")"},
    "net10": {"version": "$BV", "file": "PdbBridge-net10-$BV.dll", "sha256": "$(sha "$OUT/PdbBridge-net10-$BV.dll")"}
  }
}
EOF
cat "$OUT/version.json"

if [ "$HOST" = "none" ]; then echo "== built only (no PDB_HOST): $OUT"; exit 0; fi
echo "== upload to $HOST:$REMOTE_DIR"
ssh "$HOST" "mkdir -p '$REMOTE_DIR'"
# version.json last, so no helper sees a version whose files are not there yet
scp -q "$OUT"/*.exe "$OUT"/*.dll "$HOST:$REMOTE_DIR/"
scp -q "$OUT/version.json" "$HOST:$REMOTE_DIR/version.json"
echo "== done: helper $HV, bridge $BV"
