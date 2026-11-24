# Changelog

## 0.2.1

- Fix: switching the language back from a translated one (e.g. Lithuanian → German) did not stick -
  the page reloaded before the choice was saved.

## 0.2.0 – installer

- **PDB-Setup.exe**: wizard installer for one PC in German, English, Italian and Lithuanian:
  folders, shortcuts, start at login, PDB Helper and Mastercam bridge, port, network access, own
  database or demo. Per-user, no admin rights; Python 3.12 is included, works offline.
- **PDB.exe** launcher: start in the background, stop, uninstall (Windows Settings → Apps).
- Updates keep data and settings; an existing database from the zip package is taken over.


## 0.1.0 – first release

- Part cards with operations; consecutive MDM numbers, NC program numbers per machine (FANUC).
- Configurable fields (enable, rename, order, required, own fields, choice values).
- Customers, users with or without password, profile pictures, per-user appearance and language
  (German, English, Italian, Lithuanian), open cards and recent searches per user.
- Locks while editing, versions on save, NC export with header placeholders and `.MDM` info file,
  machine change that moves the folder and switches the Mastercam machine definition.
- PDB Helper (tray app) 0.2.7 and Mastercam bridge 0.3.0 (Mastercam 2025 and 2026+).
- Setup wizard: built-in admin / admin, forced new password, folders, fields (presets or one by
  one), machines, start of MDM numbering.
- Import of existing Mastercam files into cards (originals stay untouched).
- Automatic daily SQLite backups.
- Local package for one PC (Start-PDB.cmd), Windows server installer, workstation roll-out script.
