using Strela.Engine.Integration;
using Strela.Engine.Manager;
using Strela.Engine.Storage;
using Strela.Engine.Torrents;

namespace Strela.Engine.Tests;

public sealed class TorrentLinksTests : IDisposable
{
    private const string V1 = "magnet:?xt=urn:btih:c12fe1c06bba254a9dc9f519b335aa7c1367a88a&dn=Ubuntu+24.04&tr=udp%3A%2F%2Ftracker.example%3A80";
    private const string V2 = "magnet:?xt=urn:btmh:1220caf1e1c30e81cb361b9ee167c4aa64228a7fa4fa9f6105232b28ad099f3a302e&dn=v2";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "strela-tests", "links-" + Guid.NewGuid().ToString("N"));

    public TorrentLinksTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData(V1, true)]
    [InlineData(V2, true)]
    [InlineData("  " + V1 + "  ", true)]
    [InlineData("magnet:?xt=urn:btih:C12FE1C06BBA254A9DC9F519B335AA7C1367A88A", true)]
    [InlineData("magnet:?xt=urn:btih:YSX6DQDLXISKVHOJ6UM3GNNKPQJWPKEK", true)]   // base32
    [InlineData("magnet:?dn=no-hash", false)]
    [InlineData("magnet:?xt=urn:btih:nothex", false)]
    [InlineData("https://example.com/a.torrent", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsMagnet(string? text, bool expected) => Assert.Equal(expected, TorrentLinks.IsMagnet(text));

    [Theory]
    [InlineData("https://example.com/files/linux.torrent", null, true)]
    [InlineData("http://example.com/LINUX.TORRENT?x=1", null, true)]
    [InlineData("https://example.com/download.php?id=5", "linux.torrent", true)]
    [InlineData("https://example.com/download.php?id=5", "linux.zip", false)]
    [InlineData("https://example.com/a.torrent.zip", null, false)]
    [InlineData("ftp://example.com/a.torrent", null, false)]
    public void IsTorrentUrl(string url, string? fileName, bool expected) =>
        Assert.Equal(expected, TorrentLinks.IsTorrentUrl(new Uri(url), fileName));

    [Fact]
    public void IsTorrentFile_OnlyExistingAbsoluteTorrentPaths()
    {
        var file = Path.Combine(_dir, "a b.torrent");
        File.WriteAllText(file, "x");
        var other = Path.Combine(_dir, "a.zip");
        File.WriteAllText(other, "x");

        Assert.True(TorrentLinks.IsTorrentFile(file, out var path));
        Assert.Equal(file, path);
        Assert.True(TorrentLinks.IsTorrentFile($"\"{file}\"", out _));                  // quoted, as from "%1"
        Assert.True(TorrentLinks.IsTorrentFile(new Uri(file).AbsoluteUri, out var fromUri));   // file:///…
        Assert.Equal(file, fromUri);
        Assert.False(TorrentLinks.IsTorrentFile(other, out _));
        Assert.False(TorrentLinks.IsTorrentFile(Path.Combine(_dir, "missing.torrent"), out _));
        Assert.False(TorrentLinks.IsTorrentFile("relative.torrent", out _));
    }

    [Fact]
    public void IsTorrentRequest_RoutesMagnetFileAndLinks()
    {
        Assert.True(TorrentLinks.IsTorrentRequest(new DownloadRequest(new Uri(V1))));
        Assert.True(TorrentLinks.IsTorrentRequest(new DownloadRequest(new Uri(Path.Combine(_dir, "x.torrent")))));
        Assert.True(TorrentLinks.IsTorrentRequest(new DownloadRequest(new Uri("https://ex.com/x.torrent"))));
        Assert.True(TorrentLinks.IsTorrentRequest(new DownloadRequest(new Uri("https://ex.com/get?id=1"), FileName: "x.torrent")));
        Assert.False(TorrentLinks.IsTorrentRequest(new DownloadRequest(new Uri("https://ex.com/x.zip"))));
    }

    [Fact]
    public void Magnet_SurvivesUriRoundTrip()
    {
        Assert.True(TorrentLinks.TryParse(V1, out var uri));
        Assert.Equal("magnet", uri.Scheme);
        Assert.Equal(V1, uri.OriginalString);
    }

    [Fact]
    public void Ipc_ToRequest_AcceptsMagnetAndExistingTorrentFiles_Only()
    {
        var file = Path.Combine(_dir, "t.torrent");
        File.WriteAllText(file, "x");
        var other = Path.Combine(_dir, "evil.exe");
        File.WriteAllText(other, "x");

        Assert.Equal(V1, new IpcMessage(V1).ToRequest()!.Url.OriginalString);
        var local = new IpcMessage(file).ToRequest()!;
        Assert.True(local.Url.IsFile);
        Assert.Equal(file, local.Url.LocalPath);
        Assert.Null(new IpcMessage(other).ToRequest());
        Assert.Null(new IpcMessage(new Uri(other).AbsoluteUri).ToRequest());
        Assert.Null(new IpcMessage(Path.Combine(_dir, "gone.torrent")).ToRequest());
        Assert.Null(new IpcMessage("magnet:?dn=no-hash").ToRequest());
        Assert.Null(new IpcMessage("javascript:alert(1)").ToRequest());
        Assert.NotNull(new IpcMessage("https://ex.com/a.zip").ToRequest());
    }

    [Fact]
    public void State_WithoutKind_LoadsAsHttp()
    {
        // A state file written before torrent support.
        var id = Guid.NewGuid();
        var json = $$"""
            {"Id":"{{id}}","OriginalUrl":"https://ex.com/a.zip","CurrentUrl":"https://ex.com/a.zip","Folder":"C:\\dl",
             "FileName":"a.zip","TotalBytes":100,"SupportsRanges":true,"Segments":[{"Start":0,"End":100,"Position":40}],
             "Status":"Paused","QueueOrder":3,"AddedAt":"2026-11-01T10:00:00+00:00"}
            """;
        File.WriteAllText(Path.Combine(_dir, $"{id:D}.json"), json);

        var state = new StateStore(_dir).LoadAll(NullLog.Instance).Single();
        Assert.Equal(DownloadKind.Http, state.Kind);
        Assert.Null(state.Magnet);
        Assert.Null(state.InfoHash);
        Assert.Equal(40, state.Segments.Single().Position);
        Assert.Equal(DownloadStatus.Paused, state.Status);
    }

    [Fact]
    public void TorrentState_RoundTrips()
    {
        var store = new StateStore(_dir);
        var state = new DownloadState
        {
            Id = Guid.NewGuid(),
            Kind = DownloadKind.Torrent,
            OriginalUrl = V1,
            CurrentUrl = V1,
            Magnet = V1,
            InfoHash = "C12FE1C06BBA254A9DC9F519B335AA7C1367A88A",
            TorrentFilePath = @"C:\state\torrents\x.torrent",
            BytesDone = 12345,
            Status = DownloadStatus.Downloading,
        };
        store.Save(state);

        var json = File.ReadAllText(Path.Combine(_dir, $"{state.Id:D}.json"));
        Assert.Contains("\"Kind\":\"Torrent\"", json, StringComparison.Ordinal);
        var loaded = store.LoadAll(NullLog.Instance).Single();
        Assert.Equal(DownloadKind.Torrent, loaded.Kind);
        Assert.Equal(V1, loaded.Magnet);
        Assert.Equal(state.InfoHash, loaded.InfoHash);
        Assert.Equal(state.TorrentFilePath, loaded.TorrentFilePath);
        Assert.Equal(12345, loaded.BytesDone);
    }
}
