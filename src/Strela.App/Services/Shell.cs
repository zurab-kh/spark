using System.Diagnostics;

namespace Strela.App.Services;

public static class Shell
{
    public static bool OpenFile(string? path)
    {
        if (path is null || !File.Exists(path)) return false;
        return Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>Открывает папку; если файл есть — выделяет его.</summary>
    public static bool RevealInFolder(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (File.Exists(path))
            return Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select," + path } });
        var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        return dir is not null && Directory.Exists(dir)
            && Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { dir } });
    }

    private static bool Start(ProcessStartInfo psi)
    {
        try
        {
            using var _ = Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
