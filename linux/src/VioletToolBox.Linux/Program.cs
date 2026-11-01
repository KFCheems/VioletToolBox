using Avalonia;
using System;

namespace VioletToolBox.Linux;

internal static class Program
{
    /// <summary>Initial page name passed via `--page &lt;设备|刷入|分区|解包|edl|下载&gt;`.</summary>
    public static string? InitialPage { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "--page" or "-p")
                InitialPage = args[i + 1];
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .With(new X11PlatformOptions { EnableMultiTouch = false })
        .LogToTrace();
}
