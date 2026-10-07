using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace TouchSuite.App.Eraser;

/// <summary>
/// 「面积擦预览」页 —— 预览区 + 全部参数（6 滑块 + 2 压感开关 + 形状/长宽比/面积公式），
/// 逻辑都在 <see cref="EraserEngine"/> 里，这里只负责摆放、绘制与把触摸样本喂给引擎。
/// 尺寸单位全程用**物理像素 px**（DIU 只在绘制时换算）。
/// </summary>
public partial class EraserPreviewPage : UserControl
{
    public EraserEngine Engine { get; } = new();

    /// <summary>给宿主窗口显示状态栏用。</summary>
    public event Action<string>? Status;

    /// <summary>点「保存结果到文件」时触发（由宿主窗口负责落盘与结果组装）。</summary>
    public event Action? SaveRequested;

    /// <summary>影响标定结果的设置变化（如手掌面积公式三选一）→ 宿主重算 K 与信息栏。</summary>
    public event Action? SettingsChanged;

    /// <summary>保存结果提示（显示在本页按钮下方）。</summary>
    public string SavePathNotice
    {
        get => SavePathText.Text;
        set => SavePathText.Text = value;
    }

    private void OnSaveRequested(object sender, RoutedEventArgs e) => SaveRequested?.Invoke();

    /// <summary>接收 WPF 触摸兜底用的宿主元素。</summary>
    public FrameworkElement HostElement => Host;

    public EraserPreviewPage()
    {
        InitializeComponent();
        Engine.Changed += Redraw;
        Loaded += (_, _) => Redraw();

        // 初始勾选/滑块状态同步进引擎（XAML 解析期事件早于字段赋值，会被 null 守卫挡掉）
        SyncControlsToEngine();
        UpdateValueTexts();

        // 定时刷新：所有来源过期后把「生效来源」归零，界面显示「无」
        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background,
            (_, _) => Engine.Tick(), Dispatcher);
        Loaded += (_, _) => _tick.Start();
        Unloaded += (_, _) => _tick.Stop();
    }

    private readonly DispatcherTimer _tick;

    private void SyncControlsToEngine()
    {
        Engine.FollowSize = FollowSizeCheck.IsChecked == true;
        Engine.LockPalmSize = LockPalmSizeCheck.IsChecked == true;
        Engine.PalmPressureEnabled = PalmPressureCheck.IsChecked == true;
        Engine.AreaThresholdEnabled = AreaThresholdCheck.IsChecked == true;
        Engine.WritingUsesPressure = WritingPressureCheck.IsChecked == true;
        Engine.WritingFollowSize = WritingFollowSizeCheck.IsChecked == true;
        Engine.Shape = ShapeCircle.IsChecked == true ? EraserShape.Circle : EraserShape.Rectangle;
        Engine.Aspect = AspectCustom.IsChecked == true ? AspectSource.Custom : AspectSource.Contact;
        Engine.Formula = FormulaTrace.IsChecked == true ? AreaFormula.Trace
            : FormulaEllipse.IsChecked == true ? AreaFormula.Ellipse : AreaFormula.Rect;
        Engine.KTrim = KTrimSlider.Value;
        Engine.PalmPressureThreshold = PalmThrSlider.Value;
        Engine.PalmNormMax = PalmMaxSlider.Value;
        Engine.WritingPressureThreshold = WritingThrSlider.Value;
        Engine.WritingNormMax = WritingMaxSlider.Value;
        Engine.AreaThresholdPx2 = AreaThresholdSlider.Value;
        ParseAspect();
        SyncEnableStates();
    }

    // ================= 宿主喂入标定量 =================

    /// <summary>
    /// 把标定值喂给引擎（全部物理像素）。可重复调用：滑块只在"用户没动过"时才跟随新默认值。
    /// 手掌面积公式（三选一）由本页单选决定，不在这里覆盖。
    /// </summary>
    public void Setup(double pxPerDiuX, double pxPerDiuY,
        double palmWidthPx, double palmHeightPx, double palmTraceAreaPx2,
        double palmContactAreaPx2, double fingerContactAreaPx2,
        double? palmPressure, double? fingerPressure,
        int resX, int resY, double mmPerPxX, double mmPerPxY)
    {
        Engine.PxPerDiuX = pxPerDiuX > 0 ? pxPerDiuX : 1;
        Engine.PxPerDiuY = pxPerDiuY > 0 ? pxPerDiuY : 1;
        Engine.PalmWidthPx = palmWidthPx;
        Engine.PalmHeightPx = palmHeightPx;
        Engine.PalmTraceAreaPx2 = palmTraceAreaPx2;
        Engine.PalmContactAreaPx2 = palmContactAreaPx2;
        Engine.FingerContactAreaPx2 = fingerContactAreaPx2;
        _resX = resX;
        _resY = resY;
        _mmPerPxX = mmPerPxX;
        _mmPerPxY = mmPerPxY;

        // ① 擦/写切换阈值：绝对区间 [手指按压面积, 手掌按压面积]，默认中值
        if (Engine.HasThresholdRange)
        {
            double lo = Math.Min(palmContactAreaPx2, fingerContactAreaPx2);
            double hi = Math.Max(palmContactAreaPx2, fingerContactAreaPx2);
            _suppressSlider = true;   // 改 Minimum/Maximum 会把当前值钳进新范围并触发事件，这里要静默
            AreaThresholdSlider.Minimum = lo;
            AreaThresholdSlider.Maximum = hi;
            _suppressSlider = false;
            AreaThresholdSlider.IsEnabled = true;
            _defAreaThr = Engine.AutoThresholdAreaPx2;
            SetSliderSuppress(AreaThresholdSlider,
                !_areaTouched ? _defAreaThr : Math.Clamp(AreaThresholdSlider.Value, lo, hi));
            Engine.AreaThresholdPx2 = AreaThresholdSlider.Value;
        }
        else
        {
            AreaThresholdSlider.IsEnabled = false;
            _defAreaThr = Engine.AutoThresholdAreaPx2;
            if (!_areaTouched && _defAreaThr > 0)
            {
                SetSliderSuppress(AreaThresholdSlider, _defAreaThr);
                Engine.AreaThresholdPx2 = _defAreaThr;
            }
        }

        // ② 压感阈值默认值 = 标定压感 b3 / c3（无压感设备退回 512/1024 = 0.5）
        _defPalmThr = palmPressure is double pp ? Math.Clamp(pp, 0, 1) : EraserEngine.SimulatedPressure01;
        _defWritingThr = fingerPressure is double fp ? Math.Clamp(fp, 0, 1) : EraserEngine.SimulatedPressure01;
        if (!_palmThrTouched) SetSliderSuppress(PalmThrSlider, _defPalmThr);
        if (!_writingThrTouched) SetSliderSuppress(WritingThrSlider, _defWritingThr);
        Engine.PalmPressureThreshold = PalmThrSlider.Value;
        Engine.WritingPressureThreshold = WritingThrSlider.Value;

        UpdateValueTexts();
        Redraw();
    }

    private int _resX, _resY;
    private double _mmPerPxX, _mmPerPxY;

    // 各滑块的"自动默认值"与"用户是否动过"标记（动过后 Setup 不再覆盖，只做范围夹取）
    private double _defAreaThr, _defPalmThr = EraserEngine.SimulatedPressure01, _defWritingThr = EraserEngine.SimulatedPressure01;
    private bool _areaTouched, _palmThrTouched, _writingThrTouched;
    private bool _suppressSlider;

    private void SetSliderSuppress(Slider s, double value)
    {
        _suppressSlider = true;
        s.Value = value;
        _suppressSlider = false;
    }

    /// <summary>把手掌面积公式等设置灌回界面（载入标定时用）。</summary>
    public void ApplySettings(CalibrationResult r)
    {
        if (r.FollowSize is bool fs) FollowSizeCheck.IsChecked = fs;
        if (r.LockPalmSize is bool lp) LockPalmSizeCheck.IsChecked = lp;
        if (r.AreaThresholdEnabled is bool at) AreaThresholdCheck.IsChecked = at;
        if (r.WritingFollowSize is bool wf) WritingFollowSizeCheck.IsChecked = wf;
        if (r.PalmPressureEnabled is bool pp) PalmPressureCheck.IsChecked = pp;
        if (r.WritingUsesPressure is bool wp) WritingPressureCheck.IsChecked = wp;
        if (r.KTrim is double kt) KTrimSlider.Value = Math.Clamp(kt, KTrimSlider.Minimum, KTrimSlider.Maximum);
        if (r.PalmPressureThreshold is double pt) PalmThrSlider.Value = Math.Clamp(pt, 0, 1);
        if (r.PalmNormMax is double pm) PalmMaxSlider.Value = Math.Clamp(pm, 1, 3);
        if (r.WritingPressureThreshold is double wt) WritingThrSlider.Value = Math.Clamp(wt, 0, 1);
        if (r.WritingNormMax is double wm) WritingMaxSlider.Value = Math.Clamp(wm, 1, 3);
        if (r.ThresholdAreaPx2 is double ta && AreaThresholdSlider.IsEnabled)
            AreaThresholdSlider.Value = Math.Clamp(ta, AreaThresholdSlider.Minimum, AreaThresholdSlider.Maximum);
        if (r.AspectSource is not null)
        {
            AspectCustom.IsChecked = r.AspectSource == "Custom";
            AspectContact.IsChecked = r.AspectSource != "Custom";
        }
        if (r.AspectW is double aw && aw > 0) AspectWInput.Text = aw.ToString("0.###");
        if (r.AspectH is double ah && ah > 0) AspectHInput.Text = ah.ToString("0.###");
        if (r.EraserShape is not null)
        {
            bool circle = string.Equals(r.EraserShape, "Circle", StringComparison.OrdinalIgnoreCase);
            ShapeCircle.IsChecked = circle;
            ShapeRect.IsChecked = !circle;
        }
        if (r.PalmAreaFormula is not null)
            SetFormulaRadio(r.PalmAreaFormula switch
            {
                "Ellipse" => AreaFormula.Ellipse,
                "Trace" => AreaFormula.Trace,
                _ => AreaFormula.Rect,
            });

        SyncEnableStates();
        UpdateValueTexts();
        Engine.NotifyChanged();
    }

    /// <summary>当前界面设置快照（保存标定时写进 JSON）。</summary>
    public EraserSettings CurrentSettings
    {
        get
        {
            double.TryParse(AspectWInput.Text.Trim(), out double aw);
            double.TryParse(AspectHInput.Text.Trim(), out double ah);
            return new EraserSettings(
                Engine.FollowSize, Engine.LockPalmSize, Engine.PalmPressureEnabled, Engine.AreaThresholdEnabled,
                Engine.WritingUsesPressure, Engine.WritingFollowSize,
                Engine.KTrim, Engine.AreaThresholdPx2,
                Engine.PalmPressureThreshold, Engine.PalmNormMax,
                Engine.WritingPressureThreshold, Engine.WritingNormMax,
                Engine.Shape, Engine.Aspect, aw, ah, Engine.Formula);
        }
    }

    // ================= 来源下拉 =================

    /// <summary>用户在预览页改了来源下拉时触发（宿主窗口据此同步自己的下拉）。</summary>
    public event Action? SourceSelectionChanged;

    private bool _suppressSourceUi;

    /// <summary>按引擎当前来源状态刷新预览页下拉（宿主同步用；不会反向触发 <see cref="SourceSelectionChanged"/>）。</summary>
    public void SyncSourceSelection(SourceMode mode, HidSizeSource hidSize)
    {
        if (SourceModeCombo is null)
            return;
        _suppressSourceUi = true;
        SourceModeCombo.SelectedIndex = (int)mode;
        HidSizeCombo.SelectedIndex = (int)hidSize;
        HidSizePanel.Visibility = mode == SourceMode.RawHid ? Visibility.Visible : Visibility.Collapsed;
        _suppressSourceUi = false;
    }

    private void OnSourceModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSourceUi || SourceModeCombo is null)
            return;
        Engine.SetMode((SourceMode)Math.Clamp(SourceModeCombo.SelectedIndex, 0, 2));
        if (HidSizePanel is not null)
            HidSizePanel.Visibility = Engine.Mode == SourceMode.RawHid ? Visibility.Visible : Visibility.Collapsed;
        SourceSelectionChanged?.Invoke();
    }

    private void OnHidSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSourceUi || HidSizeCombo is null)
            return;
        Engine.SetHidSizeSource((HidSizeSource)Math.Clamp(HidSizeCombo.SelectedIndex, 0, 1));
        SourceSelectionChanged?.Invoke();
    }

    // ================= 触摸样本 → 引擎 =================

    /// <summary>最近一次喂入的样本（抬手后仍显示最后看到的值）。</summary>
    private TouchSample? _lastSample;

    /// <summary>把一次触摸样本换算成物理像素矩形后交给引擎（多指=逐根各喂一次，各画各的框）。</summary>
    public void SubmitSample(TouchSample s)
    {
        _lastSample = s;

        // 实时压感喂给引擎（压感归一用它放大擦除区/书写点）
        Engine.NotePressure(s.Pressure01);

        switch (s.Source)
        {
            case "RawHID":
                SubmitHidRect(s);
                break;

            case "STYLUS":
                // 只有压感、没有尺寸：让引擎按最新压力重算并重画
                if (s.Pressure01 is not null)
                    Engine.NotifyChanged();
                break;

            case "WPF":
                if (s.Contacts is { Count: > 0 } list)
                {
                    foreach (ContactRect c in list)
                    {
                        if (c.DiuRect is not Rect r || r.Width <= 0 || r.Height <= 0)
                            continue;
                        Engine.Submit(ContactSource.Wpf, c.Id, ToPx(r), applyThreshold: true,
                            detail: $"#{c.Id} Bounds {Precision.Fmt(r.Width, 3)}×{Precision.Fmt(r.Height, 3)} DIP "
                                  + $"= {Precision.Fmt(r.Width * Engine.PxPerDiuX)}×{Precision.Fmt(r.Height * Engine.PxPerDiuY)} px");
                    }
                    break;
                }
                if (s.DiuRect is not Rect r0 || r0.Width <= 0 || r0.Height <= 0)
                    return;
                Engine.Submit(ContactSource.Wpf, 0, ToPx(r0), applyThreshold: true,
                    detail: $"Bounds {Precision.Fmt(r0.Width, 3)}×{Precision.Fmt(r0.Height, 3)} DIP "
                          + $"= {Precision.Fmt(r0.Width * Engine.PxPerDiuX)}×{Precision.Fmt(r0.Height * Engine.PxPerDiuY)} px");
                break;
        }
    }

    private Rect ToPx(Rect diu)
        => new(diu.X * Engine.PxPerDiuX, diu.Y * Engine.PxPerDiuY,
               diu.Width * Engine.PxPerDiuX, diu.Height * Engine.PxPerDiuY);

    /// <summary>把 HID 的尺寸（物理像素）+ 归一化坐标合成一个原始HID 矩形喂给引擎（id 固定 0）。
    /// Xn/Yn 为数字化器归一化坐标 → 虚拟桌面 DIP → 预览区局部坐标。</summary>
    private void SubmitHidRect(TouchSample s)
    {
        (double? wPx, double? hPx) = HidScale.ContactPx(s, Engine.HidSizeSource, _resX, _resY, _mmPerPxX, _mmPerPxY);
        if (wPx is not double w || hPx is not double h || !(w > 0) || !(h > 0))
            return;

        double sx = Engine.PxPerDiuX > 0 ? Engine.PxPerDiuX : 1;
        double sy = Engine.PxPerDiuY > 0 ? Engine.PxPerDiuY : 1;

        double cx, cy;
        if (s.XNorm is double xn && s.YNorm is double yn)
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            Point hostOrigin = Host.PointToScreen(new Point(0, 0));   // 设备像素
            double screenDipX = SystemParameters.VirtualScreenLeft + Math.Clamp(xn, 0, 1) * SystemParameters.VirtualScreenWidth;
            double screenDipY = SystemParameters.VirtualScreenTop + Math.Clamp(yn, 0, 1) * SystemParameters.VirtualScreenHeight;
            cx = (screenDipX - hostOrigin.X / dpi.DpiScaleX) * sx;
            cy = (screenDipY - hostOrigin.Y / dpi.DpiScaleY) * sy;
        }
        else
        {
            cx = Host.ActualWidth / 2 * sx;
            cy = Host.ActualHeight / 2 * sy;
        }

        // 夹取到预览区内，避免坐标异常时把擦除区画到看不见的地方
        if (Host.ActualWidth > 0)
            cx = Math.Clamp(cx, 0, Host.ActualWidth * sx);
        if (Host.ActualHeight > 0)
            cy = Math.Clamp(cy, 0, Host.ActualHeight * sy);

        Engine.Submit(ContactSource.RawHid, 0,
            new Rect(cx - w / 2, cy - h / 2, w, h),
            applyThreshold: false,
            detail: $"HID（{SourceNames.OfHidSize(Engine.HidSizeSource)}） {Precision.Fmt(w)}×{Precision.Fmt(h)} px "
                  + $"= {Precision.Fmt(w * h, 0)} px²");
    }

    // ================= 参数控件 =================

    private void UpdateValueTexts()
    {
        KTrimValueText.Text = $"×{KTrimSlider.Value:0.00}";
        PalmThrValueText.Text = $"{PalmThrSlider.Value:0.00}";
        PalmMaxValueText.Text = $"×{PalmMaxSlider.Value:0.00}";
        WritingThrValueText.Text = $"{WritingThrSlider.Value:0.00}";
        WritingMaxValueText.Text = $"×{WritingMaxSlider.Value:0.00}";
        AreaThresholdValueText.Text = Engine.HasThresholdRange
            ? $"当前 {Precision.Fmt(AreaThresholdSlider.Value, 0)} px²"
            : "—（待标定）";
    }

    /// <summary>压感开关关掉后，对应的两个滑块灰掉不可点。</summary>
    private void SyncEnableStates()
    {
        bool palmOn = PalmPressureCheck.IsChecked == true;
        PalmThrSlider.IsEnabled = palmOn;
        PalmMaxSlider.IsEnabled = palmOn;
        bool writeOn = WritingPressureCheck.IsChecked == true;
        WritingThrSlider.IsEnabled = writeOn;
        WritingMaxSlider.IsEnabled = writeOn;
    }

    private void OnKTrimChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSlider || KTrimValueText is null)
            return;
        Engine.KTrim = e.NewValue;
        KTrimValueText.Text = $"×{e.NewValue:0.00}";
        Engine.NotifyChanged();
    }

    private void OnResetKTrim(object sender, RoutedEventArgs e)
    {
        SetSliderSuppress(KTrimSlider, 1.0);
        Engine.KTrim = 1.0;
        UpdateValueTexts();
        Engine.NotifyChanged();
    }

    private void OnAreaThresholdSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSlider || AreaThresholdValueText is null)
            return;
        _areaTouched = true;
        Engine.AreaThresholdPx2 = e.NewValue;
        AreaThresholdValueText.Text = $"当前 {e.NewValue:0} px²";
        Engine.NotifyChanged();
    }

    private void OnResetAreaThreshold(object sender, RoutedEventArgs e)
    {
        if (!AreaThresholdSlider.IsEnabled)
            return;
        SetSliderSuppress(AreaThresholdSlider,
            Math.Clamp(_defAreaThr, AreaThresholdSlider.Minimum, AreaThresholdSlider.Maximum));
        _areaTouched = false;
        Engine.AreaThresholdPx2 = AreaThresholdSlider.Value;
        UpdateValueTexts();
        Engine.NotifyChanged();
    }

    private void OnPalmPressureChanged(object sender, RoutedEventArgs e)
    {
        // 守卫点选排行更靠后的滑块：XAML 解析期字段可能尚未赋值
        if (PalmThrSlider is null)
            return;
        Engine.PalmPressureEnabled = PalmPressureCheck.IsChecked == true;
        SyncEnableStates();
        Engine.NotifyChanged();
    }

    private void OnPalmThrChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSlider || PalmThrValueText is null)
            return;
        _palmThrTouched = true;
        Engine.PalmPressureThreshold = e.NewValue;
        PalmThrValueText.Text = $"{e.NewValue:0.00}";
        Engine.NotifyChanged();
    }

    private void OnResetPalmThr(object sender, RoutedEventArgs e)
    {
        SetSliderSuppress(PalmThrSlider, Math.Clamp(_defPalmThr, 0, 1));
        _palmThrTouched = false;
        Engine.PalmPressureThreshold = PalmThrSlider.Value;
        UpdateValueTexts();
        Engine.NotifyChanged();
    }

    private void OnPalmMaxChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSlider || PalmMaxValueText is null)
            return;
        Engine.PalmNormMax = e.NewValue;
        PalmMaxValueText.Text = $"×{e.NewValue:0.00}";
        Engine.NotifyChanged();
    }

    private void OnResetPalmMax(object sender, RoutedEventArgs e)
    {
        SetSliderSuppress(PalmMaxSlider, 2.0);
        Engine.PalmNormMax = 2.0;
        UpdateValueTexts();
        Engine.NotifyChanged();
    }

    private void OnWritingPressureChanged(object sender, RoutedEventArgs e)
    {
        // 守卫点选排行更靠后的滑块：XAML 解析期字段可能尚未赋值
        if (WritingThrSlider is null)
            return;
        Engine.WritingUsesPressure = WritingPressureCheck.IsChecked == true;
        SyncEnableStates();
        Engine.NotifyChanged();
    }

    private void OnWritingThrChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSlider || WritingThrValueText is null)
            return;
        _writingThrTouched = true;
        Engine.WritingPressureThreshold = e.NewValue;
        WritingThrValueText.Text = $"{e.NewValue:0.00}";
        Engine.NotifyChanged();
    }

    private void OnResetWritingThr(object sender, RoutedEventArgs e)
    {
        SetSliderSuppress(WritingThrSlider, Math.Clamp(_defWritingThr, 0, 1));
        _writingThrTouched = false;
        Engine.WritingPressureThreshold = WritingThrSlider.Value;
        UpdateValueTexts();
        Engine.NotifyChanged();
    }

    private void OnWritingMaxChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSlider || WritingMaxValueText is null)
            return;
        Engine.WritingNormMax = e.NewValue;
        WritingMaxValueText.Text = $"×{e.NewValue:0.00}";
        Engine.NotifyChanged();
    }

    private void OnResetWritingMax(object sender, RoutedEventArgs e)
    {
        SetSliderSuppress(WritingMaxSlider, 2.0);
        Engine.WritingNormMax = 2.0;
        UpdateValueTexts();
        Engine.NotifyChanged();
    }

    private void OnShapeChanged(object sender, RoutedEventArgs e)
    {
        if (ShapeCircle is null)
            return;
        Engine.Shape = ShapeCircle.IsChecked == true ? EraserShape.Circle : EraserShape.Rectangle;

        // 一般默认：矩形用 a1×a2、椭圆用 π/4×a1×a2；用户自己选了 a3 就不再联动
        if (Engine.Formula != AreaFormula.Trace)
            SetFormulaRadio(Engine.Shape == EraserShape.Rectangle ? AreaFormula.Rect : AreaFormula.Ellipse);

        Engine.NotifyChanged();
    }

    private void SetFormulaRadio(AreaFormula f)
    {
        bool trace = f == AreaFormula.Trace;
        bool ellipse = f == AreaFormula.Ellipse;
        FormulaRect.IsChecked = !trace && !ellipse;
        FormulaEllipse.IsChecked = ellipse;
        FormulaTrace.IsChecked = trace;
    }

    private void OnFormulaChanged(object sender, RoutedEventArgs e)
    {
        if (FormulaTrace is null)
            return;
        Engine.Formula = FormulaTrace.IsChecked == true ? AreaFormula.Trace
            : FormulaEllipse.IsChecked == true ? AreaFormula.Ellipse : AreaFormula.Rect;
        SettingsChanged?.Invoke();
        Engine.NotifyChanged();
    }

    private void OnAspectChanged(object sender, RoutedEventArgs e)
    {
        if (AspectCustom is null)
            return;
        Engine.Aspect = AspectCustom.IsChecked == true ? AspectSource.Custom : AspectSource.Contact;
        ParseAspect();
        Engine.NotifyChanged();
    }

    private void OnAspectTextChanged(object sender, TextChangedEventArgs e)
    {
        if (AspectWInput is null || AspectHInput is null)
            return;
        ParseAspect();
        Engine.NotifyChanged();
    }

    private void ParseAspect()
    {
        double.TryParse(AspectWInput.Text.Trim(), out double w);
        double.TryParse(AspectHInput.Text.Trim(), out double h);
        if (w > 0 && h > 0)
            Engine.CustomAspect = w / h;
    }

    private void OnFollowSizeChanged(object sender, RoutedEventArgs e)
    {
        if (FollowSizeCheck is null)
            return;
        Engine.FollowSize = FollowSizeCheck.IsChecked == true;
        Engine.NotifyChanged();
    }

    private void OnLockPalmSizeChanged(object sender, RoutedEventArgs e)
    {
        if (LockPalmSizeCheck is null)
            return;
        Engine.LockPalmSize = LockPalmSizeCheck.IsChecked == true;
        Engine.NotifyChanged();
    }

    private void OnAreaThresholdCheckChanged(object sender, RoutedEventArgs e)
    {
        if (AreaThresholdCheck is null)
            return;
        Engine.AreaThresholdEnabled = AreaThresholdCheck.IsChecked == true;
        Engine.NotifyChanged();
    }

    private void OnWritingFollowSizeChanged(object sender, RoutedEventArgs e)
    {
        if (WritingFollowSizeCheck is null)
            return;
        Engine.WritingFollowSize = WritingFollowSizeCheck.IsChecked == true;
        Engine.NotifyChanged();
    }

    private void OnClearPreview(object sender, RoutedEventArgs e)
    {
        Engine.Clear();
        Status?.Invoke("面积擦预览已清除。");
    }

    // ================= 触摸信息面板 =================

    /// <summary>把当前样本的原始值整成一段文字：来源 / 状态 / 坐标 / 尺寸（px）/ 手指数 / 压感 / 分指明细。</summary>
    private string BuildTouchInfo()
    {
        var sb = new System.Text.StringBuilder();

        TouchSample? s = _lastSample;
        if (s is null)
        {
            sb.Append("— 尚无样本（在预览区按一下触摸屏） —");
            return sb.ToString();
        }

        sb.Append("来源: ").Append(s.Source).Append("   帧=").Append(s.FrameId)
          .Append("   t=").Append(s.TimeMs > 0 ? s.TimeMs + "ms" : "—").Append('\n');

        bool live = Engine.HasLiveContact;
        sb.Append("触摸状态: ").Append(live ? "按下（有活动接触）" : "抬起 / 无接触").Append('\n');

        int n = s.Contacts?.Count ?? (s.DiuRect is not null || s.WidthMm is not null ? 1 : 0);
        sb.Append($"当前手指数: {n}").Append('\n');

        sb.Append("坐标: ").Append(FmtPos(s)).Append('\n');

        if (s.Pressure01 is double p01)
            sb.Append($"压感: {Precision.Fmt(p01, 4)}（0~1，来自 {s.Source}）\n");
        else
            sb.Append("压感: 无\n");

        double? areaPx2 = SamplePx.AreaPx2(s, Engine.HidSizeSource, _resX, _resY,
            _mmPerPxX, _mmPerPxY, Engine.PxPerDiuX, Engine.PxPerDiuY);
        (double W, double H)? size = SamplePx.SizePx(s, Engine.HidSizeSource, _resX, _resY,
            _mmPerPxX, _mmPerPxY, Engine.PxPerDiuX, Engine.PxPerDiuY);
        if (size is not null)
            sb.Append($"尺寸（px）= {Precision.Fmt(size.Value.W)}×{Precision.Fmt(size.Value.H)}"
                    + $" → 面积 {Precision.Fmt(areaPx2 ?? 0, 0)} px²\n");

        if (s.Source == "RawHID")
            sb.Append($"HID 原始: W={s.WidthLogical}/{s.WidthLogMax}  H={s.HeightLogical}/{s.HeightLogMax}"
                    + $"（物理 {Precision.Fmt(s.WidthMm)}×{Precision.Fmt(s.HeightMm)} mm）\n");

        if (s.Contacts is { Count: > 0 } cs)
        {
            sb.Append("分指明细:\n");
            foreach (ContactRect c in cs)
            {
                sb.Append("  #").Append(c.Id).Append("  ");
                if (c.DiuRect is Rect r)
                    sb.Append($"框 {Precision.Fmt(r.X)}，{Precision.Fmt(r.Y)} {Precision.Fmt(r.Width)}×{Precision.Fmt(r.Height)} DIP"
                            + $" = {Precision.Fmt(r.Width * Engine.PxPerDiuX)}×{Precision.Fmt(r.Height * Engine.PxPerDiuY)} px");
                if (c.P01 is double cp)
                    sb.Append($"  P {Precision.Fmt(cp, 4)}");
                sb.Append('\n');
            }
        }

        sb.Append("Detail: ").Append(s.Detail);
        return sb.ToString();
    }

    private static string FmtPos(TouchSample s)
    {
        if (s.XNorm is double xn && s.YNorm is double yn)
            return $"归一化 {Precision.Fmt(xn, 4)}，{Precision.Fmt(yn, 4)}";
        if (s.ScreenPxX is double sx && s.ScreenPxY is double sy)
            return $"屏幕 {Precision.Fmt(sx, 1)}，{Precision.Fmt(sy, 1)} px";
        return "—（该来源不带坐标）";
    }

    // ================= 重画 =================

    private void Redraw()
    {
        if (Overlay is null)
            return;

        Overlay.Children.Clear();
        SourceInfoText.Text = Engine.SourceInfo();
        KText.Text = Engine.KInfo();
        AreaThresholdText.Text = Engine.AreaThresholdInfo();
        PressureInfoText.Text = Engine.PressureInfo();
        JudgeText.Text = Engine.JudgeInfo();
        EraserInfoText.Text = string.IsNullOrEmpty(Engine.SizeHint) ? Engine.EraserInfo() : Engine.SizeHint;
        HidInfoText.Text = BuildTouchInfo();
        AreaThresholdValueText.Text = Engine.HasThresholdRange
            ? $"当前 {Precision.Fmt(Engine.AreaThresholdPx2, 0)} px²"
            : "—（待标定）";

        // 抬手（来源过期）后不再画：形状应消失，只保留文字留档
        if (!Engine.HasLiveContact)
            return;

        double px2DiuX = Engine.PxPerDiuX > 0 ? Engine.PxPerDiuX : 1;
        double px2DiuY = Engine.PxPerDiuY > 0 ? Engine.PxPerDiuY : 1;

        // 多指：每根接触各画各的框（各按各的面积/判定）
        foreach ((int id, Rect c) in Engine.LastContacts)
        {
            double cx = (c.X + c.Width / 2) / px2DiuX;
            double cy = (c.Y + c.Height / 2) / px2DiuY;

            if (Engine.IsWriting(id))
            {
                // 书写：蓝点，直径 = 14px × 压感倍数（开启「书写随尺寸」时再按该指接触面积缩放）
                double d = Engine.WritingDotDiameterOfPx(c) / px2DiuX;
                var pen = new Ellipse
                {
                    Width = d,
                    Height = d,
                    Fill = Brushes.DodgerBlue,
                    Stroke = Brushes.DeepSkyBlue,
                    StrokeThickness = 1,
                };
                Overlay.Children.Add(pen);
                Canvas.SetLeft(pen, cx - d / 2);
                Canvas.SetTop(pen, cy - d / 2);
            }
            else
            {
                (double w, double h) = Engine.SizeDiu(id, c);
                if (w <= 0 || h <= 0)
                    continue;
                Shape shape = Engine.Shape == EraserShape.Circle
                    ? new Ellipse { Width = w, Height = h }
                    : new Rectangle { Width = w, Height = h };
                shape.Stroke = Brushes.Lime;
                shape.StrokeThickness = 2;
                shape.Fill = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0xFF, 0x00));
                Overlay.Children.Add(shape);
                Canvas.SetLeft(shape, cx - w / 2);
                Canvas.SetTop(shape, cy - h / 2);
            }

            var dot = new Ellipse { Width = 6, Height = 6, Fill = Brushes.Yellow };
            Overlay.Children.Add(dot);
            Canvas.SetLeft(dot, cx - 3);
            Canvas.SetTop(dot, cy - 3);
        }
    }
}