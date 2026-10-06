using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace TouchSuite.HidDump;

/// <summary>
/// 取 HID 报告描述符原始字节（USBView 同款 IOCTL_USB_GET_DESCRIPTOR_FROM_NODE_CONNECTION，类型 0x22）。
/// <para>主路径（定向）：CM 按 VID/PID 找 USB 设备实例 → 取 CM_DRP_ADDRESS 端口号 → CM_Get_Parent 拿父集线器实例 →
/// 按实例过滤取该集线器接口路径 → 打开 → 按该端口发 IOCTL。不依赖"枚举全部集线器"，xHCI 根集线器/外接集线器漏枚举也能命中。</para>
/// <para>兜底：遍历所有集线器端口按 VID/PID 匹配（含子集线器递归）。</para>
/// 虚拟设备（VHF/VMulti 等非 USB）返回 null。
/// IOCTL 代码值取自 usbiodef.h：FILE_DEVICE_USB=FILE_DEVICE_UNKNOWN(0x22)，NODE_INFORMATION=258、
/// DESCRIPTOR_FROM_NODE_CONNECTION=260、CONNECTION_NAME=261、CONNECTION_INFORMATION_EX=274。
/// </summary>
internal static class UsbDescriptor
{
    // CTL_CODE(0x22, function, METHOD_BUFFERED, FILE_ANY_ACCESS) = 0x220000 | function << 2
    private const uint IOCTL_USB_GET_NODE_INFORMATION = 0x220408;                  // 258
    private const uint IOCTL_USB_GET_DESCRIPTOR_FROM_NODE_CONNECTION = 0x220410;   // 260
    private const uint IOCTL_USB_GET_NODE_CONNECTION_NAME = 0x220414;              // 261
    private const uint IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX = 0x220448;    // 274

    private static readonly Guid HubInterfaceGuid = new("f18a0e88-c30c-11d0-8815-00a0c906bed8");
    private const int CrSuccess = 0;
    private const uint CmGetDeviceInterfaceListPresent = 0;
    private const uint CmGetIdListFilterPresent = 0x00000100;
    private const uint CmDrpEnumeratorName = 0x00000017;   // cfgmgr32.h
    private const uint CmDrpAddress = 0x0000001D;          // cfgmgr32.h

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr h, uint code, byte[]? inBuffer, uint inSize, byte[] outBuffer, uint outSize, out uint returned, IntPtr overlapped);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Parent(out uint parentDevInst, uint devInst, uint flags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_IDW(uint devInst, IntPtr buffer, uint bufferLen, uint flags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List_SizeW(out uint length, string filter, uint flags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_ListW(string filter, IntPtr buffer, uint bufferLen, uint flags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_Registry_PropertyW(uint devInst, uint property, out uint regType, IntPtr buffer, ref uint length, uint flags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid interfaceClassGuid, IntPtr deviceId, uint flags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_ListW(ref Guid interfaceClassGuid, IntPtr deviceId, IntPtr buffer, uint bufferLength, uint flags);

    /// <summary>上一次取描述符的失败原因（供界面显示）。</summary>
    public static string LastReason { get; private set; } = "";

    /// <summary>取 HID 报告描述符原始字节；失败/非 USB 设备返回 null。</summary>
    public static byte[]? TryGetHidReportDescriptor(string hidPath)
    {
        LastReason = "";
        Match m = Regex.Match(hidPath, @"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})(?:&MI_(\d{1,2}))?", RegexOptions.IgnoreCase);
        if (!m.Success)
        {
            LastReason = "不是 USB 枚举出来的设备（VHF / VMulti / VIRTUAL_DIGITIZER 等），没有 USB 父集线器。";
            return null;
        }

        ushort vid = Convert.ToUInt16(m.Groups[1].Value, 16);
        ushort pid = Convert.ToUInt16(m.Groups[2].Value, 16);
        int iface = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;

        byte[]? result = TryViaDeviceParent(vid, pid, iface) ?? TryViaHubWalk(vid, pid, iface);
        if (result is null && LastReason.Length == 0)
            LastReason = "所有集线器端口都没匹配到该设备，且集线器查询不可用。";
        return result;
    }

    // ================= 诊断（调试用，可随时删） =================

    internal static List<string> DebugTrace(ushort vid, ushort pid, int iface)
    {
        var log = new List<string>();
        List<string> all = EnumDeviceIds();
        foreach (string prefix in new[] { $"USB\\VID_{vid:X4}&PID_{pid:X4}", $"USB\\VID_{vid:x4}&PID_{pid:x4}" })
        {
            List<string> ids = all.Where(s => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            log.Add($"前缀 [{prefix}] → {ids.Count} 个实例：{string.Join(" | ", ids)}");
            foreach (string instanceId in ids)
            {
                int rc = CM_Locate_DevNodeW(out uint devInst, instanceId, 0);
                log.Add($"  {instanceId}：Locate rc={rc} devInst={devInst}");
                if (rc != CrSuccess)
                    continue;
                log.Add($"    枚举器 = {GetStringProperty(devInst, CmDrpEnumeratorName) ?? "(null)"}");
                uint? addr = GetUInt32Property(devInst, CmDrpAddress);
                log.Add($"    Address = {(addr is uint a ? a.ToString() : "(取不到)")}");
                int rcParent = CM_Get_Parent(out uint hubInst, devInst, 0);
                log.Add($"    Parent rc={rcParent} hubInst={hubInst}");
                if (rcParent != CrSuccess)
                    continue;
                string hubId = GetDeviceId(hubInst);
                log.Add($"    父集线器实例 = {hubId}");
                string? hubPath = HubInterfacePath(hubId);
                log.Add($"    父集线器接口 = {hubPath ?? "(取不到)"}");
                if (hubPath is null)
                    continue;
                IntPtr hub = HidApi.Open(hubPath, out string err);
                log.Add($"    打开集线器：{(HidApi.Ok(hub) ? "OK" : "失败 " + err)}");
                if (!HidApi.Ok(hub))
                    continue;
                try
                {
                    if (addr is uint p)
                        log.Add($"    端口 {p} 匹配 = {PortMatches(hub, (int)p, vid, pid)}");
                    byte[]? d = addr is uint p2 ? FetchReportDescriptor(hub, (int)p2, iface) : null;
                    log.Add($"    取描述符 = {(d is null ? "失败" : d.Length + " 字节")}");
                }
                finally
                {
                    HidApi.Close(hub);
                }
            }
        }
        return log;
    }

    // ================= 主路径：从设备往上找父集线器端口 =================

    private static byte[]? TryViaDeviceParent(ushort vid, ushort pid, int iface)
    {
        // 注意：CM_Get_Device_ID_ListW 的 filter 是多字符串（需双空结尾），用 string 参数会被当成单串而失效，
        // 所以这里拿全量列表后自己在代码里按前缀过滤。
        foreach (string instanceId in EnumDeviceIds())
        {
            if (!instanceId.StartsWith($"USB\\VID_{vid:X4}&PID_{pid:X4}", StringComparison.OrdinalIgnoreCase))
                continue;
            {
                if (CM_Locate_DevNodeW(out uint devInst, instanceId, 0) != CrSuccess)
                    continue;
                if (!string.Equals(GetStringProperty(devInst, CmDrpEnumeratorName), "USB", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (GetUInt32Property(devInst, CmDrpAddress) is not uint port || port == 0)
                    continue;   // 没有 Address 的不是挂在集线器端口上的设备（复合子级 / 根集线器）
                if (CM_Get_Parent(out uint hubInst, devInst, 0) != CrSuccess)
                    continue;

                string hubId = GetDeviceId(hubInst);
                if (hubId.Length == 0)
                    continue;
                string? hubPath = HubInterfacePath(hubId);
                if (hubPath is null)
                    continue;

                IntPtr hub = HidApi.Open(hubPath, out _);
                if (!HidApi.Ok(hub))
                {
                    LastReason = $"设备 {instanceId} 在端口 {port}，父集线器 {hubId} 打不开。";
                    continue;
                }
                try
                {
                    bool portOk = PortMatches(hub, (int)port, vid, pid);
                    byte[]? desc = FetchReportDescriptor(hub, (int)port, iface, out string fetchNote);
                    if (desc is not null)
                        return desc;

                    LastReason = portOk
                        ? $"设备 {instanceId} 在 {hubId} 端口 {port}（端口校验通过），但描述符请求没取到像样的内容 → {fetchNote}"
                        : $"设备 {instanceId} 挂在 {hubId} 端口 {port}，端口查询不支持（虚拟/重定向总线）；转发描述符也未成功 → {fetchNote}";
                    Log.Warn(LastReason);
                }
                finally
                {
                    HidApi.Close(hub);
                }
            }
        }
        return null;
    }

    /// <summary>取全部设备实例 ID（filter 为 NULL；过滤交给调用方做前缀匹配）。</summary>
    private static List<string> EnumDeviceIds()
    {
        var list = new List<string>();
        if (CM_Get_Device_ID_List_SizeW(out uint len, null!, CmGetIdListFilterPresent) != CrSuccess || len == 0)
            return list;

        IntPtr buf = Marshal.AllocHGlobal((int)(len * 2));
        try
        {
            if (CM_Get_Device_ID_ListW(null!, buf, len, CmGetIdListFilterPresent) != CrSuccess)
                return list;
            // 必须按长度读：PtrToStringUni 无长度版只给第一条
            string all = Marshal.PtrToStringUni(buf, (int)len) ?? "";
            foreach (string s in all.Split('\0'))
            {
                if (s.Length > 0)
                    list.Add(s);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
        return list;
    }

    private static string GetDeviceId(uint devInst)
    {
        const int chars = 512;
        IntPtr buf = Marshal.AllocHGlobal(chars * 2);
        try
        {
            return CM_Get_Device_IDW(devInst, buf, chars, 0) == CrSuccess
                ? (Marshal.PtrToStringUni(buf) ?? "")
                : "";
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>按实例名过滤取该集线器的接口路径（deviceId 传多字符串，需双空结尾）。</summary>
    private static string? HubInterfacePath(string hubInstanceId)
    {
        IntPtr idBuf = Marshal.AllocHGlobal((hubInstanceId.Length + 2) * 2);
        try
        {
            byte[] raw = Encoding.Unicode.GetBytes(hubInstanceId + "\0\0");
            Marshal.Copy(raw, 0, idBuf, raw.Length);

            Guid guid = HubInterfaceGuid;
            if (CM_Get_Device_Interface_List_SizeW(out uint len, ref guid, idBuf, CmGetDeviceInterfaceListPresent) != CrSuccess || len == 0)
                return null;

            IntPtr buf = Marshal.AllocHGlobal((int)(len * 2));
            try
            {
                if (CM_Get_Device_Interface_ListW(ref guid, idBuf, buf, len, CmGetDeviceInterfaceListPresent) != CrSuccess)
                    return null;
                string all = Marshal.PtrToStringUni(buf, (int)len) ?? "";
                string first = all.Split('\0').FirstOrDefault(p => p.Length > 0) ?? "";
                return first.Length > 0 ? first : null;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(idBuf);
        }
    }

    /// <summary>
    /// 粗校验「像不像一份 HID 报告描述符」：以 End Collection(0xC0) 结尾，或含数字化器/触摸屏顶层集合(05 0D 09 04)。
    /// 虚拟/重定向集线器不支持端口查询时，只能靠内容自证是这块设备的描述符。
    /// </summary>
    private static bool LooksLikeReportDescriptor(byte[] d)
    {
        if (d.Length < 6 || d.Length > 4096)
            return false;
        if (d[d.Length - 1] == 0xC0)
            return true;
        for (int i = 0; i + 3 < d.Length; i++)
        {
            if (d[i] == 0x05 && d[i + 1] == 0x0D && d[i + 2] == 0x09 && d[i + 3] == 0x04)
                return true;
        }
        return false;
    }

    private static bool PortMatches(IntPtr hub, int connectionIndex, ushort vid, ushort pid)    {
        var info = new byte[256];
        BitConverter.GetBytes(connectionIndex).CopyTo(info, 0);
        if (!DeviceIoControl(hub, IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX, info, 4, info, (uint)info.Length, out _, IntPtr.Zero))
            return false;
        // USB_DEVICE_DESCRIPTOR.idVendor@12、idProduct@14
        return BitConverter.ToUInt16(info, 12) == vid && BitConverter.ToUInt16(info, 14) == pid;
    }

    private static string? GetStringProperty(uint devInst, uint property)
    {
        uint len = 512;
        IntPtr buf = Marshal.AllocHGlobal(1024);
        try
        {
            return CM_Get_DevNode_Registry_PropertyW(devInst, property, out _, buf, ref len, 0) == CrSuccess
                ? (Marshal.PtrToStringUni(buf) ?? "")
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static uint? GetUInt32Property(uint devInst, uint property)
    {
        uint len = 4;
        IntPtr buf = Marshal.AllocHGlobal(4);
        try
        {
            if (CM_Get_DevNode_Registry_PropertyW(devInst, property, out _, buf, ref len, 0) != CrSuccess || len < 4)
                return null;
            return (uint)Marshal.ReadInt32(buf);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    // ================= 兜底：遍历集线器端口 =================

    private static byte[]? TryViaHubWalk(ushort vid, ushort pid, int iface)
    {
        foreach (string hubPath in EnumerateHubPaths())
        {
            IntPtr hub = HidApi.Open(hubPath, out _);
            if (!HidApi.Ok(hub))
                continue;
            try
            {
                byte[]? result = SearchHub(hub, vid, pid, iface, depth: 0);
                if (result is not null)
                    return result;
            }
            finally
            {
                HidApi.Close(hub);
            }
        }
        return null;
    }

    internal static List<string> EnumerateHubPaths()
    {
        var paths = new List<string>();
        Guid guid = HubInterfaceGuid;
        uint len = 0;
        if (CM_Get_Device_Interface_List_SizeW(out len, ref guid, IntPtr.Zero, CmGetDeviceInterfaceListPresent) != CrSuccess || len == 0)
            return paths;

        IntPtr buf = Marshal.AllocHGlobal((int)(len * 2));
        try
        {
            if (CM_Get_Device_Interface_ListW(ref guid, IntPtr.Zero, buf, len, CmGetDeviceInterfaceListPresent) != CrSuccess)
                return paths;

            // 注意：必须按长度读（PtrToStringUni 无长度版只给第一个字符串，多集线器会漏）
            string all = Marshal.PtrToStringUni(buf, (int)len) ?? "";
            foreach (string p in all.Split('\0'))
            {
                if (p.Length > 0)
                    paths.Add(p);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
        return paths;
    }

    internal static byte[]? SearchHub(IntPtr hub, ushort vid, ushort pid, int iface, int depth)
    {
        if (depth > 5)
            return null;

        // USB_NODE_INFORMATION：NodeType@0（0=UsbHub），HubInformation.HubDescriptor.bNumberOfPorts@6
        var node = new byte[512];
        if (!DeviceIoControl(hub, IOCTL_USB_GET_NODE_INFORMATION, null, 0, node, (uint)node.Length, out _, IntPtr.Zero))
            return null;
        if (BitConverter.ToInt32(node, 0) != 0)
            return null;
        int portCount = node[6];

        for (int idx = 1; idx <= portCount; idx++)
        {
            var info = new byte[256];
            BitConverter.GetBytes(idx).CopyTo(info, 0);
            if (!DeviceIoControl(hub, IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX, info, 4, info, (uint)info.Length, out _, IntPtr.Zero))
                continue;

            bool isHub = info[24] != 0;
            if (isHub)
            {
                string? child = GetChildHubPath(hub, idx);
                if (child is null)
                    continue;
                IntPtr childHub = HidApi.Open(child, out _);
                if (!HidApi.Ok(childHub))
                    continue;
                try
                {
                    byte[]? found = SearchHub(childHub, vid, pid, iface, depth + 1);
                    if (found is not null)
                        return found;
                }
                finally
                {
                    HidApi.Close(childHub);
                }
            }
            else if (BitConverter.ToUInt16(info, 12) == vid && BitConverter.ToUInt16(info, 14) == pid)
            {
                return FetchReportDescriptor(hub, idx, iface);
            }
        }
        return null;
    }

    internal static string? GetChildHubPath(IntPtr hub, int connectionIndex)
    {
        // USB_NODE_CONNECTION_NAME：ConnectionIndex@0（入）、ActualLength@4、NodeName@8（出）
        var inBuf = new byte[4];
        BitConverter.GetBytes(connectionIndex).CopyTo(inBuf, 0);
        var outBuf = new byte[2048];
        if (!DeviceIoControl(hub, IOCTL_USB_GET_NODE_CONNECTION_NAME, inBuf, (uint)inBuf.Length, outBuf, (uint)outBuf.Length, out uint returned, IntPtr.Zero) || returned <= 8)
            return null;

        int charCount = (int)(returned - 8) / 2;
        string name = Encoding.Unicode.GetString(outBuf, 8, charCount).TrimEnd('\0');
        return name.Length > 0 ? name : null;
    }

    internal static byte[]? FetchReportDescriptor(IntPtr hub, int connectionIndex, int iface)
        => FetchReportDescriptor(hub, connectionIndex, iface, out _);

    /// <summary>
    /// 取报告描述符：把「接口号 × 接收者类型」都试一遍（不同驱动/组合设备认的参数不一样），
    /// 用 LooksLikeReportDescriptor 判"像不像"，并返回尝试过程摘要供日志/界面显示。
    /// </summary>
    internal static byte[]? FetchReportDescriptor(IntPtr hub, int connectionIndex, int iface, out string note)
    {
        var tried = new List<string>();
        foreach (ushort wIndex in new ushort[] { (ushort)iface, 0, 1, 2 }.Distinct())
        {
            foreach (byte bm in new byte[] { 0x81, 0x80 })   // 0x81=接口接收者（规范用法），0x80 兜底
            {
                byte[]? d = FetchDescriptor(hub, connectionIndex, bm, 0x22, 0, wIndex, 1024);
                if (d is null)
                {
                    tried.Add($"bm0x{bm:X2}/索引{wIndex}=IOCTL失败");
                    continue;
                }
                if (LooksLikeReportDescriptor(d))
                {
                    note = $"bm=0x{bm:X2} wIndex={wIndex}";
                    return d;
                }
                tried.Add($"bm0x{bm:X2}/索引{wIndex}={d.Length}字节 首0x{d[0]:X2} 末0x{d[d.Length - 1]:X2}"
                          + $"\n{HexLines(d)}");
            }
        }
        note = string.Join("；", tried);
        return null;
    }

    private static string HexLines(byte[] d)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < d.Length; i += 16)
        {
            sb.Append($"      {i:X4}: ");
            for (int j = i; j < Math.Min(i + 16, d.Length); j++)
                sb.Append(d[j].ToString("X2")).Append(' ');
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>取设备/配置等标准描述符（虚拟/重定向总线通常只转发这几类）。</summary>
    internal static byte[]? FetchDescriptor(IntPtr hub, int connectionIndex,
        byte bmRequest, ushort descriptorType, ushort descriptorIndex, ushort wIndex, ushort wLength)
    {
        // USB_DESCRIPTOR_REQUEST：ConnectionIndex@0 + SetupPacket@4（bmRequest/bRequest/wValue/wIndex/wLength）
        var req = new byte[12];
        BitConverter.GetBytes(connectionIndex).CopyTo(req, 0);
        req[4] = bmRequest;
        req[5] = 0x06;   // bRequest：GET_DESCRIPTOR
        BitConverter.GetBytes((ushort)((descriptorType << 8) | descriptorIndex)).CopyTo(req, 6);   // wValue
        BitConverter.GetBytes(wIndex).CopyTo(req, 8);
        BitConverter.GetBytes(wLength).CopyTo(req, 10);

        var outBuf = new byte[12 + wLength];
        if (!DeviceIoControl(hub, IOCTL_USB_GET_DESCRIPTOR_FROM_NODE_CONNECTION, req, (uint)req.Length, outBuf, (uint)outBuf.Length, out uint returned, IntPtr.Zero))
            return null;
        if (returned <= 12)
            return null;

        byte[] data = new byte[returned - 12];
        Array.Copy(outBuf, 12, data, 0, data.Length);
        return data;
    }

    /// <summary>
    /// 拿不到报告描述符时的降级信息：读设备/配置描述符，把 VID/PID、接口数、以及配置描述符里
    /// HID 描述符声明的「报告描述符长度」摘出来（重定向总线一般会转发这两个）。
    /// 返回 null 表示连这些也读不到。
    /// </summary>
    public static string? TryDescribeStandard(string hidPath)
    {
        Match m = Regex.Match(hidPath, @"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase);
        if (!m.Success)
            return null;
        ushort vid = Convert.ToUInt16(m.Groups[1].Value, 16);
        ushort pid = Convert.ToUInt16(m.Groups[2].Value, 16);

        foreach (string instanceId in EnumDeviceIds())
        {
            if (!instanceId.StartsWith($"USB\\VID_{vid:X4}&PID_{pid:X4}", StringComparison.OrdinalIgnoreCase)
                && !instanceId.StartsWith($"USB\\VID_{vid:x4}&PID_{pid:x4}", StringComparison.OrdinalIgnoreCase))
                continue;
            if (CM_Locate_DevNodeW(out uint devInst, instanceId, 0) != CrSuccess)
                continue;
            if (GetUInt32Property(devInst, CmDrpAddress) is not uint port || port == 0)
                continue;
            if (CM_Get_Parent(out uint hubInst, devInst, 0) != CrSuccess)
                continue;
            string hubId = GetDeviceId(hubInst);
            string? hubPath = hubId.Length == 0 ? null : HubInterfacePath(hubId);
            if (hubPath is null)
                continue;

            IntPtr hub = HidApi.Open(hubPath, out _);
            if (!HidApi.Ok(hub))
                continue;
            try
            {
                byte[]? dev = FetchDescriptor(hub, (int)port, 0x80, 0x01, 0, 0, 18);
                byte[]? cfg = FetchDescriptor(hub, (int)port, 0x80, 0x02, 0, 0, 512);
                if (cfg is null)
                    continue;

                var sb = new System.Text.StringBuilder();
                if (dev is { Length: >= 12 })
                    sb.Append($"设备描述符：VID_{BitConverter.ToUInt16(dev, 8):X4}&PID_{BitConverter.ToUInt16(dev, 10):X4} USB{BitConverter.ToUInt16(dev, 2):X4} {dev[17]} 个配置；");
                if (cfg.Length >= 9)
                    sb.Append($"配置描述符：总长 {BitConverter.ToUInt16(cfg, 2)} 字节，{cfg[4]} 个接口；");

                // 在配置描述符里逐个找 HID 描述符(0x21)：它声明报告描述符长度
                int off = 0;
                int n = 1;
                while (off + 2 <= cfg.Length)
                {
                    int len = cfg[off];
                    byte type = cfg[off + 1];
                    if (len == 0)
                        break;
                    if (type == 0x21 && off + 9 <= cfg.Length)
                    {
                        int rdLen = cfg[off + 7] | (cfg[off + 8] << 8);
                        sb.Append($"HID 描述符#{n}：报告描述符长度 = {rdLen} 字节（bcdHID 0x{BitConverter.ToUInt16(cfg, off + 2):X4}，国家码 {cfg[off + 4]}）；");
                        n++;
                    }
                    off += len;
                }
                return sb.Length > 0 ? sb.ToString().TrimEnd('；') : null;
            }
            finally
            {
                HidApi.Close(hub);
            }
        }
        return null;
    }
}
