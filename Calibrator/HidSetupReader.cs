using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TouchErase.Calibrator;

/// <summary>
/// 用 SetupAPI 直接打开触摸屏 HID 设备读输入报告（不依赖 RawInput，也不依赖 WM_POINTER）。
///
/// 为什么需要它：真数字化器（触摸屏）走 Digitizer 通路，<c>GetRawInputDeviceList</c> 里
/// 一个 <c>RIM_TYPEHID</c> 都看不到（见 TouchInput 的日志），所以原来的原始HID 通路
/// 在触摸屏上永远收不到 WM_INPUT。而 SetupAPI 可以按 GUID_DEVINTERFACE_HID 直接
/// 枚举到设备本身，再 CreateFile + ReadFile 读报告 —— 这是 Windows 官方推荐的 HID 访问方式。
///
/// 线程模型：后台线程阻塞在 ReadFile，读到一帧就解析成 <see cref="TouchSample"/> 抛给 UI 线程。
/// </summary>
public sealed class HidSetupReader : IDisposable
{
    /// <summary>解析出的一帧样本（与 TouchInput 的 Sample 事件同签名，便于并联仲裁）。</summary>
    public event Action<TouchSample>? Sample;

    public string DeviceName { get; private set; } = "(未发现触摸屏 HID 设备)";
    public bool Found { get; private set; }
    public bool DeclaresSize { get; private set; }
    public bool DeclaresPressure { get; private set; }
    public string DiagSummary { get; private set; } = "";
    public string WScaleText { get; private set; } = "无";
    public string HScaleText { get; private set; } = "无";

    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private SafeFileHandle? _handle;
    private bool _disposed;

    // ================= interop =================

    private static Guid GUID_DEVINTERFACE_HID = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
        ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetPreparsedData(IntPtr hidDeviceObject, out IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern bool HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);

    [DllImport("hid.dll")]
    private static extern int HidP_GetValueCaps(int reportType, byte[] valueCaps, ref ushort valueCapsLength, IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetLinkCollectionNodes([In, Out] HIDP_LINK_COLLECTION_NODE[] nodes,
        ref uint linkCollectionNodesLength, IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection,
        ushort usage, out uint value, IntPtr preparsedData, byte[] report, uint reportLength);

    /// <summary>
    /// HIDP_CAPS（hidpi.h）。
    /// 注意：官方头文件里中间是 <c>USHORT Reserved[17]</c>。这里刻意**展平成 17 个独立字段**
    /// 而不用 <c>[MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]</c> ——
    /// ByValArray 在 out 场景下会走 MngdNativeArrayMarshaler，实测在 .NET 10 上会触发
    /// CLR 内部错误（0x80131506）直接崩进程。展平后完全避开 marshaler 的数组转换。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        public ushort Res01, Res02, Res03, Res04, Res05, Res06, Res07, Res08;
        public ushort Res09, Res10, Res11, Res12, Res13, Res14, Res15, Res16, Res17;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_LINK_COLLECTION_NODE
    {
        public ushort LinkUsage;
        public ushort LinkUsagePage;
        public ushort Parent;
        public ushort NumberOfChildren;
        public ushort NextSibling;
        public ushort FirstChild;
        public uint BitField;
    }

    private const uint DIGCF_PRESENT = 0x02;
    private const uint DIGCF_DEVICEINTERFACE = 0x10;
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x01;
    private const uint FILE_SHARE_WRITE = 0x02;
    private const uint OPEN_EXISTING = 3;
    private const int HIDP_INPUT = 0;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;
    private const int HidP_ValueCapsSize = 72;

    // ================= 设备能力表 =================

    private sealed class Ctx
    {
        public IntPtr Preparsed;
        public ushort Usage;
        public ushort UsagePage;
        public int InputReportLen;
        public int LinkCollections;
        public bool HasW, HasH, HasP, HasId, HasTip;
        public int WLogMin, WLogMax, WPhysMin, WPhysMax, WExp, WUnits;
        public int HLogMin, HLogMax, HPhysMin, HPhysMax, HExp, HUnits;
        public int PLogMin, PLogMax;
        public int XLogMax, YLogMax;

        /// <summary>顶层 usage = Digitizer(0x0D) / TouchScreen(0x04)。</summary>
        public bool IsTouchScreen => UsagePage == 0x0D && Usage == 0x04;
    }

    // ================= 探测 + 启动 =================

    /// <summary>
    /// 枚举全部 HID，挑出顶层 usage = Digitizer(0x0D)/TouchScreen(0x04) 的那块，
    /// 解析能力表，再起后台线程持续 ReadFile。找不到就把诊断写进 <see cref="DiagSummary"/>。
    /// </summary>
    public void Start()
    {
        var t = new Thread(() =>
        {
            try
            {
                StartCore();
            }
            catch (Exception ex)
            {
                Found = false;
                DiagSummary = "HID 探测异常: " + ex.Message;
                Log.Error("HID(SetupAPI) 探测异常: " + ex);
            }
            finally
            {
                Log.Flush();
            }
        })
        {
            IsBackground = true,
            Name = "HidSetupProbe",
        };
        t.Start();
        t.Join(3000);   // 探测通常 <100ms；给它 3 秒，超时就让它在后台继续，不拖住向导
        Log.Info($"[HID探测] 探测线程结束={!t.IsAlive}");    }

    private void StartCore()
    {
        var all = new List<string>();
        var candidates = new List<(string Path, Ctx Ctx)>();

        IntPtr set = SetupDiGetClassDevs(ref GUID_DEVINTERFACE_HID, IntPtr.Zero, IntPtr.Zero,
            DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == new IntPtr(-1) || set == IntPtr.Zero)
        {
            DiagSummary = "SetupDiGetClassDevs 失败（err=" + Marshal.GetLastWin32Error() + "）";
            return;
        }

        try
        {
            var iface = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            uint index = 0;
            while (SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref GUID_DEVINTERFACE_HID, index, ref iface))
            {
                index++;
                string? devPath = GetDevicePath(set, ref iface);
                if (devPath is null)
                    continue;

                Ctx? ctx = null;
                string? err = null;
                try
                {
                    (ctx, err) = OpenAndParse(devPath);
                }
                catch (Exception ex)
                {
                    err = "OpenAndParse 异常: " + ex.Message;
                }

                if (ctx is null)
                {
                    all.Add($"· {devPath} | {err}");
                    continue;
                }

                bool isTouch = ctx.IsTouchScreen;
                all.Add($"· {devPath} | Usage=0x{ctx.UsagePage:X2}:0x{ctx.Usage:X2} {(isTouch ? "★触摸屏" : "")}"
                        + $" | W={ctx.HasW} H={ctx.HasH} P={ctx.HasP} col={ctx.LinkCollections}");
                if (isTouch)
                    candidates.Add((devPath, ctx));
                else
                    HidD_FreePreparsedData(ctx.Preparsed);   // 必须用 HidD 的释放函数，不能用 FreeHGlobal
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        Log.Info($"HID(SetupAPI) 清单（共 {all.Count} 个）：\n  " + string.Join("\n  ", all));

        if (candidates.Count == 0)
        {
            DiagSummary = "SetupAPI 枚举里没有 Digitizer/TouchScreen（0x0D:0x04）设备";
            Log.Info("HID(SetupAPI): " + DiagSummary);
            return;
        }

        (string path, Ctx ctx0) = candidates[0];
        DeviceName = ShortName(path);
        Found = true;
        DeclaresSize = ctx0.HasW && ctx0.HasH;
        DeclaresPressure = ctx0.HasP;
        WScaleText = ScaleText("宽 0x48", ctx0.HasW, ctx0.WLogMin, ctx0.WLogMax, ctx0.WPhysMin, ctx0.WPhysMax, ctx0.WExp, ctx0.WUnits);
        HScaleText = ScaleText("高 0x49", ctx0.HasH, ctx0.HLogMin, ctx0.HLogMax, ctx0.HPhysMin, ctx0.HPhysMax, ctx0.HExp, ctx0.HUnits);

        DiagSummary = $"{DeviceName} | W={ctx0.HasW} H={ctx0.HasH} P={ctx0.HasP} id={ctx0.HasId} tip={ctx0.HasTip}"
                      + $" | 集合={ctx0.LinkCollections} 报告={ctx0.InputReportLen}字节";
        Log.Info("HID(SetupAPI) 选中: " + DiagSummary);
        Log.Info($"HID(SetupAPI) 换算表: {WScaleText} ； {HScaleText}");

        StartReading(path, ctx0);
    }

    private void StartReading(string path, Ctx ctx)
    {
        // 读报告必须用 GENERIC_READ 打开的句柄；这里绝不复用能力查询时那个 0 权限句柄。
        _handle = OpenHandle(path, requireRead: true);
        if (_handle is null || _handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            DiagSummary += $" | ReadFile 用句柄打开失败（GENERIC_READ err={err}，触摸屏可能被系统数字化器独占）";
            Log.Warn($"HID(SetupAPI) CreateFile(GENERIC_READ) 失败 err={err}: {path}");
            return;
        }

        _cts = new CancellationTokenSource();
        _thread = new Thread(() => ReadLoop(ctx, _cts.Token))
        {
            IsBackground = true,
            Name = "HidSetupReader",
        };
        _thread.Start();
    }

    private void ReadLoop(Ctx ctx, CancellationToken token)
    {
        int len = Math.Max(ctx.InputReportLen, 8) + 1;   // +1 留给 ReportID
        IntPtr buf = Marshal.AllocHGlobal(len);
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!ReadFile(_handle!, buf, (uint)len, out uint read, IntPtr.Zero) || read == 0)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (!token.IsCancellationRequested)
                        Log.Warn($"HID(SetupAPI) ReadFile 结束：read={read} err={err}");
                    return;
                }

                var report = new byte[read];
                Marshal.Copy(buf, report, 0, (int)read);

                try
                {
                    TouchSample? s = ParseFrame(ctx, report, (int)read);
                    if (s is not null)
                        Sample?.Invoke(s);
                }
                catch (Exception ex)
                {
                    Log.Error("HID(SetupAPI) 解析帧失败: " + ex.Message);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    // ================= 帧解析 =================

    private TouchSample? ParseFrame(Ctx ctx, byte[] report, int reportLen)
    {
        if (reportLen <= 0)
            return null;

        // 多指：每根手指一个链集合，从 col=1 开始（col=0 是根集合）
        if (ctx.LinkCollections > 1)
        {
            var list = new List<ContactRect>();
            double? maxW = null, maxH = null, sum = null, maxP = null;
            double sx = 0, sy = 0, sw = 0;
            int maxWLogical = 0;

            int n = Math.Min(ctx.LinkCollections, 64);
            for (int col = 1; col < n; col++)
            {
                ushort c = (ushort)col;

                bool hasTip = HidP_GetUsageValue(HIDP_INPUT, 0x0D, c, 0x42, out uint tv, ctx.Preparsed, report, (uint)reportLen) == HIDP_STATUS_SUCCESS;
                if (hasTip && tv == 0)
                    continue;   // 该集合的手指已抬起

                uint w = 0, h = 0, p = 0, x = 0, y = 0;
                bool hasW = ctx.HasW && HidP_GetUsageValue(HIDP_INPUT, 0x0D, c, 0x48, out w, ctx.Preparsed, report, (uint)reportLen) == HIDP_STATUS_SUCCESS;
                bool hasH = ctx.HasH && HidP_GetUsageValue(HIDP_INPUT, 0x0D, c, 0x49, out h, ctx.Preparsed, report, (uint)reportLen) == HIDP_STATUS_SUCCESS;
                bool hasP = ctx.HasP && HidP_GetUsageValue(HIDP_INPUT, 0x0D, c, 0x30, out p, ctx.Preparsed, report, (uint)reportLen) == HIDP_STATUS_SUCCESS;
                bool hasX = HidP_GetUsageValue(HIDP_INPUT, 0x01, c, 0x30, out x, ctx.Preparsed, report, (uint)reportLen) == HIDP_STATUS_SUCCESS;
                bool hasY = HidP_GetUsageValue(HIDP_INPUT, 0x01, c, 0x31, out y, ctx.Preparsed, report, (uint)reportLen) == HIDP_STATUS_SUCCESS;

                if (hasTip && !hasW && !hasH && !hasP && !hasX && !hasY)
                    continue;

                int id = col;
                if (HidP_GetUsageValue(HIDP_INPUT, 0x0D, c, 0x51, out uint cid, ctx.Preparsed, report, (uint)reportLen) == HIDP_STATUS_SUCCESS)
                    id = (int)cid;

                double? wMm = hasW ? ToMm(w, ctx.WLogMin, ctx.WLogMax, ctx.WPhysMin, ctx.WPhysMax, ctx.WExp, ctx.WUnits) : null;
                double? hMm = hasH ? ToMm(h, ctx.HLogMin, ctx.HLogMax, ctx.HPhysMin, ctx.HPhysMax, ctx.HExp, ctx.HUnits) : null;
                double? p01 = hasP && ctx.PLogMax > ctx.PLogMin
                    ? Math.Clamp((double)(p - (uint)ctx.PLogMin) / (ctx.PLogMax - ctx.PLogMin), 0, 1) : null;
                double? xn = hasX && ctx.XLogMax > 0 ? Math.Clamp((double)x / ctx.XLogMax, 0, 1) : null;
                double? yn = hasY && ctx.YLogMax > 0 ? Math.Clamp((double)y / ctx.YLogMax, 0, 1) : null;

                list.Add(new ContactRect(id, null, wMm, hMm, xn, yn, p01, hasW ? (int)w : 0, hasH ? (int)h : 0));

                if (wMm is double ww && hMm is double hh)
                {
                    sum = (sum ?? 0) + ww * hh;
                    if (ww > (maxW ?? 0)) maxW = ww;
                    if (hh > (maxH ?? 0)) maxH = hh;
                }
                if (hasW && w > maxWLogical) maxWLogical = (int)w;
                if (p01 is double pv && pv > (maxP ?? 0)) maxP = pv;

                if (xn is double nx && yn is double ny)
                {
                    double weight = wMm is double a && hMm is double b ? a * b : 1;
                    sx += nx * weight;
                    sy += ny * weight;
                    sw += weight;
                }
            }

            if (list.Count == 0)
                return null;

            double? cx = sw > 0 ? sx / sw : null;
            double? cy = sw > 0 ? sy / sw : null;

            string detail = list.Count == 1
                ? $"W={list[0].WLogical}/{ctx.WLogMax} → {Precision.Fmt(list[0].WMm)} mm  H={list[0].HLogical}/{ctx.HLogMax} → {Precision.Fmt(list[0].HMm)} mm"
                : $"{list.Count} 指 Σ {Precision.Fmt(sum, 0)} mm²: "
                  + string.Join("，", list.Select(c => $"#{c.Id} {Precision.Fmt(c.WMm)}×{Precision.Fmt(c.HMm)} mm"));

            return new TouchSample("HidSetup", maxW, maxH, maxP, detail,
                PressureRaw: null, PressureRange: "",
                WidthLogical: maxWLogical, HeightLogical: maxWLogical,
                XNorm: cx, YNorm: cy, Contacts: list);
        }

        // 单指兜底：usage 全挂根集合
        uint w2 = 0, h2 = 0, p2 = 0, x2 = 0, y2 = 0;
        bool hasW2 = ctx.HasW && HidP_GetUsageValue(HIDP_INPUT, 0x0D, 0, 0x48, out w2, ctx.Preparsed, report, (uint)reportLen) == HIDP_STATUS_SUCCESS;
        bool hasH2 = ctx.HasH && HidP_GetUsageValue(HIDP_INPUT, 0x0D, 0, 0x49, out h2, ctx.Preparsed, report, (uint)reportLen) == HIDP_STATUS_SUCCESS;
        bool hasP2 = ctx.HasP && HidP_GetUsageValue(HIDP_INPUT, 0x0D, 0, 0x30, out p2, ctx.Preparsed, report, (uint)reportLen) == HIDP_STATUS_SUCCESS;
        HidP_GetUsageValue(HIDP_INPUT, 0x01, 0, 0x30, out x2, ctx.Preparsed, report, (uint)reportLen);
        HidP_GetUsageValue(HIDP_INPUT, 0x01, 0, 0x31, out y2, ctx.Preparsed, report, (uint)reportLen);

        if (!hasW2 && !hasH2 && !hasP2)
            return null;

        double? wMm2 = hasW2 ? ToMm(w2, ctx.WLogMin, ctx.WLogMax, ctx.WPhysMin, ctx.WPhysMax, ctx.WExp, ctx.WUnits) : null;
        double? hMm2 = hasH2 ? ToMm(h2, ctx.HLogMin, ctx.HLogMax, ctx.HPhysMin, ctx.HPhysMax, ctx.HExp, ctx.HUnits) : null;
        double? p012 = hasP2 && ctx.PLogMax > ctx.PLogMin
            ? Math.Clamp((double)(p2 - (uint)ctx.PLogMin) / (ctx.PLogMax - ctx.PLogMin), 0, 1) : null;

        return new TouchSample("HidSetup", wMm2, hMm2, p012,
            $"W={w2}/{ctx.WLogMax} → {Precision.Fmt(wMm2)} mm  H={h2}/{ctx.HLogMax} → {Precision.Fmt(hMm2)} mm",
            hasP2 ? (int)p2 : null, hasP2 ? $"{ctx.PLogMin}..{ctx.PLogMax}" : "",
            WidthLogical: (int)w2, HeightLogical: (int)h2,
            XNorm: ctx.XLogMax > 0 ? Math.Clamp((double)x2 / ctx.XLogMax, 0, 1) : null,
            YNorm: ctx.YLogMax > 0 ? Math.Clamp((double)y2 / ctx.YLogMax, 0, 1) : null);
    }

    private double? ToMm(uint logical, int logMin, int logMax, int physMin, int physMax, int unitsExp, int units)
    {
        long logRange = (long)logMax - logMin;
        if (logRange <= 0) return null;
        long physRange = (long)physMax - physMin;
        if (physRange <= 0) return null;

        double fraction = ((long)logical - logMin) / (double)logRange;
        double physical = physMin + fraction * physRange;

        int exp = unitsExp & 0xF;
        if (exp >= 8) exp -= 16;
        double inUnit = physical * Math.Pow(10, exp);
        bool english = (units & 0xF000) == 0x3000;
        return english ? inUnit * 25.4 : inUnit * 10.0;
    }

    // ================= 打开 / 解析能力 =================

    private static (Ctx? Ctx, string? Err) OpenAndParse(string path)
    {
        SafeFileHandle h = OpenHandle(path, requireRead: false);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            return (null, $"CreateFile 失败 (err={err})");
        }

        IntPtr prep = IntPtr.Zero;
        try
        {
            if (!HidD_GetPreparsedData(h.DangerousGetHandle(), out prep) || prep == IntPtr.Zero)
                return (null, $"HidD_GetPreparsedData 失败 (err={Marshal.GetLastWin32Error()})");

            if (!HidP_GetCaps(prep, out HIDP_CAPS caps))
                return (null, "HidP_GetCaps 失败");

            var ctx = new Ctx
            {
                Preparsed = prep,
                Usage = caps.Usage,
                UsagePage = caps.UsagePage,
                InputReportLen = caps.InputReportByteLength,
                LinkCollections = caps.NumberLinkCollectionNodes,
            };
            prep = IntPtr.Zero;   // 所有权移交 ctx，由调用方释放

            if (caps.NumberInputValueCaps > 0)
            {
                var buffer = new byte[HidP_ValueCapsSize * caps.NumberInputValueCaps];
                ushort len = caps.NumberInputValueCaps;
                if (HidP_GetValueCaps(HIDP_INPUT, buffer, ref len, ctx.Preparsed) == HIDP_STATUS_SUCCESS)
                {
                    for (int k = 0; k < len; k++)
                    {
                        int off = k * HidP_ValueCapsSize;
                        ushort up = BitConverter.ToUInt16(buffer, off + 0);
                        ushort usage = BitConverter.ToUInt16(buffer, off + 56);
                        int logMin = BitConverter.ToInt32(buffer, off + 40);
                        int logMax = BitConverter.ToInt32(buffer, off + 44);
                        int physMin = BitConverter.ToInt32(buffer, off + 48);
                        int physMax = BitConverter.ToInt32(buffer, off + 52);
                        int units = BitConverter.ToInt32(buffer, off + 36);
                        int exp = BitConverter.ToInt32(buffer, off + 32);

                        if (up == 0x0D && usage == 0x48) { ctx.HasW = true; ctx.WLogMin = logMin; ctx.WLogMax = logMax; ctx.WPhysMin = physMin; ctx.WPhysMax = physMax; ctx.WExp = exp; ctx.WUnits = units; }
                        if (up == 0x0D && usage == 0x49) { ctx.HasH = true; ctx.HLogMin = logMin; ctx.HLogMax = logMax; ctx.HPhysMin = physMin; ctx.HPhysMax = physMax; ctx.HExp = exp; ctx.HUnits = units; }
                        if (up == 0x0D && usage == 0x30) { ctx.HasP = true; ctx.PLogMin = logMin; ctx.PLogMax = logMax; }
                        if (up == 0x0D && usage == 0x51) ctx.HasId = true;
                        if (up == 0x0D && usage == 0x42) ctx.HasTip = true;
                        if (up == 0x01 && usage == 0x30) ctx.XLogMax = logMax;
                        if (up == 0x01 && usage == 0x31) ctx.YLogMax = logMax;
                    }
                }
            }

            return (ctx, null);
        }
        finally
        {
            if (prep != IntPtr.Zero)
                HidD_FreePreparsedData(prep);
            h.Dispose();
        }
    }

    /// <summary>
    /// 打开设备句柄。
    /// <para><paramref name="requireRead"/>=true：读报告用，必须 GENERIC_READ（HID 读输入报告要读权限），
    /// 失败就直接失败，绝不退回 0 权限 —— 否则拿到的是"能查询不能读"的句柄，ReadFile 必报 err=5。</para>
    /// <para><paramref name="requireRead"/>=false：仅做能力查询（HidD_GetPreparsedData），
    /// 触摸屏常被系统数字化器独占，读权限可能拿不到，故退回 0 权限也能问出能力表。</para>
    /// </summary>
    private static SafeFileHandle OpenHandle(string path, bool requireRead)
    {
        const uint share = FILE_SHARE_READ | FILE_SHARE_WRITE;

        SafeFileHandle h = CreateFileW(path, GENERIC_READ, share, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (!h.IsInvalid || requireRead)
            return h;   // 读模式：拿到就返回，拿不到也返回（让上层报真实 err）

        // 查询模式：读权限拿不到时退回 0 权限（只问能力表，不读报告）
        h.Dispose();
        return CreateFileW(path, 0, share, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
    }

    private string? GetDevicePath(IntPtr set, ref SP_DEVICE_INTERFACE_DATA iface)
    {
        SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out uint required, IntPtr.Zero);
        if (required == 0)
            return null;

        IntPtr buffer = Marshal.AllocHGlobal((int)required);
        try
        {
            // SP_DEVICE_INTERFACE_DETAIL_DATA：cbSize 在 x64 惯例填 8，
            // 但结构按 pack(4) 定义，Path 只能从偏移 4 读（从 8 读会丢开头的 \\）。
            Marshal.WriteInt32(buffer, 0, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(set, ref iface, buffer, required, out _, IntPtr.Zero))
                return null;
            return Marshal.PtrToStringUni(buffer + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string ShortName(string path)
    {
        // \\?\hid#vid_1ff7&pid_0013&mi_00&col05#7&180f2f4a&0&0004#{...} → vid_1ff7&pid_0013&col05
        string[] parts = path.Split('#');
        if (parts.Length >= 4)
            return $"hid {parts[1]} #{parts[2]}";
        return path;
    }

    private static string ScaleText(string label, bool has, int logMin, int logMax, int physMin, int physMax, int exp, int units)
    {
        if (!has) return $"{label}: 未声明";
        long phys = (long)physMax - physMin;
        if (phys <= 0) return $"{label}: 无换算说明（逻辑 {logMin}..{logMax}）";
        bool english = (units & 0xF000) == 0x3000;
        int e = exp & 0xF; if (e >= 8) e -= 16;
        double unitMm = Math.Pow(10, e) * (english ? 25.4 : 10.0);
        double perCount = unitMm * phys / ((long)logMax - logMin);
        return $"{label}: {logMin}..{logMax} → {physMin}..{physMax}（{(english ? "英寸" : "厘米")}×10^{e}）"
             + $" = {Precision.Fmt(perCount, 6)} mm/计数";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle handle, IntPtr buffer, uint toRead, out uint read, IntPtr overlapped);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts?.Cancel(); } catch { }
        try { _handle?.Dispose(); } catch { }
        _cts?.Dispose();
    }
}
