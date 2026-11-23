using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Strela.App.ViewModels;
using Strela.Engine.Manager;

namespace Strela.App.Tests;

public class MainWindowTests
{
    [Fact]
    public void ListRows_RenderGridColumns_NotItemTypeName()
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
                var fake = new FakeDownloadManager();
                fake.Snapshots.Add(new DownloadSnapshot(Guid.NewGuid(), "ubuntu.iso", null, "http://x/ubuntu.iso",
                    1000, 500, 0, null, DownloadStatus.Downloading, null, 4, 0));
                var vm = new MainViewModel(fake);
                vm.Refresh();

                var window = new MainWindow(vm) { ShowActivated = false, Left = -10000, Top = -10000 };
                window.Show();
                window.UpdateLayout();

                var list = (ListView)window.FindName("List");
                var row = (ListViewItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                Assert.NotNull(row);
                Assert.NotNull(FindChild<GridViewRowPresenter>(row));
                var texts = new List<string>();
                CollectText(row, texts);
                Assert.Contains("ubuntu.iso", texts);
                Assert.DoesNotContain(texts, t => t.Contains(nameof(DownloadItemViewModel)));
                window.Close();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private static void CollectText(DependencyObject parent, List<string> texts)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TextBlock tb) texts.Add(tb.Text);
            CollectText(child, texts);
        }
    }
}
