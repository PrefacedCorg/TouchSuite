using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using TouchBridge.Receiver;

namespace TouchBridge.Receiver;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.Title = "TouchBridge Receiver";

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

        List<MonitorInfo> monitors = ScreenMapper.EnumerateMonitors();
        if (monitors.Count == 0)
        {
            Console.Error.WriteLine("未枚举到任何显示器。");
            return 1;
        }

        int monitorIndex = Math.Clamp(opt.Monitor, 0, monitors.Count - 1);
        MonitorInfo monitor = monitors[monitorIndex];

        if (opt.SelfTest)
            return RunSelfTest(monitors);

        Log.Write($"===== 启动 ===== args=[{string.Join(' ', args)}]");
        Log.Write($"模式={(opt.Vhid ? "VirtualHID(VHF)" : "user32 合成指针")}  显示器#{monitorIndex} {monitor.Width}x{monitor.Height}  端口={opt.Port}  screen-mm={opt.ScreenMm}  max-contacts={opt.MaxContacts}");

        // 落地方式：默认 user32 合成指针注入；--vhid 则改为喂给虚拟 HID 触摸屏驱动。
        ITouchSink injector = opt.Vhid
            ? new VhidSender(opt.ScreenMm, opt.PressureMax, opt.PressureFloor, opt.MaxContacts)
            : new TouchInjector(opt.MaxContacts, opt.Feedback, opt.FixedPressure, opt.ContactFallback, opt.LegacyTouchApi, opt.ContactScale, opt.InputType, opt.PressureMax, opt.ContactArea, opt.NoInject, opt.PressureFloor);
        using var sinkDisposer = injector as IDisposable;
        if (!injector.Initialize())
        {
            Console.Error.WriteLine($"初始化触摸注入失败：{injector.LastErrorText}");
            Console.Error.WriteLine("提示：需要运行在交互式桌面会话（已登录、未锁屏）中。");
            Log.Write($"注入器初始化失败：{injector.LastErrorText}");
            return 1;
        }
        Log.Write($"注入器初始化成功：{injector.ApiName}");

        Console.WriteLine("TouchBridge Receiver");
        Console.WriteLine($"  注入接口  : {injector.ApiName}");
        Console.WriteLine($"  指针类型  : {InputTypeLabel(opt.InputType)}");
        Console.WriteLine($"  面积倍率  : ×{opt.ContactScale:0.##}");
        Console.WriteLine($"  接触面积  : {injector.ContactAreaLabel}");
        Console.WriteLine($"  压力映射  : {(opt.PressureMax > 0 ? $"平板原始 0~{opt.PressureMax:0.##}  →  Windows {opt.PressureFloor:0}~1024" : "原始值直通（只裁剪到 0~1024）")}");
        Console.WriteLine($"  监听端口  : {opt.Port}");
        for (int i = 0; i < monitors.Count; i++)
            Console.WriteLine($"  显示器 #{i} : {monitors[i]}");
        Console.WriteLine($"  目标显示器: #{monitorIndex}");
        Console.WriteLine($"  映射模式  : {opt.FitMode}");
        Console.WriteLine($"  触觉反馈  : {opt.Feedback}");
        Console.WriteLine($"  最大接触点: {opt.MaxContacts}");
        if (opt.FixedPressure is double fp)
            Console.WriteLine($"  固定压感  : {fp:0.00}（忽略端上报）");
        if (opt.ContactFallback > 0)
            Console.WriteLine($"  面积回退  : {opt.ContactFallback}px（端未上报接触尺寸时）");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            injector.ReleaseAll();
        };

        // 优先双栈监听：单个 socket 同时接受 IPv6 与 IPv4（IPv4-mapped）。
        TcpListener listener;
        string listenMode;
        if (opt.Ipv4Only || !Socket.OSSupportsIPv6)
        {
            listener = new TcpListener(IPAddress.Any, opt.Port);
            listenMode = "IPv4 (0.0.0.0)";
        }
        else
        {
            listener = new TcpListener(IPAddress.IPv6Any, opt.Port);
            listener.Server.DualMode = true;
            listenMode = "IPv6 + IPv4 双栈 ([::])";
        }

        try
        {
            listener.Start();
        }
        catch (SocketException ex) when (!opt.Ipv4Only && Socket.OSSupportsIPv6)
        {
            Console.WriteLine($"IPv6 监听不可用（{ex.Message}），已回退到 IPv4。");
            listener = new TcpListener(IPAddress.Any, opt.Port);
            try
            {
                listener.Start();
            }
            catch (SocketException ex2)
            {
                Console.Error.WriteLine($"无法监听端口 {opt.Port}：{ex2.Message}");
                return 1;
            }
            listenMode = "IPv4 (0.0.0.0)";
        }
        catch (SocketException ex)
        {
            Console.Error.WriteLine($"无法监听端口 {opt.Port}：{ex.Message}");
            return 1;
        }

        Console.WriteLine($"  监听      : {listenMode}");
        Console.WriteLine($"  本机可填地址（在平板上填其中之一，端口 {opt.Port}）：");
        PrintLocalAddresses();

        // 屏幕左上角调试小窗
        var debugState = new DebugState
        {
            ApiLine = injector.ApiName,
            Mapping = $"{monitor.Device} {monitor.Width}x{monitor.Height} / {opt.FitMode}",
        };
        DebugOverlay? overlay = null;
        if (opt.Overlay)
        {
            overlay = new DebugOverlay(() => BuildDebugText(debugState), msg => Console.WriteLine("  " + msg));
            overlay.Start();
            Console.WriteLine("  调试小窗: 已开启（屏幕左上角，鼠标穿透；用 --no-overlay 关闭）");
        }

        Console.WriteLine("等待平板连接……（Ctrl+C 退出）");
        Console.WriteLine();

        try
        {
            while (!cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                Log.Write($"平板已连接：{client.Client.RemoteEndPoint}");
                await Task.Run(() => HandleClient(client, opt, injector, monitor, debugState), cts.Token);
                Console.WriteLine($"[-] 连接断开（累计注入帧 {injector.InjectedFrames} / 失败 {injector.FailedFrames}）");
            }
        }
        finally
        {
            injector.ReleaseAll();
            listener.Stop();
            overlay?.Dispose();
            Console.WriteLine("已退出。");
        }

        return 0;
    }

    private static void HandleClient(TcpClient client, Options opt, ITouchSink injector, MonitorInfo monitor, DebugState st)
    {
        try
        {
            client.NoDelay = true;
            using NetworkStream stream = client.GetStream();
            var reader = new FrameReader(stream);

            byte[]? helloPayload = reader.ReadFrame();
            if (helloPayload is null || !Protocol.TryParseHello(helloPayload, out Protocol.Hello hello, out byte version))
            {
                Log.Write($"握手失败：对端不是 TouchBridge 客户端（首帧长度={helloPayload?.Length ?? -1}）");
                st.Connection = "握手失败";
                return;
            }

            if (version != Protocol.Version)
                Console.WriteLine($"    警告：协议版本不一致（端 {version}，本机 {Protocol.Version}）。");
            if (hello.DeviceName.Length > 0)
                Console.WriteLine($"    设备：{hello.DeviceName}");
            Console.WriteLine($"    触摸面：{hello.SurfaceWidth}x{hello.SurfaceHeight}");

            var mapper = new ScreenMapper(monitor, hello.SurfaceWidth, hello.SurfaceHeight, opt.FitMode);
            Console.WriteLine($"    映射：{mapper.Describe()}");

            st.Connection = $"已连接 {client.Client.RemoteEndPoint}";
            st.Device = hello.DeviceName.Length > 0 ? hello.DeviceName : "(未命名)";
            st.Surface = $"{hello.SurfaceWidth}x{hello.SurfaceHeight}";
            st.Mapping = mapper.Describe();

            stream.Write(Protocol.BuildHelloAck());
            stream.Flush();

            long frames = 0;
            long parseFail = 0;
            long startedAt = Environment.TickCount64;
            long lastReport = startedAt;
            long lastInputReport = 0;
            long lastStatsAt = 0;

            while (true)
            {
                byte[]? payload = reader.ReadFrame();
                if (payload is null)
                    break;
                if (payload.Length == 0 || payload[0] != Protocol.MsgTouchFrame)
                    continue;
                if (!TouchFrame.TryParse(payload, out TouchFrame frame))
                {
                    parseFail++;
                    if (parseFail <= 5 || parseFail % 100 == 0)
                        Log.Write($"帧解析失败 ×{parseFail}（长度={payload.Length} 类型=0x{payload[0]:X2}）");
                    continue;
                }

                injector.Apply(frame, mapper);
                frames++;
                if (frames <= 50 || frames % 100 == 0)
                {
                    string f0 = frame.Points.Length > 0
                        ? $"首点 id={frame.Points[0].Id} state={frame.Points[0].State} 归一=({frame.Points[0].X:0.###},{frame.Points[0].Y:0.###}) P={frame.Points[0].Pressure:0.##} C={frame.Points[0].ContactW:0.###}x{frame.Points[0].ContactH:0.###}"
                        : "无点";
                    Log.Write($"帧 #{frames} action={frame.Action} seq={frame.Sequence} 点数={frame.Points.Length} {f0} | {injector.DebugText}");
                }

                st.Input = injector.DebugText;

                long nowTicks = Environment.TickCount64;
                if (nowTicks - lastStatsAt >= 200)
                {
                    double seconds = Math.Max(0.001, (nowTicks - startedAt) / 1000.0);
                    st.Stats = $"注入帧 {injector.InjectedFrames}   失败 {injector.FailedFrames}   "
                        + $"{frames / seconds:0.0} fps   活动点 {injector.ActiveCount}";
                    lastStatsAt = nowTicks;
                }

                if (injector.FailedFrames > 0 && (injector.FailedFrames == 1 || injector.FailedFrames % 100 == 0))
                    Console.WriteLine($"    注入失败 ×{injector.FailedFrames}：{injector.LastErrorText}");

                if (opt.ShowInput && Environment.TickCount64 - lastInputReport >= 120)
                {
                    lastInputReport = Environment.TickCount64;
                    Console.WriteLine($"    输入 {injector.LastInputSummary}");
                }

                if (opt.Verbose && Environment.TickCount64 - lastReport >= 1000)
                {
                    lastReport = Environment.TickCount64;
                    Console.WriteLine($"    帧 {frames}，活动点 {injector.ActiveCount}，seq {frame.Sequence}");
                }
            }
        }
        catch (IOException)
        {
            // 对端断开，正常结束
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    连接异常：{ex.Message}");
        }
        finally
        {
            injector.ReleaseAll();
            st.Connection = "未连接";
            st.Input = "(等待输入)";
            st.Stats = $"累计注入帧 {injector.InjectedFrames} / 失败 {injector.FailedFrames}";
            try { client.Close(); } catch { /* 忽略 */ }
        }
    }

    /// <summary>组装调试小窗显示的多行文本。</summary>
    private static string BuildDebugText(DebugState st)
    {
        return string.Join(Environment.NewLine,
            "TouchBridge 触摸转发 · 调试",
            $"连接    : {st.Connection}",
            $"设备    : {st.Device}",
            $"触摸面  : {st.Surface}",
            $"映射    : {st.Mapping}",
            $"注入接口: {st.ApiLine}",
            "────────────────────────────",
            st.Input,
            "────────────────────────────",
            st.Stats);
    }

    /// <summary>离线自检：验证握手/帧解析/坐标映射，不会真的注入触摸。</summary>
    private static int RunSelfTest(List<MonitorInfo> monitors)
    {
        Console.WriteLine("== 自检：离线验证协议解析与坐标映射（不会注入触摸）==");

        MonitorInfo mn = monitors[0];
        const ushort surfaceW = 1280;
        const ushort surfaceH = 800;
        var mapper = new ScreenMapper(mn, surfaceW, surfaceH, ScreenMapper.FitMode.Stretch);

        // 1) 模拟 Android 端 Hello
        byte[] name = Encoding.UTF8.GetBytes("Test Tablet");
        var hello = new byte[7 + name.Length];
        hello[0] = Protocol.MsgHello;
        hello[1] = Protocol.Version;
        BinaryPrimitives.WriteUInt16BigEndian(hello.AsSpan(2), surfaceW);
        BinaryPrimitives.WriteUInt16BigEndian(hello.AsSpan(4), surfaceH);
        hello[6] = (byte)name.Length;
        name.CopyTo(hello.AsSpan(7));

        using (var ms = new MemoryStream())
        {
            WriteFrame(ms, hello);
            ms.Position = 0;
            byte[]? back = new FrameReader(ms).ReadFrame();
            if (back is null || !Protocol.TryParseHello(back, out Protocol.Hello h, out byte ver))
            {
                Console.Error.WriteLine("Hello 解析失败");
                return 1;
            }
            Console.WriteLine($"Hello  : 版本={ver} 触摸面={h.SurfaceWidth}x{h.SurfaceHeight} 设备=\"{h.DeviceName}\"");
        }

        // 2) 模拟一帧双指事件（第一指移动、第二指抬起）
        var p0 = new TouchPointData(0, Protocol.StateUpdate, 1, 0.25f, 0.25f, 0.80f, 400f, 0.10f, 0.06f, 45f);
        var p1 = new TouchPointData(1, Protocol.StateUp, 1, 0.75f, 0.50f, 0.30f, 150f, 0.05f, 0.05f, 0f);
        byte[] frame = EncodeTestFrame(Protocol.ActionPointerUp, 42, p0, p1);

        using (var ms = new MemoryStream())
        {
            WriteFrame(ms, frame);
            ms.Position = 0;
            byte[]? back = new FrameReader(ms).ReadFrame();
            if (back is null || !TouchFrame.TryParse(back, out TouchFrame tf))
            {
                Console.Error.WriteLine("TouchFrame 解析失败");
                return 1;
            }
            Console.WriteLine($"帧     : action={tf.Action} seq={tf.Sequence} 指针数={tf.Points.Length}");
            foreach (TouchPointData pt in tf.Points)
            {
                POINT sp = mapper.Map(pt.X, pt.Y);
                (double cw, double ch) = mapper.ScaleContact(pt.ContactW, pt.ContactH);
                string state = pt.State switch
                {
                    Protocol.StateDown => "DOWN",
                    Protocol.StateUp => "UP",
                    _ => "MOVE",
                };
                Console.WriteLine(
                    $"  指针{pt.Id} {state,-4} 归一化=({pt.X:0.###},{pt.Y:0.###}) → 屏幕=({sp.X},{sp.Y}) "
                    + $"压力(原始)={pt.Pressure:0.##} size={pt.Size:0.##} 接触={cw:0.0}x{ch:0.0}px 朝向={pt.OrientationDeg:0.#}°");
            }
        }

        Console.WriteLine($"映射   : {mapper.Describe()}");
        Console.WriteLine("自检通过。");
        return 0;
    }

    private static string InputTypeLabel(TouchInputType t) => t switch
    {
        TouchInputType.Touch => "Touch（一律触摸：有面积，压感多数应用不用）",
        TouchInputType.Pen => "Pen（一律笔：压感生效，但没有面积）",
        _ => "Auto（手指→触摸、笔/橡皮→笔）",
    };

    /// <summary>打印本机可供平板填写的地址（IPv4 / 全局 IPv6），方便双栈时选填。</summary>
    private static void PrintLocalAddresses()
    {
        var v4 = new List<string>();
        var v6 = new List<string>();

        try
        {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                {
                    IPAddress a = ua.Address;
                    if (a.AddressFamily == AddressFamily.InterNetwork)
                        v4.Add($"    IPv4  {a,-15} [{ni.Name}]");
                    else if (a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal && !a.IsIPv6SiteLocal)
                        v6.Add($"    IPv6  {a} [{ni.Name}]");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    （枚举网卡失败：{ex.Message}）");
            return;
        }

        foreach (string s in v4)
            Console.WriteLine(s);
        foreach (string s in v6)
            Console.WriteLine(s);

        if (v4.Count == 0 && v6.Count == 0)
            Console.WriteLine("    （未找到可用地址，请检查网络连接）");
        else if (v6.Count == 0)
            Console.WriteLine("    （本机无全局 IPv6 地址；fe80:: 链路本地地址一般无需填写）");
        else if (v6.Count > 1)
            Console.WriteLine("    （IPv6 有多个临时地址，任选其一即可）");
    }

    private static byte[] EncodeTestFrame(byte action, uint seq, params TouchPointData[] points)
    {
        var buf = new byte[15 + points.Length * Protocol.PointRecordSize];
        buf[0] = Protocol.MsgTouchFrame;
        buf[1] = action;
        buf[2] = (byte)points.Length;
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(3), seq);
        BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(7), 123456789UL);

        int off = 15;
        foreach (TouchPointData p in points)
        {
            buf[off] = p.Id;
            buf[off + 1] = p.State;
            buf[off + 2] = p.ToolType;
            buf[off + 3] = 0;
            BinaryPrimitives.WriteSingleBigEndian(buf.AsSpan(off + 4), p.X);
            BinaryPrimitives.WriteSingleBigEndian(buf.AsSpan(off + 8), p.Y);
            BinaryPrimitives.WriteSingleBigEndian(buf.AsSpan(off + 12), p.Pressure);
            BinaryPrimitives.WriteSingleBigEndian(buf.AsSpan(off + 16), p.Size);
            BinaryPrimitives.WriteSingleBigEndian(buf.AsSpan(off + 20), p.ContactW);
            BinaryPrimitives.WriteSingleBigEndian(buf.AsSpan(off + 24), p.ContactH);
            BinaryPrimitives.WriteSingleBigEndian(buf.AsSpan(off + 28), p.OrientationDeg);
            off += Protocol.PointRecordSize;
        }
        return buf;
    }

    private static void WriteFrame(Stream stream, byte[] payload)
    {
        var header = new byte[2];
        header[0] = (byte)(payload.Length >> 8);
        header[1] = (byte)payload.Length;
        stream.Write(header);
        stream.Write(payload);
    }
}

internal sealed class DebugState
{
    public volatile string Connection = "等待平板连接…";
    public volatile string Device = "-";
    public volatile string Surface = "-";
    public volatile string Mapping = "-";
    public volatile string ApiLine = "-";
    public volatile string Input = "(等待输入)";
    public volatile string Stats = "";
}

internal sealed class Options
{
    public int Port = 9000;
    public int Monitor = 0;
    public ScreenMapper.FitMode FitMode = ScreenMapper.FitMode.Stretch;
    public TouchInjector.FeedbackMode Feedback = TouchInjector.FeedbackMode.Default;
    public uint MaxContacts = 10;
    public double? FixedPressure;
    public int ContactFallback;
    public bool Verbose;
    public bool SelfTest;
    public bool Ipv4Only;
    public bool LegacyTouchApi;
    public double ContactScale = 1.0;
    /// <summary>平板原始压力对应的满量程，映射到 Windows 的 0~1024。0 表示不归一化（原始值直通）。</summary>
    public double PressureMax = 14;
    /// <summary>映射后的输出下限（0~1024）。例如 256 表示最轻也输出 256。</summary>
    public double PressureFloor = 256;
    public TouchInputType InputType = TouchInputType.Auto;
    public bool ShowInput;
    public bool Overlay = true;
    public ContactAreaMode ContactArea = ContactAreaMode.Auto;
    public bool NoInject;
    /// <summary>改用虚拟 HID 触摸屏驱动（VHF）而非 user32 合成指针。</summary>
    public bool Vhid;
    /// <summary>目标屏幕物理宽度（mm）。用于把接触尺寸换算成毫米（0 = 按 96DPI 估算）。</summary>
    public double ScreenMm;
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
                case "--port":
                    o.Port = int.Parse(Next());
                    break;
                case "--monitor":
                    o.Monitor = int.Parse(Next());
                    break;
                case "--fit":
                    o.FitMode = Next().ToLowerInvariant() switch
                    {
                        "stretch" => ScreenMapper.FitMode.Stretch,
                        "fit" => ScreenMapper.FitMode.Fit,
                        "cover" => ScreenMapper.FitMode.Cover,
                        var v => throw new ArgumentException($"未知映射模式 {v}（可选 stretch/fit/cover）"),
                    };
                    break;
                case "--feedback":
                    o.Feedback = Next().ToLowerInvariant() switch
                    {
                        "default" => TouchInjector.FeedbackMode.Default,
                        "indirect" => TouchInjector.FeedbackMode.Indirect,
                        "none" => TouchInjector.FeedbackMode.None,
                        var v => throw new ArgumentException($"未知反馈模式 {v}（可选 default/indirect/none）"),
                    };
                    break;
                case "--max-contacts":
                    o.MaxContacts = uint.Parse(Next());
                    break;
                case "--fixed-pressure":
                    o.FixedPressure = double.Parse(Next());
                    break;
                case "--contact-fallback":
                    o.ContactFallback = int.Parse(Next());
                    break;
                case "-v":
                case "--verbose":
                    o.Verbose = true;
                    break;
                case "--self-test":
                    o.SelfTest = true;
                    break;
                case "--ipv4-only":
                    o.Ipv4Only = true;
                    break;
                case "--legacy-touch-api":
                    o.LegacyTouchApi = true;
                    break;
                case "--contact-scale":
                    o.ContactScale = double.Parse(Next());
                    if (o.ContactScale <= 0) throw new ArgumentException("--contact-scale 必须大于 0");
                    break;
                case "--pressure-max":
                    o.PressureMax = double.Parse(Next());
                    if (o.PressureMax < 0) throw new ArgumentException("--pressure-max 不能为负（0 表示原始值直通，不归一化）");
                    break;
                case "--pressure-floor":
                    o.PressureFloor = double.Parse(Next());
                    if (o.PressureFloor < 0 || o.PressureFloor > 1024) throw new ArgumentException("--pressure-floor 必须在 0~1024 之间");
                    break;
                case "--input-type":
                    o.InputType = Next().ToLowerInvariant() switch
                    {
                        "auto" => TouchInputType.Auto,
                        "touch" => TouchInputType.Touch,
                        "pen" => TouchInputType.Pen,
                        var v => throw new ArgumentException($"未知指针类型 {v}（可选 auto/touch/pen）"),
                    };
                    break;
                case "--show-input":
                    o.ShowInput = true;
                    break;
                case "--no-overlay":
                    o.Overlay = false;
                    break;
                case "--no-inject":
                    o.NoInject = true;
                    break;
                case "--vhid":
                    o.Vhid = true;
                    break;
                case "--screen-mm":
                    o.ScreenMm = double.Parse(Next());
                    if (o.ScreenMm < 0) throw new ArgumentException("--screen-mm 不能为负");
                    break;
                case "--contact-area":
                    o.ContactArea = Next().ToLowerInvariant() switch
                    {
                        "auto" => ContactAreaMode.Auto,
                        "size" => ContactAreaMode.Auto,
                        "major" => ContactAreaMode.Major,
                        var v => throw new ArgumentException($"未知接触面积模式 {v}（可选 auto/size/major）"),
                    };
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
            用法: TouchBridge.Receiver [选项]

              --port <n>             监听端口（默认 9000）
              --monitor <n>          目标显示器序号（默认 0，可用下面列出的序号）
              --fit <mode>           映射模式：stretch|fit|cover（默认 stretch）
              --feedback <mode>      触摸视觉反馈：default|indirect|none（默认 default）
              --max-contacts <n>     最大同时接触点（默认 10）
              --fixed-pressure <0..1> 强制固定压感，忽略平板上报
              --contact-fallback <px> 平板未上报接触尺寸时的回退直径（像素，默认关闭）
              -v, --verbose          打印每秒统计
              --self-test            离线自检协议解析与坐标映射（不注入触摸）
              --ipv4-only            只监听 IPv4（默认双栈：IPv6 + IPv4）
              --legacy-touch-api     强制使用旧版 InjectTouchInput（默认用新版合成指针 API）
              --contact-scale <x>    接触面积放大倍数（默认 1.0，如 2 表示面积按 2 倍放大）
              --pressure-max <x>     平板原始压力的满量程（默认 14；填 0 则原始值直通不归一）
              --pressure-floor <v>   映射后的输出下限 0~1024（默认 256），即 0~14 → 256~1024
              --input-type <t>       注入类型：auto|touch|pen（默认 auto：手指→触摸、笔→笔）
              --show-input           实时打印收到的压力/面积（用于排查压感是否有变化）
              --no-overlay           关闭屏幕左上角的调试小窗（默认开启）
              --contact-area <m>     接触面积来源：auto|size（按平板 size 定面积，W×H=size）|major（用 major/minor 包围盒）
              --no-inject           只算不注入（不碰桌面，用于核对坐标/面积/压力）
              --vhid                 改用本仓库的虚拟 HID 触摸屏驱动（VHF）而非 user32 合成指针
              --screen-mm <mm>       目标屏幕物理宽度（毫米），用于把接触尺寸换算成毫米（默认按 96DPI 估）
              -h, --help             显示本帮助
            """);
    }
}

/// <summary>
/// 落地日志：同时写到控制台和 exe 同目录下的 TouchBridge.Receiver.log，
/// 方便把整段运行时输出直接发出来定位问题。
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static readonly string FilePath =
        System.IO.Path.Combine(AppContext.BaseDirectory, "TouchBridge.Receiver.log");

    public static void Write(string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        lock (Gate)
        {
            Console.WriteLine(line);
            try
            {
                System.IO.File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch
            {
                // 日志写盘失败不影响主流程
            }
        }
    }
}
