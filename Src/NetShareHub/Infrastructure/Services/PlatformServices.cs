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

    /// <summary>
    /// pkexec rend la main tout de suite : la fenêtre d'authentification de polkit s'affiche ensuite. Une fois l'autorisation
    /// accordée, pkexec se remplace (execv) par le programme lancé ; s'il se termine avant, l'utilisateur a annulé (code 126)
    /// ou n'est pas autorisé (127). Le nom du processus (<c>/proc/PID/comm</c>, lisible par tous) dit lequel des deux arrive.
    /// </summary>
    private async Task<bool> WaitForPkexecAsync(Process pkexec, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (DateTime.UtcNow < deadline)
        {
            if (pkexec.HasExited)
            {
                _logger.LogWarning("Élévation annulée ou refusée (pkexec, code {Code}).", pkexec.ExitCode);
                return false;
            }

            string comm;
            try
            {
                comm = await File.ReadAllTextAsync($"/proc/{pkexec.Id}/comm", cancellationToken);
            }
            catch (IOException)
            {
                comm = string.Empty; // processus en train de se terminer : HasExited le dira au tour suivant
            }

            if (HasLeftPkexec(comm))
            {
                return true;
            }

            await Task.Delay(250, cancellationToken);
        }

        _logger.LogWarning("Élévation sans réponse : la demande d'authentification est restée ouverte trop longtemps.");
        return false;
    }

    /// <summary>Vrai quand le processus lancé par pkexec ne s'appelle plus « pkexec » : l'autorisation a été accordée.</summary>
    internal static bool HasLeftPkexec(string comm) => comm.Trim() is { Length: > 0 } name && name != "pkexec";

    public async Task<bool> TryRelaunchElevatedAsync(CancellationToken cancellationToken = default)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
        {
            return false;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                // UAC est synchrone : un refus lève une Win32Exception.
                Process.Start(new ProcessStartInfo(executable, SingleInstanceLock.RelaunchArgument) { UseShellExecute = true, Verb = "runas" });
                return true;
            }

            if (!OperatingSystem.IsLinux())
            {
                return false;
            }

            var environment = LinuxGuiVariables
                .Select(name => (Name: name, Value: Environment.GetEnvironmentVariable(name)))
                .Where(v => !string.IsNullOrEmpty(v.Value))
                .Select(v => new KeyValuePair<string, string>(v.Name, v.Value!));
            using var pkexec = Process.Start(BuildPkexecStartInfo(executable, environment));
            return pkexec is not null && await WaitForPkexecAsync(pkexec, cancellationToken);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // L'utilisateur a refusé l'élévation (UAC / polkit) ou le mécanisme est absent.
            _logger.LogWarning("Élévation annulée ou impossible : {Message}", ex.Message);
            return false;
        }
    }
}
