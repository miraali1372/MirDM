using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace MirDM.Core;

public static class DnsEdgeRacer
{
    private static readonly ConcurrentDictionary<string, (IPAddress Ip, DateTime Expiry)> EdgeCache = new(StringComparer.OrdinalIgnoreCase);
    public const int DefaultSocketReceiveBufferSize = 2 * 1024 * 1024; // 2MB BDP buffer
    public const int DefaultSocketSendBufferSize = 64 * 1024;

    public static void ConfigureSocket(Socket socket)
    {
        try
        {
            socket.NoDelay = true;
            socket.ReceiveBufferSize = DefaultSocketReceiveBufferSize;
            socket.SendBufferSize = DefaultSocketSendBufferSize;
            socket.LingerState = new LingerOption(false, 0);
        }
        catch
        {
            // Ignore socket option failures if platform restricts buffer sizing
        }
    }

    public static async ValueTask<Stream> ConnectFastestStreamAsync(string host, int port, CancellationToken cancellationToken)
    {
        var socket = await ConnectFastestSocketAsync(host, port, cancellationToken).ConfigureAwait(false);
        return new NetworkStream(socket, ownsSocket: true);
    }

    public static async Task<Socket> ConnectFastestSocketAsync(string host, int port, CancellationToken cancellationToken)
    {
        // 1. If direct IP or localhost, connect directly with optimized socket options
        if (IPAddress.TryParse(host, out var directIp))
        {
            var s = new Socket(directIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            ConfigureSocket(s);
            await s.ConnectAsync(directIp, port, cancellationToken).ConfigureAwait(false);
            return s;
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            ConfigureSocket(s);
            await s.ConnectAsync(IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);
            return s;
        }

        // 2. Check if we already have a cached winning Edge IP
        if (EdgeCache.TryGetValue(host, out var cached) && DateTime.UtcNow < cached.Expiry)
        {
            try
            {
                var s = new Socket(cached.Ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                ConfigureSocket(s);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                await s.ConnectAsync(cached.Ip, port, cts.Token).ConfigureAwait(false);
                return s;
            }
            catch
            {
                EdgeCache.TryRemove(host, out _);
            }
        }

        // 3. Resolve all IPv4 and IPv6 addresses
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Standard fallback
            var s = new Socket(SocketType.Stream, ProtocolType.Tcp);
            ConfigureSocket(s);
            await s.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            return s;
        }

        if (addresses.Length <= 1)
        {
            var ip = addresses.Length == 1 ? addresses[0] : IPAddress.Loopback;
            var s = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            ConfigureSocket(s);
            await s.ConnectAsync(ip, port, cancellationToken).ConfigureAwait(false);
            EdgeCache[host] = (ip, DateTime.UtcNow.AddMinutes(10));
            return s;
        }

        // 4. Parallel CDN RTT Race across all resolved IPs
        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        raceCts.CancelAfter(TimeSpan.FromSeconds(3));

        var raceTasks = addresses.Select(async ip =>
        {
            var s = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            ConfigureSocket(s);
            try
            {
                await s.ConnectAsync(ip, port, raceCts.Token).ConfigureAwait(false);
                return (Socket: s, Ip: ip);
            }
            catch
            {
                s.Dispose();
                throw;
            }
        }).ToList();

        while (raceTasks.Count > 0)
        {
            var finished = await Task.WhenAny(raceTasks).ConfigureAwait(false);
            raceTasks.Remove(finished);

            if (finished.IsCompletedSuccessfully)
            {
                var winner = finished.Result;
                raceCts.Cancel(); // Cancel remaining attempts
                EdgeCache[host] = (winner.Ip, DateTime.UtcNow.AddMinutes(10));

                // Cleanup losers in background
                _ = Task.Run(async () =>
                {
                    foreach (var t in raceTasks)
                    {
                        try
                        {
                            var other = await t.ConfigureAwait(false);
                            other.Socket.Dispose();
                        }
                        catch { }
                    }
                });

                return winner.Socket;
            }
        }

        // If race did not yield, connect directly to preferred IPv4 or first reachable IP
        var preferred = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0];
        var fallback = new Socket(preferred.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        ConfigureSocket(fallback);
        await fallback.ConnectAsync(preferred, port, cancellationToken).ConfigureAwait(false);
        return fallback;
    }
}
