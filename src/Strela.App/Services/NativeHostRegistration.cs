using System.Text.Json;
using Microsoft.Win32;
using Strela.Engine;

namespace Strela.App.Services;

/// <summary>Запись в реестр (HKCU) — отдельная абстракция, чтобы тесты не трогали настоящий реестр.</summary>
public interface IRegistryWriter
{
    /// <summary>Создаёт ключ HKCU\subKey и пишет значение по умолчанию.</summary>
    void SetDefaultValue(string subKey, string value);
}

public sealed class CurrentUserRegistryWriter : IRegistryWriter
{
    public void SetDefaultValue(string subKey, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(subKey);
        key.SetValue("", value);
    }
}

/// <summary>Регистрирует Spark как native messaging host для Chrome и Edge.</summary>
public static class NativeHostRegistration
{
    public const string HostName = "com.spark.downloader";

    /// <summary>ID расширения из browser-extension/manifest.json (поле key); проверяется тестом.</summary>
    public const string ExtensionId = "meffaihaokjhicodaannmgedjflgbcgd";

    public static readonly string[] RegistryKeys =
    {
        @"Software\Google\Chrome\NativeMessagingHosts\" + HostName,
        @"Software\Microsoft\Edge\NativeMessagingHosts\" + HostName,
    };

    public static string BuildManifest(string exePath) =>
        JsonSerializer.Serialize(new
        {
            name = HostName,
            description = "Spark download manager",
            path = exePath,
            type = "stdio",
            allowed_origins = new[] { $"chrome-extension://{ExtensionId}/" },
        }, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>
    /// Пишет манифест рядом с exe (если папка недоступна для записи — в fallbackFolder) и ключи реестра.
    /// Возвращает путь манифеста или null. Не бросает: Spark должен запускаться и без интеграции с браузером.
    /// </summary>
    public static string? Register(string exePath, IRegistryWriter registry, IStrelaLog log, string? fallbackFolder = null)
    {
        try
        {
            var manifest = BuildManifest(exePath);
            var manifestPath = WriteManifest(exePath, manifest, fallbackFolder, log);
            if (manifestPath is null) return null;
            foreach (var key in RegistryKeys)
            {
                try { registry.SetDefaultValue(key, manifestPath); }
                catch (Exception ex) { log.Warn($"Native host registry key {key} not written: {ex.Message}"); }
            }
            return manifestPath;
        }
        catch (Exception ex)
        {
            log.Error("Не удалось зарегистрировать native host", ex);
            return null;
        }
    }

    private static string? WriteManifest(string exePath, string manifest, string? fallbackFolder, IStrelaLog log)
    {
        var folders = new List<string> { Path.GetDirectoryName(exePath) ?? "" };
        if (!string.IsNullOrEmpty(fallbackFolder)) folders.Add(fallbackFolder);
        foreach (var folder in folders)
        {
            try
            {
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, HostName + ".json");
                if (!File.Exists(path) || File.ReadAllText(path) != manifest) File.WriteAllText(path, manifest);
                return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Warn($"Native host manifest not written to {folder}: {ex.Message}");
            }
        }
        return null;
    }
}
