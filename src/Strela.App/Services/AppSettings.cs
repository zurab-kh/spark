using Strela.Engine;

namespace Strela.App.Services;

public sealed record AppSettings
{
    public const int MaxConnectionsPerFile = 32;

    public string DownloadFolder { get; init; } = "";   // "" = системная «Загрузки»
    public int ConnectionsPerFile { get; init; } = 0;   // 0 = авто, иначе 1..32
    public int MaxActiveDownloads { get; init; } = 3;   // 1..10
    public bool WatchClipboard { get; init; } = true;
    public bool Notifications { get; init; } = true;
    public bool StartWithWindows { get; init; } = false;
    public bool InterceptBrowserDownloads { get; init; } = true;   // расширение Chrome/Edge передаёт загрузки в Spark
    /// <summary>Spark — обработчик magnet: и .torrent в HKCU (при выключении ключи удаляются).</summary>
    public bool HandleTorrents { get; init; } = true;
    /// <summary>Версия формата файла; 0 — файл старой версии (до автоподбора соединений). Пишется при сохранении.</summary>
    public int SettingsVersion { get; init; }

    public const int CurrentSettingsVersion = 2;

    public AppSettings Clamped() => this with
    {
        DownloadFolder = DownloadFolder ?? "",
        ConnectionsPerFile = Math.Clamp(ConnectionsPerFile, 0, MaxConnectionsPerFile),
        MaxActiveDownloads = Math.Clamp(MaxActiveDownloads, 1, 10),
    };

    public EngineOptions ToEngineOptions(string stateFolder)
    {
        var s = Clamped();
        return new EngineOptions
        {
            DownloadFolder = string.IsNullOrWhiteSpace(s.DownloadFolder) ? KnownFolders.Downloads() : s.DownloadFolder,
            StateFolder = stateFolder,
            ConnectionsPerFile = s.ConnectionsPerFile,
            MaxActiveDownloads = s.MaxActiveDownloads,
        };
    }
}
