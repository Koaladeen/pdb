#!/usr/bin/env bash
# Installs or updates the PDB server on a Raspberry Pi (Raspberry Pi OS / Debian 12+).
# Run from the copied server folder:  sudo bash deploy/pi/install.sh
# Safe to run again for updates: code is replaced, database and settings stay.
set -euo pipefail

APP_DIR=/opt/pdb
DATA_DIR=/var/lib/pdb
ENV_FILE=/etc/pdb.env
SRC_DIR="$(cd "$(dirname "$0")/../.." && pwd)"

if [ "$(id -u)" -ne 0 ]; then echo "Please run with sudo"; exit 1; fi

echo "== packages"
apt-get update -qq
apt-get install -y -qq python3 python3-venv rsync >/dev/null

echo "== user and folders"
id pdb >/dev/null 2>&1 || useradd --system --home "$DATA_DIR" --shell /usr/sbin/nologin pdb
mkdir -p "$APP_DIR" "$DATA_DIR"
chown pdb:pdb "$DATA_DIR"

echo "== code -> $APP_DIR"
rsync -a --delete --exclude '.venv' --exclude '__pycache__' --exclude '*.sqlite3*' --exclude '.pytest_cache' \
  "$SRC_DIR/" "$APP_DIR/"

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

echo "== service"
cp "$APP_DIR/deploy/pi/pdb.service" /etc/systemd/system/pdb.service
systemctl daemon-reload
systemctl enable pdb >/dev/null
systemctl restart pdb
sleep 2
if curl -fs http://127.0.0.1:8000/health >/dev/null; then
  echo "== running: http://$(hostname -I | awk '{print $1}'):8000/"
else
  echo "== service did not answer; see: journalctl -u pdb -n 50"
  exit 1
fi
