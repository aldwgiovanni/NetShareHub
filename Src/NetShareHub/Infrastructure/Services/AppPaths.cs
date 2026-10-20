using System.Runtime.InteropServices;

namespace NetShareHub.Infrastructure.Services;

/// <summary>Emplacements des fichiers de l'application.</summary>
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
    /// </summary>
    public static void EnsureUserDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            return;
        }

        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
        {
            EnsureUserDirectory(parent);
        }

        Directory.CreateDirectory(path);
        GiveToInvokingUser(path);
    }

    /// <summary>Remet un fichier ou dossier à l'utilisateur qui a lancé <c>pkexec</c> (sans effet ailleurs).</summary>
    public static void GiveToInvokingUser(string path)
    {
        if (!OperatingSystem.IsLinux() || !Environment.IsPrivilegedProcess)
        {
            return;
        }

        if (uint.TryParse(Environment.GetEnvironmentVariable("PKEXEC_UID"), out var uid))
        {
            _ = chown(path, uid, uint.MaxValue); // -1 : groupe inchangé
        }
    }

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
    private static extern int chown([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint owner, uint group);
}
