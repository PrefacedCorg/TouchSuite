using System.Windows;

namespace TouchErase.Calibrator.Eraser;

/// <summary>
/// 接触尺寸的来源（两路）：
/// <para>RawHid = RawInput（WM_INPUT）解出的设备上报尺寸（mm 真值，面积=W×H）；</para>
/// <para>Wpf = 系统的 TouchPoint.Bounds 接触框（软件量纲，需 mm/DIP 折算）。</para>
/// </summary>
public enum ContactSource { None, Wpf, RawHid }

/// <summary>
/// 来源选择（用户下拉）：
/// <para>Auto = 自适应（自动锁定）；</para>
/// <para>RawHid = 只用原始HID（设备上报）；</para>
/// <para>SoftwareWpf = 只用 WPF 的框（软件量纲，按标定 mm/DIP 折算）。</para>
/// </summary>
public enum SourceMode { Auto, RawHid, SoftwareWpf }

/// <summary>
/// HID 模式下接触尺寸（mm）的取法：
/// <para>Mapped = 驱动映射表上报的 mm（HidReader 已按设备声明的物理量程换算好）；</para>
/// <para>Calibrated = 校准时取得的 mm（用屏幕标定把原始计数推算成 mm）。</para>
/// </summary>
public enum HidMmSource { Mapped, Calibrated }

/// <summary>擦除区形状。</summary>
public enum EraserShape { Rectangle, Circle }

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

    public static string OfHidMm(HidMmSource m) => m switch
    {
        HidMmSource.Calibrated => "校准时取得（按屏幕标定换算）",
        _ => "映射表上报（设备声明 mm）",
    };
}

/// <summary>
/// HID 接触尺寸（mm）的取法 —— 对应「HID mm 来源」下拉：
/// <para>Mapped：直接用驱动映射表上报的 mm（<see cref="TouchSample.WidthMm"/>）。</para>
/// <para>Calibrated：校准时取得的 mm —— 原始计数 ÷ 该轴逻辑量程 × 屏幕物理尺寸（屏幕物理尺寸 = 标定 mm/DIU × 主屏 DIP）。</para>
/// 校准推算不可用（缺逻辑量程 / 未标定）时退回映射表值，保证流程不卡死。
/// </summary>
public static class HidScale
{
    public static (double? W, double? H) ContactMm(TouchSample s, HidMmSource src,
        double mmPerDiuX, double mmPerDiuY)
    {
        if (src != HidMmSource.Calibrated)
            return (s.WidthMm, s.HeightMm);

        if (s.XLogMax <= 0 || s.YLogMax <= 0 || mmPerDiuX <= 0 || mmPerDiuY <= 0)
            return (s.WidthMm, s.HeightMm);

        double screenWmm = mmPerDiuX * SystemParameters.PrimaryScreenWidth;
        double screenHmm = mmPerDiuY * SystemParameters.PrimaryScreenHeight;
        double? w = s.WidthLogical > 0 ? s.WidthLogical / (double)s.XLogMax * screenWmm : s.WidthMm;
        double? h = s.HeightLogical > 0 ? s.HeightLogical / (double)s.YLogMax * screenHmm : s.HeightMm;
        return (w, h);
    }
}