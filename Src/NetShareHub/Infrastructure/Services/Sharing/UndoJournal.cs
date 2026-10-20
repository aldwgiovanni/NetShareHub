using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetShareHub.Application.Contracts.Services;

namespace NetShareHub.Infrastructure.Services.Sharing;

/// <summary>
/// Journal des commandes d'annulation, persisté sur disque après chaque ajout.
/// Si l'application est tuée en plein partage, le lancement suivant peut tout défaire avec <see cref="UndoAllAsync"/>.
/// </summary>
public sealed class UndoJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ICommandRunner _runner;
    private readonly ILogger _logger;
    private readonly List<CommandSpec> _undo = new();

    public UndoJournal(string path, ICommandRunner runner, ILogger logger)
    {
        _path = path;
        _runner = runner;
        _logger = logger;
        Load();
    }

    public bool HasPending => _undo.Count > 0;

    public IReadOnlyList<CommandSpec> Pending => _undo;

    /// <summary>Enregistre la commande qui défera l'étape qu'on s'apprête à effectuer.</summary>
    public void Push(CommandSpec undo)
    {
        _undo.Add(undo.Lenient());
        Save();
    }

    /// <summary>Exécute les annulations en ordre inverse, puis vide le journal. Les erreurs sont journalisées sans interrompre le nettoyage.</summary>
    public async Task UndoAllAsync(CancellationToken cancellationToken = default)
    {
        for (var i = _undo.Count - 1; i >= 0; i--)
        {
            var command = _undo[i];
            try
            {
                var result = await _runner.RunAsync(command, cancellationToken);
                if (!result.Success)
                {
                    _logger.LogWarning("Annulation partielle ({Command}) : {Error}", command.Display, result.StdErr);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Annulation impossible : {Command}", command.Display);
            }
        }

        _undo.Clear();
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var stored = JsonSerializer.Deserialize<List<StoredCommand>>(File.ReadAllText(_path), JsonOptions);
            if (stored is null)
            {
                return;
            }

            _undo.AddRange(stored.Select(s => new CommandSpec(s.FileName, s.Arguments, s.StdIn, s.TimeoutSeconds, true, s.Description)
            {
                Script = s.Script,
            }));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Journal d'annulation illisible : {Message}", ex.Message);
        }
    }

    private void Save()
    {
        try
        {
            if (_undo.Count == 0)
            {
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }

                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var stored = _undo.Select(c => new StoredCommand(c.FileName, c.Arguments.ToList(), c.StdIn, c.TimeoutSeconds, c.Description, c.Script));
            File.WriteAllText(_path, JsonSerializer.Serialize(stored, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Journal d'annulation non enregistré : {Message}", ex.Message);
        }
    }

    private sealed record StoredCommand(
        string FileName,
        List<string> Arguments,
        string? StdIn,
        int TimeoutSeconds,
        string? Description,
        string? Script);
}
