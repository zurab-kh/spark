using System.Drawing;
using System.Windows.Forms;

namespace Strela.App.Services;

public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private string _tooltip = "";

    public TrayIcon(Action open, Action pauseAll, Action resumeAll, Action exit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => open());
        menu.Items.Add("Пауза всех", null, (_, _) => pauseAll());
        menu.Items.Add("Продолжить все", null, (_, _) => resumeAll());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => exit());

        _icon = new NotifyIcon
        {
            Icon = LoadSparkIcon(),
            Text = "Spark",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => open();
    }

    /// <summary>Логотип Spark из ресурсов, в размере значков трея; если ресурса нет — стандартный значок.</summary>
    private static Icon LoadSparkIcon()
    {
        var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Spark;component/Assets/spark.ico"));
        if (info is null) return SystemIcons.Application;
        using var stream = info.Stream;
        return new Icon(stream, SystemInformation.SmallIconSize);
    }

    /// <summary>Подсказка вида «Spark — 42 МБ/с»; без активной скорости — просто «Spark».</summary>
    public void SetSpeed(string speedText)
    {
        var text = string.IsNullOrEmpty(speedText) ? "Spark" : "Spark — " + speedText;
        if (text == _tooltip) return;
        _tooltip = text;
        _icon.Text = text.Length > 127 ? text[..127] : text;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}
