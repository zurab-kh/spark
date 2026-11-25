using Avalonia;
using Strela.App.Services;

namespace Strela.App;

public static class Program
{
    /// <summary>
    /// Режим native host (браузер запускает «Spark chrome-extension://…») стартует без Avalonia:
    /// браузер ждёт ответа на каждое сообщение.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (NativeMessagingHost.IsNativeHostLaunch(args)) return NativeMessagingHost.RunFromProcess();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new MacOSPlatformOptions { ShowInDock = true })
            .LogToTrace();
}
