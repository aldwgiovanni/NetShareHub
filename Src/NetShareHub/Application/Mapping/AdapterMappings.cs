using NetShareHub.Domain.Dtos.Outgoing;
using NetShareHub.Domain.Entities;
using NetShareHub.Domain.Enums;

namespace NetShareHub.Application.Mapping;

/// <summary>
/// Conversions entité → DTO sortant. Écrites à la main : le projet n'a pas de modèle de données relationnel
/// et les mappings sont triviaux (voir CLAUDE.md à la racine pour l'écart avec codex.md).
/// </summary>
public static class AdapterMappings
{
    public static NetworkAdapter_OutDto ToOutDto(this NetworkAdapter adapter) => new()
    {
        Id = adapter.Id,
        Name = adapter.Name,
        Description = adapter.Description,
        Kind = adapter.Kind,
        IsUp = adapter.IsUp,
        IsVirtual = adapter.IsVirtual,
        IPv4 = adapter.IPv4Address?.ToString() ?? string.Empty,
        Gateway = adapter.Gateway?.ToString() ?? string.Empty,
        Dns = string.Join(", ", adapter.DnsServers),
        Mac = adapter.MacAddress ?? string.Empty,
        SpeedBitsPerSecond = adapter.SpeedBitsPerSecond,
        CanBeSource = adapter.HasInternetRoute,
        CanBeTarget = !adapter.IsLoopback && adapter.Kind is AdapterKind.Ethernet or AdapterKind.Other or AdapterKind.Virtual,
        CanHostAccessPoint = !adapter.IsLoopback && adapter.Kind == AdapterKind.WiFi,
    };
}
