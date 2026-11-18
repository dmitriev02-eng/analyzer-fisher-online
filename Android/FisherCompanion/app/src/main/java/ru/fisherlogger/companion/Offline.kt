package ru.fisherlogger.companion

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.util.zip.GZIPInputStream
import kotlin.math.ceil
import kotlin.math.floor
import kotlin.math.hypot
import kotlin.math.max

/**
 * ANDROID-UX-01: хранилище на телефоне — последние данные ПК (списки, советник, карты, глубины, зоны), чтобы приложение
 * показывало их без связи. Только файлы в личной папке приложения (filesDir/offline); при удалении приложения удаляются.
 * Секреты сопряжения сюда не попадают. Запись атомарная: временный файл → переименование.
 */
class OfflineStore(context: Context) {
    private val dir = File(context.filesDir, "offline").apply { mkdirs() }
    private val lock = Any()

    private fun f(name: String) = File(dir, name.replace(Regex("[^A-Za-z0-9_.-]"), "_"))

    fun putBytes(name: String, b: ByteArray) = synchronized(lock) {
        val t = File(dir, f(name).name + ".tmp")
        t.writeBytes(b)
        if (!t.renameTo(f(name))) { f(name).delete(); t.renameTo(f(name)) }
    }
    fun bytes(name: String): ByteArray? = synchronized(lock) { f(name).takeIf { it.exists() }?.readBytes() }
    fun has(name: String) = f(name).exists()
    fun putText(name: String, s: String) = putBytes(name, s.toByteArray(Charsets.UTF_8))
    fun text(name: String): String? = bytes(name)?.toString(Charsets.UTF_8)
    fun putJson(name: String, j: JSONObject) = putText(name, j.toString())
    fun json(name: String): JSONObject? = try { text(name)?.let { JSONObject(it) } } catch (e: Exception) { null }

    /** Когда сохранены данные (время телефона) и с какого ПК. */
    var savedAt: Long
        get() = json("meta.json")?.optLong("savedAt") ?: 0L
        set(v) = putJson("meta.json", (json("meta.json") ?: JSONObject()).put("savedAt", v))
    var serverName: String
        get() = json("meta.json")?.optString("server") ?: ""
        set(v) = putJson("meta.json", (json("meta.json") ?: JSONObject()).put("server", v))

    fun sizeBytes(): Long = dir.listFiles()?.sumOf { it.length() } ?: 0L
    fun clear() = synchronized(lock) { dir.listFiles()?.forEach { it.delete() } }
}

/** Сетка глубин водоёма: uint16 (см), 65535 — суша/нет данных, строка 0 — maxZ (как Bathymetry.DepthAt в Windows). */
class DepthGrid(val w: Int, val h: Int, val minX: Double, val maxX: Double, val minZ: Double, val maxZ: Double, private val v: ShortArray) {
    fun depthAt(x: Double, z: Double): Double? {
        if (x.isNaN() || z.isNaN() || x < minX || x > maxX || z < minZ || z > maxZ) return null
        val i = floor((x - minX) / (maxX - minX) * w).toInt().coerceIn(0, w - 1)
        val j = floor((maxZ - z) / (maxZ - minZ) * h).toInt().coerceIn(0, h - 1)
        val raw = v[j * w + i].toInt() and 0xFFFF
        return if (raw == 65535) null else raw / 100.0
    }

    companion object {
        fun parse(gz: ByteArray, bathy: JSONObject): DepthGrid? = try {
            val g = bathy.optJSONObject("depthGrid") ?: return null
            val fr = bathy.optJSONObject("frame") ?: return null
            val w = g.getInt("width"); val h = g.getInt("height")
            val raw = GZIPInputStream(gz.inputStream()).use { it.readBytes() }
            if (raw.size != w * h * 2) null else {
                val s = ShortArray(w * h)
                for (k in 0 until w * h) s[k] = ((raw[2 * k].toInt() and 0xFF) or ((raw[2 * k + 1].toInt() and 0xFF) shl 8)).toShort()
                DepthGrid(w, h, fr.getDouble("minX"), fr.getDouble("maxX"), fr.getDouble("minZ"), fr.getDouble("maxZ"), s)
            }
        } catch (e: Exception) { null }
    }
}

object Geo {
    /** Координаты карты игры «NNN:NNN» (как MapGridCoordinates.Convert в Windows: float, ceil). null — нет преобразования / вне карты. */
    fun grid(map: JSONObject?, x: Double, z: Double): String? {
        val g = map?.optJSONObject("grid") ?: return null
        if (x.isNaN() || z.isNaN()) return null
        val ox = g.optDouble("originX").toFloat(); val oz = g.optDouble("originZ").toFloat()
        val sx = g.optDouble("sizeX").toFloat(); val sz = g.optDouble("sizeZ").toFloat(); val cell = g.optDouble("cell").toFloat()
        if (ox.isNaN() || oz.isNaN() || sx.isNaN() || sz.isNaN() || cell <= 0f) return null
        val fx = x.toFloat(); val fz = z.toFloat()
        if (fx < ox || fz < oz || fx > ox + sx || fz > oz + sz) return null
        return ceil((fx - ox) / cell).toInt().toString() + ":" + ceil((fz - oz) / cell).toInt()
    }

    private fun date(t: String): String? = if (t.length >= 10 && t[4] == '-' && t[7] == '-') t.substring(0, 10) else null

    /**
     * Зоны активности без связи — тот же алгоритм, что ActivityZones.Filter + Compute на ПК: ячейка cellM, центр — среднее
     * точек, виды по убыванию числа, порядок зон: число ↓, ix, iz. Точки — сохранённые уловы этого водоёма (все, с флагом verified).
     */
    fun zones(all: JSONObject, species: String?, from: String?, to: String?, unverified: Boolean, depth: DepthGrid?): JSONObject {
        val cell = all.optDouble("cellM", 20.0)
        val pts = mutableListOf<JSONObject>()
        val a = all.optJSONArray("points") ?: JSONArray()
        for (i in 0 until a.length()) {
            val p = a.getJSONObject(i)
            if (p.isNull("x") || p.isNull("z")) continue
            if (!p.optBoolean("verified") && !unverified) continue
            if (!species.isNullOrEmpty() && p.optString("fish") != species) continue
            if (from != null || to != null) {
                val d = date(p.optString("time")) ?: continue
                if (from != null && d < from) continue
                if (to != null && d > to) continue
            }
            pts += p
        }
        // ZONES-02: зоны — круги радиусом cell/2 (как RadiusClustering + ActivityZones.Compute на ПК)
        val r = cell / 2
        val zs = radiusGroups(pts.map { it.getDouble("x") to it.getDouble("z") }, r).map { ix ->
            val g = ix.map { pts[it] }
            val cx = g.map { it.getDouble("x") }.average(); val cz = g.map { it.getDouble("z") }.average()
            val rad = max(r, g.maxOf { hypot(it.getDouble("x") - cx, it.getDouble("z") - cz) })
            val times = g.mapNotNull { p -> p.optString("time").takeIf { date(it) != null }?.take(16) }.sorted()
            val sp = g.groupBy { it.optString("fish") }.map { it.key to it.value.size }.sortedWith(compareByDescending<Pair<String, Int>> { it.second }.thenBy { it.first })
            Triple(cx to cz, g.size, JSONObject()
                .put("x", cx).put("z", cz).put("r", rad).put("minX", cx - rad).put("maxX", cx + rad)
                .put("minZ", cz - rad).put("maxZ", cz + rad).put("count", g.size)
                .put("species", JSONArray().also { arr -> sp.forEach { arr.put(JSONObject().put("name", it.first).put("count", it.second)) } })
                .put("first", times.firstOrNull() ?: "").put("last", times.lastOrNull() ?: "")
                .put("depthM", depth?.depthAt(cx, cz) ?: JSONObject.NULL)
                .put("pts", JSONArray().also { arr -> ix.forEach { arr.put(it) } }))
        }.sortedWith(compareByDescending<Triple<Pair<Double, Double>, Int, JSONObject>> { it.second }.thenBy { it.first.first }.thenBy { it.first.second })
        val spAll = a.let { arr -> (0 until arr.length()).map { arr.getJSONObject(it) }.filter { !it.isNull("x") && (it.optBoolean("verified") || unverified) } }
            .groupBy { it.optString("fish") }.map { it.key to it.value.size }.sortedByDescending { it.second }
        return JSONObject().put("locationId", all.optInt("locationId")).put("cellM", cell).put("radiusM", r).put("zoneShape", "circle").put("total", pts.size).put("offline", true)
            .put("species", JSONArray().also { arr -> spAll.forEach { arr.put(JSONObject().put("name", it.first).put("count", it.second)) } })
            .put("zones", JSONArray().also { arr -> zs.forEach { arr.put(it.third) } })
            .put("points", JSONArray().also { arr -> pts.forEach { arr.put(it) } })
            .put("note", all.optString("note"))
    }
}

/** ZONES-02: тот же алгоритм, что RadiusClustering.Group на ПК: у каждой точки — число соседей в радиусе r; точки по убыванию
 *  соседей (при равенстве — по X, Z, индексу) становятся центрами и забирают все свободные точки в радиусе r. */
fun radiusGroups(pts: List<Pair<Double, Double>>, r0: Double): List<List<Int>> {
    val n = pts.size; if (n == 0) return emptyList()
    val r = if (r0 > 0) r0 else 5.0; val r2 = r * r
    fun key(i: Int) = floor(pts[i].first / r).toLong() to floor(pts[i].second / r).toLong()
    val buckets = HashMap<Pair<Long, Long>, MutableList<Int>>()
    for (i in 0 until n) buckets.getOrPut(key(i)) { mutableListOf() }.add(i)
    fun near(i: Int): List<Int> {
        val (bx, bz) = key(i); val out = mutableListOf<Int>()
        for (dx in -1L..1L) for (dz in -1L..1L) buckets[(bx + dx) to (bz + dz)]?.forEach { j ->
            val ex = pts[j].first - pts[i].first; val ez = pts[j].second - pts[i].second
            if (ex * ex + ez * ez <= r2 + 1e-9) out.add(j)
        }
        return out
    }
    val cnt = IntArray(n) { near(it).size }
    val order = (0 until n).sortedWith(compareByDescending<Int> { cnt[it] }.thenBy { pts[it].first }.thenBy { pts[it].second }.thenBy { it })
    val used = BooleanArray(n); val res = mutableListOf<List<Int>>()
    for (c in order) {
        if (used[c]) continue
        val g = near(c).filter { !used[it] }.sorted()
        g.forEach { used[it] = true }
        res.add(g)
    }
    return res
}
