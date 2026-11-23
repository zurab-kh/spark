using System.Globalization;
using System.Windows;
using Microsoft.Win32;
using Strela.App.Services;

namespace Strela.App.Views;

/// <summary>Немодальное окно настроек (открывается через Show()).</summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _current;
    private readonly Action<AppSettings> _onSave;

    public SettingsWindow(AppSettings current, Action<AppSettings> onSave)
    {
        _current = current;
        _onSave = onSave;
        InitializeComponent();
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
    }

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
        };
        _onSave(settings.Clamped());
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static int ParseOr(string text, int fallback) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
