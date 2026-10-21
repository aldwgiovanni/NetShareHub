using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetShareHub.Application.Contracts.Services;
using NetShareHub.Domain.Entities;
using NetShareHub.Domain.Enums;
using NetShareHub.Domain.Exceptions;
using NetShareHub.Infrastructure.Services.Dhcp;

namespace NetShareHub.Infrastructure.Services.Sharing;

/// <summary>
/// Moteur Windows. Trois mécanismes, choisis par le <c>SharingPlanner</c> :
/// <list type="bullet">
/// <item><b>ICS</b> (Partage de connexion Internet natif) quand il n'y a qu'un adaptateur cible.</item>
/// <item><b>Point d'accès mobile</b> (API WinRT) pour le hotspot Wi-Fi.</item>
/// <item><b>NAT intégré</b> (WinNAT + DHCP de NetShare Hub) pour plusieurs adaptateurs à la fois.</item>
/// </list>
/// Chaque modification est précédée de l'enregistrement de son annulation dans le <see cref="UndoJournal"/>.
/// </summary>
public sealed class WindowsSharingBackend : ISharingBackend
{
    private static readonly TimeSpan ProbeCacheDuration = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly ICommandRunner _runner;
    private readonly INetworkInventoryService _inventory;
    private readonly ILogger<WindowsSharingBackend> _logger;
    private readonly IDhcpServerFactory _dhcpFactory;
    private readonly UndoJournal _journal;
    private IAsyncDisposable? _dhcp;
    private BackendCapabilities? _cachedCapabilities;
    private DateTimeOffset _cachedAt;

    public WindowsSharingBackend(
        ICommandRunner runner,
        INetworkInventoryService inventory,
        IDhcpServerFactory dhcpFactory,
        ILogger<WindowsSharingBackend> logger,
        string? journalDirectory = null)
    {
        _runner = runner;
        _inventory = inventory;
        _dhcpFactory = dhcpFactory;
        _logger = logger;
        _journal = new UndoJournal(
            Path.Combine(journalDirectory ?? AppPaths.JournalDirectory, "undo-windows.json"), runner, logger, enforceSecurity: journalDirectory is null);
    }

    public bool HasPendingRecovery => _journal.HasPending;

    public async Task<BackendCapabilities> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedCapabilities is not null && DateTimeOffset.UtcNow - _cachedAt < ProbeCacheDuration)
        {
            return _cachedCapabilities;
        }

        var notes = new List<string>();
        var hasWifiAdapter = _inventory.Snapshot.Any(a => a.Kind == AdapterKind.WiFi);
        var hotspot = false;

        if (!hasWifiAdapter)
        {
            notes.Add("Aucune carte Wi-Fi détectée : le hotspot n'est pas disponible.");
        }
        else
        {
            var result = await _runner.RunAsync(
                CommandSpec.PowerShell(WindowsScripts.ProbeHotspot(), "Sonde du Point d'accès mobile", 30, ignoreFailure: true),
                cancellationToken);

            var capability = result.StdOut.Split('\n')
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.StartsWith("CAP:", StringComparison.Ordinal))?[4..];

            hotspot = capability == "Enabled";
            if (!hotspot)
            {
                notes.Add(capability switch
                {
                    "NoInternetProfile" => "Hotspot indisponible : aucune connexion Internet active pour le moment.",
                    null => "Hotspot indisponible : l'API Point d'accès mobile de Windows ne répond pas.",
                    var other => $"Hotspot indisponible : Point d'accès mobile « {other} ».",
                });
            }
        }

        _cachedCapabilities = new BackendCapabilities(HostPlatform.Windows, hotspot, false, false, notes);
        _cachedAt = DateTimeOffset.UtcNow;
        return _cachedCapabilities;
    }

    public async Task StartAsync(SharingPlan plan, CancellationToken cancellationToken = default)
    {
        if (_journal.HasPending)
        {
            // Reste d'une session interrompue : on repart d'un état propre avant de modifier quoi que ce soit.
            _logger.LogWarning("Restauration d'une session précédente interrompue");
            await _journal.UndoAllAsync(cancellationToken);
        }

        try
        {
            var ics = plan.Targets.FirstOrDefault(t => t.Mechanism == SharingMechanism.WindowsIcs);
            if (ics is not null || plan.Hotspot is not null)
            {
                await ClearIcsLeftoversAsync(cancellationToken);
            }

            var natTargets = plan.Targets.Where(DhcpScopes.IsBuiltInNat).ToList();
            if (natTargets.Count > 0)
            {
                await StartBuiltInNatAsync(plan, natTargets, cancellationToken);
            }

            if (ics is not null)
            {
                await StartIcsAsync(plan.PrimarySource, ics.Adapter, cancellationToken);
            }

            if (plan.Hotspot is { } hotspot)
            {
                await StartHotspotAsync(plan.PrimarySource, hotspot, cancellationToken);
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

    // --- ICS ---------------------------------------------------------------------------------------------------

    private async Task StartIcsAsync(NetworkAdapter source, NetworkAdapter target, CancellationToken ct)
    {
        _logger.LogInformation("ICS : {Source} → {Target}", source.Name, target.Name);

        // ICS fixe 192.168.137.1 sur la cible et ne la rend pas à la désactivation : on restaure son état d'origine
        // (inscrit avant, donc rejoué après l'arrêt d'ICS).
        var states = await QueryStatesAsync(new[] { target.InterfaceIndex }, ct);
        _journal.Push(UndoStep.WindowsRestoreInterface(states[target.InterfaceIndex], target.Name));
        _journal.Push(UndoStep.WindowsDisableIcs());
        await ExecAsync(
            CommandSpec.PowerShell(
                WindowsScripts.EnableIcs(source.Name, target.Name),
                $"Activation du partage ICS {source.Name} → {target.Name}"),
            ct);
    }

    /// <summary>Retire l'adresse ICS restée fixe sur un ancien adaptateur privé (voir <see cref="WindowsScripts.ClearIcsLeftovers"/>).</summary>
    private async Task ClearIcsLeftoversAsync(CancellationToken ct)
    {
        var result = await _runner.RunAsync(
            CommandSpec.PowerShell(WindowsScripts.ClearIcsLeftovers(), "Nettoyage d'un ancien partage ICS", ignoreFailure: true), ct);
        if (!result.Success)
        {
            _logger.LogWarning("Nettoyage d'un ancien partage ICS impossible : {Error}", result.StdErr);
            return;
        }

        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("CLEARED:", StringComparison.Ordinal))
            {
                _logger.LogInformation("Adresse {Address} d'un ancien partage ICS retirée de {Adapter}", WindowsScripts.IcsScopeAddress, line["CLEARED:".Length..]);
            }
        }
    }

    // --- Point d'accès mobile ---------------------------------------------------------------------------------

    private async Task StartHotspotAsync(NetworkAdapter source, WifiHotspotPlan hotspot, CancellationToken ct)
    {
        _logger.LogInformation("Hotspot Wi-Fi « {Ssid} » via {Source}", hotspot.Ssid, source.Name);
        var guid = Guid.TryParse(source.Id, out var parsed) ? parsed.ToString() : string.Empty;

        // Vérifié avant d'inscrire l'arrêt au journal : sinon l'annulation de cet échec couperait le hotspot de l'utilisateur.
        var state = await _runner.RunAsync(
            CommandSpec.PowerShell(WindowsScripts.QueryHotspotState(guid), "État du Point d'accès mobile", 30, ignoreFailure: true), ct);
        if (state.StdOut.Contains("STATE:On", StringComparison.Ordinal))
        {
            throw new SharingException("Le Point d'accès mobile de Windows est déjà actif : désactivez-le dans Paramètres avant de continuer.");
        }

        _journal.Push(UndoStep.WindowsStopHotspot());
        await ExecAsync(
            CommandSpec.PowerShell(
                WindowsScripts.StartHotspot(guid, hotspot.Ssid, hotspot.Password, hotspot.Band),
                "Démarrage du Point d'accès mobile",
                timeoutSeconds: 90),
            ct);
    }

    // --- NAT intégré -------------------------------------------------------------------------------------------

    private async Task StartBuiltInNatAsync(SharingPlan plan, List<ShareTargetPlan> targets, CancellationToken ct)
    {
        var source = plan.PrimarySource;
        var indexes = targets.Select(t => t.Adapter.InterfaceIndex).Append(source.InterfaceIndex).Distinct().ToList();
        var states = await QueryStatesAsync(indexes, ct);

        // 1. Interface source : routage activé, et priorité si une autre connexion Internet existe.
        var sourceState = states[source.InterfaceIndex];
        _journal.Push(UndoStep.WindowsSetForwarding(source.InterfaceIndex, sourceState.Forwarding, source.Name));
        await ExecAsync(
            CommandSpec.PowerShell(WindowsScripts.SetForwarding(source.InterfaceIndex, true), $"Routage IPv4 sur {source.Name}"), ct);

        var competing = _inventory.Snapshot.Any(a =>
            a.HasInternetRoute && a.Id != source.Id && plan.Targets.All(t => t.Adapter.Id != a.Id));
        if (competing)
        {
            _journal.Push(UndoStep.WindowsSetMetric(source.InterfaceIndex, sourceState.AutoMetric ? null : sourceState.Metric, source.Name));
            await ExecAsync(
                CommandSpec.PowerShell(WindowsScripts.SetMetric(source.InterfaceIndex, 5), $"Priorité de {source.Name}"), ct);
        }

        // 2. Cibles : adresse fixe, routage activé.
        foreach (var target in targets)
        {
            var adapter = target.Adapter;
            var subnet = target.Subnet!.Value;
            _journal.Push(UndoStep.WindowsRestoreInterface(states[adapter.InterfaceIndex], adapter.Name));
            await ExecAsync(
                CommandSpec.PowerShell(
                    WindowsScripts.ConfigureTarget(adapter.InterfaceIndex, subnet.HostAddress(1), subnet.PrefixLength),
                    $"Adresse {subnet.HostAddress(1)} sur {adapter.Name}"),
                ct);
        }

        // 3. NAT Windows sur tout le bloc réservé.
        var block = plan.NatBlock!.Value;
        _journal.Push(UndoStep.WindowsRemoveNat());
        var nat = await _runner.RunAsync(
            CommandSpec.PowerShell(WindowsScripts.CreateNat(block.ToString()), $"Création du NAT {block}", ignoreFailure: true), ct);
        if (!nat.Success)
        {
            throw new SharingException(
                "Impossible de créer le NAT Windows. Si WSL, Docker ou Hyper-V sont actifs, ils en utilisent peut-être déjà un.\n" + nat.StdErr);
        }

        // 4. Pare-feu + DHCP.
        _journal.Push(UndoStep.WindowsRemoveDhcpFirewallRule());
        await ExecAsync(CommandSpec.PowerShell(WindowsScripts.AddDhcpFirewallRule(), "Règle pare-feu DHCP (UDP 67)"), ct);

        try
        {
            _dhcp = _dhcpFactory.Start(DhcpScopes.FromPlan(plan, DhcpScopes.IsBuiltInNat));
        }
        catch (SocketException ex)
        {
            throw new SharingException(
                "Le port DHCP (UDP 67) est occupé par un autre service (Partage de connexion Windows, VM…). " + ex.Message, ex);
        }
    }

    private async Task<Dictionary<int, InterfaceState>> QueryStatesAsync(IEnumerable<int> indexes, CancellationToken ct)
    {
        var result = await _runner.RunAsync(
            CommandSpec.PowerShell(WindowsScripts.QueryInterfaces(indexes), "Lecture de la configuration IP", ignoreFailure: true), ct);
        if (!result.Success)
        {
            throw new SharingException("Lecture de la configuration réseau impossible : " + result.StdErr);
        }

        try
        {
            var json = result.StdOut.Trim();
            var states = JsonSerializer.Deserialize<List<InterfaceState>>(json, JsonOptions) ?? new();
            return states.ToDictionary(s => s.Index);
        }
        catch (JsonException ex)
        {
            throw new SharingException("Réponse inattendue de PowerShell lors de la lecture de la configuration IP.", ex);
        }
    }

    private async Task ExecAsync(CommandSpec spec, CancellationToken ct)
        => await _runner.RunOrThrowAsync(spec, ct);
}
