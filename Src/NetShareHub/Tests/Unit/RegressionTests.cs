using System.Net;
using System.Net.Sockets;
using NetShareHub.Application.Contracts.Services;
using NetShareHub.Application.Services;
using NetShareHub.Domain.Dtos.Incoming;
using NetShareHub.Domain.Enums;
using NetShareHub.Domain.Exceptions;
using NetShareHub.Domain.ValueObjects;
using NetShareHub.Infrastructure.Repositories;
using NetShareHub.Infrastructure.Services;
using NetShareHub.Infrastructure.Services.Dhcp;
using NetShareHub.Infrastructure.Services.Dispatch;
using NetShareHub.Infrastructure.Services.Sharing;

namespace NetShareHub.Tests.Unit;

/// <summary>Défauts relevés lors des tests utilisateur : préférences, moteurs de partage, DHCP, élévation.</summary>
public class SettingsRegressionTests
{
    [Fact]
    public async Task Des_preferences_partielles_ou_hors_plage_sont_ramenees_a_des_valeurs_sures()
    {
        using var dir = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(dir.Path, "settings.json"), """
            {
              "Sharing": { "SourceAdapterIds": null, "TargetAdapterIds": ["a", null, "a"], "SourceWeights": null,
                           "Hotspot": { "Ssid": null, "Password": null, "Band": 9 } },
              "HotspotEnabled": true,
              "Dispatch": { "Strategy": 7, "Port": 70000, "StickyMinutes": 0, "Uplinks": [ null, { "AdapterId": null }, { "AdapterId": "b", "Weight": 2 } ] }
            }
            """);

        var settings = await new JsonAppSettingsRepository(Logs.For<JsonAppSettingsRepository>(), dir.Path).GetAsync();

        Assert.Empty(settings.Sharing.SourceAdapterIds);
        Assert.Equal(new[] { "a" }, settings.Sharing.TargetAdapterIds);
        Assert.Empty(settings.Sharing.SourceWeights);
        Assert.Equal("NetShareHub", settings.Sharing.Hotspot!.Ssid);
        Assert.Equal(string.Empty, settings.Sharing.Hotspot.Password);
        Assert.Equal(WifiBand.Auto, settings.Sharing.Hotspot.Band);
        Assert.True(settings.HotspotEnabled);
        Assert.Equal(DispatchStrategy.WeightedRoundRobin, settings.Dispatch.Strategy);
        Assert.Equal(1080, settings.Dispatch.Port);
        Assert.Equal(10, settings.Dispatch.StickyMinutes);
        Assert.Equal("b", Assert.Single(settings.Dispatch.Uplinks).AdapterId);
    }

    /// <summary>Contexte d'interface bloqué (fermeture : le thread attend l'enregistrement) : rien n'y est jamais exécuté.</summary>
    private sealed class BlockedContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
        }
    }

    [Fact]
    public async Task Un_enregistrement_synchrone_n_attend_pas_un_enregistrement_lance_depuis_le_thread_d_interface()
    {
        using var dir = new TempDirectory();
        var repository = new JsonAppSettingsRepository(Logs.For<JsonAppSettingsRepository>(), dir.Path);

        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new BlockedContext());
        Task deferred;
        try
        {
            deferred = repository.SaveAsync(new AppSettings_InDto { Dispatch = { Port = 1111 } });
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        var closing = Task.Run(() => repository.SaveAsync(new AppSettings_InDto { Dispatch = { Port = 2222 } }));

        await closing.WaitAsync(TimeSpan.FromSeconds(10)); // TimeoutException = interblocage : le verrou est resté pris
        await deferred.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2222, (await repository.GetAsync()).Dispatch.Port);
    }
}

public class SharingRegressionTests
{
    private static readonly NetShareHub.Domain.Entities.NetworkAdapter Wifi = TestAdapters.Make(
        "{B85E7A63-105B-4FD0-A5AE-B2EBD796EE5B}", "Wi-Fi", AdapterKind.WiFi, ip: "192.168.1.20", gateway: "192.168.1.1", index: 12);

    [Fact]
    public async Task Windows_un_point_d_acces_mobile_deja_actif_n_est_pas_coupe_par_le_retour_arriere()
    {
        using var dir = new TempDirectory();
        var runner = new RecordingRunner(spec => spec.Description switch
        {
            "Sonde du Point d'accès mobile" => new CommandResult(0, "CAP:Enabled\nSTATE:On", ""),
            "État du Point d'accès mobile" => new CommandResult(0, "STATE:On", ""),
            _ => new CommandResult(0, "OK", ""),
        });
        var backend = new WindowsSharingBackend(runner, new FakeInventory(Wifi), new FakeDhcpFactory(), Logs.For<WindowsSharingBackend>(), dir.Path);
        var plan = new SharingPlanner().Plan(
            new SharingRequest_InDto { SourceAdapterIds = { Wifi.Id }, Hotspot = new WifiHotspot_InDto { Ssid = "Maison", Password = "motdepasse1" } },
            new[] { Wifi },
            await backend.ProbeAsync());

        var ex = await Assert.ThrowsAsync<SharingException>(() => backend.StartAsync(plan));

        Assert.Contains("déjà actif", ex.Message);
        Assert.DoesNotContain(runner.Commands, c => c.Description == "Arrêt du Point d'accès mobile");
        Assert.DoesNotContain(runner.Commands, c => c.Description == "Démarrage du Point d'accès mobile");
        Assert.False(backend.HasPendingRecovery);
    }

    [Fact]
    public void Windows_les_reglages_d_interface_ne_touchent_que_l_IPv4()
    {
        var state = new InterfaceState(7, false, true, false, 30, new() { new("192.168.50.2", 24) }, "192.168.50.1");
        var scripts = new[]
        {
            WindowsScripts.ConfigureTarget(7, IPAddress.Parse("10.77.0.1"), 24),
            WindowsScripts.RestoreInterface(state, "Ethernet"),
            WindowsScripts.RestoreInterface(state with { Dhcp = true, Addrs = new() }, "Ethernet"),
        };

        var lines = scripts.SelectMany(s => s.Split('\n')).Where(l => l.Contains("Set-NetIPInterface")).ToList();
        Assert.NotEmpty(lines);
        Assert.All(lines, l => Assert.Contains("-AddressFamily IPv4", l));
    }

    [Fact]
    public void Le_bloc_NAT_sort_de_10_0_0_0_8_quand_la_source_y_est_deja()
    {
        var corporate = Ipv4Subnet.Parse("10.0.0.0/8");

        var block = SubnetAllocator.AllocateBlock(2, new[] { corporate });
        Assert.StartsWith("172.16.", block.ToString());

        var both = SubnetAllocator.AllocateBlock(1, new[] { corporate, Ipv4Subnet.Parse("172.16.0.0/12"), Ipv4Subnet.Parse("192.168.0.0/24") });
        Assert.StartsWith("192.168.", both.ToString());
        Assert.False(both.Overlaps(Ipv4Subnet.Parse("192.168.137.0/24"))); // réservé au partage ICS de Windows
    }

    [Fact]
    public void Linux_quitter_pkexec_signifie_que_l_autorisation_a_ete_accordee()
    {
        Assert.False(PrivilegeService.HasLeftPkexec("pkexec\n"));
        Assert.False(PrivilegeService.HasLeftPkexec(string.Empty));
        Assert.True(PrivilegeService.HasLeftPkexec("netsharehub-ele\n"));
        Assert.True(PrivilegeService.HasLeftPkexec("env\n"));
    }
}

public class DhcpRegressionTests
{
    private static readonly DhcpScope Scope = DhcpScope.ForSubnet(Ipv4Subnet.Parse("10.77.0.0/24"), 7, "eth1", new[] { IPAddress.Parse("1.1.1.1") });

    private static DhcpPacket Client(DhcpMessageType type, IPAddress? requested = null, IPAddress? server = null)
    {
        var mac = new byte[16];
        new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0x01 }.CopyTo(mac, 0);
        var packet = new DhcpPacket { Op = 1, TransactionId = 42, ClientHardwareAddress = mac };
        packet.SetOption(DhcpOption.MessageType, (byte)type);
        if (requested is not null)
        {
            packet.SetAddressOption(DhcpOption.RequestedIp, requested);
        }

        if (server is not null)
        {
            packet.SetAddressOption(DhcpOption.ServerId, server);
        }

        return packet;
    }

    [Fact]
    public void Une_adresse_refusee_par_le_client_n_est_plus_proposee_pendant_la_quarantaine()
    {
        var now = DateTimeOffset.UtcNow;
        var table = new DhcpLeaseTable(Scope, () => now);
        var first = DhcpEngine.Handle(Client(DhcpMessageType.Discover), table)!.YourAddress;
        DhcpEngine.Handle(Client(DhcpMessageType.Request, first, Scope.ServerAddress), table);

        // Le client a détecté un conflit (ARP) sur cette adresse.
        Assert.Null(DhcpEngine.Handle(Client(DhcpMessageType.Decline, first, Scope.ServerAddress), table));

        var second = DhcpEngine.Handle(Client(DhcpMessageType.Discover), table)!.YourAddress;
        Assert.NotEqual(first, second);
        Assert.Equal(DhcpMessageType.Nak, DhcpEngine.Handle(Client(DhcpMessageType.Request, first, Scope.ServerAddress), table)!.MessageType);

        now += DhcpLeaseTable.QuarantineTime + TimeSpan.FromSeconds(1);
        Assert.True(table.TryCommit("aa:bb:cc:dd:ee:02", first));
    }

    [Fact]
    public void Un_refus_adresse_a_un_autre_serveur_dhcp_est_ignore()
    {
        var table = new DhcpLeaseTable(Scope);
        var offered = DhcpEngine.Handle(Client(DhcpMessageType.Discover), table)!.YourAddress;
        DhcpEngine.Handle(Client(DhcpMessageType.Request, offered, Scope.ServerAddress), table);

        DhcpEngine.Handle(Client(DhcpMessageType.Decline, offered, IPAddress.Parse("10.77.0.254")), table);

        Assert.Equal(offered, DhcpEngine.Handle(Client(DhcpMessageType.Discover), table)!.YourAddress);
    }
}

public class DispatchFailureAttributionTests
{
    [Theory]
    [InlineData(SocketError.TimedOut, false)]
    [InlineData(SocketError.HostUnreachable, false)]
    [InlineData(SocketError.AddressNotAvailable, false)]
    [InlineData(SocketError.NetworkUnreachable, true)]
    [InlineData(SocketError.NetworkDown, true)]
    public void Seules_les_erreurs_de_la_liaison_la_rapprochent_de_hors_service(SocketError error, bool uplinkFault)
        => Assert.Equal(uplinkFault, DispatchProxyService.IsUplinkFailure(new SocketException((int)error)));
}
