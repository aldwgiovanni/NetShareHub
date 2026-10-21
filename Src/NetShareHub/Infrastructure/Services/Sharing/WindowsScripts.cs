using System.Globalization;
using System.Net;
using System.Text;
using NetShareHub.Domain.Enums;

namespace NetShareHub.Infrastructure.Services.Sharing;

/// <summary>État IPv4 d'une interface avant modification, nécessaire pour la restaurer à l'identique.</summary>
public sealed record InterfaceState(
    int Index,
    bool Dhcp,
    bool Forwarding,
    bool AutoMetric,
    int Metric,
    List<InterfaceAddress> Addrs,
    string? Gateway);

public sealed record InterfaceAddress(string Ip, int Prefix);

/// <summary>
/// Scripts PowerShell 5.1 utilisés par le moteur Windows. Ils sont regroupés ici pour pouvoir être
/// validés syntaxiquement par les tests et relus d'un seul coup d'œil.
/// </summary>
internal static class WindowsScripts
{
    public const string NatName = "NetShareHub";

    /// <summary>Adresse que Windows donne à l'adaptateur privé d'ICS et du Point d'accès mobile.</summary>
    public const string IcsScopeAddress = "192.168.137.1";
    public const string FirewallRuleName = "NetShareHub DHCP";

    // PowerShell reconnaît comme guillemet simple l'apostrophe ASCII et aussi les apostrophes typographiques U+2018 à U+201B
    // (‘ ’ ‚ ‛) : un SSID « Marie’s PC » fermerait la chaîne et permettrait d'injecter du code exécuté en administrateur.
    private static readonly char[] SingleQuotes = { '\'', '‘', '’', '‚', '‛' };

    /// <summary>
    /// Entoure une chaîne de guillemets simples PowerShell (aucune interprétation possible) : chaque guillemet simple, ASCII
    /// ou typographique, est doublé, comme le fait <c>CodeGeneration.EscapeSingleQuotedStringContent</c> de PowerShell.
    /// </summary>
    public static string Quote(string value)
    {
        var quoted = new StringBuilder(value.Length + 2).Append('\'');
        foreach (var c in value)
        {
            if (Array.IndexOf(SingleQuotes, c) >= 0)
            {
                quoted.Append(c);
            }

            quoted.Append(c);
        }

        return quoted.Append('\'').ToString();
    }

    // --- Point d'accès mobile (API WinRT) -------------------------------------------------------------------

    // PowerShell ne choisit pas seul une surcharge d'AsTask pour un objet WinRT (vu comme __ComObject) :
    // « Surcharge introuvable pour AsTask ». Les deux surcharges sont donc résolues par réflexion.
    internal const string WinRtPrelude = """
        Add-Type -AssemblyName System.Runtime.WindowsRuntime
        $null = [Windows.Networking.Connectivity.NetworkInformation,Windows.Networking.Connectivity,ContentType=WindowsRuntime]
        $null = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager,Windows.Networking.NetworkOperators,ContentType=WindowsRuntime]
        $null = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringOperationResult,Windows.Networking.NetworkOperators,ContentType=WindowsRuntime]
        $asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
        function Await($op, $type) { $t = $asTaskGeneric.MakeGenericMethod($type).Invoke($null, @($op)); $null = $t.Wait(-1); $t.Result }
        $asTaskAction = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction' })[0]
        function AwaitAction($op) { $t = $asTaskAction.Invoke($null, @($op)); $null = $t.Wait(-1) }
        $resultType = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringOperationResult]
        function Get-ShareProfile($guid) {
          if ($guid) {
            foreach ($p in [Windows.Networking.Connectivity.NetworkInformation]::GetConnectionProfiles()) {
              if ($p.NetworkAdapter.NetworkAdapterId -eq [guid]$guid -and $p.GetNetworkConnectivityLevel() -eq 'InternetAccess') { return $p }
            }
          }
          $p = [Windows.Networking.Connectivity.NetworkInformation]::GetInternetConnectionProfile()
          if (-not $p) { throw 'Aucune connexion Internet active à partager.' }
          $p
        }
        """;

    /// <summary>Sonde (lecture seule) : renvoie « CAP:Enabled » quand le Point d'accès mobile est utilisable.</summary>
    public static string ProbeHotspot() => WinRtPrelude + """

        $p = [Windows.Networking.Connectivity.NetworkInformation]::GetInternetConnectionProfile()
        if (-not $p) { 'CAP:NoInternetProfile'; return }
        $cap = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::GetTetheringCapabilityFromConnectionProfile($p)
        $mgr = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($p)
        "CAP:$cap"
        "STATE:$($mgr.TetheringOperationalState)"
        """;

    public static string StartHotspot(string adapterGuid, string ssid, string passphrase, WifiBand band)
    {
        var bandName = band switch
        {
            WifiBand.Band2_4GHz => "TwoPointFourGigahertz",
            WifiBand.Band5GHz => "FiveGigahertz",
            _ => "Auto",
        };

        return WinRtPrelude + $$"""

            $profile = Get-ShareProfile {{Quote(adapterGuid)}}
            $cap = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::GetTetheringCapabilityFromConnectionProfile($profile)
            if ($cap -ne 'Enabled') { throw "Le Point d'accès mobile n'est pas disponible sur cette machine ($cap)." }
            $mgr = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($profile)
            if ($mgr.TetheringOperationalState -eq 'On') { throw "Le Point d'accès mobile de Windows est déjà actif : désactivez-le dans Paramètres avant de continuer." }
            $cfg = $mgr.GetCurrentAccessPointConfiguration()
            $cfg.Ssid = {{Quote(ssid)}}
            $cfg.Passphrase = {{Quote(passphrase)}}
            try {
              $null = [Windows.Networking.NetworkOperators.TetheringWiFiBand,Windows.Networking.NetworkOperators,ContentType=WindowsRuntime]
              $cfg.Band = [Windows.Networking.NetworkOperators.TetheringWiFiBand]::{{bandName}}
            } catch { }
            AwaitAction ($mgr.ConfigureAccessPointAsync($cfg))
            $r = Await ($mgr.StartTetheringAsync()) $resultType
            if ($r.Status -ne 'Success') { throw ('Échec du démarrage du hotspot : ' + $r.Status + ' ' + $r.AdditionalErrorMessage) }
            'OK'
            """;
    }

    /// <summary>État du Point d'accès mobile pour la connexion source (« STATE:On » s'il émet déjà).</summary>
    public static string QueryHotspotState(string adapterGuid) => WinRtPrelude + $$"""

        $profile = Get-ShareProfile {{Quote(adapterGuid)}}
        $mgr = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($profile)
        "STATE:$($mgr.TetheringOperationalState)"
        """;

    public static string StopHotspot() => WinRtPrelude + """

        $p = [Windows.Networking.Connectivity.NetworkInformation]::GetInternetConnectionProfile()
        if (-not $p) { $p = [Windows.Networking.Connectivity.NetworkInformation]::GetConnectionProfiles() | Select-Object -First 1 }
        if (-not $p) { return }
        $mgr = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($p)
        if ($mgr.TetheringOperationalState -eq 'On') { $null = Await ($mgr.StopTetheringAsync()) $resultType }
        'OK'
        """;

    // --- Partage de connexion Internet (ICS) ----------------------------------------------------------------

    private const string IcsPrelude = """
        $m = New-Object -ComObject HNetCfg.HNetShare
        function Find-Conn([string]$name) {
          foreach ($c in $m.EnumEveryConnection) { if ($m.NetConnectionProps.Invoke($c).Name -eq $name) { return $c } }
          throw "Connexion introuvable : $name"
        }
        function Clear-Sharing() {
          foreach ($c in $m.EnumEveryConnection) {
            if ($m.NetConnectionProps.Invoke($c).Name -like 'vEthernet*') { continue }
            $cfg = $m.INetSharingConfigurationForINetConnection.Invoke($c)
            if ($cfg.SharingEnabled) { $cfg.DisableSharing() }
          }
        }
        """;

    public static string EnableIcs(string publicName, string privateName) => IcsPrelude + $$"""

        $svc = Get-Service SharedAccess
        if ($svc.StartType -eq 'Disabled') { throw "Le service « Partage de connexion Internet (ICS) » est désactivé sur cette machine." }
        if ($svc.Status -ne 'Running') { Start-Service SharedAccess }
        Clear-Sharing
        $pub = $m.INetSharingConfigurationForINetConnection.Invoke((Find-Conn {{Quote(publicName)}}))
        $prv = $m.INetSharingConfigurationForINetConnection.Invoke((Find-Conn {{Quote(privateName)}}))
        $pub.EnableSharing(0)
        $prv.EnableSharing(1)
        'OK'
        """;

    public static string DisableIcs() => IcsPrelude + """

        Clear-Sharing
        'OK'
        """;

    /// <summary>
    /// Désactiver ICS laisse l'adresse fixe 192.168.137.1 sur l'ancien adaptateur privé. Tant qu'elle y reste, ICS y lie
    /// son DHCP : l'adaptateur du Point d'accès mobile se rabat sur .2 et les clients n'obtiennent plus d'adresse.
    /// L'adaptateur Wi-Fi Direct (le hotspot lui-même) est géré par Windows et n'est pas touché.
    /// </summary>
    public static string ClearIcsLeftovers() => $$"""
        foreach ($a in @(Get-NetIPAddress -AddressFamily IPv4 -IPAddress {{Quote(IcsScopeAddress)}} -ErrorAction SilentlyContinue)) {
          if ($a.PrefixOrigin -ne 'Manual') { continue }
          $ad = Get-NetAdapter -InterfaceIndex $a.InterfaceIndex -IncludeHidden -ErrorAction SilentlyContinue
          if (-not $ad -or $ad.InterfaceDescription -like '*Wi-Fi Direct*') { continue }
          $a | Remove-NetIPAddress -Confirm:$false
          if (-not (Get-NetIPAddress -InterfaceIndex $a.InterfaceIndex -AddressFamily IPv4 -PrefixOrigin Manual -ErrorAction SilentlyContinue)) {
            Set-NetIPInterface -InterfaceIndex $a.InterfaceIndex -AddressFamily IPv4 -Dhcp Enabled
          }
          "CLEARED:$($ad.Name)"
        }
        'OK'
        """;

    // --- NAT intégré (plusieurs adaptateurs) ------------------------------------------------------------------

    public static string QueryInterfaces(IEnumerable<int> indexes) => $$"""
        $result = foreach ($i in @({{string.Join(",", indexes)}})) {
          $if = Get-NetIPInterface -InterfaceIndex $i -AddressFamily IPv4
          $addrs = @(Get-NetIPAddress -InterfaceIndex $i -AddressFamily IPv4 -ErrorAction SilentlyContinue | ForEach-Object { [pscustomobject]@{ Ip = [string]$_.IPAddress; Prefix = [int]$_.PrefixLength } })
          $gw = Get-NetRoute -InterfaceIndex $i -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Select-Object -First 1
          [pscustomobject]@{
            Index = [int]$i
            Dhcp = ($if.Dhcp -eq 'Enabled')
            Forwarding = ($if.Forwarding -eq 'Enabled')
            AutoMetric = ($if.AutomaticMetric -eq 'Enabled')
            Metric = [int]$if.InterfaceMetric
            Addrs = $addrs
            Gateway = $(if ($gw -and $gw.NextHop -ne '0.0.0.0') { [string]$gw.NextHop } else { $null })
          }
        }
        ConvertTo-Json -InputObject @($result) -Depth 4 -Compress
        """;

    public static string ConfigureTarget(int index, IPAddress address, int prefix) => $$"""
        $i = {{index}}
        Set-NetIPInterface -InterfaceIndex $i -AddressFamily IPv4 -Dhcp Disabled -Forwarding Enabled
        Get-NetRoute -InterfaceIndex $i -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue
        Get-NetIPAddress -InterfaceIndex $i -AddressFamily IPv4 -ErrorAction SilentlyContinue | Remove-NetIPAddress -Confirm:$false -ErrorAction SilentlyContinue
        $null = New-NetIPAddress -InterfaceIndex $i -IPAddress {{Quote(address.ToString())}} -PrefixLength {{prefix}}
        'OK'
        """;

    public static string RestoreInterface(InterfaceState state, string adapterName)
    {
        var script = new StringBuilder();
        script.AppendLine(CultureInfo.InvariantCulture, $"$i = {state.Index}");
        script.AppendLine("Get-NetIPAddress -InterfaceIndex $i -AddressFamily IPv4 -ErrorAction SilentlyContinue | Remove-NetIPAddress -Confirm:$false -ErrorAction SilentlyContinue");
        if (state.Dhcp || state.Addrs.Count == 0)
        {
            script.AppendLine("Set-NetIPInterface -InterfaceIndex $i -AddressFamily IPv4 -Dhcp Enabled");
            script.AppendLine(CultureInfo.InvariantCulture, $"try {{ ipconfig /renew {Quote(adapterName)} | Out-Null }} catch {{ }}");
        }
        else
        {
            for (var n = 0; n < state.Addrs.Count; n++)
            {
                var addr = state.Addrs[n];
                var gateway = n == 0 && state.Gateway is not null ? $" -DefaultGateway {Quote(state.Gateway)}" : string.Empty;
                script.AppendLine(CultureInfo.InvariantCulture,
                    $"$null = New-NetIPAddress -InterfaceIndex $i -IPAddress {Quote(addr.Ip)} -PrefixLength {addr.Prefix}{gateway}");
            }
        }

        script.AppendLine(CultureInfo.InvariantCulture,
            $"Set-NetIPInterface -InterfaceIndex $i -AddressFamily IPv4 -Forwarding {(state.Forwarding ? "Enabled" : "Disabled")}");
        return script.ToString();
    }

    public static string SetForwarding(int index, bool enabled)
        => $"Set-NetIPInterface -InterfaceIndex {index} -AddressFamily IPv4 -Forwarding {(enabled ? "Enabled" : "Disabled")}";

    public static string SetMetric(int index, int? metric)
        => metric is null
            ? $"Set-NetIPInterface -InterfaceIndex {index} -AddressFamily IPv4 -AutomaticMetric Enabled"
            : $"Set-NetIPInterface -InterfaceIndex {index} -AddressFamily IPv4 -InterfaceMetric {metric.Value}";

    public static string CreateNat(string prefix) => $$"""
        Get-NetNat -Name {{Quote(NatName)}} -ErrorAction SilentlyContinue | Remove-NetNat -Confirm:$false
        $null = New-NetNat -Name {{Quote(NatName)}} -InternalIPInterfaceAddressPrefix {{Quote(prefix)}}
        'OK'
        """;

    // Les scripts d'annulation finissent par 'OK' : sans cela, PowerShell sort avec le code 1 quand il n'y avait rien à retirer
    // (l'erreur « introuvable » est masquée mais $? reste faux), ce qui produirait de faux avertissements à chaque arrêt.
    public static string RemoveNat()
        => $"Get-NetNat -Name {Quote(NatName)} -ErrorAction SilentlyContinue | Remove-NetNat -Confirm:$false; 'OK'";

    public static string AddDhcpFirewallRule() => $$"""
        Get-NetFirewallRule -DisplayName {{Quote(FirewallRuleName)}} -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        $null = New-NetFirewallRule -DisplayName {{Quote(FirewallRuleName)}} -Direction Inbound -Protocol UDP -LocalPort 67 -Action Allow -Profile Any
        'OK'
        """;

    public static string RemoveDhcpFirewallRule()
        => $"Get-NetFirewallRule -DisplayName {Quote(FirewallRuleName)} -ErrorAction SilentlyContinue | Remove-NetFirewallRule; 'OK'";
}
