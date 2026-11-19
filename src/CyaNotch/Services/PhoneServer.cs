using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CyaNotch.Core;

namespace CyaNotch.Services;

/// <summary>
/// A tiny HTTP server for the phone features. It listens on a random port, and every URL has to carry
/// a random key, so only a phone that scanned the QR code can reach it. It only runs while it is needed
/// (the QR code is on screen, or the phone remote is switched on), and only on the local network.
/// </summary>
public sealed class PhoneServer : IDisposable
{
    public delegate Task<bool> HandlerAsync(Request req, Response res);

    public sealed class Request
    {
        public string Method { get; init; } = "GET";
        public string Path { get; init; } = "/";
        public Dictionary<string, string> Query { get; init; } = new();
        public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Stream Body { get; init; } = Stream.Null;
        public long ContentLength { get; init; }
        public IPAddress Remote { get; init; } = IPAddress.None;
        public string Q(string key) => Query.TryGetValue(key, out var v) ? v : "";
    }

    public sealed class Response
    {
        private readonly Stream _stream;
        public bool Sent { get; private set; }
        public Response(Stream s) => _stream = s;

        public async Task SendAsync(int status, string contentType, byte[] body, string? extraHeaders = null)
        {
            var head = $"HTTP/1.1 {status} {Reason(status)}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\n" +
                       "Cache-Control: no-store\r\nConnection: close\r\n" + (extraHeaders ?? "") + "\r\n";
            var hb = Encoding.ASCII.GetBytes(head);
            await _stream.WriteAsync(hb);
            await _stream.WriteAsync(body);
            Sent = true;
        }

        public Task TextAsync(string text, string type = "text/plain; charset=utf-8", int status = 200) =>
            SendAsync(status, type, Encoding.UTF8.GetBytes(text));

        public async Task FileAsync(string path)
        {
            var info = new FileInfo(path);
            var name = Uri.EscapeDataString(info.Name);
            var head = $"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {info.Length}\r\n" +
                       $"Content-Disposition: attachment; filename*=UTF-8''{name}\r\nConnection: close\r\n\r\n";
            await _stream.WriteAsync(Encoding.ASCII.GetBytes(head));
            await using var fs = info.OpenRead();
            await fs.CopyToAsync(_stream);
            Sent = true;
        }

        private static string Reason(int s) => s switch
        {
            200 => "OK", 204 => "No Content", 400 => "Bad Request", 403 => "Forbidden",
            404 => "Not Found", 413 => "Payload Too Large", _ => "Error",
        };
    }

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<string, HandlerAsync> _routes = new();

    public int Port { get; private set; }
    public bool Running => _listener != null;

    /// <summary>Registers a handler for everything under /{key}/. Returns the key.</summary>
    public string Mount(HandlerAsync handler)
    {
        var key = NewKey();
        _routes[key] = handler;
        return key;
    }

    public void MountAs(string key, HandlerAsync handler) => _routes[key] = handler;

    public void Unmount(string key) => _routes.TryRemove(key, out _);

    public bool HasMounts => !_routes.IsEmpty;

    public static string NewKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

    public void Start()
    {
        if (_listener != null) return;
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoop(_listener, _cts.Token);
        Log.Info($"Phone server listening on {Port}");
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
        Log.Info("Phone server stopped");
    }

    public string UrlFor(string key) => $"http://{LanAddress()}:{Port}/{key}/";

    /// <summary>The PC's address on the Wi-Fi / Ethernet network.</summary>
    public static string LanAddress()
    {
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel &&
                            n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
                .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0 : 1)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString());
            return candidates.FirstOrDefault() ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }

    private static bool IsLocalNetwork(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var b = ip.GetAddressBytes();
        if (b.Length != 4) return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal;
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    private async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch { break; }
            _ = Task.Run(() => Handle(client), ct);
        }
    }

    private async Task Handle(TcpClient client)
    {
        using var _ = client;
        try
        {
            client.ReceiveTimeout = 15000;
            client.SendTimeout = 60000;
            var remote = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
            var stream = client.GetStream();
            var res = new Response(stream);

            if (!IsLocalNetwork(remote))
            {
                await res.TextAsync("Forbidden", status: 403);
                return;
            }

            var req = await ReadRequest(stream, remote);
            if (req == null) return;

            // /{key}/rest
            var parts = req.Path.TrimStart('/').Split('/', 2);
            if (parts.Length == 0 || !_routes.TryGetValue(parts[0], out var handler))
            {
                await res.TextAsync("This link has expired. Scan the code on your PC again.", status: 404);
                return;
            }

            var inner = new Request
            {
                Method = req.Method,
                Path = "/" + (parts.Length > 1 ? parts[1] : ""),
                Query = req.Query,
                Headers = req.Headers,
                Body = req.Body,
                ContentLength = req.ContentLength,
                Remote = remote,
            };
            if (!await handler(inner, res) && !res.Sent) await res.TextAsync("Not found", status: 404);
        }
        catch (Exception ex)
        {
            Log.Error("Phone server request failed", ex);
        }
    }

    private static async Task<Request?> ReadRequest(NetworkStream stream, IPAddress remote)
    {
        // Read headers byte by byte up to \r\n\r\n (max 16 KB), leaving the body in the stream.
        var buf = new List<byte>(1024);
        var one = new byte[1];
        while (buf.Count < 16384)
        {
            var n = await stream.ReadAsync(one);
            if (n == 0) return null;
            buf.Add(one[0]);
            var c = buf.Count;
            if (c >= 4 && buf[c - 4] == '\r' && buf[c - 3] == '\n' && buf[c - 2] == '\r' && buf[c - 1] == '\n') break;
        }

        var lines = Encoding.UTF8.GetString(buf.ToArray()).Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 2) return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in lines.Skip(1))
        {
            var i = l.IndexOf(':');
            if (i > 0) headers[l[..i].Trim()] = l[(i + 1)..].Trim();
        }

        var target = first[1];
        var q = target.IndexOf('?');
        var path = Uri.UnescapeDataString(q >= 0 ? target[..q] : target);
        var query = new Dictionary<string, string>();
        if (q >= 0)
        {
            foreach (var pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                query[Uri.UnescapeDataString(kv[0])] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
            }
        }

        long.TryParse(headers.GetValueOrDefault("Content-Length"), out var len);
        return new Request
        {
            Method = first[0].ToUpperInvariant(),
            Path = path,
            Query = query,
            Headers = headers,
            Body = stream,
            ContentLength = len,
            Remote = remote,
        };
    }

    public void Dispose() => Stop();
}
