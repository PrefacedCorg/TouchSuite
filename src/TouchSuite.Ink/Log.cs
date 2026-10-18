using System.IO;

namespace TouchSuite.Ink;

/// <summary>
/// 落盘日志：写到 exe 同目录 logs/Ink_yyyyMMdd_HHmmss.log。
/// DComp / D2D 这些底层调用一旦失败，只有 HRESULT 能说明问题，所以每一步都记。
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static string _path = "";

    public static string FilePath
    {
        get
        {
            lock (Gate)
            {
                if (_path.Length == 0)
                {
                    string dir = Path.Combine(AppContext.BaseDirectory, "logs");
                    try { Directory.CreateDirectory(dir); } catch { /* 忽略 */ }
                    _path = Path.Combine(dir, $"Ink_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                }
                return _path;
            }
        }
    }

    public static void Write(string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        lock (Gate)
        {
            System.Diagnostics.Debug.WriteLine(line);
            try
            {
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch
            {
                // 写盘失败不影响主流程
            }
        }
    }

    public static void Hr(string what, int hr) =>
        Write(hr >= 0 ? $"{what} → S_OK/成功 (0x{hr:X8})" : $"{what} → 失败 (0x{hr:X8})");
}
