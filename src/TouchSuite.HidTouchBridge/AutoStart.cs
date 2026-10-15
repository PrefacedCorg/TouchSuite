using System.Diagnostics;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TouchSuite.HidTouchBridge;

/// <summary>
/// 开机自启管理。
/// <para>
/// 首选**计划任务**（<c>schtasks /SC ONLOGON /RL HIGHEST /IT</c>）：登录即启、以最高权限运行。
/// 本程序平时是普通权限（合成指针注入要求），而建/删计划任务需要管理员 ——
/// 所以只在**勾选/取消勾选的那一下**临时请求一次 UAC，不影响常驻运行。
/// </para>
/// <para>schtasks 不可用（或被拒绝）时回退到 HKCU\...\Run：本程序是 asInvoker，登录时不会弹 UAC。</para>
/// </summary>
internal static class AutoStart
{
    private const string TaskName = "TouchSuite.HidTouchBridge";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "TouchSuite.HidTouchBridge";

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
                // 回退
            }
            return Application.ExecutablePath;
        }
    }

    public static bool IsEnabled() => IsTaskPresent() || RunValue() is not null;

    public static string Describe()
    {
        if (IsTaskPresent())
            return "计划任务（登录即启）";
        if (RunValue() is not null)
            return "HKCU Run（登录即启）";
        return "未开启";
    }

    public static void Enable()
    {
        RemoveRunValue();
        if (CreateTask(out string err))
        {
            Log.Write("开机自启：已创建计划任务（ONLOGON / 最高权限 / 交互式）。");
            return;
        }

        SetRunValue();
        Log.Write($"开机自启：计划任务创建失败（{err}），已回退 HKCU Run。");
    }

    public static void Disable()
    {
        DeleteTask(out _);
        RemoveRunValue();
        Log.Write("开机自启：已关闭（删除计划任务与 HKCU Run 项）。");
    }

    // ================= 计划任务 =================

    private static bool IsTaskPresent()
        => Run("schtasks.exe", $"/Query /TN \"{TaskName}\"", out _) == 0;

    private static bool CreateTask(out string error)
    {
        // /TR 的值里，exe 路径要再包一对引号（路径可能含空格）：
        //   最终 schtasks 收到： /TR "\"C:\path with space\app.exe\""
        string tr = "\"\\\"" + ExePath + "\\\"\"";
        string args = $"/Create /TN \"{TaskName}\" /TR {tr} /SC ONLOGON /RL HIGHEST /IT /F";

        if (Run("schtasks.exe", args, out error) == 0)
            return true;

        // 建计划任务必须管理员。进程平时是普通权限（注入要求），所以这里按需弹一次 UAC。
        if (!Elevation.IsElevated())
        {
            if (RunElevated("schtasks.exe", args, out string uacError))
            {
                error = "";
                return true;
            }
            if (uacError.Length > 0)
                error = $"{error}；按需提权失败：{uacError}";
        }
        return false;
    }

    private static bool DeleteTask(out string output)
    {
        string args = $"/Delete /TN \"{TaskName}\" /F";
        if (Run("schtasks.exe", args, out output) == 0)
            return true;
        return !Elevation.IsElevated() && RunElevated("schtasks.exe", args, out _);
    }

    private static int Run(string file, string args, out string output)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using Process p = Process.Start(psi);
            string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            output = o.Trim();
            return p.HasExited ? p.ExitCode : -1;
        }
        catch (Exception ex)
        {
            output = ex.Message;
            return -1;
        }
    }

    /// <summary>以管理员身份运行（会弹一次 UAC）。UseShellExecute=true 时无法重定向输出，只看退出码。</summary>
    private static bool RunElevated(string file, string args, out string error)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using Process p = Process.Start(psi);
            p.WaitForExit(60000);
            error = "";
            return p.HasExited && p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            error = ex.Message;   // 用户在 UAC 里点「否」也会走到这里
            return false;
        }
    }

    // ================= HKCU Run 回退 =================

    private static string? RunValue()
    {
        using RegistryKey? k = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return k?.GetValue(RunValueName) as string;
    }

    private static void SetRunValue()
    {
        using RegistryKey? k = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        k?.SetValue(RunValueName, "\"" + ExePath + "\"");
    }

    private static void RemoveRunValue()
    {
        using RegistryKey? k = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        k?.DeleteValue(RunValueName, throwOnMissingValue: false);
    }
}
