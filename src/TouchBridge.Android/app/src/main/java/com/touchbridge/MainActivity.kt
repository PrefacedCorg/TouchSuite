package com.touchbridge

import android.content.Intent
import android.content.SharedPreferences
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.text.Editable
import android.text.TextWatcher
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import com.touchbridge.databinding.ActivityMainBinding

class MainActivity : AppCompatActivity() {

    private lateinit var binding: ActivityMainBinding
    private lateinit var prefs: SharedPreferences

    /** 当前是否处于「模拟连接」状态（用于切换按钮文案）。 */
    private var simRunning = false

    private val notifPermissionLauncher =
        registerForActivityResult(ActivityResultContracts.RequestPermission()) { }

    private val statusListener: (Status) -> Unit = { s ->
        runOnUiThread {
            val head = if (s.connected) "已连接" else "未连接"
            binding.statusText.text = "状态：$head · ${s.message} · 已发送 ${s.sentFrames} 帧"
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        binding = ActivityMainBinding.inflate(layoutInflater)
        setContentView(binding.root)

        prefs = getSharedPreferences(PREFS, MODE_PRIVATE)
        binding.hostInput.setText(prefs.getString(KEY_HOST, ""))
        binding.portInput.setText(prefs.getInt(KEY_PORT, OverlayService.DEFAULT_PORT).toString())
        binding.simPressureSlider.value = snapToStep(
            prefs.getFloat(KEY_SIM_PRESSURE, DEFAULT_SIM_PRESSURE),
            binding.simPressureSlider.valueFrom,
            binding.simPressureSlider.valueTo,
            PRESSURE_STEP,
        )
        binding.simSizeSlider.value = snapToStep(
            prefs.getFloat(KEY_SIM_SIZE, DEFAULT_SIM_SIZE),
            binding.simSizeSlider.valueFrom,
            binding.simSizeSlider.valueTo,
            SIZE_STEP,
        )
        binding.simJitterInput.setText(
            prefs.getInt(KEY_SIM_JITTER, DEFAULT_SIM_JITTER).toString(),
        )
        binding.simOffsetXInput.setText(
            prefs.getInt(KEY_SIM_OFFSET_X, DEFAULT_SIM_OFFSET_X).toString(),
        )

        binding.grantButton.setOnClickListener { requestOverlayPermission() }
        binding.startButton.setOnClickListener { startForwarding() }
        binding.stopButton.setOnClickListener { stopForwarding() }
        binding.simulateButton.setOnClickListener {
            if (simRunning) stopForwarding() else startSimulating()
        }

        bindSimControls()
        updateSimButton()

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            notifPermissionLauncher.launch(android.Manifest.permission.POST_NOTIFICATIONS)
        }
    }

    override fun onResume() {
        super.onResume()
        StatusBus.addListener(statusListener)
        updatePermissionHint()
        // 服务已不在运行时纠正按钮状态（例如离开界面后服务被系统回收）
        if (StatusBus.current.message == "未运行") {
            simRunning = false
        }
        updateSimButton()
    }

    override fun onPause() {
        StatusBus.removeListener(statusListener)
        super.onPause()
    }

    private fun startForwarding() {
        val (host, port) = readTarget() ?: return

        if (!Settings.canDrawOverlays(this)) {
            toast("请先授予「显示在其他应用上层」权限")
            requestOverlayPermission()
            return
        }

        prefs.edit().putString(KEY_HOST, host).putInt(KEY_PORT, port).apply()
        simRunning = false
        updateSimButton()

        val intent = Intent(this, OverlayService::class.java)
            .putExtra(OverlayService.EXTRA_HOST, host)
            .putExtra(OverlayService.EXTRA_PORT, port)

        ContextCompat.startForegroundService(this, intent)
        moveTaskToBack(true)
    }

    /**
     * 模拟连接：不接管真实触摸、不显示覆盖层，因此也不需要悬浮窗权限；
     * 界面留在前台，方便一边看电脑端的调试小窗、一边改 prs / size。
     */
    private fun startSimulating() {
        val (host, port) = readTarget() ?: return

        syncSimInputs()
        prefs.edit()
            .putString(KEY_HOST, host)
            .putInt(KEY_PORT, port)
            .putFloat(KEY_SIM_PRESSURE, SimConfig.pressure)
            .putFloat(KEY_SIM_SIZE, SimConfig.size)
            .putInt(KEY_SIM_JITTER, SimConfig.jitterPx)
            .putInt(KEY_SIM_OFFSET_X, SimConfig.offsetXPx)
            .apply()

        val intent = Intent(this, OverlayService::class.java)
            .putExtra(OverlayService.EXTRA_HOST, host)
            .putExtra(OverlayService.EXTRA_PORT, port)
            .putExtra(OverlayService.EXTRA_SIMULATE, true)

        ContextCompat.startForegroundService(this, intent)
        simRunning = true
        updateSimButton()
        toast("已开始模拟：屏幕中心 prs=${numberToText(SimConfig.pressure)} size=${numberToText(SimConfig.size)}")
    }

    /** 读取并校验 IP / 端口；不合法时提示并返回 null。 */
    private fun readTarget(): Pair<String, Int>? {
        val host = binding.hostInput.text?.toString()?.trim().orEmpty()
        val port = binding.portInput.text?.toString()?.trim()?.toIntOrNull()
            ?: OverlayService.DEFAULT_PORT

        if (host.isEmpty()) {
            toast("请先填写电脑的 IP 地址")
            return null
        }
        if (port !in 1..65535) {
            toast("端口不合法")
            return null
        }
        return host to port
    }

    /** prs/size 滑块与 抖动/偏移 输入框都实时写进 SimConfig，模拟循环每帧现取，改完立即生效。 */
    private fun bindSimControls() {
        binding.simPressureSlider.addOnChangeListener { _, value, _ ->
            SimConfig.pressure = value
            updateSimLabels()
            updateSimButton()
        }
        binding.simSizeSlider.addOnChangeListener { _, value, _ ->
            SimConfig.size = value
            updateSimLabels()
            updateSimButton()
        }

        val watcher = object : TextWatcher {
            override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) = Unit
            override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) = Unit
            override fun afterTextChanged(s: Editable?) = syncSimInputs()
        }
        binding.simJitterInput.addTextChangedListener(watcher)
        binding.simOffsetXInput.addTextChangedListener(watcher)

        SimConfig.pressure = binding.simPressureSlider.value
        SimConfig.size = binding.simSizeSlider.value
        syncSimInputs()
        updateSimLabels()
    }

    /** 抖动 / 左右偏移两个手填输入框。 */
    private fun syncSimInputs() {
        SimConfig.jitterPx = (binding.simJitterInput.text?.toString()?.trim()?.toIntOrNull() ?: 0)
            .coerceIn(0, 2000)
        SimConfig.offsetXPx = (binding.simOffsetXInput.text?.toString()?.trim()?.toIntOrNull() ?: 0)
            .coerceIn(-10000, 10000)
        updateSimButton()
    }

    /** 把数值写到滑块上方的标签（滑块本身只在拖动时显示气泡）。 */
    private fun updateSimLabels() {
        binding.simPressureLabel.text = "压力 prs：${numberToText(SimConfig.pressure)}"
        binding.simSizeLabel.text = "接触面积 size：${numberToText(SimConfig.size)}"
    }

    /** 把上次保存的值对齐到滑块步长并裁剪到量程内，否则直接赋给 Slider 会抛异常。 */
    private fun snapToStep(value: Float, from: Float, to: Float, step: Float): Float {
        val clamped = value.coerceIn(from, to)
        val aligned = Math.round((clamped - from) / step) * step + from
        return aligned.coerceIn(from, to)
    }

    private fun updateSimButton() {
        binding.simulateButton.text = if (simRunning) {
            "停止模拟（prs=${numberToText(SimConfig.pressure)}  size=${numberToText(SimConfig.size)}）"
        } else {
            "开始模拟"
        }
    }

    /** 去掉多余的小数点（7.0 → 7）。 */
    private fun numberToText(value: Float): String =
        if (value == value.toLong().toFloat()) value.toLong().toString() else value.toString()

    private fun stopForwarding() {
        stopService(Intent(this, OverlayService::class.java))
        simRunning = false
        updateSimButton()
        StatusBus.set(false, "未运行", 0L)
    }

    private fun requestOverlayPermission() {
        val intent = Intent(
            Settings.ACTION_MANAGE_OVERLAY_PERMISSION,
            Uri.parse("package:$packageName"),
        )
        runCatching { startActivity(intent) }
            .onFailure { toast("无法打开悬浮窗权限设置页，请手动前往系统设置开启") }
    }

    private fun updatePermissionHint() {
        binding.grantButton.text = if (Settings.canDrawOverlays(this)) {
            "悬浮窗权限：已授予"
        } else {
            "授予「显示在其他应用上层」权限"
        }
    }

    private fun toast(message: String) {
        Toast.makeText(this, message, Toast.LENGTH_SHORT).show()
    }

    companion object {
        private const val PREFS = "touchbridge"
        private const val KEY_HOST = "host"
        private const val KEY_PORT = "port"
        private const val KEY_SIM_PRESSURE = "sim_pressure"
        private const val KEY_SIM_SIZE = "sim_size"
        private const val KEY_SIM_JITTER = "sim_jitter"
        private const val KEY_SIM_OFFSET_X = "sim_offset_x"
        private const val DEFAULT_SIM_PRESSURE = 7f
        private const val DEFAULT_SIM_SIZE = 400f
        private const val DEFAULT_SIM_JITTER = 10
        private const val DEFAULT_SIM_OFFSET_X = 0

        /** 必须与 activity_main.xml 里 Slider 的 stepSize 一致（用于把保存值对齐）。 */
        private const val PRESSURE_STEP = 0.1f
        private const val SIZE_STEP = 10f
    }
}
