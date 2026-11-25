using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Strela.App.Services;
using Strela.Engine.Video;

namespace Strela.App.Views;

/// <summary>Немодальное окно настроек.</summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _current;
    private readonly Action<AppSettings> _onSave;

    /// <summary>Только для дизайнера XAML.</summary>
    public SettingsWindow() : this(new AppSettings(), _ => { }) { }

    public SettingsWindow(AppSettings current, Action<AppSettings> onSave)
    {
        _current = current;
        _onSave = onSave;
        InitializeComponent();
        FolderBox.Text = current.DownloadFolder;
        // Index = value: 0 is auto, 1..32 a fixed count.
        var choices = new List<string> { "Авто (рекомендуется)" };
        for (var n = 1; n <= AppSettings.MaxConnectionsPerFile; n++) choices.Add(n.ToString(CultureInfo.InvariantCulture));
        ConnectionsCombo.ItemsSource = choices;
        ConnectionsCombo.SelectedIndex = Math.Clamp(current.ConnectionsPerFile, 0, AppSettings.MaxConnectionsPerFile);
        ActiveBox.Value = current.MaxActiveDownloads;
        ClipboardCheck.IsChecked = current.WatchClipboard;
        NotifyCheck.IsChecked = current.Notifications;
        AutostartCheck.IsChecked = current.StartWithWindows;
        BrowserCheck.IsChecked = current.InterceptBrowserDownloads;
        TorrentsCheck.IsChecked = current.HandleTorrents;
        // Index = VideoQuality value.
        VideoCombo.ItemsSource = VideoQualityNames;
        VideoCombo.SelectedIndex = (int)current.VideoQuality;
    }

    private static readonly string[] VideoQualityNames = ["Лучшее качество", "До 1080p", "До 720p", "Только звук (MP3)"];

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        IStorageFolder? start = null;
        if (!string.IsNullOrWhiteSpace(FolderBox.Text) && Directory.Exists(FolderBox.Text))
            start = await StorageProvider.TryGetFolderFromPathAsync(FolderBox.Text);
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Папка загрузок",
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path) FolderBox.Text = path;
    }

    private void ShowExtension_Click(object? sender, RoutedEventArgs e)
    {
        var folder = MacIntegration.ExtensionFolder;
        Shell.RevealInFolder(Directory.Exists(folder) ? folder : Path.GetDirectoryName(folder));
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        var settings = _current with
        {
            DownloadFolder = (FolderBox.Text ?? "").Trim(),
            ConnectionsPerFile = ConnectionsCombo.SelectedIndex >= 0 ? ConnectionsCombo.SelectedIndex : _current.ConnectionsPerFile,
            MaxActiveDownloads = ActiveBox.Value is { } v ? (int)v : _current.MaxActiveDownloads,
            WatchClipboard = ClipboardCheck.IsChecked == true,
            Notifications = NotifyCheck.IsChecked == true,
            StartWithWindows = AutostartCheck.IsChecked == true,
            InterceptBrowserDownloads = BrowserCheck.IsChecked == true,
            HandleTorrents = TorrentsCheck.IsChecked == true,
            VideoQuality = VideoCombo.SelectedIndex >= 0 ? (VideoQuality)VideoCombo.SelectedIndex : _current.VideoQuality,
        };
        _onSave(settings.Clamped());
        Close();
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
