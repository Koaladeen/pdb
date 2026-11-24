using System;
using System.Collections.Generic;
using System.Globalization;

namespace Pdb.Install
{
    /// <summary>Texts of setup and launcher. Written in German; T() returns the current language.
    /// New text: add a line here (build.sh lists texts without translation).</summary>
    public static class L
    {
        public static string Lang = "de";
        public static readonly string[] Codes = { "de", "en", "it", "lt" };
        public static readonly string[] Names = { "Deutsch", "English", "Italiano", "Lietuvių" };

        public static string Detect()
        {
            var c = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            return Array.IndexOf(Codes, c) >= 0 ? c : "en";
        }

        public static string T(string de)
        {
            var i = Array.IndexOf(Codes, Lang) - 1;
            return i >= 0 && Texts.TryGetValue(de, out var t) ? t[i] : de;
        }

        public static string F(string de, params object[] args) => string.Format(T(de), args);

        // German → { English, Italian, Lithuanian }
        static readonly Dictionary<string, string[]> Texts = new Dictionary<string, string[]>
        {
            // buttons and general
            ["‹ Zurück"] = new[] { "‹ Back", "‹ Indietro", "‹ Atgal" },
            ["Weiter ›"] = new[] { "Next ›", "Avanti ›", "Toliau ›" },
            ["Abbrechen"] = new[] { "Cancel", "Annulla", "Atšaukti" },
            ["Installieren"] = new[] { "Install", "Installa", "Įdiegti" },
            ["Fertig stellen"] = new[] { "Finish", "Fine", "Baigti" },
            ["Schließen"] = new[] { "Close", "Chiudi", "Uždaryti" },
            ["Durchsuchen …"] = new[] { "Browse …", "Sfoglia …", "Naršyti …" },
            ["Installation abbrechen?"] = new[] { "Cancel the installation?", "Annullare l'installazione?", "Nutraukti diegimą?" },
            ["ja"] = new[] { "yes", "sì", "taip" },
            ["nein"] = new[] { "no", "no", "ne" },

            // welcome
            ["Willkommen"] = new[] { "Welcome", "Benvenuto", "Sveiki" },
            ["Dieser Assistent installiert PDB auf diesem PC."] = new[] { "This wizard installs PDB on this PC.", "Questa procedura installa PDB su questo PC.", "Šis vedlys įdiegs PDB šiame kompiuteryje." },
            ["PDB verwaltet Mastercam-Programme: eine Karte pro Teil mit ihren Operationen, Sperren beim Bearbeiten, Versionen, NC-Export und Maschinenwechsel."] = new[] {
                "PDB manages Mastercam programs: a card per part with its operations, locks while editing, versions, NC export and machine changes.",
                "PDB gestisce i programmi Mastercam: una scheda per pezzo con le sue operazioni, blocchi durante la modifica, versioni, export NC e cambio macchina.",
                "PDB tvarko Mastercam programas: kortelė kiekvienai detalei su jos operacijomis, užraktai redaguojant, versijos, NC eksportas ir staklių keitimas." },
            ["Installiert wird nur für deinen Windows-Benutzer – Administratorrechte sind nicht nötig, Python ist enthalten. Daten bleiben auf diesem PC."] = new[] {
                "It is installed for your Windows user only – no administrator rights needed, Python is included. Your data stays on this PC.",
                "Viene installato solo per il tuo utente Windows – non servono diritti di amministratore, Python è incluso. I dati restano su questo PC.",
                "Diegiama tik jūsų Windows naudotojui – administratoriaus teisių nereikia, Python įtrauktas. Duomenys lieka šiame kompiuteryje." },
            ["PDB {0} ist bereits installiert. Es wird auf Version {1} aktualisiert; deine Daten und Einstellungen bleiben erhalten."] = new[] {
                "PDB {0} is already installed. It will be updated to version {1}; your data and settings are kept.",
                "PDB {0} è già installato. Verrà aggiornato alla versione {1}; dati e impostazioni restano.",
                "PDB {0} jau įdiegtas. Jis bus atnaujintas į versiją {1}; jūsų duomenys ir nustatymai išliks." },
            ["Die Sprache gilt für die Installation. In PDB wählt jeder Benutzer seine Sprache selbst (Mein Profil → Darstellung → Sprache)."] = new[] {
                "The language applies to the installation. In PDB every user picks their own language (My profile → Appearance → Language).",
                "La lingua vale per l'installazione. In PDB ogni utente sceglie la propria lingua (Il mio profilo → Aspetto → Lingua).",
                "Kalba taikoma diegimui. PDB kiekvienas naudotojas pasirenka savo kalbą (Mano profilis → Išvaizda → Kalba)." },

            // folders
            ["Ordner"] = new[] { "Folders", "Cartelle", "Aplankai" },
            ["Wo PDB und seine Daten liegen."] = new[] { "Where PDB and its data are stored.", "Dove si trovano PDB e i suoi dati.", "Kur saugomi PDB ir jo duomenys." },
            ["Programmordner"] = new[] { "Program folder", "Cartella del programma", "Programos aplankas" },
            ["Datenordner (Datenbank, Sicherungen, Protokoll)"] = new[] { "Data folder (database, backups, log)", "Cartella dati (database, backup, registro)", "Duomenų aplankas (duomenų bazė, atsarginės kopijos, žurnalas)" },
            ["Die Ordner für deine Mastercam- und NC-Dateien legst du nach der Installation im Einrichtungs-Assistenten von PDB fest – sie können überall liegen, auch auf einem Netzlaufwerk."] = new[] {
                "The folders for your Mastercam and NC files are chosen after the installation in PDB's setup wizard – they can be anywhere, also on a network drive.",
                "Le cartelle per i file Mastercam e NC si scelgono dopo l'installazione nella procedura guidata di PDB – possono essere ovunque, anche su un'unità di rete.",
                "Mastercam ir NC failų aplankus pasirinksite po diegimo PDB sąrankos vedlyje – jie gali būti bet kur, ir tinklo diske." },
            ["Bei einer Aktualisierung werden nur die Programmdateien ersetzt; der Datenordner bleibt unberührt."] = new[] {
                "An update only replaces the program files; the data folder is not touched.",
                "Un aggiornamento sostituisce solo i file del programma; la cartella dati non viene toccata.",
                "Atnaujinant keičiami tik programos failai; duomenų aplankas nepaliečiamas." },
            ["Bitte einen Programmordner wählen."] = new[] { "Please choose a program folder.", "Scegli una cartella del programma.", "Pasirinkite programos aplanką." },
            ["Bitte einen Datenordner wählen."] = new[] { "Please choose a data folder.", "Scegli una cartella dati.", "Pasirinkite duomenų aplanką." },
            ["Der Datenordner darf nicht im Programmordner liegen (der wird bei Aktualisierungen ersetzt)."] = new[] {
                "The data folder must not be inside the program folder (that one is replaced on updates).",
                "La cartella dati non può essere dentro la cartella del programma (viene sostituita negli aggiornamenti).",
                "Duomenų aplankas negali būti programos aplanke (jis keičiamas atnaujinant)." },

            // options
            ["Optionen"] = new[] { "Options", "Opzioni", "Parinktys" },
            ["Wie PDB starten und arbeiten soll."] = new[] { "How PDB should start and work.", "Come PDB deve avviarsi e lavorare.", "Kaip PDB turi pasileisti ir veikti." },
            ["Starten"] = new[] { "Start", "Avvio", "Paleidimas" },
            ["Verknüpfungen „PDB“ und „PDB beenden“ auf dem Desktop"] = new[] { "Shortcuts “PDB” and “Stop PDB” on the desktop", "Collegamenti «PDB» e «Arresta PDB» sul desktop", "Nuorodos „PDB“ ir „Sustabdyti PDB“ darbalaukyje" },
            ["Einträge im Startmenü"] = new[] { "Entries in the Start menu", "Voci nel menu Start", "Įrašai meniu Pradėti" },
            ["PDB beim Anmelden an Windows automatisch starten (im Hintergrund)"] = new[] { "Start PDB automatically when you log in to Windows (in the background)", "Avvia PDB automaticamente all'accesso a Windows (in background)", "Automatiškai paleisti PDB prisijungus prie Windows (fone)" },
            ["Mastercam"] = new[] { "Mastercam", "Mastercam", "Mastercam" },
            ["PDB Helper installieren – öffnet Dateien aus PDB in Mastercam, sperrt sie beim Bearbeiten und checkt beim Speichern ein"] = new[] {
                "Install PDB Helper – opens files from PDB in Mastercam, locks them while editing and checks them in on save",
                "Installa PDB Helper – apre i file da PDB in Mastercam, li blocca durante la modifica e li registra al salvataggio",
                "Įdiegti PDB Helper – atidaro failus iš PDB programoje Mastercam, užrakina juos redaguojant ir grąžina išsaugojus" },
            ["Gefunden: Mastercam {0}"] = new[] { "Found: Mastercam {0}", "Trovato: Mastercam {0}", "Rasta: Mastercam {0}" },
            ["Kein Mastercam 2025 oder neuer gefunden – lässt sich später im PDB Helper einrichten."] = new[] {
                "No Mastercam 2025 or newer found – can be set up later in the PDB Helper.",
                "Nessun Mastercam 2025 o successivo trovato – si può configurare più tardi nel PDB Helper.",
                "Mastercam 2025 ar naujesnis nerastas – vėliau galima nustatyti per PDB Helper." },
            ["Mastercam-Bridge einrichten – Dateien öffnen im bereits laufenden Mastercam"] = new[] {
                "Set up the Mastercam bridge – files open in the Mastercam that is already running",
                "Configura il bridge Mastercam – i file si aprono nel Mastercam già avviato",
                "Nustatyti Mastercam bridge – failai atidaromi jau veikiančioje Mastercam" },
            ["Netzwerk"] = new[] { "Network", "Rete", "Tinklas" },
            ["Port (Adresse http://localhost:<Port>):"] = new[] { "Port (address http://localhost:<port>):", "Porta (indirizzo http://localhost:<porta>):", "Prievadas (adresas http://localhost:<prievadas>):" },
            ["Von anderen PCs im Netzwerk erreichbar – Kollegen arbeiten mit derselben Datenbank (Windows fragt nach der Firewall)"] = new[] {
                "Reachable from other PCs in the network – colleagues work with the same database (Windows asks about the firewall)",
                "Raggiungibile da altri PC in rete – i colleghi lavorano con lo stesso database (Windows chiede del firewall)",
                "Pasiekiama iš kitų tinklo kompiuterių – kolegos dirba su ta pačia duomenų baze (Windows paklaus dėl užkardos)" },
            ["Port {0} wird schon von einem anderen Programm benutzt. Bitte einen anderen wählen (z. B. 8080)."] = new[] {
                "Port {0} is already used by another program. Please choose another one (e.g. 8080).",
                "La porta {0} è già usata da un altro programma. Scegline un'altra (es. 8080).",
                "Prievadą {0} jau naudoja kita programa. Pasirinkite kitą (pvz. 8080)." },

            // database
            ["Datenbank"] = new[] { "Database", "Database", "Duomenų bazė" },
            ["Womit PDB startet."] = new[] { "What PDB starts with.", "Con cosa parte PDB.", "Nuo ko pradeda PDB." },
            ["Vorhandene Datenbank gefunden ({0} MB, zuletzt geändert {1}). Sie wird weiter verwendet."] = new[] {
                "Existing database found ({0} MB, last changed {1}). It will be used.",
                "Trovato un database esistente ({0} MB, ultima modifica {1}). Verrà utilizzato.",
                "Rasta esama duomenų bazė ({0} MB, paskutinį kartą keista {1}). Ji bus naudojama." },
            ["Stattdessen mit einer neuen, leeren Datenbank beginnen (die vorhandene wird als Kopie aufbewahrt)"] = new[] {
                "Start with a new, empty database instead (the existing one is kept as a copy)",
                "Inizia invece con un database nuovo e vuoto (quello esistente viene conservato come copia)",
                "Vietoj to pradėti su nauja tuščia duomenų baze (esama išsaugoma kaip kopija)" },
            ["Eigene Datenbank – nach der Installation führt dich PDB durch die Einrichtung (Maschinen, Ordner, Felder). Erste Anmeldung: Administrator, Passwort admin."] = new[] {
                "My own database – after the installation PDB guides you through the setup (machines, folders, fields). First login: Administrator, password admin.",
                "Database personale – dopo l'installazione PDB ti guida nella configurazione (macchine, cartelle, campi). Primo accesso: Amministratore, password admin.",
                "Sava duomenų bazė – po diegimo PDB padės viską nustatyti (staklės, aplankai, laukai). Pirmas prisijungimas: Administratorius, slaptažodis admin." },
            ["Demo mit Beispieldaten – Maschinen, Kunden, Teile und Benutzer zum Ausprobieren. Später neu beginnen geht jederzeit (Setup erneut ausführen)."] = new[] {
                "Demo with sample data – machines, customers, parts and users to try things out. You can start over any time (run the setup again).",
                "Demo con dati di esempio – macchine, clienti, pezzi e utenti per provare. Si può ricominciare in qualsiasi momento (rieseguire il setup).",
                "Demo su pavyzdiniais duomenimis – staklės, klientai, detalės ir naudotojai išbandymui. Pradėti iš naujo galima bet kada (paleiskite diegimą dar kartą)." },
            ["Passwort für den Administrator (mind. 6 Zeichen)"] = new[] { "Password for the Administrator (min. 6 characters)", "Password per l'Amministratore (min. 6 caratteri)", "Administratoriaus slaptažodis (bent 6 simboliai)" },
            ["Passwort wiederholen"] = new[] { "Repeat password", "Ripeti password", "Pakartokite slaptažodį" },
            ["Passwort mit mindestens 6 Zeichen."] = new[] { "Password with at least 6 characters.", "Password di almeno 6 caratteri.", "Slaptažodis turi būti bent 6 simbolių." },
            ["Die Passwörter sind nicht gleich."] = new[] { "The passwords do not match.", "Le password non coincidono.", "Slaptažodžiai nesutampa." },
            ["Bitte ein anderes Passwort als das Standard-Passwort wählen."] = new[] { "Please choose a password other than the default one.", "Scegli una password diversa da quella predefinita.", "Pasirinkite kitą slaptažodį nei numatytasis." },

            // summary
            ["Bereit zur Installation"] = new[] { "Ready to install", "Pronto per l'installazione", "Paruošta diegti" },
            ["Prüfe die Einstellungen und klicke auf „Installieren“."] = new[] { "Check the settings and click “Install”.", "Controlla le impostazioni e fai clic su «Installa».", "Patikrinkite nustatymus ir spauskite „Įdiegti“." },
            ["Programm: {0}"] = new[] { "Program: {0}", "Programma: {0}", "Programa: {0}" },
            ["Daten: {0}"] = new[] { "Data: {0}", "Dati: {0}", "Duomenys: {0}" },
            ["Adresse: {0}"] = new[] { "Address: {0}", "Indirizzo: {0}", "Adresas: {0}" },
            ["auch von anderen PCs"] = new[] { "also from other PCs", "anche da altri PC", "ir iš kitų kompiuterių" },
            ["Datenbank: {0}"] = new[] { "Database: {0}", "Database: {0}", "Duomenų bazė: {0}" },
            ["neu (vorhandene als Kopie aufbewahrt)"] = new[] { "new (existing one kept as a copy)", "nuovo (quello esistente conservato come copia)", "nauja (esama išsaugota kaip kopija)" },
            ["vorhandene weiter verwenden"] = new[] { "keep using the existing one", "continua con quello esistente", "toliau naudoti esamą" },
            ["Demo mit Beispieldaten"] = new[] { "demo with sample data", "demo con dati di esempio", "demo su pavyzdiniais duomenimis" },
            ["neu"] = new[] { "new", "nuovo", "nauja" },
            ["PDB Helper: {0}"] = new[] { "PDB Helper: {0}", "PDB Helper: {0}", "PDB Helper: {0}" },
            ["Mastercam-Bridge: {0}"] = new[] { "Mastercam bridge: {0}", "bridge Mastercam: {0}", "Mastercam bridge: {0}" },
            ["Verknüpfungen: Desktop {0}, Startmenü {1}"] = new[] { "Shortcuts: desktop {0}, Start menu {1}", "Collegamenti: desktop {0}, menu Start {1}", "Nuorodos: darbalaukis {0}, meniu Pradėti {1}" },
            ["Automatisch starten: {0}"] = new[] { "Start automatically: {0}", "Avvio automatico: {0}", "Paleisti automatiškai: {0}" },
            ["Sprache: {0}"] = new[] { "Language: {0}", "Lingua: {0}", "Kalba: {0}" },
            ["PDB {0} wird dabei beendet und aktualisiert."] = new[] { "PDB {0} will be stopped and updated.", "PDB {0} verrà arrestato e aggiornato.", "PDB {0} bus sustabdytas ir atnaujintas." },

            // progress
            ["Installation läuft …"] = new[] { "Installing …", "Installazione in corso …", "Diegiama …" },
            ["Bitte warten, das dauert etwa eine Minute."] = new[] { "Please wait, this takes about a minute.", "Attendere, ci vuole circa un minuto.", "Palaukite, tai užtruks apie minutę." },
            ["Wird vorbereitet …"] = new[] { "Preparing …", "Preparazione …", "Ruošiama …" },
            ["PDB wird beendet …"] = new[] { "Stopping PDB …", "Arresto di PDB …", "Stabdomas PDB …" },
            ["Dateien werden kopiert …"] = new[] { "Copying files …", "Copia dei file …", "Kopijuojami failai …" },
            ["Einstellungen werden gespeichert …"] = new[] { "Saving settings …", "Salvataggio impostazioni …", "Išsaugomi nustatymai …" },
            ["PDB Helper wird eingerichtet …"] = new[] { "Setting up PDB Helper …", "Configurazione di PDB Helper …", "Nustatomas PDB Helper …" },
            ["Verknüpfungen werden angelegt …"] = new[] { "Creating shortcuts …", "Creazione collegamenti …", "Kuriamos nuorodos …" },
            ["Datenbank wird vorbereitet …"] = new[] { "Preparing the database …", "Preparazione del database …", "Ruošiama duomenų bazė …" },
            ["PDB wird gestartet …"] = new[] { "Starting PDB …", "Avvio di PDB …", "Paleidžiamas PDB …" },
            ["Beispieldaten werden angelegt …"] = new[] { "Creating sample data …", "Creazione dati di esempio …", "Kuriami pavyzdiniai duomenys …" },
            ["Fertig."] = new[] { "Done.", "Fatto.", "Baigta." },
            ["Die Installation ist fehlgeschlagen:"] = new[] { "The installation failed:", "L'installazione non è riuscita:", "Diegimas nepavyko:" },
            ["Protokoll: {0}"] = new[] { "Log: {0}", "Registro: {0}", "Žurnalas: {0}" },

            // done
            ["Fertig"] = new[] { "Done", "Fatto", "Baigta" },
            ["PDB ist installiert und läuft."] = new[] { "PDB is installed and running.", "PDB è installato e in esecuzione.", "PDB įdiegtas ir veikia." },
            ["Anmelden: Anna, Tom oder Lina anklicken (ohne Passwort) – oder Administrator mit deinem Passwort. Zum Ausprobieren des Imports liegen Beispieldateien in Dokumente\\PDB\\Alte Programme."] = new[] {
                "Log in: click Anna, Tom or Lina (no password) – or Administrator with your password. Sample files to try the import are in Documents\\PDB\\Alte Programme.",
                "Accesso: fai clic su Anna, Tom o Lina (senza password) – oppure Amministratore con la tua password. File di esempio per provare l'importazione in Documenti\\PDB\\Alte Programme.",
                "Prisijungimas: spauskite Anna, Tom arba Lina (be slaptažodžio) – arba Administratorius su savo slaptažodžiu. Pavyzdiniai failai importui išbandyti: Dokumentai\\PDB\\Alte Programme." },
            ["Erste Anmeldung: Administrator wählen, Passwort admin. Danach legst du ein eigenes Passwort fest, und der Einrichtungs-Assistent fragt nach Maschinen, Ordnern und Feldern."] = new[] {
                "First login: choose Administrator, password admin. Then you set your own password, and the setup wizard asks for machines, folders and fields.",
                "Primo accesso: scegli Amministratore, password admin. Poi imposti la tua password e la procedura guidata chiede macchine, cartelle e campi.",
                "Pirmas prisijungimas: pasirinkite Administratorius, slaptažodis admin. Tada nustatysite savo slaptažodį, o sąrankos vedlys paklaus staklių, aplankų ir laukų." },
            ["Deine vorhandene Datenbank wurde übernommen."] = new[] { "Your existing database was kept.", "Il tuo database esistente è stato mantenuto.", "Jūsų esama duomenų bazė išsaugota." },
            ["Bestehende Mastercam-Dateien übernehmen: als Administrator unter Admin → Import."] = new[] {
                "To take over existing Mastercam files: as Administrator under Admin → Import.",
                "Per importare file Mastercam esistenti: come Amministratore in Admin → Importa.",
                "Esamiems Mastercam failams perkelti: kaip Administratorius, Administravimas → Importas." },
            ["Starten: Verknüpfung „PDB“. Beenden: „PDB beenden“. Entfernen: Windows-Einstellungen → Apps → PDB."] = new[] {
                "Start: shortcut “PDB”. Stop: “Stop PDB”. Remove: Windows Settings → Apps → PDB.",
                "Avvio: collegamento «PDB». Arresto: «Arresta PDB». Rimozione: Impostazioni di Windows → App → PDB.",
                "Paleisti: nuoroda „PDB“. Sustabdyti: „Sustabdyti PDB“. Pašalinti: Windows parametrai → Programos → PDB." },
            ["Die Mastercam-Bridge richtet der PDB Helper ein, sobald Mastercam geschlossen ist; danach Mastercam neu starten."] = new[] {
                "The PDB Helper sets up the Mastercam bridge as soon as Mastercam is closed; then restart Mastercam.",
                "Il PDB Helper configura il bridge Mastercam appena Mastercam è chiuso; poi riavvia Mastercam.",
                "PDB Helper nustatys Mastercam bridge, kai tik Mastercam bus uždarytas; tada paleiskite Mastercam iš naujo." },
            ["Sicherungen der Datenbank: täglich automatisch in {0}"] = new[] { "Database backups: daily, automatically in {0}", "Backup del database: ogni giorno, automaticamente in {0}", "Duomenų bazės atsarginės kopijos: kasdien automatiškai {0}" },
            ["PDB jetzt im Browser öffnen"] = new[] { "Open PDB in the browser now", "Apri PDB nel browser ora", "Atidaryti PDB naršyklėje dabar" },

            // shortcuts and launcher
            ["PDB beenden"] = new[] { "Stop PDB", "Arresta PDB", "Sustabdyti PDB" },
            ["PDB starten und im Browser öffnen"] = new[] { "Start PDB and open it in the browser", "Avvia PDB e aprilo nel browser", "Paleisti PDB ir atidaryti naršyklėje" },
            ["PDB beim Anmelden starten"] = new[] { "Start PDB at login", "Avvia PDB all'accesso", "Paleisti PDB prisijungus" },
            ["PDB konnte nicht gestartet werden.\n\nProtokoll: {0}"] = new[] { "PDB could not be started.\n\nLog: {0}", "Impossibile avviare PDB.\n\nRegistro: {0}", "Nepavyko paleisti PDB.\n\nŽurnalas: {0}" },
            ["PDB ist beendet."] = new[] { "PDB has been stopped.", "PDB è stato arrestato.", "PDB sustabdytas." },
            ["PDB ist nicht installiert (install.json fehlt)."] = new[] { "PDB is not installed (install.json missing).", "PDB non è installato (manca install.json).", "PDB neįdiegtas (trūksta install.json)." },
            ["PDB deinstallieren"] = new[] { "Uninstall PDB", "Disinstalla PDB", "Pašalinti PDB" },
            ["PDB wird von diesem PC entfernt (Programm, Verknüpfungen, Autostart). Fortfahren?"] = new[] {
                "PDB will be removed from this PC (program, shortcuts, autostart). Continue?",
                "PDB verrà rimosso da questo PC (programma, collegamenti, avvio automatico). Continuare?",
                "PDB bus pašalintas iš šio kompiuterio (programa, nuorodos, automatinis paleidimas). Tęsti?" },
            ["Auch deine Daten löschen (Datenbank, Sicherungen, Bilder)? Das kann nicht rückgängig gemacht werden.\n\nNein = die Daten bleiben in {0}."] = new[] {
                "Also delete your data (database, backups, pictures)? This cannot be undone.\n\nNo = the data stays in {0}.",
                "Eliminare anche i tuoi dati (database, backup, immagini)? Non si può annullare.\n\nNo = i dati restano in {0}.",
                "Ištrinti ir jūsų duomenis (duomenų bazę, atsargines kopijas, paveikslėlius)? To negalima atšaukti.\n\nNe = duomenys lieka {0}." },
            ["PDB wurde entfernt."] = new[] { "PDB has been removed.", "PDB è stato rimosso.", "PDB pašalintas." },
            ["Deine Daten liegen weiterhin in {0}."] = new[] { "Your data is still in {0}.", "I tuoi dati sono ancora in {0}.", "Jūsų duomenys vis dar yra {0}." },
        };
    }
}
