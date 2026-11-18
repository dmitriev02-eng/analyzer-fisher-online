package ru.fisherlogger.companion.ui

import android.content.Context
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Button
import androidx.compose.material3.LocalTextStyle
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.TextLayoutResult
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.TextUnit
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import org.json.JSONObject
import ru.fisherlogger.companion.AppUpdate
import ru.fisherlogger.companion.BuildConfig

/** RELEASE-03: язык интерфейса телефона. Тексты в коде русские; на английском подменяются при показе (в т.ч. тексты с ПК). */
object L {
    var lang by mutableStateOf("ru")
        private set
    var chosen by mutableStateOf(true)
        private set
    private var map: Map<String, String> = emptyMap()
    private var frag: Regex? = null
    private val cache = java.util.concurrent.ConcurrentHashMap<String, String>()
    private val cyr = Regex("[А-Яа-яЁё]")
    private const val PREFS = "lang"

    fun init(ctx: Context) {
        val p = ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        chosen = p.contains("lang")
        lang = p.getString("lang", "ru") ?: "ru"
        try {
            val o = JSONObject(ctx.assets.open("ui_en.json").bufferedReader(Charsets.UTF_8).readText())
            val m = HashMap<String, String>(o.length() * 2)
            o.keys().forEach { k -> m[k] = o.getString(k) }
            map = m
            val keys = m.keys.filter { it.trim().length >= 3 && cyr.containsMatchIn(it) }.sortedByDescending { it.length }
            frag = if (keys.isEmpty()) null else Regex(keys.joinToString("|") { k ->
                (if (cyr.matches(k.take(1))) "(?<![А-Яа-яЁё])" else "") + Regex.escape(k) + (if (cyr.matches(k.takeLast(1))) "(?![А-Яа-яЁё])" else "") })
        } catch (_: Exception) { }
    }

    fun set(ctx: Context, l: String) {
        lang = if (l == "en") "en" else "ru"; chosen = true; cache.clear()
        ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putString("lang", lang).apply()
    }

    fun t(s: String): String {
        if (lang != "en" || s.isEmpty() || !cyr.containsMatchIn(s)) return s
        cache[s]?.let { return it }
        val r = map[s] ?: frag?.replace(s) { m -> map[m.value] ?: m.value } ?: s
        if (cache.size > 20000) cache.clear()
        cache[s] = r
        return r
    }
}

/** Тот же Text, что в Material 3, но с переводом строки. В файлах ui импорт material3.Text убран — используется этот. */
@Composable
fun Text(
    text: String, modifier: Modifier = Modifier, color: Color = Color.Unspecified, fontSize: TextUnit = TextUnit.Unspecified,
    fontStyle: FontStyle? = null, fontWeight: FontWeight? = null, fontFamily: FontFamily? = null, letterSpacing: TextUnit = TextUnit.Unspecified,
    textDecoration: TextDecoration? = null, textAlign: TextAlign? = null, lineHeight: TextUnit = TextUnit.Unspecified,
    overflow: TextOverflow = TextOverflow.Clip, softWrap: Boolean = true, maxLines: Int = Int.MAX_VALUE, minLines: Int = 1,
    onTextLayout: ((TextLayoutResult) -> Unit)? = null, style: TextStyle = LocalTextStyle.current
) {
    val shown = if (L.lang == "en") L.t(text) else text
    androidx.compose.material3.Text(text = shown, modifier = modifier, color = color, fontSize = fontSize, fontStyle = fontStyle, fontWeight = fontWeight,
        fontFamily = fontFamily, letterSpacing = letterSpacing, textDecoration = textDecoration, textAlign = textAlign, lineHeight = lineHeight,
        overflow = overflow, softWrap = softWrap, maxLines = maxLines, minLines = minLines, onTextLayout = onTextLayout ?: {}, style = style)
}

@Composable
fun Text(
    text: AnnotatedString, modifier: Modifier = Modifier, color: Color = Color.Unspecified, fontSize: TextUnit = TextUnit.Unspecified,
    fontWeight: FontWeight? = null, textAlign: TextAlign? = null, overflow: TextOverflow = TextOverflow.Clip, maxLines: Int = Int.MAX_VALUE,
    style: TextStyle = LocalTextStyle.current
) = androidx.compose.material3.Text(text, modifier, color, fontSize, fontWeight = fontWeight, textAlign = textAlign, overflow = overflow, maxLines = maxLines, style = style)

/** Первый запуск: выбор языка. */
@Composable
fun LanguageChooser(ctx: Context) {
    Column(Modifier.fillMaxSize().padding(32.dp), verticalArrangement = Arrangement.Center, horizontalAlignment = Alignment.CenterHorizontally) {
        androidx.compose.material3.Text("Анализатор Fisher Online", style = MaterialTheme.typography.headlineSmall)
        Spacer(Modifier.height(12.dp))
        androidx.compose.material3.Text("Выберите язык / Choose language", fontSize = 17.sp)
        Spacer(Modifier.height(24.dp))
        Button(onClick = { L.set(ctx, "ru") }, modifier = Modifier.fillMaxWidth().heightIn(min = 56.dp)) { androidx.compose.material3.Text("Русский", fontSize = 18.sp) }
        Spacer(Modifier.height(12.dp))
        Button(onClick = { L.set(ctx, "en") }, modifier = Modifier.fillMaxWidth().heightIn(min = 56.dp)) { androidx.compose.material3.Text("English", fontSize = 18.sp) }
    }
}

/** Вкладка «ПК»: язык, версия и обновление приложения через GitHub. */
@Composable
fun AboutCard(ctx: Context) {
    AppCard {
        Text("Приложение " + BuildConfig.VERSION_NAME, fontWeight = FontWeight.SemiBold, fontSize = 16.sp)
        Spacer(Modifier.height(6.dp))
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            OutlinedButton(onClick = { L.set(ctx, if (L.lang == "en") "ru" else "en") }, modifier = Modifier.heightIn(min = 48.dp)) {
                androidx.compose.material3.Text(if (L.lang == "en") "Русский" else "English")
            }
            OutlinedButton(onClick = { AppUpdate.check(ctx) }, enabled = !AppUpdate.busy, modifier = Modifier.heightIn(min = 48.dp)) {
                Text("Проверить обновления")
            }
        }
        if (AppUpdate.message.isNotEmpty()) { Spacer(Modifier.height(6.dp)); Text(AppUpdate.message, fontSize = 14.sp) }
        AppUpdate.available?.let { rel ->
            Spacer(Modifier.height(6.dp))
            Button(onClick = { AppUpdate.downloadAndInstall(ctx, rel) }, enabled = !AppUpdate.busy, modifier = Modifier.fillMaxWidth().heightIn(min = 52.dp)) {
                Text("Скачать и установить " + rel.version)
            }
        }
    }
}
