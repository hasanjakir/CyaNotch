using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CyaNotch.Core;

namespace CyaNotch.Services;

public enum AgentState { Working, NeedsYou, Done }

public sealed class AgentSession
{
    public string Id { get; init; } = "";
    public string Agent { get; set; } = "";
    public string Folder { get; set; } = "";
    public AgentState State { get; set; }
    public string Message { get; set; } = "";
    public DateTime Updated { get; set; } = DateTime.Now;
    public List<(DateTime When, string What)> Steps { get; } = new();

    public string StateText => State switch { AgentState.NeedsYou => "Needs you", AgentState.Working => "Working", _ => "Done" };
    public string Ago
    {
        get
        {
            var d = DateTime.Now - Updated;
            return d.TotalSeconds < 60 ? "now" : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes}m" : $"{(int)d.TotalHours}h";
        }
    }
    public string LastStep => Steps.Count == 0 ? Message : Steps[^1].What;
}

/// <summary>
/// Shows what coding agents are doing.
/// - With no setup: watches Claude Code and Codex session files and marks a session as working while it's being written.
/// - With hooks (Settings → Agents → Connect Claude Code): Claude Code tells CyaNotch when it starts, which tool it runs,
///   when it needs you and when it's done.
/// - Any other agent can report too: POST JSON {"state":"working|needs_you|done","message":"…","session":"…","folder":"…"}
///   to http://127.0.0.1:47821/agent/&lt;name&gt; (this PC only).
/// </summary>
public sealed partial class AgentService
{
    public const int Port = 47821;
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string ClaudeProjects = Path.Combine(Home, ".claude", "projects");
    private static readonly string CodexSessions = Path.Combine(Home, ".codex", "sessions");
    public static string ClaudeSettingsPath => Path.Combine(Home, ".claude", "settings.json");

    private readonly Dictionary<string, AgentSession> _sessions = new();
    private readonly Dictionary<string, DateTime> _hooked = new(); // sessions reported by hooks (trust them over file times)
    private TcpListener? _listener;

    public event Action? Changed;
    /// <summary>A session started needing you, or finished.</summary>
    public event Action<AgentSession>? Alert;

    public List<AgentSession> Sessions
    {
        get
        {
            lock (_sessions)
                return _sessions.Values
                    .Where(s => DateTime.Now - s.Updated < TimeSpan.FromHours(6))
                    .OrderBy(s => s.State switch { AgentState.NeedsYou => 0, AgentState.Working => 1, _ => 2 })
                    .ThenByDescending(s => s.Updated).ToList();
        }
    }

    public int NeedYou => Sessions.Count(s => s.State == AgentState.NeedsYou);
    public int Working => Sessions.Count(s => s.State == AgentState.Working);

    public void Start()
    {
        if (!Settings.Current.AgentsEnabled) return;
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
            _ = Task.Run(AcceptLoop);
        }
        catch (Exception ex)
        {
            Log.Error("Agent listener could not start", ex);
        }
        _ = Task.Run(ScanLoop);
    }

    // ---------------- session files ----------------

    private async Task ScanLoop()
    {
        while (true)
        {
            try { Scan(); } catch (Exception ex) { Log.Error("Agent scan", ex); }
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }

    private void Scan()
    {
        var changed = false;
        foreach (var (agent, dir) in new[] { ("Claude Code", ClaudeProjects), ("Codex", CodexSessions) })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in RecentFiles(dir))
            {
                var mtime = File.GetLastWriteTime(f);
                var id = Path.GetFileNameWithoutExtension(f);
                lock (_sessions)
                {
                    if (_hooked.ContainsKey(id)) continue;
                    var writing = DateTime.Now - mtime < TimeSpan.FromSeconds(10);
                    if (!_sessions.TryGetValue(id, out var s))
                    {
                        if (!writing) continue; // only show sessions that are active now or were seen active
                        s = new AgentSession { Id = id, Agent = agent, Folder = FolderOf(f), State = AgentState.Working, Updated = mtime };
                        _sessions[id] = s;
                        changed = true;
                    }
                    else if (writing && s.State != AgentState.Working)
                    {
                        s.State = AgentState.Working; s.Updated = mtime; changed = true;
                    }
                    else if (!writing && s.State == AgentState.Working)
                    {
                        // Without hooks we can't tell "finished" from "thinking", so no pop-up here.
                        s.State = AgentState.Done; s.Updated = mtime; s.Message = "Idle: finished or waiting for you";
                        changed = true;
                    }
                }
            }
        }
        if (changed) Changed?.Invoke();
    }

    private static IEnumerable<string> RecentFiles(string dir)
    {
        var cutoff = DateTime.Now - TimeSpan.FromMinutes(30);
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.AllDirectories); }
        catch { yield break; }
        foreach (var f in files)
        {
            DateTime t;
            try { t = File.GetLastWriteTime(f); } catch { continue; }
            if (t > cutoff) yield return f;
        }
    }

    [GeneratedRegex("\"cwd\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex CwdField();

    /// <summary>Project folder name of a session, read from the "cwd" in its file.</summary>
    private static string FolderOf(string file)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[Math.Min(fs.Length, 64 * 1024)];
            var n = fs.Read(buf, 0, buf.Length);
            var m = CwdField().Match(Encoding.UTF8.GetString(buf, 0, n));
            if (m.Success) return ShortFolder(Regex.Unescape(m.Groups[1].Value));
        }
        catch { }
        return Path.GetFileName(Path.GetDirectoryName(file) ?? "");
    }

    private static string ShortFolder(string path) =>
        string.IsNullOrEmpty(path) ? "" : Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : path;

    // ---------------- hooks / custom agents (loopback HTTP) ----------------

    private async Task AcceptLoop()
    {
        while (_listener != null)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(); }
            catch { return; }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            client.ReceiveTimeout = 3000;
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.UTF8, false, 8192, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync() ?? "";
            var parts = requestLine.Split(' ');
            int length = 0;
            string? line;
            var origin = false;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) int.TryParse(line[15..].Trim(), out length);
                // Web pages can't talk to this: browsers always send Origin on cross-site POSTs.
                if (line.StartsWith("Origin:", StringComparison.OrdinalIgnoreCase)) origin = true;
            }
            var body = new char[Math.Clamp(length, 0, 256 * 1024)];
            var read = 0;
            while (read < body.Length)
            {
                var n = await reader.ReadAsync(body, read, body.Length - read);
                if (n == 0) break;
                read += n;
            }

            var ok = !origin && parts.Length >= 2 && parts[0] == "POST" && parts[1].StartsWith("/agent/");
            if (ok) Report(Uri.UnescapeDataString(parts[1]["/agent/".Length..]), new string(body, 0, read));
            var reply = Encoding.ASCII.GetBytes(ok ? "HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"
                                                    : "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(reply);
        }
        catch (Exception ex)
        {
            Log.Error("Agent report failed", ex);
        }
    }

    /// <summary>Handles a Claude Code hook payload, or the simple {"state": …} format from other agents.</summary>
    private void Report(string agent, string json)
    {
        JsonNode? d;
        try { d = JsonNode.Parse(json); } catch { return; }
        if (d == null) return;

        var name = agent switch { "claude" => "Claude Code", "codex" => "Codex", "" => "Agent", _ => agent };
        var id = d["session_id"]?.GetValue<string>() ?? d["session"]?.GetValue<string>() ?? name;
        var folder = ShortFolder(d["cwd"]?.GetValue<string>() ?? d["folder"]?.GetValue<string>() ?? "");
        var evt = d["hook_event_name"]?.GetValue<string>();
        AgentState state;
        string msg;
        switch (evt)
        {
            case "UserPromptSubmit": state = AgentState.Working; msg = "Working on your request"; break;
            case "PreToolUse": state = AgentState.Working; msg = ToolText(d); break;
            case "Notification": state = AgentState.NeedsYou; msg = d["message"]?.GetValue<string>() ?? "Needs your input"; break;
            case "Stop": state = AgentState.Done; msg = "Done"; break;
            case "SessionEnd": state = AgentState.Done; msg = "Session ended"; break;
            case null:
                var st = (d["state"]?.GetValue<string>() ?? "working").ToLowerInvariant();
                state = st.StartsWith("need") || st == "input" ? AgentState.NeedsYou : st is "done" or "idle" or "finished" ? AgentState.Done : AgentState.Working;
                msg = d["message"]?.GetValue<string>() ?? "";
                break;
            default: return;
        }

        AgentSession s;
        bool alert;
        lock (_sessions)
        {
            _hooked[id] = DateTime.Now;
            if (!_sessions.TryGetValue(id, out s!)) _sessions[id] = s = new AgentSession { Id = id };
            s.Agent = name;
            if (folder.Length > 0) s.Folder = folder;
            alert = state != AgentState.Working && s.State != state;
            s.State = state;
            s.Message = msg;
            s.Updated = DateTime.Now;
            if (msg.Length > 0 && (s.Steps.Count == 0 || s.Steps[^1].What != msg))
            {
                s.Steps.Add((DateTime.Now, msg));
                if (s.Steps.Count > 40) s.Steps.RemoveAt(0);
            }
        }
        Changed?.Invoke();
        if (alert) Alert?.Invoke(s);
    }

    private static string ToolText(JsonNode d)
    {
        var tool = d["tool_name"]?.GetValue<string>() ?? "a tool";
        var input = d["tool_input"];
        string? detail = tool switch
        {
            "Bash" => input?["description"]?.GetValue<string>() ?? input?["command"]?.GetValue<string>(),
            "Edit" or "Write" or "Read" or "MultiEdit" => Path.GetFileName(input?["file_path"]?.GetValue<string>() ?? ""),
            "Grep" or "Glob" => input?["pattern"]?.GetValue<string>(),
            "WebFetch" => input?["url"]?.GetValue<string>(),
            _ => null,
        };
        var verb = tool switch
        {
            "Bash" => "Running", "Edit" or "MultiEdit" => "Editing", "Write" => "Writing", "Read" => "Reading",
            "Grep" or "Glob" => "Searching", "WebFetch" or "WebSearch" => "Browsing", _ => "Using " + tool,
        };
        if (string.IsNullOrWhiteSpace(detail)) return verb;
        detail = detail.Replace('\n', ' ');
        return $"{verb} {(detail.Length > 70 ? detail[..70] + "…" : detail)}";
    }

    // ---------------- Claude Code hook setup ----------------

    /// <summary>The command each hook runs: sends the hook's JSON to CyaNotch (curl ships with Windows 10 and 11).</summary>
    public static string HookCommand => $"curl -s -m 2 -X POST -H \"Content-Type: application/json\" --data-binary @- http://127.0.0.1:{Port}/agent/claude";

    private static readonly string[] HookEvents = { "UserPromptSubmit", "PreToolUse", "Notification", "Stop", "SessionEnd" };

    public static bool ClaudeHooksInstalled()
    {
        try { return File.Exists(ClaudeSettingsPath) && File.ReadAllText(ClaudeSettingsPath).Contains($"127.0.0.1:{Port}/agent/claude"); }
        catch { return false; }
    }

    /// <summary>Adds CyaNotch's hooks to ~/.claude/settings.json, keeping everything else (a backup is saved first).</summary>
    public static void InstallClaudeHooks()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ClaudeSettingsPath)!);
        JsonObject root;
        if (File.Exists(ClaudeSettingsPath))
        {
            var text = File.ReadAllText(ClaudeSettingsPath);
            File.Copy(ClaudeSettingsPath, ClaudeSettingsPath + ".before-cyanotch", overwrite: true);
            root = JsonNode.Parse(text) as JsonObject ?? new JsonObject();
        }
        else root = new JsonObject();

        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        root["hooks"] = hooks;
        foreach (var evt in HookEvents)
        {
            var list = hooks[evt] as JsonArray ?? new JsonArray();
            hooks[evt] = list;
            if (list.ToJsonString().Contains($"/agent/claude")) continue;
            var entry = new JsonObject
            {
                ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = HookCommand, ["timeout"] = 5 }),
            };
            if (evt == "PreToolUse") entry["matcher"] = "*";
            list.Add(entry);
        }
        File.WriteAllText(ClaudeSettingsPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
}
