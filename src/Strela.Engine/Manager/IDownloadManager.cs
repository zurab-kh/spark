namespace Strela.Engine.Manager;

public interface IDownloadManager : IAsyncDisposable
{
    Task InitializeAsync();                  // загрузить состояния; Downloading/Queued → Queued (автопродолжение); Paused остаётся
    Guid Add(DownloadRequest request);       // тот же URL уже Queued/Downloading/Paused → вернуть его Id, не дублировать
    void Pause(Guid id);
    void Resume(Guid id);
    void Retry(Guid id);                     // Resume/Retry → Queued
    void PauseAll();
    void ResumeAll();
    Task RemoveAsync(Guid id, bool deleteFile);   // deleteFile: .strela и/или готовый файл
    Task<bool> UpdateUrlAsync(Guid id, Uri newUrl); // разведка: размер совпал → сохранить прогресс, сменить CurrentUrl; иначе начать с нуля; → Queued
    void Move(Guid id, int newIndex);        // перенумеровать QueueOrder
    void ApplyOptions(EngineOptions options);// новые лимиты действуют для следующих стартов; MaxActive сразу
    IReadOnlyList<DownloadSnapshot> GetSnapshots(); // по QueueOrder; заодно обновляет SpeedMeter-ы; не блокирует
    bool HasActiveWork { get; }              // есть Downloading или Queued
    event Action<DownloadSnapshot>? Completed;
}
