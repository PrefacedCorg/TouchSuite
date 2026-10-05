package com.touchbridge

/**
 * 「模拟连接」参数：Activity 写入，模拟发送循环每帧现取，
 * 因此在界面上改数值会立即生效，不需要重启服务。
 */
object SimConfig {
    /** 上报的原始压力（0 ~ 设备量程），由电脑端按 --pressure-max 归一化。 */
    @Volatile
    var pressure: Float = 7f

    /** 上报的原始 size（接触面积），电脑端据此决定接触矩形。 */
    @Volatile
    var size: Float = 400f

    /** 基础点的左右偏移（像素，正 = 右，负 = 左）。0 = 正中央。抖动叠加在它之上。 */
    @Volatile
    var offsetXPx: Int = 0

    /** 模拟点在基础点附近的抖动幅度（像素，±）。0 = 不动。 */
    @Volatile
    var jitterPx: Int = 10

    /** 发送间隔，毫秒。 */
    @Volatile
    var intervalMs: Long = 16L
}
