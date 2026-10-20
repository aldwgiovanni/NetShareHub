using System.ComponentModel.DataAnnotations;
using NetShareHub.Domain.Enums;

namespace NetShareHub.Domain.Dtos.Incoming;

/// <summary>Paramètres du hotspot Wi-Fi à créer.</summary>
public sealed class WifiHotspot_InDto
{
    [Required, StringLength(32, MinimumLength = 1)]
    public string Ssid { get; set; } = "NetShareHub";

    [Required, StringLength(63, MinimumLength = 8)]
    public string Password { get; set; } = string.Empty;

    public WifiBand Band { get; set; } = WifiBand.Auto;

    /// <summary>Adaptateur Wi-Fi qui émettra le hotspot (obligatoire sous Linux ; ignoré sous Windows).</summary>
    public string? AdapterId { get; set; }
}

/// <summary>Demande de partage : quelles connexions partager, et vers quels adaptateurs.</summary>
public sealed class SharingRequest_InDto
{
    /// <summary>Connexions Internet à partager (sources). Plusieurs sources = agrégation si la plateforme le permet.</summary>
    public List<string> SourceAdapterIds { get; set; } = new();

    /// <summary>Adaptateurs filaires/virtuels qui reçoivent la connexion (DHCP + NAT fournis).</summary>
    public List<string> TargetAdapterIds { get; set; } = new();

    /// <summary>Hotspot Wi-Fi à créer en plus (ou à la place) des adaptateurs cibles.</summary>
    public WifiHotspot_InDto? Hotspot { get; set; }

    /// <summary>Répartit aussi le trafic des appareils connectés entre toutes les sources (Linux : routage multi-chemins).</summary>
    public bool BalanceSources { get; set; } = true;

    /// <summary>Poids de chaque source pour la répartition (clé = identifiant d'adaptateur).</summary>
    public Dictionary<string, int> SourceWeights { get; set; } = new();
}

/// <summary>Liaison montante utilisée par le Dispatch.</summary>
public sealed class DispatchUplink_InDto
{
    [Required]
    public string AdapterId { get; set; } = string.Empty;

    [Range(1, 100)]
    public int Weight { get; set; } = 1;

    public bool Enabled { get; set; } = true;
}

/// <summary>Configuration du proxy Dispatch (SOCKS5 + HTTP sur un même port).</summary>
public sealed class DispatchConfig_InDto
{
    /// <summary>Vrai : écoute sur toutes les interfaces (partage au réseau local). Faux : 127.0.0.1 uniquement.</summary>
    public bool AllowLan { get; set; }

    [Range(1, 65535)]
    public int Port { get; set; } = 1080;

    public DispatchStrategy Strategy { get; set; } = DispatchStrategy.WeightedRoundRobin;

    public List<DispatchUplink_InDto> Uplinks { get; set; } = new();

    /// <summary>Une même destination reste sur la même liaison pendant <see cref="StickyMinutes"/> (évite les déconnexions de sessions web).</summary>
    public bool StickyDestinations { get; set; } = true;

    [Range(1, 240)]
    public int StickyMinutes { get; set; } = 10;

    /// <summary>Sonde périodiquement chaque liaison pour détecter les coupures.</summary>
    public bool HealthProbe { get; set; } = true;

    public string? Username { get; set; }

    public string? Password { get; set; }
}

/// <summary>Préférences enregistrées entre deux lancements.</summary>
public sealed class AppSettings_InDto
{
    public SharingRequest_InDto Sharing { get; set; } = new();

    public bool HotspotEnabled { get; set; }

    public DispatchConfig_InDto Dispatch { get; set; } = new();
}
