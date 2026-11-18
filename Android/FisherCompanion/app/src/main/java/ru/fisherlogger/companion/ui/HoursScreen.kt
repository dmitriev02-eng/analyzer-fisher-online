package ru.fisherlogger.companion.ui

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.FilterChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.nativeCanvas
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import ru.fisherlogger.companion.AppViewModel
import ru.fisherlogger.companion.HourStatsK

/** HOURS-01: «По часам» на телефоне — водоём, рыба, период; 24 игровых часа; частая рыба по 3-часовым отрезкам; список рыбы по часу. */
@Composable
fun HoursScreen(vm: AppViewModel, wide: Boolean) {
    val data = vm.hourData
    if (data == null) {
        Box(Modifier.fillMaxSize().padding(24.dp), contentAlignment = Alignment.Center) {
            Muted(vm.hourError ?: "Подключитесь к ПК хотя бы один раз — данные по часам сохранятся на телефоне.", 16)
        }
        return
    }
    val names = remember(data) { data.recs.groupBy { it.key }.mapValues { it.value.first().fish } }
    // AUDIT-02: при выбранном водоёме — только рыба, которая там водится (записана там вами или есть в списке игры/справочнике)
    val fishList = remember(data, vm.hoursLoc) {
        val loc = vm.hoursLoc
        data.recs.groupBy { it.key }.entries
            .filter { e -> loc == null || e.value.any { it.mine && it.loc == loc } || data.habitat[e.key]?.contains(loc) == true }
            .sortedWith(compareByDescending<Map.Entry<String, List<HourStatsK.Rec>>> { e -> e.value.count { it.mine } }.thenByDescending { it.value.size }).map { it.key }
    }
    val locs = remember(data) { data.recs.mapNotNull { it.loc }.groupingBy { it }.eachCount().entries.sortedByDescending { it.value }.map { it.key } }
    fun locName(id: Int?) = if (id == null) "Все водоёмы" else vm.mapMeta(id)?.optString("name")?.takeIf { it.isNotEmpty() } ?: "водоём $id"
    val fish = vm.hoursFish
    val rep = remember(data, fish, vm.hoursLoc, vm.hoursPeriod, vm.hoursSeries) {
        HourStatsK.build(data, HourStatsK.Query(vm.hoursSeries, vm.hoursLoc, fish, vm.hoursPeriod, HourStatsK.nowText()))
    }
    val accent = if (rep.series == "pub") Pal.Public else Pal.Mine
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(horizontal = 12.dp, vertical = 4.dp)) {
        // фильтры
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            var wOpen by remember { mutableStateOf(false) }
            var fOpen by remember { mutableStateOf(false) }
            Box(Modifier.weight(1f)) {
                OutlinedButton({ wOpen = true }, Modifier.fillMaxWidth().heightIn(min = 44.dp)) { Text(locName(vm.hoursLoc), maxLines = 1, overflow = TextOverflow.Ellipsis) }
                DropdownMenu(wOpen, { wOpen = false }) {
                    DropdownMenuItem({ Text("Все водоёмы") }, { wOpen = false; vm.hoursLoc = null; vm.hoursSel = -1 })
                    locs.forEach { l -> DropdownMenuItem({ Text(locName(l)) }, { wOpen = false; vm.hoursLoc = l; vm.hoursSel = -1 }) }
                }
            }
            Box(Modifier.weight(1f)) {
                OutlinedButton({ fOpen = true }, Modifier.fillMaxWidth().heightIn(min = 44.dp)) {
                    Text(if (fish == null) "Вся рыба" else names[fish] ?: "нет записей", maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
                DropdownMenu(fOpen, { fOpen = false }) {
                    DropdownMenuItem({ Text("Вся рыба") }, { fOpen = false; vm.hoursFish = null; vm.hoursSel = -1 })
                    fishList.forEach { k -> DropdownMenuItem({ Text(names[k] ?: k) }, { fOpen = false; vm.hoursFish = k; vm.hoursSel = -1 }) }
                }
            }
        }
        Row(Modifier.horizontalScroll(rememberScrollState()).padding(top = 4.dp), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            listOf("auto" to "Авто", "day" to "Сутки", "3days" to "3 суток", "all" to "Всё время").forEach { (v, t) ->
                FilterChip(vm.hoursPeriod == v, { vm.hoursPeriod = v; vm.hoursSel = -1 }, { Text(t) }, Modifier.heightIn(min = 40.dp))
            }
            Spacer(Modifier.width(6.dp))
            listOf("auto" to "Чьи: авто", "mine" to "Мои", "pub" to "Других").forEach { (v, t) ->
                FilterChip(vm.hoursSeries == v, { vm.hoursSeries = v; vm.hoursSel = -1 }, { Text(t) }, Modifier.heightIn(min = 40.dp))
            }
        }
        Spacer(Modifier.height(6.dp))
        val head = (if (fish == null) "Вся рыба" else names[fish] ?: "Рыба") + " · " + rep.seriesText + " · " + rep.periodText
        Pill(head, when (rep.level) { "ok" -> Pal.Mine; "low" -> Pal.Warn; else -> MaterialTheme.colorScheme.outline })
        Text((if (rep.autoNote.isNotEmpty()) rep.autoNote + " " else "") + rep.levelText, fontSize = 13.sp,
            color = if (rep.level == "ok") MaterialTheme.colorScheme.onSurfaceVariant else Pal.Warn, modifier = Modifier.padding(top = 4.dp))
        Spacer(Modifier.height(8.dp))
        // частая рыба — по 3 игровых часа (только явный лидер)
        if (fish == null) {
            Text("Частая рыба по 3 часа · ≈ — несколько лидеров", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Row(Modifier.fillMaxWidth().padding(start = 28.dp, top = 2.dp)) {
                rep.hours3.forEach { b ->
                    Box(Modifier.weight(1f).height(34.dp), contentAlignment = Alignment.Center) {
                        val ic = b.icon?.let { IconCache.get(vm.icons, "fish", it.id?.toString()) }
                        when {
                            ic != null -> Image(ic, b.icon?.name, Modifier.size(32.dp))
                            b.icon != null -> Text(b.icon.name.take(3), fontSize = 10.sp)
                            b.tie -> Text("≈", fontSize = 16.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                            else -> Text("·", color = MaterialTheme.colorScheme.outline)
                        }
                    }
                }
            }
        }
        HourBarsK(rep, vm.hoursSel, accent, Modifier.fillMaxWidth().height(if (wide) 260.dp else 210.dp)) { vm.hoursSel = if (vm.hoursSel == it) -1 else it }
        Muted("Нижняя полоса — " + (if (rep.castsTotal > 0) "ваши забросы" else "время наблюдения") + "; серым — " + rep.lowText + ". Светлее — лучшие часы (" + rep.rateUnit + ").", 11)

        // список
        val sel = vm.hoursSel
        if (sel in 0..23) {
            val b = rep.hours[sel]
            SectionHead("Игровой час %02d:00–%02d:59".format(sel, sel),
                "${b.n} зап. · " + (if (rep.castsTotal > 0) casts(b.casts) else "наблюдение " + f1(b.cov) + " мин") +
                    (if (b.low) " · " + rep.lowText else if (b.n < HourStatsK.IconMinN) " · мало записей" else "") + (if (b.tie) " · явного лидера нет" else ""))
            SpList(vm, b.species, accent, b.low || rep.level != "ok" || b.n < HourStatsK.IconMinN)
        } else if (fish != null) {
            SectionHead("Когда ловится " + (names[fish] ?: ""), "лучшие часы сверху · нажмите на час на графике")
            val mx = maxOf(1, rep.hours.maxOf { it.n })
            ListBlock {
                rep.hours.filter { it.n > 0 }.sortedWith(compareBy<HourStatsK.Bin> { it.low }.thenByDescending { it.rate ?: 0.0 }.thenByDescending { it.n }).forEach { b ->
                    StatLine("%02d:00–%02d:59".format(b.h, b.h),
                        (if (rep.castsTotal > 0) casts(b.casts) else "наблюдение " + f1(b.cov) + " мин") + (b.rate?.let { " · " + f1(it) + " " + rep.rateUnit } ?: " · " + rep.lowText),
                        "${b.n} зап.", b.n.toDouble() / mx, accent, muted = b.low || rep.level != "ok")
                }
            }
        } else {
            SectionHead("Чаще всего за " + rep.periodText, if (rep.best.isNotEmpty()) "лучшие часы: " + rep.best.joinToString(", ") { "%02d:00".format(it) } else "нажмите на час — рыба этого часа")
            SpList(vm, rep.top, accent, rep.level != "ok")
        }
        Spacer(Modifier.height(8.dp))
        Muted(rep.note, 11)
        Spacer(Modifier.height(16.dp))
    }
}

private fun f1(v: Double) = String.format(java.util.Locale("ru"), "%.1f", v).removeSuffix(",0")
private fun casts(n: Int) = "$n " + plural(n, "заброс", "заброса", "забросов")

@Composable
private fun SpList(vm: AppViewModel, sp: List<HourStatsK.Sp>, accent: Color, weak: Boolean) {
    if (sp.isEmpty()) { Muted("Нет записей", 14); return }
    val mx = maxOf(1, sp.maxOf { it.n })
    ListBlock {
        sp.forEach { s ->
            StatLine(s.name, String.format(java.util.Locale("ru"), "%.0f%% записей", s.share * 100), "${s.n} зап.", s.n.toDouble() / mx, accent,
                muted = weak, icon = IconCache.get(vm.icons, "fish", s.id?.toString()))
        }
    }
}

/** 24 столбца по игровым часам; серые — мало забросов/наблюдений; светлее — лучшие часы; нажатие — выбор часа. */
@Composable
private fun HourBarsK(rep: HourStatsK.Report, sel: Int, accent: Color, modifier: Modifier, onTap: (Int) -> Unit) {
    val muted = MaterialTheme.colorScheme.onSurfaceVariant
    val grid = MaterialTheme.colorScheme.outline.copy(alpha = 0.35f)
    val selCol = MaterialTheme.colorScheme.primary.copy(alpha = 0.18f)
    Canvas(modifier.padding(top = 4.dp).pointerInput(rep) {
        detectTapGestures { p -> val left = 28.dp.toPx(); val w = (size.width - left) / 24f; if (p.x >= left) onTap(((p.x - left) / w).toInt().coerceIn(0, 23)) }
    }) {
        val left = 28.dp.toPx(); val bottomPad = 34.dp.toPx(); val topPad = 14.dp.toPx()
        val ph = size.height - bottomPad - topPad
        val w = (size.width - left) / 24f
        val max = maxOf(1, rep.hours.maxOf { it.n })
        val step = when { max <= 4 -> 1; max <= 10 -> 2; max <= 25 -> 5; max <= 60 -> 10; max <= 150 -> 25; else -> 50 }
        val top = ((max + step - 1) / step) * step
        val paint = android.graphics.Paint().apply { color = muted.toArgb(); textSize = 9.sp.toPx(); isAntiAlias = true }
        var v = 0
        while (v <= top) {
            val y = topPad + ph - v.toFloat() / top * ph
            drawLine(grid, Offset(left, y), Offset(size.width, y), 1f)
            drawContext.canvas.nativeCanvas.drawText(v.toString(), 2f, y + 3.sp.toPx(), paint)
            v += step
        }
        val best = rep.best.toSet()
        val effMax = if (rep.castsTotal > 0) maxOf(1, rep.hours.maxOf { it.casts }).toFloat() else maxOf(1.0, rep.hours.maxOf { it.cov }).toFloat()
        rep.hours.forEach { b ->
            val x = left + b.h * w
            if (b.h == sel) drawRect(selCol, Offset(x, 0f), Size(w, topPad + ph))
            val bh = b.n.toFloat() / top * ph
            val c = if (b.low) muted.copy(alpha = 0.45f) else if (b.h in best) lerpC(accent, Color.White, 0.3f) else accent
            drawRect(c, Offset(x + w * 0.15f, topPad + ph - bh), Size(w * 0.7f, bh))
            if (b.n > 0 && (b.h % 1 == 0)) drawContext.canvas.nativeCanvas.drawText(b.n.toString(), x + w * 0.5f - paint.measureText(b.n.toString()) / 2, topPad + ph - bh - 2.dp.toPx(), paint)
            if (b.h % 3 == 0) drawContext.canvas.nativeCanvas.drawText("%02d".format(b.h), x, topPad + ph + 12.sp.toPx(), paint)
            val e = if (rep.castsTotal > 0) b.casts.toFloat() else b.cov.toFloat()
            val eh = maxOf(1.5f, 6.dp.toPx() * e / effMax)
            drawRect(if (b.low) Pal.Danger.copy(alpha = 0.8f) else Pal.Coverage, Offset(x + 1f, topPad + ph + 18.dp.toPx() + (6.dp.toPx() - eh)), Size(w - 2f, eh))
        }
    }
}

private fun lerpC(a: Color, b: Color, t: Float) = Color(a.red + (b.red - a.red) * t, a.green + (b.green - a.green) * t, a.blue + (b.blue - a.blue) * t, 1f)
