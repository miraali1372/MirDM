using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MirDM.Models;

namespace MirDM.Core;

public class ChunkWorker
{
    private readonly HttpClient _httpClient;
    private ChunkState _chunk;
    private readonly string _defaultUrl;
    private readonly FileStorage _fileStorage;
    private readonly int _workerId;
    private readonly int _bufferSize;
    private readonly Action<long> _onBytesRead;
    private readonly Func<ChunkWorker, HttpStatusCode, bool>? _onRateLimited;
    private readonly Func<ChunkWorker, ChunkState?>? _getNextChunk;
    private readonly Action<string>? _onProtocolNegotiated;

    private long _totalBytesDownloadedByWorker;
    private long _workerStartTimeTicks;

    public int WorkerId => _workerId;
    public ChunkState Chunk => _chunk;
    public double WorkerSpeedBps { get; private set; }

    public ChunkWorker(
        HttpClient httpClient,
        ChunkState initialChunk,
        string defaultUrl,
        FileStorage fileStorage,
        int workerId,
        int bufferSize,
        Action<long> onBytesRead,
        Func<ChunkWorker, HttpStatusCode, bool>? onRateLimited = null,
        Func<ChunkWorker, ChunkState?>? getNextChunk = null,
        Action<string>? onProtocolNegotiated = null)
    {
        _httpClient = httpClient;
        _chunk = initialChunk;
        _defaultUrl = defaultUrl;
        _fileStorage = fileStorage;
        _workerId = workerId;
        _bufferSize = Math.Max(256 * 1024, bufferSize);
        _onBytesRead = onBytesRead;
        _onRateLimited = onRateLimited;
        _getNextChunk = getNextChunk;
        _onProtocolNegotiated = onProtocolNegotiated;

        _chunk.WorkerId = workerId;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        byte[] poolBuffer = ArrayPool<byte>.Shared.Rent(_bufferSize);
        _workerStartTimeTicks = Stopwatch.GetTimestamp();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await DownloadSingleChunkAsync(_chunk, poolBuffer, cancellationToken).ConfigureAwait(false);

                if (_chunk.Status != ChunkStatus.Completed || cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                // If Sliding Window chunk queue is configured, seamlessly pull next block
                // reusing the exact same underlying keep-alive connection!
                if (_getNextChunk == null)
                {
                    break;
                }

                var nextChunk = _getNextChunk(this);
                if (nextChunk == null)
                {
                    break;
                }

                _chunk = nextChunk;
                _chunk.WorkerId = _workerId;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(poolBuffer);
        }
    }

    private async Task DownloadSingleChunkAsync(ChunkState chunk, byte[] poolBuffer, CancellationToken cancellationToken)
    {
        chunk.Status = ChunkStatus.Downloading;
        chunk.Error = null;

        const int maxRetries = 5;
        int retryCount = 0;

        string requestUrl = !string.IsNullOrWhiteSpace(chunk.SourceUrl) ? chunk.SourceUrl : _defaultUrl;

        while (!cancellationToken.IsCancellationRequested)
        {
            long currentOffset;
            long targetEnd;
            lock (chunk)
            {
                currentOffset = chunk.StartByte + chunk.DownloadedBytes;
                targetEnd = chunk.EndByte;
            }

            // Check if already completed
            if (currentOffset > targetEnd)
            {
                chunk.Status = ChunkStatus.Completed;
                return;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl)
                {
                    Version = HttpVersion.Version30,
                    VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
                };
                request.Headers.Range = new RangeHeaderValue(currentOffset, targetEnd);
                request.Headers.Accept.ParseAdd("*/*");
                request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");

                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);

                // Detect and report negotiated HTTP version (HTTP/3, HTTP/2, HTTP/1.1)
                if (response.Version != null && _onProtocolNegotiated != null)
                {
                    string proto = response.Version.Major switch
                    {
                        3 => "HTTP/3 (QUIC)",
                        2 => "HTTP/2",
                        _ => $"HTTP/{response.Version.Major}.{response.Version.Minor}"
                    };
                    _onProtocolNegotiated(proto);
                }

                // Handle HTTP 429 Too Many Requests or 503 Service Unavailable
                if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                    response.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    retryCount++;
                    TimeSpan backoff = TimeSpan.FromMilliseconds(1000 * Math.Pow(1.5, retryCount) + Random.Shared.Next(200, 800));
                    if (response.Headers.RetryAfter?.Delta.HasValue == true)
                    {
                        backoff = response.Headers.RetryAfter.Delta.Value;
                        if (backoff < TimeSpan.FromSeconds(1)) backoff = TimeSpan.FromSeconds(1);
                        if (backoff > TimeSpan.FromSeconds(10)) backoff = TimeSpan.FromSeconds(10);
                    }

                    bool shouldContinue = _onRateLimited?.Invoke(this, response.StatusCode) ?? true;
                    if (!shouldContinue)
                    {
                        lock (chunk)
                        {
                            chunk.Status = ChunkStatus.Pending;
                            chunk.NextRetryTimeUtc = DateTime.UtcNow.Add(backoff);
                            chunk.Error = $"Rate limited (HTTP {(int)response.StatusCode}), yielded for concurrency backoff...";
                        }
                        return;
                    }

                    lock (chunk)
                    {
                        chunk.NextRetryTimeUtc = DateTime.UtcNow.Add(backoff);
                        chunk.Error = $"Rate limited (HTTP {(int)response.StatusCode}), backing off {backoff.TotalSeconds:0.1}s (retry {retryCount}/{maxRetries})...";
                    }

                    if (retryCount > maxRetries)
                    {
                        lock (chunk)
                        {
                            chunk.Status = ChunkStatus.Pending;
                        }
                        return;
                    }

                    await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // Handle HTTP 416 Range Not Satisfiable
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    lock (chunk)
                    {
                        if (currentOffset >= chunk.EndByte)
                        {
                            chunk.Status = ChunkStatus.Completed;
                            return;
                        }
                    }
                }

                // Transient 5xx server errors
                if (!response.IsSuccessStatusCode)
                {
                    if ((int)response.StatusCode >= 500 && (int)response.StatusCode < 600)
                    {
                        retryCount++;
                        if (retryCount > maxRetries)
                        {
                            // If this was a mirror, fail back to primary default URL
                            if (requestUrl != _defaultUrl)
                            {
                                requestUrl = _defaultUrl;
                                chunk.SourceUrl = _defaultUrl;
                                retryCount = 0;
                                continue;
                            }
                            throw new HttpRequestException($"Server returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                        }

                        TimeSpan backoff = TimeSpan.FromMilliseconds(500 * (1 << retryCount));
                        chunk.Error = $"Server error {(int)response.StatusCode}, retrying in {backoff.TotalSeconds:0.1}s ({retryCount}/{maxRetries})...";
                        await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    // On other client/mirror error, failover to default primary URL
                    if (requestUrl != _defaultUrl)
                    {
                        requestUrl = _defaultUrl;
                        chunk.SourceUrl = _defaultUrl;
                        continue;
                    }

                    throw new HttpRequestException($"Server returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                }

                // Successful response: reset retry count and proceed with streaming
                retryCount = 0;
                chunk.Error = null;

                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

                while (!cancellationToken.IsCancellationRequested)
                {
                    long remainingToDownload;
                    lock (chunk)
                    {
                        remainingToDownload = chunk.EndByte - currentOffset + 1;
                    }

                    if (remainingToDownload <= 0)
                    {
                        break;
                    }

                    // High-efficiency batched streaming: fill chunk buffer to minimize syscalls & IOCP overhead
                    int accumulated = 0;
                    while (accumulated < poolBuffer.Length && remainingToDownload > 0)
                    {
                        int toRead = (int)Math.Min(poolBuffer.Length - accumulated, remainingToDownload);
                        int bytesRead = await stream.ReadAsync(poolBuffer.AsMemory(accumulated, toRead), cancellationToken).ConfigureAwait(false);

                        if (bytesRead == 0)
                        {
                            break;
                        }

                        // Check if chunk boundary was shortened while we were waiting on stream.ReadAsync
                        lock (chunk)
                        {
                            long maxAllowed = chunk.EndByte - (currentOffset + accumulated) + 1;
                            if (bytesRead > maxAllowed)
                            {
                                bytesRead = (int)Math.Max(0, maxAllowed);
                                accumulated += bytesRead;
                                remainingToDownload = 0;
                                break;
                            }
                        }

                        accumulated += bytesRead;
                        remainingToDownload -= bytesRead;

                        // Flush when >= 128KB accumulated or when TCP window is drained
                        if (accumulated >= 128 * 1024 || bytesRead < toRead)
                        {
                            break;
                        }
                    }

                    if (accumulated == 0)
                    {
                        break;
                    }

                    // Direct file write at exact byte offset using Windows IOCP asynchronous direct I/O
                    await _fileStorage.WriteAtAsync(
                        poolBuffer.AsMemory(0, accumulated),
                        currentOffset,
                        cancellationToken).ConfigureAwait(false);

                    currentOffset += accumulated;
                    lock (chunk)
                    {
                        chunk.DownloadedBytes += accumulated;
                    }
                    _totalBytesDownloadedByWorker += accumulated;

                    double elapsed = (double)(Stopwatch.GetTimestamp() - _workerStartTimeTicks) / Stopwatch.Frequency;
                    if (elapsed > 0.05)
                    {
                        WorkerSpeedBps = _totalBytesDownloadedByWorker / elapsed;
                    }

                    _onBytesRead(accumulated);
                }

                lock (chunk)
                {
                    if (chunk.DownloadedBytes >= chunk.TotalBytes || currentOffset > chunk.EndByte)
                    {
                        chunk.Status = ChunkStatus.Completed;
                        return;
                    }
                }
            }
            catch (HttpRequestException ex) when (retryCount < maxRetries && !cancellationToken.IsCancellationRequested)
            {
                if (requestUrl != _defaultUrl)
                {
                    requestUrl = _defaultUrl;
                    chunk.SourceUrl = _defaultUrl;
                }
                retryCount++;
                chunk.Error = $"Network retry {retryCount}/{maxRetries}: {ex.Message}";
                await Task.Delay(500 * (1 << Math.Min(retryCount, 4)), cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex) when (retryCount < maxRetries && !cancellationToken.IsCancellationRequested)
            {
                if (requestUrl != _defaultUrl)
                {
                    requestUrl = _defaultUrl;
                    chunk.SourceUrl = _defaultUrl;
                }
                retryCount++;
                chunk.Error = $"Connection drop, resuming {retryCount}/{maxRetries}: {ex.Message}";
                await Task.Delay(500 * (1 << Math.Min(retryCount, 4)), cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException ex) when (retryCount < maxRetries && !cancellationToken.IsCancellationRequested)
            {
                if (requestUrl != _defaultUrl)
                {
                    requestUrl = _defaultUrl;
                    chunk.SourceUrl = _defaultUrl;
                }
                retryCount++;
                chunk.Error = $"Socket reconnecting {retryCount}/{maxRetries}: {ex.Message}";
                await Task.Delay(500 * (1 << Math.Min(retryCount, 4)), cancellationToken).ConfigureAwait(false);
            }
        }

        lock (chunk)
        {
            if (chunk.DownloadedBytes >= chunk.TotalBytes || (chunk.EndByte >= chunk.StartByte && chunk.StartByte + chunk.DownloadedBytes > chunk.EndByte))
            {
                chunk.Status = ChunkStatus.Completed;
            }
            else if (cancellationToken.IsCancellationRequested)
            {
                chunk.Status = ChunkStatus.Paused;
            }
        }
    }
}
