namespace NetShareHub.Domain.Exceptions;

/// <summary>Erreur fonctionnelle attendue lors du partage de connexion (message affichable à l'utilisateur).</summary>
public class SharingException : Exception
{
    public SharingException(string message) : base(message) { }

    public SharingException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Opération nécessitant des droits administrateur / root.</summary>
public sealed class PrivilegeRequiredException : SharingException
{
    public PrivilegeRequiredException()
        : base("Cette opération nécessite les droits administrateur (Windows) ou root (Linux).") { }
}

/// <summary>Erreur de configuration du service Dispatch.</summary>
public sealed class DispatchException : Exception
{
    public DispatchException(string message) : base(message) { }

    public DispatchException(string message, Exception inner) : base(message, inner) { }
}
