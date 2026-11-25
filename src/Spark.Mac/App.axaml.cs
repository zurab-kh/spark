using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Strela.App.Services;
using Strela.App.ViewModels;
using Strela.App.Views;
using Strela.Engine;
using Strela.Engine.Integration;
using Strela.Engine.Manager;
using Strela.Engine.Torrents;
using Strela.Engine.Video;

namespace Strela.App;

public partial class App : Application
{
    private const string MutexName = @"Local\Spark.SingleInstance";

    private readonly Stopwatch _startup = new();
    private readonly List<string> _pendingLinks = new();
    private IClassicDesktopStyleApplicationLifetime? _desktop;
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
    private NativeMenuItem? _traySpeed;
    private ClipboardPoller? _clipboard;
    private ISingleInstanceChannel? _channel;
    private bool _exiting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            // magnet-ссылки, .torrent из Finder и клик по значку в Dock приходят событиями, а не аргументами.
            if (this.TryGetFeature<IActivatableLifetime>() is { } activatable) activatable.Activated += OnActivated;
            desktop.ShutdownRequested += (_, _) => { _exiting = true; DisposeManager(); };   // Cmd+Q, выход из macOS
            _ = StartAsync(desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _startup.Start();
        var args = (desktop.Args ?? Array.Empty<string>()).Where(a => LinkParser.TryParseLink(a, out _)).ToList();
        try { _mutex = new Mutex(true, MutexName, out _ownsMutex); }
        catch { _ownsMutex = true; }   // именованные мьютексы недоступны — работаем как единственная копия
        _channel = new PipeSingleInstanceChannel();
        if (!_ownsMutex)
        {
            // Уже запущена другая копия: передаём ей ссылки (или просьбу показать окно) и выходим.
            await Task.Run(() => _channel.SendAsync(args)).WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { });
            desktop.Shutdown();
            return;
        }

        var data = KnownFolders.AppData();
        Directory.CreateDirectory(data);
        _log = new FileLog(Path.Combine(data, "logs"));
        _channel = new PipeSingleInstanceChannel(log: _log);
        Dispatcher.UIThread.UnhandledException += (_, ex) => _log.Error("Необработанное исключение в UI", ex.Exception);

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
        desktop.MainWindow = _window;
        _vm.GetClipboardText = async () => _window.Clipboard is { } c ? await c.TryGetTextAsync() : null;
        _vm.SetClipboardText = text => _window.Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
        _vm.ConfirmDelete = message => ConfirmDialog.AskAsync(_window, message, "Удалить");
        _window.Opened += OnFirstOpen;
        _window.Closing += OnWindowClosing;
        _window.Refreshed += UpdateTraySpeed;
        _window.SettingsRequested += ShowSettings;

        CreateTrayIcon();
        _manager.Completed += snap => Dispatcher.UIThread.Post(() => OnDownloadCompleted(snap));
        _clipboard = new ClipboardPoller(() => _window.Clipboard);
        _clipboard.TextCopied += OnClipboardText;

        _vm.AlreadyDownloaded += item =>
            ShowToast($"Уже скачано: {item.FileName}", () => Shell.RevealInFolder(item.FilePath), "Показать в Finder");

        _channel.MessagesReceived += messages => Dispatcher.UIThread.Post(() =>
        {
            _log.Info($"Получено ссылок от другого экземпляра или браузера: {messages.Count}");
            try { if (messages.Count > 0) _vm.AddRequests(messages); }
            catch (Exception ex) { _log.Error("Не удалось добавить ссылки от другого экземпляра", ex); }
            ShowWindow();
        });
        _channel.Start();
        RegisterWithMacOs();

        args.AddRange(_pendingLinks);
        _pendingLinks.Clear();
        if (args.Count > 0) _vm.AddLinks(string.Join("\n", args));
        _window.Show();
    }

    /// <summary>Браузеры, magnet/.torrent и вход в систему — в фоне и только для настоящего Spark.app.</summary>
    private void RegisterWithMacOs()
    {
        var exe = Environment.ProcessPath;
        var log = _log!;
        var settings = _settings;
        if (!OperatingSystem.IsMacOS() || MacIntegration.BundlePath(exe) is null) return;
        _ = Task.Run(() =>
        {
            MacIntegration.RegisterBrowsers(exe!, log);
            MacIntegration.ApplyTorrentHandler(settings.HandleTorrents, log);
            if (settings.StartWithWindows) MacIntegration.SetStartAtLogin(true, exe, log);   // путь к Spark.app мог смениться
        });
    }

    private void OnActivated(object? sender, ActivatedEventArgs e)
    {
        switch (e)
        {
            case ProtocolActivatedEventArgs p:
                AddFromSystem(p.Uri.OriginalString);
                break;
            case FileActivatedEventArgs f:
                foreach (var file in f.Files)
                    if (file.TryGetLocalPath() is { } path) AddFromSystem(path);
                break;
            default:
                if (e.Kind == ActivationKind.Reopen) ShowWindow();   // клик по значку Spark в Dock
                break;
        }
    }

    private void AddFromSystem(string link)
    {
        if (_vm is null)
        {
            _pendingLinks.Add(link);   // окно ещё не создано: добавим после загрузки списка
            return;
        }
        _log?.Info("Ссылка от macOS (magnet или .torrent)");
        _vm.AddLinks(link);
        ShowWindow();
    }

    private void CreateTrayIcon()
    {
        _traySpeed = new NativeMenuItem("Скорость: 0 Б/с") { IsEnabled = false };
        var show = new NativeMenuItem("Показать Spark");
        show.Click += (_, _) => ShowWindow();
        var pause = new NativeMenuItem("Приостановить все");
        pause.Click += (_, _) => _manager?.PauseAll();
        var resume = new NativeMenuItem("Продолжить все");
        resume.Click += (_, _) => _manager?.ResumeAll();
        var settings = new NativeMenuItem("Настройки…");
        settings.Click += (_, _) => ShowSettings();
        var quit = new NativeMenuItem("Выйти из Spark");
        quit.Click += (_, _) => _ = ExitAsync();

        _tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Spark/Assets/tray-template.png"))),
            ToolTipText = "Spark",
            Menu = new NativeMenu
            {
                _traySpeed, new NativeMenuItemSeparator(), show, pause, resume,
                new NativeMenuItemSeparator(), settings, new NativeMenuItemSeparator(), quit,
            },
        };
        MacOSProperties.SetIsTemplateIcon(_tray, true);   // macOS сама красит значок под светлую/тёмную строку меню
        TrayIcon.SetIcons(this, new TrayIcons { _tray });
    }

    private void UpdateTraySpeed()
    {
        if (_vm is null) return;
        var text = "Скорость: " + Format.Speed(_vm.TotalSpeed);
        if (_traySpeed is not null && _traySpeed.Header != text) _traySpeed.Header = text;
        var tip = _vm.TotalSpeed > 0 ? "Spark · " + Format.Speed(_vm.TotalSpeed) : "Spark";
        if (_tray is not null && _tray.ToolTipText != tip) _tray.ToolTipText = tip;
    }

    private void OnFirstOpen(object? sender, EventArgs e)
    {
        _window!.Opened -= OnFirstOpen;
        _log!.Info($"startup {_startup.ElapsedMilliseconds} ms");
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_exiting || e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown) return;
        // Красная кнопка и Cmd+W прячут окно; Spark продолжает качать. Выход — Cmd+Q или меню в строке меню.
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
        Cleanup();
        _desktop?.Shutdown();
    }

    private void DisposeManager()
    {
        if (_manager is not null && !_managerDisposed)
        {
            _managerDisposed = true;
            try { _manager.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)); }
            catch (Exception ex) { _log?.Error("Ошибка при остановке менеджера", ex); }
        }
        Cleanup();
    }

    private void Cleanup()
    {
        _clipboard?.Dispose();
        _channel?.Dispose();
        _tray?.Dispose();
        _log?.Dispose();
        if (_ownsMutex)
        {
            try { _mutex?.ReleaseMutex(); }
            catch { /* выходим */ }
        }
        _mutex?.Dispose();
        _ownsMutex = false;
    }

    private void OnClipboardText(string text)
    {
        if (!_settings.WatchClipboard || _vm is null) return;
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (line is null) return;
        if (TorrentLinks.IsMagnet(line))
        {
            if (_vm.Contains(line)) return;
            var title = TorrentLinks.MagnetName(line);
            _log?.Info("Буфер обмена: magnet-ссылка");
            ShowToast(title is null ? "Скачать торрент?" : $"Скачать торрент {title}?", () => _vm.AddLinks(line), "Скачать");
            return;
        }
        if (VideoLinks.TryParse(line, out var video))
        {
            if (_vm.Contains(video.AbsoluteUri)) return;
            _log?.Info($"Буфер обмена: видео {video.AbsoluteUri}");
            ShowToast("Скачать видео с YouTube?", () => _vm.AddLinks(video.AbsoluteUri), "Скачать");
            return;
        }
        if (!FileUrlDetector.TryDetect(line, out var url)) return;
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

    private void Settings_Click(object? sender, EventArgs e) => ShowSettings();

    private void ShowSettings()
    {
        if (_window is null) return;
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }
        ShowWindow();
        var w = new SettingsWindow(_settings, ApplySettings);
        w.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow = w;
        w.Show(_window);
    }

    private void ApplySettings(AppSettings s)
    {
        var torrentsChanged = s.HandleTorrents != _settings.HandleTorrents;
        _settings = s;
        try { _settingsStore!.Save(s); }
        catch (Exception ex) { _log!.Error("Не удалось сохранить настройки", ex); }
        _manager!.ApplyOptions(s.ToEngineOptions(_stateFolder));
        var exe = Environment.ProcessPath;
        var log = _log!;
        if (!OperatingSystem.IsMacOS()) return;
        _ = Task.Run(() =>
        {
            if (torrentsChanged) MacIntegration.ApplyTorrentHandler(s.HandleTorrents, log);
            MacIntegration.SetStartAtLogin(s.StartWithWindows, exe, log);
        });
    }
}
