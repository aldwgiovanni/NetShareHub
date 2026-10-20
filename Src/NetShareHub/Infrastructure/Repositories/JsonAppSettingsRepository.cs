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
            await using var stream = AppPaths.OpenUserFileForRead(_path);
            if (stream is null)
            {
                return new AppSettings_OutDto();
            }

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
            var document = new Document
            {
                Sharing = settings.Sharing,
                HotspotEnabled = settings.HotspotEnabled,
                Dispatch = settings.Dispatch,
            };

            // Fichier temporaire neuf (0600, sans suivre de lien) puis remplacement : voir AppPaths.WriteUserFileAsync.
            await AppPaths.WriteUserFileAsync(_path, JsonSerializer.Serialize(document, JsonOptions), cancellationToken);
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
