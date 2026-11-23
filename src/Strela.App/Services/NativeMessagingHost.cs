using System.Diagnostics;
using System.Text.Json;
using Strela.Engine;
using Strela.Engine.Integration;

namespace Strela.App.Services;

/// <summary>
/// Режим «Spark.exe --native-host»: принимает сообщения расширения браузера, передаёт их работающему Spark
/// через IpcClient (при необходимости запускает его) и отвечает {"ok":true|false}. Без окон и менеджера загрузок.
/// </summary>
public sealed class NativeMessagingHost
{
    public const string Flag = "--native-host";

    /// <summary>
    /// Версия протокола расширения. Сообщения без неё приходят от устаревшего расширения, которое пересылало всю
    /// историю загрузок браузера, — их отклоняем, пока пользователь не обновит расширение.
    /// </summary>
    public const int ProtocolVersion = 2;

    /// <summary>Загрузка, начавшаяся раньше этого, — не новая (история браузера), её не берём.</summary>
    public static readonly TimeSpan MaxDownloadAge = TimeSpan.FromSeconds(60);

    private readonly Func<bool> _isEnabled;
    private readonly Func<DateTimeOffset> _now;
    private readonly IStrelaLog _log;
    private readonly string _pipeName;
    private readonly Func<bool> _launchSpark;
    private readonly TimeSpan _startWait;
    private readonly TimeSpan _attemptTimeout;
    private bool _launched;

    public NativeMessagingHost(Func<bool> isEnabled, IStrelaLog? log = null, string? pipeName = null,
        Func<bool>? launchSpark = null, TimeSpan? startWait = null, TimeSpan? attemptTimeout = null,
        Func<DateTimeOffset>? now = null)
    {
        _isEnabled = isEnabled;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _log = log ?? NullLog.Instance;
        _pipeName = pipeName ?? IpcProtocol.PipeName;
        _launchSpark = launchSpark ?? LaunchSparkProcess;
        _startWait = startWait ?? TimeSpan.FromSeconds(5);
        _attemptTimeout = attemptTimeout ?? TimeSpan.FromMilliseconds(400);
    }

    /// <summary>Chrome и Edge запускают host как «exe chrome-extension://id/ --parent-window=N»; флаг нужен для ручного запуска.</summary>
    public static bool IsNativeHostLaunch(IReadOnlyList<string> args) =>
        args.Any(a => string.Equals(a, Flag, StringComparison.OrdinalIgnoreCase)
                      || a.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase));

    /// <summary>Читает кадры из input и пишет ответы в output до конца потока.</summary>
    public async Task RunAsync(Stream input, Stream output, CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            string? json;
            try
            {
                json = await NativeMessagingFrames.ReadAsync(input, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException or IOException)
            {
                _log.Warn($"Native host: input stream broken: {ex.Message}");
                return;
            }
            if (json is null) return;

            var reply = await HandleAsync(json).ConfigureAwait(false);
            try
            {
                await NativeMessagingFrames.WriteAsync(output, reply, ct).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                _log.Warn($"Native host: cannot reply: {ex.Message}");
                return;
            }
        }
    }

    /// <summary>JSON-сообщение расширения → JSON-ответ. Не бросает.</summary>
    public async Task<string> HandleAsync(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Fail("bad message");

            if (root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "ping")
                return JsonSerializer.Serialize(new { ok = true, enabled = SafeIsEnabled(), v = ProtocolVersion });

            if (!SafeIsEnabled()) return Fail("disabled");

            if (!root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != ProtocolVersion)
            {
                _log.Warn("Native host: rejected message from an outdated extension");
                return Fail("outdated extension: reload Spark in chrome://extensions");
            }
            if (!IsNewDownload(root))
            {
                _log.Warn($"Native host: rejected old download {Str(root, "url")}");
                return Fail("not new: download started too long ago");
            }

            var message = ParseMessage(root);
            if (message?.ToRequest() is null) return Fail("bad url");

            if (!await SendWithLaunchAsync(message).ConfigureAwait(false)) return Fail("Spark is not running");
            _log.Info($"Native host: forwarded {message.Url}");
            return JsonSerializer.Serialize(new { ok = true });
        }
        catch (JsonException)
        {
            return Fail("bad json");
        }
        catch (Exception ex)
        {
            _log.Error("Native host: message failed", ex);
            return Fail(ex.Message);
        }
    }

    private bool IsNewDownload(JsonElement root)
    {
        if (Str(root, "startTime") is not { } s ||
            !DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var started))
            return false;
        var age = _now() - started;
        return age <= MaxDownloadAge && age >= -MaxDownloadAge;
    }

    private bool SafeIsEnabled()
    {
        try { return _isEnabled(); }
        catch { return true; }
    }

    private static IpcMessage? ParseMessage(JsonElement root)
    {
        var url = Str(root, "url");
        if (url is null) return null;

        Dictionary<string, string>? headers = null;
        if (root.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in h.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } v)
                    (headers ??= new())[p.Name] = v;
        }
        return new IpcMessage(url, Str(root, "referer"), headers, Str(root, "cookies"), Str(root, "fileName"));
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    private async Task<bool> SendWithLaunchAsync(IpcMessage message)
    {
        var batch = new[] { message };
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            if (await IpcClient.SendAsync(_pipeName, batch, _attemptTimeout).ConfigureAwait(false)) return true;
            if (deadline.Elapsed >= _startWait) return false;
            if (!_launched)
            {
                _launched = true;
                _log.Info("Native host: Spark is not running, starting it");
                bool started;
                try { started = _launchSpark(); }
                catch (Exception ex) { _log.Error("Native host: cannot start Spark", ex); started = false; }
                if (!started) return false;
            }
            await Task.Delay(150).ConfigureAwait(false);
        }
    }

    private static bool LaunchSparkProcess()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;
        // ShellExecute: Spark не наследует каналы stdin/stdout браузера и живёт дольше host'а.
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" })?.Dispose();
        return true;
    }

    private static string Fail(string error) => JsonSerializer.Serialize(new { ok = false, error });

    /// <summary>Точка входа Spark.exe в режиме native host: stdin → stdout до закрытия потока браузером.</summary>
    public static int RunFromProcess()
    {
        using var log = new FileLog(Path.Combine(KnownFolders.AppData(), "logs"));
        var store = new SettingsStore(Path.Combine(KnownFolders.AppData(), "settings.json"));
        var host = new NativeMessagingHost(() => store.Load().InterceptBrowserDownloads, log);
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        host.RunAsync(input, output).GetAwaiter().GetResult();
        return 0;
    }
}
