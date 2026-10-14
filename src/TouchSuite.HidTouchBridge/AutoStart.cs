using System.Diagnostics;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TouchSuite.HidTouchBridge;

/// <summary>
/// 开机自启管理。
/// <para>
/// 首选**计划任务**（<c>schtasks /SC ONLOGON /RL HIGHEST /IT</c>）：登录即启、以最高权限运行，
/// 且**不弹 UAC**。因为本程序清单是 requireAdministrator，若改用 HKCU\...\Run 启动，
/// 每次登录都会弹一次 UAC 同意框 —— 所以计划任务才是正解。
/// </para>
/// <para>schtasks 不可用时回退到 HKCU\...\Run（会弹 UAC）。</para>
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
            return "计划任务（登录即启，免 UAC 提示）";
        if (RunValue() is not null)
            return "HKCU Run（登录时会弹 UAC）";
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
        Log.Write($"开机自启：计划任务创建失败（{err}），已回退 HKCU Run（登录时会弹 UAC）。");
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
        return Run("schtasks.exe",
            $"/Create /TN \"{TaskName}\" /TR {tr} /SC ONLOGON /RL HIGHEST /IT /F", out error) == 0;
    }

    private static bool DeleteTask(out string output)
        => Run("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F", out output) == 0;

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
