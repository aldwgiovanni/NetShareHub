using Avalonia;
using Avalonia.Headless;
using NetShareHub.Desktop;

namespace NetShareHub.Tests.UI;

// Les tests d'interface tournent dans un Avalonia « headless » : vrai moteur de rendu Skia, aucune fenêtre à l'écran.
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .WithInterFont()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>Une seule session Avalonia pour tous les tests : l'application n'est initialisée qu'une fois, sur son propre thread d'interface.</summary>
internal static class UiSession
{
    private static readonly Lazy<HeadlessUnitTestSession> Session = new(() => HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder)));

    public static Task RunAsync(Func<Task> body) => Session.Value.Dispatch(body, CancellationToken.None);
}
