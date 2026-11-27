using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Strela.App;
using Strela.App.Services;
using Strela.App.Tests;
using Strela.App.ViewModels;
using Strela.App.Views;
using Strela.Engine;
using Strela.Engine.Manager;

[assembly: AvaloniaTestApplication(typeof(Spark.Mac.Tests.TestApp))]

namespace Spark.Mac.Tests;

public static class TestApp
{
    // Настоящий App (стили и цвета из App.axaml) поверх Skia: снимки окна — как на экране.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public class MainWindowMacTests
{
    private const long MB = 1024 * 1024;

    private static (FakeDownloadManager Fake, MainWindow Window) Show(params DownloadSnapshot[] snaps)
    {
        var fake = new FakeDownloadManager();
        fake.Snapshots.AddRange(snaps);
        var window = new MainWindow(new MainViewModel(fake));
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (fake, window);
    }

    private static List<string> Texts(Window window) =>
        window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible)
            .Select(t => t.Text ?? "").Where(t => t.Length > 0).ToList();

    [AvaloniaFact]
    public void Renders_EveryKindOfDownload()
    {
        var (_, window) = Show(
            new DownloadSnapshot(Guid.NewGuid(), "ubuntu-24.04-desktop-amd64.iso", null, "https://h/u.iso", 6000 * MB, 4100 * MB,
                42 * MB, TimeSpan.FromSeconds(45), DownloadStatus.Downloading, null, 16, 0),
            new DownloadSnapshot(Guid.NewGuid(), "Big.Buck.Bunny.1080p", null, "magnet:?xt=urn:btih:abc", 4000 * MB, 1200 * MB,
                12 * MB, TimeSpan.FromMinutes(3), DownloadStatus.Downloading, "сиды 30 · пиры 12", 42, 1, DownloadKind.Torrent),
            new DownloadSnapshot(Guid.NewGuid(), "archive.zip", null, "https://h/a.zip", 600 * MB, 210 * MB,
                0, null, DownloadStatus.Paused, null, 0, 2),
            new DownloadSnapshot(Guid.NewGuid(), "report.pdf", "/Users/me/Downloads/report.pdf", "https://h/r.pdf", 3 * MB, 3 * MB,
                0, null, DownloadStatus.Completed, null, 0, 3),
            new DownloadSnapshot(Guid.NewGuid(), "setup.dmg", null, "https://h/s.dmg", null, 0,
                0, null, DownloadStatus.Error, "Сервер вернул 403", 0, 4),
            new DownloadSnapshot(Guid.NewGuid(), "movie.mkv", null, "https://h/m.mkv", 900 * MB, 0,
                0, null, DownloadStatus.Queued, null, 0, 5));

        var texts = Texts(window);
        Assert.Contains("ubuntu-24.04-desktop-amd64.iso", texts);
        Assert.Contains("TOR", texts);
        Assert.Contains(texts, t => t.Contains("сиды 30"));
        Assert.Contains("Приостановлено", texts);
        Assert.Contains("Готово", texts);
        Assert.Contains("Сервер вернул 403", texts);
        Assert.Contains(texts, t => t.StartsWith("В очереди №", StringComparison.Ordinal));
        Assert.DoesNotContain("Загрузок пока нет", texts);
        Snapshot(window, "mac-main.png");
    }

    [AvaloniaFact]
    public void EmptyList_ShowsEmptyState()
    {
        var (_, window) = Show();
        Assert.Contains("Загрузок пока нет", Texts(window));
        Snapshot(window, "mac-empty.png");
    }

    [AvaloniaFact]
    public void Enter_InInputBox_AddsLink()
    {
        var (fake, window) = Show();
        var input = window.FindControl<TextBox>("InputBox")!;
        input.Focus();
        input.Text = "https://example.com/file.zip";
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("https://example.com/file.zip", Assert.Single(fake.Added).Url.AbsoluteUri);
        Assert.Equal("", input.Text);
    }

    [AvaloniaFact]
    public void Backspace_RemovesSelectedFromList()
    {
        var (fake, window) = Show(FakeDownloadManager.Snap(Guid.NewGuid(), 0), FakeDownloadManager.Snap(Guid.NewGuid(), 1));
        var list = window.FindControl<ListBox>("List")!;
        list.SelectAll();
        list.ContainerFromIndex(0)!.Focus();   // как после клика по строке
        window.KeyPress(Key.Back, RawInputModifiers.None, PhysicalKey.Backspace, null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, fake.Removed.Count);
        Assert.All(fake.Removed, r => Assert.False(r.DeleteFile));
    }

    [AvaloniaFact]
    public void SettingsAndToast_Render()
    {
        var settings = new SettingsWindow(new AppSettings(), _ => { });
        settings.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("Открывать magnet и .torrent в Spark",
            settings.GetLogicalDescendants().OfType<CheckBox>().Select(c => c.Content as string));
        Snapshot(settings, "mac-settings.png");

        var toast = new ToastWindow("Готово: ubuntu.iso", () => { }, "Показать в Finder");
        toast.Show();
        Dispatcher.UIThread.RunJobs();
        Snapshot(toast, "mac-toast.png");
    }

    /// <summary>SPARK_UI_SNAPSHOTS — папка для PNG-снимков окон (для глаз, не для сравнения).</summary>
    private static void Snapshot(Window window, string name)
    {
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var dir = Environment.GetEnvironmentVariable("SPARK_UI_SNAPSHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        frame!.Save(Path.Combine(dir, name));
    }
}

public class MacIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "spark-mac-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    [Fact]
    public void MacBundlePath_FindsAppAroundExecutable()
    {
        var bundle = NativeMessagingHost.MacBundlePath("/Applications/Spark.app/Contents/MacOS/Spark");
        Assert.Equal(Path.Combine(Path.DirectorySeparatorChar + "Applications", "Spark.app"), bundle);
        Assert.Null(NativeMessagingHost.MacBundlePath("/Users/me/build/Spark"));
    }

    [Fact]
    public void RegisterBrowsers_WritesManifestOnlyForInstalledBrowsers_AndCopiesExtension()
    {
        var support = Path.Combine(_root, "Application Support");
        Directory.CreateDirectory(Path.Combine(support, "Google", "Chrome"));
        Directory.CreateDirectory(Path.Combine(support, "Microsoft Edge"));
        var macOs = Path.Combine(_root, "Spark.app", "Contents", "MacOS");
        var resources = Path.Combine(_root, "Spark.app", "Contents", "Resources", "browser-extension", "icons");
        Directory.CreateDirectory(macOs);
        Directory.CreateDirectory(resources);
        File.WriteAllText(Path.Combine(resources, "..", "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(resources, "icon-16.png"), "png");
        var exe = Path.Combine(macOs, "Spark");
        var extension = Path.Combine(_root, "Spark", "browser-extension");

        MacIntegration.RegisterBrowsers(exe, NullLog.Instance, support, extension);

        var chrome = Path.Combine(support, "Google", "Chrome", "NativeMessagingHosts", "com.spark.downloader.json");
        Assert.True(File.Exists(chrome));
        Assert.True(File.Exists(Path.Combine(support, "Microsoft Edge", "NativeMessagingHosts", "com.spark.downloader.json")));
        Assert.False(Directory.Exists(Path.Combine(support, "BraveSoftware")));
        var manifest = File.ReadAllText(chrome);
        Assert.Contains("chrome-extension://meffaihaokjhicodaannmgedjflgbcgd/", manifest);
        Assert.Contains(exe.Replace("\\", "\\\\"), manifest);
        Assert.True(File.Exists(Path.Combine(extension, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(extension, "icons", "icon-16.png")));
    }

    [Fact]
    public void LaunchAgent_OpensTheBundle()
    {
        var plist = MacIntegration.BuildLaunchAgent("/Applications/Spark & Co.app");
        Assert.Contains("<string>/usr/bin/open</string>", plist);
        Assert.Contains("<string>/Applications/Spark &amp; Co.app</string>", plist);
        Assert.Contains("<key>RunAtLoad</key>", plist);
        Assert.StartsWith("<?xml", plist);
    }
}
