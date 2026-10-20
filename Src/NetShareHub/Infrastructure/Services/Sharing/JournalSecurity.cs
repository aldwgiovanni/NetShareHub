using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace NetShareHub.Infrastructure.Services.Sharing;

/// <summary>
/// Protège le journal d'annulation, rejoué avec les droits administrateur au lancement suivant. Sous Windows,
/// <c>C:\ProgramData</c> laisse tout utilisateur créer des dossiers : sans ces contrôles, un compte standard pourrait y
/// déposer un journal et le faire exécuter par l'instance élevée.
/// <list type="bullet">
/// <item>le dossier n'accorde l'accès qu'à SYSTEM et aux Administrateurs (Windows) ou à root en 0700 (Linux) ;</item>
/// <item>un journal dont le propriétaire n'est pas SYSTEM / Administrateurs (ou root), ou qui est un lien, est refusé.</item>
/// </list>
/// </summary>
internal static class JournalSecurity
{
    /// <summary>Crée le dossier ou en corrige les droits. Renvoie faux (avec la raison) si le dossier n'est pas sûr.</summary>
    public static bool TrySecureDirectory(string directory, out string? reason)
    {
        try
        {
            if (AppPaths.IsLink(directory))
            {
                reason = "le dossier est un lien ou un point de jonction";
                return false;
            }

            if (!Environment.IsPrivilegedProcess)
            {
                // Une instance non élevée ne rejoue jamais le journal ; elle ne doit pas non plus toucher aux droits du dossier.
                reason = "droits administrateur requis";
                return false;
            }

            if (OperatingSystem.IsWindows())
            {
                var info = new DirectoryInfo(directory);
                if (info.Exists)
                {
                    info.SetAccessControl(BuildDirectorySecurity());
                }
                else
                {
                    info.Create(BuildDirectorySecurity());
                }

                reason = IsTrustedOwner(info.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)
                    ? null
                    : "le dossier n'appartient pas aux Administrateurs";
                return reason is null;
            }

            const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(directory, OwnerOnly);
            File.SetUnixFileMode(directory, OwnerOnly);
            var status = UnixFileStatus.ForPath(directory);
            reason = status is { IsDirectory: true, Uid: 0, WritableByOthers: false } ? null : "le dossier n'appartient pas à root";
            return reason is null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PrivilegeNotHeldException or InvalidOperationException)
        {
            reason = ex.Message;
            return false;
        }
    }

    /// <summary>Vrai si le fichier ouvert peut être rejoué : fichier ordinaire (pas un lien) appartenant à l'administration.</summary>
    public static bool IsTrustedJournal(FileStream stream, out string? reason)
    {
        if (AppPaths.IsLink(stream.Name))
        {
            reason = "le journal est un lien";
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            var owner = stream.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            reason = IsTrustedOwner(owner) ? null : $"propriétaire non administrateur ({owner?.Value ?? "inconnu"})";
            return reason is null;
        }

        var status = UnixFileStatus.ForHandle(stream.SafeFileHandle);
        reason = status is { IsRegularFile: true, Uid: 0, WritableByOthers: false } ? null : $"propriétaire non root (uid {status.Uid})";
        return reason is null;
    }

    /// <summary>Après écriture : le journal appartient explicitement aux Administrateurs (et non au compte qui l'a écrit).</summary>
    public static void TakeOwnership(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return;
        }

        var info = new FileInfo(path);
        var security = info.GetAccessControl();
        security.SetOwner(Administrators);
        info.SetAccessControl(security);
    }

    /// <summary>Accès complet pour SYSTEM et les Administrateurs, sans héritage du parent (qui accorde l'écriture à Utilisateurs).</summary>
    [SupportedOSPlatform("windows")]
    public static DirectorySecurity BuildDirectorySecurity()
    {
        var security = new DirectorySecurity();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { LocalSystem, Administrators })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        return security;
    }

    [SupportedOSPlatform("windows")]
    public static bool IsTrustedOwner(SecurityIdentifier? owner)
        => owner is not null && (owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier Administrators => new(WellKnownSidType.BuiltinAdministratorsSid, null);

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier LocalSystem => new(WellKnownSidType.LocalSystemSid, null);
}
