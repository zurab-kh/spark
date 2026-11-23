using System.Collections.ObjectModel;
using System.Windows;
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
        CopyUrlCommand = new RelayCommand<DownloadItemViewModel>(CopyUrl);
        RemoveCommand = new AsyncRelayCommand<DownloadItemViewModel>(i => RemoveAsync(i, false));
        RemoveWithFileCommand = new AsyncRelayCommand<DownloadItemViewModel>(RemoveWithFileAsync);
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

    /// <summary>Текст из буфера обмена для «Обновить ссылку».</summary>
    public Func<string?> GetClipboardText { get; set; } = DefaultClipboardText;

    /// <summary>Подтверждение удаления с файлом. Единственное модальное окно в программе.</summary>
    public Func<string, bool> ConfirmDelete { get; set; } = DefaultConfirmDelete;

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

    private void AddOne(DownloadRequest request)
    {
        var id = _manager.Add(request);
        if (_byId.TryGetValue(id, out var item) && item.IsCompleted)
            AlreadyDownloaded?.Invoke(item);
    }

    /// <summary>Каждая строка — абсолютный http/https адрес. Возвращает число добавленных; 0 → подсказка.</summary>
    public int AddLinks(string? text)
    {
        var added = 0;
        foreach (var line in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!LinkParser.TryParseHttp(line, out var url)) continue;
            AddOne(new DownloadRequest(url));
            added++;
        }
        if (added > 0)
        {
            InputHint = null;
            Refresh();
        }
        else
        {
            InputHint = "Не найдено ссылок";
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
            AddOne(request);
            added++;
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

    private void CopyUrl(DownloadItemViewModel? item)
    {
        if (item is null) return;
        try { Clipboard.SetText(item.Url); }
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
        if (!ConfirmDelete($"Удалить «{item.FileName}» вместе с файлом на диске?")) return;
        await RemoveAsync(item, true);
    }

    private async Task UpdateUrlAsync(DownloadItemViewModel? item)
    {
        if (item is null) return;
        string? text;
        try { text = GetClipboardText(); }
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

    private static string? DefaultClipboardText()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
        catch { return null; }
    }

    private static bool DefaultConfirmDelete(string message) =>
        MessageBox.Show(message, "Spark", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
}
