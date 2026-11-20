package ru.fisherlogger.companion

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.RectF
import android.os.Build
import android.os.IBinder
import android.text.SpannableString
import android.text.Spanned
import android.text.style.ForegroundColorSpan
import android.text.style.StyleSpan
import android.util.Base64
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import org.json.JSONObject
import ru.fisherlogger.companion.ui.L

/** FEAT-06: то, что служба берёт у приложения (подключение к ПК и иконки рыб). Служба живёт в том же процессе. */
object Shared {
    @Volatile var api: FisherApi? = null
    @Volatile var icons: JSONObject? = null
}

/**
 * FEAT-06: уведомления о поклёвках, пока приложение свёрнуто или экран выключен.
 * Раз в 3 с спрашивает у ПК новые события (/api/events). Уведомление: «Удочка 2 · Кои широ», вес и класс,
 * картинка рыбы в рамке цвета класса, название цветом класса. Пустые поклёвки — по настройке.
 * Постоянное уведомление «Слежу за поклёвками» обязательно для фоновой работы в Android (его можно скрыть в настройках канала).
 * FEAT-07: уведомление тихое (канал MIN), в нём кнопка «Остановить»; служба сама останавливается, если ПК не отвечает 5 минут
 * (программа на ПК закрыта, ПК выключен, телефон вне сети). При следующем открытии приложения слежение включится снова.
 */
class BiteService : Service() {
    companion object {
        const val CH_BITES = "bites"; const val CH_RUN = "watch"; const val RUN_ID = 1
        const val ACTION_STOP = "ru.fisherlogger.companion.STOP_WATCH"
        /** FEAT-07: сколько ждать ответа ПК, прежде чем остановиться и убрать уведомление. */
        const val IDLE_STOP_MS = 5 * 60_000L
        const val PREFS = "bite_alerts"
        fun enabled(ctx: Context) = ctx.getSharedPreferences(PREFS, MODE_PRIVATE).getBoolean("on", false)
        fun empties(ctx: Context) = ctx.getSharedPreferences(PREFS, MODE_PRIVATE).getBoolean("empties", false)
        fun set(ctx: Context, on: Boolean, empties: Boolean = empties(ctx)) {
            ctx.getSharedPreferences(PREFS, MODE_PRIVATE).edit().putBoolean("on", on).putBoolean("empties", empties).apply()
            val i = Intent(ctx, BiteService::class.java)
            if (on) { if (Build.VERSION.SDK_INT >= 26) ctx.startForegroundService(i) else ctx.startService(i) } else ctx.stopService(i)
        }
        fun channels(ctx: Context) {
            if (Build.VERSION.SDK_INT < 26) return
            val nm = ctx.getSystemService(NotificationManager::class.java)
            nm.createNotificationChannel(NotificationChannel(CH_BITES, L.t("Поклёвки"), NotificationManager.IMPORTANCE_HIGH).apply {
                description = L.t("Поклёвка на удочке: рыба, вес и класс"); enableVibration(true) })
            nm.createNotificationChannel(NotificationChannel(CH_RUN, L.t("Слежение за поклёвками"), NotificationManager.IMPORTANCE_MIN).apply {
                description = L.t("Нужно Android, чтобы приложение работало в фоне"); setShowBadge(false) })
        }
    }

    @Volatile private var running = false
    private var thread: Thread? = null
    private var last = -1L

    override fun onBind(intent: Intent?): IBinder? = null

    @Volatile private var lastOk = System.currentTimeMillis()

    private fun runNote(text: String): Notification {
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP), PendingIntent.FLAG_IMMUTABLE)
        val stop = PendingIntent.getService(this, 2, Intent(this, BiteService::class.java).setAction(ACTION_STOP), PendingIntent.FLAG_IMMUTABLE)
        return NotificationCompat.Builder(this, CH_RUN).setSmallIcon(android.R.drawable.ic_menu_view)
            .setContentTitle(L.t("Слежу за поклёвками")).setContentText(text)
            .setOngoing(true).setSilent(true).setShowWhen(false).setContentIntent(open).setPriority(NotificationCompat.PRIORITY_MIN)
            .addAction(0, L.t("Остановить"), stop).build()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        channels(this)
        val n = runNote(L.t("Уведомлю, когда клюнет на любой удочке"))
        if (Build.VERSION.SDK_INT >= 34) startForeground(RUN_ID, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC) else startForeground(RUN_ID, n)
        if (intent?.action == ACTION_STOP) { stopNow(); return START_NOT_STICKY }   // «Остановить» в уведомлении
        lastOk = System.currentTimeMillis()
        if (!running) { running = true; thread = Thread(::loop, "bite-watch").also { it.isDaemon = true; it.start() } }
        return START_NOT_STICKY   // FEAT-07: без приложения (нет подключения) перезапускать незачем
    }

    private fun stopNow() {
        running = false; thread?.interrupt()
        if (Build.VERSION.SDK_INT >= 24) stopForeground(STOP_FOREGROUND_REMOVE) else @Suppress("DEPRECATION") stopForeground(true)
        stopSelf()
    }

    override fun onDestroy() { running = false; thread?.interrupt(); super.onDestroy() }

    private fun loop() {
        while (running) {
            try {
                val api = Shared.api
                if (api?.active != null) {
                    val j = api.getJson("/api/events?since=$last")
                    val ev = j.optJSONArray("events")
                    if (last >= 0 && ev != null) for (i in 0 until ev.length()) ev.optJSONObject(i)?.let { show(it) }
                    last = maxOf(last, j.optLong("last", last))
                    lastOk = System.currentTimeMillis()
                }
            } catch (_: InterruptedException) { return } catch (_: Exception) { }
            // FEAT-07: ПК молчит 5 минут — останавливаемся, уведомление уходит
            if (System.currentTimeMillis() - lastOk > IDLE_STOP_MS) { android.os.Handler(mainLooper).post { stopNow() }; return }
            try { Thread.sleep(3000) } catch (_: InterruptedException) { return }
        }
    }

    private fun show(e: JSONObject) {
        val kind = e.optString("kind")
        if (kind == "empty_bite" && !empties(this)) return
        if (kind == "catch") return                      // о поклёвке уже сообщили; улов виден в журнале
        val w = if (e.isNull("windex")) null else e.optInt("windex")
        val rod = if (e.isNull("rod")) null else e.optInt("rod")
        val fish = L.t(e.optString("fish").ifEmpty { "рыба" })
        val kg = if (e.isNull("weight")) "" else String.format(java.util.Locale.US, "%.2f", e.optInt("weight") / 1000.0).replace('.', ',') + " " + L.t("кг")
        val head = (if (rod != null) L.t("Удочка") + " $rod · " else "") + fish
        val title = SpannableString(head).apply {
            val start = head.length - fish.length
            setSpan(ForegroundColorSpan(frame(w)), start, head.length, Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
            setSpan(StyleSpan(android.graphics.Typeface.BOLD), 0, head.length, Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
        }
        val what = if (kind == "empty_bite") L.t("пустая поклёвка") else L.t("поклёвка")
        val text = listOf(kg, grade(w), what).filter { it.isNotEmpty() }.joinToString(" · ")
        val open = PendingIntent.getActivity(this, 1, Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP), PendingIntent.FLAG_IMMUTABLE)
        val b = NotificationCompat.Builder(this, CH_BITES).setSmallIcon(android.R.drawable.star_on)
            .setContentTitle(title).setContentText(text).setColor(frame(w)).setLargeIcon(framedIcon(e.optInt("fishId", -1), w))
            .setCategory(NotificationCompat.CATEGORY_ALARM).setPriority(NotificationCompat.PRIORITY_HIGH).setAutoCancel(true)
            .setContentIntent(open).setTimeoutAfter(10 * 60_000L).setOnlyAlertOnce(false)
        // одна удочка — одно уведомление: новая поклёвка заменяет прежнюю
        try { NotificationManagerCompat.from(this).notify(100 + (rod ?: 0), b.build()) } catch (_: SecurityException) { }
    }

    private fun grade(w: Int?) = when (w) { 0 -> L.t("обычный"); 1 -> L.t("хороший"); 2 -> L.t("крупный"); 3 -> L.t("трофейный"); else -> "" }
    private fun frame(w: Int?) = when (w) { 0 -> 0xFF969696.toInt(); 1 -> 0xFF4CAF50.toInt(); 2 -> 0xFF4285F4.toInt(); 3 -> 0xFFF7C84A.toInt(); else -> 0xFF5E6E72.toInt() }

    /** Картинка рыбы на тёмном круге в рамке цвета класса (трофей — золото с внешним свечением). */
    private fun framedIcon(fishId: Int, w: Int?): Bitmap {
        val size = 192; val out = Bitmap.createBitmap(size, size, Bitmap.Config.ARGB_8888); val c = Canvas(out)
        val p = Paint(Paint.ANTI_ALIAS_FLAG)
        if (w == 3) { p.color = 0x55F7C84A; c.drawCircle(size / 2f, size / 2f, size / 2f, p) }
        p.color = 0xFF111E21.toInt(); c.drawCircle(size / 2f, size / 2f, size / 2f - 10f, p)
        p.style = Paint.Style.STROKE; p.strokeWidth = 12f; p.color = frame(w); c.drawCircle(size / 2f, size / 2f, size / 2f - 12f, p)
        try {
            val b64 = Shared.icons?.optJSONObject("fish")?.optString(fishId.toString())
            if (!b64.isNullOrEmpty()) {
                val raw = Base64.decode(b64.substringAfter(","), Base64.DEFAULT)
                BitmapFactory.decodeByteArray(raw, 0, raw.size)?.let { ic ->
                    val k = minOf(120f / ic.width, 120f / ic.height)
                    val dw = ic.width * k; val dh = ic.height * k
                    c.drawBitmap(ic, null, RectF((size - dw) / 2, (size - dh) / 2, (size + dw) / 2, (size + dh) / 2), Paint(Paint.FILTER_BITMAP_FLAG))
                }
            }
        } catch (_: Exception) { }
        return out
    }
}
