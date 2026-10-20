using System.Buffers.Binary;
using System.Net;

namespace NetShareHub.Infrastructure.Services.Dhcp;

public enum DhcpMessageType : byte
{
    Discover = 1,
    Offer = 2,
    Request = 3,
    Decline = 4,
    Ack = 5,
    Nak = 6,
    Release = 7,
    Inform = 8,
}

public static class DhcpOption
{
    public const byte SubnetMask = 1;
    public const byte Router = 3;
    public const byte DnsServers = 6;
    public const byte BroadcastAddress = 28;
    public const byte RequestedIp = 50;
    public const byte LeaseTime = 51;
    public const byte MessageType = 53;
    public const byte ServerId = 54;
    public const byte RenewalTime = 58;
    public const byte RebindingTime = 59;
    public const byte End = 255;
    public const byte Pad = 0;
}

/// <summary>Message DHCP (RFC 2131) : en-tête BOOTP de 236 octets, cookie magique, options.</summary>
public sealed class DhcpPacket
{
    private const int FixedLength = 236;
    private static readonly byte[] MagicCookie = { 99, 130, 83, 99 };

    public byte Op { get; set; }

    public byte HardwareType { get; set; } = 1;

    public byte HardwareLength { get; set; } = 6;

    public byte Hops { get; set; }

    public uint TransactionId { get; set; }

    public ushort Seconds { get; set; }

    public ushort Flags { get; set; }

    public IPAddress ClientAddress { get; set; } = IPAddress.Any;

    public IPAddress YourAddress { get; set; } = IPAddress.Any;

    public IPAddress ServerAddress { get; set; } = IPAddress.Any;

    public IPAddress GatewayAddress { get; set; } = IPAddress.Any;

    /// <summary>Adresse matérielle du client (16 octets, dont HardwareLength significatifs).</summary>
    public byte[] ClientHardwareAddress { get; set; } = new byte[16];

    public Dictionary<byte, byte[]> Options { get; } = new();

    public DhcpMessageType? MessageType
        => Options.TryGetValue(DhcpOption.MessageType, out var v) && v.Length == 1 ? (DhcpMessageType)v[0] : null;

    public string MacAddress
        => string.Join(':', ClientHardwareAddress.Take(Math.Min((int)HardwareLength, 16)).Select(b => b.ToString("x2")));

    public IPAddress? RequestedIp
        => Options.TryGetValue(DhcpOption.RequestedIp, out var v) && v.Length == 4 ? new IPAddress(v) : null;

    public IPAddress? ServerIdentifier
        => Options.TryGetValue(DhcpOption.ServerId, out var v) && v.Length == 4 ? new IPAddress(v) : null;

    public bool WantsBroadcast => (Flags & 0x8000) != 0;

    public void SetOption(byte code, params byte[] value) => Options[code] = value;

    public void SetAddressOption(byte code, params IPAddress[] addresses)
        => Options[code] = addresses.SelectMany(a => a.GetAddressBytes()).ToArray();

    public void SetUIntOption(byte code, uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        Options[code] = bytes;
    }

    public static bool TryParse(ReadOnlySpan<byte> data, out DhcpPacket packet)
    {
        packet = null!;
        if (data.Length < FixedLength + MagicCookie.Length || !data.Slice(FixedLength, 4).SequenceEqual(MagicCookie))
        {
            return false;
        }

        var result = new DhcpPacket
        {
            Op = data[0],
            HardwareType = data[1],
            HardwareLength = data[2],
            Hops = data[3],
            TransactionId = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4)),
            Seconds = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(8, 2)),
            Flags = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(10, 2)),
            ClientAddress = new IPAddress(data.Slice(12, 4)),
            YourAddress = new IPAddress(data.Slice(16, 4)),
            ServerAddress = new IPAddress(data.Slice(20, 4)),
            GatewayAddress = new IPAddress(data.Slice(24, 4)),
            ClientHardwareAddress = data.Slice(28, 16).ToArray(),
        };

        var i = FixedLength + MagicCookie.Length;
        while (i < data.Length)
        {
            var code = data[i++];
            if (code == DhcpOption.Pad)
            {
                continue;
            }

            if (code == DhcpOption.End)
            {
                break;
            }

            if (i >= data.Length)
            {
                return false;
            }

            var length = data[i++];
            if (i + length > data.Length)
            {
                return false;
            }

            result.Options[code] = data.Slice(i, length).ToArray();
            i += length;
        }

        packet = result;
        return true;
    }

    public byte[] ToBytes()
    {
        var options = Options.Sum(o => 2 + o.Value.Length) + 1;
        var buffer = new byte[Math.Max(300, FixedLength + MagicCookie.Length + options)];

        buffer[0] = Op;
        buffer[1] = HardwareType;
        buffer[2] = HardwareLength;
        buffer[3] = Hops;
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(4), TransactionId);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(8), Seconds);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(10), Flags);
        ClientAddress.GetAddressBytes().CopyTo(buffer, 12);
        YourAddress.GetAddressBytes().CopyTo(buffer, 16);
        ServerAddress.GetAddressBytes().CopyTo(buffer, 20);
        GatewayAddress.GetAddressBytes().CopyTo(buffer, 24);
        ClientHardwareAddress.AsSpan(0, Math.Min(16, ClientHardwareAddress.Length)).CopyTo(buffer.AsSpan(28));
        MagicCookie.CopyTo(buffer, FixedLength);

        var i = FixedLength + MagicCookie.Length;
        // Le type de message en premier : certains clients embarqués l'exigent.
        if (Options.TryGetValue(DhcpOption.MessageType, out var type))
        {
            i = WriteOption(buffer, i, DhcpOption.MessageType, type);
        }

        foreach (var (code, value) in Options)
        {
            if (code != DhcpOption.MessageType)
            {
                i = WriteOption(buffer, i, code, value);
            }
        }

        buffer[i] = DhcpOption.End;
        return buffer;
    }

    private static int WriteOption(byte[] buffer, int offset, byte code, byte[] value)
    {
        buffer[offset++] = code;
        buffer[offset++] = (byte)value.Length;
        value.CopyTo(buffer, offset);
        return offset + value.Length;
    }
}
