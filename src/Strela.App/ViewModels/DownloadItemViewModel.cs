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
    private string _statusLabel = "";
    private string _detailTail = "";
    private string _badgeKind = "other";
    private string _percentText = "";
    private int _queuePosition;

    public DownloadItemViewModel(Guid id) => Id = id;

    public Guid Id { get; }
    public string FileName { get => _fileName; private set => SetProperty(ref _fileName, value); }
    public string SizeText { get => _sizeText; private set => SetProperty(ref _sizeText, value); }
    /// <summary>0..1</summary>
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }
    public string ProgressText { get => _progressText; private set => SetProperty(ref _progressText, value); }
    /// <summary>Процент для крупной подписи справа («68%»); пусто, если размер неизвестен.</summary>
    public string PercentText { get => _percentText; private set => SetProperty(ref _percentText, value); }
    public string SpeedText { get => _speedText; private set => SetProperty(ref _speedText, value); }
    public string EtaText { get => _etaText; private set => SetProperty(ref _etaText, value); }
    public string? FilePath { get => _filePath; private set => SetProperty(ref _filePath, value); }
    public string Url { get => _url; private set => SetProperty(ref _url, value); }
    /// <summary>Строка под именем: «412 МБ из 607 МБ · 42 МБ/с · осталось 0:45», «Готово · 607 МБ», текст ошибки…</summary>
    public string DetailText { get => _detailText; private set => SetProperty(ref _detailText, value); }
    /// <summary>Короткая метка типа файла для значка: «ZIP», «ISO», «EXE»…</summary>
    public string TypeLabel { get => _typeLabel; private set => SetProperty(ref _typeLabel, value); }
    /// <summary>Цветное слово статуса в начале строки подробностей: «Готово», «Приостановлено», «В очереди»; пусто при загрузке и ошибке.</summary>
    public string StatusLabel { get => _statusLabel; private set => SetProperty(ref _statusLabel, value); }
    /// <summary>Остаток строки подробностей после <see cref="StatusLabel"/> (вместе с « · », если он есть).</summary>
    public string DetailTail { get => _detailTail; private set => SetProperty(ref _detailTail, value); }
    /// <summary>Цвет значка типа: archive, disk, app, media, document, torrent или other.</summary>
    public string BadgeKind { get => _badgeKind; private set => SetProperty(ref _badgeKind, value); }

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
            OnPropertyChanged(nameof(IsPaused));
            OnPropertyChanged(nameof(ShowPercent));
            OnPropertyChanged(nameof(IsQueued));
        }
    }

    public bool IsPaused => Status == DownloadStatus.Paused;
    public bool ShowPercent => Status is DownloadStatus.Downloading or DownloadStatus.Paused;

    public bool IsCompleted => Status == DownloadStatus.Completed;
    public bool IsQueued => Status == DownloadStatus.Queued;
    public bool IsActive => Status == DownloadStatus.Downloading;
    public bool IsError => Status == DownloadStatus.Error;
    public bool ShowProgressBar => Status is not (DownloadStatus.Completed or DownloadStatus.Error);
    public bool CanPause => Status is DownloadStatus.Downloading or DownloadStatus.Queued;
    public bool CanResume => Status == DownloadStatus.Paused;
    public bool CanRetry => Status == DownloadStatus.Error;

    /// <summary>
    /// Обновляет только изменившиеся свойства — PropertyChanged для остальных не поднимается.
    /// <paramref name="queuePosition"/> — номер среди ожидающих (1 — начнётся первой); 0 — не показывать.
    /// </summary>
    public void Update(DownloadSnapshot s, int queuePosition = 0)
    {
        _queuePosition = s.Status == DownloadStatus.Queued ? queuePosition : 0;
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
        PercentText = s.TotalBytes is > 0 ? $"{(int)(Progress * 100)}%" : "";
        TypeLabel = s.Kind switch
        {
            DownloadKind.Torrent => "TOR",
            // Пока видео качается, имя — заголовок ролика без расширения (в нём бывают точки): метка «YT».
            DownloadKind.Video => s.Status == DownloadStatus.Completed ? LabelFor(s.FileName) : "YT",
            _ => LabelFor(s.FileName),
        };
        BadgeKind = s.Kind switch
        {
            DownloadKind.Torrent => "torrent",
            DownloadKind.Video => "media",
            _ => KindFor(TypeLabel),
        };
        DetailText = Detail(s);
        StatusLabel = s.Status switch
        {
            DownloadStatus.Completed => "Готово",
            DownloadStatus.Paused => "Приостановлено",
            DownloadStatus.Queued => QueuedLabel,
            _ => "",
        };
        DetailTail = StatusLabel.Length == 0 ? DetailText
            : DetailText.StartsWith(StatusLabel + " · ", StringComparison.Ordinal) ? DetailText[StatusLabel.Length..]
            : DetailText == StatusLabel ? "" : " · " + DetailText;
    }

    private static string KindFor(string label) => label switch
    {
        "ZIP" or "RAR" or "7Z" or "TAR" or "GZ" or "BZ2" or "XZ" or "TGZ" or "ZST" => "archive",
        "ISO" or "IMG" or "VHD" or "VHDX" or "OVA" or "DMG" => "disk",
        "EXE" or "MSI" or "MSIX" or "APPX" or "APK" or "DEB" or "RPM" or "JAR" => "app",
        "MP4" or "MKV" or "AVI" or "MOV" or "WEBM" or "M4V" or "MP3" or "FLAC" or "WAV" or "M4A" or "OGG" or "OPUS" => "media",
        "PDF" or "EPUB" or "DJVU" or "DOCX" or "XLSX" or "PPTX" => "document",
        _ => "other",
    };

    private string Detail(DownloadSnapshot s)
    {
        var done = s.TotalBytes is > 0 ? $"{Format.Bytes(s.DownloadedBytes)} из {SizeText}" : Format.Bytes(s.DownloadedBytes);
        return s.Status switch
        {
            DownloadStatus.Completed => $"Готово · {SizeText}",
            DownloadStatus.Queued => s.TotalBytes is > 0 ? $"{QueuedLabel} · {SizeText}" : QueuedLabel,
            DownloadStatus.Paused => $"Приостановлено · {done}",
            DownloadStatus.Error => s.StatusText ?? "Ошибка",
            // Торрент: «1.2 ГБ из 4.0 ГБ · 12 МБ/с · сиды 30 · пиры 12 · осталось 3:10».
            _ when s.Kind is DownloadKind.Torrent or DownloadKind.Video =>
                Join(done, SpeedText, s.StatusText, string.IsNullOrEmpty(EtaText) ? "" : $"осталось {EtaText}"),
            _ => Join(done, SpeedText, string.IsNullOrEmpty(EtaText) ? "" : $"осталось {EtaText}", s.StatusText),
        };
    }

    /// <summary>«В очереди №2»: номер меняется от «Начать раньше / позже», хотя место в списке остаётся прежним.</summary>
    private string QueuedLabel => _queuePosition > 0 ? $"В очереди №{_queuePosition}" : "В очереди";

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
