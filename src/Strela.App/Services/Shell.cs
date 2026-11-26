using System.Diagnostics;

namespace Strela.App.Services;

public static class Shell
{
    /// <summary>Открывает файл программой по умолчанию; папку (многофайловый торрент) — в Проводнике.</summary>
    public static bool OpenFile(string? path)
    {
        if (path is null) return false;
        if (Directory.Exists(path)) return Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path } });
        if (!File.Exists(path)) return false;
        return Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>Открывает папку; если файл (или папка загрузки) есть — выделяет его.</summary>
    public static bool RevealInFolder(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (File.Exists(path) || Directory.Exists(path))
            return Start(new ProcessStartInfo("explorer.exe", SelectArguments(path)));
        var dir = Path.GetDirectoryName(path);
        return dir is not null && Directory.Exists(dir)
            && Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { dir } });
    }

    /// <summary>
    /// Аргументы Проводника, выделяющие <paramref name="path"/>: в кавычках только путь. ArgumentList взял бы в кавычки
    /// весь "/select,путь с пробелами" — такой ключ Проводник не узнаёт и открывает «Документы».
    /// </summary>
    internal static string SelectArguments(string path) => $"/select,\"{path}\"";

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
