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

    // ---------- 4a. 最小距离聚类 ----------

    public static List<List<Point>> ClusterByDistance(IReadOnlyList<Point> pts, double threshold)
    {
        var clusters = new List<List<Point>>();
        var remaining = new List<Point>(pts);
        while (remaining.Count > 0)
        {
            var seed = remaining[0];
            var cluster = new List<Point> { seed };
            remaining.RemoveAt(0);
            bool grew = true;
            while (grew)
            {
                grew = false;
                for (int i = 0; i < remaining.Count;)
                {
                    if (cluster.Any(c => Distance(c, remaining[i]) < threshold))
                    {
                        cluster.Add(remaining[i]);
                        remaining.RemoveAt(i);
                        grew = true;
                    }
                    else
                    {
                        i++;
                    }
                }
            }
            clusters.Add(cluster);
        }
        return clusters;
    }

    public static Point Centroid(IReadOnlyList<Point> pts)
    {
        if (pts.Count == 0)
            return new Point();
        double sx = 0, sy = 0;
        foreach (var p in pts)
        {
            sx += p.X;
            sy += p.Y;
        }
        return new Point(sx / pts.Count, sy / pts.Count);
    }

    // ---------- 4b. MST（Kruskal）切分，取最大连通分量 ----------

    /// <summary>
    /// 把每个簇中心作为顶点，边权为簇中心欧氏距离，Kruskal 建 MST；
    /// 删掉权值最大的 <paramref name="cutCount"/> 条边，
    /// 返回「触点总数最多」连通分量的簇下标（<paramref name="weights"/> 为每簇触点数）。
    /// </summary>
    /// <remarks>
    /// 注意：这里不能按"簇个数"排序。掌心通常是一个致密大簇（1 个节点）,
    /// 而几根手指是多个小簇，若按簇个数比大小会误把手指链判成掌心。
    /// 按触点总数比才是物理上正确的"最大簇"。
    /// </remarks>
    public static List<int> LargestComponentAfterCuts(IReadOnlyList<Point> centers,
        IReadOnlyList<int> weights, int cutCount)
    {
        int n = centers.Count;
        if (n <= 1)
            return n == 1 ? new List<int> { 0 } : new List<int>();

        var edges = new List<(double w, int a, int b)>();
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                edges.Add((Distance(centers[i], centers[j]), i, j));
        edges.Sort((e1, e2) => e1.w.CompareTo(e2.w));

        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }
        bool Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra == rb) return false;
            parent[ra] = rb;
            return true;
        }

        var mst = new List<(double w, int a, int b)>();
        foreach (var e in edges)
        {
            if (Union(e.a, e.b))
            {
                mst.Add(e);
                if (mst.Count == n - 1) break;
            }
        }

        var removed = mst.OrderByDescending(e => e.w).Take(Math.Max(0, cutCount)).ToHashSet();

        for (int i = 0; i < n; i++) parent[i] = i;
        foreach (var e in mst)
        {
            if (removed.Contains(e)) continue;
            Union(e.a, e.b);
        }

        var groups = new Dictionary<int, List<int>>();
        for (int i = 0; i < n; i++)
        {
            int r = Find(i);
            if (!groups.TryGetValue(r, out var list))
            {
                list = new List<int>();
                groups[r] = list;
            }
            list.Add(i);
        }

        return groups.Values
            .OrderByDescending(g => g.Sum(i => weights[i]))
            .ThenByDescending(g => g.Count)
            .First().ToList();
    }

    // ---------- 4c. 最小覆盖圆（Welzl 随机增量） ----------

    private readonly struct Circle
    {
        public readonly Point Center;
        public readonly double Radius;
        public Circle(Point c, double r) { Center = c; Radius = r; }
        // 注意：空圆（Radius < 0）必须"不包含任何点"，增量算法才能从第一个点开始扩张。
        public bool Contains(Point p) => Radius >= 0 && Distance(Center, p) <= Radius + 1e-6;
    }

    public static (Point center, double radius) MinEnclosingCircle(IReadOnlyList<Point> points)
    {
        var pts = new List<Point>(points);
        if (pts.Count == 0)
            return (new Point(), 0);

        var rnd = new Random(20240607);
        for (int i = pts.Count - 1; i > 0; i--)
        {
            int j = rnd.Next(i + 1);
            (pts[i], pts[j]) = (pts[j], pts[i]);
        }

        var c = new Circle(new Point(0, 0), -1);
        for (int i = 0; i < pts.Count; i++)
        {
            if (c.Contains(pts[i])) continue;
            c = new Circle(pts[i], 0);
            for (int j = 0; j < i; j++)
            {
                if (c.Contains(pts[j])) continue;
                c = CircleFrom2(pts[i], pts[j]);
                for (int k = 0; k < j; k++)
                {
                    if (c.Contains(pts[k])) continue;
                    c = CircleFrom3(pts[i], pts[j], pts[k]);
                }
            }
        }

        return (c.Center, c.Radius);
    }

    private static Circle CircleFrom2(Point a, Point b)
    {
        var center = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        return new Circle(center, Distance(a, b) / 2);
    }

    private static Circle CircleFrom3(Point a, Point b, Point c)
    {
        double d = 2 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
        if (Math.Abs(d) < 1e-9)
        {
            // 共线：退化为最长边的直径圆
            double ab = Distance(a, b), bc = Distance(b, c), ca = Distance(c, a);
            if (ab >= bc && ab >= ca) return CircleFrom2(a, b);
            if (bc >= ca) return CircleFrom2(b, c);
            return CircleFrom2(c, a);
        }

        double a2 = a.X * a.X + a.Y * a.Y;
        double b2 = b.X * b.X + b.Y * b.Y;
        double c2 = c.X * c.X + c.Y * c.Y;

        double ux = (a2 * (b.Y - c.Y) + b2 * (c.Y - a.Y) + c2 * (a.Y - b.Y)) / d;
        double uy = (a2 * (c.X - b.X) + b2 * (a.X - c.X) + c2 * (b.X - a.X)) / d;
        var center = new Point(ux, uy);
        return new Circle(center, Distance(center, a));
    }
}
