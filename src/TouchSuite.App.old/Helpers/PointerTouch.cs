using System.Runtime.InteropServices;

namespace TouchSuite.App.Old.Helpers;

/// <summary>
/// 直接用 WM_POINTER（Windows 8+ 触摸首选通路）读取触摸接触矩形 rcContact，
/// 绕开 WPF 的 WM_TOUCH 通路。用于判断/获取"接触尺寸"。
/// </summary>
public static class PointerTouch
{
    public const int WM_POINTERUPDATE = 0x0245;
    public const int WM_POINTERDOWN = 0x0246;
    public const int WM_POINTERUP = 0x0247;
    public const int WM_POINTERENTER = 0x0249;
    public const int WM_POINTERLEAVE = 0x024A;

    public const uint PT_TOUCH = 2;
    public const uint POINTER_FLAG_INCONTACT = 0x00000004;

    public const uint TOUCH_MASK_CONTACTAREA = 0x00000004;

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

    private const uint PT_PEN = 3;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetPointerType(uint pointerId, out uint pointerType);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetPointerTouchInfo(uint pointerId, ref POINTER_TOUCH_INFO touchInfo);

    [DllImport("user32.dll")]
    public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    /// <summary>WM_POINTER 的 wParam 低 16 位是 pointerId。</summary>
    public static uint GetPointerId(IntPtr wParam) => (uint)(wParam.ToInt64() & 0xFFFF);

    /// <summary>读取该 pointer 的触摸接触信息。返回 false 表示不是触摸或读取失败。</summary>
    public static bool TryRead(uint pointerId, out POINTER_TOUCH_INFO touchInfo, out bool hasContactArea)
    {
        touchInfo = default;
        hasContactArea = false;

        if (!GetPointerType(pointerId, out uint type) || type != PT_TOUCH)
            return false;

        if (!GetPointerTouchInfo(pointerId, ref touchInfo))
            return false;

        hasContactArea = (touchInfo.touchMask & TOUCH_MASK_CONTACTAREA) != 0 && touchInfo.rcContact.IsValid;
        return true;
    }
}
