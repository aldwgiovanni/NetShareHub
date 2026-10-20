using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NetShareHub.Application.Contracts.Services;
using NetShareHub.Domain.Entities;
using NetShareHub.Domain.Enums;
using NetShareHub.Domain.Exceptions;
using NetShareHub.Infrastructure.Services.Dhcp;

namespace NetShareHub.Infrastructure.Services.Sharing;

/// <summary>
/// Moteur Linux.
/// <list type="bullet">
/// <item><b>NetworkManager</b> (<c>ipv4.method shared</c>) : DHCP + DNS + NAT fournis par NM, pour les adaptateurs et le hotspot Wi-Fi.</item>
/// <item><b>NAT intégré</b> (iptables + DHCP de NetShare Hub) quand NetworkManager n'est pas disponible.</item>
/// <item><b>Routage multi-chemins</b> (<c>ip route … nexthop</c>) : répartit aussi le trafic des appareils partagés entre toutes les sources.</item>
/// </list>
/// La route multi-chemins est ajoutée avec une métrique basse, sans toucher aux routes existantes : l'annulation se résume à la supprimer.
/// </summary>
public sealed partial class LinuxSharingBackend : ISharingBackend
{
    private const int OverrideMetric = 10;
    private const string RuleTag = "NetShareHub";
    private const string AccessPointConnection = "NetShareHub-AP";
    private static readonly TimeSpan ProbeCacheDuration = TimeSpan.FromSeconds(30);

    private readonly ICommandRunner _runner;
    private readonly INetworkInventoryService _inventory;
    private readonly ILogger<LinuxSharingBackend> _logger;
    private readonly IDhcpServerFactory _dhcpFactory;
    private readonly UndoJournal _journal;
    private IAsyncDisposable? _dhcp;
    private BackendCapabilities? _cachedCapabilities;
    private DateTimeOffset _cachedAt;

    public LinuxSharingBackend(
        ICommandRunner runner,
        INetworkInventoryService inventory,
        IDhcpServerFactory dhcpFactory,
        ILogger<LinuxSharingBackend> logger,
        string? journalDirectory = null)
    {
        _runner = runner;
        _inventory = inventory;
        _dhcpFactory = dhcpFactory;
        _logger = logger;
        _journal = new UndoJournal(Path.Combine(journalDirectory ?? AppPaths.JournalDirectory, "undo-linux.json"), runner, logger);
    }

    public async Task<BackendCapabilities> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedCapabilities is not null && DateTimeOffset.UtcNow - _cachedAt < ProbeCacheDuration)
        {
            return _cachedCapabilities;
        }

        var notes = new List<string>();

        var nm = await _runner.RunAsync(CommandSpec.Of("nmcli", "-t", "-f", "RUNNING", "general"), cancellationToken);
        var networkManager = nm.Success && nm.StdOut.Trim() == "running";
        if (!networkManager)
        {
            notes.Add("NetworkManager absent ou arrêté : le partage utilise le NAT intégré (iptables) et le hotspot Wi-Fi est indisponible.");
        }

        var ip = await _runner.RunAsync(CommandSpec.Of("ip", "-V"), cancellationToken);
        if (!ip.Success)
        {
            notes.Add("La commande « ip » (iproute2) est introuvable : installez le paquet iproute2.");
        }

        var hotspot = false;
        if (networkManager && _inventory.Snapshot.Any(a => a.Kind == AdapterKind.WiFi))
        {
            var iw = await _runner.RunAsync(CommandSpec.Of("iw", "list"), cancellationToken);
            hotspot = !iw.Success || SupportsAccessPoint(iw.StdOut); // sans « iw », on tente quand même
            if (!hotspot)
            {
                notes.Add("Aucune carte Wi-Fi ne déclare le mode point d'accès (AP) : hotspot indisponible.");
            }
        }
        else if (networkManager)
        {
            notes.Add("Aucune carte Wi-Fi détectée : le hotspot n'est pas disponible.");
        }

        _cachedCapabilities = new BackendCapabilities(HostPlatform.Linux, hotspot, networkManager, ip.Success, notes);
        _cachedAt = DateTimeOffset.UtcNow;
        return _cachedCapabilities;
    }

    public async Task StartAsync(SharingPlan plan, CancellationToken cancellationToken = default)
    {
        if (_journal.HasPending)
        {
            _logger.LogWarning("Restauration d'une session précédente interrompue");
            await _journal.UndoAllAsync(cancellationToken);
        }

        try
        {
            await SetSysctlAsync("net.ipv4.ip_forward", "1", cancellationToken);
            await ApplyRoutingAsync(plan, cancellationToken);

            foreach (var target in plan.Targets)
            {
                if (target.Mechanism == SharingMechanism.LinuxNetworkManager)
                {
                    await ShareWithNetworkManagerAsync(target.Adapter, cancellationToken);
                }
                else
                {
                    await ShareWithBuiltInNatAsync(target, cancellationToken);
                }
            }

            if (plan.Hotspot is { } hotspot)
            {
                await StartHotspotAsync(hotspot, cancellationToken);
            }

            var scopes = DhcpScopes.FromPlan(plan, DhcpScopes.IsBuiltInNat);
            if (scopes.Count > 0)
            {
                try
                {
                    _dhcp = _dhcpFactory.Start(scopes);
                }
                catch (SocketException ex)
                {
                    throw new SharingException("Le port DHCP (UDP 67) est occupé par un autre service (dnsmasq, dhcpd…). " + ex.Message, ex);
                }
            }
        }
        catch
        {
            _logger.LogWarning("Échec : annulation des modifications déjà appliquées");
            await StopAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_dhcp is not null)
        {
            await _dhcp.DisposeAsync();
            _dhcp = null;
        }

        await _journal.UndoAllAsync(cancellationToken);
    }

    public async Task<bool> RecoverAsync(CancellationToken cancellationToken = default)
    {
        if (!_journal.HasPending)
        {
            return false;
        }

        _logger.LogWarning("Annulation des modifications laissées par une session interrompue");
        await _journal.UndoAllAsync(cancellationToken);
        return true;
    }

    // --- Routage ----------------------------------------------------------------------------------------------

    internal static CommandSpec BuildMultipathRoute(IReadOnlyList<NetworkAdapter> sources, IReadOnlyDictionary<string, int> weights)
    {
        var args = new List<string> { "-4", "route", "add", "default", "metric", OverrideMetric.ToString() };
        foreach (var source in sources)
        {
            args.AddRange(new[]
            {
                "nexthop", "via", source.Gateway!.ToString(), "dev", source.Name,
                "weight", Math.Clamp(weights.TryGetValue(source.Id, out var w) ? w : 1, 1, 256).ToString(),
            });
        }

        return new CommandSpec("ip", args, Description: "Route par défaut multi-chemins");
    }

    private async Task ApplyRoutingAsync(SharingPlan plan, CancellationToken ct)
    {
        var deleteOverride = CommandSpec.Of("ip", "-4", "route", "del", "default", "metric", OverrideMetric.ToString()).Lenient();

        if (plan.BalanceSources && plan.Sources.Count > 1)
        {
            await SetSysctlAsync("net.ipv4.fib_multipath_hash_policy", "1", ct, lenient: true);
            _journal.Push(deleteOverride);
            await _runner.RunAsync(deleteOverride, ct); // reste éventuel d'une session précédente
            await _runner.RunOrThrowAsync(BuildMultipathRoute(plan.Sources, plan.SourceWeights), ct);
            _logger.LogInformation("Routage multi-chemins sur {Count} sources", plan.Sources.Count);
            return;
        }

        var source = plan.PrimarySource;
        if (await UsesInterfaceAsync(source.Name, ct))
        {
            return; // c'est déjà la route par défaut du système
        }

        _journal.Push(deleteOverride);
        await _runner.RunAsync(deleteOverride, ct);
        await _runner.RunOrThrowAsync(
            new CommandSpec(
                "ip",
                new[] { "-4", "route", "add", "default", "via", source.Gateway!.ToString(), "dev", source.Name, "metric", OverrideMetric.ToString() },
                Description: $"Route par défaut via {source.Name}"),
            ct);
    }

    private async Task<bool> UsesInterfaceAsync(string interfaceName, CancellationToken ct)
    {
        var result = await _runner.RunAsync(CommandSpec.Of("ip", "-4", "route", "get", "1.1.1.1"), ct);
        if (!result.Success)
        {
            return false;
        }

        var tokens = result.StdOut.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var index = Array.IndexOf(tokens, "dev");
        return index >= 0 && index + 1 < tokens.Length && tokens[index + 1] == interfaceName;
    }

    private async Task SetSysctlAsync(string key, string value, CancellationToken ct, bool lenient = false)
    {
        var current = await _runner.RunAsync(CommandSpec.Of("sysctl", "-n", key), ct);
        if (current.Success && current.StdOut.Trim() == value)
        {
            return;
        }

        if (current.Success)
        {
            _journal.Push(CommandSpec.Of("sysctl", "-w", $"{key}={current.StdOut.Trim()}"));
        }

        var set = CommandSpec.Of("sysctl", "-w", $"{key}={value}");
        await _runner.RunOrThrowAsync(lenient ? set.Lenient() : set, ct);
    }

    // --- NetworkManager ----------------------------------------------------------------------------------------

    private async Task ShareWithNetworkManagerAsync(NetworkAdapter adapter, CancellationToken ct)
    {
        var connection = ConnectionName(adapter);
        _logger.LogInformation("NetworkManager : partage sur {Name}", adapter.Name);

        // Annulation enregistrée avant l'action : supprimer le profil, puis rendre la carte à NetworkManager.
        _journal.Push(CommandSpec.Of("nmcli", "device", "connect", adapter.Name));
        _journal.Push(CommandSpec.Of("nmcli", "connection", "delete", "id", connection));

        await _runner.RunAsync(CommandSpec.Of("nmcli", "connection", "delete", "id", connection).Lenient(), ct);
        await _runner.RunOrThrowAsync(
            new CommandSpec(
                "nmcli",
                new[]
                {
                    "connection", "add", "type", "ethernet", "ifname", adapter.Name, "con-name", connection,
                    "ipv4.method", "shared", "ipv6.method", "ignore", "connection.autoconnect", "no",
                },
                Description: $"Profil de partage NetworkManager sur {adapter.Name}"),
            ct);
        await _runner.RunOrThrowAsync(
            new CommandSpec(
                "nmcli",
                new[] { "connection", "up", "id", connection, "ifname", adapter.Name },
                TimeoutSeconds: 60,
                Description: $"Activation du partage sur {adapter.Name}"),
            ct);
    }

    private async Task StartHotspotAsync(WifiHotspotPlan hotspot, CancellationToken ct)
    {
        var adapter = hotspot.Adapter ?? throw new SharingException("Carte Wi-Fi du hotspot non définie.");
        _logger.LogInformation("Hotspot Wi-Fi « {Ssid} » sur {Name}", hotspot.Ssid, adapter.Name);

        _journal.Push(CommandSpec.Of("nmcli", "device", "connect", adapter.Name));
        _journal.Push(CommandSpec.Of("nmcli", "connection", "delete", "id", AccessPointConnection));
        await _runner.RunAsync(CommandSpec.Of("nmcli", "connection", "delete", "id", AccessPointConnection).Lenient(), ct);

        var args = new List<string>
        {
            "device", "wifi", "hotspot", "ifname", adapter.Name, "con-name", AccessPointConnection,
            "ssid", hotspot.Ssid, "password", hotspot.Password,
        };
        if (hotspot.Band != WifiBand.Auto)
        {
            args.AddRange(new[] { "band", hotspot.Band == WifiBand.Band5GHz ? "a" : "bg" });
        }

        await _runner.RunOrThrowAsync(
            new CommandSpec("nmcli", args, TimeoutSeconds: 60, Description: $"Création du hotspot Wi-Fi sur {adapter.Name}"),
            ct);
    }

    internal static string ConnectionName(NetworkAdapter adapter) => $"NetShareHub-{adapter.Name}";

    // --- NAT intégré (iptables) --------------------------------------------------------------------------------

    private async Task ShareWithBuiltInNatAsync(ShareTargetPlan target, CancellationToken ct)
    {
        var adapter = target.Adapter;
        var subnet = target.Subnet!.Value;
        var address = $"{subnet.HostAddress(1)}/{subnet.PrefixLength}";
        _logger.LogInformation("NAT intégré : {Name} ({Subnet})", adapter.Name, subnet);

        _journal.Push(CommandSpec.Of("ip", "addr", "del", address, "dev", adapter.Name));
        await _runner.RunOrThrowAsync(
            new CommandSpec("ip", new[] { "addr", "add", address, "dev", adapter.Name }, Description: $"Adresse {address} sur {adapter.Name}"), ct);
        await _runner.RunOrThrowAsync(CommandSpec.Of("ip", "link", "set", adapter.Name, "up"), ct);

        var network = subnet.ToString();
        var natTable = new[] { "-t", "nat" };
        var noTable = Array.Empty<string>();

        await AddFirewallRuleAsync(natTable, "POSTROUTING", new[] { "-s", network, "!", "-d", network }, "MASQUERADE", ct);
        await AddFirewallRuleAsync(noTable, "FORWARD", new[] { "-i", adapter.Name }, "ACCEPT", ct);
        await AddFirewallRuleAsync(
            noTable, "FORWARD", new[] { "-o", adapter.Name, "-m", "conntrack", "--ctstate", "RELATED,ESTABLISHED" }, "ACCEPT", ct);
        await AddFirewallRuleAsync(
            noTable, "INPUT", new[] { "-i", adapter.Name, "-p", "udp", "--dport", "67" }, "ACCEPT", ct);
    }

    // Les règles sont insérées en tête de chaîne (-I) et marquées d'un commentaire ; l'annulation les supprime à l'identique (-D).
    private async Task AddFirewallRuleAsync(string[] table, string chain, string[] match, string target, CancellationToken ct)
    {
        string[] Build(string action) => table
            .Append(action).Append(chain)
            .Concat(match)
            .Concat(new[] { "-m", "comment", "--comment", RuleTag, "-j", target })
            .ToArray();

        _journal.Push(new CommandSpec("iptables", Build("-D"), Description: "Suppression d'une règle NetShare Hub"));
        await _runner.RunOrThrowAsync(
            new CommandSpec("iptables", Build("-I"), Description: $"Règle pare-feu {chain} {string.Join(' ', match)}"), ct);
    }

    // --- Utilitaires ---------------------------------------------------------------------------------------------

    internal static bool SupportsAccessPoint(string iwListOutput)
        => AccessPointModeRegex().IsMatch(iwListOutput);

    [GeneratedRegex(@"Supported interface modes:[\s\S]*?\*\s*AP\b", RegexOptions.IgnoreCase)]
    private static partial Regex AccessPointModeRegex();
}
