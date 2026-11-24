#!/usr/bin/env bash
# Installs or updates the PDB server as a *user* service - no sudo needed.
# Needs once: `loginctl enable-linger <user>` so it runs without anyone logged in.
# Run from the copied server folder:  bash deploy/pi/install-user.sh
# Safe to run again for updates: database and settings stay.
set -euo pipefail

APP_DIR="$(cd "$(dirname "$0")/../.." && pwd)"
DATA_DIR="$HOME/.local/share/pdb"
ENV_FILE="$HOME/.config/pdb.env"
UNIT_DIR="$HOME/.config/systemd/user"
PORT="${PDB_PORT:-8000}"

mkdir -p "$DATA_DIR" "$UNIT_DIR" "$(dirname "$ENV_FILE")"

echo "== python environment"
[ -d "$APP_DIR/.venv" ] || python3 -m venv "$APP_DIR/.venv"
"$APP_DIR/.venv/bin/pip" install -q --upgrade pip
"$APP_DIR/.venv/bin/pip" install -q -r "$APP_DIR/requirements.txt"

if [ ! -f "$ENV_FILE" ]; then
  echo "== first install: creating $ENV_FILE"
  cat > "$ENV_FILE" <<EOF
PDB_DATABASE_URL=sqlite:///$DATA_DIR/pdb.sqlite3
PDB_LOCK_TIMEOUT_MINUTES=10
PDB_TOKEN_HOURS=12
EOF
  chmod 600 "$ENV_FILE"
  echo "   New installation: open the web page, log in as admin / admin - the setup wizard follows."
fi
# settings added in later versions
grep -q '^PDB_DATA_DIR=' "$ENV_FILE" || echo "PDB_DATA_DIR=$DATA_DIR/files" >> "$ENV_FILE"

cat > "$UNIT_DIR/pdb.service" <<EOF
[Unit]
Description=PDB server (Mastercam file management)
After=network-online.target

[Service]
WorkingDirectory=$APP_DIR
EnvironmentFile=$ENV_FILE
ExecStart=$APP_DIR/.venv/bin/uvicorn app.main:app --host 0.0.0.0 --port $PORT
Restart=on-failure
RestartSec=3

[Install]
WantedBy=default.target
EOF

echo "== service"
systemctl --user daemon-reload
systemctl --user enable pdb >/dev/null 2>&1
systemctl --user restart pdb
for _ in $(seq 1 20); do
  curl -fs "http://127.0.0.1:$PORT/health" >/dev/null && break
  sleep 1
done
if curl -fs "http://127.0.0.1:$PORT/health" >/dev/null; then
  echo "== running: http://$(hostname -I | awk '{print $1}'):$PORT/"
else
  echo "== service did not answer; see: journalctl --user -u pdb -n 50"
  exit 1
fi
