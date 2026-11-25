# PDB server

FastAPI server that owns users, machines, part cards, operations, locks, revisions and the
audit log. The Mastercam files stay on the share; the server only hands out paths, and the
PDB Helper on each PC reads and writes the files. That is why it can run on a Raspberry Pi
without access to the network drives.

## Run

Windows:

```powershell
cd server
python -m venv .venv
.venv\Scripts\pip install -r requirements.txt
# first start: log in as admin / admin, the setup wizard follows (new password, folders, machines)
.venv\Scripts\uvicorn app.main:app --host 0.0.0.0 --port 8000
```

Raspberry Pi / Linux, as a service that starts on boot:

```bash
scp -r server pi@<pi-address>:~/pdb-server      # from the Windows PC (skip .venv)
ssh pi@<pi-address>
cd ~/pdb-server && sudo bash deploy/pi/install.sh
```

The script installs to `/opt/pdb`, keeps the database in `/var/lib/pdb`, settings in
`/etc/pdb.env`, and runs the server as service `pdb` (`systemctl status pdb`, logs:
`journalctl -u pdb -f`). Run it again to update; data stays.

Without sudo: `bash deploy/pi/install-user.sh` runs it as a user service. Needs
`loginctl enable-linger <user>` once. Data in `~/.local/share/pdb`, settings in `~/.config/pdb.env`,
status `systemctl --user status pdb`, logs `journalctl --user -u pdb -f`.

Update from Windows (copies code, keeps data):
`tar --exclude=.venv --exclude=__pycache__ --exclude=*.sqlite3* --exclude=dev-data -czf - . | ssh user@server "tar -xzf - -C ~/pdb-server && cd ~/pdb-server && bash deploy/pi/install-user.sh"`

One Windows PC without a separate server: the local package (`install/make-test-zip.sh`,
Start-PDB.cmd), see the main README. A Windows server for several PCs: `install/PDB-Server-einrichten.cmd`.

Web UI: `http://<server>:8000/` (new installation: log in as admin / admin, then the setup wizard).
API docs (try every endpoint in the browser): `http://<server>:8000/docs`

Sample data (made up: 4 machines, users `anna`/`tom`/`lina` without password, 5 parts) on a NEW
database: `PDB_DEMO_ADMIN_PASSWORD=... .venv/Scripts/python scripts/demo_sample.py http://localhost:8000`.

**Dev server** (used by the helper's flow tests): `bash scripts/dev_server.sh` (`--fresh` to start
over) - sample data on `http://127.0.0.1:8000`, admin password `devadmin` or `$PDB_FLOW_ADMIN_PASSWORD`.

## Taking over existing files

- **Any Mastercam files**: web UI, Admin → Import (`routers/file_import.py`). Files are listed in the
  browser (nothing uploaded), grouped into cards, get new PDB numbers and are copied into their
  operation folders - by the server if it reaches the path, else by the PDB Helper on first open.
  The originals are never changed.
- **An existing structure of numbered operation folders** (`<prefix><nr>` folders with posted `.nc`): `scripts/import_folders.py`.
  It runs on a Windows PC that sees the share. For every `<prefix><nr>` folder it reads the posted `.nc`
  header (DB-NR, TEILENAME, ZCH-NR, OP-NAME, PROGRAMMIERER), groups operations into parts by drawing
  number + part name, matches the programmer to a PDB user by display name (else the name goes into
  the comment) and uploads `Pic<nr>.png.cam` (else `.png`). Folders without a posted `.nc` become
  "Unbekannt (<prefix><nr>)" parts. Machines are matched by their Mastercam folder. Keeps the existing
  numbers; the prefix is the server's folder name setting or `--prefix`.

```powershell
server\.venv\Scripts\python server\scripts\import_folders.py --server http://pdb-server:8000 --user admin
server\.venv\Scripts\python server\scripts\import_folders.py --server http://pdb-server:8000 --user admin --machine DMU50 --apply
```

Dry run unless `--apply`; asks for the password; existing numbers are skipped (safe to repeat).

## Web UI structure

Plain HTML + JavaScript modules in `app/static`, no build step and no internet needed.

| File | Purpose |
| --- | --- |
| `js/app.js` | Layout, navigation, routes. New screen = new file in `js/views/` + one line in `routes` |
| `js/api.js` | All server calls (token, error messages) |
| `js/store.js` | Current user, fields, machines, users; loaded once |
| `js/fields.js` | Draws inputs and values from the field definitions, so new fields need no code |
| `js/ui.js` | Element builder, dialogs, tables, toasts |
| `js/i18n.js`, `js/i18n/*.js` | Translation of the German UI (en, it, lt); new German text → add it to each dictionary |
| `js/views/*.js` | Search, part card, operation dialogs, locks, import, admin screens |
| `css/app.css` | Colours as tokens at the top |

Tests: `.venv/Scripts/python -m pytest` (Linux: `.venv/bin/python -m pytest`).
Curl smoke test against a running server with a fresh database: `bash scripts/smoke.sh http://localhost:8000`.

## First start of a new installation

An empty database gets the built-in user **admin / admin** (or `PDB_ADMIN_PASSWORD`). Its first
login opens the **setup wizard**: company name, admin name and a new password (the default one is
refused), Mastercam and NC main folders, fields (presets or one by one), machines, and where the
numbering starts (`GET /setup/status`, `POST /setup`, admin only, once). Any admin still on the
default password is made to change it at login. Change it right away on a server in the network:
until then anyone who knows the default can log in.

## Naming (admin settings → Benennung)

| Setting | New installation | Meaning |
| --- | --- | --- |
| `folder_prefix` | `PDB` | Operation folder `<prefix><number>`, e.g. `PDB1001` |
| `placeholder_prefix` | `PDB_` | NC header placeholders `#<prefix>FN`, `#<prefix>Partname`, `#<prefix>PGM`, ... |
| `info_file_extension` | empty | Info file next to the exported program, e.g. `.INF`; empty = none |

An installation that already had operations when these settings were introduced keeps the names its
folders and post processors use. Changing them later does not rename existing folders.

## Settings (environment variables)

| Variable | Default | Meaning |
| --- | --- | --- |
| `PDB_DATABASE_URL` | `sqlite:///./pdb.sqlite3` | Postgres: `postgresql+psycopg://user:pw@host/pdb` (plus `pip install psycopg`) |
| `PDB_DATA_DIR` | `./data` | Pictures, downloads (helper/bridge files), `backups/` |
| `PDB_LOCK_TIMEOUT_MINUTES` | `10` | A lock without heartbeat this long is stale and can be taken over |
| `PDB_TOKEN_HOURS` | `12` | How long a login stays valid |
| `PDB_ADMIN_USER` / `PDB_ADMIN_PASSWORD` | `admin` / empty | Built-in admin on an empty database; empty password = `admin` (must be changed at first login) |
| `PDB_BACKUP_HOURS` / `PDB_BACKUP_KEEP` | `24` / `14` | SQLite backup at start and every N hours into `PDB_DATA_DIR/backups`; `0` = off |

Folders and counters are set in the database by an admin (`/admin/settings`, `/admin/counters`),
e.g. `mcam_root` = `M:\CAM\Machines`, `nc_root` = `H:\NC`; counters start at 0.

## Model

- **Part card (`articles`)**: card fields (Teilename, Zeichnungsnummer, Kunde, ...).
- **Operation (`operations`)**: one process data row = one Mastercam file = one NC program.
  Has its own PDB number, op number, machine and (optional) NC program number.
- **Fields**: system fields (PDB-Nr., Nr., NC-Programmnummer, Maschine) are columns and
  cannot be disabled. All other fields are rows in `field_definitions`; admins enable/disable,
  rename, reorder, mark required/searchable and add new ones. Values live in a JSON column, so
  no schema change is needed.
- **Counters**: `mdm` and one `nc:<machine id>` per machine. `value` is the last number used.
  Handing out a number is atomic; numbers already in use are skipped.
- **Imported files (`imported_files`)**: where an imported operation's file came from, and whether
  it has been copied into the operation folder yet.

## Paths (as the PCs see them)

| Key | Example |
| --- | --- |
| folder | `M:\CAM\Machines\DMU50\PDB84197` |
| mcam | `...\PDB84197\00084197.mcam` |
| nc_posted / nc_old | `...\00084197.nc` / `...\00084197-old.nc` |
| next_archive | `...\PDB84197\Revisionen\00084197_r0003.mcam` |
| nc_export (naming `mdm`) | `H:\NC\DMU50\00084197.H` |
| nc_export (naming `nc`, FANUC) | `H:\NC\HAAS_VF2\1101.NC` |
| nc_info | same name with the info file extension (only if set) |

## Helper workflow

1. `POST /auth/login` with `pc_name` → token (send as `Authorization: Bearer ...`).
2. `GET /articles?q=...` to search, `GET /operations/{id}` for one operation.
3. `POST /operations/{id}/checkout` → 200 with paths, or 409 with who holds it → open read-only.
4. `POST /operations/{id}/heartbeat` every 1-2 minutes while the file is open. 409 = lock lost.
5. Save: write temp file, rename over `paths.mcam`, copy to `paths.next_archive`, then
   `POST /operations/{id}/checkin {revision: current+1, comment}`. `keep_lock: true` to keep working.
6. Close without saving: `POST /operations/{id}/release`.
7. NC export: `POST /operations/{id}/nc-export` → refused if status is "Gesperrt"; returns
   paths, header placeholder values (`#PDB_FN`, ...) and the optional info file text.
8. Machine change: `POST /operations/{id}/machine` → old and new paths; the helper moves the folder.
9. Imported file not copied yet (`import_source` in the operation): the helper copies it into
   `paths.mcam` on first open and reports `POST /operations/{id}/import-copied`.

## Not done yet

- Header template per machine is stored but not used; the post already writes the placeholders.
- Windows login (single sign-on); backups to another device.
- Postgres is supported by SQLAlchemy but not tested yet.
