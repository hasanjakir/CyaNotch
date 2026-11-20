using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using CyaNotch.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace CyaNotch.Services;

public sealed class BtDevice
{
    public string Id { get; init; } = "";
    public string Name { get; set; } = "";
    public Guid? ContainerId { get; init; }
    public bool IsLe { get; init; }
    public string Glyph { get; set; } = "";
    public int? Battery { get; set; }

    // for the list
    public string BatteryText => Battery is { } b ? $"{b}%" : "—";
    public double BatteryFraction => (Battery ?? 0) / 100.0;
    public bool HasBattery => Battery != null;
    public Brush BatteryBrush => Battery switch
    {
        null => new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
        <= 15 => new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x52)),
        <= 30 => new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x20)),
        _ => new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x84)),
    };
}

/// <summary>
/// Connected Bluetooth devices and their battery levels. Classic devices (headphones, earbuds) report
/// battery through the hands-free profile, which Windows keeps as a device property; low-energy
/// devices (mice, keyboards, controllers) through the GATT battery service.
/// </summary>
public sealed class BluetoothBatteryService
{
    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string ContainerKey = "System.Devices.Aep.ContainerId";

    private readonly Dictionary<string, BtDevice> _devices = new();
    private readonly List<DeviceWatcher> _watchers = new();
    private System.Threading.Timer? _timer;
    private int _enumerated;

    /// <summary>Raised on a background thread when the list or a battery level changes.</summary>
    public event Action? Changed;

    /// <summary>Raised on a background thread when a device connects (after startup), once its battery is read.</summary>
    public event Action<BtDevice>? Connected;

    public IReadOnlyList<BtDevice> Devices
    {
        get { lock (_devices) return _devices.Values.OrderBy(d => d.Battery == null).ThenBy(d => d.Name).ToList(); }
    }

    public void Start()
    {
        try
        {
            Watch(BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected), le: false);
            Watch(BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected), le: true);
            _timer = new System.Threading.Timer(_ => _ = RefreshAllAsync(), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
        }
        catch (Exception ex)
        {
            Log.Error("Bluetooth watch unavailable", ex);
        }
    }

    private void Watch(string selector, bool le)
    {
        var w = DeviceInformation.CreateWatcher(selector, new[] { ContainerKey, "System.Devices.Aep.IsConnected" }, DeviceInformationKind.AssociationEndpoint);
        w.Added += (_, info) => _ = OnAddedAsync(info, le);
        w.Removed += (_, upd) => { lock (_devices) _devices.Remove(upd.Id); Changed?.Invoke(); };
        w.Updated += (_, upd) =>
        {
            if (upd.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var c) && c is false)
            {
                lock (_devices) _devices.Remove(upd.Id);
                Changed?.Invoke();
            }
        };
        w.EnumerationCompleted += (_, _) => System.Threading.Interlocked.Increment(ref _enumerated);
        _watchers.Add(w);
        w.Start();
    }

    private bool Ready => _enumerated >= 2;

    private async Task OnAddedAsync(DeviceInformation info, bool le)
    {
        try
        {
            var startup = !Ready;
            Guid? container = info.Properties.TryGetValue(ContainerKey, out var c) && c is Guid g ? g : null;
            var name = string.IsNullOrWhiteSpace(info.Name) ? "Bluetooth device" : info.Name;
            var dev = new BtDevice { Id = info.Id, Name = name, ContainerId = container, IsLe = le };
            // One gadget can show up as both classic and low-energy: keep one row per physical device.
            lock (_devices)
            {
                if (container != null && _devices.Values.Any(d => d.ContainerId == container && d.Id != info.Id)) return;
                _devices[info.Id] = dev;
            }
            dev.Glyph = await GlyphAsync(info.Id, name, le);
            dev.Battery = await ReadBatteryAsync(dev);
            Changed?.Invoke();
            if (startup) return;
            // Earbuds often report battery a few seconds after connecting.
            if (dev.Battery == null)
            {
                await Task.Delay(4000);
                dev.Battery = await ReadBatteryAsync(dev);
                Changed?.Invoke();
            }
            Connected?.Invoke(dev);
        }
        catch (Exception ex)
        {
            Log.Error("Bluetooth device read failed", ex);
        }
    }

    public async Task RefreshAllAsync()
    {
        List<BtDevice> list;
        lock (_devices) list = _devices.Values.ToList();
        var changed = false;
        foreach (var d in list)
        {
            var b = await ReadBatteryAsync(d);
            if (b != d.Battery) { d.Battery = b; changed = true; }
        }
        if (changed) Changed?.Invoke();
    }

    private static async Task<int?> ReadBatteryAsync(BtDevice d)
    {
        // 1) Windows' battery property on any device node of the same physical device.
        if (d.ContainerId is { } cid)
        {
            try
            {
                var nodes = await DeviceInformation.FindAllAsync($"System.Devices.ContainerId:=\"{{{cid}}}\"",
                    new[] { BatteryKey }, DeviceInformationKind.Device);
                var best = nodes.Select(n => n.Properties.TryGetValue(BatteryKey, out var v) ? v : null)
                    .Select(v => v switch { byte b => (int?)b, int i => i, uint u => (int)u, _ => null })
                    .Where(v => v is >= 0 and <= 100).Max();
                if (best != null) return best;
            }
            catch { }
        }
        // 2) Low-energy battery service.
        if (d.IsLe)
        {
            try
            {
                using var le = await BluetoothLEDevice.FromIdAsync(d.Id);
                if (le == null) return null;
                var svcs = await le.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Cached);
                foreach (var s in svcs.Services)
                {
                    using (s)
                    {
                        var chars = await s.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Cached);
                        foreach (var ch in chars.Characteristics)
                        {
                            var r = await ch.ReadValueAsync(BluetoothCacheMode.Uncached);
                            if (r.Status != GattCommunicationStatus.Success) r = await ch.ReadValueAsync(BluetoothCacheMode.Cached);
                            if (r.Status == GattCommunicationStatus.Success && r.Value.Length > 0)
                            {
                                using var reader = DataReader.FromBuffer(r.Value);
                                var v = reader.ReadByte();
                                if (v <= 100) return v;
                            }
                        }
                    }
                }
            }
            catch { }
        }
        return null;
    }

    private static async Task<string> GlyphAsync(string id, string name, bool le)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("bud") || n.Contains("pods") || n.Contains("ear")) return ""; // earbuds
        if (n.Contains("headphone") || n.Contains("headset") || n.Contains("wh-") || n.Contains("qc")) return "";
        if (n.Contains("mouse") || n.Contains("mx ")) return "";
        if (n.Contains("keyboard") || n.Contains("keys")) return "";
        if (n.Contains("controller") || n.Contains("gamepad") || n.Contains("xbox") || n.Contains("dualsense")) return "";
        if (n.Contains("watch") || n.Contains("band")) return "";
        if (n.Contains("speaker") || n.Contains("soundbar") || n.Contains("jbl")) return "";
        if (n.Contains("phone") || n.Contains("iphone") || n.Contains("galaxy") || n.Contains("pixel")) return "";
        try
        {
            if (!le)
            {
                using var bt = await BluetoothDevice.FromIdAsync(id);
                switch (bt?.ClassOfDevice.MajorClass)
                {
                    case BluetoothMajorClass.AudioVideo: return "";
                    case BluetoothMajorClass.Phone: return "";
                    case BluetoothMajorClass.Computer: return "";
                    case BluetoothMajorClass.Peripheral:
                        var minor = bt.ClassOfDevice.RawValue; // bit 6 keyboard, bit 7 pointing device
                        return (minor & 0x40) != 0 ? "" : (minor & 0x80) != 0 ? "" : "";
                }
            }
            else
            {
                using var bl = await BluetoothLEDevice.FromIdAsync(id);
                int cat = bl?.Appearance.Category ?? 0;
                if (cat == BluetoothLEAppearanceCategories.HumanInterfaceDevice)
                {
                    var sub = bl!.Appearance.SubCategory;
                    if (sub == BluetoothLEAppearanceSubcategories.Keyboard) return "";
                    if (sub == BluetoothLEAppearanceSubcategories.Mouse) return "";
                    if (sub == BluetoothLEAppearanceSubcategories.Gamepad || sub == BluetoothLEAppearanceSubcategories.Joystick) return "";
                }
                if (cat == BluetoothLEAppearanceCategories.Watch) return "";
                if (cat == BluetoothLEAppearanceCategories.Phone) return "";
            }
        }
        catch { }
        return "";
    }
}
