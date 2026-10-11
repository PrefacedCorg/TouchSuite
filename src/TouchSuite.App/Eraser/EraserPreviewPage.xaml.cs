using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace TouchSuite.App.Eraser;

/// <summary>
/// 「面积擦预览」页 —— 预览区 + 全部参数（6 滑块 + 2 压感开关 + 形状/长宽比），
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
        Engine.SmoothJitter = SmoothJitterCheck.IsChecked == true;
        Engine.MultiTouchAsPalm = MultiTouchPalmCheck.IsChecked == true;
        Engine.MultiTouchPalmMin = Math.Max(2, MultiTouchMinCombo.SelectedIndex + 2);
        Engine.PalmGroupWindowMs = ParsePalmWindow();
        Engine.PalmGroupTol = PalmGroupTolSlider.Value;
        Engine.SizeLimit = CurrentPalmLimit();
        Engine.PalmPressureEnabled = PalmPressureCheck.IsChecked == true;
        Engine.AreaThresholdEnabled = AreaThresholdCheck.IsChecked == true;
        Engine.WritingUsesPressure = WritingPressureCheck.IsChecked == true;
        Engine.WritingFollowSize = WritingFollowSizeCheck.IsChecked == true;
        Engine.Shape = ShapeCircle.IsChecked == true ? EraserShape.Circle : EraserShape.Rectangle;
        Engine.Aspect = AspectCustom.IsChecked == true ? AspectSource.Custom
            : AspectTrace.IsChecked == true ? AspectSource.Trace
            : AspectSource.Contact;
        Engine.SetSizeScale(SizeScaleCombo.SelectedIndex == 1 ? HidSizeScale.Isotropic : HidSizeScale.Stretch);
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
    /// </summary>
    public void Setup(double pxPerDiuX, double pxPerDiuY,
        double palmWidthPx, double palmHeightPx,
        double palmContactAreaPx2, double fingerContactAreaPx2,
        double? palmPressure, double? fingerPressure,
        int resX, int resY)
    {
        Engine.PxPerDiuX = pxPerDiuX > 0 ? pxPerDiuX : 1;
        Engine.PxPerDiuY = pxPerDiuY > 0 ? pxPerDiuY : 1;
        Engine.PalmWidthPx = palmWidthPx;
        Engine.PalmHeightPx = palmHeightPx;
        Engine.PalmContactAreaPx2 = palmContactAreaPx2;
        Engine.FingerContactAreaPx2 = fingerContactAreaPx2;
        _resX = resX;
        _resY = resY;

        // ① 擦/写切换阈值：绝对区间 [0, 手掌按压面积]，默认中值 (手掌+手指)/2
        //    下限放到 0：允许把阈值压到手指按压面积以下（那样指按大小的接触也算手掌擦，面积仍按标定手掌算）
        if (Engine.HasThresholdRange)
        {
            double hi = Math.Max(palmContactAreaPx2, fingerContactAreaPx2);
            _suppressSlider = true;   // 改 Minimum/Maximum 会把当前值钳进新范围并触发事件，这里要静默
            AreaThresholdSlider.Minimum = 0;
            AreaThresholdSlider.Maximum = hi;
            _suppressSlider = false;
            AreaThresholdSlider.IsEnabled = true;
            _defAreaThr = Engine.AutoThresholdAreaPx2;
            SetSliderSuppress(AreaThresholdSlider,
                !_areaTouched ? _defAreaThr : Math.Clamp(AreaThresholdSlider.Value, 0, hi));
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

        SyncEnableStates();
        UpdateValueTexts();
        Redraw();
    }

    private int _resX, _resY;

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
        if (r.SmoothJitter is bool sj) SmoothJitterCheck.IsChecked = sj;
        if (r.MultiTouchAsPalm is bool mt) MultiTouchPalmCheck.IsChecked = mt;
        if (r.MultiTouchPalmMin is int mp) MultiTouchMinCombo.SelectedIndex = Math.Clamp(mp - 2, 0, 3);
        if (r.PalmGroupWindowMs is int gw) PalmGroupWindowBox.Text = gw.ToString();
        if (r.PalmGroupTol is double gt) PalmGroupTolSlider.Value = Math.Clamp(gt, 1, 2);
        PalmLimit limit;
        if (!string.IsNullOrEmpty(r.PalmLimit) && Enum.TryParse(r.PalmLimit, out PalmLimit pl)) limit = pl;
        else if (r.PalmFloorEnabled is bool pf) limit = pf ? PalmLimit.Floor : PalmLimit.None;
        else limit = PalmLimit.Floor;
        SetPalmLimitRadios(limit);
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

        // 先定形状（会按形状给"自定义长宽比"默认值），再灌入保存的自定义长宽比，避免被默认值覆盖
        if (r.EraserShape is not null)
        {
            bool circle = string.Equals(r.EraserShape, "Circle", StringComparison.OrdinalIgnoreCase);
            ShapeCircle.IsChecked = circle;
            ShapeRect.IsChecked = !circle;
        }
        if (r.AspectSource is not null)
        {
            AspectCustom.IsChecked = r.AspectSource == "Custom";
            AspectTrace.IsChecked = r.AspectSource == "Trace";
            AspectContact.IsChecked = r.AspectSource != "Custom" && r.AspectSource != "Trace";
        }
        if (r.AspectW is double aw && aw > 0) AspectWInput.Text = aw.ToString("0.###");
        if (r.AspectH is double ah && ah > 0) AspectHInput.Text = ah.ToString("0.###");
        ParseAspect();
        if (r.HidSizeScale is not null)
        {
            bool iso = string.Equals(r.HidSizeScale, "Isotropic", StringComparison.OrdinalIgnoreCase);
            _suppressSizeUi = true;
            SizeScaleCombo.SelectedIndex = iso ? 1 : 0;
            _suppressSizeUi = false;
            Engine.SetSizeScale(iso ? HidSizeScale.Isotropic : HidSizeScale.Stretch);
        }

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
                Engine.FollowSize, Engine.LockPalmSize, Engine.SmoothJitter, Engine.SizeLimit,
                Engine.PalmPressureEnabled, Engine.AreaThresholdEnabled,
                Engine.WritingUsesPressure, Engine.WritingFollowSize,
                Engine.KTrim, Engine.AreaThresholdPx2,
                Engine.PalmPressureThreshold, Engine.PalmNormMax,
                Engine.WritingPressureThreshold, Engine.WritingNormMax,
                Engine.Shape, Engine.Aspect, aw, ah, Engine.SizeScale, Engine.MultiTouchAsPalm, Engine.MultiTouchPalmMin,
                Engine.PalmGroupWindowMs, Engine.PalmGroupTol);
        }
    }

    // ================= 来源下拉 =================

    /// <summary>用户在预览页改了来源下拉时触发（宿主窗口据此同步自己的下拉）。</summary>
    public event Action? SourceSelectionChanged;

    private bool _suppressSourceUi;

    /// <summary>按引擎当前来源状态刷新预览页下拉（宿主同步用；不会反向触发 <see cref="SourceSelectionChanged"/>）。</summary>
    public void SyncSourceSelection(SourceMode mode)
    {
        if (SourceModeCombo is null)
            return;
        _suppressSourceUi = true;
        SourceModeCombo.SelectedIndex = (int)mode;
        _suppressSourceUi = false;
        UpdateHidDeviceVisibility();
    }

    private void OnSourceModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSourceUi || SourceModeCombo is null)
            return;
        Engine.SetMode((SourceMode)Math.Clamp(SourceModeCombo.SelectedIndex, 0, 2));
        UpdateHidDeviceVisibility();
        SourceSelectionChanged?.Invoke();
    }

    // ---- HID 触摸屏（多块时的选择）----

    private readonly List<(string Key, string Label)> _hidDevices = new();
    private bool _suppressDeviceUi;

    /// <summary>宿主把扫到的触摸类 HID 设备喂进来（第 1 项固定是"自动"）。</summary>
    public void SetHidDevices(IReadOnlyList<(string Key, string Label)> devices)
    {
        _hidDevices.Clear();
        _hidDevices.AddRange(devices);

        _suppressDeviceUi = true;
        HidDeviceCombo.Items.Clear();
        HidDeviceCombo.Items.Add("自动（第一块出数的触摸屏）");
        foreach ((string _, string label) in _hidDevices)
            HidDeviceCombo.Items.Add(label);
        // 保留当前选择（重扫设备时别把用户手动选的屏清掉）
        int idx = 0;
        for (int i = 0; i < _hidDevices.Count; i++)
            if (string.Equals(_hidDevices[i].Key, Engine.RawHidDeviceKey, StringComparison.OrdinalIgnoreCase))
            {
                idx = i + 1;
                break;
            }
        HidDeviceCombo.SelectedIndex = idx;
        _suppressDeviceUi = false;

        UpdateHidDeviceVisibility();
    }

    /// <summary>按引擎当前指定的设备刷新下拉（宿主同步用）。</summary>
    public void SyncHidDevice(string key)
    {
        if (HidDeviceCombo is null)
            return;
        _suppressDeviceUi = true;
        int idx = 0;
        for (int i = 0; i < _hidDevices.Count; i++)
            if (string.Equals(_hidDevices[i].Key, key, StringComparison.OrdinalIgnoreCase))
            {
                idx = i + 1;
                break;
            }
        HidDeviceCombo.SelectedIndex = idx;
        _suppressDeviceUi = false;
    }

    private void OnHidDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDeviceUi || HidDeviceCombo is null)
            return;
        int i = HidDeviceCombo.SelectedIndex;
        string key = i >= 1 && i - 1 < _hidDevices.Count ? _hidDevices[i - 1].Key : "";
        Engine.SetRawHidDevice(key);
        SourceSelectionChanged?.Invoke();
    }

    /// <summary>HID 触摸屏下拉：只在与 HID 相关（自适应可能锁到 HID / 手动原始HID）且确实扫到设备时才显示。</summary>
    private void UpdateHidDeviceVisibility()
    {
        if (HidDevicePanel is null)
            return;
        HidDevicePanel.Visibility = _hidDevices.Count > 0 && Engine.Mode != SourceMode.SoftwareWpf
            ? Visibility.Visible : Visibility.Collapsed;
    }

    // ================= 触摸样本 → 引擎 =================

    /// <summary>最近一次喂入的样本（抬手后仍显示最后看到的值）。</summary>
    private TouchSample? _lastSample;

    /// <summary>
    /// 只为「触摸信息」面板留档：**只记录当前生效来源的样本**。
    /// 锁到 HID 后 WPF 的帧不再进面板（否则两路的数值在面板里互相顶替 → 文字一直闪）。
    /// 另：指定了 HID 触摸屏时，也只留那一台的样本。
    /// </summary>
    private void NoteSampleForInfo(TouchSample s)
    {
        // 生效来源：手动模式 = 指定那路；自适应 = 已锁定那路，未锁定才按引擎实际生效
        ContactSource eff = Engine.Mode switch
        {
            SourceMode.RawHid => ContactSource.RawHid,
            SourceMode.SoftwareWpf => ContactSource.Wpf,
            _ => Engine.LockedSource != ContactSource.None ? Engine.LockedSource : Engine.ActiveSource,
        };

        if (eff != ContactSource.None)
        {
            bool ok = eff == ContactSource.RawHid
                ? s.Source == "RawHID"
                : s.Source is "WPF" or "STYLUS";   // STYLUS 只带压感，跟 WPF 一路
            if (!ok)
                return;
        }

        // 指定了 HID 触摸屏：别的设备的帧不进面板
        if (s.Source == "RawHID" && Engine.RawHidDeviceKey.Length > 0
            && !string.Equals(s.DeviceKey, Engine.RawHidDeviceKey, StringComparison.OrdinalIgnoreCase))
            return;

        _lastSample = s;
    }

    /// <summary>把一次触摸样本换算成物理像素矩形后交给引擎（多指=逐根各喂一次，各画各的框）。</summary>
    public void SubmitSample(TouchSample s)
    {
        NoteSampleForInfo(s);

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
                        if (c.DiuRect is not Rect r)
                            continue;
                        bool sized = r.Width > 0 && r.Height > 0;
                        // 只报坐标的设备：Bounds 为空 → 用最小占位尺寸让引擎建轨迹，供多点识别（不按尺寸判面积）
                        Rect px = sized ? ToPx(r) : PositionOnlyPx(r);
                        Engine.Submit(ContactSource.Wpf, c.Id, px, applyThreshold: sized,
                            detail: sized
                                ? $"#{c.Id} Bounds {Precision.Fmt(r.Width, 3)}×{Precision.Fmt(r.Height, 3)} DIP "
                                      + $"= {Precision.Fmt(r.Width * Engine.PxPerDiuX)}×{Precision.Fmt(r.Height * Engine.PxPerDiuY)} px"
                                : $"#{c.Id} 仅坐标（无接触尺寸）@({r.X:0.#},{r.Y:0.#}) DIP");
                    }
                    break;
                }
                if (s.DiuRect is not Rect r0)
                    return;
                {
                    bool sized0 = r0.Width > 0 && r0.Height > 0;
                    Rect px0 = sized0 ? ToPx(r0) : PositionOnlyPx(r0);
                    Engine.Submit(ContactSource.Wpf, 0, px0, applyThreshold: sized0,
                        detail: sized0
                            ? $"Bounds {Precision.Fmt(r0.Width, 3)}×{Precision.Fmt(r0.Height, 3)} DIP "
                                  + $"= {Precision.Fmt(r0.Width * Engine.PxPerDiuX)}×{Precision.Fmt(r0.Height * Engine.PxPerDiuY)} px"
                            : $"仅坐标（无接触尺寸）@({r0.X:0.#},{r0.Y:0.#}) DIP");
                }
                break;
        }
    }

    private Rect ToPx(Rect diu)
        => new(diu.X * Engine.PxPerDiuX, diu.Y * Engine.PxPerDiuY,
               diu.Width * Engine.PxPerDiuX, diu.Height * Engine.PxPerDiuY);

    /// <summary>只报坐标的接触（Bounds 为空）：在接触点处给一个最小正尺寸占位框，让引擎建轨迹做多点判定；
    /// 该框只用于定位/分组，不参与面积判定（下发给引擎时 applyThreshold=false）。</summary>
    private Rect PositionOnlyPx(Rect diu)
    {
        double cx = (diu.X + diu.Width / 2) * (Engine.PxPerDiuX > 0 ? Engine.PxPerDiuX : 1);
        double cy = (diu.Y + diu.Height / 2) * (Engine.PxPerDiuY > 0 ? Engine.PxPerDiuY : 1);
        const double s = 1.0;   // px，仅占位
        return new Rect(cx - s / 2, cy - s / 2, s, s);
    }

    /// <summary>把 HID 的尺寸（物理像素）+ 归一化坐标合成矩形喂给引擎。
    /// **多指 = 逐根接触各喂一次**（多指书写 / 多指擦除都保留，各按各的面积判定）；
    /// 「多点判手掌」由引擎按时间窗 + 大小范围判定。
    /// Xn/Yn 为数字化器归一化坐标 → 虚拟桌面 DIP → 预览区局部坐标。</summary>
    private void SubmitHidRect(TouchSample s)
    {
        double sx = Engine.PxPerDiuX > 0 ? Engine.PxPerDiuX : 1;
        double sy = Engine.PxPerDiuY > 0 ? Engine.PxPerDiuY : 1;
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        Point hostOrigin = Host.PointToScreen(new Point(0, 0));

        // 多指：逐根接触各算各的面积 / 位置 / 判定
        if (s.Contacts is { Count: > 1 } contacts)
        {
            (double fw, double fh) = HidScale.Factors(s.WidthLogMax, s.HeightLogMax, _resX, _resY, Engine.SizeScale);

            foreach (ContactRect c in contacts)
            {
                if (c.WLogical <= 0 || c.HLogical <= 0)
                    continue;
                double w = c.WLogical * fw;
                double h = c.HLogical * fh;
                if (w <= 0 || h <= 0)
                    continue;

                (double cx, double cy) = HidCenterToLocal(c.XNorm, c.YNorm, sx, sy, dpi, hostOrigin);
                Engine.Submit(ContactSource.RawHid, c.Id,
                    new Rect(cx - w / 2, cy - h / 2, w, h),
                    applyThreshold: false,
                    detail: $"#{c.Id} 计数 {c.WLogical}×{c.HLogical} → {Precision.Fmt(w)}×{Precision.Fmt(h)} px = {Precision.Fmt(w * h, 0)} px²",
                    deviceKey: s.DeviceKey);
            }
            return;
        }

        // 单指：沿用原路径
        (double? wPx, double? hPx) = HidScale.ContactPx(s, _resX, _resY, Engine.SizeScale);
        if (wPx is not double w1 || hPx is not double h1 || !(w1 > 0) || !(h1 > 0))
            return;

        (double cx0, double cy0) = HidCenterToLocal(s.XNorm, s.YNorm, sx, sy, dpi, hostOrigin);
        Engine.Submit(ContactSource.RawHid, 0,
            new Rect(cx0 - w1 / 2, cy0 - h1 / 2, w1, h1),
            applyThreshold: false,
            detail: $"HID 计数 {s.WidthLogical}/{s.WidthLogMax}、{s.HeightLogical}/{s.HeightLogMax}"
                  + $" ÷ 量程 × 分辨率({_resX}×{_resY}) = {Precision.Fmt(w1)}×{Precision.Fmt(h1)} px = {Precision.Fmt(w1 * h1, 0)} px²",
            deviceKey: s.DeviceKey);
    }

    /// <summary>HID 归一化坐标 → 预览区局部**物理像素**。**不夹取**：夹到预览区会把多点组的
    /// 跨度压到预览区大小以内（预览区通常比手掌还窄），导致"跨度永远 ≤ 手掌"而误判手掌。
    /// 画到区外没关系，Overlay/Ink 都有 ClipToBounds。</summary>
    private (double cx, double cy) HidCenterToLocal(double? xn, double? yn,
        double sx, double sy, DpiScale dpi, Point hostOrigin)
    {
        double cx, cy;
        if (xn is double x && yn is double y)
        {
            double screenDipX = SystemParameters.VirtualScreenLeft + Math.Clamp(x, 0, 1) * SystemParameters.VirtualScreenWidth;
            double screenDipY = SystemParameters.VirtualScreenTop + Math.Clamp(y, 0, 1) * SystemParameters.VirtualScreenHeight;
            cx = (screenDipX - hostOrigin.X / dpi.DpiScaleX) * sx;
            cy = (screenDipY - hostOrigin.Y / dpi.DpiScaleY) * sy;
        }
        else
        {
            cx = Host.ActualWidth / 2 * sx;
            cy = Host.ActualHeight / 2 * sy;
        }

        if (Environment.TickCount64 - _lastHidDiagTicks > 400)
        {
            _lastHidDiagTicks = Environment.TickCount64;
            Log.Info($"[HID坐标] xn={xn?.ToString("0.####") ?? "null"} yn={yn?.ToString("0.####") ?? "null"}"
                   + $" → ({cx:0},{cy:0}) sx={sx:0.####} host=({hostOrigin.X:0},{hostOrigin.Y:0}) hostSize={Host.ActualWidth:0}×{Host.ActualHeight:0}");
        }
        return (cx, cy);
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

    /// <summary>按勾选/选择同步控件：开关关掉就隐藏其内部的滑块；不相关的区块一并隐藏。
    /// - 未勾「压感」→ 隐藏其阈值/max 滑块；未勾「书写压感」→ 隐藏书写压感滑块；
    /// - 未启用「面积阈值」→ 隐藏阈值滑块 + 整个「书写」区块（永不书写）；
    /// - 未「随触摸尺寸」→ 隐藏「手掌下限」；
    /// - 「随触摸尺寸」与「手掌擦压感」都未开 → 隐藏「擦时只增不减」（面积恒定，没有可锁的量）；
    /// - 长宽比未选「自定义」→ 收起 W:H 输入；选了「自定义」→ 「触摸比例」失效，收起它。</summary>
    private void SyncEnableStates()
    {
        bool palmOn = PalmPressureCheck?.IsChecked == true;
        bool writeOn = WritingPressureCheck?.IsChecked == true;
        bool areaThrOn = AreaThresholdCheck?.IsChecked == true;
        bool multiOn = MultiTouchPalmCheck?.IsChecked == true;
        bool followSize = FollowSizeCheck?.IsChecked == true;
        bool aspectCustom = AspectCustom?.IsChecked == true;
        bool aspectContact = AspectContact?.IsChecked == true;

        if (PalmThrSlider is not null) PalmThrSlider.IsEnabled = palmOn;
        if (PalmMaxSlider is not null) PalmMaxSlider.IsEnabled = palmOn;
        if (PalmPressureDetailPanel is not null) PalmPressureDetailPanel.Visibility = Vis(palmOn);

        if (WritingThrSlider is not null) WritingThrSlider.IsEnabled = writeOn;
        if (WritingMaxSlider is not null) WritingMaxSlider.IsEnabled = writeOn;
        if (WritingPressureDetailPanel is not null) WritingPressureDetailPanel.Visibility = Vis(writeOn);

        if (AreaThresholdDetailPanel is not null) AreaThresholdDetailPanel.Visibility = Vis(areaThrOn);
        if (MultiTouchDetailPanel is not null) MultiTouchDetailPanel.Visibility = Vis(multiOn);
        if (WritingSection is not null) WritingSection.Visibility = Vis(areaThrOn || multiOn);

        // 「擦时只增不减」要靠“面积在本次按压内会变”才有意义：随尺寸（接触）或手掌擦压感至少开一个
        if (LockPalmSizeCheck is not null) LockPalmSizeCheck.Visibility = Vis(followSize || palmOn);

        if (PalmLimitPanel is not null) PalmLimitPanel.Visibility = Vis(followSize);

        if (AspectCustomPanel is not null) AspectCustomPanel.Visibility = Vis(aspectCustom);
        if (SizeScalePanel is not null) SizeScalePanel.Visibility = Vis(aspectContact);

        // 「手掌描摹」需要手掌尺寸；没有就灰掉并退回「跟随触摸尺寸」
        bool palmKnown = Engine.PalmWidthPx > 0 && Engine.PalmHeightPx > 0;
        if (AspectTrace is not null)
        {
            AspectTrace.IsEnabled = palmKnown;
            if (!palmKnown && AspectTrace.IsChecked == true && AspectContact is not null)
            {
                AspectContact.IsChecked = true;
                Engine.Aspect = AspectSource.Contact;
            }
        }
        if (AspectTraceHint is not null) AspectTraceHint.Visibility = Vis(!palmKnown);
    }

    private static Visibility Vis(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

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

        // 自定义长宽比也按形状给默认：矩形 9:14、椭圆 1:1
        SetAspectDefault(Engine.Shape);

        Engine.NotifyChanged();
    }

    /// <summary>按形状给「自定义长宽比」默认值：矩形 9:14、椭圆 1:1（改文本框会触发 OnAspectTextChanged → 落到引擎）。</summary>
    private void SetAspectDefault(EraserShape shape)
    {
        if (AspectWInput is null || AspectHInput is null)
            return;
        (string w, string h) = shape == EraserShape.Circle ? ("1", "1") : ("9", "14");
        if (AspectWInput.Text != w) AspectWInput.Text = w;
        if (AspectHInput.Text != h) AspectHInput.Text = h;
        ParseAspect();
    }

    private void OnAspectChanged(object sender, RoutedEventArgs e)
    {
        if (AspectCustom is null || AspectTrace is null || AspectContact is null)
            return;
        Engine.Aspect = AspectCustom.IsChecked == true ? AspectSource.Custom
            : AspectTrace.IsChecked == true ? AspectSource.Trace
            : AspectSource.Contact;
        ParseAspect();
        SyncEnableStates();
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

    private bool _suppressSizeUi;

    /// <summary>宽高换算下拉：归一（按屏幕拉伸）/ 不归一（1:1）。只影响擦除区形状，面积不变。</summary>
    private void OnSizeScaleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSizeUi || SizeScaleCombo is null)
            return;
        Engine.SetSizeScale(SizeScaleCombo.SelectedIndex == 1 ? HidSizeScale.Isotropic : HidSizeScale.Stretch);
    }

    private void OnFollowSizeChanged(object sender, RoutedEventArgs e)
    {
        if (FollowSizeCheck is null)
            return;
        Engine.FollowSize = FollowSizeCheck.IsChecked == true;
        SyncEnableStates();
        Engine.NotifyChanged();
    }

    private void OnLockPalmSizeChanged(object sender, RoutedEventArgs e)
    {
        if (LockPalmSizeCheck is null)
            return;
        Engine.LockPalmSize = LockPalmSizeCheck.IsChecked == true;
        Engine.NotifyChanged();
    }

    private void OnSmoothJitterChanged(object sender, RoutedEventArgs e)
    {
        if (SmoothJitterCheck is null)
            return;
        Engine.SmoothJitter = SmoothJitterCheck.IsChecked == true;
        Engine.NotifyChanged();
    }

    private void OnMultiTouchPalmChanged(object sender, RoutedEventArgs e)
    {
        if (MultiTouchPalmCheck is null)
            return;
        Engine.MultiTouchAsPalm = MultiTouchPalmCheck.IsChecked == true;
        SyncEnableStates();
        Engine.NotifyChanged();
    }

    private void OnMultiTouchMinChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MultiTouchMinCombo is null)
            return;
        Engine.MultiTouchPalmMin = Math.Max(2, MultiTouchMinCombo.SelectedIndex + 2);
        Engine.NotifyChanged();
    }

    private int ParsePalmWindow()
        => PalmGroupWindowBox is not null && int.TryParse(PalmGroupWindowBox.Text?.Trim(), out int v)
            ? Math.Clamp(v, 0, 2000) : 200;

    private void OnPalmGroupWindowChanged(object sender, RoutedEventArgs e)
    {
        if (PalmGroupWindowBox is null)
            return;
        Engine.PalmGroupWindowMs = ParsePalmWindow();
        PalmGroupWindowBox.Text = Engine.PalmGroupWindowMs.ToString();
        Engine.NotifyChanged();
    }

    private void OnPalmGroupWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        OnPalmGroupWindowChanged(sender, e);
        e.Handled = true;
    }

    private void OnPalmGroupTolChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Engine is null || PalmGroupTolSlider is null)
            return;
        Engine.PalmGroupTol = PalmGroupTolSlider.Value;
        if (PalmGroupTolValueText is not null)
            PalmGroupTolValueText.Text = $"×{PalmGroupTolSlider.Value:0.00}（手掌宽高的倍数上限）";
        Engine.NotifyChanged();
    }

    private void OnResetPalmGroupTol(object sender, RoutedEventArgs e)
    {
        if (PalmGroupTolSlider is not null)
            PalmGroupTolSlider.Value = 1.0;
    }

    /// <summary>「限制」三选一：不限 / 下限 / 上限。</summary>
    private void OnPalmLimitChanged(object sender, RoutedEventArgs e)
    {
        if (PalmLimitFloor is null)
            return;
        Engine.SizeLimit = CurrentPalmLimit();
        Engine.NotifyChanged();
    }

    /// <summary>读当前选中的限制方式（XAML 解析期控件可能未就绪 → 默认下限；全程 null 安全）。</summary>
    private PalmLimit CurrentPalmLimit()
        => PalmLimitCap?.IsChecked == true ? PalmLimit.Cap
         : PalmLimitNone?.IsChecked == true ? PalmLimit.None
         : PalmLimit.Floor;

    private void SetPalmLimitRadios(PalmLimit limit)
    {
        if (PalmLimitFloor is null)
            return;
        PalmLimitNone.IsChecked = limit == PalmLimit.None;
        PalmLimitFloor.IsChecked = limit == PalmLimit.Floor;
        PalmLimitCap.IsChecked = limit == PalmLimit.Cap;
    }

    private void OnAreaThresholdCheckChanged(object sender, RoutedEventArgs e)
    {
        if (AreaThresholdCheck is null)
            return;
        Engine.AreaThresholdEnabled = AreaThresholdCheck.IsChecked == true;
        SyncEnableStates();
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

    /// <summary>把当前样本的原始值整成一段文字（**内容稳定**：不含帧号/时刻，数值取整；
    /// 与原文字相同则不重写，避免每帧刷屏闪动）。</summary>
    private string BuildTouchInfo()
    {
        var sb = new System.Text.StringBuilder();

        TouchSample? s = _lastSample;
        if (s is null)
        {
            sb.Append("— 尚无样本（在预览区按一下触摸屏） —");
            return sb.ToString();
        }

        sb.Append("来源: ").Append(s.Source).Append("    触摸状态: ")
          .Append(Engine.HasLiveContact ? "按下" : "抬起 / 无接触").Append('\n');

        int n = s.Contacts?.Count ?? (s.DiuRect is not null || s.WidthMm is not null ? 1 : 0);
        sb.Append($"当前手指数: {n}").Append('\n');

        sb.Append("坐标: ").Append(FmtPos(s)).Append('\n');

        if (s.Pressure01 is double p01)
            sb.Append($"压感: {p01:0.000}（0~1，来自 {s.Source}）\n");
        else
            sb.Append("压感: 无\n");

        double? areaPx2 = SamplePx.AreaPx2(s, _resX, _resY, Engine.PxPerDiuX, Engine.PxPerDiuY);
        (double W, double H)? size = SamplePx.SizePx(s, _resX, _resY, Engine.SizeScale, Engine.PxPerDiuX, Engine.PxPerDiuY);
        if (size is not null)
            sb.Append($"尺寸（px）= {size.Value.W:0.#}×{size.Value.H:0.#}"
                    + $" → 面积 {(areaPx2 ?? 0):0} px²\n");

        if (s.Source == "RawHID")
        {
            (double fw, double fh) = HidScale.Factors(s.WidthLogMax, s.HeightLogMax, _resX, _resY, Engine.SizeScale);
            sb.Append($"HID 计数: W={s.WidthLogical}/{s.WidthLogMax}  H={s.HeightLogical}/{s.HeightLogMax}\n");
            sb.Append($"换算（{SourceNames.OfSizeScale(Engine.SizeScale)}）:"
                    + $" 每计数 {fw:0.######}/{fh:0.######} px"
                    + $" → {s.WidthLogical * fw:0.#}×{s.HeightLogical * fh:0.#} px\n");
        }

        if (s.Contacts is { Count: > 0 } cs)
        {
            sb.Append("分指明细:\n");
            foreach (ContactRect c in cs)
            {
                sb.Append("  #").Append(c.Id).Append("  ");
                if (c.DiuRect is Rect r)
                    sb.Append($"框 {r.X:0.#},{r.Y:0.#} {r.Width:0.#}×{r.Height:0.#} DIP"
                            + $" = {r.Width * Engine.PxPerDiuX:0.#}×{r.Height * Engine.PxPerDiuY:0.#} px");
                if (c.P01 is double cp)
                    sb.Append($"  P {cp:0.000}");
                sb.Append('\n');
            }
        }

        sb.Append("Detail: ").Append(s.Detail);
        return sb.ToString();
    }

    private static string FmtPos(TouchSample s)
    {
        if (s.XNorm is double xn && s.YNorm is double yn)
            return $"归一化 {xn:0.####}，{yn:0.####}";
        if (s.ScreenPxX is double sx && s.ScreenPxY is double sy)
            return $"屏幕 {sx:0.#}，{sy:0.#} px";
        return "—（该来源不带坐标）";
    }

    // ================= 重画 =================

    /// <summary>内容没变就不重写（TextBlock 赋值会触发布局/重绘，每帧重写会让文字闪动）。</summary>
    private static void SetText(System.Windows.Controls.TextBlock tb, string text)
    {
        if (tb is not null && tb.Text != text)
            tb.Text = text;
    }

    // ================= 简易画布（InkCanvas） =================

    private readonly Dictionary<int, List<Stroke>> _inkById = new();
    private readonly Dictionary<int, (double X, double Y)> _lastInkPos = new();
    private long _lastDiagTicks;
    private long _lastHidDiagTicks;

    /// <summary>诊断：每 ~0.5s 打一次各接触的判定（写/擦、是否多点判手掌、尺寸、位置）。</summary>
    private void DiagLogClassification()
    {
        long now = Environment.TickCount64;
        if (now - _lastDiagTicks < 500)
            return;
        _lastDiagTicks = now;

        var sb = new System.Text.StringBuilder();
        foreach ((int id, Rect c) in Engine.LastContacts)
            sb.Append($" #{id}→{(Engine.IsWriting(id) ? "写" : "擦")}[多点={Engine.IsPalmMulti(id)},定夺={Engine.IsPalmDecided(id)}]"
                    + $" {c.Width:0}×{c.Height:0}px@({c.X + c.Width / 2:0},{c.Y + c.Height / 2:0})");
        Log.Info($"[画布判定] 墨 {Ink?.Strokes.Count ?? 0} 笔（手掌 {Engine.PalmWidthPx:0}×{Engine.PalmHeightPx:0}px）|{sb}");
    }

    private void OnClearInk(object sender, RoutedEventArgs e)
    {
        Ink?.Strokes.Clear();
        _inkById.Clear();
        _lastInkPos.Clear();
    }

    /// <summary>书写：在画布上落一个墨点，并记在「这根的」名下（以便日后被判手掌时撤销）。</summary>
    private void AddInkDot(int id, double cx, double cy, double radiusDip)
    {
        if (Ink is null || radiusDip <= 0.5)
            return;
        if (_lastInkPos.TryGetValue(id, out var p) && Math.Abs(cx - p.X) < 3 && Math.Abs(cy - p.Y) < 3)
            return;
        _lastInkPos[id] = (cx, cy);

        var points = new StylusPointCollection { new StylusPoint(cx, cy) };
        var attrs = new DrawingAttributes
        {
            Color = Colors.White,
            Width = radiusDip * 2,
            Height = radiusDip * 2,
            FitToCurve = false,
            StylusTip = StylusTip.Ellipse,
        };
        var stroke = new Stroke(points, attrs);
        Ink.Strokes.Add(stroke);
        if (!_inkById.TryGetValue(id, out var list))
        {
            list = new List<Stroke>();
            _inkById[id] = list;
        }
        list.Add(stroke);
    }

    /// <summary>某根接触被改判为手掌擦 → 撤销它当初写下的墨迹。</summary>
    private void CancelInk(int id)
    {
        if (Ink is null)
            return;
        if (_inkById.TryGetValue(id, out var list))
        {
            foreach (Stroke st in list)
                Ink.Strokes.Remove(st);
            _inkById.Remove(id);
            Log.Info($"[画布] 撤销 #{id} 的 {list.Count} 笔墨（改判手掌）");
        }
        _lastInkPos.Remove(id);
    }

    /// <summary>手掌擦：抹掉与擦除区相交的墨迹。</summary>
    private void EraseInk(Rect r)
    {
        if (Ink is null || Ink.Strokes.Count == 0)
            return;
        var hit = new List<Stroke>();
        foreach (Stroke st in Ink.Strokes)
            if (st.GetBounds().IntersectsWith(r))
                hit.Add(st);
        foreach (Stroke st in hit)
        {
            Ink.Strokes.Remove(st);
            foreach (var kv in _inkById)
                kv.Value.Remove(st);
        }
        if (hit.Count > 0)
            Log.Info($"[画布] 手掌扫掉 {hit.Count} 笔墨");
    }

    private void Redraw()
    {
        if (Overlay is null)
            return;

        Overlay.Children.Clear();
        SetText(SourceInfoText, Engine.SourceInfo());
        SetText(KText, Engine.KInfo());
        SetText(AreaThresholdText, Engine.AreaThresholdInfo());
        SetText(PressureInfoText, Engine.PressureInfo());
        SetText(JudgeText, Engine.JudgeInfo());
        SetText(EraserInfoText, string.IsNullOrEmpty(Engine.SizeHint) ? Engine.EraserInfo() : Engine.SizeHint);
        DiagLogClassification();
        SetText(HidInfoText, BuildTouchInfo());
        AreaThresholdValueText.Text = Engine.HasThresholdRange
            ? $"当前 {Precision.Fmt(Engine.AreaThresholdPx2, 0)} px²"
            : "—（待标定）";

        // 抬手（来源过期）后不再画：形状应消失，只保留文字留档
        if (!Engine.HasLiveContact)
        {
            _lastInkPos.Clear();
            return;
        }

        double px2DiuX = Engine.PxPerDiuX > 0 ? Engine.PxPerDiuX : 1;
        double px2DiuY = Engine.PxPerDiuY > 0 ? Engine.PxPerDiuY : 1;

        // 多指：书写各画各的；手掌擦里，多点组**合成一个**（重心处、手掌同大），面积判定的各自画
        var multiPalm = new List<(double cx, double cy)>();

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
                AddInkDot(id, cx, cy, d / 2);
            }
            else if (Engine.IsPalmMulti(id))
            {
                // 多点判手掌：这根不单独画擦除区，收集起来到重心处合画一个
                CancelInk(id);
                multiPalm.Add((cx, cy));
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
                CancelInk(id);
                EraseInk(new Rect(cx - w / 2, cy - h / 2, w, h));
            }

            var dot = new Ellipse { Width = 6, Height = 6, Fill = Brushes.Yellow };
            Overlay.Children.Add(dot);
            Canvas.SetLeft(dot, cx - 3);
            Canvas.SetTop(dot, cy - 3);
        }

        // 多点组：在这些点的**重心**处画「一个」与手掌同大的擦除区
        if (multiPalm.Count > 0)
        {
            double mx = 0, my = 0;
            foreach ((double cx, double cy) in multiPalm)
            {
                mx += cx;
                my += cy;
            }
            mx /= multiPalm.Count;
            my /= multiPalm.Count;

            (double w, double h) = Engine.PalmSizeDiu();
            if (w > 0 && h > 0)
            {
                Shape shape = Engine.Shape == EraserShape.Circle
                    ? new Ellipse { Width = w, Height = h }
                    : new Rectangle { Width = w, Height = h };
                shape.Stroke = Brushes.Lime;
                shape.StrokeThickness = 2;
                shape.Fill = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0xFF, 0x00));
                Overlay.Children.Add(shape);
                Canvas.SetLeft(shape, mx - w / 2);
                Canvas.SetTop(shape, my - h / 2);
                EraseInk(new Rect(mx - w / 2, my - h / 2, w, h));
            }
        }
    }
}