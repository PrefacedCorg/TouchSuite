using System.Runtime.InteropServices;

namespace TouchSuite.Ink;

/// <summary>
/// WM_POINTER 帧通路（Windows 8+）。移植自 TouchSuite.App.old 的 PointerTouch，
/// 但**从单点 API 升级成帧 API**：同一帧里的所有指针是"同一设备的同一时刻"，必须整帧当一次输入处理，
/// 否则一根手指的同一位置会被派发多次；处理完整帧后要调 SkipPointerFrameMessages，
/// 把本帧剩余消息丢掉（否则 N 个触点会把同一帧派发 N 遍）。
///
/// <para><b>时序约束</b>：帧信息与"调用线程最近检索的消息"绑定，所以必须在 WndProc hook 内、
/// 同一 UI 线程、收到消息后立即调用 —— 不能挪到后台线程或延迟到下次消息。</para>
///
/// <para>本文件只处理 PT_TOUCH：真实触笔(PT_PEN)不会被提升成 Touch，继续走 WPF Stylus 通道，
/// 两者不会重复。</para>
/// </summary>
internal static class PointerTouchFrame
{
    public const int WM_POINTERUPDATE = 0x0245;
    public const int WM_POINTERDOWN = 0x0246;
    public const int WM_POINTERUP = 0x0247;
    public const int WM_POINTERENTER = 0x0249;
    public const int WM_POINTERLEAVE = 0x024A;
    public const int WM_POINTERCAPTURECHANGED = 0x024C;

    public const uint PT_TOUCH = 2;
    public const uint POINTER_FLAG_INCONTACT = 0x00000004;
    public const uint POINTER_FLAG_UP = 0x00040000;
    public const uint TOUCH_MASK_CONTACTAREA = 0x00000004;

    private const int ERROR_NO_DATA = 232;      // 帧信息已被消费/过期 → 回退单点 API
    private const int ERROR_ACCESS_DENIED = 5;  // hook 挂错窗口（调用线程不拥有该窗口）

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public bool IsValid => Right > Left && Bottom > Top;
    }

    /// <summary>字段顺序照 winuser.h 的 POINTER_INFO 原样搬（16 个字段 + 尾部 4 字节）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct POINTER_INFO
    {
        public uint pointerType;
        public uint pointerId;
        public uint frameId;
        public uint pointerFlags;
        public IntPtr sourceDevice;
        public IntPtr hwndTarget;
        public POINT ptPixelLocation;
        public POINT ptHimetricLocation;
        public POINT ptPixelLocationRaw;
        public POINT ptHimetricLocationRaw;
        public uint dwTime;
        public uint historyCount;
        public int InputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINTER_TOUCH_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint touchFlags;
        public uint touchMask;
        public RECT rcContact;
        public RECT rcContactRaw;
        public uint orientation;
        public uint pressure;
    }

    /// <summary>解出来的一根触点。Xpx/Ypx 是屏幕物理像素。</summary>
    public sealed record PointerContact(uint PointerId, int Xpx, int Ypx, bool InContact,
        uint Pressure1024, RECT Contact, uint Orientation, uint FrameId, uint HistoryCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetPointerType(uint pointerId, out uint pointerType);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetPointerTouchInfo(uint pointerId, ref POINTER_TOUCH_INFO touchInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetPointerFrameTouchInfo(uint pointerId, ref uint pointerCount,
        [Out] POINTER_TOUCH_INFO[]? touchInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetPointerFrameTouchInfoHistory(uint pointerId, ref uint entriesCount,
        ref uint pointerCount, [Out] POINTER_TOUCH_INFO[] touchInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SkipPointerFrameMessages(uint pointerId);

    /// <summary>WM_POINTER 的 wParam 低 16 位是 pointerId（高位是 flags）。</summary>
    public static uint GetPointerId(IntPtr wParam) => (uint)(wParam.ToInt64() & 0xFFFF);

    /// <summary>丢掉本帧剩余消息，避免同一帧被反复派发。只在 POINTER/组合模式下调用。</summary>
    public static void SkipFrame(uint pointerId) => SkipPointerFrameMessages(pointerId);

    /// <summary>
    /// 读该 pointerId 所在的**整帧**。isTouch=false 表示这不是触摸（例如鼠标/笔），调用方应忽略。
    /// 失败时 win32Error 会带上 GetLastError（ERROR_ACCESS_DENIED / ERROR_NO_DATA 见类注释）。
    /// </summary>
    public static bool TryReadFrame(uint pointerId, out List<PointerContact> contacts,
        out bool isTouch, out int win32Error)
    {
        contacts = new List<PointerContact>();
        isTouch = false;
        win32Error = 0;

        if (!GetPointerType(pointerId, out uint type) || type != PT_TOUCH)
            return false;
        isTouch = true;

        uint count = 0;
        if (!GetPointerFrameTouchInfo(pointerId, ref count, null))
        {
            int err = Marshal.GetLastWin32Error();
            // 帧信息已被消费/过期时不静默丢样本：退回单点读一次。
            if (err == ERROR_NO_DATA && TryReadSingle(pointerId, out PointerContact single))
            {
                contacts.Add(single);
                return true;
            }
            win32Error = err;
            return false;
        }

        if (count == 0)
            return true;

        var buf = new POINTER_TOUCH_INFO[count];
        uint filled = count;
        if (!GetPointerFrameTouchInfo(pointerId, ref filled, buf))
        {
            win32Error = Marshal.GetLastWin32Error();
            return false;
        }

        for (uint i = 0; i < filled && i < buf.Length; i++)
            contacts.Add(ToContact(buf[i]));
        return true;
    }

    /// <summary>读帧历史（一条消息可能带回多帧采样）。返回的顺序就是 Windows 给出的顺序，调用方按序回放。</summary>
    public static bool TryReadFrameHistory(uint pointerId, out List<List<PointerContact>> frames, out int win32Error)
    {
        frames = new List<List<PointerContact>>();
        win32Error = 0;

        uint entries = 8;
        uint perEntry = 16;
        var buf = new POINTER_TOUCH_INFO[entries * perEntry];
        if (!GetPointerFrameTouchInfoHistory(pointerId, ref entries, ref perEntry, buf))
        {
            win32Error = Marshal.GetLastWin32Error();
            return false;
        }

        for (uint e = 0; e < entries; e++)
        {
            var list = new List<PointerContact>();
            for (uint p = 0; p < perEntry; p++)
                list.Add(ToContact(buf[(int)(e * perEntry + p)]));
            frames.Add(list);
        }
        return true;
    }

    private static bool TryReadSingle(uint pointerId, out PointerContact contact)
    {
        contact = default!;
        var info = new POINTER_TOUCH_INFO();
        if (!GetPointerTouchInfo(pointerId, ref info))
            return false;
        contact = ToContact(info);
        return true;
    }

    private static PointerContact ToContact(POINTER_TOUCH_INFO t) => new(
        t.pointerInfo.pointerId,
        t.pointerInfo.ptPixelLocation.X,
        t.pointerInfo.ptPixelLocation.Y,
        (t.pointerInfo.pointerFlags & POINTER_FLAG_INCONTACT) != 0 &&
            (t.pointerInfo.pointerFlags & POINTER_FLAG_UP) == 0,
        t.pressure,
        t.rcContact,
        t.orientation,
        t.pointerInfo.frameId,
        t.pointerInfo.historyCount);

    /// <summary>接触面积是否可用（决定要不要把它写进日志）。</summary>
    public static bool HasContactArea(POINTER_TOUCH_INFO t) =>
        (t.touchMask & TOUCH_MASK_CONTACTAREA) != 0 && t.rcContact.IsValid;
}
