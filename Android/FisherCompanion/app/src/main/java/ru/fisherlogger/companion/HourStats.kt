package ru.fisherlogger.companion

import org.json.JSONArray
import org.json.JSONObject
import java.math.BigDecimal
import java.math.RoundingMode

/**
 * HOURS-01: «По игровым часам» — точная копия HourStats.cs (ПК), чтобы телефон считал то же самое и без связи.
 * Сверяется с ПК самопроверкой (72 комбинации фильтров). Частота записей, не вероятность клёва.
 */
object HourStatsK {
    const val DayMinN = 30; const val DayMinHours = 8; const val ThreeMinN = 60; const val ThreeMinHours = 12
    const val DayMinN1 = 20; const val DayMinHours1 = 6; const val ThreeMinN1 = 30; const val ThreeMinHours1 = 8
    const val AllMinN = 30; const val AllMinHours = 8
    const val HourRecs = 3
    const val CovMin = 10.0
    const val CastsMin = 10
    const val IconMinN = 5; const val IconMinN3 = 15; const val IconMinLeader = 3
    const val IconMinShare = 0.15; const val IconLead = 1.5; const val IconLeadAbs = 2

    class Rec(val mine: Boolean, val real: String, val hour: Int, val key: String, val fish: String, val fishId: Int?, val loc: Int?, val matched: Boolean)
    class Cov(val slot: String, val hour: Int, val minutes: Double, val loc: Int?)
    class Cast(val real: String, val hour: Int, val loc: Int?)
    /** habitat: ключ вида → водоёмы, где он водится (AUDIT-02: свои уловы + список игры + справочник). */
    class Data(val recs: List<Rec>, val cov: List<Cov>, val casts: List<Cast>, val habitat: Map<String, Set<Int>> = emptyMap())

    class Query(val series: String = "auto", val loc: Int? = null, val species: String? = null, val period: String = "auto", val now: String)
    class Sp(val key: String, val name: String, val id: Int?, val n: Int, val share: Double)
    class Bin(val h: Int, val n: Int, val cov: Double, val casts: Int, val low: Boolean, val rate: Double?, val species: List<Sp>, val icon: Sp?, val tie: Boolean)
    class Report(
        val series: String, val seriesText: String, val period: String, val periodText: String, val autoNote: String,
        val level: String, val levelText: String, val rateUnit: String, val lowText: String,
        val total: Int, val hoursWith3: Int, val castsTotal: Int, val covTotal: Double,
        val hours: List<Bin>, val hours3: List<Bin>, val top: List<Sp>, val best: List<Int>, val note: String
    )

    fun periodName(p: String) = when (p) { "day" -> "последние сутки"; "3days" -> "последние 3 суток"; else -> "всё время" }
    fun seriesName(s: String) = if (s == "pub") "объявления других игроков" else "мои уловы"

    private fun JSONArray.intOrNull(i: Int): Int? = if (isNull(i)) null else optInt(i)

    fun parse(j: JSONObject): Data {
        val r = j.optJSONArray("recs") ?: JSONArray()
        // PHONE-02 (v3): старые объявления других игроков приходят группами [час, ключ, рыба, id, водоём, совпало, число] —
        // разворачиваем обратно в записи с временем-заглушкой (раньше любого порога периода): расчёт тот же, что на ПК.
        val recs = ArrayList<Rec>()
        j.optJSONArray("pubOld")?.let { po ->
            val oldReal = j.optString("oldReal", "2000-01-01 00:00:00")
            for (i in 0 until po.length()) { val a = po.getJSONArray(i)
                val rec = Rec(false, oldReal, a.optInt(0), a.optString(1), a.optString(2), a.intOrNull(3), a.intOrNull(4), a.optInt(5) == 1)
                repeat(a.optInt(6)) { recs.add(rec) } }
        }
        for (i in 0 until r.length()) { val a = r.getJSONArray(i)
            recs.add(Rec(a.optInt(0) == 1, a.optString(1), a.optInt(2), a.optString(3), a.optString(4), a.intOrNull(5), a.intOrNull(6), a.optInt(7) == 1)) }
        val c = j.optJSONArray("cov") ?: JSONArray()
        val cov = (0 until c.length()).map { i -> val a = c.getJSONArray(i); Cov(a.optString(0) + ":00", a.optInt(1), a.optDouble(2), a.intOrNull(3)) }
        val k = j.optJSONArray("casts") ?: JSONArray()
        val casts = (0 until k.length()).map { i -> val a = k.getJSONArray(i); Cast(a.optString(0), a.optInt(1), a.intOrNull(2)) }
        val hj = j.optJSONObject("habitat")
        val habitat = hj?.keys()?.asSequence()?.associateWith { k -> hj.getJSONArray(k).let { a -> (0 until a.length()).map { a.optInt(it) }.toSet() } } ?: emptyMap()
        return Data(recs, cov, casts, habitat)
    }

    private val fmt = java.text.SimpleDateFormat("yyyy-MM-dd HH:mm:ss", java.util.Locale.US)
    fun nowText(): String = fmt.format(java.util.Date())
    private fun since(p: String, now: String): String? {
        val h = when (p) { "day" -> 24; "3days" -> 72; else -> return null }
        val d = fmt.parse(now) ?: return null
        return fmt.format(java.util.Date(d.time - h * 3600_000L))
    }

    private fun pick(d: Data, q: Query, series: String, since: String?) = d.recs.filter { r ->
        r.mine == (series == "mine") && !(series == "pub" && r.matched) && (q.loc == null || r.loc == q.loc) &&
            (q.species == null || r.key == q.species) && (since == null || r.real >= since)
    }
    private fun with3(rs: List<Rec>) = rs.groupBy { it.hour }.count { it.value.size >= HourRecs }
    private fun enough(p: String, rs: List<Rec>, one: Boolean): Boolean {
        val n = rs.size; val h = with3(rs)
        return when (p) {
            "day" -> n >= (if (one) DayMinN1 else DayMinN) && h >= (if (one) DayMinHours1 else DayMinHours)
            "3days" -> n >= (if (one) ThreeMinN1 else ThreeMinN) && h >= (if (one) ThreeMinHours1 else ThreeMinHours)
            else -> n >= (if (one) DayMinN1 else AllMinN) && h >= (if (one) DayMinHours1 else AllMinHours)
        }
    }
    private fun round(v: Double, d: Int) = BigDecimal(v).setScale(d, RoundingMode.HALF_EVEN).toDouble()
    private fun rank(rs: List<Rec>): List<Sp> {
        val n = rs.size
        return rs.groupBy { it.key }.map { (k, g) -> Sp(k, g.first().fish, g.firstNotNullOfOrNull { it.fishId }, g.size, if (n > 0) round(g.size.toDouble() / n, 4) else 0.0) }
            .sortedWith(compareByDescending<Sp> { it.n }.thenBy { it.key })
    }
    private fun leader(sp: List<Sp>, n: Int, minN: Int): Pair<Sp?, Boolean> {
        if (n < minN || sp.isEmpty()) return null to false
        val a = sp[0]
        if (a.n < IconMinLeader || a.share < IconMinShare) return null to false
        if (sp.size > 1) {
            val b = sp[1]
            if (b.n == a.n) return null to true
            if (a.n < IconLead * b.n && a.n - b.n < IconLeadAbs) return null to true
        }
        return a to false
    }

    fun build(d: Data, q: Query): Report {
        val one = q.species != null
        val series = if (q.series == "mine" || q.series == "pub") q.series
            else if (listOf("day", "3days", "all").any { p -> enough(p, pick(d, q, "mine", since(p, q.now)), one) }) "mine" else "pub"
        var period = q.period; var autoNote = ""
        if (period == "auto") {
            period = "all"
            for (p in listOf("day", "3days")) if (enough(p, pick(d, q, series, since(p, q.now)), one)) { period = p; break }
            if (period != "day") {
                val dd = pick(d, q, series, since("day", q.now))
                autoNote = "За последние сутки мало записей (${dd.size}, хорошо покрыто часов: ${with3(dd)}) — показано за ${periodName(period)}."
            }
        }
        if (q.series == "auto" && series == "pub") autoNote = "Своих уловов мало — показаны объявления других игроков. " + autoNote
        val s = since(period, q.now)
        val rs = pick(d, q, series, s)
        val c = DoubleArray(24)
        for (x in d.cov) if ((s == null || x.slot >= s) && (q.loc == null || x.loc == q.loc)) c[x.hour] += x.minutes
        val mine = series == "mine"
        val cs = IntArray(24)
        val hab = if (one) d.habitat[q.species]?.takeIf { it.isNotEmpty() } else null
        if (mine) for (k in d.casts) if ((s == null || k.real >= s) && (q.loc == null || k.loc == q.loc) && (hab == null || (k.loc != null && k.loc in hab))) cs[k.hour]++
        val useCasts = mine && cs.sum() > 0
        val castsFrom = if (useCasts) d.casts.filter { (s == null || it.real >= s) && (q.loc == null || it.loc == q.loc) }.minOfOrNull { it.real } else null
        val en = enough(period, rs, one)
        val rateUnit = if (useCasts) "уловов на 10 забросов" else "записей на 10 мин наблюдения"
        val lowText = if (useCasts) "мало забросов" else "мало наблюдений"
        val level = if (rs.isEmpty()) "none" else if (en) "ok" else "low"
        val needN = when (period) { "day" -> if (one) DayMinN1 else DayMinN; "3days" -> if (one) ThreeMinN1 else ThreeMinN; else -> if (one) DayMinN1 else AllMinN }
        val needH = when (period) { "day" -> if (one) DayMinHours1 else DayMinHours; "3days" -> if (one) ThreeMinHours1 else ThreeMinHours; else -> if (one) DayMinHours1 else AllMinHours }
        val h3 = with3(rs)
        val levelText = when (level) {
            "none" -> "Нет записей для этого выбора."
            "low" -> "Пока мало данных: ${rs.size} записей, хорошо покрыто часов: $h3 (нужно $needN записей и $needH ч). Выводы делать рано."
            else -> "${rs.size} записей, хорошо покрыто часов: $h3 из 24."
        }
        fun bin(h0: Int, len: Int, minN: Int, covMin: Double, castsMin: Int): Bin {
            val inH = rs.filter { it.hour >= h0 && it.hour < h0 + len }
            val sp = rank(inH)
            var cv = 0.0; var ca = 0
            for (h in h0 until h0 + len) { cv += c[h]; ca += cs[h] }
            val low = if (useCasts) ca < castsMin else cv < covMin
            val rate = if (low) null else if (useCasts) round(inH.count { castsFrom == null || it.real >= castsFrom } * 10.0 / ca, 2) else round(inH.size * 10.0 / cv, 2)
            val (icon, tie) = if (!one && !low && en) leader(sp, inH.size, minN) else (null to false)
            return Bin(h0, inH.size, round(cv, 1), ca, low, rate, sp.take(12), icon, tie)
        }
        val hours = (0 until 24).map { bin(it, 1, IconMinN, CovMin, CastsMin) }
        val hours3 = (0 until 24 step 3).map { bin(it, 3, IconMinN3, CovMin * 3, CastsMin * 3) }
        val best = if (en) hours.filter { it.rate != null && it.n >= IconMinN }.sortedWith(compareByDescending<Bin> { it.rate }.thenBy { it.h }).take(3).map { it.h } else emptyList()
        val note = "Частота записанных " + (if (mine) "уловов" else "объявлений") + " по игровым часам (оценка времени ±3 игр. мин), не вероятность клёва. " +
            (if (useCasts) "«Лучшие часы» — по уловам на 10 ваших забросов: «больше уловов» обычно значит «больше забрасывали». "
             else if (mine) "Забросы за этот период не записаны — нормировка по наблюдению. " else "Водоём объявления — где были вы при приёме. ") +
            "«$lowText» — не «не ловится»."
        return Report(series, seriesName(series), period, periodName(period), autoNote, level, levelText, rateUnit, lowText,
            rs.size, h3, cs.sum(), round(c.sum(), 1), hours, hours3, rank(rs).take(10), best, note)
    }

    /** Самопроверка: те же 72 запроса, что посчитал ПК (parity_expected.json). Возвращает текст отчёта. */
    fun selfTest(data: JSONObject, expected: JSONArray): String {
        val d = parse(data)
        val sb = StringBuilder(); var ok = 0; var bad = 0
        for (i in 0 until expected.length()) {
            val e = expected.getJSONObject(i); val q = e.getJSONObject("q")
            val r = build(d, Query(q.optString("series"), if (q.isNull("loc")) null else q.optInt("loc"), if (q.isNull("species")) null else q.optString("species"),
                q.optString("period"), q.optString("now")))
            val diffs = mutableListOf<String>()
            fun chk(name: String, a: Any?, b: Any?) { if (a != b) diffs += "$name: ПК=$a телефон=$b" }
            chk("series", e.optString("series"), r.series); chk("period", e.optString("period"), r.period); chk("level", e.optString("level"), r.level)
            chk("total", e.optInt("total"), r.total); chk("h3", e.optInt("covered"), r.hoursWith3)
            fun ints(k: String) = e.getJSONArray(k).let { a -> (0 until a.length()).map { a.optInt(it) } }
            fun strs(k: String) = e.getJSONArray(k).let { a -> (0 until a.length()).map { if (a.isNull(it)) null else a.optString(it) } }
            fun bools(k: String) = e.getJSONArray(k).let { a -> (0 until a.length()).map { a.optBoolean(it) } }
            fun dbls(k: String) = e.getJSONArray(k).let { a -> (0 until a.length()).map { if (a.isNull(it)) null else a.optDouble(it) } }
            chk("n", ints("n"), r.hours.map { it.n }); chk("low", bools("low"), r.hours.map { it.low }); chk("casts", ints("casts"), r.hours.map { it.casts })
            chk("tie", bools("tie"), r.hours.map { it.tie }); chk("rate", dbls("rate"), r.hours.map { it.rate })
            chk("icon", strs("icon"), r.hours.map { it.icon?.key }); chk("icon3", strs("icon3"), r.hours3.map { it.icon?.key })
            chk("top", strs("top"), r.top.map { it.key + "=" + it.n }); chk("best", ints("best"), r.best)
            if (diffs.isEmpty()) ok++ else { bad++; sb.append("FAIL ").append(q.toString()).append(" → ").append(diffs.joinToString("; ")).append('\n') }
        }
        return "HOURS selftest: pass=$ok fail=$bad\n$sb"
    }
}
