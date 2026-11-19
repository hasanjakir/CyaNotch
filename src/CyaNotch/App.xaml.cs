using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using CyaNotch.Core;
using CyaNotch.Services;
using CyaNotch.Views;
using Microsoft.Win32;

namespace CyaNotch;

public partial class App : Application
{
    private Mutex? _single;
    private bool _ownsMutex;
    private System.Windows.Forms.NotifyIcon? _tray;
    private IslandWindow? _island;
    private SettingsWindow? _settings;

    public static MediaService Media { get; } = new();
    public static AudioService Audio { get; } = new();
    public static ClipboardService Clipboard { get; } = new();
    public static ShelfService Shelf { get; } = new();
    public static NotificationService Notifications { get; } = new();
    public static SystemService Sys { get; } = new();
    public static ReminderService Reminders { get; } = new();
    public static WeatherService Weather { get; } = new();
    public static VisualizerService Visualizer { get; } = new();
    public static LyricsService Lyrics { get; } = new();
    public static GameService Game { get; } = new();
    public static PhoneServer Phone { get; } = new();
    public static DeckService Deck { get; } = new(Phone);

    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        _single = new Mutex(true, @"Local\CyaNotch.SingleInstance", out var isNew);
        if (!isNew && !ReplaceRunningCopy())
        {
            Shutdown();
            return;
        }
        _ownsMutex = true;

        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Error("Unhandled", a.ExceptionObject as Exception);

        base.OnStartup(e);
        Settings.Load();
        Themes.Apply(Settings.Current.Theme);
        ApplyStartup(Settings.Current.StartWithWindows);

        Clipboard.Load();
        Shelf.Load();
        Notifications.Load();
        Reminders.Load();

        _island = new IslandWindow();
        _island.Show();
        WidgetWindow.RestoreAll();

        CreateTray();
        Log.Info("CyaNotch started");
    }

    /// <summary>
    /// Another CyaNotch is already running (often an older build). Offer to close it and carry on
    /// with this one, instead of exiting silently.
    /// </summary>
    private bool ReplaceRunningCopy()
    {
        var answer = MessageBox.Show(
            "CyaNotch is already running.\n\nClose the running copy and start this one instead?",
            "CyaNotch", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (answer != MessageBoxResult.Yes) return false;

        var me = global::System.Diagnostics.Process.GetCurrentProcess();
        foreach (var p in global::System.Diagnostics.Process.GetProcessesByName(me.ProcessName))
        {
            if (p.Id == me.Id) continue;
            try
            {
                p.Kill();
                p.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                Log.Error("Could not close the running copy", ex);
            }
        }

        // Take over the single-instance lock now that the old copy is gone.
        try
        {
            if (_single!.WaitOne(TimeSpan.FromSeconds(5))) return true;
        }
        catch (AbandonedMutexException)
        {
            return true; // the old copy was killed while holding it; we own it now
        }
        MessageBox.Show("Couldn't close the running copy. Quit it from the tray icon and try again.", "CyaNotch");
        return false;
    }

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI exception", e.Exception);
        e.Handled = true; // keep the island alive; details go to cyanotch.log
    }

    private void CreateTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "CyaNotch",
            Visible = true,
            Icon = LoadTrayIcon(),
        };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Settings", null, (_, _) => ShowSettings());
        menu.Items.Add("Show / hide island", null, (_, _) => _island?.ToggleHidden());
        menu.Items.Add("Open data folder", null, (_, _) =>
            global::System.Diagnostics.Process.Start("explorer.exe", Settings.Folder));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Quit CyaNotch", null, (_, _) => Quit());
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, a) =>
        {
            if (a.Button == System.Windows.Forms.MouseButtons.Left) ShowSettings();
        };
    }

    private static global::System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var info = GetResourceStream(new Uri("pack://application:,,,/Assets/cyanotch.ico"));
            if (info != null) return new global::System.Drawing.Icon(info.Stream);
        }
        catch { }
        return global::System.Drawing.SystemIcons.Application;
    }

    public void ShowSettings()
    {
        if (_settings == null || !_settings.IsLoaded)
        {
            _settings = new SettingsWindow();
            _settings.Closed += (_, _) => _settings = null;
            _settings.Show();
        }
        _settings.Activate();
    }

    public void Quit()
    {
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _island?.Shutdown();
        Clipboard.Save();
        Audio.Dispose();
        Visualizer.Dispose();
        Game.Dispose();
        Phone.Dispose();
        Shutdown();
    }

    public static void ApplyStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null) return;
            if (enable)
                key.SetValue("CyaNotch", $"\"{Environment.ProcessPath}\"");
            else if (key.GetValue("CyaNotch") != null)
                key.DeleteValue("CyaNotch");
        }
        catch (Exception ex)
        {
            Log.Error("Start with Windows failed", ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex) _single?.ReleaseMutex();
        base.OnExit(e);
    }
}
