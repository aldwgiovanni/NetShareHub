using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace NetShareHub.Desktop.Logging;

/// <summary>
/// Dernier filet de sécurité : une exception non prévue (commande d'interface, tâche oubliée) est écrite dans le Journal
/// au lieu de fermer l'application sans explication, en laissant éventuellement la configuration réseau modifiée.
/// </summary>
internal sealed class UnhandledExceptionLogger : IDisposable
{
    private readonly ILogger _logger;
    private readonly Dispatcher _dispatcher;

    private UnhandledExceptionLogger(ILogger logger, Dispatcher dispatcher)
    {
        _logger = logger;
        _dispatcher = dispatcher;
    }

    public static UnhandledExceptionLogger Install(ILogger logger, Dispatcher dispatcher)
    {
        var handlers = new UnhandledExceptionLogger(logger, dispatcher);
        dispatcher.UnhandledException += handlers.OnDispatcherException;
        TaskScheduler.UnobservedTaskException += handlers.OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += handlers.OnDomainException;
        return handlers;
    }

    public void Dispose()
    {
        _dispatcher.UnhandledException -= OnDispatcherException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainException;
    }

    // Thread d'interface : l'application peut continuer, l'erreur est consignée.
    private void OnDispatcherException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger.LogError(e.Exception, "Erreur inattendue : {Message}", e.Exception.Message);
        e.Handled = true;
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger.LogError(e.Exception, "Erreur inattendue dans une tâche de fond : {Message}", e.Exception.GetBaseException().Message);
        e.SetObserved();
    }

    // Ici le processus va s'arrêter quoi qu'on fasse : on laisse au moins une trace.
    private void OnDomainException(object? sender, UnhandledExceptionEventArgs e)
        => _logger.LogCritical(e.ExceptionObject as Exception, "Erreur fatale : {Message}", (e.ExceptionObject as Exception)?.Message);
}
