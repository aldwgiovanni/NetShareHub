using Avalonia;
using NetShareHub.Infrastructure.Services;

namespace NetShareHub.Desktop;

internal static class Program
{
    // L'instance relancée en administrateur attend que l'ancienne, qui se ferme aussitôt, libère le verrou.
    private static readonly TimeSpan RelaunchWait = TimeSpan.FromSeconds(15);

    [STAThread]
    public static void Main(string[] args)
    {
        // Le verrou est tenu jusqu'à la fin de Main, sur ce thread (un mutex se libère depuis le thread qui le possède).
        using var instance = SingleInstanceLock.TryAcquire(
            args.Contains(SingleInstanceLock.RelaunchArgument) ? RelaunchWait : TimeSpan.Zero);
        App.AnotherInstanceRunning = instance is null;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
