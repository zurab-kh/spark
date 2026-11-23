using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Strela.App.ViewModels;

namespace Strela.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _timer;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) =>
        {
            _vm.Refresh();
            Refreshed?.Invoke();
        };
        Loaded += (_, _) =>
        {
            _vm.Refresh();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>После каждого опроса движка (раз в 250 мс).</summary>
    public event Action? Refreshed;

    public event Action? SettingsRequested;

    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Return) return;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;   // Shift+Enter — новая строка
        _vm.AddFromInputCommand.Execute(null);
        e.Handled = true;
    }

    private void Item_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem { DataContext: DownloadItemViewModel { IsCompleted: true } item })
            _vm.OpenFileCommand.Execute(item);
    }

    private void Item_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem item) item.IsSelected = true;
    }

    private void List_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Меню только поверх строки списка.
        if (e.OriginalSource is not DependencyObject src || ItemsControl.ContainerFromElement(List, src) is null)
            e.Handled = true;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetText(e.Data) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        var text = TryGetText(e.Data);
        if (text is not null) _vm.AddLinks(text);
        e.Handled = true;
    }

    private static string? TryGetText(IDataObject data)
    {
        if (data.GetDataPresent(DataFormats.UnicodeText)) return data.GetData(DataFormats.UnicodeText) as string;
        if (data.GetDataPresent(DataFormats.Text)) return data.GetData(DataFormats.Text) as string;
        return null;
    }
}
