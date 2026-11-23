using System.Text.Json;

namespace Strela.App.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;

    public SettingsStore(string path) => _path = path;

    /// <summary>Нет файла или он битый → значения по умолчанию.</summary>
    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new AppSettings();
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Json) ?? new AppSettings();
            // До автоподбора по умолчанию стояло 16 соединений: такие файлы переводим на «Авто».
            if (s.SettingsVersion < 2 && s.ConnectionsPerFile == 16)
                s = s with { ConnectionsPerFile = 0 };
            return s.Clamped();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings s)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s.Clamped() with { SettingsVersion = AppSettings.CurrentSettingsVersion }, Json));
        File.Move(tmp, _path, overwrite: true);
    }
}
