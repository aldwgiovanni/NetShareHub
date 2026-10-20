using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace NetShareHub.Infrastructure.Services.Dispatch;

/// <summary>Connexion sortante établie par le répartiteur, avec ses compteurs.</summary>
internal sealed class UpstreamConnection : IDisposable
{
    private int _released;

    public required Socket Socket { get; init; }

    public required string AdapterId { get; init; }

    public required Action<long> CountSent { get; init; }

    public required Action<long> CountReceived { get; init; }

    /// <summary>Libère la réservation faite auprès du répartiteur (appelé une seule fois).</summary>
    public required Action OnReleased { get; init; }

    public IPEndPoint? LocalEndPoint => Socket.LocalEndPoint as IPEndPoint;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            OnReleased();
        }

        Socket.Dispose();
    }
}

/// <summary>Établit les connexions sortantes (choix de la liaison, bascule en cas d'échec).</summary>
internal interface IUpstreamConnector
{
    Task<UpstreamConnection> ConnectAsync(string host, int port, CancellationToken cancellationToken);
}

/// <summary>Échec de connexion sortante, avec le code de réponse SOCKS5 correspondant.</summary>
internal sealed class ProxyConnectException : Exception
{
    public ProxyConnectException(string message, byte socksCode, Exception? inner = null) : base(message, inner)
    {
        SocksCode = socksCode;
    }

    public byte SocksCode { get; }

    /// <summary>Vrai si la destination n'a pas répondu à temps (HTTP 504 plutôt que 502).</summary>
    public bool TimedOut { get; init; }
}

internal sealed record ProxyCredentials(string Username, string Password);

/// <summary>
/// Une connexion cliente : détecte le protocole (SOCKS5 ou HTTP/CONNECT), négocie la destination,
/// demande une connexion sortante au répartiteur puis relaie les octets dans les deux sens.
/// </summary>
internal sealed class ProxySession
{
    private const int HandshakeTimeoutSeconds = 20;

    /// <summary>Temps laissé par défaut au répartiteur pour joindre la destination (plusieurs liaisons, plusieurs adresses).</summary>
    private static readonly TimeSpan DefaultConnectBudget = TimeSpan.FromSeconds(45);

    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan HalfClosedIdleTimeout = TimeSpan.FromSeconds(60);

    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Proxy-Connection", "Proxy-Authorization", "Connection", "Keep-Alive", "TE", "Upgrade",
    };

    private readonly Socket _client;
    private readonly NetworkStream _clientStream;
    private readonly ProxyReader _reader;
    private readonly IUpstreamConnector _connector;
    private readonly ProxyCredentials? _credentials;
    private readonly TimeSpan _connectBudget;
    private CancellationToken _serviceToken;

    public ProxySession(Socket client, IUpstreamConnector connector, ProxyCredentials? credentials, TimeSpan? connectBudget = null)
    {
        _client = client;
        _clientStream = new NetworkStream(client, ownsSocket: false);
        _reader = new ProxyReader(_clientStream);
        _connector = connector;
        _credentials = credentials;
        _connectBudget = connectBudget ?? DefaultConnectBudget;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _serviceToken = cancellationToken;
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshake.CancelAfter(TimeSpan.FromSeconds(HandshakeTimeoutSeconds));

        UpstreamConnection? upstream = null;
        try
        {
            var first = await _reader.PeekByteAsync(handshake.Token);
            switch (first)
            {
                case 5:
                    upstream = await NegotiateSocks5Async(handshake.Token);
                    break;
                case -1:
                    return;
                case 4:
                    // SOCKS4 : non pris en charge (SOCKS5 est universel).
                    return;
                default:
                    upstream = await NegotiateHttpAsync(handshake.Token);
                    break;
            }

            if (upstream is null)
            {
                return;
            }

            await RelayAsync(upstream, cancellationToken);
        }
        finally
        {
            upstream?.Dispose();
        }
    }

    // --- SOCKS5 (RFC 1928 / 1929) ---------------------------------------------------------------------------

    private async Task<UpstreamConnection?> NegotiateSocks5Async(CancellationToken ct)
    {
        await _reader.ReadByteAsync(ct); // version
        var methodCount = await _reader.ReadByteAsync(ct);
        var methods = await _reader.ReadExactAsync(methodCount, ct);

        var wanted = _credentials is null ? (byte)0x00 : (byte)0x02;
        if (!methods.Contains(wanted))
        {
            await _clientStream.WriteAsync(new byte[] { 5, 0xFF }, ct);
            return null;
        }

        await _clientStream.WriteAsync(new byte[] { 5, wanted }, ct);

        if (_credentials is not null && !await AuthenticateSocks5Async(ct))
        {
            return null;
        }

        var header = await _reader.ReadExactAsync(4, ct);
        if (header[0] != 5)
        {
            return null;
        }

        string host;
        switch (header[3])
        {
            case 1:
                host = new IPAddress(await _reader.ReadExactAsync(4, ct)).ToString();
                break;
            case 3:
                var length = await _reader.ReadByteAsync(ct);
                host = Encoding.ASCII.GetString(await _reader.ReadExactAsync(length, ct));
                break;
            case 4:
                // Les liaisons sont forcées en IPv4 : on ne peut pas joindre une adresse IPv6 littérale.
                await _reader.ReadExactAsync(18, ct);
                await ReplySocks5Async(8, null, ct);
                return null;
            default:
                await ReplySocks5Async(8, null, ct);
                return null;
        }

        var portBytes = await _reader.ReadExactAsync(2, ct);
        var port = (portBytes[0] << 8) | portBytes[1];

        if (header[1] != 1)
        {
            await ReplySocks5Async(7, null, ct); // seule la commande CONNECT est gérée (pas BIND / UDP ASSOCIATE)
            return null;
        }

        return await ConnectAsync(
            host,
            port,
            (upstream, token) => ReplySocks5Async(0, upstream.LocalEndPoint, token),
            (ex, token) => ReplySocks5Async(ex.SocksCode, null, token));
    }

    private async Task<bool> AuthenticateSocks5Async(CancellationToken ct)
    {
        await _reader.ReadByteAsync(ct); // version de la sous-négociation (1)
        var userLength = await _reader.ReadByteAsync(ct);
        var user = Encoding.UTF8.GetString(await _reader.ReadExactAsync(userLength, ct));
        var passwordLength = await _reader.ReadByteAsync(ct);
        var password = Encoding.UTF8.GetString(await _reader.ReadExactAsync(passwordLength, ct));

        var ok = CredentialsMatch(user, password);
        await _clientStream.WriteAsync(new byte[] { 1, ok ? (byte)0 : (byte)1 }, ct);
        return ok;
    }

    private async Task ReplySocks5Async(byte code, IPEndPoint? bound, CancellationToken ct)
    {
        var address = bound?.Address.GetAddressBytes() ?? new byte[4];
        var port = bound?.Port ?? 0;
        var reply = new byte[10];
        reply[0] = 5;
        reply[1] = code;
        reply[3] = 1;
        address.CopyTo(reply, 4);
        reply[8] = (byte)(port >> 8);
        reply[9] = (byte)port;
        await _clientStream.WriteAsync(reply, ct);
    }

    // --- HTTP proxy (CONNECT + requêtes absolues) -------------------------------------------------------------

    private async Task<UpstreamConnection?> NegotiateHttpAsync(CancellationToken ct)
    {
        string head;
        try
        {
            head = await _reader.ReadHeadAsync(ct);
        }
        catch (InvalidDataException)
        {
            await WriteHttpAsync("431 Request Header Fields Too Large", "En-tête trop volumineux.", ct);
            return null;
        }

        var lines = head.Split("\r\n");
        var requestLine = lines[0].Split(' ', 3);
        if (requestLine.Length != 3)
        {
            await WriteHttpAsync("400 Bad Request", "Requête invalide.", ct);
            return null;
        }

        var method = requestLine[0];
        var target = requestLine[1];
        var version = requestLine[2];
        var headers = lines.Skip(1).Where(l => l.Length > 0).ToList();

        if (_credentials is not null && !HttpAuthorized(headers))
        {
            await WriteHttpAsync(
                "407 Proxy Authentication Required",
                "Authentification requise.",
                ct,
                "Proxy-Authenticate: Basic realm=\"NetShare Hub\"");
            return null;
        }

        if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            if (!TrySplitHostPort(target, 443, out var host, out var port))
            {
                await WriteHttpAsync("400 Bad Request", "Destination invalide.", ct);
                return null;
            }

            return await ConnectAsync(
                host,
                port,
                async (_, token) => await _clientStream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), token),
                RefuseHttpAsync);
        }

        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp)
        {
            await WriteHttpAsync("200 OK", "NetShare Hub Dispatch : configurez ce port comme proxy SOCKS5 ou HTTP.", ct);
            return null;
        }

        // Réécriture en forme « origine » ; Connection: close car on ne gère qu'une requête par connexion.
        var rewritten = new StringBuilder();
        rewritten.Append(method).Append(' ').Append(uri.PathAndQuery).Append(' ').Append(version).Append("\r\n");
        foreach (var header in headers)
        {
            var name = header.Split(':', 2)[0].Trim();
            if (!HopByHopHeaders.Contains(name))
            {
                rewritten.Append(header).Append("\r\n");
            }
        }

        rewritten.Append("Connection: close\r\n\r\n");
        var bytes = Encoding.Latin1.GetBytes(rewritten.ToString());

        return await ConnectAsync(
            uri.Host,
            uri.Port,
            async (upstream, token) =>
            {
                await upstream.Socket.SendAsync(bytes, SocketFlags.None, token);
                upstream.CountSent(bytes.Length);
            },
            RefuseHttpAsync);
    }

    // Une destination muette devient 504 ; les autres échecs, 502.
    private Task RefuseHttpAsync(ProxyConnectException ex, CancellationToken ct)
        => WriteHttpAsync(ex.TimedOut ? "504 Gateway Timeout" : "502 Bad Gateway", ex.Message, ct);

    private bool HttpAuthorized(IEnumerable<string> headers)
    {
        var header = headers.FirstOrDefault(h => h.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase));
        if (header is null)
        {
            return false;
        }

        var value = header[(header.IndexOf(':') + 1)..].Trim();
        if (!value.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value[6..].Trim()));
            var separator = decoded.IndexOf(':');
            return separator >= 0 && CredentialsMatch(decoded[..separator], decoded[(separator + 1)..]);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private bool CredentialsMatch(string user, string password)
    {
        if (_credentials is null)
        {
            return true;
        }

        var userOk = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(user), Encoding.UTF8.GetBytes(_credentials.Username));
        var passwordOk = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(_credentials.Password));
        return userOk & passwordOk;
    }

    internal static bool TrySplitHostPort(string target, int defaultPort, out string host, out int port)
    {
        host = string.Empty;
        port = defaultPort;
        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        if (target.StartsWith('['))
        {
            var close = target.IndexOf(']');
            if (close < 0)
            {
                return false;
            }

            host = target[1..close];
            var rest = target[(close + 1)..];
            return rest.Length == 0 || (rest[0] == ':' && int.TryParse(rest[1..], out port) && port is > 0 and < 65536);
        }

        var colon = target.LastIndexOf(':');
        if (colon < 0)
        {
            host = target;
            return true;
        }

        host = target[..colon];
        return host.Length > 0 && int.TryParse(target[(colon + 1)..], out port) && port is > 0 and < 65536;
    }

    private async Task WriteHttpAsync(string status, string body, CancellationToken ct, string? extraHeader = null)
    {
        var payload = Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {status}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {payload.Length}\r\n" +
                   (extraHeader is null ? string.Empty : extraHeader + "\r\n") + "Connection: close\r\n\r\n";
        await _clientStream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await _clientStream.WriteAsync(payload, ct);
    }

    // --- Connexion sortante -----------------------------------------------------------------------------------

    /// <summary>
    /// Demande une connexion sortante puis l'annonce au client. Le délai de négociation ne s'applique pas ici : essayer
    /// plusieurs liaisons peut prendre plus longtemps, et un client doit toujours recevoir une réponse (0x04 / 504) plutôt
    /// qu'une coupure muette. Si l'annonce échoue (client parti), la connexion sortante est libérée aussitôt.
    /// </summary>
    private async Task<UpstreamConnection?> ConnectAsync(
        string host,
        int port,
        Func<UpstreamConnection, CancellationToken, Task> announce,
        Func<ProxyConnectException, CancellationToken, Task> refuse)
    {
        UpstreamConnection upstream;
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(_serviceToken))
        {
            connect.CancelAfter(_connectBudget);
            try
            {
                upstream = await _connector.ConnectAsync(host, port, connect.Token);
            }
            catch (ProxyConnectException ex)
            {
                await refuse(ex, _serviceToken);
                return null;
            }
            catch (OperationCanceledException) when (!_serviceToken.IsCancellationRequested)
            {
                await refuse(new ProxyConnectException($"Délai dépassé pour joindre {host}:{port}.", 4) { TimedOut = true }, _serviceToken);
                return null;
            }
        }

        try
        {
            await announce(upstream, _serviceToken);
            return upstream;
        }
        catch
        {
            upstream.Dispose();
            throw;
        }
    }

    // --- Relais ------------------------------------------------------------------------------------------------

    private async Task RelayAsync(UpstreamConnection upstream, CancellationToken cancellationToken)
    {
        using var upstreamStream = new NetworkStream(upstream.Socket, ownsSocket: false);

        // Octets envoyés par le client avant même que la connexion sortante ne soit prête (corps de requête, début TLS…).
        var pending = _reader.Buffered;
        if (pending.Length > 0)
        {
            await upstreamStream.WriteAsync(pending, cancellationToken);
            upstream.CountSent(pending.Length);
            _reader.Consume(pending.Length);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var lastActivity = Environment.TickCount64;
        var halfClosed = 0;

        void Touch() => Interlocked.Exchange(ref lastActivity, Environment.TickCount64);

        async Task PumpAsync(Stream from, Stream to, Socket destination, Action<long> count)
        {
            var buffer = new byte[32 * 1024];
            try
            {
                while (true)
                {
                    var read = await from.ReadAsync(buffer, cts.Token);
                    if (read == 0)
                    {
                        break;
                    }

                    await to.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                    count(read);
                    Touch();
                }

                // Fin d'émission de ce côté : on le propage à l'autre extrémité mais on laisse l'autre sens se terminer.
                Interlocked.Exchange(ref halfClosed, 1);
                Touch();
                try
                {
                    destination.Shutdown(SocketShutdown.Send);
                }
                catch (SocketException)
                {
                    // Déjà fermé par l'autre extrémité.
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                cts.Cancel();
            }
        }

        var watchdog = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), cts.Token);
                    var idle = TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref lastActivity));
                    if (idle > (Volatile.Read(ref halfClosed) == 1 ? HalfClosedIdleTimeout : IdleTimeout))
                    {
                        cts.Cancel();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Relais terminé.
            }
        }, CancellationToken.None);

        await Task.WhenAll(
            PumpAsync(_clientStream, upstreamStream, upstream.Socket, upstream.CountSent),
            PumpAsync(upstreamStream, _clientStream, _client, upstream.CountReceived));

        await cts.CancelAsync();
        await watchdog;
    }
}
