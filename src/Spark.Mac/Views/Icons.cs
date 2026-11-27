using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Strela.App.Views;

/// <summary>Значки (24×24) — вместо Segoe Fluent Icons, которых нет на macOS.</summary>
public static class Icons
{
    public static readonly Geometry Download = Geometry.Parse("M5 20h14v-2H5v2zM19 9h-4V3H9v6H5l7 7 7-7z");
    public static readonly Geometry Pause = Geometry.Parse("M6 5h4v14H6zM14 5h4v14h-4z");
    public static readonly Geometry Play = Geometry.Parse("M8 5v14l11-7z");
    public static readonly Geometry Check = Geometry.Parse("M9 16.17L4.83 12l-1.42 1.41L9 19 21 7l-1.41-1.41z");
    public static readonly Geometry Warning = Geometry.Parse("M1 21h22L12 2 1 21zm12-3h-2v-2h2v2zm0-4h-2v-4h2v4z");
    public static readonly Geometry Folder = Geometry.Parse("M10 4H4c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2h-8l-2-2z");
    public static readonly Geometry Open = Geometry.Parse(
        "M19 19H5V5h7V3H5a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14c1.1 0 2-.9 2-2v-7h-2v7zM14 3v2h3.59l-9.83 9.83 1.41 1.41L19 6.41V10h2V3h-7z");
    public static readonly Geometry Close = Geometry.Parse(
        "M19 6.41L17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z");
    public static readonly Geometry Search = Geometry.Parse(
        "M15.5 14h-.79l-.28-.27A6.47 6.47 0 0 0 16 9.5 6.5 6.5 0 1 0 9.5 16c1.61 0 3.09-.59 4.23-1.57l.27.28v.79l5 4.99L20.49 19l-4.99-5zm-6 0C7.01 14 5 11.99 5 9.5S7.01 5 9.5 5 14 7.01 14 9.5 11.99 14 9.5 14z");
    public static readonly Geometry Gear = Geometry.Parse(
        "M19.14 12.94c.04-.3.06-.61.06-.94 0-.32-.02-.64-.07-.94l2.03-1.58a.49.49 0 0 0 .12-.61l-1.92-3.32a.488.488 0 0 0-.59-.22l-2.39.96c-.5-.38-1.03-.7-1.62-.94l-.36-2.54a.484.484 0 0 0-.48-.41h-3.84c-.24 0-.43.17-.47.41l-.36 2.54c-.59.24-1.13.57-1.62.94l-2.39-.96c-.22-.08-.47 0-.59.22L2.74 8.87c-.12.21-.08.47.12.61l2.03 1.58c-.05.3-.09.63-.09.94s.02.64.07.94l-2.03 1.58a.49.49 0 0 0-.12.61l1.92 3.32c.12.22.37.29.59.22l2.39-.96c.5.38 1.03.7 1.62.94l.36 2.54c.05.24.24.41.48.41h3.84c.24 0 .44-.17.47-.41l.36-2.54c.59-.24 1.13-.56 1.62-.94l2.39.96c.22.08.47 0 .59-.22l1.92-3.32c.12-.22.07-.47-.12-.61l-2.01-1.58zM12 15.6c-1.98 0-3.6-1.62-3.6-3.6s1.62-3.6 3.6-3.6 3.6 1.62 3.6 3.6-1.62 3.6-3.6 3.6z");
}

/// <summary>Цвет значка типа файла по <c>DownloadItemViewModel.BadgeKind</c> — те же цвета, что в Windows-версии.</summary>
public static class Converters
{
    private static readonly Dictionary<string, (Color Bg, Color Fg)> Badge = new()
    {
        ["archive"] = (Color.Parse("#26F59E0B"), Color.Parse("#F59E0B")),
        ["disk"] = (Color.Parse("#263B82F6"), Color.Parse("#60A5FA")),
        ["app"] = (Color.Parse("#2622C55E"), Color.Parse("#22C55E")),
        ["media"] = (Color.Parse("#26A855F7"), Color.Parse("#C084FC")),
        ["document"] = (Color.Parse("#2614B8A6"), Color.Parse("#2DD4BF")),
        ["torrent"] = (Color.Parse("#26EC4899"), Color.Parse("#F472B6")),
        ["other"] = (Color.Parse("#269CA3AF"), Color.Parse("#9CA3AF")),
    };

    private static (Color Bg, Color Fg) For(string? kind) => Badge.TryGetValue(kind ?? "", out var c) ? c : Badge["other"];

    public static readonly IValueConverter BadgeBackground =
        new FuncValueConverter<string?, IBrush>(k => new SolidColorBrush(For(k).Bg));

    public static readonly IValueConverter BadgeForeground =
        new FuncValueConverter<string?, IBrush>(k => new SolidColorBrush(For(k).Fg));
}
