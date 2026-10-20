using System.Text.Json;
using NetShareHub.Application.Contracts.Services;
using NetShareHub.Domain.Enums;
using NetShareHub.Infrastructure.Services;
using NetShareHub.Infrastructure.Services.Dhcp;
using NetShareHub.Infrastructure.Services.Sharing;

namespace NetShareHub.Tests.Integration;

/// <summary>
/// Exécute les vrais scripts de lecture sur la machine de test (aucune modification du système, aucun droit administrateur requis).
/// Ils vérifient ce que les faux ne peuvent pas vérifier : le format réel de la sortie de PowerShell.
/// </summary>
public class RealSystemTests
{
    [Fact]
    public async Task Windows_la_sonde_du_hotspot_repond_avec_un_diagnostic_exploitable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDirectory();
        using var inventory = new NetworkInventoryService(Logs.For<NetworkInventoryService>());
        var backend = new WindowsSharingBackend(
            new CommandRunner(Logs.For<CommandRunner>()), inventory, new DhcpServerFactory(Logs.For<DhcpServer>()),
            Logs.For<WindowsSharingBackend>(), dir.Path);

        var caps = await backend.ProbeAsync();

        Assert.Equal(HostPlatform.Windows, caps.Platform);
        // Soit le hotspot est utilisable, soit la sonde dit pourquoi : jamais un « non » muet.
        Assert.True(caps.SupportsWifiHotspot || caps.Notes.Count > 0, "hotspot indisponible sans explication");
        Assert.False(caps.NetworkManagerAvailable);
    }

    [Fact]
    public async Task Windows_la_lecture_de_la_configuration_IP_se_deserialise_pour_chaque_adaptateur()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var inventory = new NetworkInventoryService(Logs.For<NetworkInventoryService>());
        var adapters = (await inventory.GetAdaptersAsync()).Where(a => !a.IsLoopback && a.InterfaceIndex > 0 && a.IPv4Address is not null).ToList();
        Assert.NotEmpty(adapters);

        var runner = new CommandRunner(Logs.For<CommandRunner>());
        var result = await runner.RunAsync(
            CommandSpec.PowerShell(WindowsScripts.QueryInterfaces(adapters.Select(a => a.InterfaceIndex)), "Lecture de la configuration IP"));

        Assert.True(result.Success, result.StdErr);
        var states = JsonSerializer.Deserialize<List<InterfaceState>>(result.StdOut, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.Equal(adapters.Count, states.Count);
        foreach (var adapter in adapters)
        {
            var state = Assert.Single(states, s => s.Index == adapter.InterfaceIndex);
            // L'adresse vue par .NET doit figurer parmi celles lues par PowerShell (cas d'une seule adresse compris).
            Assert.Contains(state.Addrs, a => a.Ip == adapter.IPv4Address!.ToString());
            Assert.Equal(adapter.Gateway?.ToString(), state.Gateway);
        }
    }
}

public class WindowsSharingBackendRealTests
{
    [Fact]
    public async Task Windows_les_commandes_d_annulation_sans_rien_a_annuler_sont_inoffensives()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Ces scripts sont ceux exécutés à l'arrêt : sur une machine propre ils ne doivent ni échouer ni rien changer.
        var runner = new CommandRunner(Logs.For<CommandRunner>());

        var nat = await runner.RunAsync(CommandSpec.PowerShell(WindowsScripts.RemoveNat(), "Suppression du NAT NetShare Hub", ignoreFailure: true));
        var firewall = await runner.RunAsync(CommandSpec.PowerShell(WindowsScripts.RemoveDhcpFirewallRule(), "Suppression de la règle pare-feu DHCP", ignoreFailure: true));

        // Rien à retirer = succès franc (code 0), pas un échec silencieux.
        Assert.True(nat.Success, $"code {nat.ExitCode} : {nat.StdErr}");
        Assert.True(firewall.Success, $"code {firewall.ExitCode} : {firewall.StdErr}");
    }
}
