namespace TouchBridge.Receiver;

/// <summary>从 TCP 流中按 [uint16 大端长度][负载] 读取完整帧。</summary>
internal sealed class FrameReader
{
    private readonly Stream _stream;
    private readonly byte[] _lenBuf = new byte[2];

    public FrameReader(Stream stream) => _stream = stream;

    /// <summary>返回 null 表示对端已关闭或读出错。</summary>
    public byte[]? ReadFrame()
    {
        if (!ReadExact(_lenBuf, 2))
            return null;

        int len = (_lenBuf[0] << 8) | _lenBuf[1];
        if (len <= 0)
            return Array.Empty<byte>();

        var buf = new byte[len];
        return ReadExact(buf, len) ? buf : null;
    }

    private bool ReadExact(byte[] buffer, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = _stream.Read(buffer, offset, count - offset);
            if (read <= 0)
                return false;
            offset += read;
        }
        return true;
    }
}
