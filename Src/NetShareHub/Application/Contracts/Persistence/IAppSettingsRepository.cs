using NetShareHub.Domain.Dtos.Incoming;
using NetShareHub.Domain.Dtos.Outgoing;

namespace NetShareHub.Application.Contracts.Persistence;

/// <summary>Stockage des préférences utilisateur.</summary>
public interface IAppSettingsRepository
{
    /// <summary>Charge les préférences ; renvoie les valeurs par défaut si rien n'est enregistré ou si le fichier est illisible.</summary>
    Task<AppSettings_OutDto> GetAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AppSettings_InDto settings, CancellationToken cancellationToken = default);
}
