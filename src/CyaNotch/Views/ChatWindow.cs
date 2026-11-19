using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CyaNotch.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CyaNotch.Views;

/// <summary>
/// Stream chat in a small always-on-top drawer you can drag anywhere (Twitch or YouTube live chat).
/// Uses each site's own pop-out chat page.
/// </summary>
public sealed class ChatWindow : Window
{
    private static ChatWindow? _open;

    public static void Toggle()
    {
        if (_open != null) { _open.Close(); return; }
        var url = ChatUrl();
        if (url == null)
        {
            MessageBox.Show("Set a Twitch channel or YouTube live link on the Stream tab first.", "CyaNotch");
            return;
        }
        _open = new ChatWindow(url);
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    public static bool IsOpen => _open != null;

    /// <summary>Pop-out chat page for the platform and channel in Settings.</summary>
    private static string? ChatUrl()
    {
        var ch = Settings.Current.ChatChannel.Trim();
        if (ch.Length == 0) return null;
        if (Settings.Current.ChatPlatform == "youtube")
        {
            // Accept a full link (watch?v=, youtu.be/, /live/) or a bare video id.
            var id = ch;
            if (Uri.TryCreate(ch, UriKind.Absolute, out var u))
            {
                var q = System.Web.HttpUtility.ParseQueryString(u.Query)["v"];
                id = q ?? u.Segments[^1].Trim('/');
            }
            return $"https://www.youtube.com/live_chat?is_popout=1&v={Uri.EscapeDataString(id)}";
        }
        var name = ch.TrimStart('@').Split('/')[^1];
        return $"https://www.twitch.tv/popout/{Uri.EscapeDataString(name)}/chat?popout=";
    }

    private ChatWindow(string url)
    {
        Title = "CyaNotch chat";
        Width = Settings.Current.ChatWidth;
        Height = Settings.Current.ChatHeight;
        Left = double.IsNaN(Settings.Current.ChatLeft) ? SystemParameters.WorkArea.Right - Width - 24 : Settings.Current.ChatLeft;
        Top = double.IsNaN(Settings.Current.ChatTop) ? SystemParameters.WorkArea.Top + 80 : Settings.Current.ChatTop;
        Topmost = true;
        ShowInTaskbar = false;
        WindowStyle = WindowStyle.ToolWindow;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        Background = new SolidColorBrush(Color.FromRgb(0x14, 0x15, 0x18));

        var web = new WebView2();
        Content = web;
        Loaded += async (_, _) =>
        {
            try
            {
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Settings.Folder, "WebView"));
                await web.EnsureCoreWebView2Async(env);
                web.Source = new Uri(url);
            }
            catch (Exception ex)
            {
                Log.Error("Chat drawer failed", ex);
                Content = new TextBlock { Text = "Chat needs the Microsoft Edge WebView2 Runtime.", Foreground = Brushes.White, Margin = new Thickness(16) };
            }
        };
        Closing += (_, _) =>
        {
            Settings.Current.ChatLeft = Left; Settings.Current.ChatTop = Top;
            Settings.Current.ChatWidth = Width; Settings.Current.ChatHeight = Height;
            Settings.SaveQuietly();
        };
    }
}
