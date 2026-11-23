using CommunityToolkit.Mvvm.ComponentModel;
using Strela.App.Services;
using Strela.Engine.Manager;

namespace Strela.App.ViewModels;

public sealed partial class DownloadItemViewModel : ObservableObject
{
    private string _fileName = "";
    private string _sizeText = "";
    private double _progress;
    private string _progressText = "";
    private string _speedText = "";
    private string _etaText = "";
    private DownloadStatus _status;
    private string? _filePath;
    private string _url = "";
    private string _detailText = "";
    private string _typeLabel = "";

    public DownloadItemViewModel(Guid id) => Id = id;

    public Guid Id { get; }
    public string FileName { get => _fileName; private set => SetProperty(ref _fileName, value); }
    public string SizeText { get => _sizeText; private set => SetProperty(ref _sizeText, value); }
    /// <summary>0..1</summary>
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }
    public string ProgressText { get => _progressText; private set => SetProperty(ref _progressText, value); }
    public string SpeedText { get => _speedText; private set => SetProperty(ref _speedText, value); }
    public string EtaText { get => _etaText; private set => SetProperty(ref _etaText, value); }
    public string? FilePath { get => _filePath; private set => SetProperty(ref _filePath, value); }
    public string Url { get => _url; private set => SetProperty(ref _url, value); }
    /// <summary>Строка под именем: «412 МБ из 607 МБ · 42 МБ/с · осталось 0:45», «Готово · 607 МБ», текст ошибки…</summary>
    public string DetailText { get => _detailText; private set => SetProperty(ref _detailText, value); }
    /// <summary>Короткая метка типа файла для значка: «ZIP», «ISO», «EXE»…</summary>
    public string TypeLabel { get => _typeLabel; private set => SetProperty(ref _typeLabel, value); }

    public DownloadStatus Status
    {
        get => _status;
        private set
        {
            if (!SetProperty(ref _status, value)) return;
            OnPropertyChanged(nameof(IsCompleted));
            OnPropertyChanged(nameof(CanPause));
            OnPropertyChanged(nameof(CanResume));
            OnPropertyChanged(nameof(CanRetry));
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(IsError));
            OnPropertyChanged(nameof(ShowProgressBar));
        }
    }

    public bool IsCompleted => Status == DownloadStatus.Completed;
    public bool IsActive => Status == DownloadStatus.Downloading;
    public bool IsError => Status == DownloadStatus.Error;
    public bool ShowProgressBar => Status is not (DownloadStatus.Completed or DownloadStatus.Error);
    public bool CanPause => Status is DownloadStatus.Downloading or DownloadStatus.Queued;
    public bool CanResume => Status == DownloadStatus.Paused;
    public bool CanRetry => Status == DownloadStatus.Error;

    /// <summary>Обновляет только изменившиеся свойства — PropertyChanged для остальных не поднимается.</summary>
    public void Update(DownloadSnapshot s)
    {
        FileName = s.FileName;
        Url = s.Url;
        FilePath = s.FilePath;
        Status = s.Status;
        SizeText = Format.Bytes(s.TotalBytes);
        Progress = s.Status == DownloadStatus.Completed ? 1 : Fraction(s);
        ProgressText = s.Status switch
        {
            DownloadStatus.Completed => "Готово",
            DownloadStatus.Queued => "В очереди",
            DownloadStatus.Paused => "Пауза",
            DownloadStatus.Error => s.StatusText ?? "Ошибка",
            _ => s.TotalBytes is > 0 ? $"{(int)(Progress * 100)}%" : "…",
        };
        var downloading = s.Status == DownloadStatus.Downloading;
        SpeedText = downloading ? Format.Speed(s.BytesPerSecond) : "";
        EtaText = downloading ? Format.Eta(s.Eta) : "";
        TypeLabel = LabelFor(s.FileName);
        DetailText = Detail(s);
    }

    private string Detail(DownloadSnapshot s)
    {
        var done = s.TotalBytes is > 0 ? $"{Format.Bytes(s.DownloadedBytes)} из {SizeText}" : Format.Bytes(s.DownloadedBytes);
        return s.Status switch
        {
            DownloadStatus.Completed => $"Готово · {SizeText}",
            DownloadStatus.Queued => s.TotalBytes is > 0 ? $"В очереди · {SizeText}" : "В очереди",
            DownloadStatus.Paused => $"Пауза · {done}",
            DownloadStatus.Error => s.StatusText ?? "Ошибка",
            _ => Join(done, SpeedText, string.IsNullOrEmpty(EtaText) ? "" : $"осталось {EtaText}", s.StatusText),
        };
    }

    private static string Join(params string?[] parts) =>
        string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));

    private static string LabelFor(string fileName)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant();
        return ext.Length is > 0 and <= 4 ? ext : "FILE";
    }

    private static double Fraction(DownloadSnapshot s) =>
        s.TotalBytes is > 0 ? Math.Clamp((double)s.DownloadedBytes / s.TotalBytes.Value, 0, 1) : 0;
}
