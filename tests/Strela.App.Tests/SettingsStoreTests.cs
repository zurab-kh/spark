using Strela.App.Services;

namespace Strela.App.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "strela-set-" + Guid.NewGuid().ToString("N"));

    public SettingsStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void Settings_MissingFile_Defaults()
    {
        var s = new SettingsStore(Path.Combine(_dir, "settings.json")).Load();
        Assert.Equal(0, s.ConnectionsPerFile);   // auto
        Assert.Equal(3, s.MaxActiveDownloads);
        Assert.True(s.WatchClipboard);
    }

    [Fact]
    public void Settings_RoundTrip_AndClamp()
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        store.Save(new AppSettings { ConnectionsPerFile = 99, MaxActiveDownloads = 0, DownloadFolder = @"D:\x", StartWithWindows = true, Notifications = false });
        var s = store.Load();
        Assert.Equal(32, s.ConnectionsPerFile);
        Assert.Equal(1, s.MaxActiveDownloads);
        Assert.Equal(@"D:\x", s.DownloadFolder);
        Assert.True(s.StartWithWindows);
        Assert.False(s.Notifications);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    [InlineData(1, 1)]
    [InlineData(16, 16)]
    [InlineData(32, 32)]
    [InlineData(33, 32)]
    public void Clamped_Connections_ZeroIsAuto(int saved, int expected) =>
        Assert.Equal(expected, new AppSettings { ConnectionsPerFile = saved }.Clamped().ConnectionsPerFile);

    [Fact]
    public void ToEngineOptions_DefaultIsAuto() =>
        Assert.Equal(0, new AppSettings().ToEngineOptions(Path.GetTempPath()).ConnectionsPerFile);

    [Fact]
    public void Settings_Corrupt_Defaults()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ not json");
        Assert.Equal(new AppSettings(), new SettingsStore(path).Load());
    }

    [Fact]
    public void ToEngineOptions_EmptyFolder_UsesSystemDownloads()
    {
        var o = new AppSettings { ConnectionsPerFile = 8 }.ToEngineOptions(_dir);
        Assert.Equal(_dir, o.StateFolder);
        Assert.Equal(8, o.ConnectionsPerFile);
        Assert.False(string.IsNullOrWhiteSpace(o.DownloadFolder));
    }
}
