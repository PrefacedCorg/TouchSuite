using System.Text;

namespace TouchSuite.HidTouchBridge;

/// <summary>
/// 简单持久化配置（key=value 文本，无外部依赖）。
/// 主要记住「上次转发的触摸屏」，下次启动自动沿用（命令行 --device 优先级更高）。
/// 文件放 exe 同级；该目录不可写时退回 <c>%LOCALAPPDATA%\TouchSuite.HidTouchBridge\</c>。
/// </summary>
internal sealed class Config
{
    private const string FileName = "TouchSuite.HidTouchBridge.cfg";

    /// <summary>上次转发的设备完整 HID 路径（null = 尚未记录）。</summary>
    public string? DevicePath { get; private set; }
    public ushort DeviceUsagePage { get; private set; }
    public ushort DeviceUsage { get; private set; }

    public static string FileFullPath { get; } = ResolvePath();

    private static string ResolvePath()
    {
        try
        {
            string p = Path.Combine(AppContext.BaseDirectory, FileName);
            using (File.Open(p, FileMode.OpenOrCreate, FileAccess.Write)) { }   // 试写，判断目录是否可写
            return p;
        }
        catch
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TouchSuite.HidTouchBridge");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, FileName);
        }
    }

    public static Config Load()
    {
        var c = new Config();
        try
        {
            if (!File.Exists(FileFullPath))
                return c;

            foreach (string raw in File.ReadAllLines(FileFullPath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                    continue;
                int eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;

                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();
                switch (key)
                {
                    case "device":
                        if (val.Length > 0) c.DevicePath = val;
                        break;
                    case "device_page":
                        if (ushort.TryParse(val, out ushort pg)) c.DeviceUsagePage = pg;
                        break;
                    case "device_usage":
                        if (ushort.TryParse(val, out ushort ug)) c.DeviceUsage = ug;
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("读取配置失败（按默认处理）：" + ex.Message);
        }
        return c;
    }

    /// <summary>记住某块设备；返回是否与已记录的值不同（用于避免无谓写盘）。</summary>
    public bool RememberDevice(RawHidTouch.TouchDevice d)
    {
        bool changed = DevicePath != d.Path
            || DeviceUsagePage != d.UsagePage
            || DeviceUsage != d.Usage;
        DevicePath = d.Path;
        DeviceUsagePage = d.UsagePage;
        DeviceUsage = d.Usage;
        return changed;
    }

    public void Save()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("# TouchSuite.HidTouchBridge 配置（本程序自动生成/维护）");
            sb.AppendLine("# 记住上次转发的触摸屏，下次启动自动沿用；命令行 --device 优先级更高。");
            if (DevicePath is not null)
            {
                sb.AppendLine("device=" + DevicePath);
                sb.AppendLine("device_page=" + DeviceUsagePage);
                sb.AppendLine("device_usage=" + DeviceUsage);
            }
            File.WriteAllText(FileFullPath, sb.ToString(), new UTF8Encoding(false));
            Log.Write("配置已保存：" + FileFullPath);
        }
        catch (Exception ex)
        {
            Log.Write("保存配置失败：" + ex.Message);
        }
    }

    /// <summary>在已枚举设备里找上次记住的那块；找不到返回 null（交回自动挑选）。</summary>
    public RawHidTouch.TouchDevice? FindRemembered(List<RawHidTouch.TouchDevice> devices)
    {
        string? path = DevicePath;
        if (path is null || path.Length == 0)
            return null;

        RawHidTouch.TouchDevice? exact = devices.FirstOrDefault(d => d.Path == path && d.Links.Count > 0);
        if (exact is not null)
            return exact;

        // 路径变了（换口 / 重枚举）时退化匹配：同 VID&PID 段 + 同顶层集合，且唯一
        string vidpid = ExtractVidPid(path);
        List<RawHidTouch.TouchDevice> byTlc = devices
            .Where(d => d.Links.Count > 0
                        && d.UsagePage == DeviceUsagePage
                        && d.Usage == DeviceUsage
                        && (vidpid.Length == 0
                            || ExtractVidPid(d.Path).Equals(vidpid, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return byTlc.Count == 1 ? byTlc[0] : null;
    }

    private static string ExtractVidPid(string path)
    {
        int i = path.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        if (i < 0)
            return "";
        int j = path.IndexOf('#', i);
        if (j < 0) j = path.Length;
        string seg = path.Substring(i, j - i);
        int m = seg.IndexOf("&MI_", StringComparison.OrdinalIgnoreCase);
        if (m >= 0)
            seg = seg.Substring(0, m);
        return seg;
    }
}
