using NetShareHub.Application.Services;
using NetShareHub.Domain.Dtos.Incoming;
using NetShareHub.Domain.Entities;
using NetShareHub.Domain.Enums;
using NetShareHub.Domain.Exceptions;
using NetShareHub.Domain.ValueObjects;

namespace NetShareHub.Tests.Unit;

public class SharingPlannerTests
{
    private readonly SharingPlanner _planner = new();

    private static readonly NetworkAdapter Wifi = TestAdapters.Make("wifi", "Wi-Fi", AdapterKind.WiFi, ip: "192.168.1.20", gateway: "192.168.1.1");
    private static readonly NetworkAdapter Eth = TestAdapters.Make("eth", "Ethernet", ip: "10.0.0.5", gateway: "10.0.0.1");
    private static readonly NetworkAdapter Lan1 = TestAdapters.Make("lan1", "LAN 1", ip: null, gateway: null);
    private static readonly NetworkAdapter Lan2 = TestAdapters.Make("lan2", "LAN 2", ip: null, gateway: null);

    private static SharingRequest_InDto Request(string[] sources, string[] targets, WifiHotspot_InDto? hotspot = null)
        => new() { SourceAdapterIds = sources.ToList(), TargetAdapterIds = targets.ToList(), Hotspot = hotspot };

    [Fact]
    public void Windows_un_seul_adaptateur_cible_utilise_ICS()
    {
        var plan = _planner.Plan(
            Request(new[] { "wifi" }, new[] { "lan1" }),
            new[] { Wifi, Lan1 },
            TestAdapters.Caps(HostPlatform.Windows));

        var target = Assert.Single(plan.Targets);
        Assert.Equal(SharingMechanism.WindowsIcs, target.Mechanism);
        Assert.Null(plan.NatBlock);
    }

    [Fact]
    public void Windows_plusieurs_adaptateurs_utilisent_le_NAT_integre_avec_un_sous_reseau_chacun()
    {
        var plan = _planner.Plan(
            Request(new[] { "wifi" }, new[] { "lan1", "lan2" }),
            new[] { Wifi, Lan1, Lan2 },
            TestAdapters.Caps(HostPlatform.Windows));

        Assert.All(plan.Targets, t => Assert.Equal(SharingMechanism.WindowsNat, t.Mechanism));
        Assert.NotNull(plan.NatBlock);
        Assert.Equal(23, plan.NatBlock!.Value.PrefixLength);

        var subnets = plan.Targets.Select(t => t.Subnet!.Value).ToList();
        Assert.Equal(2, subnets.Distinct().Count());
        Assert.All(subnets, s => Assert.True(plan.NatBlock.Value.Overlaps(s) && s.PrefixLength == 24));
        Assert.Contains(plan.Warnings, w => w.Contains("expérimental"));
    }

    [Fact]
    public void Hotspot_et_adaptateur_ensemble_evitent_ICS()
    {
        var plan = _planner.Plan(
            Request(new[] { "eth" }, new[] { "lan1" }, new WifiHotspot_InDto { Ssid = "Maison", Password = "motdepasse1" }),
            new[] { Eth, Lan1, Wifi },
            TestAdapters.Caps(HostPlatform.Windows));

        Assert.Equal(SharingMechanism.WindowsNat, plan.Targets.Single().Mechanism);
        Assert.Equal(SharingMechanism.WindowsMobileHotspot, plan.Hotspot!.Mechanism);
    }

    [Fact]
    public void Linux_avec_NetworkManager_utilise_NM_sinon_NAT_integre()
    {
        var withNm = _planner.Plan(
            Request(new[] { "eth" }, new[] { "lan1" }), new[] { Eth, Lan1 },
            TestAdapters.Caps(HostPlatform.Linux, networkManager: true));
        var withoutNm = _planner.Plan(
            Request(new[] { "eth" }, new[] { "lan1" }), new[] { Eth, Lan1 },
            TestAdapters.Caps(HostPlatform.Linux, networkManager: false));

        Assert.Equal(SharingMechanism.LinuxNetworkManager, withNm.Targets.Single().Mechanism);
        Assert.Null(withNm.NatBlock);
        Assert.Equal(SharingMechanism.LinuxNat, withoutNm.Targets.Single().Mechanism);
        Assert.NotNull(withoutNm.NatBlock);
    }

    [Fact]
    public void Le_bloc_NAT_evite_les_reseaux_existants()
    {
        var clash = TestAdapters.Make("vpn", "VPN", ip: "10.77.0.9", gateway: "10.77.0.1");

        var plan = _planner.Plan(
            Request(new[] { "eth" }, new[] { "lan1", "lan2" }), new[] { Eth, Lan1, Lan2, clash },
            TestAdapters.Caps(HostPlatform.Linux));

        var existing = Ipv4Subnet.FromAddressAndMask(clash.IPv4Address!, clash.IPv4Mask!);
        Assert.False(plan.NatBlock!.Value.Overlaps(existing));
    }

    [Fact]
    public void Plusieurs_sources_sont_triees_par_poids_puis_vitesse()
    {
        var slowHeavy = TestAdapters.Make("slow", "Lent", ip: "172.16.0.2", gateway: "172.16.0.1", speed: 10_000_000);
        var request = Request(new[] { "eth", "slow" }, new[] { "lan1" });
        request.SourceWeights["slow"] = 5;

        var plan = _planner.Plan(request, new[] { Eth, slowHeavy, Lan1 }, TestAdapters.Caps(HostPlatform.Linux, balancing: true));

        Assert.Equal("slow", plan.PrimarySource.Id);
        Assert.True(plan.BalanceSources);
        Assert.Equal(5, plan.SourceWeights["slow"]);
        Assert.Equal(1, plan.SourceWeights["eth"]);
    }

    [Fact]
    public void Windows_plusieurs_sources_previent_que_le_partage_n_est_pas_agrege()
    {
        var plan = _planner.Plan(
            Request(new[] { "wifi", "eth" }, new[] { "lan1" }), new[] { Wifi, Eth, Lan1 },
            TestAdapters.Caps(HostPlatform.Windows, balancing: false));

        Assert.False(plan.BalanceSources);
        Assert.Contains(plan.Warnings, w => w.Contains("Dispatch"));
    }

    public static IEnumerable<object[]> InvalidRequests()
    {
        yield return new object[] { "aucune source", Request(Array.Empty<string>(), new[] { "lan1" }), "au moins une connexion" };
        yield return new object[] { "aucune destination", Request(new[] { "wifi" }, Array.Empty<string>()), "au moins une destination" };
        yield return new object[] { "source = cible", Request(new[] { "wifi" }, new[] { "wifi" }), "à la fois source et cible" };
        yield return new object[] { "source sans internet", Request(new[] { "lan1" }, new[] { "lan2" }), "pas de connexion Internet" };
        yield return new object[] { "wifi en cible simple", Request(new[] { "eth" }, new[] { "wifi" }), "Hotspot Wi-Fi" };
        yield return new object[] { "cible inconnue", Request(new[] { "wifi" }, new[] { "fantome" }), "introuvable" };
        yield return new object[] { "cible débranchée", Request(new[] { "wifi" }, new[] { "down" }), "n'est pas connecté" };
    }

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public void Demandes_invalides_sont_refusees_avec_un_message_clair(string _, SharingRequest_InDto request, string expected)
    {
        var down = TestAdapters.Make("down", "Débranché", up: false, ip: null, gateway: null);

        var ex = Assert.Throws<SharingException>(() =>
            _planner.Plan(request, new[] { Wifi, Eth, Lan1, Lan2, down }, TestAdapters.Caps(HostPlatform.Windows)));

        Assert.Contains(expected, ex.Message);
    }

    [Theory]
    [InlineData("", "motdepasse1", "SSID")]
    [InlineData("Maison", "court", "mot de passe")]
    [InlineData("Maison", "mot de passe accentué é", "mot de passe")]
    public void Hotspot_valide_ssid_et_mot_de_passe(string ssid, string password, string expected)
    {
        var request = Request(
            new[] { "eth" }, Array.Empty<string>(), new WifiHotspot_InDto { Ssid = ssid, Password = password });

        var ex = Assert.Throws<SharingException>(() =>
            _planner.Plan(request, new[] { Eth, Wifi }, TestAdapters.Caps(HostPlatform.Windows)));

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Hotspot_refuse_quand_la_machine_ne_le_supporte_pas()
    {
        var request = Request(new[] { "eth" }, Array.Empty<string>(), new WifiHotspot_InDto { Ssid = "Maison", Password = "motdepasse1" });

        var ex = Assert.Throws<SharingException>(() =>
            _planner.Plan(request, new[] { Eth, Wifi }, TestAdapters.Caps(HostPlatform.Windows, hotspot: false)));

        Assert.Contains("hotspot Wi-Fi n'est pas disponible", ex.Message);
    }

    [Fact]
    public void Linux_hotspot_exige_une_carte_wifi_distincte_des_sources()
    {
        var wifi2 = TestAdapters.Make("wifi2", "wlan1", AdapterKind.WiFi, ip: null, gateway: null);
        var caps = TestAdapters.Caps(HostPlatform.Linux, networkManager: true);

        // Carte non précisée.
        Assert.Throws<SharingException>(() => _planner.Plan(
            Request(new[] { "eth" }, Array.Empty<string>(), new WifiHotspot_InDto { Ssid = "S", Password = "motdepasse1" }),
            new[] { Eth, wifi2 }, caps));

        // Carte utilisée comme source.
        var ex = Assert.Throws<SharingException>(() => _planner.Plan(
            Request(new[] { "wifi" }, Array.Empty<string>(), new WifiHotspot_InDto { Ssid = "S", Password = "motdepasse1", AdapterId = "wifi" }),
            new[] { Wifi, wifi2 }, caps));
        Assert.Contains("source Internet", ex.Message);

        // Cas valide.
        var plan = _planner.Plan(
            Request(new[] { "wifi" }, Array.Empty<string>(), new WifiHotspot_InDto { Ssid = "S", Password = "motdepasse1", AdapterId = "wifi2" }),
            new[] { Wifi, wifi2 }, caps);
        Assert.Equal("wifi2", plan.Hotspot!.Adapter!.Id);
    }

    [Fact]
    public void Adaptateur_virtuel_genere_un_avertissement_sans_bloquer()
    {
        var vm = TestAdapters.Make("vm", "VMware", ip: null, gateway: null, virtualAdapter: true);

        var plan = _planner.Plan(
            Request(new[] { "eth" }, new[] { "vm" }), new[] { Eth, vm }, TestAdapters.Caps(HostPlatform.Linux));

        Assert.Contains(plan.Warnings, w => w.Contains("virtuel"));
    }
}

public class Ipv4SubnetTests
{
    [Fact]
    public void Normalise_l_adresse_reseau_et_calcule_les_bornes()
    {
        var subnet = Ipv4Subnet.Parse("192.168.5.77/24");

        Assert.Equal("192.168.5.0/24", subnet.ToString());
        Assert.Equal("255.255.255.0", subnet.SubnetMask.ToString());
        Assert.Equal("192.168.5.255", subnet.BroadcastAddress.ToString());
        Assert.Equal("192.168.5.1", subnet.HostAddress(1).ToString());
        Assert.Equal(256UL, subnet.Size);
    }

    [Fact]
    public void Detecte_les_chevauchements_dans_les_deux_sens()
    {
        var big = Ipv4Subnet.Parse("10.77.0.0/22");
        var inside = Ipv4Subnet.Parse("10.77.2.0/24");
        var outside = Ipv4Subnet.Parse("10.78.0.0/24");

        Assert.True(big.Overlaps(inside));
        Assert.True(inside.Overlaps(big));
        Assert.False(big.Overlaps(outside));
    }

    [Fact]
    public void Sub_decoupe_un_bloc_en_sous_reseaux()
    {
        var block = Ipv4Subnet.Parse("10.77.0.0/22");

        Assert.Equal("10.77.0.0/24", block.Sub(24, 0).ToString());
        Assert.Equal("10.77.3.0/24", block.Sub(24, 3).ToString());
        Assert.Throws<ArgumentOutOfRangeException>(() => block.Sub(24, 4));
    }

    [Theory]
    [InlineData(1, 24)]
    [InlineData(2, 23)]
    [InlineData(3, 22)]
    [InlineData(4, 22)]
    [InlineData(5, 21)]
    public void Allocation_arrondit_a_la_puissance_de_deux(int count, int expectedPrefix)
    {
        var block = SubnetAllocator.AllocateBlock(count, Array.Empty<Ipv4Subnet>());

        Assert.Equal(expectedPrefix, block.PrefixLength);
        Assert.StartsWith("10.77.", block.ToString());
    }

    [Fact]
    public void Allocation_saute_les_blocs_occupes()
    {
        var taken = new[] { Ipv4Subnet.Parse("10.77.0.0/16") };

        var block = SubnetAllocator.AllocateBlock(2, taken);

        Assert.False(block.Overlaps(taken[0]));
    }
}
