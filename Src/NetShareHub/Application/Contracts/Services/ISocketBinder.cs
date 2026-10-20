using System.Net.Sockets;
using NetShareHub.Domain.Entities;

namespace NetShareHub.Application.Contracts.Services;

/// <summary>Crée des sockets TCP sortants forcés à passer par un adaptateur précis.</summary>
public interface ISocketBinder
{
    /// <summary>Socket TCP IPv4 dont le trafic sortant est contraint à <paramref name="adapter"/>.</summary>
    Socket CreateBoundSocket(NetworkAdapter adapter);
}
