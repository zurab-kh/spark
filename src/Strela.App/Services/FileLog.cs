using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Strela.Engine;

namespace Strela.App.Services;

/// <summary>Лог в файл: запись в фоне через Channel, ротация strela.log → strela.1.log … strela.3.log по 5 МБ.</summary>
public sealed class FileLog : IStrelaLog, IDisposable
{
    private const long MaxBytes = 5L * 1024 * 1024;
    private const int Keep = 3;

    private readonly string _folder;
    private readonly string _path;
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writer;

    public FileLog(string folder)
    {
        _folder = folder;
        _path = Path.Combine(folder, "strela.log");
        Directory.CreateDirectory(folder);
        _writer = Task.Factory.StartNew(WriteLoop, TaskCreationOptions.LongRunning).Unwrap();
    }

    public void Info(string m) => Enqueue("INF", m);
    public void Warn(string m) => Enqueue("WRN", m);
    public void Error(string m, Exception? ex = null) => Enqueue("ERR", ex is null ? m : m + Environment.NewLine + ex);

    private void Enqueue(string level, string m) =>
        _queue.Writer.TryWrite(string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {m}{Environment.NewLine}"));

    private async Task WriteLoop()
    {
        FileStream? fs = null;
        try
        {
            await foreach (var line in _queue.Reader.ReadAllAsync())
            {
                try
                {
                    fs ??= Open();
                    if (fs.Length >= MaxBytes)
                    {
                        fs.Dispose();
                        fs = null;
                        Rotate();
                        fs = Open();
                    }
                    var bytes = Encoding.UTF8.GetBytes(line);
                    fs.Write(bytes, 0, bytes.Length);
                    if (!_queue.Reader.TryPeek(out _)) fs.Flush();
                }
                catch (IOException)
                {
                    fs?.Dispose();   // лог не должен валить программу
                    fs = null;
                }
                catch (UnauthorizedAccessException)
                {
                    fs?.Dispose();
                    fs = null;
                }
            }
        }
        finally
        {
            fs?.Dispose();
        }
    }

    private FileStream Open() => new(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

    private void Rotate()
    {
        File.Delete(Numbered(Keep));
        for (var i = Keep - 1; i >= 1; i--)
            if (File.Exists(Numbered(i))) File.Move(Numbered(i), Numbered(i + 1));
        File.Move(_path, Numbered(1));
    }

    private string Numbered(int n) => Path.Combine(_folder, $"strela.{n}.log");

    /// <summary>Дописывает очередь и закрывает файл.</summary>
    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _writer.Wait(TimeSpan.FromSeconds(5));
    }
}
