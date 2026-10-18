using System.Runtime.InteropServices;
using System.Text;

namespace TouchSuite.App;

/// <summary>
/// 原始 HID 触摸读取（与 TouchSuite.HidDump 完全同一套读法）。
/// 职责：RawInput 枚举设备 + 能力探测 + WM_INPUT 报文解码，
/// 只取五样：接触尺寸（Digitizer Width 0x48 / Height 0x49）、坐标（0x01:0x30/0x31）、压感（TipPressure 0x0D:0x30）。
/// <para>
/// 关键点（与 HidDump 对齐）：
/// ① 一份 WM_INPUT 可带多份子报文（dwCount），逐份解；
/// ② 每个 usage 必须按**自己的 LinkCollection（触点槽位）**取，绝不能全传 0
///    —— 多触点设备的 0x48/0x49 声明在 L1..L6，传 0 会取不到；
/// ③ 单位制取 Units 低 4 位（1=cm、3=in），不是 0xF000。
/// </para>
/// 面积 = W×H；物理量程换算优先用驱动声明的物理量程，缺物理量程时回退到标定比例 <see cref="CountsToMmScale"/>。
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
        string WidthDetail, string HeightDetail, string PressureDetail, string XYDetail,
        string GroupKey, string GroupLabel);

    public sealed record RawTouchSample(
        string DeviceName,
        double? WidthMm, double? HeightMm, double? XNorm, double? YNorm, double? Pressure01,
        int WidthLogical, int HeightLogical,
        int XLogical, int YLogical, int XLogMax, int YLogMax,
        int WidthLogMax, int HeightLogMax,
        double? XPhysMm, double? YPhysMm,
        int LinkCollection, string LinksDetail,
        string Hex, int ContactId, IReadOnlyList<RawLink>? Contacts = null);

    /// <summary>一帧里的单根接触（原始 HID），用于「多触点求和 / 多点判手掌」。</summary>
    public sealed record RawLink(
        int Link, int ContactId,
        int WLogical, int HLogical, double? Wmm, double? Hmm,
        double? XNorm, double? YNorm, double? Pressure01);

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

    /// <summary>一条值帽声明的关键字段（HIDP_VALUE_CAPS 布局：Link@6、UnitsExp@32、Units@36、Logical@40/44、Physical@48/52）。</summary>
    private sealed record Cap(ushort Link, int LogMin, int LogMax, int PhysMin, int PhysMax, int Exp, int Units);

    private sealed class DeviceCtx
    {
        public string Name = "";
        public IntPtr Preparsed;
        public bool IsTouch;
        public readonly List<Cap> Widths = new();      // 0x0D:0x48（每个触点槽位一条）
        public readonly List<Cap> Heights = new();     // 0x0D:0x49
        public readonly List<Cap> Pressures = new();   // 0x0D:0x30
        public readonly List<Cap> Xs = new();          // 0x01:0x30
        public readonly List<Cap> Ys = new();          // 0x01:0x31
        public bool HasWidth => Widths.Count > 0;
        public bool HasHeight => Heights.Count > 0;
        public bool HasPressure => Pressures.Count > 0;

        /// <summary>设备声明的全部触点槽位（LinkCollection，升序）。</summary>
        public ushort[] Links = Array.Empty<ushort>();

        /// <summary>声明了「笔尖接触 0x0D:0x42」的槽位：只有这些槽位才需要用 TipSwitch 判"真的按下"。</summary>
        public readonly HashSet<ushort> TipSwitchLinks = new();

        /// <summary>是否曾经见过某个槽位 TipSwitch=1。一旦见过，就严格按 TipSwitch 判活，
        /// 不再对"整帧都没按下"的帧做兜底（否则抬手帧会被驱动残留的 W/H 重新当成接触）。</summary>
        public bool TipEverSeen;

        /// <summary>上一帧「真的按下」的各槽位取值。抬手过渡帧用它补出最后一组坐标 / 尺寸。</summary>
        public readonly Dictionary<ushort, LinkValues> LastValues = new();

        /// <summary>上一帧是否有「真的按下」的槽位：用来识别抬手过渡帧（只补一帧）。</summary>
        public bool HadPress;

        /// <summary>接触数量 0x0D:0x54 所在的 LinkCollection（未声明则 null）。抬手信号之一：它掉到 0。</summary>
        public ushort? ContactCountLink;

        /// <summary>数字化器自己声明的屏幕物理宽高（X/Y 坐标量程的物理满量程，mm）：屏幕长度的 HID 依据。</summary>
        public double? XPhysMm, YPhysMm;

        /// <summary>（供 UI）设备声明的物理宽高 mm 文字。</summary>
        public string ScreenPhysText = "";

        /// <summary>逻辑触摸屏键：同一块物理屏的多个 HID 顶层集合共用同一个键（ContainerId 归一，回退 VID&PID）。</summary>
        public string GroupKey = "";
    }

    // ================= 逻辑屏分组（同一物理屏的多个 HID 集合归一） =================

    private static readonly Dictionary<string, string> _deviceToGroup = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<string>> _groupMembers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> _vidPidToContainer = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>某个 HID 集合所属的逻辑屏键（未登记则回退成设备名本身）。</summary>
    public static string GroupOfDevice(string deviceName)
        => deviceName.Length > 0 && _deviceToGroup.TryGetValue(deviceName, out string? g) ? g : deviceName;

    /// <summary>某个逻辑屏包含的所有 HID 集合名。</summary>
    public static IReadOnlyList<string> GroupMembers(string groupKey)
        => _groupMembers.TryGetValue(groupKey, out List<string>? l) ? l : Array.Empty<string>();

    private const int CR_SUCCESS = 0;
    private const uint CM_LOCATE_DEVNODE_NORMAL = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    private static readonly DEVPROPKEY DEVPKEY_Device_ContainerId =
        new() { fmtid = new Guid("8c7ed206-3f8a-4827-b6cc-029f3b2e2b5f"), pid = 2 };

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int CM_Get_DevNode_PropertyW(uint dnDevInst, ref DEVPROPKEY propertyKey,
        out uint propertyType, byte[] propertyBuffer, ref uint propertyBufferSize, uint ulFlags);

    /// <summary>取设备的 ContainerId（同一物理设备的所有接口共享同一 ContainerId）。取不到返回空串。</summary>
    private static string TryContainerId(string deviceName)
    {
        try
        {
            string id = deviceName;
            if (id.StartsWith(@"\\?\")) id = id[4..];
            int hash = id.IndexOf("#{", StringComparison.Ordinal);
            if (hash >= 0) id = id[..hash];
            id = id.Replace('#', '\\');

            if (CM_Locate_DevNodeW(out uint devInst, id, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS)
                return "";

            DEVPROPKEY key = DEVPKEY_Device_ContainerId;
            byte[] buf = new byte[16];
            uint size = 16;
            if (CM_Get_DevNode_PropertyW(devInst, ref key, out uint _, buf, ref size, 0) != CR_SUCCESS || size < 16)
                return "";
            return new Guid(buf[..16]).ToString();
        }
        catch
        {
            return "";
        }
    }

    /// <summary>从设备路径里抠出 VID_xxxx&amp;PID_yyyy（去掉 &amp;MI_/&amp;Col 等后缀）。</summary>
    private static string ExtractVidPid(string deviceName)
    {
        int i = deviceName.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        if (i < 0)
            return "";
        int j = deviceName.IndexOf('#', i);
        if (j < 0) j = deviceName.Length;
        string seg = deviceName[i..j];
        int m = seg.IndexOf("&MI_", StringComparison.OrdinalIgnoreCase);
        if (m >= 0) seg = seg[..m];
        return seg;
    }

    /// <summary>计算逻辑屏键：优先按 ContainerId 归并；同一 VID&amp;PID 但对不上同一物理设备时加序号区分；最后回退 VID&amp;PID。</summary>
    private static string ResolveGroupKey(string deviceName)
    {
        string vidPid = ExtractVidPid(deviceName);
        if (vidPid.Length == 0)
            return deviceName;

        string cid = TryContainerId(deviceName);
        if (cid.Length == 0)
            return vidPid;

        if (_vidPidToContainer.TryGetValue(vidPid, out string? seen))
        {
            if (string.Equals(seen, cid, StringComparison.OrdinalIgnoreCase))
                return vidPid;
            int n = 2;
            string cand;
            do { cand = vidPid + "#" + n++; } while (_vidPidToContainer.ContainsKey(cand));
            _vidPidToContainer[cand] = cid;
            return cand;
        }

        _vidPidToContainer[vidPid] = cid;
        return vidPid;
    }

    // ================= 跨集合压感合并（希沃等：尺寸一个集合、压感另一个集合） =================

    private sealed class GroupPressureState
    {
        public readonly Dictionary<int, (double P, long Ticks)> ByContact = new();
        public double? Latest;
        public long LatestTicks;
    }

    private static readonly Dictionary<string, GroupPressureState> _groupPressure = new(StringComparer.OrdinalIgnoreCase);
    private const int PressureWindowMs = 250;

    private static readonly Dictionary<IntPtr, DeviceCtx> Ctx = new();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList([In, Out] RAWINPUTDEVICELIST[]? list, ref uint count, uint size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, IntPtr data, ref uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint numDevices, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    /// <summary>
    /// user32 合成指针注入出来的触摸屏（形如 <c>\\?\Microsoft HID RID\000D_0004\3</c>）：
    /// 它的 HID 报文里 X/Y 填的是**屏幕物理像素**（实测 0..1679 / 0..1049），
    /// 可描述符却声明逻辑量程 0..32000 —— 按描述符归一会把坐标压到左上角外（xn≈0.02）。
    /// 所以这里对该设备单独适配：X/Y 的有效量程改用虚拟屏幕的像素跨度。
    /// W/H（0x0D:0x48/0x49）不动：Windows 已按物理量程换算过，本来就是正确的。
    /// </summary>
    private static bool IsSyntheticPointerTouch(bool isTouch, string deviceName) =>
        isTouch && deviceName.Contains("Microsoft HID RID", StringComparison.OrdinalIgnoreCase);

    [DllImport("hid.dll")]
    private static extern bool HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS caps);

    [DllImport("hid.dll")]
    private static extern int HidP_GetValueCaps(int reportType, byte[] valueCaps, ref ushort valueCapsLength, IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetButtonCaps(int reportType, byte[] buttonCaps, ref ushort buttonCapsLength, IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection, ushort usage,
        out uint usageValue, IntPtr preparsedData, byte[] report, uint reportLength);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection,
        [Out] ushort[] usageList, ref uint usageLength, IntPtr preparsedData, byte[] report, uint reportLength);

    private const ushort UsageTipSwitch = 0x42;   // 0x0D:0x42 笔尖接触（该槽位真的按下）

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

            // user32 合成指针触摸屏：X/Y 实为屏幕像素 → 有效量程取虚拟屏幕像素跨度（见 IsSyntheticPointerTouch）。
            bool syntheticPointer = IsSyntheticPointerTouch(ctx.IsTouch, name);
            int pixelSpanX = syntheticPointer ? GetSystemMetrics(SM_CXVIRTUALSCREEN) : 0;
            int pixelSpanY = syntheticPointer ? GetSystemMetrics(SM_CYVIRTUALSCREEN) : 0;

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
                        ushort link = BitConverter.ToUInt16(buffer, off + 6);       // LinkCollection＝触点槽位
                        ushort usage = BitConverter.ToUInt16(buffer, off + 56);
                        int logicalMin = BitConverter.ToInt32(buffer, off + 40);
                        int logicalMax = BitConverter.ToInt32(buffer, off + 44);
                        int physicalMin = BitConverter.ToInt32(buffer, off + 48);
                        int physicalMax = BitConverter.ToInt32(buffer, off + 52);
                        int units = BitConverter.ToInt32(buffer, off + 36);
                        int unitsExp = BitConverter.ToInt32(buffer, off + 32);

                        if (shown < 24) { usages.Append($"0x{usagePage:X2}:0x{usage:X2} "); shown++; }

                        var cap = new Cap(link, logicalMin, logicalMax, physicalMin, physicalMax, unitsExp, units);
                        // 合成指针设备：X/Y 量程换成屏幕像素跨度（描述符写的 32000 与实际报文不符）
                        if (pixelSpanX > 0 && usagePage == 0x01 && usage == 0x30)
                            cap = cap with { LogMax = pixelSpanX };
                        if (pixelSpanY > 0 && usagePage == 0x01 && usage == 0x31)
                            cap = cap with { LogMax = pixelSpanY };
                        if (usagePage == 0x0D && usage == 0x54) ctx.ContactCountLink = link;   // 接触数量：抬手信号
                        if (usagePage == 0x0D && usage == 0x48) ctx.Widths.Add(cap);
                        if (usagePage == 0x0D && usage == 0x49) ctx.Heights.Add(cap);
                        if (usagePage == 0x0D && usage == 0x30) ctx.Pressures.Add(cap);
                        if (usagePage == 0x01 && usage == 0x30) ctx.Xs.Add(cap);
                        if (usagePage == 0x01 && usage == 0x31) ctx.Ys.Add(cap);
                    }
                }
            }

            // 全部触点槽位（升序）：报文解码时逐个槽位取，而不是一律传 0
            ctx.Links = ctx.Widths.Concat(ctx.Heights).Concat(ctx.Xs).Concat(ctx.Ys).Concat(ctx.Pressures)
                .Select(c => c.Link).Distinct().OrderBy(x => x).ToArray();

            // 读按钮帽：找出声明了「笔尖接触 0x0D:0x42」的槽位（只有这些槽位需要判 TipSwitch）
            if (caps.NumberInputButtonCaps > 0)
            {
                var bbuf = new byte[HidP_ValueCapsSize * caps.NumberInputButtonCaps];
                ushort blen = caps.NumberInputButtonCaps;
                if (HidP_GetButtonCaps(HidP_Input, bbuf, ref blen, preparsed) == HIDP_STATUS_SUCCESS)
                {
                    for (int k = 0; k < blen; k++)
                    {
                        int off = k * HidP_ValueCapsSize;
                        ushort page = BitConverter.ToUInt16(bbuf, off + 0);
                        ushort link = BitConverter.ToUInt16(bbuf, off + 6);
                        bool isRange = bbuf[off + 12] != 0;
                        ushort uMin = BitConverter.ToUInt16(bbuf, off + 56);
                        ushort uMax = BitConverter.ToUInt16(bbuf, off + 58);
                        bool hasTip = page == 0x0D
                            && (isRange ? uMin <= UsageTipSwitch && UsageTipSwitch <= uMax : uMin == UsageTipSwitch);
                        if (hasTip)
                            ctx.TipSwitchLinks.Add(link);
                    }
                }
            }

            // 数字化器声明的屏幕物理宽高（X/Y 的物理满量程）→ mm
            ctx.XPhysMm = PhysFullMm(ctx.Xs.FirstOrDefault());
            ctx.YPhysMm = PhysFullMm(ctx.Ys.FirstOrDefault());
            ctx.ScreenPhysText = ctx.XPhysMm is double xm && ctx.YPhysMm is double ym
                ? $"{Precision.Fmt(xm, 1)} × {Precision.Fmt(ym, 1)} mm（HID 声明）"
                : "未声明";

            if (Ctx.TryGetValue(hDevice, out DeviceCtx? old) && old.Preparsed != IntPtr.Zero && old.Preparsed != preparsed)
                Marshal.FreeHGlobal(old.Preparsed);

            // 逻辑屏分组：同一物理屏的多个 HID 集合（如希沃的 MI_00&Col04 + MI_02&Col02）归到一起
            ctx.GroupKey = ctx.IsTouch ? ResolveGroupKey(name) : name;
            if (ctx.IsTouch)
            {
                _deviceToGroup[name] = ctx.GroupKey;
                if (!_groupMembers.TryGetValue(ctx.GroupKey, out List<string>? members))
                {
                    members = new List<string>();
                    _groupMembers[ctx.GroupKey] = members;
                }
                if (!members.Contains(name, StringComparer.OrdinalIgnoreCase))
                    members.Add(name);
            }

            Ctx[hDevice] = ctx;

            Cap? w0 = ctx.Widths.FirstOrDefault();
            Cap? h0 = ctx.Heights.FirstOrDefault();
            results.Add(new HidDeviceInfo(name, ctx.IsTouch,
                ctx.HasWidth, ctx.HasHeight, ctx.HasPressure, caps.NumberInputValueCaps, usages.ToString().Trim(),
                w0 is null ? "" : ScaleText(w0),
                h0 is null ? "" : ScaleText(h0),
                ctx.Pressures.Count == 0 ? "" : string.Join("，", ctx.Pressures.Select(c => $"L{c.Link} {c.LogMin}..{c.LogMax}")),
                ctx.Xs.Count == 0 && ctx.Ys.Count == 0
                    ? "X/Y 未声明"
                    : $"槽位 {ctx.Links.Length} 个（Link {string.Join(",", ctx.Links)}）"
                      + $" X 0..{ctx.Xs.FirstOrDefault()?.LogMax ?? 0}{(syntheticPointer ? "" : PhysSuffix(ctx.Xs.FirstOrDefault()))}"
                      + $"，Y 0..{ctx.Ys.FirstOrDefault()?.LogMax ?? 0}{(syntheticPointer ? "" : PhysSuffix(ctx.Ys.FirstOrDefault()))}"
                      + (syntheticPointer ? "（合成指针适配：X/Y 报文实为屏幕像素，量程按屏幕跨度）" : "")
                      + $"　屏幕物理 {ctx.ScreenPhysText}"
                      + $"　TipSwitch 槽位 {ctx.TipSwitchLinks.Count}/{ctx.Links.Length}",
                ctx.GroupKey, ctx.GroupKey));
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

    /// <summary>一个触点槽位本帧读到的值。</summary>
    private readonly record struct LinkValues(
        ushort Link,
        int WLogical, int HLogical, int WLogMax, int HLogMax, double? Wmm, double? Hmm,
        int XLogical, int YLogical, int XLogMax, int YLogMax, double? Pressure01, int ContactId);

    /// <summary>处理 WM_INPUT：逐份子报文 × 逐触点槽位解码，取「接触尺寸乘积最大」的槽位（手掌就是最大的那个接触）。</summary>
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
            var reports = new List<byte[]>((int)reportCount);
            for (int i = 0; i < reportCount; i++)
            {
                var report = new byte[sizeHid];
                Marshal.Copy(dataPtr + (int)(i * sizeHid), report, 0, (int)sizeHid);
                reports.Add(report);
            }

            // 本集合带压感 → 先把本帧压感按 ContactID 记进「同一逻辑屏」的缓存，
            // 供不带压感的兄弟集合（如希沃 Col04 只有尺寸）补齐压感。
            if (ctx.Pressures.Count > 0)
                CacheFramePressure(ctx, reports);

            // 第一遍：按 TipSwitch 过滤（只认真的按下的槽位，排除残留幽灵槽）；同时记住本帧各槽位取值
            bool requireTip = ctx.TipSwitchLinks.Count > 0;
            RawTouchSample? pressed = PickBest(ctx, reports, requireTip, remember: true);

            // 兜底：**只在这台设备从来没置过 TipSwitch** 时忽略它再挑一次（个别驱动的怪癖）。
            if (pressed is null && requireTip && !ctx.TipEverSeen)
                pressed = PickBest(ctx, reports, requireTip: false);

            // 抬手判定（两个信号取其一，覆盖两类设备）：
            //   ① 本帧读不到任何接触；
            //   ② 接触数量 0x0D:0x54 掉到 0（有"残留幽灵槽"的设备抬手帧仍能读到 W/H，只能靠这个）。
            bool noContact = pressed is null;
            bool countZero = ContactCountIsZero(ctx, reports);
            bool lift = ctx.HadPress && (noContact || countZero);

            sample = pressed ?? (lift ? BuildLiftSample(ctx, reports) : null);

            // 置位规则：本帧有接触且"接触数"未归零 → 有活跃按压；抬手/收尾帧清零。
            // 清零后不会重复补位（下一帧即使 countZero 仍为真，HadPress 已是 false）。
            ctx.HadPress = pressed is not null && !countZero;

            if (lift && sample is not null)
                Log.Info($"[抬手补位] 触发={(noContact ? "无接触帧" : "接触数=0")} L={sample.LinkCollection} "
                       + $"X={sample.XLogical}/{sample.XLogMax} Y={sample.YLogical}/{sample.YLogMax} "
                       + $"W={sample.WidthLogical}/{sample.WidthLogMax} H={sample.HeightLogical}/{sample.HeightLogMax}");

            // 本集合无压感、但同一逻辑屏的别的集合有 → 用最近的压感补齐（同 ContactID 优先，退化为最新值）
            if (sample is not null && sample.Pressure01 is null && ctx.Pressures.Count == 0 && ctx.GroupKey.Length > 0)
            {
                double? gp = LookupGroupPressure(ctx.GroupKey, sample.ContactId);
                if (gp is double g)
                    sample = sample with { Pressure01 = g };
            }

            return WmInputResult.Handled;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>
    /// 逐份子报文 × 逐触点槽位挑出「接触面积最大」的那根（手掌就是最大的接触）。
    /// requireTip=true 时只认 TipSwitch 真的按下的槽位；
    /// remember=true 时把本帧各成功读到槽位的取值记进 <see cref="DeviceCtx.LastValues"/> 供抬手帧补位。
    /// </summary>
    private static RawTouchSample? PickBest(DeviceCtx ctx, List<byte[]> reports, bool requireTip, bool remember = false)
    {
        long bestScore = 0;
        RawTouchSample? best = null;

        for (int i = 0; i < reports.Count; i++)
        {
            byte[] report = reports[i];
            long reportBest = 0;
            LinkValues? pick = null;
            var frames = new List<string>();
            var contacts = new List<RawLink>();

            foreach (ushort link in ctx.Links)
            {
                if (!TryReadLink(ctx, report, (uint)report.Length, link, requireTip, out LinkValues v))
                    continue;

                if (remember)
                    ctx.LastValues[link] = v;   // 记下"真的按下"时的取值，供下一步抬手过渡帧补位

                frames.Add($"L{v.Link} W={v.WLogical} H={v.HLogical}");
                contacts.Add(new RawLink(v.Link, v.ContactId, v.WLogical, v.HLogical, v.Wmm, v.Hmm,
                    v.XLogMax > 0 ? (double)v.XLogical / v.XLogMax : null,
                    v.YLogMax > 0 ? (double)v.YLogical / v.YLogMax : null, v.Pressure01));

                long score = (long)v.WLogical * v.HLogical;
                if (score > reportBest)
                {
                    reportBest = score;
                    pick = v;
                }
            }

            if (pick is null || reportBest <= bestScore)
                continue;

            bestScore = reportBest;
            LinkValues bw = pick.Value;
            best = new RawTouchSample(ctx.Name, bw.Wmm, bw.Hmm,
                bw.XLogMax > 0 ? (double)bw.XLogical / bw.XLogMax : null,
                bw.YLogMax > 0 ? (double)bw.YLogical / bw.YLogMax : null,
                bw.Pressure01,
                bw.WLogical, bw.HLogical, bw.XLogical, bw.YLogical, bw.XLogMax, bw.YLogMax,
                bw.WLogMax, bw.HLogMax,
                ctx.XPhysMm, ctx.YPhysMm,
                bw.Link, $"子报文{i + 1}/{reports.Count}：" + string.Join("，", frames), ToHex(report), bw.ContactId,
                contacts);
        }
        return best;
    }

    /// <summary>本帧的接触数量 0x0D:0x54 是否为 0（设备未声明该 usage 则返回 false）。</summary>
    private static bool ContactCountIsZero(DeviceCtx ctx, List<byte[]> reports)
    {
        if (ctx.ContactCountLink is not ushort link)
            return false;
        foreach (byte[] rep in reports)
            if (HidP_GetUsageValue(HidP_Input, 0x0D, link, 0x54, out uint cc, ctx.Preparsed, rep, (uint)rep.Length) == HIDP_STATUS_SUCCESS)
                return cc == 0;
        return false;
    }

    /// <summary>
    /// 抬手过渡帧的补位样本：只取「上一帧真的读到过」的槽位（<see cref="DeviceCtx.LastValues"/>），
    /// 尺寸沿用上一帧、坐标用本帧（非 0 才采用），从而让画布能画出最后一个抬起的点。
    /// </summary>
    private static RawTouchSample? BuildLiftSample(DeviceCtx ctx, List<byte[]> reports)
    {
        RawTouchSample? best = null;
        long bestScore = 0;

        for (int i = 0; i < reports.Count; i++)
        {
            byte[] report = reports[i];
            uint len = (uint)report.Length;

            foreach (ushort link in ctx.Links)
            {
                if (!ctx.LastValues.TryGetValue(link, out LinkValues last)
                    || last.WLogical <= 0 || last.HLogical <= 0)
                    continue;

                Cap? xc = ctx.Xs.FirstOrDefault(c => c.Link == link);
                Cap? yc = ctx.Ys.FirstOrDefault(c => c.Link == link);
                uint xv = 0, yv = 0;
                bool hasX = xc is not null && Get(0x01, link, 0x30, out xv);
                bool hasY = yc is not null && Get(0x01, link, 0x31, out yv);

                LinkValues v = last with
                {
                    XLogical = hasX && xv > 0 ? (int)xv : last.XLogical,
                    YLogical = hasY && yv > 0 ? (int)yv : last.YLogical,
                };

                long score = (long)v.WLogical * v.HLogical;
                if (score <= bestScore)
                    continue;
                bestScore = score;
                best = new RawTouchSample(ctx.Name, v.Wmm, v.Hmm,
                    v.XLogMax > 0 ? (double)v.XLogical / v.XLogMax : null,
                    v.YLogMax > 0 ? (double)v.YLogical / v.YLogMax : null,
                    v.Pressure01,
                    v.WLogical, v.HLogical, v.XLogical, v.YLogical, v.XLogMax, v.YLogMax,
                    v.WLogMax, v.HLogMax,
                    ctx.XPhysMm, ctx.YPhysMm,
                    v.Link, "抬手帧补位：尺寸沿用上一帧、坐标用本帧", ToHex(report), v.ContactId,
                    new[] { new RawLink(v.Link, v.ContactId, v.WLogical, v.HLogical, v.Wmm, v.Hmm,
                        v.XLogMax > 0 ? (double)v.XLogical / v.XLogMax : null,
                        v.YLogMax > 0 ? (double)v.YLogical / v.YLogMax : null, v.Pressure01) });

                bool Get(ushort page, ushort lc, ushort usage, out uint value)
                    => HidP_GetUsageValue(HidP_Input, page, lc, usage, out value, ctx.Preparsed, report, len)
                       == HIDP_STATUS_SUCCESS;
            }
        }
        return best;
    }

    /// <summary>
    /// 按**该触点自己的 LinkCollection** 读一个槽位的尺寸/坐标/压感。
    /// <para>① requireTip 且该槽位声明了「笔尖接触 0x0D:0x42」时，必须 TipSwitch 真的按下才算有效
    /// —— 否则设备会把上一次接触的残留 W/H 留在别的槽位里，把坐标抢走（表现为坐标卡在某个固定点）。</para>
    /// <para>② 尺寸（0x48/0x49）必须两个都读到且非 0。</para>
    /// </summary>
    private static bool TryReadLink(DeviceCtx ctx, byte[] report, uint len, ushort link, bool requireTip, out LinkValues v)
    {
        v = default;

        // 该槽位是否真的按下（只对声明了 TipSwitch 的槽位做这个判断）
        if (requireTip && ctx.TipSwitchLinks.Contains(link))
        {
            if (!TipPressed(ctx, report, len, link))
                return false;
            ctx.TipEverSeen = true;   // 见过真按下 → 之后抬手帧不再走兜底
        }

        Cap? wc = ctx.Widths.FirstOrDefault(c => c.Link == link);
        Cap? hc = ctx.Heights.FirstOrDefault(c => c.Link == link);
        Cap? xc = ctx.Xs.FirstOrDefault(c => c.Link == link);
        Cap? yc = ctx.Ys.FirstOrDefault(c => c.Link == link);
        Cap? pc = ctx.Pressures.FirstOrDefault(c => c.Link == link);

        uint w = 0, h = 0, x = 0, y = 0, p = 0;
        bool hasW = wc is not null && Get(0x0D, link, 0x48, out w);
        bool hasH = hc is not null && Get(0x0D, link, 0x49, out h);
        if (!hasW || !hasH || w == 0 || h == 0)
            return false;

        bool hasX = xc is not null && Get(0x01, link, 0x30, out x);
        bool hasY = yc is not null && Get(0x01, link, 0x31, out y);
        bool hasP = pc is not null && Get(0x0D, link, 0x30, out p);
        bool hasCid = Get(0x0D, link, 0x51, out uint cid);

        v = new LinkValues(link,
            (int)w, (int)h, wc!.LogMax, hc!.LogMax,
            ToMm(w, wc.LogMax, wc.PhysMax, wc.Exp, wc.Units),
            ToMm(h, hc.LogMax, hc.PhysMax, hc.Exp, hc.Units),
            (int)x, (int)y, hasX ? xc!.LogMax : 0, hasY ? yc!.LogMax : 0,
            hasP && pc!.LogMax > pc.LogMin ? Math.Clamp((double)(p - (uint)pc.LogMin) / (pc.LogMax - pc.LogMin), 0, 1) : null,
            hasCid ? (int)cid : 0);
        return true;

        bool Get(ushort page, ushort lc, ushort usage, out uint value)
            => HidP_GetUsageValue(HidP_Input, page, lc, usage, out value, ctx.Preparsed, report, len) == HIDP_STATUS_SUCCESS;
    }

    /// <summary>该槽位本帧的「笔尖接触 0x0D:0x42」是否按下（读不到就当未按下）。</summary>
    private static bool TipPressed(DeviceCtx ctx, byte[] report, uint len, ushort link)
    {
        var usages = new ushort[64];
        uint n = (uint)usages.Length;
        if (HidP_GetUsages(HidP_Input, 0x0D, link, usages, ref n, ctx.Preparsed, report, len) != HIDP_STATUS_SUCCESS)
            return false;
        for (uint i = 0; i < n; i++)
            if (usages[i] == UsageTipSwitch)
                return true;
        return false;
    }

    /// <summary>把本帧各按下触点的压感按 ContactID 记进「同一逻辑屏」缓存（供不带压感的兄弟集合补齐）。</summary>
    private static void CacheFramePressure(DeviceCtx ctx, List<byte[]> reports)
    {
        if (ctx.GroupKey.Length == 0)
            return;
        if (!_groupPressure.TryGetValue(ctx.GroupKey, out GroupPressureState? st))
        {
            st = new GroupPressureState();
            _groupPressure[ctx.GroupKey] = st;
        }

        long now = Environment.TickCount64;
        foreach (byte[] rep in reports)
        {
            uint len = (uint)rep.Length;
            foreach (ushort link in ctx.Links)
            {
                Cap? pc = ctx.Pressures.FirstOrDefault(c => c.Link == link);
                if (pc is null || pc.LogMax <= pc.LogMin)
                    continue;

                // 声明了 TipSwitch 就按它判「真的按下」；否则按 pressure 值判
                if (ctx.TipSwitchLinks.Contains(link) && !TipPressed(ctx, rep, len, link))
                    continue;
                if (HidP_GetUsageValue(HidP_Input, 0x0D, link, 0x30, out uint p, ctx.Preparsed, rep, len) != HIDP_STATUS_SUCCESS)
                    continue;
                if (ctx.TipSwitchLinks.Count == 0 && p <= (uint)pc.LogMin)
                    continue;

                int cid = 0;
                if (HidP_GetUsageValue(HidP_Input, 0x0D, link, 0x51, out uint c, ctx.Preparsed, rep, len) == HIDP_STATUS_SUCCESS)
                    cid = (int)c;

                double pf = Math.Clamp((double)(p - (uint)pc.LogMin) / (pc.LogMax - pc.LogMin), 0, 1);
                st.ByContact[cid] = (pf, now);
                if (pf > 0)
                {
                    st.Latest = pf;
                    st.LatestTicks = now;
                }
            }
        }
    }

    /// <summary>取同屏最近的压感：先按 ContactID 匹配，再退化到最新值；超出时间窗返回 null。</summary>
    private static double? LookupGroupPressure(string groupKey, int contactId)
    {
        if (!_groupPressure.TryGetValue(groupKey, out GroupPressureState? st))
            return null;
        long now = Environment.TickCount64;
        if (st.ByContact.TryGetValue(contactId, out (double P, long Ticks) v) && now - v.Ticks <= PressureWindowMs)
            return v.P;
        if (st.Latest is double l && now - st.LatestTicks <= PressureWindowMs)
            return l;
        return null;
    }

    /// <summary>设备没声明物理量程时，用标定比例把"逻辑计数"换算成毫米：mm = 计数 × 本比例。</summary>
    public static double CountsToMmScale { get; set; }

    /// <summary>逻辑计数 → mm：优先 物理量程；否则退回 <see cref="CountsToMmScale"/>。</summary>
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
            // HID Unit 低 4 位 = 单位制：0 无 / 1 SI线性(cm) / 2 SI旋转(rad) / 3 英制线性(in) / 4 英制旋转(deg)
            bool englishLinear = (units & 0xF) == 0x3;
            return englishLinear ? inUnit * 25.4 : inUnit * 10.0;            // 英寸 / 厘米 → 毫米
        }

        return CountsToMmScale > 0 ? logical * CountsToMmScale : null;
    }

    /// <summary>换算表文字：逻辑量程 → 物理量程 → 每单位多少 mm（供 UI 展示）。</summary>
    private static string ScaleText(Cap c)
    {
        if (c.PhysMax <= 0)
            return $"L{c.Link} LogicalMax={c.LogMax} 无物理量程（用标定比例换算）";
        bool english = (c.Units & 0xF) == 0x3;
        int e = c.Exp & 0xF; if (e >= 8) e -= 16;
        double unitMm = Math.Pow(10, e) * (english ? 25.4 : 10.0);
        double perCount = unitMm * c.PhysMax / c.LogMax;
        return $"L{c.Link} LogicalMax={c.LogMax} PhysicalMax={c.PhysMax}（{(english ? "英寸" : "厘米")}×10^{e}）"
             + $" → {Precision.Fmt(perCount, 6)} mm/计数";
    }

    /// <summary>物理量程后缀（如"（物理 3050 厘米×10^-2）"），供坐标/尺寸显示对照 HidDump。</summary>
    private static string PhysSuffix(Cap? c)
    {
        if (c is null || c.PhysMax <= 0)
            return "";
        int e = c.Exp & 0xF; if (e >= 8) e -= 16;
        bool english = (c.Units & 0xF) == 0x3;
        return $"（物理 {c.PhysMax} {(english ? "英寸" : "厘米")}×10^{e}）";
    }

    /// <summary>该量的物理满量程 → mm（如 3050 厘米×10^-2 → 305mm）。未声明返回 null。</summary>
    private static double? PhysFullMm(Cap? c)
    {
        if (c is null || c.PhysMax <= 0)
            return null;
        int e = c.Exp & 0xF; if (e >= 8) e -= 16;
        bool english = (c.Units & 0xF) == 0x3;
        return c.PhysMax * Math.Pow(10, e) * (english ? 25.4 : 10.0);
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
