using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CyaNotch.Core;

namespace CyaNotch.Services;

public sealed class ShelfItem
{
    public string Path { get; set; } = "";
    public string Folder { get; set; } = ShelfService.DefaultFolder;
    public DateTime Added { get; set; } = DateTime.Now;

    [JsonIgnore] public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\'));
    [JsonIgnore] public bool Exists => File.Exists(Path) || Directory.Exists(Path);

    private ImageSource? _icon;

    [JsonIgnore]
    public ImageSource? Icon => _icon ??= ShelfService.IconFor(Path);
}

/// <summary>
/// The file shelf: files dropped on the island are kept as references (nothing is moved or copied),
/// grouped into folders, and can be dragged back out into any app.
/// </summary>
public sealed class ShelfService
{
    public const string DefaultFolder = "Shelf";
    private const string FileName = "shelf.json";

    private sealed class Saved
    {
        public List<string> Folders { get; set; } = new();
        public List<ShelfItem> Items { get; set; } = new();
    }

    public ObservableCollection<string> Folders { get; } = new();
    public ObservableCollection<ShelfItem> Items { get; } = new();

    public void Load()
    {
        var s = Storage.Load<Saved>(FileName);
        Folders.Add(DefaultFolder);
        if (s != null)
        {
            foreach (var f in s.Folders.Where(f => f != DefaultFolder)) Folders.Add(f);
            foreach (var i in s.Items) Items.Add(i);
        }
    }

    public void Save() => Storage.Save(FileName, new Saved { Folders = Folders.ToList(), Items = Items.ToList() });

    public void Add(IEnumerable<string> paths, string folder)
    {
        foreach (var p in paths)
        {
            if (Items.Any(i => string.Equals(i.Path, p, StringComparison.OrdinalIgnoreCase) && i.Folder == folder)) continue;
            Items.Add(new ShelfItem { Path = p, Folder = folder });
        }
        Save();
    }

    public void Remove(ShelfItem item)
    {
        Items.Remove(item);
        Save();
    }

    public void ClearFolder(string folder)
    {
        foreach (var i in Items.Where(i => i.Folder == folder).ToList()) Items.Remove(i);
        Save();
    }

    public string AddFolder(string name)
    {
        name = string.IsNullOrWhiteSpace(name) ? "Folder" : name.Trim();
        var unique = name;
        for (int n = 2; Folders.Contains(unique); n++) unique = $"{name} {n}";
        Folders.Add(unique);
        Save();
        return unique;
    }

    public void DeleteFolder(string folder)
    {
        if (folder == DefaultFolder) return;
        foreach (var i in Items.Where(i => i.Folder == folder)) i.Folder = DefaultFolder;
        Folders.Remove(folder);
        Save();
    }

    public void Move(ShelfItem item, string folder)
    {
        item.Folder = folder;
        var idx = Items.IndexOf(item);
        Items.RemoveAt(idx);
        Items.Insert(idx, item);
        Save();
    }

    internal static ImageSource? IconFor(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                if (ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp")
                {
                    var b = new BitmapImage();
                    b.BeginInit();
                    b.CacheOption = BitmapCacheOption.OnLoad;
                    b.DecodePixelWidth = 96;
                    b.UriSource = new Uri(path);
                    b.EndInit();
                    b.Freeze();
                    return b;
                }
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon == null) return null;
                var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
        }
        catch
        {
            // fall through
        }
        return null;
    }
}
