using NetShareHub.Domain.Enums;

namespace NetShareHub.Domain.Dtos.Outgoing;

/// <summary>Vue d'un adaptateur pour l'interface.</summary>
public sealed class NetworkAdapter_OutDto
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public AdapterKind Kind { get; set; }

    public bool IsUp { get; set; }

    public bool IsVirtual { get; set; }

    public string IPv4 { get; set; } = string.Empty;

    public string Gateway { get; set; } = string.Empty;

    public string Dns { get; set; } = string.Empty;

    public string Mac { get; set; } = string.Empty;

    public long SpeedBitsPerSecond { get; set; }

    /// <summary>Peut servir de source Internet (actif, IP + passerelle).</summary>
    public bool CanBeSource { get; set; }

    /// <summary>Peut recevoir une connexion partagée (adaptateur non Wi-Fi, non loopback).</summary>
    public bool CanBeTarget { get; set; }

    /// <summary>Peut émettre un hotspot (adaptateur Wi-Fi).</summary>
    public bool CanHostAccessPoint { get; set; }
}

/// <summary>Capacités de la machine hôte.</summary>
public sealed class PlatformInfo_OutDto
{
    public HostPlatform Platform { get; set; }

    public string OsDescription { get; set; } = string.Empty;

    public bool IsElevated { get; set; }

    public bool SupportsWifiHotspot { get; set; }

    /// <summary>Linux uniquement : le routage multi-chemins répartit aussi le trafic des appareils partagés.</summary>
    public bool SupportsTransparentBalancing { get; set; }

    public bool NetworkManagerAvailable { get; set; }

    /// <summary>Une session de partage interrompue a laissé des modifications réseau à annuler (droits requis).</summary>
    public bool HasPendingRecovery { get; set; }

    public List<string> Notes { get; set; } = new();
}

/// <summary>État du partage de connexion.</summary>
public sealed class SharingStatus_OutDto
{
    public RunState State { get; set; }

    public string Message { get; set; } = string.Empty;

    public List<string> Sources { get; set; } = new();

    public List<string> Targets { get; set; } = new();

    public List<string> Warnings { get; set; } = new();

    public string? HotspotSsid { get; set; }

    public DateTimeOffset? StartedAt { get; set; }
}

/// <summary>Statistiques d'une liaison montante.</summary>
public sealed class UplinkStats_OutDto
{
    public string AdapterId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string IPv4 { get; set; } = string.Empty;

    public UplinkHealth Health { get; set; }

    public bool Enabled { get; set; }

    public int Weight { get; set; }

    public int ActiveConnections { get; set; }

    public long TotalConnections { get; set; }

    public long FailedConnections { get; set; }

    public long BytesSent { get; set; }

    public long BytesReceived { get; set; }

    public double SendRateBytesPerSecond { get; set; }

    public double ReceiveRateBytesPerSecond { get; set; }

    public int? LatencyMs { get; set; }
}

/// <summary>État du proxy Dispatch.</summary>
public sealed class DispatchStatus_OutDto
{
    public RunState State { get; set; }

    public string Message { get; set; } = string.Empty;

    /// <summary>Adresse d'écoute (ex. « 0.0.0.0:1080 »).</summary>
    public string ListenEndpoint { get; set; } = string.Empty;

    /// <summary>Adresses à saisir côté appareils (ex. « 192.168.1.20:1080 »).</summary>
    public List<string> ClientEndpoints { get; set; } = new();

    public DispatchStrategy Strategy { get; set; }

    public int ActiveConnections { get; set; }

    public List<UplinkStats_OutDto> Uplinks { get; set; } = new();
}

/// <summary>Préférences enregistrées.</summary>
public sealed class AppSettings_OutDto
{
    public NetShareHub.Domain.Dtos.Incoming.SharingRequest_InDto Sharing { get; set; } = new();

    public bool HotspotEnabled { get; set; }

    public NetShareHub.Domain.Dtos.Incoming.DispatchConfig_InDto Dispatch { get; set; } = new();
}
