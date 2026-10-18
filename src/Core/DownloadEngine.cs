using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using MirDM.Models;

namespace MirDM.Core;

public class DownloadEngine : IAsyncDisposable
{
    private readonly DownloadItem _item;
    private readonly AppConfig _config;
    private readonly HttpClient _httpClient;
    private readonly DynamicSplitter _splitter;
    private FileStorage? _fileStorage;
    private CancellationTokenSource? _cts;
    private Task? _supervisionTask;

    private long _lastDownloadedBytes;
    private long _lastTelemetryTimeMs;
    private double _smoothedSpeed;
    private int _nextWorkerId = 1;
    private int _currentMaxConnections;
    private int _maxSafeConnections = 16;
    private long _lastScaleCheckTimeMs;
    private double _previousWindowSpeed;
    private long _rateLimitCooldownUntilMs;
    private long _lastWorkerSpawnTimestamp;
    private readonly Dictionary<string, int> _chunkFailureCounts = new();

    public DownloadItem Item => _item;
    public bool IsRunning => _item.Status == DownloadStatus.Downloading;

    public event Action<DownloadItem>? ProgressUpdated;
    public event Action<DownloadItem>? Completed;
    public event Action<DownloadItem, Exception>? Failed;

    public DownloadEngine(DownloadItem item, AppConfig? config = null)
    {
        _item = item;
        _config = config ?? AppConfig.Load();

        if (_item.IsAutoConnections)
        {
            _currentMaxConnections = Math.Clamp(_item.Connections > 0 ? _item.Connections : 8, 4, 16);
        }
        else
        {
            _currentMaxConnections = Math.Clamp(_item.Connections, 1, 64);
        }

        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            EnableMultipleHttp3Connections = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 32,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always
        };

        if (_config.EnableDnsEdgeRacing)
        {
            handler.ConnectCallback = async (context, ct) =>
            {
                return await DnsEdgeRacer.ConnectFastestStreamAsync(
                    context.DnsEndPoint.Host,
                    context.DnsEndPoint.Port,
                    ct).ConfigureAwait(false);
            };
        }
        else
        {
            handler.ConnectCallback = async (context, ct) =>
            {
                var s = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
                DnsEdgeRacer.ConfigureSocket(s);
                await s.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
                return new System.Net.Sockets.NetworkStream(s, ownsSocket: true);
            };
        }

        _httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(_config.UserAgent);
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "identity");

        _splitter = new DynamicSplitter(
            minSplitSize: Math.Max(512 * 1024, _config.MinChunkSize),
            tailLatencyElimination: _config.TailLatencyElimination,
            maxConnections: _currentMaxConnections);
    }

    public async Task StartAsync()
    {
        if (_item.Status == DownloadStatus.Downloading) return;

        _cts = new CancellationTokenSource();
        _item.Status = DownloadStatus.Initializing;
        _item.Error = null;

        try
        {
            // 1. Probe Server if not already probed
            if (_item.TotalBytes <= 0 || string.IsNullOrEmpty(_item.FileName))
            {
                await ProbeServerAsync(_cts.Token).ConfigureAwait(false);
            }

            // Ensure destination path
            if (string.IsNullOrEmpty(_item.DestinationPath))
            {
                _item.DestinationPath = Path.Combine(_config.DownloadDirectory, _item.FileName);
            }

            // 2. Initialize FileStorage with NTFS zero-fragmentation preallocation
            _fileStorage = new FileStorage(_item.DestinationPath, _item.TotalBytes);

            // 3. Partition into initial chunks if empty
            if (_item.Chunks.Count == 0)
            {
                CreateInitialChunks();
            }

            _item.Status = DownloadStatus.Downloading;
            _lastTelemetryTimeMs = Stopwatch.GetTimestamp();
            _lastDownloadedBytes = _item.DownloadedBytes;

            // 4. Start supervisor loop
            _supervisionTask = Task.Run(() => SuperviseDownloadAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            _item.Status = DownloadStatus.Failed;
            _item.Error = ex.Message;
            Failed?.Invoke(_item, ex);
        }
    }

    public async Task PauseAsync()
    {
        if (_item.Status != DownloadStatus.Downloading && _item.Status != DownloadStatus.Initializing) return;

        _cts?.Cancel();
        if (_supervisionTask != null)
        {
            try
            {
                await _supervisionTask.ConfigureAwait(false);
            }
            catch
            {
                // ignore cancellation
            }
        }

        _item.Status = DownloadStatus.Paused;
        _item.SpeedBps = 0;
        _item.EtaString = "Paused";

        if (_fileStorage != null)
        {
            await _fileStorage.FlushAsync().ConfigureAwait(false);
        }

        await SessionStore.SaveSessionAsync(_item).ConfigureAwait(false);
        ProgressUpdated?.Invoke(_item);
    }

    public async Task ResumeAsync()
    {
        if (_item.Status == DownloadStatus.Downloading) return;
        await StartAsync().ConfigureAwait(false);
    }

    private async Task ProbeServerAsync(CancellationToken ct)
    {
        const int maxProbeRetries = 3;
        for (int retry = 0; retry < maxProbeRetries; retry++)
        {
            try
            {
                using var probeReq = new HttpRequestMessage(HttpMethod.Get, _item.Url)
                {
                    Version = HttpVersion.Version30,
                    VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
                };
                probeReq.Headers.Range = new RangeHeaderValue(0, 0); // 1-byte probe
                probeReq.Headers.Accept.ParseAdd("*/*");
                probeReq.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");

                using var response = await _httpClient.SendAsync(probeReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                
                if (response.StatusCode == HttpStatusCode.TooManyRequests || response.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    TimeSpan delay = TimeSpan.FromSeconds(1.5 * (retry + 1));
                    if (response.Headers.RetryAfter?.Delta.HasValue == true)
                    {
                        delay = response.Headers.RetryAfter.Delta.Value;
                    }
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    continue;
                }

                _item.FinalUrl = response.RequestMessage?.RequestUri?.ToString() ?? _item.Url;

                // Check Range support
                if (response.StatusCode == HttpStatusCode.PartialContent)
                {
                    _item.SupportsRanges = true;
                    if (response.Content.Headers.ContentRange?.Length.HasValue == true)
                    {
                        _item.TotalBytes = response.Content.Headers.ContentRange.Length.Value;
                    }
                }
                else if (response.IsSuccessStatusCode)
                {
                    _item.SupportsRanges = response.Headers.AcceptRanges.Contains("bytes");
                    if (response.Content.Headers.ContentLength.HasValue)
                    {
                        _item.TotalBytes = response.Content.Headers.ContentLength.Value;
                    }
                }
                else
                {
                    if (retry < maxProbeRetries - 1 && (int)response.StatusCode >= 500)
                    {
                        await Task.Delay(1000 * (retry + 1), ct).ConfigureAwait(false);
                        continue;
                    }
                    throw new HttpRequestException($"Server returned HTTP {(int)response.StatusCode} on probe");
                }

                // ETag
                if (response.Headers.ETag != null)
                {
                    _item.ETag = response.Headers.ETag.Tag;
                }

                // Suggested Filename
                if (string.IsNullOrEmpty(_item.FileName))
                {
                    string? dispositionFilename = response.Content.Headers.ContentDisposition?.FileNameStar ??
                                                  response.Content.Headers.ContentDisposition?.FileName;

                    if (!string.IsNullOrWhiteSpace(dispositionFilename))
                    {
                        _item.FileName = CleanFileName(dispositionFilename.Trim('\"'));
                    }
                    else
                    {
                        try
                        {
                            var uri = new Uri(_item.FinalUrl);
                            string lastSeg = Path.GetFileName(uri.AbsolutePath);
                            _item.FileName = !string.IsNullOrWhiteSpace(lastSeg) ? CleanFileName(WebUtility.UrlDecode(lastSeg)) : "download.bin";
                        }
                        catch
                        {
                            _item.FileName = "download.bin";
                        }
                    }
                }

                return;
            }
            catch (Exception) when (retry < maxProbeRetries - 1 && !ct.IsCancellationRequested)
            {
                await Task.Delay(1000 * (retry + 1), ct).ConfigureAwait(false);
            }
        }
    }

    private static string CleanFileName(string raw)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string clean = new string(raw.Where(c => !invalid.Contains(c)).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "download.bin" : clean;
    }

    private bool HandleRateLimiting(ChunkWorker worker, HttpStatusCode statusCode)
    {
        long now = Stopwatch.GetTimestamp();
        _rateLimitCooldownUntilMs = now + (long)(Stopwatch.Frequency * 5); // 5 seconds cooldown

        if (_currentMaxConnections > 1)
        {
            _currentMaxConnections = Math.Max(1, _currentMaxConnections / 2);
            _maxSafeConnections = _currentMaxConnections;
            _item.Connections = _currentMaxConnections;
            _splitter.MaxConnections = _currentMaxConnections;
        }

        // Allow at most 1 worker to keep retrying on 429; other workers yield their chunk back to Pending
        // so active connections drop immediately to the backoff limit.
        return _currentMaxConnections <= 1;
    }

    private void CreateInitialChunks()
    {
        if (!_item.SupportsRanges || _item.TotalBytes <= 0)
        {
            // Single chunk for non-seekable streams
            var single = new ChunkState
            {
                Id = "chunk-0",
                StartByte = 0,
                EndByte = _item.TotalBytes > 0 ? _item.TotalBytes - 1 : long.MaxValue,
                Status = ChunkStatus.Pending
            };
            lock (_item.Chunks)
            {
                _item.Chunks.Add(single);
            }
            return;
        }

        int conn;
        if (_item.IsAutoConnections)
        {
            if (_item.TotalBytes > 64 * 1024 * 1024) conn = 8;
            else if (_item.TotalBytes > 16 * 1024 * 1024) conn = 6;
            else if (_item.TotalBytes > 4 * 1024 * 1024) conn = 4;
            else conn = 2;

            _currentMaxConnections = conn;
            _item.Connections = _currentMaxConnections;
            _splitter.MaxConnections = _currentMaxConnections;
        }
        else
        {
            conn = Math.Max(1, Math.Min(_item.Connections, 64));
            _currentMaxConnections = conn;
            _splitter.MaxConnections = _currentMaxConnections;
        }

        long partSize = _item.TotalBytes / conn;

        lock (_item.Chunks)
        {
            for (int i = 0; i < conn; i++)
            {
                long start = i * partSize;
                long end = (i == conn - 1) ? (_item.TotalBytes - 1) : ((i + 1) * partSize - 1);

                var chunk = new ChunkState
                {
                    Id = $"chunk-{i + 1}",
                    StartByte = start,
                    EndByte = end,
                    DownloadedBytes = 0,
                    Status = ChunkStatus.Pending
                };
                _item.Chunks.Add(chunk);
            }
        }
    }

    private async Task SuperviseDownloadAsync(CancellationToken ct)
    {
        var activeWorkers = new Dictionary<ChunkWorker, Task>();
        var chunksList = _item.Chunks.ToList();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Remove completed/failed worker tasks
                var finishedWorkers = activeWorkers.Where(kvp => kvp.Value.IsCompleted).ToList();
                foreach (var fw in finishedWorkers)
                {
                    activeWorkers.Remove(fw.Key);
                }

                // Check overall completion
                long totalDownloaded;
                lock (_item.Chunks)
                {
                    totalDownloaded = _item.Chunks.Sum(c => c.DownloadedBytes);
                }
                _item.DownloadedBytes = totalDownloaded;

                if (_item.TotalBytes > 0 && totalDownloaded >= _item.TotalBytes)
                {
                    break;
                }

                // Adaptive Concurrency Scaling (Auto Connections)
                long now = Stopwatch.GetTimestamp();
                bool isRateLimited = now < _rateLimitCooldownUntilMs;
                int autoMaxCap = Math.Min(_maxSafeConnections, _item.TotalBytes > 1024L * 1024 * 1024 ? 24 : 16);

                if (!isRateLimited && _item.IsAutoConnections && _item.SupportsRanges && _currentMaxConnections < autoMaxCap && _item.TotalBytes > 2 * 1024 * 1024)
                {
                    double scaleElapsed = (double)(now - _lastScaleCheckTimeMs) / Stopwatch.Frequency;

                    if (scaleElapsed >= 2.0)
                    {
                        double curSpeed = _smoothedSpeed;
                        long remaining = Math.Max(0, _item.TotalBytes - totalDownloaded);

                        // If workers are saturated and throughput is increasing or healthy
                        if (activeWorkers.Count >= _currentMaxConnections - 1 && remaining > (_currentMaxConnections * 1024 * 1024))
                        {
                            if (_previousWindowSpeed == 0 || curSpeed >= _previousWindowSpeed * 1.05)
                            {
                                int target = Math.Min(autoMaxCap, _currentMaxConnections + 2);
                                if (target > _currentMaxConnections)
                                {
                                    _currentMaxConnections = target;
                                    _item.Connections = _currentMaxConnections;
                                    _splitter.MaxConnections = _currentMaxConnections;
                                }
                            }
                        }

                        _previousWindowSpeed = curSpeed;
                        _lastScaleCheckTimeMs = now;
                    }
                }

                // Check for failed chunks and reset them for retry if within retry threshold
                lock (_item.Chunks)
                {
                    foreach (var c in _item.Chunks.Where(c => c.Status == ChunkStatus.Failed))
                    {
                        int count = _chunkFailureCounts.GetValueOrDefault(c.Id, 0);
                        if (count < 3)
                        {
                            _chunkFailureCounts[c.Id] = count + 1;
                            c.Status = ChunkStatus.Pending;
                        }
                    }
                }

                // Recruit workers for pending chunks (staggered by at least 250ms to respect CDN rate-limiters)
                lock (_item.Chunks)
                {
                    chunksList = _item.Chunks.ToList();
                }

                DateTime utcNow = DateTime.UtcNow;
                foreach (var chunk in chunksList.Where(c => (c.Status == ChunkStatus.Pending || c.Status == ChunkStatus.Paused) && c.NextRetryTimeUtc <= utcNow))
                {
                    if (activeWorkers.Count >= _currentMaxConnections) break;

                    double timeSinceSpawn = (double)(now - _lastWorkerSpawnTimestamp) / Stopwatch.Frequency;
                    if (_lastWorkerSpawnTimestamp > 0 && timeSinceSpawn < 0.25)
                    {
                        break; // Stagger connection initiation to prevent 429 bursts
                    }

                    int workerId = _nextWorkerId++;
                    var worker = new ChunkWorker(
                        _httpClient,
                        chunk,
                        _item.FinalUrl,
                        _fileStorage!,
                        workerId,
                        _config.BufferSizeBytes,
                        onBytesRead: _ => { },
                        onRateLimited: HandleRateLimiting);

                    var task = Task.Run(() => worker.RunAsync(ct), ct);
                    activeWorkers[worker] = task;
                    _lastWorkerSpawnTimestamp = now;

                    // Stagger: only launch 1 new worker connection per tick
                    break;
                }

                // Dynamic In-Flight Re-segmentation & Aggressive Work-Stealing (prevented while rate limited)
                if (!isRateLimited && _config.DynamicSplitting && _item.SupportsRanges && activeWorkers.Count < _currentMaxConnections)
                {
                    double timeSinceSpawn = (double)(now - _lastWorkerSpawnTimestamp) / Stopwatch.Frequency;
                    if (_lastWorkerSpawnTimestamp == 0 || timeSinceSpawn >= 0.3)
                    {
                        var newChunk = _splitter.TrySplitLargestChunk(
                            chunksList,
                            activeWorkers.Count,
                            totalDownloaded,
                            _item.TotalBytes,
                            out _);

                        if (newChunk != null)
                        {
                            lock (_item.Chunks)
                            {
                                if (!_item.Chunks.Contains(newChunk))
                                {
                                    _item.Chunks.Add(newChunk);
                                }
                            }

                            int workerId = _nextWorkerId++;
                            var worker = new ChunkWorker(
                                _httpClient,
                                newChunk,
                                _item.FinalUrl,
                                _fileStorage!,
                                workerId,
                                _config.BufferSizeBytes,
                                onBytesRead: _ => { },
                                onRateLimited: HandleRateLimiting);

                            var task = Task.Run(() => worker.RunAsync(ct), ct);
                            activeWorkers[worker] = task;
                            _lastWorkerSpawnTimestamp = now;
                        }
                    }
                }

                // Telemetry Calculation
                now = Stopwatch.GetTimestamp();
                double elapsedSec = (double)(now - _lastTelemetryTimeMs) / Stopwatch.Frequency;
                if (elapsedSec >= 0.2)
                {
                    long deltaBytes = totalDownloaded - _lastDownloadedBytes;
                    double currentSpeed = Math.Max(0, deltaBytes / elapsedSec);

                    // Exponential moving average for smooth display
                    _smoothedSpeed = _smoothedSpeed == 0 ? currentSpeed : (_smoothedSpeed * 0.7 + currentSpeed * 0.3);
                    _item.SpeedBps = _smoothedSpeed;

                    if (_item.TotalBytes > 0 && _smoothedSpeed > 0)
                    {
                        long remaining = Math.Max(0, _item.TotalBytes - totalDownloaded);
                        double etaSec = remaining / _smoothedSpeed;
                        _item.EtaString = TimeSpan.FromSeconds(Math.Min(etaSec, 86400 * 7)).ToString(@"hh\:mm\:ss");
                    }
                    else
                    {
                        _item.EtaString = "--";
                    }

                    _lastTelemetryTimeMs = now;
                    _lastDownloadedBytes = totalDownloaded;

                    ProgressUpdated?.Invoke(_item);
                }

                if (activeWorkers.Count == 0 && _item.Chunks.All(c => c.Status == ChunkStatus.Completed))
                {
                    break;
                }

                await Task.Delay(50, ct).ConfigureAwait(false);
            }

            // Await remaining worker tasks
            if (activeWorkers.Count > 0)
            {
                await Task.WhenAll(activeWorkers.Values).ConfigureAwait(false);
            }

            // Final Verification
            lock (_item.Chunks)
            {
                _item.DownloadedBytes = _item.Chunks.Sum(c => c.DownloadedBytes);
            }

            if (_fileStorage != null)
            {
                await _fileStorage.FlushAsync().ConfigureAwait(false);
                await _fileStorage.DisposeAsync().ConfigureAwait(false);
                _fileStorage = null;
            }

            _item.Status = DownloadStatus.Completed;
            _item.CompletedAt = DateTime.Now;
            _item.SpeedBps = 0;
            _item.EtaString = "Completed";

            await SessionStore.SaveSessionAsync(_item).ConfigureAwait(false);
            Completed?.Invoke(_item);
            ProgressUpdated?.Invoke(_item);
        }
        catch (OperationCanceledException)
        {
            _item.Status = DownloadStatus.Paused;
            _item.SpeedBps = 0;
            _item.EtaString = "Paused";
            ProgressUpdated?.Invoke(_item);
        }
        catch (Exception ex)
        {
            _item.Status = DownloadStatus.Failed;
            _item.Error = ex.Message;
            Failed?.Invoke(_item, ex);
            ProgressUpdated?.Invoke(_item);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _httpClient.Dispose();
        if (_fileStorage != null)
        {
            await _fileStorage.DisposeAsync().ConfigureAwait(false);
            _fileStorage = null;
        }
    }
}
