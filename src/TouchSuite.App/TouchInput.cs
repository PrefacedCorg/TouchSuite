using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace TouchSuite.App;

/// <summary>多指中的一根接触（WPF = 一个 TouchDevice）。</summary>
public sealed record ContactRect(
    int Id,
    Rect? DiuRect,                     // WPF：相对 pad 的 DIU 矩形
    double? WMm, double? HMm,          // WPF：按屏幕标定换算出的接触宽高 mm
    double? XNorm, double? YNorm,      // 归一化坐标（当前 WPF 通路未提供，恒 null）
    double? P01,                        // 压感（当前 WPF 面积通路不提供，见 Stylus 通路）
    int WLogical, int HLogical);        // 原始计数值（WPF 通路无此概念，恒 0）

/// <summary>
/// 一次触摸样本。WidthMm/HeightMm 为 null 表示没量到尺寸；
/// 多指时顶层字段是各指的概要，完整的分指数据在 <see cref="Contacts"/>；
/// DiuRect 为元素内 DIP 矩形。
/// </summary>
public sealed record TouchSample(string Source, double? WidthMm, double? HeightMm, double? Pressure01, string Detail,
    int? PressureRaw = null, string PressureRange = "",
    int WidthLogical = 0, int HeightLogical = 0,
    double? XNorm = null, double? YNorm = null,
    double? ScreenPxX = null, double? ScreenPxY = null,
    Rect? DiuRect = null,
    int FrameId = 0, uint TimeMs = 0,
    IReadOnlyList<ContactRect>? Contacts = null,
    int XLogMax = 0, int YLogMax = 0,
    int WidthLogMax = 0, int HeightLogMax = 0,
    double? XPhysMm = null, double? YPhysMm = null,
    string DeviceKey = "")
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
/// 触摸输入读取（WPF 通路）：
/// ① WPF TouchPoint.Bounds —— 接触面积（系统 WM_TOUCH 通路，逐帧、实时）；
/// ② WPF Stylus.PressureFactor —— 压感（触摸被提升为触笔后逐帧给出，实时）。
/// 接触尺寸 mm 由屏幕标定（mm/DIP）换算，不依赖设备上报的换算表。
/// </summary>
public sealed class TouchInput : IDisposable
{
    public event Action<TouchSample>? Sample;

    /// <summary>WPF 路径用：把 DIP 换算成毫米（主窗口校准后设置）。</summary>
    public double MmPerDiuX { get; set; }
    public double MmPerDiuY { get; set; }

    /// <summary>（诊断探针用）最近一次 WPF 触摸接触框（DIP）——拿来和原始HID 同刻对照。无则 null。</summary>
    public Rect? LastWpfBounds { get; private set; }

    /// <summary>（诊断探针用）最近一次 Stylus 压感。</summary>
    public double? LastStylusPressure => _lastStylusPressure;

    private bool _disposed;

    /// <summary>诊断节拍：每秒打印一次 Stylus 通道计数。</summary>
    private DispatcherTimer? _poll;

    /// <summary>挂上窗口消息钩子（当前 WPF 通路不需要，保留接口以兼容调用方）。</summary>
    public void Attach(Window window)
    {
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
    /// WPF 通路：从元素上的触摸事件取 Bounds（面积）；同时挂 Stylus 探针取逐帧压感。
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

        // 记最大那根，供「通道对照」探针
        LastWpfBounds = list.Where(c => c.DiuRect is not null)
            .OrderByDescending(c => c.DiuRect!.Value.Width * c.DiuRect!.Value.Height)
            .Select(c => c.DiuRect)
            .FirstOrDefault();

        Sample?.Invoke(new TouchSample("WPF",
            touches.Count == 1 ? onlyW : null, touches.Count == 1 ? onlyH : null,
            null, detail,
            DiuRect: touches.Count == 1 ? onlyRect : null,
            Contacts: list));
    }

    private int _stylusCount;
    private double? _lastStylusPressure;
    private bool _stylusDiagLogged;

    /// <summary>WPF 触笔通路：读逐帧 PressureFactor（触摸被提升为触笔时会走这里）。</summary>
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

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _poll?.Stop();
    }
}
