using System.Runtime.InteropServices;

namespace TouchSuite.HidTouchBridge;

/// <summary>
/// 原始 HID 触摸屏读取（RawInput + HidP）。
/// <para>
/// 与 TouchSuite.HidDump / TouchSuite.App.HidReader 同一套读法：
/// ① RawInput 枚举设备 + 取 preparsed（不先打开设备，不受句柄独占影响）；
/// ② 每个 usage 必须按**自己的 LinkCollection（触点槽位）**取，绝不能全传 0
///    —— 多触点设备的 X/Y 声明在 L1..L10，传 0 会取不到；
/// ③ 一份 WM_INPUT 可带多份子报文（dwCount），逐份解，每份都是一帧完整快照。
/// </para>
/// </summary>
internal static class RawHidTouch
{
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RIDI_DEVICENAME = 0x20000007;
    private const uint RIDI_PREPARSEDDATA = 0x20000005;
    private const int RIM_TYPEHID = 2;

    private const int HidPInput = 0;
    private const int HidPFeature = 2;
    private const int HidpStatusSuccess = 0x00110000;
    private const int CapSize = 72;   // sizeof(HIDP_VALUE_CAPS) == sizeof(HIDP_BUTTON_CAPS)

    // ================= Win32 =================

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICELIST { public IntPtr hDevice; public uint dwType; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE { public ushort usUsagePage; public ushort usUsage; public uint dwFlags; public IntPtr hwndTarget; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER { public uint dwType; public uint dwSize; public IntPtr hDevice; public IntPtr wParam; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList([In, Out] RAWINPUTDEVICELIST[]? list, ref uint count, uint size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, IntPtr data, ref uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint numDevices, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);

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

    // ================= 模型 =================

    /// <summary>某个触点槽位（LinkCollection）用到的字段。</summary>
    internal sealed class LinkInfo
    {
        public ushort Link;
        public bool HasX, HasY, HasCid, HasTip, HasInRange;
        public int XMin, XMax, YMin, YMax;
    }

    /// <summary>一个触摸屏顶层集合。</summary>
    internal sealed class TouchDevice : IDisposable
    {
        public string Path = "";
        public IntPtr RawHandle;
        public ushort UsagePage, Usage;
        public IntPtr Preparsed;
        public bool IsTouch;
        public readonly List<LinkInfo> Links = new();
        public string TlcLabel => $"0x{UsagePage:X2}:0x{Usage:X2}";

        public void Dispose()
        {
            if (Preparsed != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Preparsed);
                Preparsed = IntPtr.Zero;
            }
        }
    }

    /// <summary>一帧里的一根「按下」触点（已归一到 0..1）。</summary>
    internal readonly record struct Contact(int Link, int ContactId, double Nx, double Ny, int XRaw, int YRaw);

    /// <summary>一帧快照。</summary>
    internal readonly record struct Frame(int ContactCount, IReadOnlyList<Contact> Contacts);

    // ================= 枚举 =================

    /// <summary>枚举全部 HID 顶层集合（触摸类排前面）。</summary>
    public static List<TouchDevice> Scan()
    {
        var list = new List<TouchDevice>();

        uint count = 0;
        uint structSize = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
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

            var dev = new TouchDevice
            {
                Path = path,
                RawHandle = raw[i].hDevice,
                UsagePage = caps.UsagePage,
                Usage = caps.Usage,
                Preparsed = preparsed,
                IsTouch = caps.UsagePage == 0x0D && caps.Usage is 0x04 or 0x05 or 0x20 or 0x22 or 0x23,
            };
            BuildLinks(dev, caps);
            list.Add(dev);
        }

        list.Sort((a, b) => (b.IsTouch ? 1 : 0).CompareTo(a.IsTouch ? 1 : 0));
        return list;
    }

    private static void BuildLinks(TouchDevice dev, HidCaps caps)
    {
        var byLink = new Dictionary<ushort, LinkInfo>();

        LinkInfo Get(ushort link)
        {
            if (!byLink.TryGetValue(link, out LinkInfo? li))
                byLink[link] = li = new LinkInfo { Link = link };
            return li;
        }

        if (caps.NumberInputValueCaps > 0)
        {
            var buf = new byte[CapSize * caps.NumberInputValueCaps];
            ushort len = caps.NumberInputValueCaps;
            if (HidP_GetValueCaps(HidPInput, buf, ref len, dev.Preparsed) == HidpStatusSuccess)
            {
                for (int k = 0; k < len; k++)
                {
                    int off = k * CapSize;
                    ushort page = BitConverter.ToUInt16(buf, off + 0);
                    ushort link = BitConverter.ToUInt16(buf, off + 6);
                    int logMin = BitConverter.ToInt32(buf, off + 40);
                    int logMax = BitConverter.ToInt32(buf, off + 44);
                    ushort usage = BitConverter.ToUInt16(buf, off + 56);
                    if (link == 0)
                        continue;   // 槽位 0 是报告级字段（接触数量等），不当作触点

                    if (page == 0x01 && usage == 0x30) { LinkInfo li = Get(link); li.HasX = true; li.XMin = logMin; li.XMax = logMax; }
                    else if (page == 0x01 && usage == 0x31) { LinkInfo li = Get(link); li.HasY = true; li.YMin = logMin; li.YMax = logMax; }
                    else if (page == 0x0D && usage == 0x51) { Get(link).HasCid = true; }
                }
            }
        }

        if (caps.NumberInputButtonCaps > 0)
        {
            var buf = new byte[CapSize * caps.NumberInputButtonCaps];
            ushort len = caps.NumberInputButtonCaps;
            if (HidP_GetButtonCaps(HidPInput, buf, ref len, dev.Preparsed) == HidpStatusSuccess)
            {
                for (int k = 0; k < len; k++)
                {
                    int off = k * CapSize;
                    ushort page = BitConverter.ToUInt16(buf, off + 0);
                    ushort link = BitConverter.ToUInt16(buf, off + 6);
                    ushort uMin = BitConverter.ToUInt16(buf, off + 56);
                    if (link == 0 || page != 0x0D)
                        continue;

                    // 本设备的按钮帽是单值（0x42 / 0x32 / 0x47），逐个对上即可
                    switch (uMin)
                    {
                        case 0x42: Get(link).HasTip = true; break;        // 笔尖接触
                        case 0x32: Get(link).HasInRange = true; break;    // 感应范围内
                    }
                }
            }
        }

        dev.Links.AddRange(byLink.Values.Where(x => x.HasX || x.HasY).OrderBy(x => x.Link));
    }

    // ================= 注册 / 收包 =================

    /// <summary>注册指定顶层集合的原始输入（RIDEV_INPUTSINK：窗口不在前台也能收）。</summary>
    public static bool Register(IntPtr hwnd, ushort usagePage, ushort usage)
    {
        var devices = new[]
        {
            new RAWINPUTDEVICE { usUsagePage = usagePage, usUsage = usage, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd },
        };
        return RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    /// <summary>解 WM_INPUT：返回设备句柄 + 全部子报文（每份都是一帧完整快照）。</summary>
    public static bool TryGetHidData(IntPtr lParam, out IntPtr hDevice, out byte[][] reports)
    {
        hDevice = IntPtr.Zero;
        reports = Array.Empty<byte[]>();

        uint header = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        uint size = 0;
        GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, header);
        if (size == 0)
            return false;

        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, RID_INPUT, buf, ref size, header) != size)
                return false;
            if (Marshal.ReadInt32(buf, 0) != RIM_TYPEHID)
                return false;

            hDevice = Marshal.ReadIntPtr(buf, 8);
            int sizeHid = Marshal.ReadInt32(buf, (int)header);
            int count = Marshal.ReadInt32(buf, (int)header + 4);
            if (sizeHid <= 0 || count <= 0)
                return false;

            reports = new byte[count][];
            IntPtr data = buf + (int)header + 8;
            for (int i = 0; i < count; i++)
            {
                var report = new byte[sizeHid];
                Marshal.Copy(data + i * sizeHid, report, 0, sizeHid);
                reports[i] = report;
            }
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    // ================= 解码 =================

    private const ushort UsageContactCount = 0x54;

    /// <summary>
    /// 把一份子报文解成「本帧按下的触点」列表。
    /// 判活规则：该槽位声明了「笔尖接触 0x0D:0x42」就按 TipSwitch 判；否则退回「感应范围内 0x0D:0x32」。
    /// 未按下的槽位不返回 —— 由注入端据此产生抬起（UP）。
    /// </summary>
    public static Frame Decode(TouchDevice dev, byte[] report)
    {
        uint len = (uint)report.Length;

        int contactCount = 0;
        if (HidP_GetUsageValue(HidPInput, 0x0D, 0, UsageContactCount, out uint cc, dev.Preparsed, report, len) == HidpStatusSuccess)
            contactCount = (int)cc;

        var contacts = new List<Contact>();
        foreach (LinkInfo li in dev.Links)
        {
            if (!li.HasX || !li.HasY)
                continue;

            bool pressed;
            if (li.HasTip)
                pressed = IsPressed(dev, li.Link, 0x42, report, len);
            else if (li.HasInRange)
                pressed = IsPressed(dev, li.Link, 0x32, report, len);
            else
                pressed = true;   // 没有按钮可判：只要本槽位有坐标就算按下
            if (!pressed)
                continue;

            if (HidP_GetUsageValue(HidPInput, 0x01, li.Link, 0x30, out uint x, dev.Preparsed, report, len) != HidpStatusSuccess)
                continue;
            if (HidP_GetUsageValue(HidPInput, 0x01, li.Link, 0x31, out uint y, dev.Preparsed, report, len) != HidpStatusSuccess)
                continue;

            int cid = 0;
            if (li.HasCid && HidP_GetUsageValue(HidPInput, 0x0D, li.Link, 0x51, out uint c, dev.Preparsed, report, len) == HidpStatusSuccess)
                cid = (int)c;

            contacts.Add(new Contact(
                li.Link,
                cid,
                Norm(x, li.XMin, li.XMax),
                Norm(y, li.YMin, li.YMax),
                (int)x, (int)y));
        }

        return new Frame(contactCount, contacts);
    }

    private static bool IsPressed(TouchDevice dev, ushort link, ushort usage, byte[] report, uint len)
    {
        var usages = new ushort[64];
        uint n = (uint)usages.Length;
        if (HidP_GetUsages(HidPInput, 0x0D, link, usages, ref n, dev.Preparsed, report, len) != HidpStatusSuccess)
            return false;
        for (uint i = 0; i < n; i++)
        {
            if (usages[i] == usage)
                return true;
        }
        return false;
    }

    private static double Norm(uint raw, int min, int max)
    {
        if (max <= min)
            return 0.0;
        double t = (raw - (double)min) / (max - min);
        return t < 0 ? 0 : t > 1 ? 1 : t;
    }

    // ================= 设备名 / preparsed =================

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

    /// <summary>短路径（去掉 \\?\HID# 前缀与结尾的接口 GUID）。</summary>
    public static string ShortPath(string p)
    {
        if (p.StartsWith(@"\\?\HID#", StringComparison.OrdinalIgnoreCase))
            p = p.Substring(8);
        int hash = p.LastIndexOf('#');
        if (hash > 0 && hash + 1 < p.Length && p[hash + 1] == '{')
            p = p.Substring(0, hash);
        return p;
    }
}
