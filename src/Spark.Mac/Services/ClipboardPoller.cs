using Avalonia.Input.Platform;
using Avalonia.Threading;

namespace Strela.App.Services;

/// <summary>
/// macOS не сообщает об изменении буфера обмена — опрашиваем раз в секунду. Текст, лежавший в буфере
/// при запуске, не сообщается; тот же текст повторно — тоже.
/// </summary>
public sealed class ClipboardPoller : IDisposable
{
    private readonly Func<IClipboard?> _clipboard;
    private readonly DispatcherTimer _timer;
    private string? _last;
    private bool _primed;
    private bool _busy;

    public ClipboardPoller(Func<IClipboard?> clipboard)
    {
        _clipboard = clipboard;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => _ = PollAsync());
        _timer.Start();
    }

    /// <summary>В буфер попал новый текст (UI-поток).</summary>
    public event Action<string>? TextCopied;

    private async Task PollAsync()
    {
        if (_busy || _clipboard() is not { } clipboard) return;
        _busy = true;
        try
        {
            var text = await clipboard.TryGetTextAsync();
            if (string.IsNullOrWhiteSpace(text) || text == _last) return;
            _last = text;
            if (_primed) TextCopied?.Invoke(text);
        }
        catch
        {
            // буфер занят другой программой — попробуем через секунду
        }
        finally
        {
            _primed = true;
            _busy = false;
        }
    }

    public void Dispose() => _timer.Stop();
}
