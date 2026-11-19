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

    /// <summary>Called on WM_CLIPBOARDUPDATE (UI thread).</summary>
    public void Capture()
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
        catch (Exception ex)
        {
            // The clipboard is often locked by another app for a moment; just skip.
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
