using System.IO;
using System.Text;

namespace TouchSuite.App.Old.Helpers;

/// <summary>
/// 极简文件日志：线程安全、带缓冲（按需 Flush），并保留最近若干行供界面查看。
/// 目的：在没有触摸屏时先把关键过程（校准、HID 每帧 Bounds、聚类结果）记下来，
/// 将来上触摸屏后可回看驱动到底有没有动态上报接触尺寸。
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly List<string> Recent = new();
    private const int RecentMax = 500;

    private static StreamWriter? _writer;
    private static string _filePath = "";
    private static string _logDir = "";

    public static bool Enabled { get; set; } = true;
    public static string FilePath { get { lock (Gate) return _filePath; } }
    public static string LogDirectory { get { lock (Gate) return _logDir; } }

    public static void Init()
    {
        lock (Gate)
        {
            if (_writer is not null)
                return;

            string[] candidates =
            {
                Path.Combine(AppContext.BaseDirectory, "logs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TouchSuite.App.Old", "logs"),
            };

            foreach (string dir in candidates)
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    _logDir = dir;
                    _filePath = Path.Combine(dir, $"TouchSuite.App.Old_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                    _writer = new StreamWriter(
                        new FileStream(_filePath, FileMode.Create, FileAccess.Write, FileShare.Read),
                        new UTF8Encoding(false))
                    { AutoFlush = false };
                    break;
                }
                catch
                {
                    // 该目录不可写，尝试下一个
                }
            }

            if (_writer is not null)
            {
                WriteRaw("========== TouchSuite.App.Old 日志开始 ==========");
                WriteRaw($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                WriteRaw($"日志文件: {_filePath}");
                WriteRaw($"系统: {Environment.OSVersion}, .NET {Environment.Version}, 64位={Environment.Is64BitProcess}");
            }
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    public static void Flush()
    {
        lock (Gate)
        {
            try { _writer?.Flush(); } catch { /* ignore */ }
        }
    }

    public static void Shutdown()
    {
        lock (Gate)
        {
            try
            {
                WriteRaw("========== TouchSuite.App.Old 日志结束 ==========");
                _writer?.Flush();
                _writer?.Dispose();
            }
            catch { /* ignore */ }
            finally
            {
                _writer = null;
            }
        }
    }

    public static string[] GetRecent()
    {
        lock (Gate)
            return Recent.ToArray();
    }

    private static void Write(string level, string message)
    {
        if (!Enabled)
            return;
        WriteRaw($"[{level}] {message}");
    }

    private static void WriteRaw(string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} {message}";

        lock (Gate)
        {
            Recent.Add(line);
            if (Recent.Count > RecentMax)
                Recent.RemoveAt(0);

            if (_writer is not null)
            {
                try { _writer.WriteLine(line); } catch { /* ignore */ }
            }
        }
    }
}
