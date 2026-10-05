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

    /// <summary>最近一次喂入的样本（抬手后仍显示最后看到的值）。</summary>
    private TouchSample? _lastSample;

    /// <summary>把一次触摸样本按来源换算成预览区坐标后交给引擎（多指=逐根各喂一次，各画各的框）。</summary>
    public void SubmitSample(TouchSample s)
    {
        _lastSample = s;

        if (Engine.MmPerDiuX <= 0 || Engine.MmPerDiuY <= 0)
            return;

        // 实时压感喂给引擎（"随压力"模式用它缩放擦除区；WPF 面积样本无压感，保留上一值但会随新鲜窗口过期成 0）
        Engine.NotePressure(s.Pressure01);

        switch (s.Source)
        {
            case "RawHID":
                {
                    // 位置与尺寸都用 HID 自己的（归一化 Xn/Yn → 虚拟桌面 → 预览区局部坐标）
                    SubmitHidRect(s.WidthMm, s.HeightMm, s.XNorm, s.YNorm);
                    break;
                }

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

    private Point HostCenter => new(Host.ActualWidth / 2, Host.ActualHeight / 2);

    /// <summary>把 HID 的尺寸 + 归一化坐标合成一个原始HID 矩形喂给引擎（id 固定 0：HID 是单触点）。
    /// Xn/Yn 为数字化器归一化坐标 → 虚拟桌面 DIP → 预览区局部坐标（与主程序同一套换算法）。</summary>
    private void SubmitHidRect(double? wMm, double? hMm, double? xn, double? yn)
    {
        if (wMm is not double wv || hMm is not double hv || Engine.MmPerDiuX <= 0 || Engine.MmPerDiuY <= 0)
            return;
        double wDiu = wv / Engine.MmPerDiuX;
        double hDiu = hv / Engine.MmPerDiuY;

        Point center;
        if (xn is double x && yn is double y)
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            Point hostOrigin = Host.PointToScreen(new Point(0, 0));   // 设备像素
            double screenDipX = SystemParameters.VirtualScreenLeft + Math.Clamp(x, 0, 1) * SystemParameters.VirtualScreenWidth;
            double screenDipY = SystemParameters.VirtualScreenTop + Math.Clamp(y, 0, 1) * SystemParameters.VirtualScreenHeight;
            center = new Point(screenDipX - hostOrigin.X / dpi.DpiScaleX,
                               screenDipY - hostOrigin.Y / dpi.DpiScaleY);
        }
        else
        {
            center = HostCenter;
        }

        // 夹取到预览区内，避免坐标异常时把擦除区画到看不见的地方
        if (Host.ActualWidth > 0)
            center.X = Math.Clamp(center.X, 0, Host.ActualWidth);
        if (Host.ActualHeight > 0)
            center.Y = Math.Clamp(center.Y, 0, Host.ActualHeight);

        Engine.Submit(ContactSource.RawHid, 0,
            new Rect(center.X - wDiu / 2, center.Y - hDiu / 2, wDiu, hDiu),
            applyThreshold: false,
            detail: $"HID {Precision.Fmt(wv)}×{Precision.Fmt(hv)} mm");
    }

    // ================= 参数控件 =================

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

    // ================= 触摸信息面板 =================

    /// <summary>把 WPF 通路当前样本的原始值整成一段文字：来源 / 触摸状态 / 坐标 / 尺寸 / 手指数 / 压感 / 分指明细。</summary>
    private string BuildTouchInfo()
    {
        var sb = new System.Text.StringBuilder();

        // ---- 本帧样本 ----
        TouchSample? s = _lastSample;
        if (s is null)
        {
            sb.Append("— 尚无样本（在预览区按一下触摸屏） —");
            return sb.ToString();
        }

        sb.Append("来源: ").Append(s.Source).Append("   帧=").Append(s.FrameId)
          .Append("   t=").Append(s.TimeMs > 0 ? s.TimeMs + "ms" : "—").Append('\n');

        // 触摸状态：有接触=按下，无=抬起
        bool live = Engine.HasLiveContact;
        sb.Append("触摸状态: ").Append(live ? "按下（有活动接触）" : "抬起 / 无接触").Append('\n');

        // 手指数
        int n = s.Contacts?.Count ?? (s.DiuRect is not null || s.WidthMm is not null ? 1 : 0);
        sb.Append($"当前手指数: {n}");
        sb.Append('\n');

        // 整体坐标
        sb.Append("坐标: ").Append(FmtPos(s)).Append('\n');

        // 压感：WPF 面积通路无原生量程；STYLUS 通路给 0~1
        if (s.Pressure01 is double p01)
            sb.Append($"压感: {Precision.Fmt(p01, 4)}（0~1，来自 {s.Source}，无原生量程）\n");
        else
            sb.Append("压感: 无\n");

        // 尺寸（整体）
        if (s.WidthMm is not null || s.HeightMm is not null)
            sb.Append($"尺寸(整体): {Precision.Fmt(s.WidthMm)}×{Precision.Fmt(s.HeightMm)} mm")
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
                    sb.Append($"  {Precision.Fmt(c.WMm)}×{Precision.Fmt(c.HMm)} mm");
                if (c.P01 is double cp)
                    sb.Append($"  P {Precision.Fmt(cp, 4)}");
                sb.Append('\n');
            }
        }

        sb.Append("Detail: ").Append(s.Detail);
        return sb.ToString();
    }

    private static string Yn(bool b) => b ? "✓" : "✗";

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
        RatioText.Text = Engine.RatioInfo();
        AreaThresholdText.Text = Engine.AreaThresholdInfo();
        PressureThresholdText.Text = Engine.PressureThresholdInfo();
        JudgeText.Text = Engine.JudgeInfo();
        EraserInfoText.Text = string.IsNullOrEmpty(Engine.SizeHint) ? Engine.EraserInfo() : Engine.SizeHint;
        HidInfoText.Text = BuildTouchInfo();

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
