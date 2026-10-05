using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TouchBridge.Receiver;

/// <summary>注入成什么指针类型。</summary>
internal enum TouchInputType
{
    /// <summary>按平板上报的工具类型自动选择：手指→触摸，笔/橡皮→笔。</summary>
    Auto,
    /// <summary>一律注入为触摸（有接触面积，但极少应用会用到触摸压感）。</summary>
    Touch,
    /// <summary>一律注入为笔（压感会真正生效，但笔没有接触面积字段）。</summary>
    Pen,
}

/// <summary>接触面积怎么定。</summary>
internal enum ContactAreaMode
{
    /// <summary>自动：平板报了 size 就用 size 定面积（长宽按 major:minor 比例缩放，使 W×H = size）；否则用 major/minor 包围盒。</summary>
    Auto,
    /// <summary>强制用 touchMajor/touchMinor 折算的包围盒（旧行为）。</summary>
    Major,
}

/// <summary>
/// 把平板触摸还原成系统级指针，支持接触面积（rcContact）与压感（pressure）。
///
/// 默认使用「合成指针设备」API（Windows 10 1809+）：
/// 实测部分机器（装了第三方虚拟 HID 触摸设备时）旧版 InitializeTouchInjection +
/// InjectTouchInput 会持续返回 ERROR_INVALID_PARAMETER(87)，而新 API 正常。
/// 新 API 不可用时自动回退到旧 API（旧 API 不支持笔，会自动退化为触摸）。
/// </summary>
internal sealed class TouchInjector : ITouchSink, IDisposable
{
    public enum FeedbackMode : uint
    {
        /// <summary>遵循系统「笔和触控」设置（与真实设备一致）。</summary>
        Default = 1,
        Indirect = 2,
        None = 3,
    }

    // ---- winuser.h 常量 ----

    private const uint PT_TOUCH = 2;
    private const uint PT_PEN = 3;

    // 工具类型（对齐 android/input.h 的 AMOTION_EVENT_TOOL_TYPE_*：
    // UNKNOWN=0, FINGER=1, STYLUS=2, MOUSE=3, ERASER=4）
    private const byte ToolFinger = 1;
    private const byte ToolStylus = 2;
    private const byte ToolMouse = 3;
    private const byte ToolEraser = 4;

    private const uint POINTER_FLAG_NEW = 0x00000001;
    private const uint POINTER_FLAG_INRANGE = 0x00000002;
    private const uint POINTER_FLAG_INCONTACT = 0x00000004;
    private const uint POINTER_FLAG_PRIMARY = 0x00002000;
    private const uint POINTER_FLAG_CONFIDENCE = 0x00004000;
    private const uint POINTER_FLAG_CANCELED = 0x00008000;
    private const uint POINTER_FLAG_DOWN = 0x00010000;
    private const uint POINTER_FLAG_UPDATE = 0x00020000;
    private const uint POINTER_FLAG_UP = 0x00040000;

    private const uint TOUCH_MASK_CONTACTAREA = 0x00000001;
    private const uint TOUCH_MASK_ORIENTATION = 0x00000002;
    private const uint TOUCH_MASK_PRESSURE = 0x00000004;

    private const uint PEN_FLAG_NONE = 0x00000000;
    private const uint PEN_FLAG_INVERTED = 0x00000002;
    private const uint PEN_FLAG_ERASER = 0x00000004;
    private const uint PEN_MASK_PRESSURE = 0x00000001;

    // ---- Win32 结构 ----

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

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_PEN_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint penFlags;
        public uint penMask;
        public uint pressure;
        public uint rotation;
        public int tiltX;
        public int tiltY;
    }

    /// <summary>POINTER_TYPE_INFO：type + 联合体(touchInfo/penInfo)，x64 下总长 152。</summary>
    [StructLayout(LayoutKind.Explicit, Size = 152)]
    private struct POINTER_TYPE_INFO
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public POINTER_TOUCH_INFO touchInfo;
        [FieldOffset(8)] public POINTER_PEN_INFO penInfo;
    }

    // ---- 新版合成指针 API（首选） ----

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateSyntheticPointerDevice(uint pointerType, uint maxCount, uint mode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool InjectSyntheticPointerInput(IntPtr device, [In] POINTER_TYPE_INFO[] pointerInfo, uint count);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void DestroySyntheticPointerDevice(IntPtr device);

    // ---- 旧版触摸注入 API（回退，不支持笔） ----

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool InitializeTouchInjection(uint maxCount, uint dwMode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool InjectTouchInput(uint count, [In] POINTER_TOUCH_INFO[] contacts);

    private readonly record struct PointerState(POINT Pos, bool IsPen);

    private readonly uint _maxContacts;
    private readonly FeedbackMode _feedback;
    private readonly bool _preferLegacy;
    private readonly double? _fixedPressure;
    private readonly int _contactFallbackPx;
    private readonly double _contactScale;
    private readonly TouchInputType _inputType;
    private readonly double _pressureMax;
    private readonly double _pressureFloor;
    private readonly ContactAreaMode _contactArea;
    private readonly bool _dryRun;

    private readonly Dictionary<byte, PointerState> _lastPos = new();
    private IntPtr _deviceTouch;
    private IntPtr _devicePen;
    private bool _usingSynthetic;
    private bool _initialized;
    private uint _frameId;
    private bool _disposed;
    private bool _warnedPenUnsupported;

    public string LastErrorText { get; private set; } = "";
    public string ApiName => _usingSynthetic ? "SyntheticPointer" : "TouchInjection(旧)";
    public long InjectedFrames { get; private set; }
    public long FailedFrames { get; private set; }
    public int ActiveCount => _lastPos.Count;

    /// <summary>最近一帧首指针的输入摘要（单行，配合 --show-input 打印）。</summary>
    public string LastInputSummary => _lastSummary;

    /// <summary>最近一帧的详细调试信息（多行，供屏幕左上角调试小窗显示）。</summary>
    public string DebugText => _debugText;

    private volatile string _lastSummary = "(无)";
    private volatile string _debugText = "(等待输入)";

    public TouchInjector(
        uint maxContacts,
        FeedbackMode feedback,
        double? fixedPressure,
        int contactFallbackPx,
        bool preferLegacy = false,
        double contactScale = 1.0,
        TouchInputType inputType = TouchInputType.Auto,
        double pressureMax = 0,
        ContactAreaMode contactArea = ContactAreaMode.Auto,
        bool dryRun = false,
        double pressureFloor = 0)
    {
        _maxContacts = maxContacts;
        _feedback = feedback;
        _preferLegacy = preferLegacy;
        _fixedPressure = fixedPressure;
        _contactFallbackPx = contactFallbackPx;
        _contactScale = contactScale > 0 ? contactScale : 1.0;
        _inputType = inputType;
        _pressureMax = pressureMax;
        _pressureFloor = Math.Clamp(pressureFloor, 0, 1024);
        _contactArea = contactArea;
        _dryRun = dryRun;
    }

    public bool Initialize()
    {
        if (_initialized)
            return true;

        if (!_preferLegacy)
        {
            _deviceTouch = CreateSyntheticPointerDevice(PT_TOUCH, _maxContacts, (uint)_feedback);
            if (_deviceTouch != IntPtr.Zero)
            {
                // 笔设备必须单独创建：注入时设备类型要与指针类型一致，
                // 用触摸设备注入笔会直接报 ERROR_INVALID_PARAMETER。
                // 另外笔是单点设备，maxCount 只能传 1（传 10 同样报参数错误）。
                _devicePen = CreateSyntheticPointerDevice(PT_PEN, 1, (uint)_feedback);
                _usingSynthetic = true;
                _initialized = true;
                return true;
            }

            int err = Marshal.GetLastWin32Error();
            LastErrorText = $"CreateSyntheticPointerDevice 失败：{new Win32Exception(err).Message}";
        }

        if (InitializeTouchInjection(_maxContacts, (uint)_feedback))
        {
            _usingSynthetic = false;
            _initialized = true;
            return true;
        }

        string legacyErr = new Win32Exception(Marshal.GetLastWin32Error()).Message;
        LastErrorText = _preferLegacy
            ? $"InitializeTouchInjection 失败：{legacyErr}"
            : $"{LastErrorText}；回退 InitializeTouchInjection 也失败：{legacyErr}";
        return false;
    }

    /// <summary>注入一帧。frame.Points 为该事件中的全部活动指针。</summary>
    public void Apply(TouchFrame frame, ScreenMapper mapper)
    {
        if (!_initialized)
            return;

        if (frame.Action == Protocol.ActionCancel)
        {
            ReleaseAll(canceled: true);
            return;
        }

        int count = frame.Points.Length;
        if (count == 0)
            return;

        _frameId++;

        var infos = new POINTER_TYPE_INFO[count];
        int n = 0;

        for (int i = 0; i < count; i++)
        {
            TouchPointData p = frame.Points[i];
            bool isPen = ResolvePen(p.ToolType);
            POINT pos;
            uint flags;
            bool lifting = false;

            switch (p.State)
            {
                case Protocol.StateDown:
                    pos = mapper.Map(p.X, p.Y);
                    _lastPos[p.Id] = new PointerState(pos, isPen);
                    flags = POINTER_FLAG_NEW | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | POINTER_FLAG_DOWN;
                    break;

                case Protocol.StateUp:
                    // Windows 要求抬起的这一帧坐标必须与上一帧相同，否则注入会失败。
                    if (_lastPos.TryGetValue(p.Id, out PointerState st))
                    {
                        pos = st.Pos;
                        isPen = st.IsPen;
                    }
                    else
                    {
                        pos = mapper.Map(p.X, p.Y);
                    }
                    flags = POINTER_FLAG_INRANGE | POINTER_FLAG_UP;
                    lifting = true;
                    break;

                default: // StateUpdate
                    pos = mapper.Map(p.X, p.Y);
                    bool known = _lastPos.TryGetValue(p.Id, out PointerState prev);
                    if (known)
                        isPen = prev.IsPen;
                    _lastPos[p.Id] = new PointerState(pos, isPen);
                    // 断线重连后可能直接收到 MOVE，此时把未知指针当作 DOWN 处理。
                    flags = known
                        ? POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | POINTER_FLAG_UPDATE
                        : POINTER_FLAG_NEW | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | POINTER_FLAG_DOWN;
                    break;
            }

            infos[n++] = BuildPointer(p, pos, flags, mapper, primary: i == 0, isPen: isPen);

            if (lifting)
                _lastPos.Remove(p.Id);

            if (i == 0)
                UpdateDebugInfo(frame, p, mapper, isPen, count);
        }

        Inject(infos, n);
    }

    /// <summary>抬起当前记录的所有接触点（断开连接 / 退出时调用，避免触摸卡住）。</summary>
    public void ReleaseAll(bool canceled = false)
    {
        if (!_initialized || _lastPos.Count == 0)
            return;

        _frameId++;
        var infos = new POINTER_TYPE_INFO[_lastPos.Count];
        int n = 0;

        foreach (KeyValuePair<byte, PointerState> kv in _lastPos)
        {
            uint flags = POINTER_FLAG_INRANGE | POINTER_FLAG_UP
                | (canceled ? POINTER_FLAG_CANCELED : 0)
                | (n == 0 ? POINTER_FLAG_PRIMARY | POINTER_FLAG_CONFIDENCE : 0);

            var info = new POINTER_TYPE_INFO();
            if (kv.Value.IsPen)
            {
                info.type = PT_PEN;
                info.penInfo = new POINTER_PEN_INFO
                {
                    pointerInfo = MakePointerInfo(kv.Key, PT_PEN, flags, kv.Value.Pos),
                    penFlags = PEN_FLAG_NONE,
                    penMask = 0,
                };
            }
            else
            {
                info.type = PT_TOUCH;
                info.touchInfo = new POINTER_TOUCH_INFO
                {
                    pointerInfo = MakePointerInfo(kv.Key, PT_TOUCH, flags, kv.Value.Pos),
                    touchMask = 0,
                };
            }
            infos[n++] = info;
        }

        Inject(infos, n);
        _lastPos.Clear();
    }

    private bool ResolvePen(byte toolType)
    {
        if (!_usingSynthetic || _devicePen == IntPtr.Zero)
        {
            bool wantPen = _inputType == TouchInputType.Pen || toolType == ToolStylus || toolType == ToolEraser;
            if (!_warnedPenUnsupported && wantPen)
            {
                _warnedPenUnsupported = true;
                Console.WriteLine("    提示：当前环境不支持笔注入，已退化为触摸（多数应用不会使用触摸压感）。");
            }
            return false;
        }

        return _inputType switch
        {
            TouchInputType.Touch => false,
            TouchInputType.Pen => true,
            _ => toolType == ToolStylus || toolType == ToolEraser,
        };
    }

    /// <summary>
    /// 平板上报的原始压力 → Windows 的 0~1024。
    /// - 默认「归一化 + 下限」：输出 = 下限 + (1024-下限) × clamp(原始/量程)，即 0~量程 → 下限~1024。
    ///   例如量程 14、下限 256 → 原始 0 得 256、原始 14 得 1024。
    /// - 量程填 0 则「直通」：原始值原样（只做 0~1024 裁剪），不归一化。
    /// </summary>
    private uint ToWindowsPressure(double raw)
    {
        double value;
        if (_pressureMax > 0)
        {
            double t = raw / _pressureMax;
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            value = _pressureFloor + (1024.0 - _pressureFloor) * t;
        }
        else
        {
            value = raw;
        }

        int v = (int)Math.Round(value, MidpointRounding.AwayFromZero);
        if (v < 0) v = 0;
        if (v > 1024) v = 1024;
        return (uint)v;
    }

    public string PressureScaleLabel => _pressureMax > 0
        ? $"归一化(原始 0~{_pressureMax:0.##} → {_pressureFloor:0}~1024)"
        : "直通(不归一)";

    /// <summary>
    /// 算出最终要注入的接触长宽（屏幕像素）。
    /// FromSize=true 表示面积取自平板上报的 size（按 major:minor 比例拆分，保证 W×H = size×倍率²）。
    /// </summary>
    private (double W, double H, bool FromSize) ComputeContactPx(TouchPointData p, ScreenMapper mapper)
    {
        (double cw, double ch) = mapper.ScaleContact(p.ContactW, p.ContactH);

        // 只要报了 size 就按 size 定面积；长宽比优先取 major:minor，
        // 端未报接触尺寸（cw/ch 为 0）时退化为正方形，避免"有 size 却算不出面积"。
        bool fromSize = _contactArea != ContactAreaMode.Major && p.Size > 0;
        if (fromSize)
        {
            double ratio = cw > 0 && ch > 0 ? cw / ch : 1.0;
            double area = p.Size * _contactScale * _contactScale;      // 目标面积
            cw = Math.Sqrt(area * ratio);
            ch = Math.Sqrt(area / ratio);
        }
        else
        {
            cw *= _contactScale;
            ch *= _contactScale;
        }

        if (cw < 1.0 && ch < 1.0 && _contactFallbackPx > 0)
            cw = ch = _contactFallbackPx;

        return (cw, ch, fromSize);
    }

    public string ContactAreaLabel => _contactArea == ContactAreaMode.Major
        ? "按 touchMajor/minor 包围盒"
        : "自动（平板报了 size 就按 size 定面积，W×H=size）";

    private POINTER_TYPE_INFO BuildPointer(TouchPointData p, POINT pos, uint flags, ScreenMapper mapper, bool primary, bool isPen)
    {
        uint f = flags | (primary ? POINTER_FLAG_PRIMARY | POINTER_FLAG_CONFIDENCE : 0);
        double pressure = _fixedPressure ?? p.Pressure;

        var info = new POINTER_TYPE_INFO();

        if (isPen)
        {
            info.type = PT_PEN;
            uint penFlags = p.ToolType == ToolEraser ? (PEN_FLAG_INVERTED | PEN_FLAG_ERASER) : PEN_FLAG_NONE;
            // 先算输出值再决定是否置掩码：配了下限时原始 0 也会有压力值
            uint penPressure = ToWindowsPressure(pressure);
            uint penMask = penPressure > 0 ? PEN_MASK_PRESSURE : 0;

            info.penInfo = new POINTER_PEN_INFO
            {
                pointerInfo = MakePointerInfo(p.Id, PT_PEN, f, pos),
                penFlags = penFlags,
                penMask = penMask,
                pressure = penPressure,
            };
        }
        else
        {
            uint mask = 0;
            uint touchPressure = ToWindowsPressure(pressure);
            if (touchPressure > 0)
                mask |= TOUCH_MASK_PRESSURE;

            (double cw, double ch, _) = ComputeContactPx(p, mapper);

            RECT contact = default;
            int orientationDeg = -1;

            if (cw >= 1.0 && ch >= 1.0)
            {
                mask |= TOUCH_MASK_CONTACTAREA;
                int halfW = Math.Max(1, (int)Math.Round(cw / 2.0));
                int halfH = Math.Max(1, (int)Math.Round(ch / 2.0));
                contact = new RECT
                {
                    Left = pos.X - halfW,
                    Top = pos.Y - halfH,
                    Right = pos.X + halfW,
                    Bottom = pos.Y + halfH,
                };

                if (!float.IsNaN(p.OrientationDeg))
                {
                    mask |= TOUCH_MASK_ORIENTATION;
                    orientationDeg = (int)Math.Round(p.OrientationDeg) % 360;
                    if (orientationDeg < 0) orientationDeg += 360;
                }
            }

            info.type = PT_TOUCH;
            info.touchInfo = new POINTER_TOUCH_INFO
            {
                pointerInfo = MakePointerInfo(p.Id, PT_TOUCH, f, pos),
                touchFlags = 0,
                touchMask = mask,
                rcContact = contact,
                rcContactRaw = contact,
                orientation = orientationDeg >= 0 ? (uint)orientationDeg : 0,
                pressure = touchPressure,
            };
        }

        return info;
    }

    private POINTER_INFO MakePointerInfo(byte id, uint type, uint flags, POINT pos) => new()
    {
        pointerType = type,
        pointerId = id,
        frameId = _frameId,
        pointerFlags = flags,
        ptPixelLocation = pos,
        historyCount = 1,
    };

    private void UpdateDebugInfo(TouchFrame frame, TouchPointData p, ScreenMapper mapper, bool isPen, int pointerCount)
    {
        string tool = ToolName(p.ToolType);
        double pressure = _fixedPressure ?? p.Pressure;
        int pressureU = (int)ToWindowsPressure(pressure);
        string scaleLabel = _fixedPressure is null ? PressureScaleLabel : "固定压感";

        string area;
        if (isPen)
        {
            area = "笔无面积";
        }
        else
        {
            (double w, double h, bool fromSize) = ComputeContactPx(p, mapper);
            area = $"{w:0}x{h:0}px";
            if (fromSize)
                area += $"  (面积←size {p.Size:0.##}, W×H={w * h:0})";
            else if (p.Size > 0)
                area += $"  (size {p.Size:0.##} 未用)";
        }

        POINT sp = mapper.Map(p.X, p.Y);

        _lastSummary = $"工具={tool} 压力原始={p.Pressure:0.00}({scaleLabel}→{pressureU}) 面积={area}";

        _debugText = string.Join(Environment.NewLine,
            $"点数    : {pointerCount}    动作={ActionName(frame.Action)}",
            $"工具    : {tool}    → 注入为 {(isPen ? "笔" : "触摸")}",
            $"坐标    : ({sp.X}, {sp.Y})    归一化 ({p.X:0.000}, {p.Y:0.000})",
            $"压力    : 原始 {p.Pressure:0.00}   {scaleLabel}   → {pressureU} / 1024",
            $"接触尺寸: {area}",
            $"序号    : {frame.Sequence}");
    }

    private static string ToolName(byte toolType) => toolType switch
    {
        ToolStylus => "笔",
        ToolMouse => "鼠标",
        ToolEraser => "橡皮",
        ToolFinger => "手指",
        _ => $"未知({toolType})",
    };

    private static string ActionName(byte action) => action switch
    {
        Protocol.ActionDown => "单指按下",
        Protocol.ActionUp => "单指抬起",
        Protocol.ActionMove => "移动",
        Protocol.ActionCancel => "取消",
        Protocol.ActionPointerDown => "多指按下",
        Protocol.ActionPointerUp => "多指抬起",
        _ => action.ToString(),
    };

    private void Inject(POINTER_TYPE_INFO[] infos, int count)
    {
        if (count <= 0)
            return;

        if (_dryRun)
        {
            // 只算不注入：用于在不影响桌面的前提下核对坐标/面积/压力
            InjectedFrames++;
            return;
        }

        bool ok;
        if (_usingSynthetic)
        {
            int penCount = 0;
            for (int i = 0; i < count; i++)
            {
                if (infos[i].type == PT_PEN)
                    penCount++;
            }
            int touchCount = count - penCount;

            if (penCount > 0 && touchCount > 0)
            {
                // 同一帧里混有触摸和笔：分别投到各自的设备。
                var touchBuf = new POINTER_TYPE_INFO[touchCount];
                var penBuf = new POINTER_TYPE_INFO[penCount];
                int ti = 0, pi = 0;
                for (int i = 0; i < count; i++)
                {
                    if (infos[i].type == PT_PEN) penBuf[pi++] = infos[i];
                    else touchBuf[ti++] = infos[i];
                }
                bool okTouch = InjectSyntheticPointerInput(_deviceTouch, touchBuf, (uint)touchCount);
                bool okPen = InjectSyntheticPointerInput(_devicePen, penBuf, (uint)penCount);
                ok = okTouch && okPen;
            }
            else if (penCount > 0)
            {
                POINTER_TYPE_INFO[] payload = count == infos.Length ? infos : infos[..count];
                ok = InjectSyntheticPointerInput(_devicePen, payload, (uint)count);
            }
            else
            {
                POINTER_TYPE_INFO[] payload = count == infos.Length ? infos : infos[..count];
                ok = InjectSyntheticPointerInput(_deviceTouch, payload, (uint)count);
            }
        }
        else
        {
            // 旧 API 只支持触摸（ResolvePen 已保证不会走这里传笔）
            var legacy = new POINTER_TOUCH_INFO[count];
            for (int i = 0; i < count; i++)
                legacy[i] = infos[i].touchInfo;
            ok = InjectTouchInput((uint)count, legacy);
        }

        if (ok)
        {
            InjectedFrames++;
        }
        else
        {
            FailedFrames++;
            LastErrorText = new Win32Exception(Marshal.GetLastWin32Error()).Message;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try { ReleaseAll(); } catch { /* 忽略退出阶段错误 */ }

        if (_usingSynthetic)
        {
            if (_deviceTouch != IntPtr.Zero)
            {
                try { DestroySyntheticPointerDevice(_deviceTouch); } catch { /* 忽略 */ }
            }
            if (_devicePen != IntPtr.Zero)
            {
                try { DestroySyntheticPointerDevice(_devicePen); } catch { /* 忽略 */ }
            }
        }
        _deviceTouch = IntPtr.Zero;
        _devicePen = IntPtr.Zero;
    }
}
