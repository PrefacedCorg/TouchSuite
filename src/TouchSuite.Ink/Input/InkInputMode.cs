namespace TouchSuite.Ink;

/// <summary>输入源（工具栏下拉框选择）。默认 WPF 事件，保持原行为不被破坏。</summary>
internal enum InkInputMode
{
    /// <summary>WPF 的 Mouse / Stylus / Touch 事件（原状，默认）。</summary>
    Wpf = 0,

    /// <summary>RawInput 抓全量 HID 报告（RegisterRawInputDevices → WM_INPUT → HidP 解析）。不受前台限制。</summary>
    RawHid = 1,

    /// <summary>WM_POINTER 帧 API（user32）。依赖命中测试：窗口必须在触点底下且能收到指针消息。</summary>
    WmPointer = 2,

    /// <summary>RawInput 为主，WM_POINTER 兜底（RawInput 本帧无触点时由 POINTER 接管）。</summary>
    Combo = 3,
}

internal static class InkInputModes
{
    public static string Label(InkInputMode mode) => mode switch
    {
        InkInputMode.RawHid => "RawInput 原始 HID",
        InkInputMode.WmPointer => "WM_POINTER 帧 API",
        InkInputMode.Combo => "组合 RawInput+POINTER",
        _ => "WPF 事件",
    };

    /// <summary>状态栏显示的「实际生效」描述（不是「期望」，注册失败时要能看出来）。</summary>
    public static string ApiName(InkInputMode mode, string rawInfo, string pointerInfo) => mode switch
    {
        InkInputMode.RawHid => rawInfo.Length > 0 ? rawInfo : "未注册",
        InkInputMode.WmPointer => pointerInfo.Length > 0 ? pointerInfo : "未收到指针消息",
        InkInputMode.Combo => $"{rawInfo switch { "" => "未注册", _ => rawInfo }} / {pointerInfo switch { "" => "未收到指针消息", _ => pointerInfo }}",
        _ => "WPF Stylus/Touch/Mouse 事件",
    };
}
