using System.Net.Http.Json;

namespace PdbHelper;

static class Program
{
    /// <summary>One helper per Windows user. Runs from wherever it is started (no self-install;
    /// "--install" copies it to %LOCALAPPDATA%\PDB on purpose - normally IT installs it).
    /// A second start (e.g. from a pdb:// link) hands its link to the running helper and exits.
    /// "--updated" = started by the (opt-in) updater: wait until the old version has ended.</summary>
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--install") && Installer.InstallAndRelaunch(args.Where(a => a != "--install").ToArray())) return;

        var link = args.Select(WindowsIntegration.ParseLink).FirstOrDefault(l => l is not null);
        using var mutex = new Mutex(true, @"Local\PdbHelper", out var first);
        if (!first && args.Contains("--updated"))
        {
            try { first = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { first = true; } // old version ended without releasing
        }
        if (!first)
        {
            if (link is { } l) ForwardToRunning(l.Id, l.ReadOnly);
            return; // also "pdb://start" when already running: nothing to do
        }

        Installer.CleanupOld();
        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => Log.Write($"UI error: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"Fatal: {e.ExceptionObject}");
        Application.Run(new TrayApp(link));
        try { mutex.ReleaseMutex(); } catch (ApplicationException) { /* not owned */ }
    }

    static void ForwardToRunning(int id, bool readOnly)
    {
        try
        {
            var port = HelperConfig.Load().LocalPort;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var res = http.PostAsJsonAsync($"http://127.0.0.1:{port}/open", new { operation_id = id, read_only = readOnly }).GetAwaiter().GetResult();
            if ((int)res.StatusCode == 409 && !readOnly)
            {
                var answer = MessageBox.Show("Die Datei ist von jemand anderem gesperrt.\n\nSchreibgeschützt öffnen?", "PDB Helper", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer == DialogResult.Yes) ForwardToRunning(id, true);
            }
            else if (!res.IsSuccessStatusCode)
            {
                MessageBox.Show(res.Content.ReadAsStringAsync().GetAwaiter().GetResult(), "PDB Helper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"PDB Helper antwortet nicht: {ex.Message}", "PDB Helper");
        }
    }
}
