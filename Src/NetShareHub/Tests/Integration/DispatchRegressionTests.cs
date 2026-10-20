using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using NetShareHub.Domain.Dtos.Incoming;
using NetShareHub.Domain.Enums;
using NetShareHub.Domain.Exceptions;
using NetShareHub.Infrastructure.Services.Dispatch;

namespace NetShareHub.Tests.Integration;

// Défauts relevés lors des tests utilisateur du Dispatch.
public sealed partial class DispatchProxyTests
{
    [Fact]
    public async Task Des_requetes_vers_une_destination_impossible_ne_mettent_pas_les_liaisons_hors_service()
    {
        await _proxy.StartAsync(Config());

        for (var i = 0; i < 4; i++)
        {
            using var client = await ConnectAsync();
            var reply = await Socks5HandshakeAsync(client.GetStream(), "127.0.0.1", 0);
            Assert.Equal(2, reply[1]); // 0x02 : connexion non autorisée (port 0)
        }

        Assert.All(_proxy.GetStatus().Uplinks, u => Assert.NotEqual(UplinkHealth.Down, u.Health));
        Assert.Empty(_binder.Calls); // aucune liaison n'a même été essayée

        // Le proxy fonctionne toujours.
        using var ok = await ConnectAsync();
        await Socks5ConnectAsync(ok.GetStream(), "127.0.0.1", _echo.Port);
    }

    [Fact]
    public async Task Un_nom_d_hote_trop_long_recoit_une_erreur_et_pas_une_coupure()
    {
        await _proxy.StartAsync(Config());
        using var client = await ConnectAsync();
        var stream = client.GetStream();

        var host = new string('a', 300) + ".example";
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {host}:443 HTTP/1.1\r\n\r\n"));

        Assert.StartsWith("HTTP/1.1 502", await ReadUntilAsync(stream, "\r\n"));
        Assert.All(_proxy.GetStatus().Uplinks, u => Assert.NotEqual(UplinkHealth.Down, u.Health));
    }

    [Fact]
    public async Task Une_erreur_inattendue_au_demarrage_ne_laisse_pas_le_dispatch_bloque_sur_demarrage()
    {
        var broken = Config();
        broken.Uplinks.Insert(0, null!);

        await Assert.ThrowsAsync<DispatchException>(() => _proxy.StartAsync(broken));
        Assert.Equal(RunState.Faulted, _proxy.GetStatus().State);

        // Un nouvel essai est accepté (et une liaison cochée deux fois ne compte qu'une fois).
        await _proxy.StartAsync(Config(c => c.Uplinks.Add(new DispatchUplink_InDto { AdapterId = "WIFI", Weight = 3 })));
        Assert.Equal(RunState.Running, _proxy.GetStatus().State);
        Assert.Equal(2, _proxy.GetStatus().Uplinks.Count);
    }

    [Fact]
    public async Task Le_journal_indique_les_echecs_de_demarrage_et_decrit_le_demarrage_en_francais()
    {
        var logger = new ListLogger<DispatchProxyService>();
        await using var proxy = new DispatchProxyService(_inventory, _binder, logger);
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var port = ((IPEndPoint)occupied.LocalEndpoint).Port;

        await Assert.ThrowsAsync<DispatchException>(() => proxy.StartAsync(Config(c => c.Port = port)));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains($"port {port} est déjà utilisé"));

        await proxy.StartAsync(Config(c => c.Uplinks.RemoveAt(1)));
        Assert.Contains(logger.Entries, e => e.Message.Contains("(répartition équilibrée, 1 liaison)"));
    }
}

/// <summary>Une session de proxy face à un répartiteur simulé : libération des liaisons et réponses en cas de délai.</summary>
public sealed class ProxySessionTests : IAsyncLifetime
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private TcpClient _client = null!;
    private Socket _server = null!;

    public async Task InitializeAsync()
    {
        _listener.Start();
        _client = new TcpClient();
        var accept = _listener.AcceptSocketAsync();
        await _client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)_listener.LocalEndpoint).Port);
        _server = await accept;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _server.Dispose();
        _listener.Stop();
        return Task.CompletedTask;
    }

    private sealed class FakeConnector(Func<CancellationToken, Task<UpstreamConnection>> connect) : IUpstreamConnector
    {
        public Task<UpstreamConnection> ConnectAsync(string host, int port, CancellationToken cancellationToken) => connect(cancellationToken);
    }

    private static UpstreamConnection Upstream(Action onReleased) => new()
    {
        Socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp),
        AdapterId = "wifi",
        CountSent = _ => { },
        CountReceived = _ => { },
        OnReleased = onReleased,
    };

    private async Task<string> ReadLineAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var text = new StringBuilder();
        var one = new byte[1];
        while (!text.ToString().EndsWith("\r\n", StringComparison.Ordinal))
        {
            Assert.True(await _client.GetStream().ReadAsync(one, cts.Token) > 0, "connexion fermée sans réponse");
            text.Append((char)one[0]);
        }

        return text.ToString();
    }

    [Fact]
    public async Task Une_liaison_obtenue_est_liberee_si_le_client_est_parti_avant_la_reponse()
    {
        var released = 0;
        var connector = new FakeConnector(_ =>
        {
            _server.Dispose(); // le client disparaît pendant la connexion sortante : la réponse ne pourra pas être écrite
            return Task.FromResult(Upstream(() => released++));
        });

        await _client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("CONNECT exemple.fr:443 HTTP/1.1\r\n\r\n"));
        var session = new ProxySession(_server, connector, null);
        await Assert.ThrowsAnyAsync<Exception>(() => session.RunAsync(CancellationToken.None));

        Assert.Equal(1, released);
    }

    [Fact]
    public async Task Une_destination_muette_recoit_504_meme_au_dela_du_delai_de_negociation()
    {
        var connector = new FakeConnector(async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException();
        });

        await _client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("CONNECT exemple.fr:443 HTTP/1.1\r\n\r\n"));
        await new ProxySession(_server, connector, null, TimeSpan.FromMilliseconds(300)).RunAsync(CancellationToken.None);

        Assert.StartsWith("HTTP/1.1 504", await ReadLineAsync());
    }

    [Fact]
    public async Task En_socks5_une_destination_muette_recoit_le_code_4()
    {
        var connector = new FakeConnector(async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException();
        });

        var stream = _client.GetStream();
        await stream.WriteAsync(new byte[] { 5, 1, 0, 5, 1, 0, 1, 93, 184, 216, 34, 1, 187 });
        await new ProxySession(_server, connector, null, TimeSpan.FromMilliseconds(300)).RunAsync(CancellationToken.None);

        var reply = new byte[12];
        var read = 0;
        while (read < reply.Length)
        {
            read += await stream.ReadAsync(reply.AsMemory(read));
        }

        Assert.Equal(new byte[] { 5, 0 }, reply[..2]); // choix de la méthode
        Assert.Equal(4, reply[3]); // 0x04 : hôte injoignable
    }
}
