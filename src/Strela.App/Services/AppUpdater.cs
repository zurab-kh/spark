using System.Reflection;
using Strela.Engine;
using Velopack;
using Velopack.Sources;

namespace Strela.App.Services;

/// <summary>
/// Обновления по воздуху (Velopack). Новые версии лежат в выпусках публичного репозитория <see cref="ReleasesUrl"/>,
/// их выкладывает GitHub Actions по тегу v*. Проверка через минуту после запуска и затем раз в 6 часов; новая
/// версия скачивается тихо, Spark предлагает перезапуститься, а если пользователь не стал — ставит её при выходе.
/// Работает только в установленном Spark (Setup или портативная сборка из выпуска), не в сборке из исходников.
/// </summary>
public sealed class AppUpdater : IDisposable
{
    public const string ReleasesUrl = "https://github.com/zurab-kh/spark-releases";
    /// <summary>Для проверки: папка с выпусками vpk pack или другой адрес на GitHub вместо <see cref="ReleasesUrl"/>.</summary>
    public const string SourceVariable = "SPARK_UPDATE_SOURCE";

    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);

    private readonly IStrelaLog _log;
    private readonly UpdateManager? _manager;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Timer? _timer;
    private VelopackAsset? _ready;
    private bool _scheduled;

    public AppUpdater(IStrelaLog log)
    {
        _log = log;
        try
        {
            var source = Environment.GetEnvironmentVariable(SourceVariable) is { Length: > 0 } custom ? custom : ReleasesUrl;
            IUpdateSource updates = source.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? new GithubSource(source, null, false)
                : new SimpleFileSource(new DirectoryInfo(source));
            var manager = new UpdateManager(updates);
            if (manager.IsInstalled)
                _manager = manager;
            else
                _log.Info("updates: not an installed Spark, updates are off");
        }
        catch (Exception ex)
        {
            _log.Warn($"updates: cannot start: {ex.Message}");
        }
    }

    /// <summary>Новая версия скачана (из фонового потока; номер версии).</summary>
    public event Action<string>? UpdateReady;

    /// <summary>Установленный Spark, который умеет обновляться.</summary>
    public bool IsEnabled => _manager is not null;

    public string CurrentVersion => _manager?.CurrentVersion?.ToString() ?? AssemblyVersion();

    /// <summary>Скачанная и ещё не установленная версия.</summary>
    public string? ReadyVersion => _ready?.Version?.ToString();

    /// <summary>Velopack: самый первый вызов в Main (установка, удаление, докачанная версия при запуске).</summary>
    public static void RunHooks() => VelopackApp.Build().Run();

    public void Start()
    {
        if (_manager is not null)
            _timer = new Timer(_ => _ = CheckAsync(), null, FirstCheck, CheckEvery);
    }

    /// <summary>Проверить и скачать новую версию. Возвращает текст для пользователя.</summary>
    public async Task<string> CheckAsync()
    {
        if (_manager is null)
            return "Обновления работают в Spark, установленном из выпуска на GitHub";
        if (!await _gate.WaitAsync(0).ConfigureAwait(false))
            return "Проверка уже идёт…";
        try
        {
            if (_ready is not null)
                return ReadyText();
            var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info is null)
                return $"Установлена последняя версия ({CurrentVersion})";

            _log.Info($"updates: downloading {info.TargetFullRelease.Version}");
            await _manager.DownloadUpdatesAsync(info).ConfigureAwait(false);
            _ready = info.TargetFullRelease;
            _log.Info($"updates: {_ready.Version} ready");
            UpdateReady?.Invoke(_ready.Version.ToString());
            return ReadyText();
        }
        catch (Exception ex)
        {
            _log.Warn($"updates: check failed: {ex.Message}");
            return $"Не удалось проверить обновления: {ex.Message}";
        }
        finally
        {
            _gate.Release();
        }
    }

    private string ReadyText() => $"Версия {ReadyVersion} загружена: перезапустите Spark";

    /// <summary>
    /// Поставить скачанную версию, как только Spark завершится (и запустить его снова, если <paramref name="restart"/>).
    /// После вызова приложение должно завершиться обычным путём — загрузки успеют сохраниться.
    /// </summary>
    public bool ApplyAfterExit(bool restart)
    {
        if (_manager is null || _ready is null || _scheduled)
            return false;
        try
        {
            _manager.WaitExitThenApplyUpdates(_ready, silent: true, restart: restart);
            _scheduled = true;
            _log.Info($"updates: {_ready.Version} will be installed after exit{(restart ? ", then restart" : "")}");
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn($"updates: cannot schedule {_ready.Version}: {ex.Message}");
            return false;
        }
    }

    public void Dispose() => _timer?.Dispose();

    private static string AssemblyVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        var plus = version.IndexOf('+');
        return plus > 0 ? version[..plus] : version;
    }
}
