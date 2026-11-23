using Strela.Engine.Storage;

namespace Strela.Engine.Tests;

public sealed class PartFileTests : IDisposable
{
    private const long MB = 1L << 20;

    private readonly string _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "strela-tests", Guid.NewGuid().ToString("N"));

    public PartFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string P(string name) => System.IO.Path.Combine(_dir, name);

    [Fact]
    public void OpenOrCreate_Preallocates()
    {
        var path = P("a.bin.strela");

        using (var part = PartFile.OpenOrCreate(path, 10 * MB))
        {
            Assert.Equal(path, part.Path);
            Assert.Equal(10 * MB, new FileInfo(path).Length);
        }
    }

    [Fact]
    public void OpenOrCreate_UnknownSize_DoesNotPreallocate_AndWritesExtend()
    {
        var path = P("a.bin.strela");

        using (var part = PartFile.OpenOrCreate(path, null))
        {
            Assert.Equal(0, new FileInfo(path).Length);
            part.Write(3, new byte[] { 1, 2, 3 });
            part.FlushToDisk();
            Assert.Equal(6, new FileInfo(path).Length);
        }
    }

    [Fact]
    public async Task ParallelWrites_ProduceExactContent()
    {
        var data = TestData.Random((int)(8 * MB), seed: 42);
        var path = P("a.bin.strela");
        const int workers = 8;
        var chunk = data.Length / workers;

        using (var part = PartFile.OpenOrCreate(path, data.Length))
        {
            await Task.WhenAll(Enumerable.Range(0, workers).Select(i => Task.Run(() =>
            {
                var start = i * chunk;
                // Small pieces so the workers interleave.
                for (var pos = 0; pos < chunk; pos += 64 * 1024)
                    part.Write(start + pos, data.AsSpan(start + pos, Math.Min(64 * 1024, chunk - pos)));
            })));
            part.FlushToDisk();
        }

        Assert.Equal(TestData.Sha256(data), TestData.Sha256(path));
    }

    [Fact]
    public void Complete_RenamesAndAvoidsCollision()
    {
        var existing = P("x.bin");
        File.WriteAllText(existing, "old");
        var partPath = P("x.bin.strela");

        var part = PartFile.OpenOrCreate(partPath, 4);
        part.Write(0, new byte[] { 1, 2, 3, 4 });
        var result = part.Complete(P("x.bin"), _ => false);

        Assert.Equal(P("x (1).bin"), result);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(result));
        Assert.Equal("old", File.ReadAllText(existing));
        Assert.False(File.Exists(partPath));
    }

    [Fact]
    public void Complete_FreeName_RenamesToExactPath()
    {
        var partPath = P("x.bin.strela");

        var part = PartFile.OpenOrCreate(partPath, 2);
        part.Write(0, new byte[] { 9, 8 });
        var result = part.Complete(P("x.bin"), _ => false);

        Assert.Equal(P("x.bin"), result);
        Assert.Equal(new byte[] { 9, 8 }, File.ReadAllBytes(result));
        Assert.False(File.Exists(partPath));
    }

    [Fact]
    public void Complete_NameReservedByAnotherJob_PicksNext()
    {
        var part = PartFile.OpenOrCreate(P("x.bin.strela"), 1);

        var result = part.Complete(P("x.bin"), p => p == P("x.bin"));

        Assert.Equal(P("x (1).bin"), result);
        Assert.True(File.Exists(result));
    }

    [Fact]
    public void Reopen_KeepsExistingData()
    {
        var data = TestData.Random(100_000, seed: 7);
        var path = P("a.bin.strela");

        using (var part = PartFile.OpenOrCreate(path, data.Length))
        {
            part.Write(0, data);
            part.FlushToDisk();
        }

        using (var part = PartFile.OpenOrCreate(path, data.Length))
        {
            Assert.Equal(data.Length, new FileInfo(path).Length);
        }

        Assert.Equal(TestData.Sha256(data), TestData.Sha256(path));
    }

    [Fact]
    public void Reopen_WithDifferentSize_Resizes()
    {
        var path = P("a.bin.strela");
        using (PartFile.OpenOrCreate(path, 100)) { }

        using (PartFile.OpenOrCreate(path, 250))
            Assert.Equal(250, new FileInfo(path).Length);
    }

    [Fact]
    public void Truncate_EmptiesFile()
    {
        var path = P("a.bin.strela");

        using var part = PartFile.OpenOrCreate(path, 1000);
        part.Truncate();

        Assert.Equal(0, new FileInfo(path).Length);
    }

    [Fact]
    public void Write_AfterDispose_Throws()
    {
        var part = PartFile.OpenOrCreate(P("a.bin.strela"), 10);
        part.Dispose();

        Assert.Throws<ObjectDisposedException>(() => part.Write(0, new byte[] { 1 }));
    }

    [Fact]
    public void Write_AfterComplete_Throws()
    {
        var part = PartFile.OpenOrCreate(P("a.bin.strela"), 10);
        part.Complete(P("a.bin"), _ => false);

        Assert.Throws<ObjectDisposedException>(() => part.Write(0, new byte[] { 1 }));
    }

    [Fact]
    public void Delete_RemovesFile_AndIgnoresMissing()
    {
        var path = P("a.bin.strela");
        File.WriteAllText(path, "x");

        PartFile.Delete(path);
        PartFile.Delete(path);
        PartFile.Delete(System.IO.Path.Combine(_dir, "no-such-dir", "b.strela"));

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void OpenOrCreate_WhileOpen_ForbidsOtherWriters()
    {
        var path = P("a.bin.strela");
        using var part = PartFile.OpenOrCreate(path, 10);

        Assert.Throws<IOException>(() => new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
    }
}
