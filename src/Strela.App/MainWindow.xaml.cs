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
        // Правый клик по уже выделенной строке сохраняет всё выделение (чтобы удалить несколько сразу),
        // по другой строке — выделяет только её.
        if (sender is not ListViewItem item || item.IsSelected) return;
        List.SelectedItems.Clear();
        item.IsSelected = true;
    }

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || List.SelectedItems.Count == 0) return;
        var selected = List.SelectedItems.Cast<object>().ToList();
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            _vm.RemoveSelectedWithFileCommand.Execute(selected);
        else
            _vm.RemoveSelectedCommand.Execute(selected);
        e.Handled = true;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => List.SelectAll();

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

    /// <summary>Перетащенные .torrent-файлы (по пути на строку) или текст со ссылками.</summary>
    internal static string? TryGetText(IDataObject data)
    {
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
        {
            var torrents = files.Where(f => f.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)).ToList();
            return torrents.Count > 0 ? string.Join("\n", torrents) : null;
        }
        if (data.GetDataPresent(DataFormats.UnicodeText)) return data.GetData(DataFormats.UnicodeText) as string;
        if (data.GetDataPresent(DataFormats.Text)) return data.GetData(DataFormats.Text) as string;
        return null;
    }
}
