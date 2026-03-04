using Avalonia;
using Avalonia.ReactiveUI;
using System;
using Velopack;

namespace WindsurfPortable.UI;

sealed class Program
{
    public static StartupOptions StartupOptions { get; private set; } = new(null, Start: false, Tray: false);

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build()
            .Run();

        StartupOptions = ArgParser.Parse(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .UseReactiveUI()
            .LogToTrace();
}
