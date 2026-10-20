using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NetShareHub.Application.Contracts.Services;

namespace NetShareHub.Infrastructure.Services.Sharing;

/// <summary>
/// Journal des annulations, persisté sur disque après chaque ajout.
/// Si l'application est tuée en plein partage, le lancement suivant peut tout défaire avec <see cref="UndoAllAsync"/>.
/// <para>
/// Le fichier est rejoué avec les droits administrateur : il ne contient que des opérations typées (<see cref="UndoStep"/>),
/// revalidées à la relecture, et n'est relu que s'il appartient à l'administration (<see cref="JournalSecurity"/>).
/// </para>
/// </summary>
public sealed class UndoJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        RespectRequiredConstructorParameters = true, // « Kind » obligatoire
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, // ancien format (commandes libres) refusé
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    private readonly string _path;
    private readonly ICommandRunner _runner;
    private readonly ILogger _logger;
    private readonly bool _enforceSecurity;
    private readonly List<UndoStep> _undo = new();
    private bool _loaded;
    private bool _persistenceDisabled;

    /// <param name="path">Fichier du journal.</param>
    /// <param name="runner">Exécution des commandes.</param>
    /// <param name="logger">Journalisation.</param>
    /// <param name="enforceSecurity">
    /// Contrôle des droits du dossier et du propriétaire du fichier. Toujours actif pour le journal système ;
    /// désactivable seulement pour un dossier explicitement fourni (tests).
    /// </param>
    public UndoJournal(string path, ICommandRunner runner, ILogger logger, bool enforceSecurity = true)
    {
        _path = path;
        _runner = runner;
        _logger = logger;
        _enforceSecurity = enforceSecurity;
    }

    // Chargement différé : une instance sans droits administrateur (qui ne lit ni ne rejoue jamais le journal) n'a pas à
    // toucher au dossier protégé.
    public bool HasPending
    {
        get
        {
            EnsureLoaded();
            return _undo.Count > 0;
        }
    }

    public IReadOnlyList<UndoStep> Pending
    {
        get
        {
            EnsureLoaded();
            return _undo;
        }
    }

    /// <summary>Enregistre l'opération qui défera l'étape qu'on s'apprête à effectuer.</summary>
    public void Push(UndoStep undo)
    {
        EnsureLoaded();
        _ = undo.ToCommand(); // une étape invalide est une erreur de programmation : on la refuse tout de suite
        _undo.Add(undo);
        Save();
    }

    /// <summary>Exécute les annulations en ordre inverse, puis vide le journal. Les erreurs sont journalisées sans interrompre le nettoyage.</summary>
    public async Task UndoAllAsync(CancellationToken cancellationToken = default)
    {
        EnsureLoaded();
        for (var i = _undo.Count - 1; i >= 0; i--)
        {
            CommandSpec command;
            try
            {
                command = _undo[i].ToCommand();
            }
            catch (InvalidDataException ex)
            {
                _logger.LogWarning("Annulation ignorée : {Message}", ex.Message);
                continue;
            }

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

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        var directory = Path.GetDirectoryName(_path)!;
        if (_enforceSecurity && !JournalSecurity.TrySecureDirectory(directory, out var reason))
        {
            _persistenceDisabled = true;
            _logger.LogWarning("Journal d'annulation désactivé, dossier {Directory} non sûr : {Reason}", directory, reason);
            return;
        }

        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            List<UndoStep>? stored;
            using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (_enforceSecurity && !JournalSecurity.IsTrustedJournal(stream, out var reason))
                {
                    stream.Dispose();
                    _logger.LogWarning("Journal d'annulation refusé et supprimé ({Reason}) : il ne sera pas rejoué.", reason);
                    TryDelete(_path);
                    return;
                }

                stored = JsonSerializer.Deserialize<List<UndoStep>>(stream, JsonOptions);
            }

            if (stored is not null)
            {
                _undo.AddRange(stored.Where(s => s is not null));
            }
        }
        catch (JsonException ex)
        {
            // Ancien format (commandes libres, version 1.0.0) ou fichier altéré : il ne sera jamais rejoué, inutile de le garder.
            _logger.LogWarning("Journal d'annulation illisible, supprimé : {Message}", ex.Message);
            TryDelete(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning("Journal d'annulation illisible : {Message}", ex.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sans droit de suppression, le fichier reste ignoré à chaque lancement.
        }
    }

    private void Save()
    {
        if (_persistenceDisabled)
        {
            return;
        }

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

            // Fichier neuf puis remplacement : il hérite des droits du dossier protégé, même si un fichier déposé
            // auparavant portait d'autres droits.
            var temp = _path + ".tmp";
            File.Delete(temp);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, _undo, JsonOptions);
            }

            if (_enforceSecurity)
            {
                JournalSecurity.TakeOwnership(temp);
            }

            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning("Journal d'annulation non enregistré : {Message}", ex.Message);
        }
    }
}
