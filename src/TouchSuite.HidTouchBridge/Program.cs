using System.Text;

namespace TouchSuite.HidTouchBridge;

/// <summary>
/// HID 触摸桥：把本机那块「Win7 正常、Win8+ UWP 异常」的触摸屏读出来，
/// 重新用 user32 合成指针（符合规范的触摸）注入回系统，从而让 UWP 也正常。
///
/// 步骤：
///   1) 写 HKCU\Software\Microsoft\Wisp\Touch\TouchGate = 0，关掉那块屏原有的（非规范）触摸；
///   2) 用 RawInput 直接读它的 HID 报告，逐触点解码（Link/X/Y/接触ID/TipSwitch）；
///   3) 用 CreateSyntheticPointerDevice + InjectSyntheticPointerInput 注入（Win8 回退旧版 API）。
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.Title = "TouchSuite.HidTouchBridge";

        Options opt;
        try
        {
            opt = Options.Parse(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"参数错误：{ex.Message}");
            Options.PrintUsage();
            return 1;
        }

        if (opt.Help)
        {
            Options.PrintUsage();
            return 0;
        }

        Log.Write($"===== 启动 ===== args=[{string.Join(' ', args)}]");

        List<MonitorInfo> monitors = MonitorMap.Enumerate();
        List<RawHidTouch.TouchDevice> devices = RawHidTouch.Scan();
        try
        {
            Console.WriteLine("TouchSuite.HidTouchBridge —— 关闭原触摸 + 读 HID + 注入符合规范的合成指针");
            Console.WriteLine($"  显示器 {monitors.Count} 个：");
            foreach (MonitorInfo m in monitors)
                Console.WriteLine($"    {m}");

            Console.WriteLine($"  HID 顶层集合 {devices.Count} 个（触摸类 {devices.Count(d => d.IsTouch)}）：");
            for (int i = 0; i < devices.Count; i++)
            {
                RawHidTouch.TouchDevice d = devices[i];
                Console.WriteLine($"    [{i + 1}] {d.TlcLabel} 触摸={d.IsTouch} 触点槽位={d.Links.Count} {RawHidTouch.ShortPath(d.Path)}");
            }
            Console.WriteLine();

            if (opt.List)
                return 0;

            if (monitors.Count == 0)
            {
                Console.Error.WriteLine("未枚举到显示器。");
                return 1;
            }

            RawHidTouch.TouchDevice? target = PickDevice(devices, opt.Device);
            if (target is null)
            {
                Console.Error.WriteLine(opt.Device is null
                    ? "未找到触摸类 HID 设备（可用 --device <路径子串> / --list 指定）。"
                    : $"未找到匹配 \"{opt.Device}\" 的设备。");
                return 1;
            }
            if (target.Links.Count == 0)
            {
                Console.Error.WriteLine($"设备 {RawHidTouch.ShortPath(target.Path)} 没有声明任何带 X/Y 的触点槽位，无法解码。");
                return 1;
            }
            if (!target.IsTouch)
                Console.WriteLine($"  提示：选中的 {target.TlcLabel} 不是触摸顶层集合（0x0D:0x04/0x05），结果可能不对。");

            int monitorIndex = Math.Clamp(opt.Monitor, 0, monitors.Count - 1);
            MonitorInfo monitor = monitors[monitorIndex];

            Console.WriteLine($"  目标设备  : {target.TlcLabel}  {RawHidTouch.ShortPath(target.Path)}");
            Console.WriteLine($"  触点槽位  : {string.Join(",", target.Links.Select(l => l.Link))}");
            Console.WriteLine($"  目标显示器: {monitor}");
            Console.WriteLine($"  最大触点  : {opt.MaxContacts}");
            Console.WriteLine($"  接触面积  : {(opt.ContactPx > 0 ? $"{opt.ContactPx}px 直径" : "不注入")}");
            Console.WriteLine($"  映射断点  : ({monitor.Width - 1}, {monitor.Height - 1}) 取整（stretch 全屏）");

            // ---- 1) 关掉原有触摸 ----
            int? oldGate = null;
            if (opt.TouchGate)
            {
                oldGate = TouchGate.Disable();
                Log.Write($"TouchGate：原值 {(oldGate?.ToString() ?? "(未设置)")} → 0（已关闭原有触摸屏功能）");
                Console.WriteLine($"  TouchGate : {(oldGate?.ToString() ?? "(未设置)")} → 0（已关闭原有触摸）");
            }
            else
            {
                Console.WriteLine("  TouchGate : 未改动（--no-touchgate）");
            }

            // ---- 2) 注入器 ----
            using var injector = new SyntheticPointerInjector(opt.MaxContacts, opt.Feedback, opt.ContactPx, opt.DryRun);
            if (!injector.Initialize())
            {
                Console.Error.WriteLine($"初始化触摸注入失败：{injector.LastErrorText}");
                Console.Error.WriteLine("提示：需要运行在交互式桌面会话（已登录、未锁屏）中。");
                RestoreGate(opt, oldGate);
                return 1;
            }
            Console.WriteLine($"  注入接口  : {injector.ApiName}{(opt.DryRun ? "（--dry-run：只算不注入）" : "")}");

            // ---- 3) 收 HID → 注入 ----
            using var window = new InputWindow();
            IntPtr hwnd = window.Handle;
            if (!RawHidTouch.Register(hwnd, target.UsagePage, target.Usage))
            {
                Console.Error.WriteLine("RegisterRawInputDevices 失败。");
                RestoreGate(opt, oldGate);
                return 1;
            }

            long frames = 0;
            long down = 0;
            long lastStatsAt = Environment.TickCount64;

            window.WmInput = lParam =>
            {
                if (!RawHidTouch.TryGetHidData(lParam, out IntPtr hdev, out byte[][] reports))
                    return;
                if (hdev != target.RawHandle)
                    return;

                foreach (byte[] report in reports)
                {
                    RawHidTouch.Frame frame = RawHidTouch.Decode(target, report);
                    var actives = new List<(int ContactId, int X, int Y)>(frame.Contacts.Count);
                    foreach (RawHidTouch.Contact c in frame.Contacts)
                    {
                        // 用触点槽位（Link）当 pointerId：同一次按下内稳定，且天然唯一，规避设备 CID 复用/恒 0 的坑
                        int px = monitor.Left + (int)Math.Round(c.Nx * (monitor.Width - 1));
                        int py = monitor.Top + (int)Math.Round(c.Ny * (monitor.Height - 1));
                        actives.Add((c.Link, px, py));
                    }

                    injector.Apply(actives);
                    frames++;
                    down += frame.Contacts.Count;

                    if (opt.Verbose && frames % 50 == 0)
                    {
                        long now = Environment.TickCount64;
                        double sec = Math.Max(0.001, (now - lastStatsAt) / 1000.0);
                        lastStatsAt = now;
                        Log.Write($"帧 {frames} 活动点 {injector.ActiveCount} 注入 {injector.InjectedFrames} 失败 {injector.FailedFrames} "
                            + $"(接触数 {frame.ContactCount}) 首点 {(frame.Contacts.Count > 0 ? $"L{frame.Contacts[0].Link} CID={frame.Contacts[0].ContactId} n=({frame.Contacts[0].Nx:0.###},{frame.Contacts[0].Ny:0.###})" : "无")}"
                            + (injector.FailedFrames > 0 ? $" 错误={injector.LastErrorText}" : ""));
                    }
                }
            };

            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                try { window.BeginInvoke(new Action(Application.ExitThread)); } catch { /* 忽略 */ }
            };

            Console.WriteLine();
            Console.WriteLine("运行中…… 在触摸屏上操作即可。按 Ctrl+C 退出（退出会恢复 TouchGate）。");
            if (!opt.TouchGate)
                Console.WriteLine("  注意：未改动 TouchGate，可能与原有触摸叠加（双重输入）。");
            Application.Run(new ApplicationContext());

            injector.ReleaseAll();
            RestoreGate(opt, oldGate);
            Log.Write($"结束：累计子报文 {frames}，累计触点 {down}，注入帧 {injector.InjectedFrames}，失败 {injector.FailedFrames}");
            return 0;
        }
        finally
        {
            foreach (RawHidTouch.TouchDevice d in devices)
                d.Dispose();
        }
    }

    private static void RestoreGate(Options opt, int? oldGate)
    {
        if (!opt.TouchGate)
            return;
        TouchGate.Restore(oldGate);
        Log.Write($"TouchGate 已恢复为 {(oldGate?.ToString() ?? "(删除)")}");
    }

    /// <summary>选设备：给了 --device 就按路径子串匹配；否则挑「触点槽位最多」的触摸类设备。</summary>
    private static RawHidTouch.TouchDevice? PickDevice(List<RawHidTouch.TouchDevice> devices, string? filter)
    {
        if (devices.Count == 0)
            return null;

        if (!string.IsNullOrEmpty(filter))
        {
            return devices.FirstOrDefault(d =>
                       d.Path.Contains(filter, StringComparison.OrdinalIgnoreCase))
                   ?? devices.FirstOrDefault(d =>
                       RawHidTouch.ShortPath(d.Path).Contains(filter, StringComparison.OrdinalIgnoreCase));
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
    /// <summary>是否改动 TouchGate（默认改，退出时恢复）。</summary>
    public bool TouchGate = true;
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
                case "--no-touchgate":
                    o.TouchGate = false;
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

    public static void PrintUsage()
    {
        Console.WriteLine("""
            用法: TouchSuite.HidTouchBridge [选项]

              说明：先把 HKCU\Software\Microsoft\Wisp\Touch\TouchGate 置 0 关掉原有触摸屏功能，
                    再用 RawInput 读该触摸屏的 HID 报告，最后用 user32 合成指针注入「符合规范」的触摸。
                    退出时自动恢复 TouchGate 原值。

              --list                 只列出 HID 设备与显示器后退出
              --device <子串>        按设备路径子串选触摸屏（默认自动挑触点最多的触摸类设备）
              --monitor <n>          目标显示器序号（默认 0）
              --feedback <mode>      触摸视觉反馈：default|indirect|none（默认 default）
              --max-contacts <n>     最大同时接触点（默认 10）
              --contact-size <px>    注入的接触面积直径（默认 8；0 = 不注入接触面积）
              --dry-run              只解码不注入（不碰桌面，用于核对坐标）
              --no-touchgate         不改动 TouchGate（调试用；会与原有触摸叠加）
              -v, --verbose          打印统计
              -h, --help             显示本帮助

            注意：TouchGate 的改动可能要注销/重启才完全生效；若关闭后仍有双重输入，请注销再登录。
            """);
    }
}

/// <summary>落地日志：同时写控制台和 exe 同级的 TouchSuite.HidTouchBridge.log。</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static readonly string FilePath =
        Path.Combine(AppContext.BaseDirectory, "TouchSuite.HidTouchBridge.log");

    public static void Write(string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        lock (Gate)
        {
            Console.WriteLine(line);
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
}
