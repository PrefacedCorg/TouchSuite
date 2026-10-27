using System.Runtime.InteropServices;

namespace TouchSuite.Ink;

/// <summary>
/// 底层墨迹窗口：一个纯 Win32 窗口（没有 WPF 内容），只用来承载 DComp 的可视化树。
///
/// <para>为什么要单独开一个窗口，而不是把 DComp 挂在 WPF 窗口的 HWND 上：
/// WPF 的透明窗口走的是 layered（UpdateLayeredWindow）通路，跟 DComp 依赖的 DWM 合成是两条路，
/// 同一 HWND 里"DComp 在下、WPF 在上"不成立。分成两个窗口后，底层窗口是普通 DWM 合成窗口，
/// DComp 按官方语义工作；上层 WPF 窗口保持置顶，用 HWND 顺序保证"上/下"。</para>
///
/// <para>这个窗口永不接收输入：WS_EX_TRANSPARENT + WM_NCHITTEST 返回 HTTRANSPARENT。</para>
/// </summary>
internal sealed class InkSurfaceWindow : IDisposable
{
    private const string ClassName = "TouchSuiteInkSurface";

    private readonly Win32.WndProc _wndProc;   // 必须缓存，否则委托被 GC 后回调就悬空了
    private IntPtr _hwnd;

    public IntPtr Handle => _hwnd;
    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }

    public InkSurfaceWindow()
    {
        // MVP 只覆盖主屏：原点 (0,0) + 主屏像素尺寸。多屏要改成虚拟屏范围。
        X = 0;
        Y = 0;
        Width = Math.Max(1, Win32.GetSystemMetrics(0));   // SM_CXSCREEN
        Height = Math.Max(1, Win32.GetSystemMetrics(1));  // SM_CYSCREEN

        _wndProc = WndProcHook;

        var wc = new Win32.WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = Win32.GetModuleHandleW(null),
            lpszClassName = ClassName,
        };
        ushort atom = Win32.RegisterClassW(ref wc);
        Log.Write($"InkSurfaceWindow: RegisterClass atom={atom}");

        const int exStyle = Win32.WS_EX_TOPMOST | Win32.WS_EX_TOOLWINDOW
                          | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TRANSPARENT;

        _hwnd = Win32.CreateWindowExW(exStyle, ClassName, "TouchSuite.Ink 墨迹层",
            Win32.WS_POPUP, X, Y, Width, Height, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            Log.Write($"InkSurfaceWindow: CreateWindowEx 失败，Win32 错误 {Marshal.GetLastWin32Error()}");
            return;
        }

        Win32.ShowWindow(_hwnd, Win32.SW_SHOWNOACTIVATE);
        Log.Write($"InkSurfaceWindow: hwnd=0x{_hwnd.ToInt64():X} 区域=({X},{Y}) {Width}x{Height}");
    }

    /// <summary>
    /// 显示/隐藏墨迹层窗口。鼠标模式下必须隐藏它：
    /// 这个窗口不是 layered 窗口，命中测试永远是整块矩形，WM_NCHITTEST 返回 HTTRANSPARENT
    /// 只在**同线程**窗口之间往下传，传不到别的进程（桌面/其它 App）→ 不隐藏就会把整个屏幕的点击全吃掉。
    /// </summary>
    public void SetVisible(bool visible)
    {
        if (_hwnd == IntPtr.Zero)
            return;
        Win32.ShowWindow(_hwnd, visible ? Win32.SW_SHOWNOACTIVATE : Win32.SW_HIDE);
        Log.Write($"InkSurfaceWindow: {(visible ? "显示" : "隐藏")} hwnd=0x{_hwnd.ToInt64():X}");
    }

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Win32.WM_NCHITTEST)
            return Win32.HTTRANSPARENT;   // 同线程窗口之间的点击穿透（跨进程不起作用，见 SetVisible）
        return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            Win32.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }
}
