using Avalonia;

namespace SporeholmLauncher.App;

internal static class Program
{
    // Avalonia entry point. The headless CLI lives in the separate
    // sporeholm-launcher-cli executable (both drive SporeholmLauncher.Core).
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
