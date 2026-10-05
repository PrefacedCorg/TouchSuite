package com.touchbridge

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.graphics.Color
import android.graphics.PixelFormat
import android.graphics.drawable.GradientDrawable
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import android.view.Gravity
import android.view.View
import android.view.WindowManager
import android.widget.FrameLayout
import android.widget.TextView
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import kotlin.random.Random

/**
 * 前台服务：持有全屏触摸覆盖层，把触摸编码后经 TCP 转发到 Windows。
 * 覆盖层会接管整块屏幕的触摸（平板此时作为纯输入设备），
 * 右上角「停止转发」与通知栏按钮可结束。
 */
class OverlayService : Service() {

    private lateinit var windowManager: WindowManager
    private var root: View? = null
    private var sender: TcpSender? = null
    private var wakeLock: PowerManager.WakeLock? = null

    /** 模拟模式的发送线程；非空即表示当前处于模拟连接状态。 */
    @Volatile
    private var simThread: Thread? = null

    private val statusListener: (Status) -> Unit = { s ->
        val text = if (s.connected) {
            val sim = if (simThread != null) {
                " · 模拟中心 prs=%.2f size=%.0f".format(SimConfig.pressure, SimConfig.size)
            } else {
                ""
            }
            "已连接 ${s.message} · 已发送 ${s.sentFrames} 帧$sim"
        } else {
            s.message
        }
        updateNotification(text)
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        windowManager = getSystemService(Context.WINDOW_SERVICE) as WindowManager
        createChannel()
        StatusBus.addListener(statusListener)
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            stopSelf()
            return START_NOT_STICKY
        }

        val host = intent?.getStringExtra(EXTRA_HOST).orEmpty()
        val port = intent?.getIntExtra(EXTRA_PORT, DEFAULT_PORT) ?: DEFAULT_PORT
        val simulate = intent?.getBooleanExtra(EXTRA_SIMULATE, false) ?: false
        if (host.isEmpty()) {
            stopSelf()
            return START_NOT_STICKY
        }

        startAsForeground(if (simulate) "模拟连接 $host:$port" else "正在连接 $host:$port")
        acquireWakeLock()

        if (simulate) {
            // 模拟模式不显示全屏覆盖层：不接管真实触摸，屏幕保持可用
            if (root != null) {
                runCatching { windowManager.removeView(root) }
                root = null
            }
            if (simThread == null) {
                startSimulation(host, port)
            }
        } else {
            stopSimulation()
            if (root == null) {
                showOverlay(host, port)
            }
        }
        return START_STICKY
    }

    override fun onDestroy() {
        StatusBus.removeListener(statusListener)
        root?.let { runCatching { windowManager.removeView(it) } }
        root = null
        stopSimulation()
        sender?.stop()
        sender = null
        releaseWakeLock()
        StatusBus.set(false, "未运行", 0L)
        super.onDestroy()
    }

    // ---------------- 覆盖层 ----------------

    private fun showOverlay(host: String, port: Int) {
        val container = FrameLayout(this)

        val surface = TouchSurfaceView(this)
        container.addView(
            surface,
            FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT,
                FrameLayout.LayoutParams.MATCH_PARENT,
            ),
        )

        val stop = TextView(this).apply {
            text = "停止转发"
            setTextColor(Color.WHITE)
            setPadding(dp(16), dp(9), dp(16), dp(9))
            background = GradientDrawable().apply {
                cornerRadius = dp(20).toFloat()
                setColor(Color.argb(190, 200, 45, 45))
            }
            setOnClickListener { stopSelf() }
        }
        container.addView(
            stop,
            FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.WRAP_CONTENT,
                FrameLayout.LayoutParams.WRAP_CONTENT,
            ).apply {
                gravity = Gravity.TOP or Gravity.END
                topMargin = dp(24)
                rightMargin = dp(16)
            },
        )

        val lp = WindowManager.LayoutParams(
            WindowManager.LayoutParams.MATCH_PARENT,
            WindowManager.LayoutParams.MATCH_PARENT,
            WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
            WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN or
                WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS or
                WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or
                WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON,
            PixelFormat.TRANSLUCENT,
        ).apply {
            gravity = Gravity.TOP or Gravity.START
        }

        try {
            windowManager.addView(container, lp)
        } catch (e: Exception) {
            StatusBus.set(false, "无法显示覆盖层：${e.message}（请检查悬浮窗权限）", 0L)
            stopSelf()
            return
        }
        root = container

        // 等视图完成布局后拿到真实像素尺寸，再开始发送（坐标按此尺寸归一化）。
        container.post {
            val w = if (surface.width > 0) surface.width else resources.displayMetrics.widthPixels
            val h = if (surface.height > 0) surface.height else resources.displayMetrics.heightPixels

            val s = TcpSender(host, port, w, h, deviceLabel())
            surface.listener = TouchSurfaceView.Listener { frame ->
                s.enqueue(TouchCodec.encodeFrame(frame))
            }
            s.start()
            sender = s
        }
    }

    private fun deviceLabel(): String = "${Build.MANUFACTURER} ${Build.MODEL}".trim()

    // ---------------- 模拟连接 ----------------

    /** 不接管真实触摸，只持续把屏幕正中的一个接触点发给电脑。 */
    private fun startSimulation(host: String, port: Int) {
        sender?.stop()

        val metrics = resources.displayMetrics
        val s = TcpSender(host, port, metrics.widthPixels, metrics.heightPixels, deviceLabel())
        s.start()
        sender = s

        simThread = Thread({ simLoop(s) }, "TouchBridge-Sim").apply {
            isDaemon = true
            start()
        }
    }

    /**
     * 首帧 DOWN，之后按 [SimConfig.intervalMs] 持续发 UPDATE；
     * 压力 / size 每帧从 [SimConfig] 现取，所以界面上改数值立即生效。
     * 退出前补发一帧 UP，避免电脑端指针卡住。
     */
    private fun simLoop(s: TcpSender) {
        var sequence = 0L
        var sentDown = false
        try {
            while (!Thread.currentThread().isInterrupted) {
                val state = if (sentDown) Proto.STATE_UPDATE else Proto.STATE_DOWN
                s.enqueue(TouchCodec.encodeFrame(simFrame(sequence++, state)))
                sentDown = true
                Thread.sleep(SimConfig.intervalMs.coerceIn(MIN_INTERVAL_MS, MAX_INTERVAL_MS))
            }
        } catch (_: InterruptedException) {
            // 正常停止
        } finally {
            if (sentDown) {
                s.enqueue(TouchCodec.encodeFrame(simFrame(sequence, Proto.STATE_UP)))
                runCatching { Thread.sleep(FLUSH_WAIT_MS) }
            }
            s.stop()
            if (sender === s) {
                sender = null
            }
        }
    }

    /**
     * 屏幕正中（归一化 0.5, 0.5）叠加左右偏移 [SimConfig.offsetXPx] 与
     * ±[SimConfig.jitterPx] 像素的随机抖动；接触长宽留 0，让电脑端用 size 定面积。
     */
    private fun simFrame(sequence: Long, state: Int): TouchFrameData {
        val metrics = resources.displayMetrics
        val jitter = SimConfig.jitterPx
        val dx = SimConfig.offsetXPx + if (jitter > 0) Random.nextInt(-jitter, jitter + 1) else 0
        val dy = if (jitter > 0) Random.nextInt(-jitter, jitter + 1) else 0
        val x = (SIM_X + dx.toFloat() / metrics.widthPixels).coerceIn(0f, 1f)
        val y = (SIM_Y + dy.toFloat() / metrics.heightPixels).coerceIn(0f, 1f)

        return TouchFrameData(
            action = 0,
            sequence = sequence,
            timestampMicros = System.nanoTime() / 1000,
            points = listOf(
                PointSample(
                    id = SIM_POINTER_ID,
                    state = state,
                    toolType = SIM_TOOL_TYPE,
                    x = x,
                    y = y,
                    pressure = SimConfig.pressure,
                    size = SimConfig.size,
                    contactW = 0f,
                    contactH = 0f,
                    orientationDeg = 0f,
                ),
            ),
        )
    }

    private fun stopSimulation() {
        val t = simThread ?: return
        simThread = null
        t.interrupt()
        runCatching { t.join(JOIN_TIMEOUT_MS) }
    }

    // ---------------- 前台通知 ----------------

    private fun startAsForeground(text: String) {
        val type = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE
        } else {
            0
        }
        ServiceCompat.startForeground(this, NOTIF_ID, buildNotification(text), type)
    }

    private fun updateNotification(text: String) {
        val nm = getSystemService(NotificationManager::class.java)
        nm?.notify(NOTIF_ID, buildNotification(text))
    }

    private fun buildNotification(text: String): Notification {
        val stopIntent = Intent(this, OverlayService::class.java).apply { action = ACTION_STOP }
        val stopPending = PendingIntent.getService(
            this,
            0,
            stopIntent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )

        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle("TouchBridge 触摸转发")
            .setContentText(text)
            .setSmallIcon(android.R.drawable.ic_menu_edit)
            .setOngoing(true)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .addAction(0, "停止转发", stopPending)
            .build()
    }

    private fun createChannel() {
        val channel = NotificationChannel(
            CHANNEL_ID,
            "触摸转发",
            NotificationManager.IMPORTANCE_LOW,
        )
        getSystemService(NotificationManager::class.java)?.createNotificationChannel(channel)
    }

    // ---------------- 电源 ----------------

    private fun acquireWakeLock() {
        if (wakeLock != null) return
        val pm = getSystemService(Context.POWER_SERVICE) as PowerManager
        wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "TouchBridge::forward").apply {
            setReferenceCounted(false)
            acquire(4 * 60 * 60 * 1000L)
        }
    }

    private fun releaseWakeLock() {
        wakeLock?.let { runCatching { if (it.isHeld) it.release() } }
        wakeLock = null
    }

    private fun dp(value: Int): Int = (value * resources.displayMetrics.density).toInt()

    companion object {
        const val ACTION_STOP = "com.touchbridge.action.STOP"
        const val EXTRA_HOST = "host"
        const val EXTRA_PORT = "port"
        const val EXTRA_SIMULATE = "simulate"
        const val DEFAULT_PORT = 9000

        private const val CHANNEL_ID = "touchbridge"
        private const val NOTIF_ID = 1

        private const val SIM_X = 0.5f
        private const val SIM_Y = 0.5f
        private const val SIM_POINTER_ID = 1

        /** MotionEvent.TOOL_TYPE_FINGER，与真实触摸通路保持一致。 */
        private const val SIM_TOOL_TYPE = 1
        private const val MIN_INTERVAL_MS = 4L
        private const val MAX_INTERVAL_MS = 1000L
        private const val FLUSH_WAIT_MS = 150L
        private const val JOIN_TIMEOUT_MS = 800L
    }
}
