using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NetShareHub.Infrastructure.Services;

/// <summary>
/// Emplacements des fichiers de l'application, et écriture dans le profil utilisateur.
/// <para>
/// Sous Linux, l'application relancée par <c>pkexec</c> tourne en root mais écrit dans <c>~/.config</c>, que l'utilisateur
/// (et tout programme lancé sous son compte) contrôle : chaque écriture refuse les liens, crée ses fichiers en exclusif et
/// rend la propriété par descripteur (<c>fchown</c>) ou sans suivre les liens (<c>lchown</c>).
/// </para>
/// </summary>
public static class AppPaths
{
    private const string AppFolder = "NetShareHub";

    /// <summary>Préférences de l'utilisateur (propriété de l'utilisateur même si l'application tourne en root).</summary>
    public static string SettingsDirectory
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolder);
            }

            var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(config) || Environment.IsPrivilegedProcess)
            {
                config = Path.Combine(InvokingUserHome(), ".config");
            }

            return Path.Combine(config, "netsharehub");
        }
    }

    /// <summary>Journal d'annulation : état machine, indépendant de l'utilisateur.</summary>
    public static string JournalDirectory
        => OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppFolder)
            : "/var/lib/netsharehub";

    /// <summary>
    /// Crée le dossier s'il n'existe pas. Sous Linux, quand l'application tourne en root après <c>pkexec</c>,
    /// le dossier est rendu à l'utilisateur d'origine pour qu'il puisse encore l'utiliser sans élévation.
    /// <para>
    /// Les dossiers propres à l'application (le dossier lui-même, et son parent s'il s'agit du dossier « netsharehub »)
    /// ne doivent pas être des liens : un lien posé dans le profil redirigerait les écritures de l'instance root vers un
    /// dossier système.
    /// </para>
    /// </summary>
    /// <exception cref="IOException">Le dossier est un lien symbolique ou un point de jonction.</exception>
    public static void EnsureUserDirectory(string path)
    {
        RefuseLink(path);
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent) && string.Equals(Path.GetFileName(parent), AppFolder, StringComparison.OrdinalIgnoreCase))
        {
            RefuseLink(parent);
        }

        if (Directory.Exists(path))
        {
            return;
        }

        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
        {
            EnsureUserDirectory(parent);
        }

        Directory.CreateDirectory(path);
        GiveToInvokingUser(path);
    }

    /// <summary>
    /// Remet un fichier ou dossier à l'utilisateur qui a lancé <c>pkexec</c> (sans effet ailleurs). <c>lchown</c> : si le chemin
    /// a été remplacé par un lien entre-temps, c'est le lien qui change de propriétaire, jamais sa cible.
    /// </summary>
    public static void GiveToInvokingUser(string path)
    {
        if (InvokingUid is { } uid)
        {
            _ = lchown(path, uid, uint.MaxValue); // -1 : groupe inchangé
        }
    }

    /// <summary>Remet le fichier ouvert à l'utilisateur d'origine (<c>fchown</c> : porte sur le fichier ouvert, pas sur un chemin).</summary>
    public static void GiveToInvokingUser(SafeFileHandle handle)
    {
        if (InvokingUid is not { } uid)
        {
            return;
        }

        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            _ = fchown(handle.DangerousGetHandle().ToInt32(), uid, uint.MaxValue);
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Écrit un fichier du profil de façon atomique : le fichier temporaire restant (ou un lien piégé à sa place) est
    /// supprimé sans être suivi, puis recréé en exclusif avec les droits 0600 ; le propriétaire est rendu par descripteur et
    /// le fichier final remplacé par renommage, qui ne suit pas non plus les liens.
    /// </summary>
    public static async Task WriteUserFileAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        EnsureUserDirectory(Path.GetDirectoryName(path)!);

        var temp = path + ".tmp";
        File.Delete(temp);
        await using (var stream = new FileStream(temp, NewFileOptions()))
        {
            GiveToInvokingUser(stream.SafeFileHandle);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteAsync(content.AsMemory(), cancellationToken);
        }

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Ouvre en lecture un fichier du profil, ou renvoie <c>null</c> s'il n'existe pas.</summary>
    /// <exception cref="IOException">Le fichier n'appartient pas à l'utilisateur (lien vers un fichier système, par exemple).</exception>
    public static FileStream? OpenUserFileForRead(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (CheckOwnedByUser(stream))
        {
            return stream;
        }

        stream.Dispose();
        throw new IOException($"{path} n'appartient pas à l'utilisateur : fichier ignoré.");
    }

    /// <summary>
    /// Ouvre un fichier du profil en ajout, en le créant au besoin. Un fichier qui n'appartient pas à l'utilisateur
    /// (lien, ou lien physique vers un fichier système) est retiré — l'entrée du dossier seulement — et remplacé par un fichier neuf.
    /// </summary>
    public static FileStream OpenUserFileForAppend(string path)
    {
        EnsureUserDirectory(Path.GetDirectoryName(path)!);

        if (File.Exists(path) && !IsLink(path))
        {
            // Ouverture sans troncature : si le chemin a été remplacé entre-temps, rien n'est écrit avant le contrôle.
            var existing = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            if (CheckOwnedByUser(existing))
            {
                existing.Seek(0, SeekOrigin.End);
                return existing;
            }

            existing.Dispose();
        }

        File.Delete(path);
        var stream = new FileStream(path, NewFileOptions());
        GiveToInvokingUser(stream.SafeFileHandle);
        return stream;
    }

    /// <summary>Règle appliquée par l'instance root : fichier ordinaire appartenant à l'utilisateur qui a lancé pkexec.</summary>
    internal static bool IsAcceptableUserFile(UnixFileStatus status, uint invokingUid)
        => status.IsRegularFile && status.Uid == invokingUid;

    private static bool CheckOwnedByUser(FileStream stream)
        => InvokingUid is not { } uid || IsAcceptableUserFile(UnixFileStatus.ForHandle(stream.SafeFileHandle), uid);

    private static FileStreamOptions NewFileOptions()
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            // Le fichier peut contenir le mot de passe du hotspot : lecture réservée au propriétaire, dès la création.
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return options;
    }

    private static void RefuseLink(string path)
    {
        if (IsLink(path))
        {
            throw new IOException($"{path} est un lien : refusé pour ne pas écrire hors du profil.");
        }
    }

    internal static bool IsLink(string path)
    {
        // LinkTarget repère aussi un lien cassé ; Attributes n'a de sens que si le chemin existe (sinon -1 : tous les bits).
        var info = new FileInfo(path);
        return info.LinkTarget is not null
               || ((info.Exists || Directory.Exists(path)) && info.Attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    /// <summary>Utilisateur d'origine quand l'application tourne en root sous Linux après <c>pkexec</c> ; <c>null</c> sinon.</summary>
    private static uint? InvokingUid
        => OperatingSystem.IsLinux() && Environment.IsPrivilegedProcess
           && uint.TryParse(Environment.GetEnvironmentVariable("PKEXEC_UID"), out var uid)
            ? uid
            : null;

    private static string InvokingUserHome()
    {
        if (uint.TryParse(Environment.GetEnvironmentVariable("PKEXEC_UID"), out var uid))
        {
            try
            {
                foreach (var line in File.ReadLines("/etc/passwd"))
                {
                    var fields = line.Split(':');
                    if (fields.Length >= 6 && uint.TryParse(fields[2], out var candidate) && candidate == uid)
                    {
                        return fields[5];
                    }
                }
            }
            catch (IOException)
            {
                // /etc/passwd illisible : on retombe sur le HOME courant.
            }
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int lchown([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint owner, uint group);

    [DllImport("libc", SetLastError = true)]
    private static extern int fchown(int fd, uint owner, uint group);
}
