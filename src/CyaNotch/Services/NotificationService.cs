using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using CyaNotch.Core;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace CyaNotch.Services;

public sealed class NoteItem
{
    public uint Id { get; set; }
    public string App { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTime Time { get; set; } = DateTime.Now;
    [JsonIgnore] public string When => Time.Date == DateTime.Today ? Time.ToString("t") : Time.ToString("MMM d, t");
}

/// <summary>
/// Mirrors Windows toast notifications into the island and keeps a local history list.
/// Uses UserNotificationListener; Windows asks once for permission ("Allow access to notifications").
/// </summary>
public sealed class NotificationService
{
    private const string FileName = "notifications.json";
    private const int Max = 300;
    private UserNotificationListener? _listener;
    private readonly HashSet<uint> _seen = new();
    private System.Threading.Timer? _poll;

    public ObservableCollection<NoteItem> Items { get; } = new();
    public string Status { get; private set; } = "Not started";

    /// <summary>Raised on a background thread for each brand-new notification.</summary>
    public event Action<NoteItem>? Arrived;

    public void Load()
    {
        var saved = Storage.Load<List<NoteItem>>(FileName);
        if (saved != null) foreach (var n in saved) Items.Add(n);
    }

    public void Save() => Storage.Save(FileName, Items.ToList());

    public async Task StartAsync()
    {
        try
        {
            _listener = UserNotificationListener.Current;
            var access = await _listener.RequestAccessAsync();
            if (access != UserNotificationListenerAccessStatus.Allowed)
            {
                Status = "Windows blocked notification access. Turn it on in Settings > Privacy > Notifications.";
                Log.Info(Status);
                return;
            }

            // Mark everything already in Action Center as seen so we only pop new ones.
            foreach (var n in await _listener.GetNotificationsAsync(NotificationKinds.Toast)) _seen.Add(n.Id);

            // The NotificationChanged event needs package identity, so poll instead (cheap: once a second).
            _poll = new System.Threading.Timer(async _ => await PollAsync(), null, 1000, 1000);
            Status = "On";
        }
        catch (Exception ex)
        {
            Status = "Notification access isn't available on this PC.";
            Log.Error("Notification listener failed", ex);
        }
    }

    private int _busy;

    private async Task PollAsync()
    {
        if (_listener == null || System.Threading.Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var list = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            foreach (var n in list)
            {
                if (!_seen.Add(n.Id)) continue;
                var item = Convert(n);
                if (item != null) Arrived?.Invoke(item);
            }
            if (_seen.Count > 5000) _seen.Clear();
        }
        catch
        {
            // Transient failures are fine; next poll retries.
        }
        finally
        {
            _busy = 0;
        }
    }

    private static NoteItem? Convert(UserNotification n)
    {
        try
        {
            var binding = n.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
            var texts = binding?.GetTextElements().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? new List<string>();
            string app;
            try { app = n.AppInfo.DisplayInfo.DisplayName; } catch { app = "App"; }
            return new NoteItem
            {
                Id = n.Id,
                App = app,
                Title = texts.FirstOrDefault() ?? app,
                Body = string.Join("\n", texts.Skip(1)),
                Time = n.CreationTime.LocalDateTime,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Call on the UI thread.</summary>
    public void Add(NoteItem item)
    {
        Items.Insert(0, item);
        while (Items.Count > Max) Items.RemoveAt(Items.Count - 1);
        Save();
    }

    public void Remove(NoteItem item)
    {
        Items.Remove(item);
        Save();
    }

    public void Clear()
    {
        Items.Clear();
        Save();
    }
}
