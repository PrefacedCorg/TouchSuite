using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TouchErase.Calibrator;

/// <summary>本次校准的全部结果（可存成 JSON，供后续程序读取）。</summary>
public sealed class CalibrationResult
{
    public string Timestamp { get; set; } = "";
    public string DeviceName { get; set; } = "";

    // ---- 屏幕校准 ----
    public string SizeSource { get; set; } = "EDID";   // EDID / 对角线
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

    // ---- 手掌尺寸（左侧描摹或右侧手填）----
    public double PalmWidthCm { get; set; }
    public double PalmHeightCm { get; set; }
    public double PalmAreaCm2 { get; set; }

    // ---- 按压实测 ----
    public double? PalmContactAreaMm2 { get; set; }
    public double? PalmPressure { get; set; }
    public double? FingerContactAreaMm2 { get; set; }
    public double? FingerPressure { get; set; }

    // ---- 阈值 ----
    public double? ThresholdAreaMm2 { get; set; }   // 自动值 = (手掌 + 手指) / 2
    public double? PressureThreshold { get; set; }  // 自动值 = 手掌按压时的压感

    // ---- 阈值的现场微调（第 7 步滑块；默认 1.0 = 不调整）----
    public double? AreaThresholdTrim { get; set; }
    public double? AreaThresholdEffectiveMm2 { get; set; }
    public double? PressureThresholdTrim { get; set; }
    public double? PressureThresholdEffective { get; set; }

    // ---- 第 7 步预览页的开关与系数（保存界面设置，供主程序按同一套参数工作）----
    public bool? FollowSize { get; set; }
    public bool? LockPalmSize { get; set; }
    public bool? FollowPressure { get; set; }
    public double? PressureGain { get; set; }
    public bool? AreaThresholdEnabled { get; set; }
    public bool? WritingUsesPressure { get; set; }
    public double? WritingPressureGain { get; set; }
    public bool? WritingFollowSize { get; set; }
    public double? RatioTrim { get; set; }
    public string? EraserShape { get; set; }

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
                "TouchErase");
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
