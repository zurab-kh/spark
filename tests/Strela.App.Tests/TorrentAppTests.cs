using System.Windows;
using Strela.App.Services;
using Strela.App.ViewModels;
using Strela.Engine;
using Strela.Engine.Integration;
using Strela.Engine.Manager;

namespace Strela.App.Tests;

public sealed class TorrentAppTests : IDisposable
{
    private const string Magnet = "magnet:?xt=urn:btih:c12fe1c06bba254a9dc9f519b335aa7c1367a88a&dn=Ubuntu";
    private const long GB = 1024L * 1024 * 1024;
    private const long MB = 1024L * 1024;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spark-tor-" + Guid.NewGuid().ToString("N"));

    public TorrentAppTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    // ---- adding ----

    [Fact]
    public void AddLinks_AcceptsMagnetAndTorrentPath()
    {
        var file = Path.Combine(_dir, "My Movie.torrent");
        File.WriteAllText(file, "d4:infoe");
        var fake = new FakeDownloadManager();
        var vm = new MainViewModel(fake);

        Assert.Equal(3, vm.AddLinks($"{Magnet}\n{file}\nhttps://a/x.zip\n{Path.Combine(_dir, "missing.torrent")}"));
        Assert.Equal(Magnet, fake.Added[0].Url.OriginalString);
        Assert.True(fake.Added[1].Url.IsFile);
        Assert.Equal(file, fake.Added[1].Url.LocalPath);
        Assert.Equal("https://a/x.zip", fake.Added[2].Url.AbsoluteUri);
    }

    [Fact]
    public void AddLinks_RejectedByEngine_ShowsHint()
    {
        var vm = new MainViewModel(new ThrowingManager());
        Assert.Equal(0, vm.AddLinks(Magnet));
        Assert.Equal("Не удалось открыть торрент", vm.InputHint);
    }

    [Fact]
    public void AddRequests_FromSecondInstance_PassesMagnetAndTorrentFile()
    {
        var file = Path.Combine(_dir, "a.torrent");
        File.WriteAllText(file, "x");
        var fake = new FakeDownloadManager();
        var vm = new MainViewModel(fake);

        // What PipeSingleInstanceChannel.SendAsync sends for "Spark.exe <arg>".
        Assert.Equal(2, vm.AddRequests([new IpcMessage(Magnet), new IpcMessage(file), new IpcMessage(@"C:\Windows\notepad.exe")]));
        Assert.Equal(Magnet, fake.Added[0].Url.OriginalString);
        Assert.Equal(file, fake.Added[1].Url.LocalPath);
    }

    [Fact]
    public void LinkParser_TryParseLink()
    {
        var file = Path.Combine(_dir, "b.torrent");
        File.WriteAllText(file, "x");
        Assert.True(LinkParser.TryParseLink(Magnet, out _));
        Assert.True(LinkParser.TryParseLink(file, out var f) && f.IsFile);
        Assert.True(LinkParser.TryParseLink("https://a/b.iso", out _));
        Assert.False(LinkParser.TryParseLink(Path.Combine(_dir, "nope.torrent"), out _));
        Assert.False(LinkParser.TryParseLink("magnet:?dn=x", out _));
        Assert.False(LinkParser.TryParseLink(@"C:\Windows\notepad.exe", out _));
    }

    [Fact]
    public void Drop_TorrentFiles_BecomeLines()
    {
        var data = new DataObject(DataFormats.FileDrop, new[] { @"C:\a\one.torrent", @"C:\a\x.zip", @"C:\b\Two.TORRENT" });
        Assert.Equal("C:\\a\\one.torrent\nC:\\b\\Two.TORRENT", MainWindow.TryGetText(data));
        Assert.Null(MainWindow.TryGetText(new DataObject(DataFormats.FileDrop, new[] { @"C:\a\x.zip" })));
        Assert.Equal("https://a/b.zip", MainWindow.TryGetText(new DataObject(DataFormats.UnicodeText, "https://a/b.zip")));
    }

    // ---- row ----

    [Fact]
    public void TorrentRow_HasTorBadge_AndSeedsPeersDetail()
    {
        var vm = new DownloadItemViewModel(Guid.NewGuid());
        vm.Update(new DownloadSnapshot(vm.Id, "Ubuntu.iso", null, Magnet, 4 * GB, (long)(1.2 * GB), 12 * MB,
            TimeSpan.FromSeconds(190), DownloadStatus.Downloading, "сиды 30 · пиры 12", 42, 0, DownloadKind.Torrent));

        Assert.Equal("TOR", vm.TypeLabel);
        Assert.Equal("torrent", vm.BadgeKind);
        Assert.Equal($"{Format.Bytes((long)(1.2 * GB))} из {Format.Bytes(4 * GB)} · {Format.Speed(12 * MB)} · сиды 30 · пиры 12 · осталось {Format.Eta(TimeSpan.FromSeconds(190))}",
            vm.DetailText);
    }

    [Fact]
    public void TorrentRow_SearchingForPeers()
    {
        var vm = new DownloadItemViewModel(Guid.NewGuid());
        vm.Update(new DownloadSnapshot(vm.Id, "Торрент c12fe1c0", null, Magnet, null, 0, 0, null,
            DownloadStatus.Downloading, "Поиск пиров…", 0, 0, DownloadKind.Torrent));
        Assert.EndsWith("Поиск пиров…", vm.DetailText, StringComparison.Ordinal);
        Assert.Equal("TOR", vm.TypeLabel);
    }

    [Fact]
    public void HttpRow_Unchanged()
    {
        var vm = new DownloadItemViewModel(Guid.NewGuid());
        vm.Update(FakeDownloadManager.Snap(vm.Id, 0, name: "a.iso"));
        Assert.Equal("ISO", vm.TypeLabel);
        Assert.Equal("disk", vm.BadgeKind);
    }

    // ---- settings ----

    [Fact]
    public void HandleTorrents_DefaultsOn_AlsoForOldSettingsFiles()
    {
        Assert.True(new AppSettings().HandleTorrents);
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """{ "MaxActiveDownloads": 4, "SettingsVersion": 2 }""");
        Assert.True(new SettingsStore(path).Load().HandleTorrents);
    }

    // ---- registry ----

    [Fact]
    public void Register_WritesMagnetProtocolAndTorrentProgId()
    {
        var reg = new FakeEditor();
        var exe = @"C:\Apps\Spark\Spark.exe";

        Assert.True(TorrentAssociation.Register(exe, reg, NullLog.Instance));

        var command = "\"C:\\Apps\\Spark\\Spark.exe\" \"%1\"";
        Assert.Equal("URL:Magnet Link", reg.Get(@"Software\Classes\magnet", ""));
        Assert.Equal("", reg.Get(@"Software\Classes\magnet", "URL Protocol"));
        Assert.Equal(command, reg.Get(@"Software\Classes\magnet\shell\open\command", ""));
        Assert.Equal("Spark.Torrent", reg.Get(@"Software\Classes\.torrent", ""));
        Assert.Equal("", reg.Get(@"Software\Classes\.torrent\OpenWithProgids", "Spark.Torrent"));
        Assert.Equal(command, reg.Get(@"Software\Classes\Spark.Torrent\shell\open\command", ""));
        Assert.Equal("\"C:\\Apps\\Spark\\Spark.exe\",0", reg.Get(@"Software\Classes\Spark.Torrent\DefaultIcon", ""));
        Assert.All(reg.Values.Keys, k => Assert.StartsWith(@"Software\Classes\", k.Key, StringComparison.Ordinal));

        var count = reg.Values.Count;
        TorrentAssociation.Register(exe, reg, NullLog.Instance);
        Assert.Equal(count, reg.Values.Count);   // idempotent
    }

    [Fact]
    public void Unregister_RemovesSparkKeys()
    {
        var reg = new FakeEditor();
        reg.SetValue(@"Software\Classes\.torrent\OpenWithProgids", "qBittorrent.File.Torrent", "");
        TorrentAssociation.Register(@"C:\Spark\Spark.exe", reg, NullLog.Instance);

        Assert.True(TorrentAssociation.Unregister(reg, NullLog.Instance));

        Assert.DoesNotContain(reg.Values.Keys, k => k.Key.StartsWith(@"Software\Classes\magnet", StringComparison.Ordinal));
        Assert.DoesNotContain(reg.Values.Keys, k => k.Key.StartsWith(@"Software\Classes\Spark.Torrent", StringComparison.Ordinal));
        Assert.Null(reg.Get(@"Software\Classes\.torrent", ""));
        Assert.Null(reg.Get(@"Software\Classes\.torrent\OpenWithProgids", "Spark.Torrent"));
        Assert.Equal("", reg.Get(@"Software\Classes\.torrent\OpenWithProgids", "qBittorrent.File.Torrent"));
    }

    [Fact]
    public void Unregister_LeavesAnotherClientsHandlers()
    {
        var reg = new FakeEditor();
        reg.SetValue(@"Software\Classes\magnet\shell\open\command", "", "\"C:\\qBittorrent\\qbittorrent.exe\" \"%1\"");
        reg.SetValue(@"Software\Classes\.torrent", "", "qBittorrent.File.Torrent");

        TorrentAssociation.Unregister(reg, NullLog.Instance);

        Assert.Equal("\"C:\\qBittorrent\\qbittorrent.exe\" \"%1\"", reg.Get(@"Software\Classes\magnet\shell\open\command", ""));
        Assert.Equal("qBittorrent.File.Torrent", reg.Get(@"Software\Classes\.torrent", ""));
    }

    [Fact]
    public void Register_Failure_DoesNotThrow()
    {
        var reg = new FakeEditor { Fail = true };
        Assert.False(TorrentAssociation.Register(@"C:\Spark\Spark.exe", reg, NullLog.Instance));
        Assert.False(TorrentAssociation.Unregister(reg, NullLog.Instance));
    }

    private sealed class FakeEditor : IRegistryEditor
    {
        public Dictionary<(string Key, string Name), string> Values { get; } = new();
        public bool Fail { get; init; }

        public string? Get(string key, string name) => Values.TryGetValue((key, name), out var v) ? v : null;

        public void SetValue(string subKey, string name, string value)
        {
            if (Fail) throw new UnauthorizedAccessException("denied");
            Values[(subKey, name)] = value;
        }

        public string? GetValue(string subKey, string name)
        {
            if (Fail) throw new UnauthorizedAccessException("denied");
            return Get(subKey, name);
        }

        public void DeleteValue(string subKey, string name) => Values.Remove((subKey, name));

        public void DeleteKeyTree(string subKey)
        {
            foreach (var k in Values.Keys.Where(k => k.Key == subKey || k.Key.StartsWith(subKey + @"\", StringComparison.Ordinal)).ToList())
                Values.Remove(k);
        }
    }

    private sealed class ThrowingManager : IDownloadManager
    {
        public bool HasActiveWork => false;
        public event Action<DownloadSnapshot>? Completed { add { } remove { } }
        public Task InitializeAsync() => Task.CompletedTask;
        public Guid Add(DownloadRequest request) => throw new ArgumentException("bad torrent");
        public void Pause(Guid id) { }
        public void Resume(Guid id) { }
        public void Retry(Guid id) { }
        public void PauseAll() { }
        public void ResumeAll() { }
        public Task RemoveAsync(Guid id, bool deleteFile) => Task.CompletedTask;
        public Task<bool> UpdateUrlAsync(Guid id, Uri newUrl) => Task.FromResult(false);
        public void Move(Guid id, int newIndex) { }
        public void ApplyOptions(EngineOptions options) { }
        public IReadOnlyList<DownloadSnapshot> GetSnapshots() => [];
        public ValueTask DisposeAsync() => default;
    }
}
