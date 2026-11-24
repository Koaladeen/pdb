using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PdbHelper;

/// <summary>Windows integration: pdb:// links and start with Windows. Both only touch the
/// current user's registry (HKCU), no admin rights needed.</summary>
public static partial class WindowsIntegration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string AppName = "PdbHelper";

    static string ExePath => Environment.ProcessPath!;

    /// <summary>Registers pdb://open/123 links to start this exe. Fallback for when the browser
    /// blocks the call to 127.0.0.1.</summary>
    public static void RegisterProtocol()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\pdb");
            key.SetValue("", "URL:PDB Helper");
            key.SetValue("URL Protocol", "");
            using var cmd = key.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", $"\"{ExePath}\" \"%1\"");
        }
        catch (Exception ex)
        {
            Log.Write($"Could not register pdb:// links: {ex.Message}");
        }
    }

    public static bool Autostart
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(AppName) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(AppName, $"\"{ExePath}\"");
            else key.DeleteValue(AppName, false);
        }
    }

    [GeneratedRegex(@"^pdb://open/(\d+)(/readonly)?/?$", RegexOptions.IgnoreCase)]
    private static partial Regex LinkRegex();

    /// <summary>"pdb://open/12" or "pdb://open/12/readonly" -> (12, false/true).</summary>
    public static (int Id, bool ReadOnly)? ParseLink(string arg)
    {
        var m = LinkRegex().Match(arg.Trim());
        return m.Success ? (int.Parse(m.Groups[1].Value), m.Groups[2].Success) : null;
    }

    public static void OpenFolder(string path)
    {
        var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (dir is not null && Directory.Exists(dir)) Process.Start("explorer.exe", $"\"{dir}\"");
    }
}

/// <summary>"Mastercam starten – welche Version?" when a file is opened and no Mastercam runs.</summary>
public sealed class VersionChooser : Form
{
    public MastercamInstall? Chosen { get; private set; }
    public bool Always { get; private set; }

    public VersionChooser(IReadOnlyList<MastercamInstall> installs, string? fileName)
    {
        Text = "PDB Helper – Mastercam starten";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);
        TopMost = true;

        var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        panel.Controls.Add(new Label
        {
            Text = "Mastercam ist nicht gestartet. Welche Version soll geöffnet werden?" + (fileName is null ? "" : $"\n\n{fileName}"),
            AutoSize = true,
            MaximumSize = new Size(380, 0),
            Margin = new Padding(0, 0, 0, 10),
        });
        var always = new CheckBox { Text = "Immer diese Version verwenden (im Tray-Menü änderbar)", AutoSize = true, Margin = new Padding(0, 8, 0, 8) };
        foreach (var mi in installs)
        {
            var label = $"Mastercam {mi.Year}" + (mi.Flavor.Length == 0 ? "   (ohne PDB-Bridge)" : "");
            var b = new Button { Text = label, Width = 380, Height = 36, Margin = new Padding(0, 0, 0, 6), TextAlign = ContentAlignment.MiddleLeft };
            b.Click += (_, _) => { Chosen = mi; Always = always.Checked; DialogResult = DialogResult.OK; Close(); };
            panel.Controls.Add(b);
        }
        panel.Controls.Add(always);
        var cancel = new Button { Text = "Abbrechen", AutoSize = true, DialogResult = DialogResult.Cancel };
        panel.Controls.Add(cancel);
        CancelButton = cancel;
        Controls.Add(panel);
    }
}

/// <summary>Login and settings window, built in code (no designer files to keep in sync).</summary>
public sealed class LoginForm : Form
{
    readonly TextBox server = new() { Width = 320 };
    readonly ComboBox user = new() { Width = 320, DropDownStyle = ComboBoxStyle.DropDown };
    readonly TextBox password = new() { Width = 320, UseSystemPasswordChar = true };
    readonly TextBox mastercam = new() { Width = 320, PlaceholderText = "leer = Windows-Verknüpfung für .mcam" };
    readonly Label error = new() { ForeColor = Color.Firebrick, AutoSize = true, MaximumSize = new Size(320, 0) };
    readonly Button ok = new() { Text = "Anmelden", AutoSize = true };

    public string? Token { get; private set; }
    public UserInfo? User { get; private set; }

    public LoginForm(HelperConfig config)
    {
        Text = "PDB Helper – Anmelden";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);
        TopMost = true;

        server.Text = config.ServerUrl;
        mastercam.Text = config.MastercamExe ?? "";

        // Names come from the server, like on the web page; password only if the user has one.
        async Task LoadUsers()
        {
            try
            {
                var list = await ServerApi.LoginUsers(server.Text.Trim().TrimEnd('/'));
                user.BeginUpdate();
                user.Items.Clear();
                foreach (var u in list) user.Items.Add(u);
                user.EndUpdate();
                user.SelectedItem = list.FirstOrDefault(u => u.Username == config.Username) ?? (list.Count == 1 ? list[0] : null);
                error.Text = "";
            }
            catch (Exception ex)
            {
                error.Text = $"Benutzerliste nicht geladen: {ex.Message}";
            }
            UpdatePassword();
        }
        void UpdatePassword()
        {
            var needsPassword = user.SelectedItem is not LoginUser lu || lu.HasPassword;
            password.Enabled = needsPassword;
            password.PlaceholderText = needsPassword ? "" : "kein Passwort nötig";
            if (!needsPassword) password.Text = "";
        }
        user.SelectedIndexChanged += (_, _) => UpdatePassword();
        server.Leave += async (_, _) => await LoadUsers();
        Shown += async (_, _) =>
        {
            await LoadUsers();
            (password.Enabled ? (Control)password : ok).Focus();
        };

        var browse = new Button { Text = "…", Width = 32 };
        browse.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog { Filter = "Mastercam|Mastercam.exe", InitialDirectory = @"C:\Program Files" };
            if (dlg.ShowDialog(this) == DialogResult.OK) mastercam.Text = dlg.FileName;
        };

        var layout = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        void Row(string label, Control c)
        {
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) });
            layout.Controls.Add(c);
        }
        Row("Server", server);
        Row("Benutzer", user);
        Row("Passwort", password);
        var mcRow = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty };
        mastercam.Width = 284;
        mcRow.Controls.Add(mastercam);
        mcRow.Controls.Add(browse);
        Row("Mastercam", mcRow);
        layout.Controls.Add(new Label());
        layout.Controls.Add(error);
        layout.Controls.Add(new Label());
        layout.Controls.Add(ok);
        Controls.Add(layout);
        AcceptButton = ok;

        ok.Click += async (_, _) =>
        {
            ok.Enabled = false;
            error.Text = "";
            try
            {
                var url = server.Text.Trim().TrimEnd('/');
                var username = user.SelectedItem is LoginUser lu ? lu.Username : user.Text.Trim();
                var (token, info) = await ServerApi.Login(url, username, password.Text);
                config.ServerUrl = url;
                config.Username = username;
                config.MastercamExe = string.IsNullOrWhiteSpace(mastercam.Text) ? null : mastercam.Text.Trim();
                Token = token;
                User = info;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                error.Text = ex.Message;
                ok.Enabled = true;
            }
        };
    }
}
