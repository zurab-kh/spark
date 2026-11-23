using System.Runtime.InteropServices;
using Microsoft.Win32;
using Strela.Engine;

namespace Strela.App.Services;

/// <summary>Чтение, запись и удаление ключей HKCU — отдельная абстракция, чтобы тесты не трогали настоящий реестр.</summary>
public interface IRegistryEditor
{
    /// <summary>Создаёт ключ HKCU\subKey и пишет значение (name "" — значение по умолчанию).</summary>
    void SetValue(string subKey, string name, string value);

    /// <summary>Строковое значение или null, если ключа или значения нет.</summary>
    string? GetValue(string subKey, string name);

    void DeleteValue(string subKey, string name);

    /// <summary>Удаляет ключ со всеми подключами; ключа нет — ничего не делает.</summary>
    void DeleteKeyTree(string subKey);
}

public sealed class CurrentUserRegistryEditor : IRegistryEditor
{
    public void SetValue(string subKey, string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(subKey);
        key.SetValue(name, value);
    }

    public string? GetValue(string subKey, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey);
        return key?.GetValue(name) as string;
    }

    public void DeleteValue(string subKey, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    public void DeleteKeyTree(string subKey) => Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
}

/// <summary>
/// Spark как обработчик magnet-ссылок и .torrent-файлов для текущего пользователя (HKCU\Software\Classes):
/// протокол <c>magnet:</c> и ProgID <c>Spark.Torrent</c> для <c>.torrent</c> → <c>"Spark.exe" "%1"</c>.
/// Снятие удаляет только то, что принадлежит Spark: чужой обработчик magnet или .torrent остаётся.
/// </summary>
public static class TorrentAssociation
{
    public const string ProgId = "Spark.Torrent";
    public const string MagnetKey = @"Software\Classes\magnet";
    public const string ExtensionKey = @"Software\Classes\.torrent";
    public const string ProgIdKey = @"Software\Classes\" + ProgId;

    public static string Command(string exePath) => $"\"{exePath}\" \"%1\"";

    /// <summary>Пишет ключи (повторный вызов ничего не меняет). Не бросает.</summary>
    public static bool Register(string exePath, IRegistryEditor registry, IStrelaLog log)
    {
        try
        {
            var command = Command(exePath);
            var icon = $"\"{exePath}\",0";

            registry.SetValue(MagnetKey, "", "URL:Magnet Link");
            registry.SetValue(MagnetKey, "URL Protocol", "");
            registry.SetValue(MagnetKey + @"\DefaultIcon", "", icon);
            registry.SetValue(MagnetKey + @"\shell", "", "open");
            registry.SetValue(MagnetKey + @"\shell\open\command", "", command);

            registry.SetValue(ProgIdKey, "", "Торрент-файл");
            registry.SetValue(ProgIdKey + @"\DefaultIcon", "", icon);
            registry.SetValue(ProgIdKey + @"\shell", "", "open");
            registry.SetValue(ProgIdKey + @"\shell\open\command", "", command);

            registry.SetValue(ExtensionKey, "", ProgId);
            registry.SetValue(ExtensionKey, "Content Type", "application/x-bittorrent");
            registry.SetValue(ExtensionKey + @"\OpenWithProgids", ProgId, "");
            return true;
        }
        catch (Exception ex)
        {
            log.Error("Не удалось зарегистрировать Spark для magnet и .torrent", ex);
            return false;
        }
    }

    /// <summary>Удаляет ключи Spark; magnet и .torrent другого приложения не трогает. Не бросает.</summary>
    public static bool Unregister(IRegistryEditor registry, IStrelaLog log)
    {
        try
        {
            var magnet = registry.GetValue(MagnetKey + @"\shell\open\command", "");
            if (magnet is not null && IsSpark(magnet))
                registry.DeleteKeyTree(MagnetKey);

            if (string.Equals(registry.GetValue(ExtensionKey, ""), ProgId, StringComparison.OrdinalIgnoreCase))
                registry.DeleteValue(ExtensionKey, "");
            registry.DeleteValue(ExtensionKey + @"\OpenWithProgids", ProgId);
            registry.DeleteKeyTree(ProgIdKey);
            return true;
        }
        catch (Exception ex)
        {
            log.Error("Не удалось снять регистрацию magnet и .torrent", ex);
            return false;
        }
    }

    /// <summary>Регистрирует или снимает регистрацию и сообщает оболочке об изменении ассоциаций.</summary>
    public static void Apply(bool enabled, string exePath, IRegistryEditor registry, IStrelaLog log)
    {
        var changed = enabled ? Register(exePath, registry, log) : Unregister(registry, log);
        if (changed && registry is CurrentUserRegistryEditor)
            NotifyShell();
    }

    private static bool IsSpark(string command) =>
        command.Contains("Spark.exe", StringComparison.OrdinalIgnoreCase);

    private const int ShcneAssocChanged = 0x08000000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    private static void NotifyShell()
    {
        try
        {
            SHChangeNotify(ShcneAssocChanged, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception)
        {
            // only a refresh hint for Explorer
        }
    }
}
