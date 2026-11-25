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
        MoveUpCommand = new RelayCommand<DownloadItemViewModel>(i => MoveBy(i, -1));
        MoveDownCommand = new RelayCommand<DownloadItemViewModel>(i => MoveBy(i, +1));
        UpdateUrlCommand = new AsyncRelayCommand<DownloadItemViewModel>(UpdateUrlAsync);
    }

    public ObservableCollection<DownloadItemViewModel> Items { get; } = new();

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
    public IRelayCommand<DownloadItemViewModel> MoveUpCommand { get; }
    public IRelayCommand<DownloadItemViewModel> MoveDownCommand { get; }
    public IAsyncRelayCommand<DownloadItemViewModel> UpdateUrlCommand { get; }
    public IAsyncRelayCommand<DownloadItemViewModel> RemoveWithFileCommand { get; }
    /// <summary>Параметр — выделенные строки списка (ListView.SelectedItems).</summary>
    public IAsyncRelayCommand<IEnumerable> RemoveSelectedCommand { get; }
    public IAsyncRelayCommand<IEnumerable> RemoveSelectedWithFileCommand { get; }

    public bool Contains(string url) => _byId.Values.Any(i => string.Equals(i.Url, url, StringComparison.OrdinalIgnoreCase));

    /// <summary>Обновляет список на месте: существующие записи правятся, новые добавляются, лишние удаляются; порядок — по QueueOrder.</summary>
    public void Refresh()
    {
        var snaps = _manager.GetSnapshots();

        var known = 0;
        foreach (var s in snaps)
            if (_byId.ContainsKey(s.Id)) known++;
        if (known != _byId.Count)   // в списке есть записи, которых больше нет в движке
        {
            var alive = new HashSet<Guid>(snaps.Select(s => s.Id));
            for (var i = Items.Count - 1; i >= 0; i--)
            {
                if (alive.Contains(Items[i].Id)) continue;
                _byId.Remove(Items[i].Id);
                Items.RemoveAt(i);
            }
        }

        double speed = 0;
        int active = 0, queued = 0;
        for (var i = 0; i < snaps.Count; i++)
        {
            var s = snaps[i];
            if (!_byId.TryGetValue(s.Id, out var vm))
            {
                vm = new DownloadItemViewModel(s.Id);
                _byId[s.Id] = vm;
                vm.Update(s);
                Items.Insert(Math.Min(i, Items.Count), vm);
            }
            else
            {
                vm.Update(s);
                if (i >= Items.Count || !ReferenceEquals(Items[i], vm))
                    Items.Move(Items.IndexOf(vm), Math.Min(i, Items.Count - 1));
            }

            if (s.Status == DownloadStatus.Downloading) { active++; speed += s.BytesPerSecond; }
            else if (s.Status == DownloadStatus.Queued) queued++;
        }

        TotalSpeed = speed;
        SpeedTotalText = speed > 0 ? Format.Speed(speed) : "0 Б/с";
        ActiveText = $"Активно {active}";
        QueuedText = $"В очереди {queued}";
        TotalsText = $"Всего: {SpeedTotalText} · {ActiveText} · {QueuedText}";
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
            Refresh();
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
            Refresh();
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

    private void MoveBy(DownloadItemViewModel? item, int delta)
    {
        if (item is null) return;
        var index = Items.IndexOf(item);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Items.Count) return;
        _manager.Move(item.Id, target);
        Refresh();
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
