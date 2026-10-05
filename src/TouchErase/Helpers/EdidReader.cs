using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace TouchErase.Helpers;

/// <summary>
/// 通过 SetupAPI 读取显示器注册表中的 EDID 块，拿到屏幕真实物理毫米尺寸。
/// 对应方案「一、屏幕物理尺寸来源」：GetDeviceCaps(HORZSIZE/VERTSIZE) 在 Win7 后不可靠，
/// EDID 才是厂商固件里的真实物理尺寸。
/// </summary>
public static class EdidReader
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint index, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiOpenDevRegKey(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
        uint scope, uint hwProfile, uint keyType, uint samDesired);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    private const int DIGCF_PRESENT = 0x02;
    private const uint DICS_FLAG_GLOBAL = 0x01;
    private const uint DIREG_DEV = 0x01;
    private const uint KEY_READ = 0x20019;

    // GUID_DEVCLASS_MONITOR {4d36e96e-e325-11ce-bfc1-08002be10318}
    private static readonly Guid GuidClassMonitor =
        new(0x4d36e96e, 0xe325, 0x11ce, 0xbf, 0xc1, 0x08, 0x00, 0x2b, 0xe1, 0x03, 0x18);

    private static readonly IntPtr InvalidHandle = new(-1);

    /// <summary>
    /// 枚举所有显示器的 EDID 物理宽高（毫米）。宽高藏在 EDID 字节 66/67/68。
    /// 多显示器时"第一个 EDID"未必是主屏，调用方需用分辨率比例去挑选。
    /// </summary>
    public static List<(double widthMm, double heightMm)> GetPhysicalSizes()
    {
        var result = new List<(double, double)>();

        Guid classGuid = GuidClassMonitor;
        IntPtr devInfo = SetupDiGetClassDevs(ref classGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
        if (devInfo == IntPtr.Zero || devInfo == InvalidHandle)
            return result;

        try
        {
            for (uint i = 0; ; i++)
            {
                var did = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiEnumDeviceInfo(devInfo, i, ref did))
                    break;

                IntPtr hKey = SetupDiOpenDevRegKey(devInfo, ref did, DICS_FLAG_GLOBAL, 0, DIREG_DEV, KEY_READ);
                if (hKey == IntPtr.Zero || hKey == InvalidHandle)
                    continue;

                try
                {
                    using RegistryKey key = RegistryKey.FromHandle(new SafeRegistryHandle(hKey, false));
                    if (key.GetValue("EDID") is not byte[] edid || edid.Length < 69)
                        continue;

                    int w = ((edid[68] & 0xF0) << 4) + edid[66];
                    int h = ((edid[68] & 0x0F) << 8) + edid[67];
                    if (w > 0 && h > 0 && !result.Contains((w, h)))
                        result.Add((w, h));
                }
                catch
                {
                    // 某些设备键无权限，跳过
                }
                finally
                {
                    RegCloseKey(hKey);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devInfo);
        }

        return result;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegCloseKey(IntPtr hKey);
}
