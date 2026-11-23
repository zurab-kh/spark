using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Strela.App.Services;

/// <summary>Слушает изменения буфера обмена (AddClipboardFormatListener на message-only окне).</summary>
public sealed class ClipboardWatcher : IDisposable
{
    private const int WmClipboardUpdate = 0x031D;
    private static readonly IntPtr HwndMessage = new(-3);
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(10);

    private readonly HwndSource _source;
    private string? _lastText;
    private DateTime _lastAt;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    public ClipboardWatcher()
    {
        _source = new HwndSource(new HwndSourceParameters("StrelaClipboard") { ParentWindow = HwndMessage, WindowStyle = 0 });
        _source.AddHook(WndProc);
        AddClipboardFormatListener(_source.Handle);
    }

    /// <summary>В буфер попал текст (сырой). Тот же текст в течение 10 с повторно не сообщается.</summary>
    public event Action<string>? TextCopied;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate)
        {
            handled = true;
            OnClipboardChanged();
        }
        return IntPtr.Zero;
    }

    private void OnClipboardChanged()
    {
        var text = ReadText();
        if (string.IsNullOrWhiteSpace(text)) return;
        var now = DateTime.UtcNow;
        if (text == _lastText && now - _lastAt < RepeatWindow) return;
        _lastText = text;
        _lastAt = now;
        TextCopied?.Invoke(text);
    }

    private static string? ReadText()
    {
        // Буфер может быть занят другим процессом — пара коротких попыток.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
            catch (COMException)
            {
                Thread.Sleep(20);
            }
            catch (ExternalException)
            {
                Thread.Sleep(20);
            }
        }
        return null;
    }

    public void Dispose()
    {
        RemoveClipboardFormatListener(_source.Handle);
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
