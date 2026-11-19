using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using CyaNotch.Core;

namespace CyaNotch.Services;

/// <summary>
/// Phone remote: the phone's browser becomes a trackpad, media remote, key pad and app launcher.
/// Off by default. Each phone must be approved on the PC once before it can do anything.
/// </summary>
public sealed class DeckService
{
    private readonly PhoneServer _server;
    private string? _key;
    private readonly ConcurrentDictionary<string, byte> _asking = new();

    public DeckService(PhoneServer server) => _server = server;

    public bool Active => _key != null;
    public string? Url => _key == null ? null : _server.UrlFor(_key);

    public string Start()
    {
        if (_key != null) return _server.UrlFor(_key);
        _server.Start();
        // Keep the same key across restarts so phones keep their bookmark.
        _key = Settings.Current.DeckKey ??= PhoneServer.NewKey();
        Settings.Save();
        _server.MountAs(_key, HandleAsync);
        return _server.UrlFor(_key);
    }

    public void Stop()
    {
        if (_key == null) return;
        _server.Unmount(_key);
        _key = null;
        if (!_server.HasMounts) _server.Stop();
    }

    /// <summary>New key: every phone has to scan again.</summary>
    public void ResetKey()
    {
        var wasActive = Active;
        Stop();
        Settings.Current.DeckKey = null;
        Settings.Current.ApprovedPhones.Clear();
        Settings.Save();
        if (wasActive) Start();
    }

    private async Task<bool> HandleAsync(PhoneServer.Request req, PhoneServer.Response res)
    {
        if (req.Method == "GET" && req.Path == "/")
        {
            await res.TextAsync(WebAssets.Load("deck.html"), "text/html; charset=utf-8");
            return true;
        }

        var device = req.Headers.GetValueOrDefault("X-Device") ?? "";
        if (device.Length < 8 || device.Length > 64)
        {
            await res.TextAsync("missing device", status: 400);
            return true;
        }

        if (!Settings.Current.ApprovedPhones.Contains(device))
        {
            AskApproval(device, req.Headers.GetValueOrDefault("X-Device-Name") ?? "A phone", req.Remote.ToString());
            await res.TextAsync("waiting", status: 403);
            return true;
        }

        try
        {
            switch (req.Path)
            {
                case "/hello":
                    await res.TextAsync("ok");
                    return true;
                case "/move":
                    Native.Mouse(Native.MOUSEEVENTF_MOVE, Int(req.Q("dx")), Int(req.Q("dy")));
                    break;
                case "/click":
                    if (req.Q("b") == "right")
                    {
                        Native.Mouse(Native.MOUSEEVENTF_RIGHTDOWN);
                        Native.Mouse(Native.MOUSEEVENTF_RIGHTUP);
                    }
                    else
                    {
                        Native.Mouse(Native.MOUSEEVENTF_LEFTDOWN);
                        Native.Mouse(Native.MOUSEEVENTF_LEFTUP);
                    }
                    break;
                case "/down":
                    Native.Mouse(Native.MOUSEEVENTF_LEFTDOWN);
                    break;
                case "/up":
                    Native.Mouse(Native.MOUSEEVENTF_LEFTUP);
                    break;
                case "/scroll":
                    Native.Mouse(Native.MOUSEEVENTF_WHEEL, data: Int(req.Q("d")));
                    break;
                case "/key":
                    Key(req.Q("k"));
                    break;
                case "/type":
                    Native.TypeText(req.Q("text"));
                    break;
                case "/apps":
                    await res.TextAsync(JsonSerializer.Serialize(Settings.Current.DeckApps.Select((a, i) => new { i, name = a.Name })), "application/json");
                    return true;
                case "/launch":
                    var i = Int(req.Q("i"));
                    if (i >= 0 && i < Settings.Current.DeckApps.Count)
                        Process.Start(new ProcessStartInfo(Settings.Current.DeckApps[i].Path) { UseShellExecute = true });
                    break;
                case "/now":
                    var m = App.Media.State;
                    await res.TextAsync(JsonSerializer.Serialize(new { m.HasSession, m.IsPlaying, m.Title, m.Artist, m.AppName }), "application/json");
                    return true;
                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Deck command failed", ex);
        }
        await res.SendAsync(204, "text/plain", Array.Empty<byte>());
        return true;
    }

    private static int Int(string s) => int.TryParse(s, out var v) ? Math.Clamp(v, -4000, 4000) : 0;

    private static void Key(string k)
    {
        switch (k)
        {
            case "playpause": Native.Tap(0xB3); break;
            case "next": Native.Tap(0xB0); break;
            case "prev": Native.Tap(0xB1); break;
            case "volup": Native.Tap(0xAF); break;
            case "voldown": Native.Tap(0xAE); break;
            case "mute": Native.Tap(0xAD); break;
            case "left": Native.Tap(0x25, true); break;
            case "up": Native.Tap(0x26, true); break;
            case "right": Native.Tap(0x27, true); break;
            case "down": Native.Tap(0x28, true); break;
            case "space": Native.Tap(0x20); break;
            case "enter": Native.Tap(0x0D); break;
            case "esc": Native.Tap(0x1B); break;
            case "tab": Native.Tap(0x09); break;
            case "back": Native.Tap(0x08); break;
            case "f": Native.Tap(0x46); break;           // fullscreen in most players
            case "f5": Native.Tap(0x74); break;          // start slideshow
            case "pgup": Native.Tap(0x21, true); break;
            case "pgdn": Native.Tap(0x22, true); break;
            case "alttab": Native.SendChord(0x12, 0x09); break;
            case "win": Native.Tap(Native.VK_LWIN); break;
            case "desktop": Native.SendChord(Native.VK_LWIN, 0x44); break;
        }
    }

    private void AskApproval(string device, string name, string ip)
    {
        if (!_asking.TryAdd(device, 0)) return;
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var answer = MessageBox.Show(
                    $"{name} ({ip}) wants to control this PC as a remote (mouse, keyboard, media, apps).\n\nAllow it?",
                    "CyaNotch phone remote", MessageBoxButton.YesNo, MessageBoxImage.Question,
                    MessageBoxResult.No, MessageBoxOptions.DefaultDesktopOnly);
                if (answer == MessageBoxResult.Yes)
                {
                    Settings.Current.ApprovedPhones.Add(device);
                    Settings.Save();
                }
            }
            finally
            {
                // Allow asking again later if it was declined.
                _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => _asking.TryRemove(device, out byte _));
            }
        });
    }
}
