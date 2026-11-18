package ru.fisherlogger.companion

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.core.content.FileProvider
import org.json.JSONObject
import java.io.File
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest

/** RELEASE-03: обновление приложения телефона через GitHub Releases (тот же выпуск, что у программы на ПК).
 *  В выпуске: FisherCompanion-X.Y.Z.apk и строка с его суммой в SHA256SUMS.txt. Без совпавшей суммы APK не открывается. */
object AppUpdate {
    /** owner/repo; пусто — проверка выключена. */
    const val REPO = "dmitriev02-eng/analyzer-fisher-online"
    data class Rel(val version: String, val apkUrl: String, val apkName: String, val sumsUrl: String)

    var busy by mutableStateOf(false); private set
    var message by mutableStateOf(""); private set
    var available by mutableStateOf<Rel?>(null); private set
    private val main = Handler(Looper.getMainLooper())

    fun parseVersion(v: String): List<Int> = v.trim().trimStart('v', 'V').substringBefore('-').split('.').let { p -> (0..2).map { p.getOrNull(it)?.toIntOrNull() ?: 0 } }
    fun newer(a: String, b: String): Boolean { val x = parseVersion(a); val y = parseVersion(b); for (i in 0..2) if (x[i] != y[i]) return x[i] > y[i]; return false }

    fun parse(json: String): Rel? {
        val o = JSONObject(json)
        if (o.optBoolean("draft") || o.optBoolean("prerelease")) return null
        val tag = o.optString("tag_name"); val assets = o.optJSONArray("assets") ?: return null
        var apk: Pair<String, String>? = null; var sums: String? = null
        for (i in 0 until assets.length()) {
            val a = assets.getJSONObject(i); val n = a.optString("name"); val u = a.optString("browser_download_url")
            if (n.startsWith("FisherCompanion-") && n.endsWith(".apk")) apk = n to u
            if (n == "SHA256SUMS.txt") sums = u
        }
        return if (apk == null || sums == null) null else Rel(tag.trimStart('v', 'V'), apk.second, apk.first, sums)
    }

    fun hashFor(sums: String, name: String): String? = sums.lineSequence().map { it.trim() }.filter { it.length > 66 }
        .firstOrNull { it.substring(64).trim().trimStart('*').trim() == name }?.substring(0, 64)?.uppercase()

    private fun get(url: String): HttpURLConnection = (URL(url).openConnection() as HttpURLConnection).apply {
        connectTimeout = 15000; readTimeout = 60000; instanceFollowRedirects = true
        setRequestProperty("User-Agent", "FisherCompanion/" + BuildConfig.VERSION_NAME); setRequestProperty("Accept", "application/vnd.github+json")
    }

    fun check(ctx: Context) {
        if (busy) return
        if (REPO.isBlank()) { message = "Источник обновлений не задан"; return }
        busy = true; message = "Проверяю…"
        Thread {
            val (rel, msg) = try {
                val c = get("https://api.github.com/repos/$REPO/releases/latest")
                when (c.responseCode) {
                    404 -> null to "Выпусков пока нет"
                    200 -> { val r = parse(c.inputStream.bufferedReader().readText())
                             if (r != null && newer(r.version, BuildConfig.VERSION_NAME)) r to ("Доступна версия " + r.version) else null to "У вас последняя версия" }
                    else -> null to ("GitHub ответил " + c.responseCode)
                }
            } catch (e: Exception) { null to ("Не удалось проверить: " + (e.message ?: e.javaClass.simpleName)) }
            main.post { available = rel; message = msg; busy = false }
        }.start()
    }

    fun downloadAndInstall(ctx: Context, rel: Rel) {
        if (busy) return
        busy = true; message = "Загрузка " + rel.apkName + "…"
        val app = ctx.applicationContext
        Thread {
            val res: String? = try {
                val want = hashFor(get(rel.sumsUrl).inputStream.bufferedReader().readText(), rel.apkName) ?: throw Exception("нет контрольной суммы")
                val dir = File(app.cacheDir, "updates").apply { mkdirs() }
                dir.listFiles()?.forEach { it.delete() }
                val f = File(dir, rel.apkName)
                val md = MessageDigest.getInstance("SHA-256")
                get(rel.apkUrl).inputStream.use { i -> f.outputStream().use { o -> val b = ByteArray(64 * 1024); while (true) { val n = i.read(b); if (n < 0) break; md.update(b, 0, n); o.write(b, 0, n) } } }
                val got = md.digest().joinToString("") { "%02X".format(it) }
                if (got != want) { f.delete(); throw Exception("контрольная сумма не совпала") }
                main.post { install(app, f) }
                null
            } catch (e: Exception) { "Не удалось обновить: " + (e.message ?: e.javaClass.simpleName) }
            main.post { busy = false; message = res ?: "Подтвердите установку" }
        }.start()
    }

    private fun install(ctx: Context, f: File) {
        if (Build.VERSION.SDK_INT >= 26 && !ctx.packageManager.canRequestPackageInstalls()) {
            message = "Разрешите установку из этого приложения и нажмите ещё раз"
            ctx.startActivity(Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:" + ctx.packageName)).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
            return
        }
        val uri = FileProvider.getUriForFile(ctx, ctx.packageName + ".updates", f)
        ctx.startActivity(Intent(Intent.ACTION_VIEW).setDataAndType(uri, "application/vnd.android.package-archive")
            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK))
    }
}
