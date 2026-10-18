namespace TouchSuite.Ink;

/// <summary>
/// 把「一帧原始触点」合成「稳定的手指事件」。
///
/// <para><b>为什么需要它</b>：有些触摸屏标称 10 指但只有 6 个通道，不足的通道由多根手指
/// <b>交替上报</b> —— 所以槽位号（LinkCollection）只能当"运输容器"，绝不能当手指身份。
/// 身份按这个优先级取：① 设备报了 Contact Identifier(0x0D:0x51) 且非 0 → 直接用 CID；
/// ② 否则（例如 Microsoft HID RID 屏 CID 恒 0）→ 按位置做相邻帧最近邻跟踪，分配 StableId。</para>
///
/// <para><b>两条不能违反的经验</b>：位置<b>绝不</b>滤波（滤波会滞后约 2 帧，导致末梢漏点）；
/// 宽高做中值滤波（稳定手掌/书写判定）。</para>
/// </summary>
internal sealed class ContactTracker
{
    public enum Kind { Down, Move, Up }

    /// <summary>一帧里的一根触点（来自 RawInput）。Transport = LinkCollection 槽位，只当容器。</summary>
    public readonly record struct TrackIn(int Transport, int ContactId, bool Down,
        float X, float Y, float W, float H, float Pressure01);

    /// <summary>合成后交给上层的"手指事件"。StableId 在按下→抬起期间保持不变，且恒 &gt;= 1（0 留给鼠标）。</summary>
    public readonly record struct ContactEvent(int StableId, Kind Kind,
        float X, float Y, float W, float H, float Pressure01);

    private const int MedianWindow = 5;

    /// <summary>最近邻匹配半径（px）。&lt;=0 表示自动：min(屏宽,屏高) × 6%。</summary>
    public float MatchRadiusPx { get; set; }

    /// <summary>连续多少帧没出现才认为抬手（配合 CountZero 用）。</summary>
    public int HoldFrames { get; set; } = 3;

    /// <summary>抬手的最长容忍时间（ms）：超过就强制抬手，避免通道交替把手指"扣住"不放。</summary>
    public int TtlMs { get; set; } = 120;

    private sealed class Track
    {
        public int StableId;
        public int Transport = -1;
        public float X, Y, Pressure01;
        public readonly Queue<float> Ws = new();
        public readonly Queue<float> Hs = new();
        public int MissingFrames;
        public long LastSeenMs;
        public bool LiftSent;      // 已发过 Move(补位)，避免重复
    }

    private readonly float _autoRadius;
    private readonly Dictionary<int, Track> _byStable = new();   // StableId → Track
    private int _nextId = 1;
    private int _newTracks;

    public int ActiveCount => _byStable.Count;
    public int NewTrackCount => _newTracks;

    public ContactTracker(int screenW, int screenH)
    {
        _autoRadius = MathF.Max(24f, MathF.Min(screenW, screenH) * 0.06f);
    }

    private float Radius => MatchRadiusPx > 0 ? MatchRadiusPx : _autoRadius;

    public void Reset()
    {
        _byStable.Clear();
        _nextId = 1;
        _newTracks = 0;
    }

    public string Describe() =>
        $"轨道={_byStable.Count} 新分配={_newTracks} 半径={Radius:0}px 保持={HoldFrames}帧/{TtlMs}ms";

    /// <summary>
    /// 处理「一帧」原始触点。countZero = true 表示设备明确报了「接触数 0x0D:0x54 == 0」= 整体抬手。
    /// outEvents 里的事件顺序固定为 Down → Move → Up。
    /// </summary>
    public void Sync(IReadOnlyList<TrackIn> contacts, bool countZero, List<ContactEvent> outEvents)
    {
        long now = Environment.TickCount64;
        outEvents.Clear();

        var live = new List<TrackIn>();
        var lift = new List<TrackIn>();
        foreach (TrackIn c in contacts)
            (c.Down ? live : lift).Add(c);

        var matched = new HashSet<int>();

        // ① 按下 / 移动
        foreach (TrackIn c in live)
        {
            Track? t = Match(c, matched);
            if (t is null)
            {
                t = new Track { StableId = NextStableId(), Transport = c.Transport };
                _byStable[t.StableId] = t;
                _newTracks++;
                Seed(t, c);
                outEvents.Add(Event(t, Kind.Down));
            }
            else
            {
                t.Transport = c.Transport;
                TrackSample(t, c);
                outEvents.Add(Event(t, Kind.Move));
            }
            t.MissingFrames = 0;
            t.LastSeenMs = now;
            t.LiftSent = false;
            matched.Add(t.StableId);
        }

        // ② 抬手过渡帧（设备把 TipSwitch 清零但坐标/尺寸还在）：先补一个 Move 把末点画出来，再抬。
        foreach (TrackIn c in lift)
        {
            Track? t = Match(c, matched, relaxed: true);
            if (t is null)
                continue;
            if (!t.LiftSent && c.X != 0 && c.Y != 0)
            {
                t.X = c.X;
                t.Y = c.Y;
                TrackSample(t, c);
                outEvents.Add(Event(t, Kind.Move));   // 末点补位：画到真实抬手位置
                t.LiftSent = true;
            }
            outEvents.Add(Event(t, Kind.Up));
            matched.Add(t.StableId);
            _byStable.Remove(t.StableId);
        }

        // ③ 这一帧没被匹配到的轨道：countZero 或超过保持窗口才真抬手
        var toRemove = new List<int>();
        foreach (KeyValuePair<int, Track> kv in _byStable)
        {
            Track t = kv.Value;
            if (matched.Contains(t.StableId))
                continue;

            t.MissingFrames++;
            bool giveUp = countZero ||
                (t.MissingFrames >= HoldFrames && now - t.LastSeenMs > TtlMs);
            if (!giveUp)
                continue;

            outEvents.Add(Event(t, Kind.Up));
            toRemove.Add(t.StableId);
        }
        foreach (int id in toRemove)
            _byStable.Remove(id);
    }

    private int NextStableId() => _nextId++;

    private static void Seed(Track t, TrackIn c)
    {
        t.X = c.X;
        t.Y = c.Y;
        t.Pressure01 = c.Pressure01;
        t.Ws.Enqueue(c.W);
        t.Hs.Enqueue(c.H);
    }

    /// <summary>位置原样采用（绝不滤波）；宽高进中值窗口。</summary>
    private static void TrackSample(Track t, TrackIn c)
    {
        t.X = c.X;
        t.Y = c.Y;
        t.Pressure01 = c.Pressure01;
        Push(t.Ws, c.W);
        Push(t.Hs, c.H);
    }

    private static void Push(Queue<float> q, float v)
    {
        q.Enqueue(v);
        while (q.Count > MedianWindow)
            q.Dequeue();
    }

    private static float Median(Queue<float> q)
    {
        if (q.Count == 0)
            return 0f;
        float[] a = q.ToArray();
        Array.Sort(a);
        return a[a.Length / 2];
    }

    private static ContactEvent Event(Track t, Kind kind) =>
        new(t.StableId, kind, t.X, t.Y, Median(t.Ws), Median(t.Hs), t.Pressure01);

    /// <summary>按身份优先级找轨道：CID 非 0 直接用 CID；否则位置最近邻（同槽位视为弱证据，优先）。</summary>
    private Track? Match(TrackIn c, HashSet<int> taken, bool relaxed = false)
    {
        if (c.ContactId != 0 && _byStable.TryGetValue(c.ContactId, out Track? byCid) &&
            !taken.Contains(byCid.StableId))
            return byCid;

        float radius = Radius * (relaxed ? 1.6f : 1f);
        Track? best = null;
        float bestScore = float.MaxValue;
        foreach (KeyValuePair<int, Track> kv in _byStable)
        {
            Track t = kv.Value;
            if (taken.Contains(t.StableId))
                continue;
            float dx = t.X - c.X, dy = t.Y - c.Y;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist > radius)
                continue;
            // 同槽位优先，其次才比距离 —— 但身份始终是 StableId，不是槽位。
            float score = (t.Transport == c.Transport ? 0f : 1000f) + dist;
            if (score < bestScore)
            {
                bestScore = score;
                best = t;
            }
        }
        return best;
    }
}
