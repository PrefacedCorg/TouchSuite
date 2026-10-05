package com.touchbridge

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.RectF
import android.graphics.Typeface
import android.view.MotionEvent
import android.view.View
import java.util.Locale
import kotlin.math.cos
import kotlin.math.sin
import kotlin.math.sqrt

/**
 * 全屏触摸采集层：把每个 MotionEvent 转换成归一化的多点样本（含接触面积与压感），
 * 通过 [Listener] 回调交给上层编码转发。
 */
class TouchSurfaceView(context: Context) : View(context) {

    fun interface Listener {
        fun onFrame(frame: TouchFrameData)
    }

    var listener: Listener? = null

    private val density = resources.displayMetrics.density

    private val fillPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.argb(60, 80, 200, 255)
        style = Paint.Style.FILL
    }
    private val strokePaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.argb(200, 60, 180, 255)
        style = Paint.Style.STROKE
        strokeWidth = 2f * density
    }
    private val debugBgPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.argb(180, 0, 0, 0)
        style = Paint.Style.FILL
    }
    private val debugTextPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.argb(235, 175, 255, 185)
        textSize = 12f * density
        typeface = Typeface.MONOSPACE
    }

    private class Dot(val x: Float, val y: Float, val radius: Float)

    private val active = ArrayList<Dot>()
    private var sequence = 0L

    /** 最近一帧的原始数据，用于左上角调试信息显示。 */
    private var lastRaw: RawSample? = null

    init {
        setWillNotDraw(false)
        isClickable = true
        isFocusable = false
    }

    override fun onTouchEvent(event: MotionEvent): Boolean {
        val w = width.toFloat()
        val h = height.toFloat()
        if (w <= 0f || h <= 0f) return true

        val action = event.actionMasked

        if (action == MotionEvent.ACTION_CANCEL) {
            active.clear()
            lastRaw = null
            invalidate()
            listener?.onFrame(TouchFrameData(action, sequence++, event.eventTime * 1000L, emptyList()))
            return true
        }

        val count = event.pointerCount
        val points = ArrayList<PointSample>(count)
        active.clear()

        for (i in 0 until count) {
            val state = when {
                action == MotionEvent.ACTION_DOWN -> Proto.STATE_DOWN
                action == MotionEvent.ACTION_UP -> Proto.STATE_UP
                action == MotionEvent.ACTION_POINTER_DOWN && i == event.actionIndex -> Proto.STATE_DOWN
                action == MotionEvent.ACTION_POINTER_UP && i == event.actionIndex -> Proto.STATE_UP
                else -> Proto.STATE_UPDATE
            }
            val p = sample(event, i, w, h, state)
            points.add(p)
            active.add(Dot(p.x * w, p.y * h, radiusFor(p, w, h)))

            if (i == 0) {
                lastRaw = RawSample(
                    pointerCount = count,
                    toolType = event.getToolType(0),
                    pressure = event.getPressure(0),
                    size = event.getSize(0),
                    touchMajor = event.getTouchMajor(0),
                    touchMinor = event.getTouchMinor(0),
                    orientationDeg = normDeg(event.getOrientation(0)),
                    px = event.getX(0),
                    py = event.getY(0),
                )
            }
        }

        invalidate()
        listener?.onFrame(TouchFrameData(action, sequence++, event.eventTime * 1000L, points))
        return true
    }

    private fun radiusFor(p: PointSample, w: Float, h: Float): Float {
        val d = maxOf(p.contactW * w, p.contactH * h)
        return if (d > 1f) (d / 2f).coerceIn(6f, 240f) else 24f * density
    }

    private fun sample(event: MotionEvent, i: Int, w: Float, h: Float, state: Int): PointSample {
        val id = event.getPointerId(i)
        val tool = event.getToolType(i)
        val x = (event.getX(i) / w).coerceIn(0f, 1f)
        val y = (event.getY(i) / h).coerceIn(0f, 1f)
        // 不要截断到 0~1：很多设备返回的是未归一化的原始值（例如 0~10），原样上报，
        // 由电脑端决定怎么用（默认直通，不做归一化）。
        val rawPressure = event.getPressure(i)
        val pressure = if (rawPressure.isNaN() || rawPressure < 0f) 0f else rawPressure

        // 接触尺寸标量（AXIS_SIZE），也原样上报，可用于指定面积（电脑端 --contact-area size）
        val rawSize = event.getSize(i)
        val size = if (rawSize.isNaN() || rawSize < 0f) 0f else rawSize

        // 接触椭圆：多数设备只给 major，minor 可能为 0
        val major = event.getTouchMajor(i)
        val minor = event.getTouchMinor(i)
        var a = major / 2f
        var b = if (minor > 0f) minor / 2f else a
        if (a <= 0f) a = b
        if (a <= 0f) {
            // 设备不上报接触尺寸
            return PointSample(id, state, tool, x, y, pressure, size, 0f, 0f, 0f)
        }
        if (b <= 0f) b = a

        // 把旋转椭圆折算成轴对齐包围盒（Windows 的 rcContact 是轴对齐矩形）。
        // Android 约定（MotionEvent.PointerCoords.orientation 文档）：
        //   TouchMajor = 长轴，orientation=0 时「长轴朝上」→ 即竖直(Y)方向尺寸；
        //   TouchMinor = 短轴 → 水平(X)方向尺寸；orientation 是相对竖直方向的顺时针夹角。
        // 因此 水平(X) 半径用 minor×cosθ / major×sinθ，竖直(Y) 半径用 major×cosθ / minor×sinθ。
        // ⚠ 之前两行写反了 → 上报的 0x48 宽度 / 0x49 高度互换（圆接触看不出来，手指或手掌
        //   的长短轴差异大时很明显）。
        val theta = event.getOrientation(i)
        val ct = cos(theta)
        val st = sin(theta)
        val halfW = sqrt((b * ct) * (b * ct) + (a * st) * (a * st))   // 水平(X)方向半径
        val halfH = sqrt((a * ct) * (a * ct) + (b * st) * (b * st))   // 竖直(Y)方向半径

        val nw = ((2f * halfW) / w).coerceIn(0f, 1f)
        val nh = ((2f * halfH) / h).coerceIn(0f, 1f)

        var deg = Math.toDegrees(theta.toDouble()).toFloat() % 360f
        if (deg < 0f) deg += 360f

        return PointSample(id, state, tool, x, y, pressure, size, nw, nh, deg)
    }

    /** 一帧的原始轴数据，仅用于左上角调试显示（不做任何换算）。 */
    private class RawSample(
        val pointerCount: Int,
        val toolType: Int,
        val pressure: Float,
        val size: Float,
        val touchMajor: Float,
        val touchMinor: Float,
        val orientationDeg: Float,
        val px: Float,
        val py: Float,
    )

    private fun normDeg(rad: Float): Float {
        var deg = Math.toDegrees(rad.toDouble()).toFloat() % 360f
        if (deg < 0f) deg += 360f
        return deg
    }

    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)

        for (d in active) {
            canvas.drawCircle(d.x, d.y, d.radius, fillPaint)
            canvas.drawCircle(d.x, d.y, d.radius, strokePaint)
        }

        val inset = strokePaint.strokeWidth
        canvas.drawRect(inset, inset, width - inset, height - inset, strokePaint)

        drawDebugInfo(canvas)
    }

    /** 左上角调试信息：显示当前正在往电脑转发的内容。 */
    private fun drawDebugInfo(canvas: Canvas) {
        val lines = debugLines()
        val pad = 10f * density
        val lineHeight = debugTextPaint.textSize * 1.5f

        var maxWidth = 0f
        for (line in lines) {
            maxWidth = maxOf(maxWidth, debugTextPaint.measureText(line))
        }

        val left = 12f * density
        val top = 12f * density
        val boxWidth = maxWidth + pad * 2
        val boxHeight = lineHeight * lines.size + pad * 2

        canvas.drawRoundRect(
            RectF(left, top, left + boxWidth, top + boxHeight),
            8f * density,
            8f * density,
            debugBgPaint,
        )

        var baseline = top + pad + debugTextPaint.textSize
        for (line in lines) {
            canvas.drawText(line, left + pad, baseline, debugTextPaint)
            baseline += lineHeight
        }
    }

    private fun debugLines(): List<String> {
        val d = lastRaw
        if (d == null) {
            return listOf(
                "TouchBridge 调试 · 等待触摸",
                "（触摸平板即显示实时转发数据）",
            )
        }

        return listOf(
            "TouchBridge 调试   点数=${d.pointerCount}",
            "工具=${toolName(d.toolType)}   压力=${fmt(d.pressure, 2)}",
            "size=${fmt(d.size, 2)}   touchMajor=${fmt(d.touchMajor, 1)}px  touchMinor=${fmt(d.touchMinor, 1)}px",
            "坐标=(${d.px.toInt()}, ${d.py.toInt()})   朝向=${fmt(d.orientationDeg, 0)}°",
        )
    }

    /** 对齐 android.view.MotionEvent 的 TOOL_TYPE_*（0 未知 / 1 手指 / 2 笔 / 3 鼠标 / 4 橡皮）。 */
    private fun toolName(toolType: Int): String = when (toolType) {
        1 -> "手指"
        2 -> "笔"
        3 -> "鼠标"
        4 -> "橡皮"
        else -> "未知"
    }

    private fun fmt(value: Float, digits: Int): String =
        String.format(Locale.US, "%.${digits}f", value)
}
