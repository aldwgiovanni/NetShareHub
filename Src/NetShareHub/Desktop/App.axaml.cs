using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using NetShareHub.Desktop.Extensions;
using NetShareHub.Desktop.ViewModels;
using NetShareHub.Desktop.Views;

namespace NetShareHub.Desktop;

// Nom qualifié : l'espace de noms NetShareHub.Application (couche applicative) masquerait Avalonia.Application.
public partial class App : Avalonia.Application
{
    private ServiceProvider? _services;
    private bool _shuttingDown;

    /// <summary>Renseigné par <see cref="Program"/> : une autre instance tient déjà le verrou d'instance unique.</summary>
    public static bool AnotherInstanceRunning { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime alreadyRunning && AnotherInstanceRunning)
        {
            // Aucun service n'est créé : surtout pas le moteur de partage, qui rejouerait le journal de l'autre instance.
            alreadyRunning.MainWindow = AlreadyRunningWindow();
        }
        else if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = new ServiceCollection()
                .AddLoggingConfig()
                .AddMyInfrastructureGroup()
                .AddMyApplicationGroup()
                .AddMyViewModelGroup()
                .BuildServiceProvider();

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

    private static Window AlreadyRunningWindow()
    {
        var window = new Window
        {
            Title = "NetShare Hub",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        var close = new Button { Content = "Fermer", HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => window.Close();
        window.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = "NetShare Hub est déjà ouvert sur cet ordinateur (peut-être dans une autre session ou en administrateur). "
                           + "Utilisez la fenêtre existante : deux instances se disputeraient la configuration réseau.",
                },
                close,
            },
        };
        return window;
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
