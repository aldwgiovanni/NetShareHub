using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NetShareHub.Application.Contracts.Persistence;
using NetShareHub.Domain.Dtos.Incoming;
using NetShareHub.Domain.Dtos.Outgoing;
using NetShareHub.Domain.Enums;
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
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = AppPaths.OpenUserFileForRead(_path);
            if (stream is null)
            {
                return new AppSettings_OutDto();
            }

            var document = await JsonSerializer.DeserializeAsync<Document>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            return document is null
                ? new AppSettings_OutDto()
                : new AppSettings_OutDto
                {
                    Sharing = Normalize(document.Sharing),
                    HotspotEnabled = document.HotspotEnabled,
                    Dispatch = Normalize(document.Dispatch),
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
        // ConfigureAwait(false) : un enregistrement synchrone à la fermeture (thread d'interface bloqué) ne doit pas
        // attendre qu'un enregistrement différé, démarré sur ce même thread, y reprenne la main.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = new Document
            {
                Sharing = settings.Sharing,
                HotspotEnabled = settings.HotspotEnabled,
                Dispatch = settings.Dispatch,
            };

            // Fichier temporaire neuf (0600, sans suivre de lien) puis remplacement : voir AppPaths.WriteUserFileAsync.
            await AppPaths.WriteUserFileAsync(_path, JsonSerializer.Serialize(document, JsonOptions), cancellationToken).ConfigureAwait(false);
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

    // Un fichier modifié à la main (ou écrit par une autre version) peut contenir des null, des listes à trous ou des
    // valeurs d'énumération hors plage (JsonStringEnumConverter accepte les entiers) : on les ramène à des valeurs sûres.
    private static SharingRequest_InDto Normalize(SharingRequest_InDto? sharing)
    {
        sharing ??= new SharingRequest_InDto();
        sharing.SourceAdapterIds = CleanIds(sharing.SourceAdapterIds);
        sharing.TargetAdapterIds = CleanIds(sharing.TargetAdapterIds);
        sharing.SourceWeights = (sharing.SourceWeights ?? new Dictionary<string, int>())
            .Where(w => !string.IsNullOrWhiteSpace(w.Key))
            .ToDictionary(w => w.Key, w => Math.Clamp(w.Value, 1, 10));
        if (sharing.Hotspot is { } hotspot)
        {
            hotspot.Ssid = string.IsNullOrWhiteSpace(hotspot.Ssid) ? new WifiHotspot_InDto().Ssid : hotspot.Ssid;
            hotspot.Password ??= string.Empty;
            hotspot.Band = Enum.IsDefined(hotspot.Band) ? hotspot.Band : WifiBand.Auto;
        }

        return sharing;
    }

    private static DispatchConfig_InDto Normalize(DispatchConfig_InDto? dispatch)
    {
        var defaults = new DispatchConfig_InDto();
        dispatch ??= defaults;
        dispatch.Uplinks = (dispatch.Uplinks ?? new List<DispatchUplink_InDto>())
            .Where(u => u is not null && !string.IsNullOrWhiteSpace(u.AdapterId))
            .ToList();
        dispatch.Strategy = Enum.IsDefined(dispatch.Strategy) ? dispatch.Strategy : defaults.Strategy;
        dispatch.Port = dispatch.Port is >= 1 and <= 65535 ? dispatch.Port : defaults.Port;
        dispatch.StickyMinutes = dispatch.StickyMinutes is >= 1 and <= 240 ? dispatch.StickyMinutes : defaults.StickyMinutes;
        return dispatch;
    }

    private static List<string> CleanIds(List<string>? ids)
        => (ids ?? new List<string>()).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    // Modèle de persistance : distinct des DTO pour pouvoir faire évoluer le fichier sans toucher aux contrats.
    private sealed class Document
    {
        public SharingRequest_InDto? Sharing { get; set; }

        public bool HotspotEnabled { get; set; }

        public DispatchConfig_InDto? Dispatch { get; set; }
    }
}
