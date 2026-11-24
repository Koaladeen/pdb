@echo off
rem Stops PDB (server and PDB Helper).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install\pdb-local.ps1" -Stop
