using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace NetShareHub.Infrastructure.Services.Dhcp;

/// <summary>
/// Serveur DHCP minimal, un socket par adaptateur partagé. Utilisé quand le système n'en fournit pas
/// (NAT intégré de NetShare Hub sous Windows, ou sous Linux sans NetworkManager).
/// </summary>
public sealed class DhcpServer : IAsyncDisposable
{
    private const int ServerPort = 67;
    private const int ClientPort = 68;

    // SOL_SOCKET / SO_BINDTODEVICE sous Linux.
    private const int SolSocket = 1;
    private const int SoBindToDevice = 25;

    // SIO_UDP_CONNRESET : évite que les « port unreachable » ICMP ne fassent échouer la réception sous Windows.
    private const int SioUdpConnReset = -1744830452;

    private readonly ILogger _logger;
    private readonly List<Listener> _listeners = new();
    private readonly CancellationTokenSource _cts = new();

    public DhcpServer(ILogger logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<DhcpScope> Scopes => _listeners.Select(l => l.Table.Scope).ToList();

    /// <summary>Démarre l'écoute sur chaque scope. Lève <see cref="SocketException"/> si le port 67 est déjà pris.</summary>
    public void Start(IEnumerable<DhcpScope> scopes)
    {
        foreach (var scope in scopes)
        {
            var socket = CreateSocket(scope);
            var listener = new Listener(socket, new DhcpLeaseTable(scope));
            listener.Loop = Task.Run(() => ReceiveLoopAsync(listener, _cts.Token));
            _listeners.Add(listener);
            _logger.LogInformation("DHCP actif sur {Name} : {Start} – {End}", scope.InterfaceName, scope.RangeStart, scope.RangeEnd);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        foreach (var listener in _listeners)
        {
            listener.Socket.Close();
        }

        foreach (var listener in _listeners)
        {
            try
            {
                await listener.Loop.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // Arrêt demandé : l'erreur de réception due à la fermeture du socket est normale.
            }

            listener.Socket.Dispose();
        }

        _listeners.Clear();
        _cts.Dispose();
    }

    private static Socket CreateSocket(DhcpScope scope)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.EnableBroadcast = true;

            if (OperatingSystem.IsLinux())
            {
                // Sous Linux, un socket lié à une adresse unicast ne reçoit pas les broadcasts :
                // on écoute sur 0.0.0.0 mais on le confine à l'adaptateur partagé.
                socket.SetRawSocketOption(SolSocket, SoBindToDevice, Encoding.ASCII.GetBytes(scope.InterfaceName + "\0"));
                socket.Bind(new IPEndPoint(IPAddress.Any, ServerPort));
            }
            else
            {
                if (OperatingSystem.IsWindows())
                {
                    socket.IOControl(SioUdpConnReset, new byte[] { 0 }, null);
                }

                socket.Bind(new IPEndPoint(scope.ServerAddress, ServerPort));
            }

            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task ReceiveLoopAsync(Listener listener, CancellationToken cancellationToken)
    {
        var buffer = new byte[1500];
        var remote = new IPEndPoint(IPAddress.Any, 0);
        var broadcast = new IPEndPoint(IPAddress.Broadcast, ClientPort);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var received = await listener.Socket.ReceiveFromAsync(buffer, SocketFlags.None, remote, cancellationToken);
                if (!DhcpPacket.TryParse(buffer.AsSpan(0, received.ReceivedBytes), out var request))
                {
                    continue;
                }

                var reply = DhcpEngine.Handle(request, listener.Table);
                if (reply is null)
                {
                    continue;
                }

                // Réponse toujours en broadcast : le client n'a pas encore d'adresse et certains ignorent l'unicast.
                await listener.Socket.SendToAsync(reply.ToBytes(), SocketFlags.None, broadcast, cancellationToken);
                _logger.LogDebug("DHCP {Type} → {Mac} ({Address})", reply.MessageType, request.MacAddress, reply.YourAddress);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
            {
                // Datagramme rejeté : on continue d'écouter.
            }
            catch (SocketException ex) when (cancellationToken.IsCancellationRequested || ex.SocketErrorCode == SocketError.OperationAborted)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erreur DHCP sur {Name}", listener.Table.Scope.InterfaceName);
                await Task.Delay(500, CancellationToken.None);
            }
        }
    }

    private sealed class Listener(Socket socket, DhcpLeaseTable table)
    {
        public Socket Socket { get; } = socket;

        public DhcpLeaseTable Table { get; } = table;

        public Task Loop { get; set; } = Task.CompletedTask;
    }
}
