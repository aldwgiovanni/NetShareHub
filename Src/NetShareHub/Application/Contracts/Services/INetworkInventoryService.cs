using NetShareHub.Domain.Entities;

namespace NetShareHub.Application.Contracts.Services;

/// <summary>Inventaire des adaptateurs réseau de la machine.</summary>
public interface INetworkInventoryService
{
    /// <summary>Dernier inventaire connu (lecture instantanée, sans I/O).</summary>
    IReadOnlyList<NetworkAdapter> Snapshot { get; }

    /// <summary>Relit les adaptateurs auprès du système.</summary>
    Task<IReadOnlyList<NetworkAdapter>> GetAdaptersAsync(CancellationToken cancellationToken = default);

    /// <summary>Levé (après temporisation) lorsqu'une adresse ou un état d'adaptateur change.</summary>
    event EventHandler? AdaptersChanged;
}
