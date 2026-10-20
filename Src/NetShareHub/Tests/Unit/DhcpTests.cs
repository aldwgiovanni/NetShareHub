using System.Net;
using NetShareHub.Domain.ValueObjects;
using NetShareHub.Infrastructure.Services.Dhcp;

namespace NetShareHub.Tests.Unit;

public class DhcpTests
{
    private static readonly DhcpScope Scope = DhcpScope.ForSubnet(
        Ipv4Subnet.Parse("10.77.0.0/24"), 7, "eth1", new[] { IPAddress.Parse("1.1.1.1"), IPAddress.Parse("8.8.8.8") });

    private static DhcpPacket Client(DhcpMessageType type, byte macLast = 1, IPAddress? requested = null, IPAddress? server = null)
    {
        var mac = new byte[16];
        new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, macLast }.CopyTo(mac, 0);
        var packet = new DhcpPacket { Op = 1, TransactionId = 0xCAFEBABE, Flags = 0x8000, ClientHardwareAddress = mac };
        packet.SetOption(DhcpOption.MessageType, (byte)type);
        if (requested is not null)
        {
            packet.SetAddressOption(DhcpOption.RequestedIp, requested);
        }

        if (server is not null)
        {
            packet.SetAddressOption(DhcpOption.ServerId, server);
        }

        return packet;
    }

    [Fact]
    public void Paquet_serialise_puis_relu_a_l_identique()
    {
        var original = Client(DhcpMessageType.Request, requested: IPAddress.Parse("10.77.0.150"), server: Scope.ServerAddress);
        original.SetOption(12, System.Text.Encoding.ASCII.GetBytes("portable"));

        var bytes = original.ToBytes();
        Assert.True(DhcpPacket.TryParse(bytes, out var parsed));

        Assert.Equal(original.TransactionId, parsed.TransactionId);
        Assert.Equal(DhcpMessageType.Request, parsed.MessageType);
        Assert.Equal("aa:bb:cc:dd:ee:01", parsed.MacAddress);
        Assert.Equal(IPAddress.Parse("10.77.0.150"), parsed.RequestedIp);
        Assert.Equal(Scope.ServerAddress, parsed.ServerIdentifier);
        Assert.True(parsed.WantsBroadcast);
        Assert.Equal("portable", System.Text.Encoding.ASCII.GetString(parsed.Options[12]));
        Assert.True(bytes.Length >= 300, "les anciens clients exigent au moins 300 octets");
    }

    [Fact]
    public void Donnees_invalides_ne_sont_pas_acceptees()
    {
        Assert.False(DhcpPacket.TryParse(new byte[10], out _));
        Assert.False(DhcpPacket.TryParse(new byte[400], out _)); // cookie magique absent

        // Option coupée au milieu (code présent, longueur absente).
        var truncated = Client(DhcpMessageType.Discover).ToBytes()[..241];
        Assert.False(DhcpPacket.TryParse(truncated, out _));
    }

    [Fact]
    public void Discover_obtient_une_offre_complete()
    {
        var table = new DhcpLeaseTable(Scope);

        var offer = DhcpEngine.Handle(Client(DhcpMessageType.Discover), table);

        Assert.NotNull(offer);
        Assert.Equal(DhcpMessageType.Offer, offer!.MessageType);
        Assert.Equal(2, offer.Op);
        Assert.Equal(0xCAFEBABE, offer.TransactionId);
        Assert.Equal(IPAddress.Parse("10.77.0.100"), offer.YourAddress);
        Assert.Equal(Scope.ServerAddress, offer.ServerIdentifier);
        Assert.Equal(new byte[] { 255, 255, 255, 0 }, offer.Options[DhcpOption.SubnetMask]);
        Assert.Equal(Scope.ServerAddress.GetAddressBytes(), offer.Options[DhcpOption.Router]);
        Assert.Equal(8, offer.Options[DhcpOption.DnsServers].Length); // deux DNS
        Assert.Equal(12 * 3600u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(offer.Options[DhcpOption.LeaseTime]));
    }

    [Fact]
    public void Sequence_complete_Discover_Request_Ack()
    {
        var table = new DhcpLeaseTable(Scope);
        var offer = DhcpEngine.Handle(Client(DhcpMessageType.Discover), table)!;

        var ack = DhcpEngine.Handle(
            Client(DhcpMessageType.Request, requested: offer.YourAddress, server: Scope.ServerAddress), table);

        Assert.Equal(DhcpMessageType.Ack, ack!.MessageType);
        Assert.Equal(offer.YourAddress, ack.YourAddress);
        Assert.Equal(1, table.Count);
    }

    [Fact]
    public void Deux_clients_obtiennent_deux_adresses_differentes()
    {
        var table = new DhcpLeaseTable(Scope);

        var a = Commit(table, 1);
        var b = Commit(table, 2);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Un_client_qui_revient_retrouve_la_meme_adresse()
    {
        var table = new DhcpLeaseTable(Scope);
        var first = Commit(table, 1);
        Commit(table, 2);

        var offer = DhcpEngine.Handle(Client(DhcpMessageType.Discover, 1), table)!;

        Assert.Equal(first, offer.YourAddress);
    }

    [Fact]
    public void Request_d_une_adresse_deja_prise_par_un_autre_recoit_un_NAK()
    {
        var table = new DhcpLeaseTable(Scope);
        var taken = Commit(table, 1);

        var nak = DhcpEngine.Handle(Client(DhcpMessageType.Request, 2, requested: taken, server: Scope.ServerAddress), table);

        Assert.Equal(DhcpMessageType.Nak, nak!.MessageType);
        Assert.Equal(IPAddress.Any, nak.YourAddress);
    }

    [Fact]
    public void Request_hors_plage_recoit_un_NAK()
    {
        var table = new DhcpLeaseTable(Scope);

        var nak = DhcpEngine.Handle(
            Client(DhcpMessageType.Request, requested: IPAddress.Parse("192.168.1.50"), server: Scope.ServerAddress), table);

        Assert.Equal(DhcpMessageType.Nak, nak!.MessageType);
    }

    [Fact]
    public void Request_destine_a_un_autre_serveur_est_ignore()
    {
        var table = new DhcpLeaseTable(Scope);
        DhcpEngine.Handle(Client(DhcpMessageType.Discover), table);

        var reply = DhcpEngine.Handle(
            Client(DhcpMessageType.Request, requested: IPAddress.Parse("10.77.0.100"), server: IPAddress.Parse("192.168.1.1")), table);

        Assert.Null(reply);
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void Release_libere_l_adresse()
    {
        var table = new DhcpLeaseTable(Scope);
        Commit(table, 1);
        Assert.Equal(1, table.Count);

        Assert.Null(DhcpEngine.Handle(Client(DhcpMessageType.Release, 1), table));

        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void Les_baux_expires_sont_recycles()
    {
        var now = DateTimeOffset.Parse("2026-01-01T10:00:00Z");
        var table = new DhcpLeaseTable(Scope, () => now);
        var first = Commit(table, 1);

        now += TimeSpan.FromHours(13);

        Assert.Equal(0, table.Count);
        Assert.Equal(first, DhcpEngine.Handle(Client(DhcpMessageType.Discover, 9), table)!.YourAddress);
    }

    [Fact]
    public void Plage_epuisee_n_envoie_pas_d_offre()
    {
        var tiny = Scope with { RangeStart = IPAddress.Parse("10.77.0.100"), RangeEnd = IPAddress.Parse("10.77.0.101") };
        var table = new DhcpLeaseTable(tiny);
        Commit(table, 1);
        Commit(table, 2);

        Assert.Null(DhcpEngine.Handle(Client(DhcpMessageType.Discover, 3), table));
    }

    [Fact]
    public void Les_messages_serveur_sont_ignores()
    {
        var table = new DhcpLeaseTable(Scope);
        var fromServer = Client(DhcpMessageType.Offer);
        fromServer.Op = 2;

        Assert.Null(DhcpEngine.Handle(fromServer, table));
    }

    private static IPAddress Commit(DhcpLeaseTable table, byte client)
    {
        var offer = DhcpEngine.Handle(Client(DhcpMessageType.Discover, client), table)!;
        var ack = DhcpEngine.Handle(
            Client(DhcpMessageType.Request, client, requested: offer.YourAddress, server: Scope.ServerAddress), table)!;
        Assert.Equal(DhcpMessageType.Ack, ack.MessageType);
        return ack.YourAddress;
    }
}
