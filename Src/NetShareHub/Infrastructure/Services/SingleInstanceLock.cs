namespace NetShareHub.Infrastructure.Services;

/// <summary>
/// Garantit qu'une seule instance de NetShare Hub modifie le réseau de la machine. Sans elle, une deuxième instance
/// élevée rejouerait le journal d'annulation de la première et couperait son partage en cours.
/// <list type="bullet">
/// <item>Windows : mutex nommé <c>Global\NetShareHub</c>, commun à toutes les sessions (élevées ou non) ;</item>
/// <item>Linux : verrou <c>flock</c> sur <c>/run/netsharehub.lock</c> pour l'instance root, et sur
/// <c>$XDG_RUNTIME_DIR/netsharehub.lock</c> pour une instance utilisateur (qui ne touche pas au réseau).</item>
/// </list>
/// </summary>
public sealed class SingleInstanceLock : IDisposable
{
    /// <summary>Argument passé à l'instance relancée en administrateur : elle attend que l'ancienne libère le verrou.</summary>
    public const string RelaunchArgument = "--relaunched";

    public const string WindowsMutexName = @"Global\NetShareHub";
    public const string LinuxRootLockPath = "/run/netsharehub.lock";

    private readonly Mutex? _mutex;
    private readonly FileStream? _lockFile;

    private SingleInstanceLock(Mutex? mutex, FileStream? lockFile)
    {
        _mutex = mutex;
        _lockFile = lockFile;
    }

    /// <summary>Verrou par défaut de la plateforme courante.</summary>
    public static SingleInstanceLock? TryAcquire(TimeSpan wait)
        => OperatingSystem.IsWindows()
            ? TryAcquireMutex(WindowsMutexName, wait)
            : TryAcquireFile(DefaultLockPath(), wait);

    /// <summary>Mutex nommé, possédé par le thread appelant jusqu'à <see cref="Dispose"/>.</summary>
    public static SingleInstanceLock? TryAcquireMutex(string name, TimeSpan wait)
    {
        Mutex mutex;
        try
        {
            mutex = new Mutex(initiallyOwned: false, name);
        }
        catch (UnauthorizedAccessException)
        {
            return null; // mutex créé par une instance d'un autre compte (ou élevée) : elle tourne.
        }

        try
        {
            if (mutex.WaitOne(wait))
            {
                return new SingleInstanceLock(mutex, null);
            }
        }
        catch (AbandonedMutexException)
        {
            return new SingleInstanceLock(mutex, null); // l'instance précédente a été tuée : le verrou nous revient.
        }

        mutex.Dispose();
        return null;
    }

    /// <summary>
    /// Verrou exclusif sur un fichier. Sous Unix, <see cref="FileShare.None"/> pose un <c>flock(LOCK_EX | LOCK_NB)</c>,
    /// libéré par le noyau à la mort du processus : un verrou périmé ne bloque jamais le lancement suivant.
    /// </summary>
    public static SingleInstanceLock? TryAcquireFile(string path, TimeSpan wait)
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            try
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.OpenOrCreate,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                };
                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }

                return new SingleInstanceLock(null, new FileStream(path, options));
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(200);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    public void Dispose()
    {
        if (_mutex is not null)
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }

        _lockFile?.Dispose();
    }

    private static string DefaultLockPath()
    {
        if (Environment.IsPrivilegedProcess)
        {
            return LinuxRootLockPath;
        }

        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        return string.IsNullOrEmpty(runtime)
            ? Path.Combine(Path.GetTempPath(), $"netsharehub-{Environment.UserName}.lock")
            : Path.Combine(runtime, "netsharehub.lock");
    }
}
