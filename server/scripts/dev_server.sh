#!/usr/bin/env bash
# Dev server on http://127.0.0.1:8000 with the neutral sample data (scripts/demo_sample.py) - used by
# the helper's flow tests. Data in server/dev-data (not in git).
#   bash scripts/dev_server.sh           # start (fills the sample data on the first start)
#   bash scripts/dev_server.sh --fresh   # throw away dev-data and start over
# Admin password: $PDB_FLOW_ADMIN_PASSWORD (default "devadmin" - a dev server only).
set -euo pipefail
cd "$(dirname "$0")/.."
DATA=dev-data
[ "${1:-}" = "--fresh" ] && rm -rf "$DATA"
mkdir -p "$DATA"
PY=.venv/Scripts/python; [ -x "$PY" ] || PY=.venv/bin/python
ABS=$("$PY" -c "import os; print(os.path.abspath('$DATA'))")
export PDB_DATABASE_URL="sqlite:///$ABS/dev.sqlite3" PDB_DATA_DIR="$ABS/files" PDB_BACKUP_HOURS=0
fresh=0; [ -f "$DATA/dev.sqlite3" ] || fresh=1
"$PY" -m uvicorn app.main:app --host 127.0.0.1 --port 8000 &
pid=$!
trap 'kill $pid 2>/dev/null' INT TERM
until curl -s http://127.0.0.1:8000/health >/dev/null; do sleep 0.5; done
if [ $fresh = 1 ]; then
  PDB_DEMO_ADMIN_PASSWORD="${PDB_FLOW_ADMIN_PASSWORD:-devadmin}" PDB_DEMO_MCAM_ROOT="$ABS/M" PDB_DEMO_NC_ROOT="$ABS/H" \
    "$PY" scripts/demo_sample.py http://127.0.0.1:8000
fi
echo "== dev server: http://127.0.0.1:8000/  (admin / ${PDB_FLOW_ADMIN_PASSWORD:-devadmin}, users anna, tom, lina)"
wait $pid
