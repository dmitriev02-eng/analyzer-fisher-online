package ru.fisherlogger.companion.ui

import android.graphics.BitmapFactory
import android.util.Base64
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Clear
import androidx.compose.material.icons.filled.Search
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import org.json.JSONObject
import ru.fisherlogger.companion.AppViewModel
import ru.fisherlogger.companion.Screen

// ------------------------------------------------------------------ ANDROID-UX-01: общий стиль (как UiKit в Windows)

/** Цвета классов улова (windex 0–3), одинаковые в Советнике и на карте. */
fun gradeColor(w: Int): Color? = when (w) { 1 -> Pal.Mine; 2 -> Color(0xFF4DA3FF); 3 -> Pal.Warn; else -> null }
// GOAL-PHONE-01: названия классов как в игре и в Windows (MASTER-01): зелёный — хороший, синий — крупный, оранжевый — трофейный
/** DESIGN-03: «улов / улова / уловов». */
fun catchesWord(n: Int): String { val m10 = n % 10; val m100 = n % 100; return if (m10 == 1 && m100 != 11) "улов" else if (m10 in 2..4 && (m100 < 12 || m100 > 14)) "улова" else "уловов" }

fun gradeName(w: Int): String = when (w) { 0 -> "обычный"; 1 -> "хороший"; 2 -> "крупный"; 3 -> "★ трофейный"; else -> "" }
fun grams(g: Int): String = if (g >= 1000) String.format(java.util.Locale("ru"), "%.2f", g / 1000.0).trimEnd('0').trimEnd(',', '.') + " кг" else "$g г"

object IconCache {
    private val cache = HashMap<String, ImageBitmap?>()
    fun get(icons: JSONObject?, kind: String, id: String?): ImageBitmap? {
        if (icons == null || id.isNullOrEmpty() || id == "null") return null
        val k = "$kind/$id"
        if (cache.containsKey(k)) return cache[k]
        val b64 = icons.optJSONObject(kind)?.optString(id)?.takeIf { it.isNotEmpty() }
        val bmp = try { b64?.let { Base64.decode(it, Base64.DEFAULT) }?.let { BitmapFactory.decodeByteArray(it, 0, it.size)?.asImageBitmap() } } catch (e: Exception) { null }
        cache[k] = bmp
        return bmp
    }
    fun clear() = cache.clear()
}

@Composable
fun Pill(text: String, color: Color) {
    Box(Modifier.fillMaxWidth().clip(RoundedCornerShape(10.dp)).background(color.copy(alpha = 0.22f)).padding(horizontal = 12.dp, vertical = 9.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.size(width = 4.dp, height = 18.dp).background(color, RoundedCornerShape(2.dp)))
            Spacer(Modifier.width(10.dp))
            Text(text, fontWeight = FontWeight.SemiBold, fontSize = 14.sp, color = MaterialTheme.colorScheme.onSurface)
        }
    }
}

@Composable
fun SectionHead(title: String, hint: String = "") {
    Column(Modifier.fillMaxWidth().padding(top = 14.dp, bottom = 6.dp)) {
        Text(title, fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = MaterialTheme.colorScheme.primary)
        if (hint.isNotEmpty()) Text(hint, fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

/** Одна строка списка: иконка, название, пояснение, значение справа и (не всегда) полоса. */
@Composable
fun StatLine(title: String, sub: String, value: String, frac: Double?, accent: Color, muted: Boolean = false, icon: ImageBitmap? = null,
             onClick: (() -> Unit)? = null, titleLines: Int = 1) {
    val on = MaterialTheme.colorScheme.onSurface
    val alpha = if (muted) 0.55f else 1f
    Row(Modifier.fillMaxWidth().heightIn(min = 52.dp).let { if (onClick != null) it.clickable(onClick = onClick) else it }.padding(horizontal = 12.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically) {
        if (icon != null) { Image(icon, null, Modifier.size(34.dp)); Spacer(Modifier.width(10.dp)) }
        Column(Modifier.weight(1f)) {
            Text(title, fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = on.copy(alpha = alpha), maxLines = titleLines, overflow = TextOverflow.Ellipsis)
            if (sub.isNotEmpty()) Text(sub, fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = alpha), maxLines = 2, overflow = TextOverflow.Ellipsis)
            if (frac != null) {
                Spacer(Modifier.height(5.dp))
                Box(Modifier.fillMaxWidth().height(5.dp).clip(RoundedCornerShape(3.dp)).background(MaterialTheme.colorScheme.outline.copy(alpha = 0.35f))) {
                    Box(Modifier.fillMaxWidth(frac.toFloat().coerceIn(0.03f, 1f)).fillMaxHeight().background(accent.copy(alpha = alpha), RoundedCornerShape(3.dp)))
                }
            }
        }
        Spacer(Modifier.width(10.dp))
        Text(value, fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = accent.copy(alpha = alpha))
    }
}

@Composable
fun ListBlock(content: @Composable () -> Unit) {
    Column(Modifier.fillMaxWidth().clip(RoundedCornerShape(12.dp)).background(MaterialTheme.colorScheme.surfaceVariant)) { content() }
}

fun norm(s: String) = s.lowercase().replace('ё', 'е').trim()

/** Поиск латиницей: «ersh», «karas», «shchuka» → кириллица (английская раскладка на телефоне). */
fun translit(q: String): String {
    var s = q.lowercase()
    for ((a, b) in listOf("shch" to "щ", "sch" to "щ", "yo" to "е", "jo" to "е", "zh" to "ж", "kh" to "х", "ts" to "ц", "ch" to "ч", "sh" to "ш",
                          "yu" to "ю", "ju" to "ю", "ya" to "я", "ja" to "я", "ye" to "е")) s = s.replace(a, b)
    val one = mapOf('a' to "а", 'b' to "б", 'v' to "в", 'w' to "в", 'g' to "г", 'd' to "д", 'e' to "е", 'z' to "з", 'i' to "и", 'j' to "й", 'y' to "ы",
                    'k' to "к", 'l' to "л", 'm' to "м", 'n' to "н", 'o' to "о", 'p' to "п", 'r' to "р", 's' to "с", 't' to "т", 'u' to "у",
                    'f' to "ф", 'h' to "х", 'c' to "к", 'x' to "кс", 'q' to "к")
    return norm(s.map { one[it] ?: it.toString() }.joinToString(""))
}
fun nameMatches(name: String, q: String): Boolean {
    if (q.isEmpty()) return true
    val n = norm(name)
    if (n.contains(norm(q))) return true
    if (q.none { it in 'a'..'z' || it in 'A'..'Z' }) return false
    val t = translit(q)
    return n.contains(t) || n.contains(t.replace('ы', 'и')) || n.replace('й', 'и').contains(t.replace('ы', 'и').replace('й', 'и'))
}

// ------------------------------------------------------------------ экран «Советник»

@Composable
fun AdvisorScreen(vm: AppViewModel, wide: Boolean) {
    val adv = vm.advisor
    Column(Modifier.fillMaxSize()) {
        Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 8.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            @Composable
            fun Seg(label: String, on: Boolean, click: () -> Unit, m: Modifier) =
                if (on) Button(click, m.heightIn(min = 44.dp)) { Text(label, fontSize = 15.sp) }
                else OutlinedButton(click, m.heightIn(min = 44.dp)) { Text(label, fontSize = 15.sp) }
            Seg("Цель", vm.advisorTab == 0, { vm.advisorTab = 0 }, Modifier.weight(1f))
            Seg("Рыба", vm.advisorTab == 1, { vm.advisorTab = 1; vm.advisorHours = false }, Modifier.weight(1f))
            Seg("По часам", vm.advisorTab == 2, { vm.hoursFish = vm.advisorSel?.let { "id:$it" }; vm.hoursSel = -1; vm.advisorHours = true; vm.advisorTab = 2 }, Modifier.weight(1.2f))
        }
        if (vm.advisorTab == 0) { Box(Modifier.weight(1f)) { GoalScreen(vm, wide) }; return@Column }    // GOAL-PHONE-01
        if (vm.advisorTab == 2) { Box(Modifier.weight(1f)) { HoursScreen(vm, wide) }; return@Column }   // HOURS-01
        if (adv == null) {
            Box(Modifier.fillMaxSize().padding(24.dp), contentAlignment = Alignment.Center) {
                Muted(vm.advisorError ?: "Подключитесь к ПК хотя бы один раз — данные Советника сохранятся на телефоне.", 16)
            }
            return@Column
        }
        if (wide || vm.advisorSel == null) adv.optJSONObject("overview")?.let { o ->
            Text("Ваши данные: ${o.optInt("casts")} забросов · ${o.optInt("catches")} уловов · ${o.optInt("species")} видов · ${o.optInt("publicAnnouncements")} объявлений других игроков",
                fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(horizontal = 14.dp))
        }
        if (wide) {
            Row(Modifier.fillMaxSize().padding(top = 6.dp)) {
                Box(Modifier.width(330.dp).fillMaxHeight()) { SpeciesList(vm, adv) }
                Box(Modifier.weight(1f).fillMaxHeight()) { FishCard(vm, adv, wide = true) }
            }
        } else {
            if (vm.advisorSel == null) SpeciesList(vm, adv) else FishCard(vm, adv, wide = false)
        }
    }
}

@Composable
private fun SpeciesList(vm: AppViewModel, adv: JSONObject) {
    val all = adv.optJSONArray("species").objs()
    val q = vm.advisorQuery.trim()
    val shown = if (q.isEmpty()) all else all.filter { nameMatches(it.optString("name"), q) }
    val focus = androidx.compose.ui.platform.LocalFocusManager.current
    val order = listOf("Ловили вы", "Ловят другие игроки", "Справочник")
    val groups = shown.groupBy { it.optString("section") }
    Column(Modifier.fillMaxSize()) {
        OutlinedTextField(vm.advisorQuery, { vm.advisorQuery = it }, Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 6.dp),
            placeholder = { Text("Найти рыбу…") }, singleLine = true, leadingIcon = { Icon(Icons.Filled.Search, null) },
            trailingIcon = { if (vm.advisorQuery.isNotEmpty()) IconButton({ vm.advisorQuery = "" }) { Icon(Icons.Filled.Clear, "Очистить") } },
            keyboardOptions = KeyboardOptions(imeAction = ImeAction.Search),
            keyboardActions = androidx.compose.foundation.text.KeyboardActions(onSearch = { focus.clearFocus() }))
        LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(start = 12.dp, end = 12.dp, bottom = 16.dp)) {
            if (shown.isEmpty()) item { Muted("Ничего не найдено", 15) }
            for (sec in order) {
                val list = groups[sec] ?: continue
                item(key = "h:$sec") {
                    Text(sec.uppercase() + "  ·  " + list.size, fontSize = 12.sp, fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.primary,
                        modifier = Modifier.padding(top = 12.dp, bottom = 4.dp))
                }
                items(list, key = { "s:" + it.optInt("id") }) { s ->
                    val id = s.optInt("id")
                    val sel = vm.advisorSel == id
                    val sub = if (s.optBoolean("refOnly")) "только справочник" else if (s.optInt("own") > 0) "мои ${s.optInt("own")} · у других ${s.optInt("pub")}" else "у других ${s.optInt("pub")}"
                    Row(Modifier.fillMaxWidth().clip(RoundedCornerShape(10.dp))
                        .background(if (sel) MaterialTheme.colorScheme.secondaryContainer else Color.Transparent)
                        .clickable { focus.clearFocus(); vm.advisorSel = id; vm.hoursFish = "id:$id" }.heightIn(min = 54.dp).padding(horizontal = 8.dp, vertical = 6.dp),
                        verticalAlignment = Alignment.CenterVertically) {
                        val ic = IconCache.get(vm.icons, "fish", id.toString())
                        if (ic != null) Image(ic, null, Modifier.size(36.dp)) else Spacer(Modifier.size(36.dp))
                        Spacer(Modifier.width(10.dp))
                        Column(Modifier.weight(1f)) {
                            Text(s.optString("name"), fontSize = 16.sp, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            Text(sub, fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun FishCard(vm: AppViewModel, adv: JSONObject, wide: Boolean) {
    val id = vm.advisorSel ?: adv.optJSONArray("species").objs().firstOrNull()?.optInt("id")
    val c = id?.let { adv.optJSONObject("cards")?.optJSONObject(it.toString()) }
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(horizontal = 12.dp, vertical = 6.dp)) {
        if (!wide) TextButton({ vm.advisorSel = null }) { Icon(Icons.AutoMirrored.Filled.ArrowBack, null); Spacer(Modifier.width(6.dp)); Text("Все виды", fontSize = 15.sp) }
        if (c == null) { Muted("Выберите рыбу в списке", 16); return@Column }
        Row(verticalAlignment = Alignment.CenterVertically) {
            val ic = IconCache.get(vm.icons, "fish", id.toString())
            if (ic != null) { Image(ic, null, Modifier.size(56.dp)); Spacer(Modifier.width(12.dp)) }
            Column {
                Text(c.optString("name"), fontSize = 24.sp, fontWeight = FontWeight.SemiBold)
                val subs = mutableListOf("ваших поимок: ${c.optInt("own")}", "у других: ${c.optInt("pub")}")
                c.optString("bestClassText").takeIf { it.isNotEmpty() }?.let { subs += "лучший класс: $it" }
                c.optString("recordText").takeIf { it.isNotEmpty() }?.let { subs += "рекорд: $it" }
                Text(subs.joinToString("  ·  "), fontSize = 13.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
        Spacer(Modifier.height(10.dp))
        val st = c.optString("status")
        Pill((if (st == "advice") "✓  " else "") + c.optString("statusText"), when (st) { "advice" -> Pal.Mine; "reference" -> Pal.Warn; else -> MaterialTheme.colorScheme.outline })

        fun openMap(r: JSONObject) {
            val loc = r.optInt("locationId", -1)
            if (loc < 0 || r.isNull("locationId")) return
            if (!r.isNull("x") && !r.isNull("z")) vm.mapFocusReq = Triple(loc, r.optDouble("x"), r.optDouble("z"))
            vm.loadMap(loc); vm.screen = Screen.Maps
        }
        @Composable
        fun Rows(key: String, title: String, hint: String, icons: Boolean = false, clickable: Boolean = false) {
            val rows = c.optJSONArray(key).objs()
            if (rows.isEmpty()) return
            SectionHead(title, hint)
            ListBlock {
                rows.forEach { r ->
                    val advice = r.optBoolean("advice")
                    StatLine(r.optString("title"), r.optString("sub"), r.optString("value"),
                        if (r.isNull("frac")) null else r.optDouble("frac"), if (advice) Pal.Mine else Pal.Coverage, muted = !advice,
                        icon = if (icons) IconCache.get(vm.icons, "bait", r.opt("baitTid")?.toString()) else null,
                        onClick = if (clickable && !r.isNull("locationId")) ({ openMap(r) }) else null)
                }
            }
        }
        @Composable
        fun RefBlock() {
            c.optJSONObject("reference")?.let { rf ->
                val muted = rf.optBoolean("muted")
                SectionHead("Другие игроки", "справочник чужих уловов · не ваша статистика")
                ListBlock {
                    rf.optJSONArray("places").objs().forEach { r ->
                        // BRIDGE-FB-01: нажатие — карта этого водоёма с точками других игроков поверх (своим цветом)
                        val pts = r.optJSONArray("pts").objs().filter { !it.isNull("x") && !it.isNull("z") }
                        StatLine(r.optString("title"), r.optString("sub") + if (pts.isNotEmpty()) " · нажмите — на карте" else "", r.optString("value"), r.optDouble("frac"), Pal.Warn, muted,
                            onClick = if (!r.isNull("locationId")) ({
                                val loc = r.optInt("locationId")
                                vm.refHighlight = if (pts.isEmpty()) null
                                    else AppViewModel.RefHi(loc, c.optString("name"), pts.map { Triple(it.optDouble("x"), it.optDouble("z"), it.optString("g")) })
                                vm.loadMap(loc); vm.screen = Screen.Maps
                            }) else null)
                    }
                    if (rf.optJSONArray("baits").objs().isNotEmpty())
                        Text("НА ЧТО ЛОВЯТ", fontSize = 11.sp, fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onSurfaceVariant,
                            modifier = Modifier.padding(start = 12.dp, top = 8.dp))
                    rf.optJSONArray("baits").objs().forEach { r -> StatLine(r.optString("title"), "", r.optString("value"), r.optDouble("frac"), Pal.Warn, muted) }
                }
                val extra = mutableListOf<String>()
                rf.optJSONArray("periods")?.let { a -> if (a.length() > 0) extra += "время: " + (0 until a.length()).joinToString(", ") { a.getString(it) } }
                rf.optJSONArray("weather")?.let { a -> if (a.length() > 0) extra += "погода: " + (0 until a.length()).joinToString(", ") { a.getString(it) } }
                Spacer(Modifier.height(4.dp))
                Muted("Всего ${rf.optInt("count")} чужих поимок. " + extra.joinToString(" · "), 12)
            }
        }
        if (st != "advice") RefBlock()   // своих данных мало — ориентир сразу под заголовком
        Rows("places", "Где ловить", "ваши уловы · участок 20×20 м · нажмите — карта", clickable = true)
        Rows("waters", "Водоёмы", "доля поимок вида на ваших забросах", clickable = true)
        Rows("baits", "На что", "наживка · все водоёмы", icons = true)
        // BRIDGE-FB-01: вместо списка частей суток — схема по 24 игровым часам (свои и чужие уловы)
        WhenBlock(vm, id!!) { vm.hoursFish = "id:$id"; vm.hoursSel = -1; vm.advisorHours = true; vm.advisorTab = 2 }

        if (st == "advice") RefBlock()
        Spacer(Modifier.height(12.dp))
        Muted(adv.optString("note"), 12)
        val first = c.optJSONArray("places").objs().firstOrNull { !it.isNull("locationId") } ?: c.optJSONArray("waters").objs().firstOrNull { !it.isNull("locationId") }
        if (first != null) {
            Spacer(Modifier.height(8.dp))
            OutlinedButton({ openMap(first) }, Modifier.fillMaxWidth().heightIn(min = 48.dp)) { Text("Открыть карту водоёма") }
        }
        Spacer(Modifier.height(16.dp))
    }
}
