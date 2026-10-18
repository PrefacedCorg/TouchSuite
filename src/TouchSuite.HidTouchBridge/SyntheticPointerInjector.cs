using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TouchSuite.HidTouchBridge;

/// <summary>触摸视觉反馈模式（对齐 POINTER_FEEDBACK_*）。</summary>
internal enum FeedbackMode : uint
{
    /// <summary>遵循系统「笔和触控」设置（与真实设备一致）。</summary>
    Default = 1,
    Indirect = 2,
    None = 3,
}

/// <summary>
/// 把「本机触摸屏的 HID 帧」注入成系统级合成触摸指针（user32）。
/// <para>
/// 首选新版合成指针 API（Windows 10 1809+）：<c>CreateSyntheticPointerDevice</c> +
/// <c>InjectSyntheticPointerInput</c>；不可用时（Windows 8 / 8.1）自动回退旧版
/// <c>InitializeTouchInjection</c> + <c>InjectTouchInput</c>。
/// </para>
/// <para>
/// 注入的是**符合规范**的一路触摸：每个接触有稳定 ContactId，按下/移动/抬起状态严格对应
/// DOWN/UPDATE/UP，抬起帧沿用最后一次坐标（微软硬性要求）。
/// </para>
/// </summary>
internal sealed class SyntheticPointerInjector : IDisposable
{
    private const uint PT_TOUCH = 2;

    private const uint POINTER_FLAG_NEW = 0x00000001;
    private const uint POINTER_FLAG_INRANGE = 0x00000002;
    private const uint POINTER_FLAG_INCONTACT = 0x00000004;
    private const uint POINTER_FLAG_PRIMARY = 0x00002000;
    private const uint POINTER_FLAG_CONFIDENCE = 0x00004000;
    private const uint POINTER_FLAG_DOWN = 0x00010000;
    private const uint POINTER_FLAG_UPDATE = 0x00020000;
    private const uint POINTER_FLAG_UP = 0x00040000;

    private const uint TOUCH_MASK_CONTACTAREA = 0x00000001;

    /// <summary>ERROR_NOT_READY：默认时间戳的两帧间隔不足 0.1ms，文档要求重试同一帧。</summary>
    private const int ErrorNotReady = 21;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_INFO
    {
        public uint pointerType;
        public uint pointerId;
        public uint frameId;
        public uint pointerFlags;
        public IntPtr sourceDevice;
        public IntPtr hwndTarget;
        public POINT ptPixelLocation;
        public POINT ptHimetricLocation;
        public POINT ptPixelLocationRaw;
        public POINT ptHimetricLocationRaw;
        public uint dwTime;
        public uint historyCount;
        public int InputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_TOUCH_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint touchFlags;
        public uint touchMask;
        public RECT rcContact;
        public RECT rcContactRaw;
        public uint orientation;
        public uint pressure;
    }

    /// <summary>POINTER_TYPE_INFO：type + 联合体(touchInfo/penInfo)，x64 下总长 152。</summary>
    [StructLayout(LayoutKind.Explicit, Size = 152)]
    private struct POINTER_TYPE_INFO
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public POINTER_TOUCH_INFO touchInfo;
    }

    // ---- 新版合成指针 API（首选，Win10 1809+） ----
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateSyntheticPointerDevice(uint pointerType, uint maxCount, uint mode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool InjectSyntheticPointerInput(IntPtr device, [In] POINTER_TYPE_INFO[] pointerInfo, uint count);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void DestroySyntheticPointerDevice(IntPtr device);

    // ---- 旧版触摸注入 API（Win8/8.1 回退） ----
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool InitializeTouchInjection(uint maxCount, uint dwMode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool InjectTouchInput(uint count, [In] POINTER_TOUCH_INFO[] contacts);

    private readonly uint _maxContacts;
    private readonly FeedbackMode _feedback;
    private readonly int _contactPx;
    private readonly bool _dryRun;

    /// <summary>当前处于按下状态的接触：ContactId → 最后一次注入的屏幕坐标。</summary>
    private readonly Dictionary<int, POINT> _active = new();

    private IntPtr _device;
    private bool _usingSynthetic;
    private bool _initialized;
    private uint _frameId;
    private bool _disposed;

    public string LastErrorText { get; private set; } = "";
    public string ApiName => _usingSynthetic ? "SyntheticPointer（新版合成指针）" : "TouchInjection（旧版，Win8 回退）";
    public long InjectedFrames { get; private set; }
    public long FailedFrames { get; private set; }
    public int ActiveCount => _active.Count;

    public SyntheticPointerInjector(uint maxContacts, FeedbackMode feedback, int contactPx, bool dryRun)
    {
        _maxContacts = Math.Max(1, maxContacts);
        _feedback = feedback;
        _contactPx = Math.Max(0, contactPx);
        _dryRun = dryRun;
    }

    public bool Initialize()
    {
        if (_initialized)
            return true;

        // Win7 没有触摸注入 API（InitializeTouchInjection 是 Win8+ 才有），
        // 调用时 user32 里找不到导出会抛 EntryPointNotFoundException —— 这里优雅降级。
        try
        {
            _device = CreateSyntheticPointerDevice(PT_TOUCH, _maxContacts, (uint)_feedback);
        }
        catch (EntryPointNotFoundException)
        {
            _device = IntPtr.Zero;   // Win8/8.1：无此导出
        }

        if (_device != IntPtr.Zero)
        {
            _usingSynthetic = true;
            _initialized = true;
            return true;
        }

        string syntheticErr = new Win32Exception(Marshal.GetLastWin32Error()).Message;

        try
        {
            if (InitializeTouchInjection(_maxContacts, (uint)_feedback))
            {
                _usingSynthetic = false;
                _initialized = true;
                return true;
            }
        }
        catch (EntryPointNotFoundException)
        {
            LastErrorText = "本机不支持触摸注入 API（合成指针需 Windows 8 及以上）。";
            return false;
        }

        LastErrorText = $"CreateSyntheticPointerDevice 失败：{syntheticErr}；"
            + $"回退 InitializeTouchInjection 也失败：{new Win32Exception(Marshal.GetLastWin32Error()).Message}";
        return false;
    }

    /// <summary>
    /// 注入一帧。actives 为本帧「按下」的全部触点（已换算成屏幕物理像素）。
    /// 本帧未出现、但之前按下的接触会被自动抬起（UP），且沿用其最后一次坐标。
    /// </summary>
    public void Apply(IReadOnlyList<(int ContactId, int X, int Y)> actives)
    {
        if (!_initialized)
            return;

        _frameId++;
        var infos = new List<POINTER_TYPE_INFO>(actives.Count + 1);
        var seen = new HashSet<int>();

        foreach ((int contactId, int x, int y) in actives)
        {
            if (!seen.Add(contactId))
                continue;

            uint flags = _active.ContainsKey(contactId)
                ? POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | POINTER_FLAG_UPDATE
                : POINTER_FLAG_NEW | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | POINTER_FLAG_DOWN;

            _active[contactId] = new POINT { X = x, Y = y };
            infos.Add(Build(contactId, x, y, flags));
        }

        // 抬起：本帧没再出现的接触（坐标一律沿用最后一次，微软硬性要求）
        foreach (int contactId in _active.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            POINT last = _active[contactId];
            infos.Add(Build(contactId, last.X, last.Y, POINTER_FLAG_INRANGE | POINTER_FLAG_UP));
            _active.Remove(contactId);
        }

        if (infos.Count > 0)
        {
            // 与能正常工作的 Receiver 完全一致：每批指针的**首个**一律带 PRIMARY + CONFIDENCE（含纯抬手帧）。
            // 纯抬手帧里只有 UP 指针，若首个不带主指针标志，Windows 会把这批当成不规范帧
            // （ERROR_INVALID_PARAMETER → 取消全部活动接触），症状就是抬手丢事件 / 触摸卡住。
            infos[0] = WithPrimary(infos[0]);
            Inject(infos);
        }
    }

    /// <summary>给单个指针补上 PRIMARY + CONFIDENCE（其余字段保持不变）。</summary>
    private static POINTER_TYPE_INFO WithPrimary(POINTER_TYPE_INFO info)
    {
        info.touchInfo.pointerInfo.pointerFlags |= POINTER_FLAG_PRIMARY | POINTER_FLAG_CONFIDENCE;
        return info;
    }

    /// <summary>抬起全部接触（退出 / 断开时调用，避免触摸卡死）。</summary>
    public void ReleaseAll()
    {
        if (!_initialized || _active.Count == 0)
            return;

        _frameId++;
        var infos = new List<POINTER_TYPE_INFO>(_active.Count);
        foreach (KeyValuePair<int, POINT> kv in _active)
            infos.Add(Build(kv.Key, kv.Value.X, kv.Value.Y, POINTER_FLAG_INRANGE | POINTER_FLAG_UP));
        _active.Clear();

        if (infos.Count == 0)
            return;

        infos[0] = WithPrimary(infos[0]);   // 与 Apply 一致：首个必须是主指针
        Inject(infos);
    }

    private POINTER_TYPE_INFO Build(int contactId, int x, int y, uint flags)
    {
        var info = new POINTER_TYPE_INFO
        {
            type = PT_TOUCH,
            touchInfo = new POINTER_TOUCH_INFO
            {
                pointerInfo = new POINTER_INFO
                {
                    pointerType = PT_TOUCH,
                    pointerId = (uint)contactId,
                    frameId = _frameId,
                    pointerFlags = flags,
                    ptPixelLocation = new POINT { X = x, Y = y },
                    historyCount = 1,
                },
            },
        };

        if (_contactPx > 0)
        {
            int half = Math.Max(1, _contactPx / 2);
            var rect = new RECT { Left = x - half, Top = y - half, Right = x + half, Bottom = y + half };
            info.touchInfo.touchMask = TOUCH_MASK_CONTACTAREA;
            info.touchInfo.rcContact = rect;
            info.touchInfo.rcContactRaw = rect;
        }

        return info;
    }

    private void Inject(List<POINTER_TYPE_INFO> infos)
    {
        if (_dryRun)
        {
            InjectedFrames++;
            return;
        }

        if (TryInject(infos))
        {
            InjectedFrames++;
            return;
        }

        int err = Marshal.GetLastWin32Error();

        // 文档要求：两次注入的默认时间戳窗口靠得太近会返回 ERROR_NOT_READY(21)，
        // 此时必须把**同一帧**重试一次，否则该帧被直接丢弃（表现为笔画漏点/卡顿）。
        if (err == ErrorNotReady)
        {
            System.Threading.Thread.Sleep(1);
            if (TryInject(infos))
            {
                InjectedFrames++;
                return;
            }
            err = Marshal.GetLastWin32Error();
        }

        FailedFrames++;
        LastErrorText = new Win32Exception(err).Message;
        // 失败不能静默：不开全量日志时也要能看到（与 Receiver 的失败打印同一节奏）
        if (FailedFrames <= 5 || FailedFrames % 100 == 0)
            Log.Write($"注入失败 ×{FailedFrames}（Win32 {err}：{LastErrorText}）");
    }

    /// <summary>真正调一次注入 API（新版合成指针 / 旧版触摸注入）。</summary>
    private bool TryInject(List<POINTER_TYPE_INFO> infos)
    {
        if (_usingSynthetic)
            return InjectSyntheticPointerInput(_device, infos.ToArray(), (uint)infos.Count);

        var legacy = new POINTER_TOUCH_INFO[infos.Count];
        for (int i = 0; i < infos.Count; i++)
            legacy[i] = infos[i].touchInfo;
        return InjectTouchInput((uint)infos.Count, legacy);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try { ReleaseAll(); } catch { /* 退出阶段忽略 */ }

        if (_usingSynthetic && _device != IntPtr.Zero)
        {
            try { DestroySyntheticPointerDevice(_device); } catch { /* 忽略 */ }
        }
        _device = IntPtr.Zero;
    }
}
