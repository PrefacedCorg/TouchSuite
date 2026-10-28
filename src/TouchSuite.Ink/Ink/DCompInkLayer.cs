using System.Diagnostics;
using System.Runtime.InteropServices;
using static TouchSuite.Ink.Gfx;

namespace TouchSuite.Ink;

/// <summary>
/// 底层墨迹层：DirectComposition 表面 + Direct2D 绘制，跑在自己的渲染线程上。
///
/// <para>结构：表面是"持久位图"，每帧只把受影响的脏矩形交给 BeginDraw（换出一块临时渲染目标），
/// 清成透明后把与该矩形相交的所有笔画重画一遍 —— 这样新笔迹是增量的，而擦除/撤销这类结构性操作
/// 只需要把受影响区域标脏即可，不用维护增量/全量两套逻辑。</para>
///
/// <para><b>纯 GPU</b>：这里没有任何 CPU 像素循环，也不做 CPU→GPU 上传。笔迹是 D2D 的
/// <c>FillEllipse</c> 原语，光栅化全在 GPU 上，CPU 只负责发命令；合成由 DWM/DComp 完成。
/// 要注意 <c>IDCompositionSurface::BeginDraw</c> 只有对"带 D2D 设备的 DComp 设备"才允许要
/// <c>ID2D1DeviceContext</c>，所以设备必须用 <c>DCompositionCreateDevice2(ID2D1Device)</c> 创建
/// （老的 <c>DCompositionCreateDevice(IDXGIDevice)</c> 会 E_INVALIDARG）。</para>
///
/// <para>线程：UI 线程只往 BlockingCollection 里塞不可变命令；渲染线程独占笔画模型、D2D 资源与表面。
/// 两边没有共享可变状态，也不需要锁。</para>
///
/// <para>IID 策略：IDCompositionDevice 的 IID 已验证可用；IDCompositionVisual/Target/Surface 的 IID
/// 对不上（实测 E_NOINTERFACE），所以这三个以及 D2D 的渲染目标一律用裸指针 + vtable 槽位调用，
/// 见 <see cref="Vtbl"/> 与 <see cref="Gfx"/> 里的 Slot* 常量。</para>
/// </summary>
internal sealed class DCompInkLayer : InkLayerBase
{
    private const uint COINIT_MULTITHREADED = 0;

    /// <summary>更新区最小边长：实测 1×1 这种退化矩形会被 DComp 直接判 E_INVALIDARG，所以先撑大再交上去。</summary>
    private const int MinUpdateSide = 32;

    /// <summary>两次呈现的最小间隔（≈144Hz）。见 RenderLoop 里的呈现节流。</summary>
    private static readonly long PresentIntervalTicks = Stopwatch.Frequency * 7 / 1000;

    /// <summary>
    /// 实时笔锋（照抄 Inkeys 3 的 <c>ApplyLiveTipTaper</c>，Draw3.StrokeGeometry.cpp）。
    /// <para>收锋长度不是固定像素，而是"最后 <see cref="LiveTailSeconds"/> 秒"这段的弧长 ——
    /// 于是长度天然 = 速度 × 时间窗：写得快尾巴长、写得慢尾巴短、停住再抬几乎没有尾巴。
    /// 尖端收窄多少也按这段的长度 / 笔半径决定，短尾巴只轻微收，长尾巴收到
    /// <see cref="TaperTipScaleMax"/>。</para>
    /// </summary>
    private const double LiveTailSeconds = 0.055;   // Inkeys Fps60 profile 的 live_tail_duration_seconds
    private const float TaperTipScaleMax = 0.3f;    // 窗口够长（≥4 倍笔半径）时的尖端比例

    /// <summary>笔尖附近要一起重画的采样数（收锋会改动这一段的形状，必须一起标脏）。</summary>
    private const int TipDirtySamples = 8;

    /// <summary>
    /// 墨迹预测的提前量（秒）。Inkeys 的 Fps60 profile 用 1/45s ≈ 22ms。
    /// 预测点从<b>模型笔尖</b>沿模型速度外推，而不是拿最近两个原始采样算差分。
    /// </summary>
    private const double PredictLeadSeconds = 1.0 / 45.0;

    /// <summary>预测置信度的速度区间（px/s）：取 Inkeys/Chromium 的 5%~25% × 期望速度 500px/s。</summary>
    private const float PredictMinSpeed = 25f;
    private const float PredictMaxSpeed = 125f;

    /// <summary>预测段长度上限（px）：速度再快也不甩出去。</summary>
    private const float PredictMaxPx = 40f;

    /// <summary>预测点迟迟没有新采样跟上时，等这么久就撤掉（否则手指停住后墨迹会"甩"在前头）。</summary>
    private const int PredictExpireMs = 70;

    private long _lastPresentStamp;

    private const int D2D1_ANTIALIAS_MODE_ALIASED = 1;

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    private readonly IntPtr _hwnd;
    private readonly int _originX, _originY;
    private int _width, _height;

    private readonly ManualResetEventSlim _initDone = new(false);
    private Thread? _thread;
    private volatile bool _ready;
    private volatile string _error = "";
    private bool _shutdown;

    // ---- 以下字段只在渲染线程上访问 ----
    private ID3D11Device? _d3dDevice;    // 只用来给 ID2D1Factory1::CreateDevice 提供 IDXGIDevice
    private IDCompositionDevice? _device;
    private IntPtr _target;
    private IntPtr _visual;
    private IntPtr _surface;
    private IntPtr _strokeStyle;   // 圆头圆角画笔样式（全局一份，设备无关）

    /// <summary>模型输出缓冲（只有渲染线程用，复用以免每帧分配）。</summary>
    private readonly List<InkSample> _modeled = new();

    /// <summary>探针定下来的绘制目标 IID（ID2D1DeviceContext 优先）。</summary>
    private Guid _beginIid;

    private readonly List<InkStroke> _strokes = new();
    private readonly Stack<UndoEntry> _undo = new();
    private readonly Dictionary<int, InkStroke> _active = new();
    private RectI _dirty = RectI.Empty;
    private long _frames;
    private bool _loggedFirstFrame;

    // ---- 湿墨的预测尾巴（只在渲染线程访问）----
    private bool _predictActive;
    private InkSample _predictColor;
    private float _predictX0, _predictY0, _predictX1, _predictY1;
    private float _predictW0, _predictW1;
    private RectI _predictBounds = RectI.Empty;

    public DCompInkLayer(IntPtr hwnd, int originX, int originY, int width, int height)
    {
        _hwnd = hwnd;
        _originX = originX;
        _originY = originY;
        _width = width;
        _height = height;
    }

    public override string BackendName => "DirectComposition 表面 + D2D（GPU 光栅化，DWM 合成）";
    public override string LastError => _error;
    public bool IsReady => _ready;

    public override bool Initialize()
    {
        Log.Write($"DCompInkLayer: hwnd=0x{_hwnd.ToInt64():X} 原点=({_originX},{_originY}) 尺寸={_width}x{_height}");
        _thread = new Thread(RenderLoop) { IsBackground = true, Name = "Ink-Render" };
        _thread.Start();

        if (!_initDone.Wait(TimeSpan.FromSeconds(8)))
        {
            _error = "渲染线程初始化超时（8 秒）";
            Log.Write("DCompInkLayer: " + _error);
            return false;
        }
        return _ready;
    }

    // ================= 渲染线程 =================

    private void RenderLoop()
    {
        try
        {
            Log.Hr("CoInitializeEx(MTA)", CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED));

            if (CreateResources())
                _ready = true;
            _initDone.Set();
            if (!_ready)
                return;

            while (true)
            {
                // 有预测尾巴时不能无限期等：超时说明手指没再动，要把预测撤掉并重画一帧
                if (_predictActive)
                {
                    if (Queue.TryTake(out InkCommand? pended, PredictExpireMs))
                        Process(pended!);
                    else
                        ClearPrediction();
                }
                else
                {
                    Process(Queue.Take());
                }

                while (Queue.TryTake(out InkCommand? more))
                    Process(more!);
                if (_shutdown)
                    break;

                // 呈现节流：显示刷新只有 60~144Hz，同一帧里多提交几次不会更早被看到，
                // 只会让渲染线程被"每个输入样本一次 BeginDraw/EndDraw/Commit"拖满，
                // 反过来把墨迹越拖越靠后。等够一帧的同时把新到的样本一并吃掉，再只呈现最新状态。
                long due = _lastPresentStamp + PresentIntervalTicks;
                while (true)
                {
                    long remain = due - Stopwatch.GetTimestamp();
                    if (remain <= 0)
                        break;
                    int ms = (int)Math.Ceiling(remain * 1000.0 / Stopwatch.Frequency);
                    if (!Queue.TryTake(out InkCommand? newer, ms))
                        break;
                    Process(newer!);
                }
                if (_shutdown)
                    break;

                _lastPresentStamp = Stopwatch.GetTimestamp();   // 从呈现开始算间隔，别把呈现本身的耗时也算进去
                Present();
            }
        }
        catch (InvalidOperationException)
        {
            // CompleteAdding 之后 Take 会抛，属正常退出路径
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            Log.Write($"DCompInkLayer 渲染线程异常：{ex}");
            _initDone.Set();
        }
        finally
        {
            ReleaseAll();
            Log.Write($"DCompInkLayer: 渲染线程退出，共呈现 {_frames} 帧");
        }
    }

    private bool CreateResources()
    {
        int hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, FeatureLevels, (uint)FeatureLevels.Length,
            D3D11_SDK_VERSION, out ID3D11Device d3d, out int level, out _);
        Log.Hr($"D3D11CreateDevice(硬件, FL=0x{level:X})", hr);
        if (hr < 0)
        {
            _error = $"D3D11CreateDevice 失败 (0x{hr:X8})";
            return false;
        }
        _d3dDevice = d3d;

        if (!CreateD2DDevice(d3d, out IntPtr d2dDevice))
            return false;

        Guid iidDevice = IID_IDCompositionDevice;
        hr = DCompositionCreateDevice2(d2dDevice, ref iidDevice, out object deviceObj);
        Log.Hr("DCompositionCreateDevice2(ID2D1Device)", hr);
        Vtbl.Release(d2dDevice);          // DComp 设备自己会持有渲染设备，这里放掉我们这份引用
        if (hr < 0)
        {
            _error = $"DCompositionCreateDevice2 失败 (0x{hr:X8})";
            return false;
        }
        _device = (IDCompositionDevice)deviceObj;

        hr = _device.CreateTargetForHwnd(_hwnd, true, out _target);
        Log.Hr($"CreateTargetForHwnd(topmost=true) ptr=0x{_target.ToInt64():X}", hr);
        if (hr < 0 || _target == IntPtr.Zero)
        {
            _error = $"CreateTargetForHwnd 失败 (0x{hr:X8})";
            return false;
        }

        hr = _device.CreateVisual(out _visual);
        Log.Hr($"CreateVisual ptr=0x{_visual.ToInt64():X}", hr);
        if (hr < 0 || _visual == IntPtr.Zero)
        {
            _error = $"CreateVisual 失败 (0x{hr:X8})";
            return false;
        }

        if (!CreateSurface())
            return false;

        // IDCompositionTarget::SetRoot（槽位 3）
        var setRoot = Vtbl.Slot<Vtbl.D1>(_target, Gfx.SlotTargetSetRoot);
        hr = setRoot(_target, _visual);
        Log.Hr("Target::SetRoot(visual)", hr);

        // IDCompositionVisual::SetContent（槽位 15）
        var setContent = Vtbl.Slot<Vtbl.D1>(_visual, Gfx.SlotVisualSetContent);
        hr = setContent(_visual, _surface);
        Log.Hr("Visual::SetContent(surface)", hr);
        if (hr < 0)
        {
            _error = $"Visual::SetContent 失败 (0x{hr:X8})";
            return false;
        }

        hr = _device.Commit();
        Log.Hr("IDCompositionDevice::Commit", hr);
        if (hr < 0)
        {
            _error = $"Commit 失败 (0x{hr:X8})";
            return false;
        }

        // 探针：真正试一次 BeginDraw。只有它能成功才算这条后端可用 ——
        // 否则 Initialize 直接失败让上层回退到 WPF 层，避免出现"初始化成功但一帧都画不出来"的黑洞。
        if (!ProbeBeginDraw())
            return false;

        _dirty = new RectI(0, 0, _width, _height);   // 首帧把整面清干净
        return true;
    }

    /// <summary>
    /// D3D11 设备 → IDXGIDevice → ID2D1Factory1::CreateDevice → ID2D1Device。
    /// DComp 设备必须由这个 ID2D1Device 创建，BeginDraw 才愿意给 ID2D1DeviceContext。
    /// </summary>
    private bool CreateD2DDevice(ID3D11Device d3d, out IntPtr d2dDevice)
    {
        d2dDevice = IntPtr.Zero;

        Guid iidFactory = IID_ID2D1Factory1;
        int hr = D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, ref iidFactory, IntPtr.Zero, out IntPtr factory);
        Log.Hr($"D2D1CreateFactory(ID2D1Factory1) ptr=0x{factory.ToInt64():X}", hr);
        if (hr < 0 || factory == IntPtr.Zero)
        {
            _error = $"D2D1CreateFactory 失败 (0x{hr:X8})";
            return false;
        }

        IntPtr dxgiDevice = IntPtr.Zero;
        try
        {
            dxgiDevice = QueryDxgiDevice(d3d);
            if (dxgiDevice == IntPtr.Zero)
            {
                _error = "ID3D11Device 拿不到 IDXGIDevice";
                return false;
            }

            var createDevice = Vtbl.Slot<Vtbl.FactoryCreateDevice>(factory, Gfx.Factory.CreateDevice);
            hr = createDevice(factory, dxgiDevice, out IntPtr created);
            Log.Hr($"ID2D1Factory1::CreateDevice ptr=0x{created.ToInt64():X}", hr);
            if (hr < 0 || created == IntPtr.Zero)
            {
                _error = $"ID2D1Factory1::CreateDevice 失败 (0x{hr:X8})";
                return false;
            }
            d2dDevice = created;
            _strokeStyle = CreateRoundStrokeStyle(factory);
            return true;
        }
        finally
        {
            Vtbl.Release(dxgiDevice);
            Vtbl.Release(factory);
        }
    }

    /// <summary>圆头 + 圆角的画笔样式。笔迹被拆成很多段 DrawLine，平头端点会在拐点处露缺口（看起来"断开"）。</summary>
    private static IntPtr CreateRoundStrokeStyle(IntPtr factory)
    {
        var props = Gfx.RoundStrokeStyle;
        var create = Vtbl.Slot<Vtbl.FactoryCreateStrokeStyle>(factory, Gfx.Factory.CreateStrokeStyle);
        int hr = create(factory, ref props, IntPtr.Zero, 0, out IntPtr style);
        Log.Hr($"ID2D1Factory::CreateStrokeStyle(圆头圆角) ptr=0x{style.ToInt64():X}", hr);
        return hr < 0 ? IntPtr.Zero : style;
    }

    /// <summary>ID3D11Device 裸指针 QI 出 IDXGIDevice（调用方负责 Release）。</summary>
    private static IntPtr QueryDxgiDevice(ID3D11Device d3d)
    {
        IntPtr unknown = Marshal.GetIUnknownForObject(d3d);
        try
        {
            Guid iid = IID_IDXGIDevice;
            var queryInterface = Vtbl.Slot<Vtbl.QueryInterface>(unknown, 0);
            int hr = queryInterface(unknown, ref iid, out IntPtr dxgiDevice);
            Log.Hr("ID3D11Device → IDXGIDevice", hr);
            return hr < 0 ? IntPtr.Zero : dxgiDevice;
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    /// <summary>
    /// 依次用候选 IID 试一次 BeginDraw，命中即定下绘制目标类型。
    /// 两条都失败就返回 false，让上层回退 WPF 层（说明这台机器拿不到 GPU 绘制上下文）。
    /// </summary>
    private bool ProbeBeginDraw()
    {
        IntPtr rectMem = Marshal.AllocHGlobal(Marshal.SizeOf<RECT>());
        try
        {
            Marshal.StructureToPtr(new RECT(0, 0, _width, _height), rectMem, false);
            var beginDraw = Vtbl.Slot<Vtbl.DWithGuidRef>(_surface, Gfx.SlotSurfaceBeginDraw);

            foreach (Guid candidate in Gfx.BeginDrawCandidates)
            {
                Guid iid = candidate;
                int hr = beginDraw(_surface, rectMem, ref iid, out IntPtr obj, out _);
                Log.Write($"探针 BeginDraw(整面, IID={candidate}) hr=0x{hr:X8}");
                if (hr < 0 || obj == IntPtr.Zero)
                    continue;

                Marshal.Release(obj);
                _beginIid = candidate;
                var endDraw = Vtbl.Slot<Vtbl.D0>(_surface, Gfx.SlotSurfaceEndDraw);
                Log.Hr("探针 EndDraw", endDraw(_surface));
                _device!.Commit();
                Log.Write("BeginDraw 通路 = D2D（GPU 光栅化）");
                return true;
            }

            _error = "IDCompositionSurface::BeginDraw 拿不到 D2D 绘制目标";
            Log.Write("探针失败：" + _error);
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(rectMem);
        }
    }

    private bool CreateSurface()
    {
        int hr = _device!.CreateSurface((uint)_width, (uint)_height,
            DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_ALPHA_MODE_PREMULTIPLIED, out IntPtr surface);
        Log.Hr($"CreateSurface({_width}x{_height}, B8G8R8A8 预乘) ptr=0x{surface.ToInt64():X}", hr);
        if (hr < 0 || surface == IntPtr.Zero)
        {
            _error = $"CreateSurface 失败 (0x{hr:X8})";
            return false;
        }
        Vtbl.Release(_surface);
        _surface = surface;
        return true;
    }

    // ================= 命令处理 =================

    private void Process(InkCommand cmd)
    {
        switch (cmd)
        {
            case CmdBegin b:
            {
                ClearPrediction();
                // 有平滑开关 + 有时间戳才建模；否则（自检/合成样本）直接用原始点
                StrokeModeler? modeler = null;
                if (InkSettings.Smoothing && b.S.Ticks != 0)
                {
                    modeler = new StrokeModeler();
                    modeler.Reset(b.S);
                }
                var stroke = new InkStroke { Id = b.Id, Points = new List<InkSample> { b.S }, Modeler = modeler };
                stroke.Bounds = b.S.BoundsAt();
                _strokes.Add(stroke);
                _active[b.Id] = stroke;
                _undo.Push(new UndoEntry(new[] { stroke }, Array.Empty<InkStroke>()));
                MarkDirty(stroke.Bounds);
                break;
            }
            case CmdExtend e:
            {
                if (!_active.TryGetValue(e.Id, out InkStroke? stroke))
                    break;

                RectI appended = RectI.Empty;
                if (stroke.Modeler is { } modeler)
                {
                    _modeled.Clear();
                    if (modeler.Update(e.S, _modeled) == 0)
                    {
                        // 时间戳异常等：退回原始点，保证笔画不断
                        stroke.Points.Add(e.S);
                        appended = e.S.BoundsAt();
                        stroke.Bounds = RectI.Union(stroke.Bounds, appended);
                    }
                    else
                    {
                        foreach (InkSample p in _modeled)
                        {
                            stroke.Points.Add(p);
                            RectI b = p.BoundsAt();
                            appended = RectI.Union(appended, b);
                        }
                        stroke.Bounds = RectI.Union(stroke.Bounds, appended);
                    }
                }
                else
                {
                    stroke.Points.Add(e.S);
                    appended = e.S.BoundsAt();
                    stroke.Bounds = RectI.Union(stroke.Bounds, appended);
                }

                // 收锋会改动"笔尖附近"那几段，尾部若干采样一起标脏，否则旧笔尖会留成一条细线
                MarkDirty(RectI.Union(TailBounds(stroke), appended));
                UpdatePrediction(stroke);
                break;
            }
            case CmdEnd end:
            {
                ClearPrediction();
                if (!_active.Remove(end.Id, out InkStroke? done))
                    break;

                StrokeModeler? modeler = done.Modeler;

                // 抬笔时"距最后一次输入"过了多久：收锋长度按它衰减（停住再抬就没有尾巴）
                double liftAge = SecondsSince(done.Last.Ticks);

                // 弹簧笔尖会略微落后于真实抬笔点：抬笔时让它追到最后一个输入位置
                if (modeler is not null)
                {
                    _modeled.Clear();
                    if (modeler.Finish(done.Last, _modeled) > 0)
                    {
                        foreach (InkSample p in _modeled)
                        {
                            done.Points.Add(p);
                            done.Bounds = RectI.Union(done.Bounds, p.BoundsAt());
                        }
                    }
                }

                // 收锋：抬笔时一次性改末段宽度（书写过程中宽度恒定，不会出现凹点）
                ApplyTaper(done, liftAge);

                // 抬笔这一帧把**整条笔画**重画一遍：末端补齐 + 收锋 + 撤掉预测尾巴之后的最终形态
                // 必须一次提交完。只标尾部的话，收锋那几像素要等下一次有脏区时才被重画，
                // 表现就是"笔锋要等下一笔按下才显示"。
                MarkDirty(done.Bounds);
                break;
            }
            case CmdErase er:
            {
                ClearPrediction();
                var removed = new List<InkStroke>();
                for (int i = _strokes.Count - 1; i >= 0; i--)
                {
                    InkStroke st = _strokes[i];
                    if (!st.Bounds.Intersects(RectI.Around(er.X, er.Y, er.Radius)))
                        continue;
                    if (DistanceToPolyline(st, er.X, er.Y) > er.Radius + st.Last.Width / 2f)
                        continue;
                    removed.Add(st);
                    _strokes.RemoveAt(i);
                    _active.Remove(st.Id);
                    MarkDirty(st.Bounds);
                }
                if (removed.Count > 0)
                    _undo.Push(new UndoEntry(Array.Empty<InkStroke>(), removed));
                break;
            }
            case CmdUndo:
            {
                ClearPrediction();
                if (_undo.Count == 0)
                    break;
                UndoEntry entry = _undo.Pop();
                foreach (InkStroke st in entry.Removed)     // 撤销"擦除" → 放回去
                {
                    _strokes.Add(st);
                    MarkDirty(st.Bounds);
                }
                foreach (InkStroke st in entry.Added)       // 撤销"新增" → 拿掉
                {
                    _strokes.Remove(st);
                    MarkDirty(st.Bounds);
                }
                break;
            }
            case CmdClear:
            {
                ClearPrediction();
                if (_strokes.Count == 0)
                    break;
                var all = new List<InkStroke>(_strokes);
                foreach (InkStroke st in _strokes)
                    MarkDirty(st.Bounds);
                _strokes.Clear();
                _active.Clear();
                _undo.Push(new UndoEntry(Array.Empty<InkStroke>(), all));
                break;
            }
            case CmdResize rs:
            {
                _width = Math.Max(1, rs.Width);
                _height = Math.Max(1, rs.Height);
                if (CreateSurface())
                {
                    var setContent = Vtbl.Slot<Vtbl.D1>(_visual, Gfx.SlotVisualSetContent);
                    setContent(_visual, _surface);
                    _device?.Commit();
                    _dirty = new RectI(0, 0, _width, _height);
                }
                break;
            }
            case CmdShutdown:
                _shutdown = true;
                break;
        }
    }

    private void MarkDirty(RectI r)
    {
        if (!r.IsEmpty)
            _dirty = RectI.Union(_dirty, r);
    }

    /// <summary>笔尖附近若干采样的包围盒（平滑 + 收锋会重画这一段）。</summary>
    private static RectI TailBounds(InkStroke st)
    {
        List<InkSample> pts = st.Points;
        int from = Math.Max(0, pts.Count - TipDirtySamples);
        RectI r = RectI.Empty;
        for (int i = from; i < pts.Count; i++)
            r = RectI.Union(r, pts[i].BoundsAt());
        return r;
    }

    // ================= 墨迹预测（湿墨尾巴） =================

    /// <summary>
    /// 预测笔尖：从<b>模型笔尖</b>沿<b>模型速度</b>外推 <see cref="PredictLeadSeconds"/>，
    /// 只画在湿墨最前面，来了新采样就被替换。
    /// <para>置信度按速度在 <see cref="PredictMinSpeed"/>~<see cref="PredictMaxSpeed"/> 之间插值
    /// （Inkeys/Chromium 的 Kalman confidence 用的也是这两个比例）：手慢/手停时提前量自动归零，
    /// 不会出现"墨甩在手指前面"。</para>
    /// </summary>
    private void UpdatePrediction(InkStroke st)
    {
        if (!InkSettings.Prediction || st.Modeler is not { HasVelocity: true } modeler)
        {
            ClearPrediction();
            return;
        }

        MarkDirty(_predictBounds);        // 旧的预测段一并擦掉
        _predictBounds = RectI.Empty;
        _predictActive = false;

        float speed = modeler.Speed;
        if (speed < PredictMinSpeed)
            return;

        float conf = Math.Clamp((speed - PredictMinSpeed) / (PredictMaxSpeed - PredictMinSpeed), 0f, 1f);
        float leadX = modeler.VelX * (float)PredictLeadSeconds * conf;
        float leadY = modeler.VelY * (float)PredictLeadSeconds * conf;

        float leadLen = MathF.Sqrt(leadX * leadX + leadY * leadY);
        if (leadLen < 0.5f)
            return;
        if (leadLen > PredictMaxPx)
        {
            leadX *= PredictMaxPx / leadLen;
            leadY *= PredictMaxPx / leadLen;
        }

        _predictX0 = modeler.X;
        _predictY0 = modeler.Y;
        _predictX1 = _predictX0 + leadX;
        _predictY1 = _predictY0 + leadY;
        _predictW0 = WidthAt(st.Points, st.Points.Count - 1);   // 接上收锋之后的笔尖宽度，视觉上连成一条
        _predictW1 = _predictW0 * 0.66f;
        _predictColor = st.Last;
        _predictActive = true;

        _predictBounds = SegmentBounds(_predictX0, _predictY0, _predictX1, _predictY1,
            MathF.Max(_predictW0, _predictW1));
        MarkDirty(_predictBounds);
    }

    private void ClearPrediction()
    {
        if (!_predictActive && _predictBounds.IsEmpty)
            return;
        MarkDirty(_predictBounds);
        _predictActive = false;
        _predictBounds = RectI.Empty;
    }

    private static RectI SegmentBounds(float ax, float ay, float bx, float by, float width)
    {
        float pad = width * 0.5f + 2f;
        return new RectI(
            (int)MathF.Floor(MathF.Min(ax, bx) - pad),
            (int)MathF.Floor(MathF.Min(ay, by) - pad),
            (int)MathF.Ceiling(MathF.Max(ax, bx) + pad),
            (int)MathF.Ceiling(MathF.Max(ay, by) + pad));
    }

    private static float DistanceToPolyline(InkStroke st, float x, float y)
    {
        float best = float.MaxValue;
        List<InkSample> pts = st.Points;
        if (pts.Count == 1)
            return MathF.Sqrt((pts[0].X - x) * (pts[0].X - x) + (pts[0].Y - y) * (pts[0].Y - y));
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            float d = DistanceToSegment(pts[i].X, pts[i].Y, pts[i + 1].X, pts[i + 1].Y, x, y);
            if (d < best)
                best = d;
        }
        return best;
    }

    private static float DistanceToSegment(float x1, float y1, float x2, float y2, float px, float py)
    {
        float vx = x2 - x1, vy = y2 - y1;
        float len2 = vx * vx + vy * vy;
        if (len2 <= 0.0001f)
            return MathF.Sqrt((px - x1) * (px - x1) + (py - y1) * (py - y1));
        float t = Math.Clamp(((px - x1) * vx + (py - y1) * vy) / len2, 0f, 1f);
        float cx = x1 + vx * t, cy = y1 + vy * t;
        return MathF.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
    }

    // ================= 呈现 =================

    private void Present()
    {
        if (_dirty.IsEmpty || _surface == IntPtr.Zero || _device is null)
            return;

        RectI dirty = _dirty.Clamp(_width, _height);
        _dirty = RectI.Empty;

        // 更新区不能太小：实测 1×1 这种退化矩形会被 DComp 直接判 E_INVALIDARG，撑到 MinUpdateSide 以上。
        RectI update = Grow(dirty, MinUpdateSide);

        IntPtr rectMem = Marshal.AllocHGlobal(Marshal.SizeOf<RECT>());
        try
        {
            Marshal.StructureToPtr(new RECT(update.L, update.T, update.R, update.B), rectMem, false);

            var beginDraw = Vtbl.Slot<Vtbl.DWithGuidRef>(_surface, Gfx.SlotSurfaceBeginDraw);
            Guid iid = _beginIid;
            int hr = beginDraw(_surface, rectMem, ref iid, out IntPtr renderTarget, out POINT offset);
            if (hr < 0 || renderTarget == IntPtr.Zero)
            {
                // 没画成就把原区域重新标脏 —— 否则这一帧的墨就永久丢了。
                _dirty = RectI.Union(_dirty, dirty);
                Log.Write($"BeginDraw(更新区 {update.L},{update.T},{update.R},{update.B}) 失败 hr=0x{hr:X8}，已重新标脏");
                return;
            }

            if (!_loggedFirstFrame)
            {
                _loggedFirstFrame = true;
                Log.Write($"[首帧] 脏区=({dirty.L},{dirty.T},{dirty.R},{dirty.B}) 更新区=({update.L},{update.T},{update.R},{update.B}) "
                        + $"updateOffset=({offset.X},{offset.Y}) 笔画={_strokes.Count} 通路=D2D");
            }

            try
            {
                Draw(renderTarget, update, offset);
            }
            catch (Exception ex)
            {
                Log.Write($"绘制异常：{ex}");
            }
            finally
            {
                Marshal.Release(renderTarget);   // 更新对象必须在 EndDraw 之前释放
            }

            var endDraw = Vtbl.Slot<Vtbl.D0>(_surface, Gfx.SlotSurfaceEndDraw);
            hr = endDraw(_surface);
            if (hr < 0)
                Log.Hr("Surface::EndDraw", hr);

            hr = _device.Commit();
            if (hr < 0)
                Log.Hr("Commit", hr);

            _frames++;
        }
        finally
        {
            Marshal.FreeHGlobal(rectMem);
        }
    }

    /// <summary>把矩形撑到至少 minSide×minSide，优先往有空间的一侧扩，扩到边界就不越过。</summary>
    private RectI Grow(RectI r, int minSide)
    {
        int l = r.L, t = r.T, right = r.R, b = r.B;
        while (right - l < minSide && (right < _width || l > 0))
        {
            if (right < _width)
                right++;
            else
                l--;
        }
        while (b - t < minSide && (b < _height || t > 0))
        {
            if (b < _height)
                b++;
            else
                t--;
        }
        return new RectI(l, t, right, b);
    }

    // ================= 绘制（全部交给 GPU） =================

    /// <summary>
    /// 在 BeginDraw 换出的临时渲染目标上重画更新区。
    ///
    /// <para>两条关键语义（官方文档）：① 这块临时表面<b>不含上一次的内容</b>，必须自己清干净，
    /// 否则会残留上一帧的像素；② 更新区在表面里的位置由 <paramref name="offset"/> 给出，
    /// 它不一定等于更新区左上角，所以坐标要按 offset 平移过去。EndDraw 只把更新区回拷到表面，
    /// 清到区外不会伤到已有墨迹。</para>
    /// </summary>
    private void Draw(IntPtr renderTarget, RectI update, POINT offset)
    {
        var clear = Vtbl.Slot<Vtbl.Clear>(renderTarget, Gfx.Rt.Clear);
        var transparent = new D2D1_COLOR_F(0f, 0f, 0f, 0f);
        clear(renderTarget, ref transparent);

        // 1 单位 = 1 屏幕像素，避免设备上下文自带的 DPI 把墨迹缩放画歪
        var setDpi = Vtbl.Slot<Vtbl.SetDpi>(renderTarget, Gfx.Rt.SetDpi);
        setDpi(renderTarget, 96f, 96f);

        var setTransform = Vtbl.Slot<Vtbl.SetTransform>(renderTarget, Gfx.Rt.SetTransform);
        var matrix = new D2D1_MATRIX_3X2_F
        {
            M11 = 1f,
            M22 = 1f,
            DX = offset.X - _originX - update.L,
            DY = offset.Y - _originY - update.T,
        };
        setTransform(renderTarget, ref matrix);

        // 绘制命令本身没有裁剪，这里显式裁到更新区，省掉区外那部分无用的光栅化
        var clip = new D2D1_RECT_F
        {
            Left = update.L,
            Top = update.T,
            Right = update.R,
            Bottom = update.B,
        };
        var pushClip = Vtbl.Slot<Vtbl.PushClip>(renderTarget, Gfx.Rt.PushAxisAlignedClip);
        pushClip(renderTarget, ref clip, D2D1_ANTIALIAS_MODE_ALIASED);

        var brushes = new Dictionary<uint, IntPtr>();
        try
        {
            foreach (InkStroke st in _strokes)
            {
                if (st.Bounds.Intersects(update))
                    DrawStroke(renderTarget, st, update, brushes, _strokeStyle);
            }

            // 预测笔尖画在所有真实笔迹之后（它就是最前面那一小段）
            if (_predictActive)
            {
                IntPtr predictBrush = GetBrush(renderTarget, _predictColor, brushes);
                if (predictBrush != IntPtr.Zero)
                {
                    var drawLine = Vtbl.Slot<Vtbl.DrawLine>(renderTarget, Gfx.SlotRtDrawLine);
                    StrokeSegment(drawLine, renderTarget, predictBrush,
                        _predictX0, _predictY0, _predictX1, _predictY1,
                        _predictW0, _predictW1, update, _strokeStyle);
                }
            }
        }
        finally
        {
            foreach (IntPtr brush in brushes.Values)
                Marshal.Release(brush);
        }

        var popClip = Vtbl.Slot<Vtbl.V0>(renderTarget, Gfx.Rt.PopAxisAlignedClip);
        popClip(renderTarget);
    }

    /// <summary>
    /// 印章式笔刷：沿"中点为端、原始采样为控制点"的二次贝塞尔盖圆，圆自带抗锯齿。
    ///
    /// <para><b>平滑</b>（抄 EasiNote 的 BezierInterpolation / Inkeys 的 DrawCurve 思路）：
    /// 折线直接盖章会有明显折角；把每一对相邻采样取中点当曲线端点、原采样当控制点，
    /// 得到的曲线在采样点处切线连续，视觉上就是顺的。代价是形状永远滞后一个采样 ——
    /// 所以<b>最后一段不插值</b>，直接从上一个中点画到最新的原始采样，笔尖零延迟跟手。</para>
    ///
    /// <para><b>笔锋</b>：最后 <see cref="TaperScale"/>.Length 个采样的笔宽按递减系数收窄（收锋），
    /// 于是湿墨的尖端始终是细的、笔画结束时自然带一个尖尾巴 —— 压感为常数的手指书写也有笔锋。</para>
    ///
    /// <para><b>只画落在更新区里的那几段</b>：一条长笔画可能上千个点，整条重画等于每帧按点数线性烧 CPU
    /// （实测 400 点的笔画让单帧呈现从 2ms 涨到 4ms）。按段的包围盒先筛一遍，长笔画就退化成常数级。</para>
    /// </summary>
    private static void DrawStroke(IntPtr renderTarget, InkStroke st, RectI region, Dictionary<uint, IntPtr> brushes,
        IntPtr style)
    {
        List<InkSample> pts = st.Points;
        int n = pts.Count;
        IntPtr brush = GetBrush(renderTarget, pts[0], brushes);
        if (brush == IntPtr.Zero)
            return;

        var fillEllipse = Vtbl.Slot<Vtbl.FillEllipse>(renderTarget, Gfx.SlotRtFillEllipse);
        var drawLine = Vtbl.Slot<Vtbl.DrawLine>(renderTarget, Gfx.SlotRtDrawLine);

        if (n == 1)
        {
            StampDot(fillEllipse, renderTarget, brush, pts[0].X, pts[0].Y, pts[0].Width);
            return;
        }

        // 采样点本身已经是平滑的（模型输出间隔 240Hz，约 2~3px 一段），直接连折线即可：
        // 圆头圆角的 style 保证段与段之间不会有缺口。
        for (int i = 0; i < n - 1; i++)
        {
            StrokeSegment(drawLine, renderTarget, brush,
                pts[i].X, pts[i].Y, pts[i + 1].X, pts[i + 1].Y,
                WidthAt(pts, i), WidthAt(pts, i + 1), region, style);
        }
    }

    /// <summary>第 i 个采样实际使用的笔宽。</summary>
    private static float WidthAt(List<InkSample> pts, int i) => pts[i].Width;

    /// <summary>
    /// 抬笔收锋：照抄 Inkeys 3 的 <c>ApplyLiveTipTaper</c>。
    ///
    /// <para>① 用<b>时间</b>定窗口：从末点往回取最后 <see cref="LiveTailSeconds"/> 秒那段，
    /// 于是窗口弧长 = 速度 × 时间窗（写快尾巴长、写慢尾巴短）。
    /// ② <paramref name="ageSeconds"/> = 抬笔时刻距最后一次输入的时间，停笔越久尾巴越短，
    /// 超过时间窗就完全不出尾巴（圆头收笔）。
    /// ③ 尖端比例 = 1 - 0.7 × smoothstep(窗口弧长 / (4 × 笔半径))：短尾巴只轻微收，长尾巴收到 30%。</para>
    ///
    /// <para>必须在抬笔时一次性改宽度并重画（不能每帧按"距笔尖第几个点"重算，那样离开重画窗口的
    /// 点不会被更新，宽度序列里会留下凹点）。</para>
    /// </summary>
    private static void ApplyTaper(InkStroke st, double ageSeconds)
    {
        if (!InkSettings.Taper)
            return;

        List<InkSample> pts = st.Points;
        int n = pts.Count;
        if (n < 2)
            return;

        // ① 时间窗：末点所在时间往回 LiveTailSeconds
        double endT = Seconds(pts[n - 1].Ticks);
        double startT = endT - LiveTailSeconds;
        int first = n - 1;
        while (first > 0 && Seconds(pts[first - 1].Ticks) >= startT)
            first--;

        // ② 窗口弧长 / 参考半径（窗口内最大笔宽的一半）
        float windowLength = 0f;
        float refRadius = pts[first].Width * 0.5f;
        bool hasTime = false;
        for (int i = first + 1; i < n; i++)
        {
            windowLength += Dist(pts[i - 1], pts[i]);
            refRadius = MathF.Max(refRadius, pts[i].Width * 0.5f);
            hasTime |= pts[i].Ticks != pts[i - 1].Ticks;
        }
        // 没有时间戳（合成/测试样本）：给一个中性窗口，避免退化成"完全不收锋"
        if (!hasTime)
            windowLength = MathF.Max(pts[n - 1].Width * 4f, 14f);
        if (windowLength <= 0.5f || refRadius <= 0.1f)
            return;

        // ③ 停笔时间吃掉窗口：抬笔那一刻 age≈0 就是完整窗口
        double age = Math.Clamp(ageSeconds, 0.0, LiveTailSeconds);
        float effective = windowLength * (1f - SmoothStep01((float)(age / LiveTailSeconds)));
        if (effective <= 0.5f)
            return;

        float tipScale = 1f - 0.7f * SmoothStep01(effective / (4f * refRadius));

        // ④ 沿弧长施加：收锋区之前不动
        float taperStart = windowLength - effective;
        float arc = 0f;
        for (int i = first + 1; i < n; i++)
        {
            arc += Dist(pts[i - 1], pts[i]);
            if (arc <= taperStart)
                continue;

            float progress = Math.Clamp((arc - taperStart) / effective, 0f, 1f);
            float factor = 1f + (tipScale - 1f) * SmoothStep01(progress);
            pts[i] = pts[i] with { Width = pts[i].Width * factor };
            st.Bounds = RectI.Union(st.Bounds, pts[i].BoundsAt());
        }
    }

    /// <summary>跟 Chromium/Inkeys 的 SmoothStep01 一致：clamp 后 smoothstep。</summary>
    private static float SmoothStep01(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static double Seconds(long ticks) =>
        ticks <= 0 ? 0 : ticks / (double)Stopwatch.Frequency;

    /// <summary>距某个时间戳过了多久（秒）。</summary>
    private static double SecondsSince(long ticks)
    {
        if (ticks <= 0)
            return 0;
        long now = Stopwatch.GetTimestamp();
        return now <= ticks ? 0 : (now - ticks) / (double)Stopwatch.Frequency;
    }

    private static float Dist(InkSample a, InkSample b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// 画一段直线（两端笔宽 wa/wb）。宽度按段内插值，每 ≈0.6px 的宽度变化拆一条 ——
    /// 等宽时（手指书写）整段只发一条 DrawLine。
    /// </summary>
    private static void StrokeSegment(Vtbl.DrawLine drawLine, IntPtr rt, IntPtr brush,
        float ax, float ay, float bx, float by, float wa, float wb, RectI region, IntPtr style)
    {
        if (!BoxTouches(ax, ay, bx, by, MathF.Max(wa, wb), region))
            return;

        int steps = Math.Clamp((int)MathF.Ceiling(MathF.Abs(wb - wa) / 0.6f), 1, 8);
        float px = ax, py = ay, pw = wa;
        for (int k = 1; k <= steps; k++)
        {
            float u = (float)k / steps;
            float nx = ax + (bx - ax) * u;
            float ny = ay + (by - ay) * u;
            float nw = wa + (wb - wa) * u;
            drawLine(rt, Vtbl.PackPoint(px, py), Vtbl.PackPoint(nx, ny), brush,
                MathF.Max(0.35f, (pw + nw) * 0.5f), style);
            px = nx;
            py = ny;
            pw = nw;
        }
    }

    /// <summary>线段（含两端笔宽）的包围盒是否与更新区相交。坐标是屏幕物理像素。</summary>
    private static bool BoxTouches(float ax, float ay, float bx, float by, float width, RectI region)
    {
        float pad = width * 0.5f + 1f;
        float l = MathF.Min(ax, bx) - pad, r = MathF.Max(ax, bx) + pad;
        float t = MathF.Min(ay, by) - pad, bo = MathF.Max(ay, by) + pad;
        return l < region.R && region.L < r && t < region.B && region.T < bo;
    }

    private static IntPtr GetBrush(IntPtr renderTarget, InkSample s, Dictionary<uint, IntPtr> brushes)
    {
        uint key = ((uint)s.A << 24) | ((uint)s.R << 16) | ((uint)s.G << 8) | s.B;
        if (brushes.TryGetValue(key, out IntPtr cached))
            return cached;

        var color = new D2D1_COLOR_F(s.R / 255f, s.G / 255f, s.B / 255f, s.A / 255f);
        var createBrush = Vtbl.Slot<Vtbl.CreateBrush>(renderTarget, Gfx.SlotRtCreateSolidColorBrush);
        int hr = createBrush(renderTarget, ref color, IntPtr.Zero, out IntPtr brush);
        if (hr < 0 || brush == IntPtr.Zero)
        {
            Log.Hr("CreateSolidColorBrush", hr);
            brushes[key] = IntPtr.Zero;
            return IntPtr.Zero;
        }
        brushes[key] = brush;
        return brush;
    }

    private static void StampDot(Vtbl.FillEllipse fillEllipse, IntPtr rt, IntPtr brush, float x, float y, float width)
    {
        float r = width * 0.5f;
        if (r < 0.2f)
            return;
        var ellipse = new D2D1_ELLIPSE(x, y, r, r);   // 屏幕物理像素；原点与 offset 已经由 transform 处理
        fillEllipse(rt, ref ellipse, brush);
    }

    // ================= 清理 =================

    private void ReleaseAll()
    {
        Vtbl.Release(_surface);
        Vtbl.Release(_visual);
        Vtbl.Release(_target);
        Vtbl.Release(_strokeStyle);
        _surface = IntPtr.Zero;
        _visual = IntPtr.Zero;
        _target = IntPtr.Zero;
        _strokeStyle = IntPtr.Zero;

        if (_device is not null) { try { Marshal.ReleaseComObject(_device); } catch { } _device = null; }
        if (_d3dDevice is not null) { try { Marshal.ReleaseComObject(_d3dDevice); } catch { } _d3dDevice = null; }
    }

    public override void Dispose()
    {
        base.Dispose();
        try { _thread?.Join(TimeSpan.FromSeconds(2)); } catch { /* 忽略 */ }
        _initDone.Dispose();
    }
}

internal static class InkSampleExtensions
{
    public static RectI BoundsAt(this InkSample s) => RectI.Around(s.X, s.Y, s.Width / 2f);
}
