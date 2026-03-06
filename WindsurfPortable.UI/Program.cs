using Avalonia;
using Avalonia.ReactiveUI;
using System;
using System.Diagnostics;
using System.IO;
using Velopack;

namespace WindsurfPortable.UI;

sealed class Program
{
    public static StartupOptions StartupOptions { get; private set; } = new(null, Autostart: false, Tray: false, ForwardArgs: Array.Empty<string>(), IsForwardedInvocation: false);
    public static string[] RestartArgs { get; private set; } = Array.Empty<string>();
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build()
            .Run();

        RestartArgs = args;

        if (TryHandleApplyWindsurfUpdateMode(args))
            return;

        StartupOptions = ArgParser.Parse(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static bool TryHandleApplyWindsurfUpdateMode(string[] args)
    {
        var idx = Array.FindIndex(args, a => string.Equals(a, "--apply-windsurf-update", StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
            return false;

        if (idx + 1 >= args.Length)
            return true;

        var extractPath = args[idx + 1];

        var dashDash = Array.FindIndex(args, idx + 2, a => a == "--");
        string[] restartArgs = dashDash >= 0
            ? args[(dashDash + 1)..]
            : Array.Empty<string>();

        try
        {
            var dataRootDir = PortableDataPaths.GetDataRoot(AppContext.BaseDirectory);
            WindsurfPortable.UpdateApplier.ApplyExtractedUpdateToBaseDirectory(
                extractPath,
                Path.Combine(dataRootDir, "windsurf"));

            var currentExe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(currentExe) && File.Exists(currentExe))
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = currentExe,
                    UseShellExecute = false,
                };

                foreach (var a in restartArgs)
                    psi.ArgumentList.Add(a);

                System.Diagnostics.Process.Start(psi);
            }
        }
        catch
        {
        }

        return true;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .UseReactiveUI()
            .LogToTrace();
}
