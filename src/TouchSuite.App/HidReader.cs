using System.Runtime.InteropServices;
using System.Text;

namespace TouchSuite.App;

/// <summary>
/// 原始 HID 触摸读取（从主程序 Helpers/RawHidScan.cs 搬来）。
/// 职责：RawInput 枚举设备 + 能力探测 + WM_INPUT 报文解码，
/// 只取三样：接触尺寸（Digitizer Width 0x48 / Height 0x49）、坐标（0x01:0x30/0x31）、压感（TipPressure 0x0D:0x30）。
/// 面积 = W×H（mm²）；换算优先用驱动声明的物理量程，缺物理量程时回退到标定比例 <see cref="CountsToMmScale"/>。
/// 不打开设备、不依赖 WM_POINTER。
/// </summary>
public static class HidReader
{
    private const uint RIDI_PREPARSEDDATA = 0x20000005;
    private const uint RIDI_DEVICENAME = 0x20000007;
    private const uint RIM_TYPEHID = 2;
    private const int HidP_Input = 0;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;
    private const int HidP_ValueCapsSize = 72;

    private const uint RID_INPUT = 0x10000003;
    private const uint RIDEV_INPUTSINK = 0x00000100;

    public sealed record HidDeviceInfo(
        string DeviceName, bool IsTouchScreen,
        bool HasWidth, bool HasHeight, bool HasPressure,
        int InputValueCaps, string UsageSummary,
        string WidthDetail, string HeightDetail, string PressureDetail, string XYDetail);

    public sealed record RawTouchSample(
        string DeviceName,
        double? WidthMm, double? HeightMm, double? XNorm, double? YNorm, double? Pressure01,
        int WidthLogical, int HeightLogical,
        int XLogical, int YLogical, int XLogMax, int YLogMax,
        string Hex);

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICELIST { public IntPtr hDevice; public uint dwType; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER { public uint dwType; public uint dwSize; public IntPtr hDevice; public IntPtr wParam; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE { public ushort usUsagePage; public ushort usUsage; public uint dwFlags; public IntPtr hwndTarget; }

    /// <summary>
    /// HIDP_CAPS：中间是 USHORT Reserved[17]。这里刻意展平成 17 个独立字段，
    /// 避开 ByValArray 在 out 场景下走 MngdNativeArrayMarshaler（.NET 10 上会触发 CLR 崩溃）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
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

    private sealed class DeviceCtx
    {
        public string Name = "";
        public IntPtr Preparsed;
        public bool IsTouch;
        public bool HasWidth, HasHeight, HasPressure;
        public int WLogMax, WPhysMax, WExp, WUnits;
        public int HLogMax, HPhysMax, HExp, HUnits;
        public int PLogMin, PLogMax;
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

    // ================= 枚举 + 能力探测 =================

    public static List<HidDeviceInfo> Scan()
    {
        var results = new List<HidDeviceInfo>();

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
                Name = name,
                Preparsed = preparsed,
                IsTouch = caps.UsagePage == 0x0D && caps.Usage is 0x04 or 0x20 or 0x22 or 0x05,
            };

            var usages = new StringBuilder();
            int shown = 0;

            if (caps.NumberInputValueCaps > 0)
            {
                var buffer = new byte[HidP_ValueCapsSize * caps.NumberInputValueCaps];
                ushort len = caps.NumberInputValueCaps;
                if (HidP_GetValueCaps(HidP_Input, buffer, ref len, preparsed) == HIDP_STATUS_SUCCESS)
                {
                    for (int k = 0; k < len; k++)
                    {
                        int off = k * HidP_ValueCapsSize;
                        ushort usagePage = BitConverter.ToUInt16(buffer, off + 0);
                        ushort usage = BitConverter.ToUInt16(buffer, off + 56);
                        int logicalMin = BitConverter.ToInt32(buffer, off + 40);
                        int logicalMax = BitConverter.ToInt32(buffer, off + 44);
                        int physicalMax = BitConverter.ToInt32(buffer, off + 52);
                        int units = BitConverter.ToInt32(buffer, off + 36);
                        int unitsExp = BitConverter.ToInt32(buffer, off + 32);

                        if (shown < 24) { usages.Append($"0x{usagePage:X2}:0x{usage:X2} "); shown++; }

                        if (usagePage == 0x0D && usage == 0x48) { ctx.HasWidth = true; ctx.WLogMax = logicalMax; ctx.WPhysMax = physicalMax; ctx.WExp = unitsExp; ctx.WUnits = units; }
                        if (usagePage == 0x0D && usage == 0x49) { ctx.HasHeight = true; ctx.HLogMax = logicalMax; ctx.HPhysMax = physicalMax; ctx.HExp = unitsExp; ctx.HUnits = units; }
                        if (usagePage == 0x0D && usage == 0x30) { ctx.HasPressure = true; ctx.PLogMin = logicalMin; ctx.PLogMax = logicalMax; }
                        if (usagePage == 0x01 && usage == 0x30) ctx.XLogMax = logicalMax;
                        if (usagePage == 0x01 && usage == 0x31) ctx.YLogMax = logicalMax;
                    }
                }
            }

            if (Ctx.TryGetValue(hDevice, out DeviceCtx? old) && old.Preparsed != IntPtr.Zero && old.Preparsed != preparsed)
                Marshal.FreeHGlobal(old.Preparsed);
            Ctx[hDevice] = ctx;

            results.Add(new HidDeviceInfo(name, ctx.IsTouch,
                ctx.HasWidth, ctx.HasHeight, ctx.HasPressure, caps.NumberInputValueCaps, usages.ToString().Trim(),
                ctx.HasWidth ? ScaleText(ctx.WLogMax, ctx.WPhysMax, ctx.WExp, ctx.WUnits) : "",
                ctx.HasHeight ? ScaleText(ctx.HLogMax, ctx.HPhysMax, ctx.HExp, ctx.HUnits) : "",
                ctx.HasPressure ? $"{ctx.PLogMin}..{ctx.PLogMax}" : "",
                ctx.XLogMax > 0 || ctx.YLogMax > 0 ? $"X 0..{ctx.XLogMax}，Y 0..{ctx.YLogMax}" : "X/Y 未声明"));
        }

        return results;
    }

    /// <summary>注册触摸数字化器的原始输入，窗口不在前台也能收到（RIDEV_INPUTSINK）。</summary>
    public static bool Register(IntPtr hwnd)
    {
        var devices = new[]
        {
            new RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 0x04, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd },
            new RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 0x20, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd },
            new RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 0x22, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd },
            new RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 0x05, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd },
        };
        return RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    public enum WmInputResult
    {
        /// <summary>已处理（sample 可能为 null）。</summary>
        Handled,
        /// <summary>不是触摸 HID 设备，忽略。</summary>
        NotForUs,
        /// <summary>句柄不在能力表里 → 调用方应 Scan() 后重试。</summary>
        UnknownDevice,
    }

    private static long _lastAutoRescanTicks;

    /// <summary>自动重扫节流：距上次不足 800ms 返回 false。</summary>
    public static bool TryBeginAutoRescan()
    {
        long now = Environment.TickCount64;
        if (now - _lastAutoRescanTicks < 800)
            return false;
        _lastAutoRescanTicks = now;
        return true;
    }

    /// <summary>处理 WM_INPUT，解码第一个触点的接触尺寸 / 坐标 / 压感。</summary>
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
                return WmInputResult.UnknownDevice;
            if (!ctx.IsTouch)
                return WmInputResult.NotForUs;

            uint sizeHid = (uint)Marshal.ReadInt32(buf, (int)header);
            uint reportCount = (uint)Marshal.ReadInt32(buf, (int)header + 4);
            if (sizeHid == 0 || reportCount == 0)
                return WmInputResult.Handled;

            IntPtr dataPtr = buf + (int)header + 8;
            var report = new byte[sizeHid];
            Marshal.Copy(dataPtr, report, 0, (int)sizeHid);

            // 统一用 linkCollection = 0（第一个触点）。多指时 W/H 与 X/Y 未必同一根手指，
            // 单指/手掌场景正确；严格区分需配合 HidP_GetLinkCollectionNodes。
            uint w = 0, h = 0, p = 0, x = 0, y = 0;
            bool hasW = ctx.HasWidth && HidP_GetUsageValue(HidP_Input, 0x0D, 0, 0x48, out w, ctx.Preparsed, report, sizeHid) == HIDP_STATUS_SUCCESS;
            bool hasH = ctx.HasHeight && HidP_GetUsageValue(HidP_Input, 0x0D, 0, 0x49, out h, ctx.Preparsed, report, sizeHid) == HIDP_STATUS_SUCCESS;
            bool hasP = ctx.HasPressure && HidP_GetUsageValue(HidP_Input, 0x0D, 0, 0x30, out p, ctx.Preparsed, report, sizeHid) == HIDP_STATUS_SUCCESS;
            HidP_GetUsageValue(HidP_Input, 0x01, 0, 0x30, out x, ctx.Preparsed, report, sizeHid);
            HidP_GetUsageValue(HidP_Input, 0x01, 0, 0x31, out y, ctx.Preparsed, report, sizeHid);

            double? wMm = hasW ? ToMm(w, ctx.WLogMax, ctx.WPhysMax, ctx.WExp, ctx.WUnits) : null;
            double? hMm = hasH ? ToMm(h, ctx.HLogMax, ctx.HPhysMax, ctx.HExp, ctx.HUnits) : null;
            double? p01 = hasP && ctx.PLogMax > ctx.PLogMin
                ? Math.Clamp((double)(p - (uint)ctx.PLogMin) / (ctx.PLogMax - ctx.PLogMin), 0, 1)
                : null;
            double? xNorm = ctx.XLogMax > 0 ? (double)x / ctx.XLogMax : null;
            double? yNorm = ctx.YLogMax > 0 ? (double)y / ctx.YLogMax : null;

            sample = new RawTouchSample(ctx.Name, wMm, hMm, xNorm, yNorm, p01, (int)w, (int)h,
                (int)x, (int)y, ctx.XLogMax, ctx.YLogMax, ToHex(report));
            return WmInputResult.Handled;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>设备没声明物理量程时，用标定比例把"逻辑计数"换算成毫米：mm = 计数 × 本比例。</summary>
    public static double CountsToMmScale { get; set; }

    private static double? ToMm(uint logical, int logicalMax, int physicalMax, int unitsExp, int units)
    {
        if (logicalMax <= 0)
            return null;

        if (physicalMax > 0)
        {
            double physical = (double)logical / logicalMax * physicalMax;   // 单位 × 10^exp
            int exp = unitsExp & 0xF;
            if (exp >= 8) exp -= 16;                                        // 4 位补码
            double inUnit = physical * Math.Pow(10, exp);
            bool englishSystem = (units & 0xF000) == 0x3000;                 // 3 = English Linear
            return englishSystem ? inUnit * 25.4 : inUnit * 10.0;            // 英寸 / 厘米
        }

        return CountsToMmScale > 0 ? logical * CountsToMmScale : null;
    }

    /// <summary>换算表文字：逻辑量程 → 物理量程 → 每单位多少 mm（供 UI 展示）。</summary>
    private static string ScaleText(int logicalMax, int physicalMax, int exp, int units)
    {
        if (physicalMax <= 0)
            return $"LogicalMax={logicalMax} 无物理量程（用标定比例换算）";
        bool english = (units & 0xF000) == 0x3000;
        int e = exp & 0xF; if (e >= 8) e -= 16;
        double unitMm = Math.Pow(10, e) * (english ? 25.4 : 10.0);
        double perCount = unitMm * physicalMax / logicalMax;
        return $"LogicalMax={logicalMax} PhysicalMax={physicalMax}（{(english ? "英寸" : "厘米")}×10^{e}）"
             + $" → {Precision.Fmt(perCount, 6)} mm/计数";
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
