using Microsoft.Extensions.Logging;
using NetShareHub.Infrastructure.Services;

namespace NetShareHub.Desktop.Logging;

public sealed record LogEntry(DateTimeOffset Time, LogLevel Level, string Category, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss");

    public string LevelText => Level switch
    {
        LogLevel.Warning => "ATTENTION",
        LogLevel.Error or LogLevel.Critical => "ERREUR",
        _ => "INFO",
    };

    public bool IsProblem => Level >= LogLevel.Warning;

    public override string ToString() => $"{Time:yyyy-MM-dd HH:mm:ss} [{LevelText}] {Message}";
}

/// <summary>Conserve les derniers messages pour l'onglet Journal et les écrit dans un fichier.</summary>
public sealed class LogStore
{
    private const int Capacity = 500;
    private const long MaxFileBytes = 1_000_000;

    private readonly object _gate = new();
    private readonly List<LogEntry> _entries = new();
    private readonly string _filePath;

    public LogStore(string? directory = null)
    {
        _filePath = Path.Combine(directory ?? Path.Combine(AppPaths.SettingsDirectory, "logs"), "netsharehub.log");
    }

    public event Action<LogEntry>? EntryAdded;

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToList();
        }
    }

    public void Add(LogEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
            if (_entries.Count > Capacity)
            {
                _entries.RemoveAt(0);
            }

            WriteToFile(entry);
        }

        EntryAdded?.Invoke(entry);
    }

    private void WriteToFile(LogEntry entry)
    {
        try
        {
            AppPaths.EnsureUserDirectory(Path.GetDirectoryName(_filePath)!);
            var info = new FileInfo(_filePath);
            if (info.Exists && info.Length > MaxFileBytes)
            {
                File.Move(_filePath, _filePath + ".1", overwrite: true);
            }

            File.AppendAllText(_filePath, entry + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Le journal fichier est un confort : jamais une cause d'échec.
        }
    }
}

public sealed class LogStoreLoggerProvider : ILoggerProvider
{
    private readonly LogStore _store;

    public LogStoreLoggerProvider(LogStore store)
    {
        _store = store;
    }

    public ILogger CreateLogger(string categoryName) => new StoreLogger(_store, categoryName);

    public void Dispose()
    {
    }

    private sealed class StoreLogger(LogStore store, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // Debug (commandes système exécutées) reste hors de l'interface : trop bavard.
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (exception is not null && logLevel >= LogLevel.Error)
            {
                message += $" ({exception.GetType().Name}: {exception.Message})";
            }

            store.Add(new LogEntry(DateTimeOffset.Now, logLevel, category, message));
        }
    }
}
