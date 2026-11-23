using System.Buffers.Binary;
using System.Text;

namespace Strela.App.Services;

/// <summary>Кадры native messaging браузера: 4 байта длины (uint32 LE) и JSON в UTF-8.</summary>
public static class NativeMessagingFrames
{
    /// <summary>Браузер разрешает до 64 МиБ; нам нужны единицы килобайт, больше — признак мусора.</summary>
    public const int MaxFrameBytes = 4 * 1024 * 1024;

    /// <summary>Следующее сообщение или null, если поток закончился между кадрами.</summary>
    public static async Task<string?> ReadAsync(Stream input, CancellationToken ct = default)
    {
        var header = new byte[4];
        var first = await input.ReadAsync(header.AsMemory(0, 4), ct).ConfigureAwait(false);
        if (first == 0) return null;
        if (first < 4) await input.ReadExactlyAsync(header.AsMemory(first, 4 - first), ct).ConfigureAwait(false);

        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length > MaxFrameBytes) throw new InvalidDataException($"Native messaging frame too large: {length} bytes.");

        var body = new byte[length];
        await input.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(body);
    }

    public static async Task WriteAsync(Stream output, string json, CancellationToken ct = default)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length);
        body.CopyTo(frame, 4);
        await output.WriteAsync(frame, ct).ConfigureAwait(false);   // одним куском: кадр не должен разорваться
        await output.FlushAsync(ct).ConfigureAwait(false);
    }
}
