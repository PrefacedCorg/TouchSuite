using System.Runtime.InteropServices;

namespace TouchSuite.HidDump;

/// <summary>
/// 非 USB 设备（VHF / 虚拟数字化器）取原始报告描述符的唯一现实途径：本仓库驱动
/// （driver/TouchBridgeVhid）提供的 IOCTL_TB_VHID_GET_DESCRIPTOR。
/// Windows 对这类设备不开放任何用户态取描述符的接口（hidclass.h 里没有 GET_REPORT_DESCRIPTOR，
/// 注册表也不缓存），第三方虚拟设备（如 UU 远程）就只能靠上方的能力表。
/// 取回后按「描述符里 Width(0x48) 出现次数 == 本设备声明的槽位数」校验，
/// 避免把本驱动的描述符显示到别的设备上。
/// </summary>
internal static class VhfDescriptor
{
    // CTL_CODE(FILE_DEVICE_UNKNOWN=0x22, 0x801, METHOD_BUFFERED, FILE_ANY_ACCESS) = 0x222004
    private const uint IoctlGetDescriptor = 0x222004;
    private const string DevicePath = @"\\.\TouchBridgeVhid";
    private const int MaxDescriptor = 2048;

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareReadWrite = 0x1 | 0x2;
    private const uint OpenExisting = 3;
    private static readonly IntPtr InvalidHandle = new(-1);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr h, uint code, byte[]? inBuf, uint inSize, byte[] outBuf, uint outSize, out uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    /// <summary>取驱动正在用的报告描述符；校验不过或取不到时返回 null 并给出原因。</summary>
    public static byte[]? TryGet(int expectedSlots, out string reason)
    {
        reason = "";

        IntPtr h = CreateFileW(DevicePath, GenericRead, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (h == InvalidHandle)
            h = CreateFileW(DevicePath, GenericRead | GenericWrite, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (h == InvalidHandle)
        {
            int err = Marshal.GetLastWin32Error();
            reason = err switch
            {
                2 => @"本机没装（或没启动）TouchBridge 虚拟触摸屏驱动 —— \\.\TouchBridgeVhid 不存在",
                5 => @"打开 \\.\TouchBridgeVhid 被拒（Win32 5）：驱动装的是旧版本（只给管理员读），以管理员身份运行本工具，或重装 driver/ 里的新驱动（新驱动放开了只读）",
                _ => $@"打不开 \\.\TouchBridgeVhid（Win32 {err}）",
            };
            return null;
        }

        try
        {
            var buf = new byte[MaxDescriptor];
            if (!DeviceIoControl(h, IoctlGetDescriptor, null, 0, buf, (uint)buf.Length, out uint returned, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                reason = err is 1 or 50 or 87   // ERROR_INVALID_FUNCTION / NOT_SUPPORTED / INVALID_PARAMETER
                    ? "已安装的驱动版本不支持取描述符（旧驱动），重装 driver/ 里的新驱动即可"
                    : $"驱动取描述符失败（Win32 {err}）";
                return null;
            }
            if (returned == 0 || returned > MaxDescriptor)
            {
                reason = $"驱动返回的描述符长度异常（{returned}）";
                return null;
            }

            byte[] desc = Compat.Head(buf, (int)returned);
            int widthUsages = CountWidthUsages(desc);
            if (expectedSlots > 0 && widthUsages != expectedSlots)
            {
                reason = $"驱动描述符里的 Width 项有 {widthUsages} 个，与本设备声明的槽位 {expectedSlots} 个不符 → 判定不是这块设备的描述符，不显示";
                return null;
            }
            return desc;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>数一数描述符里 Usage(Width, 0x0D:0x48) 出现几次（驱动里每个手指块一个）。</summary>
    private static int CountWidthUsages(byte[] d)
    {
        int n = 0;
        for (int i = 0; i + 1 < d.Length; i++)
        {
            if (d[i] == 0x09 && d[i + 1] == 0x48)
                n++;
        }
        return n;
    }
}
