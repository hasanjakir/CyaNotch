using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CyaNotch.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CyaNotch.Views.Tabs;

/// <summary>
/// Web drawer: pinned sites open right inside the island (Microsoft Edge WebView2).
/// Sign-ins and cookies are kept in CyaNotch's own folder, separate from your browser.
/// </summary>
public partial class WebPage : UserControl
{
    private WebView2? _web;
    private string? _current;

    public WebPage()
    {
        InitializeComponent();
        Loaded += (_, _) => BuildSites();
    }

    private void BuildSites()
    {
        Sites.Children.Clear();
        foreach (var site in Settings.Current.WebSites)
        {
            var icon = new Image { Width = 18, Height = 18, Source = Favicon(site.Url) };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(icon);
            content.Children.Add(new TextBlock { Text = site.Name, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var b = new Button { Style = (Style)FindResource("PillButton"), Content = content, Margin = new Thickness(0, 0, 6, 0), ToolTip = site.Url };
            b.Click += (_, _) => _ = OpenAsync(site.Url);
            var menu = new ContextMenu();
            var remove = new MenuItem { Header = "Unpin" };
            remove.Click += (_, _) => { Settings.Current.WebSites.Remove(site); Settings.SaveQuietly(); BuildSites(); };
            menu.Items.Add(remove);
            b.ContextMenu = menu;
            Sites.Children.Add(b);
        }
    }

    private static ImageSource? Favicon(string url)
    {
        try
        {
            var u = new Uri(url);
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri($"{u.Scheme}://{u.Host}/favicon.ico");
            img.DecodePixelWidth = 32;
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.EndInit();
            return img;
        }
        catch
        {
            return null;
        }
    }

    private async Task OpenAsync(string url)
    {
        _current = url;
        try
        {
            if (_web == null)
            {
                Hint.Text = "Loading…";
                var folder = Path.Combine(Settings.Folder, "WebView");
                var env = await CoreWebView2Environment.CreateAsync(null, folder);
                _web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.Transparent };
                Host.Children.Add(_web);
                await _web.EnsureCoreWebView2Async(env);
                // Links that open a new window go to the normal browser.
                _web.CoreWebView2.NewWindowRequested += (_, e) =>
                {
                    e.Handled = true;
                    Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true });
                };
            }
            Hint.Visibility = Visibility.Collapsed;
            _web.Source = new Uri(url);
        }
        catch (Exception ex)
        {
            Log.Error("Web drawer failed", ex);
            Hint.Visibility = Visibility.Visible;
            Hint.Text = "This needs the Microsoft Edge WebView2 Runtime (built into Windows 11).\nGet it from microsoft.com/edge/webview2.";
        }
    }

    private void NewUrl_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var text = NewUrl.Text.Trim();
        if (text.Length == 0) return;
        if (!text.Contains("://")) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http")) return;
        var name = uri.Host.StartsWith("www.") ? uri.Host[4..] : uri.Host;
        name = char.ToUpper(name[0]) + name[1..].Split('.')[0];
        Settings.Current.WebSites.Add(new WebSite { Name = name, Url = uri.ToString() });
        Settings.SaveQuietly();
        NewUrl.Clear();
        BuildSites();
        _ = OpenAsync(uri.ToString());
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => _web?.Reload();

    private void OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        var url = _web?.Source?.ToString() ?? _current;
        if (url != null) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
