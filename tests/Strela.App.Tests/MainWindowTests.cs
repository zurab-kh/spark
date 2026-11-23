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
                Snap("setup.exe", 120 * MB, 120 * MB, DownloadStatus.Completed, 0, null, 1));

            var texts = CollectText(window);
            Assert.Contains("ubuntu-24.04.iso", texts);
            Assert.Contains(texts, t => t.StartsWith("4.0 ГБ из 5.9 ГБ · 42 МБ/с · осталось 0:45"));
            Assert.Contains("Готово · 120 МБ", texts);
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

    private static DownloadSnapshot Snap(string name, long total, long done, DownloadStatus status, double bps, TimeSpan? eta, long order) =>
        new(Guid.NewGuid(), name, null, "http://x/" + name, total, done, bps, eta, status, null, 16, order);

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

    private static List<string> CollectText(DependencyObject root)
    {
        var texts = new List<string>();
        void Walk(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is TextBlock tb) texts.Add(tb.Text);
                Walk(child);
            }
        }
        Walk(root);
        return texts;
    }
}
