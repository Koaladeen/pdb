#!/usr/bin/env bash
# Builds dist/PDB-Setup-<version>.exe: wizard installer with everything inside (Python 3.12, server,
# PDB Helper + Mastercam bridge, PDB.exe launcher). Per-user install, no admin rights, works offline.
#
#   bash installer/build.sh            # version from VERSION in the repo root
#
# Needs: .NET SDK (builds net48), a helper release in helper/release/out (bash helper/release.sh),
# and the Python files in installer/cache - downloaded once:
#   python.org: python-3.12.10-embed-amd64.zip   (signature checked below)
#   PyPI:       installer/requirements-runtime.txt as cp312 win_amd64 wheels -> cache/site-packages
set -euo pipefail
cd "$(dirname "$0")"
ROOT=..
VERSION=$(tr -d ' \r\n' < "$ROOT/VERSION")
PYVER=3.12.10
CACHE=cache; BUILD=build; PAY=$BUILD/payload
HOSTPY="$ROOT/server/.venv/Scripts/python"
REL="$ROOT/helper/release/out"

# ---- 1. Python + packages (cached)
mkdir -p "$CACHE"
if [ ! -f "$CACHE/python-$PYVER-embed-amd64.zip" ]; then
  echo "== download Python $PYVER embeddable (python.org)"
  curl -fsSL -o "$CACHE/python-$PYVER-embed-amd64.zip" "https://www.python.org/ftp/python/$PYVER/python-$PYVER-embed-amd64.zip"
fi
if [ ! -d "$CACHE/site-packages" ] || [ requirements-runtime.txt -nt "$CACHE/site-packages" ]; then
  echo "== download server packages (PyPI, cp312 win_amd64)"
  rm -rf "$CACHE/site-packages"
  "$HOSTPY" -m pip install -q --disable-pip-version-check --target "$CACHE/site-packages" \
    --platform win_amd64 --python-version 3.12 --implementation cp --only-binary=:all: -r requirements-runtime.txt
fi
[ -f "$REL/version.json" ] || { echo "no helper release in $REL - run: bash helper/release.sh"; exit 1; }

# ---- 2. texts without translation?
"$HOSTPY" - <<'PY'
import pathlib, re
code = "".join(p.read_text(encoding="utf-8") for p in pathlib.Path(".").glob("Pdb*/*.cs"))
lang = pathlib.Path("Common/Lang.cs").read_text(encoding="utf-8")
used = set(re.findall(r'L\.[TF]\("((?:[^"\\]|\\.)*)"', code))
have = set(re.findall(r'\["((?:[^"\\]|\\.)*)"\] = new', lang))
missing = sorted(used - have)
if missing:
    print("== texts without translation (stay German):"); [print("   ", m) for m in missing]
PY

# ---- 3. payload
echo "== payload"
rm -rf "$BUILD" && mkdir -p "$PAY/python/Lib" "$PAY/server/scripts" "$PAY/helper"
(cd "$PAY/python" && unzip -q "../../../$CACHE/python-$PYVER-embed-amd64.zip")
powershell -NoProfile -ExecutionPolicy Bypass -File check-python.ps1 -Dir "$(cygpath -w "$PAY/python")"
cp -r "$CACHE/site-packages" "$PAY/python/Lib/site-packages"
find "$PAY/python/Lib/site-packages" -name __pycache__ -type d -prune -exec rm -rf {} +
rm -rf "$PAY/python/Lib/site-packages/bin"
# embeddable Python only searches what python312._pth lists: its zip, site-packages and the server
printf 'python312.zip\r\n.\r\nLib\\site-packages\r\n..\\server\r\nimport site\r\n' > "$PAY/python/python312._pth"
cp -r "$ROOT/server/app" "$PAY/server/"
cp "$ROOT/server/run_pdb.py" "$PAY/server/"
cp "$ROOT/server/scripts/demo_sample.py" "$PAY/server/scripts/"
find "$PAY/server" -name __pycache__ -type d -prune -exec rm -rf {} +
cp "$REL"/*.dll "$REL/version.json" "$PAY/helper/"
cp "$REL"/PdbHelper-*.exe "$PAY/helper/PdbHelper.exe"
cp "$ROOT/LICENSE" "$PAY/LICENSE.txt"

echo "== PDB.exe"
dotnet build PdbLauncher/PdbLauncher.csproj -c Release -nologo -v q -p:Version="$VERSION" | grep -E " error |Fehler:|Error" || true
cp PdbLauncher/bin/Release/net48/PDB.exe PdbLauncher/bin/Release/net48/PDB.exe.config "$PAY/"

"$HOSTPY" - "$PAY" "$BUILD/payload.zip" <<'PY'
import os, sys, zipfile
src, out = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for d, _, files in os.walk(src):
        for f in files:
            p = os.path.join(d, f)
            z.write(p, os.path.relpath(p, src).replace(os.sep, "/"))
print(f"   {sum(1 for _ in zipfile.ZipFile(out).namelist())} files, {os.path.getsize(out) // 1048576} MB")
PY

echo "== PDB-Setup.exe"
rm -rf PdbSetup/obj PdbSetup/bin
dotnet build PdbSetup/PdbSetup.csproj -c Release -nologo -v q -p:Version="$VERSION" | grep -E " error |Fehler:|Error" || true
mkdir -p "$ROOT/dist"
cp PdbSetup/bin/Release/net48/PdbSetup.exe "$ROOT/dist/PDB-Setup-$VERSION.exe"
cp PdbSetup/bin/Release/net48/PdbSetup.exe.config "$ROOT/dist/PDB-Setup-$VERSION.exe.config" 2>/dev/null || true
echo "== $ROOT/dist/PDB-Setup-$VERSION.exe ($(du -m "$ROOT/dist/PDB-Setup-$VERSION.exe" | cut -f1) MB)"
