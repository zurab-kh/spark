using System.Windows;
using Strela.App.Services;
using Strela.App.ViewModels;
using Strela.Engine;
using Strela.Engine.Manager;

namespace Strela.App;

public partial class App : Application
{
    private IDownloadManager? _manager;

    public App()
    {
#pragma warning disable WPF0001 // Fluent-тема и ThemeMode пока помечены как экспериментальные
        ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var data = KnownFolders.AppData();
        var options = new EngineOptions
        {
            DownloadFolder = KnownFolders.Downloads(),
            StateFolder = Path.Combine(data, "downloads"),
        };
        _manager = ManagerFactory.Create(options, NullLog.Instance);
        await _manager.InitializeAsync();
        var window = new MainWindow(new MainViewModel(_manager));
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _manager?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        base.OnExit(e);
    }
}
