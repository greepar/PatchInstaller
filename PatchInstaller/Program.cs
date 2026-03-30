using System;
using Avalonia;
using PatchInstaller.Services;

namespace PatchInstaller;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (ElevationHelper.TryHandleElevatedCopy(args))
        {
            return 0;
        }

        BuildAvaloniaApp()
            .With(new Win32PlatformOptions
            {
                RenderingMode = [Win32RenderingMode.AngleEgl,Win32RenderingMode.Software],
                DpiAwareness = Win32DpiAwareness.PerMonitorDpiAware
            })
            .StartWithClassicDesktopLifetime(args);

        return 0;
    }

    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
