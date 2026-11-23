namespace Strela.App.Services;

/// <summary>Связь между экземплярами программы: второй запуск передаёт ссылки первому.</summary>
public interface ISingleInstanceChannel : IDisposable
{
    /// <summary>Отправляет ссылки работающему экземпляру. Пустой список = «покажи окно». false — никто не ответил.</summary>
    Task<bool> SendAsync(IReadOnlyList<string> urls);

    /// <summary>Пришли ссылки от другого экземпляра (поток не UI).</summary>
    event Action<IReadOnlyList<string>>? Received;

    void Start();
}
