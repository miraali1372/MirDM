using System;
using System.IO;
using System.Text.Json;

namespace MirDM.Models;

public class AppConfig
{
    public int DefaultConnections { get; set; } = 0; // 0 = Auto (Dynamic Concurrency)
    public string DownloadDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public bool DynamicSplitting { get; set; } = true;
    public bool TailLatencyElimination { get; set; } = true;
    public bool EnableDnsEdgeRacing { get; set; } = true;
    public bool EnableHttp3 { get; set; } = true;
    public bool EnableProportionalChunking { get; set; } = true;
    public bool EnablePipelinedWindow { get; set; } = true;
    public int BlockSizeBytes { get; set; } = 2 * 1024 * 1024; // 2 MB logical pipelined block size
    public bool ClipboardMonitoring { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool PlaySoundOnComplete { get; set; } = true;
    public long MinChunkSize { get; set; } = 1024 * 1024; // 1 MB
    public int BufferSizeBytes { get; set; } = 256 * 1024; // 256 KB (ArrayPool rented)
    public string UserAgent { get; set; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36";

    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MirDM",
        "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                string json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
            }
        }
        catch
        {
            // fallback to default
        }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // ignore save errors
        }
    }
}
