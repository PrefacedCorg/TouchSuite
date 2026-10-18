using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace TouchSuite.Ink;

/// <summary>
/// 上层 WPF 窗口：工具栏 + 输入 + 模式切换。
///
/// <para>层次是靠两个窗口的 HWND 顺序实现的（不是同一窗口叠两层）：底层是 <see cref="InkSurfaceWindow"/>
/// 承载 DComp 墨迹，本窗口置顶并铺满整屏，批注模式下它就是输入目标；鼠标模式给本窗口加上
/// WS_EX_TRANSPARENT，点击就穿到桌面去。</para>
/// </summary>
public partial class MainWindow : Window
{
    private const int HWND_TOPMOST = -1;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    private InkSurfaceWindow? _inkWindow;
    private IInkLayer? _layer;

    private InkToolKind _tool = InkToolKind.Pen;
    private Color _color = Color.FromRgb(0x1A, 0x1A, 0x1A);
    private double _width = 6;
    private bool _annotating;

    private const int MousePointerId = 0;

    private int _strokeSeq;

    // ---- 输入源（工具栏下拉框） ----
    private const int WM_INPUT = 0x00FF;

    /// <summary>WM_POINTER 的 pointerId 加这个偏移当笔画键，避免与 RawInput 的 StableId 撞键。</summary>
    private const uint PointerIdBase = 100000;

    private InkInputMode _mode = InkInputMode.Wpf;

    /// <summary>WPF 的 Stylus/Touch 通道是否放行。非 WPF 模式下这两条通道必须门掉，否则会被提升重复落笔。</summary>
    private bool _wpfChannelActive = true;

    private bool _suppressInputCombo;         // 程序内改下拉选中项时不重入
    private bool _uiReady;                    // InitializeComponent 期间控件还没赋值，别在这里切源/刷状态
    private IntPtr _hwnd;
    private HwndSource? _hwndSrc;
    private HwndSourceHook? _wndProcHook;    // 必须存字段，否则委托被 GC 后回调悬空
    private ContactTracker? _tracker;

    private readonly List<ContactTracker.ContactEvent> _events = new();
    private string _rawInfo = "";
    private string _pointerInfo = "";
    private int _rawActiveCount;
    private int _pointerActiveCount;
    private int _gateDroppedStylus;
    private int _gateDroppedMouse;
    private long _pointerFrames;

    // 虚拟屏范围 + 墨迹层范围（层只铺主屏，RawInput 坐标按虚拟屏换算后裁剪进来）
    private int _vx, _vy, _vw, _vh, _layerW, _layerH;

    // 探针计数（--inputprobe 用）
    private int _probeFrames, _probeSubReports, _probeContacts;
    private int _rawWmInputCount;             // WM_INPUT 消息总数（不管解没解出来），用来区分"没来"与"来了解不出"
    private Stopwatch? _probeWatch;

    /// <summary>触点 id → 本次笔画 id（0 表示"这根在擦"，不建笔画）。多指的关键就是这份表。</summary>
    private readonly Dictionary<int, int> _strokeOfPointer = new();

    /// <summary>触点 id → 它最近一次的落点（只用于「互斥失效」同刻断言，不参与绘制）。</summary>
    private readonly Dictionary<int, Point> _lastPointOf = new();

    /// <summary>同时在场的触笔指针数（>0 时鼠标事件忽略，避免同一根手指画两遍）。</summary>
    private int _stylusActiveCount;

    /// <summary>见过触笔通道后就不再走 Touch 通道 —— 触摸会被 WPF 提升成 Stylus，两边都收会重复落笔。</summary>
    private bool _sawStylus;

    private int _histLogged;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>
    /// 句柄与 HwndSource 在这里取（不是 Loaded）：WPF 官方建议的时机，保证早于第一次输入。
    /// hook 用来收 WM_INPUT（RawInput）与 WM_POINTER*。
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;
        _hwndSrc = PresentationSource.FromVisual(this) as HwndSource;
        _wndProcHook = WndProcHook;
        _hwndSrc?.AddHook(_wndProcHook);

        _vx = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN);
        _vy = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
        _vw = Math.Max(1, Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN));
        _vh = Math.Max(1, Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN));
        _layerW = Math.Max(1, Win32.GetSystemMetrics(Win32.SM_CXSCREEN));
        _layerH = Math.Max(1, Win32.GetSystemMetrics(Win32.SM_CYSCREEN));
        _tracker = new ContactTracker(_layerW, _layerH);

        Log.Write($"[输入] 句柄=0x{_hwnd.ToInt64():X} hook={(_hwndSrc is not null)} "
                + $"虚拟屏=({_vx},{_vy},{_vw},{_vh}) 墨迹层={_layerW}x{_layerH}");

        _uiReady = true;
        if (App.StartupInput != InkInputMode.Wpf)
            SwitchInputSource(App.StartupInput);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (App.InputProbe)
        {
            RunInputProbe();
            return;
        }

        _inkWindow = new InkSurfaceWindow();

        DCompInkLayer? dcomp = null;
        if (_inkWindow.Handle != IntPtr.Zero)
        {
            dcomp = new DCompInkLayer(_inkWindow.Handle, _inkWindow.X, _inkWindow.Y,
                _inkWindow.Width, _inkWindow.Height);
            Log.Write("准备初始化 DComp 墨迹层……");
            if (dcomp.Initialize())
            {
                _layer = dcomp;
                Log.Write($"墨迹层后端 = {dcomp.BackendName}");
            }
            else
            {
                Log.Write($"DComp 初始化失败：{dcomp.LastError} → 回退 WPF 层");
            }
        }
        else
        {
            Log.Write("底层墨迹窗口创建失败 → 回退 WPF 层");
        }

        if (_layer is null)
        {
            string reason = dcomp?.LastError ?? "底层窗口创建失败";
            dcomp?.Dispose();
            var wpf = new WpfInkLayer(FallbackCanvas) { LastError = reason };
            wpf.Initialize();
            _layer = wpf;
            Log.Write($"墨迹层后端 = {wpf.BackendName}");
        }

        SetAnnotating(true);

        if (App.SelfTest)
            RunSelfTest();
    }

    /// <summary>自检：推一条正弦测试笔画，2.5 秒后自动退出（用于验证 DComp 画没画出来）。</summary>
    private void RunSelfTest()
    {
        if (_layer is null)
            return;

        int id = ++_strokeSeq;
        _layer.Begin(id, new InkSample(300, 300, 9, 26, 111, 235, 255));
        for (int i = 0; i < 60; i++)
        {
            float x = 300 + i * 12f;
            float y = 300 + 80f * MathF.Sin(i / 8f);
            _layer.Extend(id, new InkSample(x, y, 4 + (i % 5), 26, 111, 235, 255));
        }
        _layer.End(id);
        Log.Write("[自检] 已推入测试笔画");

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Log.Write("[自检] 关闭窗口");
            Close();
        };
        timer.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _layer?.Dispose();
        _inkWindow?.Dispose();
        Log.Write("MainWindow 已关闭");
    }

    // ================= 输入源：RawInput / WM_POINTER =================

    /// <summary>WM_INPUT 与 WM_POINTER* 都在这里收（hook 委托已存字段，不会被 GC）。</summary>
    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_INPUT:
                _rawWmInputCount++;
                if (_annotating && _mode is InkInputMode.RawHid or InkInputMode.Combo)
                    OnRawInput(lParam);
                break;

            case PointerTouchFrame.WM_POINTERDOWN:
            case PointerTouchFrame.WM_POINTERUPDATE:
            case PointerTouchFrame.WM_POINTERUP:
                if (_annotating && _mode is InkInputMode.WmPointer or InkInputMode.Combo)
                    OnPointerMessage(wParam);
                break;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// RawInput 一帧：一条 WM_INPUT 里可能带**多份子报文**（等于多份历史采样），
    /// 逐份按"一帧"回放，绝不合并取最后一条（Android 端踩过这个坑）。
    /// </summary>
    private void OnRawInput(IntPtr lParam)
    {
        if (!RawHidTouch.TryReadFrames(lParam, out List<RawHidTouch.RawFrame> frames) || frames.Count == 0)
        {
            TryRescanForDevice();
            return;
        }

        _probeFrames++;
        _probeSubReports += frames.Count;

        var ins = new List<ContactTracker.TrackIn>();
        foreach (RawHidTouch.RawFrame frame in frames)
        {
            _probeContacts += frame.Contacts.Count;
            ins.Clear();
            foreach (RawHidTouch.RawContact c in frame.Contacts)
                ins.Add(ToTrackIn(c));

            // 前几帧把「原始量纲 → 换算像素」打出来：一眼能看出量程适配对不对（量程错会全部挤在左上角）
            if (_probeFrames <= 6)
            {
                for (int i = 0; i < frame.Contacts.Count && i < ins.Count; i++)
                {
                    RawHidTouch.RawContact c = frame.Contacts[i];
                    ContactTracker.TrackIn t = ins[i];
                    Log.Write($"[RawInput明细] 子帧{frame.SubIndex}/{frame.SubCount} 槽={c.Link} CID={c.ContactId} "
                            + $"按下={c.Down} 原始({c.XLogical}/{c.XLogMax}, {c.YLogical}/{c.YLogMax}) "
                            + $"→ px=({t.X:0.#},{t.Y:0.#}) 尺寸={t.W:0.#}x{t.H:0.#} 压感={t.Pressure01:0.###}");
                }
            }

            _tracker!.Sync(ins, frame.CountZero, _events);
            FeedEvents();
        }

        _rawActiveCount = _tracker!.ActiveCount;
        if (_probeFrames % 240 == 0)
        {
            _rawInfo = $"HID 0x0D RIDEV_INPUTSINK 消息={_probeFrames} {_tracker.Describe()}";
            UpdateStatus();
        }
    }

    /// <summary>
    /// 报文里的设备不在表里（例如启动后才出现的 user32 合成指针设备）→ 限流重扫把设备补进表。
    /// 不重扫的话这批报文会被静默丢掉，而"启动后就出现的设备"会一直收不到。
    /// </summary>
    private void TryRescanForDevice()
    {
        if (RawHidTouch.LastUnknownDevice == IntPtr.Zero || !RawHidTouch.TryBeginAutoRescan())
            return;
        IntPtr unknown = RawHidTouch.LastUnknownDevice;
        RawHidTouch.LastUnknownDevice = IntPtr.Zero;
        List<RawHidTouch.HidDeviceInfo> devices = RawHidTouch.Scan();
        Log.Write($"[RawInput] 未登记设备 0x{unknown.ToInt64():X} → 重扫，设备数={devices.Count}");
    }

    /// <summary>原始 HID 量纲 → 屏幕像素：按虚拟屏换算，再裁剪进墨迹层（层只铺主屏）。</summary>
    private ContactTracker.TrackIn ToTrackIn(RawHidTouch.RawContact c)
    {
        float px = 0, py = 0, w = 0, h = 0;
        if (c.XLogMax > 0)
        {
            double nx = Math.Clamp((double)c.XLogical / c.XLogMax, 0, 1);
            px = (float)Math.Clamp(_vx + nx * _vw, 0, _layerW - 1);
            w = (float)((double)c.WLogical / c.XLogMax * _vw);
        }
        if (c.YLogMax > 0)
        {
            double ny = Math.Clamp((double)c.YLogical / c.YLogMax, 0, 1);
            py = (float)Math.Clamp(_vy + ny * _vh, 0, _layerH - 1);
            h = (float)((double)c.HLogical / c.YLogMax * _vh);
        }
        return new ContactTracker.TrackIn(c.Link, c.ContactId, c.Down, px, py, w, h,
            (float)(c.Pressure01 ?? 1.0));
    }

    /// <summary>把合成后的手指事件落到既有的落笔/移动/抬笔函数上（不另开绘制通路）。</summary>
    private void FeedEvents()
    {
        if (!_annotating || _layer is null)
            return;

        foreach (ContactTracker.ContactEvent e in _events)
        {
            var p = new Point(e.X, e.Y);
            switch (e.Kind)
            {
                case ContactTracker.Kind.Down:
                    if (!_strokeOfPointer.ContainsKey(e.StableId))
                        PointerDown(e.StableId, p, e.Pressure01, e.W, e.H);
                    break;
                case ContactTracker.Kind.Move:
                    PointerMove(e.StableId, p, e.Pressure01, e.W, e.H);
                    break;
                case ContactTracker.Kind.Up:
                    PointerUp(e.StableId);
                    break;
            }
        }
    }

    /// <summary>
    /// WM_POINTER：用帧 API 一次读全该帧所有触点。历史采样（historyCount&gt;1）按 API 返回顺序逐帧回放；
    /// 处理完整帧后必须 SkipPointerFrameMessages，否则同一帧会被派发 N 次。
    /// </summary>
    private void OnPointerMessage(IntPtr wParam)
    {
        uint pid = PointerTouchFrame.GetPointerId(wParam);
        if (!PointerTouchFrame.TryReadFrame(pid, out List<PointerTouchFrame.PointerContact> contacts,
                out bool isTouch, out int err))
        {
            if (err != 0)
                Log.Write($"[POINTER] 读帧失败 pointerId={pid} Win32={err}"
                        + (err == 5 ? "（hook 挂错窗口）" : err == 232 ? "（帧已被消费）" : ""));
            return;
        }
        if (!isTouch)
            return;

        _probeFrames++;
        _probeContacts += contacts.Count;
        _pointerFrames++;

        if (contacts.Count > 0 && contacts[0].HistoryCount > 1 &&
            PointerTouchFrame.TryReadFrameHistory(pid, out List<List<PointerTouchFrame.PointerContact>> history, out _))
        {
            if (_pointerFrames <= 3)
                Log.Write($"[POINTER] historyCount={contacts[0].HistoryCount} → 历史帧 {history.Count} 条，"
                        + $"各帧首点X=[{string.Join(",", history.ConvertAll(f => f.Count > 0 ? f[0].Xpx.ToString() : "-"))}]"
                        + "（实测新的在前，所以倒序回放）");
            _probeSubReports += history.Count;
            // 实测 GetPointerFrameTouchInfoHistory 返回的是「新的在前」（注入递增 440→800，数组里是 800→440）。
            // 必须倒序回放才是时间正序，否则笔画会被画反、笔尖退回到上一采样。
            for (int i = history.Count - 1; i >= 0; i--)
                ApplyPointerFrame(history[i]);
        }
        else
        {
            ApplyPointerFrame(contacts);
        }

        PointerTouchFrame.SkipFrame(pid);
        _pointerActiveCount = _strokeOfPointer.Keys.Count(k => k >= PointerIdBase);
        _pointerInfo = $"GetPointerFrameTouchInfo 帧 API（PT_TOUCH 帧={_pointerFrames}，末帧 {contacts.Count} 点）";
        if (_pointerFrames % 240 == 0)
            UpdateStatus();
    }

    private void ApplyPointerFrame(List<PointerTouchFrame.PointerContact> frame)
    {
        if (!_annotating || _layer is null)
            return;

        foreach (PointerTouchFrame.PointerContact c in frame)
        {
            int id = (int)(PointerIdBase + c.PointerId);
            var p = new Point(Math.Clamp(c.Xpx, 0, _layerW - 1), Math.Clamp(c.Ypx, 0, _layerH - 1));
            float w = c.Contact.IsValid ? c.Contact.Width : 0;
            float h = c.Contact.IsValid ? c.Contact.Height : 0;
            float pressure = c.Pressure1024 / 1024f;

            if (c.InContact)
            {
                if (!_strokeOfPointer.ContainsKey(id))
                    PointerDown(id, p, pressure, w, h);
                else
                    PointerMove(id, p, pressure, w, h);
            }
            else
            {
                PointerUp(id);
            }
        }
    }

    /// <summary>
    /// 只在用户**真的关掉下拉框**时切源。原先用 SelectionChanged 会被杂散输入误触发：
    /// 实测注入触摸（被提升成鼠标点击）时选中项被改回 WPF，模式就悄悄变了。
    /// </summary>
    private void OnInputSourcePicked(object sender, EventArgs e)
    {
        if (_suppressInputCombo || !_uiReady)
            return;
        var requested = (InkInputMode)Math.Max(0, InputSourceCombo.SelectedIndex);
        if (requested == _mode)
            return;
        Log.Write($"[输入源] 下拉框选定 {InkInputModes.Label(requested)}");
        SwitchInputSource(requested);
    }

    /// <summary>
    /// 切源：先把在场笔画全部 End（否则会留下永远不结束的笔画），再拆旧源、启新源；
    /// 新源启用失败则记日志并把下拉框打回 WPF（不静默失败）。
    /// </summary>
    private void SwitchInputSource(InkInputMode mode)
    {
        foreach (int sid in _strokeOfPointer.Values)
        {
            if (sid != 0)
                _layer?.End(sid);
        }
        _strokeOfPointer.Clear();
        _tracker?.Reset();
        _rawActiveCount = 0;
        _pointerActiveCount = 0;

        if (_mode is InkInputMode.RawHid or InkInputMode.Combo)
            RawHidTouch.Unregister();
        _rawInfo = "";
        _pointerInfo = "";

        string err = "";
        if (mode is InkInputMode.RawHid or InkInputMode.Combo)
        {
            if (_hwnd == IntPtr.Zero)
            {
                err = "窗口句柄为空";
            }
            else if (RawHidTouch.Register(_hwnd, out int win32Error))
            {
                List<RawHidTouch.HidDeviceInfo> devices = RawHidTouch.Scan();
                _rawInfo = $"HID 0x0D RIDEV_INPUTSINK 设备 {devices.Count}";
                _probeFrames = _probeSubReports = _probeContacts = 0;
                foreach (RawHidTouch.HidDeviceInfo d in devices)
                {
                    Log.Write($"[输入源] 设备 0x{d.UsagePage:X2}:0x{d.Usage:X2} 槽={d.LinkCount} "
                            + $"CID={d.HasContactId} Tip={d.HasTipSwitch} Count={d.HasContactCount} "
                            + $"宽高={d.HasWidth}/{d.HasHeight} 压感={d.HasPressure}  {d.Name}");
                }
                if (devices.Count == 0)
                    Log.Write("[输入源] 提示：没枚举到触摸/数字化器 HID 集合，RawInput 收不到报文");
                else if (!devices.TrueForAll(d => d.HasContactId))
                    Log.Write("[输入源] 提示：设备未声明 Contact Identifier(0x0D:0x51)，手指身份走最近邻跟踪");
                if (!devices.Exists(d => d.HasPressure))
                    Log.Write("[输入源] 提示：本机设备未声明压感(0x0D:0x30)，笔宽按满压感");
                if (!devices.Exists(d => d.HasWidth && d.HasHeight))
                    Log.Write("[输入源] 提示：本机设备未声明接触宽高(0x0D:0x48/0x49)，接触尺寸拿不到");
            }
            else
            {
                err = $"RawInput 注册失败 err={win32Error}";
            }
        }

        if (err.Length > 0)
        {
            Log.Write($"[输入源] 启用失败：{err} → 回退 WPF 事件");
            mode = InkInputMode.Wpf;
            _rawInfo = "";
        }

        _mode = mode;
        _wpfChannelActive = mode == InkInputMode.Wpf;
        if (_wpfChannelActive)
            _sawStylus = false;

        _suppressInputCombo = true;
        InputSourceCombo.SelectedIndex = (int)mode;
        _suppressInputCombo = false;

        Log.Write($"[输入源] 切换到 {InkInputModes.Label(mode)}：{InkInputModes.ApiName(mode, _rawInfo, _pointerInfo)}");
        UpdateStatus();
    }

    // ================= 输入探针（--inputprobe） =================

    /// <summary>只测不画：注册 → 打设备清单 → 收 5 秒报文 → 打帧/子报文/触点直方图 → 退出。</summary>
    private void RunInputProbe()
    {
        InkInputMode mode = App.StartupInput == InkInputMode.Wpf ? InkInputMode.RawHid : App.StartupInput;
        Log.Write($"[探针] 输入源={InkInputModes.Label(mode)}，开始收集 10 秒（请在这 10 秒内触摸屏幕）……");
        SetAnnotating(true);            // hook 里的输入处理受这个门控（探针也要收报文）
        SwitchInputSource(mode);
        _probeWatch = Stopwatch.StartNew();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Log.Write(ProbeSummary());
            Close();
        };
        timer.Start();
    }

    private string ProbeSummary()
    {
        double sec = Math.Max(0.001, _probeWatch?.Elapsed.TotalSeconds ?? 0);
        return $"[探针] 结果 输入源={InkInputModes.Label(_mode)} "
             + $"WM_INPUT总数={_rawWmInputCount}（解出消息={_probeFrames} 子报文={_probeSubReports} 触点={_probeContacts}，"
             + $"{_probeFrames / sec:0.#} 消息/秒，{_probeContacts / sec:0.#} 点/秒） "
             + $"门丢弃 Stylus={_gateDroppedStylus} Mouse={_gateDroppedMouse} "
             + $"身份={_tracker?.Describe() ?? "无"}";
    }

    // ================= 模式切换 =================

    /// <summary>
    /// 批注模式：本窗口正常接收输入（并保持在墨迹窗口之上）；
    /// 鼠标模式：本窗口加 WS_EX_TRANSPARENT，鼠标/触摸穿过它落到桌面上。
    /// </summary>
    private void SetAnnotating(bool on)
    {
        _annotating = on;
        ModeMouse.IsChecked = !on;
        ModeAnnotate.IsChecked = on;

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            long ex = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64();
            if (on)
                ex &= ~(long)Win32.WS_EX_TRANSPARENT;
            else
                ex |= Win32.WS_EX_TRANSPARENT;
            Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
            long after = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64();
            Log.Write($"[模式] 批注={on} GWL_EXSTYLE=0x{after:X} "
                    + $"(LAYERED={(after & Win32.WS_EX_LAYERED) != 0} TRANSPARENT={(after & Win32.WS_EX_TRANSPARENT) != 0})");

            // 两个窗口都是 topmost，靠 SetWindowPos 明确把 WPF 这层压到墨迹层上面
            Win32.SetWindowPos(hwnd, new IntPtr(HWND_TOPMOST), 0, 0, 0, 0,
                SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
        }

        UpdateStatus();
    }

    // ================= 落笔（鼠标 / 触笔） =================

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        bool onToolbar = IsOnToolbar(e.OriginalSource);
        Log.Write($"[鼠标] 按下 pos=({e.GetPosition(this).X:0.#},{e.GetPosition(this).Y:0.#}) 按钮={e.ChangedButton} "
                + $"在工具栏={onToolbar} 触笔在用={_stylusActiveCount > 0} 层={_layer?.GetType().Name ?? "无"}");
        if (!_annotating)
            return;
        // 非 WPF 模式下，触摸/触笔会被提升成鼠标事件：必须丢掉，否则同一根手指画两遍。
        if (!_wpfChannelActive &&
            (e.StylusDevice is not null || _rawActiveCount > 0 || _pointerActiveCount > 0))
        {
            _gateDroppedMouse++;
            return;
        }
        if (_stylusActiveCount > 0 || onToolbar)
            return;
        PointerDown(MousePointerId, PointToScreen(e.GetPosition(this)), 1f);
        CaptureMouse();
        e.Handled = true;
    }

    /// <summary>工具栏上的点击不要当成落笔（事件会从子元素冒泡到 Window）。</summary>
    private bool IsOnToolbar(object? source) =>
        source is DependencyObject d && ToolbarBorder.IsAncestorOf(d);

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_annotating)
            return;
        if (!_wpfChannelActive && (e.StylusDevice is not null || _rawActiveCount > 0 || _pointerActiveCount > 0))
        {
            _gateDroppedMouse++;
            return;
        }
        if (_stylusActiveCount > 0 || e.LeftButton != MouseButtonState.Pressed)
            return;
        PointerMove(MousePointerId, PointToScreen(e.GetPosition(this)), 1f);
        e.Handled = true;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_stylusActiveCount > 0)
            return;
        PointerUp(MousePointerId);
        ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnStylusDown(object sender, StylusDownEventArgs e)
    {
        if (!_wpfChannelActive)
        {
            // 非 WPF 模式下触笔由 RawInput 通道负责（真实触笔不会走 WM_POINTER），这里只记丢弃计数
            _gateDroppedStylus++;
            e.Handled = true;
            return;
        }

        _sawStylus = true;
        _stylusActiveCount++;
        int pointerId = e.StylusDevice.Id;

        // 一个 Stylus 事件里可能带回多条采样（输入栈会把积压的移动合并进来）。
        // 只取最后一条 = 中间那些点被吃掉，表现为"跳过几个点直接连线" —— 必须逐条回放。
        StylusPointCollection pts = e.GetStylusPoints(this);
        if (pts.Count == 0)
            return;

        StylusPoint first = pts[0];
        PointerDown(pointerId, PointToScreen(new Point(first.X, first.Y)), first.PressureFactor);
        for (int i = 1; i < pts.Count; i++)
            PointerMove(pointerId, PointToScreen(new Point(pts[i].X, pts[i].Y)), pts[i].PressureFactor);

        CaptureStylus();
        e.Handled = true;
    }

    private void OnStylusMove(object sender, StylusEventArgs e)
    {
        if (!_wpfChannelActive)
        {
            _gateDroppedStylus++;
            e.Handled = true;
            return;
        }

        int pointerId = e.StylusDevice.Id;
        if (!_strokeOfPointer.ContainsKey(pointerId))
            return;

        StylusPointCollection pts = e.GetStylusPoints(this);
        if (pts.Count > 1 && _histLogged < 20)
        {
            _histLogged++;
            Log.Write($"[采样] 单个 Stylus 事件带回 {pts.Count} 条采样（合并），已逐条回放");
        }
        for (int i = 0; i < pts.Count; i++)
        {
            StylusPoint sp = pts[i];
            PointerMove(pointerId, PointToScreen(new Point(sp.X, sp.Y)), sp.PressureFactor);
        }
        e.Handled = true;
    }

    private void OnStylusUp(object sender, StylusEventArgs e)
    {
        if (!_wpfChannelActive)
        {
            _gateDroppedStylus++;
            e.Handled = true;
            return;
        }

        PointerUp(e.StylusDevice.Id);
        if (_stylusActiveCount > 0)
            _stylusActiveCount--;
        if (_stylusActiveCount == 0)
            ReleaseStylusCapture();
        e.Handled = true;
    }

    // ---- 触摸通道：只在"这台机器没有触笔通道"时启用 ----
    // 说明：有触笔通道时，触摸会被 WPF 提升成 Stylus 事件；两条通道都收就会同一根手指画两遍。
    private bool TouchUsable => !_sawStylus;

    private void OnTouchDown(object sender, TouchEventArgs e)
    {
        if (!_wpfChannelActive)
        {
            _gateDroppedStylus++;
            e.Handled = true;
            return;
        }

        if (!TouchUsable)
            return;
        PointerDown(e.TouchDevice.Id, PointToScreen(e.GetTouchPoint(this).Position), 1f);
        e.Handled = true;
    }

    private void OnTouchMove(object sender, TouchEventArgs e)
    {
        if (!_wpfChannelActive)
        {
            _gateDroppedStylus++;
            e.Handled = true;
            return;
        }

        if (!TouchUsable || !_strokeOfPointer.ContainsKey(e.TouchDevice.Id))
            return;
        PointerMove(e.TouchDevice.Id, PointToScreen(e.GetTouchPoint(this).Position), 1f);
        e.Handled = true;
    }

    private void OnTouchUp(object sender, TouchEventArgs e)
    {
        if (!_wpfChannelActive)
        {
            _gateDroppedStylus++;
            e.Handled = true;
            return;
        }

        if (!TouchUsable)
            return;
        PointerUp(e.TouchDevice.Id);
        e.Handled = true;
    }

    // ================= 笔画生命周期（多指：一根手指一条笔画） =================

    private void PointerDown(int pointerId, Point screenPx, float pressure, float contactWpx = 0, float contactHpx = 0)
    {
        if (_layer is null || _strokeOfPointer.ContainsKey(pointerId))
            return;

        if (_tool == InkToolKind.Eraser)
        {
            _strokeOfPointer[pointerId] = 0;   // 0 = 只标记"这根在擦"，不建笔画
            _lastPointOf[pointerId] = screenPx;
            _layer.Erase((float)screenPx.X, (float)screenPx.Y, (float)EraserRadius);
            return;
        }

        // 互斥自检：同一位置冒出另一条来源不同的新笔画 = 门控失效（同一根手指被画了两遍）
        if (CheckDoubleDraw(pointerId, screenPx))
            return;

        int src = SourceOf(pointerId);
        int strokeId = ++_strokeSeq;
        _strokeOfPointer[pointerId] = strokeId;
        _lastPointOf[pointerId] = screenPx;
        _layer.Begin(strokeId, MakeSample(screenPx, pressure));
        Log.Write($"[落笔] 触点={pointerId} 来源={SourceName(src)} 笔画={strokeId} "
                + $"px=({screenPx.X:0.#},{screenPx.Y:0.#}) 压感={pressure:0.##} 宽度={_width:0.#} "
                + $"接触尺寸={contactWpx:0.#}x{contactHpx:0.#}px 同时在场={_strokeOfPointer.Count}");
    }

    /// <summary>来源代码：0=鼠标，1=RawInput，2=WM_POINTER，3=WPF Stylus/Touch。用于互斥自检与日志。</summary>
    private int SourceOf(int pointerId) =>
        pointerId == MousePointerId ? 0
        : pointerId >= PointerIdBase ? 2
        : _wpfChannelActive ? 3 : 1;

    private static string SourceName(int code) => code switch
    {
        0 => "鼠标",
        2 => "WM_POINTER",
        3 => "WPF",
        _ => "RawInput",
    };

    /// <summary>
    /// 同一位置（&lt;3px）有另一条来源不同的在场笔画 → 记 WARN 并拒绝这一笔。
    /// 比事后翻日志快得多，是"三源互斥"失效的即时信号。
    /// </summary>
    private bool CheckDoubleDraw(int pointerId, Point screenPx)
    {
        int src = SourceOf(pointerId);
        foreach (KeyValuePair<int, int> kv in _strokeOfPointer)
        {
            if (kv.Key == pointerId || SourceOf(kv.Key) == src || kv.Value == 0)
                continue;
            if (!_lastPointOf.TryGetValue(kv.Key, out Point other))
                continue;
            double dx = other.X - screenPx.X, dy = other.Y - screenPx.Y;
            if (dx * dx + dy * dy > 9.0)
                continue;
            Log.Write($"WARN [互斥失效] 触点={pointerId}({SourceName(src)}) 与 触点={kv.Key}({SourceName(SourceOf(kv.Key))}) "
                    + $"落在同一点 ({screenPx.X:0.#},{screenPx.Y:0.#})，已拒绝第二笔");
            return true;
        }
        return false;
    }

    private void PointerMove(int pointerId, Point screenPx, float pressure, float contactWpx = 0, float contactHpx = 0)
    {
        if (_layer is null || !_strokeOfPointer.TryGetValue(pointerId, out int strokeId))
            return;

        _lastPointOf[pointerId] = screenPx;

        if (_tool == InkToolKind.Eraser || strokeId == 0)
        {
            _layer.Erase((float)screenPx.X, (float)screenPx.Y, (float)EraserRadius);
            return;
        }
        _layer.Extend(strokeId, MakeSample(screenPx, pressure));
    }

    private void PointerUp(int pointerId)
    {
        _lastPointOf.Remove(pointerId);
        if (_layer is null || !_strokeOfPointer.Remove(pointerId, out int strokeId))
            return;
        if (strokeId != 0)
            _layer.End(strokeId);
    }

    private double EraserRadius => Math.Max(8.0, _width * 2.5);

    private InkSample MakeSample(Point screenPx, float pressure)
    {
        float t = Math.Clamp(pressure, 0f, 1f);
        float w = (float)(_width * (0.35 + 0.65 * t));
        return new InkSample((float)screenPx.X, (float)screenPx.Y, w, _color.R, _color.G, _color.B, _color.A);
    }

    // ================= 工具栏 =================

    private void OnModeMouse(object sender, RoutedEventArgs e) => SetAnnotating(false);

    private void OnModeAnnotate(object sender, RoutedEventArgs e) => SetAnnotating(true);

    private void OnToolPen(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton tb && tb.IsChecked != true)
            tb.IsChecked = true;   // 不许把两个工具都取消
        _tool = InkToolKind.Pen;
        ToolEraser.IsChecked = false;
    }

    private void OnToolEraser(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton tb && tb.IsChecked != true)
            tb.IsChecked = true;
        _tool = InkToolKind.Eraser;
        ToolPen.IsChecked = false;
    }

    private void OnSwatch(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string hex && ColorConverter.ConvertFromString(hex) is Color c)
        {
            _color = c;
            Log.Write($"[工具栏] 颜色 → {hex}");
        }
    }

    private void OnWidthChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _width = e.NewValue;
    }

    private void OnUndo(object sender, RoutedEventArgs e) => _layer?.Undo();

    private void OnClear(object sender, RoutedEventArgs e) => _layer?.Clear();

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void UpdateStatus()
    {
        if (StatusText is null)
            return;   // InitializeComponent 期间控件还没赋值
        string backend = _layer?.BackendName ?? "尚未初始化";
        string hint = _annotating ? "批注模式：可落笔" : "鼠标模式：点击穿透到桌面";
        string input = InkInputModes.ApiName(_mode, _rawInfo, _pointerInfo);
        StatusText.Text = $"{backend}　|　输入源：{InkInputModes.Label(_mode)}（{input}）　|　{hint}　|　Ctrl+滚轮/滑块调粗细";
    }
}
