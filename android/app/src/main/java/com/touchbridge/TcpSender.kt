package com.touchbridge

import java.io.BufferedOutputStream
import java.io.IOException
import java.net.Inet4Address
import java.net.Inet6Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.TimeUnit

/**
 * WiFi TCP 发送端（支持 IPv4 与 IPv6）。
 * 触摸事件来自 UI 线程，写入有界队列，由独立写线程发送，保证 UI 不被网络阻塞。
 * 连接失败会自动重连；多地址时逐个尝试（IPv4 优先），成功即用。
 */
class TcpSender(
    private val host: String,
    private val port: Int,
    private val surfaceWidth: Int,
    private val surfaceHeight: Int,
    private val deviceName: String,
) {
    private val queue = ArrayBlockingQueue<ByteArray>(QUEUE_CAPACITY)

    @Volatile
    private var running = false

    @Volatile
    private var socket: Socket? = null

    private var thread: Thread? = null
    private var sentFrames = 0L

    fun start() {
        if (running) return
        running = true
        thread = Thread({ runLoop() }, "TouchBridge-Sender").apply {
            isDaemon = true
            start()
        }
    }

    /** 入队一帧；队列满时丢最旧的帧，保证低延迟。 */
    fun enqueue(bytes: ByteArray) {
        if (!running) return
        if (!queue.offer(bytes)) {
            queue.poll()
            queue.offer(bytes)
        }
    }

    fun stop() {
        if (!running) return
        running = false
        thread?.interrupt()
        closeSocket()
        thread = null
    }

    private fun runLoop() {
        while (running) {
            try {
                val candidates = resolveCandidates()
                var lastError: Exception? = null
                var connected = false

                for (addr in candidates) {
                    if (!running) break
                    try {
                        pump(addr)
                        connected = true
                        break
                    } catch (e: InterruptedException) {
                        return
                    } catch (e: Exception) {
                        lastError = e
                    } finally {
                        closeSocket()
                    }
                }

                if (!connected && running) {
                    throw lastError ?: IOException("无法解析主机 $host")
                }
            } catch (e: InterruptedException) {
                return
            } catch (e: Exception) {
                if (!running) break
                StatusBus.set(false, "连接失败：${e.message}，2 秒后重试", sentFrames)
                try {
                    Thread.sleep(RETRY_DELAY_MS)
                } catch (ie: InterruptedException) {
                    return
                }
            }
        }
        StatusBus.set(false, "已停止", sentFrames)
    }

    /** 解析候选地址：IPv6 字面量可直接解析；IPv4 优先，其余保持系统顺序。 */
    private fun resolveCandidates(): List<InetAddress> =
        InetAddress.getAllByName(normalizeHost(host))
            .sortedBy { if (it is Inet4Address) 0 else 1 }

    /** 容忍用户粘贴 `[2408::1]` 这种带方括号的写法。 */
    private fun normalizeHost(raw: String): String {
        var h = raw.trim()
        if (h.length >= 2 && h.startsWith("[") && h.endsWith("]")) {
            h = h.substring(1, h.length - 1)
        }
        return h
    }

    private fun pump(addr: InetAddress) {
        val s = Socket()
        socket = s
        s.tcpNoDelay = true
        s.keepAlive = true
        s.connect(InetSocketAddress(addr, port), CONNECT_TIMEOUT_MS)

        queue.clear() // 丢弃断线期间的过期帧

        val out = BufferedOutputStream(s.getOutputStream(), 16 * 1024)
        out.write(TouchCodec.encodeHello(surfaceWidth, surfaceHeight, deviceName))
        out.flush()

        val shown = formatHost(addr)
        StatusBus.set(true, "已连接 $shown:$port", sentFrames)

        var lastStatusAt = System.currentTimeMillis()

        while (running) {
            val frame = queue.poll(500, TimeUnit.MILLISECONDS) ?: continue
            out.write(frame)
            sentFrames++
            if (queue.isEmpty()) {
                out.flush()
            }

            val now = System.currentTimeMillis()
            if (now - lastStatusAt >= STATUS_INTERVAL_MS) {
                lastStatusAt = now
                StatusBus.set(true, "已连接 $shown:$port", sentFrames)
            }
        }
        out.flush()
    }

    /** IPv6 地址加方括号，便于阅读/复制（`[2408:8000::1]:9000`）。 */
    private fun formatHost(addr: InetAddress): String {
        val text = addr.hostAddress ?: "?"
        return if (addr is Inet6Address) "[$text]" else text
    }

    private fun closeSocket() {
        val s = socket ?: return
        socket = null
        try {
            s.close()
        } catch (_: IOException) {
            // 忽略关闭异常
        }
    }

    companion object {
        private const val QUEUE_CAPACITY = 240
        private const val CONNECT_TIMEOUT_MS = 4000
        private const val RETRY_DELAY_MS = 2000L
        private const val STATUS_INTERVAL_MS = 500L
    }
}
