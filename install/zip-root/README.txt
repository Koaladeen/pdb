PDB - file management for Mastercam programs
============================================

PDB keeps your Mastercam programs organised: a card per part with its operations, each
operation with its own PDB number and folder, locks while a file is being edited, versions,
NC export with program header, machine changes. Web interface in German, English, Italian and
Lithuanian. This package runs PDB on your own Windows PC with your own database - no server,
no admin rights needed.

INSTALL (once)
1. Unzip this folder to a permanent place, e.g. Documents\PDB-App
   (not inside the zip preview, not in Downloads if you clean that up - shortcuts point here).
2. Python 3.11 or newer is required. If you don't have it:
     winget install Python.Python.3.12
   or python.org -> Downloads (tick "Add python.exe to PATH" during install).
3. Double-click  Start-PDB.cmd
   - First start prepares everything (about a minute).
   - Choose 1 = your own database (or 2 = demo with sample data, just to look around).
   - It offers shortcuts "PDB" / "PDB beenden" (desktop + Start menu) and starting at login.
4. The browser opens. Log in as Administrator with password  admin , choose your own password.
   The setup wizard then asks for:
   - main folders for Mastercam files and NC programs (suggested: Documents\PDB\...; you can
     use any folder, also a network drive),
   - which fields the part cards and operations should have,
   - your machines (folder names, NC file naming, extension, Mastercam machine definition),
   - where the numbering starts.

EVERY DAY
- Start: shortcut "PDB" (or Start-PDB.cmd). PDB runs in the background; the shortcut opens it.
- Stop:  shortcut "PDB beenden" (or Stop-PDB.cmd).
- Users: Admin -> Benutzer/Users. Users without a password log in by clicking their name.

YOUR EXISTING MASTERCAM FILES
Log in as Administrator -> "Import". Choose a folder (only names/sizes are read), tell PDB where
that folder is (e.g. C:\Users\you\Documents\Old programs), group the files into cards - by
folder, by file name, or freely - and import. Every file gets a PDB number and is COPIED into
its PDB folder; your originals are not changed. Files imported before are skipped.

OPENING FILES IN MASTERCAM (PDB Helper)
Start-PDB also starts the PDB Helper (tray icon near the clock). It opens files in Mastercam,
locks them while you work and checks them in when you save. Once, for Mastercam 2025 or newer:
right-click the tray icon -> "Mastercam-Bridge einrichten", then restart Mastercam - files then
open in the running Mastercam. Windows may warn about the unsigned PdbHelper.exe ("More info" ->
"Run anyway").

DATA AND BACKUPS
- Database: %LOCALAPPDATA%\PDB\server\pdb.sqlite3
- Backups:  %LOCALAPPDATA%\PDB\server\files\backups (automatic, daily, last 14 kept)
  Restore: stop PDB, copy a backup over pdb.sqlite3, start PDB.
- Your Mastercam/NC files live in the folders you chose in the setup wizard.

OPTIONS (Start-PDB.cmd ...)
  -Console     server in a window instead of the background (closing it stops PDB)
  -Port 8080   if port 8000 is already used
  -Network     reachable from other PCs, so colleagues can use the same database
               (Windows asks about the firewall; they open http://<your-PC>:8000)
  -Shortcuts   ask about shortcuts / autostart again
  -Reset       start over with a new database (the old one is kept as a copy)

Uninstall: Stop-PDB.cmd, delete the shortcuts, this folder and %LOCALAPPDATA%\PDB.
A server for several PCs (starts with Windows, admin rights): install\PDB-Server-einrichten.cmd
