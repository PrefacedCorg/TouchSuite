using System.Runtime.InteropServices;

namespace TouchSuite.HidDump;

/// <summary>
/// RawInput 注册 + WM_INPUT 报文解包（参考 TouchSuite.App/HidReader.cs 的解析方式）。
/// 用 INPUTSINK 注册后窗口不在前台也能收；不打开设备，不受句柄独占影响。
/// </summary>
internal static class RawInput
{
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const int RIM_TYPEHID = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE { public ushort usUsagePage; public ushort usUsage; public uint dwFlags; public IntPtr hwndTarget; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER { public uint dwType; public uint dwSize; public IntPtr hDevice; public IntPtr wParam; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint numDevices, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    /// <summary>注册指定顶层集合的原始输入（后台也收）。</summary>
    public static bool Register(IntPtr hwnd, ushort usagePage, ushort usage)
    {
        var devices = new[]
        {
            new RAWINPUTDEVICE { usUsagePage = usagePage, usUsage = usage, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd },
        };
        return RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    /// <summary>解 WM_INPUT：只处理 HID 类型；返回设备句柄与全部子报文（多指 = 多份）。</summary>
    public static bool TryGetHidData(IntPtr lParam, out IntPtr hDevice, out byte[][] reports)
    {
        hDevice = IntPtr.Zero;
        reports = Array.Empty<byte[]>();

        uint header = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        uint size = 0;
        GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, header);
        if (size == 0)
            return false;

        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, RID_INPUT, buf, ref size, header) != size)
                return false;
            if (Marshal.ReadInt32(buf, 0) != RIM_TYPEHID)
                return false;

            hDevice = Marshal.ReadIntPtr(buf, 8);
            int sizeHid = Marshal.ReadInt32(buf, (int)header);
            int count = Marshal.ReadInt32(buf, (int)header + 4);
            if (sizeHid <= 0 || count <= 0)
                return false;

            reports = new byte[count][];
            IntPtr data = buf + (int)header + 8;
            for (int i = 0; i < count; i++)
            {
                var report = new byte[sizeHid];
                Marshal.Copy(data + i * sizeHid, report, 0, sizeHid);
                reports[i] = report;
            }
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }
}
