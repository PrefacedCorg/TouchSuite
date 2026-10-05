using System.Runtime.InteropServices;
using System.Text;

namespace TouchSuite.App.Old.Helpers;

/// <summary>
/// 原始 HID 触摸读取：Raw Input 枚举 + 能力探测，并解码输入报文里的
/// 接触尺寸（Digitizer Width 0x48 / Height 0x49）与坐标（0x01:0x30/0x31）。
/// 不打开设备、不依赖 Windows 的 rcContact 映射，因此能拿到被系统忽略的尺寸字段。
/// </summary>
public static class RawHidScan
{
    private const uint RIDI_PREPARSEDDATA = 0x20000005;
    private const uint RIDI_DEVICENAME = 0x20000007;
    private const uint RIM_TYPEHID = 2;
    private const int HidP_Input = 0;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;
    private const int HidP_ValueCapsSize = 72;

    private const uint RID_INPUT = 0x10000003;
    private const uint RIDEV_INPUTSINK = 0x00000100;

    public sealed record HidTouchInfo(
        string DeviceName, bool IsTouchScreen, bool HasWidth, bool HasHeight,
        int InputValueCaps, string UsageSummary, string WidthDetail, string HeightDetail);

    public sealed record RawTouchSample(
        double? WidthMm, double? HeightMm, double? XNorm, double? YNorm,
        int WidthLogical, int HeightLogical, string Hex);

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICELIST { public IntPtr hDevice; public uint dwType; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER { public uint dwType; public uint dwSize; public IntPtr hDevice; public IntPtr wParam; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE { public ushort usUsagePage; public ushort usUsage; public uint dwFlags; public IntPtr hwndTarget; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage; public ushort UsagePage;
        public ushort InputReportByteLength; public ushort OutputReportByteLength; public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps; public ushort NumberInputValueCaps; public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps; public ushort NumberOutputValueCaps; public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps; public ushort NumberFeatureValueCaps; public ushort NumberFeatureDataIndices;
    }

    private sealed class DeviceCtx
    {
        public IntPtr Preparsed;
        public bool IsTouch;
        public bool HasWidth, HasHeight;
        public int WLogMax, WPhysMax, WExp, WUnits;
        public int HLogMax, HPhysMax, HExp, HUnits;
        public int XLogMax, YLogMax;
    }

    private static readonly Dictionary<IntPtr, DeviceCtx> Ctx = new();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList([In, Out] RAWINPUTDEVICELIST[]? list, ref uint count, uint size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, IntPtr data, ref uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint numDevices, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    [DllImport("hid.dll")]
    private static extern bool HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS caps);

    [DllImport("hid.dll")]
    private static extern int HidP_GetValueCaps(int reportType, byte[] valueCaps, ref ushort valueCapsLength, IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection, ushort usage,
        out uint usageValue, IntPtr preparsedData, byte[] report, uint reportLength);

    // ---------- 枚举 + 能力探测 ----------

    public static List<HidTouchInfo> Scan()
    {
        var results = new List<HidTouchInfo>();

        uint count = 0;
        uint structSize = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        if (GetRawInputDeviceList(null, ref count, structSize) == unchecked((uint)-1) || count == 0)
            return results;

        var list = new RAWINPUTDEVICELIST[count];
        if (GetRawInputDeviceList(list, ref count, structSize) == unchecked((uint)-1))
            return results;

        for (int i = 0; i < list.Length; i++)
        {
            if (list[i].dwType != RIM_TYPEHID)
                continue;

            IntPtr hDevice = list[i].hDevice;
            string name = GetDeviceName(hDevice);
            IntPtr preparsed = GetPreparsedData(hDevice, out bool ok);
            if (!ok || preparsed == IntPtr.Zero)
                continue;

            if (!HidP_GetCaps(preparsed, out HIDP_CAPS caps))
            {
                Marshal.FreeHGlobal(preparsed);
                continue;
            }

            var ctx = new DeviceCtx
            {
                Preparsed = preparsed,
                IsTouch = caps.UsagePage == 0x0D && caps.Usage == 0x04,
            };

            var usages = new StringBuilder();
            int shown = 0;

            if (caps.NumberInputValueCaps > 0)
            {
                var buffer = new byte[HidP_ValueCapsSize * caps.NumberInputValueCaps];
                ushort len = caps.NumberInputValueCaps;
                int status = HidP_GetValueCaps(HidP_Input, buffer, ref len, preparsed);
                if (status == HIDP_STATUS_SUCCESS)
                {
                    for (int k = 0; k < len; k++)
                    {
                        int off = k * HidP_ValueCapsSize;
                        ushort usagePage = BitConverter.ToUInt16(buffer, off + 0);
                        ushort usage = BitConverter.ToUInt16(buffer, off + 56);
                        int logicalMax = BitConverter.ToInt32(buffer, off + 44);
                        int physicalMax = BitConverter.ToInt32(buffer, off + 52);
                        int units = BitConverter.ToInt32(buffer, off + 36);
                        int unitsExp = BitConverter.ToInt32(buffer, off + 32);

                        if (shown < 24) { usages.Append($"0x{usagePage:X2}:0x{usage:X2} "); shown++; }

                        if (usagePage == 0x0D && usage == 0x48) { ctx.HasWidth = true; ctx.WLogMax = logicalMax; ctx.WPhysMax = physicalMax; ctx.WExp = unitsExp; ctx.WUnits = units; }
                        if (usagePage == 0x0D && usage == 0x49) { ctx.HasHeight = true; ctx.HLogMax = logicalMax; ctx.HPhysMax = physicalMax; ctx.HExp = unitsExp; ctx.HUnits = units; }
                        if (usagePage == 0x01 && usage == 0x30) ctx.XLogMax = logicalMax;
                        if (usagePage == 0x01 && usage == 0x31) ctx.YLogMax = logicalMax;
                    }
                }
            }

            // 重复扫描（启动 + 按钮）时释放旧缓冲，避免 preparsed 累积
            if (Ctx.TryGetValue(hDevice, out DeviceCtx? old) && old.Preparsed != IntPtr.Zero && old.Preparsed != preparsed)
                Marshal.FreeHGlobal(old.Preparsed);
            Ctx[hDevice] = ctx; // 保留当前 preparsed 供解码使用（最后一个随进程结束回收）

            results.Add(new HidTouchInfo(name, ctx.IsTouch, ctx.HasWidth, ctx.HasHeight, caps.NumberInputValueCaps,
                usages.ToString().Trim(),
                ctx.HasWidth ? $"LogicalMax={ctx.WLogMax} PhysicalMax={ctx.WPhysMax} Units=0x{ctx.WUnits:X} UnitsExp={ctx.WExp}" : "",
                ctx.HasHeight ? $"LogicalMax={ctx.HLogMax} PhysicalMax={ctx.HPhysMax} Units=0x{ctx.HUnits:X} UnitsExp={ctx.HExp}" : ""));
        }

        return results;
    }

    /// <summary>注册触摸数字化器（0x0D/0x04）的原始输入，窗口不在前台也能收到（INPUTSINK）。</summary>
    public static bool Register(IntPtr hwnd)
    {
        var devices = new[]
        {
            new RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 0x04, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd },
        };
        return RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    /// <summary>WM_INPUT 解析结果。</summary>
    public enum WmInputResult
    {
        /// <summary>已处理（sample 可能仍为 null，例如报文里没有可解的量）。</summary>
        Handled,
        /// <summary>不是触摸 HID 设备，忽略。</summary>
        NotForUs,
        /// <summary>是 HID 设备，但句柄不在能力表里（启动时设备没枚举到 / 中途重枚举）→ 调用方应 Scan() 后重试。</summary>
        UnknownDevice,
    }

    private static long _lastAutoRescanTicks;

    /// <summary>自动重扫节流：距上次不足 800ms 返回 false，避免 WM_INPUT 高频时反复枚举设备。</summary>
    public static bool TryBeginAutoRescan()
    {
        long now = Environment.TickCount64;
        if (now - _lastAutoRescanTicks < 800)
            return false;
        _lastAutoRescanTicks = now;
        return true;
    }

    /// <summary>处理 WM_INPUT，解码第一触点的接触尺寸/坐标。</summary>
    public static WmInputResult TryHandleWmInput(IntPtr lParam, out RawTouchSample? sample)
    {
        sample = null;

        uint size = 0;
        uint header = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, header);
        if (size == 0)
            return WmInputResult.NotForUs;

        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, RID_INPUT, buf, ref size, header) != size)
                return WmInputResult.NotForUs;
            if (Marshal.ReadInt32(buf, 0) != RIM_TYPEHID)
                return WmInputResult.NotForUs;

            IntPtr hDevice = Marshal.ReadIntPtr(buf, 8);
            if (!Ctx.TryGetValue(hDevice, out DeviceCtx? ctx) || ctx is null)
                return WmInputResult.UnknownDevice;   // 句柄不认识 → 让调用方重扫后重试
            if (!ctx.IsTouch)
                return WmInputResult.NotForUs;

            uint sizeHid = (uint)Marshal.ReadInt32(buf, (int)header);
            uint reportCount = (uint)Marshal.ReadInt32(buf, (int)header + 4);
            if (sizeHid == 0 || reportCount == 0)
                return WmInputResult.Handled;

            IntPtr dataPtr = buf + (int)header + 8;
            var report = new byte[sizeHid];
            Marshal.Copy(dataPtr, report, 0, (int)sizeHid);

            // 注意：下面统一用 linkCollection = 0。多触点报文里每个触点是一个 link collection，
            // 因此读到的 W/H 与 X/Y 未必属于同一根手指（单指/单手掌场景是正确的）。
            // 要严格区分需配合 HidP_GetLinkCollectionNodes 遍历；当前按"第一个触点"近似。
            uint w = 0, h = 0, x = 0, y = 0;
            bool hasW = ctx.HasWidth && HidP_GetUsageValue(HidP_Input, 0x0D, 0, 0x48, out w, ctx.Preparsed, report, sizeHid) == HIDP_STATUS_SUCCESS;
            bool hasH = ctx.HasHeight && HidP_GetUsageValue(HidP_Input, 0x0D, 0, 0x49, out h, ctx.Preparsed, report, sizeHid) == HIDP_STATUS_SUCCESS;
            HidP_GetUsageValue(HidP_Input, 0x01, 0, 0x30, out x, ctx.Preparsed, report, sizeHid);
            HidP_GetUsageValue(HidP_Input, 0x01, 0, 0x31, out y, ctx.Preparsed, report, sizeHid);

            double? wMm = hasW ? ToMm(w, ctx.WLogMax, ctx.WPhysMax, ctx.WExp, ctx.WUnits) : null;
            double? hMm = hasH ? ToMm(h, ctx.HLogMax, ctx.HPhysMax, ctx.HExp, ctx.HUnits) : null;
            double? xNorm = ctx.XLogMax > 0 ? (double)x / ctx.XLogMax : null;
            double? yNorm = ctx.YLogMax > 0 ? (double)y / ctx.YLogMax : null;

            sample = new RawTouchSample(wMm, hMm, xNorm, yNorm, (int)w, (int)h, ToHex(report));
            return WmInputResult.Handled;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>
    /// 当设备的 Width/Height 没有声明物理量程（PhysicalMax=0 / Units=0）时，
    /// 用它把"逻辑计数"换算成毫米：mm = 计数 × 本比例。由界面标定后设置。
    /// </summary>
    public static double CountsToMmScale { get; set; }

    /// <summary>逻辑值 → 物理毫米。无物理量程时回退到已标定的 CountsToMmScale。</summary>
    private static double? ToMm(uint logical, int logicalMax, int physicalMax, int unitsExp, int units)
    {
        if (logicalMax <= 0)
            return null;

        if (physicalMax > 0)
        {
            double physical = (double)logical / logicalMax * physicalMax; // 单位 × 10^exp
            int exp = unitsExp & 0xF;
            if (exp >= 8) exp -= 16;                                   // 4 位补码
            double inUnit = physical * Math.Pow(10, exp);
            bool englishSystem = (units & 0xF000) == 0x3000;           // 3 = English Linear
            return englishSystem ? inUnit * 25.4 : inUnit * 10.0;      // 英寸 / 厘米
        }

        // 设备没给物理量程：只能用标定比例
        return CountsToMmScale > 0 ? logical * CountsToMmScale : null;
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

    private static IntPtr GetPreparsedData(IntPtr device, out bool ok)
    {
        ok = false;
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
        ok = true;
        return buf;
    }

    private static string ToHex(byte[] d)
    {
        var sb = new StringBuilder(d.Length * 3);
        foreach (byte b in d)
            sb.Append(b.ToString("X2")).Append(' ');
        return sb.ToString().TrimEnd();
    }
}
