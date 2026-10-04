using System.Windows;

namespace TouchErase.Calibrator.Eraser;

/// <summary>
/// 接触尺寸的来源。当前只保留 WPF 一路（虚拟/远程 HID 通路已整体移除）。
/// <para>Wpf = 系统的 TouchPoint.Bounds 接触框（软件量纲，需 mm/DIP 折算）。</para>
/// </summary>
public enum ContactSource { None, Wpf }

/// <summary>
/// 来源选择（用户下拉）。当前只保留 WPF 一路 + 自适应。
/// <para>Auto = 自适应；</para>
/// <para>SoftwareWpf = 数据源用 WPF 的框，尺寸按软件推算的屏幕尺度定。</para>
/// </summary>
public enum SourceMode { Auto, SoftwareWpf }

/// <summary>擦除区形状。</summary>
public enum EraserShape { Rectangle, Circle }

public static class SourceNames
{
    public static string Of(ContactSource s) => s switch
    {
        ContactSource.Wpf => "WPF（系统接触框）",
        _ => "无",
    };

    public static string OfMode(SourceMode m) => m switch
    {
        SourceMode.SoftwareWpf => "软件WPF（系统接触框 + 软件推算）",
        _ => "自适应（自动锁定）",
    };
}
