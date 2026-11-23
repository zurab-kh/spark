using System.Text.Json;
using System.Text.Json.Serialization;

namespace Strela.Engine.Storage;

/// <summary>
/// Хранит по одному JSON-файлу на загрузку. Запись атомарна: сначала <c>{id}.json.tmp</c>, затем замена файла,
/// поэтому после сбоя остаётся либо старая, либо новая полная версия.
/// Save для разных id можно вызывать параллельно; для одного id вызовы сериализует вызывающий.
/// </summary>
public sealed class StateStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _folder;

    public StateStore(string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(folder);
    }

    public void Save(DownloadState s)
    {
        var final = PathFor(s.Id);
        var tmp = final + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(s, Json);

        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        File.Move(tmp, final, overwrite: true);
    }

    public IReadOnlyList<DownloadState> LoadAll(IStrelaLog log)
    {
        // Остатки прерванного Save: предыдущий .json при этом остаётся целым.
        foreach (var tmp in Directory.EnumerateFiles(_folder, "*.json.tmp"))
        {
            try { File.Delete(tmp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.Warn($"state: cannot delete {Path.GetFileName(tmp)}: {ex.Message}"); }
        }

        var result = new List<DownloadState>();
        foreach (var file in Directory.EnumerateFiles(_folder, "*.json"))
        {
            try
            {
                var state = JsonSerializer.Deserialize<DownloadState>(File.ReadAllBytes(file), Json)
                    ?? throw new JsonException("empty document");
                result.Add(state);
            }
            catch (Exception ex)
            {
                log.Warn($"state: skip corrupt {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        return result;
    }

    public void Delete(Guid id) => File.Delete(PathFor(id));

    private string PathFor(Guid id) => Path.Combine(_folder, $"{id:D}.json");
}
