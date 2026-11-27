using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Strela.App.ViewModels;

namespace Strela.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _timer;

    /// <summary>Только для дизайнера XAML.</summary>
    public MainWindow() : this(null!) { }

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (_, _) =>
        {
            _vm.Refresh();
            Refreshed?.Invoke();
        });
        Opened += (_, _) =>
        {
            _vm?.Refresh();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();

        InputBox.AddHandler(KeyDownEvent, InputBox_KeyDown, RoutingStrategies.Tunnel);
        List.AddHandler(KeyDownEvent, List_KeyDown, RoutingStrategies.Tunnel);
        SearchBox.AddHandler(KeyDownEvent, SearchBox_KeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel);
        List.DoubleTapped += List_DoubleTapped;
        ItemMenu.Opening += ItemMenu_Opening;
        AddHandler(DragDrop.DragOverEvent, Window_DragOver);
        AddHandler(DragDrop.DropEvent, Window_Drop);
    }

    /// <summary>После каждого опроса движка (раз в 250 мс).</summary>
    public event Action? Refreshed;

    public event Action? SettingsRequested;

    private void Settings_Click(object? sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    private void InputBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return)) return;
        if ((e.KeyModifiers & KeyModifiers.Shift) != 0) return;   // Shift+Enter — новая строка
        _vm.AddFromInputCommand.Execute(null);
        e.Handled = true;
    }

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.F || e.KeyModifiers != KeyModifiers.Meta || !SearchBox.IsVisible) return;
        SearchBox.Focus();
        SearchBox.SelectAll();
        e.Handled = true;
    }

    /// <summary>Esc очищает поиск; в пустом поиске — возвращает фокус списку.</summary>
    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (!string.IsNullOrEmpty(SearchBox.Text)) SearchBox.Text = "";
        else List.Focus();
        e.Handled = true;
    }

    private void List_KeyDown(object? sender, KeyEventArgs e)
    {
        // ⌫ / Delete — убрать из списка; ⇧⌫ — вместе с файлами.
        if (e.Key is not (Key.Back or Key.Delete) || Selected().Count == 0) return;
        if ((e.KeyModifiers & KeyModifiers.Shift) != 0) _vm.RemoveSelectedWithFileCommand.Execute(Selected());
        else _vm.RemoveSelectedCommand.Execute(Selected());
        e.Handled = true;
    }

    private void List_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Control)?.DataContext is DownloadItemViewModel { IsCompleted: true } item)
            _vm.OpenFileCommand.Execute(item);
    }

    private List<DownloadItemViewModel> Selected() =>
        List.SelectedItems?.OfType<DownloadItemViewModel>().ToList() ?? new();

    private DownloadItemViewModel? Current => List.SelectedItem as DownloadItemViewModel;

    private static DownloadItemViewModel? ItemOf(object? sender) => (sender as Control)?.DataContext as DownloadItemViewModel;

    // Кнопки в строке
    private void Pause_Click(object? sender, RoutedEventArgs e) => _vm.PauseCommand.Execute(ItemOf(sender));
    private void Resume_Click(object? sender, RoutedEventArgs e) => _vm.ResumeCommand.Execute(ItemOf(sender));
    private void Retry_Click(object? sender, RoutedEventArgs e) => _vm.RetryCommand.Execute(ItemOf(sender));
    private void Open_Click(object? sender, RoutedEventArgs e) => _vm.OpenFileCommand.Execute(ItemOf(sender));
    private void Reveal_Click(object? sender, RoutedEventArgs e) => _vm.OpenFolderCommand.Execute(ItemOf(sender));

    // Контекстное меню: действия над выделенной строкой (удаление — над всеми выделенными)
    private void ItemMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (Current is not { } item)
        {
            e.Cancel = true;   // меню только поверх строки списка
            return;
        }
        MenuOpen.IsEnabled = item.IsCompleted;
        MenuPause.IsEnabled = item.CanPause;
        MenuResume.IsEnabled = item.CanResume;
        MenuRetry.IsEnabled = item.CanRetry;
        QueueSeparator.IsVisible = MenuEarlier.IsVisible = MenuLater.IsVisible = item.IsQueued;
    }

    private void MenuOpen_Click(object? sender, RoutedEventArgs e) => _vm.OpenFileCommand.Execute(Current);
    private void MenuReveal_Click(object? sender, RoutedEventArgs e) => _vm.OpenFolderCommand.Execute(Current);
    private void MenuPause_Click(object? sender, RoutedEventArgs e) => _vm.PauseCommand.Execute(Current);
    private void MenuResume_Click(object? sender, RoutedEventArgs e) => _vm.ResumeCommand.Execute(Current);
    private void MenuRetry_Click(object? sender, RoutedEventArgs e) => _vm.RetryCommand.Execute(Current);
    private void MenuUpdateUrl_Click(object? sender, RoutedEventArgs e) => _vm.UpdateUrlCommand.Execute(Current);
    private void MenuCopyUrl_Click(object? sender, RoutedEventArgs e) => _vm.CopyUrlCommand.Execute(Current);
    private void MenuEarlier_Click(object? sender, RoutedEventArgs e) => _vm.StartEarlierCommand.Execute(Current);
    private void MenuLater_Click(object? sender, RoutedEventArgs e) => _vm.StartLaterCommand.Execute(Current);
    private void MenuRemove_Click(object? sender, RoutedEventArgs e) => _vm.RemoveSelectedCommand.Execute(Selected());
    private void MenuRemoveWithFile_Click(object? sender, RoutedEventArgs e) => _vm.RemoveSelectedWithFileCommand.Execute(Selected());
    private void MenuSelectAll_Click(object? sender, RoutedEventArgs e) => List.SelectAll();

    private void Window_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = TryGetText(e.DataTransfer) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Window_Drop(object? sender, DragEventArgs e)
    {
        if (TryGetText(e.DataTransfer) is { } text) _vm.AddLinks(text);
        e.Handled = true;
    }

    /// <summary>Перетащенные .torrent-файлы (по пути на строку) или текст со ссылками.</summary>
    internal static string? TryGetText(IDataTransfer data)
    {
        if (data.TryGetFiles() is { Length: > 0 } files)
        {
            var torrents = files.Select(f => f.TryGetLocalPath())
                .Where(p => p is not null && p.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)).ToList();
            return torrents.Count > 0 ? string.Join("\n", torrents) : null;
        }
        return data.TryGetText();
    }
}
