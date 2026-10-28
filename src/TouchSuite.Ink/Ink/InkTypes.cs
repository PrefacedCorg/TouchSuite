using System.Collections.Concurrent;

namespace TouchSuite.Ink;

/// <summary>落笔工具（工具栏切换）。</summary>
internal enum InkToolKind
{
    Pen,
    Eraser,
}

/// <summary>
/// 一个采样点。位置是屏幕物理像素、原样不滤波；宽度已含压感与工具粗细。
/// <para><see cref="Ticks"/> 是采样时刻（<c>Stopwatch.GetTimestamp()</c> 的 QPC 计数），
/// 只给笔迹预测估速度用；手填的测试样本可以留 0，此时不预测。</para>
/// </summary>
internal readonly record struct InkSample(float X, float Y, float Width, byte R, byte G, byte B, byte A, long Ticks = 0);

/// <summary>矩形（屏幕物理像素，左闭右开）。</summary>
internal readonly record struct RectI(int L, int T, int R, int B)
{
    public static RectI Empty => new(int.MaxValue, int.MaxValue, int.MinValue, int.MinValue);
    public bool IsEmpty => R <= L || B <= T;
    public static RectI Union(RectI a, RectI b) =>
        new(Math.Min(a.L, b.L), Math.Min(a.T, b.T), Math.Max(a.R, b.R), Math.Max(a.B, b.B));
    public static RectI Around(float x, float y, float r) =>
        new((int)MathF.Floor(x - r - 2), (int)MathF.Floor(y - r - 2),
            (int)MathF.Ceiling(x + r + 2), (int)MathF.Ceiling(y + r + 2));
    public RectI Clamp(int w, int h)
    {
        int l = Math.Clamp(L, 0, w - 1), t = Math.Clamp(T, 0, h - 1);
        int r = Math.Clamp(R, l + 1, w), b = Math.Clamp(B, t + 1, h);
        return new RectI(l, t, r, b);
    }
    public bool Intersects(RectI o) => L < o.R && o.L < R && T < o.B && o.T < B;
}

/// <summary>一支墨迹（源数据，渲染线程独占）。</summary>
internal sealed class InkStroke
{
    public required int Id { get; init; }
    public required List<InkSample> Points { get; init; }
    public RectI Bounds { get; set; } = RectI.Empty;

    /// <summary>位置模型（弹簧 + wobble + 固定间隔重采样）。关掉平滑或没有时间戳时为 null，直接用原始点。</summary>
    public StrokeModeler? Modeler { get; init; }

    public InkSample Last => Points[^1];
}

/// <summary>撤销栈的一条记录。</summary>
internal sealed record UndoEntry(IReadOnlyList<InkStroke> Added, IReadOnlyList<InkStroke> Removed);

/// <summary>喂给渲染线程的命令（不可变，跨线程传递，所以不会共享可变状态）。</summary>
internal abstract record InkCommand;
internal sealed record CmdBegin(int Id, InkSample S) : InkCommand;
internal sealed record CmdExtend(int Id, InkSample S) : InkCommand;
internal sealed record CmdEnd(int Id) : InkCommand;
internal sealed record CmdErase(float X, float Y, float Radius) : InkCommand;
internal sealed record CmdUndo : InkCommand;
internal sealed record CmdClear : InkCommand;
internal sealed record CmdResize(int Width, int Height) : InkCommand;
internal sealed record CmdShutdown : InkCommand;

/// <summary>墨迹层抽象：湿墨渲染后端的接口。UI 只往这里塞命令，不碰渲染细节。</summary>
internal interface IInkLayer : IDisposable
{
    /// <summary>后端名（写进标题栏，便于一眼看出是不是真跑在 DComp 上）。</summary>
    string BackendName { get; }

    /// <summary>初始化失败的原因（成功时为空）。</summary>
    string LastError { get; }

    bool Initialize();

    void Begin(int id, InkSample sample);
    void Extend(int id, InkSample sample);
    void End(int id);
    void Erase(float x, float y, float radius);
    void Undo();
    void Clear();
    void Resize(int width, int height);
}

/// <summary>把命令排队交给渲染线程的公共底座。</summary>
internal abstract class InkLayerBase : IInkLayer
{
    protected readonly BlockingCollection<InkCommand> Queue = new();
    private int _nextId;

    public abstract string BackendName { get; }
    public abstract string LastError { get; }
    public abstract bool Initialize();

    public int AllocateId() => Interlocked.Increment(ref _nextId);

    public void Begin(int id, InkSample sample) => Post(new CmdBegin(id, sample));
    public void Extend(int id, InkSample sample) => Post(new CmdExtend(id, sample));
    public void End(int id) => Post(new CmdEnd(id));
    public void Erase(float x, float y, float radius) => Post(new CmdErase(x, y, radius));
    public void Undo() => Post(new CmdUndo());
    public void Clear() => Post(new CmdClear());
    public void Resize(int width, int height) => Post(new CmdResize(width, height));

    protected void Post(InkCommand cmd)
    {
        try { Queue.Add(cmd); }
        catch (InvalidOperationException) { /* 队列已关闭（退出中） */ }
    }

    public virtual void Dispose()
    {
        try { Queue.Add(new CmdShutdown()); } catch (InvalidOperationException) { /* 忽略 */ }
        try { Queue.CompleteAdding(); } catch { /* 忽略 */ }
        GC.SuppressFinalize(this);
    }
}
