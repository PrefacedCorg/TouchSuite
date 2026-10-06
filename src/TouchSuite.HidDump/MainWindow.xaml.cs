using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace TouchSuite.HidDump;

/// <summary>
/// 左触摸框 + 右表格：左边触摸/手掌按压（灰框=整屏等比缩放，红圈按全屏归一化坐标落进框里），
/// 右边每段一个表格（页 / Usage / Link / 范围属性 / 当前值），列宽全部 Auto 并用 SharedSizeGroup 跨段对齐；
/// 页列与 Link 列相同值合并只显示一次；段标题、图例、描述符在表格外，不参与列宽计算。
/// 数据走 RawInput（INPUTSINK）：不打开设备，不受句柄独占影响；WM_INPUT 解包参考 HidReader.cs。
/// </summary>
public partial class MainWindow : Window
{
    private const int WM_INPUT = 0x00FF;
    private const long RawContactTtlMs = 300;   // 红圈静默超时消失
    private const int MouseDotKey = -1;

    private List<HidApi.HidDevice> _devices = new();
    /// <summary>下拉框里实际列出的设备（按「显示非触摸设备」过滤后），索引与 DeviceCombo 一一对应。</summary>
    private readonly List<HidApi.HidDevice> _comboDevices = new();
    private HidApi.HidDevice? _selected;
    private readonly Dictionary<IntPtr, HidApi.HidDevice> _byHandle = new();
    private bool _suppressCombo;
    private long _lastRescanTicks;

    private readonly List<LiveRow> _rows = new();
    private readonly List<string> _reportLines = new();              // 导出报告用的文本（与界面同步）
    private readonly Dictionary<LiveRow, int> _reportIndex = new();   // 实时行 → 报告里对应的行号
    private bool _dirty;
    private long _frames;
    private string _lastHex = "";
    private int _lastHexLen;

    // 左侧可视化：每个 Link（触点槽位）各一套参照 cap（支持多指）
    private sealed class LinkCaps
    {
        public ushort Link;
        public HidApi.ValueCap? X, Y, W, H, Id, Az;   // Az = 0x0D:0x3F 方位角
        public HidApi.ButtonCap? Tip;                 // 0x0D:0x42 笔尖接触
    }

    private readonly Dictionary<ushort, LinkCaps> _visLinks = new();

    private sealed class RawContact
    {
        public required double Xn;
        public required double Yn;
        public double? Wn;
        public double? Hn;
        public double? AzDeg;      // HID Azimuth：绕 Z 轴逆时针，0 = 竖直向上（单位度）
        public required long Seen;
    }

    /// <summary>一个接触的可视元素：包围盒椭圆 + 方位角指针 + 角标。</summary>
    private sealed class ContactVisual
    {
        public required Ellipse Box;
        public required Line Needle;
        public required TextBlock Label;
    }

    private sealed class LiveRow
    {
        public bool IsButton;
        public bool IsArray;                 // 值帽数组（ReportCount > 1）
        public bool Summary;
        public ushort Page, Link, Usage;
        public HidApi.ValueCap? Cap;
        public HidApi.ButtonCap? Btn;
        public string Text = "—";
        public TextBlock ValueCell = null!;
    }

    private LiveRow? _contactCountRow;       // 0x0D:0x54 接触数量（状态栏显示实时值）
    private int _declaredSlots;              // 描述符声明的触点槽位数（Link 组数）
    private bool _allowAnyLink;              // 单触点设备才允许"全集合搜索"兜底（多触点严格按 Link 取，防串指）

    // 子报文解码探针（用于"最近报文"段：每份子报文里到底哪几个 Link 活着）
    private HidApi.ValueCap? _probeContactCount;
    private readonly List<(ushort Link, HidApi.ValueCap? Id, HidApi.ButtonCap? Tip, HidApi.ValueCap? X, HidApi.ValueCap? Y, HidApi.ValueCap? W, HidApi.ValueCap? H)> _probeLinks = new();
    private readonly List<TextBlock> _logLines = new();
    private readonly List<string> _pendingLog = new();
    private bool _logDirty;
    private const int LogLineCount = 12;
    private long _frameIndex;
    private long _lastSubLogTicks;           // 子报文日志节流
    private long _lastRowLogTicks;           // 表格快照日志节流
    private string? _lastSubSig;             // 上一份子报文的触点状态签名（Tip/CID/接触数），用于识别状态跳变

    private readonly Dictionary<int, RawContact> _rawContacts = new();     // key = 槽位(Link)
    private readonly Dictionary<int, ContactVisual> _rawVisuals = new();   // 同一批接触的可视元素
    private readonly Dictionary<int, Ellipse> _touchDots = new();        // WPF 触摸/鼠标蓝点

    private readonly DispatcherTimer _renderTimer;
    private Rectangle? _screenFrame;

    // 当前段的表格（列宽 Auto + SharedSizeGroup）
    private Grid _grid = null!;
    private int _tableRow;

    private static readonly Brush PageBrush = new SolidColorBrush(Color.FromRgb(0x22, 0x55, 0x9B));
    private static readonly Brush UsageBrush = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22));
    private static readonly Brush DimBrush = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77));
    private static readonly Brush ValueBrush = new SolidColorBrush(Color.FromRgb(0xC2, 0x59, 0x00));
    private static readonly Brush HeaderBrush = new SolidColorBrush(Color.FromRgb(0x22, 0x55, 0x99));
    private static readonly Brush MonoBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));
    private static readonly Brush MonoIdBrush = new SolidColorBrush(Color.FromRgb(0x7A, 0x3F, 0xA8));

    public MainWindow()
    {
        InitializeComponent();
        _renderTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(60),
        };
        _renderTimer.Tick += (_, _) => RenderTick();
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _renderTimer.Stop();
            foreach (HidApi.HidDevice d in _devices)
                d.Dispose();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource src)
            src.AddHook(WndProc);
    }

    private void OnTouchHostSizeChanged(object sender, SizeChangedEventArgs e) => RenderTick();

    private void OnRealPosChanged(object sender, RoutedEventArgs e)
    {
        // XAML 解析期 IsChecked="True" 会提前触发，此时后面的控件还没连上 → 忽略
        if (!IsLoaded)
            return;
        RenderTick();
    }

    /// <summary>红圈模式：true = 按真实屏幕位置 1:1 映射；false = 面板里画整屏等比缩放示意图。</summary>
    private bool RealPos => RealPosCheck?.IsChecked == true;

    // ================= 枚举与选择 =================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _renderTimer.Start();
        Rescan();
    }

    private void Rescan()
    {
        foreach (HidApi.HidDevice d in _devices)
            d.Dispose();

        _devices = HidApi.Scan();
        _byHandle.Clear();
        foreach (HidApi.HidDevice d in _devices)
            _byHandle[d.RawHandle] = d;

        _suppressCombo = true;
        DeviceCombo.Items.Clear();
        _comboDevices.Clear();
        _suppressCombo = false;

        Log.Info($"枚举到 HID 顶层集合 {_devices.Count} 个（触摸类 {_devices.Count(x => x.IsTouch)}）：");
        for (int i = 0; i < _devices.Count; i++)
        {
            HidApi.HidDevice d = _devices[i];
            Log.Info($"  [{i + 1}] {HidApi.TlcText(d.UsagePage, d.Usage)} 值帽 {d.Caps.NumberInputValueCaps} 按钮帽 {d.Caps.NumberInputButtonCaps} 输入长度 {d.Caps.InputReportByteLength}B 触摸={d.IsTouch} {d.Path}");
        }

        RefreshDeviceCombo();
    }

    /// <summary>
    /// 按「显示非触摸设备」勾选状态重建下拉列表（默认只列触摸类）。
    /// keepPath 指定时要尽量保持原选中项，找不到再回退到"能力最强的触摸设备"。
    /// </summary>
    private void RefreshDeviceCombo(string? keepPath = null)
    {
        string? want = keepPath ?? _selected?.Path;
        bool showAll = ShowAllCheck?.IsChecked == true;

        _suppressCombo = true;
        _comboDevices.Clear();
        DeviceCombo.Items.Clear();
        foreach (HidApi.HidDevice d in _devices)
        {
            if (!showAll && !d.IsTouch)
                continue;
            _comboDevices.Add(d);
            DeviceCombo.Items.Add($"{HidApi.TlcText(d.UsagePage, d.Usage)}　{ShortPath(d.Path)}");
        }
        _suppressCombo = false;

        int idx = want is null ? -1 : _comboDevices.FindIndex(x => x.Path == want);
        if (idx < 0)
        {
            // 默认选触摸类里"能力最强"的一块（值帽最多的那个，通常就是多点触摸数字化器）
            int best = -1;
            for (int i = 0; i < _comboDevices.Count; i++)
            {
                if (!_comboDevices[i].IsTouch)
                    continue;
                if (best < 0 || _comboDevices[i].Caps.NumberInputValueCaps > _comboDevices[best].Caps.NumberInputValueCaps)
                    best = i;
            }
            idx = best >= 0 ? best : (_comboDevices.Count > 0 ? 0 : -1);
        }

        if (idx >= 0)
        {
            DeviceCombo.SelectedIndex = idx;   // 触发 OnDevicePicked
        }
        else
        {
            _selected = null;
            Rows.Children.Clear();
            ProductText.Text = "";
            SetStatus(showAll ? "未发现任何 HID 设备。" : "未发现触摸类 HID 设备（可勾选「显示非触摸设备」查看全部）。");
        }
    }

    private void OnShowAllChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;
        RefreshDeviceCombo();
    }

    private void OnRescan(object sender, RoutedEventArgs e) => Rescan();

    private void OnDevicePicked(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCombo)
            return;
        int i = DeviceCombo.SelectedIndex;
        if (i < 0 || i >= _comboDevices.Count)
            return;

        _selected = _comboDevices[i];
        _frames = 0;
        _lastHex = "";
        _lastHexLen = 0;
        _rawContacts.Clear();
        TouchHint.Visibility = Visibility.Visible;

        BuildTable(_selected);

        // 原始报告描述符：走父 USB 集线器取（不依赖 HID 句柄）
        AddDescriptorSection(_selected.Path);

        // 产品串仅供参考；RawInput 不需要打开设备
        IntPtr h = HidApi.Open(_selected.Path, out string err);
        if (HidApi.Ok(h))
        {
            var (product, maker, serial) = HidApi.Strings(h);
            HidApi.Close(h);
            ProductText.Text = $"产品：{Blank(product)}　制造商：{Blank(maker)}　序列号：{Blank(serial)}　路径：{ShortPath(_selected.Path)}";
        }
        else
        {
            ProductText.Text = $"（设备句柄被其他程序占用：Win32 {err}；不影响 RawInput 实时取值）　路径：{ShortPath(_selected.Path)}";
        }

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        bool ok = RawInput.Register(hwnd, _selected.UsagePage, _selected.Usage);
        Log.Info($"选中 [{i + 1}] {HidApi.TlcText(_selected.UsagePage, _selected.Usage)} 原始输入注册={ok}；日志文件 {Log.Path}");
        SetStatus($"原始输入注册 = {ok}（{HidApi.UsageText(_selected.UsagePage, _selected.Usage)}）。在左边触摸，表格“当前值”列实时刷新。日志：{Log.Path}");
    }

    // ================= 表格：每段一个 Grid，列宽 Auto 且跨段共享 =================

    private void BuildTable(HidApi.HidDevice d)
    {
        Rows.Children.Clear();
        _rows.Clear();
        _reportLines.Clear();
        _reportIndex.Clear();
        _probeLinks.Clear();
        _logLines.Clear();
        _pendingLog.Clear();
        _probeContactCount = null;
        _lastSubSig = null;                      // 换设备后第一帧不算跳变
        _allowAnyLink = false;
        _visLinks.Clear();
        _rawVisuals.Clear();

        List<HidApi.ValueCap> inVals = HidApi.ValueCaps(d.Preparsed, HidApi.ReportTypeInput, d.Caps.NumberInputValueCaps);
        List<HidApi.ButtonCap> inBtns = HidApi.ButtonCaps(d.Preparsed, HidApi.ReportTypeInput, d.Caps.NumberInputButtonCaps);
        List<HidApi.ValueCap> outVals = HidApi.ValueCaps(d.Preparsed, HidApi.ReportTypeOutput, d.Caps.NumberOutputValueCaps);
        List<HidApi.ButtonCap> outBtns = HidApi.ButtonCaps(d.Preparsed, HidApi.ReportTypeOutput, d.Caps.NumberOutputButtonCaps);
        List<HidApi.ValueCap> feaVals = HidApi.ValueCaps(d.Preparsed, HidApi.ReportTypeFeature, d.Caps.NumberFeatureValueCaps);
        List<HidApi.ButtonCap> feaBtns = HidApi.ButtonCaps(d.Preparsed, HidApi.ReportTypeFeature, d.Caps.NumberFeatureButtonCaps);

        StartGrid();
        AddCells("页", "Usage", "Link", "逻辑 / 物理 / RC（Report Count×位数）/ 属性", "当前值", isHeader: true);
        AddNote("RC = Report Count × 报文位宽；范围 / 别名 = 范围帽 / 别名帽；ID = 报文 ID；Link = 触点（LinkCollection）；页列与 Link 列重复值已合并。", DimBrush, mono: false);
        int linkGroups = inVals.Where(c => c.LinkCollection != 0).Select(c => c.LinkCollection).Distinct().Count();
        _declaredSlots = linkGroups;
        HidApi.ValueCap? maxCap = feaVals.FirstOrDefault(c => c.UsagePage == 0x0D && c.UsageMin == 0x55);
        AddNote($"触点由描述符声明：Link 组 {linkGroups} 个"
                + (maxCap is null ? "" : $"；最大接触数（特征 0x0D:0x55）逻辑 {maxCap.LogicalMin}..{maxCap.LogicalMax}")
                + "；运行时每帧实际按下的 Link 见状态栏（接触数量 0x0D:0x54）。", DimBrush, mono: false);

        // ---- 输入 · 值：按 页 → Link → usage 排序，页列/Link 列只显示第一次；数组帽（RC>1）实时解 ----
        AddSection($"输入 · 值（{inVals.Count}）");
        _contactCountRow = null;
        var valEntries = new List<(ushort Page, ushort Link, ushort Usage, HidApi.ValueCap Cap, bool Live, bool Array)>();
        foreach (HidApi.ValueCap c in inVals)
        {
            TrackVisualCap(c);
            int count = c.IsRange ? c.UsageMax - c.UsageMin + 1 : 1;
            if (c.ReportCount > 1)
            {
                valEntries.Add((c.UsagePage, c.LinkCollection, c.UsageMin, c, true, true));
                continue;
            }
            if (count > 16)
            {
                valEntries.Add((c.UsagePage, c.LinkCollection, c.UsageMin, c, false, false));
                continue;
            }
            for (int u = c.UsageMin; u <= c.UsageMax; u++)
                valEntries.Add((c.UsagePage, c.LinkCollection, (ushort)u, c, true, false));
        }
        List<ushort> pageOrder = valEntries.Select(x => x.Page).Distinct().ToList();
        valEntries = valEntries.OrderBy(x => pageOrder.IndexOf(x.Page)).ThenBy(x => x.Link).ThenBy(x => x.Usage).ToList();

        ushort prevPage = ushort.MaxValue, prevLink = ushort.MaxValue;
        foreach (var (page, link, usage, cap, live, isArray) in valEntries)
        {
            string pageText = page != prevPage ? HidApi.PageText(page) : "";
            string linkText = link != prevLink ? link.ToString() : "";
            prevPage = page;
            prevLink = link;

            string rangeText = HidApi.RangeText(cap);
            if (!live)
                rangeText += $"　（范围 {cap.UsageMax - cap.UsageMin + 1} 项，不实时解）";

            LiveRow? liveRow = live ? new LiveRow { Cap = cap, Page = page, Link = link, Usage = usage, IsArray = isArray } : null;
            if (liveRow is not null && page == 0x0D && usage == 0x54)
                _contactCountRow = liveRow;
            AddDataRow(pageText, HidApi.UsageLabel(page, usage) + (isArray ? "（数组）" : ""), linkText, rangeText, liveRow);
        }
        if (inVals.Count == 0)
            AddDataRow("", "", "", "（无）", null);

        // ---- 输入 · 按钮 ----
        AddSection($"输入 · 按钮（{inBtns.Count}）");
        var btnEntries = new List<(ushort Page, ushort Link, ushort Usage, HidApi.ButtonCap Btn, bool Summary)>();
        foreach (HidApi.ButtonCap b in inBtns)
        {
            int count = b.IsRange ? b.UsageMax - b.UsageMin + 1 : 1;
            if (count > 4)
            {
                btnEntries.Add((b.UsagePage, b.LinkCollection, 0, b, true));
                continue;
            }
            for (int u = b.UsageMin; u <= b.UsageMax; u++)
                btnEntries.Add((b.UsagePage, b.LinkCollection, (ushort)u, b, false));
        }
        List<ushort> btnPageOrder = btnEntries.Select(x => x.Page).Distinct().ToList();
        btnEntries = btnEntries.OrderBy(x => btnPageOrder.IndexOf(x.Page)).ThenBy(x => x.Link).ThenBy(x => x.Usage).ToList();

        foreach (HidApi.ButtonCap b in inBtns)
            TrackVisualButton(b);   // 记下各 Link 的「笔尖接触」，左侧红圈据此判断哪几个槽位真的按下

        prevPage = prevLink = ushort.MaxValue;
        foreach (var (page, link, usage, btn, summary) in btnEntries)
        {
            string pageText = page != prevPage ? HidApi.PageText(page) : "";
            string linkText = link != prevLink ? link.ToString() : "";
            prevPage = page;
            prevLink = link;

            string rangeText = HidApi.RangeText(btn) + (summary ? "　（按下汇总）" : "");
            var liveRow = new LiveRow { Btn = btn, Page = page, Link = link, Usage = usage, IsButton = true, Summary = summary };
            AddDataRow(pageText, summary ? "（整段）" : HidApi.UsageLabel(page, usage), linkText, rangeText, liveRow);
        }
        if (inBtns.Count == 0)
            AddDataRow("", "", "", "（无）", null);

        // ---- 输出 / 特征（仅范围） ----
        AddSection($"输出（值 {outVals.Count} / 按钮 {outBtns.Count}）　特征（值 {feaVals.Count} / 按钮 {feaBtns.Count}）　仅列范围");
        int others = 0;
        foreach (HidApi.ValueCap c in outVals) { AddStaticCap(c); others++; }
        foreach (HidApi.ButtonCap b in outBtns) { AddStaticBtn(b); others++; }
        foreach (HidApi.ValueCap c in feaVals) { AddStaticCap(c); others++; }
        foreach (HidApi.ButtonCap b in feaBtns) { AddStaticBtn(b); others++; }
        if (others == 0)
            AddDataRow("", "", "", "（无）", null);

        // ---- 探针：每份子报文逐 Link 解码（看 5 指时哪几个槽位真的活着） ----
        _probeContactCount = inVals.FirstOrDefault(c => c.UsagePage == 0x0D && c.UsageMin == 0x54 && c.ReportCount <= 1);
        List<ushort> probeLinks = inVals.Where(c => c.LinkCollection != 0).Select(c => c.LinkCollection)
            .Concat(inBtns.Where(b => b.LinkCollection != 0).Select(b => b.LinkCollection))
            .Distinct().OrderBy(x => x).ToList();
        foreach (ushort link in probeLinks)
        {
            _probeLinks.Add((link,
                inVals.FirstOrDefault(c => c.LinkCollection == link && c.UsagePage == 0x0D && c.UsageMin == 0x51 && c.ReportCount <= 1),
                inBtns.FirstOrDefault(b => b.LinkCollection == link && b.UsagePage == 0x0D && !b.IsRange),
                inVals.FirstOrDefault(c => c.LinkCollection == link && c.UsagePage == 0x01 && c.UsageMin == 0x30 && c.ReportCount <= 1),
                inVals.FirstOrDefault(c => c.LinkCollection == link && c.UsagePage == 0x01 && c.UsageMin == 0x31 && c.ReportCount <= 1),
                inVals.FirstOrDefault(c => c.LinkCollection == link && c.UsagePage == 0x0D && c.UsageMin == 0x48 && c.ReportCount <= 1),
                inVals.FirstOrDefault(c => c.LinkCollection == link && c.UsagePage == 0x0D && c.UsageMin == 0x49 && c.ReportCount <= 1)));
        }

        // 报文日志放在表格外（内容很长，放进表格会撑开 Auto 列宽）
        AddNote("最近报文（每份子报文逐 Link 解码，4 帧取 1 条）", HeaderBrush, mono: false, bold: true);
        for (int i = 0; i < LogLineCount; i++)
        {
            var tb = new TextBlock
            {
                Text = "",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11.5,
                Foreground = MonoBrush,
                TextWrapping = TextWrapping.NoWrap,
                HorizontalAlignment = HorizontalAlignment.Left,
                TextAlignment = TextAlignment.Left,
                Margin = new Thickness(0, 1, 0, 1),
            };
            Rows.Children.Add(tb);
            _logLines.Add(tb);
        }

        // ---- 日志：完整能力表（供离线分析） ----
        Log.Info($"=== 能力表 {HidApi.TlcText(d.UsagePage, d.Usage)}：值帽 {inVals.Count}，按钮帽 {inBtns.Count}，Link 组 {linkGroups}，最大接触数 {(maxCap is null ? "未声明" : $"{maxCap.LogicalMin}..{maxCap.LogicalMax}")}，输入报文 {d.Caps.InputReportByteLength}B ===");
        foreach (HidApi.ValueCap v in inVals)
            Log.Info($"  值帽 {HidApi.UsageLabel(v.UsagePage, v.UsageMin)} L{v.LinkCollection} {HidApi.RangeText(v)}");
        foreach (HidApi.ButtonCap b in inBtns)
            Log.Info($"  按钮帽 {HidApi.UsageLabel(b.UsagePage, b.UsageMin)} L{b.LinkCollection} {HidApi.RangeText(b)}");
    }

    /// <summary>把一份子报文解码成一行：报文ID、接触数量、以及每个"活着"的 Link 的 CID/TS/X/Y/W/H。</summary>
    private string DescribeReport(HidApi.HidDevice d, byte[] report)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"ID={report[0]}");
        if (_probeContactCount is HidApi.ValueCap cc
            && HidApi.TryGetUsageValue(d.Preparsed, cc.UsagePage, cc.LinkCollection, cc.UsageMin, report, report.Length, out uint count, _allowAnyLink))
            sb.Append($" 接触数={count}");

        foreach (var p in _probeLinks)
        {
            HashSet<ushort> pressed = HidApi.PressedUsages(d.Preparsed, 0x0D, p.Link, report, report.Length);
            bool ts = p.Tip is not null && pressed.Contains(p.Tip.UsageMin);
            uint cid = 0, vx = 0, vy = 0;
            bool hasId = p.Id is not null && HidApi.TryGetUsageValue(d.Preparsed, 0x0D, p.Link, 0x51, report, report.Length, out cid, _allowAnyLink);
            bool hasX = p.X is not null && HidApi.TryGetUsageValue(d.Preparsed, 0x01, p.Link, 0x30, report, report.Length, out vx, _allowAnyLink);
            bool hasY = p.Y is not null && HidApi.TryGetUsageValue(d.Preparsed, 0x01, p.Link, 0x31, report, report.Length, out vy, _allowAnyLink);
            if (!ts && !hasX && !hasY)
                continue;   // 该槽位本帧没数据

            sb.Append($" ｜L{p.Link}");
            if (hasId) sb.Append($" CID={cid}");
            sb.Append($" TS={(ts ? 1 : 0)}");
            if (hasX) sb.Append($" X={vx}");
            if (hasY) sb.Append($" Y={vy}");
            if (p.W is not null && HidApi.TryGetUsageValue(d.Preparsed, 0x0D, p.Link, 0x48, report, report.Length, out uint vw, _allowAnyLink)) sb.Append($" W={vw}");
            if (p.H is not null && HidApi.TryGetUsageValue(d.Preparsed, 0x0D, p.Link, 0x49, report, report.Length, out uint vh, _allowAnyLink)) sb.Append($" H={vh}");
        }

        int hexLen = Math.Min(report.Length, 18);
        sb.Append($"  [{BitConverter.ToString(report, 0, hexLen).Replace("-", " ")}{(report.Length > hexLen ? " …" : "")}]");
        return sb.ToString();
    }

    /// <summary>
    /// 一份子报文的「触点状态签名」：逐 Link 的 CID + Tip 位，末尾附接触数。
    /// 只要这个串变了，就说明发生了接触状态跳变（按下/抬起/换指/接触数增减），
    /// 该帧必须完整记录 —— 抬手帧只有一帧，靠时间节流会把它漏掉。
    /// </summary>
    private string ReportSignature(HidApi.HidDevice d, byte[] report)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var p in _probeLinks)
        {
            HashSet<ushort> pressed = HidApi.PressedUsages(d.Preparsed, 0x0D, p.Link, report, report.Length);
            bool ts = p.Tip is not null && pressed.Contains(p.Tip.UsageMin);
            uint cid = 0;
            bool hasId = p.Id is not null && HidApi.TryGetUsageValue(d.Preparsed, 0x0D, p.Link, 0x51, report, report.Length, out cid, _allowAnyLink);
            sb.Append(hasId ? cid.ToString() : "-").Append(ts ? '1' : '0').Append(' ');
        }
        if (_probeContactCount is HidApi.ValueCap cc
            && HidApi.TryGetUsageValue(d.Preparsed, cc.UsagePage, cc.LinkCollection, cc.UsageMin, report, report.Length, out uint count, _allowAnyLink))
            sb.Append('|').Append(count);
        return sb.ToString();
    }

    private void AddStaticCap(HidApi.ValueCap c)
        => AddDataRow(HidApi.PageText(c.UsagePage), HidApi.UsageLabel(c.UsagePage, c.UsageMin), c.LinkCollection.ToString(), HidApi.RangeText(c), null);

    private void AddStaticBtn(HidApi.ButtonCap b)
        => AddDataRow(HidApi.PageText(b.UsagePage), HidApi.UsageLabel(b.UsagePage, b.UsageMin), b.LinkCollection.ToString(), HidApi.RangeText(b), null);

    /// <summary>开一段新表格：5 列全部 Auto，用 SharedSizeGroup 让所有段共列宽。</summary>
    private void StartGrid()
    {
        _grid = new Grid();
        string[] groups = { "hdPage", "hdUsage", "hdLink", "hdRange", "hdValue" };
        foreach (string g in groups)
            _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = g });
        _tableRow = 0;
        Rows.Children.Add(_grid);
    }

    /// <summary>五列一行；liveRow 非空时把“当前值”单元格挂上去，由实时刷新回填。</summary>
    private void AddDataRow(string pageText, string usageText, string linkText, string rangeText, LiveRow? liveRow)
    {
        int row = NewRow();
        AddCell(row, 0, pageText, PageBrush, bold: pageText.Length > 0);
        AddCell(row, 1, usageText, UsageBrush, bold: false);
        AddCell(row, 2, linkText, DimBrush, bold: false);
        AddCell(row, 3, rangeText, DimBrush, bold: false);

        _reportLines.Add($"{pageText}\t{usageText}\t{linkText}\t{rangeText}\t{(liveRow is null ? "" : "当前 = " + liveRow.Text)}");
        if (liveRow is not null)
            _reportIndex[liveRow] = _reportLines.Count - 1;

        var valueCell = new TextBlock
        {
            Text = liveRow is null ? "" : "当前 = —",
            Foreground = ValueBrush,
            FontWeight = FontWeights.Bold,
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalAlignment = HorizontalAlignment.Left,
            TextAlignment = TextAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(6, 1, 8, 1),
        };
        Grid.SetRow(valueCell, row);
        Grid.SetColumn(valueCell, 4);
        _grid.Children.Add(valueCell);

        if (liveRow is not null)
        {
            liveRow.ValueCell = valueCell;
            _rows.Add(liveRow);
        }
    }

    private void AddCells(string c0, string c1, string c2, string c3, string c4, bool isHeader)
    {
        int row = NewRow();
        string[] texts = { c0, c1, c2, c3, c4 };
        for (int i = 0; i < texts.Length; i++)
            AddCell(row, i, texts[i], isHeader ? HeaderBrush : UsageBrush, bold: isHeader);
    }

    private int NewRow()
    {
        _tableRow = _grid.RowDefinitions.Count;
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        return _tableRow;
    }

    private void AddCell(int row, int col, string text, Brush brush, bool bold)
    {
        var tb = new TextBlock
        {
            Text = text,
            Foreground = brush,
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalAlignment = HorizontalAlignment.Left,
            TextAlignment = TextAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 8, 1),
        };
        Grid.SetRow(tb, row);
        Grid.SetColumn(tb, col);
        _grid.Children.Add(tb);
    }

    /// <summary>段标题（表格外，不影响列宽），随后自动开一段新表格。</summary>
    private void AddSection(string title)
    {
        Rows.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.Bold,
            Foreground = HeaderBrush,
            FontSize = 12.5,
            HorizontalAlignment = HorizontalAlignment.Left,
            TextAlignment = TextAlignment.Left,
            Margin = new Thickness(0, 10, 0, 3),
        });
        _reportLines.Add("");
        _reportLines.Add($"===== {title} =====");
        StartGrid();
    }

    /// <summary>表格外的整行文字（图例 / 说明 / 描述符行 / 报文日志），不参与列宽计算。</summary>
    private void AddNote(string text, Brush brush, bool mono, bool bold = false)
    {
        var tb = new TextBlock
        {
            Text = text,
            Foreground = brush,
            TextWrapping = TextWrapping.NoWrap,
            FontSize = mono ? 11.5 : 12,
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
            HorizontalAlignment = HorizontalAlignment.Left,
            TextAlignment = TextAlignment.Left,
            Margin = new Thickness(0, 1, 0, 1),
        };
        if (mono)
            tb.FontFamily = new FontFamily("Consolas");
        if (bold)
            tb.Margin = new Thickness(0, 10, 0, 3);
        Rows.Children.Add(tb);
        _reportLines.Add(text);
    }

    /// <summary>用资源管理器打开报告/日志所在目录（logs\）。</summary>
    private void OnOpenLogFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            string dir = System.IO.Path.Combine(AppContext.BaseDirectory, "logs");
            System.IO.Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,     // 交给资源管理器打开
            });
            SetStatus("已打开：" + dir);
            Log.Info("打开报告文件夹：" + dir);
        }
        catch (Exception ex)
        {
            SetStatus("打开文件夹失败：" + ex.Message);
            Log.Error("打开报告文件夹失败", ex);
        }
    }

    /// <summary>把界面上的内容（设备信息 + 能力表 + 描述符 + 当前实时值）导出成 txt，方便发出去分析。</summary>
    private void OnExportReport(object sender, RoutedEventArgs e)
    {
        try
        {
            // 先把实时值刷新进报告行
            foreach (KeyValuePair<LiveRow, int> kv in _reportIndex)
            {
                LiveRow row = kv.Key;
                int idx = kv.Value;
                string[] parts = _reportLines[idx].Split('\t');
                if (parts.Length == 5)
                {
                    parts[4] = "当前 = " + row.Text;
                    _reportLines[idx] = string.Join("\t", parts);
                }
            }

            string dir = System.IO.Path.Combine(AppContext.BaseDirectory, "logs");
            System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, $"report-{DateTime.Now:yyyyMMdd_HHmmss}.txt");

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"TouchSuite.HidDump 报告  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"设备：{DeviceCombo.SelectedItem}");
            sb.AppendLine(ProductText.Text);
            sb.AppendLine(new string('-', 100));
            foreach (string line in _reportLines)
                sb.AppendLine(line.Replace("\t", "  |  "));

            System.IO.File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(false));
            SetStatus("报告已导出：" + path);
            Log.Info("报告已导出：" + path);
        }
        catch (Exception ex)
        {
            SetStatus("导出报告失败：" + ex.Message);
            Log.Error("导出报告失败", ex);
        }
    }

    /// <summary>记录某个 Link 上的 X/Y/宽/高/接触ID/方位角 cap，供左侧红圈可视化（逐 Link=逐指）。</summary>
    private void TrackVisualCap(HidApi.ValueCap c)
    {
        if (c.ReportCount > 1 || c.LinkCollection == 0 || c.IsRange)
            return;
        if (!_visLinks.TryGetValue(c.LinkCollection, out LinkCaps? lc))
            _visLinks[c.LinkCollection] = lc = new LinkCaps { Link = c.LinkCollection };

        if (c.UsagePage == 0x01 && c.UsageMin == 0x30) lc.X ??= c;
        else if (c.UsagePage == 0x01 && c.UsageMin == 0x31) lc.Y ??= c;
        else if (c.UsagePage == 0x0D && c.UsageMin == 0x48) lc.W ??= c;
        else if (c.UsagePage == 0x0D && c.UsageMin == 0x49) lc.H ??= c;
        else if (c.UsagePage == 0x0D && c.UsageMin == 0x51) lc.Id ??= c;
        else if (c.UsagePage == 0x0D && c.UsageMin == 0x3F) lc.Az ??= c;
    }

    /// <summary>记录某个 Link 的「笔尖接触」按钮帽 —— 用来判断该指的槽位当前是否真的按下。</summary>
    private void TrackVisualButton(HidApi.ButtonCap b)
    {
        if (b.LinkCollection == 0 || b.IsRange || b.UsagePage != 0x0D || b.UsageMin != 0x42)
            return;
        if (!_visLinks.TryGetValue(b.LinkCollection, out LinkCaps? lc))
            _visLinks[b.LinkCollection] = lc = new LinkCaps { Link = b.LinkCollection };
        lc.Tip ??= b;
    }

    /// <summary>原始报告描述符段：USB 设备走父集线器取字节流并逐项解析；虚拟设备给出说明。</summary>
    private void AddDescriptorSection(string hidPath)
    {
        AddSection("原始报告描述符（Report Descriptor）—— 0x95=Report Count，0x75=Report Size，0x85=Report ID");
        byte[]? desc = null;
        try
        {
            desc = UsbDescriptor.TryGetHidReportDescriptor(hidPath);
        }
        catch
        {
            // 集线器查询失败按取不到处理
        }

        if (desc is null)
        {
            // 非 USB 设备（VHF / 虚拟）：Windows 没有用户态接口 → 试本仓库驱动的 IOCTL
            byte[]? fromDriver = null;
            string driverReason = "";
            try
            {
                fromDriver = VhfDescriptor.TryGet(_declaredSlots, out driverReason);
            }
            catch
            {
                driverReason = "调用驱动 IOCTL 异常";
            }

            if (fromDriver is not null)
            {
                AddNote($"来源：TouchBridge 驱动 IOCTL_TB_VHID_GET_DESCRIPTOR（{fromDriver.Length} 字节；本设备非 USB，操作系统不提供描述符）", ValueBrush, mono: false);
                LogDescriptor("驱动 IOCTL", fromDriver);
                foreach (ReportDescriptor.Line line in ReportDescriptor.Parse(fromDriver))
                    AddNote(line.Text, line.Kind switch { 1 => ValueBrush, 2 => MonoIdBrush, _ => MonoBrush }, mono: true);
                return;
            }

            AddNote("取不到原始描述符：" + UsbDescriptor.LastReason, DimBrush, mono: false);
            AddNote("驱动 IOCTL 途径也没成功：" + driverReason, DimBrush, mono: false);

            // 降级信息：网络重定向之类只转发标准描述符的总线，至少能给出报告描述符长度等信息
            string? std = null;
            try
            {
                std = _selected is null ? null : UsbDescriptor.TryDescribeStandard(_selected.Path);
            }
            catch
            {
                // 忽略
            }
            if (std is not null)
                AddNote("降级信息（标准描述符读到了）：" + std, DimBrush, mono: false);

            // 最后的兜底：从 HidP 解析结果重建一份【语义等价】描述符（usage/量程/单位/RC/集合树一致，
            // 但不是原始字节的逐字节复刻）。远程重定向拿不到原始字节时，这份即是对设备描述的完整刻画。
            try
            {
                if (_selected is not null)
                {
                    List<HidApi.ValueCap> vals = HidApi.ValueCaps(_selected.Preparsed, HidApi.ReportTypeInput, _selected.Caps.NumberInputValueCaps);
                    List<HidApi.ButtonCap> btns = HidApi.ButtonCaps(_selected.Preparsed, HidApi.ReportTypeInput, _selected.Caps.NumberInputButtonCaps);
                    List<HidApi.LinkNode> nodes = HidApi.LinkNodes(_selected.Preparsed, _selected.Caps.NumberLinkCollectionNodes);
                    int rid = vals.Select(v => (int)v.ReportID).Concat(btns.Select(b => (int)b.ReportID)).FirstOrDefault(x => x != 0);
                    byte[] recon = ReportDescriptor.Reconstruct(vals, btns, nodes, _selected.UsagePage, _selected.Usage, rid);
                    AddNote($"▼ 重建描述符（语义等价，{recon.Length} 字节；原始 703 字节级别的字段定义全在此）：", ValueBrush, mono: false);
                    foreach (ReportDescriptor.Line line in ReportDescriptor.Parse(recon))
                        AddNote(line.Text, line.Kind switch { 1 => ValueBrush, 2 => MonoIdBrush, _ => MonoBrush }, mono: true);
                    AddNote("重建 hex：" + BitConverter.ToString(recon).Replace("-", " "), MonoBrush, mono: false);
                    Log.Info($"重建描述符 {recon.Length} 字节（原始字节不可得）：\n" + BitConverter.ToString(recon).Replace("-", " "));
                }
            }
            catch (Exception ex)
            {
                Log.Error("重建描述符失败", ex);
            }

            AddNote("上方表格“逻辑 / 物理 / RC”列就是 HidP 从描述符解出的对应字段值，等价可用。", DimBrush, mono: false);
            Log.Info($"描述符获取失败：USB 途径={UsbDescriptor.LastReason}；驱动 IOCTL={driverReason}");
            return;
        }

        LogDescriptor("USB 集线器", desc);
        foreach (ReportDescriptor.Line line in ReportDescriptor.Parse(desc))
            AddNote(line.Text, line.Kind switch { 1 => ValueBrush, 2 => MonoIdBrush, _ => MonoBrush }, mono: true);
    }

    /// <summary>把整份描述符（逐项 + 原始 hex）写进日志 —— 便于把日志直接发出去分析。</summary>
    private static void LogDescriptor(string source, byte[] desc)
    {
        Log.Info($"=== 报告描述符（来源：{source}，{desc.Length} 字节）逐项 ===");
        foreach (ReportDescriptor.Line line in ReportDescriptor.Parse(desc))
            Log.Info("  " + line.Text);
        Log.Info("=== 报告描述符原始 hex ===\n" + ToHexLines(desc));
    }

    private static string ToHexLines(byte[] d)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < d.Length; i += 16)
        {
            sb.Append($"  {i:X4}: ");
            for (int j = i; j < Math.Min(i + 16, d.Length); j++)
                sb.Append(d[j].ToString("X2")).Append(' ');
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    // ================= WM_INPUT → 实时值 =================

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_INPUT)
            return IntPtr.Zero;

        if (!RawInput.TryGetHidData(lParam, out IntPtr hDevice, out byte[][] reports))
            return IntPtr.Zero;

        if (_selected is HidApi.HidDevice sel && hDevice == sel.RawHandle)
        {
            _frames += reports.Length;
            var pressedCache = new Dictionary<(ushort, ushort), HashSet<ushort>>();
            foreach (byte[] report in reports)
            {
                if (_lastHexLen == 0)
                {
                    _lastHex = Hex(report);
                    _lastHexLen = report.Length;
                }
                ApplyReport(sel, report, pressedCache);

                _frameIndex++;
                // 文件日志：逐 Link 解码 + 完整原始 hex（节流 ~40 行/秒）。
                // 但「状态跳变」帧（任一槽 Tip 位翻转、接触数变化、CID 变化）一律不节流：
                // 抬手帧只有一帧，25ms 节流会把它整帧丢掉，日志里就只剩"连着的一笔"，
                // 无法判断两笔之间到底有没有发出"断开"报文。跳变帧必须逐帧留证。
                string sig = ReportSignature(sel, report);
                bool transition = _lastSubSig != null && _lastSubSig != sig;
                _lastSubSig = sig;

                if (transition || Compat.NowMs() - _lastSubLogTicks >= 25)
                {
                    _lastSubLogTicks = Compat.NowMs();
                    Log.Info($"子报文#{_frameIndex}{(transition ? " ★跳变" : "")} {DescribeReport(sel, report)} | hex={Hex(report)}");
                }
                // 界面上「最近报文」段：4 份取 1 条
                if ((_frameIndex & 3) == 0)
                {
                    _pendingLog.Add($"#{_frameIndex} {DescribeReport(sel, report)}");
                    if (_pendingLog.Count > 60)
                        _pendingLog.RemoveRange(0, _pendingLog.Count - 60);
                    _logDirty = true;
                }
            }
            _dirty = true;
        }
        else if (!_byHandle.ContainsKey(hDevice) && Compat.NowMs() - _lastRescanTicks > 800)
        {
            // 设备中途重枚举（句柄不在表里）→ 节流重扫
            _lastRescanTicks = Compat.NowMs();
            Dispatcher.BeginInvoke(Rescan);
        }
        return IntPtr.Zero;
    }

    private void ApplyReport(HidApi.HidDevice sel, byte[] report, Dictionary<(ushort, ushort), HashSet<ushort>> pressedCache)
    {
        int valueRows = 0, valueHits = 0;
        foreach (LiveRow row in _rows)
        {
            if (row.IsArray)
            {
                valueRows++;
                if (HidApi.TryGetUsageValueArray(sel.Preparsed, row.Cap!, report, report.Length, out string text))
                {
                    row.Text = text;
                    valueHits++;
                }
                else
                {
                    row.Text = "—";
                }
            }
            else if (row.IsButton)
            {
                (ushort, ushort) key = (row.Page, row.Link);
                if (!pressedCache.TryGetValue(key, out HashSet<ushort>? pressed))
                    pressed = pressedCache[key] = HidApi.PressedUsages(sel.Preparsed, row.Page, row.Link, report, report.Length);
                row.Text = row.Summary
                    ? (pressed.Count == 0 ? "—" : string.Join("、", pressed.Select(u => HidApi.UsageLabel(row.Btn!.UsagePage, u))))
                    : pressed.Contains(row.Usage) ? "按下" : "—";
            }
            else
            {
                valueRows++;
                if (HidApi.TryGetUsageValue(sel.Preparsed, row.Page, row.Link, row.Usage, report, report.Length, out uint raw, _allowAnyLink))
                {
                    row.Text = FormatRaw(row, raw);
                    valueHits++;
                }
                else
                {
                    row.Text = "—";
                }
            }
        }

        // 严格按 Link 取一个都取不到（多为单触点设备 caps 的 Link 传进去查不到）→ 才允许全集合搜索，重解一次
        if (valueRows > 0 && valueHits == 0 && !_allowAnyLink)
        {
            _allowAnyLink = true;
            ApplyReport(sel, report, pressedCache);
            return;
        }

        UpdateRawContact(report);

        // 表格快照日志（节流 250ms）：记录"程序当前算出来的值"，与原始 hex 对照即可判断是解析问题还是设备行为
        if (Compat.NowMs() - _lastRowLogTicks >= 250)
        {
            _lastRowLogTicks = Compat.NowMs();
            Log.Info("表格快照：" + string.Join(" | ",
                _rows.Where(r => r.Text.Length > 0 && r.Text != "—")
                     .Select(r => $"{(r.IsButton ? "Btn" : "Val")} {r.Page:X2}:{r.Usage:X2}/L{r.Link}={r.Text}"))
                + $"　_allowAnyLink={_allowAnyLink}");
        }
    }

    /// <summary>当前值单元格的文本：只放 HID 原始值（计数），不做任何换算/归一化。</summary>
    private static string FormatRaw(LiveRow row, uint raw) => raw.ToString();

    // ================= 左侧：整屏缩放框 + 接触红圈 =================

    private void UpdateRawContact(byte[] report)
    {
        if (_selected is null || _visLinks.Count == 0)
            return;

        // 逐 Link（= 逐触点槽位）取一份数据：支持多指；用「笔尖接触」判断该槽位本帧是否真的按下
        foreach (LinkCaps lc in _visLinks.Values)
        {
            if (lc.X is null || lc.Y is null)
                continue;

            bool active = true;
            if (lc.Tip is not null)
            {
                HashSet<ushort> pressed = HidApi.PressedUsages(_selected.Preparsed, 0x0D, lc.Link, report, report.Length);
                active = pressed.Contains(lc.Tip.UsageMin);
            }
            else if (lc.Id is not null
                     && HidApi.TryGetUsageValue(_selected.Preparsed, 0x0D, lc.Link, 0x51, report, report.Length, out uint cid0, _allowAnyLink)
                     && cid0 == 0xFF)
            {
                active = false;   // 部分设备用 0xFF 表示"该槽位无接触"
            }

            double? x = Norm(lc.X, report);
            double? y = Norm(lc.Y, report);
            if (x is not double xv || y is not double yv)
                continue;
            if (!active)
            {
                _rawContacts.Remove((int)lc.Link);
                continue;
            }

            double? wn = lc.W is null ? null : Norm(lc.W, report);
            double? hn = lc.H is null ? null : Norm(lc.H, report);

            double? az = null;
            if (lc.Az is not null
                && HidApi.TryGetUsageValue(_selected.Preparsed, 0x0D, lc.Link, 0x3F, report, report.Length, out uint azRaw, _allowAnyLink)
                && lc.Az.LogicalMax > lc.Az.LogicalMin)
                az = Compat.Clamp((azRaw - lc.Az.LogicalMin) / (double)(lc.Az.LogicalMax - lc.Az.LogicalMin), 0, 1) * 359.0;

            // 槽位号做 key（ContactID 有的设备一直是 0/1，碰撞了就看不出多指）
            _rawContacts[lc.Link] = new RawContact
            {
                Xn = xv, Yn = yv, Wn = wn, Hn = hn, AzDeg = az, Seen = Compat.NowMs(),
            };
        }
    }

    private double? Norm(HidApi.ValueCap c, byte[] report)
    {
        if (_selected is null)
            return null;
        if (!HidApi.TryGetUsageValue(_selected.Preparsed, c.UsagePage, c.LinkCollection, c.UsageMin, report, report.Length, out uint raw, _allowAnyLink))
            return null;
        if (c.LogicalMax <= c.LogicalMin)
            return null;
        return Compat.Clamp((raw - c.LogicalMin) / (double)(c.LogicalMax - c.LogicalMin), 0, 1);
    }

    /// <summary>
    /// 方位角（HID 语义：绕 Z 轴逆时针、0 = 竖直向上）→ 中文方向词。
    /// 便于一眼核对：az=0 上、45 左上、90 左、135 左下、180 下、225 右下、270 右、315 右上。
    /// </summary>
    private static string AzDirection(double az)
    {
        string[] names = { "上", "左上", "左", "左下", "下", "右下", "右", "右上" };
        double d = ((az % 360) + 360) % 360;
        return names[(int)Math.Round(d / 45.0) % 8];
    }

    /// <summary>移除一个接触的可视元素（包围盒 + 指针 + 角标）。</summary>
    private void RemoveContactVisual(int key)
    {
        if (!_rawVisuals.TryGetValue(key, out ContactVisual? vis))
            return;
        _rawVisuals.Remove(key);
        RawCanvas.Children.Remove(vis.Box);
        RawCanvas.Children.Remove(vis.Needle);
        RawCanvas.Children.Remove(vis.Label);
    }

    /// <summary>整屏在面板里的等比缩放矩形。设备上报的 X/Y 是"全屏"归一化值，必须落进这个框才对位。</summary>
    private Rect ScreenFrame()
    {
        double pw = RawCanvas.ActualWidth, ph = RawCanvas.ActualHeight;
        if (pw <= 0 || ph <= 0)
            return Rect.Empty;

        double sw = SystemParameters.VirtualScreenWidth, sh = SystemParameters.VirtualScreenHeight;
        if (sw <= 0 || sh <= 0)
            return new Rect(0, 0, pw, ph);

        const double pad = 14;
        double scale = Math.Min((pw - pad * 2) / sw, (ph - pad * 2) / sh);
        if (scale <= 0)
            return new Rect(0, 0, pw, ph);
        double w = sw * scale, h = sh * scale;
        return new Rect((pw - w) / 2, (ph - h) / 2, w, h);
    }

    private void RenderTick()
    {
        if (RawCanvas is null || TouchHost is null || StatusText is null || RealPosCheck is null)
            return;   // XAML 解析期控件尚未连上

        if (_dirty)
        {
            _dirty = false;
            foreach (LiveRow row in _rows)
                row.ValueCell.Text = "当前 = " + row.Text;
        }

        if (_logDirty && _logLines.Count > 0)
        {
            _logDirty = false;
            List<string> tail = _pendingLog.Count <= LogLineCount
                ? _pendingLog
                : _pendingLog.GetRange(_pendingLog.Count - LogLineCount, LogLineCount);
            for (int i = 0; i < _logLines.Count; i++)
                _logLines[i].Text = i < tail.Count ? tail[i] : "";
        }

        double sw = SystemParameters.VirtualScreenWidth, sh = SystemParameters.VirtualScreenHeight;
        double sl = SystemParameters.VirtualScreenLeft, st = SystemParameters.VirtualScreenTop;
        bool realPos = RealPos;

        // 过期接触清理
        long now = Compat.NowMs();
        List<int> stale = _rawContacts.Where(kv => now - kv.Value.Seen > RawContactTtlMs).Select(kv => kv.Key).ToList();
        foreach (int key in stale)
        {
            _rawContacts.Remove(key);
            RemoveContactVisual(key);
        }
        // 界面元素比数据多（例如切换到别的设备）时也清掉
        foreach (int key in _rawVisuals.Keys.Where(k => !_rawContacts.ContainsKey(k)).ToList())
            RemoveContactVisual(key);

        // 红圈落点：1:1 真实屏幕位置（面板屏幕原点 + 归一化×屏幕尺寸）；
        // 或面板里的整屏等比缩放框（baseX/baseY/unitW/unitH 为映射基准）
        double baseX, baseY, unitW = sw, unitH = sh;
        string modeText;
        if (realPos)
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            Point origin = TouchHost.PointToScreen(new Point(0, 0));   // 设备像素
            double hostX = origin.X / dpi.DpiScaleX;
            double hostY = origin.Y / dpi.DpiScaleY;
            baseX = sl - hostX;
            baseY = st - hostY;
            if (_screenFrame is not null)
                _screenFrame.Visibility = Visibility.Collapsed;
            modeText = $"红圈 1:1 真实位置（面板屏幕位置 {hostX:0} , {hostY:0}，面板外的接触不显示）";
        }
        else
        {
            Rect frame = ScreenFrame();
            if (frame.IsEmpty)
                return;
            baseX = frame.X;
            baseY = frame.Y;
            unitW = frame.Width;
            unitH = frame.Height;
            if (_screenFrame is null)
            {
                _screenFrame = new Rectangle
                {
                    Stroke = new SolidColorBrush(Color.FromRgb(0x4A, 0x52, 0x60)),
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 4, 3 },
                    IsHitTestVisible = false,
                };
                RawCanvas.Children.Insert(0, _screenFrame);
            }
            _screenFrame.Visibility = Visibility.Visible;
            _screenFrame.Width = frame.Width;
            _screenFrame.Height = frame.Height;
            Canvas.SetLeft(_screenFrame, frame.X);
            Canvas.SetTop(_screenFrame, frame.Y);
            modeText = "红圈 整屏等比缩放";
        }

        foreach (KeyValuePair<int, RawContact> kv in _rawContacts)
        {
            int key = kv.Key;
            RawContact rc = kv.Value;
            if (!_rawVisuals.TryGetValue(key, out ContactVisual? vis))
            {
                var box = new Ellipse
                {
                    Stroke = new SolidColorBrush(Color.FromRgb(0xE0, 0x4F, 0x44)),
                    StrokeThickness = 2,
                    Fill = new SolidColorBrush(Color.FromArgb(0x28, 0xE0, 0x4F, 0x44)),
                    IsHitTestVisible = false,
                };
                var needle = new Line
                {
                    Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0x9A, 0x3C)),
                    StrokeThickness = 2,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Triangle,
                    IsHitTestVisible = false,
                };
                var label = new TextBlock
                {
                    Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD1, 0x8A)),
                    FontSize = 11,
                    FontFamily = new FontFamily("Consolas"),
                    IsHitTestVisible = false,
                };
                RawCanvas.Children.Add(box);
                RawCanvas.Children.Add(needle);
                RawCanvas.Children.Add(label);
                vis = new ContactVisual { Box = box, Needle = needle, Label = label };
                _rawVisuals[key] = vis;
            }

            // 包围盒：W/H 是设备上报的"轴对齐包围盒"尺寸（无则给个 24 的示意圈）
            double w = Math.Max(8, (rc.Wn ?? 0.09) * unitW);
            double h = Math.Max(8, (rc.Hn ?? 0.09) * unitH);
            double cx = baseX + rc.Xn * unitW;
            double cy = baseY + rc.Yn * unitH;
            vis.Box.Width = w;
            vis.Box.Height = h;
            Canvas.SetLeft(vis.Box, cx - w / 2);
            Canvas.SetTop(vis.Box, cy - h / 2);

            // 方位角指针：HID Azimuth 是「绕 Z 轴逆时针、0 = 竖直向上」，
            // 屏幕坐标 y 向下 → 角度 a 的方向向量 = (-sin a, -cos a)（a=0 向上、a=90 向左）。
            double az = rc.AzDeg ?? 0;
            double rad = az * Math.PI / 180.0;
            double len = Math.Max(w, h) / 2.0 * 0.95;
            vis.Needle.X1 = cx;
            vis.Needle.Y1 = cy;
            vis.Needle.X2 = cx - Math.Sin(rad) * len;
            vis.Needle.Y2 = cy - Math.Cos(rad) * len;
            vis.Needle.Visibility = rc.AzDeg is null ? Visibility.Collapsed : Visibility.Visible;

            vis.Label.Text = rc.AzDeg is null
                ? $"#{key}"
                : $"#{key} {az:0}°{AzDirection(az)}（Win {(270 - az + 360) % 360:0}°）";
            Canvas.SetLeft(vis.Label, cx + w / 2 + 3);
            Canvas.SetTop(vis.Label, cy - h / 2 - 14);
        }

        if (_selected is not null)
        {
            string contacts = _contactCountRow is not null ? $"　接触数量(0x54) = {_contactCountRow.Text}" : "";
            StatusText.Text = $"帧 {_frames}　最新报文 {(_lastHexLen > 0 ? $"{_lastHexLen}B：{_lastHex}" : "—")}"
                            + contacts
                            + $"　屏幕 {(int)sw}×{(int)sh} DIP　{modeText}";
        }
    }

    // ================= 触摸 / 鼠标蓝点 =================

    private void OnTouchDown(object sender, TouchEventArgs e)
    {
        TouchHint.Visibility = Visibility.Collapsed;
        Ellipse ell = MakeDot();
        MoveDot(ell, e.GetTouchPoint(TouchCanvas).Position);
        TouchCanvas.Children.Add(ell);
        _touchDots[e.TouchDevice.Id] = ell;
    }

    private void OnTouchMove(object sender, TouchEventArgs e)
    {
        if (_touchDots.TryGetValue(e.TouchDevice.Id, out Ellipse? ell))
            MoveDot(ell, e.GetTouchPoint(TouchCanvas).Position);
    }

    private void OnTouchUp(object sender, TouchEventArgs e)
    {
        if (_touchDots.TryGetValue(e.TouchDevice.Id, out Ellipse? ell))
        {
            _touchDots.Remove(e.TouchDevice.Id);
            TouchCanvas.Children.Remove(ell);
        }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        TouchHint.Visibility = Visibility.Collapsed;
        TouchHost.CaptureMouse();
        Ellipse ell = MakeDot();
        MoveDot(ell, e.GetPosition(TouchCanvas));
        TouchCanvas.Children.Add(ell);
        _touchDots[MouseDotKey] = ell;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_touchDots.TryGetValue(MouseDotKey, out Ellipse? ell) && e.LeftButton == MouseButtonState.Pressed)
            MoveDot(ell, e.GetPosition(TouchCanvas));
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        TouchHost.ReleaseMouseCapture();
        if (_touchDots.TryGetValue(MouseDotKey, out Ellipse? ell))
        {
            _touchDots.Remove(MouseDotKey);
            TouchCanvas.Children.Remove(ell);
        }
    }

    private static Ellipse MakeDot() => new()
    {
        Width = 30,
        Height = 30,
        Fill = new SolidColorBrush(Color.FromArgb(0x55, 0x3D, 0x8B, 0xFF)),
        Stroke = new SolidColorBrush(Color.FromRgb(0x3D, 0x8B, 0xFF)),
        StrokeThickness = 1.5,
        IsHitTestVisible = false,
    };

    private static void MoveDot(Ellipse ell, Point p)
    {
        Canvas.SetLeft(ell, p.X - ell.Width / 2);
        Canvas.SetTop(ell, p.Y - ell.Height / 2);
    }

    // ================= 杂项 =================

    private static string ShortPath(string p)
    {
        if (p.StartsWith(@"\\?\HID#", StringComparison.OrdinalIgnoreCase))
            p = p.Substring(8);
        int lastHash = p.LastIndexOf('#');
        if (lastHash > 0 && lastHash + 1 < p.Length && p[lastHash + 1] == '{')
            p = p.Substring(0, lastHash);
        return p;
    }

    private static string Blank(string s) => string.IsNullOrWhiteSpace(s) ? "—" : s.Trim();

    private static string Hex(byte[] data)
    {
        var sb = new System.Text.StringBuilder(data.Length * 3);
        foreach (byte b in data)
            sb.Append(b.ToString("X2")).Append(' ');
        return sb.ToString().TrimEnd();
    }

    private void SetStatus(string s) => StatusText.Text = s;
}
