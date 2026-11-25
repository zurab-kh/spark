using System.Diagnostics;
using Strela.Engine;
using Strela.Engine.Manager;

// Замер скорости движка на настоящем адресе: Strela.Bench <url> [соединений: 0 = авто] [повторов]
// Файл пишется во временную папку и удаляется; печатает время, среднюю скорость, пик соединений и CPU.
if (args.Length < 1 || !Uri.TryCreate(args[0], UriKind.Absolute, out var url))
{
    Console.WriteLine("Strela.Bench <url> [connections, 0 = auto] [runs]");
    return 1;
}
var connections = args.Length > 1 ? int.Parse(args[1]) : 0;
var runs = args.Length > 2 ? int.Parse(args[2]) : 1;
var verbose = Environment.GetEnvironmentVariable("BENCH_LOG") == "1";
var clockAll = Stopwatch.StartNew();
Console.OutputEncoding = System.Text.Encoding.UTF8;

for (var run = 1; run <= runs; run++)
{
    var root = Path.Combine(Path.GetTempPath(), "strela-bench-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var options = new EngineOptions
        {
            DownloadFolder = Path.Combine(root, "files"),
            StateFolder = Path.Combine(root, "state"),
            ConnectionsPerFile = connections,
        };
        await using var manager = new DownloadManager(options, verbose ? new ConsoleLog(clockAll) : NullLog.Instance);
        await manager.InitializeAsync();

        var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var clock = Stopwatch.StartNew();
        var id = manager.Add(new DownloadRequest(url));
        var peak = 0;
        double peakSpeed = 0;
        DownloadSnapshot snap;
        while (true)
        {
            await Task.Delay(250);
            snap = manager.GetSnapshots().First(s => s.Id == id);
            peak = Math.Max(peak, snap.Connections);
            peakSpeed = Math.Max(peakSpeed, snap.BytesPerSecond);
            if (snap.Status is DownloadStatus.Completed or DownloadStatus.Error)
                break;
        }
        clock.Stop();
        process.Refresh();
        var cpu = process.TotalProcessorTime - cpuBefore;
        var mb = snap.DownloadedBytes / 1e6;
        Console.WriteLine(
            $"run {run}: {snap.Status} {mb:F0} MB in {clock.Elapsed.TotalSeconds:F1} s = {mb / clock.Elapsed.TotalSeconds:F1} MB/s " +
            $"(peak {peakSpeed / 1e6:F1} MB/s), peak connections {peak}, CPU {cpu.TotalSeconds:F1} s {snap.StatusText}");
    }
    finally
    {
        try { Directory.Delete(root, true); } catch (IOException) { }
    }
}
return 0;

internal sealed class ConsoleLog(Stopwatch clock) : IStrelaLog
{
    public void Info(string m) => Write("I", m);
    public void Warn(string m) => Write("W", m);
    public void Error(string m, Exception? ex = null) => Write("E", ex is null ? m : $"{m}: {ex}");
    private void Write(string level, string m) => Console.WriteLine($"{clock.Elapsed.TotalSeconds,7:F2} {level} {m}");
}
