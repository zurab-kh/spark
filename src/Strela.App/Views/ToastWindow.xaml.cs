using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Strela.App.Views;

/// <summary>Немодальное уведомление в правом нижнем углу рабочей области; гаснет через 8 с, фокус не забирает.</summary>
public partial class ToastWindow : Window
{
    private readonly Action _onClick;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(8) };

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
            ActionButton.Visibility = Visibility.Visible;
        }
        else
        {
            MouseLeftButtonUp += (_, _) => Fire();
            Cursor = Cursors.Hand;
        }
        _timer.Tick += (_, _) => Close();
        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
        SizeChanged += (_, _) => PlaceBottomRight();
    }

    private void PlaceBottomRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 16;
        Top = area.Bottom - ActualHeight - 16;
    }

    private void ActionButton_Click(object sender, RoutedEventArgs e) => Fire();

    private void Fire()
    {
        Close();
        _onClick();
    }
}
