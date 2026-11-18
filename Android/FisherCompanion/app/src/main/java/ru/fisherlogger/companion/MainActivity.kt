package ru.fisherlogger.companion

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.viewModels
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import ru.fisherlogger.companion.ui.FisherApp

class MainActivity : ComponentActivity() {
    private val vm: AppViewModel by viewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        WindowCompat.setDecorFitsSystemWindows(window, false)
        if (android.os.Build.VERSION.SDK_INT >= 28) {
            window.attributes = window.attributes.apply {
                layoutInDisplayCutoutMode = android.view.WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES
            }
        }
        ru.fisherlogger.companion.ui.L.init(applicationContext)   // RELEASE-03
        setContent { FisherApp(vm) }
        // HOURS-01: самопроверка расчёта «по часам» против ПК (только для проверки: adb … --es selftest hours)
        if (intent?.getStringExtra("selftest") == "hours") Thread {
            val res = try {
                val d = org.json.JSONObject(java.io.File(filesDir, "selftest_hourdata.json").readText())
                val e = org.json.JSONArray(java.io.File(filesDir, "selftest_expected.json").readText())
                HourStatsK.selfTest(d, e)
            } catch (ex: Exception) { "HOURS selftest error: $ex" }
            java.io.File(filesDir, "selftest_result.txt").writeText(res)
        }.start()
    }

    // NEXT-ANDROID-03: опрос ПК только пока приложение на экране; поворот экрана опрос не прерывает (configChanges).
    override fun onStart() { super.onStart(); vm.setForeground(true) }
    override fun onStop() { vm.setForeground(false); super.onStop() }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) {
            // Полноэкранный режим: системные панели скрыты, появляются свайпом от края.
            val c = WindowInsetsControllerCompat(window, window.decorView)
            c.systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            c.hide(WindowInsetsCompat.Type.systemBars())
        }
    }
}
