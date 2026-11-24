@echo off
rem Double-click: sets up the PDB server on this PC (asks for administrator rights).
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile -ExecutionPolicy Bypass -NoExit -File \"%~dp0install-server-windows.ps1\"'"
