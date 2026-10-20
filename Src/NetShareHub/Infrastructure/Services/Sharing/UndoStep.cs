using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using NetShareHub.Application.Contracts.Services;

namespace NetShareHub.Infrastructure.Services.Sharing;

/// <summary>
/// Nature d'une annulation. Le journal ne stocke jamais de commande libre : seulement l'une de ces opérations et ses
/// paramètres, à partir desquels la commande est reconstruite (et validée) au moment de l'annulation.
/// </summary>
public enum UndoKind
{
    WindowsDisableIcs,
    WindowsStopHotspot,
    WindowsSetForwarding,
    WindowsSetMetric,
    WindowsRestoreInterface,
    WindowsRemoveNat,
    WindowsRemoveDhcpFirewallRule,
    LinuxDeleteRouteOverride,
    LinuxRestoreSysctl,
    LinuxConnectDevice,
    LinuxDeleteConnection,
    LinuxDeleteAddress,
    LinuxDeleteFirewallRule,
}

/// <summary>Règles iptables posées par le NAT intégré Linux.</summary>
public enum LinuxFirewallRule
{
    /// <summary>MASQUERADE du sous-réseau partagé vers l'extérieur (table nat, POSTROUTING).</summary>
    Masquerade,

    /// <summary>Trafic sortant des appareils partagés (FORWARD -i).</summary>
    ForwardIn,

    /// <summary>Réponses vers les appareils partagés (FORWARD -o, connexions établies).</summary>
    ForwardOutEstablished,

    /// <summary>Requêtes DHCP des appareils partagés (INPUT, UDP 67).</summary>
    DhcpInput,
}

/// <summary>
/// Annulation typée. Les paramètres sont revalidés par <see cref="ToCommand"/> : un journal altéré ne peut produire
/// qu'une des opérations prévues, sur des valeurs bien formées (interface, adresse, préfixe…), jamais un programme arbitraire.
/// </summary>
/// <param name="Kind">Opération.</param>
/// <param name="Index">Index d'interface Windows.</param>
/// <param name="Enabled">Routage IPv4 à restaurer (<see cref="UndoKind.WindowsSetForwarding"/>).</param>
/// <param name="Metric">Métrique à restaurer, <c>null</c> pour la métrique automatique.</param>
/// <param name="Name">Nom d'adaptateur (Windows) ou d'interface (Linux), ou clé sysctl.</param>
/// <param name="Value">Valeur sysctl, adresse « ip/préfixe » ou sous-réseau selon l'opération.</param>
/// <param name="State">État IPv4 complet à restaurer (<see cref="UndoKind.WindowsRestoreInterface"/>).</param>
/// <param name="Rule">Règle iptables à retirer.</param>
public sealed partial record UndoStep(
    UndoKind Kind,
    int Index = 0,
    bool Enabled = false,
    int? Metric = null,
    string? Name = null,
    string? Value = null,
    InterfaceState? State = null,
    LinuxFirewallRule? Rule = null)
{
    public const int LinuxOverrideMetric = 10;
    public const string LinuxAccessPointConnection = "NetShareHub-AP";
    public const string LinuxRuleTag = "NetShareHub";
    private const string ConnectionPrefix = "NetShareHub-";

    private static readonly HashSet<string> RestorableSysctls = new(StringComparer.Ordinal)
    {
        "net.ipv4.ip_forward",
        "net.ipv4.fib_multipath_hash_policy",
    };

    // --- Fabriques (utilisées par les moteurs) -----------------------------------------------------------------

    public static UndoStep WindowsDisableIcs() => new(UndoKind.WindowsDisableIcs);

    public static UndoStep WindowsStopHotspot() => new(UndoKind.WindowsStopHotspot);

    public static UndoStep WindowsSetForwarding(int index, bool enabled, string adapterName)
        => new(UndoKind.WindowsSetForwarding, Index: index, Enabled: enabled, Name: adapterName);

    public static UndoStep WindowsSetMetric(int index, int? metric, string adapterName)
        => new(UndoKind.WindowsSetMetric, Index: index, Metric: metric, Name: adapterName);

    public static UndoStep WindowsRestoreInterface(InterfaceState state, string adapterName)
        => new(UndoKind.WindowsRestoreInterface, Index: state.Index, Name: adapterName, State: state);

    public static UndoStep WindowsRemoveNat() => new(UndoKind.WindowsRemoveNat);

    public static UndoStep WindowsRemoveDhcpFirewallRule() => new(UndoKind.WindowsRemoveDhcpFirewallRule);

    public static UndoStep LinuxDeleteRouteOverride() => new(UndoKind.LinuxDeleteRouteOverride);

    public static UndoStep LinuxRestoreSysctl(string key, string value)
        => new(UndoKind.LinuxRestoreSysctl, Name: key, Value: value);

    public static UndoStep LinuxConnectDevice(string interfaceName)
        => new(UndoKind.LinuxConnectDevice, Name: interfaceName);

    public static UndoStep LinuxDeleteConnection(string connectionName)
        => new(UndoKind.LinuxDeleteConnection, Value: connectionName);

    public static UndoStep LinuxDeleteAddress(string addressWithPrefix, string interfaceName)
        => new(UndoKind.LinuxDeleteAddress, Name: interfaceName, Value: addressWithPrefix);

    public static UndoStep LinuxDeleteFirewallRule(LinuxFirewallRule rule, string interfaceName, string network)
        => new(UndoKind.LinuxDeleteFirewallRule, Name: interfaceName, Value: network, Rule: rule);

    // --- Reconstruction de la commande -------------------------------------------------------------------------

    /// <summary>Construit la commande d'annulation après validation des paramètres.</summary>
    /// <exception cref="InvalidDataException">Paramètre absent ou mal formé (journal altéré).</exception>
    public CommandSpec ToCommand() => (Kind switch
    {
        UndoKind.WindowsDisableIcs => CommandSpec.PowerShell(WindowsScripts.DisableIcs(), "Arrêt du partage de connexion Windows (ICS)"),
        UndoKind.WindowsStopHotspot => CommandSpec.PowerShell(WindowsScripts.StopHotspot(), "Arrêt du Point d'accès mobile"),
        UndoKind.WindowsSetForwarding => CommandSpec.PowerShell(
            WindowsScripts.SetForwarding(ValidIndex(), Enabled), $"Restauration du routage sur {ValidAdapterName()}"),
        UndoKind.WindowsSetMetric => CommandSpec.PowerShell(
            WindowsScripts.SetMetric(ValidIndex(), ValidMetric()), $"Restauration de la priorité de {ValidAdapterName()}"),
        UndoKind.WindowsRestoreInterface => CommandSpec.PowerShell(
            WindowsScripts.RestoreInterface(ValidState(), ValidAdapterName()), $"Restauration de la configuration de {ValidAdapterName()}"),
        UndoKind.WindowsRemoveNat => CommandSpec.PowerShell(WindowsScripts.RemoveNat(), "Suppression du NAT NetShare Hub"),
        UndoKind.WindowsRemoveDhcpFirewallRule => CommandSpec.PowerShell(
            WindowsScripts.RemoveDhcpFirewallRule(), "Suppression de la règle pare-feu DHCP"),
        UndoKind.LinuxDeleteRouteOverride => CommandSpec.Of("ip", "-4", "route", "del", "default", "metric", LinuxOverrideMetric.ToString()),
        UndoKind.LinuxRestoreSysctl => CommandSpec.Of("sysctl", "-w", $"{ValidSysctlKey()}={ValidSysctlValue()}"),
        UndoKind.LinuxConnectDevice => CommandSpec.Of("nmcli", "device", "connect", ValidInterface()),
        UndoKind.LinuxDeleteConnection => CommandSpec.Of("nmcli", "connection", "delete", "id", ValidConnection()),
        UndoKind.LinuxDeleteAddress => CommandSpec.Of("ip", "addr", "del", ValidAddressWithPrefix(), "dev", ValidInterface()),
        UndoKind.LinuxDeleteFirewallRule => new CommandSpec(
            "iptables",
            IptablesArguments(Rule ?? throw Invalid("règle iptables absente"), "-D", ValidInterface(), ValidNetwork()),
            Description: "Suppression d'une règle NetShare Hub"),
        _ => throw Invalid($"opération inconnue ({(int)Kind})"),
    }).Lenient();

    /// <summary>Arguments iptables d'une règle NetShare Hub (<c>-I</c> pour la poser, <c>-D</c> pour la retirer).</summary>
    internal static string[] IptablesArguments(LinuxFirewallRule rule, string action, string interfaceName, string network)
    {
        var (table, chain, match, target) = rule switch
        {
            LinuxFirewallRule.Masquerade => (new[] { "-t", "nat" }, "POSTROUTING", new[] { "-s", network, "!", "-d", network }, "MASQUERADE"),
            LinuxFirewallRule.ForwardIn => (Array.Empty<string>(), "FORWARD", new[] { "-i", interfaceName }, "ACCEPT"),
            LinuxFirewallRule.ForwardOutEstablished => (
                Array.Empty<string>(), "FORWARD", new[] { "-o", interfaceName, "-m", "conntrack", "--ctstate", "RELATED,ESTABLISHED" }, "ACCEPT"),
            LinuxFirewallRule.DhcpInput => (Array.Empty<string>(), "INPUT", new[] { "-i", interfaceName, "-p", "udp", "--dport", "67" }, "ACCEPT"),
            _ => throw Invalid($"règle iptables inconnue ({(int)rule})"),
        };

        return table
            .Append(action).Append(chain)
            .Concat(match)
            .Concat(new[] { "-m", "comment", "--comment", LinuxRuleTag, "-j", target })
            .ToArray();
    }

    /// <summary>Nom d'interface Linux acceptable (IFNAMSIZ = 16, sans espace ni « / »).</summary>
    internal static bool IsValidLinuxInterface(string? name)
        => name is not null && LinuxInterfaceRegex().IsMatch(name);

    private int ValidIndex()
        => Index is > 0 and < 1_000_000 ? Index : throw Invalid("index d'interface");

    private int? ValidMetric()
        => Metric is null or (>= 0 and <= 9999) ? Metric : throw Invalid("métrique");

    // Le nom n'est utilisé qu'entre guillemets simples PowerShell (WindowsScripts.Quote) : on écarte seulement l'absurde.
    private string ValidAdapterName()
        => Name is { Length: > 0 and <= 256 } name && !name.Any(char.IsControl) ? name : throw Invalid("nom d'adaptateur");

    private InterfaceState ValidState()
    {
        var state = State ?? throw Invalid("état d'interface absent");
        if (state.Index != ValidIndex() || state.Addrs is null)
        {
            throw Invalid("état d'interface incohérent");
        }

        foreach (var address in state.Addrs)
        {
            if (!IsIPv4(address.Ip) || address.Prefix is < 0 or > 32)
            {
                throw Invalid("adresse d'interface");
            }
        }

        if (state.Gateway is not null && !IsIPv4(state.Gateway))
        {
            throw Invalid("passerelle");
        }

        return state;
    }

    private string ValidSysctlKey()
        => Name is not null && RestorableSysctls.Contains(Name) ? Name : throw Invalid("clé sysctl");

    private string ValidSysctlValue()
        => Value is not null && SysctlValueRegex().IsMatch(Value) ? Value : throw Invalid("valeur sysctl");

    private string ValidInterface()
        => IsValidLinuxInterface(Name) ? Name! : throw Invalid("nom d'interface");

    private string ValidConnection()
        => Value == LinuxAccessPointConnection
           || (Value is not null && Value.StartsWith(ConnectionPrefix, StringComparison.Ordinal)
               && IsValidLinuxInterface(Value[ConnectionPrefix.Length..]))
            ? Value
            : throw Invalid("profil NetworkManager");

    private string ValidAddressWithPrefix()
        => Value?.Split('/') is [var ip, var prefix] && IsIPv4(ip) && int.TryParse(prefix, out var p) && p is >= 0 and <= 32
            ? Value
            : throw Invalid("adresse");

    // Seule la règle MASQUERADE porte sur un sous-réseau ; les autres ne dépendent que de l'interface.
    private string ValidNetwork()
        => Rule == LinuxFirewallRule.Masquerade ? ValidAddressWithPrefix() : string.Empty;

    private static bool IsIPv4(string? value)
        => IPAddress.TryParse(value, out var address) && address.AddressFamily == AddressFamily.InterNetwork
           && address.ToString() == value;

    private static InvalidDataException Invalid(string what) => new($"Journal d'annulation : {what} invalide.");

    [GeneratedRegex(@"^[A-Za-z0-9_.:@][A-Za-z0-9_.:@-]{0,14}$")] // pas de « - » initial : nmcli le lirait comme une option
    private static partial Regex LinuxInterfaceRegex();

    [GeneratedRegex(@"^[0-9]{1,3}$")]
    private static partial Regex SysctlValueRegex();
}
