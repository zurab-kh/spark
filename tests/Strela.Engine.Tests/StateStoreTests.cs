using Strela.Engine.Manager;
using Strela.Engine.Storage;

namespace Strela.Engine.Tests;

public sealed class StateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "strela-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static DownloadState Sample(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        OriginalUrl = "https://example.com/a.bin",
        CurrentUrl = "https://cdn.example.com/a.bin?token=1",
        Referer = "https://example.com/",
        Headers = new Dictionary<string, string> { ["X-One"] = "1", ["X-Two"] = "два" },
        Cookies = "a=b; c=d",
        RequestedFileName = "wanted.bin",
        Folder = @"C:\Downloads",
        FileName = "a.bin",
        PartPath = @"C:\Downloads\a.bin.strela",
        FinalPath = @"C:\Downloads\a.bin",
        TotalBytes = 30_000_000,
        SupportsRanges = true,
        ETag = "\"abc\"",
        LastModified = new DateTimeOffset(2026, 5, 1, 12, 30, 45, TimeSpan.FromHours(3)),
        Segments =
        [
            new SegmentState(0, 9_999_999, 5_000_000),
            new SegmentState(10_000_000, 19_999_999, 10_000_000),
            new SegmentState(20_000_000, 29_999_999, 29_999_999),
        ],
        Status = DownloadStatus.Paused,
        Error = "boom",
        QueueOrder = 42,
        AddedAt = new DateTimeOffset(2026, 4, 1, 8, 0, 0, TimeSpan.Zero),
        CompletedAt = new DateTimeOffset(2026, 4, 1, 9, 15, 0, TimeSpan.FromHours(-5)),
    };

    [Fact]
    public void SaveLoad_RoundTrip()
    {
        var store = new StateStore(_dir);
        var original = Sample();

        store.Save(original);
        var loaded = Assert.Single(store.LoadAll(new ListLog()));

        Assert.Equal(original.Id, loaded.Id);
        Assert.Equal(original.OriginalUrl, loaded.OriginalUrl);
        Assert.Equal(original.CurrentUrl, loaded.CurrentUrl);
        Assert.Equal(original.Referer, loaded.Referer);
        Assert.Equal(original.Headers, loaded.Headers);
        Assert.Equal(original.Cookies, loaded.Cookies);
        Assert.Equal(original.RequestedFileName, loaded.RequestedFileName);
        Assert.Equal(original.Folder, loaded.Folder);
        Assert.Equal(original.FileName, loaded.FileName);
        Assert.Equal(original.PartPath, loaded.PartPath);
        Assert.Equal(original.FinalPath, loaded.FinalPath);
        Assert.Equal(original.TotalBytes, loaded.TotalBytes);
        Assert.Equal(original.SupportsRanges, loaded.SupportsRanges);
        Assert.Equal(original.ETag, loaded.ETag);
        Assert.Equal(original.LastModified, loaded.LastModified);
        Assert.Equal(original.LastModified!.Value.Offset, loaded.LastModified!.Value.Offset);
        Assert.Equal(3, loaded.Segments.Count);
        Assert.True(original.Segments.SequenceEqual(loaded.Segments));
        Assert.Equal(original.Status, loaded.Status);
        Assert.Equal(original.Error, loaded.Error);
        Assert.Equal(original.QueueOrder, loaded.QueueOrder);
        Assert.Equal(original.AddedAt, loaded.AddedAt);
        Assert.Equal(original.CompletedAt, loaded.CompletedAt);
    }

    [Fact]
    public void Save_Overwrites_And_Delete_Removes()
    {
        var store = new StateStore(_dir);
        var s = Sample();
        store.Save(s);
        s.Status = DownloadStatus.Completed;
        store.Save(s);

        Assert.Equal(DownloadStatus.Completed, Assert.Single(store.LoadAll(new ListLog())).Status);

        store.Delete(s.Id);
        Assert.Empty(store.LoadAll(new ListLog()));
        store.Delete(s.Id); // missing file is fine
    }

    [Fact]
    public void Save_IsAtomic_NoTmpLeft()
    {
        var store = new StateStore(_dir);
        for (var i = 0; i < 5; i++)
            store.Save(Sample());

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.Equal(5, Directory.GetFiles(_dir, "*.json").Length);
    }

    [Fact]
    public void LoadAll_SkipsCorruptFile()
    {
        var store = new StateStore(_dir);
        var good = Sample();
        store.Save(good);
        File.WriteAllText(Path.Combine(_dir, "bad.json"), "{ not json");
        File.WriteAllText(Path.Combine(_dir, "null.json"), "null");
        var log = new ListLog();

        var loaded = store.LoadAll(log);

        Assert.Equal(good.Id, Assert.Single(loaded).Id);
        Assert.True(log.Contains("Warn", "bad.json"));
        Assert.True(log.Contains("Warn", "null.json"));
    }

    [Fact]
    public void LoadAll_DeletesLeftoverTmpFiles()
    {
        var store = new StateStore(_dir);
        var good = Sample();
        store.Save(good);
        var tmp = Path.Combine(_dir, good.Id.ToString("D") + ".json.tmp");
        File.WriteAllText(tmp, "{ half");

        var loaded = store.LoadAll(new ListLog());

        Assert.Single(loaded);
        Assert.False(File.Exists(tmp));
    }

    [Fact]
    public void StatusIsWrittenAsString()
    {
        var store = new StateStore(_dir);
        var s = Sample();
        store.Save(s);

        Assert.Contains("\"Paused\"", File.ReadAllText(Path.Combine(_dir, s.Id.ToString("D") + ".json")));
    }

    [Fact]
    public void ToRequest_UsesCurrentUrlAndHeaders()
    {
        var s = Sample();

        var r = s.ToRequest();

        Assert.Equal(new Uri(s.CurrentUrl), r.Url);
        Assert.Equal(s.Referer, r.Referer);
        Assert.Equal(s.Headers, r.Headers);
        Assert.Equal(s.Cookies, r.Cookies);
        Assert.Equal(s.RequestedFileName, r.FileName);
    }

    [Fact]
    public async Task Save_DifferentIds_Concurrently()
    {
        var store = new StateStore(_dir);
        var states = Enumerable.Range(0, 20).Select(_ => Sample()).ToList();

        await Task.WhenAll(states.Select(s => Task.Run(() => { for (var i = 0; i < 10; i++) store.Save(s); })));

        Assert.Equal(20, store.LoadAll(new ListLog()).Count);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }
}
