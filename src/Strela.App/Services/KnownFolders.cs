using System.Runtime.InteropServices;

namespace Strela.App.Services;

public static class KnownFolders
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    /// <summary>Системная папка «Загрузки» (FOLDERID_Downloads); запасной вариант — %USERPROFILE%\Downloads.</summary>
    public static string Downloads()
    {
        try
        {
            if (SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out var p) == 0)
            {
                try
                {
                    var s = Marshal.PtrToStringUni(p);
                    if (!string.IsNullOrEmpty(s)) return s;
                }
                finally { Marshal.FreeCoTaskMem(p); }
            }
        }
        catch { /* fall through */ }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    public static string AppData() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Strela");
}
