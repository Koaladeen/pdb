# PDB – file management for Mastercam programs

PDB keeps CNC programming organised: a **card per part** with its **operations**, every operation
with its own consecutive **MDM number**, folder and Mastercam file, **locks** while someone edits a
file, **versions** on every save, **NC export** with program header, and **machine changes** that
move the files along. Web interface in German, English, Italian and Lithuanian.

| Component | What it does |
| --- | --- |
| [`server/`](server/) | FastAPI + SQLite (Postgres-ready) server with the web UI. Runs on a Raspberry Pi, a Windows server or the user's own PC. |
| [`helper/PdbHelper`](helper/) | Windows tray app on each CAM PC: opens files in Mastercam from the web page, locks, checks in on save, NC export, uploads operation pictures. |
| [`helper/PdbBridge`](helper/) | Mastercam add-in (NET-Hook) so files open in the *running* Mastercam and the machine definition can be switched. |
| [`install/`](install/) | Installers: local package for one PC, Windows server, workstation roll-out for IT. |

## Quick start (one Windows PC)

Download `PDB-Local-<version>.zip` from the [releases](../../releases), unzip it to a permanent
folder, install Python 3.11+ if needed and double-click **Start-PDB.cmd**. Log in as
**admin / admin**, set your own password, and the setup wizard asks for folders, fields and
machines. Existing Mastercam files: Admin → Import. Details: `README.txt` in the package.

## Server for several PCs

- **Windows**: `install/PDB-Server-einrichten.cmd` (as administrator) – Python environment, start at
  boot, firewall rule.
- **Raspberry Pi / Linux**: `server/deploy/pi/install.sh` (systemd) or `install-user.sh` (no sudo).
- **Workstations**: `install/install-workstation.ps1 -Server http://<server>:8000` installs the
  PDB Helper, `pdb://` links and the bridge into every Mastercam 2025+.

See [server/README.md](server/README.md) for settings, the data model and the API.

## Development

```powershell
cd server; python -m venv .venv; .venv\Scripts\pip install -r requirements.txt
.venv\Scripts\python -m pytest                 # server tests
bash scripts/dev_server.sh                     # dev server with sample data on :8000
cd ..\helper; dotnet test PdbHelper.Tests      # helper tests (flow tests need the dev server)
bash helper/release.sh                         # build helper + bridge (PDB_HOST=... to upload)
bash install/make-test-zip.sh                  # build the local package (dist/PDB-Local.zip)
```

The web UI is plain JavaScript modules (no build step). UI texts are German; translations live in
`server/app/static/js/i18n/<lang>.js`.

## Notes

- PDB Helper and the bridge are unsigned. Companies with endpoint protection should sign them and
  roll them out through IT; the helper never installs itself, adds autostart or updates itself
  unless configured to.
- Mastercam is a trademark of CNC Software, LLC. PDB is an independent project and not affiliated
  with CNC Software or any vendor of Mastercam data management products.

## License

[MIT](LICENSE)
