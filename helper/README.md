# PDB Helper (tray app)

Small Windows tray app, like XDB's tray app. The PDB web page asks it to open files; it does
the file work on this PC, so the server (Raspberry Pi) never needs access to `M:` or `H:`.

| What | How |
| --- | --- |
| Open | Web page → `http://127.0.0.1:5017/open` → check-out on the server → `.mcam` opens with the Windows association (or a set Mastercam.exe). Locked by someone else → read-only copy from `%TEMP%\PDB\nur-lesen`. Fallback link `pdb://open/<id>` if the browser blocks 127.0.0.1. |
| Keep lock | Heartbeat to the server every 60 s while the file is open. |
| Save | `.mcam` changes → copy to `Revisionen\<nr>_rNNNN.mcam` → check-in as next version (lock kept). |
| Close | File name gone from all Mastercam window titles (3 checks, 5 s apart, 90 s grace after start) → last check-in if changed → lock released. |
| NC export | Posted `<nr>.nc` with `#PDB_...` placeholders (prefix from the server) → server checks status/rights → header filled → previous export kept as `-old.nc` → copied to the machine's NC folder → optional info file written. Extra placeholder `#<prefix>PGM` = program name (NC number on FANUC machines). |

Settings and encrypted login: `%APPDATA%\PDB\helper.json`. Log: `%APPDATA%\PDB\logs`.
Registers `pdb://` links for the current user (HKCU) on start; "Mit Windows starten" in the menu.

## Mastercam bridge (open in the running Mastercam)

Windows always starts a NEW Mastercam for a .mcam (McamVersionSelector.exe). PDB avoids that with
its own add-in, `PdbBridge` (NET-Hook, `PdbBridge/`):

- In Mastercam it listens on the local pipe `PdbBridge-<process id>`; the helper sends
  `OPEN <path>` and the bridge calls `FileManager.Open(true, path)` in that Mastercam (asks to
  save the current file first) and brings the window to the front. `CURRENT` returns the open
  file, which the helper uses for close detection instead of the window title.
- Built twice from one source: `net48` for Mastercam 2025, `net10.0-windows` for Mastercam 2027
  (`mastercam-addin\Mastercam 2025|2027\PdbBridge.dll`).
- Load it: Mastercam **Datei → Konfiguration → Start/Ende → Systemstart** = the DLL (every start),
  or once per session with Alt+C (Add-In ausführen). Running it again shows its status.
- Without the bridge the helper falls back to starting a new Mastercam.
- Bridge log: `%APPDATA%\PDB\logs\bridge-<date>.log`.

## Install, updates, bridge setup (no admin rights, nothing on M:)

- **Install:** "Installieren" in the PDB top bar downloads `PdbHelper.exe` from the server. Started
  from anywhere, it copies itself to `%LOCALAPPDATA%\PDB\PdbHelper.exe`, runs from there, registers
  `pdb://` and switches on "Mit Windows starten" once (stays off if the user turns it off).
- **Start from the page:** "Starten" = `pdb://start`.
- **Updates:** every 10 min the helper reads `/downloads/version.json`; a higher version is
  downloaded, checked (SHA-256) and swapped in when no file is open for editing.
- **Bridge:** tray → "Mastercam-Bridge einrichten …" (once per user). For every installed
  Mastercam ≥ 2025 the matching build (2025 = net48, 2026+ = net10, read from NETHook3_0.dll) goes to
  `%LOCALAPPDATA%\PDB\bridge\<build>\<version>\PdbBridge.dll` and, while that Mastercam is closed,
  into "Start/Ende → Systemstart" of the user's `mcamxm.config` (backup `.vor-pdb`; another add-in
  already set there is never replaced). Local on purpose: .NET Framework refuses add-ins from network
  paths, the network can be down at start, and no write rights on M: are needed.
- **Release:** raise `<Version>`, run `bash helper/release.sh` (tests, builds, uploads to the Pi).

## Build, test, publish

```powershell
cd helper
dotnet test                       # flow test needs the demo server on 127.0.0.1:8000, or set PDB_FLOW_TEST=0
dotnet publish PdbHelper/PdbHelper.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist
```

`dist\PdbHelper.exe` is a single file and needs no .NET installed.

## Code

| File | Purpose |
| --- | --- |
| `Program.cs` | Single instance; a second start passes its `pdb://` link to the running one |
| `TrayApp.cs` | Tray icon, menu, login state, notifications |
| `Sessions.cs` | Open files: check-out, heartbeat, save → check-in, close → release |
| `NcExport.cs` | Header placeholders (pure, tested) and the export steps |
| `Mastercam.cs` | `IMastercam`: start Mastercam, detect open files via window titles (swap for an add-in later) |
| `LocalApi.cs` | The 127.0.0.1 endpoint for the web page, with origin check |
| `ServerApi.cs` | Calls to the PDB server |
| `Windows.cs` | `pdb://` links, autostart, login window |
| `Config.cs`, `Log.cs` | Settings, log |

## Known limits (demo)

- Close detection relies on Mastercam showing the file name in its window title.
- Only files opened through the helper are watched; posting from a file opened directly from the
  share is not exported automatically.
- Machine change in the open Mastercam file and the Word tool list still need Mastercam itself.
- Chrome may ask once to allow the PDB page to access apps on this device (local network access).
