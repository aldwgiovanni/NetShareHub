using NetShareHub.Domain.Entities;
using NetShareHub.Domain.Enums;
using NetShareHub.Domain.ValueObjects;

namespace NetShareHub.Application.Contracts.Services;

/// <summary>Ce que le système hôte sait faire (déterminé par sondage, sans rien modifier).</summary>
public sealed record BackendCapabilities(
    HostPlatform Platform,
    bool SupportsWifiHotspot,
    bool NetworkManagerAvailable,
    bool SupportsTransparentBalancing,
    IReadOnlyList<string> Notes);

/// <summary>Cible d'un partage : un adaptateur, et le mécanisme retenu pour l'alimenter.</summary>
public sealed record ShareTargetPlan(NetworkAdapter Adapter, SharingMechanism Mechanism, Ipv4Subnet? Subnet);

/// <summary>Hotspot Wi-Fi validé.</summary>
public sealed record WifiHotspotPlan(string Ssid, string Password, WifiBand Band, NetworkAdapter? Adapter, SharingMechanism Mechanism);

/// <summary>Plan d'exécution validé par le <c>SharingPlanner</c>, consommé par le moteur du système hôte.</summary>
public sealed record SharingPlan(
    IReadOnlyList<NetworkAdapter> Sources,
    IReadOnlyDictionary<string, int> SourceWeights,
    IReadOnlyList<ShareTargetPlan> Targets,
    WifiHotspotPlan? Hotspot,
    bool BalanceSources,
    Ipv4Subnet? NatBlock,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Source principale (la plus rapide quand plusieurs sont fournies).</summary>
    public NetworkAdapter PrimarySource => Sources[0];
}

/// <summary>Moteur de partage propre à un système d'exploitation (Windows ou Linux).</summary>
public interface ISharingBackend
{
    /// <summary>Sonde les fonctions disponibles sans rien modifier.</summary>
    Task<BackendCapabilities> ProbeAsync(CancellationToken cancellationToken = default);

    /// <summary>Applique le plan. En cas d'échec, tout ce qui a été modifié est annulé avant de remonter l'exception.</summary>
    Task StartAsync(SharingPlan plan, CancellationToken cancellationToken = default);

    /// <summary>Annule toutes les modifications faites par <see cref="StartAsync"/>.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Annule les modifications laissées par une session précédente interrompue brutalement. Renvoie vrai si quelque chose a été annulé.</summary>
    Task<bool> RecoverAsync(CancellationToken cancellationToken = default);
}
