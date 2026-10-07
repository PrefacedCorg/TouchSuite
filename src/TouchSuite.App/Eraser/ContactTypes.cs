using System.Windows;

namespace TouchSuite.App.Eraser;

/// <summary>
/// 接触尺寸的来源（两路）：
/// <para>RawHid = RawInput（WM_INPUT）解出的设备上报尺寸（按量程映射到屏幕像素）；</para>
/// <para>Wpf = 系统的 TouchPoint.Bounds 接触框（DIP × DPI 缩放 = 物理像素）。</para>
/// </summary>
public enum ContactSource { None, Wpf, RawHid }

/// <summary>
/// 来源选择（用户下拉）：
/// <para>Auto = 自适应（自动锁定）；</para>
/// <para>RawHid = 只用原始HID（设备上报）；</para>
/// <para>SoftwareWpf = 只用 WPF 的框（软件量纲，按 DPI 缩放折算成物理像素）。</para>
/// </summary>
public enum SourceMode { Auto, RawHid, SoftwareWpf }

/// <summary>
/// HID 接触尺寸（物理像素）的取法：
/// <para>LogicalToScreen = 逻辑计数 ÷ 该轴逻辑量程（不是恒定的 0..32767）× 屏幕对应分辨率（推荐）；</para>
/// <para>PhysicalRange = 设备声明的物理量程 → mm，再按屏幕标定 mm/px 换成物理像素。</para>
/// </summary>
public enum HidSizeSource { LogicalToScreen, PhysicalRange }

/// <summary>擦除区形状（两种形状都按"显示面积 = 目标面积"画：椭圆的显示面积 = π/4×W×H）。</summary>
public enum EraserShape { Rectangle, Circle }

/// <summary>长宽比来源：跟随触摸尺寸（接触框 w/h）/ 自定义（W:H 输入框；1:1 就是正圆）。</summary>
public enum AspectSource { Contact, Custom }

/// <summary>手掌像素面积（K 的分子）取值方式 —— 末页三选一。</summary>
public enum AreaFormula
{
    /// <summary>a1×a2（矩形，默认）。</summary>
    Rect,
    /// <summary>π/4×a1×a2（椭圆）。</summary>
    Ellipse,
    /// <summary>a3（描摹凹面积）。</summary>
    Trace,
}

public static class SourceNames
{
    public static string Of(ContactSource s) => s switch
    {
        ContactSource.RawHid => "原始HID（设备上报）",
        ContactSource.Wpf => "WPF（系统接触框）",
        _ => "无",
    };

    public static string OfMode(SourceMode m) => m switch
    {
        SourceMode.RawHid => "原始HID（设备上报尺寸）",
        SourceMode.SoftwareWpf => "软件WPF（系统接触框 + 软件推算）",
        _ => "自适应（自动锁定）",
    };

    public static string OfHidSize(HidSizeSource m) => m switch
    {
        HidSizeSource.PhysicalRange => "物理量程（mm）→ 屏幕换算",
        _ => "逻辑量程 → 屏幕分辨率（推荐）",
    };

    public static string OfFormula(AreaFormula f) => f switch
    {
        AreaFormula.Ellipse => "π/4×a1×a2（椭圆）",
        AreaFormula.Trace => "a3（描摹凹面积）",
        _ => "a1×a2（矩形）",
    };
}

/// <summary>
/// HID 接触尺寸 → 物理像素的换算（<see cref="TouchSample"/> 里的原始计数/量程 + 屏幕标定）。
/// 两条路互为兜底，都不可用才返回 null。
/// </summary>
public static class HidScale
{
    /// <summary>
    /// 接触宽高（物理像素）。
    /// <para>LogicalToScreen：计数 ÷ 该轴逻辑量程 × 屏幕分辨率（Width→ResX，Height→ResY）；</para>
    /// <para>PhysicalRange：设备声明物理量程换算出的 mm ÷ 屏幕标定 mm/px。</para>
    /// </summary>
    public static (double? W, double? H) ContactPx(TouchSample s, HidSizeSource src,
        int resX, int resY, double mmPerPxX, double mmPerPxY)
    {
        double? w = src == HidSizeSource.LogicalToScreen ? FromLogical(s.WidthLogical, s.WidthLogMax, resX) : null;
        double? h = src == HidSizeSource.LogicalToScreen ? FromLogical(s.HeightLogical, s.HeightLogMax, resY) : null;

        if (w is null) w = FromPhysical(s.WidthMm, mmPerPxX);
        if (h is null) h = FromPhysical(s.HeightMm, mmPerPxY);

        // 物理量程也缺（或屏幕未标定）→ 仍可退回逻辑量程映射，保证流程不卡死
        if (w is null) w = FromLogical(s.WidthLogical, s.WidthLogMax, resX);
        if (h is null) h = FromLogical(s.HeightLogical, s.HeightLogMax, resY);

        return (w, h);
    }

    /// <summary>逻辑计数 → 物理像素：计数 ÷ 该轴逻辑量程 × 屏幕分辨率。量程为 0（未声明）返回 null。</summary>
    private static double? FromLogical(int logical, int logMax, int res)
        => logMax > 0 && res > 0 ? logical / (double)logMax * res : null;

    /// <summary>物理量程 mm → 物理像素：mm ÷ 屏幕标定 mm/px。</summary>
    private static double? FromPhysical(double? mm, double mmPerPx)
        => mm is double v && mmPerPx > 0 ? v / mmPerPx : null;
}

/// <summary>把一次触摸样本换算成物理像素（RawHID 走 <see cref="HidScale"/>，WPF 走 DIP × DPI 缩放）。</summary>
public static class SamplePx
{
    /// <summary>样本的接触面积（px²）：RawHID = 尺寸乘积；WPF = 分指接触框面积之和（手掌多接触不被拆散低估）。</summary>
    public static double? AreaPx2(TouchSample s, HidSizeSource src, int resX, int resY,
        double mmPerPxX, double mmPerPxY, double pxPerDiuX, double pxPerDiuY)
    {
        if (s.Source == "RawHID")
        {
            (double? w, double? h) = HidScale.ContactPx(s, src, resX, resY, mmPerPxX, mmPerPxY);
            return w is double wv && h is double hv ? wv * hv : null;
        }

        if (s.Contacts is { Count: > 0 } list)
        {
            double sum = 0;
            bool any = false;
            foreach (ContactRect c in list)
                if (c.DiuRect is Rect r)
                {
                    sum += r.Width * pxPerDiuX * r.Height * pxPerDiuY;
                    any = true;
                }
            return any ? sum : null;
        }

        if (s.DiuRect is Rect r0)
            return r0.Width * pxPerDiuX * r0.Height * pxPerDiuY;
        return null;
    }

    /// <summary>样本最大那根接触的宽高（px）；量不到返回 null。</summary>
    public static (double W, double H)? SizePx(TouchSample s, HidSizeSource src, int resX, int resY,
        double mmPerPxX, double mmPerPxY, double pxPerDiuX, double pxPerDiuY)
    {
        if (s.Source == "RawHID")
        {
            (double? w, double? h) = HidScale.ContactPx(s, src, resX, resY, mmPerPxX, mmPerPxY);
            return w is double wv && h is double hv ? (wv, hv) : null;
        }

        if (s.Contacts is { Count: > 0 } list)
        {
            (double W, double H)? best = null;
            double bestA = 0;
            foreach (ContactRect c in list)
                if (c.DiuRect is Rect r && r.Width * r.Height > bestA)
                {
                    bestA = r.Width * r.Height;
                    best = (r.Width * pxPerDiuX, r.Height * pxPerDiuY);
                }
            return best;
        }

        if (s.DiuRect is Rect r0)
            return (r0.Width * pxPerDiuX, r0.Height * pxPerDiuY);
        return null;
    }
}

/// <summary>预览页当前设置的快照（保存进 calibration.json；载入时原样灌回）。</summary>
public sealed record EraserSettings(
    bool FollowSize, bool LockPalmSize, bool PalmPressureEnabled, bool AreaThresholdEnabled,
    bool WritingUsesPressure, bool WritingFollowSize,
    double KTrim, double AreaThresholdPx2,
    double PalmPressureThreshold, double PalmNormMax,
    double WritingPressureThreshold, double WritingNormMax,
    EraserShape Shape, AspectSource Aspect, double CustomAspectW, double CustomAspectH,
    AreaFormula Formula)
{
    /// <summary>设置齐全的默认值（引擎未标定/未载入时用）。</summary>
    public static EraserSettings Default { get; } = new(
        FollowSize: true, LockPalmSize: false, PalmPressureEnabled: true, AreaThresholdEnabled: true,
        WritingUsesPressure: true, WritingFollowSize: false,
        KTrim: 1.0, AreaThresholdPx2: 0,
        PalmPressureThreshold: 0.5, PalmNormMax: 2,
        WritingPressureThreshold: 0.5, WritingNormMax: 2,
        Shape: EraserShape.Rectangle, Aspect: AspectSource.Contact, CustomAspectW: 1, CustomAspectH: 1,
        Formula: AreaFormula.Rect);
}