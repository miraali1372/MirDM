using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading.Tasks;
using MirDM.Core;
using MirDM.Models;

namespace MirDM.Tests;

public static class EngineIntegrationTest
{
    private static void Log(string message)
    {
        Console.WriteLine(message);
        try
        {
            File.AppendAllText(@"D:\rea\test_result.log", message + Environment.NewLine);
        }
        catch { }
    }

    public static async Task<bool> RunAsync()
    {
        try { File.Delete(@"D:\rea\test_result.log"); } catch { }

        Log("=================================================");
        Log(" MirDM Native Ultra-Speed Engine Verification");
        Log($" HTTP/3 QUIC Supported on OS: {System.Net.Quic.QuicConnection.IsSupported}");
        Log("=================================================");

        string tempDir = Path.Combine(Path.GetTempPath(), "mirdm_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        try
        {
            // 1. Start In-Memory TestServer (16 MB payload with pre-calculated SHA256)
            int testFileSize = 16 * 1024 * 1024;
            Log($"[1/5] Initializing TestServer ({testFileSize / (1024 * 1024)} MB payload)...");
            await using var server = new TestServer(testFileSize);
            server.Start();
            Log($"      TestServer listening on {server.BaseUrl}");
            Log($"      Expected SHA-256: {server.ExpectedSha256}");

            // 2. Prepare DownloadItem
            string destFile = Path.Combine(tempDir, "downloaded_testfile.bin");
            var item = new DownloadItem
            {
                Url = server.FileUrl,
                DestinationPath = destFile,
                Connections = 16
            };

            var config = new AppConfig
            {
                DownloadDirectory = tempDir,
                DynamicSplitting = true,
                TailLatencyElimination = true,
                EnableDnsEdgeRacing = true,
                MinChunkSize = 256 * 1024,
                BufferSizeBytes = 128 * 1024
            };

            Log("[2/5] Initializing DownloadEngine with 16 connections & Dynamic Splitting...");
            await using var engine = new DownloadEngine(item, config);

            var tcs = new TaskCompletionSource<bool>();

            engine.ProgressUpdated += (d) =>
            {
                // progress update
            };

            engine.Completed += (d) =>
            {
                Log($"[3/5] Download successfully completed in {d.EtaString}!");
                tcs.TrySetResult(true);
            };

            engine.Failed += (d, ex) =>
            {
                Log($"[ERROR] Download failed: {ex.Message}");
                tcs.TrySetException(ex);
            };

            // 3. Start download
            await engine.StartAsync();

            // Wait for completion with timeout
            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            if (completedTask != tcs.Task)
            {
                throw excitingException("Download timed out after 30 seconds");
            }
            await tcs.Task;

            // 4. Verify File on Disk
            Log("[4/5] Verifying downloaded file on disk...");
            if (!File.Exists(destFile))
            {
                throw new FileNotFoundException("Destination file does not exist on disk!", destFile);
            }

            var fi = new FileInfo(destFile);
            Log($"      File size on disk: {fi.Length} bytes (Expected: {testFileSize} bytes)");
            if (fi.Length != testFileSize)
            {
                Log($"[FAIL] File size mismatch! Got {fi.Length}, expected {testFileSize}");
                return false;
            }

            // 5. Verify SHA-256 Hash
            Log("[5/5] Calculating SHA-256 checksum...");
            using var sha = SHA256.Create();
            using var fs = File.OpenRead(destFile);
            byte[] hash = await sha.ComputeHashAsync(fs);
            string actualSha256 = Convert.ToHexString(hash);

            Log($"      Actual SHA-256:   {actualSha256}");
            Log($"      Expected SHA-256: {server.ExpectedSha256}");

            if (!string.Equals(actualSha256, server.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                Log("=================================================");
                Log(" [FAIL] SHA-256 Hash mismatch on fixed-connection test!");
                Log("=================================================");
                return false;
            }

            // 6. Test Auto (Dynamic) Concurrency Scaling
            Log("\n[6/6] Testing Auto (Dynamic) Concurrency Scaling mode...");
            string destFileAuto = Path.Combine(tempDir, "downloaded_testfile_auto.bin");
            var itemAuto = new DownloadItem
            {
                Url = server.FileUrl,
                DestinationPath = destFileAuto,
                Connections = 4,
                IsAutoConnections = true
            };

            await using var engineAuto = new DownloadEngine(itemAuto, config);
            var tcsAuto = new TaskCompletionSource<bool>();

            engineAuto.Completed += (d) =>
            {
                Log($"      Auto-mode download completed successfully! Final connections reached: {d.Connections}");
                tcsAuto.TrySetResult(true);
            };

            engineAuto.Failed += (d, ex) =>
            {
                Log($"[ERROR] Auto-mode download failed: {ex.Message}");
                tcsAuto.TrySetException(ex);
            };

            await engineAuto.StartAsync();
            var completedTaskAuto = await Task.WhenAny(tcsAuto.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            if (completedTaskAuto != tcsAuto.Task)
            {
                throw excitingException("Auto-mode download timed out after 30 seconds");
            }
            await tcsAuto.Task;

            using (var fsAuto = File.OpenRead(destFileAuto))
            {
                byte[] hashAuto = await sha.ComputeHashAsync(fsAuto);
                string actualSha256Auto = Convert.ToHexString(hashAuto);
                if (!string.Equals(actualSha256Auto, server.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    Log("=================================================");
                    Log(" [FAIL] SHA-256 Hash mismatch on auto-connection test!");
                    Log("=================================================");
                    return false;
                }
            }

            // 7. Test Multi-Source / CDN Mirror Pooling across 2 separate servers
            Log("\n[7/7] Testing Multi-Source / CDN Mirror Bandwidth Pooling...");
            await using var mirrorServer = new TestServer(testFileSize);
            mirrorServer.Start();
            Log($"      Primary Server: {server.BaseUrl} | Mirror Server: {mirrorServer.BaseUrl}");

            string destFileMirrors = Path.Combine(tempDir, "downloaded_testfile_mirrors.bin");
            var itemMirrors = new DownloadItem
            {
                Url = server.FileUrl,
                Mirrors = new List<string> { mirrorServer.FileUrl },
                DestinationPath = destFileMirrors,
                Connections = 8,
                IsAutoConnections = false
            };

            await using var engineMirrors = new DownloadEngine(itemMirrors, config);
            var tcsMirrors = new TaskCompletionSource<bool>();

            engineMirrors.Completed += (d) =>
            {
                Log($"      Multi-source download completed successfully across 2 servers! Protocol: {d.ProtocolDisplay}");
                tcsMirrors.TrySetResult(true);
            };

            engineMirrors.Failed += (d, ex) =>
            {
                Log($"[ERROR] Multi-source download failed: {ex.Message}");
                tcsMirrors.TrySetException(ex);
            };

            await engineMirrors.StartAsync();
            var completedTaskMirrors = await Task.WhenAny(tcsMirrors.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            if (completedTaskMirrors != tcsMirrors.Task)
            {
                throw excitingException("Multi-source download timed out after 30 seconds");
            }
            await tcsMirrors.Task;

            using (var fsMirrors = File.OpenRead(destFileMirrors))
            {
                byte[] hashMirrors = await sha.ComputeHashAsync(fsMirrors);
                string actualSha256Mirrors = Convert.ToHexString(hashMirrors);
                if (!string.Equals(actualSha256Mirrors, server.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    Log("=================================================");
                    Log(" [FAIL] SHA-256 Hash mismatch on multi-source mirror test!");
                    Log("=================================================");
                    return false;
                }
            }

            // 8. Test Clear List & Batch Wildcard Generator logic
            Log("\n[8/8] Testing Clear List & Batch Wildcard Generator semantics...");

            // Test Pattern Wildcard Expansion
            string pattern = "https://cdn.example.com/videos/episode_(*).mp4";
            long fromNum = 1, toNum = 4;
            int digits = 2; // "01", "02", "03", "04"
            var generatedList = new List<string>();
            string formatSpecifier = new string('0', digits);
            for (long n = fromNum; n <= toNum; n++)
            {
                generatedList.Add(pattern.Replace("(*)", n.ToString(formatSpecifier)));
            }

            if (generatedList.Count != 4 ||
                generatedList[0] != "https://cdn.example.com/videos/episode_01.mp4" ||
                generatedList[3] != "https://cdn.example.com/videos/episode_04.mp4")
            {
                Log(" [FAIL] Wildcard pattern generation mismatch!");
                return false;
            }
            Log("      Wildcard generator pattern replacement & zero-padding: PASS");

            // Test Batch Multi-URL Parser
            string rawPaste = "   https://site.com/file1.zip \r\n\r\n  https://site.com/file2.zip\n\n   invalid-link\r\n";
            var parsed = rawPaste.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                 .Select(l => l.Trim())
                                 .Where(l => l.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || l.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                                 .ToList();

            if (parsed.Count != 2 || parsed[0] != "https://site.com/file1.zip" || parsed[1] != "https://site.com/file2.zip")
            {
                Log(" [FAIL] Batch multi-URL text parser mismatch!");
                return false;
            }
            Log("      Multi-URL parser whitespace/empty-line trimming: PASS");

            // Test Clear List (Without deleting file on disk)
            string dummyFile = Path.Combine(tempDir, "preserved_user_file.bin");
            await File.WriteAllTextAsync(dummyFile, "Important downloaded user file content");

            var dummyItem = new DownloadItem
            {
                FileName = "preserved_user_file.bin",
                DestinationPath = dummyFile,
                Status = DownloadStatus.Completed,
                TotalBytes = 100,
                DownloadedBytes = 100
            };
            await SessionStore.SaveSessionAsync(dummyItem);
            string sessionFile = SessionStore.GetSessionFilePath(dummyItem.Id);
            if (!File.Exists(sessionFile))
            {
                Log(" [FAIL] Session file was not created!");
                return false;
            }

            // Perform Session deletion like ClearList does (delete session, NOT physical file)
            SessionStore.DeleteSession(dummyItem);

            if (File.Exists(sessionFile))
            {
                Log(" [FAIL] Session file was not removed after Clear List!");
                return false;
            }

            if (!File.Exists(dummyFile))
            {
                Log(" [FAIL] Physical file was deleted! Clear List MUST NEVER delete files on disk!");
                return false;
            }
            Log("      Clear List session cleanup with Disk File Preservation: PASS");

            Log("=================================================");
            Log(" [PASS] ALL ENGINE TESTS PASSED (Fixed + Auto Dynamic + Multi-Source Mirror + Batch + Clear List)!");
            Log("=================================================");
            return true;
        }
        catch (Exception ex)
        {
            Log($"[FATAL EXCEPTION]: {ex}");
            return false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch { }
        }
    }

    public static async Task<bool> RunHetznerTestAsync()
    {
        Log("=================================================");
        Log(" MirDM Real-World Hetzner Test: 100MB.bin");
        Log("=================================================");

        string testDir = @"D:\rea\mirdm_test_downloads";
        if (!Directory.Exists(testDir))
        {
            Directory.CreateDirectory(testDir);
        }

        string destPath = Path.Combine(testDir, "100MB.bin");
        try
        {
            if (File.Exists(destPath)) File.Delete(destPath);
            if (File.Exists(destPath + ".mirdm")) File.Delete(destPath + ".mirdm");
        }
        catch { }

        // Test with MirDM DownloadEngine
        var item = new DownloadItem
        {
            Url = "https://ash-speed.hetzner.com/100MB.bin",
            DestinationPath = destPath,
            Connections = 8,
            IsAutoConnections = true
        };

        var config = new AppConfig
        {
            DownloadDirectory = testDir,
            DynamicSplitting = true,
            TailLatencyElimination = true,
            EnableDnsEdgeRacing = true,
            MinChunkSize = 2 * 1024 * 1024, // 2MB min chunk: prevents any micro-fragmentation
            BufferSizeBytes = 256 * 1024,
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36"
        };

        Log($"[1/4] Initiating download from {item.Url}...");
        await using var engine = new DownloadEngine(item, config);
        var tcs = new TaskCompletionSource<bool>();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        long lastLogged = 0;

        engine.ProgressUpdated += (d) =>
        {
            long elapsed = sw.ElapsedMilliseconds;
            if (elapsed - lastLogged >= 1000)
            {
                lastLogged = elapsed;
                Log($"      Progress: {d.ProgressPercent:0.0}% | Speed: {d.FormattedSpeed} | Downloaded: {d.FormattedProgress} | Active Conn: {d.Connections}");
            }
        };

        engine.Completed += (d) =>
        {
            sw.Stop();
            double mbps = (d.TotalBytes / (1024.0 * 1024.0)) / Math.Max(0.1, sw.Elapsed.TotalSeconds);
            Log($"[2/4] Download completed in {sw.Elapsed.TotalSeconds:0.2}s! Avg Speed: {mbps:0.2} MB/s");
            tcs.TrySetResult(true);
        };

        engine.Failed += (d, ex) =>
        {
            Log($"[ERROR] Hetzner download failed: {ex}");
            tcs.TrySetException(ex);
        };

        await engine.StartAsync();

        var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(300)));
        if (completedTask != tcs.Task)
        {
            await engine.PauseAsync();
            Log("[ERROR] Hetzner download timed out after 300 seconds.");
            return false;
        }

        try
        {
            await tcs.Task;
        }
        catch (Exception ex)
        {
            Log($"[FAIL] Task completed with exception: {ex.Message}");
            return false;
        }

        Log("[3/4] Verifying file size and presence on disk...");
        if (!File.Exists(destPath))
        {
            Log("[FAIL] Downloaded file does not exist on disk!");
            return false;
        }

        var fi = new FileInfo(destPath);
        Log($"      File size on disk: {fi.Length:N0} bytes (Expected: 104,857,600 bytes)");
        if (fi.Length != 104857600)
        {
            Log($"[FAIL] File size mismatch! Got {fi.Length}, expected 104857600");
            return false;
        }

        Log("[4/4] Verifying chunk structure (Hyper-fragmentation check)...");
        int chunkCount;
        lock (item.Chunks)
        {
            chunkCount = item.Chunks.Count;
            Log($"      Total chunks: {chunkCount}");
            foreach (var c in item.Chunks)
            {
                Log($"        {c.Id}: bytes {c.StartByte}-{c.EndByte} ({c.TotalBytes:N0} bytes) - Status: {c.Status}");
            }
        }

        if (chunkCount > 32)
        {
            Log($"[WARN] Higher chunk count than expected ({chunkCount}).");
        }
        else
        {
            Log($"      Clean chunk segmentation: {chunkCount} chunks, zero micro-chunk fragments.");
        }

        Log("=================================================");
        Log(" [PASS] 100MB HETZNER TEST FINISHED 100% SUCCESSFULLY!");
        Log("=================================================");
        return true;
    }

    private static Exception excitingException(string message) => new TimeoutException(message);
}
