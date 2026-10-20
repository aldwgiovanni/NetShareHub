using Microsoft.Extensions.Logging;

namespace NetShareHub.Infrastructure.Services.Dhcp;

/// <summary>Démarre un serveur DHCP sur des scopes. Abstraction qui évite d'ouvrir de vrais sockets dans les tests des moteurs de partage.</summary>
public interface IDhcpServerFactory
{
    /// <summary>Démarre l'écoute ; la libération de l'objet renvoyé l'arrête. Lève <see cref="System.Net.Sockets.SocketException"/> si le port est pris.</summary>
    IAsyncDisposable Start(IReadOnlyList<DhcpScope> scopes);
}

public sealed class DhcpServerFactory : IDhcpServerFactory
{
    private readonly ILogger<DhcpServer> _logger;

    public DhcpServerFactory(ILogger<DhcpServer> logger)
    {
        _logger = logger;
    }

    public IAsyncDisposable Start(IReadOnlyList<DhcpScope> scopes)
    {
        var server = new DhcpServer(_logger);
        try
        {
            server.Start(scopes);
            return server;
        }
        catch
        {
            server.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }
}
