namespace NetShareHub.Domain.Enums;

/// <summary>Système d'exploitation hôte.</summary>
public enum HostPlatform
{
    Unsupported = 0,
    Windows = 1,
    Linux = 2,
}

/// <summary>Nature d'un adaptateur réseau.</summary>
public enum AdapterKind
{
    Other = 0,
    Ethernet = 1,
    WiFi = 2,
    Cellular = 3,
    Bluetooth = 4,
    Virtual = 5,
}

/// <summary>Algorithme de répartition des connexions entre les liaisons montantes.</summary>
public enum DispatchStrategy
{
    /// <summary>Répartition proportionnelle aux poids (algorithme « smooth weighted round-robin »).</summary>
    WeightedRoundRobin = 0,

    /// <summary>Chaque nouvelle connexion va vers la liaison la moins chargée (relativement à son poids).</summary>
    LeastConnections = 1,

    /// <summary>Seule la première liaison saine est utilisée ; les suivantes servent de secours.</summary>
    Failover = 2,
}

/// <summary>État de santé d'une liaison montante.</summary>
public enum UplinkHealth
{
    Unknown = 0,
    Healthy = 1,
    Down = 2,
}

/// <summary>État d'un service de longue durée (partage ou dispatch).</summary>
public enum RunState
{
    Stopped = 0,
    Starting = 1,
    Running = 2,
    Stopping = 3,
    Faulted = 4,
}

/// <summary>Bande de fréquence du hotspot Wi-Fi.</summary>
public enum WifiBand
{
    Auto = 0,
    Band2_4GHz = 1,
    Band5GHz = 2,
}

/// <summary>Mécanisme système utilisé pour partager vers une cible.</summary>
public enum SharingMechanism
{
    None = 0,
    WindowsIcs = 1,
    WindowsMobileHotspot = 2,
    WindowsNat = 3,
    LinuxNetworkManager = 4,
    LinuxNat = 5,
}
