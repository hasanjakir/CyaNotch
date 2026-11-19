using System;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CyaNotch.Core;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace CyaNotch.Services;

/// <summary>
/// Numbers for the game strip: frames per second of the foreground game, and temperatures.
///
/// FPS: counted from the DXGI / Direct3D 9 "present" events Windows publishes through ETW. Reading ETW
/// needs CyaNotch to run as administrator; without it the strip just leaves FPS out.
/// GPU temperature: NVIDIA's NVML (ships with the driver), no admin needed.
/// CPU temperature: the ACPI thermal zone over WMI (admin only; not every PC reports it).
/// </summary>
public sealed class GameService : IDisposable
{
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");

    private TraceEventSession? _session;
    private int _targetPid;
    private int _frames;
    private DateTime _windowStart = DateTime.UtcNow;
    private double? _fps;
    private bool _etwFailed;

    public bool CanMeasureFps => !_etwFailed && (TraceEventSession.IsElevated() ?? false);

    /// <summary>Start counting frames for this process (the fullscreen game).</summary>
    public void Track(int pid)
    {
        _targetPid = pid;
        if (_session != null || !CanMeasureFps) return;
        try
        {
            _session = new TraceEventSession("CyaNotch-FPS") { StopOnDispose = true };
            _session.EnableProvider(DxgiProvider, TraceEventLevel.Informational);
            _session.EnableProvider(D3D9Provider, TraceEventLevel.Informational);
            _session.Source.Dynamic.All += OnEvent;
            var s = _session;
            Task.Run(() =>
            {
                try { s.Source.Process(); }
                catch (Exception ex) { Log.Error("ETW processing stopped", ex); }
            });
        }
        catch (Exception ex)
        {
            _etwFailed = true;
            Log.Error("FPS counter unavailable", ex);
            _session?.Dispose();
            _session = null;
        }
    }

    public void StopTracking()
    {
        _targetPid = 0;
        _fps = null;
        _session?.Dispose();
        _session = null;
    }

    private void OnEvent(TraceEvent e)
    {
        if (e.ProcessID != _targetPid) return;
        // DXGI Present_Start = 42, D3D9 Present_Start = 1
        var id = (int)e.ID;
        if ((e.ProviderGuid == DxgiProvider && id == 42) || (e.ProviderGuid == D3D9Provider && id == 1))
            Interlocked.Increment(ref _frames);
    }

    /// <summary>Frames per second over the last sample window, or null when unknown.</summary>
    public double? SampleFps()
    {
        if (_session == null) return null;
        var now = DateTime.UtcNow;
        var secs = (now - _windowStart).TotalSeconds;
        if (secs < 0.9) return _fps;
        var frames = Interlocked.Exchange(ref _frames, 0);
        _windowStart = now;
        _fps = frames / secs;
        return _fps;
    }

    // ---------- GPU temperature (NVIDIA NVML) ----------

    private bool _nvmlTried, _nvmlOk;
    private IntPtr _nvmlDevice;

    [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")] private static extern int NvmlInit();
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")] private static extern int NvmlDeviceByIndex(uint index, out IntPtr device);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature")] private static extern int NvmlTemp(IntPtr device, int sensor, out uint temp);

    public double? GpuTemp()
    {
        if (!_nvmlTried)
        {
            _nvmlTried = true;
            try { _nvmlOk = NvmlInit() == 0 && NvmlDeviceByIndex(0, out _nvmlDevice) == 0; }
            catch { _nvmlOk = false; } // no NVIDIA GPU / driver
        }
        if (!_nvmlOk) return null;
        try { return NvmlTemp(_nvmlDevice, 0, out var t) == 0 ? t : null; }
        catch { return null; }
    }

    // ---------- CPU temperature (ACPI thermal zone, admin) ----------

    private bool _cpuTempBroken;

    public double? CpuTemp()
    {
        if (_cpuTempBroken) return null;
        try
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            double max = 0;
            foreach (ManagementObject o in s.Get())
            {
                var kelvinTenths = Convert.ToDouble(o["CurrentTemperature"]);
                max = Math.Max(max, kelvinTenths / 10.0 - 273.15);
            }
            if (max > 0 && max < 120) return max;
            _cpuTempBroken = true;
        }
        catch
        {
            _cpuTempBroken = true;
        }
        return null;
    }

    public void Dispose() => StopTracking();
}
