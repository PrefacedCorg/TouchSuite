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

    private readonly List<Point> _touchPoints = new();
    private List<List<Point>>? _clusters;
    private readonly HashSet<int> _palmClusters = new();
    private bool _hasPalm;
    private Point _palmCenterDiu;
    private double _palmRadiusMm;

    // 驱动直接上报的接触矩形（来自 TouchPoint.Bounds，单位 DIP）
    private bool _hasDriverContact;
    private double _driverContactWidthMm;
    private double _driverContactHeightMm;

    // 实时接触 -> 擦除区
    private bool _shapeIsCircle;                 // 默认 false = 矩形
    private double _eraserRatio = 1.0;
    private readonly List<Rect> _contactHistory = new();
    private Rect? _lastContact;
    private bool _ratioCalibrating;
    private const int ContactHistoryMax = 5;
    private const double MinContactMm = 1.0;   // 小于此物理尺寸视为"驱动未上报有效接触面积"（占位值≈0.03mm，真实指腹≥5mm）
    private bool _driverSizeWarned;

    // 日志
    private bool _logFrames = true;
    private bool _uiReady;
    private long _lastHidLogTicks;
    private long _lastFilteredLogTicks;
    private readonly DispatcherTimer _logFlushTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    // WM_POINTER 通路（绕开 WPF 的 WM_TOUCH 直读接触矩形）
    private IntPtr _hwnd = IntPtr.Zero;
    private bool _pointerAreaLogged;
    private bool _pointerNoAreaLogged;
    private long _lastPointerLogTicks;
    private long _lastRawHidLogTicks;

    // 接触来源与优先级仲裁（原始HID > WM_POINTER > WPF）
    private enum ContactSource { None, Wpf, Pointer, RawHid }

    private const long SourceFreshMs = 300;
    private readonly Dictionary<ContactSource, (long Time, Rect Rect)> _sourceState = new();
    private readonly Dictionary<ContactSource, string> _sourceDetail = new();
    private ContactSource _activeSource = ContactSource.None;

    private static readonly Color[] ClusterPalette =
    {
        Color.FromRgb(0x7F, 0xD4, 0xFF), Color.FromRgb(0xFF, 0xC1, 0x6B),
        Color.FromRgb(0x9A, 0xE6, 0x7F), Color.FromRgb(0xE0, 0x9A, 0xFF),
        Color.FromRgb(0x6B, 0xE0, 0xC8), Color.FromRgb(0xFF, 0x9A, 0xB0),
        Color.FromRgb(0xD4, 0xD4, 0xD4), Color.FromRgb(0xC9, 0xA9, 0x6B),
    };

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
        ThresholdSlider.ValueChanged += (_, _) => UpdateSliderLabels();
        CutSlider.ValueChanged += (_, _) => UpdateSliderLabels();
        UpdateSliderLabels();

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
                if (StageTabs.SelectedIndex == 2)
                {
                    RawHidScan.RawTouchSample? sample = RawHidScan.HandleWmInput(lParam);
                    if (sample is not null)
                        HandleRawHidSample(sample);
                }
                break;

            case PointerTouch.WM_POINTERDOWN:
            case PointerTouch.WM_POINTERENTER:
            case PointerTouch.WM_POINTERUPDATE:
                if (StageTabs.SelectedIndex == 2)
                {
                    uint id = PointerTouch.GetPointerId(wParam);
                    if (id != 0 && PointerTouch.TryRead(id, out PointerTouch.POINTER_TOUCH_INFO ti, out bool hasArea))
                        HandlePointerContact(ti, hasArea);
                }
                break;
        }
        return IntPtr.Zero;
    }

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

        if (s.WidthMm is null || s.HeightMm is null || _mmPerDiuX <= 0 || _mmPerDiuY <= 0)
            return;

        double wDiu = s.WidthMm.Value / _mmPerDiuX;
        double hDiu = s.HeightMm.Value / _mmPerDiuY;

        Point hostOffset = EraserHost.TranslatePoint(new Point(0, 0), this);
        double lx, ly;
        if (s.XNorm is double xn && s.YNorm is double yn)
        {
            Point winOrigin = PointToScreen(new Point(0, 0));
            double screenDipX = xn * SystemParameters.PrimaryScreenWidth;
            double screenDipY = yn * SystemParameters.PrimaryScreenHeight;
            lx = screenDipX - winOrigin.X / _dpiScaleX - hostOffset.X;
            ly = screenDipY - winOrigin.Y / _dpiScaleY - hostOffset.Y;
        }
        else
        {
            lx = EraserHost.ActualWidth / 2;
            ly = EraserHost.ActualHeight / 2;
        }

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
            Log.Info($"WM_POINTER: 有接触矩形 rcContact={ti.rcContact.Width}x{ti.rcContact.Height} 物理像素 pressure={ti.pressure} touchFlags=0x{ti.touchFlags:X} → 改用 WM_POINTER 作为接触尺寸来源");
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

        // 客户区物理像素 → DIP，再换算到 EraserHost 局部坐标
        double cxDiu = pt.X / _dpiScaleX;
        double cyDiu = pt.Y / _dpiScaleY;
        Point hostOffset = EraserHost.TranslatePoint(new Point(0, 0), this);

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
        RunHidDiagnostics();
        UpdateSourceLabel();

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
        if (_hasPalm) DrawTouchOverlay();
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
            ApplyEdidCalibration();
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
        RefreshDerivedMm();
        UpdateCalibText();
        DrawRuler();
        DrawRulerV();
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

    private void UpdateSliderLabels()
    {
        ThresholdLabel.Text = $"θ = {ThresholdSlider.Value:0} mm";
        CutLabel.Text = $"k = {(int)CutSlider.Value}";
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

    // ================= ③ 触点采集 =================

    private void OnTouchDown(object sender, TouchEventArgs e)
    {
        TouchPoint tp = e.GetTouchPoint(TouchHost);
        AddTouchPoint(tp.Position, tp.Bounds);
    }

    private void OnTouchMove(object sender, TouchEventArgs e)
    {
        TouchPoint tp = e.GetTouchPoint(TouchHost);
        AddTouchPoint(tp.Position, tp.Bounds);
    }

    private void OnTouchUp(object sender, TouchEventArgs e)
    {
        SetStatus($"触点采样：{_touchPoints.Count} 个触点。{DriverContactSummary()}");
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
        SetStatus($"面积擦预览：{DriverContactSummary()}");
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
        _driverSizeWarned = false;
        _sourceState.Clear();
        _activeSource = ContactSource.None;
        EraserOverlay.Children.Clear();
        UpdateSourceLabel();
        EraserInfoText.Text = "已清除。用真触摸屏按一下手掌即可实时显示擦除区。";
        SetStatus("面积擦预览已清除。");
        Log.Info("清除面积擦预览");
    }

    private void OnTouchMouseDown(object sender, MouseButtonEventArgs e)
    {
        AddTouchPoint(e.GetPosition(TouchHost), null);
    }

    private void OnTouchMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            AddTouchPoint(e.GetPosition(TouchHost), null);
    }

    /// <summary>
    /// 记录驱动直接上报的接触矩形（TouchPoint.Bounds，单位 DIP），取面积最大的一次（= 手掌）。
    /// 换算成 mm 后即为"驱动给的接触面积"，正好复用 EDID 标定出的 mm/DIU。
    /// </summary>
    private void RecordDriverContact(Rect bounds)
    {
        if (_mmPerDiuX <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
            return;

        double wMm = bounds.Width * _mmPerDiuX;
        double hMm = bounds.Height * _mmPerDiuY;

        // 与面积擦预览同样的判断：极小占位值（如 0.1×0.1 DIP）视为"驱动未上报"，不记录。
        if (wMm < MinContactMm || hMm < MinContactMm)
            return;

        if (!_hasDriverContact || wMm * hMm > _driverContactWidthMm * _driverContactHeightMm)
        {
            _hasDriverContact = true;
            _driverContactWidthMm = wMm;
            _driverContactHeightMm = hMm;
            Log.Info($"驱动接触(最大): {wMm:0.0}x{hMm:0.0}mm = {wMm * hMm:0}mm² (原始 {bounds.Width:0.0}x{bounds.Height:0.0} DIP)");
        }
    }

    private string DriverContactSummary()
    {
        if (!_hasDriverContact)
            return "驱动接触: 未上报（鼠标，或该驱动/屏幕不提供 Bounds）";
        double area = _driverContactWidthMm * _driverContactHeightMm;
        return $"驱动接触: {_driverContactWidthMm:0.0} × {_driverContactHeightMm:0.0} mm = {area:0} mm²";
    }

    private void AddTouchPoint(Point p, Rect? contactBounds)
    {
        // 按住移动时不逐像素堆点：与已有点最小间距需 > 6mm，防止数据灌爆。
        double minGapDiu = _mmPerDiuX > 0 ? 6.0 / _mmPerDiuX : 12;
        if (_touchPoints.Any(q => Geometry2D.Distance(q, p) < minGapDiu))
            return;

        if (contactBounds is Rect b)
            RecordDriverContact(b);

        _touchPoints.Add(p);
        _hasPalm = false;
        _clusters = null;
        _palmClusters.Clear();
        DrawTouchOverlay();
        TouchInfoText.Text = $"已采样 {_touchPoints.Count} 个触点。\n{DriverContactSummary()}";
    }

    // ---------- 实时接触 -> 擦除区（按多大，擦多大） ----------

    /// <summary>三路接触来源统一入口：按优先级（原始HID &gt; WM_POINTER &gt; WPF）仲裁后驱动预览。</summary>
    private void SubmitContact(ContactSource src, Rect rectDiu, bool applyThreshold, string detail)
    {
        long now = Environment.TickCount64;
        _sourceState[src] = (now, rectDiu);
        _sourceDetail[src] = detail;

        // 选优先级最高、且仍在"活跃"（近 SourceFreshMs 内）的来源
        ContactSource eff = ContactSource.None;
        foreach (ContactSource s in new[] { ContactSource.RawHid, ContactSource.Pointer, ContactSource.Wpf })
        {
            if (_sourceState.TryGetValue(s, out var st) && now - st.Time <= SourceFreshMs)
            {
                eff = s;
                break;
            }
        }
        if (eff == ContactSource.None)
            return;

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

    private static string SourceName(ContactSource s) => s switch
    {
        ContactSource.RawHid => "原始HID(设备上报)",
        ContactSource.Pointer => "WM_POINTER rcContact",
        ContactSource.Wpf => "WPF TouchPoint.Bounds",
        _ => "无",
    };

    private void UpdateSourceLabel()
    {
        if (ContactSourceText is null)
            return;

        long now = Environment.TickCount64;
        var sb = new StringBuilder();
        sb.Append("生效来源: ").Append(SourceName(_activeSource));
        foreach (ContactSource s in new[] { ContactSource.RawHid, ContactSource.Pointer, ContactSource.Wpf })
        {
            bool active = s == _activeSource;
            bool fresh = _sourceState.TryGetValue(s, out var st) && now - st.Time <= SourceFreshMs;
            string val = _sourceDetail.TryGetValue(s, out string? d) ? d : "（无）";
            sb.Append('\n').Append(active ? "▶ " : "   ").Append(SourceName(s)).Append(": ").Append(val);
            if (!fresh && _sourceState.ContainsKey(s)) sb.Append("  (旧)");
        }
        ContactSourceText.Text = sb.ToString();
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
            if (!_driverSizeWarned)
            {
                _driverSizeWarned = true;
                Log.Warn($"驱动未上报有效接触面积：原始 Bounds={b.Width:0.###}x{b.Height:0.###} DIP，面积擦预览不可用（需能上报接触尺寸的数字化器）");
                if (EraserInfoText is not null)
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

        if (_ratioCalibrating && _mmPerDiuX > 0)
        {
            double wMm = _lastContact.Value.Width * _mmPerDiuX;
            double hMm = _lastContact.Value.Height * _mmPerDiuY;
            _eraserRatio = 1.0;
            RatioSlider.Value = 1.0;
            _ratioCalibrating = false;
            SetStatus($"倍率已标定并锁定：基准接触 {wMm:0.0} × {hMm:0.0} mm，倍率 1.00×（此后严格等比、稳定）。");
            Log.Info($"倍率标定: 基准接触 {wMm:0.0}x{hMm:0.0}mm, 倍率锁定 1.00x");
        }

        DrawLiveEraser();
        UpdateEraserInfo();
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
        double areaMm2 = wMm * hMm * _eraserRatio * _eraserRatio;
        Log.Info($"接触(滤波): {wMm:0.0}x{hMm:0.0}mm 倍率={_eraserRatio:0.00} 擦除区面积={areaMm2:0}mm² 形状={(_shapeIsCircle ? "正圆" : "矩形")}");
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
        RatioLabel.Text = $"倍率 = {_eraserRatio:0.00} ×";
        if (EraserOverlay is not null)
        {
            DrawLiveEraser();
            UpdateEraserInfo();
        }
    }

    private void OnAutoCalibrateRatio(object sender, RoutedEventArgs e)
    {
        _ratioCalibrating = true;
        SetStatus("倍率标定中：请到「② 触按采集」页用手掌按一次…");
        EraserInfoText.Text = "标定中…请在「② 触按采集」页用手掌按一次，程序会用该次稳定接触作为基准。";
    }

    private void DrawLiveEraser()
    {
        if (EraserOverlay is null)
            return;
        EraserOverlay.Children.Clear();
        if (_lastContact is not Rect c)
            return;

        double ratio = _eraserRatio;
        double cx = c.X + c.Width / 2;
        double cy = c.Y + c.Height / 2;

        if (_shapeIsCircle)
        {
            // 等面积圆：直径 = √(4·W·H/π)，保证"圆面积 = 接触面积"
            double d = Math.Sqrt(4 * c.Width * c.Height / Math.PI) * ratio;
            if (!double.IsFinite(d) || d <= 0) return;
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
            double w = c.Width * ratio;
            double h = c.Height * ratio;
            if (!double.IsFinite(w) || !double.IsFinite(h) || w <= 0 || h <= 0) return;
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
        double areaMm2 = inW * inH * _eraserRatio * _eraserRatio;

        string shape = _shapeIsCircle
            ? $"等面积圆 ⌀{2 * Math.Sqrt(areaMm2 / Math.PI):0.0} mm"
            : $"矩形 {inW * _eraserRatio:0.0} × {inH * _eraserRatio:0.0} mm";

        EraserInfoText.Text =
            $"接触(中值滤波): {inW:0.0} × {inH:0.0} mm = {inW * inH:0} mm²\n" +
            $"倍率: {_eraserRatio:0.00} ×（固定，保证等比）\n" +
            $"擦除区: {shape}，面积 {areaMm2:0} mm²";
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

    private void OnClearTouch(object sender, RoutedEventArgs e)
    {
        _touchPoints.Clear();
        _clusters = null;
        _palmClusters.Clear();
        _hasPalm = false;
        _hasDriverContact = false;
        _driverContactWidthMm = 0;
        _driverContactHeightMm = 0;
        TouchOverlay.Children.Clear();
        TouchInfoText.Text = "已清除。请重新采样触点或点「生成模拟触点」。";
        ResultText.Text = "";
        SetStatus("触点已清除。");
        Log.Info("清除触点");
    }

    private void OnGenerateSynthetic(object sender, RoutedEventArgs e)
    {
        if (_mmPerDiuX <= 0)
        {
            SetStatus("未校准，无法按毫米生成模拟触点，请先校准。");
            return;
        }

        double mm = _mmPerDiuX;
        var rnd = new Random(7);
        var pts = new List<Point>();
        var center = new Point(TouchHost.ActualWidth / 2, TouchHost.ActualHeight * 0.62);
        if (center.X < 100 || center.Y < 100)
            center = new Point(400, 320);

        // 掌心：致密椭圆点阵（间距 6 mm，保证 θ<10mm 时聚成一簇）
        for (double y = -32; y <= 32; y += 6)
        {
            for (double x = -38; x <= 38; x += 6)
            {
                if (x * x / (38.0 * 38.0) + y * y / (32.0 * 32.0) <= 1.0)
                {
                    double jx = x + (rnd.NextDouble() * 3 - 1.5);
                    double jy = y + (rnd.NextDouble() * 3 - 1.5);
                    pts.Add(new Point(center.X + jx / mm, center.Y + jy / mm));
                }
            }
        }

        // 4 根手指：掌心上方，簇间距 18 mm（> θ，故各自成簇）
        for (int f = 0; f < 4; f++)
        {
            double fx = -27 + f * 18;
            double fy = -48 - rnd.NextDouble() * 3;
            for (int k = 0; k < 3; k++)
            {
                pts.Add(new Point(
                    center.X + (fx + rnd.Next(-3, 4)) / mm,
                    center.Y + (fy + rnd.Next(-3, 4)) / mm));
            }
        }

        // 笔尖：右侧远处
        pts.Add(new Point(center.X + 72 / mm, center.Y - 18 / mm));

        _touchPoints.Clear();
        _touchPoints.AddRange(pts);
        _clusters = null;
        _palmClusters.Clear();
        _hasPalm = false;
        _hasDriverContact = false;
        _driverContactWidthMm = 0;
        _driverContactHeightMm = 0;
        DrawTouchOverlay();
        TouchInfoText.Text = $"已生成模拟触点 {_touchPoints.Count} 个（掌心 1 + 手指 4 + 笔尖 1 组）。\n" +
                             "模拟数据没有驱动 Bounds，故「驱动接触」显示未上报。";
        SetStatus("模拟触点已生成，点「计算遮挡半径」运行聚类 + MST。");
        Log.Info($"生成模拟触点 {_touchPoints.Count} 个");
    }

    private void OnComputePalm(object sender, RoutedEventArgs e)
    {
        if (_touchPoints.Count < 2)
        {
            SetStatus("触点太少，请先采样或生成模拟触点。");
            return;
        }
        if (_mmPerDiuX <= 0)
        {
            SetStatus("未校准，无法换算物理阈值。");
            return;
        }

        double thresholdDiu = ThresholdSlider.Value / _mmPerDiuX;
        int cutCount = (int)CutSlider.Value;

        var clusters = Geometry2D.ClusterByDistance(_touchPoints, thresholdDiu);
        var centers = clusters.Select(Geometry2D.Centroid).ToList();
        var weights = clusters.Select(c => c.Count).ToList();
        List<int> palmIdx = Geometry2D.LargestComponentAfterCuts(centers, weights, cutCount);

        var palmPts = new List<Point>();
        foreach (int ci in palmIdx)
            palmPts.AddRange(clusters[ci]);

        var (cCenter, rDiu) = Geometry2D.MinEnclosingCircle(palmPts);

        _clusters = clusters;
        _palmClusters.Clear();
        foreach (int ci in palmIdx)
            _palmClusters.Add(ci);
        _hasPalm = true;
        _palmCenterDiu = cCenter;
        _palmRadiusMm = rDiu * _mmPerDiuX;

        DrawTouchOverlay();

        TouchInfoText.Text =
            $"触点: {_touchPoints.Count}   簇: {clusters.Count}\n" +
            $"θ = {ThresholdSlider.Value:0} mm, k = {cutCount}\n" +
            $"手掌(含手指)簇: {palmIdx.Count}/{clusters.Count} 簇、{palmPts.Count} 触点（触点最多连通分量）";

        double diameterMm = _palmRadiusMm * 2;
        Log.Info($"遮挡半径: 触点={_touchPoints.Count} 簇={clusters.Count} θ={ThresholdSlider.Value:0}mm k={cutCount} 手掌簇={palmIdx.Count}/{clusters.Count} 手掌触点={palmPts.Count} r={_palmRadiusMm:0.0}mm 直径={diameterMm:0.0}mm");
        Log.Info("  对照 " + DriverContactSummary());
        ResultText.Text =
            $"【③ 遮挡半径】\n" +
            $"手掌(含手指)触点: {palmPts.Count} 个\n" +
            $"最小覆盖圆半径 r ≈ {_palmRadiusMm:0.0} mm（直径 {diameterMm:0.0} mm）\n" +
            $"渲染直径: {2 * rDiu:0} DIU\n" +
            $"—— 对照 ——\n" +
            $"{DriverContactSummary()}\n" +
            $"（聚类 → MST 切 k 条最长边（去掉笔尖等离群点）→ 触发点最多的连通分量 → Welzl 最小覆盖圆）";
    }

    // ================= 触按层渲染 =================

    private void DrawTouchOverlay()
    {
        TouchOverlay.Children.Clear();

        if (_clusters is null)
        {
            foreach (Point p in _touchPoints)
                TouchOverlay.Children.Add(Dot(p, 3, Color.FromRgb(0xAA, 0xAA, 0xAA)));
            return;
        }

        for (int ci = 0; ci < _clusters.Count; ci++)
        {
            bool isPalm = _palmClusters.Contains(ci);
            Color color = isPalm ? Color.FromRgb(0xFF, 0x44, 0x44) : ClusterPalette[ci % ClusterPalette.Length];
            foreach (Point p in _clusters[ci])
                TouchOverlay.Children.Add(Dot(p, isPalm ? 4 : 3, color));
        }

        if (!_hasPalm || _mmPerDiuX <= 0)
            return;

        // 用「物理毫米」换算成 DIU 直径，DPI 改变时尺寸随之修正。
        double rDiu = _palmRadiusMm / _mmPerDiuX;
        if (!double.IsFinite(rDiu) || rDiu <= 0)
            return; // 半径非法就不画，避免给 Ellipse.Width 赋负值/NaN 而崩溃
        var ring = new Ellipse
        {
            Width = 2 * rDiu,
            Height = 2 * rDiu,
            Stroke = Brushes.Red,
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0x44, 0x44)),
        };
        TouchOverlay.Children.Add(ring);
        Canvas.SetLeft(ring, _palmCenterDiu.X - rDiu);
        Canvas.SetTop(ring, _palmCenterDiu.Y - rDiu);

        var cross = new Ellipse { Width = 8, Height = 8, Fill = Brushes.Yellow };
        TouchOverlay.Children.Add(cross);
        Canvas.SetLeft(cross, _palmCenterDiu.X - 4);
        Canvas.SetTop(cross, _palmCenterDiu.Y - 4);
    }

    private static Ellipse Dot(Point p, double radiusDiu, Color color)
    {
        var e = new Ellipse
        {
            Width = radiusDiu * 2,
            Height = radiusDiu * 2,
            Fill = new SolidColorBrush(color),
        };
        Canvas.SetLeft(e, p.X - radiusDiu);
        Canvas.SetTop(e, p.Y - radiusDiu);
        return e;
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
            "2) 描一圈手掌轮廓 → 算面积\n" +
            "3) 放一次手掌 → 算遮挡半径\n" +
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
            "白区会出现蓝色虚线（你描的圈）和红色实线（凸包），右侧会同时给出两个面积。",
            () => ComputeAreaButton),

        new GuideStep("第 3 步 · 切到「触按采集」页",
            "接下来切到「② 触按采集」页，准备采集手掌触点。",
            () => StageTabs, () => StageTabs.SelectedIndex = 1),

        new GuideStep("第 3 步 · 采集触点",
            "有触摸屏就把手掌（含手指、笔尖）自然按上去一次——程序会同时读取\n" +
            "① 触点坐标（用于聚类）\n" +
            "② 驱动直接上报的接触矩形 TouchPoint.Bounds（换算成 mm² 的接触面积）\n" +
            "没有触摸屏（大概率）就直接点「生成模拟触点」。",
            () => GenerateButton),

        new GuideStep("第 3 步 · 计算遮挡半径",
            "点这个按钮，黑区会按簇分色显示触点。\n" +
            "红色圆就是算出的遮挡半径，它覆盖「整只手（掌心 + 手指）」，黄点是圆心。",
            () => ComputePalmButton),

        new GuideStep("第 3 步 · 调参（可选）",
            "结果不对就调这两个滑块再算一次：\n" +
            "θ 控制多近的触点算同一簇；\n" +
            "k 默认 1，只切掉笔尖等离群触点、保留整只手；\n" +
            "若你只想取「掌心」不要手指，把 k 调大即可把手指簇也切掉。",
            () => ThresholdSlider),

        new GuideStep("第 4 步 · 面积擦预览（第 ③ 页，需真触摸屏）",
            "切到新建的「③ 面积擦预览」页，把手掌按在黑区上：\n" +
            "程序读驱动上报的接触矩形，用「固定倍率 + 中值滤波」画出稳定、等比的擦除区——按多大，擦多大。\n" +
            "不抬手也会刷新：改变按压轻重/手的角度，接触面积跟着变，擦除区实时跟着变（能否变取决于驱动是否上报）。\n" +
            "默认矩形，可切正圆（等面积）；点「自动标定倍率」可用手掌一次锁定基准。",
            () => RatioSlider, () => StageTabs.SelectedIndex = 2),

        new GuideStep("完成",
            "至此四件事都齐了：手掌面积(cm²)、遮挡半径(mm)、按物理毫米渲染的遮挡圆、\n" +
            "以及第 ③ 页「按多大、擦多大」的实时面积擦预览。\n" +
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

    private void OnHidDiagnostics(object sender, RoutedEventArgs e) => RunHidDiagnostics();

    private void RunHidDiagnostics()
    {
        // 方案一（主）：Raw Input + HidP_* 直接读 HID 能力，不打开设备
        List<RawHidScan.HidTouchInfo> hid = RawHidScan.Scan();
        Log.Info($"RawInput HID 扫描: {hid.Count} 个 HID 设备");
        foreach (RawHidScan.HidTouchInfo h in hid)
            Log.Info($"HID(raw): {h.DeviceName} | 触摸屏={h.IsTouchScreen} | Width(0x48)={h.HasWidth} Height(0x49)={h.HasHeight} | 值用法数={h.InputValueCaps} | 用法: {h.UsageSummary}");

        List<RawHidScan.HidTouchInfo> touchHid = hid.Where(h => h.IsTouchScreen).ToList();
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

        // 方案二（辅）：接口 + IOCTL / 注册表兜底
        List<HidDescriptor.HidDeviceReport> reports = HidDescriptor.Enumerate();
        Log.Info($"HID 诊断: 枚举到 {HidDescriptor.LastEnumeratedCount} 个 HID 设备，其中 {reports.Count} 个读到描述符（来源: {HidDescriptor.LastSource}）");
        foreach (string d in HidDescriptor.Diagnostics)
            Log.Info("HID 诊断细节: " + d);

        List<HidDescriptor.HidDeviceReport> touch = reports.Where(r => r.IsTouchScreen).ToList();
        foreach (HidDescriptor.HidDeviceReport r in reports)
            Log.Info($"HID: {r.Name} | 描述符 {r.DescriptorLength}B | 触摸屏={r.IsTouchScreen} | Width(0x48)={r.HasWidth} Height(0x49)={r.HasHeight}");

        if (touch.Count == 0)
        {
            Log.Warn("HID 诊断: 未发现触摸屏 HID 设备（Digitizer 0x0D / Touch Screen 0x04）");
        }
        else
        {
            foreach (HidDescriptor.HidDeviceReport t in touch)
            {
                if (t.HasWidth && t.HasHeight)
                    Log.Info($"HID 结论: 触摸屏『{t.Name}』声明了 Width/Height → 可再加原始 HID 解码拿到接触尺寸");
                else
                    Log.Warn($"HID 结论: 触摸屏『{t.Name}』未声明 Width/Height → 设备不上报接触尺寸，软件层无法获取");
            }
        }

        try
        {
            if (!string.IsNullOrEmpty(Log.LogDirectory))
            {
                string path = System.IO.Path.Combine(Log.LogDirectory, "hid_descriptors.txt");
                var sb = new StringBuilder();
                foreach (HidDescriptor.HidDeviceReport r in reports)
                    sb.AppendLine($"== {r.Name} | {r.DescriptorLength}B | touch={r.IsTouchScreen} w={r.HasWidth} h={r.HasHeight}")
                      .AppendLine(r.HexDump).AppendLine();
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                Log.Info($"HID 描述符已导出: {path}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("导出 HID 描述符失败: " + ex.Message);
        }

        if (touch.Count > 0 && EraserInfoText is not null)
        {
            HidDescriptor.HidDeviceReport t = touch[0];
            EraserInfoText.Text = t.HasWidth && t.HasHeight
                ? $"HID 诊断：触摸屏声明了 Width/Height（{t.DescriptorLength}B）→ 可尝试原始 HID 解码。"
                : $"HID 诊断：触摸屏未声明 Width/Height（{t.DescriptorLength}B）→ 设备不上报接触尺寸。";
        }
    }

    private void SetStatus(string text) => StatusText.Text = text;
}
