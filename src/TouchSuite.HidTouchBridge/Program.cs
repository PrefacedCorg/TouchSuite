using System.Runtime.InteropServices;
using System.Text;

namespace TouchSuite.HidTouchBridge;

/// <summary>
/// HID 触摸桥：把本机那块「Win7 正常、Win8+ UWP 异常」的触摸屏读出来，
/// 重新用 user32 合成指针（符合规范的触摸）注入回系统，从而让 UWP 也正常。
///
/// 步骤（TouchGate 只在托盘菜单里手动开关，启动/退出都不自动改）：
///   1) 用 RawInput 直接读触摸屏的 HID 报告，逐触点解码（Link/X/Y/接触ID/TipSwitch）；
///   2) 用 CreateSyntheticPointerDevice + InjectSyntheticPointerInput 注入（Win8 回退旧版 API）；
///   3) 托盘菜单可手动「关闭原有触摸」（写 HKCU\...\Wisp\Touch\TouchGate = 0），避免与原触摸叠加。
///
/// 平时以**普通用户**运行（asInvoker）—— 合成指针注入必须在用户的中完整性会话里，提权反而会失效；
/// 只有「开机自启」建计划任务那一刻才临时请求一次 UAC。
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Cli.Attach();
        if (Cli.HasConsole)
        {
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
                Console.Title = "TouchSuite.HidTouchBridge";
            }
            catch
            {
                // 附着控制台失败不影响运行
            }
        }

        Options opt;
        try
        {
            opt = Options.Parse(args);
        }
        catch (Exception ex)
        {
            Cli.Error($"参数错误：{ex.Message}{Environment.NewLine}{Environment.NewLine}{Options.UsageText()}");
            return 1;
        }

        if (opt.Help)
        {
            Cli.Output(Options.UsageText());
            return 0;
        }

        // 默认**不提权**：user32 合成指针注入必须发生在用户的中完整性会话里，
        // 提权（高完整性）注入的触摸送不到普通窗口 —— 症状是注入返回成功但点不动。
        // 只有确实需要时才用 --elevate 显式提权。
        if (opt.Elevate && !opt.AlreadyElevated && !Elevation.IsElevated())
        {
            Log.Write("按 --elevate 要求，尝试以管理员身份重启…");
            if (Elevation.RelaunchElevated(args))
                return 0;
            Cli.Error("提权被取消或失败，将以当前（普通）权限继续运行。");
        }

        Log.Write($"===== 启动 ===== args=[{string.Join(" ", args)}] 管理员={Elevation.IsElevated()}");

        List<MonitorInfo> monitors = MonitorMap.Enumerate();
        List<RawHidTouch.TouchDevice> devices = RawHidTouch.Scan();
        bool trayOwnsDevices = false;   // 托盘接管设备生命周期后，这里不再负责释放
        try
        {
            if (opt.List)
            {
                Cli.Output(BuildListText(monitors, devices));
                return 0;
            }

            if (monitors.Count == 0)
            {
                Cli.Error("未枚举到显示器。");
                return 1;
            }

            Config cfg = Config.Load();

            // 选设备优先级：命令行 --device > 上次记住的那块 > 自动挑（触点槽位最多）
            RawHidTouch.TouchDevice? target = opt.Device is not null
                ? PickDevice(devices, opt.Device)
                : cfg.FindRemembered(devices) ?? PickDevice(devices, null);
            if (target is null)
            {
                Cli.Error(opt.Device is null
                    ? "未找到触摸类 HID 设备（可用 --device <路径子串> 指定，或 --list 查看）。"
                    : $"未找到匹配 \"{opt.Device}\" 的设备（--list 查看可用设备）。");
                return 1;
            }
            if (opt.Device is null && cfg.DevicePath is not null && target.Path == cfg.DevicePath)
                Log.Write("沿用上次记住的转发设备。");
            if (target.Links.Count == 0)
            {
                Cli.Error($"设备 {RawHidTouch.ShortPath(target.Path)} 没有声明任何带 X/Y 的触点槽位，无法解码。");
                return 1;
            }

            int monitorIndex = Compat.Clamp(opt.Monitor, 0, monitors.Count - 1);
            MonitorInfo monitor = monitors[monitorIndex];

            Log.Write($"设备：{target.TlcLabel} {RawHidTouch.ShortPath(target.Path)}（槽位 {target.Links.Count}，触摸={target.IsTouch}）");
            Log.Write($"显示器：{monitor}；最大触点={opt.MaxContacts}；接触面积={opt.ContactPx}px");

            // 不在这里自动改注册表 —— 关闭「原有触摸」(TouchGate=0) 只由托盘菜单触发。

            // ---- 注入器 ----
            using var injector = new SyntheticPointerInjector(opt.MaxContacts, opt.Feedback, opt.ContactPx, opt.DryRun);
            if (!injector.Initialize())
            {
                Cli.Error($"初始化触摸注入失败：{injector.LastErrorText}{Environment.NewLine}"
                    + "提示：需要运行在交互式桌面会话（已登录、未锁屏）中。");
                return 1;
            }
            Log.Write($"注入接口：{injector.ApiName}");

            // ---- 托盘常驻：隐藏消息窗收 HID，注入合成指针 ----
            TrayApplicationContext tray;
            try
            {
                tray = new TrayApplicationContext(injector, devices, target, monitor, opt, cfg);
                trayOwnsDevices = true;   // 之后由托盘负责在切换/退出时释放设备
            }
            catch (Exception ex)
            {
                Cli.Error("托盘/原始输入初始化失败：" + ex.Message);
                return 1;
            }

            using (tray)
            {
                if (Cli.HasConsole)
                {
                    Console.CancelKeyPress += (_, e) =>
                    {
                        e.Cancel = true;
                        tray.RequestExit();
                    };
                }

                Application.Run(tray);
            }

            return 0;
        }
        finally
        {
            if (!trayOwnsDevices)
            {
                foreach (RawHidTouch.TouchDevice d in devices)
                    d.Dispose();
            }
        }
    }

    /// <summary>选设备：给了 --device 就按路径子串匹配；否则挑「触点槽位最多」的触摸类设备。</summary>
    private static RawHidTouch.TouchDevice? PickDevice(List<RawHidTouch.TouchDevice> devices, string? filter)
    {
        if (devices.Count == 0)
            return null;

        if (!string.IsNullOrEmpty(filter))
        {
            return devices.FirstOrDefault(d =>
                       d.Path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                   ?? devices.FirstOrDefault(d =>
                       RawHidTouch.ShortPath(d.Path).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        RawHidTouch.TouchDevice? best = null;
        foreach (RawHidTouch.TouchDevice d in devices)
        {
            if (!d.IsTouch || d.Links.Count == 0)
                continue;
            if (best is null || d.Links.Count > best.Links.Count)
                best = d;
        }
        return best ?? devices.FirstOrDefault(d => d.IsTouch) ?? devices[0];
    }

    private static string BuildListText(List<MonitorInfo> monitors, List<RawHidTouch.TouchDevice> devices)
    {
        var sb = new StringBuilder();
        sb.AppendLine("显示器：");
        foreach (MonitorInfo m in monitors)
            sb.AppendLine("  " + m);
        sb.AppendLine($"HID 顶层集合 {devices.Count} 个（触摸类 {devices.Count(d => d.IsTouch)}）：");
        for (int i = 0; i < devices.Count; i++)
        {
            RawHidTouch.TouchDevice d = devices[i];
            sb.AppendLine($"  [{i + 1}] {d.TlcLabel} 触摸={d.IsTouch} 触点槽位={d.Links.Count} {RawHidTouch.ShortPath(d.Path)}");
        }
        return sb.ToString();
    }
}

/// <summary>
/// 控制台交互：WinExe 默认没有控制台。若从终端启动（有父控制台），就附加上去，
/// 让 Console.Write/WriteLine 与日志正常打印；否则帮助/错误走弹窗。
/// </summary>
internal static class Cli
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    public static bool HasConsole { get; private set; }

    public static void Attach()
    {
        try
        {
            HasConsole = AttachConsole(AttachParentProcess);
            if (!HasConsole)
                return;
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }
        catch
        {
            HasConsole = false;
        }
    }

    public static void Output(string text)
    {
        if (HasConsole)
        {
            Console.Write(text);
            return;
        }
        MessageBox.Show(text, "TouchSuite.HidTouchBridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    public static void Error(string text)
    {
        Log.Write("错误：" + text.Replace(Environment.NewLine, " "));
        if (HasConsole)
            Console.Error.WriteLine(text);
        else
            MessageBox.Show(text, "TouchSuite.HidTouchBridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}

/// <summary>隐藏消息窗：只借它的 HWND 收 WM_INPUT（RIDEV_INPUTSINK，窗口不在前台也能收）。</summary>
internal sealed class InputWindow : Form
{
    private const int WM_INPUT = 0x00FF;

    public Action<IntPtr>? WmInput;

    public InputWindow()
    {
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        // 不 Show：访问 .Handle 会创建窗口句柄，WM_INPUT 会投递到这里
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_INPUT)
        {
            WmInput?.Invoke(m.LParam);
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }
}

internal sealed class Options
{
    /// <summary>设备路径子串；null = 自动挑触点槽位最多的触摸类设备。</summary>
    public string? Device;
    public int Monitor;
    public FeedbackMode Feedback = FeedbackMode.Default;
    public uint MaxContacts = 10;
    /// <summary>注入的接触面积直径（像素）；0 = 不注入接触面积。</summary>
    public int ContactPx = 8;
    public bool DryRun;
    /// <summary>是否以管理员身份重启（默认否；提权会让合成指针注入失效）。</summary>
    public bool Elevate;
    /// <summary>内部哨兵：本次已是提权后的实例（避免反复提权）。</summary>
    public bool AlreadyElevated;
    public bool Verbose;
    public bool List;
    public bool Help;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{key} 缺少取值");

            switch (key)
            {
                case "-h":
                case "--help":
                    o.Help = true;
                    break;
                case "--list":
                    o.List = true;
                    break;
                case "--device":
                    o.Device = Next();
                    break;
                case "--monitor":
                    o.Monitor = int.Parse(Next());
                    break;
                case "--feedback":
                    o.Feedback = Next().ToLowerInvariant() switch
                    {
                        "default" => FeedbackMode.Default,
                        "indirect" => FeedbackMode.Indirect,
                        "none" => FeedbackMode.None,
                        var v => throw new ArgumentException($"未知反馈模式 {v}（可选 default/indirect/none）"),
                    };
                    break;
                case "--max-contacts":
                    o.MaxContacts = uint.Parse(Next());
                    break;
                case "--contact-size":
                    o.ContactPx = int.Parse(Next());
                    if (o.ContactPx < 0) throw new ArgumentException("--contact-size 不能为负");
                    break;
                case "--dry-run":
                    o.DryRun = true;
                    break;
                case "--elevate":
                    o.Elevate = true;
                    break;
                case "--elevated":
                    o.AlreadyElevated = true;
                    break;
                case "-v":
                case "--verbose":
                    o.Verbose = true;
                    break;
                default:
                    throw new ArgumentException($"未知参数 {key}");
            }
        }
        return o;
    }

    public static string UsageText() => """
        用法: TouchSuite.HidTouchBridge [选项]

          说明：默认以普通用户运行（**不要提权** —— 提权会让合成指针注入失效），常驻托盘。
                用 RawInput 读触摸屏的 HID 报告，再用 user32 合成指针注入「符合规范」的触摸。
                本程序不自动改系统设置 ——「关闭原有触摸」(TouchGate=0) 只在托盘菜单里手动开关，
                需要时勾一下即可（再点一下就恢复原值）。

          托盘菜单：启用转发 / 关闭原有触摸（TouchGate=0） / 转发设备 / 开机自启 / 详细日志（全量） / 打开日志 / 退出
            开机自启走「计划任务（登录即启）」，建任务时临时弹一次 UAC；失败回退 HKCU Run。

          --list                 只列出 HID 设备与显示器后退出
          --device <子串>        按设备路径子串选触摸屏（默认沿用上次记住的，否则挑触点最多的触摸类设备）
          --monitor <n>          目标显示器序号（默认 0）
          --feedback <mode>      触摸视觉反馈：default|indirect|none（默认 default）
          --max-contacts <n>     最大同时接触点（默认 10）
          --contact-size <px>    注入的接触面积直径（默认 8；0 = 不注入接触面积）
          --dry-run              只解码不注入（不碰桌面，用于核对坐标）
          --elevate              以管理员身份重启自身（一般不需要，且会让注入失效；仅个别场景用）
          -v, --verbose          启动即开全量日志（等同托盘「详细日志（全量）」开关）
          -h, --help             显示本帮助

        注意：托盘里「关闭原有触摸」的改动可能要注销/重启才完全生效；关掉后即恢复原有触摸。
        """;
}

/// <summary>落地日志：同时写控制台（若已附着）和 exe 同级的 TouchSuite.HidTouchBridge.log。</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static readonly string FileName = "TouchSuite.HidTouchBridge.log";

    public static string FileFullPath { get; } = Path.Combine(AppContext.BaseDirectory, FileName);

    public static void Write(string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        lock (Gate)
        {
            Console.WriteLine(line);
            try
            {
                File.AppendAllText(FileFullPath, line + Environment.NewLine);
            }
            catch
            {
                // 写盘失败不影响主流程
            }
        }
    }
}
