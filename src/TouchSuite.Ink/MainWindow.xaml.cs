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

    /// <summary>
    /// 同一次物理点击的去抖窗口：触摸会同时产生 Stylus 与 Touch 两条通道，
    /// 还可能被 WPF 再提升成一份鼠标事件 —— 三者都算"一次点击"。
    /// </summary>
    private static readonly long TapDedupeTicks = Stopwatch.Frequency / 4;   // 250ms

    private long _lastToolbarTapStamp;
    private object? _lastToolbarTapTarget;
    private long _menuOpenedStamp;

    // 正在拖动的滑块（触摸/触笔没有 WPF 的 Thumb 拖拽，只能自己跟手）
    private Slider? _sliderDragTarget;
    private int _sliderDragChannel;   // 0=无 1=触笔 2=触摸
    private int _sliderDragId;

    private int _strokeSeq;

    // ---- 输入源（工具栏下拉框） ----
    private const int WM_INPUT = 0x00FF;

    /// <summary>WM_POINTER 的 pointerId 加这个偏移当笔画键，避免与 RawInput 的 StableId 撞键。</summary>
    private const uint PointerIdBase = 100000;

    private InkInputMode _mode = InkInputMode.Wpf;

    /// <summary>WPF 的 Stylus/Touch 通道是否放行。非 WPF 模式下这两条通道必须门掉，否则会被提升重复落笔。</summary>
    private bool _wpfChannelActive = true;

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

    /// <summary>
    /// 本次要用的采样时间戳（QPC）。0 = 用当前时刻。
    /// <para>一条消息里带回多个历史子帧时，它们必须是<b>不同</b>的时间戳；否则
    /// <see cref="StrokeModeler.Update"/> 会因 dt&lt;=0 直接跳过建模，那些点退化成未平滑的原始点，
    /// 笔画出现折角、速度估计失真，快写/转圈时就会"一顿一甩"。WM_POINTER 用系统给的
    /// <c>POINTER_INFO.PerformanceCount</c>，RawInput 按子帧数把消息间隔回推分摊。</para>
    /// </summary>
    private long _pendingSampleTicks;

    /// <summary>上一批 RawInput 消息到达的时刻（用来把一次批量里的多个子帧还原成时间正序）。</summary>
    private long _lastRawBatchTicks;

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
        InkSettings.Load();
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
        if (!InkSettings.DComp)
        {
            Log.Write("设置：DComp 已关闭 → 不创建墨迹层，直接用 WPF 层");
        }
        else if (_inkWindow.Handle != IntPtr.Zero)
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
            string reason = dcomp?.LastError
                ?? (InkSettings.DComp ? "底层窗口创建失败" : "设置里关掉了 DComp");
            dcomp?.Dispose();
            _inkWindow.Dispose();
            _inkWindow = null;
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

        // 一批 WM_INPUT 里可能挤着多个子帧（等于多份历史采样）。它们的真实采样时刻是均匀分布的，
        // 只是"到得晚"。按子帧数把本批的消息间隔均摊回去，让模型看到的时间轴保持真实
        // —— 否则同一批所有子帧共用"处理时刻"，dt≈0 的点会被模型直接跳过（退化成未平滑点）。
        long batchNow = Stopwatch.GetTimestamp();
        long span = _lastRawBatchTicks == 0 ? 0 : batchNow - _lastRawBatchTicks;
        _lastRawBatchTicks = batchNow;
        if (span < 0 || span > Stopwatch.Frequency / 4)
            span = 0;                                  // 停很久/时钟异常：不猜，交给模型自己判断
        int subCount = frames.Count;

        for (int f = 0; f < frames.Count; f++)
        {
            RawHidTouch.RawFrame frame = frames[f];
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

            // 最后一个子帧就是"现在"，更早的按均摊间隔往前推
            _pendingSampleTicks = subCount <= 1
                ? batchNow
                : batchNow - (subCount - 1 - f) * span / subCount;

            _tracker!.Sync(ins, frame.CountZero, _events);
            FeedEvents();
        }
        _pendingSampleTicks = 0;

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
        return new ContactTracker.TrackIn(c.Link, c.HasCid, c.ContactId, c.Down, px, py, w, h,
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

        // 同一帧的所有触点是同一时刻采样的：统一用系统给的真实采样时刻（POINTER_INFO.PerformanceCount，
        // 与 Stopwatch 同源）。这样即使这条消息在 UI 线程排了 20ms，模型看到的间隔仍然是真的。
        long saved = _pendingSampleTicks;
        for (int i = 0; i < frame.Count; i++)
        {
            if (frame[i].PerformanceCount == 0)
                continue;
            _pendingSampleTicks = (long)frame[i].PerformanceCount;
            break;
        }

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

        _pendingSampleTicks = saved;
    }

    /// <summary>工具栏上的「输入源」按钮：展开/收起本窗口内的下拉面板（贴着工具栏底边）。</summary>
    private void OnInputSourceToggle(object sender, RoutedEventArgs e)
    {
        if (InputSourceMenu.Visibility == Visibility.Visible)
        {
            // 同一次触摸可能把 Click 送到这里两次（Stylus / Touch 两条通道，或手动派发 + 提升出来的鼠标）。
            // 刚打开就又被要求关闭 = 重复派发，忽略；不然就是"闪一下就没了"。
            if (Stopwatch.GetTimestamp() - _menuOpenedStamp < TapDedupeTicks)
                return;
            CloseInputSourceMenu();
            return;
        }
        GeneralTransform transform = ToolbarBorder.TransformToAncestor(this);
        Rect bounds = transform.TransformBounds(new Rect(ToolbarBorder.RenderSize));
        InputSourceMenu.Margin = new Thickness(0, bounds.Bottom + 6, 0, 0);
        InputSourceMenu.Visibility = Visibility.Visible;
        InputSourceButton.IsChecked = true;
        _menuOpenedStamp = Stopwatch.GetTimestamp();
    }

    /// <summary>下拉面板里选一项：收起面板再切源。</summary>
    private void OnInputSourcePicked(object sender, RoutedEventArgs e)
    {
        CloseInputSourceMenu();
        if (sender is not FrameworkElement item || item.Tag is not string tag || !int.TryParse(tag, out int index))
            return;
        var requested = (InkInputMode)Math.Clamp(index, 0, 3);
        if (requested == _mode)
            return;
        Log.Write($"[输入源] 选定 {InkInputModes.Label(requested)}");
        SwitchInputSource(requested);
    }

    /// <summary>收起下拉面板；返回"刚才是否展开着"。</summary>
    private bool CloseInputSourceMenu()
    {
        if (InputSourceMenu.Visibility != Visibility.Visible)
            return false;
        InputSourceMenu.Visibility = Visibility.Collapsed;
        InputSourceButton.IsChecked = false;
        return true;
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

        InputSourceButton.Content = InkInputModes.Label(mode);

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
    /// 批注模式：本窗口正常接收输入（落笔区 InputSurface 显示，整屏 alpha=1 可命中）；
    /// 鼠标模式：收起落笔区，窗口其余像素 alpha=0 —— 分层窗口按 alpha 做命中测试，点击就穿到桌面上。
    ///
    /// <para>不用 WS_EX_TRANSPARENT：它作用于整个窗口，工具栏会被一起变成穿透的（点不回来）；
    /// 而且底下的 DComp 墨迹窗口不是 layered 窗口，HTTRANSPARENT 只在同线程窗口间传递，
    /// 挡不住它吃点击 —— 所以鼠标模式必须把墨迹窗口也藏起来。</para>
    /// </summary>
    private void SetAnnotating(bool on)
    {
        _annotating = on;
        ModeMouse.IsChecked = !on;
        ModeAnnotate.IsChecked = on;
        CloseInputSourceMenu();

        InputSurface.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _inkWindow?.SetVisible(on);

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            // 两个窗口都是 topmost，靠 SetWindowPos 明确把 WPF 这层压到墨迹层上面
            Win32.SetWindowPos(hwnd, new IntPtr(HWND_TOPMOST), 0, 0, 0, 0,
                SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
        }

        Log.Write($"[模式] 批注={on} 落笔区={(on ? "显示" : "收起")} 墨迹窗口={(on ? "显示" : "隐藏")}");
        UpdateStatus();
    }

    // ================= 落笔（鼠标 / 触笔） =================

    /// <summary>
    /// 触摸/触笔刚被我们直接派发过点击时，WPF 还会把同一次接触"提升"成一份鼠标事件；
    /// 若它再落到按钮上就是点两下（下拉框闪一下就消失）。这里把提升出来的那一份吞掉。
    /// 真鼠标的 <c>StylusDevice</c> 为 null，不受影响。
    /// </summary>
    private void OnPreviewPromotedMouse(object sender, MouseButtonEventArgs e)
    {
        if (e.StylusDevice is not null &&
            Stopwatch.GetTimestamp() - _lastToolbarTapStamp < TapDedupeTicks)
            e.Handled = true;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        bool onToolbar = IsOnToolbar(e.OriginalSource);
        Log.Write($"[鼠标] 按下 pos=({e.GetPosition(this).X:0.#},{e.GetPosition(this).Y:0.#}) 按钮={e.ChangedButton} "
                + $"在工具栏={onToolbar} 触笔在用={_stylusActiveCount > 0} 层={_layer?.GetType().Name ?? "无"}");
        if (!_annotating)
            return;
        // 工具栏是 WPF 自己的 UI：不管是真鼠标还是触摸/触笔提升出来的鼠标，都必须放行给按钮，
        // 也不能在它下面落笔。这个判断必须在下面的门控之前 —— 否则非 WPF 输入源模式下
        // 触摸点不动工具栏。
        if (onToolbar)
            return;
        if (CloseInputSourceMenu())
            return;   // 这一下只是关下拉面板，不落笔
        // 非 WPF 模式下，触摸/触笔会被提升成鼠标事件：必须丢掉，否则同一根手指画两遍。
        if (!_wpfChannelActive &&
            (e.StylusDevice is not null || _rawActiveCount > 0 || _pointerActiveCount > 0))
        {
            _gateDroppedMouse++;
            return;
        }
        if (_stylusActiveCount > 0)
            return;
        PointerDown(MousePointerId, PointToScreen(e.GetPosition(this)), 1f);
        CaptureMouse();
        e.Handled = true;
    }

    /// <summary>工具栏（含输入源下拉面板）上的点击不要当成落笔（事件会从子元素冒泡到 Window）。</summary>
    private bool IsOnToolbar(object? source) =>
        source is DependencyObject d && (ToolbarBorder.IsAncestorOf(d) || InputSourceMenu.IsAncestorOf(d));

    /// <summary>
    /// 触摸/触笔落在工具栏上时，直接把这一次点击派发给命中的控件。
    ///
    /// <para>为什么不交给 WPF 自己处理：工具栏上的 Click 是鼠标语义，触摸/触笔要先被 WPF
    /// 提升成鼠标事件；而本窗口为了落笔把触摸/触笔事件全都 Handle 掉了，画过一笔之后
    /// 第一次点击经常被吞掉（实测要点两下）。这里自己派发，一次就生效。</para>
    /// </summary>
    private bool TryTapToolbar(object? source)
    {
        if (source is not DependencyObject node)
            return false;

        if (FindAncestor<ButtonBase>(node) is not ButtonBase button)
            return false;

        // 同一次接触可能同时走 Stylus 与 Touch 两条通道（WPF 对触摸会两条都发），
        // 那样一个 Tap 会被派发两次（下拉框闪一下就没了、撤销一次退两步）。
        // 同一个控件在去抖窗口内只认第一次；换了控件则不受影响，快速连点不会被吞。
        long now = Stopwatch.GetTimestamp();
        bool repeated = ReferenceEquals(button, _lastToolbarTapTarget) && now - _lastToolbarTapStamp < TapDedupeTicks;
        _lastToolbarTapTarget = button;
        _lastToolbarTapStamp = now;
        if (repeated)
            return true;

        if (button is ToggleButton toggle)
            toggle.IsChecked = !(toggle.IsChecked == true);   // RaiseEvent 不会走 OnClick，得自己翻
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
        return true;
    }

    // ---- 工具栏滑块：触摸/触笔自己跟手拖 ----

    /// <summary>
    /// 触摸/触笔按下滑块时开始拖动。WPF 的 Slider 靠 Thumb + 鼠标捕获工作，而工具栏上的
    /// 触摸/触笔事件被本层吞掉了，它收不到 Move，所以只能自己按位置算值并跟着手指更新。
    /// </summary>
    private bool TryBeginSliderDrag(object? source, int channel, int pointerId, Func<IInputElement, Point> positionOf)
    {
        if (source is not DependencyObject node || FindAncestor<Slider>(node) is not Slider slider)
            return false;

        _sliderDragTarget = slider;
        _sliderDragChannel = channel;
        _sliderDragId = pointerId;
        SetSliderFromPoint(slider, positionOf(slider));
        _lastToolbarTapStamp = Stopwatch.GetTimestamp();   // 压住提升出来的鼠标事件，别让 Thumb 同时再拖一遍
        return true;
    }

    /// <summary>拖动中：跟着这个触点更新滑块。返回 true 表示这次 Move 属于滑块。</summary>
    private bool ContinueSliderDrag(int channel, int pointerId, Func<IInputElement, Point> positionOf)
    {
        if (_sliderDragTarget is null || _sliderDragChannel != channel || _sliderDragId != pointerId)
            return false;
        SetSliderFromPoint(_sliderDragTarget, positionOf(_sliderDragTarget));
        _lastToolbarTapStamp = Stopwatch.GetTimestamp();   // 拖动期间继续压住提升出来的鼠标事件
        return true;
    }

    private bool EndSliderDrag(int channel, int pointerId)
    {
        if (_sliderDragTarget is null || _sliderDragChannel != channel || _sliderDragId != pointerId)
            return false;
        _sliderDragTarget = null;
        _sliderDragChannel = 0;
        return true;
    }

    private static void SetSliderFromPoint(Slider slider, Point p)
    {
        if (slider.ActualWidth <= 0)
            return;
        double ratio = Math.Clamp(p.X / slider.ActualWidth, 0, 1);
        slider.Value = slider.Minimum + ratio * (slider.Maximum - slider.Minimum);
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match)
                return match;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

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
        // 鼠标模式：别接、也别 handle —— 留在 WPF 手里，触笔才能点工具栏（提升成鼠标点击）。
        if (!_annotating)
            return;

        // 落在工具栏上：自己派发点击（见 TryTapToolbar），既不落笔，也不靠 WPF 的提升。
        if (IsOnToolbar(e.OriginalSource))
        {
            e.Handled = true;
            if (TryBeginSliderDrag(e.OriginalSource, 1, e.StylusDevice.Id, element => e.GetPosition(element)))
                return;
            TryTapToolbar(e.OriginalSource);
            return;
        }
        if (CloseInputSourceMenu())
        {
            e.Handled = true;
            return;
        }

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
        int pointerId = e.StylusDevice.Id;

        if (ContinueSliderDrag(1, pointerId, element => e.GetPosition(element)))
        {
            e.Handled = true;
            return;
        }

        // 不是墨迹层起的笔画（例如落在工具栏上、交给 WPF 提升成鼠标的那一笔）→ 一律不接管。
        if (!_strokeOfPointer.ContainsKey(pointerId))
            return;

        if (!_wpfChannelActive)
        {
            _gateDroppedStylus++;
            e.Handled = true;
            return;
        }

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
        int pointerId = e.StylusDevice.Id;

        if (EndSliderDrag(1, pointerId))
        {
            e.Handled = true;
            return;
        }

        if (!_strokeOfPointer.ContainsKey(pointerId))
            return;   // 没在本层起笔（例如点在工具栏）→ 留给 WPF 提升成鼠标点击

        if (!_wpfChannelActive)
        {
            _gateDroppedStylus++;
            e.Handled = true;
            return;
        }

        PointerUp(pointerId);
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
        // 鼠标模式：别接、也别 handle —— 留在 WPF 手里，手指才能点工具栏（提升成鼠标点击）。
        if (!_annotating)
            return;

        // 落在工具栏上：自己派发点击（见 TryTapToolbar），既不落笔，也不靠 WPF 的提升。
        if (IsOnToolbar(e.OriginalSource))
        {
            e.Handled = true;
            if (TryBeginSliderDrag(e.OriginalSource, 2, e.TouchDevice.Id, element => e.GetTouchPoint(element).Position))
                return;
            TryTapToolbar(e.OriginalSource);
            return;
        }
        if (CloseInputSourceMenu())
        {
            e.Handled = true;
            return;
        }

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
        if (ContinueSliderDrag(2, e.TouchDevice.Id, element => e.GetTouchPoint(element).Position))
        {
            e.Handled = true;
            return;
        }

        // 不是墨迹层起的触点（例如落在工具栏上、交给 WPF 提升成鼠标的那根手指）→ 一律不接管。
        if (!_strokeOfPointer.ContainsKey(e.TouchDevice.Id))
            return;

        if (!_annotating)
            return;
        if (!_wpfChannelActive)
        {
            _gateDroppedStylus++;
            e.Handled = true;
            return;
        }

        if (!TouchUsable)
            return;
        PointerMove(e.TouchDevice.Id, PointToScreen(e.GetTouchPoint(this).Position), 1f);
        e.Handled = true;
    }

    private void OnTouchUp(object sender, TouchEventArgs e)
    {
        if (EndSliderDrag(2, e.TouchDevice.Id))
        {
            e.Handled = true;
            return;
        }

        if (!_strokeOfPointer.ContainsKey(e.TouchDevice.Id))
            return;
        if (!_annotating)
            return;
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
        // 优先用调用方给的**真实采样时刻**（WM_POINTER 的 PerformanceCount / RawInput 子帧回推），
        // 都没有才退化成"处理时刻"。QPC 时间戳：TickCount 只有 15.6ms 粒度，估不准速度。
        long ticks = _pendingSampleTicks != 0 ? _pendingSampleTicks : Stopwatch.GetTimestamp();
        return new InkSample((float)screenPx.X, (float)screenPx.Y, w, _color.R, _color.G, _color.B, _color.A, ticks);
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

    private void OnSettings(object sender, RoutedEventArgs e) => new SettingsWindow { Owner = this }.ShowDialog();

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
