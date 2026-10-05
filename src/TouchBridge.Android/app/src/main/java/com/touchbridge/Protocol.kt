package com.touchbridge

/**
 * 与 Windows 端 Protocol.cs 逐字节对齐的线协议常量。
 * 任何改动都必须同步两侧。
 */
object Proto {
    const val VERSION = 1

    const val MSG_HELLO = 0x01
    const val MSG_HELLO_ACK = 0x02
    const val MSG_TOUCH_FRAME = 0x10

    /** 每条指针记录的字节数（含 size）。旧版为 28（不含 size），电脑端按长度自适应兼容。 */
    const val POINT_RECORD_SIZE = 32

    const val STATE_UPDATE = 0
    const val STATE_DOWN = 1
    const val STATE_UP = 2
}

/** 单个触摸点样本，坐标 / 尺寸均已归一化到 0..1。 */
data class PointSample(
    val id: Int,
    val state: Int,
    val toolType: Int,
    val x: Float,
    val y: Float,
    val pressure: Float,
    val size: Float,
    val contactW: Float,
    val contactH: Float,
    val orientationDeg: Float,
)

/** 一次 MotionEvent 的完整上报（包含当前所有活动指针）。 */
data class TouchFrameData(
    val action: Int,
    val sequence: Long,
    val timestampMicros: Long,
    val points: List<PointSample>,
)
