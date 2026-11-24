namespace PdbBridge
{
    /// <summary>The tiny line protocol between the PDB Helper (tray app) and this add-in.
    /// One request line per connection, one answer line back. Tab separated, UTF-8.
    ///   PING              -> OK  version  current-file
    ///   CURRENT           -> OK  current-file
    ///   OPEN  path        -> OK  path      (Mastercam asks to save the current file first)
    ///   MACHINE           -> OK  machine definition of the active machine group
    ///   SETMACHINE  mmd   -> OK  number of machine groups switched to that .mcam-mmd
    ///   SAVEAS  path      -> OK  path      (saves the open file under a new name/folder)
    ///   NEWFILE path [mmd] -> OK path      (new file, machine group from the .mcam-mmd/-lmd, saved there)
    ///   ERR   message     on any failure
    /// The pipe is named per Mastercam process, so the helper can talk to the right window.</summary>
    public static class BridgeProtocol
    {
        public const string PipePrefix = "PdbBridge-";

        public static string PipeName(int processId) => PipePrefix + processId;
    }
}
