package ru.fisherlogger.companion.ui

import android.app.DatePickerDialog
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.animate
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.gestures.detectTransformGestures
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.FilterChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.nativeCanvas
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import org.json.JSONObject
import androidx.compose.foundation.Image
import ru.fisherlogger.companion.Geo
import kotlin.math.floor
import ru.fisherlogger.companion.AppViewModel
import java.util.Calendar
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import kotlin.math.abs
import kotlin.math.ln
import kotlin.math.pow
import kotlin.math.hypot
import kotlin.math.max
import kotlin.math.min
import kotlin.math.sqrt

private val speciesColors = listOf(
    Color(0xFF6CCC8C), Color(0xFFF2C057), Color(0xFFDE6858), Color(0xFFB388FF), Color(0xFF4FC3F7),
    Color(0xFFFF8A65), Color(0xFFAED581), Color(0xFFF06292), Color(0xFF26C6DA), Color(0xFFFFD54F)
)
private fun colorFor(name: String) = speciesColors[(name.hashCode() and 0x7fffffff) % speciesColors.size]
private val ZoneLow = Color(0xFF00A8A0)
private val ZoneMid = Color(0xFFF2C057)
private val ZoneHigh = Color(0xFFE86248)
private fun lerp(a: Color, b: Color, t: Float) = Color(a.red + (b.red - a.red) * t, a.green + (b.green - a.green) * t, a.blue + (b.blue - a.blue) * t, 1f)

// ZONES-UX-01: компактные метки зон (тот же алгоритм группировки, что ZoneClustering в Windows).
// Метки не перекрываются: зоны, которые при текущем масштабе слились бы, показываются одной меткой-группой с суммой
// уловов; при приближении группа распадается. Размер 15…24 dp, цвет: бирюзовый (мало) → золотой → коралловый (много).
private const val ZoneMinDp = 15f
private const val ZoneMaxDp = 24f
private const val ZoneGapDp = 4f
private const val ZoneCellMinDp = 28f
private const val MaxZoom = 40f
private fun heatT(count: Int, max: Int): Float = if (max <= 1) 1f else (ln(max(1, count).toDouble()) / ln(max.toDouble())).toFloat().coerceIn(0f, 1f)
private fun heatColor(t: Float): Color = if (t < 0.5f) lerp(ZoneLow, ZoneMid, t * 2f) else lerp(ZoneMid, ZoneHigh, (t - 0.5f) * 2f)
private fun sameZone(a: JSONObject, b: JSONObject?) = b != null && (a === b ||
    (a.optDouble("x") == b.optDouble("x") && a.optDouble("z") == b.optDouble("z") && a.optInt("count") == b.optInt("count")))
private fun Color.isLight() = red * 0.299f + green * 0.587f + blue * 0.114f > 0.6f

/** Группа зон (одна зона = группа из 1). Только отображение — сами зоны не меняются. */
private class ZoneGroup(var sx: Float, var sy: Float) {
    val zones = mutableListOf<JSONObject>()
    var count = 0; var x = 0.0; var z = 0.0
    var ax = 0.0; var az = 0.0      // где рисовать: взвешенный центр якорей зон (см. anchor в clusterZones)
    var minX = 0.0; var maxX = 0.0; var minZ = 0.0; var maxZ = 0.0
    val isGroup get() = zones.size > 1
    val top get() = zones[0]
    fun add(o: JSONObject, a: Pair<Double, Double>) {
        val c = o.optInt("count")
        ax = (ax * count + a.first * c) / (count + c); az = (az * count + a.second * c) / (count + c)
        if (zones.isEmpty()) { minX = o.optDouble("minX"); maxX = o.optDouble("maxX"); minZ = o.optDouble("minZ"); maxZ = o.optDouble("maxZ") }
        else { minX = min(minX, o.optDouble("minX")); maxX = max(maxX, o.optDouble("maxX")); minZ = min(minZ, o.optDouble("minZ")); maxZ = max(maxZ, o.optDouble("maxZ")) }
        x = (x * count + o.optDouble("x") * c) / (count + c); z = (z * count + o.optDouble("z") * c) / (count + c)
        count += c; zones.add(o)
    }
    fun species(): List<Pair<String, Int>> = zones.flatMap { it.optJSONArray("species").objs() }
        .groupBy { it.optString("name") }.map { (k, v) -> k to v.sumOf { it.optInt("count") } }.sortedByDescending { it.second }
}

/** anchor — позиция метки зоны (мировые X,Z): центр её точек; при крупном масштабе прижат внутрь своей ячейки. */
private fun clusterZones(zones: List<JSONObject>, toScreen: (Double, Double) -> Offset, mergePx: Float,
                         anchor: (JSONObject) -> Pair<Double, Double> = { it.optDouble("x") to it.optDouble("z") }): List<ZoneGroup> {
    val ordered = zones.sortedByDescending { it.optInt("count") }
    val res = mutableListOf<ZoneGroup>()
    val m2 = mergePx * mergePx
    for (o in ordered) {
        val a = anchor(o)
        val p = toScreen(a.first, a.second)
        var best: ZoneGroup? = null; var bd = Float.MAX_VALUE
        for (g in res) { val d = (g.sx - p.x) * (g.sx - p.x) + (g.sy - p.y) * (g.sy - p.y); if (d < m2 && d < bd) { bd = d; best = g } }
        if (best == null) { best = ZoneGroup(p.x, p.y); res.add(best) }
        best.add(o, a)
    }
    var merged = true
    while (merged) {
        merged = false
        res.forEach { g -> val p = toScreen(g.ax, g.az); g.sx = p.x; g.sy = p.y }
        loop@ for (i in res.indices) for (j in i + 1 until res.size) {
            val dx = res[i].sx - res[j].sx; val dy = res[i].sy - res[j].sy
            if (dx * dx + dy * dy < m2) {
                val all = (res[i].zones + res[j].zones).sortedByDescending { it.optInt("count") }
                val g = ZoneGroup(res[i].sx, res[i].sy); all.forEach { g.add(it, anchor(it)) }
                res[i] = g; res.removeAt(j); merged = true; break@loop
            }
        }
    }
    return res.sortedByDescending { it.count }
}
private fun f1(v: Double) = String.format(java.util.Locale.US, "%.1f", v).removeSuffix(".0")

/** Выбранный объект карты: зона, отдельный улов или точка (глубина). */
private sealed class Sel {
    data class Zone(val z: JSONObject) : Sel()
    data class Point(val p: JSONObject) : Sel()
    data class Spot(val x: Double, val z: Double) : Sel()
    data class Ref(val x: Double, val z: Double, val g: String) : Sel()   // BRIDGE-FB-01: точка другого игрока
}

/** BRIDGE-FB-01: цвет точек других игроков — заметный и не совпадающий с цветами уловов, зон и классов. */
private val RefColor = Color(0xFFFF4FD8)

@Composable
fun MapsScreen(vm: AppViewModel, wide: Boolean) {
    val maps = vm.maps.objs()
    val id = vm.selectedMap
    LaunchedEffect(id) { id?.let { vm.loadMap(it) } }
    LaunchedEffect(id, vm.showBathy) { if (vm.showBathy) id?.let { vm.loadBathy(it) } }
    val meta = maps.firstOrNull { it.optInt("locationId") == id }
    val bathy = id?.let { vm.bathyFor(it) }
    val bathyOk = bathy?.optString("status") == "IMPLEMENTED"
    var selected by remember(id) { mutableStateOf<Sel?>(null) }
    // ZONES-UX-01: запрос «показать зону на карте» (из списка лучших мест); номер — чтобы повторное нажатие тоже срабатывало
    var focus by remember(id) { mutableStateOf<Pair<JSONObject, Int>?>(null) }
    val ranked = id?.let { vm.mapZones[it] }?.optJSONArray("zones").objs().sortedByDescending { it.optInt("count") }
    // ANDROID-UX-01: «Где ловить» в Советнике → показать это место на карте
    LaunchedEffect(vm.mapFocusReq, ranked.size) {
        val req = vm.mapFocusReq ?: return@LaunchedEffect
        if (req.first != id || ranked.isEmpty()) return@LaunchedEffect
        val cell = vm.mapZones[id]?.optDouble("cellM") ?: return@LaunchedEffect
        val hit = ranked.firstOrNull { floor(it.optDouble("x") / cell) == floor(req.second / cell) && floor(it.optDouble("z") / cell) == floor(req.third / cell) }
            ?: ranked.minByOrNull { hypot(it.optDouble("x") - req.second, it.optDouble("z") - req.third) }
        if (hit != null) { selected = Sel.Zone(hit); focus = hit to ((focus?.second ?: 0) + 1) }
        vm.mapFocusReq = null
    }
    fun rankOf(z: JSONObject) = ranked.indexOfFirst { it === z || (it.optDouble("x") == z.optDouble("x") && it.optDouble("z") == z.optDouble("z") && it.optInt("count") == z.optInt("count")) } + 1
    val ctx = LocalContext.current

    val controls: @Composable () -> Unit = {
        Column(Modifier.padding(horizontal = 12.dp, vertical = 6.dp)) {
            var open by remember { mutableStateOf(false) }
            Box {
                OutlinedButton({ open = true }, Modifier.fillMaxWidth().heightIn(min = 48.dp)) {
                    Text(meta?.optString("name") ?: "Выберите водоём", maxLines = 1, fontSize = 16.sp)
                }
                DropdownMenu(open, { open = false }) {
                    maps.sortedWith(compareByDescending<JSONObject> { it.optInt("catches") }.thenBy { it.optInt("locationId") }).forEach { m ->
                        val depth = if (m.optString("bathyStatus") == "IMPLEMENTED") " · глубины" else ""
                        DropdownMenuItem({ Text(m.optString("name") + " · моих уловов " + m.optInt("catches") + depth) },
                            { open = false; vm.loadMap(m.optInt("locationId")) })
                    }
                }
            }
            // слои (компактно, прокручиваются по горизонтали на узком экране)
            Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                vm.refHighlight?.takeIf { it.loc == id }?.let { rh ->
                    FilterChip(true, { vm.refHighlight = null; if (selected is Sel.Ref) selected = null }, { Text("Другие игроки: " + rh.fish + " · " + rh.pts.size + "  ✕", maxLines = 1) },
                        Modifier.heightIn(min = 40.dp), leadingIcon = { Box(Modifier.size(12.dp).background(RefColor, RoundedCornerShape(3.dp))) })
                }
                FilterChip(vm.showZones, { vm.showZones = !vm.showZones }, { Text("Зоны") }, Modifier.heightIn(min = 40.dp))
                FilterChip(vm.showBathy && bathyOk, { vm.showBathy = !vm.showBathy }, { Text(if (bathy == null || bathyOk) "Глубины" else "Глубины: нет") },
                    Modifier.heightIn(min = 40.dp), enabled = bathyOk)
                FilterChip(vm.showPoints, { vm.showPoints = !vm.showPoints }, { Text("Точки") }, Modifier.heightIn(min = 40.dp))
                FilterChip(vm.mapUnverified, { vm.setMapFilter(unverified = !vm.mapUnverified) }, { Text("Непроверенные") }, Modifier.heightIn(min = 40.dp))
            }
            // фильтры: вид и даты
            Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                var spOpen by remember { mutableStateOf(false) }
                val zr = id?.let { vm.mapZones[it] }
                Box {
                    OutlinedButton({ spOpen = true }, Modifier.heightIn(min = 44.dp)) { Text(vm.mapSpecies ?: "Все виды", maxLines = 1) }
                    DropdownMenu(spOpen, { spOpen = false }) {
                        DropdownMenuItem({ Text("Все виды") }, { spOpen = false; vm.setMapFilter(species = null) })
                        zr?.optJSONArray("species").objs().forEach { s ->
                            DropdownMenuItem({ Text(s.optString("name") + " (" + s.optInt("count") + ")") }, { spOpen = false; vm.setMapFilter(species = s.optString("name")) })
                        }
                    }
                }
                fun pick(initial: String?, onPick: (String) -> Unit) {
                    val c = Calendar.getInstance()
                    initial?.split("-")?.takeIf { it.size == 3 }?.let { c.set(it[0].toInt(), it[1].toInt() - 1, it[2].toInt()) }
                    DatePickerDialog(ctx, { _, y, m, d -> onPick("%04d-%02d-%02d".format(y, m + 1, d)) },
                        c.get(Calendar.YEAR), c.get(Calendar.MONTH), c.get(Calendar.DAY_OF_MONTH)).show()
                }
                OutlinedButton({ pick(vm.mapFrom ?: vm.options?.str("dateMin")) { vm.setMapFilter(from = it) } }, Modifier.heightIn(min = 44.dp)) { Text("с " + (vm.mapFrom ?: "…")) }
                OutlinedButton({ pick(vm.mapTo ?: vm.options?.str("dateMax")) { vm.setMapFilter(to = it) } }, Modifier.heightIn(min = 44.dp)) { Text("по " + (vm.mapTo ?: "…")) }
                if (vm.mapFrom != null || vm.mapTo != null)
                    TextButton({ vm.setMapFilter(from = null, to = null) }, Modifier.heightIn(min = 44.dp)) { Text("Весь период") }
            }
            // ZONES-UX-01: лучшие места — нажмите, чтобы показать зону на карте
            if (vm.showZones && ranked.isNotEmpty()) {
                Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp),
                    verticalAlignment = Alignment.CenterVertically) {
                    Text("Лучшие места:", fontSize = 13.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    ranked.take(10).forEachIndexed { i, z ->
                        val sel = (selected as? Sel.Zone)?.z
                        val on = sel != null && rankOf(sel) == i + 1
                        val sp = z.optJSONArray("species").objs().firstOrNull()?.optString("name") ?: ""
                        FilterChip(on, { selected = Sel.Zone(z); focus = z to ((focus?.second ?: 0) + 1) },
                            { Text("№" + (i + 1) + " · " + z.optInt("count") + (if (sp.isNotEmpty()) " · $sp" else ""), maxLines = 1) },
                            Modifier.heightIn(min = 40.dp),
                            leadingIcon = { Box(Modifier.size(12.dp).background(heatColor(heatT(z.optInt("count"), ranked[0].optInt("count"))), RoundedCornerShape(6.dp))) })
                    }
                }
            }
        }
        if (vm.showBathy && bathyOk) BathyLegend(bathy!!)
    }
    val mapArea: @Composable (Modifier) -> Unit = { mod ->
        Box(mod) {
            if (id == null || meta == null) Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { Muted("Нет карт — нет связи с ПК", 16) }
            else MapCanvas(vm, id, meta, bathy, selected, focus) { sel ->
                selected = sel
                if (sel is Sel.Spot && vm.showBathy && bathyOk) vm.probeDepth(id, sel.x, sel.z)
            }
            Column(Modifier.align(Alignment.BottomStart).padding(8.dp).widthIn(max = 520.dp)) {
                when (val sel = selected) {
                    is Sel.Zone -> InfoCard({ selected = null }) { ZoneCard(vm, id!!, meta, sel.z, rankOf(sel.z), ranked.size) }
                    is Sel.Point -> InfoCard({ selected = null }) {
                        val p = sel.p
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Text(p.optString("fish"), fontWeight = FontWeight.SemiBold, fontSize = 17.sp, modifier = Modifier.weight(1f))
                            Text(p.optInt("weight").toString() + " г", fontWeight = FontWeight.SemiBold, fontSize = 17.sp)
                        }
                        Text(p.optString("time"), fontSize = 14.sp)
                        if (p.optString("bait").isNotEmpty()) Muted("Наживка: " + p.optString("bait"))
                        // DESIGN-03: номер заброса и координаты сцены — только в технических данных
                        Muted(if (p.optBoolean("verified")) "Место подтверждено забросом" + (if (vm.tech) " #" + p.optLong("castId") else "") else "Место не подтверждено забросом")
                        Muted((Geo.grid(meta, p.optDouble("x"), p.optDouble("z"))?.let { "координаты $it" } ?: "") + (if (vm.tech) "  ·  X " + f1(p.optDouble("x")) + ", Z " + f1(p.optDouble("z")) else "") + " — точка заброса")
                    }
                    is Sel.Ref -> InfoCard({ selected = null }) {
                        Text("Точка других игроков: " + sel.g, fontWeight = FontWeight.SemiBold, fontSize = 17.sp)
                        Muted((vm.refHighlight?.fish ?: "") + " · справочник чужих уловов, не ваша статистика")
                        Muted("Координаты карты игры " + sel.g + " (клетка 5 м, показан её центр)")
                    }
                    is Sel.Spot -> if (vm.showBathy && bathyOk) InfoCard({ selected = null; vm.depthProbe = null }) {
                        val d = vm.depthProbe
                        val dm = if (d != null && !d.isNull("depthM")) f1(d.optDouble("depthM")) + " м" else if (d == null) "…" else "суша / нет данных"
                        Text("Глубина: $dm", fontWeight = FontWeight.SemiBold, fontSize = 17.sp)
                        Muted((Geo.grid(meta, sel.x, sel.z)?.let { "координаты $it" } ?: ("X " + f1(sel.x) + ", Z " + f1(sel.z))) +
                              " · рельеф дна игры" + if (d?.optBoolean("local") == true) " · из памяти телефона" else "")
                    }
                    null -> {
                        val zr = vm.mapZones[id ?: -1]
                        val note = buildString {
                            if (zr != null) append("Мои уловы на карте: " + zr.optInt("total") + " · зон " + (zr.optJSONArray("zones")?.length() ?: 0) +
                                                   " (радиус " + f1(zr.optDouble("radiusM", zr.optDouble("cellM") / 2)) + " м)")
                        if (zr != null && vm.showZones && (zr.optJSONArray("zones")?.length() ?: 0) > 0)
                            append("\nЧисло — мои уловы · метка с кольцом — несколько зон, нажмите")
                            if (vm.showBathy && bathyOk) append((if (isNotEmpty()) "\n" else "") + "Глубины: CONFIRMED (рельеф игры) · совмещение " + bathy!!.optString("alignmentConfidence"))
                            else if (meta != null) append((if (isNotEmpty()) "\n" else "") + "Карта: " + meta.optString("confidence"))
                        }
                        if (note.isNotEmpty()) Text(note, fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant,
                            modifier = Modifier.background(MaterialTheme.colorScheme.surface.copy(alpha = 0.85f), RoundedCornerShape(8.dp)).padding(8.dp))
                    }
                }
            }
        }
    }
    // Горизонтально на широком экране — элементы управления слева, карта занимает всю высоту.
    BoxWithConstraints(Modifier.fillMaxSize()) {
        if (wide && maxWidth > maxHeight) {
            Row(Modifier.fillMaxSize()) {
                Column(Modifier.width(330.dp).fillMaxHeight().verticalScroll(rememberScrollState())) { controls() }
                mapArea(Modifier.weight(1f).fillMaxHeight())
            }
        } else {
            Column(Modifier.fillMaxSize()) {
                controls()
                mapArea(Modifier.weight(1f).fillMaxWidth())
            }
        }
    }
}

/** ANDROID-UX-01: карточка места — что здесь ловится (до какого веса, класс, когда) и на что клевало (как в Windows). */
@Composable
private fun ZoneCard(vm: AppViewModel, id: Int, meta: JSONObject?, z: JSONObject, rank: Int, total: Int) {
    val zr = vm.mapZones[id]
    val cell = zr?.optDouble("cellM") ?: 20.0
    // ZONES-02: зона — круг; её уловы — индексы "pts" в списке точек (старый ответ ПК без "pts" — точки в радиусе зоны)
    val allPts = zr?.optJSONArray("points").objs()
    val members = z.optJSONArray("pts")
    val rad = z.optDouble("r", cell / 2)
    val pts = if (members != null) (0 until members.length()).mapNotNull { allPts.getOrNull(members.optInt(it, -1)) }
              else allPts.filter { !it.isNull("x") && !it.isNull("z") && hypot(it.optDouble("x") - z.optDouble("x"), it.optDouble("z") - z.optDouble("z")) <= rad + 1e-6 }
    val n = if (pts.isNotEmpty()) pts.size else z.optInt("count")
    Text((if (rank > 0) "Место №$rank из $total" else "Место") + " · " + n + " " + plural(n, "улов", "улова", "уловов"), fontWeight = FontWeight.SemiBold, fontSize = 17.sp)
    val where = mutableListOf<String>()
    Geo.grid(meta, z.optDouble("x"), z.optDouble("z"))?.let { where += "координаты $it" }
    if (!z.isNull("depthM")) where += "глубина " + f1(z.optDouble("depthM")) + " м"
    if (where.isNotEmpty()) Muted(where.joinToString(" · "), 13)
    Column(Modifier.heightIn(max = 190.dp).verticalScroll(rememberScrollState())) {
        if (pts.isNotEmpty()) {
            SectionHead("Что ловится")
            ListBlock {
                pts.groupBy { it.optString("fish") }.entries.sortedByDescending { it.value.size }.take(6).forEach { (fish, g) ->
                    val maxW = g.maxOf { it.optInt("weight") }
                    val best = g.maxOf { if (it.isNull("windex")) -1 else it.optInt("windex", -1) }
                    val last = g.mapNotNull { it.optString("time").takeIf { t -> t.length >= 10 } }.maxOrNull()
                    val sub = listOfNotNull(if (maxW > 0) "до " + grams(maxW) else null, gradeName(best).ifEmpty { null },
                        last?.let { it.substring(8, 10) + "." + it.substring(5, 7) }).joinToString(" · ")
                    val fid = g.firstOrNull { !it.isNull("fishId") }?.opt("fishId")?.toString()
                    StatLine(fish, sub, "${g.size} " + catchesWord(g.size), null, gradeColor(best)?.takeIf { best >= 2 } ?: Pal.Mine, icon = IconCache.get(vm.icons, "fish", fid))
                }
            }
            val baits = pts.filter { it.optString("bait").isNotEmpty() }.groupBy { it.optString("bait") }.entries.sortedByDescending { it.value.size }.take(4)
            if (baits.isNotEmpty()) {
                SectionHead("На что клевало")
                ListBlock {
                    baits.forEach { (b, g) ->
                        val top = g.groupBy { it.optString("fish") }.entries.sortedByDescending { it.value.size }.take(2).joinToString(", ") { it.key }
                        StatLine(b, top, "${g.size} " + catchesWord(g.size), null, Pal.Coverage)
                    }
                }
            }
        } else Text(z.optJSONArray("species").objs().joinToString(", ") { it.optString("name") + " — " + it.optInt("count") }, fontSize = 14.sp)
    }
    Spacer(Modifier.width(4.dp))
    Muted("Ваши уловы в точках заброса · число уловов — не вероятность клёва" + if (zr?.optBoolean("offline") == true) " · из памяти телефона" else "", 11)
}

fun plural(n: Int, one: String, few: String, many: String): String {
    val m10 = n % 10; val m100 = n % 100
    return if (m10 == 1 && m100 != 11) one else if (m10 in 2..4 && (m100 < 12 || m100 > 14)) few else many
}

@Composable
private fun InfoCard(onClose: () -> Unit, content: @Composable () -> Unit) {
    Column(Modifier.background(MaterialTheme.colorScheme.surface.copy(alpha = 0.95f), RoundedCornerShape(12.dp)).padding(12.dp)) {
        content()
        TextButton(onClose) { Text("Закрыть") }
    }
}

@Composable
private fun BathyLegend(b: JSONObject) {
    val bands = b.optJSONArray("bands").objs()
    Row(Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(horizontal = 12.dp, vertical = 2.dp),
        verticalAlignment = Alignment.CenterVertically) {
        Text("Глубина, м", fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Spacer(Modifier.width(8.dp))
        bands.forEach { band ->
            val rgb = band.optJSONArray("rgb")
            val a = band.optDouble("alpha").toFloat()
            val c = Color(rgb?.optInt(0) ?: 14, rgb?.optInt(1) ?: 52, rgb?.optInt(2) ?: 96)
            val base = Color(157, 213, 255)
            Column(horizontalAlignment = Alignment.CenterHorizontally, modifier = Modifier.padding(end = 2.dp)) {
                Box(Modifier.size(width = 34.dp, height = 12.dp).background(lerp(base, c, a), RoundedCornerShape(2.dp)))
                Text(f1(band.optDouble("fromM")), fontSize = 10.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
        Text("макс " + f1(b.optDouble("maxDepthM")), fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

@Composable
private fun MapCanvas(vm: AppViewModel, id: Int, meta: JSONObject, bathy: JSONObject?, selected: Sel?, focus: Pair<JSONObject, Int>?, onSelect: (Sel?) -> Unit) {
    val bmp = vm.mapImages[id]
    val bathyBmp = if (vm.showBathy) vm.bathyImages[id] else null
    val zr = vm.mapZones[id]
    val w = meta.optDouble("width"); val h = meta.optDouble("height")
    val minX = meta.optDouble("minX"); val maxX = meta.optDouble("maxX")
    val minZ = meta.optDouble("minZ"); val maxZ = meta.optDouble("maxZ")
    // Преобразование реестра (как в Windows MapRegistry / WorldToPixel): px = (X-minX)/(maxX-minX)*W, py = (maxZ-Z)/(maxZ-minZ)*H
    fun px(x: Double) = (x - minX) / (maxX - minX) * w
    fun py(z: Double) = (maxZ - z) / (maxZ - minZ) * h

    var viewSize by remember { mutableStateOf(IntSize.Zero) }
    var zoom by vm.mapZoom
    var offX by vm.mapOffX
    var offY by vm.mapOffY
    val scope = rememberCoroutineScope()
    var anim by remember { mutableStateOf<Job?>(null) }
    fun stopAnim() { anim?.cancel(); anim = null }
    fun zoomBy(k: Float) {
        stopAnim()
        val nz = (zoom * k).coerceIn(1f, MaxZoom); val kk = nz / zoom
        offX *= kk; offY *= kk; zoom = nz
    }
    val image = remember(bmp) { bmp?.asImageBitmap() }
    val bathyImage = remember(bathyBmp) { bathyBmp?.asImageBitmap() }
    val zones = if (vm.showZones) zr?.optJSONArray("zones").objs() else emptyList()
    val points = if (vm.showPoints) zr?.optJSONArray("points").objs() else emptyList()
    val refPts = vm.refHighlight?.takeIf { it.loc == id }?.pts ?: emptyList()
    val labels = if (bathyImage != null) bathy?.optJSONArray("labels").objs() ?: emptyList() else emptyList()
    val maxCount = max(1, zones.maxOfOrNull { it.optInt("count") } ?: 1)
    val cellM = (zr?.optDouble("cellM") ?: 20.0)
    val outline = MaterialTheme.colorScheme.outline
    val bg = MaterialTheme.colorScheme.background

    fun fit(): Float = if (viewSize.width == 0) 1f else min(viewSize.width / w.toFloat(), viewSize.height / h.toFloat())
    fun toScreen(mx: Double, my: Double): Offset {
        val s = fit() * zoom
        val ox = (viewSize.width - w.toFloat() * s) / 2f + offX
        val oy = (viewSize.height - h.toFloat() * s) / 2f + offY
        return Offset(ox + mx.toFloat() * s, oy + my.toFloat() * s)
    }
    fun worldToScreen(x: Double, z: Double) = toScreen(px(x), py(z))
    fun toWorld(p: Offset): Pair<Double, Double> {
        val s = fit() * zoom
        val ox = (viewSize.width - w.toFloat() * s) / 2f + offX
        val oy = (viewSize.height - h.toFloat() * s) / 2f + offY
        val mx = (p.x - ox) / s; val my = (p.y - oy) / s
        return Pair(minX + mx / w * (maxX - minX), maxZ - my / h * (maxZ - minZ))
    }
    fun cellMapPx() = (cellM / (maxX - minX) * w).toFloat()            // ячейка в пикселях карты (без масштаба)
    fun badgeRadius(count: Int, dp: Float) = (ZoneMinDp + (ZoneMaxDp - ZoneMinDp) * sqrt(min(1f, count / maxCount.toFloat()))) * dp
    /** Якорь метки при масштабе zz: центр точек зоны, прижатый внутрь ячейки с отступом (радиус + полпромежутка),
     *  когда ячейка крупнее метки — метки соседних ячеек тогда не сливаются даже при максимальном приближении. */
    fun anchorAt(o: JSONObject, zz: Float, dp: Float): Pair<Double, Double> {
        val s = fit() * zz
        val mx = (ZoneMaxDp + ZoneGapDp / 2) * dp / (s * (w / (maxX - minX)).toFloat())
        val mz = (ZoneMaxDp + ZoneGapDp / 2) * dp / (s * (h / (maxZ - minZ)).toFloat())
        val x0 = o.optDouble("minX"); val x1 = o.optDouble("maxX"); val z0 = o.optDouble("minZ"); val z1 = o.optDouble("maxZ")
        if (x1 - x0 < 2 * mx || z1 - z0 < 2 * mz) return o.optDouble("x") to o.optDouble("z")
        return o.optDouble("x").coerceIn(x0 + mx, x1 - mx) to o.optDouble("z").coerceIn(z0 + mz, z1 - mz)
    }
    fun groups(dp: Float) = clusterZones(zones, ::worldToScreen, (2 * ZoneMaxDp + ZoneGapDp) * dp) { anchorAt(it, zoom, dp) }
    /** ZONES-02: квадрат, описанный вокруг круга зоны (центр ± r; у старого ответа ПК — его рамка). */
    fun cellRect(o: JSONObject): Pair<Offset, Offset> {
        val r = o.optDouble("r", Double.NaN)
        if (r.isNaN()) return worldToScreen(o.optDouble("minX"), o.optDouble("maxZ")) to worldToScreen(o.optDouble("maxX"), o.optDouble("minZ"))
        val x = o.optDouble("x"); val z = o.optDouble("z")
        return worldToScreen(x - r, z + r) to worldToScreen(x + r, z - r)
    }

    /** Плавно: точка карты (mx,my) в центр экрана (по высоте — на долю ay) при масштабе tz. */
    fun animateTo(mx: Double, my: Double, tz: Float, ay: Float = 0.5f) {
        stopAnim()
        val z1 = tz.coerceIn(1f, MaxZoom)
        val s1 = fit() * z1
        val x1 = s1 * (w.toFloat() / 2f - mx.toFloat())
        val y1 = (ay - 0.5f) * viewSize.height + s1 * (h.toFloat() / 2f - my.toFloat())
        val z0 = zoom; val x0 = offX; val y0 = offY
        anim = scope.launch {
            animate(0f, 1f, animationSpec = tween(280, easing = FastOutSlowInEasing)) { v, _ ->
                zoom = z0 * (z1 / z0).pow(v); offX = x0 + (x1 - x0) * v; offY = y0 + (y1 - y0) * v
            }
        }
    }
    fun zoomToGroup(g: ZoneGroup) {
        val c = cellMapPx()
        val bw = max((px(g.maxX) - px(g.minX)).toFloat() + 2 * c, 4 * c)
        val bh = max((py(g.minZ) - py(g.maxZ)).toFloat() + 2 * c, 4 * c)
        val f = fit()
        val t = min(viewSize.width * 0.75f / (bw * f), viewSize.height * 0.75f / (bh * f))
        animateTo(px(g.x), py(g.z), max(t, zoom * 2f))
    }
    // запрос из списка «Лучшие места»: показать зону (ячейка не меньше ~90 dp) в верхней трети — снизу её карточка
    val density = androidx.compose.ui.platform.LocalDensity.current.density
    LaunchedEffect(focus) {
        val z = focus?.first ?: return@LaunchedEffect
        if (viewSize.width == 0) return@LaunchedEffect
        // масштаб, при котором выбранная зона — отдельная метка (не внутри группы)
        var tz = max(zoom, 90f * density / (cellMapPx() * fit()))
        val merge = (2 * ZoneMaxDp + ZoneGapDp) * density
        for (i in 0 until 20) {
            if (tz >= MaxZoom) break
            val sc = fit() * tz
            val zt = tz
            val alone = clusterZones(zones, { x, zz -> Offset(px(x).toFloat() * sc, py(zz).toFloat() * sc) }, merge) { anchorAt(it, zt, density) }
                .any { !it.isGroup && sameZone(it.top, z) }
            if (alone) break
            tz *= 1.5f
        }
        animateTo(px(z.optDouble("x")), py(z.optDouble("z")), tz, ay = 0.28f)
    }

    // BRIDGE-FB-01: открыли из «Другие игроки» — показать все их точки (рамка с запасом), один раз на набор
    LaunchedEffect(vm.refHighlight, viewSize) {
        val rh = vm.refHighlight?.takeIf { it.loc == id } ?: return@LaunchedEffect
        if (viewSize.width == 0 || rh.pts.isEmpty()) return@LaunchedEffect
        val xs = rh.pts.map { px(it.first) }; val ys = rh.pts.map { py(it.second) }
        val c = 6 * cellMapPx()
        val bw = max((xs.max() - xs.min()).toFloat() + 2 * c, 8 * c); val bh = max((ys.max() - ys.min()).toFloat() + 2 * c, 8 * c)
        val f = fit()
        val t = min(viewSize.width * 0.8f / (bw * f), viewSize.height * 0.6f / (bh * f))
        animateTo((xs.min() + xs.max()) / 2, (ys.min() + ys.max()) / 2, t.coerceAtLeast(1f), ay = 0.4f)
    }

    Box(Modifier.fillMaxSize().clipToBounds()) {
        Canvas(
            Modifier.fillMaxSize().onSizeChanged { viewSize = it }
                .pointerInput(id) {
                    detectTransformGestures { centroid, pan, gz, _ ->
                        stopAnim()
                        val newZoom = (zoom * gz).coerceIn(1f, MaxZoom)
                        val k = newZoom / zoom
                        val cx = viewSize.width / 2f + offX; val cy = viewSize.height / 2f + offY
                        offX = (offX + (centroid.x - cx) * (1 - k)) + pan.x
                        offY = (offY + (centroid.y - cy) * (1 - k)) + pan.y
                        zoom = newZoom
                    }
                }
                .pointerInput(id, zr, vm.showZones, vm.showPoints, vm.showBathy, refPts) {
                    detectTapGestures(
                        onDoubleTap = { stopAnim(); zoom = 1f; offX = 0f; offY = 0f },
                        onTap = { pos ->
                            // приоритет: отдельные точки → метки зон (группа → приблизить) → ячейка зоны → точка карты (глубина)
                            var best: Sel? = null; var bd = 26.dp.toPx(); var zoomed = false
                            refPts.forEach { (x, z, g) ->
                                val sp = worldToScreen(x, z)
                                val d = abs(sp.x - pos.x) + abs(sp.y - pos.y)
                                if (d < bd) { bd = d; best = Sel.Ref(x, z, g) }
                            }
                            points.forEach { p ->
                                val sp = worldToScreen(p.optDouble("x"), p.optDouble("z"))
                                val d = abs(sp.x - pos.x) + abs(sp.y - pos.y)
                                if (d < bd) { bd = d; best = Sel.Point(p) }
                            }
                            if (best == null && zones.isNotEmpty()) {
                                val gs = groups(density)
                                var hit: ZoneGroup? = null; var bz = Float.MAX_VALUE
                                gs.forEach { g ->
                                    val d = hypot(g.sx - pos.x, g.sy - pos.y)
                                    if (d <= badgeRadius(g.count, density) + 8.dp.toPx() && d < bz) { bz = d; hit = g }
                                }
                                // нажатие внутри ячейки (вдали от метки) — сразу эта зона
                                val cellZone = if (hit == null && cellMapPx() * fit() * zoom >= ZoneCellMinDp * density)
                                    zones.firstOrNull { o -> val (a, b) = cellRect(o); val c = Offset((a.x + b.x) / 2, (a.y + b.y) / 2); hypot(pos.x - c.x, pos.y - c.y) <= abs(b.x - a.x) / 2 } else null
                                if (cellZone != null) best = Sel.Zone(cellZone)
                                val g = hit
                                if (g != null) {
                                    if (g.isGroup) { zoomToGroup(g); zoomed = true }
                                    else {
                                        best = Sel.Zone(g.top)
                                        // карточка зоны открывается снизу — если зона в нижней половине, поднять её
                                        if (g.sy > viewSize.height * 0.45f) animateTo(px(g.top.optDouble("x")), py(g.top.optDouble("z")), zoom, ay = 0.28f)
                                    }
                                }
                            }
                            if (!zoomed) {
                                if (best == null) { val (wx, wz) = toWorld(pos); best = Sel.Spot(wx, wz) }
                                onSelect(best)
                            }
                        })
                }
        ) {
            drawRect(bg)
            val s = fit() * zoom
            val o = toScreen(0.0, 0.0)
            val dst = IntSize((w * s).toInt(), (h * s).toInt())
            if (image != null) drawImage(image, srcOffset = IntOffset.Zero, srcSize = IntSize(image.width, image.height), dstOffset = IntOffset(o.x.toInt(), o.y.toInt()), dstSize = dst)
            else drawRect(outline, o, Size((w * s).toFloat(), (h * s).toFloat()), style = Stroke(1f))
            // батиметрия: тот же размер и та же рамка, что у карты → то же преобразование
            if (bathyImage != null) drawImage(bathyImage, srcOffset = IntOffset.Zero, srcSize = IntSize(bathyImage.width, bathyImage.height), dstOffset = IntOffset(o.x.toInt(), o.y.toInt()), dstSize = dst)
            val txt = android.graphics.Paint().apply { isAntiAlias = true; textSize = 13.sp.toPx(); isFakeBoldText = true; textAlign = android.graphics.Paint.Align.CENTER }
            val lbl = android.graphics.Paint().apply { isAntiAlias = true; textSize = 12.sp.toPx(); color = android.graphics.Color.WHITE; isFakeBoldText = true; textAlign = android.graphics.Paint.Align.CENTER }
            val pill = android.graphics.Paint().apply { isAntiAlias = true; color = android.graphics.Color.argb(175, 10, 25, 45) }
            // подписи изобат (редкие; не налезают друг на друга)
            val placed = mutableListOf<android.graphics.RectF>()
            labels.sortedBy { if (it.optString("kind") == "max") 0 else 1 }.forEach { l ->
                val p = worldToScreen(l.optDouble("x"), l.optDouble("z"))
                if (p.x < 0 || p.y < 0 || p.x > size.width || p.y > size.height) return@forEach
                val t = (if (l.optString("kind") == "max") "макс " else "") + f1(l.optDouble("depthM")) + " м"
                val tw = lbl.measureText(t); val th = lbl.textSize
                val r = android.graphics.RectF(p.x - tw / 2 - 6, p.y - th, p.x + tw / 2 + 6, p.y + th * 0.45f)
                if (placed.any { android.graphics.RectF.intersects(it, r) }) return@forEach
                placed.add(r)
                drawContext.canvas.nativeCanvas.drawRoundRect(r, 8f, 8f, pill)
                drawContext.canvas.nativeCanvas.drawText(t, p.x, p.y, lbl)
            }
            // зоны активности: при крупном масштабе — сами ячейки, поверх — компактные метки (группы не перекрываются)
            val selZone = (selected as? Sel.Zone)?.z
            fun same(a: JSONObject, b: JSONObject?) = sameZone(a, b)
            if (zones.isNotEmpty() && cellMapPx() * s >= ZoneCellMinDp * density) zones.forEach { z ->
                val (a, b) = cellRect(z)
                if (b.x < 0 || b.y < 0 || a.x > size.width || a.y > size.height) return@forEach
                val c = heatColor(heatT(z.optInt("count"), maxCount))
                // ZONES-02: зона — круг радиусом r вокруг центра точек
                val ctr = Offset((a.x + b.x) / 2, (a.y + b.y) / 2); val rr = abs(b.x - a.x) / 2
                drawCircle(c.copy(alpha = 0.28f), rr, ctr)
                drawCircle(c.copy(alpha = if (same(z, selZone)) 1f else 0.75f), rr, ctr, style = Stroke(if (same(z, selZone)) 3.dp.toPx() else 1.dp.toPx()))
            }
            groups(density).sortedBy { it.count }.forEach { g ->
                val p = Offset(g.sx, g.sy)
                val r = max(badgeRadius(g.count, density), txt.measureText(g.count.toString()) / 2f + 5.dp.toPx())
                if (p.x < -r - 12 || p.y < -r - 12 || p.x > size.width + r + 12 || p.y > size.height + r + 12) return@forEach
                val c = heatColor(heatT(g.count, maxCount))
                drawCircle(Color.Black.copy(alpha = 0.35f), r + 2.dp.toPx(), p + Offset(0f, 1.5f.dp.toPx()))
                if (g.isGroup) drawCircle(Color.White.copy(alpha = 0.85f), r + 4.dp.toPx(), p, style = Stroke(2.dp.toPx()))
                drawCircle(c.copy(alpha = 0.95f), r, p)
                drawCircle(Color.White, r, p, style = Stroke(2.dp.toPx()))
                txt.color = if (c.isLight()) android.graphics.Color.rgb(20, 28, 40) else android.graphics.Color.WHITE
                drawContext.canvas.nativeCanvas.drawText(g.count.toString(), p.x, p.y + txt.textSize * 0.36f, txt)
                if (g.zones.any { same(it, selZone) }) drawCircle(Color(0xFFFFEC8C), r + 8.dp.toPx(), p, style = Stroke(3.dp.toPx()))
            }
            // отдельные точки уловов (по желанию)
            val pr = 6.dp.toPx()
            points.forEach { c ->
                val p = worldToScreen(c.optDouble("x"), c.optDouble("z"))
                drawCircle(Color.Black.copy(alpha = 0.6f), pr + 2f, p)
                if (c.optBoolean("verified")) drawCircle(colorFor(c.optString("fish")), pr, p) else drawCircle(Color.Gray, pr, p, style = Stroke(3f))
            }
            // BRIDGE-FB-01: точки других игроков — поверх всего, ромбом своего цвета с белой каймой
            val rr = 8.dp.toPx()
            refPts.forEach { (x, z, _) ->
                val p = worldToScreen(x, z)
                if (p.x < -rr || p.y < -rr || p.x > size.width + rr || p.y > size.height + rr) return@forEach
                fun dia(r: Float) = androidx.compose.ui.graphics.Path().apply { moveTo(p.x, p.y - r); lineTo(p.x + r, p.y); lineTo(p.x, p.y + r); lineTo(p.x - r, p.y); close() }
                drawPath(dia(rr + 3.dp.toPx()), Color.Black.copy(alpha = 0.5f))
                drawPath(dia(rr + 1.5f.dp.toPx()), Color.White)
                drawPath(dia(rr), RefColor)
            }
            when (selected) {
                is Sel.Ref -> drawCircle(Color.White, rr + 9.dp.toPx(), worldToScreen(selected.x, selected.z), style = Stroke(3f))
                is Sel.Zone -> {}   // выделение зоны — жёлтое кольцо вокруг её метки (выше)
                is Sel.Point -> drawCircle(Color.White, pr + 8f, worldToScreen(selected.p.optDouble("x"), selected.p.optDouble("z")), style = Stroke(3f))
                is Sel.Spot -> {
                    val p = worldToScreen(selected.x, selected.z)
                    drawLine(Color.White, Offset(p.x - 14, p.y), Offset(p.x + 14, p.y), 3f)
                    drawLine(Color.White, Offset(p.x, p.y - 14), Offset(p.x, p.y + 14), 3f)
                }
                null -> {}
            }
        }
        Column(Modifier.align(Alignment.TopEnd).padding(8.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            ZoomButton("+") { zoomBy(1.6f) }
            ZoomButton("−") { zoomBy(1f / 1.6f) }
            ZoomButton("⤢") { stopAnim(); zoom = 1f; offX = 0f; offY = 0f }
        }
    }
}

@Composable
private fun ZoomButton(t: String, onClick: () -> Unit) {
    FilledTonalButton(onClick, Modifier.size(52.dp), contentPadding = androidx.compose.foundation.layout.PaddingValues(0.dp)) { Text(t, fontSize = 22.sp) }
}
