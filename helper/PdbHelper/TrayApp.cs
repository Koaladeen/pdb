using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace PdbHelper;

/// <summary>The tray icon and its menu. Owns login state, the session manager and the local API.</summary>
public sealed class TrayApp : ApplicationContext, IHelperHost
{
    readonly HelperConfig config = HelperConfig.Load();
    readonly NotifyIcon icon;
    readonly SessionManager sessions;
    readonly LocalApi localApi;
    readonly StatusMonitor status;
    readonly SynchronizationContext ui;
    readonly Dictionary<HelperStatus.Level, Icon> icons = new();
    UserInfo? user;

    public TrayApp((int Id, bool ReadOnly)? startLink)
    {
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        foreach (var level in Enum.GetValues<HelperStatus.Level>()) icons[level] = MakeIcon(level);
        icon = new NotifyIcon { Icon = icons[HelperStatus.Level.Warning], Text = "PDB Helper – prüfe Verbindung …", Visible = true, ContextMenuStrip = new ContextMenuStrip() };
        icon.ContextMenuStrip.Opening += (_, _) => BuildMenu();
        icon.DoubleClick += (_, _) => OpenWebPage();

        Mastercam.ChooseVersion = ChooseMastercam;
        sessions = new SessionManager(config, Notify);
        status = new StatusMonitor(config, () => user, TimeSpan.FromSeconds(15));
        status.Changed += s => ui.Post(_ => ShowStatus(s), null);
        localApi = new LocalApi(config, sessions, this);
        try
        {
            localApi.Start();
        }
        catch (Exception ex)
        {
            Notify($"Port {config.LocalPort} ist belegt – die Webseite kann den Helper nicht erreichen. ({ex.Message})", NoticeKind.Error);
        }
        WindowsIntegration.RegisterProtocol();
        // Autostart is NOT switched on by itself (security software rightly dislikes programs that
        // add themselves to autostart); "Mit Windows starten" in the menu, or IT, does it.
        maintenance = new System.Threading.Timer(_ => _ = Maintain(userAsked: false), null, TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(10));
        status.Changed += s =>
        {
            // Mastercam was just closed: good moment to finish the bridge setup
            if (s.MastercamRunning < lastMastercamCount && config.BridgeManaged) _ = Maintain(userAsked: false);
            lastMastercamCount = s.MastercamRunning;
        };
        _ = Startup(startLink);
    }

    // ------------------------------------------------------------------ updates and bridge

    readonly System.Threading.Timer maintenance;
    int lastMastercamCount;
    int maintaining;

    /// <summary>New helper version from the server (when no file is open for editing) and, once
    /// agreed, the bridge for every installed Mastercam.</summary>
    async Task Maintain(bool userAsked)
    {
        if (Interlocked.Exchange(ref maintaining, 1) == 1) return;
        try
        {
            var manifest = await Manifest.Fetch(config.ServerUrl);
            if (manifest is null)
            {
                if (userAsked) Notify("Server nicht erreichbar – keine Updates geprüft.", NoticeKind.Warning);
                return;
            }
            if (config.BridgeManaged)
            {
                foreach (var mi in BridgeSetup.Detect())
                {
                    try
                    {
                        var line = await BridgeSetup.Ensure(mi, manifest, config.ServerUrl);
                        Log.Write(line);
                        if (userAsked || line.Contains("wird beim Start geladen")) Notify(line, NoticeKind.Info);
                    }
                    catch (Exception ex)
                    {
                        Log.Write($"Bridge setup Mastercam {mi.Year}: {ex}");
                        Notify($"Mastercam {mi.Year}: Bridge nicht eingerichtet – {ex.Message}", NoticeKind.Warning);
                    }
                }
            }
            await UpdateHelper(manifest, userAsked);
        }
        finally
        {
            Interlocked.Exchange(ref maintaining, 0);
        }
    }

    async Task UpdateHelper(Manifest manifest, bool userAsked)
    {
        if (manifest.Helper is not { } release || release.Parsed <= Installer.Current)
        {
            if (userAsked) Notify($"PDB Helper {Installer.Current.ToString(3)} ist aktuell.", NoticeKind.Info);
            return;
        }
        // Default: only announce. Replacing its own program file is opt-in (config AutoUpdate,
        // e.g. set by IT) - otherwise IT or the user installs the new version.
        if (!config.AutoUpdate || !Installer.IsInstalledCopy)
        {
            if (userAsked || config.AnnouncedVersion != release.Version)
            {
                Notify($"PDB Helper {release.Version} ist verfügbar (läuft: {Installer.Current.ToString(3)}). Die neue Version bekommst du von der IT.", NoticeKind.Info);
                config.AnnouncedVersion = release.Version;
                config.Save();
            }
            return;
        }
        if (sessions.All.Any(s => !s.ReadOnly))
        {
            if (userAsked) Notify($"Version {release.Version} wird installiert, sobald keine Datei mehr geöffnet ist.", NoticeKind.Info);
            return; // next round
        }
        try
        {
            var tmp = await Manifest.Download(config.ServerUrl, release);
            Installer.Replace(tmp);
            File.Delete(tmp);
            Log.Write($"Updated {Installer.Current} -> {release.Version}, restarting");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Installer.ExePath, "--updated") { UseShellExecute = false });
            ui.Post(_ => ExitThread(), null); // Main releases the lock; the new version takes over
        }
        catch (Exception ex)
        {
            Log.Write($"Helper update failed: {ex}");
            if (userAsked) Notify($"Update fehlgeschlagen: {ex.Message}", NoticeKind.Error);
        }
    }

    /// <summary>"Which Mastercam?" window on the UI thread (called from any thread).</summary>
    MastercamInstall? ChooseMastercam(IReadOnlyList<MastercamInstall> installs)
    {
        MastercamInstall? result = null;
        ui.Send(_ =>
        {
            using var form = new VersionChooser(installs, null);
            if (form.ShowDialog() != DialogResult.OK || form.Chosen is null) return;
            result = form.Chosen;
            if (form.Always)
            {
                config.MastercamExe = form.Chosen.Exe;
                config.Save();
            }
        }, null);
        return result;
    }

    string MastercamChoiceText()
    {
        if (string.IsNullOrWhiteSpace(config.MastercamExe)) return "Mastercam-Version: beim Start fragen";
        var year = BridgeSetup.Detect(minYear: 2018).FirstOrDefault(i => string.Equals(i.Exe, config.MastercamExe, StringComparison.OrdinalIgnoreCase))?.Year;
        return $"Mastercam-Version: immer {(year is { } y ? y.ToString() : Path.GetFileName(Path.GetDirectoryName(config.MastercamExe)))} – wieder fragen";
    }

    async Task SetUpBridge()
    {
        var installs = BridgeSetup.Detect();
        if (installs.Count == 0)
        {
            Notify("Kein Mastercam 2025 oder neuer gefunden.", NoticeKind.Warning);
            return;
        }
        var list = string.Join("\n", installs.Select(i => $"• Mastercam {i.Year}"));
        var answer = MessageBox.Show(
            "Die PDB Bridge wird eingerichtet, damit Dateien im offenen Mastercam geöffnet werden:\n\n" + list +
            "\n\n• Bridge lokal auf diesem PC ablegen (kein Netzwerk nötig, keine Admin-Rechte)\n" +
            "• in Mastercam unter Start/Ende → Systemstart eintragen (Sicherung der Einstellungen wird angelegt)\n\n" +
            "Die Einstellung wird geändert, sobald Mastercam geschlossen ist. Danach hält der Helper die Bridge selbst aktuell.",
            "PDB Bridge einrichten", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
        if (answer != DialogResult.OK) return;
        config.BridgeManaged = true;
        config.Save();
        await Maintain(userAsked: true);
    }

    async Task Startup((int Id, bool ReadOnly)? startLink)
    {
        if (config.Token is { } token)
        {
            try
            {
                var api = new ServerApi(config.ServerUrl, token);
                user = await api.Me();
                sessions.Api = api;
            }
            catch (ApiException ex)
            {
                Log.Write($"Saved login not valid: {ex.Message}");
            }
        }
        // No login window on start: the first "Öffnen" on the PDB page hands over that login.
        if (user is null) Notify("Bereit. Dateien auf der PDB-Webseite mit „Öffnen“ öffnen – der Helper übernimmt die Anmeldung von dort.", NoticeKind.Info);
        else Notify($"Angemeldet als {user.DisplayName}", NoticeKind.Info);
        if (startLink is { } link)
        {
            if (user is null) ShowLogin(); // pdb:// link without a web login: ask here
            else await OpenFromLink(link.Id, link.ReadOnly);
        }
    }

    // ------------------------------------------------------------------ IHelperHost (web page)

    UserInfo? IHelperHost.User => user;

    HelperStatus IHelperHost.Status => status.Current;

    /// <summary>Icon dot and tooltip: green = all fine, orange = something missing, red = no server.</summary>
    void ShowStatus(HelperStatus s)
    {
        icon.Icon = icons[s.Overall];
        var text = $"PDB Helper\n{s.ServerText}\n{s.MastercamText}";
        icon.Text = text.Length > 127 ? text[..127] : text; // Windows tooltip limit
    }

    public Task<bool> ApproveServer(string origin)
    {
        var answer = new TaskCompletionSource<bool>();
        ui.Post(_ =>
        {
            using var owner = new Form { TopMost = true, ShowInTaskbar = false, StartPosition = FormStartPosition.CenterScreen, Size = new Size(1, 1) };
            owner.Show();
            owner.Activate();
            var busy = sessions.All.Count > 0
                ? "\n\nAchtung: noch geöffnete Dateien vom bisherigen Server werden zuerst eingecheckt und freigegeben."
                : "";
            var result = MessageBox.Show(owner,
                $"Die Webseite\n\n{origin}\n\nmöchte Dateien über den PDB Helper öffnen.\n\n" +
                $"Nur erlauben, wenn das eure PDB-Seite ist (bisher: {config.ServerOrigin}).{busy}",
                "PDB Helper – neue Webseite erlauben?", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            answer.SetResult(result == DialogResult.Yes);
        }, null);
        return SwitchIfApproved(origin, answer.Task);
    }

    async Task<bool> SwitchIfApproved(string origin, Task<bool> approval)
    {
        if (!await approval)
        {
            Log.Write($"Refused web page {origin}");
            return false;
        }
        await sessions.CloseAll("anderer Server");
        config.ServerUrl = origin;
        config.Token = null;
        config.Save();
        user = null;
        sessions.Api = null;
        Log.Write($"Server switched to {origin}");
        return true;
    }

    public async Task AdoptToken(string token)
    {
        if (user is not null && config.Token == token) return;
        var api = new ServerApi(config.ServerUrl, token);
        UserInfo me;
        try
        {
            me = await api.Me();
        }
        catch (ApiException ex)
        {
            Log.Write($"Web login not usable: {ex.Message}");
            return; // keep the current login, if any
        }
        if (user is not null && user.Id != me.Id && sessions.All.Count > 0)
        {
            // someone else logged in on the page while files of the previous user are open
            await sessions.CloseAll($"Benutzerwechsel zu {me.DisplayName}");
        }
        user = me;
        sessions.Api = api;
        config.Token = token;
        config.Username = me.Username;
        config.Save();
        Notify($"Angemeldet als {me.DisplayName} (von der PDB-Seite übernommen)", NoticeKind.Info);
    }

    public async Task OpenFromLink(int id, bool readOnly)
    {
        try
        {
            var outcome = await sessions.Open(id, readOnly);
            if (outcome.Status == 409)
            {
                var who = outcome.Lock is { } l ? $"{l.User} an {l.PcName}" : "jemand anderem";
                var answer = MessageBox.Show($"Die Datei ist von {who} gesperrt.\n\nSchreibgeschützt öffnen?", "PDB Helper", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer == DialogResult.Yes) await sessions.Open(id, readOnly: true);
            }
            else if (outcome.Status != 200)
            {
                Notify(outcome.Message, NoticeKind.Error);
            }
        }
        catch (Exception ex)
        {
            Notify(ex.Message, NoticeKind.Error);
        }
    }

    // ------------------------------------------------------------------ login

    void ShowLogin()
    {
        ui.Post(_ =>
        {
            using var form = new LoginForm(config);
            if (form.ShowDialog() != DialogResult.OK) return;
            config.Token = form.Token;
            config.Save();
            user = form.User;
            sessions.Api = new ServerApi(config.ServerUrl, form.Token);
            Notify($"Angemeldet als {user!.DisplayName}", NoticeKind.Info);
        }, null);
    }

    async Task Logout()
    {
        if (sessions.All.Count > 0)
        {
            var ok = MessageBox.Show("Geöffnete Dateien werden eingecheckt und freigegeben. Abmelden?", "PDB Helper", MessageBoxButtons.OKCancel);
            if (ok != DialogResult.OK) return;
            await sessions.CloseAll("Abmeldung");
        }
        try { if (sessions.Api is not null) await sessions.Api.Logout(); } catch (ApiException) { }
        sessions.Api = null;
        user = null;
        config.Token = null;
        config.Save();
    }

    // ------------------------------------------------------------------ menu

    void BuildMenu()
    {
        var menu = icon.ContextMenuStrip!;
        menu.Items.Clear();
        menu.Items.Add(new ToolStripLabel(user is null ? "Nicht angemeldet" : $"{user.DisplayName} @ {new Uri(config.ServerUrl).Host}") { Font = new Font(menu.Font, FontStyle.Bold) });
        var st = status.Current;
        Color Dot(bool ok, bool warn = false) => ok ? Color.FromArgb(46, 125, 79) : warn ? Color.FromArgb(167, 98, 0) : Color.FromArgb(179, 38, 30);
        menu.Items.Add(new ToolStripLabel("● " + st.ServerText) { ForeColor = Dot(st.ServerOk) });
        menu.Items.Add(new ToolStripLabel("● " + st.MastercamText)
        {
            ForeColor = Dot(st.MastercamRunning > 0 && st.MastercamWithBridge == st.MastercamRunning, warn: true),
        });
        menu.Items.Add("Verbindung jetzt prüfen", null, async (_, _) => ShowStatus(await status.Check()));
        menu.Items.Add(config.BridgeManaged ? "Mastercam-Bridge prüfen" : "Mastercam-Bridge einrichten …", null,
            async (_, _) => { if (config.BridgeManaged) await Maintain(userAsked: true); else await SetUpBridge(); });
        menu.Items.Add($"Nach Updates suchen (Version {Installer.Current.ToString(3)})", null, async (_, _) => await Maintain(userAsked: true));
        var mcChoice = new ToolStripMenuItem(MastercamChoiceText()) { Enabled = !string.IsNullOrWhiteSpace(config.MastercamExe) };
        mcChoice.Click += (_, _) => { config.MastercamExe = null; config.Save(); Notify("Beim nächsten Öffnen wird gefragt, welches Mastercam gestartet werden soll.", NoticeKind.Info); };
        menu.Items.Add(mcChoice);
        menu.Items.Add("PDB im Browser öffnen", null, (_, _) => OpenWebPage());
        menu.Items.Add(new ToolStripSeparator());

        var open = sessions.All;
        if (open.Count == 0) menu.Items.Add(new ToolStripLabel("Keine Datei geöffnet") { ForeColor = SystemColors.GrayText });
        foreach (var s in open)
        {
            var item = new ToolStripMenuItem(s.Title + (s.LockLost ? " ⚠ Sperre verloren" : $" · V{s.Revision}"));
            item.DropDownItems.Add("Ordner öffnen", null, (_, _) => WindowsIntegration.OpenFolder(s.McamPath));
            if (!s.ReadOnly)
            {
                item.DropDownItems.Add("Jetzt einchecken", null, async (_, _) => await sessions.CheckinNow(s.OperationId));
                item.DropDownItems.Add("Einchecken und freigeben", null, async (_, _) => await sessions.Close(s.OperationId, "von Hand"));
            }
            else
            {
                item.DropDownItems.Add("Aus Liste entfernen", null, async (_, _) => await sessions.Close(s.OperationId, "von Hand"));
            }
            menu.Items.Add(item);
        }

        menu.Items.Add(new ToolStripSeparator());
        if (user is null) menu.Items.Add("Anmelden …", null, (_, _) => ShowLogin());
        else menu.Items.Add("Abmelden", null, async (_, _) => await Logout());
        var autostart = new ToolStripMenuItem("Mit Windows starten") { Checked = WindowsIntegration.Autostart };
        autostart.Click += (_, _) => WindowsIntegration.Autostart = !autostart.Checked;
        menu.Items.Add(autostart);
        menu.Items.Add("Protokoll anzeigen", null, (_, _) => WindowsIntegration.OpenFolder(Log.FolderPath));
        menu.Items.Add("Beenden", null, async (_, _) => await Quit());
    }

    void OpenWebPage() => Process.Start(new ProcessStartInfo(config.ServerUrl) { UseShellExecute = true });

    async Task Quit()
    {
        if (sessions.All.Any(s => !s.ReadOnly))
        {
            var ok = MessageBox.Show("Geöffnete Dateien werden eingecheckt und freigegeben. Beenden?", "PDB Helper", MessageBoxButtons.OKCancel);
            if (ok != DialogResult.OK) return;
        }
        await sessions.CloseAll("Helper beendet");
        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        icon.Visible = false;
        icon.Dispose();
        localApi.Dispose();
        sessions.Dispose();
        status.Dispose();
        maintenance.Dispose();
        base.ExitThreadCore();
    }

    // ------------------------------------------------------------------ notifications

    void Notify(string text, NoticeKind kind)
    {
        Log.Write($"[{kind}] {text}");
        ui.Post(_ =>
        {
            var tipIcon = kind switch { NoticeKind.Error => ToolTipIcon.Error, NoticeKind.Warning => ToolTipIcon.Warning, _ => ToolTipIcon.Info };
            icon.ShowBalloonTip(kind == NoticeKind.Error ? 10000 : 4000, "PDB Helper", text, tipIcon);
        }, null);
    }

    /// <summary>Blue "P" with a status dot bottom right (green / orange / red).</summary>
    static Icon MakeIcon(HelperStatus.Level level)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var bg = new SolidBrush(Color.FromArgb(43, 87, 154));
            g.FillRectangle(bg, 2, 2, 28, 28);
            using var font = new Font("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel);
            TextRenderer.DrawText(g, "P", font, new Rectangle(0, 0, 26, 28), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            var dot = level switch
            {
                HelperStatus.Level.Good => Color.FromArgb(60, 200, 110),
                HelperStatus.Level.Warning => Color.FromArgb(255, 170, 30),
                _ => Color.FromArgb(230, 50, 40),
            };
            using var ring = new SolidBrush(Color.White);
            g.FillEllipse(ring, 17, 17, 15, 15);
            using var fill = new SolidBrush(dot);
            g.FillEllipse(fill, 19, 19, 11, 11);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
