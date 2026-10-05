package com.touchbridge

import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * 把触摸数据编码为 [uint16 大端长度][负载] 的线协议帧。
 * 布局必须与 Windows 端 Protocol.cs / TouchInjector.cs 保持一致。
 */
object TouchCodec {

    fun encodeHello(surfaceWidth: Int, surfaceHeight: Int, deviceName: String): ByteArray {
        val name = deviceName.toByteArray(Charsets.UTF_8)
        val nameLen = minOf(name.size, 255)

        val payload = ByteBuffer.allocate(7 + nameLen).order(ByteOrder.BIG_ENDIAN)
        payload.put(Proto.MSG_HELLO.toByte())
        payload.put(Proto.VERSION.toByte())
        payload.putShort(surfaceWidth.toShort())
        payload.putShort(surfaceHeight.toShort())
        payload.put(nameLen.toByte())
        payload.put(name, 0, nameLen)

        return frame(payload.array())
    }

    fun encodeFrame(f: TouchFrameData): ByteArray {
        val count = minOf(f.points.size, 255)

        val payload = ByteBuffer.allocate(15 + count * Proto.POINT_RECORD_SIZE).order(ByteOrder.BIG_ENDIAN)
        payload.put(Proto.MSG_TOUCH_FRAME.toByte())
        payload.put(f.action.toByte())
        payload.put(count.toByte())
        payload.putInt(f.sequence.toInt())
        payload.putLong(f.timestampMicros)

        for (i in 0 until count) {
            val p = f.points[i]
            payload.put(p.id.toByte())
            payload.put(p.state.toByte())
            payload.put(p.toolType.toByte())
            payload.put(0)                       // 保留字节
            payload.putFloat(p.x)
            payload.putFloat(p.y)
            payload.putFloat(p.pressure)
            payload.putFloat(p.size)
            payload.putFloat(p.contactW)
            payload.putFloat(p.contactH)
            payload.putFloat(p.orientationDeg)
        }

        return frame(payload.array())
    }

    private fun frame(payload: ByteArray): ByteArray {
        val out = ByteArray(2 + payload.size)
        out[0] = ((payload.size shr 8) and 0xFF).toByte()
        out[1] = (payload.size and 0xFF).toByte()
        System.arraycopy(payload, 0, out, 2, payload.size)
        return out
    }
}
