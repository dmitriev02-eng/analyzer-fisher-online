package ru.fisherlogger.companion.ui

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.FilterChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.PathEffect
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.graphics.drawscope.clipPath
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.drawText
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.rememberTextMeasurer
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import org.json.JSONObject
import ru.fisherlogger.companion.AppViewModel
import kotlin.math.cos
import kotlin.math.min
import kotlin.math.sin

/** FEAT-06: цвета как на мини-карте ПК (OverlayForm) — одинаковые на обоих экранах. */
object LiveCol {
    val Player = Color(0xFFFF5A5F)
    val Wait = Color(0xFF5AA9E6); val Bite = Color(0xFFF2C057); val Hook = Color(0xFFFF8A3D); val Catch = Color(0xFF6CCC8C); val Done = Color(0xFF78899A)
    fun state(s: String) = when (s) { "bite" -> Bite; "hook" -> Hook; "catch" -> Catch; "done" -> Done; else -> Wait }
    /** Класс рыбы: рамка (как GradeBadge на ПК) и цвет названия (как в игре). */
    fun frame(w: Int?) = when (w) { 0 -> Color(0xFF969696); 1 -> Color(0xFF4CAF50); 2 -> Color(0xFF4285F4); 3 -> Color(0xFFF7C84A); else -> Color(0xFF5E6E72) }
    fun name(w: Int?) = when (w) { 0 -> Color(0xFFFFFFC7); 1 -> Color(0xFF80FF60); 2 -> Color(0xFF7896FF); 3 -> Color(0xFFFFA020); else -> Color(0xFFE5EDEB) }
    fun grade(w: Int?) = when (w) { 0 -> "обычный"; 1 -> "хороший"; 2 -> "крупный"; 3 -> "трофейный"; else -> "" }
    /** FEAT-07: зона клёва — 2 выше обычного (зелёный), 1 как обычно (жёлтый), 0 ниже (серый); как Theme.AccentGreen/Warning/TextMuted на ПК. */
    fun spot(level: Int) = when (level) { 2 -> Color(0xFF7FCB8F); 1 -> Color(0xFFEDB84E); else -> Color(0xFF8EA4A1) }
}

/** Мини-карта «Сейчас»: круг вокруг игрока, север вверх, сектор взгляда, забросы по удочкам с состоянием. */
@Composable
fun LiveMapCard(vm: AppViewModel) {
    val live = vm.live ?: return
    val loc = live.optInt("locationId", -1)
    val player = live.optJSONObject("player")
    val casts = (0 until (live.optJSONArray("casts")?.length() ?: 0)).mapNotNull { live.optJSONArray("casts")?.optJSONObject(it) }
    if (player == null && casts.isEmpty() && liveSpots(live).isEmpty()) return
    val meta = (0 until (vm.maps?.length() ?: 0)).mapNotNull { vm.maps?.optJSONObject(it) }.firstOrNull { it.optInt("locationId", -2) == loc }
    LaunchedEffect(loc) { if (loc >= 0 && meta != null) vm.loadMap(loc) }
    var radius by rememberSaveable { mutableIntStateOf(100) }
    AppCard {
        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
            Text("Мини-карта", fontWeight = FontWeight.SemiBold, fontSize = 16.sp, modifier = Modifier.weight(1f))
            listOf(50, 100, 200).forEach { r ->
                FilterChip(selected = radius == r, onClick = { radius = r }, label = { Text("$r м") }, modifier = Modifier.padding(start = 4.dp).heightIn(min = 40.dp))
            }
        }
        val age = player?.optInt("ageSec", 9999) ?: 9999
        LiveMapCanvas(vm, meta, loc, player, casts, liveSpots(live), radius, age <= 25)
        Spacer(Modifier.height(6.dp))
        Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            listOf("вы" to LiveCol.Player, "в воде" to LiveCol.Wait, "поклёвка" to LiveCol.Bite, "подсечка" to LiveCol.Hook, "улов" to LiveCol.Catch).forEach { (t, c) ->
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Box(Modifier.size(9.dp).background(c, CircleShape)); Spacer(Modifier.size(4.dp)); Text(t, fontSize = 12.sp)
                }
            }
        }
        if (player != null && age > 25) Muted("Положение игрока обновится при следующем движении или забросе (было ${age / 60} мин назад)", 12)
    }
}

@Composable
internal fun LiveMapCanvas(vm: AppViewModel, meta: JSONObject?, loc: Int, player: JSONObject?, casts: List<JSONObject>, spots: List<JSONObject>, radius: Int, livePose: Boolean,
                           modifier: Modifier = Modifier.fillMaxWidth().widthIn(max = 520.dp).aspectRatio(1f).padding(top = 8.dp)) {
    val tm = rememberTextMeasurer()
    val onSurface = MaterialTheme.colorScheme.onSurface
    val bmp = if (meta != null) vm.mapImages[loc] else null
    val img = remember(bmp) { bmp?.asImageBitmap() }
    val active = casts.filter { it.optBoolean("active") }
    val cX = player?.optDouble("x") ?: active.map { it.optDouble("x") }.average().takeIf { !it.isNaN() } ?: casts.firstOrNull()?.optDouble("x") ?: spots.firstOrNull()?.optDouble("x") ?: 0.0
    val cZ = player?.optDouble("z") ?: active.map { it.optDouble("z") }.average().takeIf { !it.isNaN() } ?: casts.firstOrNull()?.optDouble("z") ?: spots.firstOrNull()?.optDouble("z") ?: 0.0
    Canvas(modifier) {
        val cx = size.width / 2f; val cy = size.height / 2f; val rPx = min(cx, cy) - 4f
        val s = rPx / radius.toFloat()                                   // пикселей на метр
        fun sx(x: Double) = cx + ((x - cX) * s).toFloat()
        fun sy(z: Double) = cy - ((z - cZ) * s).toFloat()                 // +Z вверх (север)
        val circle = Path().apply { addOval(androidx.compose.ui.geometry.Rect(Offset(cx, cy), rPx)) }
        drawCircle(Color(0xFF0E2A30), rPx, Offset(cx, cy))
        clipPath(circle) {
            if (img != null && meta != null) {
                val minX = meta.optDouble("minX"); val maxX = meta.optDouble("maxX"); val minZ = meta.optDouble("minZ"); val maxZ = meta.optDouble("maxZ")
                val l = sx(minX); val t = sy(maxZ); val r = sx(maxX); val b = sy(minZ)
                drawImage(img, dstOffset = IntOffset(l.toInt(), t.toInt()), dstSize = IntSize((r - l).toInt().coerceAtLeast(1), (b - t).toInt().coerceAtLeast(1)))
            }
            // FEAT-07: зоны клёва, как на ПК: круг 12 м, цвет — выше/как/ниже обычного для своей снасти, подпись — % от обычного
            spots.forEach { sp ->
                val p = Offset(sx(sp.optDouble("x")), sy(sp.optDouble("z")))
                val rr = (sp.optDouble("radius", 12.0) * s).toFloat().coerceAtLeast(14f)
                val col = LiveCol.spot(sp.optInt("level", 0))
                val fade = (1f - sp.optDouble("ageMin", 0.0).toFloat() / 60f).coerceIn(0.45f, 1f)
                drawCircle(col.copy(alpha = 0.30f * fade), rr, p)
                drawCircle(col.copy(alpha = 0.85f * fade), rr, p, style = Stroke(2f, pathEffect = PathEffect.dashPathEffect(floatArrayOf(4f, 4f))))
            }
            // где стоял игрок → наживка (только в воде)
            active.forEach { k ->
                if (!k.isNull("playerX") && !k.isNull("playerZ"))
                    drawLine(LiveCol.state(k.optString("state")).copy(alpha = 0.55f), Offset(sx(k.optDouble("playerX")), sy(k.optDouble("playerZ"))),
                             Offset(sx(k.optDouble("x")), sy(k.optDouble("z"))), strokeWidth = 2.5f)
            }
            casts.sortedBy { it.optBoolean("active") }.forEach { k ->
                val p = Offset(sx(k.optDouble("x")), sy(k.optDouble("z")))
                val col = LiveCol.state(k.optString("state"))
                if (k.optBoolean("active")) {
                    drawCircle(Color.Black.copy(alpha = 0.55f), 15f, p); drawCircle(col, 12f, p)
                    val lay = tm.measure(k.optInt("rod").toString(), TextStyle(fontSize = 12.sp, fontWeight = FontWeight.Bold, color = Color(0xFF06201D)))
                    drawText(lay, topLeft = Offset(p.x - lay.size.width / 2f, p.y - lay.size.height / 2f))
                } else drawCircle(col.copy(alpha = 0.6f), 5f, p)
            }
            if (player != null) {
                val p = Offset(sx(player.optDouble("x")), sy(player.optDouble("z")))
                if (!player.isNull("yaw")) {
                    val yaw = player.optDouble("yaw").toFloat()
                    drawArc(LiveCol.Player.copy(alpha = 0.35f), yaw - 90f - 22f, 44f, true, Offset(p.x - 40f, p.y - 40f), Size(80f, 80f))
                }
                if (livePose) { drawCircle(Color.White, 10f, p); drawCircle(LiveCol.Player, 8f, p) }
                else drawCircle(LiveCol.Player, 9f, p, style = Stroke(3f, pathEffect = PathEffect.dashPathEffect(floatArrayOf(6f, 5f))))
            }
        }
        // подписи зон — поверх всего, чтобы не прятались под забросами
        spots.forEach { sp ->
            val p = Offset(sx(sp.optDouble("x")), sy(sp.optDouble("z")))
            if ((p - Offset(cx, cy)).getDistance() > rPx - 10f) return@forEach
            val t = L.t(sp.optString("label"))
            val lay = tm.measure(t, TextStyle(fontSize = 12.sp, fontWeight = FontWeight.Bold, color = Color(0xFF06201D)))
            val w = lay.size.width + 10f; val h = lay.size.height + 2f
            drawRoundRect(Color.White.copy(alpha = 0.78f), Offset(p.x - w / 2f, p.y - h / 2f), Size(w, h), androidx.compose.ui.geometry.CornerRadius(h / 2f))
            drawText(lay, topLeft = Offset(p.x - lay.size.width / 2f, p.y - lay.size.height / 2f))
        }
        // кольца: край и половина радиуса; «С» — север
        drawCircle(onSurface.copy(alpha = 0.5f), rPx, Offset(cx, cy), style = Stroke(2f))
        drawCircle(onSurface.copy(alpha = 0.25f), rPx / 2f, Offset(cx, cy), style = Stroke(1.5f, pathEffect = PathEffect.dashPathEffect(floatArrayOf(8f, 8f))))
        val n = tm.measure(L.t("С"), TextStyle(fontSize = 13.sp, fontWeight = FontWeight.Bold, color = onSurface))
        drawText(n, topLeft = Offset(cx - n.size.width / 2f, cy - rPx + 4f))
        val half = tm.measure("${radius / 2} м", TextStyle(fontSize = 11.sp, color = onSurface.copy(alpha = 0.7f)))
        drawText(half, topLeft = Offset(cx + 4f, cy - rPx / 2f - half.size.height))
    }
}

/** FEAT-06: пустые поклёвки удочки — цветной квадрат класса, вид и вес цветом класса. */
@Composable
fun EmptyBitesList(rod: JSONObject) {
    val arr = rod.optJSONArray("empties") ?: return
    if (arr.length() == 0) return
    val dark = MaterialTheme.colorScheme.background.luminance() < 0.5f   // светлая тема: цвет рамки вместо «игрового» цвета названия
    Spacer(Modifier.height(6.dp))
    Muted("Пустые поклёвки (последние ${arr.length()}):", 13)
    (0 until arr.length()).mapNotNull { arr.optJSONObject(it) }.forEach { e ->
        val w = if (e.isNull("windex")) null else e.optInt("windex")
        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(top = 3.dp)) {
            Box(Modifier.size(10.dp).background(LiveCol.frame(w)))
            Spacer(Modifier.size(8.dp))
            Text(e.optString("fish") + " " + String.format(java.util.Locale.US, "%.2f", e.optInt("weight") / 1000.0).replace('.', ',') + " кг" +
                 (if (w != null) " · " + LiveCol.grade(w) else ""), fontSize = 14.sp, color = if (dark) LiveCol.name(w) else LiveCol.frame(w), modifier = Modifier.weight(1f))
            if (!e.isNull("time")) Muted(e.optString("time"), 12)
        }
    }
}

/** FEAT-07: зоны клёва из /api/live (старый ПК — пусто). */
fun liveSpots(live: JSONObject?): List<JSONObject> = (0 until (live?.optJSONArray("spots")?.length() ?: 0)).mapNotNull { live?.optJSONArray("spots")?.optJSONObject(it) }
