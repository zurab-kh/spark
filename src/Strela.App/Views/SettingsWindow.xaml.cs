using System.Globalization;
using System.Windows;
using Microsoft.Win32;
using Strela.App.Services;
using Strela.Engine.Video;

namespace Strela.App.Views;

/// <summary>Немодальное окно настроек (открывается через Show()).</summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _current;
    private readonly Action<AppSettings> _onSave;
    private readonly AppUpdater? _updater;
    private readonly Action? _restartForUpdate;

    public SettingsWindow(AppSettings current, Action<AppSettings> onSave, AppUpdater? updater = null, Action? restartForUpdate = null)
    {
        _current = current;
        _onSave = onSave;
        _updater = updater;
        _restartForUpdate = restartForUpdate;
        InitializeComponent();
        ShowUpdateState();
        FolderBox.Text = current.DownloadFolder;
        // Index = value: 0 is auto, 1..32 a fixed count.
        ConnectionsCombo.Items.Add("Авто (рекомендуется)");
        for (var n = 1; n <= AppSettings.MaxConnectionsPerFile; n++)
            ConnectionsCombo.Items.Add(n.ToString(CultureInfo.InvariantCulture));
        ConnectionsCombo.SelectedIndex = Math.Clamp(current.ConnectionsPerFile, 0, AppSettings.MaxConnectionsPerFile);
        ActiveBox.Text = current.MaxActiveDownloads.ToString(CultureInfo.InvariantCulture);
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

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Папка загрузок" };
        if (!string.IsNullOrWhiteSpace(FolderBox.Text) && Directory.Exists(FolderBox.Text))
            dialog.InitialDirectory = FolderBox.Text;
        if (dialog.ShowDialog(this) == true) FolderBox.Text = dialog.FolderName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var settings = _current with
        {
            DownloadFolder = FolderBox.Text.Trim(),
            ConnectionsPerFile = ConnectionsCombo.SelectedIndex >= 0 ? ConnectionsCombo.SelectedIndex : _current.ConnectionsPerFile,
            MaxActiveDownloads = ParseOr(ActiveBox.Text, _current.MaxActiveDownloads),
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

    private void ShowUpdateState()
    {
        VersionText.Text = _updater is null ? "Spark" : $"Spark {_updater.CurrentVersion}";
        if (_updater is { ReadyVersion: { } ready })
            UpdateText.Text = $"Версия {ready} загружена: перезапустите Spark";
        else if (_updater is { IsEnabled: false })
            UpdateText.Text = "Обновления работают в Spark, установленном из выпуска на GitHub";
        RestartButton.Visibility = _updater?.ReadyVersion is not null ? Visibility.Visible : Visibility.Collapsed;
        CheckButton.IsEnabled = _updater?.IsEnabled == true;
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        if (_updater is null) return;
        CheckButton.IsEnabled = false;
        UpdateText.Text = "Проверяю…";
        var result = await _updater.CheckAsync();
        ShowUpdateState();
        UpdateText.Text = result;
    }

    private void Restart_Click(object sender, RoutedEventArgs e) => _restartForUpdate?.Invoke();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static int ParseOr(string text, int fallback) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
