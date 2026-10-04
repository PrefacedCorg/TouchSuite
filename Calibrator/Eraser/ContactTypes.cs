using System.Windows;

namespace TouchErase.Calibrator.Eraser;

/// <summary>
/// 接触尺寸的来源（真数据源，共 3 路）：
/// <para>RawHid = RawInput 的 WM_INPUT 报文解出的设备上报尺寸；</para>
/// <para>HidSetup = SetupAPI 直接打开 HID 设备 ReadFile 读到的上报尺寸；</para>
/// <para>Wpf = 系统的 TouchPoint.Bounds 接触框（软件量纲，需 mm/DIP 折算）。</para>
/// </summary>
public enum ContactSource { None, Wpf, RawHid, HidSetup }

/// <summary>
/// 来源选择（用户下拉，共 4 条 + 自适应）：
/// <para>Auto = 自适应（自动锁定）；</para>
/// <para>RawInput = 只用 RawInput 的设备上报原始尺寸；</para>
/// <para>HidSetup = 只用 SetupAPI 直读的设备上报原始尺寸；</para>
/// <para>SoftwareHid = 数据源用 HID（RawInput 优先、SetupAPI 兜底），尺寸按软件推算的屏幕尺度定；</para>
/// <para>SoftwareWpf = 数据源用 WPF 的框，尺寸按软件推算的屏幕尺度定。</para>
/// </summary>
public enum SourceMode { Auto, RawInput, HidSetup, SoftwareHid, SoftwareWpf }

/// <summary>擦除区形状。</summary>
public enum EraserShape { Rectangle, Circle }

public static class SourceNames
{
    public static string Of(ContactSource s) => s switch
    {
        ContactSource.RawHid => "RawInput（设备上报）",
        ContactSource.HidSetup => "SetupAPI 直读（设备上报）",
        ContactSource.Wpf => "WPF（系统接触框）",
        _ => "无",
    };

    public static string OfMode(SourceMode m) => m switch
    {
        SourceMode.RawInput => "原始HID·RawInput（设备上报尺寸）",
        SourceMode.HidSetup => "原始HID·SetupAPI（设备上报尺寸）",
        SourceMode.SoftwareHid => "软件HID（HID 数据 + 软件推算）",
        SourceMode.SoftwareWpf => "软件WPF（系统接触框 + 软件推算）",
        _ => "自适应（自动锁定）",
    };
}

/// <summary>
/// 软件（屏幕尺度）推算 —— 「软件HID」模式的口径：
/// 不用驱动自带的 W/H 物理量程换算表（厂商可能填错），而是用屏幕标定尺度直接换算原始计数：
/// <para>W 毫米 = W 计数 ÷ X 轴逻辑量程 × 屏幕物理宽；H 毫米 = H 计数 ÷ Y 轴逻辑量程 × 屏幕物理高。</para>
/// 与「原始HID」（驱动换算表口径）互为独立参照，两边差异大即说明驱动表有问题。
/// </summary>
public static class SoftwareHidScale
{
    /// <summary>屏幕物理宽/高（mm）：mm/DIU × 主屏 DIP 尺寸（与标定同口径：主屏物理像素 × mm/px）。</summary>
    public static (double W, double H) ScreenMm(double mmPerDiuX, double mmPerDiuY)
        => (mmPerDiuX * SystemParameters.PrimaryScreenWidth,
            mmPerDiuY * SystemParameters.PrimaryScreenHeight);

    /// <summary>一根接触的软件推算尺寸；计数或量程缺失返回 null。</summary>
    public static (double? wMm, double? hMm) ContactMm(ContactRect c, int xLogMax, int yLogMax,
        double screenWmm, double screenHmm)
    {
        double? w = c.WLogical > 0 ? c.WLogical / (double)xLogMax * screenWmm : null;
        double? h = c.HLogical > 0 ? c.HLogical / (double)yLogMax * screenHmm : null;
        return (w, h);
    }

    /// <summary>整帧样本的软件推算面积（mm²，Σ各指 W×H）；数据不足返回 null。</summary>
    public static double? AreaMm2(TouchSample s, double mmPerDiuX, double mmPerDiuY)
    {
        if (s.XLogMax is not int xm || xm <= 0 || s.YLogMax is not int ym || ym <= 0)
            return null;
        if (mmPerDiuX <= 0 || mmPerDiuY <= 0)
            return null;

        (double sw, double sh) = ScreenMm(mmPerDiuX, mmPerDiuY);
        IEnumerable<ContactRect> list = s.Contacts is { Count: > 0 } l
            ? l
            : new[] { new ContactRect(0, null, null, null, null, null, null, s.WidthLogical, s.HeightLogical) };

        double sum = 0;
        bool any = false;
        foreach (ContactRect c in list)
        {
            (double? w, double? h) = ContactMm(c, xm, ym, sw, sh);
            if (w is double wv && h is double hv)
            {
                sum += wv * hv;
                any = true;
            }
        }
        return any ? sum : null;
    }
}
