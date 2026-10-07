using System.Text;
using System.Windows;

namespace TouchSuite.App.Eraser;

/// <summary>
/// 面积擦引擎 —— 与界面完全解耦的纯后端，尺寸单位一律用**物理像素 px**。
/// 职责链：两路来源仲裁（自适应锁定 / 手动指定）→ 中值滤波 → K 定值放大 → 形状/长宽比 → 算出擦除区大小。
/// <para>
/// K 定值 = 手掌像素面积（a1×a2 / π/4×a1×a2 / a3 三选一）÷ 标定时手掌按压上报的触摸尺寸乘积（b1×b2）。
/// 系统报多少触摸像素面积，乘 K 就是手掌擦要显示的像素面积；随压感再放大（只放大不缩小）。
/// </para>
/// </summary>
public sealed class EraserEngine
{
    public const long SourceFreshMs = 300;        // 来源新鲜窗口
    public const long SourceLockObserveMs = 300;  // 自适应：连续有效达到此时长才锁定
    public const long SourceLockReleaseMs = 1500; // 自适应：锁定来源静默超过此时长则解锁
    public const int ContactHistoryMax = 5;       // 中值滤波窗口
    public const double MinContactPx = 0.5;       // 小于此像素尺寸视为"驱动未上报有效接触面积"

    /// <summary>不启用压感时的模拟压感：原生计数 512 / 1024 = 0.5（无压感设备的中间值）。</summary>
    public const double SimulatedPressureRaw = 512;
    public static double SimulatedPressure01 => SimulatedPressureRaw / 1024.0;

    /// <summary>书写点基准直径（px）：压感倍数 = 1× 时的直径。</summary>
    public const double WritingDotBasePx = 14;

    private static readonly ContactSource[] PriorityOrder =
        { ContactSource.RawHid, ContactSource.Wpf };

    // ================= 由外部喂入：标定值（全部物理像素）=================

    /// <summary>物理像素 / DIU（= DPI 缩放）：px → 预览区 DIU 换算用。</summary>
    public double PxPerDiuX { get; set; } = 1;
    public double PxPerDiuY { get; set; } = 1;

    public double PalmWidthPx { get; set; }        // a2 宽（横向）
    public double PalmHeightPx { get; set; }       // a1 高（纵向）
    public double PalmTraceAreaPx2 { get; set; }   // a3 描摹凹面积
    public AreaFormula Formula { get; set; } = AreaFormula.Rect;
    /// <summary>标定测得的手掌按压面积 b1×b2（px²）：K 的分母。0 = 未标定。</summary>
    public double PalmContactAreaPx2 { get; set; }
    /// <summary>标定测得的手指按压面积 c1×c2（px²）：切换阈值滑块的下界。</summary>
    public double FingerContactAreaPx2 { get; set; }

    /// <summary>手掌像素面积（K 的分子）：a1×a2 / π/4×a1×a2 / a3，末页三选一。</summary>
    public double PalmAreaPx2 => PalmAreaFrom(Formula, PalmWidthPx, PalmHeightPx, PalmTraceAreaPx2);

    /// <summary>三选一的面积公式（末页与主窗口共用同一实现）。</summary>
    public static double PalmAreaFrom(AreaFormula f, double w, double h, double trace) => f switch
    {
        AreaFormula.Ellipse when w > 0 && h > 0 => Math.PI / 4 * w * h,
        AreaFormula.Trace when trace > 0 => trace,
        _ => w > 0 && h > 0 ? w * h : 0,
    };

    /// <summary>手掌外接矩形长宽比 w/h（&lt;=0 = 未设定）。</summary>
    public double PalmAspect => PalmWidthPx > 0 && PalmHeightPx > 0 ? PalmWidthPx / PalmHeightPx : 0;

    // ================= 设置（界面绑定） =================

    public SourceMode Mode { get; private set; } = SourceMode.Auto;

    /// <summary>HID 模式下接触尺寸（物理像素）的取法。</summary>
    public HidSizeSource HidSizeSource { get; private set; } = HidSizeSource.LogicalToScreen;

    public EraserShape Shape { get; set; } = EraserShape.Rectangle;

    /// <summary>长宽比来源：跟随触摸尺寸 / 自定义（<see cref="CustomAspect"/>）。</summary>
    public AspectSource Aspect { get; set; } = AspectSource.Contact;

    /// <summary>自定义长宽比 W/H（1:1 = 正圆）。</summary>
    public double CustomAspect { get; set; } = 1.0;

    /// <summary>手掌擦随触摸尺寸（按多大擦多大）：擦除面积 = 接触面积 × 生效 K；关闭则固定为手掌面积 × 倍率。</summary>
    public bool FollowSize { get; set; } = true;

    /// <summary>手掌擦启用压感：压感按 [阈值..1] → [1×..max×] 放大擦除区（不缩小）。关闭则用模拟压感 0.5。</summary>
    public bool PalmPressureEnabled { get; set; } = true;

    /// <summary>书写启用压感；关闭则用模拟压感 0.5（512/1024）。</summary>
    public bool WritingUsesPressure { get; set; } = true;

    /// <summary>书写是否也随接触尺寸：开启后书写点再乘 √(接触面积 ÷ 手掌按压面积)。</summary>
    public bool WritingFollowSize { get; set; } = false;

    /// <summary>锁定手掌大小：手掌擦的擦除区「只增不减」——同一次按压内取最大值，抬手后再重来。按指独立。</summary>
    public bool LockPalmSize { get; set; } = false;

    /// <summary>启用面积阈值：接触面积低于阈值判为「书写」；关闭则一律按手掌擦处理。</summary>
    public bool AreaThresholdEnabled { get; set; } = true;

    /// <summary>K 倍率 ×0.5~×2：直接乘在 K 上（1.0 = 不调整）。</summary>
    public double KTrim { get; set; } = 1.0;

    // ---- 擦/写切换阈值（绝对值，px²；自动默认 = (b + c) / 2）----

    /// <summary>当前擦写切换阈值（绝对，px²）。</summary>
    public double AreaThresholdPx2 { get; set; }
    /// <summary>自动默认值 = (手掌 + 手指) / 2。</summary>
    public double AutoThresholdAreaPx2
        => PalmContactAreaPx2 > 0 && FingerContactAreaPx2 > 0
            ? (PalmContactAreaPx2 + FingerContactAreaPx2) / 2.0
            : PalmContactAreaPx2 > 0 ? PalmContactAreaPx2 : 0;
    /// <summary>滑块的绝对区间 [手指, 手掌] 是否可用。</summary>
    public bool HasThresholdRange
        => PalmContactAreaPx2 > 0 && FingerContactAreaPx2 > 0 && Math.Abs(PalmContactAreaPx2 - FingerContactAreaPx2) > 1e-9;

    // ---- 压感归一：p ∈ [阈值..1] → 放大 [1×..max×]，p < 阈值 → 1×（不缩小）----

    public double PalmPressureThreshold { get; set; } = SimulatedPressure01;   // 默认 = b3（标定填入）
    public double PalmNormMax { get; set; } = 2.0;                            // 1~3
    public double WritingPressureThreshold { get; set; } = SimulatedPressure01; // 默认 = c3（标定填入）
    public double WritingNormMax { get; set; } = 2.0;                         // 1~3

    /// <summary>当前实时压力（0~1），由界面每次样本喂入。</summary>
    public double? CurrentPressure { get; set; }

    private long _lastPressureTicks;

    /// <summary>喂入一次实时压力；null 表示该样本不带压感（保留上一值，但会随新鲜窗口过期成 0）。</summary>
    public void NotePressure(double? p)
    {
        if (p is double v)
        {
            CurrentPressure = v;
            _lastPressureTicks = Environment.TickCount64;
        }
    }

    /// <summary>生效压感：只有新鲜窗口内收到过压感才算数，抬手/停报后一律按 0。</summary>
    public double EffectivePressure01 =>
        CurrentPressure is double v && Environment.TickCount64 - _lastPressureTicks <= SourceFreshMs
            ? Math.Clamp(v, 0, 1) : 0.0;

    /// <summary>手掌擦实际使用的压感：启用→实测（过期按 0）；关闭→模拟 512/1024 = 0.5。</summary>
    public double PalmPressure01 => PalmPressureEnabled ? EffectivePressure01 : SimulatedPressure01;

    /// <summary>书写实际使用的压感：启用→实测（过期按 0）；关闭→模拟 512/1024 = 0.5。</summary>
    public double WritingPressure01 => WritingUsesPressure ? EffectivePressure01 : SimulatedPressure01;

    /// <summary>归一倍数：p ∈ [阈值..1] 线性映射到 [1×..max×]；p 低于阈值 = 1×（不缩小）。</summary>
    public static double NormGain(double p01, double threshold, double normMax)
    {
        double max = Math.Clamp(normMax, 1, 3);
        if (!(threshold < 1))
            return 1.0;
        double t = Math.Clamp((p01 - threshold) / (1 - threshold), 0, 1);
        return 1.0 + t * (max - 1);
    }

    /// <summary>手掌擦当前压感倍数（1× ~ max×）。</summary>
    public double PalmGain => NormGain(PalmPressure01, PalmPressureThreshold, PalmNormMax);

    /// <summary>书写当前压感倍数（1× ~ max×）。</summary>
    public double WritingGain => NormGain(WritingPressure01, WritingPressureThreshold, WritingNormMax);

    // ================= 结果状态 =================

    public string SizeHint { get; private set; } = "";

    /// <summary>一根手指/接触的独立状态：多指各画各的框，滤波/峰值/锁定/判定全按指分开。</summary>
    private sealed class Track
    {
        public long LastTicks;
        public readonly List<Rect> History = new();
        public Rect? Median;             // 中值滤波后的最新接触矩形（px）
        public double LastPxW, LastPxH;
        public double PeakPxW, PeakPxH;
        public double LockedAreaPx2;     // 锁定模式：本指本次按压内见过的最大擦除区面积
        public bool PalmLatched;         // 锁定模式：本指已判定为手掌擦（保持到抬手，不再退回书写）
    }

    private readonly Dictionary<int, Track> _tracks = new();

    /// <summary>按指的接触矩形（中值滤波后，**物理像素**；多指=多项，单指=1 项）。抬手/超窗的指自动消失。</summary>
    public IReadOnlyList<(int Id, Rect Rect)> LastContacts
        => _tracks.Where(kv => kv.Value.Median is not null)
                  .Select(kv => (kv.Key, kv.Value.Median!.Value))
                  .ToList();

    private Track? BestTrack()
    {
        Track? best = null;
        double bestA = 0;
        foreach (Track t in _tracks.Values)
            if (t.Median is Rect r && r.Width * r.Height > bestA)
            {
                bestA = r.Width * r.Height;
                best = t;
            }
        return best;
    }

    public Rect? LastContact => BestTrack()?.Median;

    public double LastPxW => BestTrack()?.LastPxW ?? 0;
    public double LastPxH => BestTrack()?.LastPxH ?? 0;
    public double PeakPxW => BestTrack()?.PeakPxW ?? 0;
    public double PeakPxH => BestTrack()?.PeakPxH ?? 0;

    /// <summary>全部分指的峰值面积之和（px²）（未标定时的 K 退路；单指 = 单指峰值面积）。</summary>
    public double TotalPeakAreaPx2
    {
        get
        {
            double s = 0;
            foreach (Track t in _tracks.Values)
                s += t.PeakPxW * t.PeakPxH;
            return s;
        }
    }

    private readonly Dictionary<ContactSource, (long Time, Rect Rect, bool Eligible)> _state = new();
    private readonly Dictionary<ContactSource, string> _detail = new();
    private readonly Dictionary<ContactSource, long> _validSince = new();
    private ContactSource _active = ContactSource.None;
    private ContactSource _locked = ContactSource.None;
    private long _lastContactTicks;
    private bool _driverSizeWarned;

    /// <summary>任何影响界面显示的状态变化后触发。</summary>
    public event Action? Changed;

    private void Raise() => Changed?.Invoke();

    // ================= 来源模式 =================

    public void SetMode(SourceMode mode)
    {
        Mode = mode;
        Log.Info($"接触来源模式 = {mode}");
        _locked = ContactSource.None;
        _active = ContactSource.None;
        _validSince.Clear();
        _tracks.Clear();
        Raise();
    }

    /// <summary>HID 尺寸取法（逻辑量程→屏幕分辨率 / 物理量程→屏幕换算）；切换后重置滤波。</summary>
    public void SetHidSizeSource(HidSizeSource src)
    {
        HidSizeSource = src;
        Log.Info($"HID 尺寸取法 = {src}");
        _tracks.Clear();
        Raise();
    }

    // ================= 统一入口：两路来源仲裁 =================

    /// <summary>提交一根接触的矩形（**物理像素**）。contactId 标识是哪根手指，
    /// 多根手指各调各的、各画各的框。applyThreshold=false 时不套尺寸阈值（原始 HID 的真值）。</summary>
    public void Submit(ContactSource src, int contactId, Rect rectPx, bool applyThreshold, string detail)
    {
        long now = Environment.TickCount64;

        // 退化接触框（0×0）不携带任何尺寸信息：设备即使没抬手也会周期夹带这种"空槽位"帧。
        // 若把它记成"该来源不可用"，高优先来源会被短暂降级，与低优先来源反复横跳，而每次切换都会
        // 清空中值滤波窗 → 擦除区尺寸抖动。故直接忽略，保留上次有效值直到自然过期。
        if (rectPx.Width <= 0 || rectPx.Height <= 0)
            return;

        bool eligible = true;
        if (applyThreshold)
            eligible = rectPx.Width >= MinContactPx && rectPx.Height >= MinContactPx;

        _state[src] = (now, rectPx, eligible);
        _detail[src] = detail;

        if (eligible)
        {
            if (!_validSince.ContainsKey(src))
                _validSince[src] = now;

            // 距上次有效接触超过新鲜窗口 → 视为一次新的按压，重置全部分指的峰值、滤波窗与「锁定大小」
            if (now - _lastContactTicks > SourceFreshMs)
                _tracks.Clear();
            _lastContactTicks = now;
        }
        else
        {
            _validSince.Remove(src);
        }

        // 分指过期：先抬的那根清掉（它的样本停更了），其余的框保持
        if (_tracks.Count > 0)
        {
            List<int> gone = _tracks.Where(kv => now - kv.Value.LastTicks > SourceFreshMs)
                .Select(kv => kv.Key).ToList();
            foreach (int k in gone)
                _tracks.Remove(k);
        }

        ContactSource eff = ResolveSource(now);
        if (eff == ContactSource.None)
        {
            Raise();
            return;
        }

        // 只有"生效来源"的样本才写尺寸轨道：同一次按下 HID 与 WPF 两路都会出帧，
        // 若都写，尺寸/位置会在两路之间来回跳。非生效来源只更新状态后返回。
        if (src != eff)
        {
            Raise();
            return;
        }

        if (eff != _active)
        {
            _active = eff;
            _tracks.Clear(); // 换来源就重置滤波，避免不同量纲互相污染
        }

        UpdateFromContact(contactId, rectPx, applyThreshold);
        Raise();
    }

    private bool IsUsable(ContactSource s, long now)
        => _state.TryGetValue(s, out var st) && st.Eligible && now - st.Time <= SourceFreshMs;

    /// <summary>手动指定则只认那一路；自适应则稳定识别后锁定。</summary>
    private ContactSource ResolveSource(long now)
    {
        if (Mode != SourceMode.Auto)
        {
            ContactSource want = Mode switch
            {
                SourceMode.RawHid => ContactSource.RawHid,
                SourceMode.SoftwareWpf => ContactSource.Wpf,
                _ => ContactSource.None,
            };
            return want != ContactSource.None && IsUsable(want, now) ? want : ContactSource.None;
        }

        // 已锁定：一直跟随，直到它静默太久（抬手/失效）才解锁重新识别
        if (_locked != ContactSource.None)
        {
            if (_state.TryGetValue(_locked, out var st) && st.Eligible && now - st.Time <= SourceLockReleaseMs)
                return _locked;
            _locked = ContactSource.None;
            _validSince.Clear();
        }

        // 未锁定：按优先级找"连续有效 ≥ 观察期"的来源并锁定
        foreach (ContactSource s in PriorityOrder)
            if (IsUsable(s, now) && _validSince.TryGetValue(s, out long since) && now - since >= SourceLockObserveMs)
            {
                _locked = s;
                return s;
            }

        // 观察期内先用当前最高优先的可用来源垫着显示
        foreach (ContactSource s in PriorityOrder)
            if (IsUsable(s, now))
                return s;

        return ContactSource.None;
    }

    /// <summary>定时刷新：所有来源过期后把生效来源归零；分指过期后该指的框消失，其余的保持。</summary>
    public void Tick()
    {
        long now = Environment.TickCount64;
        bool anyFresh = false;
        foreach (ContactSource s in PriorityOrder)
            if (IsUsable(s, now)) { anyFresh = true; break; }

        if (!anyFresh && _active != ContactSource.None)
            _active = ContactSource.None;

        if (_tracks.Count > 0)
        {
            List<int> gone = _tracks.Where(kv => now - kv.Value.LastTicks > SourceFreshMs)
                .Select(kv => kv.Key).ToList();
            foreach (int k in gone)
                _tracks.Remove(k);
        }

        Raise();
    }

    // ================= 滤波 + 计算（按指独立）=================

    private void UpdateFromContact(int id, Rect b, bool applyThreshold)
    {
        bool tooSmall = b.Width <= 0 || b.Height <= 0;
        if (!tooSmall && applyThreshold)
            tooSmall = b.Width < MinContactPx || b.Height < MinContactPx;

        if (tooSmall)
        {
            SizeHint = "驱动未上报接触尺寸（只给位置或占位值）。";
            if (!_driverSizeWarned)
            {
                _driverSizeWarned = true;
                SizeHint += " 面积擦需要能上报接触尺寸的数字化器（真触摸屏/笔）。";
            }
            return;
        }

        if (!_tracks.TryGetValue(id, out Track? track))
        {
            track = new Track();
            _tracks[id] = track;
        }

        // 同一帧可能被元素事件和全局帧事件重复喂入：相同值只算一次，避免滤波窗被占满
        if (track.History.Count > 0)
        {
            Rect last = track.History[^1];
            if (Math.Abs(last.X - b.X) < 0.01 && Math.Abs(last.Y - b.Y) < 0.01 &&
                Math.Abs(last.Width - b.Width) < 0.01 && Math.Abs(last.Height - b.Height) < 0.01)
                return;
        }

        track.History.Add(b);
        if (track.History.Count > ContactHistoryMax)
            track.History.RemoveAt(0);

        track.Median = MedianRect(track.History);
        track.LastTicks = Environment.TickCount64;
        Rect cur = track.Median.Value;

        // 锁定手掌大小（按指独立）：①本指擦除面积只增不减 ②本指一旦判为手掌擦就保持到抬手
        if (LockPalmSize)
        {
            double raw = EraserAreaRawPx2(cur);
            if (raw > track.LockedAreaPx2)
                track.LockedAreaPx2 = raw;

            if (AreaThresholdEnabled && AreaThresholdPx2 > 0
                && cur.Width * cur.Height >= AreaThresholdPx2)
                track.PalmLatched = true;
        }

        track.LastPxW = cur.Width;
        track.LastPxH = cur.Height;

        // 峰值取"面积最大"那一帧（手掌接触面积取峰值，而非松手瞬间缩小的值）
        if (track.LastPxW * track.LastPxH > track.PeakPxW * track.PeakPxH)
        {
            track.PeakPxW = track.LastPxW;
            track.PeakPxH = track.LastPxH;
        }

        SizeHint = "";
    }

    private static Rect MedianRect(List<Rect> items) => new(
        Median(items.Select(r => r.X)),
        Median(items.Select(r => r.Y)),
        Median(items.Select(r => r.Width)),
        Median(items.Select(r => r.Height)));

    private static double Median(IEnumerable<double> values)
    {
        double[] a = values.OrderBy(v => v).ToArray();
        int n = a.Length;
        if (n == 0) return 0;
        return n % 2 == 1 ? a[n / 2] : (a[n / 2 - 1] + a[n / 2]) / 2.0;
    }

    // ================= K 定值 =================

    /// <summary>K = 手掌像素面积 ÷ 标定手掌按压的触摸尺寸乘积（面积比）。
    /// 未标定才退回"本次按压峰值"（多指 = 全部分指峰值面积之和）。数据不足返回 0。</summary>
    public double ComputeK()
    {
        double palm = PalmAreaPx2;
        if (!(palm > 0))
            return 0;
        double b = PalmContactAreaPx2 > 0 ? PalmContactAreaPx2 : TotalPeakAreaPx2;
        if (!(b > 0))
            return 0;
        double k = palm / b;
        return double.IsFinite(k) && k > 0 ? k : 0;
    }

    /// <summary>当前实际生效的 K：标定 K（数据不足按 1 计）× K 倍率。</summary>
    public double EffectiveK()
    {
        double k = ComputeK();
        if (!(k > 0))
            k = 1.0;
        return k * Math.Clamp(KTrim, 0.5, 2.0);
    }

    /// <summary>生效 K 开到边长上的倍数（√K）：触摸报的 w/h 各乘它 = 擦除区 w/h。</summary>
    public double LinearFactor() => Math.Sqrt(EffectiveK());

    /// <summary>擦除区面积（px²）。随尺寸 = 接触面积 × 生效 K × 压感倍数²；否则固定手掌面积 × 倍率 × 压感倍数²。</summary>
    private double EraserAreaRawPx2(Rect c)
    {
        double g = PalmGain;
        double area = FollowSize
            ? c.Width * c.Height * EffectiveK()
            : PalmAreaPx2 * Math.Clamp(KTrim, 0.5, 2.0);
        return area * g * g;
    }

    /// <summary>某根接触的擦除区面积（px²）：开启「锁定手掌大小」时返回本指本次按压内见过的最大值（只增不减）。</summary>
    public double EraserAreaPx2(int id, Rect c)
        => LockPalmSize && _tracks.TryGetValue(id, out Track? t) && t.LockedAreaPx2 > 0
            ? t.LockedAreaPx2
            : EraserAreaRawPx2(c);

    /// <summary>该接触当前的长宽比 W/H：自定义优先；否则跟随触摸尺寸；固定/无尺寸时用手掌比例。</summary>
    private double AspectFor(Rect? c)
    {
        double aspect;
        if (Aspect == AspectSource.Custom && CustomAspect > 0)
            aspect = CustomAspect;
        else if (FollowSize && c is Rect cc && cc.Height > 0)
            aspect = cc.Width / cc.Height;
        else if (PalmAspect > 0)
            aspect = PalmAspect;
        else
            aspect = 1;
        return aspect > 0 && double.IsFinite(aspect) ? aspect : 1;
    }

    /// <summary>某根接触的擦除区尺寸（DIU）：
    /// 矩形 = W×H 面积即目标面积；椭圆 = π/4×W×H = 目标面积（1:1 时就是正圆）。</summary>
    public (double w, double h) SizeDiu(int id, Rect c)
    {
        if (PxPerDiuX <= 0 || PxPerDiuY <= 0)
            return (0, 0);

        double areaPx2 = EraserAreaPx2(id, c);
        if (!(areaPx2 > 0))
            return (0, 0);

        double aspect = AspectFor(c);
        double wPx, hPx;
        if (Shape == EraserShape.Circle)
        {
            hPx = Math.Sqrt(4 * areaPx2 / (Math.PI * aspect));
            wPx = aspect * hPx;
        }
        else
        {
            hPx = Math.Sqrt(areaPx2 / aspect);
            wPx = aspect * hPx;
        }

        double w = wPx / PxPerDiuX;
        double h = hPx / PxPerDiuY;
        return double.IsFinite(w) && double.IsFinite(h) && w > 0 && h > 0 ? (w, h) : (0, 0);
    }

    /// <summary>擦除区尺寸（物理像素，界面文字用）。</summary>
    public (double w, double h) SizePx(int id, Rect c)
    {
        double areaPx2 = EraserAreaPx2(id, c);
        if (!(areaPx2 > 0))
            return (0, 0);
        double aspect = AspectFor(c);
        double hPx = Shape == EraserShape.Circle
            ? Math.Sqrt(4 * areaPx2 / (Math.PI * aspect))
            : Math.Sqrt(areaPx2 / aspect);
        double wPx = aspect * hPx;
        return double.IsFinite(wPx) && double.IsFinite(hPx) && wPx > 0 && hPx > 0 ? (wPx, hPx) : (0, 0);
    }

    /// <summary>界面改了形状/倍率/压感等设置后，通知重画。</summary>
    public void NotifyChanged() => Raise();

    public void Clear()
    {
        _tracks.Clear();
        _lastContactTicks = 0;
        CurrentPressure = null;
        _lastPressureTicks = 0;
        _driverSizeWarned = false;
        _state.Clear();
        _detail.Clear();
        _active = ContactSource.None;
        _locked = ContactSource.None;
        _validSince.Clear();
        SizeHint = "已清除。用真触摸屏按一下手掌即可实时显示擦除区。";
        Raise();
    }

    // ================= 书写点 =================

    /// <summary>书写点直径（px）= 基准 14px × 压感倍数（1×~max×）；
    /// 开启「书写随尺寸」再乘 √(接触面积 ÷ 手掌按压面积)。</summary>
    public double WritingDotDiameterOfPx(Rect? contact)
    {
        double d = WritingDotBasePx * WritingGain;
        if (!WritingFollowSize || contact is not Rect c)
            return d;
        double a = c.Width * c.Height;
        if (!(a > 0) || !(PalmContactAreaPx2 > 0))
            return d;
        double k = Math.Sqrt(a / PalmContactAreaPx2);
        return double.IsFinite(k) && k > 0 ? d * k : d;
    }

    /// <summary>是否还有"新鲜"的接触（抬手/静默过期后为 false）。</summary>
    public bool HasLiveContact => _active != ContactSource.None && _tracks.Count > 0;

    /// <summary>某根接触是否判为「书写」：启用面积阈值，且该指接触面积（px²）&lt; 阈值（按指独立判定）。
    /// 例外：开启「锁定手掌大小」时，本指一旦判为手掌擦就保持到抬手，不再退回书写。</summary>
    public bool IsWriting(int id)
    {
        if (!AreaThresholdEnabled)
            return false;
        if (!_tracks.TryGetValue(id, out Track? t) || t.Median is not Rect c)
            return false;

        if (LockPalmSize && t.PalmLatched)
            return false;

        if (AreaThresholdPx2 <= 0)
            return false;
        return c.Width * c.Height < AreaThresholdPx2;
    }

    // ================= 文字（界面直接显示） =================

    public string SourceInfo()
    {
        long now = Environment.TickCount64;
        var sb = new StringBuilder();
        string mode = Mode != SourceMode.Auto
            ? $"手动：{SourceNames.OfMode(Mode)}"
            : (_locked != ContactSource.None
                ? $"自适应：已锁定 {SourceNames.Of(_locked)}"
                : "自适应：识别中…");
        sb.Append(mode).Append("   |   生效: ").Append(SourceNames.Of(_active));

        foreach (ContactSource s in PriorityOrder)
        {
            bool active = s == _active;
            bool fresh = _state.TryGetValue(s, out var st) && now - st.Time <= SourceFreshMs;
            string val = _detail.TryGetValue(s, out string? d) ? d : "（无）";
            sb.Append('\n').Append(active ? "▶ " : "   ").Append(SourceNames.Of(s)).Append(": ").Append(val);
            if (!fresh && _state.ContainsKey(s)) sb.Append("  (旧)");
        }
        return sb.ToString();
    }

    /// <summary>K 定值框文字：手掌像素面积（三选一）/ 触摸尺寸乘积 / K / 倍率。</summary>
    public string KInfo()
    {
        double k = ComputeK();
        double palm = PalmAreaPx2;
        double b = PalmContactAreaPx2 > 0 ? PalmContactAreaPx2 : TotalPeakAreaPx2;

        var sb = new StringBuilder();
        sb.Append($"手掌像素面积（{SourceNames.OfFormula(Formula)}）= {(palm > 0 ? Precision.Fmt(palm, 0) + " px²" : "—")}");
        sb.Append($"\na1 高 {Precision.Fmt(PalmHeightPx, 0)} × a2 宽 {Precision.Fmt(PalmWidthPx, 0)} px");
        if (Formula == AreaFormula.Trace)
            sb.Append($"（a3 描摹凹面积 {Precision.Fmt(PalmTraceAreaPx2, 0)} px²）");
        sb.Append($"\n触摸尺寸乘积（手掌按压 b1×b2）= {(b > 0 ? Precision.Fmt(b, 0) + " px²" : "待第 5 步记录手掌按压")}");

        if (k > 0)
        {
            double eff = k * Math.Clamp(KTrim, 0.5, 2.0);
            sb.Append($"\nK = {Precision.Fmt(k)}（手掌 ÷ 触摸；每边 ×{Precision.Fmt(Math.Sqrt(k))}）");
            sb.Append($"\n× 倍率 {Precision.Fmt(KTrim, 2)} → 生效 K = {Precision.Fmt(eff)}（每边 ×{Precision.Fmt(Math.Sqrt(eff))}）");
        }
        else
        {
            sb.Append("\nK = —（数据不足，按 1 计：触摸报多少就多大）");
        }
        return sb.ToString();
    }

    /// <summary>擦/写切换阈值框文字。</summary>
    public string AreaThresholdInfo()
    {
        if (!HasThresholdRange)
            return "自动 = (手掌按压面积 + 手指按压面积) ÷ 2\n待第 5、6 步记录手掌与手指按压";
        double lo = Math.Min(PalmContactAreaPx2, FingerContactAreaPx2);
        double hi = Math.Max(PalmContactAreaPx2, FingerContactAreaPx2);
        return $"自动 = {Precision.Fmt(AutoThresholdAreaPx2, 0)} px²（中值）\n"
             + $"可调范围 = {Precision.Fmt(lo, 0)}（手指）~ {Precision.Fmt(hi, 0)}（手掌）px²\n"
             + $"当前阈值 = {Precision.Fmt(AreaThresholdPx2, 0)} px²";
    }

    /// <summary>压感归一框文字：两侧的开关 / 阈值 / max / 现行倍数。</summary>
    public string PressureInfo()
    {
        var sb = new StringBuilder();
        sb.Append($"手掌擦：{(PalmPressureEnabled ? "启用" : "关闭（模拟 512/1024 = 0.5）")}  当前 p = {Precision.Fmt(PalmPressure01, 3)}");
        sb.Append($"\n  [阈值 {Precision.Fmt(PalmPressureThreshold, 2)} .. 1] → [1× .. {Precision.Fmt(PalmNormMax, 2)}×]   现行 ×{Precision.Fmt(PalmGain, 3)}");
        sb.Append($"\n书写：{(WritingUsesPressure ? "启用" : "关闭（模拟 512/1024 = 0.5）")}  当前 p = {Precision.Fmt(WritingPressure01, 3)}");
        sb.Append($"\n  [阈值 {Precision.Fmt(WritingPressureThreshold, 2)} .. 1] → [1× .. {Precision.Fmt(WritingNormMax, 2)}×]   现行 ×{Precision.Fmt(WritingGain, 3)}");
        return sb.ToString();
    }

    /// <summary>手掌擦 / 书写 判定说明（多指 = 每指各判各的）。</summary>
    public string JudgeInfo()
    {
        IReadOnlyList<(int Id, Rect Rect)> list = LastContacts;
        if (list.Count == 0)
            return "判定：—（无接触）";
        if (!AreaThresholdEnabled)
            return "判定：手掌擦（面积阈值已关闭 → 永远手掌擦）";
        if (AreaThresholdPx2 <= 0)
            return "判定：手掌擦（面积阈值未标定）";

        string head = $"阈值 {Precision.Fmt(AreaThresholdPx2, 0)} px² → ";
        var parts = new List<string>(list.Count);
        bool anyWriting = false;
        foreach ((int id, Rect c) in list)
        {
            double area = c.Width * c.Height;
            bool writing = IsWriting(id);
            anyWriting |= writing;
            parts.Add(list.Count > 1
                ? $"#{id} {Precision.Fmt(area, 0)}px² {(writing ? "书写" : "手掌擦")}"
                : $"{Precision.Fmt(area, 0)}px² {(writing ? "书写" : "手掌擦")}");
        }

        string press = anyWriting
            ? (WritingUsesPressure
                ? $"（压感 实测 {Precision.Fmt(WritingPressure01, 3)} → ×{Precision.Fmt(WritingGain, 2)}）"
                : $"（压感 模拟 {SimulatedPressureRaw:0}/1024 = {Precision.Fmt(SimulatedPressure01, 3)}）")
            : "";
        return "判定：" + head + string.Join(" ｜ ", parts) + press;
    }

    public string EraserInfo()
    {
        IReadOnlyList<(int Id, Rect Rect)> list = LastContacts;
        if (list.Count == 0 || PxPerDiuX <= 0)
            return "尚无驱动接触数据（需真触摸屏；鼠标/部分驱动不提供接触尺寸）。";

        double contactSum = 0, eraseSum = 0;
        var per = new List<string>(list.Count);
        foreach ((int id, Rect c) in list)
        {
            (double w, double h) = SizePx(id, c);
            contactSum += c.Width * c.Height;
            eraseSum += EraserAreaPx2(id, c);
            per.Add(list.Count > 1
                ? $"#{id} {Precision.Fmt(c.Width)}×{Precision.Fmt(c.Height)}px → 擦 {Precision.Fmt(w, 0)}×{Precision.Fmt(h, 0)}px"
                : $"{Precision.Fmt(c.Width)}×{Precision.Fmt(c.Height)}px → 擦 {Precision.Fmt(w, 0)}×{Precision.Fmt(h, 0)}px");
        }

        double k = ComputeK();
        double effK = k > 0 ? k * Math.Clamp(KTrim, 0.5, 2.0) : 1.0;
        string kText = k > 0
            ? $"K {Precision.Fmt(k)} × 倍率 {Precision.Fmt(KTrim, 2)} → 生效 {Precision.Fmt(effK)}（每边 ×{Precision.Fmt(Math.Sqrt(effK))}）"
            : "K 未标定（按 1 计：触摸报多少就多大）";

        string sizeText = FollowSize
            ? $"随接触尺寸: Σ {Precision.Fmt(contactSum, 0)} px² × K（{kText}）"
            : $"固定手掌面积: {Precision.Fmt(PalmAreaPx2, 0)} px² × 倍率 {Precision.Fmt(KTrim, 2)}";

        string pressText = PalmPressureEnabled
            ? $"\n手掌擦压感: p {Precision.Fmt(PalmPressure01, 3)}（阈值 {Precision.Fmt(PalmPressureThreshold, 2)}）→ 倍数 ×{Precision.Fmt(PalmGain, 3)}"
            : "\n手掌擦压感: 关闭（模拟 512/1024 = 0.5）";

        string shapeText = Shape == EraserShape.Circle ? "各指等面积椭圆（π/4×W×H）" : "各指矩形";
        return $"模式: {(FollowSize ? "随尺寸" : "固定手掌")}{(LockPalmSize ? " + 锁定大小(只增不减)" : "")}"
             + $"{(Aspect == AspectSource.Custom ? $" + 长宽比 {Precision.Fmt(CustomAspect, 2)}:1" : " + 长宽比跟随触摸")}\n"
             + sizeText + pressText
             + $"\n擦除区: Σ {Precision.Fmt(eraseSum, 0)} px²（{shapeText}，见预览区）";
    }
}