using Microsoft.Win32.SafeHandles;
using Strela.Engine.Probe;

namespace Strela.Engine.Storage;

/// <summary>
/// Temporary <c>.strela</c> file shared by all workers of one download.
/// Writes are positional (<see cref="RandomAccess"/>), so they are safe from many threads for non-overlapping ranges.
/// </summary>
public sealed class PartFile : IDisposable
{
    private const int ErrorHandleDiskFull = 0x27;
    private const int ErrorDiskFull = 0x70;
    private const string DiskFullMessage = "Нет места на диске";

    private readonly FileStream _stream;
    private readonly SafeFileHandle _handle;
    private volatile bool _disposed;

    private PartFile(string path, FileStream stream)
    {
        Path = path;
        _stream = stream;
        _handle = stream.SafeFileHandle;
    }

    public string Path { get; }

    public static PartFile OpenOrCreate(string partPath, long? totalBytes)
    {
        var stream = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
            bufferSize: 0, FileOptions.RandomAccess);
        try
        {
            if (totalBytes is { } total && stream.Length != total)
                GuardDiskFull(() => stream.SetLength(total));
            return new PartFile(partPath, stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void Write(long offset, ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            RandomAccess.Write(_handle, data, offset);
        }
        catch (IOException ex) when (IsDiskFull(ex))
        {
            throw new DiskFullException(DiskFullMessage, ex);
        }
    }

    public void FlushToDisk()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _stream.Flush(true);
    }

    /// <summary>Restart from scratch.</summary>
    public void Truncate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _stream.SetLength(0);
    }

    /// <summary>Flushes, closes and renames to <paramref name="finalPath"/> (or a unique sibling if that name is taken).</summary>
    public string Complete(string finalPath, Func<string, bool> isTaken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _stream.Flush(true);
        Dispose();

        // ReserveUniquePath also treats finalPath + ".strela" as taken, which may be this very part file,
        // so it is consulted only when finalPath itself is actually occupied.
        var chosen = File.Exists(finalPath) || isTaken(finalPath)
            ? FileNameResolver.ReserveUniquePath(
                System.IO.Path.GetDirectoryName(finalPath)!, System.IO.Path.GetFileName(finalPath), isTaken)
            : finalPath;

        File.Move(Path, chosen);
        return chosen;
    }

    public static void Delete(string partPath)
    {
        try
        {
            File.Delete(partPath);
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _stream.Dispose();
    }

    private static bool IsDiskFull(IOException ex) => (ex.HResult & 0xFFFF) is ErrorDiskFull or ErrorHandleDiskFull;

    private static void GuardDiskFull(Action action)
    {
        try
        {
            action();
        }
        catch (IOException ex) when (IsDiskFull(ex))
        {
            throw new DiskFullException(DiskFullMessage, ex);
        }
    }
}
