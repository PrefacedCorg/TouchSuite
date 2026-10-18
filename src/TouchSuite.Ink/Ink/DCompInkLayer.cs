using System.Runtime.InteropServices;
using static TouchSuite.Ink.Gfx;

namespace TouchSuite.Ink;

/// <summary>
/// 底层墨迹层：DirectComposition 表面 + Direct2D 绘制，跑在自己的渲染线程上。
///
/// <para>结构：表面是"持久位图"，每帧只把受影响的脏矩形交给 BeginDraw（它会把该矩形清成透明），
/// 然后把与该矩形相交的所有笔画重画一遍 —— 这样新笔迹是增量的，而擦除/撤销这类结构性操作
/// 只需要把受影响区域标脏即可，不用维护增量/全量两套逻辑。</para>
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
    private ID3D11Device? _d3dDevice;
    private IntPtr _d3dContext;          // 裸指针：UpdateSubresource 走 vtable 槽位调用
    private IDCompositionDevice? _device;
    private IntPtr _target;
    private IntPtr _visual;
    private IntPtr _surface;

    // BeginDraw 探针定下来的绘制通路
    private Guid _beginIid;
    private bool _useD3D;                // true = IDXGISurface + D3D11 上传；false = D2D 印章

    // CPU 侧 BGRA 预乘缓冲（按脏矩形大小复用，行距 = 宽 × 4）
    private uint[] _scratch = Array.Empty<uint>();
    private GCHandle _scratchPin;
    private int _scratchW, _scratchH;

    private readonly List<InkStroke> _strokes = new();
    private readonly Stack<UndoEntry> _undo = new();
    private readonly Dictionary<int, InkStroke> _active = new();
    private RectI _dirty = RectI.Empty;
    private long _frames;
    private bool _loggedFirstFrame;

    public DCompInkLayer(IntPtr hwnd, int originX, int originY, int width, int height)
    {
        _hwnd = hwnd;
        _originX = originX;
        _originY = originY;
        _width = width;
        _height = height;
    }

    public override string BackendName => _useD3D
        ? "DirectComposition 表面 + D3D11 上传（DWM 合成，低延迟）"
        : "DirectComposition 表面 + D2D（DWM 合成）";
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
                InkCommand cmd = Queue.Take();
                Process(cmd);
                while (Queue.TryTake(out InkCommand? more))
                    Process(more!);
                if (_shutdown)
                    break;
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
            D3D11_SDK_VERSION, out ID3D11Device d3d, out int level, out IntPtr d3dCtx);
        Log.Hr($"D3D11CreateDevice(硬件, FL=0x{level:X})", hr);
        if (hr < 0)
        {
            _error = $"D3D11CreateDevice 失败 (0x{hr:X8})";
            return false;
        }
        _d3dDevice = d3d;
        _d3dContext = d3dCtx;

        var dxgi = (IDXGIDevice)d3d;

        Guid iidDevice = IID_IDCompositionDevice;
        hr = DCompositionCreateDevice(dxgi, ref iidDevice, out object deviceObj);
        Log.Hr("DCompositionCreateDevice", hr);
        if (hr < 0)
        {
            _error = $"DCompositionCreateDevice 失败 (0x{hr:X8})";
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

        // IDCompositionVisual::SetContent（槽位 11）
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
    /// 用 1×1 的小更新区依次探 BeginDraw 的候选 IID，命中即定下本后端的绘制通路。
    /// 首选 IID_IDXGISurface：换出可写 DXGI 表面 → QI 出 ID3D11Texture2D → 之后用 D3D11 上传像素。
    /// 只有它拿不到时才退到 D2D 印章式绘制。两条都失败就返回 false，让上层回退 WPF 层。
    /// </summary>
    private bool ProbeBeginDraw()
    {
        IntPtr rectMem = Marshal.AllocHGlobal(Marshal.SizeOf<RECT>());
        try
        {
            Marshal.StructureToPtr(new RECT(0, 0, _width, _height), rectMem, false);
            var beginDraw = Vtbl.Slot<Vtbl.DWithGuidRef>(_surface, Gfx.SlotSurfaceBeginDraw);

            foreach ((Guid iid, bool isD3D) in Gfx.BeginDrawCandidates)
            {
                Guid candidate = iid;
                int hr = beginDraw(_surface, rectMem, ref candidate, out IntPtr obj, out _);
                Log.Write($"探针 BeginDraw(整面, IID={iid}) hr=0x{hr:X8}");
                if (hr < 0 || obj == IntPtr.Zero)
                    continue;

                if (isD3D)
                {
                    // 走 D3D 上传的前提是能 QI 出纹理，拿不到就换下一个候选。
                    bool ok = TryQiTexture(obj, out IntPtr tex);
                    if (ok)
                        Marshal.Release(tex);
                    Marshal.Release(obj);
                    if (!ok)
                        continue;
                }
                else
                {
                    Marshal.Release(obj);
                }

                _beginIid = iid;
                _useD3D = isD3D;
                var endDraw = Vtbl.Slot<Vtbl.D0>(_surface, Gfx.SlotSurfaceEndDraw);
                Log.Hr("探针 EndDraw", endDraw(_surface));
                _device!.Commit();
                Log.Write($"BeginDraw 通路 = {(_useD3D ? "IDXGISurface + D3D11 上传" : "D2D 印章")}");
                return true;
            }

            _error = "IDCompositionSurface::BeginDraw 拿不到绘图上下文";
            Log.Write("探针失败：" + _error);
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(rectMem);
        }
    }

    /// <summary>把 BeginDraw 换出的 IDXGISurface 转成 ID3D11Texture2D（QQI 走 vtable 槽位 0，避开 IID 属性问题）。</summary>
    private static bool TryQiTexture(IntPtr dxgiSurface, out IntPtr texture)
    {
        Guid iid = IID_ID3D11Texture2D;
        var queryInterface = Vtbl.Slot<Vtbl.QueryInterface>(dxgiSurface, 0);
        int hr = queryInterface(dxgiSurface, ref iid, out texture);
        Log.Hr("IDXGISurface → ID3D11Texture2D", hr);
        if (hr < 0 || texture == IntPtr.Zero)
        {
            texture = IntPtr.Zero;
            return false;
        }
        return true;
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
                var stroke = new InkStroke { Id = b.Id, Points = new List<InkSample> { b.S } };
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
                stroke.Points.Add(e.S);
                stroke.Bounds = RectI.Union(stroke.Bounds, e.S.BoundsAt());
                MarkDirty(RectI.Union(stroke.Points[^2].BoundsAt(), e.S.BoundsAt()));
                break;
            }
            case CmdEnd end:
                _active.Remove(end.Id);
                break;
            case CmdErase er:
            {
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
            int hr = beginDraw(_surface, rectMem, ref iid, out IntPtr updateObject, out POINT offset);
            if (hr < 0 || updateObject == IntPtr.Zero)
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
                        + $"updateOffset=({offset.X},{offset.Y}) 笔画={_strokes.Count} 通路={(_useD3D ? "D3D11 上传" : "D2D 印章")}");
            }

            try
            {
                if (_useD3D)
                    UploadD3D(updateObject, update, offset);
                else
                    StampAll(updateObject, update, offset);
            }
            catch (Exception ex)
            {
                Log.Write($"绘制异常：{ex}");
            }
            finally
            {
                Marshal.Release(updateObject);   // 更新对象必须在 EndDraw 之前释放
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

    // ================= 通路一：D3D11 上传（低延迟，首选） =================

    /// <summary>
    /// 在 CPU 上把更新区光栅成 BGRA 预乘位图，再用 UpdateSubresource 拷进 BeginDraw 换出的纹理。
    ///
    /// <para><b>关键</b>：BeginDraw 返回的纹理是<b>整面</b>大小（实测 1696×1056，比 1680×1050 略大），
    /// 而 EndDraw 时 DComp 只把<b>更新区那一块</b>回拷到表面。所以 box 必须是该更新区在<b>纹理=表面</b>
    /// 坐标系下的矩形，绝不能减去 updateOffset —— 否则像素会写到纹理左上角，而那块永远不在回拷范围内，
    /// 结果就是"局部更新一帧都看不见"。</para>
    /// </summary>
    private void UploadD3D(IntPtr dxgiSurface, RectI region, POINT offset)
    {
        if (!TryQiTexture(dxgiSurface, out IntPtr texture))
            return;

        try
        {
            int dw = region.R - region.L;
            int dh = region.B - region.T;
            int pixels = dw * dh;
            if (_scratch.Length < pixels)
            {
                if (_scratchPin.IsAllocated)
                    _scratchPin.Free();
                _scratch = new uint[pixels];
                _scratchPin = GCHandle.Alloc(_scratch, GCHandleType.Pinned);
            }
            _scratchW = dw;
            _scratchH = dh;
            // BeginDraw 已经把更新区清成透明，本地缓冲也从全透明开始，再叠加笔画。
            Array.Clear(_scratch, 0, pixels);

            RasterizeStrokes(region);

            var box = new D3D11_BOX
            {
                Left = (uint)region.L,
                Top = (uint)region.T,
                Front = 0,
                Right = (uint)region.R,
                Bottom = (uint)region.B,
                Back = 1,
            };
            if (_frames == 0)
                Log.Write($"[上传] UpdateSubresource box=({box.Left},{box.Top},{box.Right},{box.Bottom}) 缓冲={dw}x{dh} "
                        + $"updateOffset=({offset.X},{offset.Y})");

            IntPtr boxMem = Marshal.AllocHGlobal(Marshal.SizeOf<D3D11_BOX>());
            try
            {
                Marshal.StructureToPtr(box, boxMem, false);
                var update = Vtbl.Slot<Vtbl.UpdateSubresource>(_d3dContext, Gfx.Ctx.UpdateSubresource);
                update(_d3dContext, texture, 0, boxMem, _scratchPin.AddrOfPinnedObject(), (uint)(dw * 4), 0);

                // UpdateSubresource 只是把上传命令排进 D3D11 命令队列；不主动 Flush，这批像素要等
                // 队列下次刷新才真正落到纹理上 —— 表现就是"刚画的墨看不见，等下一下才蹦出来"。
                var flush = Vtbl.Slot<Vtbl.V0>(_d3dContext, Gfx.Ctx.Flush);
                flush(_d3dContext);
            }
            finally
            {
                Marshal.FreeHGlobal(boxMem);
            }
        }
        finally
        {
            Marshal.Release(texture);
        }
    }

    /// <summary>把与该更新区相交的笔画盖进 _scratch（预乘 BGRA，行距 = 宽 × 4）。</summary>
    private void RasterizeStrokes(RectI region)
    {
        foreach (InkStroke st in _strokes)
        {
            if (!st.Bounds.Intersects(region))
                continue;

            List<InkSample> pts = st.Points;
            if (pts.Count == 1)
            {
                StampBuffer(pts[0], pts[0], 0f, region);
                continue;
            }

            for (int i = 0; i + 1 < pts.Count; i++)
            {
                InkSample a = pts[i], b = pts[i + 1];
                float dist = MathF.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                float minR = MathF.Max(0.6f, MathF.Min(a.Width, b.Width) * 0.5f);
                int steps = Math.Clamp((int)MathF.Ceiling(dist / (minR * 0.6f)), 1, 96);
                for (int k = 0; k <= steps; k++)
                    StampBuffer(a, b, (float)k / steps, region);
            }
        }
    }

    /// <summary>在 (a,b) 之间按 t 插值盖一个抗锯齿圆，做预乘 alpha 的 source-over 混合。</summary>
    private void StampBuffer(InkSample a, InkSample b, float t, RectI region)
    {
        float sx = a.X + (b.X - a.X) * t;      // 屏幕物理像素
        float sy = a.Y + (b.Y - a.Y) * t;
        float radius = (a.Width + (b.Width - a.Width) * t) * 0.5f;
        if (radius < 0.35f)
            return;

        // 屏幕 → 缓冲：先减窗口原点，再减更新区左上角。
        float cx = sx - _originX - region.L;
        float cy = sy - _originY - region.T;
        float edge = radius + 0.5f;            // 半径外再留半个像素做抗锯齿羽化

        int x0 = Math.Max(0, (int)MathF.Floor(cx - edge));
        int x1 = Math.Min(_scratchW - 1, (int)MathF.Ceiling(cx + edge));
        int y0 = Math.Max(0, (int)MathF.Floor(cy - edge));
        int y1 = Math.Min(_scratchH - 1, (int)MathF.Ceiling(cy + edge));
        if (x0 > x1 || y0 > y1)
            return;

        const float Inv255 = 1f / 255f;
        float alpha = a.A * Inv255;
        float pr = a.R * alpha, pg = a.G * alpha, pb = a.B * alpha;

        uint[] buf = _scratch;
        int w = _scratchW;
        for (int y = y0; y <= y1; y++)
        {
            float dy = y + 0.5f - cy;
            int row = y * w;
            for (int x = x0; x <= x1; x++)
            {
                float dx = x + 0.5f - cx;
                float coverage = edge - MathF.Sqrt(dx * dx + dy * dy);
                if (coverage <= 0f)
                    continue;
                if (coverage > 1f)
                    coverage = 1f;

                float srcA = alpha * coverage;
                float keep = 1f - srcA;
                uint dst = buf[row + x];
                float dr = (dst >> 16) & 0xFF, dg = (dst >> 8) & 0xFF, db = dst & 0xFF, da = (dst >> 24) & 0xFF;

                float nr = pr * coverage + dr * keep;
                float ng = pg * coverage + dg * keep;
                float nb = pb * coverage + db * keep;
                float na = srcA * 255f + da * keep;

                buf[row + x] = (ToByte(na) << 24) | (ToByte(nr) << 16) | (ToByte(ng) << 8) | ToByte(nb);
            }
        }
    }

    private static uint ToByte(float v) => (uint)Math.Clamp((int)(v + 0.5f), 0, 255);

    // ================= 通路二：D2D 印章（备选） =================

    private void StampAll(IntPtr renderTarget, RectI region, POINT offset)
    {
        // 屏幕像素 → 表面绘制坐标：先减窗口原点，再减本次更新区在表面内的偏移。
        float dx = -(_originX + offset.X);
        float dy = -(_originY + offset.Y);
        foreach (InkStroke st in _strokes)
        {
            if (st.Bounds.Intersects(region))
                StampStroke(renderTarget, st, dx, dy);
        }
    }

    /// <summary>
    /// 印章式笔刷：在相邻采样之间按半径插值盖圆。不用 ID2D1PathGeometry，
    /// 少一大片 vtable 依赖；圆自带抗锯齿，观感够用。
    /// </summary>
    private static void StampStroke(IntPtr renderTarget, InkStroke st, float dx, float dy)
    {
        List<InkSample> pts = st.Points;
        InkSample first = pts[0];
        var color = new D2D1_COLOR_F(first.R / 255f, first.G / 255f, first.B / 255f, first.A / 255f);

        var createBrush = Vtbl.Slot<Vtbl.CreateBrush>(renderTarget, Gfx.SlotRtCreateSolidColorBrush);
        int hr = createBrush(renderTarget, ref color, IntPtr.Zero, out IntPtr brush);
        if (hr < 0 || brush == IntPtr.Zero)
        {
            Log.Hr("CreateSolidColorBrush", hr);
            return;
        }

        try
        {
            var fillEllipse = Vtbl.Slot<Vtbl.FillEllipse>(renderTarget, Gfx.SlotRtFillEllipse);

            if (pts.Count == 1)
            {
                StampCircle(fillEllipse, renderTarget, brush, first, first, 0f, dx, dy);
                return;
            }

            for (int i = 0; i + 1 < pts.Count; i++)
            {
                InkSample a = pts[i], b = pts[i + 1];
                float dist = MathF.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                float minR = MathF.Max(0.6f, MathF.Min(a.Width, b.Width) * 0.5f);
                int steps = Math.Clamp((int)MathF.Ceiling(dist / (minR * 0.6f)), 1, 96);
                for (int k = 0; k <= steps; k++)
                    StampCircle(fillEllipse, renderTarget, brush, a, b, (float)k / steps, dx, dy);
            }
        }
        finally
        {
            Marshal.Release(brush);
        }
    }

    private static void StampCircle(Vtbl.FillEllipse fillEllipse, IntPtr rt, IntPtr brush,
        InkSample a, InkSample b, float t, float dx, float dy)
    {
        float x = a.X + (b.X - a.X) * t + dx;
        float y = a.Y + (b.Y - a.Y) * t + dy;
        float r = (a.Width + (b.Width - a.Width) * t) * 0.5f;
        if (r < 0.35f)
            return;
        var ellipse = new D2D1_ELLIPSE(x, y, r, r);
        fillEllipse(rt, ref ellipse, brush);
    }

    // ================= 清理 =================

    private void ReleaseAll()
    {
        Vtbl.Release(_surface);
        Vtbl.Release(_visual);
        Vtbl.Release(_target);
        _surface = IntPtr.Zero;
        _visual = IntPtr.Zero;
        _target = IntPtr.Zero;

        if (_device is not null) { try { Marshal.ReleaseComObject(_device); } catch { } _device = null; }
        if (_d3dContext != IntPtr.Zero) { Vtbl.Release(_d3dContext); _d3dContext = IntPtr.Zero; }
        if (_d3dDevice is not null) { try { Marshal.ReleaseComObject(_d3dDevice); } catch { } _d3dDevice = null; }

        if (_scratchPin.IsAllocated)
            _scratchPin.Free();
        _scratch = Array.Empty<uint>();
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
