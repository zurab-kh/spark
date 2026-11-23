using System.Security.Cryptography;
using System.Text.Json;
using Strela.App.Services;
using Strela.Engine;

namespace Strela.App.Tests;

public sealed class NativeHostRegistrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "spark-reg-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class FakeRegistry : IRegistryWriter
    {
        public Dictionary<string, string> Values { get; } = new();
        public Func<string, bool> Fails { get; init; } = _ => false;

        public void SetDefaultValue(string subKey, string value)
        {
            if (Fails(subKey)) throw new UnauthorizedAccessException("denied");
            Values[subKey] = value;
        }
    }

    [Fact]
    public void Register_WritesManifestNextToExe_WithPathAndOrigin()
    {
        var exe = Path.Combine(_root, "app", "Spark.exe");
        var registry = new FakeRegistry();

        var manifestPath = NativeHostRegistration.Register(exe, registry, NullLog.Instance);

        Assert.Equal(Path.Combine(_root, "app", "com.spark.downloader.json"), manifestPath);
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath!));
        var root = doc.RootElement;
        Assert.Equal("com.spark.downloader", root.GetProperty("name").GetString());
        Assert.Equal(exe, root.GetProperty("path").GetString());
        Assert.Equal("stdio", root.GetProperty("type").GetString());
        Assert.Equal(new[] { "chrome-extension://" + NativeHostRegistration.ExtensionId + "/" },
            root.GetProperty("allowed_origins").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public void Register_PointsChromeAndEdgeKeys_AtManifest()
    {
        var exe = Path.Combine(_root, "Spark.exe");
        var registry = new FakeRegistry();

        var manifestPath = NativeHostRegistration.Register(exe, registry, NullLog.Instance);

        Assert.Equal(manifestPath, registry.Values[@"Software\Google\Chrome\NativeMessagingHosts\com.spark.downloader"]);
        Assert.Equal(manifestPath, registry.Values[@"Software\Microsoft\Edge\NativeMessagingHosts\com.spark.downloader"]);
        Assert.Equal(2, registry.Values.Count);
    }

    [Fact]
    public void Register_IsIdempotent_AndRepointsWhenExeMoves()
    {
        var registry = new FakeRegistry();
        NativeHostRegistration.Register(Path.Combine(_root, "a", "Spark.exe"), registry, NullLog.Instance);
        NativeHostRegistration.Register(Path.Combine(_root, "a", "Spark.exe"), registry, NullLog.Instance);
        var moved = NativeHostRegistration.Register(Path.Combine(_root, "b", "Spark.exe"), registry, NullLog.Instance);

        Assert.Equal(moved, registry.Values[NativeHostRegistration.RegistryKeys[0]]);
        using var doc = JsonDocument.Parse(File.ReadAllText(moved!));
        Assert.Equal(Path.Combine(_root, "b", "Spark.exe"), doc.RootElement.GetProperty("path").GetString());
    }

    [Fact]
    public void Register_RegistryFailure_DoesNotThrow_AndOtherBrowserStillWritten()
    {
        var registry = new FakeRegistry { Fails = key => key.Contains("Chrome") };

        var manifestPath = NativeHostRegistration.Register(Path.Combine(_root, "Spark.exe"), registry, NullLog.Instance);

        Assert.NotNull(manifestPath);
        Assert.Single(registry.Values);
        Assert.Contains(registry.Values.Keys, k => k.Contains("Edge"));
    }

    [Fact]
    public void Register_UnwritableExeFolder_FallsBackToGivenFolder()
    {
        Directory.CreateDirectory(_root);
        var blocker = Path.Combine(_root, "file-not-folder");
        File.WriteAllText(blocker, "x");   // CreateDirectory под файлом невозможен
        var exe = Path.Combine(blocker, "Spark.exe");
        var fallback = Path.Combine(_root, "fallback");
        var registry = new FakeRegistry();

        var manifestPath = NativeHostRegistration.Register(exe, registry, NullLog.Instance, fallback);

        Assert.Equal(Path.Combine(fallback, "com.spark.downloader.json"), manifestPath);
        Assert.Equal(manifestPath, registry.Values[NativeHostRegistration.RegistryKeys[0]]);
    }

    [Fact]
    public void Register_NothingWritable_ReturnsNullWithoutThrowing()
    {
        Directory.CreateDirectory(_root);
        var blocker = Path.Combine(_root, "file-not-folder");
        File.WriteAllText(blocker, "x");

        Assert.Null(NativeHostRegistration.Register(Path.Combine(blocker, "Spark.exe"), new FakeRegistry(), NullLog.Instance));
    }

    [Fact]
    public void ExtensionId_MatchesKeyInExtensionManifest()
    {
        var manifest = FindExtensionManifest();
        using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
        var der = Convert.FromBase64String(doc.RootElement.GetProperty("key").GetString()!);
        var hash = SHA256.HashData(der);
        var id = string.Concat(hash.Take(16).SelectMany(b => new[] { (char)('a' + (b >> 4)), (char)('a' + (b & 15)) }));

        Assert.Equal(NativeHostRegistration.ExtensionId, id);
    }

    private static string FindExtensionManifest()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "browser-extension", "manifest.json");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("browser-extension/manifest.json not found above the test output folder.");
    }
}
