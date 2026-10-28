using System.Diagnostics;

namespace TouchSuite.Ink;

/// <summary>
/// 笔迹位置模型：移植 Chromium ink_stroke_modeler 的核心三步（Inkeys 3 用的就是它）。
///
/// <para>① <b>Wobble 平滑</b>：对原始采样做"按时间加权的滑动平均"，再按平均速度在
/// 「均值位置」与「原始位置」之间插值 —— 低速几乎全用均值（抑制数字板抖动），
/// 快速书写时逼近原始位置（不拖泥带水）。</para>
///
/// <para>② <b>弹簧-质量-阻尼</b>：笔尖不是直接跳到输入点，而是被一根弹簧拉着追输入，
/// 同时受阻尼器减速。这是"顺滑但不飘"的来源：位置二阶连续，速度不会突变。</para>
///
/// <para>③ <b>固定间隔重采样</b>：把每次输入按 min_output_rate 拆成若干小步逐积分，
/// 输出间隔均匀的点。这样渲染与输入采样率解耦，画出来的折线段足够短，看不出棱角。</para>
///
/// 参数与 Inkeys 3（Draw3.InkPrediction.cpp 的 Fps60 profile）一致；
/// 单位：位置 = 屏幕物理像素，时间 = 秒。
/// </summary>
internal sealed class StrokeModeler
{
    private const double SpringMass = 11.0 / 32400.0;   // s²，弹簧质量的倒数（Chromium 默认）
    private const double Drag = 72.0;                   // 1/s，阻尼
    private const double MinOutputRate = 240.0;         // Hz，重采样输出率
    private const double WobbleTimeout = 2.5 / 60.0;    // s，滑动平均窗口
    private const double ExpectedSpeed = 500.0;         // px/s，96dpi 下的期望书写速度
    private const double WobbleSpeedFloor = 0.015 * ExpectedSpeed;     // 7.5 px/s：以下全用均值
    private const double WobbleSpeedCeiling = 0.08 * ExpectedSpeed;    // 40 px/s：以上不用平滑
    private const int MaxStepsPerInput = 480;           // 单次输入最多拆多少步（防挂起后爆量）

    private sealed class WobbleSample
    {
        public double X, Y, T;      // 原始位置与时间
        public double Wx, Wy;       // 位置 × 停留时长（加权和用）
        public double Dur;          // 距上一个采样的时长
        public double Dist;         // 距上一个采样的距离
    }

    private readonly List<WobbleSample> _window = new();
    private double _sumWx, _sumWy, _sumDur, _sumDist;

    private double _tipX, _tipY;          // 模型笔尖（弹簧端点）
    private double _velX, _velY;          // 笔尖速度 px/s
    private double _time;                 // 模型当前时间（s）
    private double _anchorX, _anchorY;    // 上一次输入（wobble 平滑后）的位置
    private double _lastInputT;           // 上一次输入的时间
    private float _lastWidth;
    private readonly Queue<float> _widths = new();   // 最近几个原始笔宽（中值滤波用）
    private double _rawX, _rawY;                     // 最后一个**原始**采样位置（抬笔收尾要追到它）

    /// <summary>模型笔尖位置（屏幕物理像素）。</summary>
    public float X => (float)_tipX;
    public float Y => (float)_tipY;

    /// <summary>笔尖速度（px/s）。预测段用它外推。</summary>
    public float VelX => (float)_velX;
    public float VelY => (float)_velY;
    public float Speed => (float)Math.Sqrt(_velX * _velX + _velY * _velY);

    /// <summary>是否已经有可用的速度估计（采样太少时预测没有意义）。</summary>
    public bool HasVelocity { get; private set; }

    public void Reset(InkSample s)
    {
        _window.Clear();
        _sumWx = _sumWy = _sumDur = _sumDist = 0;

        _tipX = s.X;
        _tipY = s.Y;
        _velX = _velY = 0;
        _time = TicksToSeconds(s.Ticks);
        _anchorX = s.X;
        _anchorY = s.Y;
        _rawX = s.X;
        _rawY = s.Y;
        _lastInputT = _time;
        _lastWidth = s.Width;
        HasVelocity = false;

        _widths.Clear();
        _widths.Enqueue(s.Width);

        _window.Add(new WobbleSample { X = s.X, Y = s.Y, T = _time });
    }

    /// <summary>
    /// 吃一个原始采样，把这段位移按固定输出率拆成若干步积分，追加到 <paramref name="output"/>。
    /// 返回新追加的点数。
    /// </summary>
    public int Update(InkSample s, List<InkSample> output)
    {
        double t = TicksToSeconds(s.Ticks);
        double dt = t - _lastInputT;
        if (dt <= 0)
            return 0;                       // 时间戳相同/倒流：不建模，由调用方补原始点
        if (dt > 0.5)
        {
            // 中间断过（设备挂起/丢包）：直接用原始点重启模型，避免一次性补上千步
            Reset(s);
            return 0;
        }

        (double ax, double ay) = Wobble(s.X, s.Y, t);

        int steps = (int)Math.Ceiling(dt * MinOutputRate);
        steps = Math.Clamp(steps, 1, MaxStepsPerInput);

        double stepDt = dt / steps;
        double fromX = _anchorX, fromY = _anchorY;
        float fromW = _lastWidth;
        // 笔宽做中值滤波：有些面板/驱动的压感（或接触面积换算出的压力）逐点跳变，
        // 直接跟会画成"一会细一会粗"。中值只去尖峰、不改变真正缓慢升降的压感。
        float toW = MedianWidth(s.Width);
        int added = 0;

        for (int i = 1; i <= steps; i++)
        {
            double u = (double)i / steps;
            double anchorX = fromX + (ax - fromX) * u;   // 输入点之间线性插值当"锚点"
            double anchorY = fromY + (ay - fromY) * u;

            // 弹簧 + 阻尼积分（半隐式欧拉，与 Chromium 一致）
            double accX = (anchorX - _tipX) / SpringMass - Drag * _velX;
            double accY = (anchorY - _tipY) / SpringMass - Drag * _velY;
            _velX += accX * stepDt;
            _velY += accY * stepDt;
            _tipX += _velX * stepDt;
            _tipY += _velY * stepDt;
            _time += stepDt;

            float w = (float)(fromW + (toW - fromW) * u);
            output.Add(new InkSample((float)_tipX, (float)_tipY, w, s.R, s.G, s.B, s.A, s.Ticks));
            added++;
        }

        _anchorX = ax;
        _anchorY = ay;
        _lastInputT = t;
        _lastWidth = toW;
        _rawX = s.X;
        _rawY = s.Y;

        // 速度稳定到"真的在动"之后才允许预测（Chromium 的 confidence 里也是按速度门限来的）
        if (_window.Count >= 4)
            HasVelocity = true;

        return added;
    }

    /// <summary>
    /// wobble 平滑：返回"按速度混合后的输入位置"。
    /// 均值按每段停留时长加权（而不是简单算术平均），所以手停住时不会漂。
    /// </summary>
    private (double X, double Y) Wobble(float x, float y, double t)
    {
        if (_window.Count == 0)
            return (x, y);

        WobbleSample last = _window[^1];
        double dur = t - last.T;
        double dist = Math.Sqrt((x - last.X) * (x - last.X) + (y - last.Y) * (y - last.Y));
        var sample = new WobbleSample
        {
            X = x,
            Y = y,
            T = t,
            Wx = x * dur,
            Wy = y * dur,
            Dur = dur,
            Dist = dist,
        };
        _window.Add(sample);
        _sumWx += sample.Wx;
        _sumWy += sample.Wy;
        _sumDur += dur;
        _sumDist += dist;

        while (_window.Count > 2 && _window[0].T < t - WobbleTimeout)
        {
            WobbleSample old = _window[0];
            _sumWx -= old.Wx;
            _sumWy -= old.Wy;
            _sumDur -= old.Dur;
            _sumDist -= old.Dist;
            _window.RemoveAt(0);
        }

        if (_sumDur <= 0)
            return (x, y);

        double avgX = _sumWx / _sumDur;
        double avgY = _sumWy / _sumDur;
        double avgSpeed = _sumDist / _sumDur;

        // 速度越低越信均值（更平滑），越快越贴原始位置（不拖）
        double k = (avgSpeed - WobbleSpeedFloor) / (WobbleSpeedCeiling - WobbleSpeedFloor);
        k = Math.Clamp(k, 0, 1);
        return (avgX + (x - avgX) * k, avgY + (y - avgY) * k);
    }

    /// <summary>
    /// 抬笔收尾：锚点不再前进，让弹簧把笔尖"追到"最后一次输入的位置
    /// （否则模型笔尖会比真实抬笔点短一截）。最多 20 步，位移小于 0.01px 就停。
    /// </summary>
    public int Finish(InkSample last, List<InkSample> output)
    {
        // wobble 均值滞后半个窗口（500px/s 时可达 20px）、弹簧再滞后约 12px，
        // 所以末端一定要补到**最后一个原始采样**，否则笔画短一截。
        double fromX = _tipX, fromY = _tipY;
        double dx = _rawX - fromX, dy = _rawY - fromY;
        double dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist < 0.5)
            return 0;

        // 用直线补齐而不是继续积分弹簧：欠阻尼弹簧会绕目标来回摆，末端会画出一个小勾
        int steps = Math.Clamp((int)Math.Ceiling(dist / 2.0), 1, 24);   // 每 ≈2px 一个点
        float w = MathF.Max(0.5f, _lastWidth);
        for (int i = 1; i <= steps; i++)
        {
            double u = (double)i / steps;
            output.Add(new InkSample((float)(fromX + dx * u), (float)(fromY + dy * u),
                w, last.R, last.G, last.B, last.A, last.Ticks));
        }

        _tipX = _rawX;
        _tipY = _rawY;
        _velX = _velY = 0;
        return steps;
    }

    /// <summary>笔宽中值滤波（窗口 5）：去掉单点尖峰，保留真实的压感升降。</summary>
    private float MedianWidth(float w)
    {
        _widths.Enqueue(w);
        while (_widths.Count > 5)
            _widths.Dequeue();

        float[] a = _widths.ToArray();
        Array.Sort(a);
        return a[a.Length / 2];
    }

    private static double TicksToSeconds(long ticks) => ticks <= 0 ? 0 : ticks / (double)Stopwatch.Frequency;
}
