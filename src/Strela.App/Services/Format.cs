using System.Globalization;

namespace Strela.App.Services;

/// <summary>Человекочитаемые размеры, скорость и время. 1 КБ = 1024; меньше 10 единиц — один знак после точки.</summary>
public static class Format
{
    private static readonly string[] Units = { "Б", "КБ", "МБ", "ГБ", "ТБ" };

    public static string Bytes(long? bytes)
    {
        if (bytes is null) return "—";
        return Scale(Math.Max(0, bytes.Value));
    }

    public static string Speed(double bps) => bps <= 0 ? "" : Scale(bps) + "/с";

    public static string Eta(TimeSpan? eta)
    {
        if (eta is null) return "";
        var t = eta.Value < TimeSpan.Zero ? TimeSpan.Zero : eta.Value;
        var totalHours = (long)t.TotalHours;
        return totalHours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{totalHours}:{t.Minutes:00}:{t.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{t.Minutes}:{t.Seconds:00}");
    }

    private static string Scale(double value)
    {
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        if (unit == 0) return string.Create(CultureInfo.InvariantCulture, $"{value:0} {Units[0]}");
        var text = value < 9.95
            ? value.ToString("0.0", CultureInfo.InvariantCulture)
            : value.ToString("0", CultureInfo.InvariantCulture);
        return text + " " + Units[unit];
    }
}
