using System.Text;

namespace TouchSuite.HidDump;

/// <summary>
/// HID 报告描述符逐项解析（短项 + 长项兜底）。
/// 解析规则：short item 首字节 bit0-1=数据长度(3→4)，bit2-3=类型(0 Main/1 Global/2 Local)，bit4-7=标签。
/// Kind：0 普通；1 Report Count / Report Size（用户关注点，高亮）；2 Report ID。
/// </summary>
internal static class ReportDescriptor
{
    public sealed record Line(string Text, int Kind);

    public static List<Line> Parse(byte[] data)
    {
        var lines = new List<Line>();
        int i = 0;
        int depth = 0;
        int reportSize = 0, reportCount = 0, reportId = 0;
        ushort usagePage = 0;

        while (i < data.Length)
        {
            int start = i;
            byte head = data[i++];

            if (head == 0xFE)
            {
                // 长项：size 字节 + type 字节 + 数据
                if (i + 2 > data.Length)
                    break;
                int longSize = data[i];
                int itemType = data[i + 1];
                i += 2 + Math.Min(longSize, Math.Max(0, data.Length - i - 2));
                lines.Add(new Line($"{Off(start)}  FE ——    长项（type 0x{itemType:X2}，{longSize} 字节）", 0));
                continue;
            }

            int size = head & 0x03;
            if (size == 3)
                size = 4;
            int type = (head >> 2) & 0x03;
            int tag = (head >> 4) & 0x0F;

            ulong uval = 0;
            for (int k = 0; k < size && i < data.Length; k++)
                uval |= (ulong)data[i++] << (8 * k);
            long sval = size switch
            {
                1 => (sbyte)uval,
                2 => (short)uval,
                4 => unchecked((int)uval),
                _ => 0L,
            };

            string ind = new(' ', depth * 2);
            string text;
            int kind = 0;

            switch (type)
            {
                case 0:   // Main
                    switch (tag)
                    {
                        case 0x8:
                            text = $"{ind}Input ({MainFlags(uval)})　[报文ID {reportId}：Report Size {reportSize} × Report Count {reportCount}]";
                            break;
                        case 0x9:
                            text = $"{ind}Output ({MainFlags(uval)})　[报文ID {reportId}：Report Size {reportSize} × Report Count {reportCount}]";
                            break;
                        case 0xB:
                            text = $"{ind}Feature ({MainFlags(uval)})　[报文ID {reportId}：Report Size {reportSize} × Report Count {reportCount}]";
                            break;
                        case 0xA:
                            text = $"{ind}Collection ({CollectionType(uval)})";
                            depth++;
                            break;
                        case 0xC:
                            depth = Math.Max(0, depth - 1);
                            ind = new(' ', depth * 2);
                            text = $"{ind}End Collection";
                            break;
                        default:
                            text = $"{ind}Main 0x{tag:X2} (0x{uval:X})";
                            break;
                    }
                    break;

                case 1:   // Global
                    switch (tag)
                    {
                        case 0x0:
                            usagePage = (ushort)uval;
                            text = $"{ind}Usage Page ({UsagePageName(usagePage)})";
                            break;
                        case 0x1: text = $"{ind}Logical Min ({sval})"; break;
                        case 0x2: text = $"{ind}Logical Max ({sval})"; break;
                        case 0x3: text = $"{ind}Physical Min ({sval})"; break;
                        case 0x4: text = $"{ind}Physical Max ({sval})"; break;
                        case 0x5:
                        {
                            int e = (int)(uval & 0xF);   // 指数按 4 位补码显示（0x0E = -2）
                            if (e >= 8) e -= 16;
                            text = $"{ind}Unit Exponent ({e})";
                            break;
                        }
                        case 0x6: text = $"{ind}Unit (0x{uval:X})"; break;
                        case 0x7:
                            reportSize = (int)uval;
                            kind = 1;
                            text = $"{ind}Report Size ({uval})";
                            break;
                        case 0x8:
                            reportId = (int)uval;
                            kind = 2;
                            text = $"{ind}Report ID ({uval})";
                            break;
                        case 0x9:
                            reportCount = (int)uval;
                            kind = 1;
                            text = $"{ind}Report Count ({uval})";
                            break;
                        case 0xA: text = $"{ind}Push"; break;
                        case 0xB: text = $"{ind}Pop"; break;
                        default: text = $"{ind}Global 0x{tag:X2} ({sval})"; break;
                    }
                    break;

                case 2:   // Local
                    switch (tag)
                    {
                        case 0x0: text = $"{ind}Usage ({Usage(usagePage, (ushort)uval)})"; break;
                        case 0x1: text = $"{ind}Usage Min ({Usage(usagePage, (ushort)uval)})"; break;
                        case 0x2: text = $"{ind}Usage Max ({Usage(usagePage, (ushort)uval)})"; break;
                        default: text = $"{ind}Local 0x{tag:X2} ({sval})"; break;
                    }
                    break;

                default:
                    text = $"{ind}？";
                    break;
            }

            lines.Add(new Line($"{Off(start)}  {HexSpan(data, start, i),-9} {text}", kind));
        }
        return lines;
    }

    private static string Off(int start) => $"0x{start:X4}";

    private static string HexSpan(byte[] data, int from, int to)
    {
        var sb = new StringBuilder((to - from) * 3);
        for (int i = from; i < to && i < data.Length; i++)
            sb.Append(data[i].ToString("X2")).Append(' ');
        return sb.ToString().TrimEnd();
    }

    private static string MainFlags(ulong f)
    {
        var parts = new List<string>
        {
            (f & 0x1) == 0 ? "Data" : "Const",
            (f & 0x2) == 0 ? "Array" : "Var",
            (f & 0x4) == 0 ? "Abs" : "Rel",
        };
        if ((f & 0x8) != 0) parts.Add("Wrap");
        if ((f & 0x10) != 0) parts.Add("NonLinear");
        if ((f & 0x20) != 0) parts.Add("NoPreferred");
        if ((f & 0x40) != 0) parts.Add("NullState");
        if ((f & 0x80) != 0) parts.Add("Volatile");
        if ((f & 0x300) != 0) parts.Add($"位8-9=0x{(f & 0x300) >> 8:X}");
        return string.Join(", ", parts);
    }

    private static string CollectionType(ulong t) => t switch
    {
        0x00 => "Physical",
        0x01 => "Application",
        0x02 => "Logical",
        0x03 => "Report",
        0x04 => "Named Array",
        0x05 => "Usage Switch",
        0x06 => "Usage Modifier",
        _ => $"0x{t:X2}",
    };

    private static string UsagePageName(ushort page) => page switch
    {
        0x01 => "Generic Desktop 通用桌面",
        0x02 => "Simulation",
        0x03 => "VR",
        0x04 => "Sport",
        0x05 => "Game",
        0x07 => "Keyboard 键盘",
        0x08 => "LED",
        0x09 => "Button 按钮",
        0x0C => "Consumer 消费类",
        0x0D => "Digitizer 数字化器",
        0x0F => "PID",
        0x14 => "Unicode",
        0x59 => "Games? 0x59",
        var p when p >= 0xFF00 => $"Vendor Defined 厂商自定义 0x{p:X4}",
        _ => $"0x{page:X2}",
    };

    private static string Usage(ushort page, ushort usage)
    {
        string name = HidApi.UsageName(page, usage);
        return $"0x{page:X2}:{usage:X2}{(string.IsNullOrEmpty(name) ? "" : " " + name)}";
    }

    // ================= 重建：从 HidP 解析结果反推一份语义等价的描述符 =================

    /// <summary>
    /// 原始字节拿不到时（网络重定向总线只转发标准描述符），用 HidP 已解析出的
    /// 「usage/量程/单位/Report Count/集合树」重建一份【语义等价】的报告描述符。
    /// 注意：不是原始字节的逐字节复刻 —— 字段声明顺序、位打包方式可能不同，
    /// 但 usage 集合、逻辑/物理量程、单位、Report Count 与原设备一致。
    /// </summary>
    public static byte[] Reconstruct(
        List<HidApi.ValueCap> values, List<HidApi.ButtonCap> buttons,
        List<HidApi.LinkNode> nodes, ushort tlcPage, ushort tlcUsage, int reportId)
    {
        var d = new List<byte>();
        ushort page = 0;

        // 短项编码：baseSize1 = 该项目的「1 字节数据」形态（如 0x05 = Usage Page/1B）。
        // 低 2 位是长度码（0→无数据，1→1B，2→2B，3→4B），高位是标签。
        void Emit(byte baseSize1, int value)
        {
            int size = value == 0 ? 0
                : value is >= -128 and <= 127 ? 1
                : value is >= -32768 and <= 32767 ? 2
                : 4;
            int code = size switch { 0 => 0, 1 => 1, 2 => 2, _ => 3 };
            d.Add((byte)((baseSize1 & 0xFC) | code));
            for (int i = 0; i < size; i++)
                d.Add((byte)((value >> (8 * i)) & 0xFF));
        }

        void EmitValueCap(HidApi.ValueCap c)
        {
            if (c.UsagePage != page)
            {
                page = c.UsagePage;
                Emit(0x05, page);
            }
            if (c.IsRange)
            {
                Emit(0x19, c.UsageMin);
                Emit(0x29, c.UsageMax);
            }
            else
            {
                Emit(0x09, c.UsageMin);
            }
            Emit(0x15, c.LogicalMin);
            Emit(0x25, c.LogicalMax);
            if (c.PhysicalMin != 0 || c.PhysicalMax != 0)
            {
                Emit(0x35, c.PhysicalMin);
                Emit(0x45, c.PhysicalMax);
            }
            Emit(0x55, c.Units != 0 ? c.UnitsExp : 0);
            Emit(0x65, c.Units);
            Emit(0x75, c.BitSize);
            Emit(0x95, c.ReportCount);
            d.Add(0x81);
            d.Add(0x02);   // Input (Data, Var, Abs)
        }

        void EmitButtonCap(HidApi.ButtonCap b)
        {
            if (b.UsagePage != page)
            {
                page = b.UsagePage;
                Emit(0x05, page);
            }
            if (b.IsRange)
            {
                Emit(0x19, b.UsageMin);
                Emit(0x29, b.UsageMax);
            }
            else
            {
                Emit(0x09, b.UsageMin);
            }
            Emit(0x15, 0);
            Emit(0x25, 1);
            Emit(0x75, 1);
            Emit(0x95, 1);
            d.Add(0x81);
            d.Add(0x02);
        }

        // 深度优先：本节点的帽子（值帽在前、按钮帽在后），再递归子集合（FirstChild → NextSibling 链）
        void EmitNode(int idx)
        {
            foreach (HidApi.ValueCap c in values.Where(v => v.LinkCollection == idx))
                EmitValueCap(c);
            foreach (HidApi.ButtonCap b in buttons.Where(b => b.LinkCollection == idx))
                EmitButtonCap(b);

            for (ushort child = nodes[idx].FirstChild; child != 0 && child < nodes.Count; child = nodes[child].NextSibling)
            {
                Emit(0x05, nodes[child].UsagePage);
                // 部分设备给子集合的 LinkUsage 是 0（别名集合没记 usage）——触摸屏 TLC 下的按 Finger 补
                ushort childUsage = nodes[child].Usage != 0 ? nodes[child].Usage
                    : nodes[child].UsagePage == 0x0D ? (ushort)0x22 : nodes[child].Usage;
                Emit(0x09, childUsage);
                d.Add(0xA1);
                d.Add((byte)(nodes[child].CollectionType is 0x00 or 0x01 ? nodes[child].CollectionType : 0x02));
                EmitNode(child);
                d.Add(0xC0);
            }
        }

        // 顶层：Application 集合（节点 0）
        Emit(0x05, tlcPage);
        Emit(0x09, tlcUsage);
        d.Add(0xA1);
        d.Add(0x01);
        if (reportId != 0)
            Emit(0x85, reportId);

        if (nodes.Count > 0)
            EmitNode(0);
        d.Add(0xC0);
        return d.ToArray();
    }
}
