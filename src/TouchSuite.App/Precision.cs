namespace TouchSuite.App;

/// <summary>
/// 全局精度开关与格式化。
/// <para>
/// 高精度模式（默认开）：后端**一律双精度、不做任何取整**；显示按有效数字自适应给出小数位
/// （保留尾零，方便直接看出设备分辨率，例如 0.001234600 mm）。
/// </para>
/// 关闭后回到旧行为：后端取整、显示固定 1 位小数。
/// </summary>
public static class Precision
{
    public static bool High { get; set; } = true;

    /// <summary>后端取整：高精度模式原样返回；否则四舍五入到整数。</summary>
    public static double Round(double v) => High ? v : Math.Round(v);

    public static double? Round(double? v) => v is double d ? Round(d) : null;

    public static double Round(double v, int decimals) => High ? v : Math.Round(v, decimals);

    /// <summary>自适应有效位格式化（约 7 位有效数字；高精度模式保留尾零）。</summary>
    public static string Fmt(double v, int normalDecimals = 1)
    {
        if (double.IsNaN(v) || double.IsInfinity(v))
            return "—";
        if (!High)
            return v.ToString("0." + new string('0', Math.Max(0, normalDecimals)));

        double a = Math.Abs(v);
        if (a == 0)
            return "0";

        int exp = (int)Math.Floor(Math.Log10(a));      // 123.4 → 2；0.0012346 → -3
        int dec = Math.Clamp(6 - exp, 0, 12);
        return v.ToString("0." + new string('0', dec));
    }

    public static string Fmt(double? v, int normalDecimals = 1)
        => v is double d ? Fmt(d, normalDecimals) : "—";

    /// <summary>固定小数位格式化（高精度模式也强制取整）。专给"每帧都在变的实时读数"用：
    /// 用 <see cref="Fmt"/> 会给出约 7 位有效数字，末位每帧抖动 → 界面文字闪动。</summary>
    public static string FmtFixed(double? v, int decimals = 0)
    {
        if (v is not double d || double.IsNaN(d) || double.IsInfinity(d))
            return "—";
        string fmt = decimals <= 0 ? "0" : "0." + new string('0', Math.Clamp(decimals, 1, 8));
        return d.ToString(fmt);
    }
}
