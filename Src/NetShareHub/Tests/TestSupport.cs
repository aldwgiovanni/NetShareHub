using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using NetShareHub.Application.Contracts.Services;
using NetShareHub.Domain.Entities;
using NetShareHub.Domain.Enums;

namespace NetShareHub.Tests;

internal static class TestAdapters
{
    public static NetworkAdapter Make(
        string id,
        string? name = null,
        AdapterKind kind = AdapterKind.Ethernet,
        bool up = true,
        string? ip = "192.168.1.20",
        string? gateway = "192.168.1.1",
        bool virtualAdapter = false,
        int index = 0,
        long speed = 1_000_000_000)
        => new()
        {
            Id = id,
            Name = name ?? id,
            Kind = kind,
            IsUp = up,
            IsVirtual = virtualAdapter,
            InterfaceIndex = index == 0 ? Math.Abs(id.GetHashCode() % 50) + 2 : index,
            IPv4Address = ip is null ? null : IPAddress.Parse(ip),
            IPv4Mask = ip is null ? null : IPAddress.Parse("255.255.255.0"),
            Gateway = gateway is null ? null : IPAddress.Parse(gateway),
            DnsServers = new[] { IPAddress.Parse("192.168.1.1") },
            SpeedBitsPerSecond = speed,
        };

    public static BackendCapabilities Caps(
        HostPlatform platform,
        bool hotspot = true,
        bool networkManager = false,
        bool balancing = false)
        => new(platform, hotspot, networkManager, balancing, Array.Empty<string>());
}

internal sealed class FakeInventory : INetworkInventoryService
{
    public FakeInventory(params NetworkAdapter[] adapters)
    {
        Snapshot = adapters;
    }

    public IReadOnlyList<NetworkAdapter> Snapshot { get; set; }

    public event EventHandler? AdaptersChanged
    {
        add { }
        remove { }
    }

    public Task<IReadOnlyList<NetworkAdapter>> GetAdaptersAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Snapshot);
}

/// <summary>Enregistre les commandes au lieu de les exécuter ; permet de simuler des réponses.</summary>
internal sealed class RecordingRunner : ICommandRunner
{
    private readonly Func<CommandSpec, CommandResult>? _responder;

    public RecordingRunner(Func<CommandSpec, CommandResult>? responder = null)
    {
        _responder = responder;
    }

    public List<CommandSpec> Commands { get; } = new();

    public IEnumerable<string> Lines => Commands.Select(c => c.Script is not null ? "ps: " + c.Description : c.Display);

    public Task<CommandResult> RunAsync(CommandSpec spec, CancellationToken cancellationToken = default)
    {
        lock (Commands)
        {
            Commands.Add(spec);
        }

        return Task.FromResult(_responder?.Invoke(spec) ?? new CommandResult(0, string.Empty, string.Empty));
    }
}

/// <summary>Remplace le forçage d'interface par un simple socket lié à la boucle locale, en comptant les appels par adaptateur.</summary>
internal sealed class LoopbackBinder : ISocketBinder
{
    private readonly Dictionary<string, int> _calls = new();

    public IReadOnlyDictionary<string, int> Calls
    {
        get
        {
            lock (_calls)
            {
                return new Dictionary<string, int>(_calls);
            }
        }
    }

    public Socket CreateBoundSocket(NetworkAdapter adapter)
    {
        lock (_calls)
        {
            _calls[adapter.Id] = _calls.GetValueOrDefault(adapter.Id) + 1;
        }

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return socket;
    }
}

/// <summary>Remplace le vrai serveur DHCP : mémorise les scopes demandés et peut simuler un port déjà occupé.</summary>
internal sealed class FakeDhcpFactory : NetShareHub.Infrastructure.Services.Dhcp.IDhcpServerFactory
{
    public bool PortBusy { get; set; }

    public List<NetShareHub.Infrastructure.Services.Dhcp.DhcpScope> Scopes { get; } = new();

    public int Stopped { get; private set; }

    public IAsyncDisposable Start(IReadOnlyList<NetShareHub.Infrastructure.Services.Dhcp.DhcpScope> scopes)
    {
        if (PortBusy)
        {
            throw new SocketException((int)SocketError.AddressAlreadyInUse);
        }

        Scopes.AddRange(scopes);
        return new Handle(this);
    }

    private sealed class Handle(FakeDhcpFactory owner) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            owner.Stopped++;
            return ValueTask.CompletedTask;
        }
    }
}

internal static class Logs
{
    public static Microsoft.Extensions.Logging.ILogger<T> For<T>() => NullLogger<T>.Instance;
}

/// <summary>Mémorise les messages écrits, pour vérifier ce qui apparaît dans le Journal.</summary>
internal sealed class ListLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    private readonly List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> _entries = new();

    public IReadOnlyList<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToList();
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_entries)
        {
            _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nsh-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Nettoyage au mieux.
        }
    }
}
