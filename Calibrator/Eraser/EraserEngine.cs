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
    public const double MinContactMm = 1.0;       // 小于此物理尺寸视为"驱动未上报有效接触面积"
    public const int MinHandPeakCount = 5;        // 手掌接触的最小计数
    public const int MaxSimCount = 2000;          // 自测注入的计数上限
    public const double MaxInjectMm = 500;        // 自测注入的物理尺寸上限

    private static readonly ContactSource[] PriorityOrder =
        { ContactSource.RawHid, ContactSource.Wpf };

    // ================= 由外部喂入 =================

    public double MmPerDiuX { get; set; }
    public double MmPerDiuY { get; set; }
    public double PalmAreaCm2 { get; set; }
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

    /// <summary>启用面积阈值：接触面积低于阈值判为「书写」；关闭则一律按手掌擦处理。</summary>
    public bool AreaThresholdEnabled { get; set; } = true;

    /// <summary>书写是否启用压感；关闭则用固定模拟压感。</summary>
    public bool WritingUsesPressure { get; set; } = true;

    /// <summary>不启用压感时给「书写」的固定模拟压感（原生计数 512 / 1024 = 0.5）。</summary>
    public const double SimulatedWritingPressureRaw = 512;
    public static double SimulatedWritingPressure01 => SimulatedWritingPressureRaw / 1024.0;

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

    public double CountsToMmScale { get; private set; }
    public int HandPeakCount { get; private set; }
    public Rect? LastContact { get; private set; }
    public double LastMmW { get; private set; }
    public double LastMmH { get; private set; }
    public double PeakMmW { get; private set; }
    public double PeakMmH { get; private set; }
    public string SizeHint { get; private set; } = "";
    public bool HidScaleHintShown { get; private set; }

    private readonly Dictionary<ContactSource, (long Time, Rect Rect, bool Eligible)> _state = new();
    private readonly Dictionary<ContactSource, string> _detail = new();
    private readonly Dictionary<ContactSource, long> _validSince = new();
    private readonly List<Rect> _history = new();
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
        _history.Clear();
        LastContact = null;
        PeakMmW = PeakMmH = 0;
        Raise();
    }

    // ================= 统一入口：三路来源仲裁 =================

    /// <summary>提交一次接触矩形（DIU）。applyThreshold=false 时不套 mm 阈值（原始 HID 的真值）。</summary>
    public void Submit(ContactSource src, Rect rectDiu, bool applyThreshold, string detail)
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

            // 距上次有效接触超过新鲜窗口 → 视为一次新的按压，重置峰值
            if (now - _lastContactTicks > SourceFreshMs)
                PeakMmW = PeakMmH = 0;
            _lastContactTicks = now;
        }
        else
        {
            _validSince.Remove(src);
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
            _history.Clear(); // 换来源就重置滤波，避免不同量纲互相污染
        }

        UpdateFromContact(_state[eff].Rect, applyThreshold: eff != ContactSource.RawHid);
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
                SourceMode.Pointer => ContactSource.Pointer,
                SourceMode.Wpf => ContactSource.Wpf,
                _ => ContactSource.None,
            };
            return IsUsable(want, now) ? want : ContactSource.None;
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

    /// <summary>定时刷新：所有来源过期后把生效来源归零，界面显示「无」。</summary>
    public void Tick()
    {
        long now = Environment.TickCount64;
        bool anyFresh = false;
        foreach (ContactSource s in PriorityOrder)
            if (IsUsable(s, now)) { anyFresh = true; break; }

        if (!anyFresh && _active != ContactSource.None)
            _active = ContactSource.None;

        Raise();
    }

    // ================= 滤波 + 计算 =================

    private void UpdateFromContact(Rect b, bool applyThreshold)
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
                if (!HidScaleHintShown) SizeHint += " 面积擦需要能上报接触尺寸的数字化器（真触摸屏/笔）。";
            }
            return;
        }

        // 同一帧可能被元素事件和全局帧事件重复喂入：相同值只算一次，避免滤波窗被占满
        if (_history.Count > 0)
        {
            Rect last = _history[^1];
            if (Math.Abs(last.X - b.X) < 0.01 && Math.Abs(last.Y - b.Y) < 0.01 &&
                Math.Abs(last.Width - b.Width) < 0.01 && Math.Abs(last.Height - b.Height) < 0.01)
                return;
        }

        _history.Add(b);
        if (_history.Count > ContactHistoryMax)
            _history.RemoveAt(0);

        LastContact = MedianRect(_history);
        Rect cur = LastContact.Value;

        if (MmPerDiuX > 0 && MmPerDiuY > 0)
        {
            LastMmW = cur.Width * MmPerDiuX;
            LastMmH = cur.Height * MmPerDiuY;

            // 峰值取"面积最大"那一帧（手掌接触面积取峰值，而非松手瞬间缩小的值）
            if (LastMmW * LastMmH > PeakMmW * PeakMmH)
            {
                PeakMmW = LastMmW;
                PeakMmH = LastMmH;
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

    /// <summary>自动倍率 k = √(a / b)：a = 手掌面积，b = 手掌按压峰值。数据不足返回 0；不设上限。</summary>
    public double ComputeAutoRatio()
    {
        if (PalmAreaCm2 <= 0 || PeakMmW <= 0 || PeakMmH <= 0)
            return 0;
        double aMm2 = PalmAreaCm2 * 100.0;
        double bMm2 = PeakMmW * PeakMmH;
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

    /// <summary>压力系数 = 当前压力 ÷ 参考压力（=手掌按压压力），裁剪到 0~1。</summary>
    private double PressureFactor()
    {
        double p = Math.Clamp(CurrentPressure ?? 0, 0, 1);
        double refP = PressureThresholdBase is double b && b > 0 ? b : 1.0;
        return Math.Clamp(p / refP, 0, 1);
    }

    /// <summary>擦除区面积（mm²）：随尺寸 = 接触面积 × 倍率²（否则固定手掌面积）；随压力再乘压力系数。</summary>
    public double EraserAreaMm2(Rect c)
    {
        double area = FollowSize
            ? c.Width * MmPerDiuX * c.Height * MmPerDiuY * Math.Pow(EffectiveRatio(), 2)
            : PalmAreaCm2 * 100.0;
        if (FollowPressure)
            area *= PressureFactor();
        return area;
    }

    private double PalmAreaDiu2()
    {
        if (PalmAreaCm2 <= 0 || MmPerDiuX <= 0 || MmPerDiuY <= 0)
            return 0;
        double a = PalmAreaCm2 * 100.0 / (MmPerDiuX * MmPerDiuY);
        return double.IsFinite(a) && a > 0 ? a : 0;
    }

    /// <summary>擦除区矩形尺寸（DIU）：面积按模式算，长宽比优先用①页设定的手掌长宽比。</summary>
    public (double w, double h) RectSizeDiu(Rect c)
    {
        if (MmPerDiuX <= 0 || MmPerDiuY <= 0)
            return (0, 0);

        double areaMm2 = EraserAreaMm2(c);
        if (!(areaMm2 > 0))
            return (0, 0);

        double areaDiu2 = areaMm2 / (MmPerDiuX * MmPerDiuY);
        if (!double.IsFinite(areaDiu2) || areaDiu2 <= 0)
            return (0, 0);

        double aspect = PalmAspect > 0
            ? PalmAspect
            : (PeakMmW > 0 && PeakMmH > 0
                ? PeakMmW / PeakMmH
                : (c.Height > 0 && c.Width > 0 ? c.Width / c.Height : 1));
        if (!(aspect > 0) || !double.IsFinite(aspect))
            aspect = 1;

        double h = Math.Sqrt(areaDiu2 / aspect);
        double w = aspect * h;
        return double.IsFinite(w) && double.IsFinite(h) && w > 0 && h > 0 ? (w, h) : (0, 0);
    }

    /// <summary>擦除区为正圆时的直径（DIU）；不可用返回 null。</summary>
    public double? CircleDiameterDiu(Rect c)
    {
        double r = EffectiveRatio();
        double areaDiu2 = FollowSize ? c.Width * c.Height * r * r : PalmAreaDiu2();
        if (FollowPressure)
            areaDiu2 *= PressureFactor();
        if (!double.IsFinite(areaDiu2) || areaDiu2 <= 0)
            return null;
        double d = Math.Sqrt(4 * areaDiu2 / Math.PI);
        return double.IsFinite(d) && d > 0 ? d : null;
    }

    // ================= HID 计数 → mm 标定 =================

    /// <summary>记录手掌按压时的峰值计数（供标定用）。</summary>
    public void NoteHandPeakCount(int peak)
    {
        if (peak > HandPeakCount)
        {
            HandPeakCount = peak;
            Raise();
        }
    }

    /// <summary>原始HID 只给逻辑计数：记录峰值，并在"无物理单位且未标定"时给出明确引导。</summary>
    public void NoteRawCounts(int w, int h)
    {
        NoteHandPeakCount(Math.Max(w, h));

        if (CountsToMmScale <= 0 && !HidScaleHintShown && (w > 0 || h > 0))
        {
            HidScaleHintShown = true;
            SizeHint =
                "该设备只上报逻辑计数（无毫米单位），当前未标定，因此还算不出擦除区。\n" +
                "请依次：① 在「① 手掌尺寸」页量出手掌面积 → ② 回到本页用整只手按一下 → ③ 点「标定 HID 计数→mm」。";
            Raise();
        }
    }

    /// <summary>界面改了形状/倍率/随压力等设置后，通知重画。</summary>
    public void NotifyChanged() => Raise();

    /// <summary>用①的手掌面积标定「1 计数 = ? mm」。返回是否成功与提示语。</summary>
    public bool CalibrateHidScale(out string message)
    {
        if (PalmAreaCm2 <= 0)
        {
            message = "请先到「① 手掌尺寸」页描一圈或填入手掌宽高。";
            Log.Warn("HID 标定中止: " + message);
            return false;
        }
        if (HandPeakCount <= 0)
        {
            message = "请先在本页用整只手（含手指）按一下，采到计数后再点标定。";
            Log.Warn("HID 标定中止: " + message);
            return false;
        }
        if (HandPeakCount < MinHandPeakCount)
        {
            message = $"采到的峰值计数只有 {HandPeakCount}（< {MinHandPeakCount}），像是单指轻按或该设备不上报有效尺寸。" +
                      "请用整只手重按一次；或改用「1 计数 = ? mm」手动设置。";
            Log.Warn("HID 标定中止: " + message);
            return false;
        }

        double diameterMm = 2 * Math.Sqrt(PalmAreaCm2 * 100 / Math.PI);
        CountsToMmScale = diameterMm / HandPeakCount;
        HidScaleHintShown = true;
        _driverSizeWarned = false;
        message = $"标定完成：1 计数 ≈ {Precision.Fmt(CountsToMmScale)} mm。现在按手掌即可看到等大擦除区。";
        Log.Info($"HID 标定完成: 手掌面积={Precision.Fmt(PalmAreaCm2)}cm² 峰值计数={HandPeakCount} → 1 计数={Precision.Fmt(CountsToMmScale)}mm");
        Raise();
        return true;
    }

    public bool ApplyManualScale(double mmPerCount, out string message)
    {
        if (!(mmPerCount > 0) || mmPerCount > 100)
        {
            message = "请输入 0~100 之间的 mm/计数。";
            Log.Warn("手动比例设置中止: " + message);
            return false;
        }
        CountsToMmScale = mmPerCount;
        HidScaleHintShown = true;
        message = $"已设置：1 计数 = {Precision.Fmt(mmPerCount)} mm。可点「注入」自测整条链路。";
        Log.Info($"手动比例: 1 计数 = {Precision.Fmt(mmPerCount)} mm");
        Raise();
        return true;
    }

    /// <summary>注入一组模拟原始 HID 计数（无触摸硬件时自测）。</summary>
    public bool Inject(int count, Point centerDiu, out string message)
    {
        if (count <= 0 || count > MaxSimCount)
        {
            message = $"请输入 1~{MaxSimCount} 的模拟计数。";
            return false;
        }
        if (CountsToMmScale <= 0)
        {
            message = "请先设置「1 计数 = ? mm」或完成手掌标定。";
            return false;
        }
        if (MmPerDiuX <= 0 || MmPerDiuY <= 0)
        {
            message = "尚未校准，无法换算。";
            return false;
        }

        double wMm = Math.Min(count * CountsToMmScale, MaxInjectMm);
        double wDiu = wMm / MmPerDiuX;
        double hDiu = wMm / MmPerDiuY;
        HidScaleHintShown = true;
        Submit(ContactSource.RawHid,
            new Rect(centerDiu.X - wDiu / 2, centerDiu.Y - hDiu / 2, wDiu, hDiu),
            applyThreshold: false,
            detail: $"W={count} H={count}(模拟) → {Precision.Fmt(wMm)}×{Precision.Fmt(wMm)} mm");
        message = $"已注入：{count} 计数 → {Precision.Fmt(wMm)} mm";
        Log.Info($"注入模拟计数 {count} → {Precision.Fmt(wMm)}mm（用于无触摸硬件时自测）");
        return true;
    }

    public void Clear()
    {
        _history.Clear();
        LastContact = null;
        PeakMmW = PeakMmH = 0;
        LastMmW = LastMmH = 0;
        _lastContactTicks = 0;
        _driverSizeWarned = false;
        _state.Clear();
        _detail.Clear();
        _active = ContactSource.None;
        _locked = ContactSource.None;
        _validSince.Clear();
        HandPeakCount = 0;
        HidScaleHintShown = false;
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

    public string HidScaleInfo()
    {
        string scale = CountsToMmScale > 0 ? $"{Precision.Fmt(CountsToMmScale)} mm/计数" : "未标定";
        string area = PalmAreaCm2 > 0 ? $"{Precision.Fmt(PalmAreaCm2)} cm²" : "未测";
        return $"HID 计数标定: {scale}    手掌峰值计数: {HandPeakCount}    手掌面积: {area}";
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

    /// <summary>当前是否判为「书写」：启用面积阈值，且接触面积 &lt; 阈值。</summary>
    public bool IsWriting()
    {
        if (!AreaThresholdEnabled || LastContact is not Rect c)
            return false;
        if (EffectiveAreaThresholdMm2 <= 0 || MmPerDiuX <= 0 || MmPerDiuY <= 0)
            return false;
        return c.Width * MmPerDiuX * c.Height * MmPerDiuY < EffectiveAreaThresholdMm2;
    }

    /// <summary>书写时实际使用的压感（0~1）：启用压感→实测；关闭→固定模拟 512/1024。</summary>
    public double WritingPressure01 =>
        WritingUsesPressure ? Math.Clamp(CurrentPressure ?? 0, 0, 1) : SimulatedWritingPressure01;

    /// <summary>手掌擦 / 书写 判定说明。</summary>
    public string JudgeInfo()
    {
        if (LastContact is not Rect c)
            return "判定：—（无接触）";
        if (!AreaThresholdEnabled)
            return "判定：手掌擦（面积阈值已关闭 → 永远手掌擦）";
        if (EffectiveAreaThresholdMm2 <= 0)
            return "判定：手掌擦（面积阈值未标定）";

        double area = c.Width * MmPerDiuX * c.Height * MmPerDiuY;
        string head = $"接触 {Precision.Fmt(area, 0)} ｜ 阈值 {Precision.Fmt(EffectiveAreaThresholdMm2, 0)} mm² → ";
        if (area >= EffectiveAreaThresholdMm2)
            return head + "手掌擦";
        return WritingUsesPressure
            ? head + $"书写（压感 实测 {Precision.Fmt(WritingPressure01, 3)}）"
            : head + $"书写（压感 模拟 {SimulatedWritingPressureRaw:0}/1024 = {Precision.Fmt(SimulatedWritingPressure01, 3)}）";
    }

    public string EraserInfo()
    {
        if (LastContact is not Rect c || MmPerDiuX <= 0)
            return "尚无驱动接触数据（需真触摸屏；鼠标/部分驱动不提供接触尺寸）。";

        double inW = c.Width * MmPerDiuX;
        double inH = c.Height * MmPerDiuY;
        double areaMm2 = EraserAreaMm2(c);
        var (rectW, rectH) = RectSizeDiu(c);

        string shape = Shape == EraserShape.Circle
            ? $"等面积圆 ⌀{Precision.Fmt(2 * Math.Sqrt(Math.Max(areaMm2, 0) / Math.PI))} mm（面积 {Precision.Fmt(areaMm2, 0)} mm²）"
            : $"矩形 {Precision.Fmt(rectW * MmPerDiuX)} × {Precision.Fmt(rectH * MmPerDiuY)} mm（面积 {Precision.Fmt(areaMm2, 0)} mm²）";

        double autoK = ComputeAutoRatio();
        string ratioText = (autoK > 0 ? $"自动 {Precision.Fmt(autoK)} ×" : "自动数据不足（按 1 计）")
            + $" 微调 ×{Precision.Fmt(RatioTrim, 2)} → 生效 {Precision.Fmt(EffectiveRatio())} ×";

        string sizeText = FollowSize
            ? $"随接触尺寸: {Precision.Fmt(inW)} × {Precision.Fmt(inH)} mm = {Precision.Fmt(inW * inH, 0)} mm² × 倍率 {ratioText}"
            : (PalmAreaCm2 > 0
                ? $"固定手掌面积: {Precision.Fmt(PalmAreaCm2)} cm² = {Precision.Fmt(PalmAreaCm2 * 100, 0)} mm²"
                : "尚未测出手掌面积——请先到「① 手掌尺寸」页描一圈或填入宽高。");

        string pressText = FollowPressure
            ? $"\n随压力: 当前 {Precision.Fmt(CurrentPressure ?? 0, 3)} ÷ 手掌压力 "
              + $"{(PressureThresholdBase is double pb && pb > 0 ? Precision.Fmt(pb, 3) : "未标定(按满量程1)")}"
              + $" → 系数 {Precision.Fmt(PressureFactor(), 3)}"
            : "";

        return $"模式: {(FollowSize ? "随尺寸" : "固定手掌")}{(FollowPressure ? " + 随压力" : "")}\n"
             + sizeText + pressText
             + $"\n擦除区: {shape}";
    }
}
