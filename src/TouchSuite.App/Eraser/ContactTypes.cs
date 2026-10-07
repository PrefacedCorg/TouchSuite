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

/// <summary>擦除区形状（两种形状都按"显示面积 = 目标面积"画：椭圆的显示面积 = π/4×W×H）。</summary>
public enum EraserShape { Rectangle, Circle }

/// <summary>
/// HID 宽/高计数 → 屏幕像素的换算方式（**只影响擦除区形状，不影响面积**）。
/// <para>Stretch（归一）= 宽按屏宽拉伸、高按屏高拉伸（宽 0..量程 → 0..ResX，高 → 0..ResY）；
/// 屏幕不是 16:9 时正圆会被拉成椭圆（倍数 = ResX/ResY）。</para>
/// <para>Isotropic（不归一）= 两轴用同一因子（1:1），正圆就是正圆。</para>
/// 两种方式的"面积因子"相同（f·f = f_w·f_h），所以切换只改形状、不改面积，K/阈值无需重标。
/// </summary>
public enum HidSizeScale { Stretch, Isotropic }

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

    public static string OfAspect(AspectSource a) => a switch
    {
        AspectSource.Custom => "自定义",
        _ => "跟随触摸尺寸",
    };

    public static string OfSizeScale(HidSizeScale s) => s switch
    {
        HidSizeScale.Isotropic => "不归一 —— 1:1（两轴同比例，圆就是圆）",
        _ => "归一 —— 按屏幕拉伸（宽→屏宽、高→屏高）",
    };

    public static string OfFormula(AreaFormula f) => f switch
    {
        AreaFormula.Ellipse => "π/4×a1×a2（椭圆）",
        AreaFormula.Trace => "a3（描摹凹面积）",
        _ => "a1×a2（矩形）",
    };
}

/// <summary>
/// HID 接触尺寸 → 物理像素（**全程不碰 mm**）。
/// 宽/高的逻辑量程直接映射到屏幕分辨率：宽 0..WidthLogMax → 0..ResX，高 0..HeightLogMax → 0..ResY。
/// </summary>
public static class HidScale
{
    /// <summary>两轴的「每计数多少像素」因子。
    /// <para>Stretch：fw = ResX/宽量程、fh = ResY/高量程（按屏幕拉伸）；</para>
    /// <para>Isotropic：两轴同因子 f = √(fw·fh)（1:1）。注意 f·f == fw·fh → 面积因子恒等。</para></summary>
    public static (double fw, double fh) Factors(int wLogMax, int hLogMax, int resX, int resY, HidSizeScale scale)
    {
        if (wLogMax <= 0 || hLogMax <= 0 || resX <= 0 || resY <= 0)
            return (0, 0);

        double fw = resX / (double)wLogMax;
        double fh = resY / (double)hLogMax;
        if (scale == HidSizeScale.Isotropic)
        {
            double f = Math.Sqrt(fw * fh);   // 各向同性，且 f·f == fw·fh（面积不变）
            fw = f;
            fh = f;
        }
        return (fw, fh);
    }

    /// <summary>接触宽高（物理像素）。stretch 见 <see cref="HidSizeScale"/>。</summary>
    public static (double? W, double? H) ContactPx(TouchSample s, int resX, int resY, HidSizeScale scale)
    {
        (double fw, double fh) = Factors(s.WidthLogMax, s.HeightLogMax, resX, resY, scale);
        if (fw <= 0 || fh <= 0)
            return (null, null);
        return (s.WidthLogical * fw, s.HeightLogical * fh);
    }
}

/// <summary>把一次触摸样本换算成物理像素（RawHID 走 <see cref="HidScale"/>，WPF 走 DIP × DPI 缩放）。</summary>
public static class SamplePx
{
    /// <summary>样本的接触面积（px²）：RawHID = 宽×高（px）；WPF = 分指接触框面积之和。
    /// 面积与 <see cref="HidSizeScale"/> 无关（面积因子恒等），只是为了统一签名传进来。</summary>
    public static double? AreaPx2(TouchSample s, int resX, int resY, HidSizeScale scale,
        double pxPerDiuX, double pxPerDiuY)
    {
        if (s.Source == "RawHID")
        {
            (double? w, double? h) = HidScale.ContactPx(s, resX, resY, scale);
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
    public static (double W, double H)? SizePx(TouchSample s, int resX, int resY, HidSizeScale scale,
        double pxPerDiuX, double pxPerDiuY)
    {
        if (s.Source == "RawHID")
        {
            (double? w, double? h) = HidScale.ContactPx(s, resX, resY, scale);
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
    bool FollowSize, bool LockPalmSize, bool SmoothJitter, bool PalmFloorEnabled,
    bool PalmPressureEnabled, bool AreaThresholdEnabled,
    bool WritingUsesPressure, bool WritingFollowSize,
    double KTrim, double AreaThresholdPx2,
    double PalmPressureThreshold, double PalmNormMax,
    double WritingPressureThreshold, double WritingNormMax,
    EraserShape Shape, AspectSource Aspect, double CustomAspectW, double CustomAspectH,
    AreaFormula Formula, HidSizeScale SizeScale)
{
    /// <summary>设置齐全的默认值（引擎未标定/未载入时用）。</summary>
    public static EraserSettings Default { get; } = new(
        FollowSize: true, LockPalmSize: false, SmoothJitter: true, PalmFloorEnabled: true,
        PalmPressureEnabled: true, AreaThresholdEnabled: true,
        WritingUsesPressure: true, WritingFollowSize: false,
        KTrim: 1.0, AreaThresholdPx2: 0,
        PalmPressureThreshold: 0.5, PalmNormMax: 2,
        WritingPressureThreshold: 0.5, WritingNormMax: 2,
        Shape: EraserShape.Rectangle, Aspect: AspectSource.Contact, CustomAspectW: 1, CustomAspectH: 1,
        Formula: AreaFormula.Rect, SizeScale: HidSizeScale.Stretch);
}