using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using CyaNotch.Core;

namespace CyaNotch.Services;

/// <summary>
/// Screenshots, snips and screen recordings from the island.
/// Screenshots are saved by CyaNotch; snips use Windows' Snipping Tool; recordings use the
/// Xbox Game Bar recorder (records the active app with its sound), started and stopped by its shortcut.
/// </summary>
public sealed class CaptureService
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "CyaNotch");
    public static string RecordingsFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Captures");

    public bool Recording { get; private set; }
    public DateTime RecordingSince { get; private set; }
    public event Action? Changed;

    /// <summary>Saves the screen under the pointer as a PNG and returns its path.</summary>
    public string? Screenshot()
    {
        try
        {
            var screen = Screen.FromPoint(Cursor.Position).Bounds;
            using var bmp = new Bitmap(screen.Width, screen.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(screen.Left, screen.Top, 0, 0, screen.Size);
            Directory.CreateDirectory(Folder);
            var path = Path.Combine(Folder, $"Screenshot {DateTime.Now:yyyy-MM-dd HHmmss}.png");
            bmp.Save(path, ImageFormat.Png);
            if (Settings.Current.CapturesOnShelf) App.Shelf.Add(new[] { path }, "Captures");
            return path;
        }
        catch (Exception ex)
        {
            Log.Error("Screenshot failed", ex);
            return null;
        }
    }

    public void Snip()
    {
        try { Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("Snip failed", ex); }
    }

    /// <summary>Starts or stops a Game Bar recording (Win+Alt+R).</summary>
    public void ToggleRecording()
    {
        Native.SendChord(Native.VK_LWIN, 0x12 /* Alt */, 0x52 /* R */);
        Recording = !Recording;
        if (Recording) RecordingSince = DateTime.Now;
        else OpenRecordings();
        Changed?.Invoke();
    }

    public static void OpenRecordings()
    {
        var dir = Directory.Exists(RecordingsFolder) ? RecordingsFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        try { Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true }); } catch { }
    }
}
