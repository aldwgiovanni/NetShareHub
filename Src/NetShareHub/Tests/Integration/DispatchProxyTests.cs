using System.Net;
using System.Net.Sockets;
using System.Text;
using NetShareHub.Domain.Dtos.Incoming;
using NetShareHub.Domain.Enums;
using NetShareHub.Domain.Exceptions;
using NetShareHub.Infrastructure.Services.Dispatch;

namespace NetShareHub.Tests.Integration;

/// <summary>
/// Teste le vrai proxy de bout en bout sur la boucle locale : de vrais sockets, de vrais protocoles SOCKS5 et HTTP.
/// Seul le forçage d'interface est remplacé (<see cref="LoopbackBinder"/>), le reste est le code de production.
/// </summary>
public sealed class DispatchProxyTests : IAsyncLifetime
{
    private readonly LoopbackBinder _binder = new();
    private readonly FakeInventory _inventory = new(
        TestAdapters.Make("wifi", "Wi-Fi", AdapterKind.WiFi, ip: "127.0.0.1", gateway: "127.0.0.2"),
        TestAdapters.Make("eth", "Ethernet", ip: "127.0.0.1", gateway: "127.0.0.3"));

    private DispatchProxyService _proxy = null!;
    private EchoServer _echo = null!;

    public Task InitializeAsync()
    {
        _proxy = new DispatchProxyService(_inventory, _binder, Logs.For<DispatchProxyService>());
        _echo = new EchoServer();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _proxy.DisposeAsync();
        _echo.Dispose();
    }

    private static DispatchConfig_InDto Config(Action<DispatchConfig_InDto>? tweak = null)
    {
        var config = new DispatchConfig_InDto
        {
            Port = 0,
            HealthProbe = false,
            StickyDestinations = false,
            Uplinks =
            {
                new DispatchUplink_InDto { AdapterId = "wifi", Weight = 1 },
                new DispatchUplink_InDto { AdapterId = "eth", Weight = 1 },
            },
        };
        tweak?.Invoke(config);
        return config;
    }

    private async Task<TcpClient> ConnectAsync()
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _proxy.BoundPort);
        return client;
    }

    // --- SOCKS5 -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Socks5_connect_relaie_les_donnees_dans_les_deux_sens()
    {
        await _proxy.StartAsync(Config());

        using var client = await ConnectAsync();
        var stream = client.GetStream();
        await Socks5ConnectAsync(stream, "127.0.0.1", _echo.Port);

        await stream.WriteAsync(Encoding.UTF8.GetBytes("bonjour"));
        Assert.Equal("BONJOUR", await ReadStringAsync(stream, 7));
    }

    [Fact]
    public async Task Socks5_accepte_un_nom_de_domaine()
    {
        await _proxy.StartAsync(Config());

        using var client = await ConnectAsync();
        var stream = client.GetStream();
        await Socks5ConnectAsync(stream, "localhost", _echo.Port);

        await stream.WriteAsync(Encoding.UTF8.GetBytes("ok"));
        Assert.Equal("OK", await ReadStringAsync(stream, 2));
    }

    [Fact]
    public async Task Socks5_destination_fermee_repond_connexion_refusee()
    {
        await _proxy.StartAsync(Config());
        var closedPort = FreePort();

        using var client = await ConnectAsync();
        var stream = client.GetStream();
        var reply = await Socks5HandshakeAsync(stream, "127.0.0.1", closedPort);

        Assert.Equal(5, reply[1]); // 0x05 = connection refused
    }

    [Fact]
    public async Task Socks5_refuse_les_commandes_autres_que_connect()
    {
        await _proxy.StartAsync(Config());

        using var client = await ConnectAsync();
        var stream = client.GetStream();
        await stream.WriteAsync(new byte[] { 5, 1, 0 });
        await ReadExactAsync(stream, 2);
        await stream.WriteAsync(new byte[] { 5, 3 /* UDP ASSOCIATE */, 0, 1, 127, 0, 0, 1, 0, 80 });

        Assert.Equal(7, (await ReadExactAsync(stream, 10))[1]);
    }

    [Fact]
    public async Task Socks5_avec_authentification_refuse_un_mauvais_mot_de_passe_et_accepte_le_bon()
    {
        await _proxy.StartAsync(Config(c => { c.Username = "alice"; c.Password = "secret"; }));

        // Mauvais mot de passe.
        using (var bad = await ConnectAsync())
        {
            var stream = bad.GetStream();
            await stream.WriteAsync(new byte[] { 5, 1, 2 });
            Assert.Equal(new byte[] { 5, 2 }, await ReadExactAsync(stream, 2));
            await stream.WriteAsync(Credentials("alice", "faux"));
            Assert.Equal(1, (await ReadExactAsync(stream, 2))[1]);
        }

        // Bon mot de passe.
        using var good = await ConnectAsync();
        var s = good.GetStream();
        await s.WriteAsync(new byte[] { 5, 1, 2 });
        await ReadExactAsync(s, 2);
        await s.WriteAsync(Credentials("alice", "secret"));
        Assert.Equal(0, (await ReadExactAsync(s, 2))[1]);
    }

    [Fact]
    public async Task Socks5_sans_methode_acceptable_est_refuse_quand_une_authentification_est_exigee()
    {
        await _proxy.StartAsync(Config(c => { c.Username = "alice"; c.Password = "secret"; }));

        using var client = await ConnectAsync();
        var stream = client.GetStream();
        await stream.WriteAsync(new byte[] { 5, 1, 0 }); // le client n'offre que « sans authentification »

        Assert.Equal(new byte[] { 5, 0xFF }, await ReadExactAsync(stream, 2));
    }

    // --- HTTP -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Http_connect_etablit_un_tunnel()
    {
        await _proxy.StartAsync(Config());

        using var client = await ConnectAsync();
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{_echo.Port} HTTP/1.1\r\nHost: 127.0.0.1:{_echo.Port}\r\n\r\n"));

        var head = await ReadUntilAsync(stream, "\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 200", head);

        await stream.WriteAsync(Encoding.UTF8.GetBytes("tunnel"));
        Assert.Equal("TUNNEL", await ReadStringAsync(stream, 6));
    }

    [Fact]
    public async Task Http_connect_avec_donnees_envoyees_dans_le_meme_paquet()
    {
        await _proxy.StartAsync(Config());

        using var client = await ConnectAsync();
        var stream = client.GetStream();
        // Le client enchaîne directement les premiers octets après la requête (typique d'un début de TLS).
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{_echo.Port} HTTP/1.1\r\n\r\nprecoce"));

        await ReadUntilAsync(stream, "\r\n\r\n");
        Assert.Equal("PRECOCE", await ReadStringAsync(stream, 7));
    }

    [Fact]
    public async Task Http_requete_simple_est_reecrite_en_forme_origine()
    {
        await _proxy.StartAsync(Config());
        using var web = new WebServer();

        using var client = await ConnectAsync();
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"GET http://127.0.0.1:{web.Port}/chemin?a=1 HTTP/1.1\r\nHost: 127.0.0.1:{web.Port}\r\nProxy-Connection: keep-alive\r\n\r\n"));

        var response = await ReadAllAsync(stream);

        Assert.StartsWith("HTTP/1.1 200", response);
        Assert.Contains("GET /chemin?a=1 HTTP/1.1", web.LastRequest);
        Assert.DoesNotContain("Proxy-Connection", web.LastRequest);
        Assert.Contains("Connection: close", web.LastRequest);
    }

    [Fact]
    public async Task Http_sans_authentification_recoit_407_quand_elle_est_exigee()
    {
        await _proxy.StartAsync(Config(c => { c.Username = "alice"; c.Password = "secret"; }));

        using var client = await ConnectAsync();
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{_echo.Port} HTTP/1.1\r\n\r\n"));

        Assert.StartsWith("HTTP/1.1 407", await ReadAllAsync(stream));
    }

    [Fact]
    public async Task Http_avec_authentification_basic_correcte_est_accepte()
    {
        await _proxy.StartAsync(Config(c => { c.Username = "alice"; c.Password = "secret"; }));
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:secret"));

        using var client = await ConnectAsync();
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"CONNECT 127.0.0.1:{_echo.Port} HTTP/1.1\r\nProxy-Authorization: Basic {token}\r\n\r\n"));

        Assert.StartsWith("HTTP/1.1 200", await ReadUntilAsync(stream, "\r\n\r\n"));
    }

    // --- Répartition ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Les_connexions_sont_reparties_a_parts_egales_entre_deux_liaisons()
    {
        await _proxy.StartAsync(Config());

        for (var i = 0; i < 20; i++)
        {
            await RoundTripAsync("x");
        }

        var calls = _binder.Calls;
        Assert.Equal(10, calls["wifi"]);
        Assert.Equal(10, calls["eth"]);
    }

    [Fact]
    public async Task Les_poids_orientent_la_repartition()
    {
        await _proxy.StartAsync(Config(c => c.Uplinks[0].Weight = 3));

        for (var i = 0; i < 20; i++)
        {
            await RoundTripAsync("x");
        }

        Assert.Equal(15, _binder.Calls["wifi"]);
        Assert.Equal(5, _binder.Calls["eth"]);
    }

    [Fact]
    public async Task Les_statistiques_comptent_connexions_et_octets_par_liaison()
    {
        await _proxy.StartAsync(Config());

        for (var i = 0; i < 4; i++)
        {
            await RoundTripAsync("0123456789");
        }

        // Les compteurs d'octets sont alimentés par le relais : on laisse les connexions se refermer.
        var status = await WaitForAsync(() => _proxy.GetStatus(), s => s.ActiveConnections == 0 && s.Uplinks.All(u => u.BytesSent >= 20));

        Assert.Equal(RunState.Running, status.State);
        Assert.All(status.Uplinks, u =>
        {
            Assert.Equal(2, u.TotalConnections);
            Assert.Equal(20, u.BytesSent);
            Assert.Equal(20, u.BytesReceived);
        });
        Assert.Contains($"127.0.0.1:{_proxy.BoundPort}", status.ClientEndpoints);
    }

    [Fact]
    public async Task Une_liaison_deconnectee_est_ecartee_sans_erreur_pour_le_client()
    {
        await _proxy.StartAsync(Config());
        // Le câble Ethernet est débranché : l'adaptateur n'a plus de passerelle.
        _inventory.Snapshot = new[]
        {
            _inventory.Snapshot[0],
            TestAdapters.Make("eth", "Ethernet", up: false, ip: null, gateway: null),
        };

        for (var i = 0; i < 10; i++)
        {
            await RoundTripAsync("ping");
        }

        Assert.Equal(10, _binder.Calls["wifi"]);
        Assert.False(_binder.Calls.ContainsKey("eth"));
    }

    [Fact]
    public async Task Le_mode_failover_reste_sur_la_premiere_liaison()
    {
        await _proxy.StartAsync(Config(c => c.Strategy = DispatchStrategy.Failover));

        for (var i = 0; i < 6; i++)
        {
            await RoundTripAsync("x");
        }

        Assert.Equal(6, _binder.Calls["wifi"]);
        Assert.False(_binder.Calls.ContainsKey("eth"));
    }

    [Fact]
    public async Task L_affinite_garde_la_meme_liaison_pour_une_meme_destination()
    {
        await _proxy.StartAsync(Config(c => { c.StickyDestinations = true; c.StickyMinutes = 5; }));

        for (var i = 0; i < 8; i++)
        {
            await RoundTripAsync("x");
        }

        Assert.Single(_binder.Calls);
        Assert.Equal(8, _binder.Calls.Values.Single());
    }

    // --- Cycle de vie ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Demarrage_refuse_sans_liaison_ou_avec_une_liaison_sans_internet()
    {
        var noUplink = await Assert.ThrowsAsync<DispatchException>(() => _proxy.StartAsync(new DispatchConfig_InDto { Port = 0 }));
        Assert.Contains("au moins une connexion", noUplink.Message);

        _inventory.Snapshot = new[] { TestAdapters.Make("wifi", "Wi-Fi", ip: null, gateway: null) };
        var offline = await Assert.ThrowsAsync<DispatchException>(() => _proxy.StartAsync(Config(c => c.Uplinks.RemoveAt(1))));
        Assert.Contains("pas d'accès Internet", offline.Message);
    }

    [Fact]
    public async Task Port_deja_utilise_donne_un_message_clair()
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var port = ((IPEndPoint)occupied.LocalEndpoint).Port;

        var ex = await Assert.ThrowsAsync<DispatchException>(() => _proxy.StartAsync(Config(c => c.Port = port)));

        Assert.Contains(port.ToString(), ex.Message);
        Assert.Equal(RunState.Faulted, _proxy.GetStatus().State);
    }

    [Fact]
    public async Task Double_demarrage_est_refuse_et_l_arret_ferme_les_connexions_ouvertes()
    {
        await _proxy.StartAsync(Config());
        await Assert.ThrowsAsync<DispatchException>(() => _proxy.StartAsync(Config()));

        using var client = await ConnectAsync();
        var stream = client.GetStream();
        await Socks5ConnectAsync(stream, "127.0.0.1", _echo.Port);

        await _proxy.StopAsync();

        // Le tunnel ouvert est coupé par l'arrêt : la lecture se termine (fin de flux ou erreur) au lieu de bloquer.
        var buffer = new byte[1];
        var read = await Task.WhenAny(ReadOrFailAsync(stream, buffer), Task.Delay(3000));
        Assert.True(read.IsCompleted);
        Assert.Equal(RunState.Stopped, _proxy.GetStatus().State);

        // Un nouveau démarrage est possible après l'arrêt.
        await _proxy.StartAsync(Config());
        Assert.Equal(RunState.Running, _proxy.GetStatus().State);
    }

    [Fact]
    public async Task En_mode_reseau_local_les_adresses_a_saisir_sont_listees()
    {
        await _proxy.StartAsync(Config(c => c.AllowLan = true));

        var status = _proxy.GetStatus();

        Assert.StartsWith("0.0.0.0:", status.ListenEndpoint);
        Assert.Contains(status.ClientEndpoints, e => e.StartsWith("127.0.0.1:"));
    }

    // --- Outils ---------------------------------------------------------------------------------------------------------

    private async Task RoundTripAsync(string payload)
    {
        using var client = await ConnectAsync();
        var stream = client.GetStream();
        await Socks5ConnectAsync(stream, "127.0.0.1", _echo.Port);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(payload));
        Assert.Equal(payload.ToUpperInvariant(), await ReadStringAsync(stream, payload.Length));
    }

    private static async Task Socks5ConnectAsync(NetworkStream stream, string host, int port)
    {
        var reply = await Socks5HandshakeAsync(stream, host, port);
        Assert.Equal(0, reply[1]);
    }

    private static async Task<byte[]> Socks5HandshakeAsync(NetworkStream stream, string host, int port)
    {
        await stream.WriteAsync(new byte[] { 5, 1, 0 });
        Assert.Equal(new byte[] { 5, 0 }, await ReadExactAsync(stream, 2));

        var request = new List<byte> { 5, 1, 0 };
        if (IPAddress.TryParse(host, out var ip))
        {
            request.Add(1);
            request.AddRange(ip.GetAddressBytes());
        }
        else
        {
            var name = Encoding.ASCII.GetBytes(host);
            request.Add(3);
            request.Add((byte)name.Length);
            request.AddRange(name);
        }

        request.Add((byte)(port >> 8));
        request.Add((byte)port);
        await stream.WriteAsync(request.ToArray());
        return await ReadExactAsync(stream, 10);
    }

    private static byte[] Credentials(string user, string password)
    {
        var u = Encoding.UTF8.GetBytes(user);
        var p = Encoding.UTF8.GetBytes(password);
        return new byte[] { 1, (byte)u.Length }.Concat(u).Append((byte)p.Length).Concat(p).ToArray();
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), cts.Token);
            Assert.True(n > 0, "connexion fermée prématurément");
            read += n;
        }

        return buffer;
    }

    private static async Task<string> ReadStringAsync(NetworkStream stream, int count)
        => Encoding.UTF8.GetString(await ReadExactAsync(stream, count));

    private static async Task<string> ReadUntilAsync(NetworkStream stream, string terminator)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var text = new StringBuilder();
        var one = new byte[1];
        while (!text.ToString().EndsWith(terminator, StringComparison.Ordinal))
        {
            var n = await stream.ReadAsync(one, cts.Token);
            Assert.True(n > 0, "connexion fermée prématurément");
            text.Append((char)one[0]);
        }

        return text.ToString();
    }

    private static async Task<string> ReadAllAsync(NetworkStream stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cts.Token);
        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private static async Task ReadOrFailAsync(NetworkStream stream, byte[] buffer)
    {
        try
        {
            _ = await stream.ReadAsync(buffer); // on attend seulement la fin de la lecture (octets ou fermeture)
        }
        catch (IOException)
        {
            // Fermeture brutale : c'est bien ce qu'on attend.
        }
    }

    private static async Task<T> WaitForAsync<T>(Func<T> read, Func<T, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var value = read();
            if (condition(value) || DateTime.UtcNow > deadline)
            {
                return value;
            }

            await Task.Delay(50);
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Renvoie chaque octet reçu en majuscules.</summary>
    private sealed class EchoServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();

        public EchoServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptAsync);
        }

        public int Port { get; }

        private async Task AcceptAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = Task.Run(() => EchoAsync(client));
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }
            }
        }

        private async Task EchoAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var buffer = new byte[1024];
                try
                {
                    int n;
                    while ((n = await stream.ReadAsync(buffer, _cts.Token)) > 0)
                    {
                        for (var i = 0; i < n; i++)
                        {
                            buffer[i] = (byte)char.ToUpperInvariant((char)buffer[i]);
                        }

                        await stream.WriteAsync(buffer.AsMemory(0, n), _cts.Token);
                    }
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException)
                {
                    // Client parti.
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }

    /// <summary>Serveur HTTP minimal : mémorise la dernière requête reçue et répond 200.</summary>
    private sealed class WebServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public WebServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(ServeAsync);
        }

        public int Port { get; }

        public string LastRequest { get; private set; } = string.Empty;

        private async Task ServeAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var buffer = new byte[4096];
                var text = new StringBuilder();
                while (!text.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var n = await stream.ReadAsync(buffer);
                    if (n == 0)
                    {
                        break;
                    }

                    text.Append(Encoding.ASCII.GetString(buffer, 0, n));
                }

                LastRequest = text.ToString();
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"));
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
            {
                // Arrêt du test.
            }
        }

        public void Dispose() => _listener.Stop();
    }
}
