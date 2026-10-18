using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace MirDM.Core;

public class TestServer : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly byte[] _payload;
    private readonly string _sha256Hex;
    private readonly int _port;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenTask;

    public int Port => _port;
    public string BaseUrl => $"http://127.0.0.1:{_port}/";
    public string FileUrl => $"http://127.0.0.1:{_port}/testfile.bin";
    public string ExpectedSha256 => _sha256Hex;
    public int FileSizeBytes => _payload.Length;

    public TestServer(int sizeBytes = 16 * 1024 * 1024) // 16 MB default
    {
        _payload = new byte[sizeBytes];
        var rng = new Random(1337);
        rng.NextBytes(_payload);

        using var sha = SHA256.Create();
        _sha256Hex = Convert.ToHexString(sha.ComputeHash(_payload));

        Exception? lastEx = null;
        HttpListener? activeListener = null;
        for (int p = 54321 + Random.Shared.Next(1, 100); p < 55500; p++)
        {
            var l = new HttpListener();
            try
            {
                l.Prefixes.Add($"http://127.0.0.1:{p}/");
                l.Start();
                activeListener = l;
                _port = p;
                break;
            }
            catch (Exception ex)
            {
                lastEx = ex;
                try { l.Close(); } catch { }
            }
        }

        if (activeListener == null || !activeListener.IsListening)
        {
            throw new InvalidOperationException($"Could not bind TestServer to any local port: {lastEx?.Message}", lastEx);
        }

        _listener = activeListener;
    }

    public void Start()
    {
        _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync().ConfigureAwait(false);
                _ = Task.Run(() => HandleRequestAsync(context), ct);
            }
            catch
            {
                if (ct.IsCancellationRequested || !_listener.IsListening) break;
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        var req = context.Request;
        var res = context.Response;

        try
        {
            res.Headers["Accept-Ranges"] = "bytes";
            res.Headers["ETag"] = $"\"{_sha256Hex[..16]}\"";
            res.Headers["Content-Disposition"] = "attachment; filename=\"testfile.bin\"";
            res.ContentType = "application/octet-stream";

            string? rangeHeader = req.Headers["Range"];
            if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
            {
                string rangeVal = rangeHeader["bytes=".Length..];
                string[] parts = rangeVal.Split('-');

                long start = long.Parse(parts[0]);
                long end = parts.Length > 1 && !string.IsNullOrEmpty(parts[1])
                    ? long.Parse(parts[1])
                    : _payload.Length - 1;

                end = Math.Min(end, _payload.Length - 1);
                long rangeLength = end - start + 1;

                res.StatusCode = (int)HttpStatusCode.PartialContent;
                res.Headers["Content-Range"] = $"bytes {start}-{end}/{_payload.Length}";
                res.ContentLength64 = rangeLength;

                if (req.HttpMethod != "HEAD")
                {
                    await res.OutputStream.WriteAsync(_payload.AsMemory((int)start, (int)rangeLength)).ConfigureAwait(false);
                }
            }
            else
            {
                res.StatusCode = (int)HttpStatusCode.OK;
                res.ContentLength64 = _payload.Length;

                if (req.HttpMethod != "HEAD")
                {
                    await res.OutputStream.WriteAsync(_payload.AsMemory(0, _payload.Length)).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            // stream aborted
        }
        finally
        {
            try { res.Close(); } catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _cts.Dispose();
        if (_listener.IsListening)
        {
            _listener.Stop();
            _listener.Close();
        }
        if (_listenTask != null)
        {
            try { await _listenTask.ConfigureAwait(false); } catch { }
        }
    }
}
