using System.IO;
using System.Text;

namespace TouchSuite.App;

/// <summary>
/// 校准向导的极简文件日志（与主程序 Helpers/Log.cs 同款写法，独立日志文件）。
/// 目的：把每一步做了什么、EDID 读到什么、三路来源各报多少、阈值怎么算的都留痕，
/// 将来拿到真触摸屏后可回看驱动到底有没有动态上报接触尺寸。
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
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TouchSuite", "Calibrator", "logs"),
            };

            foreach (string dir in candidates)
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    _logDir = dir;
                    _filePath = Path.Combine(dir, $"TouchSuite.App_{DateTime.Now:yyyyMMdd_HHmmss}.log");
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
                WriteRaw("========== TouchSuite 校准向导 日志开始 ==========");
                WriteRaw($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                WriteRaw($"日志文件: {_filePath}");
                WriteRaw($"系统: {Environment.OSVersion}, .NET {Environment.Version}, 64位={Environment.Is64BitProcess}");
            }
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    // 带异常的重载：把类型 / 消息 / 堆栈 / InnerException 一并写进日志（ex.ToString() 自带堆栈）。
    public static void Warn(string message, Exception ex) => Write("WARN", $"{message}: {ex}");
    public static void Error(string message, Exception ex) => Write("ERROR", $"{message}: {ex}");
    public static void Error(Exception ex) => Write("ERROR", ex.ToString());

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
                WriteRaw("========== TouchSuite 校准向导 日志结束 ==========");
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
