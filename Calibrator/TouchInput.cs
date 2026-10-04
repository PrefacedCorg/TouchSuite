using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace TouchErase.Calibrator;

/// <summary>
/// 一次触摸样本。WidthMm/HeightMm 为 null 表示该来源没报尺寸；
/// WidthLogical/HeightLogical 是驱动原始计数（供"计数→mm"标定）；
/// XNorm/YNorm 为数字化器归一化坐标（原始HID）；
/// DiuRect 为元素内 DIP 矩形（WPF TouchPoint.Bounds 直接给整框）。
/// </summary>
public sealed record TouchSample(string Source, double? WidthMm, double? HeightMm, double? Pressure01, string Detail,
    int? PressureRaw = null, string PressureRange = "",
    int WidthLogical = 0, int HeightLogical = 0,
    double? XNorm = null, double? YNorm = null,
    double? ScreenPxX = null, double? ScreenPxY = null,
    Rect? DiuRect = null,
    int FrameId = 0, uint TimeMs = 0)
{
    public double? AreaMm2 => WidthMm is double w && HeightMm is double h ? w * h : null;
}

/// <summary>
/// 触摸输入读取（从零实现）：
/// ① RawInput 直接解 HID 的 Width(0x48)/Height(0x49)/TipPressure(0x0D:0x30) —— 真数字化器最准；
/// ② WPF TouchPoint.Bounds —— 接触面积（系统 WM_TOUCH 通路，实时）；
/// ③ WPF Stylus.PressureFactor —— 压感（触摸被提升为触笔后逐帧给出，实时）。
/// 不用 WM_POINTER：系统对触摸只给"按下那一刻"的指针快照，抬手才刷新。
/// </summary>
public sealed class TouchInput : IDisposable
{
    public event Action<TouchSample>? Sample;

    public bool TouchDeclaresSize { get; private set; }
    public bool TouchDeclaresPressure { get; private set; }
    public string DeviceName { get; private set; } = "(未发现触摸屏 HID 设备)";
    public string DiagSummary { get; private set; } = "";

    /// <summary>WPF 路径用：把 DIP 换算成毫米（主窗口校准后设置）。</summary>
    public double MmPerDiuX { get; set; }
    public double MmPerDiuY { get; set; }

    /// <summary>设备只给逻辑计数、不给物理量程时，用现场标定的「1 计数 = ? mm」（0 = 未标定）。</summary>
    public double CountsToMmScale { get; set; }

    private long _lastHidTicks;              // 原始HID 最近一次样本来到的时刻（用于优先级仲裁）
    private const long FreshMs = 300;

    // ================= interop =================

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICELIST
    {
        public IntPtr hDevice;
        public uint dwType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("user32.dll")] private static extern uint GetRawInputDeviceList(RAWINPUTDEVICELIST[]? list, ref uint count, uint size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint GetRawInputDeviceInfo(IntPtr device, uint cmd, IntPtr data, ref uint size);
    [DllImport("user32.dll")] private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll")] private static extern uint GetRawInputData(IntPtr hRawInput, uint cmd, IntPtr data, ref uint size, uint headerSize);

    [DllImport("hid.dll")] private static extern bool HidP_GetCaps(IntPtr preparsed, out HIDP_CAPS caps);
    [DllImport("hid.dll")] private static extern int HidP_GetValueCaps(int reportType, byte[] valueCaps, ref ushort length, IntPtr preparsed);
    [DllImport("hid.dll")] private static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection,
        ushort usage, out uint value, IntPtr preparsed, byte[] report, uint reportLength);

    // ---- 说明：触摸接触面积不走 WM_POINTER（希沃 EasiNote 亦然）----
    // 系统对触摸投递的是 WM_TOUCH（WPF 的 TouchPoint.Bounds 即来自它）；
    // WM_POINTER 对触摸只给按下那一刻的快照、抬手才刷新，不能用于实时接触面积。
    // 压感则来自 WPF 把触摸提升出来的 Stylus.PressureFactor（见 EmitStylus）。

    private const int RIM_TYPEHID = 2;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDI_DEVICENAME = 0x20000007;
    private const uint RIDI_PREPARSEDDATA = 0x20000005;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const int HidP_Input = 0;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;
    private const int HidP_ValueCapsSize = 72;
    private const int WM_INPUT = 0x00FF;

    // ================= 设备能力表 =================

    private sealed class DeviceCtx
    {
        public IntPtr Preparsed;
        public bool IsTouch;
        public bool HasW, HasH, HasP;
        public int WLogMin, WLogMax, WPhysMin, WPhysMax, WExp, WUnits;
        public int HLogMin, HLogMax, HPhysMin, HPhysMax, HExp, HUnits;
        public int PLogMin, PLogMax;
        public int XLogMax, YLogMax;
    }

    private readonly Dictionary<IntPtr, DeviceCtx> _ctx = new();
    private IntPtr _hwnd;
    private HwndSource? _src;
    private bool _disposed;

    // 诊断节拍：每秒打印一次 Stylus 通道计数
    private DispatcherTimer? _poll;

    /// <summary>扫描 HID 设备能力，并挂上窗口消息钩子、注册触摸原始输入。</summary>
    public void Attach(Window window)
    {
        Scan();

        _hwnd = new WindowInteropHelper(window).Handle;
        _src = PresentationSource.FromVisual(window) as HwndSource;
        _src?.AddHook(WndProc);

        var devices = new[]
        {
            new RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 0x04, dwFlags = RIDEV_INPUTSINK, hwndTarget = _hwnd },
        };
        bool ok = RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        DiagSummary += $"\nRawInput 触摸注册 = {ok}";

        StartPointerPolling(window.Dispatcher);
    }

    /// <summary>诊断节拍：每秒打印一次 Stylus（实时压感）通道计数。</summary>
    private void StartPointerPolling(Dispatcher dispatcher)
    {
        _poll = new DispatcherTimer(TimeSpan.FromMilliseconds(1000), DispatcherPriority.Background,
            (_, _) =>
            {
                if (_disposed)
                    return;
                Log.Info($"Stylus 通道 {_stylusCount} 条/秒（压感={(_lastStylusPressure?.ToString("0.####") ?? "无")}）");
                _stylusCount = 0;
            }, dispatcher);
        _poll.Start();
    }

    /// <summary>WPF 兜底：从元素上的触摸事件取 Bounds（无压感）；同时挂 Stylus 探针取逐帧压感。</summary>
    public void AttachFallback(FrameworkElement pad)
    {
        pad.TouchDown += (_, e) => EmitWpf(e, pad);
        pad.TouchMove += (_, e) => EmitWpf(e, pad);
        pad.StylusDown += (_, e) => EmitStylus(e, pad);
        pad.StylusMove += (_, e) => EmitStylus(e, pad);
    }

    private int _stylusCount;
    private double? _lastStylusPressure;

    /// <summary>WPF 触笔通路：读逐帧 PressureFactor（触摸被提升为触笔时会走这里）。先只做探针，不参与来源仲裁。</summary>
    private void EmitStylus(StylusEventArgs e, FrameworkElement pad)
    {
        try
        {
            StylusPointCollection pts = e.GetStylusPoints(pad);
            if (pts.Count == 0)
                return;

            double pf = pts[pts.Count - 1].PressureFactor;
            _stylusCount++;
            _lastStylusPressure = pf;
            Sample?.Invoke(new TouchSample("STYLUS", null, null, pf > 0 ? pf : null,
                $"PressureFactor={pf:0.#####}", FrameId: _stylusCount));
        }
        catch
        {
            // 忽略
        }
    }

    private void EmitWpf(TouchEventArgs e, FrameworkElement pad)
    {
        if (Environment.TickCount64 - _lastHidTicks <= FreshMs)
            return; // 原始HID 正在出数 → 不用 WPF 兜底

        Rect b = e.GetTouchPoint(pad).Bounds;
        double? wMm = MmPerDiuX > 0 ? b.Width * MmPerDiuX : null;
        double? hMm = MmPerDiuY > 0 ? b.Height * MmPerDiuY : null;
        Sample?.Invoke(new TouchSample("WPF", wMm, hMm, null,
            $"Bounds {Precision.Fmt(b.Width, 3)}×{Precision.Fmt(b.Height, 3)} DIP → {Precision.Fmt(wMm)}×{Precision.Fmt(hMm)} mm",
            DiuRect: b));
    }

    // ================= WM_INPUT =================

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_INPUT)
            Decode(lParam);
        return IntPtr.Zero;
    }

    private void Decode(IntPtr lParam)
    {
        uint size = 0;
        uint header = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, header);
        if (size == 0)
            return;

        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, RID_INPUT, buf, ref size, header) != size)
                return;
            if (Marshal.ReadInt32(buf, 0) != RIM_TYPEHID)
                return;

            IntPtr hDevice = Marshal.ReadIntPtr(buf, 8);
            if (!_ctx.TryGetValue(hDevice, out DeviceCtx? ctx) || ctx is null || !ctx.IsTouch)
                return;

            uint sizeHid = (uint)Marshal.ReadInt32(buf, (int)header);
            uint reportCount = (uint)Marshal.ReadInt32(buf, (int)header + 4);
            if (sizeHid == 0 || reportCount == 0)
                return;

            IntPtr dataPtr = buf + (int)header + 8;
            var report = new byte[sizeHid];
            Marshal.Copy(dataPtr, report, 0, (int)sizeHid);

            uint w = 0, h = 0, p = 0, x = 0, y = 0;
            bool hasW = ctx.HasW && HidP_GetUsageValue(HidP_Input, 0x0D, 0, 0x48, out w, ctx.Preparsed, report, sizeHid) == HIDP_STATUS_SUCCESS;
            bool hasH = ctx.HasH && HidP_GetUsageValue(HidP_Input, 0x0D, 0, 0x49, out h, ctx.Preparsed, report, sizeHid) == HIDP_STATUS_SUCCESS;
            bool hasP = ctx.HasP && HidP_GetUsageValue(HidP_Input, 0x0D, 0, 0x30, out p, ctx.Preparsed, report, sizeHid) == HIDP_STATUS_SUCCESS;
            HidP_GetUsageValue(HidP_Input, 0x01, 0, 0x30, out x, ctx.Preparsed, report, sizeHid);
            HidP_GetUsageValue(HidP_Input, 0x01, 0, 0x31, out y, ctx.Preparsed, report, sizeHid);

            double? wMm = hasW ? ToMm(w, ctx.WLogMin, ctx.WLogMax, ctx.WPhysMin, ctx.WPhysMax, ctx.WExp, ctx.WUnits) : null;
            double? hMm = hasH ? ToMm(h, ctx.HLogMin, ctx.HLogMax, ctx.HPhysMin, ctx.HPhysMax, ctx.HExp, ctx.HUnits) : null;
            double? p01 = hasP && ctx.PLogMax > ctx.PLogMin
                ? Math.Clamp((double)(p - (uint)ctx.PLogMin) / (ctx.PLogMax - ctx.PLogMin), 0, 1)
                : null;

            double? xn = ctx.XLogMax > 0 ? Math.Clamp((double)x / ctx.XLogMax, 0, 1) : null;
            double? yn = ctx.YLogMax > 0 ? Math.Clamp((double)y / ctx.YLogMax, 0, 1) : null;

            _lastHidTicks = Environment.TickCount64;
            Sample?.Invoke(new TouchSample("RawHID", wMm, hMm, p01,
                $"W={w}/{ctx.WLogMax} → {Precision.Fmt(wMm)} mm  H={h}/{ctx.HLogMax} → {Precision.Fmt(hMm)} mm  P={p}({Precision.Fmt(p01, 2)})",
                hasP ? (int)p : null, hasP ? $"{ctx.PLogMin}..{ctx.PLogMax}" : "",
                WidthLogical: (int)w, HeightLogical: (int)h,
                XNorm: xn, YNorm: yn));
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>
    /// 逻辑值 → 毫米。**全量程线性映射、全程双精度**：
    /// fraction = (logical − LogicalMin) / (LogicalMax − LogicalMin)；
    /// physical = PhysicalMin + fraction × (PhysicalMax − PhysicalMin)；再按 UnitsExp / Units 换算成 mm。
    /// 物理量程缺失（PhysicalMax ≤ PhysicalMin）时退回现场标定的「1 计数 = ? mm」。
    /// </summary>
    private double? ToMm(uint logical, int logicalMin, int logicalMax,
        int physicalMin, int physicalMax, int unitsExp, int units)
    {
        long logRange = (long)logicalMax - logicalMin;
        if (logRange <= 0)
            return null;

        long physRange = (long)physicalMax - physicalMin;
        if (physRange <= 0)
            return CountsToMmScale > 0 ? logical * CountsToMmScale : null;

        double fraction = ((long)logical - logicalMin) / (double)logRange;
        double physical = physicalMin + fraction * physRange;

        int exp = unitsExp & 0xF;
        if (exp >= 8) exp -= 16;                          // 4 位补码（负指数）
        double inUnit = physical * Math.Pow(10, exp);
        bool englishSystem = (units & 0xF000) == 0x3000;   // 3 = English Linear
        return englishSystem ? inUnit * 25.4 : inUnit * 10.0;   // 英寸 / 厘米
    }

    // ================= 扫描 =================

    private void Scan()
    {
        uint count = 0;
        uint structSize = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        if (GetRawInputDeviceList(null, ref count, structSize) == unchecked((uint)-1) || count == 0)
            return;

        var list = new RAWINPUTDEVICELIST[count];
        if (GetRawInputDeviceList(list, ref count, structSize) == unchecked((uint)-1))
            return;

        var found = new List<string>();

        for (int i = 0; i < list.Length; i++)
        {
            if (list[i].dwType != RIM_TYPEHID)
                continue;

            IntPtr hDevice = list[i].hDevice;
            IntPtr preparsed = GetPreparsed(hDevice, out bool ok);
            if (!ok || preparsed == IntPtr.Zero)
                continue;

            if (!HidP_GetCaps(preparsed, out HIDP_CAPS caps))
            {
                Marshal.FreeHGlobal(preparsed);
                continue;
            }

            var ctx = new DeviceCtx
            {
                Preparsed = preparsed,
                IsTouch = caps.UsagePage == 0x0D && caps.Usage == 0x04,
            };

            if (caps.NumberInputValueCaps > 0)
            {
                var buffer = new byte[HidP_ValueCapsSize * caps.NumberInputValueCaps];
                ushort len = caps.NumberInputValueCaps;
                if (HidP_GetValueCaps(HidP_Input, buffer, ref len, preparsed) == HIDP_STATUS_SUCCESS)
                {
                    for (int k = 0; k < len; k++)
                    {
                        int off = k * HidP_ValueCapsSize;
                        ushort usagePage = BitConverter.ToUInt16(buffer, off + 0);
                        ushort usage = BitConverter.ToUInt16(buffer, off + 56);
                        int logicalMin = BitConverter.ToInt32(buffer, off + 40);
                        int logicalMax = BitConverter.ToInt32(buffer, off + 44);
                        int physicalMin = BitConverter.ToInt32(buffer, off + 48);
                        int physicalMax = BitConverter.ToInt32(buffer, off + 52);
                        int units = BitConverter.ToInt32(buffer, off + 36);
                        int unitsExp = BitConverter.ToInt32(buffer, off + 32);

                        if (usagePage == 0x0D && usage == 0x48)
                        {
                            ctx.HasW = true;
                            ctx.WLogMin = logicalMin; ctx.WLogMax = logicalMax;
                            ctx.WPhysMin = physicalMin; ctx.WPhysMax = physicalMax;
                            ctx.WExp = unitsExp; ctx.WUnits = units;
                        }
                        if (usagePage == 0x0D && usage == 0x49)
                        {
                            ctx.HasH = true;
                            ctx.HLogMin = logicalMin; ctx.HLogMax = logicalMax;
                            ctx.HPhysMin = physicalMin; ctx.HPhysMax = physicalMax;
                            ctx.HExp = unitsExp; ctx.HUnits = units;
                        }
                        if (usagePage == 0x0D && usage == 0x30) { ctx.HasP = true; ctx.PLogMin = logicalMin; ctx.PLogMax = logicalMax; }
                        if (usagePage == 0x01 && usage == 0x30) ctx.XLogMax = logicalMax;
                        if (usagePage == 0x01 && usage == 0x31) ctx.YLogMax = logicalMax;
                    }
                }
            }

            if (_ctx.TryGetValue(hDevice, out DeviceCtx? old) && old.Preparsed != IntPtr.Zero && old.Preparsed != preparsed)
                Marshal.FreeHGlobal(old.Preparsed);
            _ctx[hDevice] = ctx;

            if (ctx.IsTouch)
            {
                string name = GetDeviceName(hDevice);
                found.Add($"{name} | W={ctx.HasW} H={ctx.HasH} P={ctx.HasP}");
                TouchDeclaresSize |= ctx.HasW && ctx.HasH;
                TouchDeclaresPressure |= ctx.HasP;
                DeviceName = name;
            }
        }

        DiagSummary = found.Count > 0
            ? "触摸 HID 设备：\n  " + string.Join("\n  ", found)
            : "未发现触摸屏 HID 设备（Digitizer 0x0D / Touch Screen 0x04）";
    }

    private static string GetDeviceName(IntPtr device)
    {
        uint size = 0;
        GetRawInputDeviceInfo(device, RIDI_DEVICENAME, IntPtr.Zero, ref size);
        if (size == 0)
            return "(无名)";

        IntPtr buf = Marshal.AllocHGlobal((int)(size * 2 + 2));
        try
        {
            uint chars = size;
            GetRawInputDeviceInfo(device, RIDI_DEVICENAME, buf, ref chars);
            return (Marshal.PtrToStringUni(buf) ?? "(无名)").Replace("\0", "");
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static IntPtr GetPreparsed(IntPtr device, out bool ok)
    {
        ok = false;
        uint size = 0;
        GetRawInputDeviceInfo(device, RIDI_PREPARSEDDATA, IntPtr.Zero, ref size);
        if (size == 0)
            return IntPtr.Zero;

        IntPtr buf = Marshal.AllocHGlobal((int)size);
        uint actual = size;
        if (GetRawInputDeviceInfo(device, RIDI_PREPARSEDDATA, buf, ref actual) == unchecked((uint)-1))
        {
            Marshal.FreeHGlobal(buf);
            return IntPtr.Zero;
        }
        ok = true;
        return buf;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _poll?.Stop();
        _src?.RemoveHook(WndProc);
    }
}
