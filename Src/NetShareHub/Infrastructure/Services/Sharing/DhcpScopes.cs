using System.Net;
using NetShareHub.Application.Contracts.Services;
using NetShareHub.Domain.Entities;
using NetShareHub.Domain.Enums;
using NetShareHub.Infrastructure.Services.Dhcp;

namespace NetShareHub.Infrastructure.Services.Sharing;

internal static class DhcpScopes
{
    private static readonly IPAddress[] FallbackDns = { IPAddress.Parse("1.1.1.1"), IPAddress.Parse("8.8.8.8") };

    /// <summary>Un scope par cible gérée par le NAT intégré ; les DNS annoncés sont ceux de la source principale.</summary>
    public static List<DhcpScope> FromPlan(SharingPlan plan, Func<ShareTargetPlan, bool> isNatTarget)
    {
        var dns = DnsFor(plan.PrimarySource);
        return plan.Targets
            .Where(t => isNatTarget(t) && t.Subnet is not null)
            .Select(t => DhcpScope.ForSubnet(t.Subnet!.Value, t.Adapter.InterfaceIndex, t.Adapter.Name, dns))
            .ToList();
    }

    public static bool IsBuiltInNat(ShareTargetPlan target)
        => target.Mechanism is SharingMechanism.WindowsNat or SharingMechanism.LinuxNat;

    private static IReadOnlyList<IPAddress> DnsFor(NetworkAdapter source)
    {
        var dns = source.DnsServers
            .Where(d => !IPAddress.IsLoopback(d) && !d.Equals(IPAddress.Any))
            .Take(2)
            .ToList();
        return dns.Count > 0 ? dns : FallbackDns;
    }
}
