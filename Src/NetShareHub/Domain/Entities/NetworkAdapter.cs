using System.Net;
using NetShareHub.Domain.Enums;
using NetShareHub.Domain.ValueObjects;

namespace NetShareHub.Domain.Entities;

/// <summary>Adaptateur réseau de la machine, tel qu'observé à un instant donné.</summary>
public sealed class NetworkAdapter
{
    /// <summary>Identifiant stable fourni par le système (GUID sous Windows, nom sous Linux).</summary>
    public required string Id { get; init; }

    /// <summary>Nom d'usage (« Wi-Fi », « Ethernet », « wlp3s0 »…).</summary>
    public required string Name { get; init; }

    public string Description { get; init; } = string.Empty;

    public AdapterKind Kind { get; init; }

    public bool IsUp { get; init; }

    public bool IsLoopback { get; init; }

    /// <summary>Vrai pour les adaptateurs logiciels (VMware, Hyper-V, Docker, VPN…).</summary>
    public bool IsVirtual { get; init; }

    public int InterfaceIndex { get; init; }

    public string? MacAddress { get; init; }

    public IPAddress? IPv4Address { get; init; }

    public IPAddress? IPv4Mask { get; init; }

    public IPAddress? Gateway { get; init; }

    public IReadOnlyList<IPAddress> DnsServers { get; init; } = Array.Empty<IPAddress>();

    public long SpeedBitsPerSecond { get; init; }

    /// <summary>Connexion active disposant d'une adresse IPv4 et d'une passerelle : candidate à servir de source Internet.</summary>
    public bool HasInternetRoute => IsUp && !IsLoopback && IPv4Address is not null && Gateway is not null;

    public Ipv4Subnet? Subnet
        => IPv4Address is not null && IPv4Mask is not null
            ? Ipv4Subnet.FromAddressAndMask(IPv4Address, IPv4Mask)
            : null;
}
