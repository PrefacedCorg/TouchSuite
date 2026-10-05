using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace TouchSuite.App;

/// <summary>EDID 给出的物理尺寸（毫米）。</summary>
public sealed record EdidSize(int WidthMm, int HeightMm);

/// <summary>物理尺寸推算方式。</summary>
public enum SizeMode
{
    /// <summary>横竖都按 EDID（各按各）。</summary>
    UseEdid,
    /// <summary>只信宽度，高度按方形像素用宽度推（按宽推竖）。</summary>
    TrustWidth,
    /// <summary>只信高度，宽度按方形像素用高度推（按竖推宽）。</summary>
    TrustHeight,
}

/// <summary>读取 EDID 物理尺寸 + 由分辨率推导 mm/像素。从零实现，不依赖主程序。</summary>
public static class EdidReader
{
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

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

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegCloseKey(IntPtr hKey);

    private const int DIGCF_PRESENT = 0x02;
    private const uint DICS_FLAG_GLOBAL = 0x01;
    private const uint DIREG_DEV = 0x01;
    private const uint KEY_READ = 0x20019;

    // GUID_DEVCLASS_MONITOR {4d36e96e-e325-11ce-bfc1-08002be10318}
    private static readonly Guid GuidClassMonitor =
        new(0x4d36e96e, 0xe325, 0x11ce, 0xbf, 0xc1, 0x08, 0x00, 0x2b, 0xe1, 0x03, 0x18);

    private static readonly IntPtr InvalidHandle = new(-1);

    /// <summary>主屏分辨率（物理像素）。</summary>
    public static (int ResX, int ResY) PrimaryPixels()
    {
        int w = GetSystemMetrics(0);  // SM_CXSCREEN
        int h = GetSystemMetrics(1);  // SM_CYSCREEN
        if (w <= 0) w = 1920;
        if (h <= 0) h = 1080;
        return (w, h);
    }

    /// <summary>
    /// 只枚举**当前在用（present）**的显示器 EDID 物理尺寸（mm）。
    /// 注意：不能直接扫注册表 Enum\DISPLAY 下的所有键——那里含已拔除/不活动的显示器，会挑错。
    /// </summary>
    public static List<EdidSize> ReadAll()
    {
        var list = new List<EdidSize>();

        Guid classGuid = GuidClassMonitor;
        IntPtr devInfo = SetupDiGetClassDevs(ref classGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
        if (devInfo == IntPtr.Zero || devInfo == InvalidHandle)
            return list;

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

                    // 详细时序描述符：66=宽(mm)低字节, 67=高(mm)低字节,
                    // 68 高 4 位=宽高 4 位、低 4 位=高高 4 位
                    int wMm = ((edid[68] & 0xF0) << 4) | edid[66];
                    int hMm = ((edid[68] & 0x0F) << 8) | edid[67];

                    if (wMm <= 0 || hMm <= 0)   // 退化到基本显示参数里的厘米值
                    {
                        wMm = edid[21] * 10;
                        hMm = edid[22] * 10;
                    }

                    if (wMm >= 50 && hMm >= 50 && !list.Contains(new EdidSize(wMm, hMm)))
                        list.Add(new EdidSize(wMm, hMm));
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

        return list;
    }

    /// <summary>多块 EDID 时，按主屏分辨率比例挑最匹配的一块。</summary>
    public static EdidSize? PickBest(List<EdidSize> all, int resX, int resY)
    {
        if (all.Count == 0)
            return null;
        double want = (double)resX / resY;
        return all.OrderBy(e => Math.Abs((double)e.WidthMm / e.HeightMm - want)).First();
    }
}

/// <summary>一次屏幕校准结果（物理尺寸 + 推算方式 + 每像素毫米）。</summary>
public sealed class ScreenCalibration
{
    public EdidSize Edid { get; }
    public int ResX { get; }
    public int ResY { get; }
    public SizeMode Mode { get; }
    public double MmPerPxX { get; }
    public double MmPerPxY { get; }

    public double ScreenWidthMm => MmPerPxX * ResX;
    public double ScreenHeightMm => MmPerPxY * ResY;

    public string ModeName => Mode switch
    {
        SizeMode.TrustWidth => "按宽推竖（方形像素）",
        SizeMode.TrustHeight => "按竖推宽（方形像素）",
        _ => "各按各（EDID 原样）",
    };

    public ScreenCalibration(EdidSize edid, int resX, int resY, SizeMode mode)
    {
        Edid = edid;
        ResX = resX;
        ResY = resY;
        Mode = mode;

        double wx = (double)edid.WidthMm / resX;
        double wy = (double)edid.HeightMm / resY;

        switch (mode)
        {
            case SizeMode.TrustWidth:
                MmPerPxX = wx;
                MmPerPxY = wx;
                break;
            case SizeMode.TrustHeight:
                MmPerPxY = wy;
                MmPerPxX = wy;
                break;
            default:
                MmPerPxX = wx;
                MmPerPxY = wy;
                break;
        }
    }
}
