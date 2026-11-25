@echo off
rem Stops PDB (server and PDB Helper) of this folder.
start "" "%~dp0PDB.exe" --stop
