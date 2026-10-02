using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace TouchErase.Helpers;

/// <summary>
/// HID 触摸设备诊断：读取 HID 报告描述符，判断触摸数字化器到底有没有声明
/// "接触尺寸"用法（Digitizer Width 0x48 / Height 0x49）。
/// 主路径用 IOCTL_HID_GET_REPORT_DESCRIPTOR（不依赖注册表），注册表读作为兜底。
/// </summary>
public static class HidDescriptor
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint index, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
        ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiOpenDevRegKey(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
        uint scope, uint hwProfile, uint keyType, uint samDesired);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
        uint property, out uint propertyRegDataType, byte[]? propertyBuffer, uint propertyBufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr device, uint ioControlCode, IntPtr inBuffer, uint inBufferSize,
        byte[] outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegCloseKey(IntPtr hKey);

    private const int DIGCF_PRESENT = 0x02;
    private const int DIGCF_DEVICEINTERFACE = 0x10;
    private const uint DICS_FLAG_GLOBAL = 0x01;
    private const uint DIREG_DEV = 0x01;
    private const uint DIREG_DRV = 0x02;
    private const uint KEY_READ = 0x20019;
    private const uint SPDRP_FRIENDLYNAME = 0x0000000C;
    private const uint SPDRP_DEVICEDESC = 0x00000000;
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x01;
    private const uint FILE_SHARE_WRITE = 0x02;
    private const uint OPEN_EXISTING = 3;
    private const uint IOCTL_HID_GET_REPORT_DESCRIPTOR = 0x000B0190;

    private static readonly Guid GuidClassHid =
        new(0x745a17a0, 0x74d3, 0x11d0, 0xb6, 0xfe, 0x00, 0xa0, 0xc9, 0x0f, 0x57, 0xda);

    // GUID_DEVINTERFACE_HID {4d1e55b2-f16f-11cf-88cb-001111000030}
    private static readonly Guid GuidDevInterfaceHid =
        new(0x4d1e55b2, 0xf16f, 0x11cf, 0x88, 0xcb, 0x00, 0x11, 0x11, 0x00, 0x00, 0x30);

    private static readonly IntPtr InvalidHandle = new(-1);

    public sealed record HidDeviceReport(
        string Name,
        int DescriptorLength,
        bool IsTouchScreen,
        bool HasWidth,
        bool HasHeight,
        string HexDump);

    public static int LastEnumeratedCount { get; private set; }
    public static string LastSource { get; private set; } = "";

    public static List<HidDeviceReport> Enumerate()
    {
        LastEnumeratedCount = 0;
        Diagnostics.Clear();

        // 主路径：HID 设备接口 + IOCTL 读描述符
        List<HidDeviceReport> viaInterface = EnumerateViaInterface();
        if (viaInterface.Count > 0)
        {
            LastSource = "IOCTL_HID_GET_REPORT_DESCRIPTOR";
            return viaInterface;
        }

        // 兜底：类设备 + 注册表
        LastSource = "注册表 ReportDescriptor";
        return EnumerateViaRegistry();
    }

    // ---------- 主路径：接口 + IOCTL ----------

    private static List<HidDeviceReport> EnumerateViaInterface()
    {
        var reports = new List<HidDeviceReport>();

        Guid guid = GuidDevInterfaceHid;
        IntPtr devInfo = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (devInfo == IntPtr.Zero || devInfo == InvalidHandle)
            return reports;

        try
        {
            for (uint i = 0; ; i++)
            {
                var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref guid, i, ref ifData))
                    break;

                LastEnumeratedCount++;

                // 首次调用只用于查询所需缓冲大小：它会返回 false（ERROR_INSUFFICIENT_BUFFER），
                // 这是正常的，只要 required > 0 就继续。
                SetupDiGetDeviceInterfaceDetail(devInfo, ref ifData, IntPtr.Zero, 0, out uint required, IntPtr.Zero);
                if (required == 0)
                {
                    AddDiag($"GetDeviceInterfaceDetail 拿不到长度 err={Marshal.GetLastWin32Error()}");
                    continue;
                }

                IntPtr detail = Marshal.AllocHGlobal((int)required);
                try
                {
                    int cbSize = IntPtr.Size == 8 ? 8 : 6;
                    Marshal.WriteInt32(detail, cbSize);
                    if (!SetupDiGetDeviceInterfaceDetail(devInfo, ref ifData, detail, required, out _, IntPtr.Zero))
                        continue;

                    string path = Marshal.PtrToStringUni(detail + 4) ?? "";
                    if (path.Length == 0)
                        continue;

                    string name = System.IO.Path.GetFileName(path);
                    byte[]? descriptor = ReadDescriptorViaIoctl(path);
                    if (descriptor is null || descriptor.Length == 0)
                        continue;

                    reports.Add(BuildReport(name, descriptor));
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devInfo);
        }

        return reports;
    }

    private static byte[]? ReadDescriptorViaIoctl(string path)
    {
        IntPtr handle = CreateFile(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == InvalidHandle)
        {
            int e1 = Marshal.GetLastWin32Error();
            // 有些设备不允许 GENERIC_READ，用 0 访问再试
            handle = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle == IntPtr.Zero || handle == InvalidHandle)
            {
                int e2 = Marshal.GetLastWin32Error();
                AddDiag($"CreateFile 失败 err={e1}/{e2}  {path}");
                return null;
            }
        }

        try
        {
            uint size = 4096;
            var buffer = new byte[size];
            if (!DeviceIoControl(handle, IOCTL_HID_GET_REPORT_DESCRIPTOR, IntPtr.Zero, 0,
                    buffer, size, out uint returned, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                if (returned > 0 && returned <= 65536)
                {
                    // ERROR_INSUFFICIENT_BUFFER 时按返回的所需长度重试
                    buffer = new byte[returned];
                    if (!DeviceIoControl(handle, IOCTL_HID_GET_REPORT_DESCRIPTOR, IntPtr.Zero, 0,
                            buffer, returned, out uint returned2, IntPtr.Zero))
                    {
                        AddDiag($"DeviceIoControl 重试失败 err={Marshal.GetLastWin32Error()}  {path}");
                        return null;
                    }
                    returned = returned2;
                }
                else
                {
                    AddDiag($"DeviceIoControl 失败 err={err}  {path}");
                    return null;
                }
            }

            if (returned == 0 || returned > buffer.Length)
            {
                AddDiag($"描述符长度异常 returned={returned}  {path}");
                return null;
            }

            var result = new byte[returned];
            Array.Copy(buffer, result, (int)returned);
            return result;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static List<string> Diagnostics { get; } = new();

    private static void AddDiag(string message)
    {
        if (Diagnostics.Count < 40)
            Diagnostics.Add(message);
    }

    // ---------- 兜底：类设备 + 注册表 ----------

    private static List<HidDeviceReport> EnumerateViaRegistry()
    {
        var reports = new List<HidDeviceReport>();

        Guid classGuid = GuidClassHid;
        IntPtr devInfo = SetupDiGetClassDevs(ref classGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
        if (devInfo == IntPtr.Zero || devInfo == InvalidHandle)
            return reports;

        try
        {
            for (uint i = 0; ; i++)
            {
                var did = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiEnumDeviceInfo(devInfo, i, ref did))
                    break;

                LastEnumeratedCount++;

                byte[]? descriptor = ReadDescriptorViaRegistry(devInfo, ref did);
                if (descriptor is null || descriptor.Length == 0)
                    continue;

                string name = GetProperty(devInfo, ref did, SPDRP_FRIENDLYNAME)
                              ?? GetProperty(devInfo, ref did, SPDRP_DEVICEDESC)
                              ?? "(未知)";

                reports.Add(BuildReport(name, descriptor));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devInfo);
        }

        return reports;
    }

    private static byte[]? ReadDescriptorViaRegistry(IntPtr devInfo, ref SP_DEVINFO_DATA did)
    {
        foreach (uint keyType in new[] { DIREG_DEV, DIREG_DRV })
        {
            IntPtr hKey = SetupDiOpenDevRegKey(devInfo, ref did, DICS_FLAG_GLOBAL, 0, keyType, KEY_READ);
            if (hKey == IntPtr.Zero || hKey == InvalidHandle)
                continue;

            try
            {
                using RegistryKey key = RegistryKey.FromHandle(new SafeRegistryHandle(hKey, false));
                if (TryReadFromKey(key, out byte[]? direct))
                    return direct;

                using RegistryKey? sub = key.OpenSubKey("Device Parameters");
                if (sub is not null && TryReadFromKey(sub, out byte[]? fromSub))
                    return fromSub;
            }
            catch
            {
                // 忽略
            }
            finally
            {
                RegCloseKey(hKey);
            }
        }
        return null;
    }

    private static bool TryReadFromKey(RegistryKey key, out byte[]? bytes)
    {
        bytes = null;
        try
        {
            if (key.GetValue("ReportDescriptor") is byte[] v && v.Length > 0)
            {
                bytes = v;
                return true;
            }
        }
        catch
        {
            // 忽略
        }
        return false;
    }

    private static string? GetProperty(IntPtr devInfo, ref SP_DEVINFO_DATA did, uint property)
    {
        byte[] buffer = new byte[512];
        if (!SetupDiGetDeviceRegistryProperty(devInfo, ref did, property, out _, buffer, (uint)buffer.Length, out uint size)
            || size < 2)
            return null;
        int len = (int)Math.Min(size, (uint)buffer.Length) / 2;
        string s = Encoding.Unicode.GetString(buffer, 0, len * 2).TrimEnd('\0');
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    // ---------- 描述符分析 ----------

    private static HidDeviceReport BuildReport(string name, byte[] descriptor) => new(
        name,
        descriptor.Length,
        IsTouchScreen(descriptor),
        ContainsUsage(descriptor, 0x48),
        ContainsUsage(descriptor, 0x49),
        ToHex(descriptor));

    /// <summary>描述符里是否出现 "Usage Page 0x0D(Digitizer) + Usage 0x04(Touch Screen)"。</summary>
    private static bool IsTouchScreen(byte[] d)
    {
        for (int i = 0; i + 3 < d.Length; i++)
            if (d[i] == 0x05 && d[i + 1] == 0x0D && d[i + 2] == 0x09 && d[i + 3] == 0x04)
                return true;
        return false;
    }

    /// <summary>描述符里是否出现 "Usage (0x09, id)"。</summary>
    private static bool ContainsUsage(byte[] d, byte usage)
    {
        for (int i = 0; i + 1 < d.Length; i++)
            if (d[i] == 0x09 && d[i + 1] == usage)
                return true;
        return false;
    }

    private static string ToHex(byte[] d)
    {
        var sb = new StringBuilder(d.Length * 3);
        foreach (byte b in d)
            sb.Append(b.ToString("X2")).Append(' ');
        return sb.ToString().TrimEnd();
    }
}
