using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetShareHub.Desktop.Extensions;
using NetShareHub.Desktop.Logging;
using NetShareHub.Desktop.ViewModels;
using NetShareHub.Desktop.Views;

namespace NetShareHub.Desktop;

// Nom qualifié : l'espace de noms NetShareHub.Application (couche applicative) masquerait Avalonia.Application.
public partial class App : Avalonia.Application
{
    private ServiceProvider? _services;
    private bool _shuttingDown;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = new ServiceCollection()
                .AddLoggingConfig()
                .AddMyInfrastructureGroup()
                .AddMyApplicationGroup()
                .AddMyViewModelGroup()
                .BuildServiceProvider();

            UnhandledExceptionLogger.Install(
                _services.GetRequiredService<ILoggerFactory>().CreateLogger("NetShareHub"),
                Dispatcher.UIThread);

            var main = _services.GetRequiredService<MainViewModel>();
            main.RequestExit = () =>
            {
                _shuttingDown = true; // relancement en administrateur : l'ancienne instance n'a rien à nettoyer
                desktop.Shutdown();
            };

            desktop.MainWindow = new MainWindow { DataContext = main };

            // Fermer l'application arrête le partage et restaure la configuration réseau avant de quitter.
            desktop.ShutdownRequested += (_, e) =>
            {
                if (_shuttingDown)
                {
                    return;
                }

                e.Cancel = true;
                _ = ShutdownAsync(main, desktop);
            };

            _ = main.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task ShutdownAsync(MainViewModel main, IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            await main.ShutdownAsync();
        }
        finally
        {
            _shuttingDown = true;
            desktop.Shutdown();
        }
    }
}
