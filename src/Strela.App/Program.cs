using Strela.App.Services;

namespace Strela.App;

public static class Program
{
    /// <summary>
    /// Свой Main (App.xaml собирается как Page), чтобы режим native host стартовал мгновенно,
    /// не поднимая WPF: браузер ждёт ответа на каждое сообщение.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (NativeMessagingHost.IsNativeHostLaunch(args)) return NativeMessagingHost.RunFromProcess();

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
