<#
.SYNOPSIS
  Sets up the PDB server on a Windows machine: Python environment, start at boot, firewall,
  then opens the setup wizard in the browser.

.DESCRIPTION
  Run as administrator from the extracted PDB folder:
    powershell -ExecutionPolicy Bypass -File install\install-server-windows.ps1
  Options:
    -InstallDir C:\PDB   where the server goes (default C:\PDB)
    -DataDir C:\PDB\data database, pictures, downloads (default <InstallDir>\data)
    -Port 8000           web port
    -DryRun              only show what would be done
    -Uninstall           remove start task and firewall rule (data and files stay)
  Running it again updates the program files; the data folder is never touched.
#>
param(
    [string]$InstallDir = "C:\PDB",
    [string]$DataDir = "",
    [int]$Port = 8000,
    [switch]$DryRun,
    [switch]$Uninstall
)
$ErrorActionPreference = "Stop"
$TaskName = "PDB Server"
$RuleName = "PDB Server (TCP $Port)"
if (-not $DataDir) { $DataDir = Join-Path $InstallDir "data" }
$Source = Join-Path (Split-Path $PSScriptRoot -Parent) "server"

function Step([string]$text) { Write-Host "== $text" -ForegroundColor Cyan }
function Do-It([string]$what, [scriptblock]$action) {
    if ($DryRun) { Write-Host "   (dry run) $what" -ForegroundColor DarkGray } else { & $action }
}

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin -and -not $DryRun) {
    Write-Host "Bitte als Administrator ausführen (Rechtsklick auf PowerShell -> Als Administrator ausführen)." -ForegroundColor Yellow
    Write-Host "Please run as administrator." -ForegroundColor Yellow
    exit 1
}

if ($Uninstall) {
    Step "Removing start task and firewall rule (files and data stay in $InstallDir)"
    Do-It "Unregister-ScheduledTask '$TaskName'" { Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue }
    Do-It "Remove firewall rule '$RuleName'" { Remove-NetFirewallRule -DisplayName $RuleName -ErrorAction SilentlyContinue }
    Write-Host "Done."
    exit 0
}

if (-not (Test-Path (Join-Path $Source "app\main.py"))) {
    throw "Server files not found next to this script ($Source). Run it from the extracted PDB folder."
}

# ---------------------------------------------------------------- Python
Step "Python"
$python = $null
foreach ($cand in @("py -3", "python")) {
    try {
        $v = & cmd /c "$cand -c ""import sys; print('%d.%d' % sys.version_info[:2])""" 2>$null
        if ($v -and [version]$v -ge [version]"3.11") { $python = $cand; Write-Host "   found $cand (Python $v)"; break }
    } catch { }
}
if (-not $python) {
    Write-Host "   Python 3.11 or newer is needed." -ForegroundColor Yellow
    $answer = Read-Host "   Install Python 3.12 with winget now? (y/n)"
    if ($answer -notmatch '^(y|j)') { throw "Please install Python 3.11+ (python.org) and run this script again." }
    Do-It "winget install Python.Python.3.12" { winget install --id Python.Python.3.12 --exact --scope machine --accept-source-agreements --accept-package-agreements }
    $python = "py -3"
}

# ---------------------------------------------------------------- files
Step "Program files -> $InstallDir (data stays in $DataDir)"
Do-It "copy server files" {
    New-Item -ItemType Directory -Force -Path $InstallDir, $DataDir | Out-Null
    robocopy $Source (Join-Path $InstallDir "server") /MIR /XD .venv __pycache__ .pytest_cache data demo-data /XF *.sqlite3* /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copy failed (robocopy $LASTEXITCODE)" }
}
$serverDir = Join-Path $InstallDir "server"
$venv = Join-Path $serverDir ".venv"

Step "Python environment"
Do-It "create venv and install requirements" {
    if (-not (Test-Path (Join-Path $venv "Scripts\python.exe"))) { & cmd /c "$python -m venv ""$venv""" }
    & (Join-Path $venv "Scripts\python.exe") -m pip install -q --upgrade pip
    & (Join-Path $venv "Scripts\python.exe") -m pip install -q -r (Join-Path $serverDir "requirements.txt")
}

# ---------------------------------------------------------------- start script + task at boot
Step "Start at boot (scheduled task '$TaskName', runs as SYSTEM)"
$startCmd = Join-Path $InstallDir "start-pdb.cmd"
$db = (Join-Path $DataDir "pdb.sqlite3") -replace '\\', '/'
$cmdText = @"
@echo off
rem PDB server - started at boot by the scheduled task "$TaskName". Settings below.
set PDB_DATABASE_URL=sqlite:///$db
set PDB_DATA_DIR=$(Join-Path $DataDir 'files')
set PDB_LOCK_TIMEOUT_MINUTES=10
set PDB_TOKEN_HOURS=12
cd /d "$serverDir"
"$venv\Scripts\python.exe" -m uvicorn app.main:app --host 0.0.0.0 --port $Port >> "$(Join-Path $DataDir 'server.log')" 2>&1
"@
Do-It "write $startCmd" { Set-Content -Path $startCmd -Value $cmdText -Encoding ASCII }
Do-It "register scheduled task" {
    $action = New-ScheduledTaskAction -Execute "cmd.exe" -Argument "/c `"$startCmd`""
    $trigger = New-ScheduledTaskTrigger -AtStartup
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -User "SYSTEM" -RunLevel Highest -Force | Out-Null
}

Step "Firewall: allow TCP $Port inbound"
Do-It "New-NetFirewallRule $RuleName" {
    if (-not (Get-NetFirewallRule -DisplayName $RuleName -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName $RuleName -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow -Profile Domain, Private | Out-Null
    }
}

Step "Start"
Do-It "start task and wait for the server" {
    Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    Start-ScheduledTask -TaskName $TaskName
    $ok = $false
    foreach ($i in 1..30) {
        Start-Sleep -Seconds 2
        try { if ((Invoke-WebRequest -UseBasicParsing "http://localhost:$Port/health" -TimeoutSec 2).StatusCode -eq 200) { $ok = $true; break } } catch { }
    }
    if (-not $ok) { throw "Server did not answer. See $(Join-Path $DataDir 'server.log')" }
}

$hostName = [System.Net.Dns]::GetHostName()
Write-Host ""
Write-Host "PDB is running: http://${hostName}:$Port/" -ForegroundColor Green
Write-Host "New installation: log in as admin / admin - you set a new password, then the setup wizard follows."
Write-Host "Data: $DataDir   Log: $(Join-Path $DataDir 'server.log')   Remove: -Uninstall"
if (-not $DryRun) { Start-Process "http://localhost:$Port/" }
