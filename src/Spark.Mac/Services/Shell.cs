using System.Diagnostics;

namespace Strela.App.Services;

/// <summary>Finder и программы по умолчанию через /usr/bin/open.</summary>
public static class Shell
{
    /// <summary>Открывает файл программой по умолчанию; папку (многофайловый торрент) — в Finder.</summary>
    public static bool OpenFile(string? path)
    {
        if (path is null || !(File.Exists(path) || Directory.Exists(path))) return false;
        return Open(path);
    }

    /// <summary>Показывает файл в Finder (выделенным); если его нет — открывает папку.</summary>
    public static bool RevealInFolder(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (File.Exists(path) || Directory.Exists(path)) return Open("-R", path);
        var dir = Path.GetDirectoryName(path);
        return dir is not null && Directory.Exists(dir) && Open(dir);
    }

    private static bool Open(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/open");
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var _ = Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
