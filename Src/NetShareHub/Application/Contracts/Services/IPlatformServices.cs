using NetShareHub.Domain.Enums;

namespace NetShareHub.Application.Contracts.Services;

/// <summary>Informations sur le système hôte.</summary>
public interface IPlatformInfo
{
    HostPlatform Platform { get; }

    string Description { get; }
}

/// <summary>Gestion des privilèges (administrateur / root) nécessaires au partage de connexion.</summary>
public interface IPrivilegeService
{
    bool IsElevated { get; }

    /// <summary>Relance l'application avec élévation (UAC sous Windows, pkexec sous Linux). Renvoie faux si l'utilisateur annule.</summary>
    bool TryRelaunchElevated();
}
