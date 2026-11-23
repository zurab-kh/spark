using System.Diagnostics;
using System.Windows;
using Strela.App.Services;
using Strela.App.ViewModels;
using Strela.App.Views;
using Strela.Engine;
using Strela.Engine.Integration;
using Strela.Engine.Manager;

namespace Strela.App;

public partial class App : Application
{
    private const string MutexName = @"Local\Strela.SingleInstance";

    private readonly Stopwatch _startup = new();
    private Mutex? _mutex;
    private bool _ownsMutex;
    private FileLog? _log;
    private SettingsStore? _settingsStore;
    private AppSettings _settings = new();
    private string _stateFolder = "";
    private IDownloadManager? _manager;
    private bool _managerDisposed;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private SettingsWindow? _settingsWindow;
    private ToastWindow? _toast;
    private TrayIcon? _tray;
    private ClipboardWatcher? _clipboard;
    private UiLagMonitor? _lag;
    private ISingleInstanceChannel? _channel;
    private bool _exiting;

    public App()
    {
#pragma warning disable WPF0001 // Fluent-тема и ThemeMode пока помечены как экспериментальные
        ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        _startup.Start();
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var args = e.Args.Where(a => LinkParser.TryParseHttp(a, out _)).ToList();
        _mutex = new Mutex(true, MutexName, out _ownsMutex);
        _channel = new PipeSingleInstanceChannel();
        if (!_ownsMutex)
        {
            // Уже запущена другая копия: передаём ей ссылки (или просьбу показать окно) и выходим.
            Task.Run(() => _channel.SendAsync(args)).Wait(TimeSpan.FromSeconds(5));
            Shutdown();
            return;
        }

        var data = KnownFolders.AppData();
        _log = new FileLog(Path.Combine(data, "logs"));
        _channel = new PipeSingleInstanceChannel(log: _log);
        DispatcherUnhandledException += (_, ex) => _log.Error("Необработанное исключение в UI", ex.Exception);

        _settingsStore = new SettingsStore(Path.Combine(data, "settings.json"));
        _settings = _settingsStore.Load();
        _stateFolder = Path.Combine(data, "downloads");
        _manager = ManagerFactory.Create(_settings.ToEngineOptions(_stateFolder), _log);
        try
        {
            await _manager.InitializeAsync();
        }
        catch (Exception ex)
        {
            _log.Error("Не удалось загрузить сохранённые загрузки", ex);
        }

        _vm = new MainViewModel(_manager);
        _window = new MainWindow(_vm);
        MainWindow = _window;
        _window.ContentRendered += OnFirstRender;
        _window.Closing += OnWindowClosing;
        _window.Refreshed += () => _tray?.SetSpeed(Format.Speed(_vm.TotalSpeed));
        _window.SettingsRequested += ShowSettings;

        _tray = new TrayIcon(ShowWindow, () => _manager.PauseAll(), () => _manager.ResumeAll(), () => _ = ExitAsync());
        _manager.Completed += snap => Dispatcher.InvokeAsync(() => OnDownloadCompleted(snap));
        _clipboard = new ClipboardWatcher();
        _clipboard.TextCopied += OnClipboardText;
        _lag = new UiLagMonitor(Dispatcher, _log);
        SessionEnding += (_, _) => { _exiting = true; DisposeManager(); };   // выход из Windows не должен упираться в трей

        _vm.AlreadyDownloaded += item =>
            ShowToast($"Уже скачано: {item.FileName}", () => Shell.RevealInFolder(item.FilePath), "Показать в папке");

        _channel.MessagesReceived += messages => Dispatcher.InvokeAsync(() =>
        {
            _log.Info($"Получено ссылок от другого экземпляра или браузера: {messages.Count}");
            try { if (messages.Count > 0) _vm.AddRequests(messages); }
            catch (Exception ex) { _log.Error("Не удалось добавить ссылки от другого экземпляра", ex); }
            ShowWindow();
        });
        _channel.Start();
        RegisterBrowserHost();

        if (args.Count > 0) _vm.AddLinks(string.Join("\n", args));
        _window.Show();
    }

    /// <summary>Манифест native host и ключи реестра для Chrome и Edge; в фоне, чтобы не тормозить запуск.</summary>
    private void RegisterBrowserHost()
    {
        var exe = Environment.ProcessPath;
        var log = _log!;
        if (string.IsNullOrEmpty(exe) || !string.Equals(Path.GetFileName(exe), "Spark.exe", StringComparison.OrdinalIgnoreCase)) return;
        var fallback = KnownFolders.AppData();
        _ = Task.Run(() => NativeHostRegistration.Register(exe, new CurrentUserRegistryWriter(), log, fallback));
    }

    private void OnFirstRender(object? sender, EventArgs e)
    {
        _window!.ContentRendered -= OnFirstRender;
        _log!.Info($"startup {_startup.ElapsedMilliseconds} ms");
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting) return;
        // Крестик всегда прячет окно в трей; закрыть Spark можно только через трей → «Выход».
        e.Cancel = true;
        _window!.Hide();
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        try
        {
            if (_manager is not null && !_managerDisposed)
            {
                _managerDisposed = true;
                await _manager.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            _log?.Error("Ошибка при остановке менеджера", ex);
        }
        Shutdown();
    }

    private void DisposeManager()
    {
        if (_manager is null || _managerDisposed) return;
        _managerDisposed = true;
        try { _manager.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) { _log?.Error("Ошибка при остановке менеджера", ex); }
    }

    private void OnClipboardText(string text)
    {
        if (!_settings.WatchClipboard || _vm is null) return;
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (line is null || !FileUrlDetector.TryDetect(line, out var url)) return;
        if (_vm.Contains(url.AbsoluteUri) || _vm.Contains(url.OriginalString)) return;
        var name = Uri.UnescapeDataString(url.Segments.LastOrDefault()?.Trim('/') ?? "");
        if (name.Length == 0) name = url.Host;
        _log?.Info($"Буфер обмена: ссылка {url.AbsoluteUri}");
        ShowToast($"Скачать {name}?", () => _vm.AddLinks(url.AbsoluteUri), "Скачать");
    }

    private void OnDownloadCompleted(DownloadSnapshot snap)
    {
        if (!_settings.Notifications) return;
        ShowToast($"Готово: {snap.FileName}", () => Shell.RevealInFolder(snap.FilePath));
    }

    private void ShowToast(string message, Action onClick, string? buttonText = null)
    {
        _toast?.Close();
        var toast = new ToastWindow(message, onClick, buttonText);
        toast.Closed += (_, _) => { if (ReferenceEquals(_toast, toast)) _toast = null; };
        _toast = toast;
        toast.Show();
    }

    private void ShowSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }
        var w = new SettingsWindow(_settings, ApplySettings) { Owner = _window };
        w.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow = w;
        w.Show();
    }

    private void ApplySettings(AppSettings s)
    {
        _settings = s;
        try { _settingsStore!.Save(s); }
        catch (Exception ex) { _log!.Error("Не удалось сохранить настройки", ex); }
        _manager!.ApplyOptions(s.ToEngineOptions(_stateFolder));
        try { Autostart.Set(s.StartWithWindows, Environment.ProcessPath ?? ""); }
        catch (Exception ex) { _log!.Error("Не удалось изменить автозапуск", ex); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DisposeManager();
        _lag?.Dispose();
        _clipboard?.Dispose();
        _channel?.Dispose();
        _tray?.Dispose();
        _log?.Dispose();
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
