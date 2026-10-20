using NetShareHub.Domain.Dtos.Incoming;
using NetShareHub.Domain.Dtos.Outgoing;

namespace NetShareHub.Application.Contracts.Services;

/// <summary>Partage de connexion Internet (hotspot Wi-Fi, routeur filaire).</summary>
public interface IInternetSharingService
{
    SharingStatus_OutDto Status { get; }

    event EventHandler<SharingStatus_OutDto>? StatusChanged;

    Task<PlatformInfo_OutDto> GetPlatformInfoAsync(CancellationToken cancellationToken = default);

    Task StartAsync(SharingRequest_InDto request, CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Défait les modifications d'une session précédente interrompue (plantage, coupure de courant).</summary>
    Task<bool> RecoverAsync(CancellationToken cancellationToken = default);
}
