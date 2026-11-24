using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Pdb.Install
{
    /// <summary>Setup wizard: language → folders → options → database → summary → progress → done.</summary>
    sealed class Wizard : Form
    {
        const int Welcome = 0, Folders = 1, Options = 2, Database = 3, Summary = 4, Progress = 5, Done = 6;

        readonly Label title = new() { AutoSize = true, ForeColor = Color.White, Font = new Font("Segoe UI", 14f, FontStyle.Bold), Location = new Point(20, 10) };
        readonly Label subtitle = new() { AutoSize = true, ForeColor = Color.FromArgb(225, 232, 245), Location = new Point(22, 44) };
        readonly Panel body = new() { Dock = DockStyle.Fill, AutoScroll = true };
        readonly Button back = new() { Width = 96, Height = 28 }, next = new() { Width = 110, Height = 28 }, cancel = new() { Width = 96, Height = 28 };

        readonly InstallConfig cfg;
        readonly InstallConfig? previous;
        readonly List<int> mastercams = Mastercam.Detect();
        int page, y;
        bool installing, finished, failed;
        // database page
        bool demo, reset;
        string pw1 = "", pw2 = "";
        // done page
        bool openNow = true;
        InstallResult? result;
        ProgressBar? bar;
        Label? status;
        TextBox? log;

        public Wizard(string[] args)
        {
            previous = InstallConfig.FindInstalled();
            cfg = Setup.FromArgs(previous ?? new InstallConfig(), args);
            L.Lang = Array.IndexOf(args, "--lang") >= 0 ? cfg.Language : previous?.Language ?? L.Detect();
            cfg.Language = L.Lang;

            Text = "PDB Setup " + Setup.Version;
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(680, 500);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;

            var head = new Panel { Dock = DockStyle.Top, Height = 72, BackColor = Color.FromArgb(43, 87, 154) };
            head.Controls.Add(title);
            head.Controls.Add(subtitle);
            var foot = new Panel { Dock = DockStyle.Bottom, Height = 52 };
            foot.Controls.Add(new Label { Dock = DockStyle.Top, Height = 1, BackColor = Color.FromArgb(215, 220, 229) });
            foreach (var b in new[] { back, next, cancel }) foot.Controls.Add(b);
            foot.Resize += (_, _) => PlaceButtons(foot);
            Controls.Add(body);
            Controls.Add(foot);
            Controls.Add(head);
            body.BringToFront();

            back.Click += (_, _) => { page--; ShowPage(); };
            next.Click += (_, _) => Next();
            cancel.Click += (_, _) => Close();
            FormClosing += (_, e) =>
            {
                if (installing) { e.Cancel = true; return; }
                if (!finished && !failed && MessageBox.Show(L.T("Installation abbrechen?"), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    e.Cancel = true;
            };
            Load += (_, _) => { PlaceButtons(foot); ShowPage(); };
        }

        void PlaceButtons(Panel foot)
        {
            var top = (foot.Height - cancel.Height) / 2 + 1;
            cancel.Location = new Point(foot.Width - cancel.Width - 16, top);
            next.Location = new Point(cancel.Left - next.Width - 8, top);
            back.Location = new Point(next.Left - back.Width - 6, top);
        }

        // ------------------------------------------------------------------ layout helpers

        int Inner => body.ClientSize.Width - 56;

        void Begin(string heading, string sub)
        {
            body.Controls.Clear();
            title.Text = L.T(heading);
            subtitle.Text = L.T(sub);
            y = 18;
        }

        T Add<T>(T c, int indent = 0, int gap = 8) where T : Control
        {
            c.Location = new Point(28 + indent, y);
            var width = Inner - indent;
            if (c is Label || c is CheckBox || c is RadioButton)
            {
                // measure the wrapped text ourselves: AutoSize heights are not known before layout
                var glyph = c is ButtonBase ? 22 : 0;
                var text = TextRenderer.MeasureText(c.Text, c.Font, new Size(width - glyph, int.MaxValue), TextFormatFlags.WordBreak);
                c.AutoSize = false;
                c.MaximumSize = Size.Empty;
                c.Size = new Size(width, text.Height + (glyph > 0 ? 6 : 2));
                if (c is CheckBox cb) { cb.CheckAlign = ContentAlignment.TopLeft; cb.TextAlign = ContentAlignment.TopLeft; }
                if (c is RadioButton rb) { rb.CheckAlign = ContentAlignment.TopLeft; rb.TextAlign = ContentAlignment.TopLeft; }
            }
            else c.Width = width;
            body.Controls.Add(c);
            y += c.Height + gap;
            return c;
        }

        Label Text_(string text, int indent = 0, bool bold = false, int gap = 10) =>
            Add(new Label { AutoSize = true, Text = text, Font = bold ? new Font(Font, FontStyle.Bold) : Font }, indent, gap);

        CheckBox Check(string text, bool value, Action<bool> set, int indent = 0)
        {
            var cb = new CheckBox { AutoSize = true, Checked = value };
            cb.CheckedChanged += (_, _) => set(cb.Checked);
            return Choice(cb, text, indent);
        }

        RadioButton Radio(string text, bool value, int indent = 0) => Choice(new RadioButton { AutoSize = true, Checked = value }, text, indent);

        // Box/button without text + a wrapping label beside it (WinForms does not wrap check box texts).
        // Clicking the label works like clicking the box.
        T Choice<T>(T box, string text, int indent) where T : ButtonBase
        {
            const int glyph = 20;
            var width = Inner - indent - glyph;
            var label = new Label
            {
                Text = text,
                AutoSize = false,
                Location = new Point(28 + indent + glyph, y + 1),
                Size = new Size(width, TextRenderer.MeasureText(text, Font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak).Height + 2),
            };
            box.Location = new Point(28 + indent, y);
            label.Click += (_, _) =>
            {
                if (!box.Enabled) return;
                if (box is CheckBox c) c.Checked = !c.Checked;
                else if (box is RadioButton r) r.Checked = true;
            };
            box.EnabledChanged += (_, _) => label.ForeColor = box.Enabled ? SystemColors.ControlText : SystemColors.GrayText;
            body.Controls.Add(box);
            body.Controls.Add(label);
            y += Math.Max(label.Height, 18) + 8;
            return box;
        }

        TextBox FolderRow(string label, string value, Action<string> set)
        {
            Text_(L.T(label), gap: 4);
            var tb = new TextBox { Text = value, Width = Inner - 120 };
            tb.TextChanged += (_, _) => set(tb.Text.Trim());
            tb.Location = new Point(28, y);
            var browse = new Button { Text = L.T("Durchsuchen …"), Width = 110, Height = tb.Height + 2, Location = new Point(28 + Inner - 110, y - 1) };
            browse.Click += (_, _) =>
            {
                using var dlg = new FolderBrowserDialog { SelectedPath = Directory.Exists(tb.Text) ? tb.Text : "", ShowNewFolderButton = true };
                if (dlg.ShowDialog(this) == DialogResult.OK) tb.Text = dlg.SelectedPath;
            };
            body.Controls.Add(tb);
            body.Controls.Add(browse);
            y += tb.Height + 14;
            return tb;
        }

        // ------------------------------------------------------------------ pages

        void ShowPage()
        {
            switch (page)
            {
                case Welcome: PageWelcome(); break;
                case Folders: PageFolders(); break;
                case Options: PageOptions(); break;
                case Database: PageDatabase(); break;
                case Summary: PageSummary(); break;
                case Progress: PageProgress(); break;
                case Done: PageDone(); break;
            }
            back.Text = L.T("‹ Zurück");
            cancel.Text = L.T("Abbrechen");
            back.Visible = page > Welcome && page <= Summary;
            cancel.Visible = page <= Summary;
            next.Enabled = page != Progress || failed;
            next.Text = page == Summary ? L.T("Installieren") : page == Done ? L.T("Fertig stellen") : failed ? L.T("Schließen") : L.T("Weiter ›");
            AcceptButton = next;
        }

        void PageWelcome()
        {
            Begin("Willkommen", "Dieser Assistent installiert PDB auf diesem PC.");
            Text_(L.T("PDB verwaltet Mastercam-Programme: eine Karte pro Teil mit ihren Operationen, Sperren beim Bearbeiten, Versionen, NC-Export und Maschinenwechsel."));
            Text_(L.T("Installiert wird nur für deinen Windows-Benutzer – Administratorrechte sind nicht nötig, Python ist enthalten. Daten bleiben auf diesem PC."));
            if (previous != null)
                Text_(L.F("PDB {0} ist bereits installiert. Es wird auf Version {1} aktualisiert; deine Daten und Einstellungen bleiben erhalten.", previous.Version, Setup.Version), bold: true);
            y += 8;
            Text_("Sprache / Language / Lingua / Kalba:", gap: 4);
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
            combo.Items.AddRange(L.Names);
            combo.SelectedIndex = Math.Max(0, Array.IndexOf(L.Codes, L.Lang));
            combo.SelectedIndexChanged += (_, _) => { L.Lang = cfg.Language = L.Codes[combo.SelectedIndex]; ShowPage(); };
            combo.Location = new Point(28, y);
            body.Controls.Add(combo);
            y += combo.Height + 10;
            Text_(L.T("Die Sprache gilt für die Installation. In PDB wählt jeder Benutzer seine Sprache selbst (Mein Profil → Darstellung → Sprache)."), gap: 4).ForeColor = SystemColors.GrayText;
        }

        void PageFolders()
        {
            Begin("Ordner", "Wo PDB und seine Daten liegen.");
            FolderRow("Programmordner", cfg.InstallDir, v => cfg.InstallDir = v);
            FolderRow("Datenordner (Datenbank, Sicherungen, Protokoll)", cfg.DataDir, v => cfg.DataDir = v);
            Text_(L.T("Die Ordner für deine Mastercam- und NC-Dateien legst du nach der Installation im Einrichtungs-Assistenten von PDB fest – sie können überall liegen, auch auf einem Netzlaufwerk.")).ForeColor = SystemColors.GrayText;
            Text_(L.T("Bei einer Aktualisierung werden nur die Programmdateien ersetzt; der Datenordner bleibt unberührt.")).ForeColor = SystemColors.GrayText;
        }

        void PageOptions()
        {
            Begin("Optionen", "Wie PDB starten und arbeiten soll.");
            Text_(L.T("Starten"), bold: true, gap: 4);
            Check(L.T("Verknüpfungen „PDB“ und „PDB beenden“ auf dem Desktop"), cfg.DesktopShortcut, v => cfg.DesktopShortcut = v);
            Check(L.T("Einträge im Startmenü"), cfg.StartMenu, v => cfg.StartMenu = v);
            Check(L.T("PDB beim Anmelden an Windows automatisch starten (im Hintergrund)"), cfg.Autostart, v => cfg.Autostart = v);
            y += 6;
            Text_(L.T("Mastercam"), bold: true, gap: 4);
            CheckBox? bridge = null;
            Check(L.T("PDB Helper installieren – öffnet Dateien aus PDB in Mastercam, sperrt sie beim Bearbeiten und checkt beim Speichern ein"), cfg.Helper,
                v => { cfg.Helper = v; if (bridge != null) bridge.Enabled = v && mastercams.Count > 0; });
            var found = mastercams.Count > 0
                ? L.F("Gefunden: Mastercam {0}", string.Join(", ", mastercams))
                : L.T("Kein Mastercam 2025 oder neuer gefunden – lässt sich später im PDB Helper einrichten.");
            bridge = Check(L.T("Mastercam-Bridge einrichten – Dateien öffnen im bereits laufenden Mastercam") + "\n" + found, cfg.Bridge && mastercams.Count > 0, v => cfg.Bridge = v, 20);
            bridge.Enabled = cfg.Helper && mastercams.Count > 0;
            y += 6;
            Text_(L.T("Netzwerk"), bold: true, gap: 4);
            var portLabel = Text_(L.T("Port (Adresse http://localhost:<Port>):"), gap: 2);
            var port = new NumericUpDown { Minimum = 1024, Maximum = 65535, Value = cfg.Port, Width = 90 };
            port.ValueChanged += (_, _) => cfg.Port = (int)port.Value;
            port.Location = new Point(28, y);
            body.Controls.Add(port);
            y += port.Height + 8;
            Check(L.T("Von anderen PCs im Netzwerk erreichbar – Kollegen arbeiten mit derselben Datenbank (Windows fragt nach der Firewall)"), cfg.Network, v => cfg.Network = v);
        }

        void PageDatabase()
        {
            Begin("Datenbank", "Womit PDB startet.");
            if (File.Exists(cfg.DbPath))
            {
                var fi = new FileInfo(cfg.DbPath);
                Text_(L.F("Vorhandene Datenbank gefunden ({0} MB, zuletzt geändert {1}). Sie wird weiter verwendet.", Math.Max(0.1, Math.Round(fi.Length / 1048576.0, 1)), fi.LastWriteTime.ToString("g")), bold: true);
                Check(L.T("Stattdessen mit einer neuen, leeren Datenbank beginnen (die vorhandene wird als Kopie aufbewahrt)"), reset, v => reset = v);
                demo = false;
                return;
            }
            var own = Radio(L.T("Eigene Datenbank – nach der Installation führt dich PDB durch die Einrichtung (Maschinen, Ordner, Felder). Erste Anmeldung: Administrator, Passwort admin."), !demo);
            var demoRadio = Radio(L.T("Demo mit Beispieldaten – Maschinen, Kunden, Teile und Benutzer zum Ausprobieren. Später neu beginnen geht jederzeit (Setup erneut ausführen)."), demo);
            var box = new Panel { Height = 64 };
            Add(box, 20);
            var col = (Inner - 20) / 2;
            var l1 = new Label { Text = L.T("Passwort für den Administrator (mind. 6 Zeichen)"), AutoSize = false, Size = new Size(col - 12, 18), Location = new Point(0, 2) };
            var t1 = new TextBox { UseSystemPasswordChar = true, Width = col - 12, Text = pw1, Location = new Point(0, 22) };
            var l2 = new Label { Text = L.T("Passwort wiederholen"), AutoSize = false, Size = new Size(col - 12, 18), Location = new Point(col, 2) };
            var t2 = new TextBox { UseSystemPasswordChar = true, Width = col - 12, Text = pw2, Location = new Point(col, 22) };
            t1.TextChanged += (_, _) => pw1 = t1.Text;
            t2.TextChanged += (_, _) => pw2 = t2.Text;
            box.Controls.AddRange(new Control[] { l1, t1, l2, t2 });
            box.Enabled = demo;
            own.CheckedChanged += (_, _) => { demo = demoRadio.Checked; box.Enabled = demo; };
            demoRadio.CheckedChanged += (_, _) => { demo = demoRadio.Checked; box.Enabled = demo; };
        }

        void PageSummary()
        {
            Begin("Bereit zur Installation", "Prüfe die Einstellungen und klicke auf „Installieren“.");
            string yes(bool b) => b ? L.T("ja") : L.T("nein");
            var lines = new List<string>
            {
                L.F("Programm: {0}", cfg.InstallDir),
                L.F("Daten: {0}", cfg.DataDir),
                L.F("Adresse: {0}", cfg.Url) + (cfg.Network ? "  (" + L.T("auch von anderen PCs") + ")" : ""),
                L.F("Datenbank: {0}", File.Exists(cfg.DbPath) ? (reset ? L.T("neu (vorhandene als Kopie aufbewahrt)") : L.T("vorhandene weiter verwenden")) : demo ? L.T("Demo mit Beispieldaten") : L.T("neu")),
                L.F("PDB Helper: {0}", yes(cfg.Helper)) + (cfg.Helper ? ", " + L.F("Mastercam-Bridge: {0}", yes(cfg.Bridge && mastercams.Count > 0)) : ""),
                L.F("Verknüpfungen: Desktop {0}, Startmenü {1}", yes(cfg.DesktopShortcut), yes(cfg.StartMenu)),
                L.F("Automatisch starten: {0}", yes(cfg.Autostart)),
                L.F("Sprache: {0}", L.Names[Math.Max(0, Array.IndexOf(L.Codes, L.Lang))]),
            };
            Add(new TextBox { Multiline = true, ReadOnly = true, Height = 190, Text = string.Join("\r\n", lines), BackColor = SystemColors.Window, ScrollBars = ScrollBars.Vertical });
            if (previous != null) Text_(L.F("PDB {0} wird dabei beendet und aktualisiert.", previous.Version)).ForeColor = SystemColors.GrayText;
        }

        void PageProgress()
        {
            Begin("Installation läuft …", "Bitte warten, das dauert etwa eine Minute.");
            bar = Add(new ProgressBar { Height = 22, Maximum = 100 });
            status = Text_(L.T("Wird vorbereitet …"));
            log = Add(new TextBox { Multiline = true, ReadOnly = true, Height = 220, ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Window });
        }

        void PageDone()
        {
            Begin("Fertig", "PDB ist installiert und läuft.");
            if (result?.Demo == true)
                Text_(L.T("Anmelden: Anna, Tom oder Lina anklicken (ohne Passwort) – oder Administrator mit deinem Passwort. Zum Ausprobieren des Imports liegen Beispieldateien in Dokumente\\PDB\\Alte Programme."));
            else if (result?.NewDatabase == true)
                Text_(L.T("Erste Anmeldung: Administrator wählen, Passwort admin. Danach legst du ein eigenes Passwort fest, und der Einrichtungs-Assistent fragt nach Maschinen, Ordnern und Feldern."), bold: true);
            else
                Text_(L.T("Deine vorhandene Datenbank wurde übernommen."), bold: true);
            Text_(L.T("Bestehende Mastercam-Dateien übernehmen: als Administrator unter Admin → Import."));
            Text_(L.T("Starten: Verknüpfung „PDB“. Beenden: „PDB beenden“. Entfernen: Windows-Einstellungen → Apps → PDB."));
            if (cfg.Helper && cfg.Bridge && mastercams.Count > 0)
                Text_(L.T("Die Mastercam-Bridge richtet der PDB Helper ein, sobald Mastercam geschlossen ist; danach Mastercam neu starten."));
            Text_(L.F("Sicherungen der Datenbank: täglich automatisch in {0}", Path.Combine(cfg.DataDir, "files", "backups"))).ForeColor = SystemColors.GrayText;
            y += 6;
            Check(L.T("PDB jetzt im Browser öffnen"), openNow, v => openNow = v);
        }

        // ------------------------------------------------------------------ navigation

        void Next()
        {
            if (failed) { Close(); return; }
            var problem = Check();
            if (problem != null) { MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (page == Done)
            {
                if (openNow) try { Process.Start(cfg.Url); } catch { }
                Close();
                return;
            }
            page++;
            ShowPage();
            if (page == Progress) StartInstall();
        }

        string? Check()
        {
            if (page == Folders)
            {
                if (cfg.InstallDir.Length < 4 || !Path.IsPathRooted(cfg.InstallDir)) return L.T("Bitte einen Programmordner wählen.");
                if (cfg.DataDir.Length < 4 || !Path.IsPathRooted(cfg.DataDir)) return L.T("Bitte einen Datenordner wählen.");
                var inst = Path.GetFullPath(cfg.InstallDir).TrimEnd('\\') + "\\";
                if ((Path.GetFullPath(cfg.DataDir).TrimEnd('\\') + "\\").StartsWith(inst, StringComparison.OrdinalIgnoreCase))
                    return L.T("Der Datenordner darf nicht im Programmordner liegen (der wird bei Aktualisierungen ersetzt).");
            }
            if (page == Options && Server.PortInUse(cfg.Port) && !Server.Healthy(cfg.Port))
                return L.F("Port {0} wird schon von einem anderen Programm benutzt. Bitte einen anderen wählen (z. B. 8080).", cfg.Port);
            if (page == Database && demo && !File.Exists(cfg.DbPath))
            {
                if (pw1.Length < 6) return L.T("Passwort mit mindestens 6 Zeichen.");
                if (pw1 != pw2) return L.T("Die Passwörter sind nicht gleich.");
                if (pw1 == "admin") return L.T("Bitte ein anderes Passwort als das Standard-Passwort wählen.");
            }
            return null;
        }

        void StartInstall()
        {
            installing = true;
            var options = new InstallOptions { ResetDatabase = reset, DemoPassword = demo && !File.Exists(cfg.DbPath) ? pw1 : null };
            new Thread(() =>
            {
                try
                {
                    var r = Setup.Install(cfg, options, (p, s) => BeginInvoke((Action)(() =>
                    {
                        bar!.Value = Math.Min(100, Math.Max(0, p));
                        status!.Text = s;
                        if (log!.Lines.Length == 0 || log.Lines[log.Lines.Length - 1] != s) log.AppendText(s + "\r\n");
                    })));
                    BeginInvoke((Action)(() => { installing = false; finished = true; result = r; pw1 = pw2 = ""; page = Done; ShowPage(); }));
                }
                catch (Exception ex)
                {
                    BeginInvoke((Action)(() =>
                    {
                        installing = false;
                        failed = true;
                        status!.Text = L.T("Die Installation ist fehlgeschlagen:");
                        status.ForeColor = Color.Firebrick;
                        log!.AppendText("\r\n" + ex.Message + "\r\n\r\n" + L.F("Protokoll: {0}", Setup.LogFile) + "\r\n");
                        ShowPage();
                    }));
                }
            }) { IsBackground = true }.Start();
        }
    }
}
