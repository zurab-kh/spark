using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using Strela.Engine;

namespace Strela.App.Services;

/// <summary>Связь Spark с macOS: браузеры, вход в систему, magnet и .torrent. Ничего не бросает.</summary>
public static class MacIntegration
{
    public const string BundleId = "com.spark.downloader";
    public const string HostName = "com.spark.downloader";

    /// <summary>ID расширения из browser-extension/manifest.json (поле key) — тот же, что в Windows-версии.</summary>
    public const string ExtensionId = "meffaihaokjhicodaannmgedjflgbcgd";

    /// <summary>Профили браузеров на Chromium (относительно ~/Library/Application Support).</summary>
    public static readonly string[] BrowserFolders =
    {
        "Google/Chrome", "Google/Chrome Beta", "Microsoft Edge", "BraveSoftware/Brave-Browser",
        "Chromium", "Vivaldi", "Arc/User Data", "Yandex/YandexBrowser",
    };

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string AppSupport => Path.Combine(Home, "Library", "Application Support");

    /// <summary>…/Spark.app/Contents/MacOS/Spark → …/Spark.app; null вне .app (запуск из папки сборки).</summary>
    public static string? BundlePath(string? exe) => exe is null ? null : NativeMessagingHost.MacBundlePath(exe);

    /// <summary>Папка расширения, которую нужно указать в chrome://extensions → «Загрузить распакованное».</summary>
    public static string ExtensionFolder => Path.Combine(KnownFolders.AppData(), "browser-extension");

    public static string BuildHostManifest(string exePath) =>
        JsonSerializer.Serialize(new
        {
            name = HostName,
            description = "Spark download manager",
            path = exePath,
            type = "stdio",
            allowed_origins = new[] { $"chrome-extension://{ExtensionId}/" },
        }, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>
    /// Манифест native host для каждого установленного браузера на Chromium и копия расширения в
    /// ~/Library/Application Support/Spark/browser-extension (внутрь .app диалог Chrome не заходит).
    /// </summary>
    public static void RegisterBrowsers(string exePath, IStrelaLog log, string? appSupport = null, string? extensionFolder = null)
    {
        var root = appSupport ?? AppSupport;
        var manifest = BuildHostManifest(exePath);
        foreach (var browser in BrowserFolders)
        {
            var profile = Path.Combine(root, browser);
            if (!Directory.Exists(profile)) continue;
            try
            {
                var dir = Path.Combine(profile, "NativeMessagingHosts");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, HostName + ".json");
                if (!File.Exists(path) || File.ReadAllText(path) != manifest) File.WriteAllText(path, manifest);
            }
            catch (Exception ex)
            {
                log.Warn($"Native host manifest for {browser} not written: {ex.Message}");
            }
        }
        CopyExtension(exePath, extensionFolder ?? ExtensionFolder, log);
    }

    private static void CopyExtension(string exePath, string targetFolder, IStrelaLog log)
    {
        var exeDir = Path.GetDirectoryName(exePath) ?? "";
        var source = new[]
        {
            Path.Combine(exeDir, "..", "Resources", "browser-extension"),
            Path.Combine(exeDir, "browser-extension"),
        }.Select(Path.GetFullPath).FirstOrDefault(Directory.Exists);
        if (source is null) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(targetFolder, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            log.Warn($"Browser extension not copied: {ex.Message}");
        }
    }

    // --- Вход в систему: LaunchAgent в ~/Library/LaunchAgents ---

    public static string LaunchAgentPath => Path.Combine(Home, "Library", "LaunchAgents", BundleId + ".plist");

    public static string BuildLaunchAgent(string bundlePath) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>Label</key>
            <string>{BundleId}</string>
            <key>ProgramArguments</key>
            <array>
                <string>/usr/bin/open</string>
                <string>-a</string>
                <string>{SecurityElement.Escape(bundlePath)}</string>
            </array>
            <key>RunAtLoad</key>
            <true/>
        </dict>
        </plist>
        """;

    public static void SetStartAtLogin(bool enabled, string? exePath, IStrelaLog log)
    {
        try
        {
            if (!enabled)
            {
                if (File.Exists(LaunchAgentPath)) File.Delete(LaunchAgentPath);
                return;
            }
            if (BundlePath(exePath) is not { } bundle) return;
            Directory.CreateDirectory(Path.GetDirectoryName(LaunchAgentPath)!);
            File.WriteAllText(LaunchAgentPath, BuildLaunchAgent(bundle));
        }
        catch (Exception ex)
        {
            log.Error("Не удалось изменить запуск при входе", ex);
        }
    }

    // --- magnet: и .torrent: Spark — программа по умолчанию (LaunchServices) ---

    private const string TorrentType = "org.bittorrent.torrent";
    private const uint Utf8 = 0x08000100;
    private const uint AllRoles = 0xFFFFFFFF;
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string CoreServices = "/System/Library/Frameworks/CoreServices.framework/CoreServices";

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, [MarshalAs(UnmanagedType.LPUTF8Str)] string s, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFStringGetLength(IntPtr s);

    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool CFStringGetCString(IntPtr s, byte[] buffer, IntPtr size, uint encoding);

    [DllImport(CoreServices)]
    private static extern int LSSetDefaultHandlerForURLScheme(IntPtr scheme, IntPtr bundleId);

    [DllImport(CoreServices)]
    private static extern IntPtr LSCopyDefaultHandlerForURLScheme(IntPtr scheme);

    [DllImport(CoreServices)]
    private static extern int LSSetDefaultRoleHandlerForContentType(IntPtr contentType, uint role, IntPtr bundleId);

    [DllImport(CoreServices)]
    private static extern IntPtr LSCopyDefaultRoleHandlerForContentType(IntPtr contentType, uint role);

    /// <summary>
    /// Делает Spark программой по умолчанию для magnet: и .torrent, если это ещё не так. Выключение ничего
    /// не снимает: macOS сама выберет другую программу, когда её назначат.
    /// </summary>
    public static void ApplyTorrentHandler(bool enabled, IStrelaLog log)
    {
        if (!enabled || !OperatingSystem.IsMacOS()) return;
        try
        {
            using var bundle = new CfString(BundleId);
            using var magnet = new CfString("magnet");
            using var torrent = new CfString(TorrentType);
            if (!IsUs(LSCopyDefaultHandlerForURLScheme(magnet.Handle)))
                Check(LSSetDefaultHandlerForURLScheme(magnet.Handle, bundle.Handle), "magnet:", log);
            if (!IsUs(LSCopyDefaultRoleHandlerForContentType(torrent.Handle, AllRoles)))
                Check(LSSetDefaultRoleHandlerForContentType(torrent.Handle, AllRoles, bundle.Handle), ".torrent", log);
        }
        catch (Exception ex)
        {
            log.Error("Не удалось назначить Spark для magnet и .torrent", ex);
        }
    }

    private static void Check(int status, string what, IStrelaLog log)
    {
        if (status == 0) log.Info($"Spark назначен для {what}");
        else log.Warn($"LaunchServices отказал для {what}: {status}");
    }

    private static bool IsUs(IntPtr handler)
    {
        if (handler == IntPtr.Zero) return false;
        try
        {
            var buffer = new byte[(CFStringGetLength(handler).ToInt32() + 1) * 4];
            return CFStringGetCString(handler, buffer, buffer.Length, Utf8)
                && string.Equals(System.Text.Encoding.UTF8.GetString(buffer).TrimEnd('\0'), BundleId, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CFRelease(handler);
        }
    }

    private sealed class CfString : IDisposable
    {
        public CfString(string s) => Handle = CFStringCreateWithCString(IntPtr.Zero, s, Utf8);
        public IntPtr Handle { get; }
        public void Dispose() { if (Handle != IntPtr.Zero) CFRelease(Handle); }
    }
}
