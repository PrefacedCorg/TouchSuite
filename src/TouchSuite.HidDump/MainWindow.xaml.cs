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
    private HidApi.HidDevice? _selected;
    private readonly Dictionary<IntPtr, HidApi.HidDevice> _byHandle = new();
    private bool _suppressCombo;
    private long _lastRescanTicks;

    private readonly List<LiveRow> _rows = new();
    private bool _dirty;
    private long _frames;
    private string _lastHex = "";
    private int _lastHexLen;

    // 可视化参考 cap（构建表格时找到的首个）
    private HidApi.ValueCap? _capX, _capY, _capW, _capH, _capId;

    private sealed class RawContact
    {
        public required double Xn;
        public required double Yn;
        public double? Wn;
        public double? Hn;
        public required long Seen;
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
    private int _pressedLinks;               // 当前按下的 Link 数
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

    private readonly Dictionary<int, RawContact> _rawContacts = new();   // key = ContactID（无则 Link）
    private readonly Dictionary<int, Ellipse> _rawEllipses = new();
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
        foreach (HidApi.HidDevice d in _devices)
        {
            string tag = d.IsTouch ? "" : "　（非触摸类）";
            DeviceCombo.Items.Add($"{HidApi.TlcText(d.UsagePage, d.Usage)}　{ShortPath(d.Path)}{tag}");
        }
        _suppressCombo = false;

        Log.Info($"枚举到 HID 顶层集合 {_devices.Count} 个（触摸类 {_devices.Count(x => x.IsTouch)}）：");
        for (int i = 0; i < _devices.Count; i++)
        {
            HidApi.HidDevice d = _devices[i];
            Log.Info($"  [{i + 1}] {HidApi.TlcText(d.UsagePage, d.Usage)} 值帽 {d.Caps.NumberInputValueCaps} 按钮帽 {d.Caps.NumberInputButtonCaps} 输入长度 {d.Caps.InputReportByteLength}B 触摸={d.IsTouch} {d.Path}");
        }

        // 默认选触摸类里"能力最强"的一块（值帽最多的那个，通常就是多点触摸数字化器），
        // 而不是列表里的第一块（那往往是单触点的辅助集合）。
        int pick = -1;
        for (int i = 0; i < _devices.Count; i++)
        {
            if (!_devices[i].IsTouch)
                continue;
            if (pick < 0 || _devices[i].Caps.NumberInputValueCaps > _devices[pick].Caps.NumberInputValueCaps)
                pick = i;
        }
        if (pick < 0 && _devices.Count > 0)
            pick = 0;
        if (pick >= 0)
            DeviceCombo.SelectedIndex = pick;   // 触发 OnDevicePicked
        else
        {
            _selected = null;
            Rows.Children.Clear();
            ProductText.Text = "";
            SetStatus("未发现任何 HID 设备。");
        }
    }

    private void OnRescan(object sender, RoutedEventArgs e) => Rescan();

    private void OnDevicePicked(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCombo)
            return;
        int i = DeviceCombo.SelectedIndex;
        if (i < 0 || i >= _devices.Count)
            return;

        _selected = _devices[i];
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
        _probeLinks.Clear();
        _logLines.Clear();
        _pendingLog.Clear();
        _probeContactCount = null;
        _allowAnyLink = false;
        _capX = _capY = _capW = _capH = _capId = null;

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
    }

    /// <summary>记录首个 X/Y/宽/高/接触ID 的 cap，供左侧红圈可视化。</summary>
    private void TrackVisualCap(HidApi.ValueCap c)
    {
        if (c.ReportCount > 1)
            return;
        if (_capX is null && c.UsagePage == 0x01 && c.UsageMin == 0x30) _capX = c;
        else if (_capY is null && c.UsagePage == 0x01 && c.UsageMin == 0x31) _capY = c;
        else if (_capW is null && c.UsagePage == 0x0D && c.UsageMin == 0x48) _capW = c;
        else if (_capH is null && c.UsagePage == 0x0D && c.UsageMin == 0x49) _capH = c;
        else if (_capId is null && c.UsagePage == 0x0D && c.UsageMin == 0x51) _capId = c;
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
            AddNote("取不到原始描述符：" + UsbDescriptor.LastReason, DimBrush, mono: false);
            AddNote("上方表格“逻辑 / 物理 / RC”列就是 HidP 从描述符解出的对应字段值，等价可用。", DimBrush, mono: false);
            return;
        }

        foreach (ReportDescriptor.Line line in ReportDescriptor.Parse(desc))
            AddNote(line.Text, line.Kind switch { 1 => ValueBrush, 2 => MonoIdBrush, _ => MonoBrush }, mono: true);
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
                // 文件日志：逐 Link 解码 + 完整原始 hex（节流 ~40 行/秒）
                if (Environment.TickCount64 - _lastSubLogTicks >= 25)
                {
                    _lastSubLogTicks = Environment.TickCount64;
                    Log.Info($"子报文#{_frameIndex} {DescribeReport(sel, report)} | hex={Hex(report)}");
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
        else if (!_byHandle.ContainsKey(hDevice) && Environment.TickCount64 - _lastRescanTicks > 800)
        {
            // 设备中途重枚举（句柄不在表里）→ 节流重扫
            _lastRescanTicks = Environment.TickCount64;
            Dispatcher.BeginInvoke(Rescan);
        }
        return IntPtr.Zero;
    }

    private void ApplyReport(HidApi.HidDevice sel, byte[] report, Dictionary<(ushort, ushort), HashSet<ushort>> pressedCache)
    {
        int pressedLinks = 0;
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
                // 0x0D:0x42 笔尖接触 每个 Link 一个 → 按下数即当前触点数
                if (!row.Summary && row.Text == "按下" && row.Usage == 0x42 && row.Page == 0x0D)
                    pressedLinks++;
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

        _pressedLinks = pressedLinks;
        UpdateRawContact(report);

        // 表格快照日志（节流 250ms）：记录"程序当前算出来的值"，与原始 hex 对照即可判断是解析问题还是设备行为
        if (Environment.TickCount64 - _lastRowLogTicks >= 250)
        {
            _lastRowLogTicks = Environment.TickCount64;
            Log.Info("表格快照：" + string.Join(" | ",
                _rows.Where(r => r.Text.Length > 0 && r.Text != "—")
                     .Select(r => $"{(r.IsButton ? "Btn" : "Val")} {r.Page:X2}:{r.Usage:X2}/L{r.Link}={r.Text}"))
                + $"　按下Link={pressedLinks}　_allowAnyLink={_allowAnyLink}");
        }
    }

    /// <summary>原始计数 + 附加解读（X/Y 百分比、压感归一、宽/高换算 mm）。</summary>
    private static string FormatRaw(LiveRow row, uint raw)
    {
        HidApi.ValueCap c = row.Cap!;
        string extra = (row.Page, row.Usage) switch
        {
            (0x01, 0x30) or (0x01, 0x31) when c.LogicalMax > c.LogicalMin
                => $"（{100.0 * Math.Clamp((raw - c.LogicalMin) / (double)(c.LogicalMax - c.LogicalMin), 0, 1):0.#}%）",
            (0x0D, 0x30) when c.LogicalMax > c.LogicalMin
                => $"（{Math.Clamp((raw - c.LogicalMin) / (double)(c.LogicalMax - c.LogicalMin), 0, 1):0.###}）",
            (0x0D, 0x48) or (0x0D, 0x49) when HidApi.MmPerCount(c) is double per
                => $"（{raw * per:0.##} mm）",
            _ => "",
        };
        return $"{raw}{extra}";
    }

    // ================= 左侧：整屏缩放框 + 接触红圈 =================

    private void UpdateRawContact(byte[] report)
    {
        if (_capX is null || _capY is null || _selected is null)
            return;

        double? x = Norm(_capX, report);
        double? y = Norm(_capY, report);
        if (x is not double xv || y is not double yv)
            return;

        double? wn = _capW is null ? null : Norm(_capW, report);
        double? hn = _capH is null ? null : Norm(_capH, report);
        int key = _capId is HidApi.ValueCap idc
                  && HidApi.TryGetUsageValue(_selected.Preparsed, idc.UsagePage, idc.LinkCollection, idc.UsageMin, report, report.Length, out uint idv)
            ? (int)idv
            : _capX.LinkCollection;
        _rawContacts[key] = new RawContact { Xn = xv, Yn = yv, Wn = wn, Hn = hn, Seen = Environment.TickCount64 };
    }

    private double? Norm(HidApi.ValueCap c, byte[] report)
    {
        if (_selected is null)
            return null;
        if (!HidApi.TryGetUsageValue(_selected.Preparsed, c.UsagePage, c.LinkCollection, c.UsageMin, report, report.Length, out uint raw, _allowAnyLink))
            return null;
        if (c.LogicalMax <= c.LogicalMin)
            return null;
        return Math.Clamp((raw - c.LogicalMin) / (double)(c.LogicalMax - c.LogicalMin), 0, 1);
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
        long now = Environment.TickCount64;
        List<int> stale = _rawContacts.Where(kv => now - kv.Value.Seen > RawContactTtlMs).Select(kv => kv.Key).ToList();
        foreach (int key in stale)
        {
            _rawContacts.Remove(key);
            if (_rawEllipses.Remove(key, out Ellipse? gone))
                RawCanvas.Children.Remove(gone);
        }

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

        foreach ((int key, RawContact rc) in _rawContacts)
        {
            if (!_rawEllipses.TryGetValue(key, out Ellipse? ell))
            {
                ell = new Ellipse
                {
                    Stroke = new SolidColorBrush(Color.FromRgb(0xE0, 0x4F, 0x44)),
                    StrokeThickness = 2,
                    Fill = new SolidColorBrush(Color.FromArgb(0x28, 0xE0, 0x4F, 0x44)),
                    IsHitTestVisible = false,
                };
                RawCanvas.Children.Add(ell);
                _rawEllipses[key] = ell;
            }
            double w = rc.Wn is double wv ? Math.Max(8, wv * unitW) : 24;
            double h = rc.Hn is double hv ? Math.Max(8, hv * unitH) : 24;
            ell.Width = w;
            ell.Height = h;
            Canvas.SetLeft(ell, baseX + rc.Xn * unitW - w / 2);
            Canvas.SetTop(ell, baseY + rc.Yn * unitH - h / 2);
        }

        if (_selected is not null)
        {
            string contacts = _contactCountRow is not null ? $"　接触数量 = {_contactCountRow.Text}" : "";
            // 设备自报的接触数 > 描述符声明的槽位数 → 驱动只塞得下这么多槽位，多出来的触点会被丢弃/轮换
            string over = "";
            if (_contactCountRow is not null && int.TryParse(_contactCountRow.Text, out int liveCount)
                && _declaredSlots > 0 && liveCount > _declaredSlots)
                over = $"　⚠ 自报接触数 {liveCount} > 声明槽位 {_declaredSlots}：驱动每条报文只塞 {_declaredSlots} 个，其余触点被丢弃或轮换到别的槽位";
            StatusText.Text = $"帧 {_frames}　最新报文 {(_lastHexLen > 0 ? $"{_lastHexLen}B：{_lastHex}" : "—")}"
                            + $"　触点 {_pressedLinks} 个{contacts}{over}"
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
        if (_touchDots.Remove(e.TouchDevice.Id, out Ellipse? ell))
            TouchCanvas.Children.Remove(ell);
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
        if (_touchDots.Remove(MouseDotKey, out Ellipse? ell))
            TouchCanvas.Children.Remove(ell);
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
            p = p[8..];
        int lastHash = p.LastIndexOf('#');
        if (lastHash > 0 && lastHash + 1 < p.Length && p[lastHash + 1] == '{')
            p = p[..lastHash];
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
