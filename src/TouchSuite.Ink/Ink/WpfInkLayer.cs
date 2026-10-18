using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TouchSuite.Ink;

/// <summary>
/// 回退后端：DComp 初始化失败时，把同一套笔画画在 WPF 层上（与工具栏同窗口）。
/// 只是为了"还能用"，不是主设计 —— 它跑在 UI 线程，所以不走命令队列。
/// </summary>
internal sealed class WpfInkLayer : IInkLayer
{
    private sealed record WpfStroke(int Id, List<InkSample> Points, Polyline Line, Color Color, double Width);

    private readonly Canvas _canvas;
    private readonly List<WpfStroke> _strokes = new();
    private readonly Dictionary<int, WpfStroke> _active = new();
    private readonly Stack<(IReadOnlyList<WpfStroke> Added, IReadOnlyList<WpfStroke> Removed)> _undo = new();

    /// <summary>回退层的失败原因固定为 DComp 的失败原因，由外面填。</summary>
    public string LastError { get; set; } = "";

    public WpfInkLayer(Canvas canvas) => _canvas = canvas;

    public string BackendName => "WPF 回退层（DComp 不可用时的降级渲染）";

    public bool Initialize() => true;

    private static Color ToColor(InkSample s) => Color.FromArgb(s.A, s.R, s.G, s.B);

    public void Begin(int id, InkSample sample)
    {
        var line = new Polyline { StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        var stroke = new WpfStroke(id, new List<InkSample> { sample }, line, ToColor(sample), sample.Width);
        Apply(line, stroke.Color, stroke.Width);
        line.Points.Add(new Point(sample.X, sample.Y));
        _canvas.Children.Add(line);
        _strokes.Add(stroke);
        _active[id] = stroke;
        _undo.Push((new[] { stroke }, Array.Empty<WpfStroke>()));
    }

    public void Extend(int id, InkSample sample)
    {
        if (!_active.TryGetValue(id, out WpfStroke? stroke))
            return;
        stroke.Points.Add(sample);
        stroke.Line.Points.Add(new Point(sample.X, sample.Y));
    }

    public void End(int id) => _active.Remove(id);

    public void Erase(float x, float y, float radius)
    {
        var removed = new List<WpfStroke>();
        for (int i = _strokes.Count - 1; i >= 0; i--)
        {
            WpfStroke st = _strokes[i];
            bool hit = false;
            foreach (InkSample p in st.Points)
            {
                float dx = p.X - x, dy = p.Y - y;
                if (dx * dx + dy * dy <= (radius + p.Width) * (radius + p.Width))
                {
                    hit = true;
                    break;
                }
            }
            if (!hit)
                continue;
            removed.Add(st);
            _strokes.RemoveAt(i);
            _active.Remove(st.Id);
            _canvas.Children.Remove(st.Line);
        }
        if (removed.Count > 0)
            _undo.Push((Array.Empty<WpfStroke>(), removed));
    }

    public void Undo()
    {
        if (_undo.Count == 0)
            return;
        (IReadOnlyList<WpfStroke> added, IReadOnlyList<WpfStroke> removed) = _undo.Pop();
        foreach (WpfStroke st in removed)
        {
            _strokes.Add(st);
            if (!_canvas.Children.Contains(st.Line))
                _canvas.Children.Add(st.Line);
        }
        foreach (WpfStroke st in added)
        {
            _strokes.Remove(st);
            _canvas.Children.Remove(st.Line);
        }
    }

    public void Clear()
    {
        if (_strokes.Count == 0)
            return;
        var all = new List<WpfStroke>(_strokes);
        _strokes.Clear();
        _active.Clear();
        foreach (WpfStroke st in all)
            _canvas.Children.Remove(st.Line);
        _undo.Push((Array.Empty<WpfStroke>(), all));
    }

    public void Resize(int width, int height) { /* WPF 层随窗口自动布局，无需处理 */ }

    public void Dispose() { }

    private static void Apply(Polyline line, Color color, double width)
    {
        line.Stroke = new SolidColorBrush(color);
        line.StrokeThickness = Math.Max(1.0, width);
        line.SnapsToDevicePixels = false;
    }
}
