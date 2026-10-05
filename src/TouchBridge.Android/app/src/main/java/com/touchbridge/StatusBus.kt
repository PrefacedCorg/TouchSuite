package com.touchbridge

/** 进程内状态广播（服务在后台线程写入，Activity 订阅后切回主线程刷新 UI）。 */
data class Status(
    val connected: Boolean,
    val message: String,
    val sentFrames: Long,
)

object StatusBus {
    private val listeners = java.util.concurrent.CopyOnWriteArrayList<(Status) -> Unit>()

    @Volatile
    var current: Status = Status(false, "未运行", 0L)
        private set

    fun addListener(l: (Status) -> Unit) {
        listeners.add(l)
        l(current)
    }

    fun removeListener(l: (Status) -> Unit) {
        listeners.remove(l)
    }

    fun set(connected: Boolean, message: String, sentFrames: Long = current.sentFrames) {
        val s = Status(connected, message, sentFrames)
        current = s
        for (l in listeners) {
            runCatching { l(s) }
        }
    }
}
