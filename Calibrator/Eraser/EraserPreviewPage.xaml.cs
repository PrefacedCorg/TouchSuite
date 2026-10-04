using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    /// <summary>接收 WPF 触摸兜底用的宿主元素。</summary>
    public FrameworkElement HostElement => Host;

    public EraserPreviewPage()
    {
        InitializeComponent();
        Engine.Changed += Redraw;
        Loaded += (_, _) => Redraw();

        // 初始勾选状态同步进引擎（XAML 解析期事件早于字段赋值，会被 null 守卫挡掉）
        Engine.FollowSize = FollowSizeCheck.IsChecked == true;
        Engine.FollowPressure = FollowPressureCheck.IsChecked == true;
        Engine.AreaThresholdEnabled = AreaThresholdCheck.IsChecked == true;
        Engine.WritingUsesPressure = WritingPressureCheck.IsChecked == true;

        // 定时刷新：所有来源过期后把「生效来源」归零，界面显示「无」
        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background,
            (_, _) => Engine.Tick(), Dispatcher);
        Loaded += (_, _) => _tick.Start();
        Unloaded += (_, _) => _tick.Stop();
    }

    private readonly DispatcherTimer _tick;

    // ================= 宿主喂入标定量 =================

    public void Setup(double mmPerDiuX, double mmPerDiuY, double palmAreaCm2, double palmAspect,
                      double areaThresholdMm2 = 0, double? pressureThreshold = null)
    {
        Engine.MmPerDiuX = mmPerDiuX;
        Engine.MmPerDiuY = mmPerDiuY;
        Engine.PalmAreaCm2 = palmAreaCm2;
        Engine.PalmAspect = palmAspect;
        Engine.AreaThresholdBaseMm2 = areaThresholdMm2;
        Engine.PressureThresholdBase = pressureThreshold;
        Redraw();
    }

    // ================= 触摸样本 → 引擎 =================

    /// <summary>把一次触摸样本按来源换算成预览区坐标后交给引擎。</summary>
    public void SubmitSample(TouchSample s)
    {
        if (Engine.MmPerDiuX <= 0 || Engine.MmPerDiuY <= 0)
            return;

        // 实时压感喂给引擎（"随压力"模式用它缩放擦除区；WPF 面积样本无压感，保留上一次的值）
        if (s.Pressure01 is double p)
            Engine.CurrentPressure = p;

        switch (s.Source)
        {
            case "RawHID":
            {
                Engine.NoteRawCounts(s.WidthLogical, s.HeightLogical);
                if (s.WidthMm is not double wMm || s.HeightMm is not double hMm)
                    return; // 只给逻辑计数（未标定），引擎已给出引导
                double wDiu = wMm / Engine.MmPerDiuX;
                double hDiu = hMm / Engine.MmPerDiuY;
                Point c = ContactCenter(s);
                Engine.Submit(ContactSource.RawHid,
                    new Rect(c.X - wDiu / 2, c.Y - hDiu / 2, wDiu, hDiu),
                    applyThreshold: false,
                    detail: $"W={s.WidthLogical} → {Precision.Fmt(wMm)}×{Precision.Fmt(hMm)} mm");
                break;
            }

            case "WM_POINTER":
            {
                if (s.WidthMm is not double wMm || s.HeightMm is not double hMm)
                    return;
                double wDiu = wMm / Engine.MmPerDiuX;
                double hDiu = hMm / Engine.MmPerDiuY;
                Point c = ContactCenter(s);
                Engine.Submit(ContactSource.Pointer,
                    new Rect(c.X - wDiu / 2, c.Y - hDiu / 2, wDiu, hDiu),
                    applyThreshold: true,
                    detail: $"{Precision.Fmt(wMm)}×{Precision.Fmt(hMm)} mm（rcContact）");
                break;
            }

            case "WPF":
            {
                if (s.DiuRect is not Rect r || r.Width <= 0 || r.Height <= 0)
                    return;
                Engine.Submit(ContactSource.Wpf, r, applyThreshold: true,
                    detail: $"Bounds {Precision.Fmt(r.Width, 3)}×{Precision.Fmt(r.Height, 3)} DIP");
                break;
            }
        }
    }

    /// <summary>把样本的屏幕/归一化坐标换算成预览区内的坐标；拿不到就用中心。</summary>
    private Point ContactCenter(TouchSample s)
    {
        try
        {
            Point origin = Host.PointToScreen(new Point(0, 0));   // 设备像素
            DpiScale dpi = VisualTreeHelper.GetDpi(this);

            if (s.XNorm is double xn && s.YNorm is double yn)
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

    // ================= 触摸入口（WPF 兜底，同原实现） =================

    private void OnHostTouchDown(object sender, TouchEventArgs e) => FeedWpf(e);

    private void OnHostTouchMove(object sender, TouchEventArgs e) => FeedWpf(e);

    private void OnHostTouchUp(object sender, TouchEventArgs e)
        => Status?.Invoke("面积擦预览：" + (Engine.LastContact is Rect
            ? $"接触 {Precision.Fmt(Engine.LastMmW)} × {Precision.Fmt(Engine.LastMmH)} mm"
            : "未读到接触尺寸"));

    private void OnHostMouseMove(object sender, MouseEventArgs e) { /* 鼠标无接触尺寸，留空以免页面无响应 */ }

    private void FeedWpf(TouchEventArgs e)
    {
        if (Engine.MmPerDiuX <= 0)
            return;
        Rect b = e.GetTouchPoint(Host).Bounds;
        Engine.Submit(ContactSource.Wpf, b, applyThreshold: true,
            detail: $"Bounds {Precision.Fmt(b.Width, 3)}×{Precision.Fmt(b.Height, 3)} DIP");
    }

    // ================= 参数控件 =================

    private void OnSourceModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceModeCombo is null)
            return; // XAML 解析期提前触发
        Engine.SetMode(SourceModeCombo.SelectedIndex switch
        {
            1 => SourceMode.RawHid,
            2 => SourceMode.Wpf,
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

    private void OnFollowPressureChanged(object sender, RoutedEventArgs e)
    {
        if (FollowPressureCheck is null)
            return;
        Engine.FollowPressure = FollowPressureCheck.IsChecked == true;
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

    private void OnClearPreview(object sender, RoutedEventArgs e)
    {
        Engine.Clear();
        Status?.Invoke("面积擦预览已清除。");
    }

    private void OnCalibrateHidScale(object sender, RoutedEventArgs e)
    {
        Status?.Invoke(Engine.CalibrateHidScale(out string msg) ? msg : "标定中止：" + msg);
    }

    private void OnApplyManualScale(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(ScaleInput.Text.Trim(), out double k))
        {
            Status?.Invoke("请输入 0~100 之间的 mm/计数。");
            return;
        }
        Status?.Invoke(Engine.ApplyManualScale(k, out string msg) ? msg : msg);
    }

    private void OnInjectSyntheticCount(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(SimCountInput.Text.Trim(), out int count))
        {
            Status?.Invoke($"请输入 1~{EraserEngine.MaxSimCount} 的模拟计数。");
            return;
        }
        Status?.Invoke(Engine.Inject(count, new Point(Host.ActualWidth / 2, Host.ActualHeight / 2), out string msg) ? msg : msg);
    }

    // ================= 重画 =================

    private void Redraw()
    {
        if (Overlay is null)
            return;

        Overlay.Children.Clear();
        SourceInfoText.Text = Engine.SourceInfo();
        HidScaleText.Text = Engine.HidScaleInfo();
        RatioText.Text = Engine.RatioInfo();
        AreaThresholdText.Text = Engine.AreaThresholdInfo();
        PressureThresholdText.Text = Engine.PressureThresholdInfo();
        JudgeText.Text = Engine.JudgeInfo();
        EraserInfoText.Text = string.IsNullOrEmpty(Engine.SizeHint) ? Engine.EraserInfo() : Engine.SizeHint;

        if (Engine.LastContact is not Rect c)
            return;

        double cx = c.X + c.Width / 2;
        double cy = c.Y + c.Height / 2;

        if (Engine.IsWriting())
        {
            // 书写：蓝点，直径随书写压感（不启用压感时用固定模拟 512/1024 = 0.5）
            double p = Math.Clamp(Engine.WritingPressure01, 0, 1);
            double d = 6 + 22 * p;
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
            if (Engine.CircleDiameterDiu(c) is not double d)
                return;
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
            var (w, h) = Engine.RectSizeDiu(c);
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
