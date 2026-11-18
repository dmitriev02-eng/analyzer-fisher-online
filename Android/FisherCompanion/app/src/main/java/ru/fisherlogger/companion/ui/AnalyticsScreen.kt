package ru.fisherlogger.companion.ui

import android.app.DatePickerDialog
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.FilterChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.nativeCanvas
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import org.json.JSONObject
import ru.fisherlogger.companion.AppViewModel
import java.util.Calendar
import kotlin.math.ceil
import kotlin.math.floor
import kotlin.math.log10
import kotlin.math.max
import kotlin.math.pow

private fun JSONObject?.hours(): IntArray? {
    if (this == null) return null
    val a = optJSONArray("hours") ?: return null
    return IntArray(24) { a.optInt(it) }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
fun AnalyticsScreen(vm: AppViewModel, wide: Boolean) {
    val a = vm.analytics
    val f = vm.filter
    var selHour by rememberSaveable { mutableIntStateOf(-1) }
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(horizontal = 12.dp, vertical = 8.dp)) {
        Text("Активность по игровым часам", style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold)
        Muted("Ось X — игровой час (оценка по модели), ось Y — " + if (f.percent) "% от своей серии" else "число записей")
        Spacer(Modifier.height(8.dp))
        FiltersPanel(vm)
        Spacer(Modifier.height(8.dp))
        vm.analyticsError?.let { Text("Ошибка: $it", color = Pal.Danger) }
        val mine = a?.optJSONObject("mine")
        val pub = a?.optJSONObject("pub")
        val cov = a?.optJSONArray("coverageMinutes")?.let { arr -> DoubleArray(24) { arr.optDouble(it) } }
        FlowRow {
            if (f.series != "pub") Legend(Pal.Mine, "Мои уловы (n=" + (mine?.optInt("total") ?: 0) + ")")
            if (f.series != "mine") Legend(Pal.Public, "Объявления (n=" + (pub?.optInt("total") ?: 0) + ")")
            Legend(Pal.Coverage, "Наблюдение, мин")
        }
        Spacer(Modifier.height(6.dp))
        val chartH = if (wide) 340.dp else 280.dp
        AppCard {
            HourChart(mine.hours(), pub.hours(), cov, f.percent, selHour, { selHour = it },
                Modifier.fillMaxWidth().height(chartH), wide)
        }
        if (selHour in 0..23 && a != null) {
            Spacer(Modifier.height(8.dp))
            AppCard {
                Text("Игровой час %02d:00–%02d:59".format(selHour, selHour), fontWeight = FontWeight.SemiBold, fontSize = 16.sp)
                mine?.let { m -> val h = m.hours()!![selHour]; val t = m.optInt("total")
                    Text("Мои уловы: $h" + if (t > 0) " (${pct(h, t)}% серии)" else "", color = Pal.Mine, fontSize = 15.sp) }
                pub?.let { p -> val h = p.hours()!![selHour]; val t = p.optInt("total")
                    Text("Объявления: $h" + if (t > 0) " (${pct(h, t)}% серии)" else "", color = Pal.Public, fontSize = 15.sp) }
                cov?.let { Text("Наблюдение: " + String.format(java.util.Locale.US, "%.1f", it[selHour]) + " мин реального времени", fontSize = 15.sp) }
            }
        }
        if (a != null) {
            Spacer(Modifier.height(8.dp))
            AppCard {
                Text("Итоги", fontWeight = FontWeight.SemiBold, fontSize = 16.sp)
                mine?.let { SeriesSummary("Мои уловы", Pal.Mine, it, false) }
                pub?.let { SeriesSummary("Объявления", Pal.Public, it, f.excludeOwn) }
                Text("Видов в выборке: " + a.optInt("species"), fontSize = 14.sp)
                Spacer(Modifier.height(6.dp))
                Muted("Модель игрового времени: " + a.optString("model"))
            }
            Spacer(Modifier.height(8.dp))
            AppCard {
                Text("Сводка (как в FisherLogger)", fontWeight = FontWeight.SemiBold, fontSize = 16.sp)
                Spacer(Modifier.height(4.dp))
                Text(a.optString("summary"), fontSize = 13.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            Spacer(Modifier.height(8.dp))
            AppCard { Text(a.optString("interpretation"), fontSize = 13.sp, color = MaterialTheme.colorScheme.onSurfaceVariant) }
        } else Muted("Нет данных аналитики — проверьте подключение.")
        Spacer(Modifier.height(16.dp))
    }
}

private fun pct(h: Int, t: Int) = (100.0 * h / t).let { String.format(java.util.Locale.US, "%.0f", it) }

@Composable
private fun SeriesSummary(name: String, color: Color, s: JSONObject, excl: Boolean) {
    Spacer(Modifier.height(6.dp))
    Text("$name: " + s.optInt("total") + " в графике", color = color, fontWeight = FontWeight.SemiBold, fontSize = 15.sp)
    Muted("без достоверного времени: " + s.optInt("missingTime") + " · у границы часа: " + s.optInt("nearBoundary") +
          (if (excl) " · исключено как мои: " + s.optInt("ownExcluded") else ""))
    val top = s.optJSONArray("top")
    val topText = if (top == null || top.length() == 0) "—" else (0 until top.length()).joinToString(" · ") {
        val t = top.getJSONObject(it); "%02d:00 — %d".format(t.getInt("hour"), t.getInt("count")) }
    Text("Чаще всего: $topText", fontSize = 14.sp)
    Text("n=" + s.optInt("total") + ": " + s.optString("sampleNote"), fontSize = 13.sp, color = Pal.Warn)
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun FiltersPanel(vm: AppViewModel) {
    val f = vm.filter
    val o = vm.options
    val ctx = LocalContext.current
    FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
        FilterChip(f.series == "both", { vm.updateFilter(f.copy(series = "both")) }, { Text("Обе серии") }, Modifier.heightIn(min = 40.dp))
        FilterChip(f.series == "mine", { vm.updateFilter(f.copy(series = "mine")) }, { Text("Мои") }, Modifier.heightIn(min = 40.dp))
        FilterChip(f.series == "pub", { vm.updateFilter(f.copy(series = "pub")) }, { Text("Объявления") }, Modifier.heightIn(min = 40.dp))
        FilterChip(f.percent, { vm.updateFilter(f.copy(percent = !f.percent)) }, { Text("Доли, %") }, Modifier.heightIn(min = 40.dp))
        FilterChip(f.excludeOwn, { vm.updateFilter(f.copy(excludeOwn = !f.excludeOwn)) }, { Text("Без моих в объявлениях") }, Modifier.heightIn(min = 40.dp))
        FilterChip(f.allSessions, { vm.updateFilter(f.copy(allSessions = !f.allSessions)) },
            { Text(if (f.allSessions) "Все сессии" else "Текущая сессия") }, Modifier.heightIn(min = 40.dp))
    }
    Spacer(Modifier.height(6.dp))
    Row(horizontalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.fillMaxWidth()) {
        // вид
        var spOpen by remember { mutableStateOf(false) }
        Box(Modifier.weight(1f)) {
            OutlinedButton({ spOpen = true }, Modifier.fillMaxWidth().heightIn(min = 48.dp)) { Text(f.speciesName, maxLines = 1) }
            DropdownMenu(spOpen, { spOpen = false }) {
                DropdownMenuItem({ Text("Все виды") }, { spOpen = false; vm.updateFilter(f.copy(speciesKey = null, speciesName = "Все виды")) })
                o?.optJSONArray("species").objs().forEach { s ->
                    DropdownMenuItem({ Text(s.optString("name") + " — мои " + s.optInt("mine") + ", публ. " + s.optInt("pub")) },
                        { spOpen = false; vm.updateFilter(f.copy(speciesKey = s.optString("key"), speciesName = s.optString("name"))) })
                }
            }
        }
        // водоём
        var wOpen by remember { mutableStateOf(false) }
        Box(Modifier.weight(1f)) {
            OutlinedButton({ wOpen = true }, Modifier.fillMaxWidth().heightIn(min = 48.dp)) { Text(f.locName, maxLines = 1) }
            DropdownMenu(wOpen, { wOpen = false }) {
                DropdownMenuItem({ Text("Все водоёмы") }, { wOpen = false; vm.updateFilter(f.copy(loc = null, locName = "Все водоёмы")) })
                o?.optJSONArray("waterbodies").objs().forEach { w ->
                    val id = if (w.isNull("id")) "none" else w.optInt("id").toString()
                    DropdownMenuItem({ Text(w.optString("name") + " — мои " + w.optInt("mine") + ", публ. " + w.optInt("pub")) },
                        { wOpen = false; vm.updateFilter(f.copy(loc = id, locName = w.optString("name"))) })
                }
            }
        }
    }
    Spacer(Modifier.height(6.dp))
    Row(horizontalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.fillMaxWidth()) {
        fun pick(initial: String?, onPick: (String) -> Unit) {
            val c = Calendar.getInstance()
            initial?.split("-")?.takeIf { it.size == 3 }?.let { c.set(it[0].toInt(), it[1].toInt() - 1, it[2].toInt()) }
            DatePickerDialog(ctx, { _, y, m, d -> onPick("%04d-%02d-%02d".format(y, m + 1, d)) },
                c.get(Calendar.YEAR), c.get(Calendar.MONTH), c.get(Calendar.DAY_OF_MONTH)).show()
        }
        OutlinedButton({ pick(f.from ?: o?.str("dateMin")) { vm.updateFilter(f.copy(from = it)) } }, Modifier.weight(1f).heightIn(min = 48.dp)) {
            Text("с " + (f.from ?: "…"), maxLines = 1) }
        OutlinedButton({ pick(f.to ?: o?.str("dateMax")) { vm.updateFilter(f.copy(to = it)) } }, Modifier.weight(1f).heightIn(min = 48.dp)) {
            Text("по " + (f.to ?: "…"), maxLines = 1) }
        if (f.from != null || f.to != null)
            OutlinedButton({ vm.updateFilter(f.copy(from = null, to = null)) }, Modifier.heightIn(min = 48.dp)) { Text("Весь период") }
    }
}

private fun niceStep(max: Double): Double {
    val raw = max / 5
    val p = 10.0.pow(floor(log10(raw.coerceAtLeast(1e-9))))
    for (m in doubleArrayOf(1.0, 2.0, 5.0, 10.0)) if (raw <= m * p) return (m * p).coerceAtLeast(1.0)
    return (10 * p).coerceAtLeast(1.0)
}

@Composable
fun HourChart(mine: IntArray?, pub: IntArray?, cov: DoubleArray?, percent: Boolean, sel: Int, onSel: (Int) -> Unit, modifier: Modifier, wide: Boolean) {
    val density = LocalDensity.current
    val textColor = MaterialTheme.colorScheme.onSurface.toArgb()
    val muted = MaterialTheme.colorScheme.onSurfaceVariant.toArgb()
    val grid = MaterialTheme.colorScheme.outline
    val selColor = MaterialTheme.colorScheme.primary.copy(alpha = 0.15f)
    val emptyBg = MaterialTheme.colorScheme.background
    val mt = mine?.sum() ?: 0
    val pt = pub?.sum() ?: 0
    fun v(a: IntArray?, t: Int, h: Int): Double = if (a == null) 0.0 else if (percent) (if (t == 0) 0.0 else 100.0 * a[h] / t) else a[h].toDouble()
    val empty = mt == 0 && pt == 0
    Canvas(modifier.pointerInput(Unit) {
        detectTapGestures { pos ->
            val left = with(density) { 34.dp.toPx() }
            val w = size.width - left - with(density) { 6.dp.toPx() }
            if (pos.x >= left) onSel(((pos.x - left) / (w / 24f)).toInt().coerceIn(0, 23))
        }
    }) {
        val left = 34.dp.toPx(); val right = 6.dp.toPx(); val top = 8.dp.toPx()
        val stripH = 18.dp.toPx(); val labelH = 16.dp.toPx()
        val bottom = labelH + stripH + 6.dp.toPx()
        val pw = size.width - left - right
        val ph = size.height - top - bottom
        val cw = pw / 24f
        var max = 0.0
        for (h in 0..23) { max = max(max, v(mine, mt, h)); max = max(max, v(pub, pt, h)) }
        val step = niceStep(max(max, if (percent) 10.0 else 4.0))
        val yMax = max(step, ceil(max / step) * step)
        val small = android.graphics.Paint().apply { isAntiAlias = true; textSize = 11.sp.toPx(); color = muted }
        val strong = android.graphics.Paint().apply { isAntiAlias = true; textSize = 11.sp.toPx(); color = textColor; isFakeBoldText = true }
        var y0 = 0.0
        while (y0 <= yMax + 1e-9) {
            val y = top + ph - (y0 / yMax * ph).toFloat()
            drawLine(grid, Offset(left, y), Offset(left + pw, y), 1f)
            val lab = if (percent) "%.0f%%".format(y0) else "%.0f".format(y0)
            drawContext.canvas.nativeCanvas.drawText(lab, left - small.measureText(lab) - 4.dp.toPx(), y + 4.dp.toPx(), small)
            y0 += step
        }
        if (sel in 0..23) drawRect(selColor, Offset(left + sel * cw, top), Size(cw, ph))
        val n = (if (mine != null) 1 else 0) + (if (pub != null) 1 else 0)
        val gap = max(1f, cw * 0.12f)
        val bw = (cw - 2 * gap) / max(1, n)
        val labelEvery = if (cw < 14.dp.toPx()) 3 else if (cw < 22.dp.toPx()) 2 else 1
        val labels = mutableListOf<Triple<String, Float, Float>>()
        for (h in 0..23) {
            val x0 = left + h * cw
            if (h % labelEvery == 0) {
                val hl = "%02d".format(h)
                drawContext.canvas.nativeCanvas.drawText(hl, x0 + cw / 2 - small.measureText(hl) / 2, top + ph + labelH - 3.dp.toPx(), small)
            }
            if (empty) continue
            var idx = 0
            var prevLabel: android.graphics.RectF? = null
            fun bar(a: IntArray?, t: Int, c: Color) {
                if (a == null) return
                val bx = x0 + gap + idx * bw; idx++
                if (a[h] == 0) return
                val bh = (v(a, t, h) / yMax * ph).toFloat()
                drawRect(c, Offset(bx, top + ph - bh), Size(max(1f, bw - 1f), bh))
                val cnt = a[h].toString()
                val tw = strong.measureText(cnt)
                val lx = bx + bw / 2 - tw / 2
                val ly = top + ph - bh - 3.dp.toPx()
                val rect = android.graphics.RectF(lx, ly - strong.textSize, lx + tw, ly)
                // подпись числа рисуется, если не налезает на подпись соседнего столбика того же часа
                if (prevLabel == null || !android.graphics.RectF.intersects(prevLabel!!, rect)) {
                    labels.add(Triple(cnt, lx, ly))
                    prevLabel = rect
                }
            }
            bar(mine, mt, Pal.Mine)
            bar(pub, pt, Pal.Public)
        }
        labels.forEach { (t, x, y) -> drawContext.canvas.nativeCanvas.drawText(t, x, y, strong) }
        // полоса наблюдения
        val sy = top + ph + labelH + 4.dp.toPx()
        val cmax = cov?.maxOrNull() ?: 0.0
        for (h in 0..23) {
            val m = cov?.get(h) ?: 0.0
            val c = if (m > 0 && cmax > 0) Pal.Coverage.copy(alpha = (0.2f + 0.7f * (m / cmax).toFloat())) else emptyBg
            drawRect(c, Offset(left + h * cw + 1f, sy), Size(cw - 2f, stripH))
        }
        if (empty) {
            val msg = "Недостаточно данных для выбранных фильтров"
            val p = android.graphics.Paint().apply { isAntiAlias = true; textSize = 14.sp.toPx(); color = muted }
            drawContext.canvas.nativeCanvas.drawText(msg, left + pw / 2 - p.measureText(msg) / 2, top + ph / 2, p)
        }
    }
}
