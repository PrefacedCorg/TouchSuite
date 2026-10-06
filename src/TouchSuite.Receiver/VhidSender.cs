using System.Runtime.InteropServices;

namespace TouchSuite.Receiver;

/// <summary>
/// 触摸接收端的「落地」抽象：既可以是现有的 user32 合成指针注入（<see cref="TouchInjector"/>），
/// 也可以是本仓库的虚拟 HID 触摸屏驱动（<see cref="VhidSender"/>）。
/// </summary>
internal interface ITouchSink
{
    bool Initialize();
    string ApiName { get; }
    string ContactAreaLabel { get; }
    string LastErrorText { get; }
    long InjectedFrames { get; }
    long FailedFrames { get; }
    int ActiveCount { get; }
    string DebugText { get; }
    string LastInputSummary { get; }
    void Apply(TouchFrame frame, ScreenMapper mapper);
    void ReleaseAll(bool canceled = false);
}

/// <summary>
/// 把触摸帧发送给「TouchSuite 虚拟触摸屏驱动」(VHF)：
///   打开 \\.\TouchBridgeVhid → 每帧 DeviceIoControl(IOCTL_TB_VHID_SUBMIT) 灌一份 HID 报告。
/// 与 TouchInjector 的 user32 合成指针完全独立（可并存，互不影响）。
///
/// 驱动的报告描述符里 Width/Height 单位是 0.01mm、压感 0..1024、X/Y 为 0..32767 归一化，
/// 接触朝向 Azimuth 为 0..359 度（可选用法，无角度信息填 0），
/// 所以这里把平板的归一化接触尺寸换算成毫米后上报 —— Windows / WPF 拿到的就是真面积与朝向。
/// </summary>
internal sealed class VhidSender : ITouchSink, IDisposable
{
    // ---- 与驱动 TouchBridgeVhidPublic.h 严格对应 ----
    private const int MaxContacts = 10;

    private const uint FileDeviceUnknown = 0x00000022;
    private const uint MethodBuffered = 0;
    private const uint FileWriteData = 0x0002;

    private static readonly uint IoctlSubmit =
        (FileDeviceUnknown << 16) | (FileWriteData << 14) | (0x800 << 2) | MethodBuffered;

    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct VhidContact
    {
        public uint Flags;
        public uint Id;
        public uint X;
        public uint Y;
        public uint WidthMm100;
        public uint HeightMm100;
        public uint AzimuthDeg;      // 接触朝向 0..359 度（无角度信息填 0）
        public uint Pressure;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VhidFrame
    {
        public uint Count;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxContacts)]
        public VhidContact[] Contacts;
    }

    // 与驱动 TouchBridgeVhidPublic.h 的 TB_VHID_DIAG 对应（IOCTL 可选输出）。
    [StructLayout(LayoutKind.Sequential)]
    private struct VhidDiag
    {
        public uint SubmitAttempts;
        public uint SubmitFailures;
        public uint ReadyCallbacks;
        public uint FeatureRequests;
        public int LastStatus;
    }

    private const uint FlagTip = 0x1;
    private const uint FlagInRange = 0x2;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr device, uint ioControlCode,
        IntPtr inBuffer, uint inBufferSize, IntPtr outBuffer, uint outBufferSize,
        out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private readonly Dictionary<byte, TouchPointData> _active = new();
    // 平板指针 id → 它在本设备上占用的固定 HID 槽位（按下时分配，抬起时释放）。
    // 驱动上报的 Contact Identifier 就是槽位号，所以槽位必须在一根手指的整个生命周期内保持不变。
    private readonly Dictionary<byte, int> _slotOf = new();
    private long _submitCount;
    private readonly double _screenWidthMm;
    private readonly double _pressureMax;
    private readonly double _pressureFloor;
    private readonly uint _maxContacts;

    private IntPtr _device = InvalidHandle;
    private string _lastSummary = "";
    private string _debugText = "虚拟HID：未连接";

    public VhidSender(double screenWidthMm, double pressureMax, double pressureFloor, uint maxContacts)
    {
        _screenWidthMm = screenWidthMm > 0 ? screenWidthMm : 0;   // 0 → 按 96DPI 估算
        _pressureMax = pressureMax > 0 ? pressureMax : 1.0;
        _pressureFloor = Math.Clamp(pressureFloor, 0, 1024);
        _maxContacts = Math.Clamp(maxContacts, 1, MaxContacts);
    }

    public string LastErrorText { get; private set; } = "";
    public string ApiName => "VirtualHID(VHF)";
    public string ContactAreaLabel => _screenWidthMm > 0
        ? $"来自平板接触尺寸 → 0.01mm（屏幕物理宽 {_screenWidthMm}mm）"
        : "来自平板接触尺寸 → 0.01mm（屏幕宽未指定，按 96DPI 估）";
    public long InjectedFrames { get; private set; }
    public long FailedFrames { get; private set; }
    public int ActiveCount => _active.Count;
    public string DebugText => _debugText;
    public string LastInputSummary => _lastSummary;

    public bool Initialize()
    {
        _device = CreateFileW(@"\\.\TouchBridgeVhid", GenericWrite, 0, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (_device == InvalidHandle)
        {
            int err = Marshal.GetLastWin32Error();
            LastErrorText = err == 5
                ? "打开 \\\\.\\TouchBridgeVhid 被拒绝（Win32 5 = 权限不足），请以管理员身份运行。"
                : $"打开 \\\\.\\TouchBridgeVhid 失败（Win32 {err}），请先安装并启动虚拟触摸屏驱动（见 driver/）。";
            _debugText = "虚拟HID：打开失败";
            Log.Write($"[Vhid] CreateFile 失败 Win32={err}" + (err == 5 ? "（拒绝访问 → 需管理员运行）" : ""));
            return false;
        }
        Log.Write($"[Vhid] 已打开 \\\\.\\TouchBridgeVhid，句柄=0x{_device.ToInt64():X}，GenericWrite");
        _debugText = "虚拟HID：已连接驱动";
        return true;
    }

    public void Apply(TouchFrame frame, ScreenMapper mapper)
    {
        if (_device == InvalidHandle)
            return;

        // 维护"当前仍按下的触点"集合，并为每个指针分配一个在按下→抬起期间固定不变的 HID 槽位。
        // HID 并行模式下 Windows 靠 Contact Identifier(0x51) 跨帧识别同一根手指，而驱动上报的
        // 标识符 = 触点所在槽位号。若槽位随 Dictionary 枚举顺序漂移，多指就会互相"抢"槽位/ID，
        // 表现为同一根手指在两点之间来回闪跳。
        foreach (TouchPointData p in frame.Points)
        {
            if (p.State == Protocol.StateUp)
            {
                _active.Remove(p.Id);
                _slotOf.Remove(p.Id);
            }
            else
            {
                _active[p.Id] = p;
                if (!_slotOf.ContainsKey(p.Id))
                {
                    int slot = AllocSlot();
                    if (slot >= 0)
                        _slotOf[p.Id] = slot;
                }
            }
        }

        var vf = new VhidFrame { Count = 0, Contacts = new VhidContact[MaxContacts] };
        for (int i = 0; i < MaxContacts; i++)
            vf.Contacts[i] = default;

        int n = 0;
        string first = "无活动点";
        // 每像素对应的物理毫米：优先用 --screen-mm ÷ 映射区像素宽；未指定则按 96DPI 估。
        double mmPerPx = _screenWidthMm > 0 && mapper.RegionWidth > 0
            ? _screenWidthMm / mapper.RegionWidth
            : 25.4 / 96.0;

        foreach (KeyValuePair<byte, TouchPointData> kv in _active)
        {
            // 按分配到的固定槽位落位（而不是按枚举顺序），保证槽位号 = 稳定的 Contact Identifier。
            if (!_slotOf.TryGetValue(kv.Key, out int slot) || slot < 0 || slot >= MaxContacts)
                continue;
            TouchPointData p = kv.Value;

            mapper.MapToUnit15(p.X, p.Y, out ushort ux, out ushort uy);
            (double cw, double ch) = mapper.ScaleContact(p.ContactW, p.ContactH);   // 目标屏像素
            double wMm = cw * mmPerPx;
            double hMm = ch * mmPerPx;

            vf.Contacts[slot] = new VhidContact
            {
                Flags = FlagTip | FlagInRange,
                Id = (uint)slot,
                X = ux,
                Y = uy,
                WidthMm100 = (uint)Math.Clamp(Math.Round(wMm * 100.0), 0, 32767),
                HeightMm100 = (uint)Math.Clamp(Math.Round(hMm * 100.0), 0, 32767),
                AzimuthDeg = (uint)Math.Clamp(Math.Round(NormalizeAzimuth(p.OrientationDeg)), 0, 359),
                Pressure = (uint)Math.Clamp(Math.Round(MapPressure(p.Pressure)), 0, 1024),
            };
            if (n == 0)
            {
                first = $"槽{slot} X={ux} Y={uy} W={vf.Contacts[slot].WidthMm100} "
                    + $"H={vf.Contacts[slot].HeightMm100} A={vf.Contacts[slot].AzimuthDeg}° "
                    + $"P={vf.Contacts[slot].Pressure}";
            }
            n++;
        }
        vf.Count = (uint)n;

        int size = Marshal.SizeOf<VhidFrame>();
        int diagSize = Marshal.SizeOf<VhidDiag>();
        IntPtr buf = Marshal.AllocHGlobal(size);
        IntPtr diagBuf = Marshal.AllocHGlobal(diagSize);
        try
        {
            Marshal.StructureToPtr(vf, buf, false);
            Marshal.StructureToPtr(default(VhidDiag), diagBuf, false);
            bool ok = DeviceIoControl(_device, IoctlSubmit, buf, (uint)size, diagBuf, (uint)diagSize, out uint returned, IntPtr.Zero);
            _submitCount++;

            string diagText = "";
            if (returned >= diagSize)
            {
                var d = Marshal.PtrToStructure<VhidDiag>(diagBuf);
                diagText = $" 驱动[提交{d.SubmitAttempts}/失败{d.SubmitFailures}/就绪回调{d.ReadyCallbacks}/特性读取{d.FeatureRequests}/最后状态0x{d.LastStatus:X8}]";
            }

            if (ok)
            {
                InjectedFrames++;
                _debugText = $"虚拟HID：{ActiveCount} 点，累计 {InjectedFrames} 帧";
                if (_submitCount <= 50 || _submitCount % 100 == 0)
                {
                    Log.Write($"[Vhid] submit#{_submitCount} 收到点={frame.Points.Length} 上报={n} {first} → IOCTL OK{diagText}");
                }
            }
            else
            {
                int err = Marshal.GetLastWin32Error();
                FailedFrames++;
                LastErrorText = $"DeviceIoControl 失败（Win32 {err}）";
                _debugText = $"虚拟HID：注入失败 ×{FailedFrames}";
                Log.Write($"[Vhid] submit#{_submitCount} 收到点={frame.Points.Length} 上报={n} → IOCTL 失败 Win32={err}{diagText}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
            Marshal.FreeHGlobal(diagBuf);
        }

        if (frame.Points.Length > 0)
        {
            TouchPointData p0 = frame.Points[0];
            (double cw0, double ch0) = mapper.ScaleContact(p0.ContactW, p0.ContactH);
            _lastSummary = $"id{p0.Id} 归一({p0.X:0.###},{p0.Y:0.###}) →0..32767 面积 {cw0:0.#}×{ch0:0.#}px 压感 {MapPressure(p0.Pressure):0}";
        }
    }

    /// <summary>
    /// 平板角度（度，可能为 NaN/无穷）→ HID Azimuth 语义的 0..359。
    /// 约定换算：Android getOrientation() 是「相对竖直方向、顺时针」，
    /// 而 HID Azimuth(0x0D:0x3F) 官方定义是「绕 Z 轴【逆时针】旋转」
    /// （MS：The counter-clockwise rotation of the cursor about the Z-axis），
    /// 参考轴同为竖直、方向相反 → 取 360 - a。无角度信息给 0。
    /// </summary>
    private static double NormalizeAzimuth(float deg)
    {
        if (float.IsNaN(deg) || float.IsInfinity(deg))
            return 0;
        double d = deg % 360.0;
        if (d < 0) d += 360.0;
        return d == 0 ? 0 : 360.0 - d;
    }

    /// <summary>平板原始压感 → 0..1024（与 user32 注入端同一套映射）。</summary>
    private double MapPressure(double raw)
    {
        double p01 = Math.Clamp(raw / _pressureMax, 0.0, 1.0);
        return Math.Round(_pressureFloor + p01 * (1024.0 - _pressureFloor));
    }

    /// <summary>分配一个当前未被占用的 HID 槽位（0.._maxContacts-1）；无空闲则返回 -1。</summary>
    private int AllocSlot()
    {
        for (int s = 0; s < _maxContacts; s++)
        {
            bool used = false;
            foreach (int v in _slotOf.Values)
            {
                if (v == s)
                {
                    used = true;
                    break;
                }
            }
            if (!used)
                return s;
        }
        return -1;
    }

    /// <summary>抬手：发一帧 Count=0，Windows 认为所有接触都已离开。</summary>
    public void ReleaseAll(bool canceled = false)
    {
        _active.Clear();
        _slotOf.Clear();
        if (_device == InvalidHandle)
            return;

        var vf = new VhidFrame { Count = 0, Contacts = new VhidContact[MaxContacts] };
        int size = Marshal.SizeOf<VhidFrame>();
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(vf, buf, false);
            bool ok = DeviceIoControl(_device, IoctlSubmit, buf, (uint)size, IntPtr.Zero, 0, out _, IntPtr.Zero);
            Log.Write($"[Vhid] ReleaseAll 抬手上报 Count=0 → IOCTL {(ok ? "OK" : "失败 Win32=" + Marshal.GetLastWin32Error())}");
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    public void Dispose()
    {
        if (_device != InvalidHandle)
        {
            CloseHandle(_device);
            _device = InvalidHandle;
        }
    }
}