using System.Diagnostics;
using System.Text;
using NetShareHub.Application.Contracts.Services;
using NetShareHub.Application.Services;
using NetShareHub.Domain.Dtos.Incoming;
using NetShareHub.Domain.Entities;
using NetShareHub.Domain.Enums;
using NetShareHub.Domain.Exceptions;
using NetShareHub.Infrastructure.Services.Dhcp;
using NetShareHub.Infrastructure.Services.Sharing;

namespace NetShareHub.Tests.Unit;

public class UndoJournalTests
{
    [Fact]
    public async Task Les_annulations_s_executent_en_ordre_inverse_et_le_journal_se_vide()
    {
        using var dir = new TempDirectory();
        var runner = new RecordingRunner();
        var journal = new UndoJournal(Path.Combine(dir.Path, "j.json"), runner, Logs.For<UndoJournalTests>(), enforceSecurity: false);

        journal.Push(UndoStep.LinuxConnectDevice("first"));
        journal.Push(UndoStep.LinuxConnectDevice("second"));
        journal.Push(UndoStep.LinuxConnectDevice("third"));
        await journal.UndoAllAsync();

        Assert.Equal(new[] { "third", "second", "first" }, runner.Commands.Select(c => c.Arguments[^1]));
        Assert.False(journal.HasPending);
        Assert.False(File.Exists(Path.Combine(dir.Path, "j.json")));
    }

    [Fact]
    public async Task Le_journal_survit_a_un_redemarrage_de_l_application()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "j.json");
        var before = new UndoJournal(path, new RecordingRunner(), Logs.For<UndoJournalTests>(), enforceSecurity: false);
        before.Push(UndoStep.LinuxDeleteRouteOverride());
        before.Push(UndoStep.WindowsSetForwarding(5, false, "Ethernet"));

        // « Plantage » : un nouveau journal relit le fichier.
        var runner = new RecordingRunner();
        var after = new UndoJournal(path, runner, Logs.For<UndoJournalTests>(), enforceSecurity: false);
        Assert.True(after.HasPending);
        await after.UndoAllAsync();

        Assert.Equal(2, runner.Commands.Count);
        Assert.Equal("powershell.exe", runner.Commands[0].FileName);
        Assert.Equal(WindowsScripts.SetForwarding(5, false), runner.Commands[0].Script);
        Assert.Equal("Restauration du routage sur Ethernet", runner.Commands[0].Description);
        Assert.Equal("ip", runner.Commands[1].FileName);
        Assert.Equal(new[] { "-4", "route", "del", "default", "metric", "10" }, runner.Commands[1].Arguments);
    }

    [Fact]
    public async Task Une_annulation_en_echec_n_interrompt_pas_les_suivantes()
    {
        using var dir = new TempDirectory();
        var runner = new RecordingRunner(spec => spec.Arguments[^1] == "boom" ? new CommandResult(1, "", "erreur") : new CommandResult(0, "", ""));
        var journal = new UndoJournal(Path.Combine(dir.Path, "j.json"), runner, Logs.For<UndoJournalTests>(), enforceSecurity: false);
        journal.Push(UndoStep.LinuxConnectDevice("last-to-run"));
        journal.Push(UndoStep.LinuxConnectDevice("boom"));

        await journal.UndoAllAsync();

        Assert.Equal(new[] { "boom", "last-to-run" }, runner.Commands.Select(c => c.Arguments[^1]));
        Assert.False(journal.HasPending);
    }
}

public class LinuxSharingBackendTests
{
    private static readonly NetworkAdapter Wifi = TestAdapters.Make("wlan0", "wlan0", AdapterKind.WiFi, ip: "192.168.1.20", gateway: "192.168.1.1");
    private static readonly NetworkAdapter Eth = TestAdapters.Make("eth0", "eth0", ip: "10.0.0.5", gateway: "10.0.0.1");
    private static readonly NetworkAdapter Lan = TestAdapters.Make("eth1", "eth1", ip: null, gateway: null);
    private static readonly NetworkAdapter Lan2 = TestAdapters.Make("eth2", "eth2", ip: null, gateway: null);
    private static readonly NetworkAdapter Wifi2 = TestAdapters.Make("wlan1", "wlan1", AdapterKind.WiFi, ip: null, gateway: null);

    private static (LinuxSharingBackend Backend, RecordingRunner Runner, TempDirectory Dir) Create(
        Func<CommandSpec, CommandResult>? responder = null, params NetworkAdapter[] adapters)
    {
        var (backend, runner, dir, _) = CreateWithDhcp(responder, adapters);
        return (backend, runner, dir);
    }

    private static (LinuxSharingBackend Backend, RecordingRunner Runner, TempDirectory Dir, FakeDhcpFactory Dhcp) CreateWithDhcp(
        Func<CommandSpec, CommandResult>? responder, params NetworkAdapter[] adapters)
    {
        var dir = new TempDirectory();
        var runner = new RecordingRunner(responder ?? DefaultResponder);
        var dhcp = new FakeDhcpFactory();
        var backend = new LinuxSharingBackend(runner, new FakeInventory(adapters), dhcp, Logs.For<LinuxSharingBackend>(), dir.Path);
        return (backend, runner, dir, dhcp);
    }

    private static CommandResult DefaultResponder(CommandSpec spec)
    {
        if (spec.FileName == "nmcli" && spec.Arguments.SequenceEqual(new[] { "-t", "-f", "RUNNING", "general" }))
        {
            return new CommandResult(0, "running", "");
        }

        if (spec.FileName == "sysctl" && spec.Arguments[0] == "-n")
        {
            return new CommandResult(0, "0", "");
        }

        if (spec.FileName == "ip" && spec.Arguments.SequenceEqual(new[] { "-4", "route", "get", "1.1.1.1" }))
        {
            return new CommandResult(0, "1.1.1.1 via 192.168.1.1 dev wlan0 src 192.168.1.20 uid 0", "");
        }

        if (spec.FileName == "iw")
        {
            return new CommandResult(0, "Wiphy phy0\n\tSupported interface modes:\n\t\t * IBSS\n\t\t * managed\n\t\t * AP\n", "");
        }

        return new CommandResult(0, "", "");
    }

    private static SharingPlan Plan(SharingRequest_InDto request, BackendCapabilities caps, params NetworkAdapter[] adapters)
        => new SharingPlanner().Plan(request, adapters, caps);

    [Fact]
    public async Task Probe_detecte_NetworkManager_et_le_mode_AP()
    {
        var (backend, _, dir) = Create(null, Wifi, Wifi2);
        using var scope = dir;

        var caps = await backend.ProbeAsync();

        Assert.True(caps.NetworkManagerAvailable);
        Assert.True(caps.SupportsWifiHotspot);
        Assert.True(caps.SupportsTransparentBalancing);
    }

    [Fact]
    public async Task Probe_sans_NetworkManager_desactive_le_hotspot()
    {
        var (backend, _, dir) = Create(spec => spec.FileName == "nmcli" ? new CommandResult(127, "", "not found") : new CommandResult(0, "", ""), Wifi);
        using var scope = dir;

        var caps = await backend.ProbeAsync();

        Assert.False(caps.NetworkManagerAvailable);
        Assert.False(caps.SupportsWifiHotspot);
        Assert.Contains(caps.Notes, n => n.Contains("NetworkManager"));
    }

    [Theory]
    [InlineData("Supported interface modes:\n * IBSS\n * managed\n * AP\n * monitor", true)]
    [InlineData("Supported interface modes:\n * IBSS\n * managed\n * monitor", false)]
    public void Detection_du_mode_point_d_acces(string output, bool expected)
        => Assert.Equal(expected, LinuxSharingBackend.SupportsAccessPoint(output));

    [Fact]
    public async Task Partage_NetworkManager_cree_puis_active_le_profil_et_defait_tout_a_l_arret()
    {
        var (backend, runner, dir) = Create(null, Wifi, Lan);
        using var scope = dir;
        var caps = await backend.ProbeAsync();
        var plan = Plan(new SharingRequest_InDto { SourceAdapterIds = { "wlan0" }, TargetAdapterIds = { "eth1" } }, caps, Wifi, Lan);
        runner.Commands.Clear();

        await backend.StartAsync(plan);

        var lines = runner.Commands.Select(c => c.Display).ToList();
        Assert.Contains(lines, l => l.StartsWith("nmcli connection add type ethernet ifname eth1 con-name NetShareHub-eth1 ipv4.method shared"));
        Assert.Contains(lines, l => l == "nmcli connection up id NetShareHub-eth1 ifname eth1");
        Assert.Contains(lines, l => l == "sysctl -w net.ipv4.ip_forward=1");
        // Wi-Fi est déjà la route par défaut du système : aucune route forcée.
        Assert.DoesNotContain(lines, l => l.Contains("route add default"));

        runner.Commands.Clear();
        await backend.StopAsync();

        var undo = runner.Commands.Select(c => c.Display).ToList();
        Assert.Equal("nmcli connection delete id NetShareHub-eth1", undo[0]);
        Assert.Equal("nmcli device connect eth1", undo[1]);
        Assert.Equal("sysctl -w net.ipv4.ip_forward=0", undo[^1]);
    }

    [Fact]
    public async Task Source_differente_de_la_route_par_defaut_ajoute_puis_retire_une_route_prioritaire()
    {
        var (backend, runner, dir) = Create(null, Wifi, Eth, Lan);
        using var scope = dir;
        var caps = await backend.ProbeAsync();
        // Le système sort par wlan0, l'utilisateur veut partager eth0.
        var plan = Plan(new SharingRequest_InDto { SourceAdapterIds = { "eth0" }, TargetAdapterIds = { "eth1" } }, caps, Wifi, Eth, Lan);
        runner.Commands.Clear();

        await backend.StartAsync(plan);
        Assert.Contains(runner.Commands, c => c.Display == "ip -4 route add default via 10.0.0.1 dev eth0 metric 10");

        runner.Commands.Clear();
        await backend.StopAsync();
        Assert.Contains(runner.Commands, c => c.Display == "ip -4 route del default metric 10");
    }

    [Fact]
    public async Task Deux_sources_produisent_une_route_multi_chemins_ponderee()
    {
        var (backend, runner, dir) = Create(null, Wifi, Eth, Lan);
        using var scope = dir;
        var caps = await backend.ProbeAsync();
        var request = new SharingRequest_InDto { SourceAdapterIds = { "wlan0", "eth0" }, TargetAdapterIds = { "eth1" } };
        request.SourceWeights["wlan0"] = 3;
        var plan = Plan(request, caps, Wifi, Eth, Lan);
        runner.Commands.Clear();

        await backend.StartAsync(plan);

        var route = runner.Commands.Single(c => c.FileName == "ip" && c.Arguments.Contains("nexthop"));
        Assert.Equal(
            "ip -4 route add default metric 10 nexthop via 192.168.1.1 dev wlan0 weight 3 nexthop via 10.0.0.1 dev eth0 weight 1",
            route.Display);
        Assert.Contains(runner.Commands, c => c.Display == "sysctl -w net.ipv4.fib_multipath_hash_policy=1");
    }

    [Fact]
    public async Task Hotspot_utilise_nmcli_avec_la_bande_demandee()
    {
        var (backend, runner, dir) = Create(null, Eth, Wifi2);
        using var scope = dir;
        var caps = await backend.ProbeAsync();
        var plan = Plan(
            new SharingRequest_InDto
            {
                SourceAdapterIds = { "eth0" },
                Hotspot = new WifiHotspot_InDto { Ssid = "Maison", Password = "motdepasse1", AdapterId = "wlan1", Band = WifiBand.Band5GHz },
            },
            caps, Eth, Wifi2);
        runner.Commands.Clear();

        await backend.StartAsync(plan);

        var hotspot = runner.Commands.Single(c => c.Arguments.Contains("hotspot"));
        Assert.Equal(
            new[] { "device", "wifi", "hotspot", "ifname", "wlan1", "con-name", "NetShareHub-AP", "ssid", "Maison", "password", "motdepasse1", "band", "a" },
            hotspot.Arguments);
    }

    [Fact]
    public async Task Sans_NetworkManager_le_NAT_integre_pose_adresse_et_regles_iptables_puis_les_retire()
    {
        var (backend, runner, dir, dhcp) = CreateWithDhcp(
            spec => spec.FileName == "nmcli" ? new CommandResult(127, "", "absent") : DefaultResponder(spec), Eth, Lan, Lan2);
        using var scope = dir;
        var caps = await backend.ProbeAsync();
        var plan = Plan(new SharingRequest_InDto { SourceAdapterIds = { "eth0" }, TargetAdapterIds = { "eth1" } }, caps, Eth, Lan, Lan2);
        var subnet = plan.Targets.Single().Subnet!.Value;
        runner.Commands.Clear();

        await backend.StartAsync(plan);

        var applied = runner.Commands.Select(c => c.Display).ToList();
        Assert.Contains($"ip addr add {subnet.HostAddress(1)}/24 dev eth1", applied);
        Assert.Contains(
            applied,
            l => l.StartsWith("iptables -t nat -I POSTROUTING -s ") && l.EndsWith("-m comment --comment NetShareHub -j MASQUERADE"));
        Assert.Contains(applied, l => l.StartsWith("iptables -I FORWARD -i eth1"));

        // Le DHCP est lancé sur la cible avec les DNS de la source.
        var dhcpScope = Assert.Single(dhcp.Scopes);
        Assert.Equal("eth1", dhcpScope.InterfaceName);
        Assert.Equal(subnet.HostAddress(1), dhcpScope.ServerAddress);
        Assert.Equal("192.168.1.1", dhcpScope.DnsServers.Single().ToString());

        // Tout ce qui a été posé doit avoir son annulation, et le DHCP doit s'arrêter.
        runner.Commands.Clear();
        await backend.StopAsync();
        var undone = runner.Commands.Select(c => c.Display).ToList();
        Assert.Contains($"ip addr del {subnet.HostAddress(1)}/24 dev eth1", undone);
        Assert.Contains(undone, l => l.StartsWith("iptables -t nat -D POSTROUTING"));
        Assert.Contains(undone, l => l.StartsWith("iptables -D FORWARD -i eth1"));
        Assert.Equal(1, dhcp.Stopped);
    }

    [Fact]
    public async Task Port_DHCP_occupe_donne_un_message_clair_et_annule_la_configuration()
    {
        var (backend, runner, dir, dhcp) = CreateWithDhcp(
            spec => spec.FileName == "nmcli" ? new CommandResult(127, "", "absent") : DefaultResponder(spec), Eth, Lan);
        using var scope = dir;
        dhcp.PortBusy = true;
        var caps = await backend.ProbeAsync();
        var plan = Plan(new SharingRequest_InDto { SourceAdapterIds = { "eth0" }, TargetAdapterIds = { "eth1" } }, caps, Eth, Lan);

        var ex = await Assert.ThrowsAsync<SharingException>(() => backend.StartAsync(plan));

        Assert.Contains("UDP 67", ex.Message);
        Assert.Contains(runner.Commands, c => c.Display.StartsWith("iptables -t nat -D POSTROUTING"));
    }

    [Fact]
    public async Task Echec_au_demarrage_annule_ce_qui_a_deja_ete_applique()
    {
        var (backend, runner, dir) = Create(
            spec => spec.FileName == "nmcli" && spec.Arguments.Contains("up")
                ? new CommandResult(4, "", "Erreur : activation échouée")
                : DefaultResponder(spec),
            Wifi, Lan);
        using var scope = dir;
        var caps = await backend.ProbeAsync();
        var plan = Plan(new SharingRequest_InDto { SourceAdapterIds = { "wlan0" }, TargetAdapterIds = { "eth1" } }, caps, Wifi, Lan);

        var ex = await Assert.ThrowsAsync<SharingException>(() => backend.StartAsync(plan));

        Assert.Contains("activation échouée", ex.Message);
        Assert.Contains(runner.Commands, c => c.Display == "nmcli connection delete id NetShareHub-eth1");
        Assert.Contains(runner.Commands, c => c.Display == "sysctl -w net.ipv4.ip_forward=0");
    }

    [Fact]
    public async Task Recover_defait_une_session_interrompue()
    {
        using var dir = new TempDirectory();
        var first = new RecordingRunner(DefaultResponder);
        var crashed = new LinuxSharingBackend(first, new FakeInventory(Wifi, Lan), new FakeDhcpFactory(), Logs.For<LinuxSharingBackend>(), dir.Path);
        var caps = await crashed.ProbeAsync();
        await crashed.StartAsync(Plan(new SharingRequest_InDto { SourceAdapterIds = { "wlan0" }, TargetAdapterIds = { "eth1" } }, caps, Wifi, Lan));
        // … l'application est tuée ici : on ne fait pas StopAsync.

        var second = new RecordingRunner(DefaultResponder);
        var restarted = new LinuxSharingBackend(second, new FakeInventory(Wifi, Lan), new FakeDhcpFactory(), Logs.For<LinuxSharingBackend>(), dir.Path);

        Assert.True(await restarted.RecoverAsync());
        Assert.Contains(second.Commands, c => c.Display == "nmcli connection delete id NetShareHub-eth1");
        Assert.False(await restarted.RecoverAsync());
    }
}

public class WindowsSharingBackendTests
{
    private static readonly NetworkAdapter Wifi = TestAdapters.Make(
        "{B85E7A63-105B-4FD0-A5AE-B2EBD796EE5B}", "Wi-Fi", AdapterKind.WiFi, ip: "192.168.1.20", gateway: "192.168.1.1", index: 12);
    private static readonly NetworkAdapter Eth = TestAdapters.Make("eth-id", "Ethernet", ip: null, gateway: null, index: 5);
    private static readonly NetworkAdapter Eth2 = TestAdapters.Make("eth2-id", "Ethernet 2", ip: null, gateway: null, index: 6);

    private const string QueryResult = """
        [{"Index":12,"Dhcp":true,"Forwarding":false,"AutoMetric":true,"Metric":25,"Addrs":[{"Ip":"192.168.1.20","Prefix":24}],"Gateway":"192.168.1.1"},
         {"Index":5,"Dhcp":false,"Forwarding":false,"AutoMetric":true,"Metric":25,"Addrs":[{"Ip":"192.168.50.2","Prefix":24}],"Gateway":null},
         {"Index":6,"Dhcp":true,"Forwarding":false,"AutoMetric":true,"Metric":25,"Addrs":[],"Gateway":null}]
        """;

    private static CommandResult Responder(CommandSpec spec)
    {
        if (spec.Description == "Lecture de la configuration IP")
        {
            return new CommandResult(0, QueryResult, "");
        }

        if (spec.Description == "Sonde du Point d'accès mobile")
        {
            return new CommandResult(0, "CAP:Enabled\nSTATE:Off", "");
        }

        return new CommandResult(0, "OK", "");
    }

    private static (WindowsSharingBackend Backend, RecordingRunner Runner, TempDirectory Dir) Create(
        Func<CommandSpec, CommandResult>? responder = null)
    {
        var (backend, runner, dir, _) = CreateWithDhcp(responder);
        return (backend, runner, dir);
    }

    private static (WindowsSharingBackend Backend, RecordingRunner Runner, TempDirectory Dir, FakeDhcpFactory Dhcp) CreateWithDhcp(
        Func<CommandSpec, CommandResult>? responder = null)
    {
        var dir = new TempDirectory();
        var runner = new RecordingRunner(responder ?? Responder);
        var dhcp = new FakeDhcpFactory();
        var backend = new WindowsSharingBackend(runner, new FakeInventory(Wifi, Eth, Eth2), dhcp, Logs.For<WindowsSharingBackend>(), dir.Path);
        return (backend, runner, dir, dhcp);
    }

    [Fact]
    public async Task Probe_reconnait_le_Point_d_acces_mobile()
    {
        var (backend, _, dir) = Create();
        using var scope = dir;

        var caps = await backend.ProbeAsync();

        Assert.True(caps.SupportsWifiHotspot);
        Assert.False(caps.SupportsTransparentBalancing);
    }

    [Fact]
    public async Task Probe_explique_pourquoi_le_hotspot_est_indisponible()
    {
        var (backend, _, dir) = Create(spec => new CommandResult(0, "CAP:DisabledByGroupPolicy", ""));
        using var scope = dir;

        var caps = await backend.ProbeAsync();

        Assert.False(caps.SupportsWifiHotspot);
        Assert.Contains(caps.Notes, n => n.Contains("DisabledByGroupPolicy"));
    }

    [Fact]
    public async Task Un_adaptateur_cible_active_ICS_et_l_arret_le_desactive()
    {
        var (backend, runner, dir) = Create();
        using var scope = dir;
        var caps = await backend.ProbeAsync();
        var plan = new SharingPlanner().Plan(
            new SharingRequest_InDto { SourceAdapterIds = { Wifi.Id }, TargetAdapterIds = { "eth-id" } },
            new[] { Wifi, Eth, Eth2 }, caps);
        runner.Commands.Clear();

        await backend.StartAsync(plan);

        var enable = Assert.Single(runner.Commands, c => c.Description!.StartsWith("Activation du partage ICS"));
        Assert.Contains("Find-Conn 'Wi-Fi'", enable.Script);
        Assert.Contains("Find-Conn 'Ethernet'", enable.Script);
        Assert.Contains("EnableSharing(0)", enable.Script);

        runner.Commands.Clear();
        await backend.StopAsync();
        Assert.Contains(runner.Commands, c => c.Description == "Arrêt du partage de connexion Windows (ICS)");
    }

    [Fact]
    public async Task Hotspot_transmet_ssid_et_mot_de_passe_et_cible_la_bonne_connexion()
    {
        var (backend, runner, dir) = Create();
        using var scope = dir;
        var caps = await backend.ProbeAsync();
        var plan = new SharingPlanner().Plan(
            new SharingRequest_InDto
            {
                SourceAdapterIds = { Wifi.Id },
                Hotspot = new WifiHotspot_InDto { Ssid = "L'été", Password = "motdepasse1" },
            },
            new[] { Wifi, Eth }, caps);
        runner.Commands.Clear();

        await backend.StartAsync(plan);

        var start = Assert.Single(runner.Commands, c => c.Description == "Démarrage du Point d'accès mobile");
        Assert.Contains("$cfg.Ssid = 'L''été'", start.Script); // apostrophe doublée : pas d'injection possible
        Assert.Contains("$cfg.Passphrase = 'motdepasse1'", start.Script);
        Assert.Contains("Get-ShareProfile 'b85e7a63-105b-4fd0-a5ae-b2ebd796ee5b'", start.Script);
    }

    [Fact]
    public async Task Plusieurs_adaptateurs_configurent_le_NAT_integre_et_restaurent_l_etat_initial()
    {
        var (backend, runner, dir, dhcp) = CreateWithDhcp();
        using var scope = dir;
        var caps = await backend.ProbeAsync();
        var plan = new SharingPlanner().Plan(
            new SharingRequest_InDto { SourceAdapterIds = { Wifi.Id }, TargetAdapterIds = { "eth-id", "eth2-id" } },
            new[] { Wifi, Eth, Eth2 }, caps);
        runner.Commands.Clear();

        await backend.StartAsync(plan);

        Assert.Equal(2, dhcp.Scopes.Count);
        Assert.All(dhcp.Scopes, s => Assert.StartsWith("10.77.", s.ServerAddress.ToString()));

        var descriptions = runner.Commands.Select(c => c.Description).ToList();
        Assert.Contains(descriptions, d => d!.StartsWith("Adresse 10.77.") && d.EndsWith("sur Ethernet"));
        Assert.Contains(descriptions, d => d!.StartsWith("Adresse 10.77.") && d.EndsWith("sur Ethernet 2"));
        Assert.Contains(descriptions, d => d!.StartsWith("Création du NAT 10.77."));

        runner.Commands.Clear();
        await backend.StopAsync();

        // L'adaptateur Ethernet était en IP statique, Ethernet 2 en DHCP : chacun retrouve son état.
        var restoreStatic = runner.Commands.Single(c => c.Description == "Restauration de la configuration de Ethernet");
        Assert.Contains("New-NetIPAddress -InterfaceIndex $i -IPAddress '192.168.50.2' -PrefixLength 24", restoreStatic.Script);
        var restoreDhcp = runner.Commands.Single(c => c.Description == "Restauration de la configuration de Ethernet 2");
        Assert.Contains("Set-NetIPInterface -InterfaceIndex $i -AddressFamily IPv4 -Dhcp Enabled", restoreDhcp.Script);
        Assert.Contains(runner.Commands, c => c.Description == "Suppression du NAT NetShare Hub");
    }

    [Fact]
    public async Task Un_echec_NAT_est_explique_et_annule_les_etapes_precedentes()
    {
        var (backend, runner, dir) = Create(spec =>
            spec.Description!.StartsWith("Création du NAT") ? new CommandResult(1, "", "multiple NAT") : Responder(spec));
        using var scope = dir;
        var caps = await backend.ProbeAsync();
        var plan = new SharingPlanner().Plan(
            new SharingRequest_InDto { SourceAdapterIds = { Wifi.Id }, TargetAdapterIds = { "eth-id", "eth2-id" } },
            new[] { Wifi, Eth, Eth2 }, caps);

        var ex = await Assert.ThrowsAsync<SharingException>(() => backend.StartAsync(plan));

        Assert.Contains("WSL, Docker ou Hyper-V", ex.Message);
        Assert.Contains(runner.Commands, c => c.Description == "Restauration de la configuration de Ethernet");
    }
}

/// <summary>Garde-fou : un script PowerShell mal formé ne se verrait qu'en production, sur la machine de l'utilisateur.</summary>
public class WindowsScriptsSyntaxTests
{
    public static IEnumerable<object[]> Scripts()
    {
        var state = new InterfaceState(7, false, true, false, 30, new() { new("192.168.50.2", 24) }, "192.168.50.1");
        var dhcp = state with { Dhcp = true, Addrs = new() };
        var address = System.Net.IPAddress.Parse("10.77.0.1");

        yield return new object[] { "ProbeHotspot", WindowsScripts.ProbeHotspot() };
        yield return new object[] { "StartHotspot", WindowsScripts.StartHotspot("b85e7a63-105b-4fd0-a5ae-b2ebd796ee5b", "L'été", "pa'ss word1", WifiBand.Band5GHz) };
        yield return new object[] { "StartHotspotTypographic", WindowsScripts.StartHotspot(string.Empty, "Marie’s PC", "x’; Write-Output INJECTED; #", WifiBand.Auto) };
        yield return new object[] { "StopHotspot", WindowsScripts.StopHotspot() };
        yield return new object[] { "EnableIcs", WindowsScripts.EnableIcs("Wi-Fi", "Ethernet d'essai") };
        yield return new object[] { "DisableIcs", WindowsScripts.DisableIcs() };
        yield return new object[] { "QueryInterfaces", WindowsScripts.QueryInterfaces(new[] { 5, 6, 12 }) };
        yield return new object[] { "ConfigureTarget", WindowsScripts.ConfigureTarget(5, address, 24) };
        yield return new object[] { "RestoreStatic", WindowsScripts.RestoreInterface(state, "Ethernet") };
        yield return new object[] { "RestoreDhcp", WindowsScripts.RestoreInterface(dhcp, "Ethernet 2") };
        yield return new object[] { "SetForwarding", WindowsScripts.SetForwarding(5, true) };
        yield return new object[] { "SetMetricFixed", WindowsScripts.SetMetric(5, 5) };
        yield return new object[] { "SetMetricAuto", WindowsScripts.SetMetric(5, null) };
        yield return new object[] { "CreateNat", WindowsScripts.CreateNat("10.77.0.0/23") };
        yield return new object[] { "RemoveNat", WindowsScripts.RemoveNat() };
        yield return new object[] { "AddFirewall", WindowsScripts.AddDhcpFirewallRule() };
        yield return new object[] { "RemoveFirewall", WindowsScripts.RemoveDhcpFirewallRule() };
    }

    [Fact]
    public void Tous_les_scripts_sont_syntaxiquement_valides_pour_PowerShell_5_1()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Windows PowerShell 5.1 n'existe que sous Windows : la cible réelle de ces scripts.
        }

        using var dir = new TempDirectory();
        var scripts = Scripts().Select(s => ((string)s[0], (string)s[1])).ToList();
        foreach (var (name, script) in scripts)
        {
            File.WriteAllText(Path.Combine(dir.Path, name + ".ps1"), script, new UTF8Encoding(true));
        }

        const string checker = """
            param($dir)
            foreach ($f in Get-ChildItem $dir -Filter *.ps1) {
              $tokens = $null; $errors = $null
              [void][System.Management.Automation.Language.Parser]::ParseFile($f.FullName, [ref]$tokens, [ref]$errors)
              foreach ($e in $errors) { "$($f.BaseName): $($e.Message) (ligne $($e.Extent.StartLineNumber))" }
            }
            "TERMINE"
            """;
        var checkerPath = Path.Combine(dir.Path, "check.psx");
        File.WriteAllText(checkerPath, checker, new UTF8Encoding(true));

        var start = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", $"& ([scriptblock]::Create((Get-Content -Raw '{checkerPath}'))) '{dir.Path}'" })
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        Assert.True(output.Trim() == "TERMINE", $"Erreurs de syntaxe PowerShell :{Environment.NewLine}{output}");
    }

    [Fact]
    public void Quote_double_les_apostrophes()
    {
        Assert.Equal("'l''été'", WindowsScripts.Quote("l'été"));
        Assert.Equal("''''", WindowsScripts.Quote("'"));
    }
}
