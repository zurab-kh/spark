namespace Strela.App.ViewModels;

/// <summary>Без окна буфер обмена и диалог недоступны; App подставляет настоящие после создания окна.</summary>
public sealed partial class MainViewModel
{
    private static Task<string?> DefaultClipboardTextAsync() => Task.FromResult<string?>(null);

    private static Task DefaultSetClipboardTextAsync(string text) => Task.CompletedTask;

    private static Task<bool> DefaultConfirmDeleteAsync(string message) => Task.FromResult(false);
}
