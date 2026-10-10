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
/// 面积另由 <see cref="HidScale.AreaFactor"/> 显式给出（各向同性），与这里的选择完全无关。
/// </summary>
public enum HidSizeScale { Stretch, Isotropic }

/// <summary>长宽比来源：跟随触摸尺寸（接触框 w/h）/ 手掌描摹（手掌宽高比）/ 自定义（W:H 输入框）。</summary>
public enum AspectSource { Contact, Trace, Custom }

/// <summary>随触摸尺寸时，擦除区大小相对「手掌标定面积」的限制方式。</summary>
public enum PalmLimit
{
    /// <summary>不限：擦除区 = 接触面积 × K（可小可大）。</summary>
    None,
    /// <summary>下限：不小于手掌标定面积（只增不减）。</summary>
    Floor,
    /// <summary>上限：不超过手掌标定面积（只减不增）。</summary>
    Cap,
}

public static class SourceNames
{
    public static string Of(ContactSource s) => s switch
    {
        ContactSource.RawHid => "原始HID",
        ContactSource.Wpf => "软件WPF",
        _ => "无",
    };

    public static string OfMode(SourceMode m) => m switch
    {
        SourceMode.RawHid => "原始HID",
        SourceMode.SoftwareWpf => "软件WPF",
        _ => "自适应",
    };

    public static string OfAspect(AspectSource a) => a switch
    {
        AspectSource.Custom => "自定义",
        AspectSource.Trace => "手掌描摹",
        _ => "跟随触摸",
    };

    public static string OfSizeScale(HidSizeScale s) => s switch
    {
        HidSizeScale.Isotropic => "不归一 1:1",
        _ => "归一 按屏幕拉伸",
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

    /// <summary>两轴共用的「面积因子」（= 不归一/各向同性面积，最贴近设备实测）：= fw·fh。
    /// 接触面积 = 宽计数 × 高计数 × 此因子，**固定用它**；切换形状比例不改它，所以 K/阈值无需重标。</summary>
    public static double AreaFactor(int wLogMax, int hLogMax, int resX, int resY)
    {
        if (wLogMax <= 0 || hLogMax <= 0 || resX <= 0 || resY <= 0)
            return 0;
        return (resX / (double)wLogMax) * (resY / (double)hLogMax);
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
    /// <summary>样本的接触面积（px²）：RawHID = 宽计数 × 高计数 × 面积因子（各向同性，与形状比例无关）；
    /// WPF = 分指接触框面积之和。<paramref name="sumRawContacts"/> 为真且样本带分指列表时，RawHID 也按各接触求和。</summary>
    public static double? AreaPx2(TouchSample s, int resX, int resY,
        double pxPerDiuX, double pxPerDiuY, bool sumRawContacts = true)
    {
        if (s.Source == "RawHID")
        {
            double af = HidScale.AreaFactor(s.WidthLogMax, s.HeightLogMax, resX, resY);
            if (af <= 0)
                return null;

            if (sumRawContacts && s.Contacts is { Count: > 0 } rawList)
            {
                double sumRaw = 0;
                bool anyRaw = false;
                foreach (ContactRect c in rawList)
                    if (c.WLogical > 0 && c.HLogical > 0)
                    {
                        sumRaw += (double)c.WLogical * c.HLogical * af;
                        anyRaw = true;
                    }
                return anyRaw ? sumRaw : null;
            }

            if (s.WidthLogical <= 0 || s.HeightLogical <= 0)
                return null;
            return s.WidthLogical * s.HeightLogical * af;
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
    bool FollowSize, bool LockPalmSize, bool SmoothJitter, PalmLimit SizeLimit,
    bool PalmPressureEnabled, bool AreaThresholdEnabled,
    bool WritingUsesPressure, bool WritingFollowSize,
    double KTrim, double AreaThresholdPx2,
    double PalmPressureThreshold, double PalmNormMax,
    double WritingPressureThreshold, double WritingNormMax,
    EraserShape Shape, AspectSource Aspect, double CustomAspectW, double CustomAspectH,
    HidSizeScale SizeScale, bool MultiTouchAsPalm)
{
    /// <summary>设置齐全的默认值（引擎未标定/未载入时用）。</summary>
    public static EraserSettings Default { get; } = new(
        FollowSize: true, LockPalmSize: false, SmoothJitter: true, SizeLimit: PalmLimit.Floor,
        PalmPressureEnabled: true, AreaThresholdEnabled: true,
        WritingUsesPressure: true, WritingFollowSize: false,
        KTrim: 1.0, AreaThresholdPx2: 0,
        PalmPressureThreshold: 0.5, PalmNormMax: 2,
        WritingPressureThreshold: 0.5, WritingNormMax: 2,
        Shape: EraserShape.Rectangle, Aspect: AspectSource.Contact, CustomAspectW: 1, CustomAspectH: 1,
        SizeScale: HidSizeScale.Stretch, MultiTouchAsPalm: true);
}