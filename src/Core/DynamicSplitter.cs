using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using MirDM.Models;

namespace MirDM.Core;

public class DynamicSplitter
{
    private readonly long _minSplitSize;
    private readonly bool _tailLatencyElimination;
    private int _maxConnections;
    private long _lastSplitTimestamp;

    public int MaxConnections
    {
        get => _maxConnections;
        set => _maxConnections = Math.Clamp(value, 1, 64);
    }

    public DynamicSplitter(
        long minSplitSize = 1024 * 1024,
        bool tailLatencyElimination = true,
        int maxConnections = 16)
    {
        _minSplitSize = Math.Max(256 * 1024, minSplitSize);
        _tailLatencyElimination = tailLatencyElimination;
        _maxConnections = Math.Clamp(maxConnections, 1, 64);
    }

    /// <summary>
    /// Checks if in-flight active chunks can be split to recruit an idle worker.
    /// Strictly protects against hyper-fragmentation: chunks smaller than 1MB are never split.
    /// Returns the newly created chunk if a split occurred, or null if no chunk is suitable.
    /// </summary>
    public ChunkState? TrySplitLargestChunk(
        List<ChunkState> allChunks,
        int activeWorkerCount,
        long totalDownloaded,
        long totalFileSize,
        out ChunkState? parentChunk)
    {
        parentChunk = null;

        if (activeWorkerCount >= _maxConnections)
        {
            return null;
        }

        // Rate-limit splitting frequency: avoid splitting bursts closer than 300ms
        long now = Stopwatch.GetTimestamp();
        double timeSinceLastSplitSec = (double)(now - _lastSplitTimestamp) / Stopwatch.Frequency;
        if (timeSinceLastSplitSec < 0.3)
        {
            return null;
        }

        long remainingTotal = totalFileSize > 0 ? Math.Max(0, totalFileSize - totalDownloaded) : long.MaxValue;

        // If the remaining total download is under 2 MB, do NOT split.
        // Active TCP sockets will stream the remaining bytes faster than TCP handshake + Range overhead.
        if (totalFileSize > 0 && remainingTotal < 2 * 1024 * 1024)
        {
            return null;
        }

        // Determine minimum chunk split threshold:
        // For files > 10MB: 1MB minimum chunk size (meaning chunk must have >= 2MB remaining to split into two >= 1MB chunks)
        // For files <= 10MB: 512KB minimum chunk size (meaning chunk must have >= 1MB remaining)
        long effectiveMinChunk = (totalFileSize > 10 * 1024 * 1024 || totalFileSize <= 0)
            ? Math.Max(1024 * 1024, _minSplitSize)
            : Math.Max(512 * 1024, _minSplitSize / 2);

        long minRemainingToSplit = effectiveMinChunk * 2;

        lock (allChunks)
        {
            // Find all downloading chunks with enough remaining bytes
            var candidateChunks = allChunks
                .Where(c => c.Status == ChunkStatus.Downloading)
                .Select(c =>
                {
                    long currentOffset;
                    long end;
                    lock (c)
                    {
                        currentOffset = c.StartByte + c.DownloadedBytes;
                        end = c.EndByte;
                    }
                    long remaining = Math.Max(0, end - currentOffset + 1);
                    return new { Chunk = c, CurrentOffset = currentOffset, Remaining = remaining };
                })
                .Where(x => x.Remaining >= minRemainingToSplit)
                .OrderByDescending(x => x.Remaining)
                .ToList();

            if (candidateChunks.Count == 0)
            {
                return null;
            }

            var largest = candidateChunks[0];
            parentChunk = largest.Chunk;

            // Half split point, aligned to 64KB boundary to respect I/O page alignment
            long half = largest.Remaining / 2;
            long splitPoint = largest.CurrentOffset + half;
            splitPoint = (splitPoint / (64 * 1024)) * (64 * 1024);

            if (splitPoint <= largest.CurrentOffset + effectiveMinChunk ||
                splitPoint >= largest.Chunk.EndByte - effectiveMinChunk)
            {
                splitPoint = largest.CurrentOffset + half;
            }

            long oldEndByte;
            lock (parentChunk)
            {
                long latestOffset = parentChunk.StartByte + parentChunk.DownloadedBytes;
                if (latestOffset >= splitPoint - (64 * 1024))
                {
                    // Parent worker already streamed near/past the split point
                    return null;
                }

                oldEndByte = parentChunk.EndByte;
                parentChunk.EndByte = splitPoint - 1;
            }

            bool isTailPhase = _tailLatencyElimination &&
                               totalFileSize > 0 &&
                               ((double)totalDownloaded / totalFileSize) >= 0.85;

            var newChunk = new ChunkState
            {
                Id = $"chunk-{Guid.NewGuid().ToString("N")[..6]}",
                StartByte = splitPoint,
                EndByte = oldEndByte,
                DownloadedBytes = 0,
                Status = ChunkStatus.Pending,
                IsTailSteal = isTailPhase
            };

            allChunks.Add(newChunk);
            _lastSplitTimestamp = now;
            return newChunk;
        }
    }

    /// <summary>
    /// Calculates proportional chunk boundaries based on measured connection throughputs or inverse RTTs.
    /// Allocates larger chunks to faster workers so all workers complete their initial ranges concurrently.
    /// </summary>
    public List<(long Start, long End)> CalculateProportionalPartitions(
        long totalBytes,
        int connectionCount,
        double[]? workerWeights = null)
    {
        var partitions = new List<(long Start, long End)>();
        if (totalBytes <= 0) return partitions;

        long effectiveMin = Math.Max(256 * 1024, _minSplitSize);
        int effectiveCount = Math.Clamp(connectionCount, 1, 64);

        if (totalBytes < effectiveCount * effectiveMin)
        {
            effectiveCount = Math.Max(1, (int)(totalBytes / effectiveMin));
        }

        if (effectiveCount <= 1)
        {
            partitions.Add((0, totalBytes - 1));
            return partitions;
        }

        // If no weights or invalid weights provided, partition evenly
        if (workerWeights == null || workerWeights.Length != effectiveCount || workerWeights.Any(w => w <= 0))
        {
            long partSize = totalBytes / effectiveCount;
            for (int i = 0; i < effectiveCount; i++)
            {
                long start = i * partSize;
                long end = (i == effectiveCount - 1) ? (totalBytes - 1) : ((i + 1) * partSize - 1);
                partitions.Add((start, end));
            }
            return partitions;
        }

        // Proportional sizing: Size_i = totalBytes * (Weight_i / sumWeights)
        double totalWeight = workerWeights.Sum();
        long[] sizes = new long[effectiveCount];
        long allocated = 0;

        for (int i = 0; i < effectiveCount; i++)
        {
            double ratio = workerWeights[i] / totalWeight;
            long rawSize = (long)Math.Round(totalBytes * ratio);
            // 64KB page alignment
            rawSize = Math.Max(effectiveMin, (rawSize / (64 * 1024)) * (64 * 1024));
            sizes[i] = rawSize;
            allocated += rawSize;
        }

        // Adjust discrepancy on the largest partition
        long diff = totalBytes - allocated;
        int largestIdx = 0;
        for (int i = 1; i < effectiveCount; i++)
        {
            if (sizes[i] > sizes[largestIdx]) largestIdx = i;
        }
        sizes[largestIdx] += diff;

        long currentStart = 0;
        for (int i = 0; i < effectiveCount; i++)
        {
            long size = sizes[i];
            long end = (i == effectiveCount - 1) ? (totalBytes - 1) : (currentStart + size - 1);
            if (end < currentStart) end = currentStart;
            partitions.Add((currentStart, end));
            currentStart = end + 1;
        }

        return partitions;
    }

    /// <summary>
    /// Partitions the file into fine-grained pipelined logical blocks for sliding window scheduling.
    /// Eliminates tail stragglers and allows idle workers to continuously pull blocks from a concurrent queue.
    /// </summary>
    public List<(long Start, long End)> CalculatePipelinedBlocks(long totalBytes, long blockSizeBytes)
    {
        var blocks = new List<(long Start, long End)>();
        if (totalBytes <= 0) return blocks;

        long blockSize = Math.Clamp(blockSizeBytes, 512 * 1024, 16 * 1024 * 1024);
        long start = 0;

        while (start < totalBytes)
        {
            long end = Math.Min(totalBytes - 1, start + blockSize - 1);
            blocks.Add((start, end));
            start = end + 1;
        }

        return blocks;
    }
}
