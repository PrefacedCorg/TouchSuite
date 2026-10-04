using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace TouchErase.Calibrator;

/// <summary>多指中的一根接触（WPF = 一个 TouchDevice，原始HID = 一个 contact id）。</summary>
public sealed record ContactRect(
    int Id,
    Rect? DiuRect,                     // WPF：相对 pad 的 DIU 矩形
    double? WMm, double? HMm,          // 原始HID：接触宽高 mm
    double? XNorm, double? YNorm,      // 原始HID：归一化坐标
    double? P01,                        // 原始HID：压感
    int WLogical, int HLogical);        // 原始HID：原始计数

/// <summary>
/// 一次触摸样本。WidthMm/HeightMm 为 null 表示该来源没报尺寸；
/// 多指时顶层字段是各指的概要（max 尺寸 / Σ面积），完整的分指数据在 <see cref="Contacts"/>；
/// WidthLogical/HeightLogical 是驱动原始计数（供"计数→mm"标定）；
/// XNorm/YNorm 为数字化器归一化坐标（原始HID，多指时为面积加权中心）；
/// DiuRect 为元素内 DIP 矩形（WPF）。
/// </summary>
public sealed record TouchSample(string Source, double? WidthMm, double? HeightMm, double? Pressure01, string Detail,
    int? PressureRaw = null, string PressureRange = "",
    int WidthLogical = 0, int HeightLogical = 0,
    double? XNorm = null, double? YNorm = null,
    double? ScreenPxX = null, double? ScreenPxY = null,
    Rect? DiuRect = null,
    int FrameId = 0, uint TimeMs = 0,
    IReadOnlyList<ContactRect>? Contacts = null)
{
    /// <summary>接触面积：带分指列表时 = 各指面积之和（手掌多接触不被拆散低估）；单指退化为 W×H。</summary>
    public double? AreaMm2
    {
        get
        {
            if (Contacts is { Count: > 0 } list)
            {
                double sum = 0;
                bool any = false;
                foreach (ContactRect c in list)
                    if (c.WMm is double cw && c.HMm is double ch) { sum += cw * ch; any = true; }
                if (any) return sum;
                return null;
            }
            return WidthMm is double w && HeightMm is double h ? w * h : null;
        }
    }
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

    /// <summary>换算表文字（HID 逻辑量程 → 物理量程 → mm），供界面直接回显驱动实际给的换算说明。</summary>
    public string WScaleText { get; private set; } = "无";
    public string HScaleText { get; private set; } = "无";

    /// <summary>最大接触数（0x55 声明；0 = 未声明）与链集合数（多指：每根手指一个 collection）。</summary>
    public int MaxContactsDeclared { get; private set; }
    public int LinkCollectionCount { get; private set; }

    /// <summary>设备声明了哪些能力（0x48 宽 / 0x49 高 / 0x30 压感 / 0x51 接触ID / 0x42 tip）。
    /// 其它项（倾斜角 0x3F、方向 0x52、方位角 0x51 等）当前**未在本项目声明与读取**。</summary>
    public bool HasWidthUsage { get; private set; }
    public bool HasHeightUsage { get; private set; }
    public bool HasPressureUsage { get; private set; }
    public bool HasContactIdUsage { get; private set; }
    public bool HasTipSwitchUsage { get; private set; }

    /// <summary>设备声明支持但当前未读取的 HID 用法（倾斜角/方向等），供界面如实说明。</summary>
    public string UnreadUsages { get; private set; } = "";

    /// <summary>WPF 路径用：把 DIP 换算成毫米（主窗口校准后设置）。</summary>
    public double MmPerDiuX { get; set; }
    public double MmPerDiuY { get; set; }

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
        // 注意：HIDP_CAPS.Reserved 是 17 个 ushort。若写成 [MarshalAs(ByValArray)] ushort[]，
        // 会被 MngdNativeArrayMarshaler 处理，遇多云化器时报 CLR 内部错误 0x80131506。
        // 展平成 17 个独立字段，走纯 blittable 封送，稳定。
        public ushort Res01, Res02, Res03, Res04, Res05, Res06, Res07, Res08, Res09;
        public ushort Res10, Res11, Res12, Res13, Res14, Res15, Res16, Res17;
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

    // 注意：[In, Out] 不能省！缺 Out 时 CLR 只把托管数组拷到原生侧，原生填充的内容不会拷回，
    // list[i].dwType 永远是 0 → 一个 RIM_TYPEHID 都匹配不到（"RawInput 扫不到 HID"的真凶）。
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList([In, Out] RAWINPUTDEVICELIST[]? list, ref uint count, uint size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetRawInputDeviceInfo(IntPtr device, uint cmd, IntPtr data, ref uint size);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputData(IntPtr hRawInput, uint cmd, IntPtr data, ref uint size, uint headerSize);

    [DllImport("hid.dll")] private static extern bool HidP_GetCaps(IntPtr preparsed, out HIDP_CAPS caps);
    [DllImport("hid.dll")] private static extern int HidP_GetValueCaps(int reportType, byte[] valueCaps, ref ushort length, IntPtr preparsed);
    [DllImport("hid.dll")]
    private static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection,
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
        public int LinkCollections;      // HID 链集合数（多指：每根手指一个 collection）
        public int MaxContacts;          // 0x55 声明的最大接触数（0 = 未声明）
        public bool HasContactId;        // 0x51 Contact Identifier
        public bool HasTipSwitch;       // 0x42 Tip Switch
        public string? Unread;          // 声明了但本项目未读取的用法（倾斜角/方向等）
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

        // 注册所有可能的触摸/数字化器 usage：只注册 0x04 会漏掉以 Multi-touch Digitizer(0x22)
        // 或 Stylus(0x20) 声明的屏 —— 那样即使 Scan 认得它，也永远收不到 WM_INPUT。
        var devices = new[]
        {
            new RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 0x04, dwFlags = RIDEV_INPUTSINK, hwndTarget = _hwnd },
            new RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 0x05, dwFlags = RIDEV_INPUTSINK, hwndTarget = _hwnd },
            new RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 0x20, dwFlags = RIDEV_INPUTSINK, hwndTarget = _hwnd },
            new RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 0x22, dwFlags = RIDEV_INPUTSINK, hwndTarget = _hwnd },
        };
        bool ok = RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        int err = Marshal.GetLastWin32Error();
        DiagSummary += $"\nRawInput 触摸注册 = {ok}（err={err}）";
        Log.Info($"RawInput 注册触摸 usage 0x04/0x05/0x20/0x22 = {ok}（GetLastError={err}）");

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

    /// <summary>
    /// WPF 兜底：从元素上的触摸事件取 Bounds（无压感）；同时挂 Stylus 探针取逐帧压感。
    /// 多指适配：每个 pad 维护「活动触点表」（TouchDevice.Id → Bounds）。样本携带**分指**的
    /// Contacts 列表（静止手指不触发事件，靠活动表凑齐全表）——下游按指分桶，各画各的框。
    /// </summary>
    public void AttachFallback(FrameworkElement pad)
    {
        var touches = new Dictionary<int, Rect>();
        _padTouches[pad] = touches;

        pad.TouchDown += (_, e) => EmitWpf(e, pad, touches);
        pad.TouchMove += (_, e) => EmitWpf(e, pad, touches);
        pad.TouchUp += (_, e) => RemoveTouch(e, touches);
        pad.TouchLeave += (_, e) => RemoveTouch(e, touches);
        pad.StylusDown += (_, e) => EmitStylus(e, pad);
        pad.StylusMove += (_, e) => EmitStylus(e, pad);
    }

    private readonly Dictionary<FrameworkElement, Dictionary<int, Rect>> _padTouches = new();

    /// <summary>该指抬起/移出：只从活动表移除它自己，再把剩下的各指发一遍；全空则停发（靠新鲜窗口过期）。</summary>
    private void RemoveTouch(TouchEventArgs e, Dictionary<int, Rect> touches)
    {
        if (!touches.Remove(e.TouchDevice.Id))
            return;
        if (touches.Count > 0)
            EmitWpfList(touches);
    }

    private void EmitWpf(TouchEventArgs e, FrameworkElement pad, Dictionary<int, Rect> touches)
    {
        if (Environment.TickCount64 - _lastHidTicks <= FreshMs)
            return; // 原始HID 正在出有效尺寸 → 不用 WPF 兜底

        Rect b = e.GetTouchPoint(pad).Bounds;
        if (b.Width > 0 && b.Height > 0)
            touches[e.TouchDevice.Id] = b;
        else
            touches.Remove(e.TouchDevice.Id);

        EmitWpfList(touches);
    }

    /// <summary>把 pad 上当前全部活动触点按**分指**发出（每指一个 ContactRect，互不合并）。</summary>
    private void EmitWpfList(Dictionary<int, Rect> touches)
    {
        if (touches.Count == 0)
            return;
        if (Environment.TickCount64 - _lastHidTicks <= FreshMs)
            return;

        var list = new List<ContactRect>(touches.Count);
        double? onlyW = null, onlyH = null;
        Rect onlyRect = default;
        foreach ((int id, Rect r) in touches)
        {
            double? wMm = MmPerDiuX > 0 ? r.Width * MmPerDiuX : null;
            double? hMm = MmPerDiuY > 0 ? r.Height * MmPerDiuY : null;
            list.Add(new ContactRect(id, r, wMm, hMm, null, null, null, 0, 0));
            onlyW = wMm; onlyH = hMm; onlyRect = r;   // 单指时填顶层（多指时顶层无意义，AreaMm2 走 Contacts）
        }

        string detail = touches.Count > 1
            ? $"{touches.Count} 指: {string.Join("，", list.Select(c => $"#{c.Id} {Precision.Fmt(c.WMm)}×{Precision.Fmt(c.HMm)} mm"))}"
            : $"Bounds {Precision.Fmt(onlyRect.Width, 3)}×{Precision.Fmt(onlyRect.Height, 3)} DIP → {Precision.Fmt(onlyW)}×{Precision.Fmt(onlyH)} mm";

        Sample?.Invoke(new TouchSample("WPF",
            touches.Count == 1 ? onlyW : null, touches.Count == 1 ? onlyH : null,
            null, detail,
            DiuRect: touches.Count == 1 ? onlyRect : null,
            Contacts: list));
    }

    private int _stylusCount;
    private double? _lastStylusPressure;
    private bool _stylusDiagLogged;

    /// <summary>WPF 触笔通路：读逐帧 PressureFactor（触摸被提升为触笔时会走这里）。先只做探针，不参与来源仲裁。</summary>
    private void EmitStylus(StylusEventArgs e, FrameworkElement pad)
    {
        try
        {
            StylusPointCollection pts = e.GetStylusPoints(pad);
            if (pts.Count == 0)
                return;

            StylusPoint sp = pts[pts.Count - 1];
            double pf = sp.PressureFactor;
            _stylusCount++;
            _lastStylusPressure = pf;

            if (!_stylusDiagLogged)
            {
                _stylusDiagLogged = true;
                TabletDevice? td = e.StylusDevice?.TabletDevice;
                Log.Info($"触笔/触摸设备诊断: TabletType={td?.Type} 名={td?.Name} | "
                         + $"点数={pts.Count} | 压感={pf:0.####}");
            }

            Sample?.Invoke(new TouchSample("STYLUS", null, null, pf > 0 ? pf : null,
                $"PressureFactor={pf:0.#####}", FrameId: _stylusCount));
        }
        catch
        {
            // 忽略
        }
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

            // ---- 多指：按链集合逐手指解析，活动表整理成**分指**样本（每指各画各的框）----
            if (ctx.LinkCollections > 1)
            {
                int frameHits = ReadFrameContacts(ctx, hDevice, report, sizeHid);
                if (frameHits > 0 || (_contacts.TryGetValue(hDevice, out var tbl) && tbl.Count > 0))
                {
                    TouchSample? multi = BuildContactsSample(ctx, hDevice);
                    if (multi is not null)
                    {
                        // 只在原始HID 出**有效尺寸**时压制 WPF 兜底；只报位置的设备仍让 WPF 供面积
                        if (multi.WidthMm is not null)
                            _lastHidTicks = Environment.TickCount64;
                        Sample?.Invoke(multi);
                        return;
                    }
                }
            }

            // ---- 兜底：单值解析（老式单指设备，usage 全挂在根集合）----
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

            if (wMm is not null || hMm is not null)
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

    // ================= 多指 contact 聚合（原始HID）=================

    /// <summary>一根手指/接触的最近一次数据（跨帧保留——分帧轮发的驱动每帧只带一根手指的数据）。</summary>
    private sealed class ContactInfo
    {
        public long Ticks;
        public double XNorm, YNorm;
        public bool HasPos;
        public double? WMm, HMm;
        public double? P01;
        public int PRaw;
        public bool HasP;
        public int WLogical, HLogical;
    }

    private const long ContactStaleMs = 300;   // 与来源新鲜窗口一致

    /// <summary>每台设备的活动 contact 表：contact id → 最近数据。</summary>
    private readonly Dictionary<IntPtr, Dictionary<int, ContactInfo>> _contacts = new();

    /// <summary>
    /// 按链集合逐根手指解析本帧报告（多指触摸屏每根手指各占一个 collection）。
    /// tip=1 → 更新活动表；tip=0 → 从表移除（驱动明示抬起）。
    /// 返回本帧命中的 collection 数；多指设备静默期返回 0（活动表仍在）。
    /// </summary>
    private int ReadFrameContacts(DeviceCtx ctx, IntPtr hDevice, byte[] report, uint sizeHid)
    {
        int n = Math.Min(ctx.LinkCollections, 64);   // 物理上不存在超 64 指的设备
        if (n <= 1)
            return 0;

        if (!_contacts.TryGetValue(hDevice, out Dictionary<int, ContactInfo>? table))
        {
            table = new Dictionary<int, ContactInfo>();
            _contacts[hDevice] = table;
        }

        long now = Environment.TickCount64;
        int hit = 0;

        // col=0 是根集合（老式单指设备的 usage 挂在根上，走 Decode 的兜底路径）；多指从 col=1 开始
        for (int col = 1; col < n; col++)
        {
            ushort c = (ushort)col;

            bool hasTip = HidP_GetUsageValue(HidP_Input, 0x0D, c, 0x42, out uint tv, ctx.Preparsed, report, sizeHid) == HIDP_STATUS_SUCCESS;
            bool tip = !hasTip || tv != 0;   // 取不到 tip 就当作按下（以 W/H/坐标为准）

            uint w = 0, h = 0, p = 0, x = 0, y = 0;
            bool hasW = ctx.HasW && HidP_GetUsageValue(HidP_Input, 0x0D, c, 0x48, out w, ctx.Preparsed, report, sizeHid) == HIDP_STATUS_SUCCESS;
            bool hasH = ctx.HasH && HidP_GetUsageValue(HidP_Input, 0x0D, c, 0x49, out h, ctx.Preparsed, report, sizeHid) == HIDP_STATUS_SUCCESS;
            bool hasP = ctx.HasP && HidP_GetUsageValue(HidP_Input, 0x0D, c, 0x30, out p, ctx.Preparsed, report, sizeHid) == HIDP_STATUS_SUCCESS;
            bool hasX = HidP_GetUsageValue(HidP_Input, 0x01, c, 0x30, out x, ctx.Preparsed, report, sizeHid) == HIDP_STATUS_SUCCESS;
            bool hasY = HidP_GetUsageValue(HidP_Input, 0x01, c, 0x31, out y, ctx.Preparsed, report, sizeHid) == HIDP_STATUS_SUCCESS;

            if (!hasTip && !hasW && !hasH && !hasP && !hasX && !hasY)
                continue;   // 本帧这个集合没有手指数据
            hit++;

            // contact id（0x51）缺失时用集合索引代替
            int id = col;
            if (HidP_GetUsageValue(HidP_Input, 0x0D, c, 0x51, out uint cid, ctx.Preparsed, report, sizeHid) == HIDP_STATUS_SUCCESS)
                id = (int)cid;

            if (hasTip && !tip)
            {
                table.Remove(id);
                continue;
            }

            table[id] = new ContactInfo
            {
                Ticks = now,
                WMm = hasW ? ToMm(w, ctx.WLogMin, ctx.WLogMax, ctx.WPhysMin, ctx.WPhysMax, ctx.WExp, ctx.WUnits) : null,
                HMm = hasH ? ToMm(h, ctx.HLogMin, ctx.HLogMax, ctx.HPhysMin, ctx.HPhysMax, ctx.HExp, ctx.HUnits) : null,
                P01 = hasP && ctx.PLogMax > ctx.PLogMin
                    ? Math.Clamp((double)(p - (uint)ctx.PLogMin) / (ctx.PLogMax - ctx.PLogMin), 0, 1)
                    : null,
                PRaw = hasP ? (int)p : 0,
                HasP = hasP,
                WLogical = hasW ? (int)w : 0,
                HLogical = hasH ? (int)h : 0,
                HasPos = hasX && ctx.XLogMax > 0,
                XNorm = hasX && ctx.XLogMax > 0 ? Math.Clamp((double)x / ctx.XLogMax, 0, 1) : 0,
                YNorm = hasY && ctx.YLogMax > 0 ? Math.Clamp((double)y / ctx.YLogMax, 0, 1) : 0,
            };
        }

        // 超时清理：分帧轮发的驱动只更新当前帧的手指；静默超窗的手指视为已抬起
        if (table.Count > 0)
        {
            List<int> stale = table.Where(kv => now - kv.Value.Ticks > ContactStaleMs)
                .Select(kv => kv.Key).ToList();
            foreach (int key in stale)
                table.Remove(key);
        }

        return hit;
    }

    /// <summary>
    /// 把活动 contact 表整理成**分指**样本：顶层填各指概要（max 尺寸 / Σ面积 / 加权中心 / max 压感），
    /// 完整的每指数据放 Contacts（下游按指分桶，各画各的框）。单指退化为原值。
    /// </summary>
    private TouchSample? BuildContactsSample(DeviceCtx ctx, IntPtr hDevice)
    {
        if (!_contacts.TryGetValue(hDevice, out Dictionary<int, ContactInfo>? table) || table.Count == 0)
            return null;

        double? maxW = null, maxH = null, maxP = null, sum = null;
        int maxWLogical = 0;
        double sx = 0, sy = 0, sw = 0;
        ContactInfo? pMax = null;

        var list = new List<ContactRect>(table.Count);
        foreach ((int id, ContactInfo k) in table)
        {
            list.Add(new ContactRect(id, null, k.WMm, k.HMm, k.HasPos ? k.XNorm : null, k.HasPos ? k.YNorm : null,
                k.HasP ? k.P01 : null, k.WLogical, k.HLogical));

            if (k.WMm is double w && k.HMm is double h)
            {
                sum = (sum ?? 0) + w * h;
                if (w > (maxW ?? 0)) maxW = w;
                if (h > (maxH ?? 0)) maxH = h;
            }
            if (k.WLogical > maxWLogical) maxWLogical = k.WLogical;
            if (k.HasP && k.P01 is double pv && pv > (maxP ?? 0)) { maxP = pv; pMax = k; }

            if (k.HasPos)
            {
                double weight = k.WMm is double ww && k.HMm is double hh ? ww * hh : 1;   // 无尺寸的接触等权参与定位
                sx += k.XNorm * weight;
                sy += k.YNorm * weight;
                sw += weight;
            }
        }

        double? xn = sw > 0 ? Math.Clamp(sx / sw, 0, 1) : null;
        double? yn = sw > 0 ? Math.Clamp(sy / sw, 0, 1) : null;

        string detail;
        if (table.Count == 1)
        {
            ContactInfo only = table.Values.First();
            detail = $"W={only.WLogical}/{ctx.WLogMax} → {Precision.Fmt(only.WMm)} mm  " +
                     $"H={only.HLogical}/{ctx.HLogMax} → {Precision.Fmt(only.HMm)} mm" +
                     (only.HasP && only.P01 is double pv ? $"  P={only.PRaw}({Precision.Fmt(pv, 2)})" : "");
        }
        else
        {
            detail = $"{table.Count} 指 Σ {Precision.Fmt(sum, 0)} mm²: " +
                     string.Join("，", list.Select(c => $"#{c.Id} {Precision.Fmt(c.WMm)}×{Precision.Fmt(c.HMm)} mm")) +
                     (maxP is double mp ? $"  P={pMax!.PRaw}({Precision.Fmt(mp, 2)})" : "");
        }

        return new TouchSample("RawHID", maxW, maxH, maxP, detail,
            PressureRaw: pMax is not null && pMax.HasP ? pMax.PRaw : null,
            PressureRange: pMax is not null && pMax.HasP ? $"{ctx.PLogMin}..{ctx.PLogMax}" : "",
            WidthLogical: maxWLogical, HeightLogical: maxWLogical,
            XNorm: xn, YNorm: yn,
            Contacts: list);
    }

    /// <summary>
    /// 逻辑值 → 毫米。**全量程线性映射、全程双精度**：
    /// fraction = (logical − LogicalMin) / (LogicalMax − LogicalMin)；
    /// physical = PhysicalMin + fraction × (PhysicalMax − PhysicalMin)；再按 UnitsExp / Units 换算成 mm。
    /// 物理量程缺失（PhysicalMax ≤ PhysicalMin）时返回 null —— 该设备报的尺寸不可用，由 WPF 通路兜底。
    /// </summary>
    private double? ToMm(uint logical, int logicalMin, int logicalMax,
        int physicalMin, int physicalMax, int unitsExp, int units)
    {
        long logRange = (long)logicalMax - logicalMin;
        if (logRange <= 0)
            return null;

        long physRange = (long)physicalMax - physicalMin;
        if (physRange <= 0)
            return null;   // 设备没给换算说明 → 尺寸不可用

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
        var allHid = new List<string>();   // 诊断：全部 HID 设备（含非触摸）
        int hidTotal = 0;

        for (int i = 0; i < list.Length; i++)
        {
            if (list[i].dwType != RIM_TYPEHID)
                continue;

            hidTotal++;
            IntPtr hDevice = list[i].hDevice;
            IntPtr preparsed = GetPreparsed(hDevice, out bool ok);
            if (!ok || preparsed == IntPtr.Zero)
            {
                allHid.Add($"[{hidTotal}] {GetDeviceName(hDevice)} | 无 preparsed data");
                continue;
            }

            if (!HidP_GetCaps(preparsed, out HIDP_CAPS caps))
            {
                Marshal.FreeHGlobal(preparsed);
                allHid.Add($"[{hidTotal}] {GetDeviceName(hDevice)} | HidP_GetCaps 失败");
                continue;
            }

            var ctx = new DeviceCtx
            {
                Preparsed = preparsed,
                // 触摸屏（Digitizer / Touch Screen）都算：0x0D/0x04=Touch Screen，
                // 0x0D/0x20=Stylus，0x0D/0x22=Multi-touch Digitizer，0x0D/0x05=Touch Pad
                IsTouch = caps.UsagePage == 0x0D && caps.Usage is 0x04 or 0x20 or 0x22 or 0x05,
                LinkCollections = caps.NumberLinkCollectionNodes,
            };

            // 诊断：不管是不是触摸，都把 UsagePage/Usage/集合数打出来
            allHid.Add($"[{hidTotal}] UsagePage=0x{caps.UsagePage:X2} Usage=0x{caps.Usage:X2} "
                       + $"collections={caps.NumberLinkCollectionNodes} "
                       + $"inCaps={caps.NumberInputValueCaps} inLen={caps.InputReportByteLength} "
                       + $"| {(ctx.IsTouch ? "★触摸屏" : "非触摸")} | {GetDeviceName(hDevice)}");

            if (caps.NumberInputValueCaps > 0)
            {
                var unread = new List<string>();
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
                        if (usagePage == 0x0D && usage == 0x51) ctx.HasContactId = true;
                        if (usagePage == 0x0D && usage == 0x42) ctx.HasTipSwitch = true;
                        if (usagePage == 0x0D && usage == 0x55) ctx.MaxContacts = logicalMax > 0 ? logicalMax : logicalMin;
                        if (usagePage == 0x01 && usage == 0x30) ctx.XLogMax = logicalMax;
                        if (usagePage == 0x01 && usage == 0x31) ctx.YLogMax = logicalMax;

                        // 声明了但本项目未读取的用法（让界面如实说明"驱动有、我们没取"）
                        if (usagePage == 0x0D && usage == 0x3F) unread.Add("倾斜角 0x3F");
                        if (usagePage == 0x0D && usage == 0x52) unread.Add("方向/方位角 0x52");
                        if (usagePage == 0x0D && usage == 0x56) unread.Add("扫描时间 0x56");
                        if (usagePage == 0x0D && usage == 0x54) unread.Add("接触计数 0x54");
                    }
                }
                ctx.Unread = unread.Count > 0 ? string.Join("、", unread.Distinct()) : "";
            }

            if (_ctx.TryGetValue(hDevice, out DeviceCtx? old) && old.Preparsed != IntPtr.Zero && old.Preparsed != preparsed)
                Marshal.FreeHGlobal(old.Preparsed);
            _ctx[hDevice] = ctx;

            if (ctx.IsTouch)
            {
                string name = GetDeviceName(hDevice);
                found.Add($"{name} | W={ctx.HasW} H={ctx.HasH} P={ctx.HasP} | " +
                          $"contactId={ctx.HasContactId} tipSwitch={ctx.HasTipSwitch} 最大接触={ctx.MaxContacts} collections={ctx.LinkCollections}");
                TouchDeclaresSize |= ctx.HasW && ctx.HasH;
                TouchDeclaresPressure |= ctx.HasP;

                // 换算表 / 能力表：取第一块触摸屏（多块时够用，回显的是驱动真实声明）
                DeviceName = name;
                MaxContactsDeclared = ctx.MaxContacts;
                LinkCollectionCount = ctx.LinkCollections;
                HasWidthUsage = ctx.HasW;
                HasHeightUsage = ctx.HasH;
                HasPressureUsage = ctx.HasP;
                HasContactIdUsage = ctx.HasContactId;
                HasTipSwitchUsage = ctx.HasTipSwitch;
                UnreadUsages = ctx.Unread ?? "";
                WScaleText = ScaleText("宽 0x48", ctx.HasW, ctx.WLogMin, ctx.WLogMax, ctx.WPhysMin, ctx.WPhysMax, ctx.WExp, ctx.WUnits);
                HScaleText = ScaleText("高 0x49", ctx.HasH, ctx.HLogMin, ctx.HLogMax, ctx.HPhysMin, ctx.HPhysMax, ctx.HExp, ctx.HUnits);
            }
        }

        DiagSummary = found.Count > 0
            ? "触摸 HID 设备：\n  " + string.Join("\n  ", found)
            : "未发现触摸屏 HID 设备（Digitizer 0x0D / Touch Screen 0x04）";

        // 诊断：无论找没找到触摸屏，都把全部 HID 设备打出来（定位"驱动有、但没匹配上"）
        if (allHid.Count > 0)
            Log.Info($"HID 设备清单（共 {allHid.Count} 个 RIM_TYPEHID，含非触摸）：\n  " + string.Join("\n  ", allHid));
        else
            Log.Info("HID 设备清单：RawInput 里一个 RIM_TYPEHID 都没有（触摸屏可能走数字化器通路，不暴露 HID 顶层设备）");
    }

    /// <summary>把「换算表」组成一行人话：逻辑量程 → 物理量程 → 每单位多少 mm。</summary>
    private static string ScaleText(string label, bool has, int logMin, int logMax,
        int physMin, int physMax, int exp, int units)
    {
        if (!has)
            return $"{label}: 设备未声明";
        long phys = (long)physMax - physMin;
        if (phys <= 0)
            return $"{label}: 无换算说明（逻辑 {logMin}..{logMax}，物理缺失 → 尺寸不可换算）";
        bool english = (units & 0xF000) == 0x3000;
        double perUnit = english ? 25.4 : 10.0;   // 1 物理单位 = 英寸(25.4mm) / 厘米(10mm)
        int e = exp & 0xF; if (e >= 8) e -= 16;
        double unitMm = Math.Pow(10, e) * perUnit;
        double perCount = phys > 0 ? unitMm * phys / ((long)logMax - logMin) : 0;
        return $"{label}: 逻辑 {logMin}..{logMax} → 物理 {physMin}..{physMax}"
             + $"（单位{(english ? "英寸" : "厘米")}×10^{e} = {Precision.Fmt(unitMm, 4)} mm）"
             + $" → {Precision.Fmt(perCount, 6)} mm/计数";
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
