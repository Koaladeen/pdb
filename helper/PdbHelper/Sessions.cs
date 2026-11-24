namespace PdbHelper;

public enum NoticeKind { Info, Warning, Error }

/// <summary>One operation opened through the helper.</summary>
public sealed class FileSession : IDisposable
{
    public required int OperationId { get; init; }
    public required string MdmText { get; init; }
    public required string PartName { get; init; }
    // settable: a machine change moves the operation to another machine folder
    public required string McamPath { get; set; }
    public required string NcPostedPath { get; set; }

    /// <summary>.mcam-mmd the file should use (from the machine in PDB); "" = not set.</summary>
    public string MachineDefinition { get; set; } = "";
    public required bool ReadOnly { get; init; }
    public int Revision { get; set; }
    public string NextArchive { get; set; } = "";
    public DateTime OpenedAt { get; } = DateTime.Now;
    public int Misses { get; set; }
    public bool LockLost { get; set; }

    /// <summary>Write time + size of the .mcam at open / last check-in; a change means "saved".</summary>
    public (DateTime, long)? Known { get; set; }

    internal FileSystemWatcher? Watcher { get; set; }
    internal CancellationTokenSource? SaveDebounce;
    internal CancellationTokenSource? NcDebounce;
    internal CancellationTokenSource? PicDebounce;

    /// <summary>Time of the picture on the server (UTC, ISO), null if none.</summary>
    public string? PictureAt { get; set; }
    internal readonly SemaphoreSlim Busy = new(1, 1);

    public string Title => $"MDM {MdmText}{(PartName.Length > 0 ? $" – {PartName}" : "")}{(ReadOnly ? " (nur lesen)" : "")}";

    public static (DateTime, long)? Stamp(string path)
    {
        var fi = new FileInfo(path);
        return fi.Exists ? (fi.LastWriteTimeUtc, fi.Length) : null;
    }

    public void Dispose()
    {
        Watcher?.Dispose();
        SaveDebounce?.Cancel();
        NcDebounce?.Cancel();
        PicDebounce?.Cancel();
    }

    /// <summary>MDM's picture names: Pic00084197.png.cam (current, with toolpaths), PIC00084197.png
    /// (older model view), Pic-00000001.png. Case does not matter.</summary>
    public bool IsPicture(string fileName) =>
        System.Text.RegularExpressions.Regex.IsMatch(fileName, $@"^pic-?{MdmText}\.png(\.cam)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Best local picture: the newest .png.cam, else the newest .png.</summary>
    public FileInfo? LocalPicture()
    {
        var dir = Path.GetDirectoryName(McamPath);
        if (dir is null || !Directory.Exists(dir)) return null;
        var pics = new DirectoryInfo(dir).GetFiles().Where(f => IsPicture(f.Name)).ToList();
        return pics.Where(f => f.Name.EndsWith(".cam", StringComparison.OrdinalIgnoreCase)).MaxBy(f => f.LastWriteTimeUtc)
            ?? pics.MaxBy(f => f.LastWriteTimeUtc);
    }
}

public sealed record OpenOutcome(int Status, string Message, string Mode = "", LockInfo? Lock = null);

/// <summary>Keeps track of the files opened through the helper: check-out, heartbeat,
/// check-in on save, release on close, NC export after posting.</summary>
public sealed class SessionManager : IDisposable
{
    readonly HelperConfig config;
    readonly IMastercam mastercam;
    readonly Action<string, NoticeKind> notify;
    readonly Dictionary<int, FileSession> sessions = new();
    readonly object gate = new();
    readonly System.Threading.Timer heartbeatTimer;
    readonly System.Threading.Timer closeTimer;

    public ServerApi? Api { get; set; }

    public SessionManager(HelperConfig config, Action<string, NoticeKind> notify, IMastercam? mastercam = null)
    {
        this.config = config;
        this.notify = notify;
        this.mastercam = mastercam ?? new Mastercam(config);
        heartbeatTimer = new System.Threading.Timer(_ => _ = HeartbeatAll(), null, TimeSpan.FromSeconds(config.HeartbeatSeconds), TimeSpan.FromSeconds(config.HeartbeatSeconds));
        closeTimer = new System.Threading.Timer(_ => _ = CheckClosed(), null, TimeSpan.FromSeconds(config.CloseCheckSeconds), TimeSpan.FromSeconds(config.CloseCheckSeconds));
    }

    public IReadOnlyList<FileSession> All
    {
        get { lock (gate) return sessions.Values.ToList(); }
    }

    // ------------------------------------------------------------------ open

    public async Task<OpenOutcome> Open(int operationId, bool readOnly)
    {
        var api = Api ?? throw new InvalidOperationException("Nicht angemeldet");
        FileSession? existing;
        lock (gate) sessions.TryGetValue(operationId, out existing);
        if (existing is not null && (!existing.ReadOnly || readOnly))
        {
            Launch(existing);
            return new(200, "Bereits geöffnet", existing.ReadOnly ? "readonly" : "edit");
        }

        var op = await api.Operation(operationId);
        if (readOnly) return OpenReadOnly(op);

        CheckoutResult checkout;
        try
        {
            checkout = await api.Checkout(operationId);
        }
        catch (ApiException ex) when (ex.Status == System.Net.HttpStatusCode.Conflict)
        {
            return new(409, ex.Message, Lock: ex.Lock);
        }

        var paths = checkout.Paths;
        var session = new FileSession
        {
            OperationId = op.Id,
            MdmText = op.MdmNumberText,
            PartName = op.PartName,
            McamPath = paths["mcam"],
            NcPostedPath = paths["nc_posted"],
            ReadOnly = false,
            Revision = checkout.CurrentRevision,
            NextArchive = paths["next_archive"],
            MachineDefinition = op.MachineDefinition ?? "",
        };
        try
        {
            Directory.CreateDirectory(paths["folder"]);
        }
        catch (Exception ex)
        {
            await api.Release(op.Id);
            return new(500, $"Ordner nicht erreichbar: {paths["folder"]} ({ex.Message})");
        }
        // Imported file, first open: copy the original into the operation folder (the original
        // stays where it is). Never create an empty new file instead.
        if (!File.Exists(session.McamPath) && !string.IsNullOrEmpty(op.ImportSource))
        {
            try
            {
                File.Copy(op.ImportSource, session.McamPath);
                Log.Write($"Imported {op.ImportSource} -> {session.McamPath}");
                try { await api.ImportCopied(op.Id); } catch (Exception ex) { Log.Write($"import-copied not reported: {ex.Message}"); }
            }
            catch (Exception ex)
            {
                await api.Release(op.Id);
                return new(404, $"Original nicht erreichbar: {op.ImportSource} ({ex.Message})");
            }
        }
        session.Known = FileSession.Stamp(session.McamPath);
        session.PictureAt = op.PictureAt;
        Watch(session, paths["folder"]);
        lock (gate)
        {
            existing?.Dispose();
            sessions[op.Id] = session;
        }
        Launch(session);
        _ = SyncPicture(session, force: false); // existing picture the server does not have yet
        Log.Write($"Checked out op {op.Id} ({session.McamPath})");
        return new(200, "Ausgecheckt und geöffnet", "edit");
    }

    OpenOutcome OpenReadOnly(OperationInfo op)
    {
        var source = op.Paths["mcam"];
        if (!File.Exists(source) && !string.IsNullOrEmpty(op.ImportSource)) source = op.ImportSource; // imported, not copied yet
        if (!File.Exists(source)) return new(404, $"Datei existiert noch nicht: {source}");
        var dir = Path.Combine(Path.GetTempPath(), "PDB", "nur-lesen");
        Directory.CreateDirectory(dir);
        var copy = Path.Combine(dir, $"{op.MdmNumberText}_nur-lesen_{DateTime.Now:HHmmss}.mcam");
        File.Copy(source, copy, overwrite: true);
        File.SetAttributes(copy, FileAttributes.ReadOnly);
        mastercam.Open(copy);
        return new(200, "Schreibgeschützte Kopie geöffnet", "readonly");
    }

    void Launch(FileSession s)
    {
        if (File.Exists(s.McamPath))
        {
            mastercam.Open(s.McamPath);
            _ = AlignMachineWhenOpen(s);
            return;
        }
        // New operation: no file yet. Mastercam (via the bridge) creates it with the machine
        // definition and saves it in the operation folder; the folder watcher checks it in.
        // In the background: Mastercam may first have to start.
        _ = Task.Run(() =>
        {
            var error = mastercam.CreateNew(s.McamPath, File.Exists(s.MachineDefinition) ? s.MachineDefinition : "");
            if (error is null)
            {
                notify($"{s.Title}: neue Datei angelegt ({Path.GetFileName(s.McamPath)}{(s.MachineDefinition.Length > 0 ? $", {Path.GetFileName(s.MachineDefinition)}" : "")}).", NoticeKind.Info);
                return;
            }
            notify($"{s.Title}: {error}", NoticeKind.Warning);
            if (!File.Exists(s.McamPath)) System.Diagnostics.Process.Start("explorer.exe", $"\"{Path.GetDirectoryName(s.McamPath)}\"");
        });
    }

    // ------------------------------------------------------------------ machine definition

    /// <summary>Once the file is open in Mastercam (with bridge), make sure it uses the machine
    /// definition of its PDB machine. Covers a machine change made while the file was closed.</summary>
    async Task AlignMachineWhenOpen(FileSession s)
    {
        if (s.MachineDefinition.Length == 0) return;
        // Opening may wait for "save the current file?" in Mastercam; give it time.
        for (var i = 0; i < 120 && sessions.ContainsKey(s.OperationId); i++)
        {
            var current = mastercam.MachineOf(s.McamPath);
            if (current is not null)
            {
                if (SameMachine(current, s.MachineDefinition)) return;
                if (!File.Exists(s.MachineDefinition))
                {
                    notify($"{s.Title}: Maschinendefinition nicht gefunden: {s.MachineDefinition}", NoticeKind.Warning);
                    return;
                }
                var error = mastercam.SetMachine(s.McamPath, s.MachineDefinition);
                notify(error is null
                    ? $"{s.Title}: Maschinendefinition auf {Path.GetFileName(s.MachineDefinition)} umgestellt – bitte speichern."
                    : $"{s.Title}: Maschinendefinition nicht umgestellt: {error}", error is null ? NoticeKind.Info : NoticeKind.Warning);
                Log.Write($"Align machine op {s.OperationId}: {current} -> {s.MachineDefinition}: {error ?? "ok"}");
                return;
            }
            await Task.Delay(5000);
        }
    }

    /// <summary>Same full path, or (Mastercam may write the path differently, e.g. M:\ vs. the
    /// network name) the same .mcam-mmd file name.</summary>
    internal static bool SameMachine(string current, string wanted)
    {
        if (current.Length == 0) return false;
        if (string.Equals(current, wanted, StringComparison.OrdinalIgnoreCase)) return true;
        return string.Equals(Path.GetFileName(current), Path.GetFileName(wanted), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"Maschine wechseln" from the web page. Server first (rights, new NC number), then
    /// the files: if the operation is open here, Mastercam switches the machine definition and
    /// saves into the new machine folder; otherwise the folder is moved.</summary>
    public async Task<OpenOutcome> ChangeMachine(int operationId, int machineId)
    {
        var api = Api ?? throw new InvalidOperationException("Nicht angemeldet");
        FileSession? s;
        lock (gate) sessions.TryGetValue(operationId, out s);
        var editing = s is { ReadOnly: false, LockLost: false };
        if (editing && File.Exists(s!.McamPath) && mastercam.MachineOf(s.McamPath) is null)
            return new(409, "Die Datei ist offen, aber nicht in einem Mastercam mit PDB-Bridge – bitte zuerst schließen oder die Bridge laden.");

        MachineChangeResult res;
        try
        {
            res = await api.ChangeMachine(operationId, machineId);
        }
        catch (ApiException ex)
        {
            return new((int)ex.Status == 0 ? 502 : (int)ex.Status, ex.Message, Lock: ex.Lock);
        }

        var oldFolder = res.OldPaths["folder"];
        var newFolder = res.Paths["folder"];
        var mmd = res.MachineDefinition ?? "";
        try
        {
            if (editing)
            {
                await s!.Busy.WaitAsync();
                try
                {
                    s.Watcher?.Dispose();
                    if (File.Exists(s.McamPath))
                    {
                        if (mmd.Length > 0 && File.Exists(mmd) && mastercam.SetMachine(s.McamPath, mmd) is { } e1)
                            notify($"{s.Title}: Maschinendefinition nicht umgestellt: {e1}", NoticeKind.Warning);
                        if (mastercam.SaveAs(s.McamPath, res.Paths["mcam"]) is { } e2)
                            throw new IOException($"Speichern im neuen Ordner fehlgeschlagen: {e2}");
                    }
                    MoveFolderContents(oldFolder, newFolder, skip: Path.GetFileName(res.OldPaths["mcam"]));
                    TryDelete(res.OldPaths["mcam"]);
                    TryDeleteEmpty(oldFolder);
                    s.McamPath = res.Paths["mcam"];
                    s.NcPostedPath = res.Paths["nc_posted"];
                    s.NextArchive = res.Paths["next_archive"];
                    s.MachineDefinition = mmd;
                    s.Known = null; // file was saved in the new place: next check-in records it
                    s.Misses = 0;   // the close check looked at the old path meanwhile
                    Watch(s, newFolder);
                }
                finally
                {
                    s.Busy.Release();
                }
                var saved = await CheckinIfChanged(s, keepLock: true, comment: $"Maschine gewechselt: {res.Machine}");
                Log.Write($"Machine change op {operationId} (open): {oldFolder} -> {newFolder} {saved}");
                return new(200, $"Maschine gewechselt auf {res.Machine}. Datei liegt jetzt in {newFolder}.", "edit");
            }

            MoveFolderContents(oldFolder, newFolder, skip: null);
            TryDeleteEmpty(oldFolder);
            Log.Write($"Machine change op {operationId} (closed): {oldFolder} -> {newFolder}");
            return new(200, $"Maschine gewechselt auf {res.Machine}. Ordner verschoben; die Maschinendefinition wird beim nächsten Öffnen umgestellt.");
        }
        catch (Exception ex)
        {
            Log.Write($"Machine change files failed op {operationId}: {ex}");
            return new(500, $"In PDB auf {res.Machine} umgestellt, aber die Dateien konnten nicht verschoben werden: {ex.Message}. Bitte {oldFolder} prüfen.");
        }
    }

    /// <summary>Moves files and subfolders (e.g. Revisionen) into the new folder; existing files
    /// in the target are kept, the old one is then left in place.</summary>
    static void MoveFolderContents(string from, string to, string? skip)
    {
        if (!Directory.Exists(from)) return;
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            var name = Path.GetFileName(file);
            if (skip is not null && name.Equals(skip, StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.Combine(to, name);
            if (!File.Exists(target)) File.Move(file, target);
        }
        foreach (var dir in Directory.GetDirectories(from))
            MoveFolderContents(dir, Path.Combine(to, Path.GetFileName(dir)), null);
        TryDeleteEmpty(from);
    }

    static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch (Exception ex) { Log.Write($"Could not delete {file}: {ex.Message}"); }
    }

    static void TryDeleteEmpty(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch (Exception ex)
        {
            Log.Write($"Could not remove {folder}: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ watching the folder

    void Watch(FileSession s, string folder)
    {
        var w = new FileSystemWatcher(folder)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
        };
        var mcamName = Path.GetFileName(s.McamPath);
        var ncName = Path.GetFileName(s.NcPostedPath);
        void OnEvent(string name)
        {
            if (name.Equals(mcamName, StringComparison.OrdinalIgnoreCase))
                Debounce(ref s.SaveDebounce, TimeSpan.FromSeconds(3), () => SaveDetected(s));
            else if (name.Equals(ncName, StringComparison.OrdinalIgnoreCase))
                Debounce(ref s.NcDebounce, TimeSpan.FromSeconds(2), () => NcPosted(s));
            else if (s.IsPicture(name))
                Debounce(ref s.PicDebounce, TimeSpan.FromSeconds(2), () => SyncPicture(s, force: true));
        }
        w.Changed += (_, e) => OnEvent(e.Name ?? "");
        w.Created += (_, e) => OnEvent(e.Name ?? "");
        w.Renamed += (_, e) => OnEvent(e.Name ?? "");
        w.EnableRaisingEvents = true;
        s.Watcher = w;
    }

    static void Debounce(ref CancellationTokenSource? slot, TimeSpan delay, Func<Task> action)
    {
        slot?.Cancel();
        var cts = new CancellationTokenSource();
        slot = cts;
        _ = Task.Delay(delay, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) _ = action();
        }, TaskScheduler.Default);
    }

    async Task SaveDetected(FileSession s)
    {
        var msg = await CheckinIfChanged(s, keepLock: true, comment: "Gespeichert in Mastercam");
        if (msg is not null) notify(msg, NoticeKind.Info);
    }

    async Task NcPosted(FileSession s)
    {
        if (Api is null) return;
        await s.Busy.WaitAsync();
        try
        {
            var outcome = await new NcExportService(Api).Process(s.OperationId, s.NcPostedPath);
            if (outcome.Done) notify(outcome.Message, outcome.Message.StartsWith("NC-Export abgelehnt") ? NoticeKind.Warning : NoticeKind.Info);
        }
        catch (Exception ex)
        {
            Log.Write($"NC export failed op {s.OperationId}: {ex}");
            notify($"NC-Export fehlgeschlagen: {ex.Message}", NoticeKind.Error);
        }
        finally
        {
            s.Busy.Release();
        }
    }

    /// <summary>Uploads the local picture if it changed (force) or is newer than the server's.</summary>
    async Task SyncPicture(FileSession s, bool force)
    {
        if (Api is null) return;
        try
        {
            var pic = s.LocalPicture();
            if (pic is null) return;
            if (!force && s.PictureAt is { } at && DateTime.TryParse(at, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var serverTime)
                && pic.LastWriteTimeUtc <= serverTime.AddSeconds(2))
                return;
            byte[] bytes;
            for (var i = 0; ; i++)
            {
                try { bytes = await File.ReadAllBytesAsync(pic.FullName); break; }
                catch (IOException) when (i < 10) { await Task.Delay(500); }
            }
            s.PictureAt = await Api.UploadPicture(s.OperationId, bytes);
            Log.Write($"Picture uploaded op {s.OperationId}: {pic.Name}");
        }
        catch (Exception ex)
        {
            Log.Write($"Picture upload failed op {s.OperationId}: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ check-in, release

    /// <summary>If the .mcam changed since the last check-in: archive copy + check-in.
    /// Returns a message for the user, or null if nothing changed.</summary>
    async Task<string?> CheckinIfChanged(FileSession s, bool keepLock, string comment)
    {
        if (Api is null || s.ReadOnly || s.LockLost) return null;
        await s.Busy.WaitAsync();
        try
        {
            var stamp = FileSession.Stamp(s.McamPath);
            if (stamp is null || stamp == s.Known) return null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var revision = s.Revision + 1;
                try
                {
                    await FileOps.CopyWithRetry(s.McamPath, s.NextArchive);
                    var res = await Api.Checkin(s.OperationId, revision, comment, keepLock);
                    s.Revision = res.Revision;
                    s.NextArchive = res.Paths["next_archive"];
                    s.Known = stamp;
                    Log.Write($"Checked in op {s.OperationId} r{res.Revision} -> {res.ArchivePath}");
                    return $"{s.Title}: Version {res.Revision} gespeichert";
                }
                catch (ApiException ex) when (ex.Status == System.Net.HttpStatusCode.Conflict && ex.Message.StartsWith("Expected revision") && attempt == 0)
                {
                    // someone (e.g. admin restore) moved the revision on: reload and retry once
                    var op = await Api.Operation(s.OperationId);
                    s.Revision = op.CurrentRevision;
                    s.NextArchive = op.Paths["next_archive"];
                }
            }
            return null;
        }
        catch (ApiException ex)
        {
            Log.Write($"Check-in failed op {s.OperationId}: {ex.Message}");
            if (ex.Status == System.Net.HttpStatusCode.Conflict) s.LockLost = true;
            notify($"{s.Title}: Einchecken fehlgeschlagen – {ex.Message}", NoticeKind.Error);
            return null;
        }
        finally
        {
            s.Busy.Release();
        }
    }

    public async Task Close(int operationId, string reason)
    {
        FileSession? s;
        lock (gate) sessions.Remove(operationId, out s);
        if (s is null) return;
        s.Watcher?.Dispose();
        if (Api is not null && !s.ReadOnly && !s.LockLost)
        {
            var saved = await CheckinIfChanged(s, keepLock: false, comment: "Gespeichert, beim Schließen eingecheckt");
            if (saved is null)
            {
                try { await Api.Release(operationId); }
                catch (ApiException ex) { Log.Write($"Release failed op {operationId}: {ex.Message}"); }
            }
        }
        s.Dispose();
        Log.Write($"Closed op {operationId}: {reason}");
        notify($"{s.Title}: freigegeben ({reason})", NoticeKind.Info);
    }

    public async Task CloseAll(string reason)
    {
        foreach (var s in All) await Close(s.OperationId, reason);
    }

    public Task CheckinNow(int operationId)
    {
        FileSession? s;
        lock (gate) sessions.TryGetValue(operationId, out s);
        return s is null ? Task.CompletedTask : SaveDetected(s);
    }

    // ------------------------------------------------------------------ timers

    async Task HeartbeatAll()
    {
        if (Api is null) return;
        foreach (var s in All.Where(x => !x.ReadOnly && !x.LockLost))
        {
            try
            {
                await Api.Heartbeat(s.OperationId);
            }
            catch (ApiException ex) when (ex.Status == System.Net.HttpStatusCode.Conflict)
            {
                s.LockLost = true;
                Log.Write($"Lock lost op {s.OperationId}: {ex.Message}");
                notify($"{s.Title}: Sperre verloren! Jemand anderes hat die Datei übernommen. Änderungen werden NICHT eingecheckt.", NoticeKind.Error);
            }
            catch (ApiException ex)
            {
                Log.Write($"Heartbeat failed op {s.OperationId}: {ex.Message}"); // server briefly away: try again next round
            }
        }
    }

    int checking;

    async Task CheckClosed()
    {
        if (Interlocked.Exchange(ref checking, 1) == 1) return;
        try
        {
            foreach (var s in All)
            {
                if ((DateTime.Now - s.OpenedAt).TotalSeconds < config.StartupGraceSeconds) continue;
                if (!File.Exists(s.McamPath) && !s.ReadOnly)
                {
                    s.Misses = 0; // new file not saved yet: wait for the user
                    continue;
                }
                if (mastercam.IsOpen(s.McamPath))
                {
                    s.Misses = 0;
                    continue;
                }
                if (++s.Misses >= config.CloseAfterMisses) await Close(s.OperationId, "in Mastercam geschlossen");
            }
        }
        finally
        {
            Interlocked.Exchange(ref checking, 0);
        }
    }

    public void Dispose()
    {
        heartbeatTimer.Dispose();
        closeTimer.Dispose();
        foreach (var s in All) s.Dispose();
    }
}
