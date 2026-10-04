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
