using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace MirDM.Models;

public enum DownloadStatus
{
    Queued,
    Initializing,
    Downloading,
    Paused,
    Completed,
    Failed
}

public class DownloadItem : INotifyPropertyChanged
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _url = "";
    private string _finalUrl = "";
    private string _fileName = "";
    private string _destinationPath = "";
    private long _totalBytes = -1;
    private long _downloadedBytes;
    private double _speedBps;
    private DownloadStatus _status = DownloadStatus.Queued;
    private int _connections = 16;
    private bool _isAutoConnections = true;
    private bool _supportsRanges = true;
    private string? _etag;
    private DateTime _createdAt = DateTime.Now;
    private DateTime? _completedAt;
    private string? _error;
    private string _etaString = "--";
    private string _protocol = "HTTP/3+2";
    private List<string> _mirrors = new();
    private ObservableCollection<ChunkState> _chunks = new();

    public string Protocol
    {
        get => _protocol;
        set
        {
            if (SetField(ref _protocol, value))
            {
                OnPropertyChanged(nameof(ProtocolDisplay));
            }
        }
    }

    public List<string> Mirrors
    {
        get => _mirrors;
        set
        {
            if (SetField(ref _mirrors, value))
            {
                OnPropertyChanged(nameof(ProtocolDisplay));
            }
        }
    }

    public string ProtocolDisplay
    {
        get
        {
            if (Mirrors != null && Mirrors.Count > 0) return $"{Protocol} ({Mirrors.Count + 1} hosts)";
            return Protocol;
        }
    }

    public string Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    public string Url
    {
        get => _url;
        set => SetField(ref _url, value);
    }

    public string FinalUrl
    {
        get => _finalUrl;
        set => SetField(ref _finalUrl, value);
    }

    public string FileName
    {
        get => _fileName;
        set
        {
            if (SetField(ref _fileName, value))
            {
                OnPropertyChanged(nameof(FileExtension));
                OnPropertyChanged(nameof(FileIconGlyph));
            }
        }
    }

    public string DestinationPath
    {
        get => _destinationPath;
        set => SetField(ref _destinationPath, value);
    }

    public long TotalBytes
    {
        get => _totalBytes;
        set
        {
            if (SetField(ref _totalBytes, value))
            {
                OnPropertyChanged(nameof(ProgressPercent));
                OnPropertyChanged(nameof(FormattedSize));
                OnPropertyChanged(nameof(FormattedProgress));
            }
        }
    }

    public long DownloadedBytes
    {
        get => _downloadedBytes;
        set
        {
            if (SetField(ref _downloadedBytes, value))
            {
                OnPropertyChanged(nameof(ProgressPercent));
                OnPropertyChanged(nameof(FormattedProgress));
            }
        }
    }

    public double ProgressPercent
    {
        get
        {
            if (TotalBytes <= 0) return 0;
            return Math.Clamp((double)DownloadedBytes / TotalBytes * 100.0, 0, 100.0);
        }
    }

    public double SpeedBps
    {
        get => _speedBps;
        set
        {
            if (SetField(ref _speedBps, value))
            {
                OnPropertyChanged(nameof(FormattedSpeed));
                OnPropertyChanged(nameof(SpeedMBps));
            }
        }
    }

    public double SpeedMBps => Math.Max(0, SpeedBps / (1024.0 * 1024.0));

    public DownloadStatus Status
    {
        get => _status;
        set
        {
            if (SetField(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusDisplay));
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(IsPaused));
                OnPropertyChanged(nameof(IsCompleted));
                OnPropertyChanged(nameof(StatusBadgeColor));
            }
        }
    }

    public bool IsActive => Status == DownloadStatus.Downloading || Status == DownloadStatus.Initializing;
    public bool IsPaused => Status == DownloadStatus.Paused;
    public bool IsCompleted => Status == DownloadStatus.Completed;

    public string StatusDisplay => Status switch
    {
        DownloadStatus.Queued => "Queued",
        DownloadStatus.Initializing => "Connecting...",
        DownloadStatus.Downloading => "Downloading",
        DownloadStatus.Paused => "Paused",
        DownloadStatus.Completed => "Completed",
        DownloadStatus.Failed => string.IsNullOrWhiteSpace(_error) ? "Failed" : $"Failed: {_error}",
        _ => Status.ToString()
    };

    public string StatusBadgeColor => Status switch
    {
        DownloadStatus.Downloading => "#2F81F7", // Electric Blue
        DownloadStatus.Initializing => "#388BFD",
        DownloadStatus.Completed => "#238636",   // Emerald Green
        DownloadStatus.Paused => "#D29922",      // Amber
        DownloadStatus.Failed => "#F85149",      // Red
        _ => "#6E7681"                           // Grey
    };

    public int Connections
    {
        get => _connections;
        set
        {
            if (SetField(ref _connections, value))
            {
                OnPropertyChanged(nameof(ConnectionsDisplay));
            }
        }
    }

    public bool IsAutoConnections
    {
        get => _isAutoConnections;
        set
        {
            if (SetField(ref _isAutoConnections, value))
            {
                OnPropertyChanged(nameof(ConnectionsDisplay));
            }
        }
    }

    public string ConnectionsDisplay => IsAutoConnections ? $"⚡ Auto ({Connections})" : $"{Connections} conn";

    public bool SupportsRanges
    {
        get => _supportsRanges;
        set => SetField(ref _supportsRanges, value);
    }

    public string? ETag
    {
        get => _etag;
        set => SetField(ref _etag, value);
    }

    public DateTime CreatedAt
    {
        get => _createdAt;
        set => SetField(ref _createdAt, value);
    }

    public DateTime? CompletedAt
    {
        get => _completedAt;
        set => SetField(ref _completedAt, value);
    }

    public string? Error
    {
        get => _error;
        set
        {
            if (SetField(ref _error, value))
            {
                OnPropertyChanged(nameof(StatusDisplay));
            }
        }
    }

    public string EtaString
    {
        get => _etaString;
        set => SetField(ref _etaString, value);
    }

    public ObservableCollection<ChunkState> Chunks
    {
        get => _chunks;
        set => SetField(ref _chunks, value);
    }

    public string FileExtension => Path.GetExtension(FileName).ToLowerInvariant();

    public string FileIconGlyph => FileExtension switch
    {
        ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".iso" => "📦",
        ".exe" or ".msi" or ".bat" or ".cmd" => "⚡",
        ".mp4" or ".mkv" or ".avi" or ".mov" or ".webm" => "🎬",
        ".mp3" or ".flac" or ".wav" or ".m4a" => "🎵",
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".svg" => "🖼",
        ".pdf" or ".doc" or ".docx" or ".txt" or ".xlsx" => "📄",
        _ => "📁"
    };

    public string FormattedSize => FormatBytes(TotalBytes);
    public string FormattedProgress => TotalBytes > 0 
        ? $"{FormatBytes(DownloadedBytes)} / {FormatBytes(TotalBytes)}" 
        : FormatBytes(DownloadedBytes);
    public string FormattedSpeed => SpeedBps > 0 ? $"{FormatBytes((long)SpeedBps)}/s" : "0 B/s";

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "Unknown";
        if (bytes == 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int i = 0;
        double d = bytes;
        while (d >= 1024 && i < units.Length - 1)
        {
            d /= 1024;
            i++;
        }
        return $"{d:0.##} {units[i]}";
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
