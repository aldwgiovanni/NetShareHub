using System.Text;

namespace NetShareHub.Application.Contracts.Services;

/// <summary>Commande système à exécuter (aucun interpréteur de shell : les arguments sont passés tels quels).</summary>
/// <param name="FileName">Exécutable (ex. « ip », « nmcli », « powershell.exe »).</param>
/// <param name="Arguments">Arguments, un élément par argument.</param>
/// <param name="StdIn">Contenu envoyé sur l'entrée standard.</param>
/// <param name="TimeoutSeconds">Délai maximal avant arrêt forcé.</param>
/// <param name="IgnoreFailure">Si vrai, un code de sortie non nul n'est pas une erreur (commandes de nettoyage).</param>
/// <param name="Description">Libellé lisible pour les journaux et les messages d'erreur.</param>
public sealed record CommandSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? StdIn = null,
    int TimeoutSeconds = 30,
    bool IgnoreFailure = false,
    string? Description = null)
{
    // Le « trap » écrit le message d'erreur seul sur stderr : sans lui, PowerShell enveloppe l'erreur en CLIXML
    // avec la position dans le script, dans la langue du système (« At line: » / « Au caractère Ligne: »…).
    private const string PowerShellPrelude =
        "$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';" +
        "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8;" +
        "trap { [Console]::Error.WriteLine($_.Exception.Message); exit 1 };";

    /// <summary>Script PowerShell Windows (5.1), transmis en Base64 pour éviter tout problème de guillemets.</summary>
    public static CommandSpec PowerShell(string script, string? description = null, int timeoutSeconds = 60, bool ignoreFailure = false)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(PowerShellPrelude + script));
        return new CommandSpec(
            "powershell.exe",
            new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded },
            null,
            timeoutSeconds,
            ignoreFailure,
            description)
        {
            Script = script,
        };
    }

    public static CommandSpec Of(string fileName, params string[] arguments)
        => new(fileName, arguments);

    /// <summary>Script PowerShell d'origine (renseigné par <see cref="PowerShell"/>), utile aux tests et aux journaux.</summary>
    public string? Script { get; init; }

    public CommandSpec Lenient() => this with { IgnoreFailure = true };

    public CommandSpec Describe(string description) => this with { Description = description };

    /// <summary>Représentation courte pour les journaux.</summary>
    public string Display => Script is not null
        ? $"powershell: {Description ?? Truncate(Script, 80)}"
        : $"{FileName} {string.Join(' ', Arguments)}";

    private static string Truncate(string value, int length)
        => value.Length <= length ? value : value[..length] + "…";
}

/// <summary>Résultat d'une commande système.</summary>
public sealed record CommandResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

/// <summary>Exécute des commandes système. Abstraction indispensable pour tester les moteurs de partage sans toucher au réseau.</summary>
public interface ICommandRunner
{
    Task<CommandResult> RunAsync(CommandSpec spec, CancellationToken cancellationToken = default);
}
