using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CyaNotch.Core;

namespace CyaNotch.Views.Tabs;

/// <summary>App launcher: a grid of shortcuts, with Ctrl+Alt+1..9 for the first nine.</summary>
public partial class LauncherPage : UserControl
{
    private sealed record Row(DeckApp App, string Name, string Path, ImageSource? Icon, string Key);

    public LauncherPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    public void Refresh()
    {
        var list = Settings.Current.LauncherApps;
        Apps.ItemsSource = list.Select((a, i) => new Row(a, a.Name, a.Path, Icon(a.Path), i < 9 ? $"{i + 1}" : "")).ToList();
        Empty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static ImageSource? Icon(string path)
    {
        try { return File.Exists(path) ? AppIcons.FromFile(path) : null; }
        catch { return null; }
    }

    /// <summary>Opens launcher app number <paramref name="index"/> (0-based). Used by the hotkeys.</summary>
    public static void Launch(int index)
    {
        var list = Settings.Current.LauncherApps;
        if (index < 0 || index >= list.Count) return;
        try { Process.Start(new ProcessStartInfo(list[index].Path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("Launcher could not open " + list[index].Path, ex); }
    }

    private void App_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Row r)
            Launch(Settings.Current.LauncherApps.IndexOf(r.App));
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Row r) return;
        Settings.Current.LauncherApps.Remove(r.App);
        Settings.SaveQuietly();
        Refresh();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add an app to the launcher",
            Filter = "Apps and shortcuts|*.exe;*.lnk;*.url;*.bat;*.cmd|All files|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Multiselect = true,
        };
        if (dlg.ShowDialog() == true) AddPaths(dlg.FileNames);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) AddPaths(files);
        e.Handled = true;
    }

    private void AddPaths(string[] paths)
    {
        foreach (var p in paths)
            if (!Settings.Current.LauncherApps.Any(a => string.Equals(a.Path, p, StringComparison.OrdinalIgnoreCase)))
                Settings.Current.LauncherApps.Add(new DeckApp { Name = System.IO.Path.GetFileNameWithoutExtension(p), Path = p });
        Settings.SaveQuietly();
        Refresh();
    }
}
