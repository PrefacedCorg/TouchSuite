namespace TouchErase.Calibrator.Eraser;

/// <summary>接触尺寸的来源（优先级：原始HID &gt; WM_POINTER &gt; WPF）。</summary>
public enum ContactSource { None, Wpf, Pointer, RawHid }

/// <summary>来源选择：自适应（自动锁定）或手动指定某一路。</summary>
public enum SourceMode { Auto, RawHid, Pointer, Wpf }

/// <summary>擦除区形状。</summary>
public enum EraserShape { Rectangle, Circle }

public static class SourceNames
{
    public static string Of(ContactSource s) => s switch
    {
        ContactSource.RawHid => "原始HID(设备上报)",
        ContactSource.Pointer => "WM_POINTER rcContact",
        ContactSource.Wpf => "WPF TouchPoint.Bounds",
        _ => "无",
    };

    public static string OfMode(SourceMode m) => m switch
    {
        SourceMode.RawHid => "原始HID(设备上报)",
        SourceMode.Pointer => "WM_POINTER rcContact",
        SourceMode.Wpf => "WPF TouchPoint.Bounds",
        _ => "自适应(自动锁定)",
    };
}
