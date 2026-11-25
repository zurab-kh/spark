using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Strela.App.Views;

/// <summary>Немодальное уведомление в правом верхнем углу экрана (как у macOS); гаснет через 8 с, фокус не забирает.</summary>
public partial class ToastWindow : Window
{
    private readonly Action _onClick;
    private readonly DispatcherTimer _timer;

    /// <summary>Только для дизайнера XAML.</summary>
    public ToastWindow() : this("", () => { }) { }

    /// <param name="message">Текст.</param>
    /// <param name="onClick">Действие по кнопке (если задан <paramref name="buttonText"/>) или по клику на уведомление.</param>
    public ToastWindow(string message, Action onClick, string? buttonText = null)
    {
        _onClick = onClick;
        InitializeComponent();
        MessageText.Text = message;
        if (buttonText is not null)
        {
            ActionButton.Content = buttonText;
            ActionButton.IsVisible = true;
        }
        else
        {
            PointerReleased += (_, _) => Fire();
            Cursor = new Cursor(StandardCursorType.Hand);
        }
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(8), DispatcherPriority.Normal, (_, _) => Close());
        Opened += (_, _) => { PlaceTopRight(); _timer.Start(); };
        Closed += (_, _) => _timer.Stop();
        SizeChanged += (_, _) => PlaceTopRight();
    }

    private void PlaceTopRight()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var width = (int)(Bounds.Width * scale);
        Position = new PixelPoint(area.Right - width - (int)(8 * scale), area.Y + (int)(8 * scale));
    }

    private void ActionButton_Click(object? sender, RoutedEventArgs e) => Fire();

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;   // не считать закрытие кликом по уведомлению
        Close();
    }

    private void Fire()
    {
        Close();
        _onClick();
    }
}
