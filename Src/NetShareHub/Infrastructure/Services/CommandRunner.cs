using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NetShareHub.Application.Contracts.Services;

namespace NetShareHub.Infrastructure.Services;

/// <summary>Exécute des processus système sans passer par un shell (pas d'injection possible via les arguments).</summary>
public sealed partial class CommandRunner : ICommandRunner
{
    private readonly ILogger<CommandRunner> _logger;

    public CommandRunner(ILogger<CommandRunner> logger)
    {
        _logger = logger;
    }

    public async Task<CommandResult> RunAsync(CommandSpec spec, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(spec.FileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = spec.StdIn is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in spec.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        _logger.LogDebug("Exécution : {Command}", spec.Display);

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (stdout) { stdout.AppendLine(e.Data); } } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (stderr) { stderr.AppendLine(e.Data); } } };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return new CommandResult(127, string.Empty, $"Commande introuvable : {spec.FileName} ({ex.Message})");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (spec.StdIn is not null)
        {
            await process.StandardInput.WriteAsync(spec.StdIn);
            process.StandardInput.Close();
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(spec.TimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new CommandResult(124, stdout.ToString(), $"Délai dépassé ({spec.TimeoutSeconds}s) : {spec.Display}");
        }

        // Laisse les gestionnaires asynchrones vider leurs tampons.
        process.WaitForExit();

        var result = new CommandResult(process.ExitCode, stdout.ToString().Trim(), CleanStdErr(stderr.ToString()));
        if (!result.Success)
        {
            _logger.LogDebug("Code {Code} pour {Command} : {Error}", result.ExitCode, spec.Display, result.StdErr);
        }

        return result;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Le processus s'est terminé entre-temps.
        }
    }

    /// <summary>
    /// Quand une erreur PowerShell n'a pas été interceptée par le <c>trap</c> de <see cref="CommandSpec.PowerShell"/>,
    /// elle arrive en CLIXML sur stderr. On en garde le premier message (les suivants décrivent la position dans le
    /// script, dans la langue du système) et on ignore la progression.
    /// </summary>
    internal static string CleanStdErr(string stderr)
    {
        var text = stderr.Trim();
        if (!text.StartsWith("#< CLIXML", StringComparison.Ordinal))
        {
            return text;
        }

        var first = ClixmlErrorRegex().Match(text);
        return first.Success ? DecodeClixml(first.Groups[1].Value).Trim() : string.Empty;
    }

    private static string DecodeClixml(string value)
        => ClixmlEscapeRegex().Replace(
            System.Net.WebUtility.HtmlDecode(value),
            m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());

    [GeneratedRegex("""<S S="Error">(.*?)</S>""", RegexOptions.Singleline)]
    private static partial Regex ClixmlErrorRegex();

    [GeneratedRegex("_x([0-9A-Fa-f]{4})_")]
    private static partial Regex ClixmlEscapeRegex();
}
