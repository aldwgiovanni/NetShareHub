using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NetShareHub.Application.Contracts.Services;
using NetShareHub.Domain.Enums;

namespace NetShareHub.Infrastructure.Services;

public sealed class PlatformInfo : IPlatformInfo
{
    public HostPlatform Platform { get; } =
        OperatingSystem.IsWindows() ? HostPlatform.Windows :
        OperatingSystem.IsLinux() ? HostPlatform.Linux :
        HostPlatform.Unsupported;

    public string Description => RuntimeInformation.OSDescription;
}

public sealed class PrivilegeService : IPrivilegeService
{
    // Variables d'environnement graphiques à conserver quand pkexec relance l'application.
    private static readonly string[] LinuxGuiVariables =
    {
        "DISPLAY", "XAUTHORITY", "WAYLAND_DISPLAY", "XDG_RUNTIME_DIR", "XDG_SESSION_TYPE", "DBUS_SESSION_BUS_ADDRESS",
    };

    private readonly ILogger<PrivilegeService> _logger;

    public PrivilegeService(ILogger<PrivilegeService> logger)
    {
        _logger = logger;
    }

    public bool IsElevated => Environment.IsPrivilegedProcess;

    /// <summary>
    /// Commande <c>pkexec</c> qui relance l'application en root en conservant l'environnement graphique.
    /// Avec le script <c>netsharehub-elevate</c> installé à côté de l'exécutable (paquet .deb / install.sh), polkit affiche
    /// un message d'authentification propre à l'application ; sinon on passe par <c>env</c> (message générique).
    /// </summary>
    internal static ProcessStartInfo BuildPkexecStartInfo(string executable, IEnumerable<KeyValuePair<string, string>> environment)
    {
        var startInfo = new ProcessStartInfo("pkexec") { UseShellExecute = false };
        var wrapper = Path.Combine(Path.GetDirectoryName(executable) ?? string.Empty, ElevationWrapperName);

        startInfo.ArgumentList.Add(File.Exists(wrapper) ? wrapper : "env");
        foreach (var (name, value) in environment)
        {
            startInfo.ArgumentList.Add($"{name}={value}");
        }

        if (!File.Exists(wrapper))
        {
            startInfo.ArgumentList.Add(executable);
        }

        return startInfo;
    }

    internal const string ElevationWrapperName = "netsharehub-elevate";

    public bool TryRelaunchElevated()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
        {
            return false;
        }

        try
        {
            ProcessStartInfo startInfo;
            if (OperatingSystem.IsWindows())
            {
                startInfo = new ProcessStartInfo(executable, SingleInstanceLock.RelaunchArgument) { UseShellExecute = true, Verb = "runas" };
            }
            else if (OperatingSystem.IsLinux())
            {
                var environment = LinuxGuiVariables
                    .Select(name => (Name: name, Value: Environment.GetEnvironmentVariable(name)))
                    .Where(v => !string.IsNullOrEmpty(v.Value))
                    .Select(v => new KeyValuePair<string, string>(v.Name, v.Value!));
                startInfo = BuildPkexecStartInfo(executable, environment);
            }
            else
            {
                return false;
            }

            Process.Start(startInfo);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // L'utilisateur a refusé l'élévation (UAC / polkit) ou le mécanisme est absent.
            _logger.LogWarning("Élévation annulée ou impossible : {Message}", ex.Message);
            return false;
        }
    }
}
