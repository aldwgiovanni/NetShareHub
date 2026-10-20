using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using NetShareHub.Application.Contracts.Services;
using NetShareHub.Domain.Entities;
using NetShareHub.Domain.Enums;

namespace NetShareHub.Infrastructure.Services;

/// <summary>Inventaire des adaptateurs via <see cref="NetworkInterface"/> (identique sous Windows et Linux).</summary>
public sealed class NetworkInventoryService : INetworkInventoryService, IDisposable
{
    private static readonly string[] VirtualMarkers =
    {
        "vmware", "virtualbox", "vbox", "hyper-v", "vethernet", "docker", "veth", "virbr", "tap-windows", "tailscale",
        "zerotier", "wireguard", "openvpn", "wsl", "loopback", "pseudo", "teredo", "isatap", "6to4", "npcap", "vpn", "tun", "br-",
        "wi-fi direct", "kernel debugger", "débogueur du noyau",
    };

    private readonly ILogger<NetworkInventoryService> _logger;
    private readonly Timer _debounce;
    private IReadOnlyList<NetworkAdapter> _snapshot = Array.Empty<NetworkAdapter>();

    public NetworkInventoryService(ILogger<NetworkInventoryService> logger)
    {
        _logger = logger;
        _debounce = new Timer(_ => Raise(), null, Timeout.Infinite, Timeout.Infinite);
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        _snapshot = Read();
    }

    public IReadOnlyList<NetworkAdapter> Snapshot => _snapshot;

    public event EventHandler? AdaptersChanged;

    public Task<IReadOnlyList<NetworkAdapter>> GetAdaptersAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => _snapshot = Read(), cancellationToken);

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        _debounce.Dispose();
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => _debounce.Change(1000, Timeout.Infinite);

    private void Raise()
    {
        try
        {
            _snapshot = Read();
            AdaptersChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Relecture des adaptateurs impossible");
        }
    }

    private IReadOnlyList<NetworkAdapter> Read()
    {
        var result = new List<NetworkAdapter>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            // Windows énumère aussi les « liaisons de filtrage » de chaque carte (QoS Packet Scheduler, WFP, filtres
            // Wi-Fi, extensions de commutateur Hyper-V…). Elles n'ont pas de pile IPv4 et n'ont aucun intérêt ici.
            if (OperatingSystem.IsWindows() && !nic.Supports(NetworkInterfaceComponent.IPv4))
            {
                continue;
            }

            try
            {
                result.Add(Map(nic));
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException)
            {
                _logger.LogDebug("Adaptateur ignoré ({Name}) : {Message}", nic.Name, ex.Message);
            }
        }

        return result
            .OrderByDescending(a => a.HasInternetRoute)
            .ThenByDescending(a => a.IsUp)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static NetworkAdapter Map(NetworkInterface nic)
    {
        var properties = nic.GetIPProperties();

        var unicast = properties.UnicastAddresses
            .FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork && !IsLinkLocal(u.Address));
        var gateway = properties.GatewayAddresses
            .Select(g => g.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
        var dns = properties.DnsAddresses
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .ToList();

        var index = 0;
        try
        {
            index = properties.GetIPv4Properties().Index;
        }
        catch (NetworkInformationException)
        {
            // Pas de pile IPv4 sur cet adaptateur.
        }

        long speed = 0;
        try
        {
            speed = nic.Speed;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NetworkInformationException)
        {
            // Vitesse non exposée (Wi-Fi sous Linux, par exemple).
        }

        var haystack = $"{nic.Name} {nic.Description}".ToLowerInvariant();
        var isLoopback = nic.NetworkInterfaceType == NetworkInterfaceType.Loopback;
        var isVirtual = !isLoopback && (VirtualMarkers.Any(haystack.Contains) || IsLinuxVirtual(nic.Name));

        var mac = nic.GetPhysicalAddress().ToString();
        return new NetworkAdapter
        {
            Id = nic.Id,
            Name = nic.Name,
            Description = nic.Description,
            Kind = KindOf(nic, haystack),
            IsUp = nic.OperationalStatus == OperationalStatus.Up,
            IsLoopback = isLoopback,
            IsVirtual = isVirtual,
            InterfaceIndex = index,
            MacAddress = mac.Length == 12 ? string.Join(':', Enumerable.Range(0, 6).Select(i => mac.Substring(i * 2, 2))) : null,
            IPv4Address = unicast?.Address,
            IPv4Mask = unicast?.IPv4Mask,
            Gateway = gateway,
            DnsServers = dns,
            SpeedBitsPerSecond = speed > 0 ? speed : 0,
        };
    }

    private static AdapterKind KindOf(NetworkInterface nic, string haystack)
    {
        if (haystack.Contains("bluetooth", StringComparison.Ordinal))
        {
            return AdapterKind.Bluetooth;
        }

        return nic.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Wireless80211 => AdapterKind.WiFi,
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
                or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => AdapterKind.Ethernet,
            NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 or NetworkInterfaceType.Ppp => AdapterKind.Cellular,
            _ => AdapterKind.Other,
        };
    }

    private static bool IsLinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }

    // Sous Linux, tout ce qui vit dans /sys/devices/virtual/net est un adaptateur logiciel.
    private static bool IsLinuxVirtual(string name)
        => OperatingSystem.IsLinux() && Directory.Exists($"/sys/devices/virtual/net/{name}");
}
