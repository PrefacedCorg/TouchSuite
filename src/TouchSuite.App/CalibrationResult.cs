using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TouchSuite.App;

/// <summary>本次校准的全部结果（可存成 JSON，供后续程序读取）；接触尺寸一律以**物理像素 px** 计。</summary>
public sealed class CalibrationResult
{
    public string Timestamp { get; set; } = "";
    public string DeviceName { get; set; } = "";

    // ---- 屏幕校准 ----
    public string SizeSource { get; set; } = "EDID";   // EDID / 对角线 / 尺子
    public double? DiagonalInch { get; set; }          // 手填对角线英寸（仅 SizeSource=对角线 时）
    public int EdidWidthMm { get; set; }
    public int EdidHeightMm { get; set; }
    public int ResX { get; set; }
    public int ResY { get; set; }
    public string SizeMode { get; set; } = "";
    public double MmPerPxX { get; set; }
    public double MmPerPxY { get; set; }
    public bool WidthRulerOk { get; set; }
    public bool HeightRulerOk { get; set; }

    // ---- 手掌尺寸（描摹自动填 / 右侧手填；物理像素）----
    public double PalmWidthPx { get; set; }        // a2 宽（横向）
    public double PalmHeightPx { get; set; }       // a1 高（纵向）
    public double PalmTraceAreaPx2 { get; set; }   // a3 描摹凹面积（沿外轮廓一笔描一圈围出的面积）
    public string PalmAreaFormula { get; set; } = "Rect";   // Rect / Ellipse / Trace 三选一
    public double PalmAreaPx2 { get; set; }        // 手掌像素面积 = 三选一公式的结果（K 的分子）

    // ---- 按压实测（物理像素面积 + 压感 0~1）----
    public double? PalmContactAreaPx2 { get; set; }      // b1×b2 手掌按压时系统上报的尺寸乘积
    public double? PalmPressure { get; set; }            // b3 手掌按压时的压感
    public double? FingerContactAreaPx2 { get; set; }    // c1×c2 手指按压时系统上报的尺寸乘积
    public double? FingerPressure { get; set; }          // c3 手指按压时的压感

    // ---- K 定值 ----
    /// <summary>K = 手掌像素面积 ÷ 触摸尺寸乘积（面积比）：触摸报多少像素面积，乘 K 就是手掌擦要显示的像素面积。</summary>
    public double? K { get; set; }
    public double? ThresholdAreaPx2 { get; set; }   // 擦/写切换阈值（绝对，px²；自动值 = (b + c) / 2）

    // ---- 末页设置（6 滑块 + 2 压感开关 + 形状/长宽比/面积公式）----
    public double? KTrim { get; set; }                    // K 倍率 ×0.5~×2
    public double? PalmPressureThreshold { get; set; }    // 掌擦压感阈值 0~1（默认 = b3）
    public double? PalmNormMax { get; set; }              // 掌擦归一 max 1~3（默认 2）
    public double? WritingPressureThreshold { get; set; } // 书写压感阈值 0~1（默认 = c3）
    public double? WritingNormMax { get; set; }           // 书写归一 max 1~3（默认 2）
    public bool? PalmPressureEnabled { get; set; }        // 手掌擦压感开关（关 → 模拟 512/1024）
    public bool? WritingUsesPressure { get; set; }        // 书写压感开关（关 → 模拟 512/1024）
    public bool? FollowSize { get; set; }                 // 手掌擦随触摸尺寸（按多大擦多大）
    public bool? LockPalmSize { get; set; }               // 禁止手掌擦缩小（擦时只增不减）
    public bool? AreaThresholdEnabled { get; set; }       // 启用面积阈值判 擦/写
    public bool? WritingFollowSize { get; set; }          // 书写也随触摸尺寸
    public string? EraserShape { get; set; }              // Rectangle / Circle（椭圆）
    public string? AspectSource { get; set; }             // Contact / Custom
    public double? AspectW { get; set; }                  // 自定义长宽比 W
    public double? AspectH { get; set; }                  // 自定义长宽比 H

    /// <summary>默认保存到 exe 同级的 calibration.json；不可写则退回 %LOCALAPPDATA%。</summary>
    public static string DefaultPath()
    {
        string dir = AppContext.BaseDirectory;
        try
        {
            string probe = Path.Combine(dir, ".write_probe");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return Path.Combine(dir, "calibration.json");
        }
        catch
        {
            string fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TouchSuite");
            return Path.Combine(fallback, "calibration.json");
        }
    }

    public void Save(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(this, options), Encoding.UTF8);
    }

    /// <summary>从 JSON 载入上次保存的标定；文件不存在或格式不对则返回 null。</summary>
    public static CalibrationResult? Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            return JsonSerializer.Deserialize<CalibrationResult>(File.ReadAllText(path, Encoding.UTF8));
        }
        catch
        {
            return null;
        }
    }
}