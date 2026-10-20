using System.Buffers.Binary;
using System.Net;

namespace NetShareHub.Domain.ValueObjects;

/// <summary>Sous-réseau IPv4 (adresse réseau + longueur de préfixe), toujours normalisé.</summary>
public readonly record struct Ipv4Subnet
{
    public uint Network { get; }

    public int PrefixLength { get; }

    public Ipv4Subnet(uint address, int prefixLength)
    {
        if (prefixLength is < 0 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(prefixLength));
        }

        PrefixLength = prefixLength;
        Network = address & MaskFor(prefixLength);
    }

    public Ipv4Subnet(IPAddress address, int prefixLength) : this(ToUInt(address), prefixLength) { }

    public uint Mask => MaskFor(PrefixLength);

    public IPAddress NetworkAddress => ToAddress(Network);

    public IPAddress SubnetMask => ToAddress(Mask);

    public IPAddress BroadcastAddress => ToAddress(Network | ~Mask);

    /// <summary>Nombre d'adresses du bloc (réseau et broadcast inclus).</summary>
    public ulong Size => 1UL << (32 - PrefixLength);

    public bool Contains(IPAddress address) => (ToUInt(address) & Mask) == Network;

    public bool Overlaps(Ipv4Subnet other)
    {
        var shortest = Math.Min(PrefixLength, other.PrefixLength);
        var mask = MaskFor(shortest);
        return (Network & mask) == (other.Network & mask);
    }

    /// <summary>Adresse hôte à la position <paramref name="offset"/> dans le bloc (1 = première adresse utilisable).</summary>
    public IPAddress HostAddress(uint offset) => ToAddress(Network + offset);

    /// <summary>Sous-bloc de longueur <paramref name="prefixLength"/> à l'index donné (ex. le 2e /24 d'un /22).</summary>
    public Ipv4Subnet Sub(int prefixLength, int index)
    {
        if (prefixLength < PrefixLength)
        {
            throw new ArgumentOutOfRangeException(nameof(prefixLength));
        }

        var blockSize = 1UL << (32 - prefixLength);
        var offset = blockSize * (ulong)index;
        if (offset + blockSize > Size)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return new Ipv4Subnet((uint)(Network + offset), prefixLength);
    }

    public static Ipv4Subnet FromAddressAndMask(IPAddress address, IPAddress mask)
        => new(address, PrefixOf(mask));

    public static Ipv4Subnet Parse(string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var ip) || !int.TryParse(parts[1], out var prefix))
        {
            throw new FormatException($"Sous-réseau invalide : {cidr}");
        }

        return new Ipv4Subnet(ip, prefix);
    }

    public override string ToString() => $"{NetworkAddress}/{PrefixLength}";

    public static int PrefixOf(IPAddress mask)
    {
        var value = ToUInt(mask);
        return System.Numerics.BitOperations.PopCount(value);
    }

    public static uint ToUInt(IPAddress address)
        => BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());

    public static IPAddress ToAddress(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes);
    }

    private static uint MaskFor(int prefixLength)
        => prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
}
