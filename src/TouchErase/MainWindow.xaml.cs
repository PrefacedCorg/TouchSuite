using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using TouchErase.Helpers;

namespace TouchErase;

public partial class MainWindow : Window
{
    private ScreenCalibration? _calib;
    private List<(double widthMm, double heightMm)> _edidCandidates = new();
    private (double widthMm, double heightMm) _edidPicked;
    private EdidSizeMode _edidSizeMode = EdidSizeMode.TrustWidth;
    private double _dpiScaleX = 1;
    private double _dpiScaleY = 1;
    private double _mmPerDiuX;
    private double _mmPerDiuY;

    private readonly List<Point> _palmPoints = new();
    private readonly List<List<Point>> _palmStrokes = new();
    private List<Point>? _hull;
    private List<Point>? _simplified;
    private readonly List<List<Point>> _simplifiedStrokes = new();
    private bool _hasArea;
    private double _traceAreaDiu2;

    // 实时接触 -> 擦除区
    private bool _shapeIsCircle;                 // 默认 false = 矩形
    private double _eraserRatio = 1.0;           // 手动倍率（滑块）
    private bool _autoRatio = true;              // 自动倍率：k = √(①手掌面积a / ②按压峰值b)，不用滑块、不设上限
    private bool _followPressure = true;         // true=擦除区随接触面积等比变化；false=固定为①的手掌面积
    private readonly List<Rect> _contactHistory = new();
    private Rect? _lastContact;
    private double _peakContactMmW;              // 本次按压峰值接触的物理尺寸（mm），与 DPI 无关
    private double _peakContactMmH;
    private long _lastContactTicks;              // 上次有效接触时刻，用于判断“是否是一次新的按压”
    private double _lastContactMmW;              // 上一次接触的物理尺寸（mm），DPI 变化时用它重建 DIU
    private double _lastContactMmH;
    private const int ContactHistoryMax = 5;
    private const double MinContactMm = 1.0;   // 小于此物理尺寸视为"驱动未上报有效接触面积"（占位值≈0.03mm，真实指腹≥5mm）
    private const int MinHandPeakCount = 5;    // 手掌接触的最小计数；低于此值视为单指轻按，拒绝用于标定
    private const int MaxSimCount = 2000;      // 自测注入的计数上限
    private const double MaxInjectMm = 500;    // 自测注入的物理尺寸上限（避免生成超大 Visual）
    private bool _driverSizeWarned;

    // 日志
    private bool _logFrames = true;
    private bool _uiReady;
    private long _lastHidLogTicks;
    private long _lastFilteredLogTicks;
    private readonly DispatcherTimer _logFlushTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _sourceRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    // WM_POINTER 通路（绕开 WPF 的 WM_TOUCH 直读接触矩形）
    private IntPtr _hwnd = IntPtr.Zero;
    private bool _pointerAreaLogged;
    private bool _pointerNoAreaLogged;
    private long _lastPointerLogTicks;
    private long _lastRawHidLogTicks;

    // 接触来源与优先级仲裁（原始HID > WM_POINTER > WPF）
    private enum ContactSource { None, Wpf, Pointer, RawHid }

    private const long SourceFreshMs = 300;
    private readonly Dictionary<ContactSource, (long Time, Rect Rect, bool Eligible)> _sourceState = new();
    private readonly Dictionary<ContactSource, string> _sourceDetail = new();
    private ContactSource _activeSource = ContactSource.None;

    // 来源选择：自适应（自动锁定）或手动指定某一路
    private enum SourceMode { Auto, RawHid, Pointer, Wpf }
    private SourceMode _sourceMode = SourceMode.Auto;
    private ContactSource _lockedSource = ContactSource.None;
    private readonly Dictionary<ContactSource, long> _sourceValidSince = new(); // 各来源“连续有效”的起始时刻
    private const long SourceLockObserveMs = 300;   // 自适应：某来源连续有效达到此时长才锁定
    private const long SourceLockReleaseMs = 1500;  // 自适应：锁定来源静默超过此时长则解锁、重新识别

    // HID 计数标定（设备只给逻辑计数、不给物理单位时用）
    private double _palmAreaCm2;
    private double _palmAspect;      // 手掌长宽比 w/h（来自"手输长宽"或①页轮廓外接矩形）；<=0 = 未设定
    private string _palmSource = "";  // "手输" / "描摹"，仅用于显示
    private int _handPeakCount;
    private bool _hidScaleHintShown;

    // ---- 引导模式 ----
    private sealed record GuideStep(string Title, string Body,
        Func<FrameworkElement?>? Target = null, Action? OnEnter = null);

    private List<GuideStep> _guideSteps = new();
    private int _guideIndex;

    public MainWindow()
    {
        InitializeComponent();

        RulerCanvas.SizeChanged += (_, _) => DrawRuler();
        RulerCanvasV.SizeChanged += (_, _) => DrawRulerV();

        TraceCanvas.DefaultDrawingAttributes = new System.Windows.Ink.DrawingAttributes
        {
            Width = 3,
            Height = 3,
            Color = Color.FromRgb(0x22, 0x66, 0xCC),
            FitToCurve = true,
        };

        SizeChanged += (_, _) => RefreshGuideLayout();

        // 全局触摸帧：不抬手也能刷新（尺寸变化/多指时靠它兜底）
        Touch.FrameReported += OnTouchFrameReported;

        // 日志：定时落盘（写文件带缓冲，避免逐帧 I/O 拖慢 UI）
        _logFlushTimer.Tick += (_, _) => Log.Flush();
        _logFlushTimer.Start();
        Closing += (_, _) => Log.Flush();

        // 定时刷新来源状态：来源全部过期后把"生效来源"归零，避免界面一直显示某个来源在用
        _sourceRefreshTimer.Tick += (_, _) => RefreshSourceState();
        _sourceRefreshTimer.Start();

        _uiReady = true;
    }

    // ================= WM_POINTER 直读接触矩形 =================

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource? src = PresentationSource.FromVisual(this) as HwndSource;
        src?.AddHook(WndProc);
        bool raw = RawHidScan.Register(_hwnd);
        Log.Info($"窗口句柄 0x{_hwnd.ToInt64():X}，已挂 WM_POINTER 钩子；RawInput 触摸注册={raw}");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_INPUT:
                if (StageTabs.SelectedIndex is 1 or 2) // ② 手掌接触面积 / ③ 面积擦预览 都接收原始HID
                {
                    RawHidScan.WmInputResult r = RawHidScan.TryHandleWmInput(lParam, out RawHidScan.RawTouchSample? sample);

                    // 句柄不在能力表（启动时设备没枚举到 / 中途重枚举）→ 自动重扫一次再解（自愈）
                    if (r == RawHidScan.WmInputResult.UnknownDevice && RawHidScan.TryBeginAutoRescan())
                    {
                        List<RawHidScan.HidTouchInfo> rescan = RawHidScan.Scan();
                        int touchCount = rescan.Count(h => h.IsTouchScreen);
                        Log.Info($"原始HID 遇到未知设备句柄 → 自动重扫：{rescan.Count} 个 HID 设备（触摸屏 {touchCount} 个）");
                        RawHidScan.TryHandleWmInput(lParam, out sample);
                    }

                    if (sample is not null)
                        HandleRawHidSample(sample);
                }
                break;

            case PointerTouch.WM_POINTERDOWN:
            case PointerTouch.WM_POINTERENTER:
            case PointerTouch.WM_POINTERUPDATE:
                if (StageTabs.SelectedIndex is 1 or 2)
                {
                    uint id = PointerTouch.GetPointerId(wParam);
                    if (id != 0 && PointerTouch.TryRead(id, out PointerTouch.POINTER_TOUCH_INFO ti, out bool hasArea))
                        HandlePointerContact(ti, hasArea);
                }
                break;
        }
        return IntPtr.Zero;
    }

    /// <summary>当前页接收触摸的宿主元素：② 手掌接触面积 → ContactHost，其余 → EraserHost。</summary>
    private FrameworkElement TouchHostElement()
        => StageTabs.SelectedIndex == 1 ? ContactHost : EraserHost;

    private const int WM_INPUT = 0x00FF;

    /// <summary>处理原始 HID 触摸样本：优先用它的接触尺寸驱动预览。</summary>
    private void HandleRawHidSample(RawHidScan.RawTouchSample s)
    {
        long now = Environment.TickCount64;
        if (now - _lastRawHidLogTicks >= 100)
        {
            _lastRawHidLogTicks = now;
            Log.Info($"原始HID: W={s.WidthLogical}({s.WidthMm?.ToString("0.0") ?? "-"}mm) H={s.HeightLogical}({s.HeightMm?.ToString("0.0") ?? "-"}mm) Xn={s.XNorm?.ToString("0.000") ?? "-"} Yn={s.YNorm?.ToString("0.000") ?? "-"} raw=[{s.Hex}]");
        }

        // 记录"整只手按下"时的峰值计数，供「计数→mm」标定用（即使当前还没标定也要记）
        int peak = Math.Max(s.WidthLogical, s.HeightLogical);
        if (peak > _handPeakCount)
        {
            _handPeakCount = peak;
            UpdateHidScaleLabel();
        }

        if (s.WidthMm is null || s.HeightMm is null)
        {
            // 设备只给逻辑计数、又还没标定 → 明确引导用户去标定，而不是"没反应"
            if (!_hidScaleHintShown)
            {
                _hidScaleHintShown = true;
                Log.Warn($"原始HID 无物理单位且未标定（W={s.WidthLogical} H={s.HeightLogical}）：需先用第①页手掌面积标定「计数→mm」");
                if (EraserInfoText is not null)
                    EraserInfoText.Text =
                        "该设备只上报逻辑计数（无毫米单位），当前未标定，因此还算不出擦除区。\n" +
                        "请依次：① 在「① 描摹手掌轮廓」页描一圈手掌并计算面积 → " +
                        "② 回到本页用整只手按一下 → ③ 点「标定 HID 计数→mm」。";
            }
            return;
        }

        if (_mmPerDiuX <= 0 || _mmPerDiuY <= 0)
            return;

        double wDiu = s.WidthMm.Value / _mmPerDiuX;
        double hDiu = s.HeightMm.Value / _mmPerDiuY;

        FrameworkElement host = TouchHostElement();
        Point hostOffset = host.TranslatePoint(new Point(0, 0), this);
        double lx, ly;
        if (s.XNorm is double xn && s.YNorm is double yn)
        {
            // 数字化器归一化坐标 → 虚拟桌面 DIP（多显示器时比 PrimaryScreen 更贴近真实映射）。
            // 注意：位置换算用的是本窗口所在屏的 dpiScale，混合 DPI 场景下会有误差。
            Point winOrigin = PointToScreen(new Point(0, 0));
            double screenDipX = SystemParameters.VirtualScreenLeft + xn * SystemParameters.VirtualScreenWidth;
            double screenDipY = SystemParameters.VirtualScreenTop + yn * SystemParameters.VirtualScreenHeight;
            lx = screenDipX - winOrigin.X / _dpiScaleX - hostOffset.X;
            ly = screenDipY - winOrigin.Y / _dpiScaleY - hostOffset.Y;
        }
        else
        {
            lx = host.ActualWidth / 2;
            ly = host.ActualHeight / 2;
        }

        // 夹取到预览区内，避免坐标异常时把擦除区画到看不见的地方
        if (host.ActualWidth > 0)
            lx = Math.Clamp(lx, 0, host.ActualWidth);
        if (host.ActualHeight > 0)
            ly = Math.Clamp(ly, 0, host.ActualHeight);

        string detail = $"W={s.WidthLogical} H={s.HeightLogical} → {s.WidthMm?.ToString("0.0") ?? "-"}×{s.HeightMm?.ToString("0.0") ?? "-"} mm";
        SubmitContact(ContactSource.RawHid, new Rect(lx - wDiu / 2, ly - hDiu / 2, wDiu, hDiu), applyThreshold: false, detail);
    }

    private void HandlePointerContact(PointerTouch.POINTER_TOUCH_INFO ti, bool hasArea)
    {
        if (!hasArea)
        {
            if (!_pointerNoAreaLogged)
            {
                _pointerNoAreaLogged = true;
                Log.Warn($"WM_POINTER: 触摸无接触矩形（touchMask=0x{ti.touchMask:X}, rcContact=({ti.rcContact.Left},{ti.rcContact.Top},{ti.rcContact.Right},{ti.rcContact.Bottom})）→ 该设备未上报接触尺寸");
                if (EraserInfoText is not null)
                    EraserInfoText.Text = "WM_POINTER 也没有接触矩形：该触摸设备未上报接触尺寸。\n（连 Windows 原生指针 API 都拿不到，软件层无法获取面积）";
            }
            return;
        }

        if (!_pointerAreaLogged)
        {
            _pointerAreaLogged = true;
            Log.Info($"WM_POINTER: 有接触矩形 rcContact={ti.rcContact.Width}x{ti.rcContact.Height} 物理像素 pressure={ti.pressure} touchFlags=0x{ti.touchFlags:X} → 作为接触尺寸候选来源（是否生效由优先级仲裁决定）");
        }

        long now = Environment.TickCount64;
        if (now - _lastPointerLogTicks >= 100)
        {
            _lastPointerLogTicks = now;
            Log.Info($"WM_POINTER rcContact = {ti.rcContact.Width}x{ti.rcContact.Height} px  pressure={ti.pressure}");
        }

        if (_dpiScaleX <= 0 || _dpiScaleY <= 0 || _hwnd == IntPtr.Zero)
            return;

        double wDiu = ti.rcContact.Width / _dpiScaleX;
        double hDiu = ti.rcContact.Height / _dpiScaleY;

        var pt = new PointerTouch.POINT { X = ti.pointerInfo.ptPixelLocation.X, Y = ti.pointerInfo.ptPixelLocation.Y };
        if (!PointerTouch.ScreenToClient(_hwnd, ref pt))
            return;

        // 客户区物理像素 → DIP，再换算到当前宿主元素局部坐标
        double cxDiu = pt.X / _dpiScaleX;
        double cyDiu = pt.Y / _dpiScaleY;
        FrameworkElement host = TouchHostElement();
        Point hostOffset = host.TranslatePoint(new Point(0, 0), this);

        var rect = new Rect(cxDiu - hostOffset.X - wDiu / 2, cyDiu - hostOffset.Y - hDiu / 2, wDiu, hDiu);
        string detail = $"{ti.rcContact.Width}×{ti.rcContact.Height} px → {wDiu * _mmPerDiuX:0.0}×{hDiu * _mmPerDiuY:0.0} mm";
        SubmitContact(ContactSource.Pointer, rect, applyThreshold: true, detail);
    }

    // ================= 生命周期 =================

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        UpdateDpiFromVisual();
        ReloadEdid();
        UpdateLogUi();
        RunHidDiagnostics(full: false); // 启动只做轻量自检；完整诊断留给「HID 触摸诊断」按钮
        UpdateSourceLabel();
        UpdateHidScaleLabel();
        UpdateRatioLabel();
        UpdatePalmSizeText();

        // 启动即进入引导模式（等布局完成后再定位高亮框）。
        Dispatcher.BeginInvoke(new Action(StartGuide), DispatcherPriority.Loaded);
    }

    private void OnDpiChanged(object sender, DpiChangedEventArgs e)
    {
        UpdateDpiFromVisual();
        RefreshDerivedMm();
        UpdateCalibText();
        DrawRuler();
        DrawRulerV();
        if (_hasArea) UpdateAreaOutput();
        RescaleLiveEraserToDpi();
        SetStatus($"DPI 变化：{e.OldDpi.PixelsPerInchX:0} → {e.NewDpi.PixelsPerInchX:0} DPI，已按当前屏重算 mm/DIU。");
        Log.Info($"DPI 变化: {e.OldDpi.PixelsPerInchX:0} -> {e.NewDpi.PixelsPerInchX:0} DPI, mm/DIU={_mmPerDiuX:0.0000}x{_mmPerDiuY:0.0000}");
    }

    private void UpdateDpiFromVisual()
    {
        PresentationSource? src = PresentationSource.FromVisual(this);
        Matrix m = src?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        _dpiScaleX = m.M11 > 0 ? m.M11 : 1;
        _dpiScaleY = m.M22 > 0 ? m.M22 : 1;
    }

    private void RefreshDerivedMm()
    {
        if (_calib is null)
        {
            _mmPerDiuX = 0;
            _mmPerDiuY = 0;
            return;
        }
        // 1 DIU = dpiScale 物理像素；物理像素 -> mm 用 EDID 得来的 mm/px。
        _mmPerDiuX = _calib.MmPerPxX * _dpiScaleX;
        _mmPerDiuY = _calib.MmPerPxY * _dpiScaleY;
    }

    // ================= 校准 =================

    private void ReloadEdid()
    {
        _edidCandidates = EdidReader.GetPhysicalSizes();
        var (resX, resY) = ScreenCalibration.GetPrimaryScreenPixels();
        if (resX <= 0) resX = 1920;
        if (resY <= 0) resY = 1080;

        if (_edidCandidates.Count > 0)
        {
            // 多显示器时选"与主屏分辨率比例最接近"的那块 EDID，而不是盲目取第一个。
            double wantAspect = (double)resX / resY;
            _edidPicked = _edidCandidates
                .OrderBy(s => Math.Abs(s.widthMm / s.heightMm - wantAspect))
                .First();

            string candidates = string.Join(", ", _edidCandidates.Select(s =>
                $"{s.widthMm:0}x{s.heightMm:0}mm({s.widthMm / s.heightMm:0.00})"));
            Log.Info($"EDID 候选 {_edidCandidates.Count} 个: {candidates}；主屏分辨率 {resX}x{resY}(比例 {wantAspect:0.00}) → 选用 {_edidPicked.widthMm:0}x{_edidPicked.heightMm:0}mm");

            ApplyEdidCalibration();
            return;
        }

        _calib = null;
        SetStatus("EDID 读取失败（投影仪/电视常见），请用右侧手动填对角线英寸。");
        RefreshDerivedMm();
        UpdateCalibText();
        DrawRuler();
        DrawRulerV();
        Log.Warn("校准: EDID 读取失败，等待手动输入对角线英寸");
    }

    /// <summary>按当前"尺寸推算方式"用已选中的 EDID 重建校准（切换模式时复用）。</summary>
    private void ApplyEdidCalibration()
    {
        _calib = ScreenCalibration.FromEdid(_edidPicked.widthMm, _edidPicked.heightMm, _edidSizeMode);
        SetStatus($"EDID 读取成功：物理 {_calib.ScreenWidthMm:0} x {_calib.ScreenHeightMm:0} mm。");

        RefreshDerivedMm();
        UpdateCalibText();
        DrawRuler();
        DrawRulerV();
        if (_hasArea) UpdateAreaOutput();
        RescaleLiveEraserToDpi();           // 擦除区按新的 mm/DIU 重建

        string modeLabel = _edidSizeMode switch
        {
            EdidSizeMode.UseEdid => "横竖都按EDID",
            EdidSizeMode.TrustHeight => "按竖推宽",
            _ => "按宽推竖",
        };
        Log.Info($"校准(EDID/{modeLabel}): 物理={_calib.ScreenWidthMm:0.0}x{_calib.ScreenHeightMm:0.0}mm 分辨率={_calib.ResX}x{_calib.ResY} mm/px={_calib.MmPerPxX:0.0000}x{_calib.MmPerPxY:0.0000} 来源={_calib.Source}");
    }

    private void OnEdidModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || EdidModeCombo is null)
            return;

        _edidSizeMode = EdidModeCombo.SelectedIndex switch
        {
            0 => EdidSizeMode.UseEdid,
            2 => EdidSizeMode.TrustHeight,
            _ => EdidSizeMode.TrustWidth,
        };
        Log.Info($"EDID 尺寸推算方式切换为索引 {EdidModeCombo.SelectedIndex}（{_edidSizeMode}）");

        if (_edidCandidates.Count > 0)
        {
            ApplyEdidCalibration();
        }
        else
        {
            SetStatus("当前是手动对角线校准，「EDID 尺寸推算方式」暂不生效；想改回 EDID 请点「重新读取 EDID」。");
            Log.Info("切换 EDID 推算方式被忽略：当前为手动对角线校准");
        }
    }

    private void OnReloadEdid(object sender, RoutedEventArgs e) => ReloadEdid();

    private void OnApplyDiagonal(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(DiagInput.Text.Trim(), out double diag) || diag <= 1 || diag > 200)
        {
            SetStatus("对角线英寸无效，请输入 2~200 之间的数字。");
            return;
        }

        var (resX, resY) = ScreenCalibration.GetPrimaryScreenPixels();
        if (resX <= 0) resX = 1920;
        if (resY <= 0) resY = 1080;

        // 直接用分辨率当作宽高比，不再猜 16:9 / 16:10 / 4:3（否则 3:2 等会被算错）。
        _calib = ScreenCalibration.FromDiagonal(diag, resX, resY, resX, resY);
        _edidCandidates = new(); // 已改为手动，清空 EDID 候选，避免切换推算方式时把手动值悄悄覆盖
        RefreshDerivedMm();
        UpdateCalibText();
        DrawRuler();
        DrawRulerV();
        if (_hasArea) UpdateAreaOutput();
        RescaleLiveEraserToDpi();
        SetStatus($"已按对角线 {diag:0.##}\" 反推物理尺寸（按分辨率比例 {resX}:{resY}）。");
        Log.Info($"校准(手动): 对角线={diag:0.##}\" 按分辨率比例 {resX}:{resY} 物理={_calib.ScreenWidthMm:0.0}x{_calib.ScreenHeightMm:0.0}mm mm/DIU={_mmPerDiuX:0.0000}x{_mmPerDiuY:0.0000}");
    }

    private void UpdateCalibText()
    {
        if (_calib is null)
        {
            CalibInfoText.Text = "未校准：EDID 读取失败，请在右侧手动填对角线英寸。";
            return;
        }

        CalibInfoText.Text =
            $"来源: {_calib.Source}    物理: {_calib.ScreenWidthMm:0.0} x {_calib.ScreenHeightMm:0.0} mm    " +
            $"分辨率: {_calib.ResX} x {_calib.ResY}\n" +
            $"mm/px: {_calib.MmPerPxX:0.0000} x {_calib.MmPerPxY:0.0000}    " +
            $"DPI 缩放: {_dpiScaleX:0.###} x {_dpiScaleY:0.###}    " +
            $"mm/DIU: {_mmPerDiuX:0.0000} x {_mmPerDiuY:0.0000}";

        // 一致性检查：物理比例与分辨率比例不符 → 很可能选错了屏（多显示器/虚拟机），mm 结果会偏
        if (_calib.MmPerPxX > 0 && _calib.MmPerPxY > 0)
        {
            double pxRatio = _calib.MmPerPxX / _calib.MmPerPxY;
            if (Math.Abs(pxRatio - 1) > 0.03)
                CalibInfoText.Text += $"\n⚠ 物理比例({_calib.ScreenWidthMm / _calib.ScreenHeightMm:0.00})与分辨率比例({(double)_calib.ResX / _calib.ResY:0.00})不符，两轴 mm/px 相差 {Math.Abs(pxRatio - 1) * 100:0}%，物理尺寸不可信——建议改用右侧手动填对角线英寸。";
        }
    }

    // ================= 参考标尺 =================

    private void DrawRuler()
    {
        RulerCanvas.Children.Clear();
        double width = RulerCanvas.ActualWidth;
        if (width <= 0) return;

        double mmPerDiu = _mmPerDiuX > 0 ? _mmPerDiuX : 96.0 / (25.4 * _dpiScaleX);
        double cmDiu = 10.0 / mmPerDiu; // 1 cm 对应多少 DIU
        if (!IsUsable(cmDiu)) return;

        const double startX = 12;
        const double baseline = 42;
        double maxLen = Math.Max(0, width - startX - 6);

        var axis = new Line
        {
            X1 = startX, Y1 = baseline, X2 = startX + maxLen, Y2 = baseline,
            Stroke = Brushes.Black, StrokeThickness = 1.5,
        };
        RulerCanvas.Children.Add(axis);

        bool labelEveryCm = cmDiu >= 20;
        int maxCm = (int)Math.Floor(maxLen / cmDiu);
        for (int cm = 0; cm <= maxCm; cm++)
        {
            double x = startX + cm * cmDiu;
            var tick = new Line
            {
                X1 = x, Y1 = baseline, X2 = x, Y2 = baseline - (cm % 5 == 0 ? 14 : 8),
                Stroke = Brushes.Black, StrokeThickness = 1,
            };
            RulerCanvas.Children.Add(tick);

            if (labelEveryCm || cm % 5 == 0)
            {
                var label = new TextBlock { Text = $"{cm}", FontSize = 10, Foreground = Brushes.Black };
                RulerCanvas.Children.Add(label);
                Canvas.SetLeft(label, x - 5);
                Canvas.SetTop(label, 8);
            }
        }

        var cap = new TextBlock { Text = "cm", FontSize = 11, Foreground = Brushes.Black };
        RulerCanvas.Children.Add(cap);
        Canvas.SetLeft(cap, startX + maxLen - 16);
        Canvas.SetTop(cap, baseline + 4);
    }

    /// <summary>纵向参考标尺：从顶向下，按同一物理比例铺满可用高度。</summary>
    private void DrawRulerV()
    {
        RulerCanvasV.Children.Clear();
        double height = RulerCanvasV.ActualHeight;
        if (height <= 0) return;

        double mmPerDiu = _mmPerDiuY > 0 ? _mmPerDiuY : 96.0 / (25.4 * _dpiScaleY);
        double cmDiu = 10.0 / mmPerDiu;
        if (!IsUsable(cmDiu)) return;

        const double startY = 12;
        const double baseline = 22; // 竖尺的轴线 x
        double maxLen = Math.Max(0, height - startY - 6);

        var axis = new Line
        {
            X1 = baseline, Y1 = startY, X2 = baseline, Y2 = startY + maxLen,
            Stroke = Brushes.Black, StrokeThickness = 1.5,
        };
        RulerCanvasV.Children.Add(axis);

        bool labelEveryCm = cmDiu >= 20;
        int maxCm = (int)Math.Floor(maxLen / cmDiu);
        for (int cm = 0; cm <= maxCm; cm++)
        {
            double y = startY + cm * cmDiu;
            var tick = new Line
            {
                X1 = baseline, Y1 = y, X2 = baseline + (cm % 5 == 0 ? 14 : 8), Y2 = y,
                Stroke = Brushes.Black, StrokeThickness = 1,
            };
            RulerCanvasV.Children.Add(tick);

            if (labelEveryCm || cm % 5 == 0)
            {
                var label = new TextBlock { Text = $"{cm}", FontSize = 10, Foreground = Brushes.Black };
                RulerCanvasV.Children.Add(label);
                Canvas.SetLeft(label, baseline + 16);
                Canvas.SetTop(label, y - 7);
            }
        }

        var cap = new TextBlock { Text = "cm↓", FontSize = 11, Foreground = Brushes.Black };
        RulerCanvasV.Children.Add(cap);
        Canvas.SetLeft(cap, baseline + 2);
        Canvas.SetTop(cap, startY + maxLen + 2);
    }

    private static bool IsUsable(double v) => v > 0 && double.IsFinite(v);

    // ================= ② InkCanvas 描摹 =================

    // 注意：StylusDown/Move/Up 只在真正的笔/触控笔设备上触发，鼠标描摹不会触发，
    // 因此这里统一用 StrokeCollected —— 鼠标、手指、笔任一输入都会触发。
    private void OnStrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
    {
        // e.Stroke.StylusPoints 即原始密集点（等同 GetStylusPoints），已绕过坐标插值。
        var stroke = new List<Point>(e.Stroke.StylusPoints.Count);
        foreach (StylusPoint p in e.Stroke.StylusPoints)
        {
            var pt = new Point(p.X, p.Y);
            stroke.Add(pt);
            _palmPoints.Add(pt);
        }
        _palmStrokes.Add(stroke);

        SetStatus($"描摹采集中：已收集 {_palmPoints.Count} 点（{TraceCanvas.Strokes.Count} 笔）。");
        Log.Info($"描摹: 本条笔画 {stroke.Count} 点，累计 {_palmPoints.Count} 点 / {_palmStrokes.Count} 笔");
    }

    private void OnClearTrace(object sender, RoutedEventArgs e)
    {
        _palmPoints.Clear();
        _palmStrokes.Clear();
        _hull = null;
        _simplified = null;
        _simplifiedStrokes.Clear();
        _hasArea = false;
        _traceAreaDiu2 = 0;
        TraceCanvas.Strokes.Clear();
        TraceOverlay.Children.Clear();
        TraceInfoText.Text = "已清除。请重新描一圈手掌轮廓。";
        ResultText.Text = "";

        // 若当前手掌尺寸来自描摹，则一并清除（手输的不受影响）
        if (_palmSource == "描摹")
        {
            _palmAreaCm2 = 0;
            _palmAspect = 0;
            _palmSource = "";
        }
        UpdatePalmSizeText();
        UpdateHidScaleLabel();
        RedrawEraser();

        SetStatus("描摹已清除。");
        Log.Info("清除描摹");
    }

    private void OnComputeArea(object sender, RoutedEventArgs e)
    {
        if (_palmPoints.Count < 3)
        {
            SetStatus("描摹点太少，请先在「① 描摹手掌轮廓」页描一圈。");
            return;
        }

        // 逐笔做 Douglas-Peucker，避免多笔之间产生虚假的连接边。
        _simplified = new List<Point>();
        _simplifiedStrokes.Clear();
        _traceAreaDiu2 = 0;
        foreach (List<Point> stroke in _palmStrokes)
        {
            List<Point> s = Geometry2D.DouglasPeucker(stroke, 2.5);
            _simplifiedStrokes.Add(s);
            _simplified.AddRange(s);
            if (s.Count >= 3)
                _traceAreaDiu2 += Geometry2D.PolygonArea(s); // 直接算描摹闭合曲线的面积
        }
        _hull = Geometry2D.ConvexHull(_simplified);
        if (_hull.Count < 3)
        {
            SetStatus("凸包退化（点近似共线），请重新描摹。");
            _hasArea = false;
            return;
        }

        _hasArea = true;
        DrawTraceOverlay();
        UpdateAreaOutput();
    }

    /// <summary>方式二：直接量手宽×手长（cm）→ 手掌面积 a = 宽×长，长宽比 = 宽/长。</summary>
    private void OnApplyHandSize(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(HandWidthInput.Text.Trim(), out double w) ||
            !double.TryParse(HandHeightInput.Text.Trim(), out double h) ||
            w < 2 || w > 60 || h < 2 || h > 60)
        {
            SetStatus("手宽/手长请填 2~60 cm。");
            Log.Warn("手输手掌尺寸无效");
            return;
        }

        _palmAreaCm2 = w * h;
        _palmAspect = w / h;
        _palmSource = "手输";
        Log.Info($"手掌尺寸(手输): {w:0.0} × {h:0.0} cm = {_palmAreaCm2:0.0} cm²，长宽比 {_palmAspect:0.000}");
        SetStatus($"已按手输尺寸设定手掌：{w:0.0} × {h:0.0} cm = {_palmAreaCm2:0.0} cm²。");

        UpdatePalmSizeText();
        UpdateHidScaleLabel();
        RedrawEraser(); // 自动倍率随之更新
    }

    private void UpdatePalmSizeText()
    {
        if (PalmSizeText is null)
            return;

        if (_palmAreaCm2 <= 0)
        {
            PalmSizeText.Text = "尚未设定手掌尺寸。";
            return;
        }

        string src = string.IsNullOrEmpty(_palmSource) ? "" : $"（{_palmSource}）";
        string shape = _palmAspect > 0 ? $"\n长宽比 {_palmAspect:0.000}（擦除区形状按此比值）" : "";
        PalmSizeText.Text = $"手掌 a = {_palmAreaCm2:0.0} cm²{src}{shape}";
    }

    private void UpdateAreaOutput()
    {
        if (!_hasArea || _hull is null || _simplified is null || _calib is null)
        {
            TraceInfoText.Text = "未校准：请先完成屏幕校准。";
            return;
        }

        double hullDiu2 = Geometry2D.PolygonArea(_hull);
        double traceDiu2 = _traceAreaDiu2;

        double hullCm2 = hullDiu2 * _mmPerDiuX * _mmPerDiuY / 100.0;
        double traceCm2 = traceDiu2 * _mmPerDiuX * _mmPerDiuY / 100.0;
        double ratio = traceDiu2 > 0 ? hullDiu2 / traceDiu2 : 0;

        _palmAreaCm2 = traceCm2; // 供第③页「HID 计数→mm」标定使用
        _palmSource = "描摹";
        // 轮廓外接矩形的长宽比作为擦除区形状（你描横就横、描竖就竖）
        double minX = _hull.Min(p => p.X), maxX = _hull.Max(p => p.X);
        double minY = _hull.Min(p => p.Y), maxY = _hull.Max(p => p.Y);
        double bboxW = maxX - minX, bboxH = maxY - minY;
        _palmAspect = (bboxW > 0 && bboxH > 0) ? bboxW / bboxH : 0;
        UpdatePalmSizeText();
        UpdateHidScaleLabel();
        RedrawEraser(); // 手掌面积变了 → 自动倍率随之更新

        Log.Info($"手掌面积: 原始点={_palmPoints.Count} 去抖后={_simplified.Count} 凸包顶点={_hull.Count} 描摹曲线={traceCm2:0.00}cm² 凸包={hullCm2:0.00}cm² 凸包/描摹={ratio:0.00}");

        TraceInfoText.Text =
            $"原始点: {_palmPoints.Count}（{_palmStrokes.Count} 笔）\n" +
            $"去抖后 (Douglas-Peucker ε=2.5): {_simplified.Count}\n" +
            $"凸包顶点: {_hull.Count}\n" +
            $"描摹曲线面积: {traceDiu2:0} DIU²\n" +
            $"凸包面积: {hullDiu2:0} DIU²  （比值 {ratio:0.00}）";

        ResultText.Text =
            $"【② 手掌面积】\n" +
            $"描摹闭合曲线 ≈ {traceCm2:0.00} cm²   ← 直接 Shoelace\n" +
            $"凸包 ≈ {hullCm2:0.00} cm²   ← 方案用的（近凸近似）\n" +
            $"凸包/描摹 = {ratio:0.00}（≈1 说明轮廓近似凸；>1.1 多为手指分叉的内凹或被填平的凹口）";
    }

    private void DrawTraceOverlay()
    {
        TraceOverlay.Children.Clear();

        // 描摹去抖后的轮廓（逐笔闭合，蓝色虚线）——这就是"圈"本身
        foreach (List<Point> s in _simplifiedStrokes)
        {
            if (s.Count < 2) continue;
            var traced = BuildPolyline(s, true);
            traced.Stroke = Brushes.DodgerBlue;
            traced.StrokeThickness = 1.5;
            traced.StrokeDashArray = new DoubleCollection { 3, 3 };
            TraceOverlay.Children.Add(traced);
        }

        if (_hull is not null)
        {
            var hullLine = BuildPolyline(_hull, true);
            hullLine.Stroke = Brushes.OrangeRed;
            hullLine.StrokeThickness = 2;
            TraceOverlay.Children.Add(hullLine);
        }
    }

    private static Polyline BuildPolyline(IReadOnlyList<Point> pts, bool close)
    {
        var line = new Polyline
        {
            Stroke = Brushes.SeaGreen,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 3 },
        };
        foreach (Point p in pts)
            line.Points.Add(p);
        if (close && pts.Count > 0)
            line.Points.Add(pts[0]);
        return line;
    }

    // ================= ② 手掌接触面积（只读，不推算） =================

    private void OnContactTouchDown(object sender, TouchEventArgs e)
    {
        Rect b = e.GetTouchPoint(ContactHost).Bounds;
        LogHidFrame(b);
        SubmitContact(ContactSource.Wpf, b, applyThreshold: true, WpfDetail(b));
    }

    private void OnContactTouchMove(object sender, TouchEventArgs e)
    {
        Rect b = e.GetTouchPoint(ContactHost).Bounds;
        LogHidFrame(b);
        SubmitContact(ContactSource.Wpf, b, applyThreshold: true, WpfDetail(b));
    }

    private void OnContactTouchUp(object sender, TouchEventArgs e)
        => SetStatus("手掌接触面积：" + PeakContactLine());

    private void OnContactMouseDown(object sender, MouseButtonEventArgs e)
        => SetStatus("鼠标不携带接触尺寸；请用真触摸屏，或到「③ 面积擦预览」页用「注入」自测。");

    private void OnContactMouseMove(object sender, MouseEventArgs e)
    {
        // 鼠标没有接触尺寸，这里不做任何事（留空以免页面无响应）
    }

    // ---- 第三页「面积擦预览」的触摸入口 ----

    private void OnEraserTouchDown(object sender, TouchEventArgs e)
    {
        Rect b = e.GetTouchPoint(EraserHost).Bounds;
        LogHidFrame(b);
        SubmitContact(ContactSource.Wpf, b, applyThreshold: true, WpfDetail(b));
    }

    private void OnEraserTouchMove(object sender, TouchEventArgs e)
    {
        Rect b = e.GetTouchPoint(EraserHost).Bounds;
        LogHidFrame(b);
        SubmitContact(ContactSource.Wpf, b, applyThreshold: true, WpfDetail(b));
    }

    private string WpfDetail(Rect b)
        => $"{b.Width:0.###}×{b.Height:0.###} DIP → {b.Width * _mmPerDiuX:0.###}×{b.Height * _mmPerDiuY:0.###} mm";

    private void OnEraserTouchUp(object sender, TouchEventArgs e)
    {
        SetStatus($"面积擦预览：{CurrentContactLine()}");
    }

    /// <summary>
    /// 全局触摸帧回调：每个触摸帧都会来，取当前活动中接触面积最大的触点（= 手掌）。
    /// 这样只要没抬手，改变按压/角度导致接触矩形变化时，预览会实时刷新，而不依赖位置是否移动。
    /// </summary>
    private void OnTouchFrameReported(object sender, TouchFrameEventArgs e)
    {
        if (StageTabs.SelectedIndex != 2)
            return; // 只在本页（面积擦预览）工作

        try
        {
            TouchPointCollection contacts = e.GetTouchPoints(EraserHost);
            TouchPoint? best = null;
            double bestArea = -1;
            foreach (TouchPoint tp in contacts)
            {
                double area = tp.Bounds.Width * tp.Bounds.Height;
                if (area > bestArea)
                {
                    bestArea = area;
                    best = tp;
                }
            }

            if (best is TouchPoint p)
            {
                LogHidFrame(p.Bounds);
                SubmitContact(ContactSource.Wpf, p.Bounds, applyThreshold: true, WpfDetail(p.Bounds));
            }
        }
        catch
        {
            // 标签页切换瞬间元素尚未布局完成，忽略该帧
        }
    }

    /// <summary>逐帧记录驱动上报的原始接触矩形（限流，最多约 40 行/秒）。</summary>
    private void LogHidFrame(Rect b)
    {
        if (!_logFrames || !Log.Enabled)
            return;
        long now = Environment.TickCount64;
        if (now - _lastHidLogTicks < 25)
            return;
        _lastHidLogTicks = now;
        Log.Info($"HID 原始 Bounds = {b.Width:0.0} x {b.Height:0.0} DIP @({b.X:0.0},{b.Y:0.0})");
    }

    private void OnClearEraserPreview(object sender, RoutedEventArgs e)
    {
        _contactHistory.Clear();
        _lastContact = null;
        _peakContactMmW = 0;
        _peakContactMmH = 0;
        _lastContactTicks = 0;
        _driverSizeWarned = false;
        _sourceState.Clear();
        _sourceDetail.Clear();
        _activeSource = ContactSource.None;
        _lockedSource = ContactSource.None;
        _sourceValidSince.Clear();
        _handPeakCount = 0;
        _hidScaleHintShown = false;
        EraserOverlay.Children.Clear();
        UpdateSourceLabel();
        UpdateHidScaleLabel();
        EraserInfoText.Text = "已清除。用真触摸屏按一下手掌即可实时显示擦除区。";
        UpdateContactInfo();
        UpdateRatioLabel();
        SetStatus("面积擦预览已清除。");
        Log.Info("清除面积擦预览");
    }

    /// <summary>把某个接触矩形格式化成一行物理尺寸（mm）。</summary>
    private bool TryFormatContact(Rect? r, out string text)
    {
        text = "";
        if (r is not Rect c || _mmPerDiuX <= 0)
            return false;
        double wMm = c.Width * _mmPerDiuX;
        double hMm = c.Height * _mmPerDiuY;
        text = $"{wMm:0.0} × {hMm:0.0} mm = {wMm * hMm:0} mm²";
        return true;
    }

    private string CurrentContactLine()
        => TryFormatContact(_lastContact, out string s) ? "当前 " + s : "当前：未读到（需真触摸屏）";

    private string PeakContactLine()
        => _peakContactMmW > 0 && _peakContactMmH > 0
            ? $"本次峰值 {_peakContactMmW:0.0} × {_peakContactMmH:0.0} mm = {_peakContactMmW * _peakContactMmH:0} mm²"
            : "本次峰值：未读到";

    private void UpdateContactInfo()
    {
        if (ContactInfoText is null)
            return;
        if (_lastContact is not Rect && !(_peakContactMmW > 0 && _peakContactMmH > 0))
        {
            ContactInfoText.Text = "尚未读到接触尺寸。";
            return;
        }
        ContactInfoText.Text = CurrentContactLine() + "\n" + PeakContactLine();
    }

    // ---------- 实时接触 -> 擦除区（按多大，擦多大） ----------

    /// <summary>三路接触来源统一入口：按优先级（原始HID &gt; WM_POINTER &gt; WPF）仲裁后驱动预览。</summary>
    private void SubmitContact(ContactSource src, Rect rectDiu, bool applyThreshold, string detail)
    {
        long now = Environment.TickCount64;

        // 退化接触框（0×0）不携带任何尺寸信息：设备即使手指没抬起，也会周期性地夹带这种"空槽位"帧
        // （实测真机 raw HID 会间歇报 W=0 H=0）。若把它记成"该来源不可用"，高优先来源会被短暂降级，
        // 于是与低优先来源反复横跳，而每次切换都会清空中值滤波窗 → 擦除区尺寸抖动。
        // 故直接忽略退化帧，保留上一次有效值，直到超过 SourceFreshMs 自然过期。
        if (rectDiu.Width <= 0 || rectDiu.Height <= 0)
            return;

        // 只有"尺寸可用"的来源才有资格成为生效来源（避免被拒的来源与有效来源反复横跳）
        bool eligible = true;
        if (applyThreshold && _mmPerDiuX > 0)
            eligible = rectDiu.Width * _mmPerDiuX >= MinContactMm && rectDiu.Height * _mmPerDiuY >= MinContactMm;

        _sourceState[src] = (now, rectDiu, eligible);
        _sourceDetail[src] = detail;

        // 维护“连续有效起始时刻”：自适应锁定靠它判断某一路是否稳定可用
        if (eligible)
        {
            if (!_sourceValidSince.ContainsKey(src))
                _sourceValidSince[src] = now;

            // 距上一次有效接触超过新鲜窗口 → 判定为一次新的按压，重置峰值
            if (now - _lastContactTicks > SourceFreshMs)
            {
                _peakContactMmW = 0;
                _peakContactMmH = 0;
            }
            _lastContactTicks = now;
        }
        else
        {
            _sourceValidSince.Remove(src);
        }

        ContactSource eff = ResolveSource(now);
        if (eff == ContactSource.None)
        {
            UpdateSourceLabel();
            return;
        }

        if (eff != _activeSource)
        {
            _activeSource = eff;
            _contactHistory.Clear(); // 换来源就重置滤波，避免不同量纲互相污染
            Log.Info($"接触来源切换为 {SourceName(eff)}");
        }
        UpdateSourceLabel();

        // 原始HID 是设备上报的真值，不套 mm 阈值；其余来源套阈值过滤占位值
        UpdateLiveContact(_sourceState[eff].Rect, applyThreshold: eff != ContactSource.RawHid);
    }

    private static readonly ContactSource[] PriorityOrder =
        { ContactSource.RawHid, ContactSource.Pointer, ContactSource.Wpf };

    private bool IsSourceUsable(ContactSource s, long now)
        => _sourceState.TryGetValue(s, out var st) && st.Eligible && now - st.Time <= SourceFreshMs;

    /// <summary>决定当前该用哪一路来源：手动指定则只认那一路；自适应则稳定识别后锁定。</summary>
    private ContactSource ResolveSource(long now)
    {
        // 手动指定：只认那一路，没数据就是没数据（避免又偷偷切回别的来源）
        if (_sourceMode != SourceMode.Auto)
        {
            ContactSource want = _sourceMode switch
            {
                SourceMode.RawHid => ContactSource.RawHid,
                SourceMode.Pointer => ContactSource.Pointer,
                SourceMode.Wpf => ContactSource.Wpf,
                _ => ContactSource.None,
            };
            return IsSourceUsable(want, now) ? want : ContactSource.None;
        }

        // 自适应：已锁定 → 一直跟随，直到它静默太久（抬手/失效）才解锁重新识别
        if (_lockedSource != ContactSource.None)
        {
            if (_sourceState.TryGetValue(_lockedSource, out var st)
                && st.Eligible && now - st.Time <= SourceLockReleaseMs)
                return _lockedSource;

            Log.Info($"自适应解锁：{SourceName(_lockedSource)} 已静默超过 {SourceLockReleaseMs}ms，重新识别");
            _lockedSource = ContactSource.None;
            _sourceValidSince.Clear();
        }

        // 未锁定：按优先级找“连续有效 ≥ 观察期”的来源并锁定
        foreach (ContactSource s in PriorityOrder)
        {
            if (IsSourceUsable(s, now) && _sourceValidSince.TryGetValue(s, out long since)
                && now - since >= SourceLockObserveMs)
            {
                _lockedSource = s;
                Log.Info($"自适应锁定来源：{SourceName(s)}（连续有效 ≥ {SourceLockObserveMs}ms）");
                return s;
            }
        }

        // 观察期内还没锁定：先用当前最高优先的可用来源垫着显示
        foreach (ContactSource s in PriorityOrder)
            if (IsSourceUsable(s, now))
                return s;

        return ContactSource.None;
    }

    private static string SourceModeName(SourceMode m) => m switch
    {
        SourceMode.RawHid => "原始HID(设备上报)",
        SourceMode.Pointer => "WM_POINTER rcContact",
        SourceMode.Wpf => "WPF TouchPoint.Bounds",
        _ => "自适应(自动锁定)",
    };

    private void OnSourceModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceModeCombo is null)
            return; // XAML 解析期提前触发，忽略

        _sourceMode = SourceModeCombo.SelectedIndex switch
        {
            1 => SourceMode.RawHid,
            2 => SourceMode.Pointer,
            3 => SourceMode.Wpf,
            _ => SourceMode.Auto,
        };
        _lockedSource = ContactSource.None;
        _activeSource = ContactSource.None;
        _sourceValidSince.Clear();
        _contactHistory.Clear();
        _lastContact = null;
        _peakContactMmW = 0;
        _peakContactMmH = 0;
        Log.Info($"接触来源模式切换为: {SourceModeName(_sourceMode)}");
        UpdateSourceLabel();
    }

    private static string SourceName(ContactSource s) => s switch
    {
        ContactSource.RawHid => "原始HID(设备上报)",
        ContactSource.Pointer => "WM_POINTER rcContact",
        ContactSource.Wpf => "WPF TouchPoint.Bounds",
        _ => "无",
    };

    private void UpdateSourceLabel()
    {
        if (SourceInfoText is null)
            return;

        long now = Environment.TickCount64;
        var sb = new StringBuilder();

        string mode = _sourceMode != SourceMode.Auto
            ? $"手动：{SourceModeName(_sourceMode)}"
            : (_lockedSource != ContactSource.None
                ? $"自适应：已锁定 {SourceName(_lockedSource)}"
                : "自适应：识别中…");
        sb.Append(mode).Append("   |   生效: ").Append(SourceName(_activeSource));

        foreach (ContactSource s in PriorityOrder)
        {
            bool active = s == _activeSource;
            bool fresh = _sourceState.TryGetValue(s, out var st) && now - st.Time <= SourceFreshMs;
            string val = _sourceDetail.TryGetValue(s, out string? d) ? d : "（无）";
            sb.Append('\n').Append(active ? "▶ " : "   ").Append(SourceName(s)).Append(": ").Append(val);
            if (!fresh && _sourceState.ContainsKey(s)) sb.Append("  (旧)");
        }
        SourceInfoText.Text = sb.ToString();
    }

    /// <summary>定时刷新来源状态：所有来源都过期后把生效来源归零，并让界面显示「无」。</summary>
    private void RefreshSourceState()
    {
        long now = Environment.TickCount64;
        bool anyFresh = false;
        foreach (ContactSource s in new[] { ContactSource.RawHid, ContactSource.Pointer, ContactSource.Wpf })
        {
            if (_sourceState.TryGetValue(s, out var st) && st.Eligible && now - st.Time <= SourceFreshMs)
            {
                anyFresh = true;
                break;
            }
        }

        if (!anyFresh && _activeSource != ContactSource.None)
        {
            _activeSource = ContactSource.None;
            Log.Info("接触来源全部过期，生效来源归零");
        }
        UpdateSourceLabel();
    }

    // ---------- HID 计数 → mm 标定（设备只给逻辑计数、不给物理单位时） ----------

    /// <summary>用第①页量出的手掌面积标定"1 个 HID 计数 = 多少 mm"。</summary>
    private void OnCalibrateHidScale(object sender, RoutedEventArgs e)
    {
        if (_palmAreaCm2 <= 0)
        {
            SetStatus("请先到「① 描摹手掌轮廓」页描一圈手掌并点「计算手掌面积」。");
            Log.Warn("HID 标定中止：尚未测量手掌面积");
            return;
        }
        if (_handPeakCount <= 0)
        {
            SetStatus("请先在本页用整只手（含手指）按一下，采到计数后再点标定。");
            Log.Warn("HID 标定中止：未采到手掌计数");
            return;
        }
        // 手掌接触的计数应明显大于单指；太小说明采到的是单指（或设备根本不上报尺寸），
        // 直接标定会被放大成很大的 mm/计数，之后单指也能画出整只手大小的擦除区。
        if (_handPeakCount < MinHandPeakCount)
        {
            SetStatus($"采到的峰值计数只有 {_handPeakCount}（< {MinHandPeakCount}），像是单指轻按或该设备不上报有效尺寸。" +
                      "请用整只手重按一次；或改用「1 计数 = ? mm」手动设置。");
            Log.Warn($"HID 标定中止：峰值计数 {_handPeakCount} < {MinHandPeakCount}，疑似单指或设备不上报有效尺寸");
            return;
        }

        double diameterMm = 2 * Math.Sqrt(_palmAreaCm2 * 100 / Math.PI); // 面积 cm² → 等效圆直径 mm
        double scale = diameterMm / _handPeakCount;
        RawHidScan.CountsToMmScale = scale;
        if (scale > 10)
            Log.Warn($"HID 标定结果偏大：1 计数 = {scale:0.000}mm（等效直径 {diameterMm:0.0}mm ÷ 峰值计数 {_handPeakCount}），单指可能被画得过大，可用「倍率」修正");
        Log.Info($"HID 计数标定: 手掌面积={_palmAreaCm2:0.00}cm² 等效直径={diameterMm:0.0}mm 峰值计数={_handPeakCount} → 1 计数 = {scale:0.0000} mm");
        SetStatus($"标定完成：1 计数 ≈ {scale:0.0000} mm。现在按手掌即可看到等大擦除区。");
        _driverSizeWarned = false; // 允许重新提示
        _hidScaleHintShown = true; // 已标定，不再提示
        UpdateHidScaleLabel();
    }

    private void UpdateHidScaleLabel()
    {
        if (HidScaleText is null)
            return;
        string scale = RawHidScan.CountsToMmScale > 0 ? $"{RawHidScan.CountsToMmScale:0.0000} mm/计数" : "未标定";
        string area = _palmAreaCm2 > 0 ? $"{_palmAreaCm2:0.0} cm²" : "未测";
        HidScaleText.Text = $"HID 计数标定: {scale}    手掌峰值计数: {_handPeakCount}    手掌面积: {area}";
    }

    /// <summary>手动设置「1 计数 = ? mm」（没硬件/无法按手掌时用）。</summary>
    private void OnApplyManualScale(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(ScaleInput.Text.Trim(), out double k) || k <= 0 || k > 100)
        {
            SetStatus("请输入 0~100 之间的 mm/计数。");
            return;
        }
        RawHidScan.CountsToMmScale = k;
        _hidScaleHintShown = true;
        Log.Info($"手动设置 HID 计数比例: 1 计数 = {k:0.0000} mm");
        SetStatus($"已设置：1 计数 = {k:0.0000} mm。可点「注入」自测整条链路。");
        UpdateHidScaleLabel();
    }

    /// <summary>注入一组模拟原始 HID 计数：无触摸硬件时也能验证绘制/倍率/等面积圆。</summary>
    private void OnInjectSyntheticCount(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(SimCountInput.Text.Trim(), out int count) || count <= 0 || count > MaxSimCount)
        {
            SetStatus($"请输入 1~{MaxSimCount} 的模拟计数。");
            return;
        }
        if (RawHidScan.CountsToMmScale <= 0)
        {
            SetStatus("请先设置「1 计数 = ? mm」或完成手掌标定。");
            return;
        }
        if (_mmPerDiuX <= 0 || _mmPerDiuY <= 0)
        {
            SetStatus("尚未校准，无法换算。");
            return;
        }

        double wMm = Math.Min(count * RawHidScan.CountsToMmScale, MaxInjectMm);
        double wDiu = wMm / _mmPerDiuX;
        double hDiu = wMm / _mmPerDiuY;
        double lx = EraserHost.ActualWidth / 2;
        double ly = EraserHost.ActualHeight / 2;

        Log.Info($"注入模拟原始HID: 计数={count} → {wMm:0.0}mm");
        _hidScaleHintShown = true;
        SubmitContact(ContactSource.RawHid,
            new Rect(lx - wDiu / 2, ly - hDiu / 2, wDiu, hDiu),
            applyThreshold: false,
            detail: $"W={count} H={count}(模拟) → {wMm:0.0}×{wMm:0.0} mm");
    }

    /// <summary>
    /// 收到一次接触矩形（DIP）：中值滤波求稳定值，再等比映射成擦除区。
    /// 中值滤波保证"稳定不乱跳"，固定倍率保证"等比"。
    /// <paramref name="applyThreshold"/> = false 时不套用 mm 阈值（用于原始 HID 上报的真值）。
    /// </summary>
    private void UpdateLiveContact(Rect b, bool applyThreshold = true)
    {
        // 驱动没上报接触尺寸的两种情形：直接给 0；或给极小占位值（如 0.1×0.1 DIP）。
        // 两种都判为"未上报"，绝不参与计算（避免乱报），并提示一次。
        bool tooSmall = b.Width <= 0 || b.Height <= 0;
        if (!tooSmall && applyThreshold && _mmPerDiuX > 0)
        {
            double wMmRaw = b.Width * _mmPerDiuX;
            double hMmRaw = b.Height * _mmPerDiuY;
            tooSmall = wMmRaw < MinContactMm || hMmRaw < MinContactMm;
        }

        if (tooSmall)
        {
            if (ContactInfoText is not null)
                ContactInfoText.Text = "驱动未上报接触尺寸（只给位置或占位值）。";
            if (!_driverSizeWarned)
            {
                _driverSizeWarned = true;
                Log.Warn($"驱动未上报有效接触面积：原始 Bounds={b.Width:0.###}x{b.Height:0.###} DIP，面积擦预览不可用（需能上报接触尺寸的数字化器）");
                if (EraserInfoText is not null && !_hidScaleHintShown)
                    EraserInfoText.Text = $"驱动只上报位置、未上报接触尺寸（原始 Bounds={b.Width:0.##}×{b.Height:0.##} DIP）。\n面积擦需要能上报接触尺寸的数字化器（真触摸屏/笔）。";
            }
            return;
        }

        // 同一帧可能被元素事件和全局帧事件重复喂入：相同值只算一次，避免滤波窗被占满。
        if (_contactHistory.Count > 0)
        {
            Rect last = _contactHistory[^1];
            if (Math.Abs(last.X - b.X) < 0.01 && Math.Abs(last.Y - b.Y) < 0.01 &&
                Math.Abs(last.Width - b.Width) < 0.01 && Math.Abs(last.Height - b.Height) < 0.01)
                return;
        }

        _contactHistory.Add(b);
        if (_contactHistory.Count > ContactHistoryMax)
            _contactHistory.RemoveAt(0);

        _lastContact = MedianRect(_contactHistory);

        Rect cur = _lastContact.Value;
        if (_mmPerDiuX > 0 && _mmPerDiuY > 0)
        {
            double curMmW = cur.Width * _mmPerDiuX;
            double curMmH = cur.Height * _mmPerDiuY;
            _lastContactMmW = curMmW;
            _lastContactMmH = curMmH;

            // 记录本次按压的峰值接触（按面积最大）——“手掌接触面积”取峰值，而不是松手瞬间缩小的值
            if (curMmW * curMmH > _peakContactMmW * _peakContactMmH)
            {
                _peakContactMmW = curMmW;
                _peakContactMmH = curMmH;
            }
        }

        DrawLiveEraser();
        UpdateEraserInfo();
        UpdateContactInfo();
        UpdateRatioLabel();
        LogFilteredContact();
    }

    /// <summary>限流记录滤波后的接触与擦除区（约 4 行/秒），便于回看"数值是否稳定、是否等比"。</summary>
    private void LogFilteredContact()
    {
        if (!Log.Enabled || _lastContact is not Rect c || _mmPerDiuX <= 0)
            return;
        long now = Environment.TickCount64;
        if (now - _lastFilteredLogTicks < 250)
            return;
        _lastFilteredLogTicks = now;

        double wMm = c.Width * _mmPerDiuX;
        double hMm = c.Height * _mmPerDiuY;
        double areaMm2 = EraserAreaMm2(c);
        double r = EffectiveRatio();
        Log.Info($"接触(滤波): {wMm:0.0}x{hMm:0.0}mm 模式={(_followPressure ? "随压力" : "固定手掌")} 倍率={r:0.000}({(_autoRatio ? "自动" : "手动")}) 擦除区面积={areaMm2:0}mm² 形状={(_shapeIsCircle ? "正圆" : "矩形")}");
    }

    private void OnShapeChanged(object sender, RoutedEventArgs e)
    {
        if (ShapeCircle is null)
            return; // XAML 解析期提前触发，忽略
        _shapeIsCircle = ShapeCircle.IsChecked == true;
        Log.Info($"擦除形状切换为: {(_shapeIsCircle ? "正圆(等面积)" : "矩形")}");
        if (EraserOverlay is not null)
        {
            DrawLiveEraser();
            UpdateEraserInfo();
        }
    }

    private void OnRatioChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RatioLabel is null)
            return;
        _eraserRatio = e.NewValue;
        Log.Info($"手动倍率 = {_eraserRatio:0.00} ×");
        RedrawEraser();
    }

    private void OnAutoRatioChanged(object sender, RoutedEventArgs e)
    {
        if (AutoRatioCheck is null)
            return;
        _autoRatio = AutoRatioCheck.IsChecked == true;
        if (RatioSlider is not null)
            RatioSlider.IsEnabled = !_autoRatio; // 只有手动倍率才用滑块
        Log.Info($"倍率模式: {(_autoRatio ? "自动" : "手动")}");
        RedrawEraser();
    }

    private void OnFollowPressureChanged(object sender, RoutedEventArgs e)
    {
        if (FollowPressureCheck is null)
            return;
        _followPressure = FollowPressureCheck.IsChecked == true;
        Log.Info($"擦除区随压力变化 = {_followPressure}");
        RedrawEraser();
    }

    /// <summary>自动倍率 k = √(a / b)：a = ①手掌面积，b = ②页手掌按压峰值。数据不足返回 0；不设上限。</summary>
    private double ComputeAutoRatio()
    {
        if (_palmAreaCm2 <= 0)
            return 0;
        if (_peakContactMmW <= 0 || _peakContactMmH <= 0)
            return 0;

        double aMm2 = _palmAreaCm2 * 100.0;
        double bMm2 = _peakContactMmW * _peakContactMmH;
        if (!(bMm2 > 0))
            return 0;

        double k = Math.Sqrt(aMm2 / bMm2);
        return double.IsFinite(k) && k > 0 ? k : 0;
    }

    /// <summary>当前实际生效的倍率：自动优先；自动数据不足时退回手动滑块值。</summary>
    private double EffectiveRatio()
    {
        if (_autoRatio)
        {
            double k = ComputeAutoRatio();
            if (k > 0)
                return k;
        }
        return _eraserRatio;
    }

    /// <summary>手动倍率滑块只在"手动模式"，或"自动模式但自动数据不足"时可用（避免自动模式下滑块成死控件）。</summary>
    private void SyncRatioSliderEnabled()
    {
        if (RatioSlider is null)
            return;
        bool enable = !_autoRatio || ComputeAutoRatio() <= 0;
        if (RatioSlider.IsEnabled != enable)
            RatioSlider.IsEnabled = enable;
    }

    private void UpdateRatioLabel()
    {
        if (RatioLabel is null)
            return;

        SyncRatioSliderEnabled();

        if (!_autoRatio)
        {
            RatioLabel.Text = $"倍率(手动) = {_eraserRatio:0.00} ×";
            return;
        }

        double k = ComputeAutoRatio();
        RatioLabel.Text = k > 0
            ? $"倍率(自动) = {k:0.000} ×"
            : $"倍率(自动) = 数据不足（需①面积 + ②按压）→ 暂用手动 {_eraserRatio:0.00} ×";
    }

    /// <summary>重画擦除区并刷新倍率标签与说明（倍率/开关/形状变化后调用）。</summary>
    private void RedrawEraser()
    {
        UpdateRatioLabel();
        if (EraserOverlay is not null)
        {
            DrawLiveEraser();
            UpdateEraserInfo();
        }
    }

    /// <summary>
    /// DPI/校准变化后用保存的物理毫米重建擦除区：DIU 尺寸已不是同一个物理尺寸，
    /// 必须按新的 mm/DIU 反算，否则绿框会保持旧的物理大小。
    /// </summary>
    private void RescaleLiveEraserToDpi()
    {
        if (_lastContact is not Rect c || _lastContactMmW <= 0 || _lastContactMmH <= 0)
            return;
        if (_mmPerDiuX <= 0 || _mmPerDiuY <= 0)
            return;

        double w = _lastContactMmW / _mmPerDiuX;
        double h = _lastContactMmH / _mmPerDiuY;
        if (!double.IsFinite(w) || !double.IsFinite(h) || w <= 0 || h <= 0)
            return;

        _lastContact = new Rect(c.X + c.Width / 2 - w / 2, c.Y + c.Height / 2 - h / 2, w, h);
        DrawLiveEraser();
        UpdateEraserInfo();
    }

    private void DrawLiveEraser()
    {
        if (EraserOverlay is null)
            return;
        EraserOverlay.Children.Clear();
        if (_lastContact is not Rect c)
            return;

        double cx = c.X + c.Width / 2;
        double cy = c.Y + c.Height / 2;

        if (_shapeIsCircle)
        {
            if (CircleDiameterDiu(c) is not double d)
                return;
            var circle = new Ellipse
            {
                Width = d,
                Height = d,
                Stroke = Brushes.Lime,
                StrokeThickness = 2,
                Fill = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0xFF, 0x00)),
            };
            EraserOverlay.Children.Add(circle);
            Canvas.SetLeft(circle, cx - d / 2);
            Canvas.SetTop(circle, cy - d / 2);
        }
        else
        {
            var (w, h) = RectSizeDiu(c);
            if (w <= 0 || h <= 0)
                return;
            var rect = new Rectangle
            {
                Width = w,
                Height = h,
                Stroke = Brushes.Lime,
                StrokeThickness = 2,
                Fill = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0xFF, 0x00)),
            };
            EraserOverlay.Children.Add(rect);
            Canvas.SetLeft(rect, cx - w / 2);
            Canvas.SetTop(rect, cy - h / 2);
        }

        var dot = new Ellipse { Width = 6, Height = 6, Fill = Brushes.Yellow };
        EraserOverlay.Children.Add(dot);
        Canvas.SetLeft(dot, cx - 3);
        Canvas.SetTop(dot, cy - 3);
    }

    /// <summary>擦除区面积（mm²）：随压力=接触面积×倍率²；固定=①的手掌面积。</summary>
    private double EraserAreaMm2(Rect c)
    {
        if (_followPressure)
        {
            double r = EffectiveRatio();
            double wMm = c.Width * _mmPerDiuX;
            double hMm = c.Height * _mmPerDiuY;
            return wMm * hMm * r * r;
        }
        return _palmAreaCm2 * 100.0;
    }

    /// <summary>① 手掌面积换算成预览区的 DIU²；不可用返回 0。</summary>
    private double PalmAreaDiu2()
    {
        if (_palmAreaCm2 <= 0 || _mmPerDiuX <= 0 || _mmPerDiuY <= 0)
            return 0;
        double a = _palmAreaCm2 * 100.0 / (_mmPerDiuX * _mmPerDiuY);
        return double.IsFinite(a) && a > 0 ? a : 0;
    }

    /// <summary>擦除区矩形尺寸（DIU）：面积按模式算，形状（长宽比）用①页设定的手掌长宽比。</summary>
    private (double w, double h) RectSizeDiu(Rect c)
    {
        if (_mmPerDiuX <= 0 || _mmPerDiuY <= 0)
            return (0, 0);

        double areaMm2 = EraserAreaMm2(c);
        if (!(areaMm2 > 0))
            return (0, 0);

        double areaDiu2 = areaMm2 / (_mmPerDiuX * _mmPerDiuY);
        if (!double.IsFinite(areaDiu2) || areaDiu2 <= 0)
            return (0, 0);

        // 形状（长宽比 w/h）：优先①页设定的手掌长宽比；没有则退回②页按压峰值；再没有用当前接触
        double aspect = _palmAspect > 0
            ? _palmAspect
            : (_peakContactMmW > 0 && _peakContactMmH > 0
                ? _peakContactMmW / _peakContactMmH
                : (c.Height > 0 && c.Width > 0 ? c.Width / c.Height : 1));
        if (!(aspect > 0) || !double.IsFinite(aspect))
            aspect = 1;

        double h = Math.Sqrt(areaDiu2 / aspect);
        double w = aspect * h;
        return double.IsFinite(w) && double.IsFinite(h) && w > 0 && h > 0 ? (w, h) : (0, 0);
    }

    /// <summary>擦除区为正圆时的直径（DIU）；不可用返回 null。</summary>
    private double? CircleDiameterDiu(Rect c)
    {
        double r = EffectiveRatio();
        double areaDiu2 = _followPressure
            ? c.Width * c.Height * r * r
            : PalmAreaDiu2();
        if (!double.IsFinite(areaDiu2) || areaDiu2 <= 0)
            return null;
        double d = Math.Sqrt(4 * areaDiu2 / Math.PI);
        return double.IsFinite(d) && d > 0 ? d : null;
    }

    private void UpdateEraserInfo()
    {
        if (EraserInfoText is null)
            return;

        if (_lastContact is not Rect c || _mmPerDiuX <= 0)
        {
            EraserInfoText.Text = "尚无驱动接触数据（需真触摸屏；鼠标/部分驱动不提供 Bounds）。";
            return;
        }

        double inW = c.Width * _mmPerDiuX;
        double inH = c.Height * _mmPerDiuY;
        double areaMm2 = EraserAreaMm2(c);

        var (rectW, rectH) = RectSizeDiu(c);
        string shape = _shapeIsCircle
            ? $"等面积圆 ⌀{2 * Math.Sqrt(Math.Max(areaMm2, 0) / Math.PI):0.0} mm（面积 {areaMm2:0} mm²）"
            : $"矩形 {rectW * _mmPerDiuX:0.0} × {rectH * _mmPerDiuY:0.0} mm（面积 {areaMm2:0} mm²）";

        double autoK = ComputeAutoRatio();
        string ratioText = _autoRatio
            ? (autoK > 0 ? $"自动 {autoK:0.000} ×" : $"手动 {_eraserRatio:0.00} ×（自动数据不足）")
            : $"手动 {_eraserRatio:0.00} ×";

        if (_followPressure)
        {
            EraserInfoText.Text =
                $"模式: 随压力变化（按越重越大）\n" +
                $"接触(中值滤波): {inW:0.0} × {inH:0.0} mm = {inW * inH:0} mm²，倍率 {ratioText}\n" +
                $"擦除区: {shape}";
        }
        else
        {
            string line = _palmAreaCm2 > 0
                ? $"手掌面积 a: {_palmAreaCm2:0.0} cm² = {areaMm2:0} mm²\n擦除区: {shape}"
                : "尚未测出手掌面积——请先到「① 描摹手掌轮廓」页描一圈并点「计算手掌面积」。";
            EraserInfoText.Text = "模式: 固定为手掌面积（不随压力变）\n" + line;
        }
    }

    private static Rect MedianRect(List<Rect> items)
    {
        return new Rect(
            Median(items.Select(r => r.X)),
            Median(items.Select(r => r.Y)),
            Median(items.Select(r => r.Width)),
            Median(items.Select(r => r.Height)));
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] a = values.OrderBy(v => v).ToArray();
        int n = a.Length;
        if (n == 0) return 0;
        return n % 2 == 1 ? a[n / 2] : (a[n / 2 - 1] + a[n / 2]) / 2.0;
    }

    // ================= 引导模式 =================

    private void OnStartGuide(object sender, RoutedEventArgs e) => StartGuide();

    private void StartGuide()
    {
        _guideSteps = BuildGuideSteps();
        _guideIndex = 0;
        GuideLayer.Visibility = Visibility.Visible;
        Log.Info("引导开始");
        ShowGuideStep(0);
    }

    private List<GuideStep> BuildGuideSteps() => new()
    {
        new GuideStep("欢迎使用 TouchErase · 手掌擦矫正 Demo",
            "整个流程：\n" +
            "1) 校准屏幕物理尺寸\n" +
            "2) 描一圈手掌轮廓 → 算手掌面积\n" +
            "3) 把手掌按上去 → 读驱动上报的接触面积\n" +
            "4) 面积擦预览 → 按多大、擦多大\n\n" +
            "点「下一步」我带你按顺序走一遍；想直接上手就点「跳过引导」。"),

        new GuideStep("第 1 步 · 检查校准",
            "顶部这一行是屏幕物理尺寸校准结果。\n" +
            "如果显示「来源: EDID（显示器固件）」，说明已自动读到，可以直接进入下一步。",
            () => CalibPanel),

        new GuideStep("第 1 步（备用）· EDID 读不到时",
            "若上方显示「EDID 读取失败」，在这个输入框填屏幕对角线英寸（如 15.6），再点右边的「应用」。",
            () => DiagInput),

        new GuideStep("第 2 步 · 描一圈手掌轮廓",
            "「手掌擦」指的是整只手（掌心 + 手指），所以要把手连手指一起描一圈。\n" +
            "在这个白色区域按住鼠标（或笔）描一圈；描的时候看窗口底部状态栏会显示「已收集 N 点」，数字在涨就说明在正常采点。",
            () => TraceCanvas, () => StageTabs.SelectedIndex = 0),

        new GuideStep("第 2 步 · 计算面积",
            "描好后点这个按钮。\n" +
            "白区会出现蓝色虚线（你描的圈）和红色实线（凸包），右侧会同时给出两个面积。算出的手掌面积还会用于第③页的「HID 计数→mm」标定。",
            () => ComputeAreaButton),

        new GuideStep("第 3 步 · 把手掌按上去读接触面积",
            "切到「② 手掌接触面积」页，把整只手（含手指）按在黑区上。\n" +
            "右侧实时显示当前接触尺寸，并记录本次按压的峰值（手掌接触面积取峰值）；不抬手、改变按压轻重或手的角度也会跟着变。\n" +
            "注意：需要能上报接触尺寸的数字化器（真触摸屏/笔）；鼠标不携带接触尺寸。",
            () => StageTabs, () => StageTabs.SelectedIndex = 1),

        new GuideStep("第 4 步 · 面积擦预览（第 ③ 页）",
            "切到「③ 面积擦预览」页，把手掌按在黑区上（真触摸屏）。\n" +
            "程序取驱动上报的接触尺寸画出等大擦除区——按多大，擦多大。\n" +
            "倍率默认「自动」= √(①手掌面积 ÷ ②按压峰值)，不用点、实时算，按下时擦除区≈手掌面积；取消自动才用下面的手动滑块。\n" +
            "设备只给逻辑计数（无毫米单位）时，先用①的手掌面积点「标定 HID 计数→mm」。\n" +
            "没有触摸硬件时：设「1 计数 = ? mm」再点「注入」，可自测整条链路。",
            () => AutoRatioCheck, () => StageTabs.SelectedIndex = 2),

        new GuideStep("完成",
            "至此都齐了：手掌面积(cm²)、驱动上报的接触面积，以及第 ③ 页「按多大、擦多大」的实时面积擦预览。\n" +
            "随时可以点右上角「引导」重新看一遍。"),
    };

    private void ShowGuideStep(int index)
    {
        _guideIndex = index;
        GuideStep step = _guideSteps[index];

        step.OnEnter?.Invoke();
        UpdateLayout();

        GuideStepLabel();
        GuideTitle.Text = step.Title;
        GuideBody.Text = step.Body;
        GuideNext.Content = index == _guideSteps.Count - 1 ? "完成" : "下一步";
        GuideBack.Visibility = index == 0 ? Visibility.Collapsed : Visibility.Visible;

        GuideLayer.Visibility = Visibility.Visible;
        RefreshGuideLayout();
    }

    private void GuideStepLabel()
        => GuideStepText.Text = $"第 {_guideIndex + 1} / {_guideSteps.Count} 步";

    private void OnGuideNext(object sender, RoutedEventArgs e)
    {
        if (_guideIndex >= _guideSteps.Count - 1)
        {
            CloseGuide();
            return;
        }
        ShowGuideStep(_guideIndex + 1);
    }

    private void OnGuideBack(object sender, RoutedEventArgs e)
    {
        if (_guideIndex > 0)
            ShowGuideStep(_guideIndex - 1);
    }

    private void OnGuideSkip(object sender, RoutedEventArgs e) => CloseGuide();

    private void CloseGuide()
    {
        GuideLayer.Visibility = Visibility.Collapsed;
        SetStatus("引导已结束。可随时点右上角「引导」重新查看。");
        Log.Info("引导结束");
    }

    /// <summary>窗口尺寸变化时，重算当前步的高亮框与卡片位置。</summary>
    private void RefreshGuideLayout()
    {
        if (GuideLayer.Visibility != Visibility.Visible)
            return;
        UpdateLayout();

        FrameworkElement? target = _guideSteps.Count > 0 && _guideIndex < _guideSteps.Count
            ? _guideSteps[_guideIndex].Target?.Invoke()
            : null;

        PositionGuide(GetElementRect(target));
    }

    private Rect? GetElementRect(FrameworkElement? el)
    {
        if (el is null || el.ActualWidth <= 0 || el.ActualHeight <= 0)
            return null;
        try
        {
            Point p = el.TransformToAncestor(this).Transform(new Point(0, 0));
            return new Rect(p, new Size(el.ActualWidth, el.ActualHeight));
        }
        catch
        {
            return null;
        }
    }

    private void PositionGuide(Rect? target)
    {
        double w = GuideLayer.ActualWidth;
        double h = GuideLayer.ActualHeight;
        if (w <= 0 || h <= 0)
            return;

        GuideDim.Data = BuildDimGeometry(target, w, h);

        if (target is Rect r)
        {
            var hole = r;
            hole.Inflate(6, 6);
            GuideHole.Visibility = Visibility.Visible;
            GuideHole.Width = hole.Width + 6;
            GuideHole.Height = hole.Height + 6;
            Canvas.SetLeft(GuideHole, hole.X - 3);
            Canvas.SetTop(GuideHole, hole.Y - 3);
        }
        else
        {
            GuideHole.Visibility = Visibility.Collapsed;
        }

        const double cardW = 400;
        GuideCard.Measure(new Size(cardW, double.PositiveInfinity));
        double cardH = GuideCard.DesiredSize.Height;

        double cx, cy;
        if (target is Rect t)
        {
            cx = t.X + t.Width / 2 - cardW / 2;
            cy = t.Y + t.Height + 18;
            if (cy + cardH + 12 > h)
                cy = t.Y - cardH - 18; // 下方放不下就放到上方
        }
        else
        {
            cx = (w - cardW) / 2;
            cy = (h - cardH) / 2;
        }

        cx = Math.Clamp(cx, 12, Math.Max(12, w - cardW - 12));
        cy = Math.Clamp(cy, 12, Math.Max(12, h - cardH - 12));

        Canvas.SetLeft(GuideCard, cx);
        Canvas.SetTop(GuideCard, cy);
    }

    private static Geometry BuildDimGeometry(Rect? hole, double w, double h)
    {
        var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
        group.Children.Add(new RectangleGeometry(new Rect(0, 0, w, h)));
        if (hole is Rect r)
        {
            var hr = r;
            hr.Inflate(6, 6);
            hr.Intersect(new Rect(0, 0, w, h));
            if (hr.Width > 0 && hr.Height > 0)
                group.Children.Add(new RectangleGeometry(hr, 8, 8));
        }
        return group;
    }

    // ================= 日志 UI =================

    private void UpdateLogUi()
    {
        if (LogPathText is null)
            return;
        LogPathText.Text = string.IsNullOrEmpty(Log.FilePath)
            ? "日志未初始化（可能无写入权限）"
            : Log.FilePath;
    }

    private void OnLogEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (!_uiReady || LogEnabledCheck is null)
            return;
        Log.Enabled = LogEnabledCheck.IsChecked == true;
        if (Log.Enabled)
            Log.Info("日志记录已开启");
        else
            Log.Warn("日志记录已关闭");
    }

    private void OnLogFramesChanged(object sender, RoutedEventArgs e)
    {
        if (!_uiReady || LogFramesCheck is null)
            return;
        _logFrames = LogFramesCheck.IsChecked == true;
        Log.Info($"逐帧记录 HID Bounds: {(_logFrames ? "开" : "关")}");
    }

    private void OnOpenLogFolder(object sender, RoutedEventArgs e)
    {
        string dir = Log.LogDirectory;
        if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir))
        {
            SetStatus("日志目录不存在（日志未初始化或无写入权限）。");
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", dir)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SetStatus("打开日志目录失败：" + ex.Message);
        }
    }

    // ================= HID 触摸诊断 =================

    private void OnHidDiagnostics(object sender, RoutedEventArgs e) => RunHidDiagnostics(full: true);

    /// <summary>
    /// HID 触摸诊断：Raw Input + HidP_* 直接读 HID 能力（不打开设备）。
    /// <paramref name="full"/> = false 仅写日志（启动轻量自检）；true 时额外把结论显示到界面。
    /// </summary>
    private void RunHidDiagnostics(bool full)
    {
        List<RawHidScan.HidTouchInfo> hid = RawHidScan.Scan();
        Log.Info($"RawInput HID 扫描: {hid.Count} 个 HID 设备");
        foreach (RawHidScan.HidTouchInfo h in hid)
            Log.Info($"HID(raw): {h.DeviceName} | 触摸屏={h.IsTouchScreen} | Width(0x48)={h.HasWidth} Height(0x49)={h.HasHeight} | 值用法数={h.InputValueCaps} | 用法: {h.UsageSummary}");

        List<RawHidScan.HidTouchInfo> touchHid = hid.Where(h => h.IsTouchScreen).ToList();
        RawHidScan.HidTouchInfo? withSize = touchHid.FirstOrDefault(t => t.HasWidth && t.HasHeight);

        if (touchHid.Count == 0)
        {
            Log.Warn("RawHid 结论: 未发现触摸屏 HID 设备（Digitizer 0x0D / Touch Screen 0x04）");
        }
        else
        {
            foreach (RawHidScan.HidTouchInfo t in touchHid)
            {
                if (t.HasWidth && t.HasHeight)
                    Log.Info($"RawHid 结论: 触摸屏『{t.DeviceName}』声明了接触尺寸 → 可加原始 HID 解码拿到面积。W[{t.WidthDetail}] H[{t.HeightDetail}]");
                else
                    Log.Warn($"RawHid 结论: 触摸屏『{t.DeviceName}』未声明 Width/Height → 设备不上报接触尺寸，软件层无法获取");
            }
        }

        if (!full || EraserInfoText is null)
            return;

        if (withSize is not null)
            EraserInfoText.Text = $"HID 诊断：触摸屏『{withSize.DeviceName}』声明了 Width/Height → 可尝试原始 HID 解码。";
        else if (touchHid.Count > 0)
            EraserInfoText.Text = "HID 诊断：检测到触摸屏但未声明 Width/Height → 设备不上报接触尺寸。";
        else
            EraserInfoText.Text = "HID 诊断：未发现触摸屏 HID 设备。";
    }

    private void SetStatus(string text) => StatusText.Text = text;
}
