using System.Windows;

namespace TouchErase.Helpers;

/// <summary>
/// 方案「三、核心代码实现」中的几何算法集合。
/// 全部基于 WPF 的 DIU（device-independent unit）坐标系，与 InkCanvas 一致。
/// </summary>
public static class Geometry2D
{
    public static double Distance(Point a, Point b)
        => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    // ---------- 1. Ramer-Douglas-Peucker 去抖 ----------

    public static List<Point> DouglasPeucker(IReadOnlyList<Point> points, double epsilon)
    {
        int n = points.Count;
        if (n < 3)
            return new List<Point>(points);

        var keep = new bool[n];
        keep[0] = true;
        keep[n - 1] = true;

        var stack = new Stack<(int start, int end)>();
        stack.Push((0, n - 1));
        while (stack.Count > 0)
        {
            var (start, end) = stack.Pop();
            double maxDist = 0;
            int index = -1;
            for (int i = start + 1; i < end; i++)
            {
                double d = PerpendicularDistance(points[i], points[start], points[end]);
                if (d > maxDist)
                {
                    maxDist = d;
                    index = i;
                }
            }

            if (maxDist > epsilon && index != -1)
            {
                keep[index] = true;
                stack.Push((start, index));
                stack.Push((index, end));
            }
        }

        var result = new List<Point>();
        for (int i = 0; i < n; i++)
            if (keep[i])
                result.Add(points[i]);
        return result;
    }

    private static double PerpendicularDistance(Point p, Point a, Point b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9)
            return Distance(p, a);
        return Math.Abs(dy * p.X - dx * p.Y + b.X * a.Y - b.Y * a.X) / len;
    }

    // ---------- 2. Andrew 单调链凸包 O(n log n) ----------

    public static List<Point> ConvexHull(IReadOnlyList<Point> pts)
    {
        var p = pts.OrderBy(q => q.X).ThenBy(q => q.Y).ToList();
        if (p.Count <= 2)
            return p;

        var lower = new List<Point>();
        foreach (var q in p)
        {
            while (lower.Count >= 2 && Cross(lower[^2], lower[^1], q) <= 0)
                lower.RemoveAt(lower.Count - 1);
            lower.Add(q);
        }

        var upper = new List<Point>();
        for (int i = p.Count - 1; i >= 0; i--)
        {
            var q = p[i];
            while (upper.Count >= 2 && Cross(upper[^2], upper[^1], q) <= 0)
                upper.RemoveAt(upper.Count - 1);
            upper.Add(q);
        }

        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);
        return lower;
    }

    private static double Cross(Point o, Point a, Point b)
        => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

    // ---------- 3. Shoelace 多边形面积 ----------

    public static double PolygonArea(IReadOnlyList<Point> poly)
    {
        int n = poly.Count;
        if (n < 3)
            return 0;
        double s = 0;
        for (int i = 0; i < n; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % n];
            s += a.X * b.Y - b.X * a.Y;
        }
        return Math.Abs(s) / 2.0;
    }
}
