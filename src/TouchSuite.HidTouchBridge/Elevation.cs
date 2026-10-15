using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;

namespace TouchSuite.HidTouchBridge;

/// <summary>
/// 提权支持。**默认不提权**：app.manifest 是 <c>asInvoker</c>（合成指针注入要求中完整性会话）。
/// 这里保留 <c>--elevate</c> 这个显式开关（个别场景需要管理员时用），以及给「开机自启」按需提权用。
/// </summary>
internal static class Elevation
{
    public static bool IsElevated()
    {
        try
        {
            using WindowsIdentity id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static string ExePath
    {
        get
        {
            try
            {
                string? f = Process.GetCurrentProcess().MainModule?.FileName;
                if (f is not null && f.Length > 0)
                    return f;
            }
            catch
            {
                // 取进程路径失败时回退
            }
            return Application.ExecutablePath;
        }
    }

    /// <summary>用 runas 重新以管理员身份启动自身（会弹 UAC）。成功返回 true，调用方应直接退出。</summary>
    public static bool RelaunchElevated(string[] originalArgs)
    {
        var sb = new StringBuilder();
        foreach (string a in originalArgs)
        {
            if (sb.Length > 0)
                sb.Append(' ');
            sb.Append(Quote(a));
        }
        if (sb.Length > 0)
            sb.Append(' ');
        sb.Append("--elevated");   // 哨兵：避免提权后再次提权形成死循环

        var psi = new ProcessStartInfo
        {
            FileName = ExePath,
            Arguments = sb.ToString(),
            UseShellExecute = true,
            Verb = "runas",
        };
        try
        {
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("自动提权失败（可能被用户在 UAC 里取消）：" + ex.Message);
            return false;
        }
    }

    private static string Quote(string a)
        => a.Length > 0 && a.IndexOf(' ') < 0 && a.IndexOf('"') < 0
            ? a
            : "\"" + a.Replace("\"", "\\\"") + "\"";
}
