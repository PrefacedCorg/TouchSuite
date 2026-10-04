using System.Runtime.InteropServices;
using System.Text;

namespace TouchErase.Calibrator;

/// <summary>
/// WM_POINTER 设备属性探测（只读，不参与采集）。
/// <para>
/// 背景：触摸屏（Digitizer）在 Windows 上走 WM_POINTER 数字化器栈，**不注册 RawInput HID 设备**，
/// 所以 <see cref="TouchInput"/> 的 RawInput 枚举永远扫不到它。真实触摸屏的 HID 描述符属性
/// 只能通过 <c>GetPointerDeviceProperties</c> 拿到。
/// </para>
/// <para>
/// 本类只做一件事：枚举系统上全部 Pointer 设备，把每个设备上报的**全部 HID 属性**
/// （usagePage / usage / 逻辑量程 / 物理量程 / 单位 / 单位指数）列出来 ——
/// 从而判断这块屏到底报不报接触尺寸（Width 0x48 / Height 0x49）、有没有换算表、
/// 有没有压感（TipPressure）、接触 ID（ContactIdentifier）。
/// </para>
/// </summary>
public static class PointerProbe
{
    // ---- 结构体 ----

    /// <summary>
    /// 原生 tagPOINTER_DEVICE_INFO（winuser.h）。
    /// 必须声明 CharSet=Unicode：productString 在原生里是 WCHAR[520]（1040 字节），
    /// ByValTStr 的 SizeConst 是"字符数"，字节宽度由 CharSet 决定 —— 缺省 Ansi 时封送器只给
    /// 520 字节缓冲，GetPointerDevices 却按 1040 字节写 → 每个设备越界写 520 字节 → 堆损坏
    /// （0xc0000374，延迟检出，崩点在任意后续堆操作上）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct POINTER_DEVICE_INFO
    {
        public uint displayId;
        public IntPtr device;              // HANDLE
        public uint pointerDeviceType;     // POINTER_DEVICE_TYPE
        public IntPtr monitor;             // HMONITOR
        public uint startingCursorId;
        public ushort maxActiveContacts;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 520)]   // POINTER_DEVICE_PRODUCT_STRING_MAX = 520 个 WCHAR
        public string productString;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_DEVICE_PROPERTY
    {
        public int logicalMin;
        public int logicalMax;
        public int physicalMin;
        public int physicalMax;
        public uint unit;
        public uint unitExponent;
        public ushort usagePageId;
        public ushort usageId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left, top, right, bottom;
        public int Width => right - left;
        public int Height => bottom - top;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetPointerDevices(ref uint deviceCount, [Out] POINTER_DEVICE_INFO[]? devices);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetPointerDeviceProperties(IntPtr device, ref uint propertyCount,
        [Out] POINTER_DEVICE_PROPERTY[]? properties);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetPointerDeviceRects(IntPtr device, out RECT pointerDeviceRect, out RECT displayRect);

    // ---- 结果 ----

    /// <summary>探测结果文本（多行，供日志与界面直接显示）。</summary>
    public static string Report { get; private set; } = "（未探测）";

    /// <summary>是否发现任何 Pointer 设备。</summary>
    public static bool AnyDevice { get; private set; }

    /// <summary>是否至少有一个设备上报了接触尺寸（Width 0x48 + Height 0x49）。</summary>
    public static bool AnyDeclaresSize { get; private set; }

    /// <summary>是否至少有一个设备上报了压感（TipPressure 0x30）。</summary>
    public static bool AnyDeclaresPressure { get; private set; }

    /// <summary>触摸屏（PT_TOUCH=4）设备名列表。</summary>
    public static string TouchDeviceNames { get; private set; } = "";

    private const uint PT_TOUCH = 4;
    private const uint PT_PEN = 3;
    private const uint PT_TOUCHPAD = 5;
    private const ushort UP_GENERIC = 0x01;
    private const ushort UP_DIGITIZER = 0x0D;

    /// <summary>跑一次探测：枚举设备 + 属性 + 设备/显示矩形，组装报告。纯读，不注册、不钩消息。</summary>
    public static void Run()
    {
        var sb = new StringBuilder();
        var names = new List<string>();
        AnyDevice = false;
        AnyDeclaresSize = false;
        AnyDeclaresPressure = false;

        uint count = 0;
        GetPointerDevices(ref count, null);
        if (count == 0)
        {
            Report = "WM_POINTER 设备：0 个（系统未启用 Pointer 栈 / 无触摸数字化器）";
            return;
        }

        var arr = new POINTER_DEVICE_INFO[count];
        if (!GetPointerDevices(ref count, arr))
        {
            Report = $"WM_POINTER GetPointerDevices 失败（err={Marshal.GetLastWin32Error()}）";
            return;
        }

        AnyDevice = true;
        sb.Append("WM_POINTER 设备（共 ").Append(count).Append(" 个）：");

        for (int i = 0; i < count; i++)
        {
            POINTER_DEVICE_INFO di = arr[i];
            string type = di.pointerDeviceType switch
            {
                PT_TOUCH => "触摸屏",
                PT_PEN => "触笔",
                PT_TOUCHPAD => "触摸板",
                _ => $"类型{di.pointerDeviceType}",
            };
            string name = string.IsNullOrWhiteSpace(di.productString) ? "(无名)" : di.productString;
            sb.Append($"\n\n[{i + 1}] {type} | {name} | 最大接触={di.maxActiveContacts} | 首光标Id={di.startingCursorId}");

            if (di.pointerDeviceType == PT_TOUCH)
                names.Add($"{name}（最大接触 {di.maxActiveContacts}）");

            // 设备逻辑矩形 → 屏幕矩形（换算表需要它把设备单位映射到屏幕像素）
            if (GetPointerDeviceRects(di.device, out RECT devRect, out RECT dispRect))
                sb.Append($"\n     设备矩形 {devRect.Width}×{devRect.Height}  显示矩形 {dispRect.Width}×{dispRect.Height}");

            uint pc = 0;
            if (!GetPointerDeviceProperties(di.device, ref pc, null) || pc == 0)
            {
                sb.Append($"\n     属性：无（err={Marshal.GetLastWin32Error()}）← 该设备不通过 Pointer 暴露 HID 属性");
                continue;
            }

            var props = new POINTER_DEVICE_PROPERTY[pc];
            if (!GetPointerDeviceProperties(di.device, ref pc, props))
            {
                sb.Append($"\n     属性：读取失败（err={Marshal.GetLastWin32Error()}）");
                continue;
            }

            bool hasW = false, hasH = false, hasP = false, hasCid = false, hasX = false, hasY = false;
            sb.Append($"\n     属性 {pc} 个（usagePage:usage  逻辑量程  物理量程  单位  指数）：");
            for (int k = 0; k < pc; k++)
            {
                POINTER_DEVICE_PROPERTY p = props[k];
                string label = UsageName(p.usagePageId, p.usageId);
                sb.Append($"\n       {k,2}. {label,-34} {p.logicalMin}..{p.logicalMax}   "
                          + $"物理 {p.physicalMin}..{p.physicalMax}   单位 {UnitName(p.unit)}  exp {Exponent(p.unitExponent)}");

                if (p.usagePageId == UP_DIGITIZER && p.usageId == 0x48) hasW = true;
                if (p.usagePageId == UP_DIGITIZER && p.usageId == 0x49) hasH = true;
                if (p.usagePageId == UP_DIGITIZER && p.usageId == 0x30) hasP = true;
                if (p.usagePageId == UP_DIGITIZER && p.usageId == 0x51) hasCid = true;
                if (p.usagePageId == UP_GENERIC && p.usageId == 0x30) hasX = true;
                if (p.usagePageId == UP_GENERIC && p.usageId == 0x31) hasY = true;
            }

            // 换算表可用性结论
            sb.Append($"\n     能力：X={Yn(hasX)} Y={Yn(hasY)} 宽(0x48)={Yn(hasW)} 高(0x49)={Yn(hasH)} "
                      + $"压感(0x30)={Yn(hasP)} 接触ID(0x51)={Yn(hasCid)}");

            if (hasW && hasH)
            {
                AnyDeclaresSize = true;
                sb.Append("\n     → 该设备**会报接触尺寸**，可从裸数据取 Width/Height（等价 HID 0x48/0x49）");
            }
            else
            {
                sb.Append("\n     → 该设备**不报接触尺寸**：面积只能靠 Bounds / rcContactRaw 推算（红外屏常见）");
            }
            if (hasP)
                AnyDeclaresPressure = true;
        }

        TouchDeviceNames = names.Count > 0 ? string.Join("；", names) : "";
        Report = sb.ToString();
    }

    private static string UsageName(ushort page, ushort usage)
    {
        if (page == UP_GENERIC && usage == 0x30) return "Generic/X (0x01:0x30)";
        if (page == UP_GENERIC && usage == 0x31) return "Generic/Y (0x01:0x31)";
        if (page == UP_DIGITIZER && usage == 0x30) return "Digitizer/TipPressure (0x0D:0x30)";
        if (page == UP_DIGITIZER && usage == 0x42) return "Digitizer/TipSwitch (0x0D:0x42)";
        if (page == UP_DIGITIZER && usage == 0x48) return "Digitizer/Width (0x0D:0x48)";
        if (page == UP_DIGITIZER && usage == 0x49) return "Digitizer/Height (0x0D:0x49)";
        if (page == UP_DIGITIZER && usage == 0x51) return "Digitizer/ContactId (0x0D:0x51)";
        if (page == UP_DIGITIZER && usage == 0x55) return "Digitizer/MaxContact (0x0D:0x55)";
        if (page == UP_DIGITIZER && usage == 0x3F) return "Digitizer/Tilt (0x0D:0x3F)";
        if (page == UP_DIGITIZER && usage == 0x52) return "Digitizer/Azimuth (0x0D:0x52)";
        return $"未知 (0x{page:X2}:0x{usage:X2})";
    }

    private static string UnitName(uint unit) => (unit & 0xF) switch
    {
        1 => "厘米",
        2 => "弧度",
        3 => "英寸",
        4 => "度",
        0 => "无",
        _ => $"单位{(unit & 0xF)}",
    };

    private static int Exponent(uint unitExp)
    {
        int e = (int)(unitExp & 0x0F);
        // 4 位补码
        if (e >= 8) e -= 16;
        return e;
    }

    private static string Yn(bool b) => b ? "✓" : "✗";
}
