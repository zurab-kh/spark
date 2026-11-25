namespace Strela.App.Services;

public static class KnownFolders
{
    /// <summary>~/Downloads.</summary>
    public static string Downloads() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>~/Library/Application Support/Spark: загрузки, настройки, журнал, копия расширения браузера.</summary>
    public static string AppData() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Spark");
}
