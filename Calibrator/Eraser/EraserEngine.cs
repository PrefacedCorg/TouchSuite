using System.Text;
using System.Windows;

namespace TouchErase.Calibrator.Eraser;

/// <summary>
/// 面积擦引擎 —— 从主程序第③页搬过来的纯后端，与界面完全解耦。
/// 职责链：三路来源仲裁（自适应锁定 / 手动指定）→ 中值滤波 → 倍率与形状 → 算出擦除区大小。
/// 对外只暴露物理量与文字，DIU 换算用 <see cref="MmPerDiuX"/>/<see cref="MmPerDiuY"/>。
/// </summary>
public sealed class EraserEngine
{
    public const long SourceFreshMs = 300;        // 来源新鲜窗口
    public const long SourceLockObserveMs = 300;  // 自适应：连续有效达到此时长才锁定
    public const long SourceLockReleaseMs = 1500; // 自适应：锁定来源静默超过此时长则解锁
    public const int ContactHistoryMax = 5;       // 中值滤波窗口
    public const double MinContactMm = 0.1;       // 小于此物理尺寸视为"驱动未上报有效接触面积"（真手指/手掌远大于此，0.1 只为滤掉近 0 的占位值）

    private static readonly ContactSource[] PriorityOrder =
        { ContactSource.RawHid, ContactSource.HidSetup, ContactSource.Wpf };

    // ================= 由外部喂入 =================

    public double MmPerDiuX { get; set; }
    public double MmPerDiuY { get; set; }
    public double PalmAreaCm2 { get; set; }
    /// <summary>标定测得的「手掌按压面积」（mm²）：自动倍率的分母，标定后就是定值。0 = 未标定。</summary>
    public double PalmPressAreaMm2 { get; set; }
    public double PalmAspect { get; set; }          // 手掌长宽比 w/h；<=0 = 未设定

    // ================= 设置（界面绑定） =================

    public SourceMode Mode { get; private set; } = SourceMode.Auto;
    public EraserShape Shape { get; set; } = EraserShape.Rectangle;

    /// <summary>随接触尺寸变化（按多大擦多大）：擦除面积 = 接触面积 × 倍率²；关闭则固定为手掌面积。</summary>
    public bool FollowSize { get; set; } = true;

    /// <summary>随压力变化（按多重擦多大）：擦除面积再乘压力系数（当前压力 ÷ 手掌按压压力，0~1）。</summary>
    public bool FollowPressure { get; set; } = false;

    /// <summary>当前实时压力（0~1），由界面每次样本喂入。</summary>
    public double? CurrentPressure { get; set; }

    /// <summary>最近一次收到压力样本的时刻（TickCount64）；无新鲜压感时该值会过期。</summary>
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

    /// <summary>生效压感：只有新鲜窗口内收到过压感才算数，抬手/停报后一律按 0（避免用残留压感放大尺寸）。</summary>
    public double EffectivePressure01 =>
        CurrentPressure is double v && Environment.TickCount64 - _lastPressureTicks <= SourceFreshMs
            ? Math.Clamp(v, 0, 1) : 0.0;

    /// <summary>启用面积阈值：接触面积低于阈值判为「书写」；关闭则一律按手掌擦处理。</summary>
    public bool AreaThresholdEnabled { get; set; } = true;

    /// <summary>书写是否启用压感；关闭则用固定模拟压感。</summary>
    public bool WritingUsesPressure { get; set; } = true;

    /// <summary>书写是否也随接触尺寸变化：开启后书写点大小再乘 √(接触面积 ÷ 手掌按压面积)。
    /// 关闭则书写点只随压感（恒定基准大小）。</summary>
    public bool WritingFollowSize { get; set; } = false;

    /// <summary>不启用压感时给「书写」的固定模拟压感（原生计数 512 / 1024 = 0.5）。</summary>
    public const double SimulatedWritingPressureRaw = 512;
    public static double SimulatedWritingPressure01 => SimulatedWritingPressureRaw / 1024.0;

    /// <summary>锁定手掌大小：手掌擦的擦除区「只增不减」——同一次按压内取最大值，抬手后再重来。按指独立。</summary>
    public bool LockPalmSize { get; set; } = false;

    /// <summary>倍率微调：在**自动倍率**基础上再乘（1.0 = 不调整）。</summary>
    public double RatioTrim { get; set; } = 1.0;

    // ---- 两个判定阈值（自动值来自标定，滑块只做微调）----

    /// <summary>面积判定阈值自动值（手掌擦 / 书写，mm²）。</summary>
    public double AreaThresholdBaseMm2 { get; set; }
    public double AreaThresholdTrim { get; set; } = 1.0;
    public double EffectiveAreaThresholdMm2 => AreaThresholdBaseMm2 * AreaThresholdTrim;

    /// <summary>压感判定阈值自动值（0~1）。</summary>
    public double? PressureThresholdBase { get; set; }
    public double PressureThresholdTrim { get; set; } = 1.0;
    public double? EffectivePressureThreshold => PressureThresholdBase * PressureThresholdTrim;

    // ================= 结果状态 =================

    public string SizeHint { get; private set; } = "";

    /// <summary>一根手指/接触的独立状态：多指各画各的框，滤波/峰值/锁定/判定全按指分开。</summary>
    private sealed class Track
    {
        public long LastTicks;
        public readonly List<Rect> History = new();
        public Rect? Median;             // 中值滤波后的最新接触矩形（DIU）
        public double LastMmW, LastMmH;
        public double PeakMmW, PeakMmH;
        public double LockedAreaMm2;     // 锁定模式：本指本次按压内见过的最大擦除区面积
        public bool PalmLatched;         // 锁定模式：本指已判定为手掌擦（保持到抬手，不再退回书写）
    }

    /// <summary>生效来源内的分指接触表：接触 id → 独立状态。</summary>
    private readonly Dictionary<int, Track> _tracks = new();

    /// <summary>按指的接触矩形（中值滤波后；多指=多项，单指=1 项）。抬手/超窗的指自动消失。</summary>
    public IReadOnlyList<(int Id, Rect Rect)> LastContacts
        => _tracks.Where(kv => kv.Value.Median is not null)
                  .Select(kv => (kv.Key, kv.Value.Median!.Value))
                  .ToList();

    /// <summary>兼容单指语义：面积最大的那根接触。</summary>
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

    public double LastMmW => BestTrack()?.LastMmW ?? 0;
    public double LastMmH => BestTrack()?.LastMmH ?? 0;
    public double PeakMmW => BestTrack()?.PeakMmW ?? 0;
    public double PeakMmH => BestTrack()?.PeakMmH ?? 0;

    /// <summary>全部分指的峰值面积之和（未标定时的倍率退路；单指 = 单指峰值面积）。</summary>
    public double TotalPeakAreaMm2
    {
        get
        {
            double s = 0;
            foreach (Track t in _tracks.Values)
                s += t.PeakMmW * t.PeakMmH;
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

    // ================= 统一入口：三路来源仲裁 =================

    /// <summary>提交一根接触的矩形（DIU）。contactId 标识是哪根手指（WPF=TouchDevice.Id，HID=contact id），
    /// 多根手指各调各的、各画各的框。applyThreshold=false 时不套 mm 阈值（原始 HID 的真值）。</summary>
    public void Submit(ContactSource src, int contactId, Rect rectDiu, bool applyThreshold, string detail)
    {
        long now = Environment.TickCount64;

        // 退化接触框（0×0）不携带任何尺寸信息：设备即使没抬手也会周期夹带这种"空槽位"帧。
        // 若把它记成"该来源不可用"，高优先来源会被短暂降级，与低优先来源反复横跳，而每次切换都会
        // 清空中值滤波窗 → 擦除区尺寸抖动。故直接忽略，保留上次有效值直到自然过期。
        if (rectDiu.Width <= 0 || rectDiu.Height <= 0)
            return;

        bool eligible = true;
        if (applyThreshold && MmPerDiuX > 0)
            eligible = rectDiu.Width * MmPerDiuX >= MinContactMm && rectDiu.Height * MmPerDiuY >= MinContactMm;

        _state[src] = (now, rectDiu, eligible);
        _detail[src] = detail;

        if (eligible)
        {
            if (!_validSince.ContainsKey(src))
                _validSince[src] = now;

            // 距上次有效接触超过新鲜窗口 → 视为一次新的按压，重置全部分指的峰值、滤波窗与「锁定大小」
            // （多指时其中一指抬起又按下不算新按压——另一根按着的手指一直在出数）
            if (now - _lastContactTicks > SourceFreshMs)
                _tracks.Clear();   // 关键：别把上一次按压的旧接触带进中值滤波，否则新按下的初始尺寸会偏大
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

        if (eff != _active)
        {
            _active = eff;
            _tracks.Clear(); // 换来源就重置滤波，避免不同量纲互相污染
        }

        // 原始HID（RawInput / SetupAPI 直读）= 设备直报的真值，不套 mm 阈值；其余（含软件HID/软件WPF）都用屏幕尺度过滤退化值
        bool rawHid = eff is ContactSource.RawHid or ContactSource.HidSetup;
        UpdateFromContact(contactId, rectDiu, applyThreshold: !rawHid);
        Raise();
    }

    private bool IsUsable(ContactSource s, long now)
        => _state.TryGetValue(s, out var st) && st.Eligible && now - st.Time <= SourceFreshMs;

    /// <summary>软件HID 模式下的数据源：RawInput 有货就用它，否则退回 SetupAPI 直读；都没有则 None。</summary>
    private ContactSource PreferredHid(long now)
    {
        if (IsUsable(ContactSource.RawHid, now))
            return ContactSource.RawHid;
        if (IsUsable(ContactSource.HidSetup, now))
            return ContactSource.HidSetup;
        return ContactSource.None;
    }

    /// <summary>手动指定则只认那一路；自适应则稳定识别后锁定。</summary>
    private ContactSource ResolveSource(long now)
    {
        if (Mode != SourceMode.Auto)
        {
            // 软件HID 的数据源仍是 HID（RawInput 优先、SetupAPI 直读兜底），尺寸按软件推算；
            // 软件WPF 走 WPF 框；RawInput / HidSetup 各自只认自己那一路真值。
            ContactSource want = Mode switch
            {
                SourceMode.RawInput => ContactSource.RawHid,
                SourceMode.HidSetup => ContactSource.HidSetup,
                SourceMode.SoftwareHid => PreferredHid(now),
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
        if (!tooSmall && applyThreshold && MmPerDiuX > 0)
            tooSmall = b.Width * MmPerDiuX < MinContactMm || b.Height * MmPerDiuY < MinContactMm;

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
            double raw = EraserAreaRawMm2(cur);
            if (raw > track.LockedAreaMm2)
                track.LockedAreaMm2 = raw;

            if (AreaThresholdEnabled && EffectiveAreaThresholdMm2 > 0 && MmPerDiuX > 0 && MmPerDiuY > 0
                && cur.Width * MmPerDiuX * cur.Height * MmPerDiuY >= EffectiveAreaThresholdMm2)
                track.PalmLatched = true;
        }

        if (MmPerDiuX > 0 && MmPerDiuY > 0)
        {
            track.LastMmW = cur.Width * MmPerDiuX;
            track.LastMmH = cur.Height * MmPerDiuY;

            // 峰值取"面积最大"那一帧（手掌接触面积取峰值，而非松手瞬间缩小的值）
            if (track.LastMmW * track.LastMmH > track.PeakMmW * track.PeakMmH)
            {
                track.PeakMmW = track.LastMmW;
                track.PeakMmH = track.LastMmH;
            }
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

    /// <summary>自动倍率 k = √(a / b)：a = 手掌面积，b = **标定测得的手掌按压面积**（定值）。
    /// 未标定才退回"本次按压峰值"（多指 = 全部分指峰值面积之和）。数据不足返回 0；不设上限。</summary>
    public double ComputeAutoRatio()
    {
        if (PalmAreaCm2 <= 0)
            return 0;
        double aMm2 = PalmAreaCm2 * 100.0;
        double bMm2 = PalmPressAreaMm2 > 0 ? PalmPressAreaMm2 : TotalPeakAreaMm2;
        if (!(bMm2 > 0))
            return 0;
        double k = Math.Sqrt(aMm2 / bMm2);
        return double.IsFinite(k) && k > 0 ? k : 0;
    }

    /// <summary>当前实际生效的倍率：自动倍率（数据不足按 1 计）× 微调。</summary>
    public double EffectiveRatio()
    {
        double auto = ComputeAutoRatio();
        if (!(auto > 0))
            auto = 1.0;
        return auto * RatioTrim;
    }

    /// <summary>随压力倍数（压力系数增益）：1.0 = 默认（系数 = 压力比）；&gt;1 更易达到满幅，&lt;1 更难。</summary>
    public double PressureGain { get; set; } = 1.0;

    /// <summary>书写压感基础幅度（px）：倍数 = 1 时的满量程增幅。</summary>
    public const double WritingPressureBaseAmp = 22;

    /// <summary>书写压感倍数：1.0 = 默认（幅度 22px）；&gt;1 变化更明显，&lt;1 更平缓。</summary>
    public double WritingPressureGain { get; set; } = 1.0;

    /// <summary>压力系数 = 1 + (超出标定掌压的部分) × 倍数。
    /// 即：低于标定掌压一律按 1（不变小），超过标定掌压才开始按倍数放大。</summary>
    private double PressureFactor()
    {
        double p = EffectivePressure01;
        double refP = PressureThresholdBase is double b && b > 0 ? b : 1.0;
        double over = Math.Max(0, p / refP - 1.0);                 // 只取"超过标定掌压"的部分
        return 1.0 + Math.Clamp(PressureGain, 0, 1.5) * over;      // 归一(=1) + 超出部分 × 倍数
    }

    /// <summary>擦除区面积（mm²）：随尺寸 = 接触面积 × 倍率²（否则固定手掌面积）；随压力再乘压力系数。</summary>
    private double EraserAreaRawMm2(Rect c)
    {
        double area = FollowSize
            ? c.Width * MmPerDiuX * c.Height * MmPerDiuY * Math.Pow(EffectiveRatio(), 2)
            : PalmAreaCm2 * 100.0;
        if (FollowPressure)
            area *= PressureFactor();
        return area;
    }

    /// <summary>某根接触的擦除区面积（mm²）：开启「锁定手掌大小」时返回本指本次按压内见过的最大值（只增不减）。</summary>
    public double EraserAreaMm2(int id, Rect c)
        => LockPalmSize && _tracks.TryGetValue(id, out Track? t) && t.LockedAreaMm2 > 0
            ? t.LockedAreaMm2
            : EraserAreaRawMm2(c);

    /// <summary>某根接触的擦除区矩形尺寸（DIU）：面积按模式算，长宽比优先用①页设定的手掌长宽比。</summary>
    public (double w, double h) RectSizeDiu(int id, Rect c)
    {
        if (MmPerDiuX <= 0 || MmPerDiuY <= 0)
            return (0, 0);

        double areaMm2 = EraserAreaMm2(id, c);
        if (!(areaMm2 > 0))
            return (0, 0);

        double areaDiu2 = areaMm2 / (MmPerDiuX * MmPerDiuY);
        if (!double.IsFinite(areaDiu2) || areaDiu2 <= 0)
            return (0, 0);

        Track? t = _tracks.TryGetValue(id, out Track? track) ? track : null;
        double aspect = PalmAspect > 0
            ? PalmAspect
            : (t is not null && t.PeakMmW > 0 && t.PeakMmH > 0
                ? t.PeakMmW / t.PeakMmH
                : (c.Height > 0 && c.Width > 0 ? c.Width / c.Height : 1));
        if (!(aspect > 0) || !double.IsFinite(aspect))
            aspect = 1;

        double h = Math.Sqrt(areaDiu2 / aspect);
        double w = aspect * h;
        return double.IsFinite(w) && double.IsFinite(h) && w > 0 && h > 0 ? (w, h) : (0, 0);
    }

    /// <summary>某根接触的擦除区为正圆时的直径（DIU）；不可用返回 null。</summary>
    public double? CircleDiameterDiu(int id, Rect c)
    {
        if (MmPerDiuX <= 0 || MmPerDiuY <= 0)
            return null;

        double areaMm2 = EraserAreaMm2(id, c);   // 与矩形共用同一面积（含「锁定手掌大小」）
        if (!(areaMm2 > 0))
            return null;

        double areaDiu2 = areaMm2 / (MmPerDiuX * MmPerDiuY);
        if (!double.IsFinite(areaDiu2) || areaDiu2 <= 0)
            return null;

        double d = Math.Sqrt(4 * areaDiu2 / Math.PI);
        return double.IsFinite(d) && d > 0 ? d : null;
    }

    /// <summary>界面改了形状/倍率/随压力等设置后，通知重画。</summary>
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

    /// <summary>倍率框文字：自动值 + 微调 + 生效值。</summary>
    public string RatioInfo()
    {
        double auto = ComputeAutoRatio();
        string autoText = auto > 0 ? $"{Precision.Fmt(auto)} ×" : "1.000000 ×（数据不足，按 1 计）";
        return $"自动 = {autoText}\n微调 ×{Precision.Fmt(RatioTrim, 2)} → 生效 {Precision.Fmt(auto * RatioTrim)} ×";
    }

    /// <summary>面积阈值框文字：自动 + 微调 + 生效。</summary>
    public string AreaThresholdInfo()
        => AreaThresholdBaseMm2 > 0
            ? $"自动 {Precision.Fmt(AreaThresholdBaseMm2, 0)} mm²\n微调 ×{Precision.Fmt(AreaThresholdTrim, 2)} → 生效 {Precision.Fmt(EffectiveAreaThresholdMm2, 0)} mm²"
            : "自动：待标定（需在第 5、6 步记录手掌与手指按压）";

    /// <summary>压感阈值框文字：自动 + 微调 + 生效。</summary>
    public string PressureThresholdInfo()
        => PressureThresholdBase is double b && b > 0
            ? $"自动 {Precision.Fmt(b, 2)}\n微调 ×{Precision.Fmt(PressureThresholdTrim, 2)} → 生效 {Precision.Fmt(EffectivePressureThreshold ?? 0, 2)}"
            : "自动：无压感 / 未采到";

    /// <summary>当前是否还有"新鲜"的接触（抬手/静默过期后为 false）。</summary>
    public bool HasLiveContact => _active != ContactSource.None && _tracks.Count > 0;

    /// <summary>某根接触是否判为「书写」：启用面积阈值，且该指接触面积 &lt; 阈值（按指独立判定）。
    /// 例外：开启「锁定手掌大小」时，本指一旦判为手掌擦就保持到抬手，不再退回书写。</summary>
    public bool IsWriting(int id)
    {
        if (!AreaThresholdEnabled)
            return false;
        if (!_tracks.TryGetValue(id, out Track? t) || t.Median is not Rect c)
            return false;

        // 锁定：本指已锁定为手掌擦 → 保持（面积小到阈值以下也不退回书写，直到抬手）
        if (LockPalmSize && t.PalmLatched)
            return false;

        if (EffectiveAreaThresholdMm2 <= 0 || MmPerDiuX <= 0 || MmPerDiuY <= 0)
            return false;
        return c.Width * MmPerDiuX * c.Height * MmPerDiuY < EffectiveAreaThresholdMm2;
    }

    /// <summary>书写时实际使用的压感（0~1）：启用压感→实测（抬手/停报按 0）；关闭→固定模拟 512/1024。</summary>
    public double WritingPressure01 =>
        WritingUsesPressure ? EffectivePressure01 : SimulatedWritingPressure01;

    /// <summary>书写点直径（px）= 6 + 22 × 倍数 × 压感；压感为 0 时固定为 6，倍数不影响。
    /// 开启「书写也随尺寸」时再乘 √(接触面积 ÷ 手掌按压面积)。</summary>
    public double WritingDotDiameter => WritingDotDiameterOf(null);

    /// <summary>指定接触的书写点直径（px）：开启「书写随尺寸」时按该接触面积缩放；传 null 用最大那根。</summary>
    public double WritingDotDiameterOf(Rect? contact)
    {
        double baseD = 6 + WritingPressureBaseAmp * Math.Clamp(WritingPressureGain, 0, 5)
            * Math.Clamp(WritingPressure01, 0, 1);

        if (!WritingFollowSize || contact is not Rect c || MmPerDiuX <= 0 || MmPerDiuY <= 0)
            return baseD;

        // 与手掌擦同一套「倍率」语义：√(手掌面积 ÷ 手掌按压面积)，只是这里用当前接触面积做分子
        double pressMm2 = PalmPressAreaMm2 > 0 ? PalmPressAreaMm2 : TotalPeakAreaMm2;
        if (!(pressMm2 > 0))
            return baseD;
        double areaMm2 = c.Width * MmPerDiuX * c.Height * MmPerDiuY;
        if (!(areaMm2 > 0))
            return baseD;

        double k = Math.Sqrt(areaMm2 / pressMm2);
        return double.IsFinite(k) && k > 0 ? baseD * k : baseD;
    }

    /// <summary>手掌擦 / 书写 判定说明（多指 = 每指各判各的）。</summary>
    public string JudgeInfo()
    {
        IReadOnlyList<(int Id, Rect Rect)> list = LastContacts;
        if (list.Count == 0)
            return "判定：—（无接触）";
        if (!AreaThresholdEnabled)
            return "判定：手掌擦（面积阈值已关闭 → 永远手掌擦）";
        if (EffectiveAreaThresholdMm2 <= 0)
            return "判定：手掌擦（面积阈值未标定）";

        string head = $"阈值 {Precision.Fmt(EffectiveAreaThresholdMm2, 0)} mm² → ";
        var parts = new List<string>(list.Count);
        bool anyWriting = false;
        foreach ((int id, Rect c) in list)
        {
            double area = c.Width * MmPerDiuX * c.Height * MmPerDiuY;
            bool writing = IsWriting(id);
            anyWriting |= writing;
            parts.Add(list.Count > 1
                ? $"#{id} {Precision.Fmt(area, 0)}mm² {(writing ? "书写" : "手掌擦")}"
                : $"{Precision.Fmt(area, 0)}mm² {(writing ? "书写" : "手掌擦")}");
        }

        string press = anyWriting
            ? (WritingUsesPressure
                ? $"（压感 实测 {Precision.Fmt(WritingPressure01, 3)}）"
                : $"（压感 模拟 {SimulatedWritingPressureRaw:0}/1024 = {Precision.Fmt(SimulatedWritingPressure01, 3)}）")
            : "";
        return "判定：" + head + string.Join(" ｜ ", parts) + press;
    }

    public string EraserInfo()
    {
        IReadOnlyList<(int Id, Rect Rect)> list = LastContacts;
        if (list.Count == 0 || MmPerDiuX <= 0)
            return "尚无驱动接触数据（需真触摸屏；鼠标/部分驱动不提供接触尺寸）。";

        double contactSum = 0, eraseSum = 0;
        var per = new List<string>(list.Count);
        foreach ((int id, Rect c) in list)
        {
            double inW = c.Width * MmPerDiuX;
            double inH = c.Height * MmPerDiuY;
            contactSum += inW * inH;
            eraseSum += EraserAreaMm2(id, c);
            per.Add(list.Count > 1
                ? $"#{id} {Precision.Fmt(inW)}×{Precision.Fmt(inH)}"
                : $"{Precision.Fmt(inW)}×{Precision.Fmt(inH)}");
        }

        double autoK = ComputeAutoRatio();
        string ratioText = (autoK > 0 ? $"自动 {Precision.Fmt(autoK)} ×" : "自动数据不足（按 1 计）")
            + $" 微调 ×{Precision.Fmt(RatioTrim, 2)} → 生效 {Precision.Fmt(EffectiveRatio())} ×";

        string sizeText = FollowSize
            ? (list.Count > 1
                ? $"随接触尺寸: {list.Count} 指 Σ {Precision.Fmt(contactSum, 0)} mm²（{string.Join("，", per)} mm） × 倍率 {ratioText}"
                : $"随接触尺寸: {per[0]} mm = {Precision.Fmt(contactSum, 0)} mm² × 倍率 {ratioText}")
            : (PalmAreaCm2 > 0
                ? $"固定手掌面积: {Precision.Fmt(PalmAreaCm2)} cm² = {Precision.Fmt(PalmAreaCm2 * 100, 0)} mm²"
                : "尚未测出手掌面积——请先到「① 手掌尺寸」页描一圈或填入宽高。");

        string pressText = FollowPressure
            ? $"\n手掌擦压感: 当前 {Precision.Fmt(EffectivePressure01, 3)} ÷ 手掌压力 "
              + $"{(PressureThresholdBase is double pb && pb > 0 ? Precision.Fmt(pb, 3) : "未标定(按满量程1)")}"
              + $" → 系数 {Precision.Fmt(PressureFactor(), 3)}（倍数 ×{Precision.Fmt(PressureGain, 2)}）"
            : "";

        return $"模式: {(FollowSize ? "随尺寸" : "固定手掌")}{(FollowPressure ? " + 手掌擦压感" : "")}{(LockPalmSize ? " + 锁定大小(只增不减)" : "")}\n"
             + sizeText + pressText
             + $"\n擦除区: Σ {Precision.Fmt(eraseSum, 0)} mm²（{(Shape == EraserShape.Circle ? "各指等面积圆" : "各指矩形")}，见预览区）";
    }
}
