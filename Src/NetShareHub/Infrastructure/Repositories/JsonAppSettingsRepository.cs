using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NetShareHub.Application.Contracts.Persistence;
using NetShareHub.Domain.Dtos.Incoming;
using NetShareHub.Domain.Dtos.Outgoing;
using NetShareHub.Infrastructure.Services;

namespace NetShareHub.Infrastructure.Repositories;

/// <summary>
/// Préférences stockées dans un fichier JSON du profil utilisateur. Pas de Unit of Work : il n'y a ni base de données
/// ni transaction, une écriture atomique (fichier temporaire + remplacement) suffit.
/// </summary>
public sealed class JsonAppSettingsRepository : IAppSettingsRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly ILogger<JsonAppSettingsRepository> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonAppSettingsRepository(ILogger<JsonAppSettingsRepository> logger, string? directory = null)
    {
        _logger = logger;
        _path = Path.Combine(directory ?? AppPaths.SettingsDirectory, "settings.json");
    }

    public async Task<AppSettings_OutDto> GetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_path))
            {
                return new AppSettings_OutDto();
            }

            await using var stream = File.OpenRead(_path);
            var document = await JsonSerializer.DeserializeAsync<Document>(stream, JsonOptions, cancellationToken);
            return document is null
                ? new AppSettings_OutDto()
                : new AppSettings_OutDto
                {
                    Sharing = document.Sharing ?? new SharingRequest_InDto(),
                    HotspotEnabled = document.HotspotEnabled,
                    Dispatch = document.Dispatch ?? new DispatchConfig_InDto(),
                };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Préférences illisibles, valeurs par défaut utilisées : {Message}", ex.Message);
            return new AppSettings_OutDto();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AppSettings_InDto settings, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_path)!;
            AppPaths.EnsureUserDirectory(directory);

            var temp = _path + ".tmp";
            var document = new Document
            {
                Sharing = settings.Sharing,
                HotspotEnabled = settings.HotspotEnabled,
                Dispatch = settings.Dispatch,
            };

            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(document, JsonOptions), cancellationToken);
            if (!OperatingSystem.IsWindows())
            {
                // Le fichier peut contenir le mot de passe du hotspot : lecture réservée au propriétaire.
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            AppPaths.GiveToInvokingUser(temp);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Perdre des préférences n'est pas bloquant.
            _logger.LogWarning("Préférences non enregistrées : {Message}", ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Modèle de persistance : distinct des DTO pour pouvoir faire évoluer le fichier sans toucher aux contrats.
    private sealed class Document
    {
        public SharingRequest_InDto? Sharing { get; set; }

        public bool HotspotEnabled { get; set; }

        public DispatchConfig_InDto? Dispatch { get; set; }
    }
}
