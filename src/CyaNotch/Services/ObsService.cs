using System;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CyaNotch.Core;

namespace CyaNotch.Services;

/// <summary>
/// Talks to OBS Studio through its built-in WebSocket server (Tools → WebSocket Server Settings, OBS 28+),
/// so the island can show a REC / LIVE pill with the elapsed time. Only connects to this PC.
/// </summary>
public sealed class ObsService
{
    public bool Connected { get; private set; }
    public bool Recording { get; private set; }
    public bool Streaming { get; private set; }
    public DateTime RecordingSince { get; private set; }
    public DateTime StreamingSince { get; private set; }
    public string Status { get; private set; } = "Off";

    public event Action? Changed;

    private CancellationTokenSource? _cts;
    private ClientWebSocket? _ws;

    public void Start()
    {
        Stop();
        if (!Settings.Current.ObsEnabled) return;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        try { _ws?.Abort(); } catch { }
        Set(false, false, false, "Off");
    }

    public Task StartRecordingAsync() => SendRequestAsync("StartRecord");
    public Task StopRecordingAsync() => SendRequestAsync("StopRecord");

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Set(false, false, false, "Looking for OBS…");
                await RunOnceAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                if (ex is not WebSocketException) Log.Error("OBS connection", ex);
            }
            Set(false, false, false, "OBS isn't running, or its WebSocket server is off");
            try { await Task.Delay(TimeSpan.FromSeconds(8), ct); } catch { return; }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        _ws = ws;
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{Settings.Current.ObsPort}"), ct);

        // Hello → Identify (with password if OBS asks for one)
        var hello = await ReceiveAsync(ws, ct);
        var identify = new JsonObject { ["rpcVersion"] = 1, ["eventSubscriptions"] = 64 /* Outputs */ };
        var auth = hello?["d"]?["authentication"];
        if (auth != null)
        {
            var secret = Sha256B64(Settings.Current.ObsPassword + auth["salt"]!.GetValue<string>());
            identify["authentication"] = Sha256B64(secret + auth["challenge"]!.GetValue<string>());
        }
        await SendAsync(ws, new JsonObject { ["op"] = 1, ["d"] = identify }, ct);

        var identified = await ReceiveAsync(ws, ct);
        if (identified?["op"]?.GetValue<int>() != 2)
        {
            Set(false, false, false, "OBS refused the password");
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return;
        }
        Set(true, false, false, "Connected to OBS");
        await SendRequestAsync("GetRecordStatus", ws, ct);
        await SendRequestAsync("GetStreamStatus", ws, ct);

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var msg = await ReceiveAsync(ws, ct);
            if (msg == null) break;
            var op = msg["op"]?.GetValue<int>();
            var d = msg["d"];
            if (op == 5) // event
            {
                var type = d?["eventType"]?.GetValue<string>();
                var active = d?["eventData"]?["outputActive"]?.GetValue<bool>() ?? false;
                if (type == "RecordStateChanged") SetRecording(active, TimeSpan.Zero);
                if (type == "StreamStateChanged") SetStreaming(active, TimeSpan.Zero);
            }
            else if (op == 7) // request response
            {
                var type = d?["requestType"]?.GetValue<string>();
                var data = d?["responseData"];
                var active = data?["outputActive"]?.GetValue<bool>() ?? false;
                var ms = data?["outputDuration"]?.GetValue<double>() ?? 0;
                if (type == "GetRecordStatus") SetRecording(active, TimeSpan.FromMilliseconds(ms));
                if (type == "GetStreamStatus") SetStreaming(active, TimeSpan.FromMilliseconds(ms));
            }
        }
    }

    private void SetRecording(bool on, TimeSpan already)
    {
        if (on && !Recording) RecordingSince = DateTime.Now - already;
        Recording = on;
        Changed?.Invoke();
    }

    private void SetStreaming(bool on, TimeSpan already)
    {
        if (on && !Streaming) StreamingSince = DateTime.Now - already;
        Streaming = on;
        Changed?.Invoke();
    }

    private void Set(bool connected, bool rec, bool live, string status)
    {
        Connected = connected; Recording = rec; Streaming = live; Status = status;
        Changed?.Invoke();
    }

    private Task SendRequestAsync(string type) =>
        _ws is { State: WebSocketState.Open } ws ? SendRequestAsync(type, ws, CancellationToken.None) : Task.CompletedTask;

    private static Task SendRequestAsync(string type, ClientWebSocket ws, CancellationToken ct) =>
        SendAsync(ws, new JsonObject { ["op"] = 6, ["d"] = new JsonObject { ["requestType"] = type, ["requestId"] = Guid.NewGuid().ToString("N") } }, ct);

    private static readonly SemaphoreSlim SendLock = new(1, 1);

    private static async Task SendAsync(ClientWebSocket ws, JsonObject msg, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
        await SendLock.WaitAsync(ct);
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
        finally { SendLock.Release(); }
    }

    private static async Task<JsonNode?> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new System.IO.MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) break;
        }
        return JsonNode.Parse(ms.ToArray());
    }

    private static string Sha256B64(string s) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}
