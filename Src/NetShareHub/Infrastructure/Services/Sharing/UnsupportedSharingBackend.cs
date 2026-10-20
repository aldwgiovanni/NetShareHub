using NetShareHub.Application.Contracts.Services;
using NetShareHub.Domain.Enums;
using NetShareHub.Domain.Exceptions;

namespace NetShareHub.Infrastructure.Services.Sharing;

/// <summary>Moteur de repli pour les systèmes non gérés (macOS, BSD…) : l'application s'ouvre mais ne partage rien.</summary>
public sealed class UnsupportedSharingBackend : ISharingBackend
{
    public Task<BackendCapabilities> ProbeAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new BackendCapabilities(
            HostPlatform.Unsupported,
            false,
            false,
            false,
            new[] { "Le partage de connexion n'est disponible que sous Windows et Linux. Le Dispatch (proxy) fonctionne partout." }));

    public Task StartAsync(SharingPlan plan, CancellationToken cancellationToken = default)
        => throw new SharingException("Le partage de connexion n'est disponible que sous Windows et Linux.");

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<bool> RecoverAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
}
