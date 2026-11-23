using System.Net.Http.Headers;
using Strela.Engine.Probe;

namespace Strela.Engine.Tests;

public class FileNameResolverTests
{
    [Theory]
    [InlineData("attachment; filename*=UTF-8''%D0%BE%D1%82%D1%87%D1%91%D1%82.pdf", "http://x/a", "отчёт.pdf")]
    [InlineData("attachment; filename=\"setup.exe\"", "http://x/a", "setup.exe")]
    [InlineData(null, "http://x/files/my%20file.zip?x=1", "my file.zip")]
    [InlineData(null, "http://x/download.php?id=5", "download.php")]
    [InlineData(null, "http://x/", "download")]
    public void Resolve_PicksBestName(string? cd, string url, string expected)
    {
        var header = cd is null ? null : ContentDispositionHeaderValue.Parse(cd);

        Assert.Equal(expected, FileNameResolver.Resolve(header, new Uri(url)));
    }

    [Theory]
    [InlineData("a<b>c.txt", "a_b_c.txt")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("name. . ", "name")]
    [InlineData("", "download")]
    public void Sanitize_ProducesValidWindowsName(string input, string expected)
    {
        Assert.Equal(expected, FileNameResolver.Sanitize(input));
    }

    [Fact]
    public void Sanitize_TruncatesLongNameKeepingExtension()
    {
        var result = FileNameResolver.Sanitize(new string('a', 300) + ".iso");

        Assert.Equal(200, result.Length);
        Assert.EndsWith(".iso", result);
    }

    [Fact]
    public void ReserveUniquePath_SkipsExistingFinalPartAndTaken()
    {
        var folder = Path.Combine(Path.GetTempPath(), "strela-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "f.bin"), "x");
            File.WriteAllText(Path.Combine(folder, "f (1).bin.strela"), "x");
            var taken = Path.Combine(folder, "f (2).bin");

            var result = FileNameResolver.ReserveUniquePath(folder, "f.bin", p => p == taken);

            Assert.Equal(Path.Combine(folder, "f (3).bin"), result);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
