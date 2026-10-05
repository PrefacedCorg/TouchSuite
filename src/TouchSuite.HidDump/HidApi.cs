using System.Runtime.InteropServices;
using System.Text;

namespace TouchSuite.HidDump;

/// <summary>
/// HID API 封装。解析方式对齐 TouchSuite.App/HidReader.cs：
/// <para>- RawInput 枚举设备 + 取 preparsed（不先打开设备）；</para>
/// <para>- HIDP_CAPS 展平中间的 USHORT Reserved[17]，避开 .NET 10 上 ByValArray out 场景的 CLR 崩溃；</para>
/// <para>- HIDP_VALUE_CAPS / HIDP_BUTTON_CAPS 都是 72 字节定长结构，用原始缓冲 + 偏移手工解。</para>
/// </summary>
internal static class HidApi
{
    public const int ReportTypeInput = 0;
    public const int ReportTypeOutput = 1;
    public const int ReportTypeFeature = 2;

    private const int HidpStatusSuccess = 0x00110000;
    private const int CapSize = 72;   // sizeof(HIDP_VALUE_CAPS) == sizeof(HIDP_BUTTON_CAPS)

    // ================= 设备枚举 =================

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICELIST { public IntPtr hDevice; public uint dwType; }

    private const uint RIDI_DEVICENAME = 0x20000007;
    private const uint RIDI_PREPARSEDDATA = 0x20000005;
    private const uint RIM_TYPEHID = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList([In, Out] RAWINPUTDEVICELIST[]? list, ref uint count, uint size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, IntPtr data, ref uint size);

    /// <summary>RawInput 枚举到的单个 HID 顶层集合（同一物理设备可有多个）。</summary>
    public sealed class HidDevice : IDisposable
    {
        public required string Path;
        public required IntPtr RawHandle;    // RawInput 设备句柄（WM_INPUT 头里用它匹配）
        public required ushort UsagePage;
        public required ushort Usage;
        public required IntPtr Preparsed;
        public required HidCaps Caps;
        public bool IsTouch => UsagePage == 0x0D && Usage is 0x04 or 0x05 or 0x20 or 0x22 or 0x23;

        public void Dispose()
        {
            if (Preparsed != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Preparsed);
                Preparsed = IntPtr.Zero;
            }
        }
    }

    /// <summary>枚举当前所有 HID 顶层集合（按 路径+TLC 去重，触摸类排前面）。</summary>
    public static List<HidDevice> Scan()
    {
        uint count = 0;
        uint structSize = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        var list = new List<HidDevice>();
        if (GetRawInputDeviceList(null, ref count, structSize) == unchecked((uint)-1) || count == 0)
            return list;

        var raw = new RAWINPUTDEVICELIST[count];
        if (GetRawInputDeviceList(raw, ref count, structSize) == unchecked((uint)-1))
            return list;

        var seen = new HashSet<(string, ushort, ushort)>();
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i].dwType != RIM_TYPEHID)
                continue;

            string path = GetDeviceName(raw[i].hDevice);
            IntPtr preparsed = GetPreparsedData(raw[i].hDevice);
            if (preparsed == IntPtr.Zero || !HidP_GetCaps(preparsed, out HidCaps caps))
            {
                if (preparsed != IntPtr.Zero) Marshal.FreeHGlobal(preparsed);
                continue;
            }

            if (!seen.Add((path, caps.UsagePage, caps.Usage)))
            {
                Marshal.FreeHGlobal(preparsed);
                continue;
            }

            list.Add(new HidDevice
            {
                Path = path,
                RawHandle = raw[i].hDevice,
                UsagePage = caps.UsagePage,
                Usage = caps.Usage,
                Preparsed = preparsed,
                Caps = caps,
            });
        }

        return list;
    }

    private static string GetDeviceName(IntPtr device)
    {
        uint size = 0;
        GetRawInputDeviceInfo(device, RIDI_DEVICENAME, IntPtr.Zero, ref size);
        if (size == 0)
            return "(无名)";

        IntPtr buf = Marshal.AllocHGlobal((int)(size * 2 + 2));
        try
        {
            uint chars = size;
            GetRawInputDeviceInfo(device, RIDI_DEVICENAME, buf, ref chars);
            return (Marshal.PtrToStringUni(buf) ?? "(无名)").Replace("\0", "");
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static IntPtr GetPreparsedData(IntPtr device)
    {
        uint size = 0;
        GetRawInputDeviceInfo(device, RIDI_PREPARSEDDATA, IntPtr.Zero, ref size);
        if (size == 0)
            return IntPtr.Zero;

        IntPtr buf = Marshal.AllocHGlobal((int)size);
        uint actual = size;
        if (GetRawInputDeviceInfo(device, RIDI_PREPARSEDDATA, buf, ref actual) == unchecked((uint)-1))
        {
            Marshal.FreeHGlobal(buf);
            return IntPtr.Zero;
        }
        return buf;
    }

    // ================= HIDP 能力 =================

    /// <summary>HIDP_CAPS：中间 Reserved[17] 展平成独立字段（同 HidReader.cs 的处理）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct HidCaps
    {
        public ushort Usage; public ushort UsagePage;
        public ushort InputReportByteLength; public ushort OutputReportByteLength; public ushort FeatureReportByteLength;
        public ushort Res01, Res02, Res03, Res04, Res05, Res06, Res07, Res08;
        public ushort Res09, Res10, Res11, Res12, Res13, Res14, Res15, Res16, Res17;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps; public ushort NumberInputValueCaps; public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps; public ushort NumberOutputValueCaps; public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps; public ushort NumberFeatureValueCaps; public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")]
    private static extern bool HidP_GetCaps(IntPtr preparsedData, out HidCaps caps);

    [DllImport("hid.dll")]
    private static extern int HidP_GetValueCaps(int reportType, byte[] caps, ref ushort capsLength, IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetButtonCaps(int reportType, byte[] caps, ref ushort capsLength, IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection, ushort usage,
        out uint usageValue, IntPtr preparsedData, byte[] report, uint reportLength);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection,
        [Out] ushort[] usageList, ref uint usageLength, IntPtr preparsedData, byte[] report, uint reportLength);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsageValueArray(int reportType, ushort usagePage, ushort linkCollection, ushort usage,
        [Out] byte[] usageValueBuffer, ushort usageValueByteLength, IntPtr preparsedData, byte[] report, uint reportLength);

    /// <summary>
    /// 值能力（HIDP_VALUE_CAPS，72B）。布局取自 Windows SDK hidpi.h（10.0.26100）：
    /// UsagePage@0、ReportID@2、IsAlias@3、LinkCollection@6、IsRange@12、IsAbsolute@15、
    /// BitSize@18、ReportCount@20、UnitsExp@32、Units@36、Logical@40/44、Physical@48/52、Usage@56/58。
    /// Logical/Physical/Usage 偏移与 HidReader.cs 一致（已在主程序验证）。
    /// </summary>
    public sealed record ValueCap(
        ushort UsagePage, byte ReportID, bool IsAlias, ushort LinkCollection,
        bool IsRange, bool IsAbsolute,
        ushort BitSize, ushort ReportCount,
        int LogicalMin, int LogicalMax, int PhysicalMin, int PhysicalMax,
        int UnitsExp, int Units,
        ushort UsageMin, ushort UsageMax);

    /// <summary>
    /// 按钮能力（HIDP_BUTTON_CAPS，72B）：中间是 ULONG Reserved[9]，Range/NotRange 联合体同样在 56：
    /// UsagePage@0、ReportID@2、IsAlias@3、LinkCollection@6、IsRange@12、Usage@56/58。
    /// </summary>
    public sealed record ButtonCap(
        ushort UsagePage, byte ReportID, bool IsAlias, ushort LinkCollection,
        bool IsRange, ushort UsageMin, ushort UsageMax);

    public static List<ValueCap> ValueCaps(IntPtr preparsed, int reportType, ushort number)
    {
        var list = new List<ValueCap>();
        if (number <= 0)
            return list;

        var buf = new byte[CapSize * number];
        ushort len = number;
        if (HidP_GetValueCaps(reportType, buf, ref len, preparsed) != HidpStatusSuccess)
            return list;

        for (int k = 0; k < len; k++)
        {
            int off = k * CapSize;
            list.Add(new ValueCap(
                BitConverter.ToUInt16(buf, off + 0),      // UsagePage
                buf[off + 2],                             // ReportID
                buf[off + 3] != 0,                        // IsAlias
                BitConverter.ToUInt16(buf, off + 6),      // LinkCollection
                buf[off + 12] != 0,                       // IsRange
                buf[off + 15] != 0,                       // IsAbsolute
                BitConverter.ToUInt16(buf, off + 18),     // BitSize
                BitConverter.ToUInt16(buf, off + 20),     // ReportCount
                BitConverter.ToInt32(buf, off + 40),      // LogicalMin
                BitConverter.ToInt32(buf, off + 44),      // LogicalMax
                BitConverter.ToInt32(buf, off + 48),      // PhysicalMin
                BitConverter.ToInt32(buf, off + 52),      // PhysicalMax
                BitConverter.ToInt32(buf, off + 32),      // UnitsExp
                BitConverter.ToInt32(buf, off + 36),      // Units
                BitConverter.ToUInt16(buf, off + 56),     // UsageMin（NotRange.Usage 同偏移）
                BitConverter.ToUInt16(buf, off + 58)));   // UsageMax
        }
        return list;
    }

    public static List<ButtonCap> ButtonCaps(IntPtr preparsed, int reportType, ushort number)
    {
        var list = new List<ButtonCap>();
        if (number <= 0)
            return list;

        var buf = new byte[CapSize * number];
        ushort len = number;
        if (HidP_GetButtonCaps(reportType, buf, ref len, preparsed) != HidpStatusSuccess)
            return list;

        for (int k = 0; k < len; k++)
        {
            int off = k * CapSize;
            list.Add(new ButtonCap(
                BitConverter.ToUInt16(buf, off + 0),      // UsagePage
                buf[off + 2],                             // ReportID
                buf[off + 3] != 0,                        // IsAlias
                BitConverter.ToUInt16(buf, off + 6),      // LinkCollection
                buf[off + 12] != 0,                       // IsRange
                BitConverter.ToUInt16(buf, off + 56),     // UsageMin（NotRange.Usage 同偏移）
                BitConverter.ToUInt16(buf, off + 58)));   // UsageMax
        }
        return list;
    }

    /// <summary>
    /// 从输入报文取单个 usage 的值。默认严格按 LinkCollection 取（多触点设备必须严格，否则会串到别的手指）；
    /// allowAnyLink = true 时才在失败后回退到 0（全集合搜索，只给"caps 的 Link 传进去查不到"的单触点设备用）。
    /// </summary>
    public static bool TryGetUsageValue(IntPtr preparsed, ushort page, ushort link, ushort usage,
        byte[] report, int length, out uint value, bool allowAnyLink = false)
    {
        if (HidP_GetUsageValue(ReportTypeInput, page, link, usage, out value, preparsed, report, (uint)length) == HidpStatusSuccess)
            return true;
        if (allowAnyLink && link != 0
            && HidP_GetUsageValue(ReportTypeInput, page, 0, usage, out value, preparsed, report, (uint)length) == HidpStatusSuccess)
            return true;
        value = 0;
        return false;
    }

    /// <summary>取 (usagePage, linkCollection) 分组下当前按下的 usage 列表。</summary>
    public static HashSet<ushort> PressedUsages(IntPtr preparsed, ushort page, ushort link, byte[] report, int length)
    {
        var usages = new ushort[64];
        uint n = (uint)usages.Length;
        if (HidP_GetUsages(ReportTypeInput, page, link, usages, ref n, preparsed, report, (uint)length) != HidpStatusSuccess || n == 0)
            return new HashSet<ushort>();
        var set = new HashSet<ushort>();
        for (uint i = 0; i < n; i++)
            set.Add(usages[i]);
        return set;
    }

    /// <summary>
    /// 读值帽数组（ReportCount &gt; 1，如"一次报 N 个触点"的数字化器）：返回各元素当前值。
    /// 位宽 8/16/32 按字节对齐解析，其他位宽只按字节显示。
    /// </summary>
    public static bool TryGetUsageValueArray(IntPtr preparsed, ValueCap c, byte[] report, int length, out string text)
    {
        text = "";
        if (c.BitSize == 0 || c.ReportCount == 0)
            return false;
        int bytes = (c.BitSize * c.ReportCount + 7) / 8;
        var buf = new byte[bytes];
        if (HidP_GetUsageValueArray(ReportTypeInput, c.UsagePage, c.LinkCollection, c.UsageMin, buf, (ushort)bytes,
                preparsed, report, (uint)length) != HidpStatusSuccess)
            return false;

        int elemBytes = (c.BitSize + 7) / 8;
        int show = Math.Min((int)c.ReportCount, 10);
        var parts = new List<string>(show);
        for (int i = 0; i < show; i++)
        {
            int off = i * elemBytes;
            if (off + elemBytes > buf.Length)
                break;
            long v = c.BitSize switch
            {
                8 => buf[off],
                16 => BitConverter.ToUInt16(buf, off),
                32 => BitConverter.ToUInt32(buf, off),
                _ => buf[off],
            };
            parts.Add(v.ToString());
        }
        text = string.Join(", ", parts) + (c.ReportCount > show ? $" …(+{c.ReportCount - show})" : "");
        return true;
    }

    // ================= 设备打开 / 名称串 =================

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint OPEN_EXISTING = 3;
    private static readonly IntPtr InvalidHandle = new(-1);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(IntPtr handle, [Out] byte[] buffer, int toRead, out int read, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(IntPtr handle, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    private static extern bool HidD_GetProductString(IntPtr handle, [Out] StringBuilder buffer, uint bufferLength);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    private static extern bool HidD_GetManufacturerString(IntPtr handle, [Out] StringBuilder buffer, uint bufferLength);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    private static extern bool HidD_GetSerialNumberString(IntPtr handle, [Out] StringBuilder buffer, uint bufferLength);

    public static bool Ok(IntPtr h) => h != IntPtr.Zero && h != InvalidHandle;

    /// <summary>打开设备（先读写共享，失败退只读）；返回句柄与失败原因。</summary>
    public static IntPtr Open(string path, out string error)
    {
        IntPtr h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (Ok(h))
        {
            error = "";
            return h;
        }

        int err = Marshal.GetLastWin32Error();
        h = CreateFile(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (Ok(h))
        {
            error = "";
            return h;
        }

        error = $"Win32 {err}";
        return h;
    }

    public static void CancelIo(IntPtr h) => CancelIoEx(h, IntPtr.Zero);

    public static void Close(IntPtr h)
    {
        if (Ok(h))
            CloseHandle(h);
    }

    public static bool Read(IntPtr h, byte[] buffer, out int read) => ReadFile(h, buffer, buffer.Length, out read, IntPtr.Zero);

    private static string HidString(Func<StringBuilder, uint, bool> getter)
    {
        var sb = new StringBuilder(256);
        return getter(sb, (uint)sb.Capacity) ? sb.ToString().Trim('\0') : "";
    }

    public static (string Product, string Manufacturer, string Serial) Strings(IntPtr h)
        => (HidString((b, n) => HidD_GetProductString(h, b, n)),
            HidString((b, n) => HidD_GetManufacturerString(h, b, n)),
            HidString((b, n) => HidD_GetSerialNumberString(h, b, n)));

    // ================= 展示辅助（InfoBar / HidTableText 句式）=================

    /// <summary>页列文字：如 “0x0D 数字化器”。</summary>
    public static string PageText(ushort page)
    {
        string name = PageName(page);
        return name.Length > 0 ? $"0x{page:X2} {name}" : $"0x{page:X2}";
    }

    /// <summary>Usage 列文字：如 “0x48 宽度”（不含页前缀，页已在页列）。</summary>
    public static string UsageLabel(ushort page, ushort usage)
    {
        string name = UsageName(page, usage);
        return $"0x{usage:X2}{(string.IsNullOrEmpty(name) ? "" : " " + name)}";
    }

    /// <summary>常用 usage 页的中文名。</summary>
    public static string PageName(ushort page) => page switch
    {
        0x01 => "通用桌面",
        0x02 => "仿真",
        0x05 => "游戏",
        0x07 => "键盘",
        0x08 => "LED",
        0x09 => "按钮",
        0x0C => "消费类",
        0x0D => "数字化器",
        0x0F => "PID",
        0x14 => "Unicode",
        var p when p >= 0xFF00 => "厂商自定义",
        _ => "",
    };

    public static string UsageText(ushort page, ushort usage) => $"0x{page:X2}:0x{usage:X2}";

    /// <summary>常用 usage 的中文名（对齐校准向导 InfoBar 的词汇）。</summary>
    public static string UsageName(ushort page, ushort usage) => (page, usage) switch
    {
        (0x0D, 0x01) => "数字化器",
        (0x0D, 0x02) => "笔",
        (0x0D, 0x04) => "触摸屏",
        (0x0D, 0x05) => "触摸板",
        (0x0D, 0x20) => "数字化器集合",
        (0x0D, 0x22) => "触摸数字化器",
        (0x0D, 0x23) => "集成触摸",
        (0x0D, 0x30) => "压感",
        (0x0D, 0x31) => "笔尖高度",
        (0x0D, 0x32) => "感应范围内",
        (0x0D, 0x3A) => "翻转",
        (0x0D, 0x3B) => "扭转",
        (0x0D, 0x3C) => "倾斜X",
        (0x0D, 0x3D) => "倾斜Y",
        (0x0D, 0x42) => "笔尖接触",
        (0x0D, 0x43) => "第二桶形开关",
        (0x0D, 0x44) => "桶形开关",
        (0x0D, 0x45) => "橡皮擦",
        (0x0D, 0x48) => "宽度",
        (0x0D, 0x49) => "高度",
        (0x0D, 0x51) => "接触ID",
        (0x0D, 0x52) => "设备模式",
        (0x0D, 0x54) => "接触数量",
        (0x0D, 0x55) => "最大接触数",
        (0x0D, 0x56) => "扫描时间",
        (0x01, 0x30) => "X坐标",
        (0x01, 0x31) => "Y坐标",
        (0x01, 0x32) => "Z坐标",
        (0x01, 0x33) => "旋转X",
        (0x01, 0x34) => "旋转Y",
        (0x01, 0x35) => "旋转Z",
        (0x01, 0x36) => "滑块",
        (0x01, 0x37) => "拨盘",
        (0x01, 0x38) => "滚轮",
        (0x01, 0x39) => "帽形开关",
        (0x09, _) => $"按钮 #{usage}",
        _ => "",
    };

    /// <summary>顶层集合标签，如 “0x0D:0x22 触摸数字化器”。</summary>
    public static string TlcText(ushort page, ushort usage)
    {
        string name = UsageName(page, usage);
        return $"{UsageText(page, usage)}{(string.IsNullOrEmpty(name) ? "" : " " + name)}";
    }

    /// <summary>值帽标签：单值 “0x0D:0x30 压感”；范围 “0x0D:0x30..0x0D:0x31 压感..笔尖高度”。</summary>
    public static string CapLabel(ValueCap c)
    {
        if (!c.IsRange)
        {
            string name = UsageName(c.UsagePage, c.UsageMin);
            return $"{UsageText(c.UsagePage, c.UsageMin)}{(string.IsNullOrEmpty(name) ? "" : " " + name)}";
        }
        return $"{UsageText(c.UsagePage, c.UsageMin)}..{UsageText(c.UsagePage, c.UsageMax)}"
             + $" {UsageName(c.UsagePage, c.UsageMin)}..{UsageName(c.UsagePage, c.UsageMax)}";
    }

    public static string CapLabel(ButtonCap b)
    {
        if (!b.IsRange)
        {
            string name = UsageName(b.UsagePage, b.UsageMin);
            return $"{UsageText(b.UsagePage, b.UsageMin)}{(string.IsNullOrEmpty(name) ? "" : " " + name)}";
        }
        return $"{UsageText(b.UsagePage, b.UsageMin)}..{UsageText(b.UsagePage, b.UsageMax)}";
    }

    /// <summary>带具体 usage 的标签（范围值帽逐项实时值用）。</summary>
    public static string CapLabel(ValueCap c, ushort usage)
    {
        string name = UsageName(c.UsagePage, usage);
        return $"{UsageText(c.UsagePage, usage)}{(string.IsNullOrEmpty(name) ? "" : " " + name)}";
    }

    public static string CapLabel(ButtonCap b, ushort usage)
    {
        string name = UsageName(b.UsagePage, usage);
        return $"{UsageText(b.UsagePage, usage)}{(string.IsNullOrEmpty(name) ? "" : " " + name)}";
    }

    /// <summary>物理量程（紧凑）：0..3050(cm×10^-2)；无物理量程给 —。</summary>
    public static string PhysText(ValueCap c)
    {
        if (c.PhysicalMin == 0 && c.PhysicalMax == 0)
            return "—";
        int nibble = (c.Units >> 12) & 0xF;
        string unit = nibble switch
        {
            0x1 => "cm",
            0x2 => "rad",
            0x3 => "in",
            0x4 => "deg",
            _ => "",
        };
        int exp = c.UnitsExp & 0xF;
        if (exp >= 8) exp -= 16;
        string suffix = unit.Length > 0 || exp != 0 ? $"({unit}×10^{exp})" : "";
        return c.PhysicalMin == 0 ? $"{c.PhysicalMax}{suffix}" : $"{c.PhysicalMin}..{c.PhysicalMax}{suffix}";
    }

    /// <summary>逻辑量程 → 物理 → 每计数毫米（仅 宽度/高度 且物理量程有效、量纲为 厘米/英寸 时给出）。</summary>
    public static double? MmPerCount(ValueCap c)
    {
        if (c.LogicalMax <= c.LogicalMin || c.PhysicalMax <= c.PhysicalMin)
            return null;
        int nibble = (c.Units >> 12) & 0xF;
        if (nibble is not (0x1 or 0x3))
            return null;
        bool english = nibble == 0x3;
        int exp = c.UnitsExp & 0xF;
        if (exp >= 8) exp -= 16;
        double unitMm = Math.Pow(10, exp) * (english ? 25.4 : 10.0);
        return unitMm * c.PhysicalMax / (double)c.LogicalMax;
    }

    /// <summary>宽/高 usage 的 mm 换算提示（紧凑）。</summary>
    public static string MmHint(ValueCap c)
        => (c.UsagePage, c.UsageMin) is (0x0D, 0x48) or (0x0D, 0x49) && MmPerCount(c) is double mm
            ? $"　→ {mm:0.######}mm/计数"
            : "";

    /// <summary>值帽的范围串（紧凑单行：Link 已单独成列，不在这里重复）。</summary>
    public static string RangeText(ValueCap c)
        => $"逻辑 {c.LogicalMin}..{c.LogicalMax}　物理 {PhysText(c)}　RC {c.ReportCount}×{c.BitSize}位"
         + $"　{(c.IsAbsolute ? "绝对" : "相对")}"
         + $"{(c.IsRange ? " 范围" : "")}{(c.IsAlias ? " 别名" : "")}"
         + $"{(c.ReportID != 0 ? $" ID{c.ReportID}" : "")}{MmHint(c)}";

    /// <summary>按钮帽的范围串（紧凑）。</summary>
    public static string RangeText(ButtonCap b)
        => (b.IsRange ? $"范围 {b.UsageMin}..{b.UsageMax}" : "单值")
         + $"{(b.IsAlias ? " 别名" : "")}{(b.ReportID != 0 ? $" ID{b.ReportID}" : "")}";
}
