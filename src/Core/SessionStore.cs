using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using MirDM.Models;

namespace MirDM.Core;

public class SessionStateRecord
{
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public string FinalUrl { get; set; } = "";
    public string Protocol { get; set; } = "HTTP/3+2";
    public List<string> Mirrors { get; set; } = new();
    public string FileName { get; set; } = "";
    public string DestinationPath { get; set; } = "";
    public long TotalBytes { get; set; }
    public long DownloadedBytes { get; set; }
    public DownloadStatus Status { get; set; }
    public int Connections { get; set; }
    public bool IsAutoConnections { get; set; } = true;
    public bool SupportsRanges { get; set; }
    public string? ETag { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public List<ChunkStateRecord> Chunks { get; set; } = new();
}

public class ChunkStateRecord
{
    public string Id { get; set; } = "";
    public int WorkerId { get; set; }
    public string SourceUrl { get; set; } = "";
    public long StartByte { get; set; }
    public long EndByte { get; set; }
    public long DownloadedBytes { get; set; }
    public ChunkStatus Status { get; set; }
    public bool IsTailSteal { get; set; }
}

public static class SessionStore
{
    private static readonly string SessionsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MirDM",
        "sessions");

    public static string GetSessionFilePath(string id)
    {
        if (!Directory.Exists(SessionsDirectory))
        {
            Directory.CreateDirectory(SessionsDirectory);
        }
        return Path.Combine(SessionsDirectory, $"{id}.mirdm.json");
    }

    public static async Task SaveSessionAsync(DownloadItem item)
    {
        try
        {
            var record = new SessionStateRecord
            {
                Id = item.Id,
                Url = item.Url,
                FinalUrl = item.FinalUrl,
                Protocol = item.Protocol,
                Mirrors = item.Mirrors != null ? new List<string>(item.Mirrors) : new List<string>(),
                FileName = item.FileName,
                DestinationPath = item.DestinationPath,
                TotalBytes = item.TotalBytes,
                DownloadedBytes = item.DownloadedBytes,
                Status = item.Status,
                Connections = item.Connections,
                IsAutoConnections = item.IsAutoConnections,
                SupportsRanges = item.SupportsRanges,
                ETag = item.ETag,
                CreatedAt = item.CreatedAt,
                CompletedAt = item.CompletedAt,
                Chunks = new List<ChunkStateRecord>()
            };

            lock (item.Chunks)
            {
                foreach (var chunk in item.Chunks)
                {
                    record.Chunks.Add(new ChunkStateRecord
                    {
                        Id = chunk.Id,
                        WorkerId = chunk.WorkerId,
                        SourceUrl = chunk.SourceUrl ?? "",
                        StartByte = chunk.StartByte,
                        EndByte = chunk.EndByte,
                        DownloadedBytes = chunk.DownloadedBytes,
                        Status = chunk.Status,
                        IsTailSteal = chunk.IsTailSteal
                    });
                }
            }

            string filePath = GetSessionFilePath(item.Id);
            string json = JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(filePath, json).ConfigureAwait(false);

            // Also persist companion .mirdm sidecar file if destination path is known
            if (!string.IsNullOrWhiteSpace(item.DestinationPath))
            {
                try
                {
                    string sidecar = item.DestinationPath + ".mirdm";
                    await File.WriteAllTextAsync(sidecar, json).ConfigureAwait(false);
                }
                catch { }
            }
        }
        catch
        {
            // Ignore serialization errors during active write
        }
    }

    public static async Task<List<DownloadItem>> LoadAllSessionsAsync()
    {
        var items = new List<DownloadItem>();
        if (!Directory.Exists(SessionsDirectory)) return items;

        foreach (var file in Directory.GetFiles(SessionsDirectory, "*.mirdm.json"))
        {
            try
            {
                string json = await File.ReadAllTextAsync(file).ConfigureAwait(false);
                var record = JsonSerializer.Deserialize<SessionStateRecord>(json);
                if (record != null)
                {
                    var item = new DownloadItem
                    {
                        Id = record.Id,
                        Url = record.Url,
                        FinalUrl = record.FinalUrl,
                        Protocol = string.IsNullOrEmpty(record.Protocol) ? "HTTP/3+2" : record.Protocol,
                        Mirrors = record.Mirrors != null ? new List<string>(record.Mirrors) : new List<string>(),
                        FileName = record.FileName,
                        DestinationPath = record.DestinationPath,
                        TotalBytes = record.TotalBytes,
                        DownloadedBytes = record.DownloadedBytes,
                        Status = record.Status == DownloadStatus.Downloading ? DownloadStatus.Paused : record.Status,
                        Connections = record.Connections > 0 ? record.Connections : 16,
                        IsAutoConnections = record.IsAutoConnections,
                        SupportsRanges = record.SupportsRanges,
                        ETag = record.ETag,
                        CreatedAt = record.CreatedAt,
                        CompletedAt = record.CompletedAt
                    };

                    foreach (var c in record.Chunks)
                    {
                        item.Chunks.Add(new ChunkState
                        {
                            Id = c.Id,
                            WorkerId = c.WorkerId,
                            SourceUrl = c.SourceUrl ?? "",
                            StartByte = c.StartByte,
                            EndByte = c.EndByte,
                            DownloadedBytes = c.DownloadedBytes,
                            Status = c.Status == ChunkStatus.Downloading ? ChunkStatus.Paused : c.Status,
                            IsTailSteal = c.IsTailSteal
                        });
                    }

                    items.Add(item);
                }
            }
            catch
            {
                // ignore corrupt session file
            }
        }
        return items;
    }

    public static void DeleteSession(DownloadItem item)
    {
        try
        {
            string path = GetSessionFilePath(item.Id);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            if (!string.IsNullOrEmpty(item.DestinationPath))
            {
                string sidecar = item.DestinationPath + ".mirdm";
                if (File.Exists(sidecar))
                {
                    File.Delete(sidecar);
                }
            }
        }
        catch
        {
            // ignore
        }
    }
}
