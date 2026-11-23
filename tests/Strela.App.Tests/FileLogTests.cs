using Strela.App.Services;

namespace Strela.App.Tests;

public sealed class FileLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "strela-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void FileLog_WritesLines()
    {
        using (var log = new FileLog(_dir))
        {
            log.Info("hello");
            log.Warn("careful");
            log.Error("bad", new InvalidOperationException("boom"));
        }
        var text = File.ReadAllText(Path.Combine(_dir, "strela.log"));
        Assert.Contains("hello", text);
        Assert.Contains("careful", text);
        Assert.Contains("boom", text);
    }

    [Fact]
    public void FileLog_RotatesAt5MB_Keeps3()
    {
        var line = new string('x', 1000);
        using (var log = new FileLog(_dir))
        {
            for (var i = 0; i < 16_000; i++) log.Info(line);
        }
        Assert.True(File.Exists(Path.Combine(_dir, "strela.log")));
        Assert.True(File.Exists(Path.Combine(_dir, "strela.1.log")));
        Assert.True(File.Exists(Path.Combine(_dir, "strela.2.log")));
        Assert.True(File.Exists(Path.Combine(_dir, "strela.3.log")));
        Assert.False(File.Exists(Path.Combine(_dir, "strela.4.log")));
    }
}
