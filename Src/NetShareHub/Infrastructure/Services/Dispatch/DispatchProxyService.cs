using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using NetShareHub.Application.Contracts.Services;
using NetShareHub.Application.Services;
using NetShareHub.Domain.Dtos.Incoming;
using NetShareHub.Domain.Dtos.Outgoing;
using NetShareHub.Domain.Entities;
using NetShareHub.Domain.Enums;
using NetShareHub.Domain.Exceptions;

namespace NetShareHub.Infrastructure.Services.Dispatch;

/// <summary>
/// Proxy SOCKS5 + HTTP sur un seul port. Chaque nouvelle connexion est affectée à une liaison Internet
/// par le <see cref="UplinkBalancer"/> puis forcée à sortir par l'adaptateur correspondant.
/// Fonctionne sans droits administrateur, sous Windows comme sous Linux.
/// </summary>
public sealed class DispatchProxyService : IDispatchService, IUpstreamConnector, IAsyncDisposable
{
    private const int MaxClientConnections = 4096;
    private const int ConnectTimeoutMs = 8000;
    private const int FailuresBeforeDown = 3;
    private static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(5);

    private static readonly IPEndPoint[] ProbeTargets =
    {
        new(IPAddress.Parse("1.1.1.1"), 443),
        new(IPAddress.Parse("8.8.8.8"), 443),
        new(IPAddress.Parse("9.9.9.9"), 443),
    };

    private readonly INetworkInventoryService _inventory;
    private readonly ISocketBinder _binder;
    private readonly ILogger<DispatchProxyService> _logger;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly ConcurrentDictionary<Socket, byte> _clients = new();

    private RunState _state = RunState.Stopped;
    private string _message = "Dispatch arrêté.";
    private DispatchConfig_InDto? _config;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private UplinkBalancer? _balancer;
    private Dictionary<string, UplinkRuntime> _runtimes = new(StringComparer.OrdinalIgnoreCase);
    private ProxyCredentials? _credentials;
    private List<Task> _background = new();
    private (DateTime At, IReadOnlySet<IPAddress> Addresses)? _localAddresses;

    public DispatchProxyService(INetworkInventoryService inventory, ISocketBinder binder, ILogger<DispatchProxyService> logger)
    {
        _inventory = inventory;
        _binder = binder;
        _logger = logger;
    }

    /// <summary>Port réellement ouvert (utile quand la configuration demande le port 0 dans les tests).</summary>
    public int BoundPort => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? 0;

    public async Task StartAsync(DispatchConfig_InDto config, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (_state is RunState.Running or RunState.Starting)
            {
                throw new DispatchException("Le Dispatch est déjà démarré.");
            }

            if (config.Port is < 0 or > 65535)
            {
                throw new DispatchException("Le port doit être compris entre 1 et 65535.");
            }

            var adapters = _inventory.Snapshot;
            var uplinks = config.Uplinks.Where(u => u.Enabled).ToList();
            if (uplinks.Count == 0)
            {
                throw new DispatchException("Choisissez au moins une connexion Internet à utiliser.");
            }

            foreach (var uplink in uplinks)
            {
                var adapter = adapters.FirstOrDefault(a => a.Id.Equals(uplink.AdapterId, StringComparison.OrdinalIgnoreCase));
                if (adapter is null || !adapter.HasInternetRoute)
                {
                    throw new DispatchException($"La connexion « {adapter?.Name ?? uplink.AdapterId} » n'a pas d'accès Internet actif.");
                }
            }

            var hasUser = !string.IsNullOrEmpty(config.Username);
            if (hasUser && string.IsNullOrEmpty(config.Password))
            {
                throw new DispatchException("Renseignez aussi un mot de passe, ou laissez l'identifiant vide pour un proxy ouvert.");
            }

            _credentials = hasUser ? new ProxyCredentials(config.Username!, config.Password!) : null;
            _config = config;
            _message = "Démarrage…";
            _state = RunState.Starting;

            _balancer = new UplinkBalancer(
                config.Strategy,
                config.StickyDestinations ? TimeSpan.FromMinutes(Math.Clamp(config.StickyMinutes, 1, 240)) : null);
            _balancer.SetUplinks(config.Uplinks.Select(u => (u.AdapterId, u.Weight, u.Enabled)));
            _runtimes = config.Uplinks.ToDictionary(
                u => u.AdapterId,
                u => new UplinkRuntime(u.AdapterId, u.Weight, u.Enabled),
                StringComparer.OrdinalIgnoreCase);

            var address = config.AllowLan ? IPAddress.Any : IPAddress.Loopback;
            var listener = new TcpListener(address, config.Port);
            try
            {
                listener.Start(512);
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
            {
                _state = RunState.Faulted;
                _message = $"Le port {config.Port} est déjà utilisé par un autre programme.";
                throw new DispatchException(_message, ex);
            }

            _listener = listener;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _background = new List<Task>
            {
                Task.Run(() => AcceptLoopAsync(listener, token), CancellationToken.None),
                Task.Run(() => HealthLoopAsync(token), CancellationToken.None),
                Task.Run(() => RateLoopAsync(token), CancellationToken.None),
            };

            _state = RunState.Running;
            _message = "Dispatch actif.";
            _logger.LogInformation(
                "Dispatch démarré sur {Address}:{Port} ({Strategy}, {Count} liaisons)",
                address, BoundPort, config.Strategy, uplinks.Count);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_state is RunState.Stopped)
            {
                return;
            }

            _state = RunState.Stopping;
            await StopCoreAsync();
            _state = RunState.Stopped;
            _message = "Dispatch arrêté.";
            _logger.LogInformation("Dispatch arrêté");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    public DispatchStatus_OutDto GetStatus()
    {
        var adapters = _inventory.Snapshot;
        var config = _config;
        var balancer = _balancer;
        var running = _state == RunState.Running;

        var status = new DispatchStatus_OutDto
        {
            State = _state,
            Message = _message,
            Strategy = config?.Strategy ?? DispatchStrategy.WeightedRoundRobin,
        };

        if (config is null || balancer is null)
        {
            return status;
        }

        status.ListenEndpoint = $"{(config.AllowLan ? "0.0.0.0" : "127.0.0.1")}:{BoundPort}";
        if (running)
        {
            status.ClientEndpoints = ClientEndpoints(config.AllowLan, BoundPort, adapters);
        }

        foreach (var uplink in config.Uplinks)
        {
            if (!_runtimes.TryGetValue(uplink.AdapterId, out var runtime))
            {
                continue;
            }

            var adapter = adapters.FirstOrDefault(a => a.Id.Equals(uplink.AdapterId, StringComparison.OrdinalIgnoreCase));
            status.Uplinks.Add(new UplinkStats_OutDto
            {
                AdapterId = uplink.AdapterId,
                Name = adapter?.Name ?? uplink.AdapterId,
                IPv4 = adapter?.IPv4Address?.ToString() ?? string.Empty,
                Health = balancer.GetHealth(uplink.AdapterId),
                Enabled = uplink.Enabled,
                Weight = uplink.Weight,
                ActiveConnections = balancer.GetActive(uplink.AdapterId),
                TotalConnections = Interlocked.Read(ref runtime.Total),
                FailedConnections = Interlocked.Read(ref runtime.Failed),
                BytesSent = Interlocked.Read(ref runtime.BytesSent),
                BytesReceived = Interlocked.Read(ref runtime.BytesReceived),
                SendRateBytesPerSecond = runtime.SendRate,
                ReceiveRateBytesPerSecond = runtime.ReceiveRate,
                LatencyMs = runtime.LatencyMs,
            });
        }

        status.ActiveConnections = status.Uplinks.Sum(u => u.ActiveConnections);
        return status;
    }

    internal static List<string> ClientEndpoints(bool allowLan, int port, IReadOnlyList<NetworkAdapter> adapters)
    {
        var endpoints = new List<string> { $"127.0.0.1:{port}" };
        if (allowLan)
        {
            endpoints.AddRange(adapters
                .Where(a => a.IsUp && !a.IsLoopback && a.IPv4Address is not null)
                .Select(a => $"{a.IPv4Address}:{port}"));
        }

        return endpoints;
    }

    // --- Connexions entrantes ------------------------------------------------------------------------------

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptSocketAsync(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                _logger.LogWarning("Erreur d'acceptation : {Message}", ex.Message);
                await Task.Delay(200, CancellationToken.None);
                continue;
            }

            if (_clients.Count >= MaxClientConnections)
            {
                client.Dispose();
                continue;
            }

            _clients[client] = 0;
            _ = Task.Run(() => HandleClientAsync(client, token), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(Socket client, CancellationToken token)
    {
        try
        {
            client.NoDelay = true;
            client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            // Un client venu du réseau (mode « Ouvert au réseau local ») ne doit pas atteindre les services de cette machine
            // par le proxy : ce serait contourner le pare-feu et les services qui n'écoutent qu'en local.
            var fromThisMachine = client.RemoteEndPoint is IPEndPoint remote && IPAddress.IsLoopback(remote.Address);
            IUpstreamConnector connector = fromThisMachine ? this : new NetworkClientConnector(this);
            await new ProxySession(client, connector, _credentials).RunAsync(token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException
                                       or EndOfStreamException or InvalidDataException)
        {
            // Client déconnecté, délai de négociation dépassé ou arrêt du service : sans importance.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Erreur inattendue sur une connexion cliente");
        }
        finally
        {
            _clients.TryRemove(client, out _);
            client.Dispose();
        }
    }

    // --- Connexions sortantes (IUpstreamConnector) -----------------------------------------------------------

    Task<UpstreamConnection> IUpstreamConnector.ConnectAsync(string host, int port, CancellationToken cancellationToken)
        => ConnectUpstreamAsync(host, port, fromNetwork: false, cancellationToken);

    private async Task<UpstreamConnection> ConnectUpstreamAsync(string host, int port, bool fromNetwork, CancellationToken cancellationToken)
    {
        var balancer = _balancer ?? throw new ProxyConnectException("Dispatch arrêté.", 1);

        var addresses = await ResolveAsync(host, cancellationToken);
        if (fromNetwork)
        {
            // Filtre appliqué après résolution : un nom qui pointe vers 127.0.0.1 ou vers la machine est refusé lui aussi.
            var local = LocalAddresses();
            addresses = addresses.Where(a => !IsHostDestination(a, local)).ToList();
            if (addresses.Count == 0)
            {
                throw new ProxyConnectException($"Destination refusée : {host} désigne cet ordinateur ou une adresse réservée.", 2);
            }
        }

        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? lastError = null;

        while (true)
        {
            var adapterId = balancer.Acquire(host, tried);
            if (adapterId is null)
            {
                throw new ProxyConnectException(
                    lastError ?? "Aucune liaison Internet disponible.",
                    lastError is null ? (byte)3 : (byte)4);
            }

            tried.Add(adapterId);
            var runtime = _runtimes[adapterId];
            var adapter = _inventory.Snapshot.FirstOrDefault(a => a.Id.Equals(adapterId, StringComparison.OrdinalIgnoreCase));
            if (adapter is null || !adapter.HasInternetRoute)
            {
                MarkDown(adapterId, "adaptateur absent ou déconnecté");
                balancer.Release(adapterId);
                lastError = "La liaison choisie est déconnectée.";
                continue;
            }

            Socket? socket = null;
            try
            {
                socket = await ConnectBoundAsync(adapter, addresses, port, cancellationToken);
                Interlocked.Exchange(ref runtime.ConsecutiveFailures, 0);
                Interlocked.Increment(ref runtime.Total);
                return new UpstreamConnection
                {
                    Socket = socket,
                    AdapterId = adapterId,
                    CountSent = n => Interlocked.Add(ref runtime.BytesSent, n),
                    CountReceived = n => Interlocked.Add(ref runtime.BytesReceived, n),
                    OnReleased = () => balancer.Release(adapterId),
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                balancer.Release(adapterId);
                throw;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or DispatchException)
            {
                socket?.Dispose();
                balancer.Release(adapterId);

                var socksCode = ex is SocketException se ? SocksCodeFor(se.SocketErrorCode) : (byte)1;
                lastError = $"Connexion impossible vers {host}:{port} via {adapter.Name} ({ex.Message})";

                // Un refus de connexion vient de la destination, pas de la liaison : inutile d'essayer les autres ni de la sanctionner.
                if (socksCode == 5)
                {
                    throw new ProxyConnectException(lastError, socksCode, ex);
                }

                Interlocked.Increment(ref runtime.Failed);
                if (Interlocked.Increment(ref runtime.ConsecutiveFailures) >= FailuresBeforeDown)
                {
                    MarkDown(adapterId, "échecs de connexion répétés");
                }
            }
        }
    }

    private async Task<Socket> ConnectBoundAsync(
        NetworkAdapter adapter, IReadOnlyList<IPAddress> addresses, int port, CancellationToken cancellationToken)
    {
        Exception? last = null;
        foreach (var address in addresses.Take(3))
        {
            var socket = _binder.CreateBoundSocket(adapter);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(ConnectTimeoutMs);
                await socket.ConnectAsync(new IPEndPoint(address, port), timeout.Token);
                return socket;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                socket.Dispose();
                last = new TimeoutException($"délai de {ConnectTimeoutMs / 1000}s dépassé");
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                last = ex;
                if (ex.SocketErrorCode == SocketError.ConnectionRefused)
                {
                    throw;
                }
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw last is SocketException se ? se : new SocketException((int)SocketError.TimedOut);
    }

    private static async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        // Dns.GetHostAddressesAsync("") renvoie toutes les adresses de la machine : un nom vide n'est jamais une destination.
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ProxyConnectException("Nom de destination vide.", 2);
        }

        if (IPAddress.TryParse(host, out var literal))
        {
            return literal.AddressFamily == AddressFamily.InterNetwork
                ? new[] { literal }
                : throw new ProxyConnectException("Les adresses IPv6 littérales ne sont pas gérées.", 8);
        }

        try
        {
            var all = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cancellationToken);
            return all.Length > 0
                ? all
                : throw new ProxyConnectException($"Aucune adresse IPv4 pour {host}.", 4);
        }
        catch (SocketException ex)
        {
            throw new ProxyConnectException($"Nom introuvable : {host}.", 4, ex);
        }
    }

    /// <summary>
    /// Vrai pour une destination interdite à un client du réseau : boucle locale, 0.0.0.0/8, lien local (169.254/16),
    /// multidiffusion et adresses réservées (224/3, dont 255.255.255.255), ou l'une des adresses de cette machine.
    /// </summary>
    internal static bool IsHostDestination(IPAddress address, IReadOnlySet<IPAddress> localAddresses)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || localAddresses.Contains(address))
        {
            return true;
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return true; // les liaisons sont forcées en IPv4 : rien de légitime à joindre autrement
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 0
               || (bytes[0] == 169 && bytes[1] == 254)
               || bytes[0] >= 224;
    }

    // Adresses IPv4 de toutes les interfaces (pas seulement celles que l'inventaire affiche), relues au plus toutes les 5 s.
    private IReadOnlySet<IPAddress> LocalAddresses()
    {
        var cached = _localAddresses;
        if (cached is not null && DateTime.UtcNow - cached.Value.At < TimeSpan.FromSeconds(5))
        {
            return cached.Value.Addresses;
        }

        var addresses = new HashSet<IPAddress>(_inventory.Snapshot.Select(a => a.IPv4Address).OfType<IPAddress>());
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    addresses.Add(unicast.Address.IsIPv4MappedToIPv6 ? unicast.Address.MapToIPv4() : unicast.Address);
                }
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            // Liste système indisponible : on garde les adresses de l'inventaire.
        }

        _localAddresses = (DateTime.UtcNow, addresses);
        return addresses;
    }

    private static byte SocksCodeFor(SocketError error) => error switch
    {
        SocketError.ConnectionRefused => 5,
        SocketError.HostNotFound or SocketError.HostUnreachable or SocketError.NoData => 4,
        SocketError.NetworkUnreachable => 3,
        SocketError.TimedOut => 4,
        _ => 1,
    };

    // --- Santé et débits -------------------------------------------------------------------------------------

    private async Task HealthLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(HealthInterval);
        do
        {
            try
            {
                var checks = _runtimes.Values.Where(r => r.Enabled).Select(r => CheckAsync(r, token));
                await Task.WhenAll(checks);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erreur du contrôle de santé");
            }
        }
        while (await WaitNextAsync(timer, token));
    }

    private static async Task<bool> WaitNextAsync(PeriodicTimer timer, CancellationToken token)
    {
        try
        {
            return await timer.WaitForNextTickAsync(token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task CheckAsync(UplinkRuntime runtime, CancellationToken token)
    {
        var adapter = _inventory.Snapshot.FirstOrDefault(a => a.Id.Equals(runtime.AdapterId, StringComparison.OrdinalIgnoreCase));
        if (adapter is null || !adapter.HasInternetRoute)
        {
            MarkDown(runtime.AdapterId, "adaptateur absent ou déconnecté");
            return;
        }

        if (_config?.HealthProbe != true)
        {
            runtime.LatencyMs = null;
            MarkUp(runtime.AdapterId);
            return;
        }

        foreach (var target in ProbeTargets)
        {
            var started = Environment.TickCount64;
            try
            {
                using var socket = _binder.CreateBoundSocket(adapter);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(2500);
                await socket.ConnectAsync(target, timeout.Token);
                runtime.LatencyMs = (int)(Environment.TickCount64 - started);
                Interlocked.Exchange(ref runtime.ProbeFailures, 0);
                MarkUp(runtime.AdapterId);
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or DispatchException)
            {
                // Essai suivant.
            }
        }

        runtime.LatencyMs = null;
        if (Interlocked.Increment(ref runtime.ProbeFailures) >= 2)
        {
            MarkDown(runtime.AdapterId, "aucune réponse aux sondes");
        }
    }

    private void MarkDown(string adapterId, string reason)
    {
        var balancer = _balancer;
        if (balancer is null || balancer.GetHealth(adapterId) == UplinkHealth.Down)
        {
            return;
        }

        balancer.SetHealth(adapterId, UplinkHealth.Down);
        balancer.ForgetSticky(adapterId);
        _logger.LogWarning("Liaison {Id} hors service : {Reason}", adapterId, reason);
    }

    private void MarkUp(string adapterId)
    {
        var balancer = _balancer;
        if (balancer is null)
        {
            return;
        }

        if (balancer.GetHealth(adapterId) == UplinkHealth.Down)
        {
            _logger.LogInformation("Liaison {Id} rétablie", adapterId);
        }

        Interlocked.Exchange(ref _runtimes[adapterId].ConsecutiveFailures, 0);
        balancer.SetHealth(adapterId, UplinkHealth.Healthy);
    }

    private async Task RateLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var previous = Environment.TickCount64;
        while (await WaitNextAsync(timer, token))
        {
            var now = Environment.TickCount64;
            var seconds = Math.Max(0.1, (now - previous) / 1000.0);
            previous = now;
            foreach (var runtime in _runtimes.Values)
            {
                runtime.SampleRates(seconds);
            }
        }
    }

    private async Task StopCoreAsync()
    {
        var cts = _cts;
        if (cts is not null)
        {
            await cts.CancelAsync();
        }

        try
        {
            _listener?.Stop();
        }
        catch (SocketException)
        {
            // Déjà arrêté.
        }

        foreach (var client in _clients.Keys)
        {
            try
            {
                client.Close();
            }
            catch (ObjectDisposedException)
            {
                // Déjà fermé.
            }
        }

        try
        {
            await Task.WhenAll(_background).WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            // Les boucles s'arrêtent d'elles-mêmes dès que le jeton est annulé.
        }

        _background = new List<Task>();
        _listener = null;
        cts?.Dispose();
        _cts = null;
    }

    /// <summary>Connexions sortantes d'un client venu du réseau : les destinations de cette machine lui sont refusées.</summary>
    private sealed class NetworkClientConnector(DispatchProxyService owner) : IUpstreamConnector
    {
        public Task<UpstreamConnection> ConnectAsync(string host, int port, CancellationToken cancellationToken)
            => owner.ConnectUpstreamAsync(host, port, fromNetwork: true, cancellationToken);
    }

    private sealed class UplinkRuntime(string adapterId, int weight, bool enabled)
    {
        public string AdapterId { get; } = adapterId;

        public int Weight { get; } = weight;

        public bool Enabled { get; } = enabled;

        public long Total;
        public long Failed;
        public long BytesSent;
        public long BytesReceived;
        public int ConsecutiveFailures;
        public int ProbeFailures;
        private int _latency = -1;

        public int? LatencyMs
        {
            get
            {
                var value = Volatile.Read(ref _latency);
                return value < 0 ? null : value;
            }
            set => Volatile.Write(ref _latency, value ?? -1);
        }

        public double SendRate;
        public double ReceiveRate;
        private long _lastSent;
        private long _lastReceived;

        public void SampleRates(double seconds)
        {
            var sent = Interlocked.Read(ref BytesSent);
            var received = Interlocked.Read(ref BytesReceived);
            // Moyenne exponentielle pour des chiffres lisibles à l'écran.
            SendRate = (SendRate * 0.4) + ((sent - _lastSent) / seconds * 0.6);
            ReceiveRate = (ReceiveRate * 0.4) + ((received - _lastReceived) / seconds * 0.6);
            _lastSent = sent;
            _lastReceived = received;
        }
    }
}
