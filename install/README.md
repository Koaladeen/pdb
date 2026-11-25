# PDB installieren

## 0. Nur ausprobieren (eigener Windows-PC, keine Admin-Rechte)
1. Python 3.11+ installieren, falls nicht vorhanden (`winget install Python.Python.3.12` oder python.org,
   „Add to PATH“ anhaken).
2. **`Start-PDB.cmd`** doppelklicken. Erster Start dauert ca. 1 Minute, dann öffnet sich der
   Browser. Anmelden als **Administrator**, Passwort **admin** → eigenes Passwort festlegen → Einrichtungs-Assistent
   (Ordner sind schon ausgefüllt: `Dokumente\PDB-Test`).
3. Fenster schliessen = PDB beendet. Nochmal doppelklicken = weiter mit denselben Daten.
   Neu anfangen: `Start-PDB.cmd -Reset`. Von anderen PCs erreichbar: `-Network`.
Mastercam und PDB Helper braucht es zum Ausprobieren nicht (Karten, Suche, Admin gehen ohne).

*Quick try (EN/IT): install Python 3.11+, double-click `Start-PDB.cmd`, log in as admin / admin, then
the setup wizard follows. Language: Mein Profil → Darstellung → Sprache.*

## 1. Server (einmal)
- **Windows:** `PDB-Server-einrichten.cmd` doppelklicken (fragt nach Admin-Rechten).
  Prüft/installiert Python, kopiert den Server nach `C:\PDB`, startet ihn bei jedem Hochfahren
  (Aufgabe „PDB Server“), öffnet Port 8000 in der Firewall und dann den Browser.
  Optionen: `install-server-windows.ps1 -InstallDir D:\PDB -Port 8080 -DryRun -Uninstall`
- **Linux / Raspberry Pi:** `server/deploy/pi/install.sh` (systemd) oder `install-user.sh` (ohne sudo).

Erste Anmeldung mit **admin / admin** → neues Passwort → **Einrichtungs-Assistent**: Firma, Administrator, Ordner, Felder,
Maschinen, erste PDB-Nummer. Erneutes Ausführen aktualisiert das Programm; Daten bleiben.

## 2. Arbeitsplätze (IT)
`install-workstation.ps1 -Server http://<server>:8000 [-Autostart]` als Admin, mit
`PdbHelper.exe` und `bridge\net48|net10\PdbBridge.dll` daneben. Installiert den PDB Helper für alle
Benutzer, `pdb://`-Links, die Serveradresse und die Bridge in jedes Mastercam 2025+.
Vorher: Helper signieren bzw. im Virenschutz/EDR freigeben lassen.
