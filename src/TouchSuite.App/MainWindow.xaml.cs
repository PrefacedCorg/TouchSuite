using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using TouchSuite.App.Eraser;

namespace TouchSuite.App;

public partial class MainWindow : Window
{
    private static readonly string[] StepTitles =
    {
        "第 1 步 · 检查横向 10cm",
        "第 2 步 · 检查纵向 10cm",
        "第 3 步 · 确定推算方式",
        "第 4 步 · 手掌尺寸",
        "第 5 步 · 手掌按一下",
        "第 6 步 · 手指按一下",
        "第 7 步 · 结果",
    };

    private readonly TouchInput _touch = new();
    private CalibrationResult _result = new();

    private ScreenCalibration? _calib;
    private EdidSize _edid = new(0, 0);
    private string _sizeSource = "EDID";   // EDID / 对角线
    private bool _edidFailed;              // EDID 读取失败（退回 96dpi 假设）

    // 手画 10cm 标定
    private bool _drawing;
    private Point _drawStart, _drawEnd;
    private bool _hasDrawn;
    private bool _drawnIsHorizontal;
    private double _drawnLenDiu;
    private double _drawnMmPerPx;          // 画线推得的 mm/物理像素（未含微调）
    private Line? _userDrawLine, _progLine;

    // 手掌擦预览（整套在 Eraser/EraserPreviewPage + EraserEngine 里）
    private readonly List<EdidSize> _edidCandidates = new();
    private bool _suppressEdidCombo;
    private int _step;

    private double _dpiScaleX = 1, _dpiScaleY = 1;
    private double _mmPerDiuX, _mmPerDiuY;

    // 描摹包围盒（DIU，相对 TraceCanvas）
    private double _bx0, _by0, _bx1, _by1;
    private bool _hasBox;
    private double _traceAreaPx2;      // a3 描摹凹面积（沿外轮廓一笔围出的多边形面积，物理像素²）

    private double? _palmPeakAreaPx2, _palmPeakPressure;
    private double? _fingerPeakAreaPx2, _fingerPeakPressure;

    // 多次按压取中值
    private readonly List<double> _palmAreas = new();
    private readonly List<double> _palmPressures = new();
    private readonly List<double> _fingerAreas = new();
    private readonly List<double> _fingerPressures = new();

    private long _lastSampleLogTicks;   // 样本日志节流：≥250ms 才记一行，避免刷屏
    private long _lastInfoBarTicks;     // 信息栏刷新节流（实时压感 60Hz，节流后才看得清）
    private double? _liveAreaPx2;       // 手掌/手指实时接触面积（物理像素²）
    private double? _livePressure;      // 手掌/手指实时压感（来自 WPF Stylus）
    private long _lastPressTicks;       // 上一次收到样本的时刻（判"抬手"用）
    private const long PressGapMs = 350;   // 静默超过此时长视为抬手，下一次样本算新的一次按压

    // 原始HID（RawInput）：设备上报的接触尺寸→面积 + 压感；映射表只读展示
    private IntPtr _hwnd;
    private List<HidReader.HidDeviceInfo> _hidDevices = new();
    private long _lastHidLogTicks;
    private long _lastHidTicks;         // 最近一次有效原始HID 样本时刻（HID 新鲜时忽略 WPF 面积，避免打架）

    // 来源下拉
    private bool _suppressSourceUi;     // 同步多个下拉时抑制 SelectionChanged 回环
    private string _palmActiveSource = "";     // 手掌当前实际生效的原始来源
    private string _fingerActiveSource = "";   // 手指当前实际生效的原始来源

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += (_, _) => _touch.Dispose();

        // 「保存结果到文件」按钮在预览页里（EraserPreviewPage.xaml），宿主只负责落盘
        EraserPage.SaveRequested += OnSaveRequested;

        // 预览页的来源下拉改动 → 同步本窗口第 5/6 步的两个下拉
        EraserPage.SourceSelectionChanged += SyncSourceUi;

        // 预览页改了手掌面积公式（三选一）→ 重算手掌像素面积与信息栏
        EraserPage.SettingsChanged += RecomputePalmSize;

        // 默认两项都判"准"（对应界面 RadioButton 的默认选中），首次标尺即按 EDID 原样
        _result.WidthRulerOk = true;
        _result.HeightRulerOk = true;

        TraceCanvas.DefaultDrawingAttributes = new DrawingAttributes
        {
            Width = 3,
            Height = 3,
            Color = Color.FromRgb(0x22, 0x66, 0xCC),
            FitToCurve = true,
        };
    }

    // ================= 原始HID（RawInput）=================

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        if (PresentationSource.FromVisual(this) is HwndSource src)
            src.AddHook(WndProc);

        _hidDevices = HidReader.Scan();
        bool ok = HidReader.Register(_hwnd);
        Log.Info($"原始HID 触摸注册 = {ok}；HID 设备 {_hidDevices.Count} 个（触摸类 {_hidDevices.Count(d => d.IsTouchScreen)} 个）");
        foreach (HidReader.HidDeviceInfo d in _hidDevices.Where(d => d.IsTouchScreen))
            Log.Info($"HID(raw): {d.DeviceName} | W={d.HasWidth} H={d.HasHeight} P={d.HasPressure} | 宽:{d.WidthDetail} 高:{d.HeightDetail} 压感:{d.PressureDetail} | {d.XYDetail}");
    }

    private const int WM_INPUT = 0x00FF;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_INPUT)
        {
            HidReader.WmInputResult r = HidReader.TryHandleWmInput(lParam, out HidReader.RawTouchSample? sample);

            // 句柄不在能力表（设备中途重枚举）→ 节流重扫一次再解
            if (r == HidReader.WmInputResult.UnknownDevice && HidReader.TryBeginAutoRescan())
            {
                _hidDevices = HidReader.Scan();
                HidReader.TryHandleWmInput(lParam, out sample);
            }

            if (sample is not null)
                HandleHidSample(sample);
        }
        return IntPtr.Zero;
    }

    /// <summary>原始HID 样本：只取「接触尺寸→面积」与「压感」，封装成 TouchSample 走同一条处理链。</summary>
    private void HandleHidSample(HidReader.RawTouchSample s)
    {
        if (s.WidthMm is not double w || s.HeightMm is not double h)
            return;   // 没尺寸的帧不带面积（空槽位/占位值）

        long now = Environment.TickCount64;
        _lastHidTicks = now;
        if (now - _lastHidLogTicks >= 100)
        {
            _lastHidLogTicks = now;
            Log.Info($"原始HID: 设备={ShortName(s.DeviceName)} W={s.WidthLogical}/{s.WidthLogMax}({Precision.Fmt(w)}mm) H={s.HeightLogical}/{s.HeightLogMax}({Precision.Fmt(h)}mm)"
                 + $" X={s.XLogical}/{s.XLogMax}({Precision.Fmt(s.XNorm, 4)}) Y={s.YLogical}/{s.YLogMax}({Precision.Fmt(s.YNorm, 4)})"
                 + $" 压感={Precision.Fmt(s.Pressure01, 3)} raw=[{s.Hex}]");
        }

        OnSample(new TouchSample("RawHID", s.WidthMm, s.HeightMm, s.Pressure01,
            Detail: $"W={s.WidthLogical}/{s.WidthLogMax} H={s.HeightLogical}/{s.HeightLogMax} raw=[{s.Hex}]",
            WidthLogical: s.WidthLogical, HeightLogical: s.HeightLogical,
            XNorm: s.XNorm, YNorm: s.YNorm,
            XLogMax: s.XLogMax, YLogMax: s.YLogMax,
            WidthLogMax: s.WidthLogMax, HeightLogMax: s.HeightLogMax));
    }

    /// <summary>原始HID 设备换算表（映射表）文字，只读展示。</summary>
    private string HidTableText()
    {
        List<HidReader.HidDeviceInfo> touch = _hidDevices.Where(d => d.IsTouchScreen).ToList();
        if (touch.Count == 0)
            return "原始HID 映射表：（未发现触摸类 HID 设备）";
        return "原始HID 映射表：" + string.Join("",
            touch.Select(d => $"\n  {ShortName(d.DeviceName)}：宽 [{d.WidthDetail}]；高 [{d.HeightDetail}]；压感 [{d.PressureDetail}]；X/Y [{d.XYDetail}]"));
    }

    private static string ShortName(string path)
    {
        string[] parts = path.Split('#');
        return parts.Length >= 3 ? $"{parts[1]} #{parts[2]}" : path;
    }

    // ================= 来源下拉 =================

    private void OnPalmSourceModeChanged(object sender, SelectionChangedEventArgs e)
        => ApplySourceUi(((ComboBox)sender).SelectedIndex, null);

    private void OnFingerSourceModeChanged(object sender, SelectionChangedEventArgs e)
        => ApplySourceUi(((ComboBox)sender).SelectedIndex, null);

    private void OnPalmHidMmChanged(object sender, SelectionChangedEventArgs e)
        => ApplySourceUi(null, ((ComboBox)sender).SelectedIndex);

    private void OnFingerHidMmChanged(object sender, SelectionChangedEventArgs e)
        => ApplySourceUi(null, ((ComboBox)sender).SelectedIndex);

    /// <summary>把某个下拉的改动落实到引擎，再把所有下拉同步成引擎当前状态。</summary>
    private void ApplySourceUi(int? modeIndex, int? mmIndex)
    {
        if (_suppressSourceUi || !IsLoaded || EraserPage is null)
            return;
        if (modeIndex is int mi)
            EraserPage.Engine.SetMode((SourceMode)Math.Clamp(mi, 0, 2));
        if (mmIndex is int hi)
            EraserPage.Engine.SetHidSizeSource((HidSizeSource)Math.Clamp(hi, 0, 1));
        SyncSourceUi();
    }

    /// <summary>按引擎当前来源状态刷新三处下拉与 HID 尺寸取法面板可见性。</summary>
    private void SyncSourceUi()
    {
        if (PalmSourceCombo is null || EraserPage is null)
            return;

        SourceMode mode = EraserPage.Engine.Mode;
        HidSizeSource mm = EraserPage.Engine.HidSizeSource;
        Visibility hidVis = mode == SourceMode.RawHid ? Visibility.Visible : Visibility.Collapsed;

        _suppressSourceUi = true;
        if (FingerSourceCombo is not null) FingerSourceCombo.SelectedIndex = (int)mode;
        PalmSourceCombo.SelectedIndex = (int)mode;
        if (PalmHidMmCombo is not null) PalmHidMmCombo.SelectedIndex = (int)mm;
        if (FingerHidMmCombo is not null) FingerHidMmCombo.SelectedIndex = (int)mm;
        if (PalmHidMmPanel is not null) PalmHidMmPanel.Visibility = hidVis;
        if (FingerHidMmPanel is not null) FingerHidMmPanel.Visibility = hidVis;
        _suppressSourceUi = false;

        EraserPage.SyncSourceSelection(mode, mm);
        UpdateSourceInfoText();
    }

    /// <summary>第 5/6 步「当前生效」：显示当前实际生效的来源。</summary>
    private void UpdateSourceInfoText()
    {
        if (PalmSourceInfoText is not null)
            PalmSourceInfoText.Text = "当前生效：" + ActiveLabel(_palmActiveSource);
        if (FingerSourceInfoText is not null)
            FingerSourceInfoText.Text = "当前生效：" + ActiveLabel(_fingerActiveSource);
    }

    private static string ActiveLabel(string src) => src switch
    {
        "RawHID" => SourceNames.Of(ContactSource.RawHid),
        "WPF" or "STYLUS" => SourceNames.Of(ContactSource.Wpf),
        _ => "—",
    };

    // ================= 生命周期 =================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateDpi();

        var (rx, ry) = EdidReader.PrimaryPixels();
        _result.ResX = rx;
        _result.ResY = ry;

        _edidCandidates.Clear();
        _edidCandidates.AddRange(EdidReader.ReadAll());

        EdidSize? best = EdidReader.PickBest(_edidCandidates, rx, ry);
        if (best is null)
        {
            // 读不到 EDID：按 96dpi 假设一个（仅供流程走通，标尺不可信）
            best = new EdidSize((int)Math.Round(rx / 96.0 * 25.4), (int)Math.Round(ry / 96.0 * 25.4));
            _edidCandidates.Add(best);
            _edidFailed = true;
            SetStatus("EDID 读取失败：已按 96dpi 假设尺寸（标尺不可信，请在第 3 步填对角线）。");
        }
        _edid = best;

        if (_edidCandidates.Count > 1)
            Log.Info($"EDID 候选 {_edidCandidates.Count} 块: {string.Join(" / ", _edidCandidates.Select(c => $"{c.WidthMm}×{c.HeightMm}mm"))}");
        Log.Info($"EDID 选用 {_edid.WidthMm}×{_edid.HeightMm}mm；主屏分辨率 {rx}×{ry}；读取失败={_edidFailed}");

        // 候选下拉（>1 块时显示，便于手动纠正挑错的那块）
        _suppressEdidCombo = true;
        EdidCombo.Items.Clear();
        foreach (EdidSize c in _edidCandidates)
            EdidCombo.Items.Add($"{c.WidthMm} × {c.HeightMm} mm");
        EdidCombo.SelectedIndex = Math.Max(0, _edidCandidates.IndexOf(_edid));
        _suppressEdidCombo = false;
        EdidPickPanel.Visibility = _edidCandidates.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        RebuildCalibration();
        RecomputePalmSize();   // 默认 120×180px 也先算一遍，信息栏/预览页立刻有值

        _touch.Sample += OnSample;
        _touch.Attach(this);
        _touch.AttachFallback(PalmHost);
        _touch.AttachFallback(FingerHost);
        _touch.AttachFallback(EraserPage.HostElement);

        // 面积擦预览：状态回显到底部状态栏
        EraserPage.Status += SetStatus;

        _result.DeviceName = "WPF + 原始HID";
        Log.Info("接触来源：原始HID（RawInput WM_INPUT：W×H 面积 + TipPressure 压感）+ WPF TouchPoint.Bounds/Stylus 兜底");
        SetStatus("触摸来源：可在第 5/6 步下拉里选「自适应 / 原始HID / 软件WPF」；HID 还可选尺寸取法（逻辑量程→屏幕分辨率 / 物理量程换算）");

        SyncSourceUi();

        // 启动时若存在上次保存的标定，询问是否载入（载入则直接跳到结果页）
        if (!TryLoadSaved())
            ShowStep(0);
    }

    /// <summary>启动时若已保存过标定，弹窗询问是否载入；载入成功则跳到结果页并返回 true。</summary>
    private bool TryLoadSaved()
    {
        string path = CalibrationResult.DefaultPath();
        if (!File.Exists(path))
            return false;

        CalibrationResult? saved = CalibrationResult.Load(path);
        string stamp = saved?.Timestamp is { Length: > 0 } ts ? ts : "（无时间戳）";
        MessageBoxResult answer = MessageBox.Show(
            $"检测到上次保存的标定：\n{path}\n保存时间：{stamp}\n\n是否载入？\n\n【是】载入并直接跳到结果页（可查看面积擦预览）\n【否】从第 1 步重新标定",
            "TouchSuite 校准向导 · 载入标定", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            Log.Info("启动：用户选择重新标定（不载入)");
            return false;
        }

        if (saved is null)
        {
            MessageBox.Show("载入失败：文件损坏或格式不正确。", "TouchSuite 校准向导",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            Log.Warn($"启动载入失败（解析错误）: {path}");
            return false;
        }

        ApplyLoaded(saved);
        Log.Info($"启动载入已保存标定: {path}（{stamp}）");
        ShowStep(6);
        SetStatus("已载入上次保存的标定；要重来请点「上一步」回到前面的步骤，或直接在本页查看预览。");
        return true;
    }

    /// <summary>把载入的标定灌回界面与校准状态（物理尺寸 / 手掌尺寸 / 阈值 / 微调）。</summary>
    private void ApplyLoaded(CalibrationResult saved)
    {
        _result = saved;
        _sizeSource = saved.SizeSource;
        _edid = new EdidSize(saved.EdidWidthMm, saved.EdidHeightMm);
        if (!_edidCandidates.Contains(_edid))
            _edidCandidates.Add(_edid);

        if (saved.PalmWidthPx > 0) PalmWidthInput.Text = saved.PalmWidthPx.ToString("0.###");
        if (saved.PalmHeightPx > 0) PalmHeightInput.Text = saved.PalmHeightPx.ToString("0.###");
        _traceAreaPx2 = saved.PalmTraceAreaPx2;   // a3 已存；描摹线本身不持久化

        // 建立标尺用的 ScreenCalibration，然后用保存的 mm/px 覆盖（对角线 / 手画尺子时推不出来）
        RebuildCalibration();
        if (saved.MmPerPxX > 0 && saved.MmPerPxY > 0)
        {
            _result.MmPerPxX = saved.MmPerPxX;
            _result.MmPerPxY = saved.MmPerPxY;
            _mmPerDiuX = saved.MmPerPxX * _dpiScaleX;
            _mmPerDiuY = saved.MmPerPxY * _dpiScaleY;
        }

        // 恢复预览页的开关/滑块/形状/长宽比/面积公式（老版本 JSON 无这些字段时保持默认）
        EraserPage.ApplySettings(saved);
    }

    private void UpdateDpi()
    {
        PresentationSource? src = PresentationSource.FromVisual(this);
        Matrix m = src?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        _dpiScaleX = m.M11 > 0 ? m.M11 : 1;
        _dpiScaleY = m.M22 > 0 ? m.M22 : 1;
    }

    /// <summary>按当前答案重建校准（推算方式 + mm/px + mm/DIU），并重画标尺。</summary>
    private void RebuildCalibration()
    {
        // 手填对角线 / 手画尺子时按方形像素推，推算方式就不起作用了
        SizeMode mode = _sizeSource == "EDID"
            ? ModeFrom(_result.WidthRulerOk, _result.HeightRulerOk)
            : SizeMode.UseEdid;
        _calib = new ScreenCalibration(_edid, _result.ResX, _result.ResY, mode);

        _result.SizeSource = _sizeSource;
        _result.EdidWidthMm = _edid.WidthMm;
        _result.EdidHeightMm = _edid.HeightMm;
        _result.SizeMode = _sizeSource == "EDID" ? _calib.ModeName : $"{_sizeSource}（方形像素）";
        _result.MmPerPxX = _calib.MmPerPxX;
        _result.MmPerPxY = _calib.MmPerPxY;

        _mmPerDiuX = _calib.MmPerPxX * _dpiScaleX;
        _mmPerDiuY = _calib.MmPerPxY * _dpiScaleY;

        _touch.MmPerDiuX = _mmPerDiuX;
        _touch.MmPerDiuY = _mmPerDiuY;

        string head = _sizeSource switch
        {
            "对角线" => $"手填对角线 {_result.DiagonalInch:0.#} 英寸 → 物理 {_edid.WidthMm} × {_edid.HeightMm} mm（方形像素）",
            "尺子" => $"屏幕画的 10cm 标定 → 物理 {_edid.WidthMm} × {_edid.HeightMm} mm（方形像素）",
            _ => _calib.ModeName,
        };
        ModeText.Text = $"{head}\n物理: {_calib.ScreenWidthMm:0.0} × {_calib.ScreenHeightMm:0.0} mm    " +
                        $"分辨率: {_calib.ResX} × {_calib.ResY}\n" +
                        $"mm/px: {Precision.Fmt(_calib.MmPerPxX)} × {Precision.Fmt(_calib.MmPerPxY)}";

        DrawRulers();
        RefreshInfoBar();
        UpdateDiagonalPanelVisibility();
        SyncEraserPage();
        Log.Info($"重建校准: 来源={_sizeSource} 方式={_result.SizeMode} 物理={_calib.ScreenWidthMm:0.0}×{_calib.ScreenHeightMm:0.0}mm "
                 + $"mm/px={Precision.Fmt(_calib.MmPerPxX)}×{Precision.Fmt(_calib.MmPerPxY)} 横10cm准={_result.WidthRulerOk} 竖10cm准={_result.HeightRulerOk}");
    }

    /// <summary>高精度模式开关：后端取整策略 + 显示有效位；切换后立即全量刷新。</summary>
    private void OnHighPrecisionChanged(object sender, RoutedEventArgs e)
    {
        // XAML 解析期 IsChecked="True" 会提前触发，此时后面的控件还没连上 → 忽略
        if (HighPrecisionCheck is null || EraserPage is null || PalmWidthInput is null)
            return;

        Precision.High = HighPrecisionCheck.IsChecked == true;
        Log.Info($"高精度模式 = {Precision.High}");

        RecomputePalmSize();
        RefreshInfoBar();
        EraserPage.Engine.NotifyChanged();
        SetStatus(Precision.High
            ? "高精度模式：已开启（后端双精度、不取整；显示按有效位自适应）"
            : "高精度模式：已关闭（后端取整、显示固定 1 位小数）");
    }

    /// <summary>只在「横竖都不准」或「EDID 读失败」时才露出对角线兜底输入。</summary>
    private void UpdateDiagonalPanelVisibility()
    {
        if (DiagonalPanel is null)
            return;

        bool bothBad = !_result.WidthRulerOk && !_result.HeightRulerOk;
        DiagonalPanel.Visibility = (_edidFailed || bothBad) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>EDID 不可信（都不准 / 读失败）时：手填对角线英寸反推物理尺寸（方形像素，按分辨率比例）。</summary>
    private void OnApplyDiagonal(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(DiagonalInput.Text.Trim(), out double inch) || inch < 2 || inch > 120)
        {
            SetStatus("对角线请填 2~120 英寸。");
            return;
        }

        _edid = SizeFromDiagonal(inch, _result.ResX, _result.ResY);
        _sizeSource = "对角线";
        _result.DiagonalInch = inch;
        Log.Info($"手填对角线 {inch:0.#} 英寸 → {_edid.WidthMm}×{_edid.HeightMm}mm（方形像素）");
        RebuildCalibration();
        SetStatus($"已按 {inch:0.#} 英寸对角线设定：{_edid.WidthMm} × {_edid.HeightMm} mm（方形像素）。");
    }

    private static EdidSize SizeFromDiagonal(double inch, int resX, int resY)
    {
        double diagMm = inch * 25.4;
        double diagPx = Math.Sqrt((double)resX * resX + (double)resY * resY);
        double mmPerPx = diagMm / diagPx;
        return new EdidSize((int)Math.Round(mmPerPx * resX), (int)Math.Round(mmPerPx * resY));
    }

    // ================= 手画 10cm 标定 =================

    private void OnDrawDown(object sender, MouseButtonEventArgs e)
    {
        _drawing = true;
        _drawStart = e.GetPosition(DrawCanvas);
        _drawEnd = _drawStart;
        DrawCanvas.CaptureMouse();
        EnsureDrawLines();
        UpdateUserLine();
    }

    private void OnDrawMove(object sender, MouseEventArgs e)
    {
        if (!_drawing)
            return;
        _drawEnd = e.GetPosition(DrawCanvas);
        UpdateUserLine();
    }

    private void OnDrawUp(object sender, MouseButtonEventArgs e)
    {
        if (!_drawing)
            return;
        _drawing = false;
        DrawCanvas.ReleaseMouseCapture();
        _drawEnd = e.GetPosition(DrawCanvas);
        FinishDraw();
    }

    private void EnsureDrawLines()
    {
        if (_userDrawLine is not null)
            return;
        _userDrawLine = new Line { Stroke = Brushes.SteelBlue, StrokeThickness = 2 };
        _progLine = new Line { Stroke = Brushes.Red, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 4, 3 } };
        DrawCanvas.Children.Add(_userDrawLine);
        DrawCanvas.Children.Add(_progLine);
    }

    private void UpdateUserLine()
    {
        if (_userDrawLine is null)
            return;
        _userDrawLine.X1 = _drawStart.X;
        _userDrawLine.Y1 = _drawStart.Y;
        _userDrawLine.X2 = _drawEnd.X;
        _userDrawLine.Y2 = _drawEnd.Y;
    }

    /// <summary>画完：斜的自动摆正到较长的那一边，由该边长度推 mm/像素（方形像素）。</summary>
    private void FinishDraw()
    {
        double dx = Math.Abs(_drawEnd.X - _drawStart.X);
        double dy = Math.Abs(_drawEnd.Y - _drawStart.Y);
        _drawnIsHorizontal = dx >= dy;
        _drawnLenDiu = Math.Max(dx, dy);

        if (_drawnLenDiu < 30)
        {
            _hasDrawn = false;
            DrawInfoText.Text = "线段太短，请拖一条更长的（目标正好 10 cm）。";
            FineTunePanel.Visibility = Visibility.Collapsed;
            return;
        }

        double dpiScale = _drawnIsHorizontal ? _dpiScaleX : _dpiScaleY;
        _drawnMmPerPx = 100.0 / _drawnLenDiu / dpiScale;   // 该轴 10cm=100mm

        _hasDrawn = true;
        if (FineTuneSlider is not null)
            FineTuneSlider.Value = 1.0;
        FineTunePanel.Visibility = Visibility.Visible;
        RedrawPreview();
    }

    private void RedrawPreview()
    {
        if (!_hasDrawn || _progLine is null || DrawInfoText is null)
            return;

        double scale = FineTuneSlider?.Value ?? 1.0;
        double dpiScale = _drawnIsHorizontal ? _dpiScaleX : _dpiScaleY;
        double mmPerDiu = _drawnMmPerPx * scale * dpiScale;
        double lenDiu = mmPerDiu > 0 ? 100.0 / mmPerDiu : 0;

        if (_drawnIsHorizontal)
        {
            _progLine.X1 = _drawStart.X;
            _progLine.Y1 = _drawStart.Y;
            _progLine.X2 = _drawStart.X + lenDiu;
            _progLine.Y2 = _drawStart.Y;
        }
        else
        {
            _progLine.X1 = _drawStart.X;
            _progLine.Y1 = _drawStart.Y;
            _progLine.X2 = _drawStart.X;
            _progLine.Y2 = _drawStart.Y + lenDiu;
        }

        if (FineTuneText is not null)
            FineTuneText.Text = $"×{Precision.Fmt(scale, 3)} → mm/px {Precision.Fmt(_drawnMmPerPx * scale)}";
        DrawInfoText.Text = $"你画的是{(_drawnIsHorizontal ? "横向" : "纵向")} {_drawnLenDiu:0} DIU → mm/px {Precision.Fmt(_drawnMmPerPx)}；红虚线=程序算的 10cm，对齐它即可";
    }

    private void OnFineTuneChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => RedrawPreview();

    private void OnApplyDrawn(object sender, RoutedEventArgs e)
    {
        if (!_hasDrawn)
        {
            SetStatus("请先在灰框里画一条 10cm 的线。");
            return;
        }

        double mmPerPx = _drawnMmPerPx * (FineTuneSlider?.Value ?? 1.0);
        if (!(mmPerPx > 0))
            return;

        _edid = new EdidSize((int)Math.Round(mmPerPx * _result.ResX), (int)Math.Round(mmPerPx * _result.ResY));
        _sizeSource = "尺子";
        _result.DiagonalInch = null;
        Log.Info($"手画10cm标定: 方向={(_drawnIsHorizontal ? "横" : "竖")} 长度={_drawnLenDiu:0}DIU "
                 + $"微调={(FineTuneSlider?.Value ?? 1.0):0.###} → mm/px={Precision.Fmt(mmPerPx)} → {_edid.WidthMm}×{_edid.HeightMm}mm");
        RebuildCalibration();
        SetStatus($"已按屏幕画的 10cm 标定：{_edid.WidthMm} × {_edid.HeightMm} mm（方形像素）。");
    }

    private static SizeMode ModeFrom(bool wOk, bool hOk)
    {
        if (wOk && hOk) return SizeMode.UseEdid;
        if (wOk) return SizeMode.TrustWidth;
        if (hOk) return SizeMode.TrustHeight;
        return SizeMode.UseEdid;  // 都不准 → 只能退回原样，由界面提示
    }

    /// <summary>手动改选 EDID 候选（程序按比例挑错时用来纠正）。</summary>
    private void OnEdidPicked(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEdidCombo)
            return;

        int i = EdidCombo.SelectedIndex;
        if (i < 0 || i >= _edidCandidates.Count)
            return;

        _edid = _edidCandidates[i];
        _sizeSource = "EDID";
        _result.DiagonalInch = null;
        Log.Info($"手动改选 EDID 候选: {_edid.WidthMm}×{_edid.HeightMm}mm");
        RebuildCalibration();
        SetStatus($"已改用 EDID 候选 {_edid.WidthMm} × {_edid.HeightMm} mm。");
    }

    // ================= 10cm 标尺 =================

    private void DrawRulers()
    {
        RulerH.Children.Clear();
        RulerV.Children.Clear();
        if (_mmPerDiuX <= 0 || _mmPerDiuY <= 0)
            return;

        const double lenMm = 100.0;   // 10 cm
        DrawRulerLine(RulerH, horizontal: true, lenMm, _mmPerDiuX);
        DrawRulerLine(RulerV, horizontal: false, lenMm, _mmPerDiuY);
    }

    /// <summary>画一条 10cm 标尺：每 1mm 一个刻度，每 5mm 中刻度、每 10mm 长刻度并标 cm 数字。</summary>
    private static void DrawRulerLine(Canvas canvas, bool horizontal, double lenMm, double mmPerDiu)
    {
        if (mmPerDiu <= 0)
            return;

        const double start = 24;    // 起点坐标（DIU）
        const double axis = 70;     // 轴线坐标（DIU）
        double lenDiu = lenMm / mmPerDiu;
        if (!double.IsFinite(lenDiu) || lenDiu <= 0)
            return;

        var axisLine = horizontal
            ? new Line { X1 = start, Y1 = axis, X2 = start + lenDiu, Y2 = axis }
            : new Line { X1 = axis, Y1 = start, X2 = axis, Y2 = start + lenDiu };
        axisLine.Stroke = Brushes.Black;
        axisLine.StrokeThickness = 1.5;
        canvas.Children.Add(axisLine);

        int total = (int)Math.Round(lenMm);
        for (int mm = 0; mm <= total; mm++)
        {
            double pos = start + mm / mmPerDiu;
            bool major = mm % 10 == 0;
            bool half = mm % 5 == 0;
            double h = major ? 16 : (half ? 11 : 6);

            var tk = horizontal
                ? new Line { X1 = pos, Y1 = axis - h, X2 = pos, Y2 = axis + h }
                : new Line { X1 = axis - h, Y1 = pos, X2 = axis + h, Y2 = pos };
            tk.Stroke = Brushes.Black;
            tk.StrokeThickness = major ? 1.6 : 0.8;
            canvas.Children.Add(tk);

            if (major)
            {
                var lb = new TextBlock { Text = (mm / 10).ToString(), FontSize = 11, Foreground = Brushes.Black };
                canvas.Children.Add(lb);
                if (horizontal)
                {
                    Canvas.SetLeft(lb, pos - 5);
                    Canvas.SetTop(lb, axis + 18);
                }
                else
                {
                    Canvas.SetLeft(lb, axis + 22);
                    Canvas.SetTop(lb, pos - 8);
                }
            }
        }

        var cap = new TextBlock { Text = "10 cm（每格 1mm，标注为 cm）", FontSize = 11, Foreground = Brushes.Gray };
        canvas.Children.Add(cap);
        if (horizontal)
        {
            Canvas.SetLeft(cap, start);
            Canvas.SetTop(cap, axis + 34);
        }
        else
        {
            Canvas.SetLeft(cap, axis + 22);
            Canvas.SetTop(cap, start + lenDiu + 6);
        }
    }

    // ================= 步骤导航 =================

    private void ShowStep(int step)
    {
        _step = step;
        Step0.Visibility = step == 0 ? Visibility.Visible : Visibility.Collapsed;
        Step1.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        Step4.Visibility = step == 4 ? Visibility.Visible : Visibility.Collapsed;
        Step5.Visibility = step == 5 ? Visibility.Visible : Visibility.Collapsed;
        Step6.Visibility = step == 6 ? Visibility.Visible : Visibility.Collapsed;

        StepTitle.Text = StepTitles[step];
        BackBtn.IsEnabled = step > 0 && step < 6;
        NextBtn.Content = step == 5 ? "完成" : "下一步";
        NextBtn.IsEnabled = step < 6;

        if (step == 6)
            BuildResult();
        else if (step == 3)
            RecomputePalmSize();   // 进入手掌尺寸步时按当前输入框重算

        Log.Info($"进入 {StepTitles[step]}");
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_step > 0)
            ShowStep(_step - 1);
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        switch (_step)
        {
            case 0:
                _result.WidthRulerOk = HWideOk.IsChecked == true;
                Log.Info($"第1步 横10cm 判定={(_result.WidthRulerOk ? "准" : "不准")}");
                RebuildCalibration();
                ShowStep(1);
                break;

            case 1:
                _result.HeightRulerOk = VWideOk.IsChecked == true;
                Log.Info($"第2步 竖10cm 判定={(_result.HeightRulerOk ? "准" : "不准")}");
                if (!_result.WidthRulerOk && !_result.HeightRulerOk)
                    SetStatus("横向、纵向都判为不准 → 只能退回 EDID 原样，标尺可能仍有偏差。");
                RebuildCalibration();
                ShowStep(2);
                break;

            case 2:
                ShowStep(3);
                break;

            case 3:
                if (_result.PalmAreaPx2 <= 0)
                {
                    SetStatus("请先描一圈，或在右侧填入手掌宽/高（物理像素 px）。");
                    return;
                }
                ShowStep(4);
                break;

            case 4:
                if (_palmAreas.Count == 0)
                {
                    SetStatus("请先把手掌按在黑区上，点「记录手掌」（可多按几次取中值）。");
                    return;
                }
                _result.PalmContactAreaPx2 = Precision.Round(Median(_palmAreas));
                _result.PalmPressure = _palmPressures.Count > 0 ? Precision.Round(Median(_palmPressures), 3) : null;
                Log.Info($"第4步 手掌定案: {_palmAreas.Count} 次中值面积={FmtArea(_result.PalmContactAreaPx2)}{FmtPressure(_result.PalmPressure)}");
                RefreshInfoBar();
                ShowStep(5);
                break;

            case 5:
                if (_fingerAreas.Count == 0)
                {
                    SetStatus("请先用一根手指按在黑区上，点「记录手指」（可多按几次取中值）。");
                    return;
                }
                _result.FingerContactAreaPx2 = Precision.Round(Median(_fingerAreas));
                _result.FingerPressure = _fingerPressures.Count > 0 ? Precision.Round(Median(_fingerPressures), 3) : null;
                Log.Info($"第5步 手指定案: {_fingerAreas.Count} 次中值面积={FmtArea(_result.FingerContactAreaPx2)}{FmtPressure(_result.FingerPressure)}");
                RefreshInfoBar();
                ShowStep(6);
                break;
        }
    }

    // ================= 步骤 4：手掌尺寸 =================

    private void OnStrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
    {
        foreach (StylusPoint p in e.Stroke.StylusPoints)
        {
            if (!_hasBox)
            {
                _bx0 = _bx1 = p.X;
                _by0 = _by1 = p.Y;
                _hasBox = true;
            }
            else
            {
                if (p.X < _bx0) _bx0 = p.X;
                if (p.X > _bx1) _bx1 = p.X;
                if (p.Y < _by0) _by0 = p.Y;
                if (p.Y > _by1) _by1 = p.Y;
            }
        }

        // 用描摹外接矩形填右侧（DIU → 物理像素 px）
        ComputeTraceArea();

        if (_hasBox)
        {
            double wPx = (_bx1 - _bx0) * _dpiScaleX;
            double hPx = (_by1 - _by0) * _dpiScaleY;
            PalmWidthInput.Text = wPx.ToString("0.##");
            PalmHeightInput.Text = hPx.ToString("0.##");
        }
    }

    /// <summary>a3 = 描摹凹面积：对每条描摹线按点序做鞋带公式（隐式闭合）取面积，DIU² → 物理像素²。
    /// 沿手掌外轮廓一笔描一圈时 = 真实凹面积；多笔各自取面积后相加。</summary>
    private void ComputeTraceArea()
    {
        double sumDiu2 = 0;
        foreach (Stroke st in TraceCanvas.Strokes)
        {
            StylusPointCollection pts = st.StylusPoints;
            if (pts.Count < 3)
                continue;
            double a = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                StylusPoint p1 = pts[i];
                StylusPoint p2 = pts[(i + 1) % pts.Count];
                a += p1.X * p2.Y - p2.X * p1.Y;
            }
            sumDiu2 += Math.Abs(a) / 2.0;
        }
        _traceAreaPx2 = sumDiu2 * _dpiScaleX * _dpiScaleY;
    }

    private void OnClearTrace(object sender, RoutedEventArgs e)
    {
        TraceCanvas.Strokes.Clear();
        _hasBox = false;
        _traceAreaPx2 = 0;
        RecomputePalmSize();
        SetStatus("描摹已清除（右侧数值保留；a3 已归零）。");
    }

    private void OnPalmSizeChanged(object sender, TextChangedEventArgs e) => RecomputePalmSize();

    /// <summary>按右侧输入框重算手掌宽/长/面积（px²，取面积公式三选一）；进入该步或改公式时也会调用。</summary>
    private void RecomputePalmSize()
    {
        if (PalmWidthInput is null || PalmHeightInput is null || PalmSizeText is null)
            return;   // XAML 解析期字段尚未全部赋值，忽略

        double.TryParse(PalmWidthInput.Text.Trim(), out double w);
        double.TryParse(PalmHeightInput.Text.Trim(), out double h);

        _result.PalmWidthPx = w;
        _result.PalmHeightPx = h;

        AreaFormula f = EraserPage?.Engine.Formula ?? AreaFormula.Rect;
        _result.PalmAreaFormula = f.ToString();
        _result.PalmAreaPx2 = EraserEngine.PalmAreaFrom(f, w, h, _traceAreaPx2);

        PalmSizeText.Text = _result.PalmAreaPx2 > 0
            ? $"宽 a2 = {Precision.Fmt(w)} px × 高 a1 = {Precision.Fmt(h)} px\n"
              + $"描摹凹面积 a3 = {Precision.Fmt(_traceAreaPx2, 0)} px²\n"
              + $"取面积（{SourceNames.OfFormula(f)}）= {Precision.Fmt(_result.PalmAreaPx2, 0)} px²"
            : "面积：—（描一圈或在右侧填入宽/高）";

        SyncEraserPage();
        RefreshInfoBar();
    }

    // ================= 步骤 5/6：按压采样 =================

    private void OnSample(TouchSample s)
    {
        long nowTicks = Environment.TickCount64;
        if (nowTicks - _lastSampleLogTicks >= 250)
        {
            _lastSampleLogTicks = nowTicks;
            Log.Info($"样本[{StepTitles[_step]}] {Describe(s)}");
        }

        // 信息栏节流刷新（内容都是标定值；节流只为避免每帧刷）
        if (nowTicks - _lastInfoBarTicks >= 300)
        {
            _lastInfoBarTicks = nowTicks;
            RefreshInfoBar();
        }

        if (_step == 6)
        {
            // 信息栏的「当前接触 / 当前压感」在最后一页也要更新（原来这一步提前 return，值永远为 —）
            if (SampleAreaPx2(s) is double la6) _liveAreaPx2 = la6;
            if (s.Pressure01 is double lp6) _livePressure = lp6;
            EraserPage.SubmitSample(s);
            return;
        }

        if (_step != 4 && _step != 5)
            return;

        if (!Accept(s))
            return;

        double? area = SampleAreaPx2(s);
        double? press = s.Pressure01;
        if (area is null && press is null)
            return;

        if (area is double la) _liveAreaPx2 = la;
        if (press is double lp) _livePressure = lp;

        // 峰值只算"当前这一次按压"：抬手静默超过 PressGapMs 后再有样本 → 视为新的一次按压，清空重算
        bool newPress = nowTicks - _lastPressTicks > PressGapMs;
        _lastPressTicks = nowTicks;

        if (_step == 4)
        {
            if (newPress)
            {
                _palmPeakAreaPx2 = null;
                _palmPeakPressure = null;
            }
            _palmActiveSource = s.Source;
            if (area is double a && a > (_palmPeakAreaPx2 ?? 0)) _palmPeakAreaPx2 = a;
            if (press is double p && p > (_palmPeakPressure ?? 0)) _palmPeakPressure = p;
            PalmLiveText.Text = LiveText();
            RefreshPalmPeakText();
            UpdateSourceInfoText();
        }
        else
        {
            if (newPress)
            {
                _fingerPeakAreaPx2 = null;
                _fingerPeakPressure = null;
            }
            _fingerActiveSource = s.Source;
            if (area is double a && a > (_fingerPeakAreaPx2 ?? 0)) _fingerPeakAreaPx2 = a;
            if (press is double p && p > (_fingerPeakPressure ?? 0)) _fingerPeakPressure = p;
            FingerLiveText.Text = LiveText();
            RefreshFingerPeakText();
            UpdateSourceInfoText();
        }
    }

    /// <summary>样本的接触面积（物理像素²）：RawHID 按当前「HID 尺寸取法」换算（逻辑量程→屏幕分辨率等）；WPF 用接触框（DIP × DPI 缩放）。</summary>
    private double? SampleAreaPx2(TouchSample s)
        => SamplePx.AreaPx2(s, EraserPage?.Engine.HidSizeSource ?? HidSizeSource.LogicalToScreen,
            _result.ResX, _result.ResY, _result.MmPerPxX, _result.MmPerPxY, _dpiScaleX, _dpiScaleY);

    /// <summary>
    /// 该样本是否参与标定采样。Stylus 只带压感、不参与面积，始终放行；
    /// 手动指定来源时只认那一路；自适应时跟随引擎的锁定来源（锁了谁就只认谁，另一个直接忽略），
    /// 还没锁定时 HID 优先、HID 静默才用 WPF 垫着。
    /// </summary>
    private bool Accept(TouchSample s)
    {
        if (s.Source == "STYLUS")
            return true;

        SourceMode mode = EraserPage?.Engine.Mode ?? SourceMode.Auto;
        if (mode == SourceMode.RawHid)
            return s.Source == "RawHID";
        if (mode == SourceMode.SoftwareWpf)
            return s.Source == "WPF";

        // 自适应：引擎一旦锁定，就只用锁定来源的样本
        ContactSource locked = EraserPage?.Engine.LockedSource ?? ContactSource.None;
        if (locked == ContactSource.RawHid)
            return s.Source == "RawHID";
        if (locked == ContactSource.Wpf)
            return s.Source == "WPF";

        if (s.Source == "RawHID")
            return true;
        if (s.Source == "WPF")
            return Environment.TickCount64 - _lastHidTicks > EraserEngine.SourceFreshMs;
        return false;
    }

    /// <summary>「实时：」一行的文字：面积（物理像素²）与压感分别显示当前值。</summary>
    private string LiveText()
        => $"实时：接触 {(_liveAreaPx2 is double a ? Precision.Fmt(a, 0) + " px²" : "—")}"
         + $" ｜ 压感 {(_livePressure is double p ? Precision.Fmt(p, 3) : "—")}";

    private void RefreshPalmPeakText()
    {
        string rec = _palmAreas.Count > 0
            ? $"已记录 {_palmAreas.Count} 次｜面积中值 {FmtArea(Median(_palmAreas))}"
            : "记录后开始测下一次（建议多按几次取中值）";
        PalmPeakText.Text = $"本次峰值：{FmtArea(_palmPeakAreaPx2)}{FmtPressure(_palmPeakPressure)}\n{rec}";
    }

    private void RefreshFingerPeakText()
    {
        string rec = _fingerAreas.Count > 0
            ? $"已记录 {_fingerAreas.Count} 次｜面积中值 {FmtArea(Median(_fingerAreas))}"
            : "记录后开始测下一次（建议多按几次取中值）";
        FingerPeakText.Text = $"本次峰值：{FmtArea(_fingerPeakAreaPx2)}{FmtPressure(_fingerPeakPressure)}\n{rec}";
    }

    private static double Median(List<double> v)
    {
        var a = v.OrderBy(x => x).ToArray();
        int n = a.Length;
        if (n == 0) return 0;
        return n % 2 == 1 ? a[n / 2] : (a[n / 2 - 1] + a[n / 2]) / 2.0;
    }

    private static string Describe(TouchSample s)
    {
        string area = s.AreaMm2 is double a ? Precision.Fmt(a, 0) + " mm²" : "无尺寸";
        string multi = s.Contacts is { Count: > 1 } cs ? $"（{cs.Count} 指）" : "";
        string p = s.Pressure01 is double v ? $"，压感 {Precision.Fmt(v, 2)}" : "";
        if (s.PressureRaw is int raw)
            p += $"（原生 {raw} / {s.PressureRange}）";
        return $"[{s.Source}]{multi} {area}{p}";
    }

    private static string FmtArea(double? a) => a is double v ? Precision.Fmt(v, 0) + " px²" : "—";
    private static string FmtPressure(double? p) => p is double v ? $"（压感 {Precision.Fmt(v, 2)}）" : "";

    private void OnRecordPalm(object sender, RoutedEventArgs e)
    {
        if (_palmPeakAreaPx2 is not double a)
        {
            SetStatus("还没采到手掌面积，请把手掌按在黑区上再点。");
            return;
        }
        _palmAreas.Add(a);
        if (_palmPeakPressure is double pp)
            _palmPressures.Add(pp);
        _palmPeakAreaPx2 = null;
        _palmPeakPressure = null;
        Log.Info($"记录手掌 第{_palmAreas.Count}次: 面积={Precision.Fmt(a, 0)}px² 压感={(_palmPressures.Count > 0 ? Precision.Fmt(_palmPressures[^1], 2) : "无")}");
        RefreshPalmPeakText();
        RefreshInfoBar();
        SetStatus($"已记录第 {_palmAreas.Count} 次手掌面积 {Precision.Fmt(a, 0)} px²（可再按几次取中值）。");
    }

    private void OnRecordFinger(object sender, RoutedEventArgs e)
    {
        if (_fingerPeakAreaPx2 is not double a)
        {
            SetStatus("还没采到手指面积，请用手指按在黑区上再点。");
            return;
        }
        _fingerAreas.Add(a);
        if (_fingerPeakPressure is double fp)
            _fingerPressures.Add(fp);
        _fingerPeakAreaPx2 = null;
        _fingerPeakPressure = null;
        Log.Info($"记录手指 第{_fingerAreas.Count}次: 面积={Precision.Fmt(a, 0)}px² 压感={(_fingerPressures.Count > 0 ? Precision.Fmt(_fingerPressures[^1], 2) : "无")}");
        RefreshFingerPeakText();
        RefreshInfoBar();
        SetStatus($"已记录第 {_fingerAreas.Count} 次手指面积 {Precision.Fmt(a, 0)} px²（可再按几次取中值）。");
    }

    // ================= 步骤 7：结果 =================

    private void BuildResult()
    {
        SyncEraserPage();   // 先喂标定值：引擎据此算 K，并给阈值滑块设默认（未动过时）

        // 阈值取引擎当前值：新标定 = 自动中值；载入的标定 = 保存时的滑块值（不被自动值覆盖）
        _result.ThresholdAreaPx2 = EraserPage.Engine.AreaThresholdPx2 > 0
            ? Precision.Round(EraserPage.Engine.AreaThresholdPx2)
            : null;
        _result.K = EraserPage.Engine.ComputeK() is double k && k > 0 ? Precision.Round(k) : null;
        _result.Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        Log.Info($"标定结果: 手掌={FmtArea(_result.PalmContactAreaPx2)} 手指={FmtArea(_result.FingerContactAreaPx2)} "
                 + $"→ 切换阈值={FmtArea(_result.ThresholdAreaPx2)} K={(_result.K is double kv ? Precision.Fmt(kv) : "无")}");

        RefreshInfoBar();
    }

    /// <summary>把标定值喂给面积擦预览页（手掌宽高/描摹面积/按压面积/压感，全部物理像素）。</summary>
    private void SyncEraserPage()
    {
        EraserPage.Setup(_dpiScaleX, _dpiScaleY,
            _result.PalmWidthPx, _result.PalmHeightPx, _result.PalmTraceAreaPx2,
            _result.PalmContactAreaPx2 ?? 0, _result.FingerContactAreaPx2 ?? 0,
            _result.PalmPressure, _result.FingerPressure,
            _result.ResX, _result.ResY, _result.MmPerPxX, _result.MmPerPxY);
    }

    private void OnSaveRequested()
    {
        try
        {
            // 预览页的滑块/开关/形状/长宽比/面积公式：保存界面设置，供主程序按同一套参数工作
            EraserSettings set = EraserPage.CurrentSettings;
            _result.KTrim = set.KTrim;
            _result.ThresholdAreaPx2 = set.AreaThresholdPx2;
            _result.PalmPressureThreshold = set.PalmPressureThreshold;
            _result.PalmNormMax = set.PalmNormMax;
            _result.WritingPressureThreshold = set.WritingPressureThreshold;
            _result.WritingNormMax = set.WritingNormMax;
            _result.PalmPressureEnabled = set.PalmPressureEnabled;
            _result.WritingUsesPressure = set.WritingUsesPressure;
            _result.FollowSize = set.FollowSize;
            _result.LockPalmSize = set.LockPalmSize;
            _result.AreaThresholdEnabled = set.AreaThresholdEnabled;
            _result.WritingFollowSize = set.WritingFollowSize;
            _result.EraserShape = set.Shape.ToString();
            _result.AspectSource = set.Aspect.ToString();
            _result.AspectW = set.CustomAspectW;
            _result.AspectH = set.CustomAspectH;
            _result.PalmAreaFormula = set.Formula.ToString();
            _result.K = EraserPage.Engine.ComputeK() is double k && k > 0 ? Precision.Round(k) : null;
            _result.PalmAreaPx2 = EraserPage.Engine.PalmAreaPx2;

            string path = CalibrationResult.DefaultPath();
            _result.Save(path);
            EraserPage.SavePathNotice = "已保存到：" + path;
            SetStatus("结果已保存。");
            Log.Info($"结果已保存: {path}（K={(_result.K is double kv ? Precision.Fmt(kv) : "无")} 随尺寸={set.FollowSize} "
                     + $"锁定={set.LockPalmSize} 掌擦压感={set.PalmPressureEnabled} 书写压感={set.WritingUsesPressure} "
                     + $"形状={set.Shape} 长宽比={set.Aspect} 公式={set.Formula}）");
        }
        catch (Exception ex)
        {
            EraserPage.SavePathNotice = "保存失败：" + ex.Message;
            SetStatus("保存失败。");
            Log.Error("结果保存失败: " + ex);
        }
    }

    // ================= 顶部信息栏 =================

    private void RefreshInfoBar()
    {
        if (_calib is null)
        {
            InfoBar.Text = "正在读取 EDID ...";
            return;
        }

        double? kLive = EraserPage?.Engine.ComputeK() is double ek && ek > 0 ? ek : _result.K;
        string kText = kLive is double kv
            ? $"{Precision.Fmt(kv)}（每边 ×{Precision.Fmt(Math.Sqrt(kv))}）"
            : "—（待第 5 步标定手掌按压）";

        double thLive = EraserPage?.Engine.AreaThresholdPx2 ?? 0;

        InfoBar.Text =
            // ① 屏幕：物理尺寸 / 来源 / 分辨率
            $"显示屏尺寸：{_calib.ScreenWidthMm:0.0}毫米 × {_calib.ScreenHeightMm:0.0}毫米"
            + $"    来源：{_sizeSource}：{_calib.Edid.WidthMm}毫米 × {_calib.Edid.HeightMm}毫米"
            + $"    分辨率：{_calib.ResX}像素 × {_calib.ResY}像素\n"
            // ② 推算方式 / 毫米每像素
            + $"推算方式：横{Yes(_result.WidthRulerOk)} 竖{Yes(_result.HeightRulerOk)} {_result.SizeMode}"
            + $"    毫米/像素：{Precision.Fmt(_calib.MmPerPxX)} × {Precision.Fmt(_calib.MmPerPxY)}\n"
            // ③ 手掌描摹（px / a3 / 取面积公式）/ 按压
            + $"手掌描摹：宽a2={Precision.Fmt(_result.PalmWidthPx, 0)}px 高a1={Precision.Fmt(_result.PalmHeightPx, 0)}px"
            + $"    描摹凹面积a3={Precision.Fmt(_result.PalmTraceAreaPx2, 0)}px²"
            + $"    取面积({SourceNames.OfFormula(EraserPage?.Engine.Formula ?? AreaFormula.Rect)})={Precision.Fmt(_result.PalmAreaPx2, 0)}px²\n"
            + $"手掌按压：面积{FmtArea(_result.PalmContactAreaPx2)} 压感{FmtPressureVal(_result.PalmPressure)}"
            + $"    手指按压：面积{FmtArea(_result.FingerContactAreaPx2)} 压感{FmtPressureVal(_result.FingerPressure)}"
            + $"    切换阈值：{(thLive > 0 ? Precision.Fmt(thLive, 0) + " px²" : "—")}\n"
            // ④ K 定值 + 当前实时值
            + $"K 定值（手掌像素面积 ÷ 触摸尺寸乘积）= {kText}"
            + $"    当前接触：{(_liveAreaPx2 is double la ? Precision.Fmt(la, 0) + " px²" : "—")}"
            + $"    当前压感：{(_livePressure is double lp ? Precision.Fmt(lp, 2) : "—")}\n"
            // ⑤ 原始HID 映射表（设备换算表，只读展示）
            + HidTableText();
    }

    private static string FmtPressureVal(double? p) => p is double v ? Precision.Fmt(v, 2) : "—";

    private static string Yes(bool b) => b ? "准" : "不准";

    private void SetStatus(string s) => StatusText.Text = s;
}
