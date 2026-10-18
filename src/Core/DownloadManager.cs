using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using MirDM.Models;
using WpfApplication = System.Windows.Application;

namespace MirDM.Core;

public class DownloadManager
{
    private static DownloadManager? _instance;
    public static DownloadManager Instance => _instance ??= new DownloadManager();

    private readonly AppConfig _config;
    private readonly ConcurrentDictionary<string, DownloadEngine> _activeEngines = new();

    public ObservableCollection<DownloadItem> Downloads { get; } = new();
    public AppConfig Config => _config;

    public event Action? OnCollectionUpdated;
    public event Action<DownloadItem>? OnDownloadCompleted;
    public event Action<DownloadItem, Exception>? OnDownloadFailed;

    private DownloadManager()
    {
        _config = AppConfig.Load();
    }

    public async Task InitializeAsync()
    {
        var savedSessions = await SessionStore.LoadAllSessionsAsync();
        WpfApplication.Current?.Dispatcher?.Invoke(() =>
        {
            foreach (var item in savedSessions)
            {
                Downloads.Add(item);
            }
        });
    }

    public double TotalSpeedBps => Downloads
        .Where(d => d.Status == DownloadStatus.Downloading)
        .Sum(d => d.SpeedBps);

    public int ActiveCount => Downloads
        .Count(d => d.Status == DownloadStatus.Downloading || d.Status == DownloadStatus.Initializing);

    public int CompletedCount => Downloads
        .Count(d => d.Status == DownloadStatus.Completed);

    public async Task<DownloadItem> AddDownloadAsync(
        string url,
        string? destinationPath = null,
        int connections = 16,
        bool isAutoConnections = true,
        bool startImmediately = true,
        List<string>? mirrors = null)
    {
        var item = new DownloadItem
        {
            Url = url.Trim(),
            Mirrors = mirrors != null ? mirrors.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).ToList() : new List<string>(),
            Connections = connections <= 0 ? 16 : Math.Clamp(connections, 1, 64),
            IsAutoConnections = isAutoConnections,
            DestinationPath = destinationPath ?? "",
            Status = DownloadStatus.Queued
        };

        WpfApplication.Current?.Dispatcher?.Invoke(() =>
        {
            Downloads.Insert(0, item);
        });

        if (startImmediately)
        {
            await StartDownloadAsync(item);
        }
        else
        {
            await SessionStore.SaveSessionAsync(item);
        }

        OnCollectionUpdated?.Invoke();
        return item;
    }

    public async Task<List<DownloadItem>> AddBatchDownloadsAsync(
        IEnumerable<string> urls,
        string saveDirectory,
        int connections = 16,
        bool isAutoConnections = true,
        bool startImmediately = false)
    {
        var addedItems = new List<DownloadItem>();
        var urlList = urls.Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.Trim()).Distinct().ToList();
        if (urlList.Count == 0) return addedItems;

        foreach (var url in urlList)
        {
            string destPath = "";
            try
            {
                var uri = new Uri(url);
                string fileName = Path.GetFileName(uri.AbsolutePath);
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    fileName = System.Net.WebUtility.UrlDecode(fileName);
                    if (!string.IsNullOrWhiteSpace(saveDirectory))
                    {
                        destPath = Path.Combine(saveDirectory, fileName);
                    }
                }
            }
            catch { }

            var item = new DownloadItem
            {
                Url = url,
                Connections = connections <= 0 ? 16 : Math.Clamp(connections, 1, 64),
                IsAutoConnections = isAutoConnections,
                DestinationPath = destPath,
                Status = DownloadStatus.Queued
            };

            addedItems.Add(item);
        }

        WpfApplication.Current?.Dispatcher?.Invoke(() =>
        {
            // Insert at beginning in reverse so original order is preserved
            for (int i = addedItems.Count - 1; i >= 0; i--)
            {
                Downloads.Insert(0, addedItems[i]);
            }
        });

        foreach (var item in addedItems)
        {
            if (startImmediately)
            {
                await StartDownloadAsync(item);
            }
            else
            {
                item.Status = DownloadStatus.Queued;
                await SessionStore.SaveSessionAsync(item);
            }
        }

        OnCollectionUpdated?.Invoke();
        return addedItems;
    }

    public async Task StartDownloadAsync(DownloadItem item)
    {
        if (_activeEngines.TryGetValue(item.Id, out var existingEngine))
        {
            if (existingEngine.IsRunning) return;
            await existingEngine.DisposeAsync();
            _activeEngines.TryRemove(item.Id, out _);
        }

        var engine = new DownloadEngine(item, _config);
        _activeEngines[item.Id] = engine;

        engine.ProgressUpdated += (d) =>
        {
            OnCollectionUpdated?.Invoke();
        };

        engine.Completed += (d) =>
        {
            _activeEngines.TryRemove(d.Id, out _);
            OnDownloadCompleted?.Invoke(d);
            OnCollectionUpdated?.Invoke();
        };

        engine.Failed += (d, ex) =>
        {
            _activeEngines.TryRemove(d.Id, out _);
            OnDownloadFailed?.Invoke(d, ex);
            OnCollectionUpdated?.Invoke();
        };

        _ = Task.Run(async () =>
        {
            await engine.StartAsync();
        });
    }

    public async Task PauseDownloadAsync(DownloadItem item)
    {
        if (_activeEngines.TryGetValue(item.Id, out var engine))
        {
            await engine.PauseAsync();
            await engine.DisposeAsync();
            _activeEngines.TryRemove(item.Id, out _);
        }
        else
        {
            item.Status = DownloadStatus.Paused;
            item.SpeedBps = 0;
            await SessionStore.SaveSessionAsync(item);
        }
        OnCollectionUpdated?.Invoke();
    }

    public async Task ResumeDownloadAsync(DownloadItem item)
    {
        await StartDownloadAsync(item);
    }

    public async Task RemoveDownloadAsync(DownloadItem item, bool deleteFile = false)
    {
        if (_activeEngines.TryGetValue(item.Id, out var engine))
        {
            await engine.PauseAsync();
            await engine.DisposeAsync();
            _activeEngines.TryRemove(item.Id, out _);
        }

        WpfApplication.Current?.Dispatcher?.Invoke(() =>
        {
            Downloads.Remove(item);
        });

        SessionStore.DeleteSession(item);

        if (deleteFile && !string.IsNullOrEmpty(item.DestinationPath) && File.Exists(item.DestinationPath))
        {
            try
            {
                File.Delete(item.DestinationPath);
            }
            catch { }
        }

        OnCollectionUpdated?.Invoke();
    }

    public Task ClearCompletedAsync()
    {
        var completed = Downloads.Where(d => d.Status == DownloadStatus.Completed).ToList();
        if (completed.Count == 0) return Task.CompletedTask;

        foreach (var item in completed)
        {
            SessionStore.DeleteSession(item);
        }

        WpfApplication.Current?.Dispatcher?.Invoke(() =>
        {
            foreach (var item in completed)
            {
                Downloads.Remove(item);
            }
        });

        OnCollectionUpdated?.Invoke();
        return Task.CompletedTask;
    }

    public async Task ClearAllAsync()
    {
        var all = Downloads.ToList();
        foreach (var item in all)
        {
            if (_activeEngines.TryGetValue(item.Id, out var engine))
            {
                try
                {
                    await engine.PauseAsync();
                    await engine.DisposeAsync();
                }
                catch { }
                _activeEngines.TryRemove(item.Id, out _);
            }
            SessionStore.DeleteSession(item);
        }

        WpfApplication.Current?.Dispatcher?.Invoke(() =>
        {
            Downloads.Clear();
        });

        OnCollectionUpdated?.Invoke();
    }

    public async Task RemoveMultipleAsync(IEnumerable<DownloadItem> items, bool deleteFile = false)
    {
        var list = items.ToList();
        if (list.Count == 0) return;

        foreach (var item in list)
        {
            if (_activeEngines.TryGetValue(item.Id, out var engine))
            {
                try
                {
                    await engine.PauseAsync();
                    await engine.DisposeAsync();
                }
                catch { }
                _activeEngines.TryRemove(item.Id, out _);
            }

            SessionStore.DeleteSession(item);

            if (deleteFile && !string.IsNullOrEmpty(item.DestinationPath) && File.Exists(item.DestinationPath))
            {
                try
                {
                    File.Delete(item.DestinationPath);
                }
                catch { }
            }
        }

        WpfApplication.Current?.Dispatcher?.Invoke(() =>
        {
            foreach (var item in list)
            {
                Downloads.Remove(item);
            }
        });

        OnCollectionUpdated?.Invoke();
    }

    public async Task PauseAllAsync()
    {
        var running = Downloads.Where(d => d.Status == DownloadStatus.Downloading).ToList();
        foreach (var item in running)
        {
            await PauseDownloadAsync(item);
        }
    }

    public async Task ResumeAllAsync()
    {
        var paused = Downloads.Where(d => d.Status == DownloadStatus.Paused || d.Status == DownloadStatus.Queued).ToList();
        foreach (var item in paused)
        {
            await ResumeDownloadAsync(item);
        }
    }
}
