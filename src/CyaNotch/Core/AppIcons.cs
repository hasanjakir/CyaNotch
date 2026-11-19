using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CyaNotch.Core;

/// <summary>Icons of running apps, for the badge on the music card's artwork.</summary>
public static class AppIcons
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The icon of the app with this media id, or null if it can't be found (e.g. Store apps).</summary>
    public static ImageSource? ForMediaApp(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return null;
        lock (Cache) if (Cache.TryGetValue(appId, out var hit)) return hit;

        ImageSource? icon = null;
        try
        {
            // Desktop apps report their exe ("Spotify.exe", "chrome"); Store apps report "Package!App".
            if (!appId.Contains('!'))
            {
                var name = Path.GetFileNameWithoutExtension(appId);
                var path = Process.GetProcessesByName(name)
                    .Select(p => { try { return p.MainModule?.FileName; } catch { return null; } })
                    .FirstOrDefault(f => !string.IsNullOrEmpty(f));
                if (path != null) icon = FromFile(path);
            }
        }
        catch (Exception ex)
        {
            Log.Error("App icon lookup failed", ex);
        }

        lock (Cache) Cache[appId] = icon;
        return icon;
    }

    public static ImageSource? FromFile(string path)
    {
        using var ico = System.Drawing.Icon.ExtractAssociatedIcon(path);
        if (ico == null) return null;
        var src = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        src.Freeze();
        return src;
    }
}
