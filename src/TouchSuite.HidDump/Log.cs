using System.IO;
using System.Text;

namespace TouchSuite.HidDump;

/// <summary>
/// 简单文件日志：输出到 &lt;exe&gt;\logs\HidDump_yyyyMMdd_HHmmss.log，每行立即落盘（便于运行后离线分析）。
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static string _path = "";

    public static string Path => _path;

    public static void Init()
    {
        try
        {
            string dir = System.IO.Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            _path = System.IO.Path.Combine(dir, $"HidDump_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            Line($"==== TouchSuite.HidDump 启动 {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ====");
            Line($"环境：OS {Environment.OSVersion}，{(Environment.Is64BitProcess ? "x64" : "x86")}，.NET {Environment.Version}");
        }
        catch
        {
            _path = "";
        }
    }

    public static void Line(string text)
    {
        if (_path.Length == 0)
            return;
        lock (Gate)
        {
            try
            {
                File.AppendAllText(_path, text + Environment.NewLine, new UTF8Encoding(false));
            }
            catch
            {
                // 日志写失败不影响主流程
            }
        }
    }

    public static void Info(string text) => Line($"{DateTime.Now:HH:mm:ss.fff} [I] {text}");
    public static void Warn(string text) => Line($"{DateTime.Now:HH:mm:ss.fff} [W] {text}");
    public static void Error(string text, Exception? ex = null) => Line($"{DateTime.Now:HH:mm:ss.fff} [E] {text}{(ex is null ? "" : " :: " + ex)}");
}
