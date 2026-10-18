using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace MirDM.Core;

public class FileStorage : IAsyncDisposable
{
    private readonly string _filePath;
    private readonly SafeFileHandle _handle;
    private long _bytesWrittenSinceFlush;
    private const long FlushThresholdBytes = 16 * 1024 * 1024; // 16 MB batched flush
    private bool _isDisposed;

    public string FilePath => _filePath;
    public SafeFileHandle Handle => _handle;

    public FileStorage(string filePath, long totalBytes)
    {
        _filePath = filePath;
        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Direct kernel handle allocation with FileOptions.Asynchronous
        _handle = File.OpenHandle(
            filePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite,
            FileOptions.Asynchronous);

        // Instant zero-fragmentation sparse file preallocation on NTFS via RandomAccess.SetLength
        if (totalBytes > 0)
        {
            try
            {
                RandomAccess.SetLength(_handle, totalBytes);
            }
            catch
            {
                // Fallback if filesystem does not support SetLength
            }
        }
    }

    public async ValueTask WriteAtAsync(ReadOnlyMemory<byte> buffer, long fileOffset, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        await RandomAccess.WriteAsync(_handle, buffer, fileOffset, cancellationToken).ConfigureAwait(false);

        if (Interlocked.Add(ref _bytesWrittenSinceFlush, buffer.Length) >= FlushThresholdBytes)
        {
            Interlocked.Exchange(ref _bytesWrittenSinceFlush, 0);
            try
            {
                RandomAccess.FlushToDisk(_handle);
            }
            catch { }
        }
    }

    public ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        try
        {
            RandomAccess.FlushToDisk(_handle);
        }
        catch { }
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            try
            {
                RandomAccess.FlushToDisk(_handle);
            }
            catch { }
            _handle.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
