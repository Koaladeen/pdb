<#
.SYNOPSIS
  PDB on one Windows PC - your own database, your own Mastercam files. No admin rights needed.

.DESCRIPTION
  Start-PDB.cmd runs this. First start:
  - prepares Python packages in %LOCALAPPDATA%\PDB\server (about a minute)
  - asks: your own database (setup wizard) or a demo with sample data
  - offers shortcuts (desktop, Start menu) and starting PDB when you log in
  Then: the PDB server runs in the background (no window), the browser opens, and the PDB Helper
  (tray icon, opens files in Mastercam) is started if it is included.

  Options:
    -Console     run the server in this window instead (closing the window stops it)
    -Stop        stop PDB
    -Shortcuts   ask about shortcuts / autostart again
    -Port 8080   another port (default 8000)
    -Network     reachable from other PCs (Windows asks about the firewall)
    -NoBrowser   don't open the browser      -NoHelper  don't start the PDB Helper
    -Reset       start over with a NEW database (the old one is kept as a copy)
    -Demo        first start: demo data without asking
  Data:     %LOCALAPPDATA%\PDB\server (database, backups in ...\files\backups, log)
  Settings for the PDB Helper: %APPDATA%\PDB\helper.json
#>
param(
    [int]$Port = 8000,
    [switch]$Console, [switch]$Background, [switch]$Stop, [switch]$Shortcuts,
    [switch]$Network, [switch]$NoBrowser, [switch]$NoHelper, [switch]$Reset, [switch]$Demo
)
$ErrorActionPreference = "Stop"
$Pkg = Split-Path $PSScriptRoot -Parent
$Source = Join-Path $Pkg "server"
$Data = Join-Path $env:LOCALAPPDATA "PDB\server"
$Venv = Join-Path $Data ".venv"
$Py = Join-Path $Venv "Scripts\python.exe"
$Db = Join-Path $Data "pdb.sqlite3"
$Files = Join-Path $Data "files"
$Log = Join-Path $Data "server.log"
$State = Join-Path $Data "launcher.json"
$Docs = Join-Path ([Environment]::GetFolderPath("MyDocuments")) "PDB"
$Url = "http://localhost:$Port/"
$HelperExe = Join-Path $Pkg "helper\PdbHelper.exe"
$interactive = -not $Background

function Say([string]$text, [string]$color = "Gray") { if ($interactive) { Write-Host $text -ForegroundColor $color } }
function Fail([string]$text) {
    if ($interactive) { Write-Host $text -ForegroundColor Red; Read-Host "Enter" }
    else { Add-Type -AssemblyName PresentationFramework; [void][System.Windows.MessageBox]::Show($text, "PDB") }
    exit 1
}
function Running { try { return (Invoke-WebRequest -UseBasicParsing "http://127.0.0.1:$Port/health" -TimeoutSec 2).StatusCode -eq 200 } catch { return $false } }
function ServerProcess {
    $c = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($c) { Get-Process -Id $c.OwningProcess -ErrorAction SilentlyContinue }
}
function ReadState { try { return Get-Content $State -Raw | ConvertFrom-Json } catch { return [pscustomobject]@{} } }
function SaveState($s) { New-Item -ItemType Directory -Force -Path $Data | Out-Null; $s | ConvertTo-Json | Set-Content $State -Encoding UTF8 }

# ------------------------------------------------------------------ stop
if ($Stop) {
    $p = ServerProcess
    if ($p -and $p.ProcessName -like "python*") { Stop-Process -Id $p.Id -Force; Say "PDB stopped." "Green" }
    else { Say "PDB is not running." }
    Get-Process PdbHelper -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    exit 0
}

if (-not (Test-Path (Join-Path $Source "app\main.py"))) { Fail "Server folder not found ($Source). Unzip the whole PDB folder first." }
if ($Pkg -match '\\(Temp|AppData\\Local\\Temp)\\' -or $Pkg -match '\.zip\\') {
    Fail "Please unzip the PDB folder first (right-click the zip -> Extract all) to a permanent place, e.g. Documents\PDB-App, and start it from there."
}

# ------------------------------------------------------------------ already running: just open it
if ((Running) -and -not $Reset) {
    if (-not $NoBrowser) { Start-Process $Url }
    if (-not $NoHelper -and (Test-Path $HelperExe) -and -not (Get-Process PdbHelper -ErrorAction SilentlyContinue)) { Start-Process $HelperExe }
    exit 0
}

Say "PDB" "Cyan"

# ------------------------------------------------------------------ Python + packages
New-Item -ItemType Directory -Force -Path $Data, $Files | Out-Null
if (-not (Test-Path $Py)) {
    $python = $null
    foreach ($cand in @("py -3", "python")) {
        try {
            $v = & cmd /c "$cand -c ""import sys; print('%d.%d' % sys.version_info[:2])""" 2>$null
            if ($v -and [version]$v -ge [version]"3.11") { $python = $cand; break }
        } catch { }
    }
    if (-not $python) { Fail "Python 3.11 or newer is needed:`n  winget install Python.Python.3.12`nor python.org (tick 'Add python.exe to PATH'). Then start PDB again." }
    Say "== Preparing (first start, about a minute) ..."
    & cmd /c "$python -m venv ""$Venv"""
}
$req = Join-Path $Source "requirements.txt"
$stamp = Join-Path $Data "requirements.done"
if (-not (Test-Path $stamp) -or (Get-Item $req).LastWriteTime -gt (Get-Item $stamp).LastWriteTime) {
    & $Py -m pip install -q --disable-pip-version-check -r $req
    if ($LASTEXITCODE -ne 0) { Fail "Installing the Python packages failed (internet connection?)." }
    Set-Content $stamp (Get-Date)
}

# ------------------------------------------------------------------ database
if ($Reset -and (Test-Path $Db)) {
    if ($interactive -and (Read-Host "Start over with a NEW, empty database? The current one is kept as a copy. (y/n)") -notmatch '^(y|j)') { exit 0 }
    $p = ServerProcess; if ($p -and $p.ProcessName -like "python*") { Stop-Process -Id $p.Id -Force; Start-Sleep 1 }
    $keep = "$Db.before-reset-$(Get-Date -Format yyyyMMdd-HHmmss)"
    Move-Item $Db $keep
    Remove-Item "$Db-wal", "$Db-shm" -Force -ErrorAction SilentlyContinue
    Say "== Old database kept as $keep"
}
$fresh = -not (Test-Path $Db)
$useDemo = $false
if ($fresh) {
    if ($Demo) { $useDemo = $true }
    elseif ($interactive) {
        Say ""
        Say "New database / Neue Datenbank / Nauja duomenų bazė:" "Cyan"
        Say "  1 = My own database - set up machines, folders and fields in the setup wizard"
        Say "  2 = Demo with sample data (to look around; start over later with -Reset)"
        $useDemo = (Read-Host "1 / 2 [1]") -eq "2"
    }
}
$demoPassword = $env:PDB_DEMO_ADMIN_PASSWORD
if ($useDemo -and -not $demoPassword) {
    while ($true) {
        $sec = Read-Host "Choose a password for the Administrator (min. 6 characters)" -AsSecureString
        $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec))
        if ($plain.Length -ge 6 -and $plain -ne "admin") { $demoPassword = $plain; break }
        Say "At least 6 characters, not 'admin'." "Yellow"
    }
}
$mcam = Join-Path $Docs "Mastercam"; $nc = Join-Path $Docs "NC"

# ------------------------------------------------------------------ shortcuts / autostart (asked once)
$st = ReadState
if ($interactive -and ($Shortcuts -or -not $st.PSObject.Properties["shortcuts_asked"])) {
    $wsh = New-Object -ComObject WScript.Shell
    $ps = Join-Path $env:WINDIR "System32\WindowsPowerShell\v1.0\powershell.exe"
    $me = Join-Path $PSScriptRoot "pdb-local.ps1"
    $icon = if (Test-Path $HelperExe) { "$HelperExe,0" } else { "$ps,0" }
    function Link([string]$path, [string]$params, [string]$what) {
        $l = $wsh.CreateShortcut($path)
        $l.TargetPath = $ps
        $l.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$me`" -Background -Port $Port $params"
        $l.WorkingDirectory = $Pkg
        $l.IconLocation = $icon
        $l.Description = $what
        $l.Save()
    }
    Say ""
    if ((Read-Host "Create shortcuts 'PDB' and 'PDB beenden' on the desktop and in the Start menu? (y/n) [y]") -notmatch '^n') {
        $menu = Join-Path ([Environment]::GetFolderPath("Programs")) "PDB"
        New-Item -ItemType Directory -Force -Path $menu | Out-Null
        foreach ($dir in @([Environment]::GetFolderPath("Desktop"), $menu)) {
            Link (Join-Path $dir "PDB.lnk") "" "Start PDB and open it in the browser"
            Link (Join-Path $dir "PDB beenden.lnk") "-Stop" "Stop PDB"
        }
        Say "   Shortcuts created. They point to this folder - don't move it afterwards (or run Start-PDB.cmd -Shortcuts again)."
    }
    $startup = Join-Path ([Environment]::GetFolderPath("Startup")) "PDB.lnk"
    if ((Read-Host "Start PDB automatically when you log in to Windows? (y/n) [n]") -match '^(y|j)') {
        Link $startup "-NoBrowser" "Start PDB at login"
        Say "   PDB starts at login (remove: delete PDB.lnk in shell:startup)."
    } else { Remove-Item $startup -ErrorAction SilentlyContinue }
    $st | Add-Member -Force shortcuts_asked $true
    SaveState $st
}

# ------------------------------------------------------------------ PDB Helper (Mastercam integration)
$helperFiles = Join-Path $Pkg "helper"
if (Test-Path (Join-Path $helperFiles "version.json")) {
    # the helper fetches the Mastercam bridge from this server's downloads
    $dl = Join-Path $Files "downloads"
    New-Item -ItemType Directory -Force -Path $dl | Out-Null
    Get-ChildItem $helperFiles -File | Where-Object { $_.Name -ne "PdbHelper.exe" } | ForEach-Object { Copy-Item $_.FullName (Join-Path $dl $_.Name) -Force }
}
$helperConfig = Join-Path $env:APPDATA "PDB\helper.json"
if ((Test-Path $HelperExe) -and -not (Test-Path $helperConfig)) {
    New-Item -ItemType Directory -Force -Path (Split-Path $helperConfig) | Out-Null
    @{ ServerUrl = "http://localhost:$Port" } | ConvertTo-Json | Set-Content $helperConfig -Encoding UTF8
}

# ------------------------------------------------------------------ start the server
$env:PDB_DATABASE_URL = "sqlite:///" + ($Db -replace '\\', '/')
$env:PDB_DATA_DIR = $Files
$env:PDB_SUGGEST_MCAM_ROOT = $mcam
$env:PDB_SUGGEST_NC_ROOT = $nc
$bind = if ($Network) { "0.0.0.0" } else { "127.0.0.1" }
$uv = @("-m", "uvicorn", "app.main:app", "--host", $bind, "--port", "$Port", "--log-level", "warning")
Set-Location $Source
Say ""
Say "== starting PDB ..."
if ($Console) {
    $server = Start-Process -FilePath $Py -ArgumentList $uv -NoNewWindow -PassThru
} else {
    $server = Start-Process -FilePath $Py -ArgumentList $uv -WindowStyle Hidden -PassThru -RedirectStandardError $Log -RedirectStandardOutput "$Log.out"
}
$up = $false
foreach ($i in 1..60) { Start-Sleep -Milliseconds 500; if (Running) { $up = $true; break }; if ($server.HasExited) { break } }
if (-not $up) { Fail "PDB did not start. Port $Port busy? Try: Start-PDB.cmd -Port 8080`nLog: $Log" }

if ($useDemo) {
    $env:PDB_DEMO_ADMIN_PASSWORD = $demoPassword
    $env:PDB_DEMO_MCAM_ROOT = $mcam
    $env:PDB_DEMO_NC_ROOT = $nc
    & $Py (Join-Path $Source "scripts\demo_sample.py") "http://127.0.0.1:$Port"
    Remove-Item Env:PDB_DEMO_ADMIN_PASSWORD -ErrorAction SilentlyContinue
    $demoPassword = $null; $plain = $null
    $old = Join-Path $Docs "Alte Programme"  # placeholders to try the import with (not real Mastercam files)
    foreach ($f in @("Nordic Robotics\NR-20431_OP1.mcam", "Nordic Robotics\NR-20431_OP2.mcam", "Nordic Robotics\NR-20502.mcam",
                     "Baltic Marine\BMP-3310_front.mcam", "Baltic Marine\BMP-3310_back.mcam", "Baltic Marine\Backup\BMP-3310_front.mcam",
                     "Haas_VF2\Fixture plate.mcam")) {
        $p = Join-Path $old $f
        New-Item -ItemType Directory -Force -Path (Split-Path $p) | Out-Null
        if (-not (Test-Path $p)) { Set-Content -Path $p -Value "PDB demo placeholder - not a real Mastercam file" }
    }
}

if (-not $NoHelper -and (Test-Path $HelperExe) -and -not (Get-Process PdbHelper -ErrorAction SilentlyContinue)) { Start-Process $HelperExe }

Say ""
Say "PDB is running: $Url" "Green"
if ($useDemo) { Say "Log in: click Anna, Tom or Lina (no password) - or Administrator with your password." "Yellow" }
elseif ($fresh) { Say "First login: Administrator, password admin -> choose your own password -> setup wizard." "Yellow" }
Say "Your existing Mastercam files: log in as Administrator -> Import."
Say "Language: My profile (your name, top right) -> Appearance -> Language (DE/EN/IT/LT)."
Say "Database: $Db (automatic backups in $Files\backups)"
if (-not $NoBrowser) { Start-Process $Url }
if ($Console) {
    Say "Close this window to stop PDB." "DarkGray"
    try { Wait-Process -Id $server.Id } finally { if (-not $server.HasExited) { Stop-Process -Id $server.Id -Force } }
} elseif ($interactive) {
    Say "PDB keeps running in the background. Stop it with 'PDB beenden' (or Start-PDB.cmd -Stop)." "DarkGray"
    Start-Sleep 4
}
