using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MirDM.Models;

public enum ChunkStatus
{
    Pending,
    Downloading,
    Completed,
    Paused,
    Failed
}

public class ChunkState : INotifyPropertyChanged
{
    private string _id = "";
    private int _workerId;
    private long _startByte;
    private long _endByte;
    private long _downloadedBytes;
    private double _speedBps;
    private ChunkStatus _status = ChunkStatus.Pending;
    private bool _isTailSteal;
    private string? _error;
    private string _sourceUrl = "";

    public string SourceUrl
    {
        get => _sourceUrl;
        set => SetField(ref _sourceUrl, value);
    }

    public string Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    public int WorkerId
    {
        get => _workerId;
        set => SetField(ref _workerId, value);
    }

    public long StartByte
    {
        get => _startByte;
        set { if (SetField(ref _startByte, value)) OnPropertyChanged(nameof(TotalBytes)); }
    }

    public long EndByte
    {
        get => _endByte;
        set { if (SetField(ref _endByte, value)) OnPropertyChanged(nameof(TotalBytes)); }
    }

    public long TotalBytes => Math.Max(0, EndByte - StartByte + 1);

    public long DownloadedBytes
    {
        get => _downloadedBytes;
        set
        {
            if (SetField(ref _downloadedBytes, value))
            {
                OnPropertyChanged(nameof(ProgressPercent));
                OnPropertyChanged(nameof(RemainingBytes));
            }
        }
    }

    public long RemainingBytes => Math.Max(0, TotalBytes - DownloadedBytes);

    public double ProgressPercent
    {
        get
        {
            long total = TotalBytes;
            if (total <= 0) return 0;
            return Math.Clamp((double)DownloadedBytes / total * 100.0, 0, 100.0);
        }
    }

    public double SpeedBps
    {
        get => _speedBps;
        set => SetField(ref _speedBps, value);
    }

    public ChunkStatus Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public bool IsTailSteal
    {
        get => _isTailSteal;
        set => SetField(ref _isTailSteal, value);
    }

    public string? Error
    {
        get => _error;
        set => SetField(ref _error, value);
    }

    private DateTime _nextRetryTimeUtc = DateTime.MinValue;
    public DateTime NextRetryTimeUtc
    {
        get => _nextRetryTimeUtc;
        set => SetField(ref _nextRetryTimeUtc, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
