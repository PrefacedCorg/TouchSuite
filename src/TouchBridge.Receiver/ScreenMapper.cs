using System.Runtime.InteropServices;

namespace TouchBridge.Receiver;

internal readonly record struct MonitorInfo(string Device, int Left, int Top, int Width, int Height, bool Primary)
{
    public override string ToString() =>
        $"{Device} {Width}x{Height} @({Left},{Top}){(Primary ? " [主屏]" : "")}";
}

/// <summary>
/// 把平板归一化坐标 (0..1) 映射到目标显示器的物理像素坐标，
/// 并按同一变换缩放接触面积。
/// </summary>
internal sealed class ScreenMapper
{
    public enum FitMode
    {
        /// <summary>拉伸铺满显示器（会按屏幕比例变形）。</summary>
        Stretch,
        /// <summary>等比完整显示（左右或上下留黑边）。</summary>
        Fit,
        /// <summary>等比铺满（超出部分裁剪）。</summary>
        Cover,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

    private const uint MONITORINFOF_PRIMARY = 0x1;

    public static List<MonitorInfo> EnumerateMonitors()
    {
        var list = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr d) =>
        {
            var mi = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMon, ref mi))
            {
                list.Add(new MonitorInfo(
                    (mi.szDevice ?? "").TrimEnd('\0'),
                    mi.rcMonitor.Left, mi.rcMonitor.Top,
                    mi.rcMonitor.Right - mi.rcMonitor.Left,
                    mi.rcMonitor.Bottom - mi.rcMonitor.Top,
                    (mi.dwFlags & MONITORINFOF_PRIMARY) != 0));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private readonly MonitorInfo _monitor;
    private readonly FitMode _mode;
    private readonly int _left;
    private readonly int _top;
    private readonly int _width;
    private readonly int _height;

    public ScreenMapper(MonitorInfo monitor, double surfaceWidth, double surfaceHeight, FitMode mode)
    {
        _monitor = monitor;
        _mode = mode;

        double monW = Math.Max(1, monitor.Width);
        double monH = Math.Max(1, monitor.Height);
        double surfaceAspect = surfaceHeight > 0 ? surfaceWidth / surfaceHeight : 1.0;
        double monitorAspect = monW / monH;

        double w = monW, h = monH;
        switch (mode)
        {
            case FitMode.Stretch:
                break;
            case FitMode.Fit:
                if (surfaceAspect > monitorAspect) h = monW / surfaceAspect;
                else w = monH * surfaceAspect;
                break;
            case FitMode.Cover:
                if (surfaceAspect > monitorAspect) w = monH * surfaceAspect;
                else h = monW / surfaceAspect;
                break;
        }

        _width = Math.Max(1, (int)Math.Round(w));
        _height = Math.Max(1, (int)Math.Round(h));
        _left = monitor.Left + (monitor.Width - _width) / 2;
        _top = monitor.Top + (monitor.Height - _height) / 2;
    }

    public string Describe() =>
        $"{_monitor.Device} {_monitor.Width}x{_monitor.Height} 映射区 {_width}x{_height} @({_left},{_top}) 模式={_mode}";

    /// <summary>归一化坐标 → 屏幕物理像素（并夹取到目标显示器范围内）。</summary>
    public POINT Map(float nx, float ny)
    {
        int x = (int)Math.Round(_left + nx * _width);
        int y = (int)Math.Round(_top + ny * _height);
        return new POINT
        {
            X = Math.Clamp(x, _monitor.Left, _monitor.Left + _monitor.Width - 1),
            Y = Math.Clamp(y, _monitor.Top, _monitor.Top + _monitor.Height - 1),
        };
    }

    /// <summary>归一化接触尺寸 → 屏幕物理像素尺寸。</summary>
    public (double Width, double Height) ScaleContact(float normalizedW, float normalizedH) =>
        (normalizedW * _width, normalizedH * _height);

    /// <summary>映射区（目标显示器上的实际呈现区域）物理像素尺寸。</summary>
    public int RegionWidth => _width;
    public int RegionHeight => _height;

    /// <summary>归一化坐标 → 数字化器口径 0..32767（相对映射区），供 HID 上报。</summary>
    public void MapToUnit15(float nx, float ny, out ushort x, out ushort y)
    {
        double cx = Math.Clamp(nx, 0.0, 1.0);
        double cy = Math.Clamp(ny, 0.0, 1.0);
        x = (ushort)Math.Round(cx * 32767.0);
        y = (ushort)Math.Round(cy * 32767.0);
    }
}
