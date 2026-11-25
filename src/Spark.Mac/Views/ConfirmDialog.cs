using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Strela.App.Views;

/// <summary>Подтверждение удаления с файлом — единственное модальное окно в программе.</summary>
public static class ConfirmDialog
{
    public static async Task<bool> AskAsync(Window owner, string message, string confirmText)
    {
        var result = false;
        var dialog = new Window
        {
            Title = "Spark",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var confirm = new Button
        {
            Content = confirmText,
            Padding = new Thickness(20, 6),
            Background = new SolidColorBrush(Color.Parse("#E5484D")),
            Foreground = Brushes.White,
        };
        var cancel = new Button { Content = "Отмена", Padding = new Thickness(20, 6), IsDefault = true, IsCancel = true };
        confirm.Click += (_, _) => { result = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 14 },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, confirm },
                },
            },
        };
        await dialog.ShowDialog(owner);
        return result;
    }
}
