using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media.Imaging;
using CyaNotch.Core;

namespace CyaNotch.Services;

public enum ClipKind { Text, Link, Image, Files }

public sealed class ClipItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ClipKind Kind { get; set; }
    public string Text { get; set; } = "";
    public string? ImageFile { get; set; }
    public List<string> Files { get; set; } = new();
    public bool Pinned { get; set; }
    public DateTime Time { get; set; } = DateTime.Now;

    [JsonIgnore]
    public string Preview => Kind switch
    {
        ClipKind.Files => Files.Count == 1 ? Path.GetFileName(Files[0]) : $"{Files.Count} files: {string.Join(", ", Files.Select(Path.GetFileName))}",
        ClipKind.Image => "Image",
        _ => Text.Length > 300 ? Text[..300] + "…" : Text.Replace("\r", "").Trim(),
    };

    [JsonIgnore]
    public string Glyph => Kind switch
    {
        ClipKind.Link => "",
        ClipKind.Image => "",
        ClipKind.Files => "",
        _ => "",
    };

    [JsonIgnore]
    public BitmapImage? Thumbnail
    {
        get
        {
            if (Kind != ClipKind.Image || ImageFile == null || !File.Exists(ImageFile)) return null;
            try
            {
                var b = new BitmapImage();
                b.BeginInit();
                b.CacheOption = BitmapCacheOption.OnLoad;
                b.DecodePixelHeight = 120;
                b.UriSource = new Uri(ImageFile);
                b.EndInit();
                b.Freeze();
                return b;
            }
            catch { return null; }
        }
    }

    [JsonIgnore] public string When => Time.Date == DateTime.Today ? Time.ToString("t") : Time.ToString("MMM d");

    [JsonIgnore] public bool IsText => Kind != ClipKind.Image;

    [JsonIgnore] public string KindLabel => Kind switch { ClipKind.Link => "Link", ClipKind.Image => "Image", ClipKind.Files => "File", _ => "Text" };

    /// <summary>Short age for the card header: now, 5m, 2h, 3d.</summary>
    [JsonIgnore]
    public string Age
    {
        get
        {
            var a = DateTime.Now - Time;
            if (a.TotalMinutes < 1) return "now";
            if (a.TotalHours < 1) return $"{(int)a.TotalMinutes}m";
            if (a.TotalDays < 1) return $"{(int)a.TotalHours}h";
            return $"{(int)a.TotalDays}d";
        }
    }

    /// <summary>Line under the card: image size, file folder, or link host.</summary>
    [JsonIgnore]
    public string Detail
    {
        get
        {
            try
            {
                return Kind switch
                {
                    ClipKind.Image when Thumbnail is { } t && ImageFile != null => ImageSize(ImageFile),
                    ClipKind.Files when Files.Count > 0 => Files.Count == 1 ? Path.GetFileName(Path.GetDirectoryName(Files[0])) ?? "" : $"{Files.Count} files",
                    ClipKind.Link when Uri.TryCreate(Text.Trim(), UriKind.Absolute, out var u) => u.Host,
                    _ => $"{Text.Length} characters",
                };
            }
            catch { return ""; }
        }
    }

    private static string ImageSize(string file)
    {
        var d = BitmapFrame.Create(new Uri(file), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        return $"{d.PixelWidth} × {d.PixelHeight}";
    }

    private static System.Windows.Media.SolidColorBrush Frozen(byte a, uint rgb)
    {
        var b = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(a, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        b.Freeze();
        return b;
    }

    private uint KindRgb => Kind switch { ClipKind.Link => 0x2EC4C4u, ClipKind.Image => 0x3DDC84u, ClipKind.Files => 0xF5A623u, _ => 0x4FA3FFu };

    /// <summary>Colour of the card's label, by kind.</summary>
    [JsonIgnore] public System.Windows.Media.Brush KindBrush => Frozen(0xFF, KindRgb);

    /// <summary>Tinted strip behind the card's label.</summary>
    [JsonIgnore] public System.Windows.Media.Brush HeaderBrush => Frozen(0x30, KindRgb);
}

/// <summary>Clipboard history: text, links, images and files. Everything stays in %AppData%\CyaNotch.</summary>
public sealed class ClipboardService
{
    private const string FileName = "clipboard.json";
    private static readonly string ImageFolder = Path.Combine(Settings.Folder, "clips");
    private bool _suppressNext;

    public ObservableCollection<ClipItem> Items { get; } = new();

    public void Load()
    {
        var saved = Storage.Load<List<ClipItem>>(FileName);
        if (saved != null) foreach (var i in saved) Items.Add(i);
        Items.CollectionChanged += OnChanged;
    }

    private void OnChanged(object? sender, NotifyCollectionChangedEventArgs e) => Save();

    public void Save() => Storage.Save(FileName, Items.ToList());

    /// <summary>Called on WM_CLIPBOARDUPDATE (UI thread). Retries briefly if another app has the clipboard open.</summary>
    public void Capture()
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                CaptureOnce();
                return;
            }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 3)
            {
                System.Threading.Thread.Sleep(40);
            }
            catch (System.Runtime.InteropServices.ExternalException ex)
            {
                Log.Error("Clipboard busy, skipped one copy", ex);
                return;
            }
        }
    }

    private void CaptureOnce()
    {
        if (_suppressNext)
        {
            _suppressNext = false;
            return;
        }

        try
        {
            ClipItem? item = null;
            var data = Clipboard.GetDataObject();
            if (data == null) return;

            // Respect apps (password managers) that ask clipboard history to skip their data.
            if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing") ||
                data.GetDataPresent("Clipboard Viewer Ignore"))
                return;

            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>().ToList();
                if (files.Count > 0) item = new ClipItem { Kind = ClipKind.Files, Files = files, Text = string.Join("\n", files) };
            }
            else if (Clipboard.ContainsImage())
            {
                var img = Clipboard.GetImage();
                if (img != null)
                {
                    Directory.CreateDirectory(ImageFolder);
                    var path = Path.Combine(ImageFolder, $"{DateTime.Now:yyyyMMdd-HHmmssfff}.png");
                    using (var fs = File.Create(path))
                    {
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(img));
                        enc.Save(fs);
                    }
                    item = new ClipItem { Kind = ClipKind.Image, ImageFile = path };
                }
            }
            else if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var trimmed = text.Trim();
                    var isLink = Uri.TryCreate(trimmed, UriKind.Absolute, out var u) &&
                                 (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && !trimmed.Contains(' ');
                    item = new ClipItem { Kind = isLink ? ClipKind.Link : ClipKind.Text, Text = text };
                }
            }

            if (item == null) return;

            // Same as an existing entry? Move it to the top instead of duplicating.
            var dup = Items.FirstOrDefault(i => i.Kind == item.Kind && i.Kind != ClipKind.Image && i.Text == item.Text);
            if (dup != null)
            {
                dup.Time = DateTime.Now;
                Items.Move(Items.IndexOf(dup), 0);
                return;
            }

            Items.Insert(0, item);
            Trim();
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            throw; // clipboard locked by another app: Capture() retries
        }
        catch (Exception ex)
        {
            Log.Error("Clipboard capture failed", ex);
        }
    }

    private void Trim()
    {
        var limit = Math.Max(20, Settings.Current.ClipboardLimit);
        while (Items.Count(i => !i.Pinned) > limit)
        {
            var last = Items.Last(i => !i.Pinned);
            Delete(last);
        }
    }

    /// <summary>Puts an item back on the clipboard (without re-recording it).</summary>
    public void Restore(ClipItem item)
    {
        _suppressNext = true;
        try
        {
            switch (item.Kind)
            {
                case ClipKind.Files:
                    var col = new System.Collections.Specialized.StringCollection();
                    col.AddRange(item.Files.Where(File.Exists).Concat(item.Files.Where(Directory.Exists)).Distinct().ToArray());
                    Clipboard.SetFileDropList(col);
                    break;
                case ClipKind.Image when item.Thumbnail != null && item.ImageFile != null:
                    var full = new BitmapImage(new Uri(item.ImageFile));
                    Clipboard.SetImage(full);
                    break;
                default:
                    Clipboard.SetText(item.Text);
                    break;
            }
            item.Time = DateTime.Now;
            if (!item.Pinned) Items.Move(Items.IndexOf(item), 0);
        }
        catch (Exception ex)
        {
            _suppressNext = false;
            Log.Error("Clipboard restore failed", ex);
        }
    }

    public void TogglePin(ClipItem item)
    {
        item.Pinned = !item.Pinned;
        // Re-insert to refresh list bindings.
        var idx = Items.IndexOf(item);
        Items.RemoveAt(idx);
        Items.Insert(item.Pinned ? 0 : idx, item);
    }

    public void Delete(ClipItem item)
    {
        Items.Remove(item);
        if (item.ImageFile != null)
        {
            try { File.Delete(item.ImageFile); } catch { /* ignore */ }
        }
    }

    public void ClearUnpinned()
    {
        foreach (var i in Items.Where(i => !i.Pinned).ToList()) Delete(i);
    }
}
