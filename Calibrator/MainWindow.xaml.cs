using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using TouchErase.Calibrator.Eraser;

namespace TouchErase.Calibrator;

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
    private readonly CalibrationResult _result = new();

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

    // 压感诊断
    private double? _lastPressure01;
    private int? _lastPressureRaw;
    private string _lastPressureRange = "";
    private string _lastPressureSource = "";
    private double _pressureMin = double.MaxValue, _pressureMax = double.MinValue;

    // 手掌擦预览（整套在 Eraser/EraserPreviewPage + EraserEngine 里）
    private readonly List<EdidSize> _edidCandidates = new();
    private bool _suppressEdidCombo;
    private int _step;

    private double _dpiScaleX = 1, _dpiScaleY = 1;
    private double _mmPerDiuX, _mmPerDiuY;

    // 描摹包围盒（DIU，相对 TraceCanvas）
    private double _bx0, _by0, _bx1, _by1;
    private bool _hasBox;

    private double? _palmPeakAreaMm2, _palmPeakPressure;
    private double? _fingerPeakAreaMm2, _fingerPeakPressure;

    // 多次按压取中值
    private readonly List<double> _palmAreas = new();
    private readonly List<double> _palmPressures = new();
    private readonly List<double> _fingerAreas = new();
    private readonly List<double> _fingerPressures = new();

    private long _lastSampleLogTicks;   // 样本日志节流：≥250ms 才记一行，避免刷屏
    private long _lastInfoBarTicks;     // 信息栏刷新节流（实时压感 60Hz，节流后才看得清）
    private double? _liveAreaMm2;       // 手掌/手指实时接触面积（来自 WPF Touch）
    private double? _livePressure;      // 手掌/手指实时压感（来自 WPF Stylus）
    private int _palmSourceMode;        // 手掌：0=自适应 1=原始HID 2=WPF
    private int _fingerSourceMode;      // 手指：同上
    private long _lastRawHidTicks;      // 原始HID 最近一次样本时刻（自适应判优用）
    private long _lastPressTicks;       // 上一次收到样本的时刻（判"抬手"用）
    private const long PressGapMs = 350;   // 静默超过此时长视为抬手，下一次样本算新的一次按压
    private string _palmActiveSource = "";     // 手掌当前实际生效的原始来源
    private string _fingerActiveSource = "";   // 手指当前实际生效的原始来源

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += (_, _) => _touch.Dispose();

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

        _touch.Sample += OnSample;
        _touch.Attach(this);
        _touch.AttachFallback(PalmHost);
        _touch.AttachFallback(FingerHost);
        _touch.AttachFallback(EraserPage.HostElement);

        // 面积擦预览：状态回显到底部状态栏；标定结果同步给触摸层（原始HID 计数→mm）
        EraserPage.Status += SetStatus;
        EraserPage.Engine.Changed += SyncCountsScale;

        _result.DeviceName = _touch.DeviceName;
        Log.Info($"触摸屏: {_touch.DeviceName} | 声明接触尺寸={_touch.TouchDeclaresSize} | 声明压感={_touch.TouchDeclaresPressure}");
        SetStatus(_touch.TouchDeclaresSize
            ? $"触摸屏声明接触尺寸 ✓{( _touch.TouchDeclaresPressure ? "，支持压感 ✓" : "，无压感")}"
            : "触摸屏未上报接触尺寸（可能拿不到面积）");

        ShowStep(0);
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

        if (step == 4 && PalmSourceInfoText is not null)
            PalmSourceInfoText.Text = "当前生效：" + EffectiveSourceLabel(_palmSourceMode, _palmActiveSource);
        else if (step == 5 && FingerSourceInfoText is not null)
            FingerSourceInfoText.Text = "当前生效：" + EffectiveSourceLabel(_fingerSourceMode, _fingerActiveSource);

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
                if (_result.PalmAreaCm2 <= 0)
                {
                    SetStatus("请先描一圈，或在右侧填入手掌宽/长（cm）。");
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
                _result.PalmContactAreaMm2 = Precision.Round(Median(_palmAreas));
                _result.PalmPressure = _palmPressures.Count > 0 ? Precision.Round(Median(_palmPressures), 3) : null;
                Log.Info($"第4步 手掌定案: {_palmAreas.Count} 次中值面积={FmtArea(_result.PalmContactAreaMm2)}{FmtPressure(_result.PalmPressure)}");
                RefreshInfoBar();
                ShowStep(5);
                break;

            case 5:
                if (_fingerAreas.Count == 0)
                {
                    SetStatus("请先用一根手指按在黑区上，点「记录手指」（可多按几次取中值）。");
                    return;
                }
                _result.FingerContactAreaMm2 = Precision.Round(Median(_fingerAreas));
                _result.FingerPressure = _fingerPressures.Count > 0 ? Precision.Round(Median(_fingerPressures), 3) : null;
                Log.Info($"第5步 手指定案: {_fingerAreas.Count} 次中值面积={FmtArea(_result.FingerContactAreaMm2)}{FmtPressure(_result.FingerPressure)}");
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

        // 用描摹外接矩形填右侧（毫米 → 厘米）
        if (_mmPerDiuX > 0 && _mmPerDiuY > 0 && _hasBox)
        {
            double wCm = (_bx1 - _bx0) * _mmPerDiuX / 10.0;
            double hCm = (_by1 - _by0) * _mmPerDiuY / 10.0;
            PalmWidthInput.Text = Precision.Fmt(wCm);
            PalmHeightInput.Text = Precision.Fmt(hCm);
        }
    }

    private void OnClearTrace(object sender, RoutedEventArgs e)
    {
        TraceCanvas.Strokes.Clear();
        _hasBox = false;
        SetStatus("描摹已清除（右侧数值保留）。");
    }

    private void OnPalmSizeChanged(object sender, TextChangedEventArgs e) => RecomputePalmSize();

    /// <summary>按右侧输入框重算手掌宽/长/面积（进入该步时也会调用，不依赖 TextChanged 是否触发）。</summary>
    private void RecomputePalmSize()
    {
        if (PalmWidthInput is null || PalmHeightInput is null || PalmSizeText is null)
            return;   // XAML 解析期字段尚未全部赋值，忽略

        double.TryParse(PalmWidthInput.Text.Trim(), out double w);
        double.TryParse(PalmHeightInput.Text.Trim(), out double h);

        _result.PalmWidthCm = w;
        _result.PalmHeightCm = h;
        _result.PalmAreaCm2 = w > 0 && h > 0 ? w * h : 0;

        PalmSizeText.Text = _result.PalmAreaCm2 > 0
            ? $"宽 {Precision.Fmt(w)} × 长 {Precision.Fmt(h)} cm\n面积 = {Precision.Fmt(_result.PalmAreaCm2)} cm²\n长宽比 = {Precision.Fmt(w / h)}"
            : "面积：—";
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

        // 压感诊断：实时压感有 ~60Hz，信息栏节流刷新才看得清
        if (s.Pressure01 is double pv)
        {
            _lastPressure01 = pv;
            _lastPressureRaw = s.PressureRaw;
            _lastPressureRange = s.PressureRange;
            _lastPressureSource = s.Source;
            if (pv < _pressureMin) _pressureMin = pv;
            if (pv > _pressureMax) _pressureMax = pv;
            if (nowTicks - _lastInfoBarTicks >= 300)
            {
                _lastInfoBarTicks = nowTicks;
                RefreshInfoBar();
            }
        }

        if (_step == 6)
        {
            EraserPage.SubmitSample(s);
            SyncCountsScale();
            return;
        }

        if (_step != 4 && _step != 5)
            return;

        if (s.Source == "RawHID")
            _lastRawHidTicks = nowTicks;

        // 当前步骤选定的接触来源（自适应 / 原始HID / WM_POINTER / WPF）
        int mode = _step == 4 ? _palmSourceMode : _fingerSourceMode;
        if (!Accept(mode, s, nowTicks))
            return;

        double? area = s.AreaMm2;
        double? press = s.Pressure01;
        if (area is null && press is null)
            return;

        if (area is double la) _liveAreaMm2 = la;
        if (press is double lp) _livePressure = lp;

        // 峰值只算"当前这一次按压"：抬手静默超过 PressGapMs 后再有样本 → 视为新的一次按压，清空重算
        bool newPress = nowTicks - _lastPressTicks > PressGapMs;
        _lastPressTicks = nowTicks;

        if (_step == 4)
        {
            if (newPress)
            {
                _palmPeakAreaMm2 = null;
                _palmPeakPressure = null;
            }
            _palmActiveSource = s.Source;
            if (area is double a && a > (_palmPeakAreaMm2 ?? 0)) _palmPeakAreaMm2 = a;
            if (press is double p && p > (_palmPeakPressure ?? 0)) _palmPeakPressure = p;
            if (PalmSourceInfoText is not null)
                PalmSourceInfoText.Text = "当前生效：" + EffectiveSourceLabel(_palmSourceMode, _palmActiveSource);
            PalmLiveText.Text = LiveText();
            RefreshPalmPeakText();
        }
        else
        {
            if (newPress)
            {
                _fingerPeakAreaMm2 = null;
                _fingerPeakPressure = null;
            }
            _fingerActiveSource = s.Source;
            if (area is double a && a > (_fingerPeakAreaMm2 ?? 0)) _fingerPeakAreaMm2 = a;
            if (press is double p && p > (_fingerPeakPressure ?? 0)) _fingerPeakPressure = p;
            if (FingerSourceInfoText is not null)
                FingerSourceInfoText.Text = "当前生效：" + EffectiveSourceLabel(_fingerSourceMode, _fingerActiveSource);
            FingerLiveText.Text = LiveText();
            RefreshFingerPeakText();
        }
    }

    /// <summary>把原始来源名合并成"逻辑来源"（WPF 面积 与 Stylus 压感同属 WPF 通路）。</summary>
    private static string LogicalSource(string src) => src switch
    {
        "RawHID" => "原始HID（设备上报）",
        "WPF" or "STYLUS" => "WPF 通路（Touch 面积 + Stylus 压感）",
        _ => src.Length > 0 ? src : "—",
    };

    /// <summary>「当前生效」一行的文字：自适应下显示实际锁定的来源，手动模式下显示所选来源。</summary>
    private static string EffectiveSourceLabel(int mode, string active)
    {
        if (mode != 0)
            return mode switch
            {
                1 => "原始HID（设备上报）",
                2 => "WPF 通路（Touch 面积 + Stylus 压感）",
                _ => "—",
            };
        return active.Length > 0 ? LogicalSource(active) : "自适应：识别中…";
    }

    /// <summary>该来源在当前选择下是否参与标定采样。</summary>
    private bool Accept(int mode, TouchSample s, long now)
    {
        switch (mode)
        {
            case 1: return s.Source == "RawHID";
            case 2: return s.Source is "WPF" or "STYLUS";
            default: // 自适应：有原始HID 就用它；否则用 WPF 通路（Touch 面积 + Stylus 压感）
                return now - _lastRawHidTicks <= 500
                    ? s.Source == "RawHID"
                    : s.Source is "WPF" or "STYLUS";
        }
    }

    private void OnPalmSourceModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PalmSourceCombo is null)
            return;
        _palmSourceMode = Math.Max(0, PalmSourceCombo.SelectedIndex);
        _palmActiveSource = "";
        if (PalmSourceInfoText is not null)
            PalmSourceInfoText.Text = "当前生效：" + EffectiveSourceLabel(_palmSourceMode, "");
    }

    private void OnFingerSourceModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FingerSourceCombo is null)
            return;
        _fingerSourceMode = Math.Max(0, FingerSourceCombo.SelectedIndex);
        _fingerActiveSource = "";
        if (FingerSourceInfoText is not null)
            FingerSourceInfoText.Text = "当前生效：" + EffectiveSourceLabel(_fingerSourceMode, "");
    }

    /// <summary>「实时：」一行的文字：面积（WPF Touch）与压感（WPF Stylus）分别显示当前值。</summary>
    private string LiveText()
        => $"实时：接触 {(_liveAreaMm2 is double a ? Precision.Fmt(a, 1) + " mm²" : "—")}"
         + $" ｜ 压感 {(_livePressure is double p ? Precision.Fmt(p, 3) : "—")}";

    private void RefreshPalmPeakText()
    {
        string rec = _palmAreas.Count > 0
            ? $"已记录 {_palmAreas.Count} 次｜面积中值 {FmtArea(Median(_palmAreas))}"
            : "记录后开始测下一次（建议多按几次取中值）";
        PalmPeakText.Text = $"本次峰值：{FmtArea(_palmPeakAreaMm2)}{FmtPressure(_palmPeakPressure)}\n{rec}";
    }

    private void RefreshFingerPeakText()
    {
        string rec = _fingerAreas.Count > 0
            ? $"已记录 {_fingerAreas.Count} 次｜面积中值 {FmtArea(Median(_fingerAreas))}"
            : "记录后开始测下一次（建议多按几次取中值）";
        FingerPeakText.Text = $"本次峰值：{FmtArea(_fingerPeakAreaMm2)}{FmtPressure(_fingerPeakPressure)}\n{rec}";
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
        string p = s.Pressure01 is double v ? $"，压感 {Precision.Fmt(v, 2)}" : "";
        if (s.PressureRaw is int raw)
            p += $"（原生 {raw} / {s.PressureRange}）";
        return $"[{s.Source}] {area}{p}";
    }

    private static string FmtArea(double? a) => a is double v ? Precision.Fmt(v, 0) + " mm²" : "—";
    private static string FmtPressure(double? p) => p is double v ? $"（压感 {Precision.Fmt(v, 2)}）" : "";

    private void OnRecordPalm(object sender, RoutedEventArgs e)
    {
        if (_palmPeakAreaMm2 is not double a)
        {
            SetStatus("还没采到手掌面积，请把手掌按在黑区上再点。");
            return;
        }
        _palmAreas.Add(a);
        if (_palmPeakPressure is double pp)
            _palmPressures.Add(pp);
        _palmPeakAreaMm2 = null;
        _palmPeakPressure = null;
        Log.Info($"记录手掌 第{_palmAreas.Count}次: 面积={Precision.Fmt(a, 0)}mm² 压感={(_palmPressures.Count > 0 ? Precision.Fmt(_palmPressures[^1], 2) : "无")}");
        RefreshPalmPeakText();
        RefreshInfoBar();
        SetStatus($"已记录第 {_palmAreas.Count} 次手掌面积 {Precision.Fmt(a, 0)} mm²（可再按几次取中值）。");
    }

    private void OnRecordFinger(object sender, RoutedEventArgs e)
    {
        if (_fingerPeakAreaMm2 is not double a)
        {
            SetStatus("还没采到手指面积，请用手指按在黑区上再点。");
            return;
        }
        _fingerAreas.Add(a);
        if (_fingerPeakPressure is double fp)
            _fingerPressures.Add(fp);
        _fingerPeakAreaMm2 = null;
        _fingerPeakPressure = null;
        Log.Info($"记录手指 第{_fingerAreas.Count}次: 面积={Precision.Fmt(a, 0)}mm² 压感={(_fingerPressures.Count > 0 ? Precision.Fmt(_fingerPressures[^1], 2) : "无")}");
        RefreshFingerPeakText();
        RefreshInfoBar();
        SetStatus($"已记录第 {_fingerAreas.Count} 次手指面积 {Precision.Fmt(a, 0)} mm²（可再按几次取中值）。");
    }

    // ================= 步骤 7：结果 =================

    private void BuildResult()
    {
        if (_result.PalmContactAreaMm2 is double pa && _result.FingerContactAreaMm2 is double fa)
            _result.ThresholdAreaMm2 = Precision.Round((pa + fa) / 2.0);

        _result.PressureThreshold = _result.PalmPressure;
        _result.Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        Log.Info($"标定结果: 手掌={FmtArea(_result.PalmContactAreaMm2)} 手指={FmtArea(_result.FingerContactAreaMm2)} "
                 + $"→ 面积阈值={FmtArea(_result.ThresholdAreaMm2)} 压感阈值={(_result.PressureThreshold is double prv ? Precision.Fmt(prv, 2) : "无")}");

        var sb = new StringBuilder();
        sb.Append($"推算方式: {_result.SizeMode}    分辨率: {_result.ResX}×{_result.ResY}\n");
        sb.Append($"物理尺寸: {_result.EdidWidthMm}×{_result.EdidHeightMm} mm（来源: {_result.SizeSource}"
            + (_result.DiagonalInch is double di ? $"，{di:0.#} 英寸" : "") + $")    mm/px: {Precision.Fmt(_result.MmPerPxX)}×{Precision.Fmt(_result.MmPerPxY)}\n");
        sb.Append($"横10cm准: {(_result.WidthRulerOk ? "是" : "否")}    竖10cm准: {(_result.HeightRulerOk ? "是" : "否")}\n");
        sb.Append($"手掌尺寸: {Precision.Fmt(_result.PalmWidthCm)} × {Precision.Fmt(_result.PalmHeightCm)} cm = {Precision.Fmt(_result.PalmAreaCm2)} cm²\n");
        sb.Append($"手掌按压面积: {FmtArea(_result.PalmContactAreaMm2)}{FmtPressure(_result.PalmPressure)}\n");
        sb.Append($"手指按压面积: {FmtArea(_result.FingerContactAreaMm2)}{FmtPressure(_result.FingerPressure)}\n");
        sb.Append($"★ 手掌擦 / 书写 判定阈值（面积）= (手掌 + 手指) / 2 = {FmtArea(_result.ThresholdAreaMm2)}\n");
        sb.Append($"★ 压感阈值 = {(_result.PressureThreshold is double pt ? Precision.Fmt(pt, 2) : "无压感 / 未采到")}");
        sb.Append("    ← 以上为自动值；可在第 7 步「倍率 / 面积阈值 / 压感阈值」框里用滑块微调，保存时取微调后的生效值");
        ResultText.Text = sb.ToString();

        RefreshInfoBar();
        SyncEraserPage();
    }

    /// <summary>把标定结果喂给面积擦预览页，并把「计数→mm」同步给触摸层。</summary>
    private void SyncEraserPage()
    {
        double aspect = _result.PalmHeightCm > 0 ? _result.PalmWidthCm / _result.PalmHeightCm : 0;
        EraserPage.Setup(_mmPerDiuX, _mmPerDiuY, _result.PalmAreaCm2, aspect,
            _result.ThresholdAreaMm2 ?? 0, _result.PressureThreshold);
        _touch.CountsToMmScale = EraserPage.Engine.CountsToMmScale;
    }

    private void SyncCountsScale()
        => _touch.CountsToMmScale = EraserPage.Engine.CountsToMmScale;

    private void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            // 记录阈值微调：JSON 里同时保留自动值与微调后的生效值
            EraserEngine eng = EraserPage.Engine;
            _result.AreaThresholdTrim = eng.AreaThresholdTrim;
            _result.AreaThresholdEffectiveMm2 = eng.AreaThresholdBaseMm2 > 0 ? eng.EffectiveAreaThresholdMm2 : null;
            _result.PressureThresholdTrim = eng.PressureThresholdTrim;
            _result.PressureThresholdEffective = eng.PressureThresholdBase is double ? eng.EffectivePressureThreshold : null;

            string path = CalibrationResult.DefaultPath();
            _result.Save(path);
            SavePathText.Text = "已保存到：" + path;
            SetStatus("结果已保存。");
            Log.Info($"结果已保存: {path}");
        }
        catch (Exception ex)
        {
            SavePathText.Text = "保存失败：" + ex.Message;
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

        var sb = new StringBuilder();
        sb.Append($"EDID 物理: {_calib.Edid.WidthMm} × {_calib.Edid.HeightMm} mm（来源: {_sizeSource}）    分辨率: {_calib.ResX} × {_calib.ResY}    推算方式: {_result.SizeMode}");
        if (_edidCandidates.Count > 1)
            sb.Append($"    在用显示器 EDID 候选 {_edidCandidates.Count} 块: {string.Join(" / ", _edidCandidates.Select(c => $"{c.WidthMm}×{c.HeightMm}"))}");
        sb.Append('\n');
        sb.Append($"mm/px: {Precision.Fmt(_calib.MmPerPxX)} × {Precision.Fmt(_calib.MmPerPxY)}    屏幕物理: {_calib.ScreenWidthMm:0.0} × {_calib.ScreenHeightMm:0.0} mm");
        sb.Append($"    横10cm: {Yes(_result.WidthRulerOk)}    竖10cm: {Yes(_result.HeightRulerOk)}\n");

        sb.Append($"手掌: {Precision.Fmt(_result.PalmWidthCm)} × {Precision.Fmt(_result.PalmHeightCm)} cm = {Precision.Fmt(_result.PalmAreaCm2)} cm²");
        if (_result.PalmContactAreaMm2 is double pa) sb.Append($"    手掌按压: {Precision.Fmt(pa, 0)} mm²{FmtPressure(_result.PalmPressure)}");
        if (_result.FingerContactAreaMm2 is double fa) sb.Append($"    手指按压: {Precision.Fmt(fa, 0)} mm²{FmtPressure(_result.FingerPressure)}");
        if (_result.ThresholdAreaMm2 is double th) sb.Append($"    ★阈值: {Precision.Fmt(th, 0)} mm²");

        // 压感诊断（判断"压感一直是 1"是设备还是映射问题）
        if (_lastPressure01 is double lp)
        {
            string rng = _pressureMin == _pressureMax
                ? $"恒定 {Precision.Fmt(_pressureMin, 2)}（原生值不随力度变化 → 该设备压感是示性/饱和的，非真实压感）"
                : $"范围 {Precision.Fmt(_pressureMin, 2)} ~ {Precision.Fmt(_pressureMax, 2)}（随力度变化 → 压感可用）";
            sb.Append($"\n压感: 原生 {(_lastPressureRaw?.ToString() ?? "—")} / {(_lastPressureRange == "" ? "—" : _lastPressureRange)}"
                      + $" → {Precision.Fmt(lp, 2)}  [{_lastPressureSource}]    {rng}");
        }
        else
        {
            sb.Append("\n压感: 未采到（该设备/来源可能不支持压感）");
        }

        InfoBar.Text = sb.ToString();
    }

    private static string Yes(bool b) => b ? "准" : "不准";

    private void SetStatus(string s) => StatusText.Text = s;
}
