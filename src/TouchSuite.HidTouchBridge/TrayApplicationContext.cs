using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TouchSuite.HidTouchBridge;

/// <summary>
/// 托盘宿主：隐藏一个消息窗收 WM_INPUT（RIDEV_INPUTSINK，窗口不在前台也能收），
/// 托盘菜单提供「启用转发 / 关闭原有触摸 / 转发设备 / 开机自启 / 打开日志 / 退出」。
/// <para>
/// 本程序**不自动改注册表**：<c>TouchGate</c> 只在托盘里手动勾选「关闭原有触摸」时才写，
/// 再点一下就恢复原值；程序退出也不改动它（保持用户最后一次的手动选择）。
/// </para>
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly SyntheticPointerInjector _injector;
    private readonly MonitorInfo _monitor;
    private readonly Options _opt;
    private readonly Config _cfg;

    private readonly InputWindow _msgWindow;
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _forwardItem;
    private readonly ToolStripMenuItem _gateItem;
    private readonly ToolStripMenuItem _deviceMenu;
    private readonly ToolStripMenuItem _autoItem;
    private readonly ToolStripMenuItem _verboseItem;

    /// <summary>当前枚举到的全部 HID 顶层集合（本上下文拥有其生命周期）。</summary>
    private List<RawHidTouch.TouchDevice> _devices;

    /// <summary>当前转发中的触摸屏（可为 null：设备掉了且没重扫到）。</summary>
    private RawHidTouch.TouchDevice? _device;

    private bool _forwarding = true;   // 转发开关：false = 停止转发（不注入）
    /// <summary>全量日志开关：true = 逐帧记录触点/坐标/注入结果。</summary>
    private bool _verbose;
    private bool _exited;
    private long _frames;

    /// <summary>当前按下的槽位（用于 DOWN/UP 诊断日志）。</summary>
    private readonly HashSet<int> _activeLinks = new();

    /// <summary>首次改动前的 TouchGate 原值（供「关闭原有触摸」取消时恢复）。</summary>
    private readonly int? _gateOriginal;

    public TrayApplicationContext(
        SyntheticPointerInjector injector,
        List<RawHidTouch.TouchDevice> devices,
        RawHidTouch.TouchDevice initialDevice,
        MonitorInfo monitor,
        Options opt,
        Config cfg)
    {
        _injector = injector;
        _devices = devices;
        _device = initialDevice;
        _monitor = monitor;
        _opt = opt;
        _cfg = cfg;                         // 记住转发设备用
        _gateOriginal = TouchGate.Read();   // 只读，不改

        _msgWindow = new InputWindow { WmInput = OnWmInput };
        if (!RawHidTouch.Register(_msgWindow.Handle, initialDevice.UsagePage, initialDevice.Usage))
            throw new InvalidOperationException("RegisterRawInputDevices 失败（无法接收 HID 原始输入）。");

        _statusItem = new ToolStripMenuItem("状态：初始化…") { Enabled = false };

        // 转发开关：勾选 = 启用转发（HID → 合成指针），取消 = 停止转发
        _forwardItem = new ToolStripMenuItem("启用转发")
        {
            CheckOnClick = true,
            Checked = true,
            ToolTipText = "勾选 = 启用转发（把该触摸屏的 HID 注入成合成指针）；取消 = 停止转发（不再注入）。",
        };
        _forwardItem.CheckedChanged += (_, _) =>
        {
            _forwarding = _forwardItem.Checked;
            if (!_forwarding)
            {
                _injector.ReleaseAll();   // 停止转发瞬间抬起所有触点，避免卡键
                _activeLinks.Clear();
            }
            Log.Write(_forwarding ? "托盘：已启用转发。" : "托盘：已停止转发。");
            RefreshStatus();
        };

        // 唯一会改注册表的入口：勾上写 TouchGate=0，取消则恢复原值
        _gateItem = new ToolStripMenuItem("关闭原有触摸（TouchGate=0）")
        {
            CheckOnClick = true,
            Checked = TouchGate.Read() == 0,
            ToolTipText = "手动开关：避免与原触摸叠加。取消勾选可恢复原值；程序退出不改动此项。",
        };
        _gateItem.CheckedChanged += OnGateChanged;

        _deviceMenu = new ToolStripMenuItem("转发设备");
        BuildDeviceMenu(initialDevice);

        _autoItem = new ToolStripMenuItem("开机自启") { CheckOnClick = true, Checked = AutoStart.IsEnabled() };
        _autoItem.CheckedChanged += OnAutoStartChanged;

        // 全量日志开关：开启后逐帧记录（排查用；日志文件会明显变大）
        _verbose = _opt.Verbose;
        _verboseItem = new ToolStripMenuItem("详细日志（全量）")
        {
            CheckOnClick = true,
            Checked = _opt.Verbose,
            ToolTipText = "开启后逐帧记录触点数量/坐标/注入结果，以及每次按下-抬起。排查问题时打开。",
        };
        _verboseItem.CheckedChanged += (_, _) =>
        {
            _verbose = _verboseItem.Checked;
            Log.Write(_verbose ? "托盘：已开启全量日志。" : "托盘：已关闭全量日志。");
            RefreshStatus();
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_forwardItem);
        menu.Items.Add(_gateItem);
        menu.Items.Add(_deviceMenu);
        menu.Items.Add(_autoItem);
        menu.Items.Add(_verboseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("打开日志", null, (_, _) => OpenLog()));
        menu.Items.Add(new ToolStripMenuItem("打开日志文件夹", null, (_, _) => OpenLogFolder()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitApp()));

        _tray = new NotifyIcon
        {
            Icon = BuildIcon(),
            Text = "TouchSuite 触摸桥",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) =>
            _tray.ShowBalloonTip(4000, "TouchSuite 触摸桥", StatusText(detailed: true), ToolTipIcon.Info);

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => RefreshStatus();
        _timer.Start();

        RefreshStatus();
        _tray.ShowBalloonTip(4000, "TouchSuite 触摸桥",
            $"已启动：{_injector.ApiName}{(_opt.DryRun ? "（dry-run）" : "")}\n设备：{DeviceName()}", ToolTipIcon.Info);
        Log.Write("托盘已就绪（未改动注册表）。");

        // 首次运行（或自动挑选结果变化时）把当前设备记住，下次启动直接沿用
        if (_cfg.RememberDevice(initialDevice))
            _cfg.Save();
    }

    /// <summary>供 Ctrl+C（附着控制台时）触发干净退出。</summary>
    public void RequestExit()
    {
        try
        {
            if (_msgWindow.IsHandleCreated)
                _msgWindow.BeginInvoke(new Action(ExitApp));
            else
                ExitApp();
        }
        catch
        {
            ExitApp();
        }
    }

    // ================= 收 HID → 注入 =================

    private void OnWmInput(IntPtr lParam)
    {
        if (!_forwarding || _device is null)
            return;
        if (!RawHidTouch.TryGetHidData(lParam, out IntPtr hdev, out byte[][] reports))
            return;
        if (hdev != _device.RawHandle)
            return;

        foreach (byte[] report in reports)
        {
            RawHidTouch.Frame frame = RawHidTouch.Decode(_device, report);
            var actives = new List<(int ContactId, int X, int Y)>(frame.Contacts.Count);
            var nowActive = new HashSet<int>();
            var detail = _verbose ? new List<string>(frame.Contacts.Count) : null;

            foreach (RawHidTouch.Contact c in frame.Contacts)
            {
                // 用触点槽位（Link）当 pointerId：同一次按下内稳定、天然唯一，规避设备 CID 复用/恒 0 的坑
                int px = _monitor.Left + (int)Math.Round(c.Nx * (_monitor.Width - 1));
                int py = _monitor.Top + (int)Math.Round(c.Ny * (_monitor.Height - 1));
                actives.Add((c.Link, px, py));

                nowActive.Add(c.Link);
                if (_activeLinks.Add(c.Link))
                {
                    // 诊断：按下时把「原始 HID 坐标 → 归一化 → 最终屏幕像素」都记下来，
                    // 一跑一看就知道点到底落在哪、有没有真正的 DOWN。
                    Log.Write($"DOWN L{c.Link} cid={c.ContactId} raw=({c.XRaw},{c.YRaw}) "
                        + $"norm=({c.Nx:0.###},{c.Ny:0.###}) screen=({px},{py})");
                }
                detail?.Add($"L{c.Link} cid={c.ContactId} raw=({c.XRaw},{c.YRaw}) screen=({px},{py})");
            }

            foreach (int link in _activeLinks.Where(l => !nowActive.Contains(l)).ToList())
            {
                _activeLinks.Remove(link);
                Log.Write($"UP   L{link}");
            }

            _injector.Apply(actives);
            _frames++;

            // 全量日志：逐帧记录（含空帧），便于把「看到的动作」和「实际注入」对齐
            if (detail is not null)
            {
                Log.Write($"帧 {_frames} 报告接触数={frame.ContactCount} 按下={frame.Contacts.Count} "
                    + $"活动={_injector.ActiveCount} 注入={_injector.InjectedFrames} 失败={_injector.FailedFrames}"
                    + (detail.Count > 0 ? " | " + string.Join(" | ", detail) : ""));
            }
        }
    }

    // ================= 转发设备 =================

    private void BuildDeviceMenu(RawHidTouch.TouchDevice? selected)
    {
        _deviceMenu.DropDownItems.Clear();

        List<RawHidTouch.TouchDevice> candidates = _devices.Where(d => d.Links.Count > 0).ToList();
        if (candidates.Count == 0)
        {
            _deviceMenu.DropDownItems.Add(new ToolStripMenuItem("（无可转发的设备：无带 X/Y 的触摸集合）") { Enabled = false });
        }
        else
        {
            foreach (RawHidTouch.TouchDevice d in candidates)
            {
                var item = new ToolStripMenuItem(DeviceLabel(d))
                {
                    Checked = ReferenceEquals(d, selected),
                    Tag = d,
                };
                item.Click += (s, _) =>
                {
                    if (((ToolStripMenuItem)s!).Tag is RawHidTouch.TouchDevice dev)
                        SelectDevice(dev);
                };
                _deviceMenu.DropDownItems.Add(item);
            }
        }

        _deviceMenu.DropDownItems.Add(new ToolStripSeparator());
        _deviceMenu.DropDownItems.Add(new ToolStripMenuItem("重新扫描设备", null, (_, _) => RescanDevices()));
    }

    private static string DeviceLabel(RawHidTouch.TouchDevice d)
        => $"{d.TlcLabel} · {d.Links.Count} 槽位{(d.IsTouch ? "" : "（非触摸）")} · {RawHidTouch.ShortPath(d.Path)}";

    private void SelectDevice(RawHidTouch.TouchDevice dev)
    {
        if (ReferenceEquals(dev, _device))
            return;

        _injector.ReleaseAll();   // 切换前抬起旧设备的触点，避免卡键
        _activeLinks.Clear();
        _device = dev;
        RawHidTouch.Register(_msgWindow.Handle, dev.UsagePage, dev.Usage);

        foreach (ToolStripItem it in _deviceMenu.DropDownItems)
        {
            if (it is ToolStripMenuItem mi && mi.Tag is RawHidTouch.TouchDevice)
                mi.Checked = ReferenceEquals(mi.Tag, dev);
        }

        Log.Write($"切换转发设备：{dev.TlcLabel} {RawHidTouch.ShortPath(dev.Path)}（槽位 {dev.Links.Count}，触摸={dev.IsTouch}）");
        if (_cfg.RememberDevice(dev))
            _cfg.Save();                     // 记住这次选择，下次启动沿用
        _tray.ShowBalloonTip(3000, "转发设备", "已切换到：\n" + RawHidTouch.ShortPath(dev.Path), ToolTipIcon.Info);
        RefreshStatus();
    }

    /// <summary>重新枚举 HID（设备热插拔后句柄会变），尽量保持原设备，否则挑最优。</summary>
    private void RescanDevices()
    {
        string oldPath = _device?.Path ?? "";
        _injector.ReleaseAll();
        _activeLinks.Clear();

        foreach (RawHidTouch.TouchDevice d in _devices)
            d.Dispose();
        _devices = RawHidTouch.Scan();

        RawHidTouch.TouchDevice? keep =
            _devices.FirstOrDefault(x => x.Path == oldPath && x.Links.Count > 0)
            ?? _devices.Where(x => x.IsTouch && x.Links.Count > 0).OrderByDescending(x => x.Links.Count).FirstOrDefault()
            ?? _devices.FirstOrDefault(x => x.Links.Count > 0);

        _device = null;                 // 旧引用已释放，先失效
        BuildDeviceMenu(keep);
        if (keep is not null)
            SelectDevice(keep);

        Log.Write($"重新扫描：HID 顶层集合 {_devices.Count} 个，转发 {(_device is null ? "（无）" : RawHidTouch.ShortPath(_device.Path))}");
        RefreshStatus();
    }

    // ================= 其它托盘菜单 =================

    private void OnGateChanged(object? sender, EventArgs e)
    {
        try
        {
            if (_gateItem.Checked)
            {
                TouchGate.Write(0);
                Log.Write($"托盘：关闭原有触摸（TouchGate {_gateOriginal?.ToString() ?? "(未设置)"} → 0）");
                _tray.ShowBalloonTip(3000, "关闭原有触摸",
                    "已写入 TouchGate=0（可能要注销/重启才完全生效）。", ToolTipIcon.Info);
            }
            else
            {
                TouchGate.Restore(_gateOriginal);
                Log.Write($"托盘：恢复原有触摸（TouchGate → {_gateOriginal?.ToString() ?? "(删除)"}）");
                _tray.ShowBalloonTip(3000, "关闭原有触摸", "已恢复原值。", ToolTipIcon.Info);
            }
        }
        catch (Exception ex)
        {
            Log.Write("设置 TouchGate 失败：" + ex);
            _gateItem.Checked = TouchGate.Read() == 0;   // 回滚勾选
            _tray.ShowBalloonTip(4000, "关闭原有触摸", "设置失败：" + ex.Message, ToolTipIcon.Warning);
        }
        RefreshStatus();
    }

    private void OnAutoStartChanged(object? sender, EventArgs e)
    {
        try
        {
            if (_autoItem.Checked)
            {
                AutoStart.Enable();
                _tray.ShowBalloonTip(3000, "开机自启", "已开启：" + AutoStart.Describe(), ToolTipIcon.Info);
            }
            else
            {
                AutoStart.Disable();
                _tray.ShowBalloonTip(3000, "开机自启", "已关闭。", ToolTipIcon.Info);
            }
        }
        catch (Exception ex)
        {
            Log.Write("设置开机自启失败：" + ex);
            _autoItem.Checked = AutoStart.IsEnabled();   // 回滚勾选
            _tray.ShowBalloonTip(4000, "开机自启", "设置失败：" + ex.Message, ToolTipIcon.Warning);
        }
    }

    private static void OpenLog()
    {
        try
        {
            Process.Start("notepad.exe", Log.FileFullPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开日志失败：" + ex.Message, "TouchSuite 触摸桥");
        }
    }

    private static void OpenLogFolder()
    {
        try
        {
            Process.Start("explorer.exe", "/select,\"" + Log.FileFullPath + "\"");
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开文件夹失败：" + ex.Message, "TouchSuite 触摸桥");
        }
    }

    // ================= 状态 / 退出 =================

    private string DeviceName() => _device is null ? "（无）" : RawHidTouch.ShortPath(_device.Path);

    private string StatusText(bool detailed = false)
    {
        string state = _forwarding ? "转发中" : "已停止转发";
        string s = $"{state} · {_injector.ApiName} · 活动点 {_injector.ActiveCount} · 帧 {_frames} · 失败 {_injector.FailedFrames}";
        if (detailed)
        {
            s += $"\n设备：{DeviceName()}"
               + $"\n显示器：{_monitor}"
               + $"\n原有触摸：{(TouchGate.Read() == 0 ? "已关闭 (TouchGate=0)" : "未关闭（可在托盘菜单手动关闭）")}"
               + $"\n开机自启：{AutoStart.Describe()}"
               + $"\n全量日志：{(_verbose ? "开" : "关")}"
               + $"\n权限：{(Elevation.IsElevated() ? "管理员（注入可能失效，请改用普通用户运行）" : "普通用户（正确）")}"
               + $"\n配置：{Config.FileFullPath}";
        }
        return s;
    }

    private void RefreshStatus()
    {
        _statusItem.Text = $"状态：{(_forwarding ? "转发中" : "已停止转发")}　活动点 {_injector.ActiveCount}　帧 {_frames}";
        string tip = StatusText();
        _tray.Text = tip.Length <= 63 ? tip : tip.Substring(0, 63);
    }

    private void ExitApp()
    {
        if (_exited)
            return;
        _exited = true;

        try
        {
            _timer.Stop();
            _tray.Visible = false;
            _injector.ReleaseAll();
            // 不自动改动 TouchGate：保持用户最后一次的手动选择
            Log.Write($"退出：帧 {_frames}，注入 {_injector.InjectedFrames}，失败 {_injector.FailedFrames}"
                + $"；TouchGate 保持 {(TouchGate.Read()?.ToString() ?? "(未设置)")}");
            _injector.Dispose();
            _tray.Dispose();
            _msgWindow.Dispose();

            foreach (RawHidTouch.TouchDevice d in _devices)
                d.Dispose();
            _devices.Clear();
        }
        catch (Exception ex)
        {
            Log.Write("退出清理异常：" + ex.Message);
        }

        ExitThread();
    }

    // ================= 图标 =================

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>运行时画一个托盘图标（蓝底白圈白点），避免附带二进制资源。</summary>
    private static Icon BuildIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var fill = new SolidBrush(Color.FromArgb(0x2D, 0x7D, 0xF6));
            g.FillEllipse(fill, 1, 1, 30, 30);
            using var ring = new Pen(Color.White, 3f);
            g.DrawEllipse(ring, 8, 8, 16, 16);
            using var dot = new SolidBrush(Color.White);
            g.FillEllipse(dot, 13, 13, 6, 6);
        }

        IntPtr h = bmp.GetHicon();
        try
        {
            using Icon tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(h);
        }
    }
}
