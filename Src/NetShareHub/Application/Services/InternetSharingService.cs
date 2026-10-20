using Microsoft.Extensions.Logging;
using NetShareHub.Application.Contracts.Services;
using NetShareHub.Domain.Dtos.Incoming;
using NetShareHub.Domain.Dtos.Outgoing;
using NetShareHub.Domain.Enums;
using NetShareHub.Domain.Exceptions;

namespace NetShareHub.Application.Services;

/// <summary>Orchestre un partage de connexion : validation, plan, exécution par le moteur du système, état.</summary>
public sealed class InternetSharingService : IInternetSharingService
{
    private readonly INetworkInventoryService _inventory;
    private readonly ISharingBackend _backend;
    private readonly IPrivilegeService _privilege;
    private readonly IPlatformInfo _platform;
    private readonly SharingPlanner _planner;
    private readonly ILogger<InternetSharingService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SharingStatus_OutDto _status = new() { State = RunState.Stopped, Message = "Partage arrêté." };

    public InternetSharingService(
        INetworkInventoryService inventory,
        ISharingBackend backend,
        IPrivilegeService privilege,
        IPlatformInfo platform,
        SharingPlanner planner,
        ILogger<InternetSharingService> logger)
    {
        _inventory = inventory;
        _backend = backend;
        _privilege = privilege;
        _platform = platform;
        _planner = planner;
        _logger = logger;
    }

    public SharingStatus_OutDto Status => _status;

    public event EventHandler<SharingStatus_OutDto>? StatusChanged;

    public async Task<PlatformInfo_OutDto> GetPlatformInfoAsync(CancellationToken cancellationToken = default)
    {
        var capabilities = await _backend.ProbeAsync(cancellationToken);
        return new PlatformInfo_OutDto
        {
            Platform = _platform.Platform,
            OsDescription = _platform.Description,
            IsElevated = _privilege.IsElevated,
            SupportsWifiHotspot = capabilities.SupportsWifiHotspot,
            SupportsTransparentBalancing = capabilities.SupportsTransparentBalancing,
            NetworkManagerAvailable = capabilities.NetworkManagerAvailable,
            HasPendingRecovery = _backend.HasPendingRecovery,
            Notes = capabilities.Notes.ToList(),
        };
    }

    public async Task StartAsync(SharingRequest_InDto request, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_status.State is RunState.Running or RunState.Starting)
            {
                throw new SharingException("Un partage est déjà actif. Arrêtez-le avant d'en démarrer un autre.");
            }

            if (!_privilege.IsElevated)
            {
                throw new PrivilegeRequiredException();
            }

            Publish(new SharingStatus_OutDto { State = RunState.Starting, Message = "Préparation du partage…" });

            try
            {
                var adapters = await _inventory.GetAdaptersAsync(cancellationToken);
                var capabilities = await _backend.ProbeAsync(cancellationToken);
                var plan = _planner.Plan(request, adapters, capabilities);

                foreach (var warning in plan.Warnings)
                {
                    _logger.LogWarning("{Warning}", warning);
                }

                await _backend.StartAsync(plan, cancellationToken);

                Publish(new SharingStatus_OutDto
                {
                    State = RunState.Running,
                    Message = "Partage actif.",
                    Sources = plan.Sources.Select(s => s.Name).ToList(),
                    Targets = plan.Targets.Select(t => t.Adapter.Name).ToList(),
                    Warnings = plan.Warnings.ToList(),
                    HotspotSsid = plan.Hotspot?.Ssid,
                    StartedAt = DateTimeOffset.Now,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Échec du démarrage du partage");
                Publish(new SharingStatus_OutDto { State = RunState.Faulted, Message = ex.Message });
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_status.State == RunState.Stopped)
            {
                return;
            }

            Publish(new SharingStatus_OutDto { State = RunState.Stopping, Message = "Arrêt du partage…" });
            try
            {
                await _backend.StopAsync(cancellationToken);
                Publish(new SharingStatus_OutDto { State = RunState.Stopped, Message = "Partage arrêté." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Échec de l'arrêt du partage");
                Publish(new SharingStatus_OutDto { State = RunState.Faulted, Message = $"Arrêt incomplet : {ex.Message}" });
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RecoverAsync(CancellationToken cancellationToken = default)
    {
        if (!_privilege.IsElevated)
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await _backend.RecoverAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Publish(SharingStatus_OutDto status)
    {
        _status = status;
        StatusChanged?.Invoke(this, status);
    }
}
