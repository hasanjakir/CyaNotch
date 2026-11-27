using System;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using CyaNotch.Core;
using Windows.Devices.Radios;

namespace CyaNotch.Services;

public sealed record SystemStats(double Cpu, double Ram, double RamUsedGb, double RamTotalGb, int? Battery, bool Charging, double? Gpu);

/// <summary>PC stats, battery, brightness, Wi-Fi/Bluetooth radios, power mode, and shortcuts into Windows settings.</summary>
public sealed class SystemService
{
    private long _lastIdle, _lastKernel, _lastUser;

    public SystemStats Sample()
    {
        double cpu = 0;
        if (Native.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            var dIdle = idle - _lastIdle;
            var dTotal = (kernel - _lastKernel) + (user - _lastUser);
            if (_lastKernel != 0 && dTotal > 0) cpu = Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100);
            _lastIdle = idle; _lastKernel = kernel; _lastUser = user;
        }

        var mem = Native.MEMORYSTATUSEX.Create();
        Native.GlobalMemoryStatusEx(ref mem);
        var total = mem.ullTotalPhys / 1073741824.0;
        var used = (mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824.0;

        var ps = System.Windows.Forms.SystemInformation.PowerStatus;
        int? battery = ps.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.NoSystemBattery) ||
                       ps.BatteryLifePercent > 1.0f
            ? null
            : (int)Math.Round(ps.BatteryLifePercent * 100);
        var charging = ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;

        return new SystemStats(cpu, mem.dwMemoryLoad, used, total, battery, charging, SampleGpu());
    }

    private readonly System.Collections.Generic.Dictionary<string, PerformanceCounter> _gpuCounters = new();
    private DateTime _gpuRescan, _gpuNext;
    private double? _gpuLast;
    private bool _gpuBroken;

    /// <summary>GPU 3D engine load from the "GPU Engine" counters (sampled at most every 2 s).</summary>
    private double? SampleGpu()
    {
        if (_gpuBroken) return null;
        if (DateTime.Now < _gpuNext) return _gpuLast;
        _gpuNext = DateTime.Now.AddSeconds(2);
        try
        {
            if (DateTime.Now >= _gpuRescan)
            {
                _gpuRescan = DateTime.Now.AddSeconds(15);
                var names = new PerformanceCounterCategory("GPU Engine").GetInstanceNames()
                    .Where(n => n.EndsWith("engtype_3D")).ToHashSet();
                foreach (var gone in _gpuCounters.Keys.Where(k => !names.Contains(k)).ToList())
                {
                    _gpuCounters[gone].Dispose();
                    _gpuCounters.Remove(gone);
                }
                foreach (var n in names.Where(n => !_gpuCounters.ContainsKey(n)))
                {
                    var c = new PerformanceCounter("GPU Engine", "Utilization Percentage", n, true);
                    c.NextValue();
                    _gpuCounters[n] = c;
                }
            }
            double sum = 0;
            foreach (var c in _gpuCounters.Values)
            {
                try { sum += c.NextValue(); } catch { /* process exited */ }
            }
            _gpuLast = Math.Clamp(sum, 0, 100);
        }
        catch
        {
            _gpuBroken = true;
            _gpuLast = null;
        }
        return _gpuLast;
    }

    // ---------- Brightness (laptop panels, via WMI) ----------

    public int? GetBrightness()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementObject o in s.Get()) return System.Convert.ToInt32(o["CurrentBrightness"]);
        }
        catch { }
        return null;
    }

    public void SetBrightness(int value)
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject o in s.Get())
                o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)Math.Clamp(value, 0, 100) });
        }
        catch (Exception ex)
        {
            Log.Error("Brightness failed", ex);
        }
    }

    // ---------- Radios ----------

    public async Task<bool?> GetRadioAsync(RadioKind kind)
    {
        try
        {
            var radios = await Radio.GetRadiosAsync();
            var r = radios.FirstOrDefault(x => x.Kind == kind);
            return r == null ? null : r.State == RadioState.On;
        }
        catch { return null; }
    }

    public async Task<bool> SetRadioAsync(RadioKind kind, bool on)
    {
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return false;
            var radios = await Radio.GetRadiosAsync();
            var r = radios.FirstOrDefault(x => x.Kind == kind);
            if (r == null) return false;
            return await r.SetStateAsync(on ? RadioState.On : RadioState.Off) == RadioAccessStatus.Allowed;
        }
        catch (Exception ex)
        {
            Log.Error("Radio toggle failed", ex);
            return false;
        }
    }

    // ---------- Power mode (Windows 10/11 power slider overlays) ----------

    public static readonly Guid PowerBattery = new("961cc777-2547-4f9d-8174-7d86181b8a7a"); // best efficiency
    public static readonly Guid PowerBalanced = Guid.Empty;
    public static readonly Guid PowerPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238"); // best performance

    public Guid GetPowerMode()
    {
        try { return Native.PowerGetEffectiveOverlayScheme(out var g) == 0 ? g : Guid.Empty; }
        catch { return Guid.Empty; }
    }

    public void SetPowerMode(Guid mode)
    {
        try { Native.PowerSetActiveOverlayScheme(mode); }
        catch (Exception ex) { Log.Error("Power mode failed", ex); }
    }

    // ---------- Settings that Windows doesn't expose to apps: open the right page ----------

    public static void OpenSettings(string page)
    {
        try { Process.Start(new ProcessStartInfo($"ms-settings:{page}") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("Open settings failed", ex); }
    }

    public static void OpenNightLight() => OpenSettings("nightlight");
    public static void OpenFocus() => OpenSettings("focus");

    // ---------- Night light ----------
    // Windows keeps Night light's on/off state in a CloudStore blob. We only read it.

    private const string NightLightKey =
        @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.bluelightreduction.bluelightreductionstate\windows.data.bluelightreduction.bluelightreductionstate";

    /// <summary>Night light's state, or null if this PC doesn't have it.</summary>
    public bool? NightLightOn()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(NightLightKey);
            if (key?.GetValue("Data") is not byte[] data || data.Length < 24) return null;
            return data[18] == 0x15;
        }
        catch { return null; }
    }

    /// <summary>
    /// Always false: the island opens Windows' Night light settings instead. Editing Windows' private
    /// Night light record by hand could leave it unreadable, which greys out Night light in Quick Settings.
    /// </summary>
    public bool SetNightLight(bool on) => false;

    // ---------- Do not disturb ----------
    // The global "show notifications" switch. Off means Windows keeps banners quiet (Do not disturb).

    private const string ToastKey = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";

    public bool DoNotDisturb
    {
        get
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ToastKey);
                return key?.GetValue("NOC_GLOBAL_SETTING_TOASTS_ENABLED") is int v && v == 0;
            }
            catch { return false; }
        }
        set
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(ToastKey, writable: true);
                key.SetValue("NOC_GLOBAL_SETTING_TOASTS_ENABLED", value ? 0 : 1, Microsoft.Win32.RegistryValueKind.DWord);
            }
            catch (Exception ex) { Log.Error("Do not disturb failed", ex); }
        }
    }
}
