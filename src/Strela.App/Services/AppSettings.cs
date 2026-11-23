using Strela.Engine;

namespace Strela.App.Services;

public sealed record AppSettings
{
    public string DownloadFolder { get; init; } = "";   // "" = системная «Загрузки»
    public int ConnectionsPerFile { get; init; } = 16;  // 1..32
    public int MaxActiveDownloads { get; init; } = 3;   // 1..10
    public bool WatchClipboard { get; init; } = true;
    public bool Notifications { get; init; } = true;
    public bool StartWithWindows { get; init; } = false;
    public bool InterceptBrowserDownloads { get; init; } = true;   // расширение Chrome/Edge передаёт загрузки в Spark

    public AppSettings Clamped() => this with
    {
        DownloadFolder = DownloadFolder ?? "",
        ConnectionsPerFile = Math.Clamp(ConnectionsPerFile, 1, 32),
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
