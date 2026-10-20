using System.Net;
using NetShareHub.Domain.ValueObjects;

namespace NetShareHub.Infrastructure.Services.Dhcp;

/// <summary>Plage d'adresses distribuée sur un adaptateur.</summary>
public sealed record DhcpScope(
    int InterfaceIndex,
    string InterfaceName,
    IPAddress ServerAddress,
    IPAddress SubnetMask,
    IPAddress BroadcastAddress,
    IPAddress RangeStart,
    IPAddress RangeEnd,
    IReadOnlyList<IPAddress> DnsServers,
    TimeSpan LeaseTime)
{
    /// <summary>Scope standard d'un /24 : passerelle en .1, baux de .100 à .250.</summary>
    public static DhcpScope ForSubnet(
        Ipv4Subnet subnet, int interfaceIndex, string interfaceName, IReadOnlyList<IPAddress> dns, TimeSpan? lease = null)
        => new(
            interfaceIndex,
            interfaceName,
            subnet.HostAddress(1),
            subnet.SubnetMask,
            subnet.BroadcastAddress,
            subnet.HostAddress(100),
            subnet.HostAddress(250),
            dns,
            lease ?? TimeSpan.FromHours(12));
}

/// <summary>Table des baux d'un scope (en mémoire : la durée de vie d'une session de partage).</summary>
public sealed class DhcpLeaseTable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Lease> _byMac = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _clock;
    private readonly uint _start;
    private readonly uint _end;

    public DhcpLeaseTable(DhcpScope scope, Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _start = Ipv4Subnet.ToUInt(scope.RangeStart);
        _end = Ipv4Subnet.ToUInt(scope.RangeEnd);
        Scope = scope;
    }

    public DhcpScope Scope { get; }

    public bool InRange(IPAddress address)
    {
        var value = Ipv4Subnet.ToUInt(address);
        return value >= _start && value <= _end;
    }

    /// <summary>Adresse à proposer à ce client : son bail existant, l'adresse demandée si libre, sinon la première libre.</summary>
    public IPAddress? Propose(string mac, IPAddress? requested)
    {
        lock (_gate)
        {
            Purge();
            if (_byMac.TryGetValue(mac, out var existing))
            {
                return existing.Address;
            }

            if (requested is not null && InRange(requested) && IsFree(requested, mac))
            {
                return requested;
            }

            for (var candidate = _start; candidate <= _end; candidate++)
            {
                var address = Ipv4Subnet.ToAddress(candidate);
                if (IsFree(address, mac))
                {
                    return address;
                }
            }

            return null;
        }
    }

    /// <summary>Confirme l'attribution. Renvoie faux si l'adresse n'est pas utilisable par ce client.</summary>
    public bool TryCommit(string mac, IPAddress address)
    {
        lock (_gate)
        {
            Purge();
            if (!InRange(address) || !IsFree(address, mac))
            {
                return false;
            }

            _byMac[mac] = new Lease(address, _clock() + Scope.LeaseTime);
            return true;
        }
    }

    public void Release(string mac)
    {
        lock (_gate)
        {
            _byMac.Remove(mac);
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                Purge();
                return _byMac.Count;
            }
        }
    }

    private bool IsFree(IPAddress address, string mac)
        => !_byMac.Any(kv => kv.Value.Address.Equals(address) && !kv.Key.Equals(mac, StringComparison.OrdinalIgnoreCase));

    private void Purge()
    {
        var now = _clock();
        foreach (var mac in _byMac.Where(kv => kv.Value.Expires <= now).Select(kv => kv.Key).ToList())
        {
            _byMac.Remove(mac);
        }
    }

    private sealed record Lease(IPAddress Address, DateTimeOffset Expires);
}

/// <summary>Logique du protocole DHCP, sans aucune E/S : transforme une requête en réponse.</summary>
public static class DhcpEngine
{
    /// <summary>Renvoie la réponse à émettre, ou <c>null</c> s'il n'y a rien à répondre.</summary>
    public static DhcpPacket? Handle(DhcpPacket request, DhcpLeaseTable table)
    {
        if (request.Op != 1 || request.HardwareLength is 0 or > 16)
        {
            return null;
        }

        var scope = table.Scope;
        var mac = request.MacAddress;

        switch (request.MessageType)
        {
            case DhcpMessageType.Discover:
            {
                var offer = table.Propose(mac, request.RequestedIp);
                return offer is null ? null : Reply(request, scope, DhcpMessageType.Offer, offer);
            }

            case DhcpMessageType.Request:
            {
                // Le client a choisi un autre serveur DHCP : on libère notre proposition éventuelle et on se tait.
                if (request.ServerIdentifier is { } chosen && !chosen.Equals(scope.ServerAddress))
                {
                    table.Release(mac);
                    return null;
                }

                var wanted = request.RequestedIp ?? (request.ClientAddress.Equals(IPAddress.Any) ? null : request.ClientAddress);
                if (wanted is null)
                {
                    return null;
                }

                return table.TryCommit(mac, wanted)
                    ? Reply(request, scope, DhcpMessageType.Ack, wanted)
                    : Reply(request, scope, DhcpMessageType.Nak, IPAddress.Any);
            }

            case DhcpMessageType.Release:
            case DhcpMessageType.Decline:
                table.Release(mac);
                return null;

            default:
                return null;
        }
    }

    private static DhcpPacket Reply(DhcpPacket request, DhcpScope scope, DhcpMessageType type, IPAddress yourAddress)
    {
        var reply = new DhcpPacket
        {
            Op = 2,
            HardwareType = request.HardwareType,
            HardwareLength = request.HardwareLength,
            TransactionId = request.TransactionId,
            Flags = request.Flags,
            YourAddress = yourAddress,
            ServerAddress = type == DhcpMessageType.Nak ? IPAddress.Any : scope.ServerAddress,
            GatewayAddress = request.GatewayAddress,
            ClientHardwareAddress = request.ClientHardwareAddress,
        };

        reply.SetOption(DhcpOption.MessageType, (byte)type);
        reply.SetAddressOption(DhcpOption.ServerId, scope.ServerAddress);

        if (type != DhcpMessageType.Nak)
        {
            var seconds = (uint)scope.LeaseTime.TotalSeconds;
            reply.SetUIntOption(DhcpOption.LeaseTime, seconds);
            reply.SetUIntOption(DhcpOption.RenewalTime, seconds / 2);
            reply.SetUIntOption(DhcpOption.RebindingTime, seconds / 8 * 7);
            reply.SetAddressOption(DhcpOption.SubnetMask, scope.SubnetMask);
            reply.SetAddressOption(DhcpOption.Router, scope.ServerAddress);
            reply.SetAddressOption(DhcpOption.BroadcastAddress, scope.BroadcastAddress);
            if (scope.DnsServers.Count > 0)
            {
                reply.SetAddressOption(DhcpOption.DnsServers, scope.DnsServers.ToArray());
            }
        }

        return reply;
    }
}
