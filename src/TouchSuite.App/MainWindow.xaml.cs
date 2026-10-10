using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using TouchSuite.App.Eraser;

namespace TouchSuite.App;

public partial class MainWindow : Window
{
    /// <summary>校准流程里的步骤（具体顺序由所选方式决定）。</summary>
    private enum FlowStep { Route, ScreenPick, Screen, PalmCm, PalmTrace, Press, Finger, Result }

    /// <summary>屏幕标定的三种方式。</summary>
    private enum ScreenMode { Edid, Draw, Diag }

    private readonly List<FlowStep> _flow = new();
    private int _flowIndex;
    private ScreenMode _screenMode = ScreenMode.Edid;
    private bool _edidUnusable;   // 方式 1(EDID) 横竖都不准后置位：退回选择页时不再显示方式 1
    private FlowStep Current => _flow.Count > 0 ? _flow[Math.Clamp(_flowIndex, 0, _flow.Count - 1)] : FlowStep.Route;

    private static string TitleOf(FlowStep s) => s switch
    {
        FlowStep.Route => "选择路线",
        FlowStep.ScreenPick => "选择屏幕标定方式",
        FlowStep.Screen => "屏幕标定",
        FlowStep.PalmCm => "手掌尺寸（厘米）",
        FlowStep.PalmTrace => "描摹手掌",
        FlowStep.Press => "手掌按一下",
        FlowStep.Finger => "手指按一下",
        FlowStep.Result => "结果",
        _ => "",
    };

    private static Visibility Vis(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    private readonly TouchInput _touch = new();
    private CalibrationResult _result = new();

    private ScreenCalibration? _calib;
    private EdidSize _edid = new(0, 0);
    private string _sizeSource = "EDID";   // EDID / 对角线
    private bool _edidFailed;              // EDID 读取失败（退回 96dpi 假设）

    // 手画 10cm 标定
    private bool _drawing;
    private Point _drawStart, _drawEnd;
    private bool _hasDrawn;
    private bool _drawnIsHorizontal;
    private double _drawnLenDiu;
    private double _drawnMmPerPx;          // 画线推得的 mm/物理像素（未含微调）
    private Line? _userDrawLine, _progLine;

    // 手掌擦预览（整套在 Eraser/EraserPreviewPage + EraserEngine 里）
    private readonly List<EdidSize> _edidCandidates = new();
    private bool _suppressEdidCombo;

    private double _dpiScaleX = 1, _dpiScaleY = 1;
    private double _mmPerDiuX, _mmPerDiuY;

    // 描摹包围盒（DIU，相对 TraceCanvas）
    private double _bx0, _by0, _bx1, _by1;
    private bool _hasBox;

    private double? _palmPeakAreaPx2, _palmPeakPressure;
    private double? _fingerPeakAreaPx2, _fingerPeakPressure;

    // 多次按压取中值
    private readonly List<double> _palmAreas = new();
    private readonly List<double> _palmPressures = new();
    private readonly List<double> _fingerAreas = new();
    private readonly List<double> _fingerPressures = new();

    private long _lastSampleLogTicks;   // 样本日志节流：≥250ms 才记一行，避免刷屏
    private long _lastSampleTicks;      // 最近一次收到任何样本的时刻（抬手后用它把实时读数归零）
    private DispatcherTimer? _uiTick;   // 抬手检测：静默超过新鲜窗口就把「当前接触/压感」清成 —
    private long _lastInfoBarTicks;     // 信息栏刷新节流（实时压感 60Hz，节流后才看得清）
    private double? _liveAreaPx2;       // 手掌/手指实时接触面积（物理像素²）
    private double? _livePressure;      // 手掌/手指实时压感（来自 WPF Stylus）

    // 原始HID（RawInput）：设备上报的接触尺寸→面积 + 压感；映射表只读展示
    private IntPtr _hwnd;
    private List<HidReader.HidDeviceInfo> _hidDevices = new();
    private long _lastHidLogTicks;
    private long _lastHidTicks;         // 最近一次有效原始HID 样本时刻（HID 新鲜时忽略 WPF 面积，避免打架）

    // 来源下拉
    private bool _suppressSourceUi;     // 同步多个下拉时抑制 SelectionChanged 回环
    private string _palmActiveSource = "";     // 手掌当前实际生效的原始来源
    private string _fingerActiveSource = "";   // 手指当前实际生效的原始来源

    public MainWindow()
    {
        InitializeComponent();
        FitToWorkArea();
        Loaded += OnLoaded;
        Closed += (_, _) => _touch.Dispose();

        // 「保存结果到文件」按钮在预览页里（EraserPreviewPage.xaml），宿主只负责落盘
        EraserPage.SaveRequested += OnSaveRequested;

        // 预览页的来源下拉改动 → 同步本窗口第 5/6 步的两个下拉
        EraserPage.SourceSelectionChanged += SyncSourceUi;

        // 默认两项都判"准"（对应界面 RadioButton 的默认选中），首次标尺即按 EDID 原样
        _result.WidthRulerOk = true;
        _result.HeightRulerOk = true;
        TraceCanvas.DefaultDrawingAttributes = new DrawingAttributes
        {
            Width = 3,
            Height = 3,
            Color = Color.FromRgb(0x22, 0x66, 0xCC),
            FitToCurve = true,
        };
    }

    /// <summary>把窗口限制在主屏工作区内：高 DPI 下窗口按物理像素会超过屏幕，
    /// CenterScreen 会把标题栏顶到屏幕上方导致拖不动。这里改为手动定位并夹取。</summary>
    private void FitToWorkArea()
    {
        Rect wa = SystemParameters.WorkArea;   // 逻辑单位(DIP)
        const double margin = 24;
        double maxH = Math.Max(320, wa.Height - margin);
        double maxW = Math.Max(400, wa.Width - margin);

        if (double.IsNaN(Height) || Height > maxH) Height = maxH;
        if (double.IsNaN(Width) || Width > maxW) Width = maxW;

        WindowStartupLocation = WindowStartupLocation.Manual;
        double left = wa.Left + Math.Max(0, (wa.Width - Width) / 2);
        double top = wa.Top + Math.Max(0, (wa.Height - Height) / 2);
        Left = Math.Clamp(left, wa.Left, Math.Max(wa.Left, wa.Right - Width));
        Top = Math.Clamp(top, wa.Top, Math.Max(wa.Top, wa.Bottom - Height));
    }

    // ================= 原始HID（RawInput）=================

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        if (PresentationSource.FromVisual(this) is HwndSource src)
            src.AddHook(WndProc);

        _hidDevices = HidReader.Scan();
        bool ok = HidReader.Register(_hwnd);
        Log.Info($"原始HID 触摸注册 = {ok}；HID 设备 {_hidDevices.Count} 个（触摸类 {_hidDevices.Count(d => d.IsTouchScreen)} 个）");
        foreach (HidReader.HidDeviceInfo d in _hidDevices.Where(d => d.IsTouchScreen))
            Log.Info($"HID(raw): {d.DeviceName} | W={d.HasWidth} H={d.HasHeight} P={d.HasPressure} | 宽:{d.WidthDetail} 高:{d.HeightDetail} 压感:{d.PressureDetail} | {d.XYDetail}");

        // 多块触摸屏：把触摸类设备喂给预览页下拉（第 1 项固定"自动"）
        PushHidDevices();
    }

    /// <summary>把扫到的触摸类 HID **按逻辑屏归并后**喂给三处「HID 触摸屏」下拉（索引 0 固定"自动"）。</summary>
    private void PushHidDevices()
    {
        _hidDeviceKeys.Clear();

        // 同一物理屏的多个 HID 集合（希沃：MI_00&Col04 + MI_02&Col02）合成一个逻辑屏，下拉只列逻辑屏
        var groups = new List<string>();
        var agg = new Dictionary<string, (bool W, bool H, bool P, int N)>(StringComparer.OrdinalIgnoreCase);
        foreach (HidReader.HidDeviceInfo d in _hidDevices.Where(d => d.IsTouchScreen))
        {
            if (!agg.TryGetValue(d.GroupKey, out (bool W, bool H, bool P, int N) a))
            {
                a = (false, false, false, 0);
                groups.Add(d.GroupKey);
            }
            agg[d.GroupKey] = (a.W || d.HasWidth, a.H || d.HasHeight, a.P || d.HasPressure, a.N + 1);
        }
        foreach (string k in groups)
            _hidDeviceKeys.Add(k);

        _suppressDeviceUi = true;
        foreach (ComboBox? combo in new[] { PalmHidDeviceCombo, FingerHidDeviceCombo })
        {
            if (combo is null)
                continue;
            combo.Items.Clear();
            combo.Items.Add("自动（第一块出数的触摸屏）");
            foreach (string k in groups)
                combo.Items.Add(HidGroupLabel(k, agg[k]));
            combo.SelectedIndex = 0;
        }
        _suppressDeviceUi = false;

        if (EraserPage is not null)
            EraserPage.SetHidDevices(groups.Select(k => (Key: k, Label: HidGroupLabel(k, agg[k]))).ToList());

        SyncHidDeviceUi();
    }

    /// <summary>逻辑屏下拉标签：VID&amp;PID + 能力标记（含尺寸/含压感）+ 是否由多个集合合并而来。</summary>
    private static string HidGroupLabel(string key, (bool W, bool H, bool P, int N) a)
    {
        var caps = new List<string>();
        if (a.W || a.H) caps.Add("含尺寸");
        if (a.P) caps.Add("含压感");
        caps.Add(caps.Count > 0 ? "" : "仅坐标");
        caps.RemoveAll(s => s.Length == 0);
        string merged = a.N > 1 ? $"，已合并{a.N}集合" : "";
        return $"{key}（{string.Join("·", caps)}{merged}）";
    }

    private readonly List<string> _hidDeviceKeys = new();
    private bool _suppressDeviceUi;

    private void OnPalmHidDeviceChanged(object sender, SelectionChangedEventArgs e)
        => ApplyHidDeviceChange(((ComboBox)sender).SelectedIndex);

    private void OnFingerHidDeviceChanged(object sender, SelectionChangedEventArgs e)
        => ApplyHidDeviceChange(((ComboBox)sender).SelectedIndex);

    /// <summary>三处「HID 触摸屏」下拉任一改动：落到引擎，再把三处同步一致。</summary>
    private void ApplyHidDeviceChange(int index)
    {
        if (_suppressDeviceUi || EraserPage is null)
            return;
        string key = index >= 1 && index - 1 < _hidDeviceKeys.Count ? _hidDeviceKeys[index - 1] : "";
        EraserPage.Engine.SetRawHidDevice(key);
        SyncHidDeviceUi();
    }

    /// <summary>把引擎当前指定的 HID 触摸屏同步到三处下拉。</summary>
    private void SyncHidDeviceUi()
    {
        if (EraserPage is null)
            return;
        string key = EraserPage.Engine.RawHidDeviceKey;
        int idx = 0;
        for (int i = 0; i < _hidDeviceKeys.Count; i++)
            if (string.Equals(_hidDeviceKeys[i], key, StringComparison.OrdinalIgnoreCase))
            {
                idx = i + 1;
                break;
            }

        _suppressDeviceUi = true;
        if (PalmHidDeviceCombo is { Items.Count: > 0 } pc)
            pc.SelectedIndex = Math.Min(idx, pc.Items.Count - 1);
        if (FingerHidDeviceCombo is { Items.Count: > 0 } fc)
            fc.SelectedIndex = Math.Min(idx, fc.Items.Count - 1);
        _suppressDeviceUi = false;

        EraserPage.SyncHidDevice(key);
    }

    private const int WM_INPUT = 0x00FF;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_INPUT)
        {
            HidReader.WmInputResult r = HidReader.TryHandleWmInput(lParam, out HidReader.RawTouchSample? sample);

            // 句柄不在能力表（设备中途重枚举）→ 节流重扫一次再解
            if (r == HidReader.WmInputResult.UnknownDevice && HidReader.TryBeginAutoRescan())
            {
                _hidDevices = HidReader.Scan();
                PushHidDevices();
                HidReader.TryHandleWmInput(lParam, out sample);
            }

            if (sample is not null)
                HandleHidSample(sample);
        }
        return IntPtr.Zero;
    }

    /// <summary>原始HID 样本：只取「接触尺寸→面积」与「压感」，封装成 TouchSample 走同一条处理链。</summary>
    private void HandleHidSample(HidReader.RawTouchSample s)
    {
        // 多块触摸屏：与预览页共用同一把设备闸门（指定那台 / 自动锁第一台出数的），
        // 保证「标定采样」和「预览」用的是同一块屏，别让第二块屏的样本污染 K。
        // 闸门按「逻辑屏键」判：同一物理屏的多个 HID 集合（尺寸/压感分开报的）算同一块屏。
        string groupKey = HidReader.GroupOfDevice(s.DeviceName);
        if (EraserPage is not null && !EraserPage.Engine.AcceptHidDevice(groupKey))
            return;

        // 尺寸只需要计数 + 该轴逻辑量程（空槽位/占位帧两样都没有才丢帧）。不需要 mm。
        bool hasLog = s.WidthLogical > 0 && s.HeightLogical > 0 && s.WidthLogMax > 0 && s.HeightLogMax > 0;
        if (!hasLog)
            return;

        long now = Environment.TickCount64;
        _lastHidTicks = now;
        if (now - _lastHidLogTicks >= 100)
        {
            _lastHidLogTicks = now;
            Log.Info($"原始HID: 设备={ShortName(s.DeviceName)} 生效槽位L={s.LinkCollection} W={s.WidthLogical}/{s.WidthLogMax} H={s.HeightLogical}/{s.HeightLogMax}"
                 + $" X={s.XLogical}/{s.XLogMax}({Precision.Fmt(s.XNorm, 4)}) Y={s.YLogical}/{s.YLogMax}({Precision.Fmt(s.YNorm, 4)})"
                 + $" 压感={Precision.Fmt(s.Pressure01, 3)} raw=[{s.Hex}]");
            Log.Info($"原始HID 有数据槽位: {s.LinksDetail}");
            Log.Info($"通道对照: 原始HID 触点数={s.Contacts?.Count ?? 0}"
                 + $" W={s.WidthLogical}/{s.WidthLogMax} H={s.HeightLogical}/{s.HeightLogMax}"
                 + $" | WPF {(_touch.LastWpfBounds is Rect wb ? $"{Precision.Fmt(wb.Width, 3)}×{Precision.Fmt(wb.Height, 3)} DIP" : "无")}"
                 + $" | Stylus {(_touch.LastStylusPressure?.ToString("0.###") ?? "无")}");
        }

        IReadOnlyList<ContactRect>? rawContacts = s.Contacts is { Count: > 0 } rl
            ? rl.Select(c => new ContactRect(c.ContactId, null, c.Wmm, c.Hmm, c.XNorm, c.YNorm, c.Pressure01,
                c.WLogical, c.HLogical)).ToList()
            : null;

        OnSample(new TouchSample("RawHID", s.WidthMm, s.HeightMm, s.Pressure01,
            Detail: $"L={s.LinkCollection} W={s.WidthLogical}/{s.WidthLogMax} H={s.HeightLogical}/{s.HeightLogMax} raw=[{s.Hex}]",
            WidthLogical: s.WidthLogical, HeightLogical: s.HeightLogical,
            XNorm: s.XNorm, YNorm: s.YNorm,
            XLogMax: s.XLogMax, YLogMax: s.YLogMax,
            WidthLogMax: s.WidthLogMax, HeightLogMax: s.HeightLogMax,
            Contacts: rawContacts,
            XPhysMm: s.XPhysMm, YPhysMm: s.YPhysMm,
            DeviceKey: groupKey));
    }

    /// <summary>原始HID 设备换算表（映射表）文字，只读展示。同一物理屏的多个集合合到一行。</summary>
    private string HidTableText()
    {
        List<HidReader.HidDeviceInfo> touch = _hidDevices.Where(d => d.IsTouchScreen).ToList();
        if (touch.Count == 0)
            return "原始HID 映射表：（未发现触摸类 HID 设备）";
        return "原始HID 映射表：" + string.Join("",
            touch.GroupBy(d => d.GroupKey).Select(g =>
            {
                string members = string.Join("、", g.Select(d => ShortName(d.DeviceName)));
                HidReader.HidDeviceInfo p = g.FirstOrDefault(d => d.HasWidth || d.HasHeight) ?? g.First();
                string press = string.Join(" | ", g.Where(x => x.PressureDetail.Length > 0).Select(x => x.PressureDetail));
                return $"\n  【{g.Key}】{members}：宽 [{p.WidthDetail}]；高 [{p.HeightDetail}]；压感 [{press}]；X/Y [{p.XYDetail}]";
            }));
    }

    private static string ShortName(string path)
    {
        string[] parts = path.Split('#');
        return parts.Length >= 3 ? $"{parts[1]} #{parts[2]}" : path;
    }

    // ================= 来源下拉 =================

    private void OnPalmSourceModeChanged(object sender, SelectionChangedEventArgs e)
        => ApplySourceUi(((ComboBox)sender).SelectedIndex);

    private void OnFingerSourceModeChanged(object sender, SelectionChangedEventArgs e)
        => ApplySourceUi(((ComboBox)sender).SelectedIndex);

    /// <summary>把来源下拉的改动落实到引擎，再把所有下拉同步成引擎当前状态。</summary>
    private void ApplySourceUi(int modeIndex)
    {
        if (_suppressSourceUi || !IsLoaded || EraserPage is null)
            return;
        EraserPage.Engine.SetMode((SourceMode)Math.Clamp(modeIndex, 0, 2));
        SyncSourceUi();
    }

    /// <summary>按引擎当前来源状态刷新下拉与 HID 面板可见性。</summary>
    private void SyncSourceUi()
    {
        if (PalmSourceCombo is null || EraserPage is null)
            return;

        SourceMode mode = EraserPage.Engine.Mode;
        // 自适应可能锁到 HID、手动原始HID 必用 HID —— 两种情况都把 HID 面板亮出来
        Visibility hidVis = mode != SourceMode.SoftwareWpf ? Visibility.Visible : Visibility.Collapsed;

        _suppressSourceUi = true;
        if (FingerSourceCombo is not null) FingerSourceCombo.SelectedIndex = (int)mode;
        PalmSourceCombo.SelectedIndex = (int)mode;
        if (PalmHidMmPanel is not null) PalmHidMmPanel.Visibility = hidVis;
        if (FingerHidMmPanel is not null) FingerHidMmPanel.Visibility = hidVis;
        _suppressSourceUi = false;

        EraserPage.SyncSourceSelection(mode);
        SyncHidDeviceUi();
        UpdateSourceInfoText();
    }

    /// <summary>第 5/6 步「当前生效」：显示当前实际生效的来源。</summary>
    private void UpdateSourceInfoText()
    {
        if (PalmSourceInfoText is not null)
            PalmSourceInfoText.Text = "当前生效：" + ActiveLabel(_palmActiveSource);
        if (FingerSourceInfoText is not null)
            FingerSourceInfoText.Text = "当前生效：" + ActiveLabel(_fingerActiveSource);
    }

    private static string ActiveLabel(string src) => src switch
    {
        "RawHID" => SourceNames.Of(ContactSource.RawHid),
        "WPF" or "STYLUS" => SourceNames.Of(ContactSource.Wpf),
        _ => "—",
    };

    // ================= 生命周期 =================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateDpi();

        var (rx, ry) = EdidReader.PrimaryPixels();
        _result.ResX = rx;
        _result.ResY = ry;

        _edidCandidates.Clear();
        _edidCandidates.AddRange(EdidReader.ReadAll());

        EdidSize? best = EdidReader.PickBest(_edidCandidates, rx, ry);
        if (best is null)
        {
            // 读不到 EDID：按 96dpi 假设一个（仅供流程走通，标尺不可信）
            best = new EdidSize((int)Math.Round(rx / 96.0 * 25.4), (int)Math.Round(ry / 96.0 * 25.4));
            _edidCandidates.Add(best);
            _edidFailed = true;
            SetStatus("EDID 读取失败：已按 96dpi 假设尺寸（标尺不可信，请在第 3 步填对角线）。");
        }
        _edid = best;

        if (_edidCandidates.Count > 1)
            Log.Info($"EDID 候选 {_edidCandidates.Count} 块: {string.Join(" / ", _edidCandidates.Select(c => $"{c.WidthMm}×{c.HeightMm}mm"))}");
        Log.Info($"EDID 选用 {_edid.WidthMm}×{_edid.HeightMm}mm；主屏分辨率 {rx}×{ry}；读取失败={_edidFailed}");

        // 候选下拉（>1 块时显示，便于手动纠正挑错的那块）
        _suppressEdidCombo = true;
        EdidCombo.Items.Clear();
        foreach (EdidSize c in _edidCandidates)
            EdidCombo.Items.Add($"{c.WidthMm} × {c.HeightMm} mm");
        EdidCombo.SelectedIndex = Math.Max(0, _edidCandidates.IndexOf(_edid));
        _suppressEdidCombo = false;
        EdidPickPanel.Visibility = _edidCandidates.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        RebuildCalibration();
        RecomputePalmSize();   // 默认 120×180px 也先算一遍，信息栏/预览页立刻有值

        _touch.Sample += OnSample;
        _touch.Attach(this);
        _touch.AttachFallback(PalmHost);
        _touch.AttachFallback(FingerHost);
        _touch.AttachFallback(EraserPage.HostElement);

        // 面积擦预览：状态回显到底部状态栏
        EraserPage.Status += SetStatus;

        _result.DeviceName = "WPF + 原始HID";
        Log.Info("接触来源：原始HID（RawInput WM_INPUT：W×H 面积 + TipPressure 压感）+ WPF TouchPoint.Bounds/Stylus 兜底");
        SetStatus("触摸来源：可在第 5/6 步下拉里选「自适应 / 原始HID / 软件WPF」；HID 还可选尺寸取法（逻辑量程→屏幕分辨率 / 物理量程换算）");

        SyncSourceUi();

        // 抬手检测：静默超过新鲜窗口 → 把「当前接触/压感」清成 —（否则会一直停在最后一个值）
        _uiTick = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            long now = Environment.TickCount64;
            if (now - _lastSampleTicks <= EraserEngine.SourceFreshMs)
                return;
            if (_liveAreaPx2 is null && _livePressure is null)
                return;

            _liveAreaPx2 = null;
            _livePressure = null;
            if (Current == FlowStep.Press)
                PalmLiveText.Text = LiveText();
            else if (Current == FlowStep.Finger)
                FingerLiveText.Text = LiveText();
            RefreshInfoBar();
        }, Dispatcher);
        _uiTick.Start();

        InitFlow();
        if (!TryLoadSaved())
            ShowFlow();
    }

    /// <summary>启动时若已保存过标定，弹窗询问是否载入；载入成功则跳到结果页并返回 true。</summary>
    private bool TryLoadSaved()
    {
        string path = CalibrationResult.DefaultPath();
        if (!File.Exists(path))
            return false;

        CalibrationResult? saved = CalibrationResult.Load(path);
        string stamp = saved?.Timestamp is { Length: > 0 } ts ? ts : "（无时间戳）";
        MessageBoxResult answer = MessageBox.Show(
            $"检测到上次保存的标定：\n{path}\n保存时间：{stamp}\n\n是否载入？\n\n【是】载入并直接跳到结果页（可查看面积擦预览）\n【否】从第 1 步重新标定",
            "TouchSuite 校准向导 · 载入标定", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            Log.Info("启动：用户选择重新标定（不载入)");
            return false;
        }

        if (saved is null)
        {
            MessageBox.Show("载入失败：文件损坏或格式不正确。", "TouchSuite 校准向导",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            Log.Warn($"启动载入失败（解析错误）: {path}");
            return false;
        }

        ApplyLoaded(saved);
        Log.Info($"启动载入已保存标定: {path}（{stamp}）");
        _flowIndex = Math.Max(0, _flow.IndexOf(FlowStep.Result));
        ShowFlow();
        SetStatus("已载入上次保存的标定；要重来请点「上一步」回到前面的步骤，或直接在本页查看预览。");
        return true;
    }

    /// <summary>把载入的标定灌回界面与校准状态（物理尺寸 / 手掌尺寸 / 阈值 / 微调）。</summary>
    private void ApplyLoaded(CalibrationResult saved)
    {
        _result = saved;
        _sizeSource = saved.SizeSource;
        _edid = new EdidSize(saved.EdidWidthMm, saved.EdidHeightMm);
        if (!_edidCandidates.Contains(_edid))
            _edidCandidates.Add(_edid);

        if (saved.PalmWidthPx > 0) PalmWidthInput.Text = saved.PalmWidthPx.ToString("0.###");
        if (saved.PalmHeightPx > 0) PalmHeightInput.Text = saved.PalmHeightPx.ToString("0.###");

        // 建立标尺用的 ScreenCalibration，然后用保存的 mm/px 覆盖（对角线 / 手画尺子时推不出来）
        RebuildCalibration();
        if (saved.MmPerPxX > 0 && saved.MmPerPxY > 0)
        {
            _result.MmPerPxX = saved.MmPerPxX;
            _result.MmPerPxY = saved.MmPerPxY;
            _mmPerDiuX = saved.MmPerPxX * _dpiScaleX;
            _mmPerDiuY = saved.MmPerPxY * _dpiScaleY;
        }

        // 恢复预览页的开关/滑块/形状/长宽比/面积公式（老版本 JSON 无这些字段时保持默认）
        EraserPage.ApplySettings(saved);

        // 恢复 HID 触摸屏（老版本 JSON 无则保持默认"自动"）。
        // 现在这个键是「逻辑屏键」(VID&PID)；旧版本存的是完整设备路径，认不出 → 退回"自动"，避免锁死到不存在的设备。
        if (!string.IsNullOrEmpty(saved.HidDeviceKey))
        {
            string key = saved.HidDeviceKey;
            bool known = _hidDevices.Any(d => d.IsTouchScreen
                && string.Equals(d.GroupKey, key, StringComparison.OrdinalIgnoreCase));
            EraserPage.Engine.SetRawHidDevice(known ? key : "");
        }
        SyncSourceUi();
    }

    private void UpdateDpi()
    {
        PresentationSource? src = PresentationSource.FromVisual(this);
        Matrix m = src?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        _dpiScaleX = m.M11 > 0 ? m.M11 : 1;
        _dpiScaleY = m.M22 > 0 ? m.M22 : 1;
    }

    /// <summary>按当前答案重建校准（推算方式 + mm/px + mm/DIU），并重画标尺。</summary>
    private void RebuildCalibration()
    {
        // 手填对角线 / 手画尺子时按方形像素推，推算方式就不起作用了
        SizeMode mode = _sizeSource == "EDID"
            ? ModeFrom(_result.WidthRulerOk, _result.HeightRulerOk)
            : SizeMode.UseEdid;
        _calib = new ScreenCalibration(_edid, _result.ResX, _result.ResY, mode);

        _result.SizeSource = _sizeSource;
        _result.EdidWidthMm = _edid.WidthMm;
        _result.EdidHeightMm = _edid.HeightMm;
        _result.SizeMode = _sizeSource == "EDID" ? _calib.ModeName : $"{_sizeSource}（方形像素）";
        _result.MmPerPxX = _calib.MmPerPxX;
        _result.MmPerPxY = _calib.MmPerPxY;

        _mmPerDiuX = _calib.MmPerPxX * _dpiScaleX;
        _mmPerDiuY = _calib.MmPerPxY * _dpiScaleY;

        _touch.MmPerDiuX = _mmPerDiuX;
        _touch.MmPerDiuY = _mmPerDiuY;

        string head = _sizeSource switch
        {
            "对角线" => $"手填对角线 {_result.DiagonalInch:0.#} 英寸 → 物理 {_edid.WidthMm} × {_edid.HeightMm} mm（方形像素）",
            "尺子" => $"屏幕画的 10cm 标定 → 物理 {_edid.WidthMm} × {_edid.HeightMm} mm（方形像素）",
            _ => _calib.ModeName,
        };
        ModeText.Text = $"{head}\n物理: {_calib.ScreenWidthMm:0.0} × {_calib.ScreenHeightMm:0.0} mm    " +
                        $"分辨率: {_calib.ResX} × {_calib.ResY}\n" +
                        $"mm/px: {Precision.Fmt(_calib.MmPerPxX)} × {Precision.Fmt(_calib.MmPerPxY)}";

        DrawRulers();
        RefreshInfoBar();
        SyncEraserPage();
        Log.Info($"重建校准: 来源={_sizeSource} 方式={_result.SizeMode} 物理={_calib.ScreenWidthMm:0.0}×{_calib.ScreenHeightMm:0.0}mm "
                 + $"mm/px={Precision.Fmt(_calib.MmPerPxX)}×{Precision.Fmt(_calib.MmPerPxY)} 横10cm准={_result.WidthRulerOk} 竖10cm准={_result.HeightRulerOk}");
    }

    /// <summary>高精度模式开关：后端取整策略 + 显示有效位；切换后立即全量刷新。</summary>
    private void OnHighPrecisionChanged(object sender, RoutedEventArgs e)
    {
        // XAML 解析期 IsChecked="True" 会提前触发，此时后面的控件还没连上 → 忽略
        if (HighPrecisionCheck is null || EraserPage is null || PalmWidthInput is null)
            return;

        Precision.High = HighPrecisionCheck.IsChecked == true;
        Log.Info($"高精度模式 = {Precision.High}");

        RecomputePalmSize();
        RefreshInfoBar();
        EraserPage.Engine.NotifyChanged();
        SetStatus(Precision.High
            ? "高精度模式：已开启（后端双精度、不取整；显示按有效位自适应）"
            : "高精度模式：已关闭（后端取整、显示固定 1 位小数）");
    }

    /// <summary>EDID 不可信（都不准 / 读失败）时：手填对角线英寸反推物理尺寸（方形像素，按分辨率比例）。</summary>
    private void OnApplyDiagonal(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(DiagonalInput.Text.Trim(), out double inch) || inch < 2 || inch > 120)
        {
            SetStatus("对角线请填 2~120 英寸。");
            return;
        }

        _edid = SizeFromDiagonal(inch, _result.ResX, _result.ResY);
        _sizeSource = "对角线";
        _result.DiagonalInch = inch;
        Log.Info($"手填对角线 {inch:0.#} 英寸 → {_edid.WidthMm}×{_edid.HeightMm}mm（方形像素）");
        RebuildCalibration();
        SetStatus($"已按 {inch:0.#} 英寸对角线设定：{_edid.WidthMm} × {_edid.HeightMm} mm（方形像素）。");
    }

    private static EdidSize SizeFromDiagonal(double inch, int resX, int resY)
    {
        double diagMm = inch * 25.4;
        double diagPx = Math.Sqrt((double)resX * resX + (double)resY * resY);
        double mmPerPx = diagMm / diagPx;
        return new EdidSize((int)Math.Round(mmPerPx * resX), (int)Math.Round(mmPerPx * resY));
    }

    // ================= 手画 10cm 标定 =================

    private void OnDrawDown(object sender, MouseButtonEventArgs e)
    {
        _drawing = true;
        _drawStart = e.GetPosition(DrawCanvas);
        _drawEnd = _drawStart;
        DrawCanvas.CaptureMouse();
        EnsureDrawLines();
        UpdateUserLine();
    }

    private void OnDrawMove(object sender, MouseEventArgs e)
    {
        if (!_drawing)
            return;
        _drawEnd = e.GetPosition(DrawCanvas);
        UpdateUserLine();
    }

    private void OnDrawUp(object sender, MouseButtonEventArgs e)
    {
        if (!_drawing)
            return;
        _drawing = false;
        DrawCanvas.ReleaseMouseCapture();
        _drawEnd = e.GetPosition(DrawCanvas);
        FinishDraw();
    }

    private void EnsureDrawLines()
    {
        if (_userDrawLine is not null)
            return;
        _userDrawLine = new Line { Stroke = Brushes.SteelBlue, StrokeThickness = 2 };
        _progLine = new Line { Stroke = Brushes.Red, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 4, 3 } };
        DrawCanvas.Children.Add(_userDrawLine);
        DrawCanvas.Children.Add(_progLine);
    }

    private void UpdateUserLine()
    {
        if (_userDrawLine is null)
            return;
        _userDrawLine.X1 = _drawStart.X;
        _userDrawLine.Y1 = _drawStart.Y;
        _userDrawLine.X2 = _drawEnd.X;
        _userDrawLine.Y2 = _drawEnd.Y;
    }

    /// <summary>画完：斜的自动摆正到较长的那一边，由该边长度推 mm/像素（方形像素）。</summary>
    private void FinishDraw()
    {
        double dx = Math.Abs(_drawEnd.X - _drawStart.X);
        double dy = Math.Abs(_drawEnd.Y - _drawStart.Y);
        _drawnIsHorizontal = dx >= dy;
        _drawnLenDiu = Math.Max(dx, dy);

        if (_drawnLenDiu < 30)
        {
            _hasDrawn = false;
            DrawInfoText.Text = "线段太短，请拖一条更长的（目标正好 10 cm）。";
            FineTunePanel.Visibility = Visibility.Collapsed;
            return;
        }

        double dpiScale = _drawnIsHorizontal ? _dpiScaleX : _dpiScaleY;
        _drawnMmPerPx = 100.0 / _drawnLenDiu / dpiScale;   // 该轴 10cm=100mm

        _hasDrawn = true;
        if (FineTuneSlider is not null)
            FineTuneSlider.Value = 1.0;
        FineTunePanel.Visibility = Visibility.Visible;
        RedrawPreview();
    }

    private void RedrawPreview()
    {
        if (!_hasDrawn || _progLine is null || DrawInfoText is null)
            return;

        double scale = FineTuneSlider?.Value ?? 1.0;
        double dpiScale = _drawnIsHorizontal ? _dpiScaleX : _dpiScaleY;
        double mmPerDiu = _drawnMmPerPx * scale * dpiScale;
        double lenDiu = mmPerDiu > 0 ? 100.0 / mmPerDiu : 0;

        if (_drawnIsHorizontal)
        {
            _progLine.X1 = _drawStart.X;
            _progLine.Y1 = _drawStart.Y;
            _progLine.X2 = _drawStart.X + lenDiu;
            _progLine.Y2 = _drawStart.Y;
        }
        else
        {
            _progLine.X1 = _drawStart.X;
            _progLine.Y1 = _drawStart.Y;
            _progLine.X2 = _drawStart.X;
            _progLine.Y2 = _drawStart.Y + lenDiu;
        }

        if (FineTuneText is not null)
            FineTuneText.Text = $"×{Precision.Fmt(scale, 3)} → mm/px {Precision.Fmt(_drawnMmPerPx * scale)}";
        DrawInfoText.Text = $"你画的是{(_drawnIsHorizontal ? "横向" : "纵向")} {_drawnLenDiu:0} DIU → mm/px {Precision.Fmt(_drawnMmPerPx)}；红虚线=程序算的 10cm，对齐它即可";
    }

    private void OnFineTuneChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => RedrawPreview();

    private void OnApplyDrawn(object sender, RoutedEventArgs e)
    {
        if (!_hasDrawn)
        {
            SetStatus("请先在灰框里画一条 10cm 的线。");
            return;
        }

        double mmPerPx = _drawnMmPerPx * (FineTuneSlider?.Value ?? 1.0);
        if (!(mmPerPx > 0))
            return;

        _edid = new EdidSize((int)Math.Round(mmPerPx * _result.ResX), (int)Math.Round(mmPerPx * _result.ResY));
        _sizeSource = "尺子";
        _result.DiagonalInch = null;
        Log.Info($"手画10cm标定: 方向={(_drawnIsHorizontal ? "横" : "竖")} 长度={_drawnLenDiu:0}DIU "
                 + $"微调={(FineTuneSlider?.Value ?? 1.0):0.###} → mm/px={Precision.Fmt(mmPerPx)} → {_edid.WidthMm}×{_edid.HeightMm}mm");
        RebuildCalibration();
        SetStatus($"已按屏幕画的 10cm 标定：{_edid.WidthMm} × {_edid.HeightMm} mm（方形像素）。");
    }

    private static SizeMode ModeFrom(bool wOk, bool hOk)
    {
        if (wOk && hOk) return SizeMode.UseEdid;
        if (wOk) return SizeMode.TrustWidth;
        if (hOk) return SizeMode.TrustHeight;
        return SizeMode.UseEdid;  // 都不准 → 只能退回原样，由界面提示
    }

    /// <summary>手动改选 EDID 候选（程序按比例挑错时用来纠正）。</summary>
    private void OnEdidPicked(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEdidCombo)
            return;

        int i = EdidCombo.SelectedIndex;
        if (i < 0 || i >= _edidCandidates.Count)
            return;

        _edid = _edidCandidates[i];
        _sizeSource = "EDID";
        _result.DiagonalInch = null;
        Log.Info($"手动改选 EDID 候选: {_edid.WidthMm}×{_edid.HeightMm}mm");
        RebuildCalibration();
        SetStatus($"已改用 EDID 候选 {_edid.WidthMm} × {_edid.HeightMm} mm。");
    }

    // ================= 10cm 标尺 =================

    private void DrawRulers()
    {
        RulerH.Children.Clear();
        RulerV.Children.Clear();
        if (_mmPerDiuX <= 0 || _mmPerDiuY <= 0)
            return;

        const double lenMm = 100.0;   // 10 cm
        DrawRulerLine(RulerH, horizontal: true, lenMm, _mmPerDiuX);
        DrawRulerLine(RulerV, horizontal: false, lenMm, _mmPerDiuY);
    }

    /// <summary>画一条 10cm 标尺：每 1mm 一个刻度，每 5mm 中刻度、每 10mm 长刻度并标 cm 数字。</summary>
    private static void DrawRulerLine(Canvas canvas, bool horizontal, double lenMm, double mmPerDiu)
    {
        if (mmPerDiu <= 0)
            return;

        const double start = 24;    // 起点坐标（DIU）
        const double axis = 70;     // 轴线坐标（DIU）
        double lenDiu = lenMm / mmPerDiu;
        if (!double.IsFinite(lenDiu) || lenDiu <= 0)
            return;

        var axisLine = horizontal
            ? new Line { X1 = start, Y1 = axis, X2 = start + lenDiu, Y2 = axis }
            : new Line { X1 = axis, Y1 = start, X2 = axis, Y2 = start + lenDiu };
        axisLine.Stroke = Brushes.Black;
        axisLine.StrokeThickness = 1.5;
        canvas.Children.Add(axisLine);

        int total = (int)Math.Round(lenMm);
        for (int mm = 0; mm <= total; mm++)
        {
            double pos = start + mm / mmPerDiu;
            bool major = mm % 10 == 0;
            bool half = mm % 5 == 0;
            double h = major ? 16 : (half ? 11 : 6);

            var tk = horizontal
                ? new Line { X1 = pos, Y1 = axis - h, X2 = pos, Y2 = axis + h }
                : new Line { X1 = axis - h, Y1 = pos, X2 = axis + h, Y2 = pos };
            tk.Stroke = Brushes.Black;
            tk.StrokeThickness = major ? 1.6 : 0.8;
            canvas.Children.Add(tk);

            if (major)
            {
                var lb = new TextBlock { Text = (mm / 10).ToString(), FontSize = 11, Foreground = Brushes.Black };
                canvas.Children.Add(lb);
                if (horizontal)
                {
                    Canvas.SetLeft(lb, pos - 5);
                    Canvas.SetTop(lb, axis + 18);
                }
                else
                {
                    Canvas.SetLeft(lb, axis + 22);
                    Canvas.SetTop(lb, pos - 8);
                }
            }
        }

        var cap = new TextBlock { Text = "10 cm（每格 1mm，标注为 cm）", FontSize = 11, Foreground = Brushes.Gray };
        canvas.Children.Add(cap);
        if (horizontal)
        {
            Canvas.SetLeft(cap, start);
            Canvas.SetTop(cap, axis + 34);
        }
        else
        {
            Canvas.SetLeft(cap, axis + 22);
            Canvas.SetTop(cap, start + lenDiu + 6);
        }
    }

    // ================= 路线与步骤导航 =================

    /// <summary>初始（未选路线）：一条含「选路线」的默认序列。</summary>
    private void InitFlow()
    {
        _flow.Clear();
        _flow.Add(FlowStep.Route);
        _flow.Add(FlowStep.ScreenPick);
        _flow.Add(FlowStep.Screen);
        _flow.Add(FlowStep.PalmCm);
        _flow.Add(FlowStep.Press);
        _flow.Add(FlowStep.Finger);
        _flow.Add(FlowStep.Result);
        _flowIndex = 0;
    }

    /// <summary>按所选方式构造步骤序列，并直接进入第一步。</summary>
    private void BuildFlow(bool traceRoute)
    {
        _flow.Clear();
        _edidUnusable = false;
        _flow.Add(FlowStep.Route);
        if (traceRoute)
        {
            _flow.Add(FlowStep.PalmTrace);
        }
        else
        {
            _flow.Add(FlowStep.ScreenPick);
            _flow.Add(FlowStep.Screen);
            _flow.Add(FlowStep.PalmCm);
        }
        _flow.Add(FlowStep.Press);
        _flow.Add(FlowStep.Finger);
        _flow.Add(FlowStep.Result);

        Log.Info(traceRoute ? "选择路线一：直接描摹手掌" : "选择路线二：屏幕标定 → 手掌厘米");
        _flowIndex = 1;   // 直接进入第一步
        ShowFlow();
    }

    private void OnPickTraceRoute(object sender, RoutedEventArgs e) => BuildFlow(traceRoute: true);
    private void OnPickScreenRoute(object sender, RoutedEventArgs e) => BuildFlow(traceRoute: false);

    private void ShowFlow()
    {
        FlowStep s = Current;
        StepRoute.Visibility = Vis(s == FlowStep.Route);
        StepScreenPick.Visibility = Vis(s == FlowStep.ScreenPick);
        StepScreen.Visibility = Vis(s == FlowStep.Screen);
        StepPalmCm.Visibility = Vis(s == FlowStep.PalmCm);
        StepPalmTrace.Visibility = Vis(s == FlowStep.PalmTrace);
        Step4.Visibility = Vis(s == FlowStep.Press);
        Step5.Visibility = Vis(s == FlowStep.Finger);
        Step6.Visibility = Vis(s == FlowStep.Result);

        StepTitle.Text = TitleOf(s);
        BackBtn.IsEnabled = _flowIndex > 0;
        NextBtn.Visibility = Vis(s != FlowStep.Result);
        NextBtn.Content = s == FlowStep.Finger ? "完成" : "下一步";

        if (s == FlowStep.ScreenPick)
            UpdateScreenPick();
        else if (s == FlowStep.Screen)
            ApplyScreenMode();
        else if (s == FlowStep.PalmCm)
            UpdatePalmCm();
        else if (s == FlowStep.PalmTrace)
            RecomputePalmSize();
        else if (s == FlowStep.Result)
            BuildResult();

        Log.Info($"进入 {TitleOf(s)}");
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_flowIndex > 0)
        {
            _flowIndex--;
            ShowFlow();
        }
    }

    private void Advance()
    {
        if (_flowIndex < _flow.Count - 1)
        {
            _flowIndex++;
            ShowFlow();
        }
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        switch (Current)
        {
            case FlowStep.Screen:
                if (_screenMode == ScreenMode.Edid)
                {
                    _result.WidthRulerOk = HWideOk.IsChecked == true;
                    _result.HeightRulerOk = VWideOk.IsChecked == true;
                    RebuildCalibration();
                    // 横竖都不准 → 退回「选方式」，让你换「画 10cm」或「对角英寸」
                    if (!_result.WidthRulerOk && !_result.HeightRulerOk)
                    {
                        _edidUnusable = true;
                        _flowIndex = Math.Max(0, _flow.IndexOf(FlowStep.ScreenPick));
                        ShowFlow();
                        SetStatus("方式 1（EDID）横竖都不准 → 已隐藏方式 1，请改选方式 2 或 3。");
                        return;
                    }
                }
                if (!(_result.MmPerPxX > 0 && _result.MmPerPxY > 0))
                {
                    SetStatus("当前标定方式还没算出毫米/像素，请先完成标定。");
                    return;
                }
                Advance();
                break;

            case FlowStep.PalmCm:
                UpdatePalmCm();
                if (_result.PalmAreaPx2 <= 0)
                {
                    SetStatus("请填入手掌宽/高（厘米），且屏幕标定已完成。");
                    return;
                }
                Advance();
                break;

            case FlowStep.PalmTrace:
                if (_result.PalmAreaPx2 <= 0)
                {
                    SetStatus("请在左侧描摹手掌，或在右侧填入手掌宽/高（px）。");
                    return;
                }
                Advance();
                break;

            case FlowStep.Press:
                if (_palmAreas.Count == 0)
                {
                    SetStatus("请先把手掌按在黑区上，点「记录手掌」（可多按几次取中值）。");
                    return;
                }
                _result.PalmContactAreaPx2 = Precision.Round(Median(_palmAreas));
                _result.PalmPressure = _palmPressures.Count > 0 ? Precision.Round(Median(_palmPressures), 3) : null;
                Log.Info($"手掌定案: {_palmAreas.Count} 次中值面积={FmtArea(_result.PalmContactAreaPx2)}{FmtPressure(_result.PalmPressure)}");
                RefreshInfoBar();
                Advance();
                break;

            case FlowStep.Finger:
                if (_fingerAreas.Count == 0)
                {
                    SetStatus("请先用一根手指按在黑区上，点「记录手指」（可多按几次取中值）。");
                    return;
                }
                _result.FingerContactAreaPx2 = Precision.Round(Median(_fingerAreas));
                _result.FingerPressure = _fingerPressures.Count > 0 ? Precision.Round(Median(_fingerPressures), 3) : null;
                Log.Info($"手指定案: {_fingerAreas.Count} 次中值面积={FmtArea(_result.FingerContactAreaPx2)}{FmtPressure(_result.FingerPressure)}");
                RefreshInfoBar();
                Advance();
                break;
        }
    }

    // ================= 屏幕标定方式（三选一，选完进对应界面）=================

    private void OnPickScreenEdid(object sender, RoutedEventArgs e) => PickScreenMode(ScreenMode.Edid);
    private void OnPickScreenDraw(object sender, RoutedEventArgs e) => PickScreenMode(ScreenMode.Draw);
    private void OnPickScreenDiag(object sender, RoutedEventArgs e) => PickScreenMode(ScreenMode.Diag);

    private void PickScreenMode(ScreenMode mode)
    {
        _screenMode = mode;
        Advance();   // 从「选方式」进到对应的标定界面
    }

    /// <summary>选方式页：方式 1（EDID）若已判定横竖都不准，就不再显示它。</summary>
    private void UpdateScreenPick()
    {
        if (ScreenPickEdid is not null)
            ScreenPickEdid.Visibility = Vis(!_edidUnusable);
    }

    /// <summary>按当前所选方式显示对应面板（EDID 方式顺带重画标尺）。</summary>
    private void ApplyScreenMode()
    {
        if (ScreenModeTitle is null)
            return;   // XAML 解析期字段尚未就绪

        bool edid = _screenMode == ScreenMode.Edid;
        ScreenEdidPanel.Visibility = Vis(edid);
        ScreenDrawPanel.Visibility = Vis(_screenMode == ScreenMode.Draw);
        ScreenDiagPanel.Visibility = Vis(_screenMode == ScreenMode.Diag);
        ScreenModeTitle.Text = _screenMode switch
        {
            ScreenMode.Edid => "屏幕标定 · 方式 1：EDID 核对（用尺子量下面两段 10cm）",
            ScreenMode.Draw => "屏幕标定 · 方式 2：画一条 10cm 线",
            _ => "屏幕标定 · 方式 3：填屏幕对角线英寸",
        };
        if (edid)
            RebuildCalibration();
    }

    // ================= 路线一：手掌厘米 → 像素 =================

    private void OnPalmCmChanged(object sender, TextChangedEventArgs e) => UpdatePalmCm();

    /// <summary>手掌宽/高（cm）× 当前 mm/px → 像素尺寸与面积（K 的分子）。</summary>
    private void UpdatePalmCm()
    {
        if (PalmCmWidth is null || PalmCmHeight is null || PalmCmResult is null)
            return;   // XAML 解析期字段尚未就绪

        double.TryParse(PalmCmWidth.Text.Trim(), out double wcm);
        double.TryParse(PalmCmHeight.Text.Trim(), out double hcm);

        double mmx = _calib?.MmPerPxX ?? 0;
        double mmy = _calib?.MmPerPxY ?? 0;
        double wPx = mmx > 0 && wcm > 0 ? wcm * 10.0 / mmx : 0;
        double hPx = mmy > 0 && hcm > 0 ? hcm * 10.0 / mmy : 0;

        _result.PalmWidthPx = wPx;
        _result.PalmHeightPx = hPx;
        _result.PalmAreaPx2 = wPx > 0 && hPx > 0 ? wPx * hPx : 0;

        PalmCmResult.Text = _result.PalmAreaPx2 > 0
            ? $"宽 {Precision.Fmt(wcm, 1)}cm → {Precision.Fmt(wPx, 0)}px｜高 {Precision.Fmt(hcm, 1)}cm → {Precision.Fmt(hPx, 0)}px\n"
              + $"手掌面积 = {Precision.Fmt(_result.PalmAreaPx2, 0)} px²（mm/px {Precision.Fmt(mmx)} × {Precision.Fmt(mmy)}）"
            : "—（请填入手掌宽/高，且屏幕标定已完成）";

        SyncEraserPage();
        RefreshInfoBar();
    }

    // ================= 步骤 4：手掌尺寸 =================

    private void OnStrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
    {
        foreach (StylusPoint p in e.Stroke.StylusPoints)
        {
            if (!_hasBox)
            {
                _bx0 = _bx1 = p.X;
                _by0 = _by1 = p.Y;
                _hasBox = true;
            }
            else
            {
                if (p.X < _bx0) _bx0 = p.X;
                if (p.X > _bx1) _bx1 = p.X;
                if (p.Y < _by0) _by0 = p.Y;
                if (p.Y > _by1) _by1 = p.Y;
            }
        }

        // 用描摹外接矩形填右侧（DIU → 物理像素 px）
        if (_hasBox)
        {
            double wPx = (_bx1 - _bx0) * _dpiScaleX;
            double hPx = (_by1 - _by0) * _dpiScaleY;
            PalmWidthInput.Text = wPx.ToString("0.##");
            PalmHeightInput.Text = hPx.ToString("0.##");
        }
    }

    private void OnClearTrace(object sender, RoutedEventArgs e)
    {
        TraceCanvas.Strokes.Clear();
        _hasBox = false;
        RecomputePalmSize();
        SetStatus("描摹已清除（右侧数值保留）。");
    }

    private void OnPalmSizeChanged(object sender, TextChangedEventArgs e) => RecomputePalmSize();

    /// <summary>按右侧输入框重算手掌宽/长/面积（px²，固定按外接矩形 a1×a2）；进入该步或改宽高时也会调用。</summary>
    private void RecomputePalmSize()
    {
        if (PalmWidthInput is null || PalmHeightInput is null || PalmSizeText is null)
            return;   // XAML 解析期字段尚未全部赋值，忽略

        double.TryParse(PalmWidthInput.Text.Trim(), out double w);
        double.TryParse(PalmHeightInput.Text.Trim(), out double h);

        _result.PalmWidthPx = w;
        _result.PalmHeightPx = h;

        _result.PalmAreaPx2 = w > 0 && h > 0 ? w * h : 0;

        PalmSizeText.Text = _result.PalmAreaPx2 > 0
            ? $"宽 a2 = {Precision.Fmt(w)} px × 高 a1 = {Precision.Fmt(h)} px\n"
              + $"手掌面积（a1×a2）= {Precision.Fmt(_result.PalmAreaPx2, 0)} px²（K 的分子）"
            : "面积：—（在右侧填入手掌宽/高 px 即可，描摹可选）";

        SyncEraserPage();
        RefreshInfoBar();
    }

    // ================= 步骤 5/6：按压采样 =================

    private void OnSample(TouchSample s)
    {
        long nowTicks = Environment.TickCount64;
        _lastSampleTicks = nowTicks;
        if (nowTicks - _lastSampleLogTicks >= 250)
        {
            _lastSampleLogTicks = nowTicks;
            Log.Info($"样本[{TitleOf(Current)}] {Describe(s)}");
        }

        // 信息栏节流刷新（内容都是标定值；节流只为避免每帧刷）
        if (nowTicks - _lastInfoBarTicks >= 300)
        {
            _lastInfoBarTicks = nowTicks;
            RefreshInfoBar();
        }

        if (Current == FlowStep.Result)
        {
            // 信息栏的「当前接触 / 当前压感」在最后一页也要更新（原来这一步提前 return，值永远为 —）
            if (SampleAreaPx2(s) is double la6) _liveAreaPx2 = la6;
            if (s.Pressure01 is double lp6) _livePressure = lp6;
            EraserPage.SubmitSample(s);
            return;
        }

        if (Current != FlowStep.Press && Current != FlowStep.Finger)
            return;

        if (!Accept(s))
            return;

        double? area = SampleAreaPx2(s);
        double? press = s.Pressure01;
        if (area is null && press is null)
            return;

        if (area is double la) _liveAreaPx2 = la;
        if (press is double lp) _livePressure = lp;

        // 峰值 = 自上次「记录」以来的最大接触，抬手不清空：
        // 点「记录」时手指落在按钮上的那一下小接触（远小于手掌）不会顶掉手掌的峰值。
        if (Current == FlowStep.Press)
        {
            _palmActiveSource = s.Source;
            if (area is double a && a > (_palmPeakAreaPx2 ?? 0)) _palmPeakAreaPx2 = a;
            if (press is double p && p > (_palmPeakPressure ?? 0)) _palmPeakPressure = p;
            PalmLiveText.Text = LiveText();
            RefreshPalmPeakText();
            UpdateSourceInfoText();
        }
        else
        {
            _fingerActiveSource = s.Source;
            if (area is double a && a > (_fingerPeakAreaPx2 ?? 0)) _fingerPeakAreaPx2 = a;
            if (press is double p && p > (_fingerPeakPressure ?? 0)) _fingerPeakPressure = p;
            FingerLiveText.Text = LiveText();
            RefreshFingerPeakText();
            UpdateSourceInfoText();
        }
    }

    /// <summary>样本的接触面积（物理像素²）：RawHID = 宽高计数换算后的 px 乘积；WPF = 接触框（DIP × DPI）。
    /// 面积与宽高换算方式无关（面积因子恒等）。</summary>
    private double? SampleAreaPx2(TouchSample s)
        => SamplePx.AreaPx2(s, _result.ResX, _result.ResY, _dpiScaleX, _dpiScaleY,
            EraserPage?.Engine.MultiTouchAsPalm ?? true);

    /// <summary>
    /// 该样本是否参与标定采样。Stylus 只带压感、不参与面积，始终放行；
    /// 手动指定来源时只认那一路；自适应时跟随引擎的锁定来源（锁了谁就只认谁，另一个直接忽略），
    /// 还没锁定时 HID 优先、HID 静默才用 WPF 垫着。
    /// </summary>
    private bool Accept(TouchSample s)
    {
        if (s.Source == "STYLUS")
            return true;

        SourceMode mode = EraserPage?.Engine.Mode ?? SourceMode.Auto;
        if (mode == SourceMode.RawHid)
            return s.Source == "RawHID";
        if (mode == SourceMode.SoftwareWpf)
            return s.Source == "WPF";

        // 自适应：引擎一旦锁定，就只用锁定来源的样本
        ContactSource locked = EraserPage?.Engine.LockedSource ?? ContactSource.None;
        if (locked == ContactSource.RawHid)
            return s.Source == "RawHID";
        if (locked == ContactSource.Wpf)
            return s.Source == "WPF";

        if (s.Source == "RawHID")
            return true;
        if (s.Source == "WPF")
            return Environment.TickCount64 - _lastHidTicks > EraserEngine.SourceFreshMs;
        return false;
    }

    /// <summary>「实时：」一行的文字：面积（物理像素²）与压感分别显示当前值。</summary>
    private string LiveText()
        => $"实时：接触 {(_liveAreaPx2 is double a ? Precision.Fmt(a, 0) + " px²" : "—")}"
         + $" ｜ 压感 {(_livePressure is double p ? Precision.Fmt(p, 3) : "—")}";

    private void RefreshPalmPeakText()
    {
        string rec = _palmAreas.Count > 0
            ? $"已记录 {_palmAreas.Count} 次｜面积中值 {FmtArea(Median(_palmAreas))}"
            : "记录后开始测下一次（建议多按几次取中值）";
        PalmPeakText.Text = $"本次峰值：{FmtArea(_palmPeakAreaPx2)}{FmtPressure(_palmPeakPressure)}\n{rec}";
    }

    private void RefreshFingerPeakText()
    {
        string rec = _fingerAreas.Count > 0
            ? $"已记录 {_fingerAreas.Count} 次｜面积中值 {FmtArea(Median(_fingerAreas))}"
            : "记录后开始测下一次（建议多按几次取中值）";
        FingerPeakText.Text = $"本次峰值：{FmtArea(_fingerPeakAreaPx2)}{FmtPressure(_fingerPeakPressure)}\n{rec}";
    }

    private static double Median(List<double> v)
    {
        var a = v.OrderBy(x => x).ToArray();
        int n = a.Length;
        if (n == 0) return 0;
        return n % 2 == 1 ? a[n / 2] : (a[n / 2 - 1] + a[n / 2]) / 2.0;
    }

    private string Describe(TouchSample s)
    {
        double? px2 = SampleAreaPx2(s);
        string area = px2 is double a ? Precision.Fmt(a, 0) + " px²" : "无尺寸";
        string multi = s.Contacts is { Count: > 1 } cs ? $"（{cs.Count} 指）" : "";
        string p = s.Pressure01 is double v ? $"，压感 {Precision.Fmt(v, 2)}" : "";
        if (s.PressureRaw is int raw)
            p += $"（原生 {raw} / {s.PressureRange}）";
        return $"[{s.Source}]{multi} {area}{p}";
    }

    private static string FmtArea(double? a) => a is double v ? Precision.Fmt(v, 0) + " px²" : "—";
    private static string FmtPressure(double? p) => p is double v ? $"（压感 {Precision.Fmt(v, 2)}）" : "";

    private void OnRecordPalm(object sender, RoutedEventArgs e)
    {
        if (_palmPeakAreaPx2 is not double a)
        {
            SetStatus("还没采到手掌面积，请把手掌按在黑区上再点。");
            return;
        }
        _palmAreas.Add(a);
        if (_palmPeakPressure is double pp)
            _palmPressures.Add(pp);
        _palmPeakAreaPx2 = null;
        _palmPeakPressure = null;
        Log.Info($"记录手掌 第{_palmAreas.Count}次: 面积={Precision.Fmt(a, 0)}px² 压感={(_palmPressures.Count > 0 ? Precision.Fmt(_palmPressures[^1], 2) : "无")}");
        RefreshPalmPeakText();
        RefreshInfoBar();
        SetStatus($"已记录第 {_palmAreas.Count} 次手掌面积 {Precision.Fmt(a, 0)} px²（可再按几次取中值）。");
    }

    private void OnRecordFinger(object sender, RoutedEventArgs e)
    {
        if (_fingerPeakAreaPx2 is not double a)
        {
            SetStatus("还没采到手指面积，请用手指按在黑区上再点。");
            return;
        }
        _fingerAreas.Add(a);
        if (_fingerPeakPressure is double fp)
            _fingerPressures.Add(fp);
        _fingerPeakAreaPx2 = null;
        _fingerPeakPressure = null;
        Log.Info($"记录手指 第{_fingerAreas.Count}次: 面积={Precision.Fmt(a, 0)}px² 压感={(_fingerPressures.Count > 0 ? Precision.Fmt(_fingerPressures[^1], 2) : "无")}");
        RefreshFingerPeakText();
        RefreshInfoBar();
        SetStatus($"已记录第 {_fingerAreas.Count} 次手指面积 {Precision.Fmt(a, 0)} px²（可再按几次取中值）。");
    }

    // ================= 步骤 7：结果 =================

    private void BuildResult()
    {
        SyncEraserPage();   // 先喂标定值：引擎据此算 K，并给阈值滑块设默认（未动过时）

        // 阈值取引擎当前值：新标定 = 自动中值；载入的标定 = 保存时的滑块值（不被自动值覆盖）
        _result.ThresholdAreaPx2 = EraserPage.Engine.AreaThresholdPx2 > 0
            ? Precision.Round(EraserPage.Engine.AreaThresholdPx2)
            : null;
        _result.K = EraserPage.Engine.ComputeK() is double k && k > 0 ? Precision.Round(k) : null;
        _result.Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        Log.Info($"标定结果: 手掌={FmtArea(_result.PalmContactAreaPx2)} 手指={FmtArea(_result.FingerContactAreaPx2)} "
                 + $"→ 切换阈值={FmtArea(_result.ThresholdAreaPx2)} K={(_result.K is double kv ? Precision.Fmt(kv) : "无")}");

        RefreshInfoBar();
    }

    /// <summary>把标定值喂给面积擦预览页（手掌宽高/按压面积/压感，全部物理像素）。</summary>
    private void SyncEraserPage()
    {
        EraserPage.Setup(_dpiScaleX, _dpiScaleY,
            _result.PalmWidthPx, _result.PalmHeightPx,
            _result.PalmContactAreaPx2 ?? 0, _result.FingerContactAreaPx2 ?? 0,
            _result.PalmPressure, _result.FingerPressure,
            _result.ResX, _result.ResY);
    }

    private void OnSaveRequested()
    {
        try
        {
            // 预览页的滑块/开关/形状/长宽比/面积公式：保存界面设置，供主程序按同一套参数工作
            EraserSettings set = EraserPage.CurrentSettings;
            _result.KTrim = set.KTrim;
            _result.ThresholdAreaPx2 = set.AreaThresholdPx2;
            _result.PalmPressureThreshold = set.PalmPressureThreshold;
            _result.PalmNormMax = set.PalmNormMax;
            _result.WritingPressureThreshold = set.WritingPressureThreshold;
            _result.WritingNormMax = set.WritingNormMax;
            _result.PalmPressureEnabled = set.PalmPressureEnabled;
            _result.WritingUsesPressure = set.WritingUsesPressure;
            _result.FollowSize = set.FollowSize;
            _result.LockPalmSize = set.LockPalmSize;
            _result.SmoothJitter = set.SmoothJitter;
            _result.MultiTouchAsPalm = set.MultiTouchAsPalm;
            _result.PalmLimit = set.SizeLimit.ToString();
            _result.AreaThresholdEnabled = set.AreaThresholdEnabled;
            _result.WritingFollowSize = set.WritingFollowSize;
            _result.EraserShape = set.Shape.ToString();
            _result.AspectSource = set.Aspect.ToString();
            _result.AspectW = set.CustomAspectW;
            _result.AspectH = set.CustomAspectH;
            _result.HidSizeScale = set.SizeScale.ToString();
            _result.HidDeviceKey = EraserPage.Engine.RawHidDeviceKey.Length > 0 ? EraserPage.Engine.RawHidDeviceKey : null;
            _result.K = EraserPage.Engine.ComputeK() is double k && k > 0 ? Precision.Round(k) : null;
            _result.PalmAreaPx2 = EraserPage.Engine.PalmAreaPx2;

            string path = CalibrationResult.DefaultPath();
            _result.Save(path);
            EraserPage.SavePathNotice = "已保存到：" + path;
            SetStatus("结果已保存。");
            Log.Info($"结果已保存: {path}（K={(_result.K is double kv ? Precision.Fmt(kv) : "无")} 随尺寸={set.FollowSize} "
                     + $"锁定={set.LockPalmSize} 掌擦压感={set.PalmPressureEnabled} 书写压感={set.WritingUsesPressure} "
                     + $"形状={set.Shape} 长宽比={set.Aspect}）");
        }
        catch (Exception ex)
        {
            EraserPage.SavePathNotice = "保存失败：" + ex.Message;
            SetStatus("保存失败。");
            Log.Error("结果保存失败", ex);
        }
    }

    // ================= 顶部信息栏 =================

    private void RefreshInfoBar()
    {
        if (_calib is null)
        {
            InfoBar.Text = "正在读取 EDID ...";
            return;
        }

        double? kLive = EraserPage?.Engine.ComputeK() is double ek && ek > 0 ? ek : _result.K;
        string kText = kLive is double kv
            ? $"{Precision.Fmt(kv)}（每边 ×{Precision.Fmt(Math.Sqrt(kv))}）"
            : "—（待第 5 步标定手掌按压）";

        double thLive = EraserPage?.Engine.AreaThresholdPx2 ?? 0;

        InfoBar.Text =
            // ① 屏幕：物理尺寸 / 来源 / 分辨率
            $"显示屏尺寸：{_calib.ScreenWidthMm:0.0}毫米 × {_calib.ScreenHeightMm:0.0}毫米"
            + $"    来源：{_sizeSource}：{_calib.Edid.WidthMm}毫米 × {_calib.Edid.HeightMm}毫米"
            + $"    分辨率：{_calib.ResX}像素 × {_calib.ResY}像素\n"
            // ② 推算方式 / 毫米每像素
            + $"推算方式：横{Yes(_result.WidthRulerOk)} 竖{Yes(_result.HeightRulerOk)} {_result.SizeMode}"
            + $"    毫米/像素：{Precision.Fmt(_calib.MmPerPxX)} × {Precision.Fmt(_calib.MmPerPxY)}\n"
            // ③ 手掌描摹（px）/ 按压
            + $"手掌描摹：宽a2={Precision.Fmt(_result.PalmWidthPx, 0)}px 高a1={Precision.Fmt(_result.PalmHeightPx, 0)}px"
            + $"    手掌面积(a1×a2)={Precision.Fmt(_result.PalmAreaPx2, 0)}px²\n"
            + $"手掌按压：面积{FmtArea(_result.PalmContactAreaPx2)} 压感{FmtPressureVal(_result.PalmPressure)}"
            + $"    手指按压：面积{FmtArea(_result.FingerContactAreaPx2)} 压感{FmtPressureVal(_result.FingerPressure)}"
            + $"    切换阈值：{(thLive > 0 ? Precision.Fmt(thLive, 0) + " px²" : "—")}\n"
            // ④ K 定值 + 当前实时值
            + $"K 定值（手掌像素面积 ÷ 触摸尺寸乘积）= {kText}"
            + $"    当前接触：{(_liveAreaPx2 is double la ? Precision.Fmt(la, 0) + " px²" : "—")}"
            + $"    当前压感：{(_livePressure is double lp ? Precision.Fmt(lp, 2) : "—")}";

        // ⑤ 原始HID 映射表：默认不显示，勾上「显示原始HID映射表」才附在末尾
        if (ShowHidTableCheck?.IsChecked == true)
            InfoBar.Text += "\n" + HidTableText();
    }

    /// <summary>「显示原始HID映射表」开关：切换后立即刷新信息栏。</summary>
    private void OnShowHidTableChanged(object sender, RoutedEventArgs e)
    {
        if (ShowHidTableCheck is null || InfoBar is null)
            return;   // XAML 解析期事件早于字段赋值
        RefreshInfoBar();
    }

    private static string FmtPressureVal(double? p) => p is double v ? Precision.Fmt(v, 2) : "—";

    private static string Yes(bool b) => b ? "准" : "不准";

    private void SetStatus(string s) => StatusText.Text = s;
}
