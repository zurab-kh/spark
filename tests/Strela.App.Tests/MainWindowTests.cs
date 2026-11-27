using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Strela.App.ViewModels;
using Strela.Engine.Manager;

namespace Strela.App.Tests;

public class MainWindowTests
{
    private const long MB = 1024 * 1024;

    // One test method: a WPF Application (needed for the Fluent theme) can exist once per process and is bound to its thread.
    [Fact]
    public void MainWindow_RendersRowsAndEmptyState()
    {
        RunOnSta(() =>
        {
            Rows_ShowFileNameAndDetails_NotItemTypeName();
            EmptyList_ShowsEmptyState();
            Search_FiltersRows_ThenShowsNothingFound();
            SecondaryWindows_Load();
        });
    }

    private static void SecondaryWindows_Load()
    {
        var settings = new Views.SettingsWindow(new Services.AppSettings(), _ => { }) { ShowActivated = false, Left = -10000, Top = -10000 };
        settings.Show();
        settings.UpdateLayout();
        Assert.Contains("Соединений на файл", CollectText(settings));
        SaveSnapshotIfRequested(settings, "settings.png");
        settings.Close();

        var toast = new Views.ToastWindow("Скачать ubuntu-24.04.iso?", () => { }, "Скачать");
        toast.Show();
        toast.UpdateLayout();
        Assert.Contains("Скачать ubuntu-24.04.iso?", CollectText(toast));
        SaveSnapshotIfRequested(toast, "toast.png");
        toast.Close();
    }

    private static void Rows_ShowFileNameAndDetails_NotItemTypeName()
    {
        {
            var window = CreateWindow(
                Snap("ubuntu-24.04.iso", 6_000 * MB, 4_100 * MB, DownloadStatus.Downloading, 42 * MB, TimeSpan.FromSeconds(45), 0),
                Snap("setup.exe", 120 * MB, 120 * MB, DownloadStatus.Completed, 0, null, 1),
                Snap("assets_final.zip", 1200 * MB, 144 * MB, DownloadStatus.Paused, 0, null, 2),
                Snap("movie.mkv", 2000 * MB, 0, DownloadStatus.Queued, 0, null, 3),
                Snap("driver_pack.rar", null, 0, DownloadStatus.Error, 0, null, 4, "Файл не найден на сервере (404)"));

            var texts = CollectText(window);
            Assert.Contains("ubuntu-24.04.iso", texts);
            Assert.Contains(texts, t => t.StartsWith("4.0 ГБ из 5.9 ГБ · 42 МБ/с · осталось 0:45"));
            Assert.Contains("Готово · 120 МБ", texts);
            Assert.Contains("Приостановлено · 144 МБ из 1.2 ГБ", texts);
            Assert.Contains("Файл не найден на сервере (404)", texts);
            Assert.DoesNotContain(texts, t => t.Contains(nameof(DownloadItemViewModel)));
            SaveSnapshotIfRequested(window, "main-list.png");
            window.Close();
        }
    }

    private static void EmptyList_ShowsEmptyState()
    {
        var window = CreateWindow();
        var empty = (FrameworkElement)window.FindName("EmptyState");
        Assert.Equal(Visibility.Visible, empty.Visibility);
        SaveSnapshotIfRequested(window, "main-empty.png");
        window.Close();
    }

    private static void Search_FiltersRows_ThenShowsNothingFound()
    {
        var window = CreateWindow(
            Snap("A320 AUA Pack by Atarium.zip", 960 * MB, 401 * MB, DownloadStatus.Downloading, 64 * MB, TimeSpan.FromSeconds(8), 1),
            Snap("IniBuilds Global Jet Manual v1.0.pdf", 11 * MB, 11 * MB, DownloadStatus.Completed, 0, null, 0));
        var vm = (MainViewModel)window.DataContext;
        var search = (FrameworkElement)window.FindName("SearchBox");
        Assert.True(search.IsVisible);
        // Screen readers get the box by its name only: the magnifier and the placeholder are not separate elements.
        Assert.DoesNotContain(ControlNames(window), n => n is "Поиск" or "\uE721" or "Вставьте ссылку и нажмите Enter");
        SaveSnapshotIfRequested(window, "main-newest-first.png");

        vm.SearchText = "manual";
        window.UpdateLayout();
        var texts = CollectText(window);
        Assert.Contains("IniBuilds Global Jet Manual v1.0.pdf", texts);
        Assert.DoesNotContain("A320 AUA Pack by Atarium.zip", texts);
        SaveSnapshotIfRequested(window, "main-search.png");

        vm.SearchText = "nothing-like-this";
        window.UpdateLayout();
        Assert.Contains("Ничего не найдено", CollectText(window));
        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("EmptyState")).Visibility);
        SaveSnapshotIfRequested(window, "main-nothing-found.png");
        window.Close();
    }

    private static DownloadSnapshot Snap(string name, long? total, long done, DownloadStatus status, double bps, TimeSpan? eta,
        long order, string? statusText = null) =>
        new(Guid.NewGuid(), name, null, "http://x/" + name, total, done, bps, eta, status, statusText, 16, order);

    private static MainWindow CreateWindow(params DownloadSnapshot[] snaps)
    {
        var fake = new FakeDownloadManager();
        fake.Snapshots.AddRange(snaps);
        var vm = new MainViewModel(fake);
        vm.Refresh();
        var window = new MainWindow(vm) { ShowActivated = false, Left = -10000, Top = -10000 };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static void RunOnSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                // Same Fluent theme as App.OnStartup, so style keys resolve exactly as in the real app.
#pragma warning disable WPF0001
                _ = new Application { ThemeMode = ThemeMode.System, ShutdownMode = ShutdownMode.OnExplicitShutdown };
#pragma warning restore WPF0001
                body();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    /// <summary>Set STRELA_UI_SNAPSHOTS to a folder to get PNG renders of the window for visual review.</summary>
    private static void SaveSnapshotIfRequested(Window window, string fileName)
    {
        var dir = Environment.GetEnvironmentVariable("STRELA_UI_SNAPSHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        var root = (FrameworkElement)window.Content;
        var bmp = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen())
            // Fluent windows paint their backdrop via Mica, which an offscreen render doesn't capture.
            dc.DrawRectangle(window.TryFindResource("ApplicationBackgroundBrush") as Brush ?? Brushes.Black, null,
                new Rect(0, 0, window.ActualWidth, window.ActualHeight));
        bmp.Render(bg);
        bmp.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        Directory.CreateDirectory(dir);
        using var fs = File.Create(Path.Combine(dir, fileName));
        encoder.Save(fs);
    }

    private static List<string> ControlNames(Window window)
    {
        var names = new List<string>();
        void Walk(System.Windows.Automation.Peers.AutomationPeer peer)
        {
            foreach (var child in peer.GetChildren() ?? [])
            {
                if (child.IsControlElement()) names.Add(child.GetName());
                Walk(child);
            }
        }
        Walk(System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(window));
        return names;
    }

    private static List<string> CollectText(DependencyObject root)
    {
        var texts = new List<string>();
        void Walk(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is TextBlock tb)
                    texts.Add(tb.Inlines.Count > 1 ? string.Concat(tb.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text)) : tb.Text);
                Walk(child);
            }
        }
        Walk(root);
        return texts;
    }
}
