using System.Buffers.Binary;
using System.Text;

namespace TouchBridge.Receiver;

/// <summary>
/// TouchBridge 线协议（v1）。TCP，大端序，帧格式为 [uint16 长度][负载]。
/// Android 端 TouchCodec.kt 必须与本文件逐字节保持一致。
/// </summary>
internal static class Protocol
{
    public const byte Version = 1;

    // 消息类型
    public const byte MsgHello = 0x01;
    public const byte MsgHelloAck = 0x02;
    public const byte MsgTouchFrame = 0x10;

    // action（与 Android MotionEvent.getActionMasked() 对齐）
    public const byte ActionDown = 0;
    public const byte ActionUp = 1;
    public const byte ActionMove = 2;
    public const byte ActionCancel = 3;
    public const byte ActionPointerDown = 5;
    public const byte ActionPointerUp = 6;

    // 单个指针的状态
    public const byte StateUpdate = 0;
    public const byte StateDown = 1;
    public const byte StateUp = 2;

    /// <summary>TouchFrame 中每条指针记录的字节数（当前版本，含 size）。</summary>
    public const int PointRecordSize = 32;

    /// <summary>旧版记录字节数（不含 size）。解析时按实际长度自适应，兼容旧 APK。</summary>
    public const int PointRecordSizeLegacy = 28;

    /// <summary>Hello 握手信息。</summary>
    internal readonly record struct Hello(ushort SurfaceWidth, ushort SurfaceHeight, string DeviceName);

    public static bool TryParseHello(ReadOnlySpan<byte> payload, out Hello hello, out byte version)
    {
        hello = default;
        version = 0;
        if (payload.Length < 8 || payload[0] != MsgHello)
            return false;

        version = payload[1];
        ushort w = BinaryPrimitives.ReadUInt16BigEndian(payload[2..]);
        ushort h = BinaryPrimitives.ReadUInt16BigEndian(payload[4..]);
        int nameLen = payload[6];
        if (payload.Length < 7 + nameLen)
            return false;

        string name = nameLen > 0 ? Encoding.UTF8.GetString(payload.Slice(7, nameLen)) : "";
        hello = new Hello(w, h, name);
        return true;
    }

    public static byte[] BuildHelloAck()
    {
        return new byte[] { MsgHelloAck, Version };
    }
}

/// <summary>单个触摸点的一帧数据（坐标/尺寸均已归一化到 0..1）。</summary>
internal readonly struct TouchPointData
{
    public readonly byte Id;
    public readonly byte State;
    public readonly byte ToolType;      // 1=Finger 2=Stylus 3=Eraser 4=Mouse（对齐 Android MotionEvent）
    public readonly float X;
    public readonly float Y;
    /// <summary>平板 getPressure() 的原始值。很多设备不是 0~1 归一化值（如 0~10），原样传，由 Windows 端决定怎么用。</summary>
    public readonly float Pressure;
    /// <summary>平板 getSize() 的原始值（AXIS_SIZE）。可用于指定接触面积（见 --contact-area size）。</summary>
    public readonly float Size;
    public readonly float ContactW;     // 归一化接触椭圆包围盒宽
    public readonly float ContactH;     // 归一化接触椭圆包围盒高
    public readonly float OrientationDeg;

    public TouchPointData(byte id, byte state, byte toolType, float x, float y,
        float pressure, float size, float contactW, float contactH, float orientationDeg)
    {
        Id = id;
        State = state;
        ToolType = toolType;
        X = x;
        Y = y;
        Pressure = pressure;
        Size = size;
        ContactW = contactW;
        ContactH = contactH;
        OrientationDeg = orientationDeg;
    }
}

/// <summary>一次 MotionEvent 的完整上报（包含当前所有活动指针）。</summary>
internal sealed class TouchFrame
{
    public byte Action;
    public uint Sequence;
    public ulong TimestampMicros;
    public TouchPointData[] Points = Array.Empty<TouchPointData>();

    public static bool TryParse(ReadOnlySpan<byte> p, out TouchFrame frame)
    {
        frame = null!;
        if (p.Length < 15 || p[0] != Protocol.MsgTouchFrame)
            return false;

        byte action = p[1];
        int count = p[2];
        uint seq = BinaryPrimitives.ReadUInt32BigEndian(p[3..]);
        ulong ts = BinaryPrimitives.ReadUInt64BigEndian(p[7..]);

        if (count < 0)
            return false;

        if (count == 0)
        {
            frame = new TouchFrame { Action = action, Sequence = seq, TimestampMicros = ts, Points = Array.Empty<TouchPointData>() };
            return true;
        }

        // 按实际长度推算每条记录大小：兼容旧版 28 字节与新版的 32 字节
        int recordSize = (p.Length - 15) / count;
        if (recordSize != Protocol.PointRecordSize && recordSize != Protocol.PointRecordSizeLegacy)
            return false;
        if (p.Length < 15 + count * recordSize)
            return false;

        var points = new TouchPointData[count];
        int off = 15;
        for (int i = 0; i < count; i++)
        {
            byte id = p[off];
            byte state = p[off + 1];
            byte tool = p[off + 2];
            float x = BinaryPrimitives.ReadSingleBigEndian(p[(off + 4)..]);
            float y = BinaryPrimitives.ReadSingleBigEndian(p[(off + 8)..]);
            float pressure = BinaryPrimitives.ReadSingleBigEndian(p[(off + 12)..]);

            bool hasSize = recordSize >= Protocol.PointRecordSize;
            float size = hasSize ? BinaryPrimitives.ReadSingleBigEndian(p[(off + 16)..]) : 0f;
            int baseOff = hasSize ? off + 20 : off + 16;
            float cw = BinaryPrimitives.ReadSingleBigEndian(p[baseOff..]);
            float ch = BinaryPrimitives.ReadSingleBigEndian(p[(baseOff + 4)..]);
            float orient = BinaryPrimitives.ReadSingleBigEndian(p[(baseOff + 8)..]);

            points[i] = new TouchPointData(id, state, tool, x, y, pressure, size, cw, ch, orient);
            off += recordSize;
        }

        frame = new TouchFrame { Action = action, Sequence = seq, TimestampMicros = ts, Points = points };
        return true;
    }
}
