namespace RedCompute.Node.Control;

/// <summary>
/// One agent per OS user (design 2.2): the named mutex <c>Local\LeafNode-&lt;SID&gt;</c> on Windows, an
/// exclusive lock on <c>$XDG_RUNTIME_DIR/leaf-node.lock</c> on Linux (.NET takes it with <c>flock</c>,
/// so it disappears with the process, however it ends).
/// </summary>
public sealed class InstanceLock : IDisposable
{
    // A mutex is re-entrant for the thread that owns it: within one process the held names are tracked too.
    private static readonly HashSet<string> Held = new(StringComparer.Ordinal);

    private readonly string _name;
    private readonly Mutex? _mutex;
    private readonly FileStream? _file;

    private InstanceLock(string name, Mutex? mutex, FileStream? file)
    {
        _name = name;
        _mutex = mutex;
        _file = file;
    }

    /// <summary>The lock, or null when another agent of this user holds it.</summary>
    public static InstanceLock? TryAcquire(NodePaths paths)
    {
        var name = OperatingSystem.IsWindows() ? ControlChannel.MutexName(paths) : paths.LockFile;
        lock (Held)
        {
            if (!Held.Add(name)) return null;
        }
        var acquired = OperatingSystem.IsWindows() ? AcquireMutex(name) : AcquireFile(paths);
        if (acquired is null)
            lock (Held) Held.Remove(name);
        return acquired;
    }

    private static InstanceLock? AcquireMutex(string name)
    {
        var mutex = new Mutex(initiallyOwned: false, name);
        try
        {
            if (mutex.WaitOne(TimeSpan.Zero)) return new InstanceLock(name, mutex, null);
        }
        catch (AbandonedMutexException)
        {
            return new InstanceLock(name, mutex, null); // the previous agent died holding it: ours now
        }
        mutex.Dispose();
        return null;
    }

    private static InstanceLock? AcquireFile(NodePaths paths)
    {
        Directory.CreateDirectory(paths.Runtime);
        try
        {
            return new InstanceLock(paths.LockFile, null,
                new FileStream(paths.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        lock (Held) Held.Remove(_name);
        if (_mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // released from another thread than the one that took it: the handle closes below anyway
            }
            _mutex.Dispose();
        }
        _file?.Dispose();
    }
}
