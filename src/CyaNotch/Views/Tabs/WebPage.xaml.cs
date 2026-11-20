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

    /// <summary>True while a site is open; the island makes room for the browser.</summary>
    public bool Browsing => BrowserView.Visibility == Visibility.Visible;

    /// <summary>Raised when the page switches between the site list and the browser.</summary>
    public event Action? BrowsingChanged;

    public WebPage()
    {
        InitializeComponent();
        Loaded += (_, _) => BuildSites();
    }

    private static readonly Color[] TileColours =
    {
        Color.FromRgb(0xE5, 0x39, 0x35), Color.FromRgb(0x1E, 0x88, 0xE5), Color.FromRgb(0x1D, 0xB9, 0x54),
        Color.FromRgb(0x8E, 0x24, 0xAA), Color.FromRgb(0xF5, 0x7C, 0x00), Color.FromRgb(0x00, 0x89, 0x7B),
    };

    private void BuildSites()
    {
        Sites.Children.Clear();
        var i = 0;
        foreach (var site in Settings.Current.WebSites)
        {
            var colour = TileColours[i++ % TileColours.Length];
            var icon = new Image { Width = 16, Height = 16, Source = Favicon(site.Url) };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            var badge = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(0x40, colour.R, colour.G, colour.B)),
                Child = icon, VerticalAlignment = VerticalAlignment.Center,
            };
            var host = Uri.TryCreate(site.Url, UriKind.Absolute, out var u) ? u.Host.Replace("www.", "") : site.Url;
            var text = new StackPanel { Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = site.Name, FontSize = 11.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 64 });
            text.Children.Add(new TextBlock { Text = host, FontSize = 10, Foreground = (Brush)FindResource("SubTextBrush"), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 64 });
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(badge);
            row.Children.Add(text);
            var tile = new Border
            {
                Width = 116, Height = 42, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(7, 0, 5, 0),
                Background = new SolidColorBrush(Color.FromArgb(0x26, colour.R, colour.G, colour.B)),
                Child = row, Cursor = Cursors.Hand, ToolTip = site.Url,
            };
            tile.MouseLeftButtonUp += (_, _) => _ = OpenAsync(site.Url, site.Name);
            var menu = new ContextMenu();
            var remove = new MenuItem { Header = "Unpin" };
            remove.Click += (_, _) => { Settings.Current.WebSites.Remove(site); Settings.SaveQuietly(); BuildSites(); };
            menu.Items.Add(remove);
            tile.ContextMenu = menu;
            Sites.Children.Add(tile);
        }

        // "+ Add a site": turns into a box to type the address
        var add = new Border
        {
            Width = 116, Height = 42, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 6, 6),
            BorderBrush = (Brush)FindResource("TrackBrush"), BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
        };
        var label = new TextBlock { Text = "+  Add a site", FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("SubTextBrush") };
        add.Child = label;
        add.MouseLeftButtonUp += (_, _) =>
        {
            if (add.Child is TextBox) return;
            var box = new TextBox { Style = (Style)FindResource("Field"), Tag = "example.com", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) { BuildSites(); return; }
                if (e.Key == Key.Enter) AddSite(box.Text);
            };
            box.LostKeyboardFocus += (_, _) => { if (box.Text.Trim().Length == 0) BuildSites(); };
            add.Child = box;
            box.Focus();
        };
        Sites.Children.Add(add);
    }

    private void AddSite(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        if (!text.Contains("://")) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http")) return;
        var name = uri.Host.StartsWith("www.") ? uri.Host[4..] : uri.Host;
        name = char.ToUpper(name[0]) + name[1..].Split('.')[0];
        Settings.Current.WebSites.Add(new WebSite { Name = name, Url = uri.ToString() });
        Settings.SaveQuietly();
        BuildSites();
    }

    private void ShowBrowser(bool on)
    {
        if (Browsing == on) return;
        BrowserView.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        SitesView.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        BrowsingChanged?.Invoke();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => ShowBrowser(false);

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

    private async Task OpenAsync(string url, string name)
    {
        _current = url;
        BrowserTitle.Text = name;
        ShowBrowser(true);
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

    private void Reload_Click(object sender, RoutedEventArgs e) => _web?.Reload();

    private void OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        var url = _web?.Source?.ToString() ?? _current;
        if (url != null) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
