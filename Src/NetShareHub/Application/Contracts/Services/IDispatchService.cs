using NetShareHub.Domain.Dtos.Incoming;
using NetShareHub.Domain.Dtos.Outgoing;

namespace NetShareHub.Application.Contracts.Services;

/// <summary>Proxy SOCKS5/HTTP qui répartit les connexions sortantes entre plusieurs liaisons Internet.</summary>
public interface IDispatchService
{
    /// <summary>Instantané de l'état courant (appel peu coûteux, destiné à un rafraîchissement périodique de l'interface).</summary>
    DispatchStatus_OutDto GetStatus();

    Task StartAsync(DispatchConfig_InDto config, CancellationToken cancellationToken = default);

    Task StopAsync();
}
