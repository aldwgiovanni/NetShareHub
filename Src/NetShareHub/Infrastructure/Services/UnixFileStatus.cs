using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NetShareHub.Infrastructure.Services;

/// <summary>
/// Propriétaire et type d'un fichier Unix, que .NET n'expose pas. On passe par <c>libSystem.Native</c> (livrée avec le
/// runtime) plutôt que par <c>stat</c> de la libc, dont la structure varie selon l'architecture : seuls les premiers champs
/// de <c>FileStatus</c> (Flags, Mode, Uid, Gid), stables, sont lus.
/// </summary>
internal readonly record struct UnixFileStatus(int Mode, uint Uid, uint Gid)
{
    private const int TypeMask = 0xF000;
    private const int RegularType = 0x8000;
    private const int DirectoryType = 0x4000;
    private const int SymlinkType = 0xA000;
    private const int GroupOrOtherWrite = 0x12; // S_IWGRP | S_IWOTH

    // Tampon largement supérieur à sizeof(FileStatus) (≈ 130 octets) : seule compte la position des premiers champs.
    private const int BufferSize = 512;

    public bool IsRegularFile => (Mode & TypeMask) == RegularType;

    public bool IsDirectory => (Mode & TypeMask) == DirectoryType;

    public bool IsSymlink => (Mode & TypeMask) == SymlinkType;

    public bool WritableByOthers => (Mode & GroupOrOtherWrite) != 0;

    /// <summary>État du chemin lui-même, sans suivre de lien symbolique (<c>lstat</c>).</summary>
    public static UnixFileStatus? ForPath(string path)
    {
        var buffer = new byte[BufferSize];
        return LStat(path, buffer) == 0 ? Parse(buffer) : null;
    }

    /// <summary>État du fichier réellement ouvert (<c>fstat</c>) : insensible au remplacement du chemin entre-temps.</summary>
    public static UnixFileStatus ForHandle(SafeFileHandle handle)
    {
        var buffer = new byte[BufferSize];
        return FStat(handle, buffer) == 0
            ? Parse(buffer)
            : throw new IOException($"fstat impossible (errno {Marshal.GetLastPInvokeError()}).");
    }

    private static UnixFileStatus Parse(byte[] buffer)
        => new(BitConverter.ToInt32(buffer, 4), BitConverter.ToUInt32(buffer, 8), BitConverter.ToUInt32(buffer, 12));

    [DllImport("libSystem.Native", EntryPoint = "SystemNative_LStat", SetLastError = true)]
    private static extern int LStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] output);

    [DllImport("libSystem.Native", EntryPoint = "SystemNative_FStat", SetLastError = true)]
    private static extern int FStat(SafeFileHandle fd, [Out] byte[] output);
}
