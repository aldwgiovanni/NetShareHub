using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using NetShareHub.Application.Contracts.Services;
using NetShareHub.Domain.Entities;
using NetShareHub.Domain.Exceptions;

namespace NetShareHub.Infrastructure.Services.Dispatch;

/// <summary>
/// Force un socket sortant à emprunter un adaptateur précis, sans pilote ni droits particuliers :
/// <list type="bullet">
/// <item>Windows : <c>IP_UNICAST_IF</c> (interface de sortie imposée) + liaison à l'adresse source.</item>
/// <item>Linux : <c>SO_BINDTODEVICE</c> (requiert root ou noyau ≥ 5.7) + liaison à l'adresse source.</item>
/// </list>
/// </summary>
public sealed class SocketBinder : ISocketBinder
{
    private const int IpProtoIp = 0;
    private const int IpUnicastIf = 31;
    private const int SolSocket = 1;
    private const int SoBindToDevice = 25;

    private readonly ILogger<SocketBinder> _logger;
    private int _warnedBindToDevice;

    public SocketBinder(ILogger<SocketBinder> logger)
    {
        _logger = logger;
    }

    public Socket CreateBoundSocket(NetworkAdapter adapter)
    {
        if (adapter.IPv4Address is null)
        {
            throw new DispatchException($"« {adapter.Name} » n'a pas d'adresse IPv4.");
        }

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

            if (OperatingSystem.IsWindows())
            {
                var index = IPAddress.HostToNetworkOrder(adapter.InterfaceIndex);
                socket.SetRawSocketOption(IpProtoIp, IpUnicastIf, BitConverter.GetBytes(index));
            }
            else if (OperatingSystem.IsLinux())
            {
                try
                {
                    socket.SetRawSocketOption(SolSocket, SoBindToDevice, Encoding.ASCII.GetBytes(adapter.Name + "\0"));
                }
                catch (SocketException ex)
                {
                    if (Interlocked.Exchange(ref _warnedBindToDevice, 1) == 0)
                    {
                        _logger.LogWarning(
                            "SO_BINDTODEVICE refusé ({Message}) : lancez NetShare Hub en root pour garantir le passage par la bonne carte.",
                            ex.Message);
                    }
                }
            }

            socket.Bind(new IPEndPoint(adapter.IPv4Address, 0));
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
