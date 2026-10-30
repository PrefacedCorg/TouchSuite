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
/// <para>参数对齐 Inkeys 3 的 <b>Fps120</b> profile（Draw3.StrokeGeometry.cpp 的
/// GetStrokeTimingProfile）：min_output_rate=360、wobble_timeout=2.5/120。
/// 之前这里是 Fps60 档（240 / 2.5/60），窗口宽了一倍 —— 在 120Hz 面板上白白多出
/// 约一帧的平滑滞后，转弯处还会把圆弧拉成弦（半径内缩）。</para>
///
/// <para>④ <b>运动学估计（预测用）</b>：额外维护一个约 90ms 的<b>原始输入</b>窗口，从中算出
/// 末端速度、角速度 ω 与轨迹线性度。预测段因此可以沿<b>圆弧</b>外推而不是沿切线直着甩出去 ——
/// 这是"转圈时尾巴不乱甩"的关键，详见 <see cref="BuildPrediction"/>。</para>
///
/// 单位：位置 = 屏幕物理像素，时间 = 秒。
/// </summary>
internal sealed class StrokeModeler
{
    private const double SpringMass = 11.0 / 32400.0;   // s²，弹簧质量的倒数（Chromium 默认）
    private const double Drag = 72.0;                   // 1/s，阻尼
    private const double MinOutputRate = 360.0;         // Hz，重采样输出率（Inkeys Fps120 档）
    private const double WobbleTimeout = 2.5 / 120.0;   // s，滑动平均窗口（Inkeys Fps120 档）
    private const double ExpectedSpeed = 500.0;         // px/s，96dpi 下的期望书写速度
    private const double WobbleSpeedFloor = 0.015 * ExpectedSpeed;     // 7.5 px/s：以下全用均值
    private const double WobbleSpeedCeiling = 0.08 * ExpectedSpeed;    // 40 px/s：以上不用平滑
    private const int MaxStepsPerInput = 700;           // 单次输入最多拆多少步（防挂起后爆量）

    // ---- 预测参数 ----
    private const double MotionWindowSeconds = 0.09;    // 运动学窗口：足够算角速度，又不至于跟丢转弯
    private const int MotionWindowMax = 40;
    private const double PredictMinSpeed = 25.0;        // px/s，低于它不预测（手停住/抖动）
    private const double PredictMaxSpeed = 125.0;       // px/s，达到它就满置信度
    private const double PredictMaxArc = 40.0;          // px，预测段弧长上限
    private const double PredictMinLead = 0.012;        // s，预测提前量下限
    private const double PredictMaxLead = 0.033;        // s，预测提前量上限
    private const double PredictStepDt = 1.0 / 600.0;   // s，弧线积分步长
    private const double EmitSpacing = 2.5;             // px，预测折线点间距
    private const double MaxTurnRate = 18.0;            // rad/s，角速度钳位（≈2.9 圈/秒）
    private const double PredictSmoothTau = 0.032;      // s，运动学量的 EMA 时间常数
    private const double OmegaDeadZone = 1.2;           // rad/s，直线死区（≈69°/s），以下当成不转弯

    private sealed class WobbleSample
    {
        public double X, Y, T;      // 原始位置与时间
        public double Wx, Wy;       // 位置 × 停留时长（加权和用）
        public double Dur;          // 距上一个采样的时长
        public double Dist;         // 距上一个采样的距离
    }

    /// <summary>运动学窗口里的一个原始采样（只用于估速度/角速度，不参与画图）。</summary>
    private readonly record struct MotionSample(double X, double Y, double T);

    private readonly List<WobbleSample> _window = new();
    private readonly Queue<MotionSample> _motion = new();
    private double _sumWx, _sumWy, _sumDur, _sumDist;

    private double _tipX, _tipY;          // 模型笔尖（弹簧端点）
    private double _velX, _velY;          // 笔尖速度 px/s（弹簧状态，不是输入速度）
    private double _time;                 // 模型当前时间（s）
    private double _anchorX, _anchorY;    // 上一次输入（wobble 平滑后）的位置
    private double _lastInputT;           // 上一次输入的时间
    private double _inputInterval = 1.0 / 120.0;   // 采样间隔的指数平均（预测提前量按它自适应）
    private double _lastMotionT;                   // 运动学窗口里最后一个采样的时间
    private float _lastWidth;
    private readonly Queue<float> _widths = new();   // 最近几个原始笔宽（中值滤波用）
    private double _rawX, _rawY;                     // 最后一个**原始**采样位置（抬笔收尾要追到它）
    private double _lastPredictDir;                  // 上一次预测方向（抑制方向反转时的"甩"）

    // ---- 平滑后的预测运动学（EMA 状态）----
    private double _pSpeed, _pDirSin, _pDirCos, _pOmega, _pLag, _pConf;
    private double _pLastT;
    private bool _pInit, _pLagInit, _pConfInit;

    /// <summary>模型笔尖位置（屏幕物理像素）。</summary>
    public float X => (float)_tipX;
    public float Y => (float)_tipY;

    /// <summary>弹簧速度（px/s）。预测<b>不再</b>用它：欠阻尼弹簧在拐弯处会摆，拿它外推正好会甩。</summary>
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

        _motion.Clear();
        _motion.Enqueue(new MotionSample(s.X, s.Y, _time));
        _lastPredictDir = double.NaN;
        _pInit = false;
        _pLagInit = false;
        _pConfInit = false;
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

        // 采样间隔指数平均：预测提前量要跟着面板/设备的实际出点率走
        _inputInterval += (Math.Clamp(dt, 1.0 / 1000.0, 0.05) - _inputInterval) * 0.2;

        (double ax, double ay) = Wobble(s.X, s.Y, t);
        PushMotion(s.X, s.Y, t);

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
        HasVelocity = _motion.Count >= 3 && (_lastMotionT - _motion.Peek().T) >= 0.012;
        if (!HasVelocity)
            _lastPredictDir = double.NaN;

        return added;
    }

    // ================= 墨迹预测（曲率感知外推） =================

    /// <summary>
    /// 生成预测段，从<b>模型笔尖</b>沿<b>运动学拟合出来的弧线</b>外推，追加到 <paramref name="output"/>。
    /// 返回点数（&lt;2 表示这一帧不该画预测）。
    ///
    /// <para><b>为什么必须是弧线</b>：直线外推在转圈时等于沿切线飞出去，偏离真实轨迹的距离是
    /// v²t²/(2R) —— 半径 30px、速度 1000px/s 时 22ms 就差 8px，写得再快一点就是几十像素的"乱甩"。
    /// 改成把速度矢量随角速度 ω 旋转（等于圆弧积分），转圈时预测点就贴着自己即将走过的轨迹。</para>
    ///
    /// <para><b>为什么不能只用弹簧速度</b>：SpringMass=11/32400、Drag=72 对应 ζ≈0.66，是欠阻尼。
    /// 拐弯时弹簧速度会先冲过头再回来，方向本身在摆；拿它外推等于把摆动放大到笔尖前面。
    /// 所以速度/方向全部来自<b>原始输入</b>窗口（arc/t、总转角/t），弹簧只负责画已确认的笔迹。</para>
    ///
    /// <para><b>三道置信度闸门</b>（任一为 0 就不画，避免"墨甩在手指前面"）：
    /// ① 速度：低于 25px/s（手停住）不预测；
    /// ② 角速度：转得越快，弧外推的方向误差越大 → 超过 ≈1 圈/秒开始衰减，≈3 圈/秒归零；
    /// ③ 方向反转：本次方向与上一次预测方向夹角 &gt; 100° → 置信度乘 0.35（急停回勾时先收，等方向稳定）。</para>
    /// </summary>
    /// <param name="ageSeconds">距最后一次输入已经过去的时间。它直接吃预测提前量：
    /// 手指停住后预测段会一帧帧<b>缩回去</b>，而不是原地挂 70ms 再突然消失。</param>
    public int BuildPrediction(InkSample style, double ageSeconds, List<InkSample> output)
    {
        output.Clear();
        if (!HasVelocity)
            return 0;
        if (!TryGetSmoothedMotion(out double speed, out double omega, out double dirRad))
            return 0;
        if (speed < PredictMinSpeed)
            return 0;

        // ① 速度置信度
        double speedConf = Clamp01((speed - PredictMinSpeed) / (PredictMaxSpeed - PredictMinSpeed));

        // ② 角速度：先过死区，再算置信度。
        // 直线上的 ω 是纯噪声（拟合残差 + 采样抖动），必须<b>硬归零</b>：
        // 否则预测段会随机向上/向下弯，表现就是"直线时上下跳"。
        double omegaMag = Math.Abs(omega);
        if (omegaMag < OmegaDeadZone)
        {
            omega = 0;
        }
        else
        {
            // 死区外做一次软过渡，避免刚出死区时突然跳成弧线
            double ramp = SmoothStep01((omegaMag - OmegaDeadZone) / OmegaDeadZone);
            omega = Math.Sign(omega) * (omegaMag - OmegaDeadZone) * ramp;
        }

        omega = Math.Clamp(omega, -MaxTurnRate, MaxTurnRate);
        double turnConf = 1.0 - SmoothStep01((Math.Abs(omega) - 6.0) / 14.0);

        // ③ 方向反转惩罚：急停/回勾意味着输入方向正在翻，这时候任何外推都是猜的
        double reverseConf = 1.0;
        if (!double.IsNaN(_lastPredictDir))
        {
            double diff = Math.Abs(NormalizeAngle(dirRad - _lastPredictDir));
            if (diff > 100.0 * Math.PI / 180.0)
                reverseConf = 0.35;
            else if (diff > 55.0 * Math.PI / 180.0)
                reverseConf = 0.75;
        }
        _lastPredictDir = dirRad;

        double conf = speedConf * turnConf * reverseConf;
        if (conf <= 0.02)
            return 0;

        // 置信度本身也过一道 EMA：它直接决定预测长度，抖动 10% 就是尖端前后跳几个像素。
        _pConf = _pConfInit ? _pConf + (conf - _pConf) * 0.3 : conf;
        _pConfInit = true;
        conf = _pConf;

        // 提前量：跟着实际出点间隔走（120Hz≈8.3ms→18ms；60Hz≈16.7ms→33ms），再乘置信度。
        //
        // age 里要留一个「一个采样周期」的容差：渲染节拍(8ms)和采样节拍(8.3ms)不同步，
        // 必然有帧"这一帧还没来新样本"。如果 age 直接线性扣减，预测长度就会在
        // 满长/短一截之间来回抖 —— 尖端看起来就是在前后跳。只有真的超时（手停了）
        // 才开始缩，一帧的相位差不算。
        double stale = Math.Max(0.0, ageSeconds - _inputInterval * 1.2);
        double nominalLead = Math.Clamp(_inputInterval * 2.2, PredictMinLead, PredictMaxLead);
        double remain = nominalLead - stale;
        if (remain <= 0.002)
            return 0;
        double freshness = remain / nominalLead;
        double lead = remain * conf;

        // 预测的不是"下一帧"，而是"从笔尖补到手指现在的位置"：笔尖本身还落后 raw 点一段距离，
        // 这段距离必须补上，否则预测再准也还是慢半拍。停手时 freshness 下降，补偿同步收掉。
        // lag 也要平滑：它随笔尖追赶在变，直接用会让预测长度跟着一起跳。
        double lagX = _rawX - _tipX, lagY = _rawY - _tipY;
        double lagNow = Math.Sqrt(lagX * lagX + lagY * lagY);
        _pLag = _pLagInit ? _pLag + (lagNow - _pLag) * 0.25 : lagNow;
        _pLagInit = true;
        double lagBudget = Math.Min(_pLag, PredictMaxArc * 0.5) * freshness;
        double target = speed * lead + lagBudget;
        if (target < 1.0)
            return 0;
        target = Math.Min(target, PredictMaxArc);

        // 弧线积分：速度矢量按 ω 旋转（= 沿圆弧走），大小恒定（短时内足够准）
        double px = _tipX, py = _tipY;
        double travelled = 0.0, ang = 0.0;
        double nextEmit = EmitSpacing;
        double w0 = MathF.Max(0.6f, style.Width);
        float r = style.R, g = style.G, b = style.B, a = style.A;
        long ticks = style.Ticks;
        int guard = 0;

        while (travelled < target && guard++ < 500)
        {
            ang += omega * PredictStepDt;
            double dir = dirRad + ang;
            px += Math.Cos(dir) * speed * PredictStepDt;
            py += Math.Sin(dir) * speed * PredictStepDt;
            travelled += speed * PredictStepDt;

            if (travelled + 1e-6 >= nextEmit)
            {
                double u = Clamp01(travelled / target);
                // 末端收到 0.3 倍而不是 0.12：太尖的"针尖"会把零点几度的方向误差放大成人眼可见的摆动。
                double taper = 1.0 - 0.70 * SmoothStep01(u);
                float w = (float)Math.Max(0.5, w0 * taper);
                output.Add(new InkSample((float)px, (float)py, w, (byte)r, (byte)g, (byte)b, (byte)a, ticks));
                nextEmit += EmitSpacing;
            }
        }

        if (output.Count == 0)
        {
            // 太短也要给一段，否则低速时预测永远显不出来
            double dir = dirRad + ang;
            output.Add(new InkSample((float)px, (float)py, (float)Math.Max(0.5, w0 * 0.8),
                (byte)r, (byte)g, (byte)b, (byte)a, ticks));
        }
        return output.Count;
    }

    /// <summary>
    /// 取出<b>平滑后</b>的运动学量（速度、角速度、方向）。
    ///
    /// <para>拟合用<b>加权二次最小二乘</b>：权重按时间指数衰减（新点权重大），u=(t-t_end)/span ∈ [-1,0]。
    /// 一次项就是当前速度，二次项就是加速度 —— 直线上二次项天然趋近 0（零均值噪声被最小二乘平均掉），
    /// 圆弧上它正好是向心加速度，所以"直线 / 转弯"不需要另外判断，也不会像逐点差分那样被 0.5px 抖动带飞。</para>
    ///
    /// <para>拟合结果再过一道时间常数 32ms 的 EMA（方向用 sin/cos 双分量，避开 ±π 环绕）。
    /// 这一步是"直线不上下跳"的关键：单帧拟合残差仍有残余，直接画成预测段就是肉眼可见的抖动。</para>
    /// </summary>
    private bool TryGetSmoothedMotion(out double speed, out double omega, out double dirRad)
    {
        speed = omega = dirRad = 0;
        if (!FitMotion(out double rawSpeed, out double rawDir, out double rawOmega, out double tEnd))
            return false;

        if (!_pInit)
        {
            _pSpeed = rawSpeed;
            _pDirSin = Math.Sin(rawDir);
            _pDirCos = Math.Cos(rawDir);
            _pOmega = rawOmega;
            _pInit = true;
            _pLastT = tEnd;
        }
        else
        {
            double dt = tEnd - _pLastT;
            if (dt > 0.0005)
            {
                double k = 1.0 - Math.Exp(-dt / PredictSmoothTau);
                _pSpeed += (rawSpeed - _pSpeed) * k;
                _pDirSin += (Math.Sin(rawDir) - _pDirSin) * k;
                _pDirCos += (Math.Cos(rawDir) - _pDirCos) * k;
                _pOmega += (rawOmega - _pOmega) * k;
                _pLastT = tEnd;
            }
        }

        speed = _pSpeed;
        dirRad = Math.Atan2(_pDirSin, _pDirCos);
        omega = _pOmega;
        return speed > 0.001;
    }

    /// <summary>
    /// 加权最小二乘拟合最近的运动学：位置对时间拟合成 c0 + c1·u + c2·u²。
    /// 点数不足 5 时只做线性拟合（二次项由 3 个点拟合纯属过拟合，会把噪声放大成 ω）。
    /// </summary>
    private bool FitMotion(out double speed, out double dirRad, out double omega, out double tEnd)
    {
        speed = dirRad = omega = tEnd = 0;
        if (_motion.Count < 3)
            return false;

        MotionSample[] a = _motion.ToArray();
        tEnd = a[^1].T;
        if (tEnd - a[0].T < 0.015)
            return false;

        double span = Math.Clamp(tEnd - a[0].T, 0.04, 0.10);
        double s0 = 0, s1 = 0, s2 = 0, s3 = 0, s4 = 0;
        double tx0 = 0, tx1 = 0, tx2 = 0, ty0 = 0, ty1 = 0, ty2 = 0;
        int n = 0;

        for (int i = a.Length - 1; i >= 0; i--)
        {
            double u = (a[i].T - tEnd) / span;
            if (u < -1.2)
                break;                     // 只吃最近 1.2 个 span（≈ 48~120ms）
            double w = Math.Exp(u * 1.6);  // u=-1 → 0.20，u=0 → 1.0
            double u2 = u * u;
            s0 += w;
            s1 += w * u;
            s2 += w * u2;
            s3 += w * u2 * u;
            s4 += w * u2 * u2;
            tx0 += w * a[i].X;
            tx1 += w * u * a[i].X;
            tx2 += w * u2 * a[i].X;
            ty0 += w * a[i].Y;
            ty1 += w * u * a[i].Y;
            ty2 += w * u2 * a[i].Y;
            n++;
        }

        if (n < 3)
            return false;

        bool quadratic = n >= 5;
        double vx, ax2, vy, ay2;
        if (quadratic)
        {
            if (!Solve3(s0, s1, s2, s1, s2, s3, s2, s3, s4, tx0, tx1, tx2, out _, out vx, out ax2))
                return false;
            if (!Solve3(s0, s1, s2, s1, s2, s3, s2, s3, s4, ty0, ty1, ty2, out _, out vy, out ay2))
                return false;
        }
        else
        {
            if (!Solve2(s0, s1, s1, s2, tx0, tx1, out _, out vx))
                return false;
            if (!Solve2(s0, s1, s1, s2, ty0, ty1, out _, out vy))
                return false;
            ax2 = ay2 = 0;
        }

        // u = (t - tEnd)/span ⇒ 一阶导 = c1/span，二阶导 = 2·c2/span²
        vx /= span;
        vy /= span;
        double ax = 2 * ax2 / (span * span);
        double ay = 2 * ay2 / (span * span);

        speed = Math.Sqrt(vx * vx + vy * vy);
        if (speed < 0.001)
            return false;
        dirRad = Math.Atan2(vy, vx);

        // 角速度 = 法向加速度 / 速度（κ·v）。切向分量只管快慢，不影响方向，丢掉。
        double nx = -vy / speed, ny = vx / speed;
        omega = (ax * nx + ay * ny) / speed;
        return true;
    }

    private static bool Solve2(double a11, double a12, double a21, double a22,
        double b1, double b2, out double x1, out double x2)
    {
        double det = a11 * a22 - a12 * a21;
        if (Math.Abs(det) < 1e-12)
        {
            x1 = x2 = 0;
            return false;
        }
        x1 = (b1 * a22 - a12 * b2) / det;
        x2 = (a11 * b2 - b1 * a21) / det;
        return true;
    }

    private static bool Solve3(double a11, double a12, double a13,
        double a21, double a22, double a23,
        double a31, double a32, double a33,
        double b1, double b2, double b3,
        out double x1, out double x2, out double x3)
    {
        double c11 = a22 * a33 - a23 * a32;
        double c12 = a21 * a33 - a23 * a31;
        double c13 = a21 * a32 - a22 * a31;
        double det = a11 * c11 - a12 * c12 + a13 * c13;
        if (Math.Abs(det) < 1e-18)
        {
            x1 = x2 = x3 = 0;
            return false;
        }
        x1 = (b1 * c11 - a12 * (b2 * a33 - a23 * b3) + a13 * (b2 * a32 - a22 * b3)) / det;
        x2 = (a11 * (b2 * a33 - a23 * b3) - b1 * c12 + a13 * (a21 * b3 - b2 * a31)) / det;
        x3 = (a11 * (a22 * b3 - b2 * a32) - a12 * (a21 * b3 - b2 * a31) + b1 * c13) / det;
        return true;
    }

    /// <summary>把原始采样塞进运动学窗口（只保留最近 90ms / 40 点）。</summary>
    private void PushMotion(double x, double y, double t)
    {
        _lastMotionT = t;
        _motion.Enqueue(new MotionSample(x, y, t));
        while (_motion.Count > 2 &&
               (_motion.Count > MotionWindowMax || _motion.Peek().T < t - MotionWindowSeconds))
            _motion.Dequeue();
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

    private static double NormalizeAngle(double a)
    {
        while (a > Math.PI)
            a -= 2 * Math.PI;
        while (a < -Math.PI)
            a += 2 * Math.PI;
        return a;
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    private static double SmoothStep01(double t)
    {
        t = Clamp01(t);
        return t * t * (3 - 2 * t);
    }

    private static double TicksToSeconds(long ticks) => ticks <= 0 ? 0 : ticks / (double)Stopwatch.Frequency;
}
