using System.IO;
using System.Text.Json;

namespace TouchSuite.Ink;

/// <summary>设置窗口里的四个开关（存到程序目录下 settings.json，读不到就用默认值）。</summary>
internal sealed class InkSettingsData
{
    public bool DComp { get; set; } = true;        // 是否用 DComp（GPU）墨迹层；关掉则退回 WPF 层
    public bool Prediction { get; set; } = true;   // 笔迹预测（外推笔尖）
    public bool Smoothing { get; set; } = true;    // 二次贝塞尔平滑
    public bool Taper { get; set; } = true;        // 笔锋（收笔渐细）
}

internal static class InkSettings
{
    private static readonly string FilePath =
        Path.Combine(AppContext.BaseDirectory, "settings.json");

    private static InkSettingsData _data = new();

    public static bool DComp
    {
        get => _data.DComp;
        set => _data.DComp = value;
    }

    public static bool Prediction
    {
        get => _data.Prediction;
        set => _data.Prediction = value;
    }

    public static bool Smoothing
    {
        get => _data.Smoothing;
        set => _data.Smoothing = value;
    }

    public static bool Taper
    {
        get => _data.Taper;
        set => _data.Taper = value;
    }

    public static void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return;
            string json = File.ReadAllText(FilePath);
            _data = JsonSerializer.Deserialize<InkSettingsData>(json) ?? new InkSettingsData();
        }
        catch
        {
            // 配置坏了就用默认值，不影响启动
            _data = new InkSettingsData();
        }
    }

    public static void Save()
    {
        try
        {
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 无写权限就只在本次运行内生效
        }
    }
}
