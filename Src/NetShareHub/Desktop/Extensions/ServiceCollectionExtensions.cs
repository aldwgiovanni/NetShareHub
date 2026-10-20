using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetShareHub.Application.Contracts.Persistence;
using NetShareHub.Application.Contracts.Services;
using NetShareHub.Application.Services;
using NetShareHub.Desktop.Logging;
using NetShareHub.Desktop.ViewModels;
using NetShareHub.Infrastructure.Repositories;
using NetShareHub.Infrastructure.Services;
using NetShareHub.Infrastructure.Services.Dhcp;
using NetShareHub.Infrastructure.Services.Dispatch;
using NetShareHub.Infrastructure.Services.Sharing;

namespace NetShareHub.Desktop.Extensions;

/// <summary>
/// Racine de composition de l'application (équivalent desktop des extensions <c>Add*</c> de la couche Api).
/// Toute la composition vit ici : les autres couches ne connaissent pas le conteneur.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLoggingConfig(this IServiceCollection services)
    {
        services.AddSingleton<LogStore>();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Information));
        services.AddSingleton<ILoggerProvider, LogStoreLoggerProvider>();
        return services;
    }

    /// <summary>Implémentations techniques : système, réseau, moteurs de partage, proxy, stockage.</summary>
    public static IServiceCollection AddMyInfrastructureGroup(this IServiceCollection services)
    {
        services.AddSingleton<IPlatformInfo, PlatformInfo>();
        services.AddSingleton<IPrivilegeService, PrivilegeService>();
        services.AddSingleton<ICommandRunner, CommandRunner>();
        services.AddSingleton<INetworkInventoryService, NetworkInventoryService>();
        services.AddSingleton<ISocketBinder, SocketBinder>();
        services.AddSingleton<IDhcpServerFactory, DhcpServerFactory>();
        services.AddSingleton<IDispatchService, DispatchProxyService>();
        services.AddSingleton<IAppSettingsRepository, JsonAppSettingsRepository>();

        services.AddSingleton<ISharingBackend>(provider =>
            OperatingSystem.IsWindows() ? ActivatorUtilities.CreateInstance<WindowsSharingBackend>(provider)
            : OperatingSystem.IsLinux() ? ActivatorUtilities.CreateInstance<LinuxSharingBackend>(provider)
            : new UnsupportedSharingBackend());

        return services;
    }

    /// <summary>Services applicatifs (logique pure).</summary>
    public static IServiceCollection AddMyApplicationGroup(this IServiceCollection services)
    {
        services.AddSingleton<SharingPlanner>();
        services.AddSingleton<IInternetSharingService, InternetSharingService>();
        return services;
    }

    public static IServiceCollection AddMyViewModelGroup(this IServiceCollection services)
    {
        services.AddSingleton<AdapterCatalog>();
        services.AddSingleton<MainViewModel>();
        return services;
    }
}
