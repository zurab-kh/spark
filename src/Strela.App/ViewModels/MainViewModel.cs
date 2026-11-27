using System.Collections;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Strela.App.Services;
using Strela.Engine;
using Strela.Engine.Integration;
using Strela.Engine.Manager;

namespace Strela.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IDownloadManager _manager;
    private readonly Dictionary<Guid, DownloadItemViewModel> _byId = new();
    private IReadOnlyList<DownloadSnapshot> _queue = [];   // все загрузки по QueueOrder на момент последнего Refresh
    private string _searchText = "";
    private string[] _searchWords = [];
    private bool _hasDownloads;
    private bool _nothingFound;
    private string _inputText = "";
    private string? _inputHint;
    private string _totalsText = "";
    private string _speedTotalText = "0 Б/с";
    private string _activeText = "Активно 0";
    private string _queuedText = "В очереди 0";

    public MainViewModel(IDownloadManager manager)
    {
        _manager = manager;
        AddFromInputCommand = new RelayCommand(AddFromInput);
        PauseCommand = new RelayCommand<DownloadItemViewModel>(i => { if (i is not null) { _manager.Pause(i.Id); Refresh(); } });
        ResumeCommand = new RelayCommand<DownloadItemViewModel>(i => { if (i is not null) { _manager.Resume(i.Id); Refresh(); } });
        RetryCommand = new RelayCommand<DownloadItemViewModel>(i => { if (i is not null) { _manager.Retry(i.Id); Refresh(); } });
        OpenFileCommand = new RelayCommand<DownloadItemViewModel>(OpenFile);
        OpenFolderCommand = new RelayCommand<DownloadItemViewModel>(i => { if (i is not null) Shell.RevealInFolder(i.FilePath); });
        CopyUrlCommand = new AsyncRelayCommand<DownloadItemViewModel>(CopyUrlAsync);
        RemoveCommand = new AsyncRelayCommand<DownloadItemViewModel>(i => RemoveAsync(i, false));
        RemoveWithFileCommand = new AsyncRelayCommand<DownloadItemViewModel>(RemoveWithFileAsync);
        RemoveSelectedCommand = new AsyncRelayCommand<IEnumerable>(s => RemoveSelectedAsync(s, deleteFile: false));
        RemoveSelectedWithFileCommand = new AsyncRelayCommand<IEnumerable>(s => RemoveSelectedAsync(s, deleteFile: true));
        StartEarlierCommand = new RelayCommand<DownloadItemViewModel>(i => MoveInQueue(i, -1));
        StartLaterCommand = new RelayCommand<DownloadItemViewModel>(i => MoveInQueue(i, +1));
        UpdateUrlCommand = new AsyncRelayCommand<DownloadItemViewModel>(UpdateUrlAsync);
    }

    /// <summary>Видимые загрузки: подходящие под поиск, новые сверху.</summary>
    public ObservableCollection<DownloadItemViewModel> Items { get; } = new();

    /// <summary>Быстрый поиск: каждое слово должно найтись в имени файла или в ссылке (без учёта регистра).</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? "")) return;
            _searchWords = _searchText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            Refresh();
        }
    }

    /// <summary>В программе есть хоть одна загрузка (видимая или скрытая поиском).</summary>
    public bool HasDownloads { get => _hasDownloads; private set => SetProperty(ref _hasDownloads, value); }

    /// <summary>Загрузки есть, но поиск не нашёл ни одной.</summary>
    public bool NothingFound { get => _nothingFound; private set => SetProperty(ref _nothingFound, value); }

    public string InputText
    {
        get => _inputText;
        set
        {
            if (SetProperty(ref _inputText, value)) InputHint = null;
        }
    }

    public string? InputHint { get => _inputHint; private set => SetProperty(ref _inputHint, value); }
    public string TotalsText { get => _totalsText; private set => SetProperty(ref _totalsText, value); }
    // Части строки состояния по отдельности — для раскладки с цветными точками.
    public string SpeedTotalText { get => _speedTotalText; private set => SetProperty(ref _speedTotalText, value); }
    public string ActiveText { get => _activeText; private set => SetProperty(ref _activeText, value); }
    public string QueuedText { get => _queuedText; private set => SetProperty(ref _queuedText, value); }

    /// <summary>Суммарная скорость активных загрузок, байт/с (для подсказки трея).</summary>
    public double TotalSpeed { get; private set; }

    // Буфер обмена и окно подтверждения — свои у каждой платформы (MainViewModel.Windows.cs / .Mac.cs).

    /// <summary>Текст из буфера обмена для «Обновить ссылку».</summary>
    public Func<Task<string?>> GetClipboardText { get; set; } = DefaultClipboardTextAsync;

    /// <summary>Запись в буфер обмена («Копировать ссылку»).</summary>
    public Func<string, Task> SetClipboardText { get; set; } = DefaultSetClipboardTextAsync;

    /// <summary>Подтверждение удаления с файлом. Единственное модальное окно в программе.</summary>
    public Func<string, Task<bool>> ConfirmDelete { get; set; } = DefaultConfirmDeleteAsync;

    public IRelayCommand AddFromInputCommand { get; }
    public IRelayCommand<DownloadItemViewModel> PauseCommand { get; }
    public IRelayCommand<DownloadItemViewModel> ResumeCommand { get; }
    public IRelayCommand<DownloadItemViewModel> RetryCommand { get; }
    public IRelayCommand<DownloadItemViewModel> OpenFileCommand { get; }
    public IRelayCommand<DownloadItemViewModel> OpenFolderCommand { get; }
    public IRelayCommand<DownloadItemViewModel> CopyUrlCommand { get; }
    public IRelayCommand<DownloadItemViewModel> RemoveCommand { get; }
    /// <summary>Ожидающая загрузка начнётся раньше / позже соседней ожидающей. Место в списке не меняется.</summary>
    public IRelayCommand<DownloadItemViewModel> StartEarlierCommand { get; }
    public IRelayCommand<DownloadItemViewModel> StartLaterCommand { get; }
    public IAsyncRelayCommand<DownloadItemViewModel> UpdateUrlCommand { get; }
    public IAsyncRelayCommand<DownloadItemViewModel> RemoveWithFileCommand { get; }
    /// <summary>Параметр — выделенные строки списка (ListView.SelectedItems).</summary>
    public IAsyncRelayCommand<IEnumerable> RemoveSelectedCommand { get; }
    public IAsyncRelayCommand<IEnumerable> RemoveSelectedWithFileCommand { get; }

    public bool Contains(string url) => _byId.Values.Any(i => string.Equals(i.Url, url, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Обновляет список на месте: существующие записи правятся, новые добавляются, лишние (удалённые или не подходящие
    /// под поиск) убираются. Порядок — новые сверху (AddedAt, при равенстве — позже в очереди выше).
    /// </summary>
    public void Refresh()
    {
        var snaps = _manager.GetSnapshots();
        if (snaps.Count == 0 && _searchText.Length > 0)
        {
            // Поле поиска прячется вместе с последней загрузкой: оставшийся текст незаметно скрывал бы новые.
            ShowAll();
            return;
        }
        _queue = snaps;

        double speed = 0;
        int active = 0, queued = 0;
        foreach (var s in snaps)
        {
            if (s.Status == DownloadStatus.Downloading) { active++; speed += s.BytesPerSecond; }
            else if (s.Status == DownloadStatus.Queued) queued++;

            if (!_byId.TryGetValue(s.Id, out var vm))
                _byId[s.Id] = vm = new DownloadItemViewModel(s.Id);
            vm.Update(s, queuePosition: queued);   // snaps идут по QueueOrder: queued — номер этой среди ожидающих
        }
        if (_byId.Count != snaps.Count)   // в словаре есть записи, которых больше нет в движке
        {
            var alive = new HashSet<Guid>(snaps.Select(s => s.Id));
            foreach (var id in _byId.Keys.Where(id => !alive.Contains(id)).ToList())
                _byId.Remove(id);
        }

        var visible = snaps.Where(Matches)
            .OrderByDescending(s => s.AddedAt).ThenByDescending(s => s.QueueOrder)
            .Select(s => _byId[s.Id]).ToList();
        SyncItems(visible);
        HasDownloads = snaps.Count > 0;
        NothingFound = HasDownloads && visible.Count == 0;

        TotalSpeed = speed;
        SpeedTotalText = speed > 0 ? Format.Speed(speed) : "0 Б/с";
        ActiveText = $"Активно {active}";
        QueuedText = $"В очереди {queued}";
        TotalsText = $"Всего: {SpeedTotalText} · {ActiveText} · {QueuedText}";
    }

    /// <summary>Сбрасывает поиск (с Refresh); без поиска — просто Refresh. Новая загрузка не должна прятаться за фильтром.</summary>
    private void ShowAll()
    {
        if (_searchText.Length > 0) SearchText = "";
        else Refresh();
    }

    private bool Matches(DownloadSnapshot s)
    {
        foreach (var word in _searchWords)
        {
            if (!s.FileName.Contains(word, StringComparison.OrdinalIgnoreCase)
                && !s.Url.Contains(word, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    /// <summary>Приводит Items к <paramref name="visible"/> минимумом изменений: выделение и прокрутка списка сохраняются.</summary>
    private void SyncItems(List<DownloadItemViewModel> visible)
    {
        // После шага i начало Items совпадает с visible[0..i]; всё лишнее оказывается в хвосте.
        for (var i = 0; i < visible.Count; i++)
        {
            var vm = visible[i];
            if (i < Items.Count && ReferenceEquals(Items[i], vm)) continue;
            var at = Items.IndexOf(vm);
            if (at < 0) Items.Insert(i, vm);
            else Items.Move(at, i);
        }
        while (Items.Count > visible.Count)   // удалённые и скрытые поиском
            Items.RemoveAt(Items.Count - 1);
    }

    /// <summary>Ссылку уже скачали, файл на месте — новая загрузка не создана.</summary>
    public event Action<DownloadItemViewModel>? AlreadyDownloaded;

    /// <summary>false — движок не принял запрос (битый .torrent-файл, неверная magnet-ссылка).</summary>
    private bool AddOne(DownloadRequest request)
    {
        Guid id;
        try
        {
            id = _manager.Add(request);
        }
        catch (ArgumentException)
        {
            return false;
        }
        if (_byId.TryGetValue(id, out var item) && item.IsCompleted)
            AlreadyDownloaded?.Invoke(item);
        return true;
    }

    /// <summary>
    /// Каждая строка — http/https адрес, magnet-ссылка или путь к .torrent-файлу. Возвращает число добавленных;
    /// 0 → подсказка.
    /// </summary>
    public int AddLinks(string? text)
    {
        int added = 0, rejected = 0;
        foreach (var line in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!LinkParser.TryParseLink(line, out var url)) continue;
            if (AddOne(new DownloadRequest(url))) added++;
            else rejected++;
        }
        if (added > 0)
        {
            InputHint = null;
            ShowAll();
        }
        else
        {
            InputHint = rejected > 0 ? "Не удалось открыть торрент" : "Не найдено ссылок";
        }
        return added;
    }

    /// <summary>Сообщения от расширения браузера или второго экземпляра: Referer, заголовки, cookies и имя файла сохраняются.</summary>
    public int AddRequests(IEnumerable<IpcMessage> messages)
    {
        var added = 0;
        foreach (var message in messages)
        {
            if (message.ToRequest() is not { } request) continue;
            if (AddOne(request)) added++;
        }
        if (added > 0)
        {
            InputHint = null;
            ShowAll();
        }
        return added;
    }

    private void AddFromInput()
    {
        if (AddLinks(InputText) > 0) InputText = "";
    }

    private void OpenFile(DownloadItemViewModel? item)
    {
        if (item is null) return;
        if (!Shell.OpenFile(item.FilePath)) InputHint = "Файл не найден";
    }

    private async Task CopyUrlAsync(DownloadItemViewModel? item)
    {
        if (item is null) return;
        try { await SetClipboardText(item.Url); }
        catch { InputHint = "Не удалось скопировать: буфер обмена занят"; }
    }

    /// <summary>
    /// Ставит ожидающую загрузку в очереди на место ближайшей ожидающей перед ней (<paramref name="delta"/> = −1) или
    /// после неё (+1). Идущие, на паузе и готовые загрузки не трогаются; список остаётся «новые сверху».
    /// </summary>
    private void MoveInQueue(DownloadItemViewModel? item, int delta)
    {
        if (item is null) return;
        var at = -1;
        for (var i = 0; i < _queue.Count; i++)
            if (_queue[i].Id == item.Id) { at = i; break; }
        if (at < 0 || _queue[at].Status != DownloadStatus.Queued) return;
        for (var i = at + delta; i >= 0 && i < _queue.Count; i += delta)
        {
            if (_queue[i].Status != DownloadStatus.Queued) continue;
            _manager.Move(item.Id, i);
            Refresh();
            return;
        }
    }

    private async Task RemoveAsync(DownloadItemViewModel? item, bool deleteFile)
    {
        if (item is null) return;
        await _manager.RemoveAsync(item.Id, deleteFile);
        Refresh();
    }

    private async Task RemoveWithFileAsync(DownloadItemViewModel? item)
    {
        if (item is null) return;
        await RemoveSelectedAsync(new[] { item }, deleteFile: true);
    }

    /// <summary>Удаляет выделенные загрузки; с файлами — после одного подтверждения на все сразу.</summary>
    private async Task RemoveSelectedAsync(IEnumerable? selected, bool deleteFile)
    {
        var items = selected?.OfType<DownloadItemViewModel>().ToList() ?? new();
        if (items.Count == 0) return;
        if (deleteFile)
        {
            var question = items.Count == 1
                ? $"Удалить «{items[0].FileName}» вместе с файлом на диске?"
                : $"Удалить {items.Count} {Plural(items.Count, "загрузку", "загрузки", "загрузок")} вместе с файлами на диске?";
            if (!await ConfirmDelete(question)) return;
        }
        foreach (var item in items)
            await _manager.RemoveAsync(item.Id, deleteFile);
        Refresh();
    }

    private static string Plural(int n, string one, string few, string many) =>
        (n % 100) is >= 11 and <= 14 ? many : (n % 10) switch { 1 => one, >= 2 and <= 4 => few, _ => many };

    private async Task UpdateUrlAsync(DownloadItemViewModel? item)
    {
        if (item is null) return;
        string? text;
        try { text = await GetClipboardText(); }
        catch { text = null; }
        if (!LinkParser.TryParseHttp(FirstLine(text), out var url))
        {
            InputHint = "Скопируйте новую ссылку в буфер обмена";
            return;
        }
        InputHint = null;
        await _manager.UpdateUrlAsync(item.Id, url);
        Refresh();
    }

    private static string? FirstLine(string? text) =>
        text?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
}
