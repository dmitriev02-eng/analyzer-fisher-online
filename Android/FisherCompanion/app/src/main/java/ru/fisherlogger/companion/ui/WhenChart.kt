package ru.fisherlogger.companion.ui

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.background
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.nativeCanvas
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import ru.fisherlogger.companion.AppViewModel
import ru.fisherlogger.companion.HourStatsK

/**
 * BRIDGE-FB-01: «Когда» в карточке рыбы — схема по 24 игровым часам: доля записей вида в каждом часе,
 * отдельно ваши уловы (зелёный) и объявления других игроков (синий), всё время, все водоёмы.
 * Доли, а не штуки, — чтобы серии разного размера были сравнимы. Серым — час, где ваших забросов мало
 * (это «мало данных», а не «не ловится»). Частота записей, не вероятность клёва. Тот же расчёт, что «По часам» (HourStatsK).
 */
object WhenK {
    class Result(val mine: HourStatsK.Report, val pub: HourStatsK.Report, val bestText: String, val bestFrom: String)

    fun build(d: HourStatsK.Data, fishKey: String, now: String): Result {
        val m = HourStatsK.build(d, HourStatsK.Query("mine", null, fishKey, "all", now))
        val p = HourStatsK.build(d, HourStatsK.Query("pub", null, fishKey, "all", now))
        fun hrs(r: HourStatsK.Report) = r.best.sorted().joinToString(", ") { "%02d:00".format(it) }
        val (txt, from) = when {
            m.level == "ok" && m.best.isNotEmpty() -> hrs(m) to "по вашим уловам"
            p.level == "ok" && p.best.isNotEmpty() -> hrs(p) to "по объявлениям других игроков"
            else -> "" to ""
        }
        return Result(m, p, txt, from)
    }
}

@Composable
fun WhenBlock(vm: AppViewModel, fishId: Int, onOpenHours: () -> Unit) {
    val data = vm.hourData
    SectionHead("Когда", "игровое время · доля поимок по часам · нажмите — подробно «По часам»")
    if (data == null) { Muted(vm.hourError ?: "Нет данных по часам — подключитесь к ПК.", 13); return }
    val r = remember(data, fishId) { WhenK.build(data, "id:$fishId", HourStatsK.nowText()) }
    if (r.mine.total == 0 && r.pub.total == 0) { Muted("Записей с игровым временем для этой рыбы пока нет.", 13); return }
    Column(Modifier.fillMaxWidth().clip(RoundedCornerShape(12.dp)).background(MaterialTheme.colorScheme.surfaceVariant)
        .clickable(onClick = onOpenHours).padding(horizontal = 8.dp, vertical = 8.dp)) {
        WhenChartK(r, Modifier.fillMaxWidth().height(170.dp))
        Row(Modifier.padding(top = 4.dp, start = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.size(10.dp).background(Pal.Mine, RoundedCornerShape(2.dp)))
            Text("  мои уловы: ${r.mine.total}      ", fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Box(Modifier.size(10.dp).background(Pal.Public, RoundedCornerShape(2.dp)))
            Text("  другие игроки: ${r.pub.total}", fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        // вывод — отдельно по каждой серии, цветом серии; при нехватке данных так и сказано
        @Composable
        fun Line(rep: HourStatsK.Report, who: String, col: androidx.compose.ui.graphics.Color) {
            if (rep.total == 0) return
            val t = if (rep.level == "ok" && rep.best.isNotEmpty()) "$who: чаще всего " + rep.best.sorted().joinToString(", ") { "%02d:00".format(it) }
                    else "$who: мало данных для вывода (${rep.total} зап.)"
            Text(t, fontSize = 14.sp, color = if (rep.level == "ok" && rep.best.isNotEmpty()) col else Pal.Warn, modifier = Modifier.padding(top = 2.dp, start = 4.dp))
        }
        Line(r.mine, "Ваши уловы", Pal.Mine)
        Line(r.pub, "Другие игроки", Pal.Public)
    }
    Muted("Высота — доля записей этой рыбы в часе (свои и чужие отдельно). «Чаще всего» — с поправкой на число забросов / время наблюдения, " +
          "поэтому может не совпадать с самым высоким столбцом. Серым — мало ваших забросов. Не вероятность клёва.", 11)
}

@Composable
private fun WhenChartK(r: WhenK.Result, modifier: Modifier) {
    val muted = MaterialTheme.colorScheme.onSurfaceVariant
    val grid = MaterialTheme.colorScheme.outline.copy(alpha = 0.45f)
    val bandA = MaterialTheme.colorScheme.background.copy(alpha = 0.35f)
    Canvas(modifier) {
        val left = 4.dp.toPx(); val bottomPad = 18.dp.toPx(); val topPad = 16.dp.toPx()
        val ph = size.height - bottomPad - topPad
        val w = (size.width - left) / 24f
        val paint = android.graphics.Paint().apply { color = muted.toArgb(); textSize = 10.sp.toPx(); isAntiAlias = true }
        // части суток — фон и подписи
        listOf(0 to "ночь", 6 to "утро", 12 to "день", 18 to "вечер").forEachIndexed { i, (h, t) ->
            if (i % 2 == 0) drawRect(bandA, Offset(left + h * w, 0f), Size(6 * w, topPad + ph))
            drawContext.canvas.nativeCanvas.drawText(t, left + h * w + 3.dp.toPx(), 11.sp.toPx(), paint)
        }
        val mt = maxOf(1, r.mine.total).toFloat(); val pt = maxOf(1, r.pub.total).toFloat()
        val maxShare = maxOf(0.01f, r.mine.hours.maxOf { it.n / mt }, r.pub.hours.maxOf { it.n / pt })
        drawLine(grid, Offset(left, topPad + ph), Offset(size.width, topPad + ph), 1f)
        val bestM = r.mine.best.toSet(); val bestP = r.pub.best.toSet()
        for (h in 0 until 24) {
            val x = left + h * w
            val bm = r.mine.hours[h]; val bp = r.pub.hours[h]
            val hm = if (r.mine.total > 0) bm.n / mt / maxShare * ph else 0f
            val hp = if (r.pub.total > 0) bp.n / pt / maxShare * ph else 0f
            val cm = if (bm.low) muted.copy(alpha = 0.45f) else if (h in bestM) lerpW(Pal.Mine) else Pal.Mine
            if (hm > 0) drawRect(cm, Offset(x + w * 0.08f, topPad + ph - hm), Size(w * 0.40f, hm))
            if (hp > 0) drawRect(if (h in bestP) lerpW(Pal.Public) else Pal.Public.copy(alpha = 0.9f),
                Offset(x + w * 0.52f, topPad + ph - hp), Size(w * 0.40f, hp))
            if (h % 3 == 0) drawContext.canvas.nativeCanvas.drawText("%02d".format(h), x, topPad + ph + 13.sp.toPx(), paint)
        }
    }
}

private fun lerpW(a: Color) = Color(a.red + (1 - a.red) * 0.35f, a.green + (1 - a.green) * 0.35f, a.blue + (1 - a.blue) * 0.35f, 1f)
