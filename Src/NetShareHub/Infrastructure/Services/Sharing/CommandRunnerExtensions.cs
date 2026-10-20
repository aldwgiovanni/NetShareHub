using NetShareHub.Application.Contracts.Services;
using NetShareHub.Domain.Exceptions;

namespace NetShareHub.Infrastructure.Services.Sharing;

internal static class CommandRunnerExtensions
{
    /// <summary>Exécute la commande et transforme un échec en <see cref="SharingException"/> lisible.</summary>
    public static async Task<CommandResult> RunOrThrowAsync(this ICommandRunner runner, CommandSpec spec, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(spec, cancellationToken);
        if (!result.Success && !spec.IgnoreFailure)
        {
            var detail = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
            throw new SharingException($"{spec.Description ?? spec.Display} : {detail}".Trim());
        }

        return result;
    }
}
