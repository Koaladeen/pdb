<#
.SYNOPSIS
  Sets up one CAM workstation for PDB - meant for IT (software deployment or by hand, as admin).

.DESCRIPTION
  - PDB Helper -> C:\Program Files\PDB\PdbHelper.exe
  - server address for all users of this PC -> %ProgramData%\PDB\helper-defaults.json
  - pdb:// links for all users (HKLM) and optionally start with Windows for all users
  - PDB bridge into every installed Mastercam 2025+ (chooks folder of that version)
  Each user then only sets "Start/Ende -> Systemstart" once - or uses "Mastercam-Bridge
  einrichten" in the helper's tray menu, which does it for them.

  powershell -ExecutionPolicy Bypass -File install\install-workstation.ps1 -Server http://pdb-server:8000
  Options: -Autostart (start helper with Windows for all users), -DryRun, -Uninstall
  Expects next to this script: PdbHelper.exe and the bridge builds
    bridge\net48\PdbBridge.dll (Mastercam 2025) and bridge\net10\PdbBridge.dll (Mastercam 2026+)
  (helper/release.sh output, or helper\dist + mastercam-addin from the repository).
#>
param(
    [Parameter(Mandatory = $true)][string]$Server,
    [switch]$Autostart,
    [switch]$DryRun,
    [switch]$Uninstall,
    [string]$Files = $PSScriptRoot
)
$ErrorActionPreference = "Stop"
$Target = Join-Path $env:ProgramFiles "PDB"
$Defaults = Join-Path $env:ProgramData "PDB\helper-defaults.json"

function Step([string]$text) { Write-Host "== $text" -ForegroundColor Cyan }
function Do-It([string]$what, [scriptblock]$action) {
    if ($DryRun) { Write-Host "   (dry run) $what" -ForegroundColor DarkGray } else { & $action }
}

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin -and -not $DryRun) { Write-Host "Please run as administrator." -ForegroundColor Yellow; exit 1 }

# Mastercam versions with a bridge build: NETHook3_0.dll tells .NET Framework (2025) or .NET (2026+)
function Get-Mastercams {
    foreach ($dir in Get-ChildItem $env:ProgramFiles -Directory -Filter "Mastercam*") {
        foreach ($exeDir in @($dir.FullName, (Join-Path $dir.FullName "Mastercam"))) {
            $exe = Join-Path $exeDir "Mastercam.exe"; $hook = Join-Path $exeDir "NETHook3_0.dll"
            if (-not (Test-Path $exe) -or -not (Test-Path $hook)) { continue }
            $year = 1998 + (Get-Item $exe).VersionInfo.FileMajorPart
            if ($year -lt 2025) { continue }
            $flavor = if ([Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($hook)).Contains(".NETCoreApp,Version=")) { "net10" } else { "net48" }
            [pscustomobject]@{ Year = $year; Dir = $exeDir; Flavor = $flavor }
        }
    }
}

if ($Uninstall) {
    Step "Removing PDB Helper, links, autostart and bridges (user settings stay)"
    Do-It "remove $Target" { Get-Process PdbHelper -ErrorAction SilentlyContinue | Stop-Process -Force; Remove-Item $Target -Recurse -Force -ErrorAction SilentlyContinue }
    Do-It "remove HKLM pdb:// and Run entry" {
        Remove-Item "HKLM:\Software\Classes\pdb" -Recurse -Force -ErrorAction SilentlyContinue
        Remove-ItemProperty "HKLM:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "PdbHelper" -ErrorAction SilentlyContinue
    }
    foreach ($mc in Get-Mastercams) { Do-It "remove bridge from Mastercam $($mc.Year)" { Remove-Item (Join-Path $mc.Dir "chooks\PdbBridge.*") -Force -ErrorAction SilentlyContinue } }
    exit 0
}

$helper = Join-Path $Files "PdbHelper.exe"
if (-not (Test-Path $helper)) { throw "PdbHelper.exe not found in $Files" }

Step "PDB Helper -> $Target"
Do-It "copy PdbHelper.exe" {
    Get-Process PdbHelper -ErrorAction SilentlyContinue | Stop-Process -Force
    New-Item -ItemType Directory -Force -Path $Target | Out-Null
    Copy-Item $helper (Join-Path $Target "PdbHelper.exe") -Force
}

Step "Server address for all users: $Server"
Do-It "write $Defaults" {
    New-Item -ItemType Directory -Force -Path (Split-Path $Defaults) | Out-Null
    @{ ServerUrl = $Server.TrimEnd('/') } | ConvertTo-Json | Set-Content -Path $Defaults -Encoding UTF8
}

Step "pdb:// links for all users"
Do-It "HKLM\Software\Classes\pdb" {
    $key = "HKLM:\Software\Classes\pdb"
    New-Item -Path "$key\shell\open\command" -Force | Out-Null
    Set-ItemProperty -Path $key -Name "(default)" -Value "URL:PDB Helper"
    Set-ItemProperty -Path $key -Name "URL Protocol" -Value ""
    Set-ItemProperty -Path "$key\shell\open\command" -Name "(default)" -Value "`"$Target\PdbHelper.exe`" `"%1`""
}

if ($Autostart) {
    Step "Start with Windows for all users"
    Do-It "HKLM Run PdbHelper" { Set-ItemProperty "HKLM:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "PdbHelper" -Value "`"$Target\PdbHelper.exe`"" }
}

Step "PDB bridge into Mastercam"
$mcs = @(Get-Mastercams)
if ($mcs.Count -eq 0) { Write-Host "   no Mastercam 2025+ found" }
foreach ($mc in $mcs) {
    $dll = Join-Path $Files "bridge\$($mc.Flavor)\PdbBridge.dll"
    if (-not (Test-Path $dll)) { Write-Host "   Mastercam $($mc.Year): $dll missing - skipped" -ForegroundColor Yellow; continue }
    Do-It "Mastercam $($mc.Year) ($($mc.Flavor)) -> $($mc.Dir)\chooks" {
        Copy-Item $dll (Join-Path $mc.Dir "chooks\PdbBridge.dll") -Force
        @"
# PDB Bridge: lets the PDB Helper open files in this running Mastercam.
APPLICATION "_PdbBridge_"
FUNC_DLL   "PdbBridge.dll"
RES_DLL    "SAME"
CATEGORY   "PDB"
FUNCTION NET "Run"
END_FUNCTION
"@ | Set-Content -Path (Join-Path $mc.Dir "chooks\PdbBridge.ft") -Encoding ASCII
    }
    Write-Host "   Mastercam $($mc.Year): each user sets once 'Datei > Konfiguration > Start/Ende > Systemstart' = PdbBridge.dll"
}

Write-Host ""
Write-Host "Workstation ready. Users open files from the PDB page ($Server) with 'Öffnen' (Open)." -ForegroundColor Green
