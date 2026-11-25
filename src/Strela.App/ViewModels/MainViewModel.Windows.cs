using System.Windows;

namespace Strela.App.ViewModels;

public sealed partial class MainViewModel
{
    private static Task<string?> DefaultClipboardTextAsync()
    {
        try { return Task.FromResult(Clipboard.ContainsText() ? Clipboard.GetText() : null); }
        catch { return Task.FromResult<string?>(null); }
    }

    private static Task DefaultSetClipboardTextAsync(string text)
    {
        Clipboard.SetText(text);
        return Task.CompletedTask;
    }

    private static Task<bool> DefaultConfirmDeleteAsync(string message) =>
        Task.FromResult(MessageBox.Show(message, "Spark", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
                        == MessageBoxResult.Yes);
}
