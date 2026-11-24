@echo off
rem Starts PDB (first start: sets it up). Options: see README.txt
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install\pdb-local.ps1" %*
if errorlevel 1 pause
