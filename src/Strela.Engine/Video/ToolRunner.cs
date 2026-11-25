using System.Diagnostics;
using System.Text;

namespace Strela.Engine.Video;

/// <summary>Запускает программу, отдаёт её stdout и stderr построчно; отмена убивает всё дерево процессов. Возвращает код выхода.</summary>
internal delegate Task<int> ToolRunner(string exe, IReadOnlyList<string> args, Action<string> onStdout, Action<string> onStderr,
                                       CancellationToken ct);

internal static class ProcessRunner
{
    public static async Task<int> RunAsync(string exe, IReadOnlyList<string> args, Action<string> onStdout, Action<string> onStderr,
                                           CancellationToken ct)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        // Обычным программам на Python; yt-dlp.exe (PyInstaller) эти переменные не читает — ему нужен --encoding utf-8.
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        info.Environment["PYTHONUTF8"] = "1";

        using var process = new Process { StartInfo = info };
        process.Start();
        var stdout = PumpAsync(process.StandardOutput, onStdout);
        var stderr = PumpAsync(process.StandardError, onStderr);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // уже завершился
            }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        return process.ExitCode;
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> onLine)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            onLine(line);
    }
}
