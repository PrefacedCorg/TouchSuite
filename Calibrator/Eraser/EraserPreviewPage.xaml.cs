using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace TouchErase.Calibrator.Eraser;

/// <summary>
/// 「面积擦预览」页 —— 从主程序第③页整套搬过来的界面（预览区 + 全部参数），
/// 逻辑都在 <see cref="EraserEngine"/> 里，这里只负责摆放、绘制与把触摸样本喂给引擎。
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

        // 初始勾选状态同步进引擎（XAML 解析期事件早于字段赋值，会被 null 守卫挡掉）
        Engine.FollowSize = FollowSizeCheck.IsChecked == true;
        Engine.LockPalmSize = LockPalmSizeCheck.IsChecked == true;
        Engine.FollowPressure = FollowPressureCheck.IsChecked == true;
        Engine.AreaThresholdEnabled = AreaThresholdCheck.IsChecked == true;
        Engine.WritingUsesPressure = WritingPressureCheck.IsChecked == true;
        Engine.WritingFollowSize = WritingFollowSizeCheck.IsChecked == true;
        Engine.PressureGain = PressureGainSlider.Value;
        Engine.WritingPressureGain = WritingGainSlider.Value;

        // 定时刷新：所有来源过期后把「生效来源」归零，界面显示「无」
        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background,
            (_, _) => Engine.Tick(), Dispatcher);
        Loaded += (_, _) => _tick.Start();
        Unloaded += (_, _) => _tick.Stop();
    }

    private readonly DispatcherTimer _tick;

    // ================= 宿主喂入标定量 =================

    public void Setup(double mmPerDiuX, double mmPerDiuY, double palmAreaCm2, double palmAspect,
                      double areaThresholdMm2 = 0, double? pressureThreshold = null,
                      double palmPressAreaMm2 = 0)
    {
        Engine.MmPerDiuX = mmPerDiuX;
        Engine.MmPerDiuY = mmPerDiuY;
        Engine.PalmAreaCm2 = palmAreaCm2;
        Engine.PalmPressAreaMm2 = palmPressAreaMm2;
        Engine.PalmAspect = palmAspect;
        Engine.AreaThresholdBaseMm2 = areaThresholdMm2;
        Engine.PressureThresholdBase = pressureThreshold;
        Redraw();
    }

    /// <summary>载入已保存标定时，把两个阈值微调恢复成保存时的值（超出滑块范围则夹取）。</summary>
    public void ApplyTrims(double? areaTrim, double? pressureTrim)
    {
        if (areaTrim is double a)
            AreaTrimSlider.Value = Math.Clamp(a, AreaTrimSlider.Minimum, AreaTrimSlider.Maximum);
        if (pressureTrim is double p)
            PressureTrimSlider.Value = Math.Clamp(p, PressureTrimSlider.Minimum, PressureTrimSlider.Maximum);
    }

    /// <summary>把保存过的预览页开关/滑块灌回界面（载入标定时用）。</summary>
    public void ApplySettings(bool? followSize, bool? lockPalm, bool? followPressure, double? pressureGain,
                              bool? areaThreshold, bool? writingPressure, double? writingGain,
                              double? ratioTrim, string? shape, bool? writingFollowSize = null)
    {
        if (followSize is bool fs) FollowSizeCheck.IsChecked = fs;
        if (lockPalm is bool lp) LockPalmSizeCheck.IsChecked = lp;
        if (followPressure is bool fp) FollowPressureCheck.IsChecked = fp;
        if (areaThreshold is bool at) AreaThresholdCheck.IsChecked = at;
        if (writingPressure is bool wp) WritingPressureCheck.IsChecked = wp;
        if (writingFollowSize is bool wfs) WritingFollowSizeCheck.IsChecked = wfs;
        if (ratioTrim is double rt)
            RatioTrimSlider.Value = Math.Clamp(rt, RatioTrimSlider.Minimum, RatioTrimSlider.Maximum);
        if (pressureGain is double pg)
            PressureGainSlider.Value = Math.Clamp(pg, PressureGainSlider.Minimum, PressureGainSlider.Maximum);
        if (writingGain is double wg)
            WritingGainSlider.Value = Math.Clamp(wg, WritingGainSlider.Minimum, WritingGainSlider.Maximum);
        if (shape is not null)
        {
            bool circle = string.Equals(shape, "Circle", StringComparison.OrdinalIgnoreCase);
            ShapeCircle.IsChecked = circle;
            ShapeRect.IsChecked = !circle;
        }

        Engine.NotifyChanged();
    }

    /// <summary>当前界面设置快照（保存标定时写进 JSON）。</summary>
    public (bool FollowSize, bool LockPalmSize, bool FollowPressure, double PressureGain,
            bool AreaThresholdEnabled, bool WritingUsesPressure, double WritingPressureGain,
            double RatioTrim, string Shape, bool WritingFollowSize) SettingsSnapshot
        => (Engine.FollowSize, Engine.LockPalmSize, Engine.FollowPressure, Engine.PressureGain,
            Engine.AreaThresholdEnabled, Engine.WritingUsesPressure, Engine.WritingPressureGain,
            Engine.RatioTrim, Engine.Shape.ToString(), Engine.WritingFollowSize);

    // ================= 触摸样本 → 引擎 =================

    /// <summary>最近一次喂入的样本（HID 信息面板回显用；抬手后仍显示最后看到的原始值）。</summary>
    private TouchSample? _lastSample;

    /// <summary>最近一次「原始 HID 通路」样本（RawHID / HidSetup），用于面积对比栏的 HID 侧。</summary>
    private TouchSample? _lastHidSample;

    /// <summary>最近一次「WPF 通路」样本，用于面积对比栏的 WPF 侧。</summary>
    private TouchSample? _lastWpfSample;

    /// <summary>
    /// HID 信息面板用的能力/换算表文字（由宿主在 Attach 后灌一次）。
    /// 传 null / 空则显示"设备未声明"。
    /// </summary>
    public void SetHidCapabilities(
        string deviceName, int maxContacts, int linkCollections,
        bool hasW, bool hasH, bool hasP, bool hasContactId, bool hasTip,
        string wScale, string hScale, string unread)
    {
        _hidDeviceName = deviceName;
        _hidMaxContacts = maxContacts;
        _hidLinkCollections = linkCollections;
        _hidHasW = hasW; _hidHasH = hasH; _hidHasP = hasP;
        _hidHasContactId = hasContactId; _hidHasTip = hasTip;
        _hidWScale = wScale; _hidHScale = hScale; _hidUnread = unread;
    }

    private string _hidDeviceName = "(未发现触摸屏)";
    private int _hidMaxContacts;
    private int _hidLinkCollections;
    private bool _hidHasW, _hidHasH, _hidHasP, _hidHasContactId, _hidHasTip;
    private string _hidWScale = "—", _hidHScale = "—", _hidUnread = "";

    private string _pointerReport = "";
    private string _pointerTouchNames = "";
    private bool _pointerDeclaresSize;
    private bool _pointerDeclaresPressure;

    /// <summary>WM_POINTER 设备属性探测结果（由宿主启动时灌入；触摸屏真身走这条路）。</summary>
    public void SetPointerProbe(string report, string touchNames, bool declaresSize, bool declaresPressure)
    {
        _pointerReport = report;
        _pointerTouchNames = touchNames;
        _pointerDeclaresSize = declaresSize;
        _pointerDeclaresPressure = declaresPressure;
    }

    /// <summary>把一次触摸样本按来源换算成预览区坐标后交给引擎（多指=逐根各喂一次，各画各的框）。</summary>
    public void SubmitSample(TouchSample s)
    {
        _lastSample = s;

        // 按通路各留一份最近样本，供「面积对比」栏并排显示两条通路的面积。
        if (s.Source is "RawHID" or "HidSetup")
            _lastHidSample = s;
        else if (s.Source is "WPF")
            _lastWpfSample = s;

        if (Engine.MmPerDiuX <= 0 || Engine.MmPerDiuY <= 0)
            return;

        // 实时压感喂给引擎（"随压力"模式用它缩放擦除区；WPF 面积样本无压感，保留上一值但会随新鲜窗口过期成 0）
        Engine.NotePressure(s.Pressure01);

        switch (s.Source)
        {
            // RawInput 与 SetupAPI 直读同属"设备上报真值"，只是分发到不同的 ContactSource，供仲裁区分。
            // 软件（屏幕尺度）推算口径：软件HID 模式下用屏幕标定换算计数，不走驱动的 W/H 换算表。
            case "RawHID":
                SubmitHidRoute(s, ContactSource.RawHid);
                break;

            case "HidSetup":
                SubmitHidRoute(s, ContactSource.HidSetup);
                break;

            case "STYLUS":
                {
                    // 只有压感、没有尺寸：让引擎按最新压力重算并重画（"随压力变化"模式靠这条实时生效）
                    if (s.Pressure01 is not null)
                        Engine.NotifyChanged();
                    break;
                }

            case "WPF":
                {
                    if (s.Contacts is { Count: > 0 } list)
                    {
                        foreach (ContactRect c in list)
                        {
                            if (c.DiuRect is not Rect r || r.Width <= 0 || r.Height <= 0)
                                continue;
                            Engine.Submit(ContactSource.Wpf, c.Id, r, applyThreshold: true,
                                detail: $"#{c.Id} Bounds {Precision.Fmt(r.Width, 3)}×{Precision.Fmt(r.Height, 3)} DIP");
                        }
                        break;
                    }
                    if (s.DiuRect is not Rect r0 || r0.Width <= 0 || r0.Height <= 0)
                        return;
                    Engine.Submit(ContactSource.Wpf, 0, r0, applyThreshold: true,
                        detail: $"Bounds {Precision.Fmt(r0.Width, 3)}×{Precision.Fmt(r0.Height, 3)} DIP");
                    break;
                }
        }
    }

    /// <summary>原始HID 两条路的分发：软件HID 模式走屏幕尺度推算，其余模式走驱动换算表的设备上报值。</summary>
    private void SubmitHidRoute(TouchSample s, ContactSource src)
    {
        if (Engine.Mode == SourceMode.SoftwareHid)
            SubmitSoftwareHidContact(s, src);
        else
            SubmitDriverContact(s, src);
    }

    /// <summary>设备上报真值（RawInput / SetupAPI 直读）的统一喂入：按分指列表逐根喂，无列表则用整体尺寸。</summary>
    private void SubmitDriverContact(TouchSample s, ContactSource src)
    {
        if (s.Contacts is { Count: > 0 } list)
        {
            // 多指：每根接触单独喂（各自算面积、各自画框）
            foreach (ContactRect c in list)
            {
                if (c.WMm is not double wv || c.HMm is not double hv)
                    continue;   // 该设备报的尺寸不可用，交给 WPF 通路
                Point ctr = ContactCenter(c.XNorm, c.YNorm, s);
                Engine.Submit(src, c.Id,
                    new Rect(ctr.X - wv / Engine.MmPerDiuX / 2, ctr.Y - hv / Engine.MmPerDiuY / 2,
                             wv / Engine.MmPerDiuX, hv / Engine.MmPerDiuY),
                    applyThreshold: false,
                    detail: $"#{c.Id} W={c.WLogical} → {Precision.Fmt(wv)}×{Precision.Fmt(hv)} mm");
            }
            return;
        }

        if (s.WidthMm is not double w0 || s.HeightMm is not double h0)
            return; // 该设备报的尺寸不可用，交给 WPF 通路
        double wDiu = w0 / Engine.MmPerDiuX;
        double hDiu = h0 / Engine.MmPerDiuY;
        Point p0 = ContactCenter(s.XNorm, s.YNorm, s);
        Engine.Submit(src, 0,
            new Rect(p0.X - wDiu / 2, p0.Y - hDiu / 2, wDiu, hDiu),
            applyThreshold: false,
            detail: $"W={s.WidthLogical} → {Precision.Fmt(w0)}×{Precision.Fmt(h0)} mm");
    }

    /// <summary>
    /// 「软件HID」模式喂入：不用驱动的 W/H 换算表，用屏幕标定尺度把原始计数推算成尺寸
    /// （W 按 X 轴尺度 = 屏宽/逻辑量程，H 按 Y 轴尺度 = 屏高/逻辑量程）。
    /// 厂商把换算表填错的场景（如 LKS-238 把 Width 的物理量程错填成 Y 的）下，这是独立口径。
    /// </summary>
    private void SubmitSoftwareHidContact(TouchSample s, ContactSource src)
    {
        if (s.XLogMax is not int xm || xm <= 0 || s.YLogMax is not int ym || ym <= 0)
            return;   // 设备没报 X/Y 逻辑量程 → 无法软件推算

        (double screenWmm, double screenHmm) = SoftwareHidScale.ScreenMm(Engine.MmPerDiuX, Engine.MmPerDiuY);

        void Feed(int id, int wLogical, int hLogical, double? xNorm, double? yNorm)
        {
            if (wLogical <= 0 || hLogical <= 0)
                return;
            double wMm = (double)wLogical / xm * screenWmm;
            double hMm = (double)hLogical / ym * screenHmm;
            Point ctr = ContactCenter(xNorm, yNorm, s);
            Engine.Submit(src, id,
                new Rect(ctr.X - wMm / Engine.MmPerDiuX / 2, ctr.Y - hMm / Engine.MmPerDiuY / 2,
                         wMm / Engine.MmPerDiuX, hMm / Engine.MmPerDiuY),
                applyThreshold: false,
                detail: $"#{id} 软件推算 W={wLogical}/{xm}→{Precision.Fmt(wMm)}×{Precision.Fmt(hMm)} mm（屏尺度，非驱动表）");
        }

        if (s.Contacts is { Count: > 0 } list)
        {
            foreach (ContactRect c in list)
                Feed(c.Id, c.WLogical, c.HLogical, c.XNorm, c.YNorm);
            return;
        }

        Feed(0, s.WidthLogical, s.HeightLogical, s.XNorm, s.YNorm);
    }

    /// <summary>把一根接触的屏幕/归一化坐标换算成预览区内的坐标；拿不到就用中心。</summary>
    private Point ContactCenter(double? xNorm, double? yNorm, TouchSample s)
    {
        try
        {
            Point origin = Host.PointToScreen(new Point(0, 0));   // 设备像素
            DpiScale dpi = VisualTreeHelper.GetDpi(this);

            if (xNorm is double xn && yNorm is double yn)
            {
                // 归一化 → 虚拟桌面 DIP（与原实现一致）
                double sx = SystemParameters.VirtualScreenLeft + xn * SystemParameters.VirtualScreenWidth;
                double sy = SystemParameters.VirtualScreenTop + yn * SystemParameters.VirtualScreenHeight;
                return Clamp(new Point(sx - origin.X / dpi.DpiScaleX, sy - origin.Y / dpi.DpiScaleY));
            }

            if (s.ScreenPxX is double px && s.ScreenPxY is double py)
                return Clamp(new Point((px - origin.X) / dpi.DpiScaleX, (py - origin.Y) / dpi.DpiScaleY));
        }
        catch
        {
            // 布局未完成时忽略，退回中心
        }

        return new Point(Host.ActualWidth / 2, Host.ActualHeight / 2);
    }

    private Point Clamp(Point p)
    {
        double x = Host.ActualWidth > 0 ? Math.Clamp(p.X, 0, Host.ActualWidth) : p.X;
        double y = Host.ActualHeight > 0 ? Math.Clamp(p.Y, 0, Host.ActualHeight) : p.Y;
        return new Point(x, y);
    }

    // ================= 参数控件 =================

    private void OnSourceModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceModeCombo is null)
            return; // XAML 解析期提前触发
        Engine.SetMode(SourceModeCombo.SelectedIndex switch
        {
            1 => SourceMode.RawInput,
            2 => SourceMode.HidSetup,
            3 => SourceMode.SoftwareHid,
            4 => SourceMode.SoftwareWpf,
            _ => SourceMode.Auto,
        });
    }

    private void OnShapeChanged(object sender, RoutedEventArgs e)
    {
        if (ShapeCircle is null)
            return;
        Engine.Shape = ShapeCircle.IsChecked == true ? EraserShape.Circle : EraserShape.Rectangle;
        Engine.NotifyChanged();
    }

    private void OnRatioTrimChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RatioText is null)
            return;
        Engine.RatioTrim = e.NewValue;
        Engine.NotifyChanged();
    }

    private void OnAreaTrimChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AreaThresholdText is null)
            return;
        Engine.AreaThresholdTrim = e.NewValue;
        Engine.NotifyChanged();
    }

    private void OnPressureTrimChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PressureThresholdText is null)
            return;
        Engine.PressureThresholdTrim = e.NewValue;
        Engine.NotifyChanged();
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

    private void OnFollowPressureChanged(object sender, RoutedEventArgs e)
    {
        if (FollowPressureCheck is null)
            return;
        Engine.FollowPressure = FollowPressureCheck.IsChecked == true;
        Engine.NotifyChanged();
    }

    private void OnPressureGainChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PressureGainText is null)
            return;
        Engine.PressureGain = e.NewValue;
        PressureGainText.Text = $"随压力倍数 ×{e.NewValue:0.00}";
        Engine.NotifyChanged();
    }

    private void OnWritingGainChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (WritingGainText is null)
            return;
        Engine.WritingPressureGain = e.NewValue;
        WritingGainText.Text = $"书写压感倍数 ×{e.NewValue:0.00}";
        Engine.NotifyChanged();
    }

    private void OnAreaThresholdChanged(object sender, RoutedEventArgs e)
    {
        if (AreaThresholdCheck is null)
            return;
        Engine.AreaThresholdEnabled = AreaThresholdCheck.IsChecked == true;
        Engine.NotifyChanged();
    }

    private void OnWritingPressureChanged(object sender, RoutedEventArgs e)
    {
        if (WritingPressureCheck is null)
            return;
        Engine.WritingUsesPressure = WritingPressureCheck.IsChecked == true;
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

    // ================= HID 信息面板 =================

    /// <summary>把驱动实际上报的原始值整成一段文字：触摸状态 / 坐标 / 尺寸 / 接触ID / 手指数 / 压感 / 换算表 / 倾斜角 / 方向。</summary>
    private string BuildHidInfo()
    {
        var sb = new System.Text.StringBuilder();

        // ---- 面积对比（两条通路并排，回答"WPF 面积 ≈ HID Width×Height？"）----
        sb.Append(BuildAreaCompare());

        // ---- 设备与能力（RawInput HID 通路）----
        sb.Append("────────────────\n");
        sb.Append("【RawInput HID 通路】").Append('\n');
        sb.Append("设备: ").Append(_hidDeviceName).Append('\n');
        sb.Append("能力: ")
          .Append($"宽={Yn(_hidHasW)} 高={Yn(_hidHasH)} 压感={Yn(_hidHasP)} ")
          .Append($"接触ID={Yn(_hidHasContactId)} tip={Yn(_hidHasTip)}");
        sb.Append('\n');
        sb.Append($"链集合={_hidLinkCollections}（每根手指一个）  最大接触数(0x55)=")
          .Append(_hidMaxContacts > 0 ? _hidMaxContacts.ToString() : "未声明");
        sb.Append('\n');

        // ---- 换算表 ----
        sb.Append("换算表: ").Append(_hidWScale).Append('\n');
        sb.Append("        ").Append(_hidHScale).Append('\n');
        sb.Append("倾斜角/方向: ")
          .Append(string.IsNullOrEmpty(_hidUnread) ? "驱动未声明（本项目也无此读取项）" : _hidUnread + "（驱动有，本项目未读）")
          .Append('\n');

        // ---- WM_POINTER 通路（触摸屏真身）----
        if (!string.IsNullOrEmpty(_pointerReport))
        {
            sb.Append("────────────────\n");
            sb.Append("【WM_POINTER 通路】（触摸屏走这条，RawInput 扫不到）\n");
            if (!string.IsNullOrEmpty(_pointerTouchNames))
                sb.Append("触摸屏: ").Append(_pointerTouchNames).Append('\n');
            sb.Append("接触尺寸(0x48/0x49): ").Append(_pointerDeclaresSize ? "★会报" : "不报（面积需软件推算）")
              .Append("   压感(0x30): ").Append(_pointerDeclaresPressure ? "★会报" : "不报").Append('\n');
            sb.Append(_pointerReport).Append('\n');
        }

        // ---- 本帧样本 ----
        TouchSample? s = _lastSample;
        if (s is null)
        {
            sb.Append("— 尚无样本（在预览区按一下真实触摸屏） —");
            return sb.ToString();
        }

        sb.Append("────────────────\n");
        sb.Append("来源: ").Append(s.Source).Append("   帧=").Append(s.FrameId)
          .Append("   t=").Append(s.TimeMs > 0 ? s.TimeMs + "ms" : "—").Append('\n');

        // 触摸状态：有接触=按下，无=抬起；tip 由驱动决定
        bool live = Engine.HasLiveContact;
        sb.Append("触摸状态: ").Append(live ? "按下（有活动接触）" : "抬起 / 无接触").Append('\n');

        // 手指数
        int n = s.Contacts?.Count ?? (s.DiuRect is not null || s.WidthMm is not null ? 1 : 0);
        sb.Append($"当前手指数: {n}    最大手指数: ").Append(_hidMaxContacts > 0 ? _hidMaxContacts.ToString() : "未声明");
        sb.Append('\n');

        // 整体坐标（面积加权中心 / WPF 框）
        sb.Append("坐标: ").Append(FmtPos(s)).Append('\n');

        // 按压感：0..1024 原生计数 + 0~1
        if (s.PressureRaw is int praw)
            sb.Append($"压感: {praw} / 1024  ({Precision.Fmt(s.Pressure01, 4)})  量程 {s.PressureRange}\n");
        else if (s.Pressure01 is double p01)
            sb.Append($"压感: {Precision.Fmt(p01, 4)}（0~1，来自 {s.Source}，无原生量程）\n");
        else
            sb.Append("压感: 无\n");

        // 尺寸：整体
        if (s.WidthMm is not null || s.HeightMm is not null)
            sb.Append($"尺寸(整体): {Precision.Fmt(s.WidthMm)}×{Precision.Fmt(s.HeightMm)} mm")
              .Append($"  原始计数 {s.WidthLogical}×{s.HeightLogical}")
              .Append($"  面积 {Precision.Fmt(s.AreaMm2, 0)} mm²\n");
        else
            sb.Append("尺寸(整体): 无（该来源未报尺寸）\n");

        // 分指明细
        if (s.Contacts is { Count: > 0 } cs)
        {
            sb.Append("分指明细:\n");
            foreach (ContactRect c in cs)
            {
                sb.Append("  #").Append(c.Id).Append("  ");
                if (c.DiuRect is Rect r)
                    sb.Append($"框 {Precision.Fmt(r.X)}，{Precision.Fmt(r.Y)} {Precision.Fmt(r.Width)}×{Precision.Fmt(r.Height)} DIP");
                if (c.WMm is not null || c.HMm is not null)
                    sb.Append($"  {Precision.Fmt(c.WMm)}×{Precision.Fmt(c.HMm)} mm（计数 {c.WLogical}×{c.HLogical}）");
                if (c.XNorm is double xn && c.YNorm is double yn)
                    sb.Append($"  归一化 {Precision.Fmt(xn, 4)}，{Precision.Fmt(yn, 4)}");
                if (c.P01 is double cp)
                    sb.Append($"  P {Precision.Fmt(cp, 4)}");
                sb.Append('\n');
            }
        }

        // 原始编码（HID 才有）
        sb.Append("驱动原始 Detail: ").Append(s.Detail);
        return sb.ToString();
    }

    private static string Yn(bool b) => b ? "✓" : "✗";

    /// <summary>
    /// 「面积对比」栏：把原始 HID 通路（驱动上报 Width×Height）与 WPF 通路（系统接触框）的
    /// 接触面积并排列出，并算出差值 / 比值，直观验证「WPF 面积 ≈ HID Width×Height」。
    /// 两条通路各自独立到达，故各留最近一份样本（抬手后仍显示最后值，便于读数）。
    /// </summary>
    private string BuildAreaCompare()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("【面积对比】原始 HID Width×Height  vs  WPF 接触框\n");

        double? hidArea = AreaOf(_lastHidSample, out string hidText);
        double? wpfArea = AreaOf(_lastWpfSample, out string wpfText);

        sb.Append("· HID : ").Append(hidText).Append('\n');
        sb.Append("· WPF : ").Append(wpfText).Append('\n');

        if (hidArea is double ha && wpfArea is double wa && ha > 0 && wa > 0)
        {
            double diff = wa - ha;
            double pct = diff / ha * 100.0;
            sb.Append($"· 对比: 差 {diff:+0;-0;0} mm²（WPF/HID = {wa / ha:0.000}×，{pct:+0.0;-0.0;0.0}%）");
            // 差异来源判断：两条通路都源自 HID 的 Width/Height，理论应接近；
            // 明显偏差通常来自 DIU 缩放 / DPI 舍入 / 驱动分辨率量化。
            if (Math.Abs(pct) <= 5)
                sb.Append("  ≈ 吻合\n");
            else
                sb.Append("  ⚠ 偏差偏大（多为 DIP 缩放/DPI 舍入）\n");
        }
        else
        {
            sb.Append("· 对比: 需两条通路各出现一次接触后才能比较（在预览区按一下触摸屏）\n");
        }

        if (_lastHidSample is not null)
            sb.Append($"  HID 来源: {_lastHidSample.Source}  W={_lastHidSample.WidthLogical} H={_lastHidSample.HeightLogical}\n");

        return sb.ToString();
    }

    /// <summary>取一个样本的接触面积（mm²）：带尺寸直接算；WPF 侧只有 DIP 框，用 mm/DIP 换算。</summary>
    private double? AreaOf(TouchSample? s, out string text)
    {
        if (s is null)
        {
            text = "（尚无样本）";
            return null;
        }

        // HID 侧：优先分指列表，其次整体 WidthMm×HeightMm
        if (s.AreaMm2 is double a && a > 0)
        {
            text = $"面积 {Precision.Fmt(a, 0)} mm²" +
                   (s.WidthMm is double w && s.HeightMm is double h
                       ? $"（{Precision.Fmt(w)}×{Precision.Fmt(h)} mm）"
                       : $"（{s.Contacts?.Count ?? 0} 指求和）");
            return a;
        }

        // WPF 侧：DiuRect（或分指 DiuRect）按 mm/DIP 折算到 mm²
        if (Engine.MmPerDiuX > 0 && Engine.MmPerDiuY > 0)
        {
            double mmx = Engine.MmPerDiuX, mmy = Engine.MmPerDiuY;
            if (s.Contacts is { Count: > 0 } list)
            {
                double sum = 0; bool any = false;
                foreach (ContactRect c in list)
                    if (c.DiuRect is Rect rc && rc.Width > 0 && rc.Height > 0)
                    { sum += rc.Width * mmx * rc.Height * mmy; any = true; }
                if (any)
                {
                    text = $"面积 {Precision.Fmt(sum, 0)} mm²（{list.Count} 指 DIP 框 × {Precision.Fmt(mmx)}/{Precision.Fmt(mmy)} mm/DIP）";
                    return sum;
                }
            }
            if (s.DiuRect is Rect r && r.Width > 0 && r.Height > 0)
            {
                double mm2 = r.Width * mmx * r.Height * mmy;
                text = $"面积 {Precision.Fmt(mm2, 0)} mm²（DIP {Precision.Fmt(r.Width, 3)}×{Precision.Fmt(r.Height, 3)} × {Precision.Fmt(mmx)}/{Precision.Fmt(mmy)} mm/DIP）";
                return mm2;
            }
        }

        text = "（该样本未带尺寸/DIP 框）";
        return null;
    }

    private static string FmtPos(TouchSample s)
    {
        if (s.XNorm is double xn && s.YNorm is double yn)
            return $"归一化 {Precision.Fmt(xn, 4)}，{Precision.Fmt(yn, 4)}" +
                   (s.ScreenPxX is double px && s.ScreenPxY is double py
                       ? $"   屏幕 {Precision.Fmt(px, 1)}，{Precision.Fmt(py, 1)} px" : "");
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
        RatioText.Text = Engine.RatioInfo();
        AreaThresholdText.Text = Engine.AreaThresholdInfo();
        PressureThresholdText.Text = Engine.PressureThresholdInfo();
        JudgeText.Text = Engine.JudgeInfo();
        EraserInfoText.Text = string.IsNullOrEmpty(Engine.SizeHint) ? Engine.EraserInfo() : Engine.SizeHint;
        HidInfoText.Text = BuildHidInfo();

        // 抬手（来源过期）后不再画：形状应消失，只保留文字留档
        if (!Engine.HasLiveContact)
            return;

        // 多指：每根接触各画各的框（各按各的面积/判定）
        foreach ((int id, Rect c) in Engine.LastContacts)
        {
            double cx = c.X + c.Width / 2;
            double cy = c.Y + c.Height / 2;

            if (Engine.IsWriting(id))
            {
                // 书写：蓝点，直径 = 6 + 22 × 压感倍数 × 压感；开启「书写随尺寸」时再按该指接触面积缩放
                double d = Engine.WritingDotDiameterOf(c);
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
            else if (Engine.Shape == EraserShape.Circle)
            {
                if (Engine.CircleDiameterDiu(id, c) is not double d)
                    continue;
                var circle = new Ellipse
                {
                    Width = d,
                    Height = d,
                    Stroke = Brushes.Lime,
                    StrokeThickness = 2,
                    Fill = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0xFF, 0x00)),
                };
                Overlay.Children.Add(circle);
                Canvas.SetLeft(circle, cx - d / 2);
                Canvas.SetTop(circle, cy - d / 2);
            }
            else
            {
                var (w, h) = Engine.RectSizeDiu(id, c);
                if (w <= 0 || h <= 0)
                    continue;
                var rect = new Rectangle
                {
                    Width = w,
                    Height = h,
                    Stroke = Brushes.Lime,
                    StrokeThickness = 2,
                    Fill = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0xFF, 0x00)),
                };
                Overlay.Children.Add(rect);
                Canvas.SetLeft(rect, cx - w / 2);
                Canvas.SetTop(rect, cy - h / 2);
            }

            var dot = new Ellipse { Width = 6, Height = 6, Fill = Brushes.Yellow };
            Overlay.Children.Add(dot);
            Canvas.SetLeft(dot, cx - 3);
            Canvas.SetTop(dot, cy - 3);
        }
    }
}
