package ru.fisherlogger.companion

import android.app.Application
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject

enum class ConnState { NotConfigured, NotPaired, Searching, Connecting, Connected, Reconnecting, Offline, AuthFailed }

enum class Screen { Dashboard, Analytics, Maps, History, Connection }   // DESIGN-03: Dashboard = «Сейчас», Analytics = «Куда идти», History = «Журнал»

data class AnalyticsFilterUi(
    val allSessions: Boolean = true,
    val series: String = "both",          // both | mine | pub
    val speciesKey: String? = null,
    val speciesName: String = "Все виды",
    val loc: String? = null,              // null = все, "none" = не записан, иначе id
    val locName: String = "Все водоёмы",
    val from: String? = null,             // yyyy-MM-dd
    val to: String? = null,
    val excludeOwn: Boolean = true,
    val percent: Boolean = false
)

class AppViewModel(app: Application) : AndroidViewModel(app) {
    val store = PairingStore(app)
    private val api = FisherApi(store)

    /** Сопряжённые ПК (для экрана «ПК»). */
    var pairings by mutableStateOf(store.all()); private set
    /** ПК, к которому подключены сейчас (deviceId сопряжения), и его адрес. */
    var activeDevice by mutableStateOf<String?>(null); private set
    var activeHost by mutableStateOf<String?>(null); private set
    /** Результат «Найти ПК в сети»: ответившие FisherLogger (без подписи — только подсказка для сопряжения). */
    var discovered by mutableStateOf<List<Responder>>(emptyList()); private set
    var searching by mutableStateOf(false); private set

    var screen by mutableStateOf(if (store.hasPairings) Screen.Dashboard else Screen.Connection)
    var darkTheme by mutableStateOf(store.darkTheme)
    var tech by mutableStateOf(store.tech)   // DESIGN-03
        private set
    var conn by mutableStateOf(if (store.hasPairings) ConnState.Searching else if (store.host.isEmpty()) ConnState.NotConfigured else ConnState.NotPaired)
        private set
    var connMessage by mutableStateOf("")
        private set
    var lastOk by mutableStateOf<Long?>(null)
        private set
    var busy by mutableStateOf(false)
        private set

    var status by mutableStateOf<JSONObject?>(null); private set
    var options by mutableStateOf<JSONObject?>(null); private set
    var analytics by mutableStateOf<JSONObject?>(null); private set
    var analyticsError by mutableStateOf<String?>(null); private set
    var catches by mutableStateOf<JSONArray?>(null); private set
    var publicFeed by mutableStateOf<JSONArray?>(null); private set
    var maps by mutableStateOf<JSONArray?>(null); private set
    var filter by mutableStateOf(AnalyticsFilterUi()); private set

    // ANDROID-UX-01: хранилище на телефоне и «Советник»
    val offline = OfflineStore(app)
    /** Данные на экране взяты из памяти телефона (последняя удачная загрузка), а не получены сейчас. */
    var fromCache by mutableStateOf(false); private set
    var cacheSavedAt by mutableStateOf(0L); private set
    var syncText by mutableStateOf(""); private set
    var advisor by mutableStateOf<JSONObject?>(null); private set
    var advisorError by mutableStateOf<String?>(null); private set
    var icons by mutableStateOf<JSONObject?>(null); private set
    var advisorSel by mutableStateOf<Int?>(null)
    var advisorQuery by mutableStateOf("")
    var advisorHours by mutableStateOf(false)
    /** Переход «показать место на карте» из Советника: (водоём, X, Z); номер — чтобы повтор срабатывал. */
    var mapFocusReq by mutableStateOf<Triple<Int, Double, Double>?>(null)
    /** BRIDGE-FB-01: точки других игроков (справочник fisher-hub) для показа поверх карты водоёма; x/z — центр клетки карты игры. */
    class RefHi(val loc: Int, val fish: String, val pts: List<Triple<Double, Double, String>>)
    var refHighlight by mutableStateOf<RefHi?>(null)
    private val depthGrids = HashMap<Int, DepthGrid?>()
    // HOURS-01: «По часам» — записи сохраняются на телефоне и считаются здесь (как на ПК)
    var hourData by mutableStateOf<HourStatsK.Data?>(null); private set
    var hourError by mutableStateOf<String?>(null); private set
    var hoursFish by mutableStateOf<String?>(null)
    var hoursLoc by mutableStateOf<Int?>(null)
    var hoursPeriod by mutableStateOf("auto")
    var hoursSeries by mutableStateOf("auto")
    var hoursSel by mutableStateOf(-1)

    // GOAL-PHONE-01: «Цель» — план и подсказка «Сейчас» считаются на ПК (/api/goal); последний ответ хранится на телефоне
    var advisorTab by mutableStateOf(0)                       // 0 — Цель, 1 — Рыба, 2 — По часам
    var goal by mutableStateOf<JSONObject?>(null); private set
    var goalError by mutableStateOf<String?>(null); private set
    /** Ответ получен от ПК только что (true) или показан сохранённый (false). */
    var goalFresh by mutableStateOf(false); private set
    var goalFish by mutableStateOf(0)                         // 0 — цель, выбранная на ПК
    var goalCls by mutableStateOf(2)
    var goalLoc by mutableStateOf(0)                          // 0 — любой водоём, где водится
    var goalPick by mutableStateOf(false)
    var goalQuery by mutableStateOf("")
    private var goalJob: Job? = null
    private fun goalFile(f: Int = goalFish, c: Int = goalCls, l: Int = goalLoc) = if (f <= 0) "goal_pc.json" else "goal_${f}_${c}_${l}.json"

    fun chooseGoal(fish: Int, cls: Int, loc: Int) {
        goalFish = fish; goalCls = cls.coerceIn(0, 3); goalLoc = loc; goalPick = false; goalQuery = ""
        viewModelScope.launch {
            withContext(Dispatchers.IO) { offline.putJson("goal_sel.json", JSONObject().put("fish", goalFish).put("cls", goalCls).put("loc", goalLoc)) }
            goal = withContext(Dispatchers.IO) { offline.json(goalFile()) }; goalFresh = false
            loadGoal()
        }
    }

    fun loadGoal() {
        if (goalJob?.isActive == true) return
        val f = goalFish; val c = goalCls; val l = goalLoc
        goalJob = viewModelScope.launch {
            try {
                val q = if (f > 0) mapOf("fish" to f.toString(), "cls" to c.toString(), "loc" to (if (l > 0) l.toString() else null)) else emptyMap()
                val j = withContext(Dispatchers.IO) { api.getJson("/api/goal", q) }
                if (f != goalFish || c != goalCls || l != goalLoc) return@launch          // цель сменили, пока шёл запрос
                goal = j; goalError = null; goalFresh = true
                withContext(Dispatchers.IO) { offline.putJson(goalFile(f, c, l), j) }
            } catch (e: ApiException) {
                goalFresh = false
                goalError = if (e.status == 404) "На ПК старая версия FisherLogger — обновите её, чтобы появилась «Цель»." else msg(e)
            } catch (e: kotlinx.coroutines.CancellationException) { throw e
            } catch (e: Exception) { goalFresh = false; goalError = msg(e) }
        }
    }

    fun loadHourData() {
        viewModelScope.launch {
            try {
                val j = withContext(Dispatchers.IO) { api.getJson("/api/hourdata") }
                hourData = withContext(Dispatchers.Default) { HourStatsK.parse(j) }; hourError = null
                withContext(Dispatchers.IO) { offline.putJson("hourdata.json", j) }
            } catch (e: ApiException) {
                if (hourData == null) hourError = if (e.status == 404) "На ПК старая версия FisherLogger — обновите её, чтобы появилась статистика по часам." else msg(e)
            } catch (e: Exception) { if (hourData == null) hourError = msg(e) }
        }
    }

    init { viewModelScope.launch { loadCache() } }

    private fun sc() = if (filter.allSessions) "all" else "current"

    /** Показать последние сохранённые данные сразу при запуске (до связи с ПК). */
    private suspend fun loadCache() {
        val s = sc()
        val c = withContext(Dispatchers.IO) {
            listOf(offline.json("status.json"), offline.json("options_$s.json"), offline.json("catches_$s.json"), offline.json("public_$s.json"),
                   offline.json("maps_$s.json"), offline.json("bathy.json"), offline.json("advisor.json"), offline.json("icons.json"), offline.json("analytics_$s.json"))
        }
        if (status == null) c[0]?.let { status = it }
        if (options == null) options = c[1]
        if (catches == null) catches = c[2]?.optJSONArray("items")
        if (publicFeed == null) publicFeed = c[3]?.optJSONArray("items")
        if (maps == null) maps = c[4]?.optJSONArray("maps")
        if (bathyList == null) bathyList = c[5]?.optJSONArray("items")
        if (advisor == null) advisor = c[6]
        if (icons == null) icons = c[7]
        if (analytics == null) c[8]?.let { a -> if (a.optString("_filter") == filterKey(filter)) analytics = a }
        if (hourData == null) withContext(Dispatchers.IO) { offline.json("hourdata.json") }?.let { j -> hourData = withContext(Dispatchers.Default) { HourStatsK.parse(j) } }
        withContext(Dispatchers.IO) { offline.json("goal_sel.json") }?.let { s -> goalFish = s.optInt("fish"); goalCls = s.optInt("cls", 2); goalLoc = s.optInt("loc") }
        if (goal == null) goal = withContext(Dispatchers.IO) { offline.json(goalFile()) }
        cacheSavedAt = offline.savedAt
        if (lastOk == null && cacheSavedAt > 0) fromCache = true
        if (selectedMap == null) selectedMap = defaultMap()
    }

    private fun filterKey(f: AnalyticsFilterUi) = listOf(f.allSessions, f.series, f.speciesKey, f.loc, f.from, f.to, f.excludeOwn).joinToString("|")
    private val online get() = api.active != null && conn == ConnState.Connected

    private fun grid(id: Int): DepthGrid? {
        if (depthGrids.containsKey(id)) return depthGrids[id]
        val b = bathyFor(id) ?: return null
        val sha = b.optJSONObject("depthGrid")?.optString("sha256") ?: return null
        val g = if (offline.text("grid_$id.sha") == sha) offline.bytes("grid_$id.gz")?.let { DepthGrid.parse(it, b) } else null
        if (g != null) depthGrids[id] = g
        return g
    }
    fun depthAt(id: Int, x: Double, z: Double): Double? = grid(id)?.depthAt(x, z)
    fun mapMeta(id: Int?): JSONObject? = maps.let { a -> if (a == null || id == null) null else (0 until a.length()).map { a.getJSONObject(it) }.firstOrNull { it.optInt("locationId") == id } }

    /** «Советник»: пакет карточек всех видов (как в Windows) — сохраняется на телефоне. */
    fun loadAdvisor() {
        viewModelScope.launch {
            try {
                val a = withContext(Dispatchers.IO) { api.getJson("/api/advisor") }
                advisor = a; advisorError = null
                withContext(Dispatchers.IO) { offline.putJson("advisor.json", a) }
                val n = a.optJSONArray("species")?.length() ?: 0
                if (icons == null || icons?.optInt("_n") != n) {
                    val ic = withContext(Dispatchers.IO) { api.getJson("/api/icons") }.put("_n", n)
                    icons = ic
                    withContext(Dispatchers.IO) { offline.putJson("icons.json", ic) }
                }
            } catch (e: ApiException) {
                advisorError = if (e.status == 404) "На ПК старая версия FisherLogger — обновите её, чтобы появился «Советник»." else msg(e)
            } catch (e: Exception) { advisorError = msg(e) }
        }
    }

    private var syncJob: Job? = null
    /**
     * Сохранить для работы без связи: картинки карт, глубины (картинка и сетка), все уловы и зоны водоёмов.
     * Картинки и сетки скачиваются заново только при смене контрольной суммы на ПК.
     */
    private fun syncOffline() {
        if (syncJob?.isActive == true) return
        syncJob = viewModelScope.launch {
            val list = maps.objsKt()
            var i = 0
            for (m in list) {
                i++
                if (!online) break
                val id = m.optInt("locationId")
                syncText = "Сохраняю для офлайна: водоём $i из ${list.size}"
                try {
                    withContext(Dispatchers.IO) {
                        val sha = m.optString("sha256")
                        if (sha.isNotEmpty() && offline.text("map_$id.sha") != sha) {
                            offline.putBytes("map_$id.png", api.getBytes("/api/maps/$id/image")); offline.putText("map_$id.sha", sha)
                        }
                        val b = bathyFor(id)
                        if (b != null && b.optString("status") == "IMPLEMENTED") {
                            val os = b.optString("overlaySha256")
                            if (os.isNotEmpty() && offline.text("bathy_$id.sha") != os) {
                                offline.putBytes("bathy_$id.png", api.getBytes("/api/maps/$id/bathymetry")); offline.putText("bathy_$id.sha", os)
                            }
                            val gs = b.optJSONObject("depthGrid")?.optString("sha256") ?: ""
                            if (gs.isNotEmpty() && offline.text("grid_$id.sha") != gs) {
                                offline.putBytes("grid_$id.gz", api.getBytes("/api/maps/$id/depthgrid")); offline.putText("grid_$id.sha", gs)
                            }
                        }
                        if (m.optInt("catches") > 0) {
                            offline.putJson("zonesall_$id.json", api.getJson("/api/maps/$id/zones", mapOf("scope" to "all", "unverified" to "1")))
                            offline.putJson("points_all_$id.json", api.getJson("/api/maps/$id/points", mapOf("scope" to "all")))
                        }
                    }
                } catch (e: kotlinx.coroutines.CancellationException) { throw e
                } catch (e: Exception) { /* старый ПК без сетки глубин и т.п. — пропускаем */ }
            }
            depthGrids.entries.removeAll { it.value == null }
            syncText = if (online) "Карты и глубины сохранены на телефоне (" + (offline.sizeBytes() / (1024 * 1024)) + " МБ)" else ""
        }
    }
    private fun JSONArray?.objsKt(): List<JSONObject> = if (this == null) emptyList() else (0 until length()).map { getJSONObject(it) }

    var selectedMap by mutableStateOf<Int?>(null)
    val mapImages = mutableStateMapOf<Int, Bitmap?>()
    val mapPoints = mutableStateMapOf<Int, JSONObject>()
    var historyTab by mutableStateOf(0)

    // NEXT-ANDROID-02: слои и фильтры карты (переживают поворот — живут в ViewModel)
    var showZones by mutableStateOf(true)
    var showBathy by mutableStateOf(false)
    var showPoints by mutableStateOf(false)
    var mapSpecies by mutableStateOf<String?>(null)
    var mapFrom by mutableStateOf<String?>(null)
    var mapTo by mutableStateOf<String?>(null)
    var mapUnverified by mutableStateOf(false)
    var bathyList by mutableStateOf<JSONArray?>(null); private set
    val bathyImages = mutableStateMapOf<Int, Bitmap?>()
    val mapZones = mutableStateMapOf<Int, JSONObject>()
    var depthProbe by mutableStateOf<JSONObject?>(null)
    // масштаб и сдвиг карты живут в ViewModel — сохраняются при повороте и смене раскладки
    val mapZoom = androidx.compose.runtime.mutableFloatStateOf(1f)
    val mapOffX = androidx.compose.runtime.mutableFloatStateOf(0f)
    val mapOffY = androidx.compose.runtime.mutableFloatStateOf(0f)

    fun bathyFor(id: Int): JSONObject? {
        val a = bathyList ?: return null
        for (i in 0 until a.length()) { val b = a.getJSONObject(i); if (b.optInt("locationId") == id) return b }
        return null
    }

    private var zonesGen = 0
    /** Зоны без связи: из сохранённых уловов водоёма тем же алгоритмом, что на ПК. */
    private suspend fun localZones(id: Int): JSONObject? = withContext(Dispatchers.Default) {
        offline.json("zonesall_$id.json")?.let { Geo.zones(it, mapSpecies, mapFrom, mapTo, mapUnverified, grid(id)) }
    }
    fun loadZones(id: Int) {
        val gen = ++zonesGen
        viewModelScope.launch {
            if (!online) { localZones(id)?.let { if (gen == zonesGen || !mapZones.containsKey(id)) mapZones[id] = it }; return@launch }
            try {
                val q = mapOf("scope" to if (filter.allSessions) "all" else "current", "species" to mapSpecies, "from" to mapFrom, "to" to mapTo,
                              "unverified" to if (mapUnverified) "1" else "0")
                val z = withContext(Dispatchers.IO) { api.getJson("/api/maps/$id/zones", q) }
                if (gen == zonesGen || !mapZones.containsKey(id)) mapZones[id] = z   // устаревший ответ не затирает новый
            } catch (e: Exception) {
                val lz = localZones(id)
                if (lz != null) { if (gen == zonesGen || !mapZones.containsKey(id)) mapZones[id] = lz } else connMessage = "Зоны: " + msg(e)
            }
        }
    }

    fun setMapFilter(species: String? = mapSpecies, from: String? = mapFrom, to: String? = mapTo, unverified: Boolean = mapUnverified) {
        mapSpecies = species; mapFrom = from; mapTo = to; mapUnverified = unverified
        selectedMap?.let { loadZones(it) }
    }

    fun loadBathy(id: Int) {
        if (bathyImages.containsKey(id)) return
        viewModelScope.launch {
            try {
                val sha = bathyFor(id)?.optString("overlaySha256") ?: ""
                val bytes = withContext(Dispatchers.IO) {
                    if (sha.isNotEmpty() && offline.text("bathy_$id.sha") == sha) offline.bytes("bathy_$id.png")
                    else api.getBytes("/api/maps/$id/bathymetry").also { if (sha.isNotEmpty()) { offline.putBytes("bathy_$id.png", it); offline.putText("bathy_$id.sha", sha) } }
                } ?: throw ApiException(-1, "нет сохранённых глубин")
                bathyImages[id] = withContext(Dispatchers.Default) { BitmapFactory.decodeByteArray(bytes, 0, bytes.size) }
            } catch (e: Exception) { bathyImages[id] = null; connMessage = "Глубины: " + msg(e) }
        }
    }

    fun probeDepth(id: Int, x: Double, z: Double) {
        val g = grid(id)
        if (g != null) {
            depthProbe = JSONObject().put("locationId", id).put("x", x).put("z", z).put("depthM", g.depthAt(x, z) ?: JSONObject.NULL).put("local", true)
            return
        }
        viewModelScope.launch {
            try {
                depthProbe = withContext(Dispatchers.IO) {
                    api.getJson("/api/maps/$id/depth", mapOf("x" to String.format(java.util.Locale.US, "%.2f", x), "z" to String.format(java.util.Locale.US, "%.2f", z)))
                }
            } catch (e: Exception) { connMessage = "Глубина: " + msg(e) }
        }
    }

    private var poller: Job? = null
    private var refreshJob: Job? = null
    private var foreground = false
    private var lastDataVersion: String? = null
    private var lastRefreshAt = 0L
    private var lastSweepAt = 0L

    // Опрос запускается из MainActivity.onStart (setForeground(true)) и останавливается в onStop: в фоне сеть не используется.

    fun toggleTheme() { darkTheme = !darkTheme; store.darkTheme = darkTheme }
    fun toggleTech() { tech = !tech; store.tech = tech }

    private fun ru(m: String?): String = when {
        m == null -> "ошибка"
        m.contains("wrong pairing code") -> "неверный код сопряжения"
        m.contains("pairing not active") -> "сопряжение не начато: создайте код в FisherLogger (кнопка «Телефон»)"
        m.contains("unknown device") -> "ПК не знает это устройство (сопряжение отозвано?)"
        m.contains("clock skew") -> "часы телефона и ПК расходятся больше чем на 2 минуты"
        m.contains("replayed") -> "повтор запроса отклонён"
        m.contains("bad signature") -> "неверная подпись запроса — повторите сопряжение"
        m.contains("too many") -> "слишком много неудачных попыток, подождите 5 минут"
        else -> m
    }

    private fun msg(e: Throwable): String = when (e) {
        is ApiException -> ru(e.message)
        is java.net.ConnectException -> "ПК не отвечает (FisherLogger закрыт, сервис выключен или брандмауэр)"
        is java.net.SocketTimeoutException -> "Нет ответа от ПК (тайм-аут)"
        is java.net.UnknownHostException -> "Неверный адрес"
        is java.net.NoRouteToHostException -> "Нет маршрута к ПК (другая сеть?)"
        else -> e.javaClass.simpleName + ": " + (e.message ?: "")
    }

    private fun netKey(): String = Net.current(getApplication())?.key ?: ""

    /** Активити на экране / в фоне. В фоне опрос ПК останавливается (данные остаются на экране). */
    fun setForeground(fg: Boolean) {
        if (fg == foreground) return
        foreground = fg
        if (fg) { if (store.hasPairings) startPolling() } else { poller?.cancel(); poller = null }
    }

    fun saveAddress(host: String, port: Int) { store.host = host.trim(); store.port = port }

    fun testConnection(host: String, port: Int) {
        saveAddress(host, port)
        viewModelScope.launch {
            busy = true
            try {
                val (j, _) = withContext(Dispatchers.IO) { api.ping(host, port) }
                val known = j.optString("serverId").let { id -> id.isNotEmpty() && pairings.any { it.serverId == id } }
                connMessage = "ПК найден: FisherLogger API ${j.optInt("api")}" +
                    (if (j.optBoolean("pairing")) " · ожидает сопряжения" else "") + (if (known) " · уже сопряжён" else "")
            } catch (e: Exception) { connMessage = msg(e) }
            busy = false
        }
    }

    /** Ручное сопряжение (нужно один раз для каждого ПК; повторно — только если ПК отозвал устройство). */
    fun pair(host: String, port: Int, code: String) {
        saveAddress(host, port)
        viewModelScope.launch {
            busy = true
            try {
                val net = withContext(Dispatchers.IO) { netKey() }
                val p = withContext(Dispatchers.IO) {
                    val (pj, _) = api.ping(host, port)
                    if (pj.optString("app") != "FisherLogger") throw ApiException(0, "по этому адресу не FisherLogger")
                    val (id, secret) = api.pair(host, port, code.filter { it.isDigit() })
                    try { store.addPairing(id, secret, port, pj.optString("serverId"), "", host.trim(), net) } finally { java.util.Arrays.fill(secret, 0) }
                }
                pairings = store.all()
                // новое сопряжение сразу становится активным
                val r = withContext(Dispatchers.IO) { api.probe(p, host.trim()) }
                if (r is Probe.Ok) activate(p, host.trim(), r, net)
                connMessage = "Сопряжено"
                screen = Screen.Dashboard
                startPolling()
            } catch (e: Exception) {
                connMessage = "Сопряжение не удалось: " + msg(e)
                if (!store.hasPairings) conn = ConnState.NotPaired
            }
            busy = false
        }
    }

    /** Забыть одно сопряжение на телефоне (по явному действию пользователя). */
    fun forget(deviceId: String) {
        val wasActive = api.active?.pairing?.deviceId == deviceId
        store.remove(deviceId)
        pairings = store.all()
        if (wasActive) {
            poller?.cancel(); api.active = null; activeDevice = null; activeHost = null
            clearData()
        }
        connMessage = "Сопряжение удалено с телефона. На ПК отзовите устройство в окне «Телефон»."
        if (!store.hasPairings) { poller?.cancel(); conn = ConnState.NotPaired }
        else if (wasActive) startPolling()
    }

    private fun clearData() {
        offline.clear(); fromCache = false; cacheSavedAt = 0L; advisor = null; icons = null; depthGrids.clear(); syncText = ""; hourData = null
        goal = null; goalError = null; goalFresh = false; goalFish = 0; goalCls = 2; goalLoc = 0
        status = null; options = null; analytics = null; catches = null; publicFeed = null; maps = null
        mapImages.clear(); mapPoints.clear(); lastOk = null; lastDataVersion = null
        bathyList = null; bathyImages.clear(); mapZones.clear(); depthProbe = null; selectedMap = null
    }

    /** Кнопка «Переподключиться»: заново найти сопряжённый ПК (с поиском по сети). */
    fun reconnect() {
        api.active = null; lastSweepAt = 0
        manualHint = store.host.trim().ifEmpty { null }   // адрес из поля тоже проверяется (подписью) — без повторного сопряжения
        startPolling()
    }
    @Volatile private var manualHint: String? = null

    /** Кнопка «Найти ПК в сети»: список FisherLogger в текущей сети (для первого сопряжения без ввода адреса). */
    fun discover() {
        if (searching) return
        viewModelScope.launch {
            searching = true
            try {
                val n = withContext(Dispatchers.IO) { Net.current(getApplication()) }
                if (n == null) { connMessage = "Телефон не подключён к Wi-Fi"; discovered = emptyList() }
                else {
                    val ports = (pairings.map { it.port } + store.port + 47811).distinct()
                    discovered = Net.sweep(api, n, ports)
                    connMessage = if (discovered.isEmpty()) "В сети не найден FisherLogger с включённым сервисом" else ""
                }
            } catch (e: Exception) { connMessage = msg(e) }
            searching = false
        }
    }

    private fun activate(p: Pairing, host: String, ok: Probe.Ok, net: String) {
        api.active = Active(p, host, ok.offsetMs, net)
        store.recordSuccess(p.deviceId, host, net, ok.status.optString("serverId"), ok.status.optString("serverName"))
        pairings = store.all()
        activeDevice = p.deviceId; activeHost = host
    }

    private class Resolved(val ok: Probe.Ok?, val revoked: Boolean)

    /**
     * Найти сопряжённый ПК: сначала известные адреса (сначала — запомненные для текущей сети), затем — если не нашёлся —
     * поиск по подсети телефона (не чаще раза в 60 с). Каждый кандидат подтверждается подписанным ответом.
     */
    private suspend fun resolve(): Resolved = withContext(Dispatchers.IO) {
        val net = Net.current(getApplication())
        val key = net?.key ?: ""
        val ps = store.all()
        if (ps.isEmpty()) return@withContext Resolved(null, false)
        var revoked = false
        val tried = HashSet<String>()
        val ordered = ps.sortedWith(compareByDescending<Pairing> { p -> p.endpoints.any { it.network == key && key.isNotEmpty() } }.thenByDescending { it.lastOk })
        val cands = ordered.flatMap { p ->
            p.endpoints.sortedWith(compareByDescending<Endpoint> { it.network == key && key.isNotEmpty() }.thenByDescending { it.lastOk }).map { p to it.host }
        }
        for ((p, h) in cands) {
            if (!tried.add(p.deviceId + "@" + h + ":" + p.port)) continue
            when (val r = api.probe(p, h)) {
                is Probe.Ok -> { withContext(Dispatchers.Main) { activate(p, h, r, key) }; return@withContext Resolved(r, false) }
                Probe.Revoked -> revoked = true
                else -> {}
            }
        }
        manualHint?.let { h ->
            manualHint = null
            for (p in ordered) {
                if (!tried.add(p.deviceId + "@" + h + ":" + p.port)) continue
                when (val r = api.probe(p, h)) {
                    is Probe.Ok -> { withContext(Dispatchers.Main) { activate(p, h, r, key) }; return@withContext Resolved(r, false) }
                    Probe.Revoked -> revoked = true
                    else -> {}
                }
            }
        }
        val now = System.currentTimeMillis()
        if (net != null && now - lastSweepAt > 60_000) {
            lastSweepAt = now
            val found = Net.sweep(api, net, ps.map { it.port }.distinct())
            for (resp in found) for (p in ordered) {
                if (p.port != resp.port) continue
                if (p.serverId.isNotEmpty() && resp.serverId != p.serverId) continue
                if (!tried.add(p.deviceId + "@" + resp.host + ":" + p.port)) continue
                when (val r = api.probe(p, resp.host)) {
                    is Probe.Ok -> { withContext(Dispatchers.Main) { activate(p, resp.host, r, key) }; return@withContext Resolved(r, false) }
                    Probe.Revoked -> revoked = true
                    else -> {}
                }
            }
        }
        Resolved(null, revoked)
    }

    /**
     * Опрос каждые 10 с: подписанный /api/status (лёгкий запрос). Данные перезагружаются только когда ПК сообщил
     * новую dataVersion (новый улов, заброс, публичный улов, смена сессии). При обрыве — поиск ПК с паузой 3…30 с;
     * последние данные остаются на экране. Сопряжения при ошибках связи не удаляются.
     */
    fun startPolling() {
        poller?.cancel()
        if (!store.hasPairings) { conn = ConnState.NotPaired; return }
        poller = viewModelScope.launch {
            var backoff = 3000L
            while (isActive) {
                try {
                    var st: JSONObject? = null
                    if (api.active != null) {
                        try { st = withContext(Dispatchers.IO) { api.getJson("/api/status") } }
                        catch (e: ApiException) { if (e.status == 401 || e.status == 0 || e.status == -1) api.active = null else throw e }
                        catch (e: Exception) { api.active = null; connMessage = msg(e) }
                    }
                    if (st == null) {
                        conn = if (lastOk != null) ConnState.Reconnecting else ConnState.Searching
                        val r = resolve()
                        if (r.ok == null) {
                            if (r.revoked) {
                                conn = ConnState.AuthFailed
                                connMessage = "ПК не принимает это сопряжение (устройство отозвано?). Повторите сопряжение на вкладке «ПК»."
                                delay(60_000)
                            } else {
                                conn = if (lastOk != null) ConnState.Reconnecting else ConnState.Offline
                                connMessage = "Сопряжённый ПК не найден в сети: FisherLogger закрыт, сервис выключен или ПК в другой сети. Повтор автоматически."
                                delay(backoff); backoff = (backoff * 2).coerceAtMost(30_000)
                            }
                            continue
                        }
                        st = r.ok.status
                    }
                    onStatus(st)
                    backoff = 3000L
                    delay(10_000)
                } catch (e: kotlinx.coroutines.CancellationException) { throw e
                } catch (e: Exception) {
                    conn = if (lastOk != null) ConnState.Reconnecting else ConnState.Offline
                    connMessage = msg(e)
                    delay(backoff); backoff = (backoff * 2).coerceAtMost(30_000)
                }
            }
        }
    }

    private fun onStatus(st: JSONObject) {
        status = st
        lastOk = System.currentTimeMillis()
        viewModelScope.launch(Dispatchers.IO) { try { offline.putJson("status.json", st) } catch (e: Exception) {} }
        conn = ConnState.Connected
        connMessage = ""
        val dv = st.optString("dataVersion")
        val need = if (dv.isNotEmpty()) dv != lastDataVersion
                   else lastDataVersion == null || System.currentTimeMillis() - lastRefreshAt > 30_000   // старый ПК без dataVersion
        if (need) refreshData(dv.ifEmpty { "-" }, force = false)
    }

    /** Ручное обновление / смена охвата данных: перезагрузить сейчас. */
    fun refreshAll() = refreshData(null, force = true)

    /**
     * Обновление данных без сброса экрана: новые списки подменяют старые целиком (без дублей), выбранный водоём,
     * масштаб, сдвиг карты, фильтры и слои не меняются. Одновременно выполняется не больше одного обновления.
     */
    private fun refreshData(version: String?, force: Boolean) {
        if (refreshJob?.isActive == true) { if (!force) return; refreshJob?.cancel() }
        refreshJob = viewModelScope.launch {
            busy = true
            try {
                val scope = if (filter.allSessions) "all" else "current"
                val (o, c, p, m, b) = withContext(Dispatchers.IO) {
                    listOf(
                        api.getJson("/api/options", mapOf("scope" to scope)),
                        api.getJson("/api/catches", mapOf("scope" to scope, "limit" to "300")),
                        api.getJson("/api/public", mapOf("scope" to scope, "limit" to "300")),
                        api.getJson("/api/maps", mapOf("scope" to scope)),
                        api.getJson("/api/bathymetry"))
                }
                options = o
                catches = c.getJSONArray("items")
                publicFeed = p.getJSONArray("items")
                maps = m.getJSONArray("maps")
                bathyList = b.getJSONArray("items")
                lastRefreshAt = System.currentTimeMillis()
                // ANDROID-UX-01: сохранить на телефоне — показывается при запуске и без связи
                withContext(Dispatchers.IO) {
                    offline.putJson("options_$scope.json", o); offline.putJson("catches_$scope.json", c); offline.putJson("public_$scope.json", p)
                    offline.putJson("maps_$scope.json", m); offline.putJson("bathy.json", b)
                    offline.savedAt = lastRefreshAt; offline.serverName = api.active?.pairing?.serverName ?: ""
                }
                cacheSavedAt = lastRefreshAt; fromCache = false
                depthGrids.clear()
                if (version != null) lastDataVersion = version else lastDataVersion = status?.optString("dataVersion")?.ifEmpty { "-" }
                if (selectedMap == null) selectedMap = defaultMap()
                // кэш точек/зон других водоёмов устарел — загрузятся при выборе; неудачные картинки — повторить
                val sel = selectedMap
                mapZones.keys.filter { it != sel }.forEach { mapZones.remove(it) }
                mapPoints.keys.filter { it != sel }.forEach { mapPoints.remove(it) }
                mapImages.entries.filter { it.value == null }.map { it.key }.forEach { mapImages.remove(it) }
                bathyImages.entries.filter { it.value == null }.map { it.key }.forEach { bathyImages.remove(it) }
                loadAnalytics()
                loadAdvisor()
                loadHourData()
                loadGoal()
                sel?.let { reloadMapData(it) }
                syncOffline()
            } catch (e: kotlinx.coroutines.CancellationException) { throw e
            } catch (e: Exception) { connMessage = msg(e) }
            busy = false
        }
    }

    /** Точки и зоны выбранного водоёма — замена на месте, без сброса масштаба и выбора. */
    private fun reloadMapData(id: Int) {
        if (!mapImages.containsKey(id)) loadMap(id)
        loadZones(id)
        viewModelScope.launch {
            try {
                val scope = if (filter.allSessions) "all" else "current"
                mapPoints[id] = withContext(Dispatchers.IO) { api.getJson("/api/maps/$id/points", mapOf("scope" to scope)) }
            } catch (e: Exception) { connMessage = "Точки карты: " + msg(e) }
        }
    }

    private fun defaultMap(): Int? {
        val cur = status?.optInt("currentLocationId", -1) ?: -1
        val arr = maps ?: return null
        var best: Int? = null; var bestCount = -1
        for (i in 0 until arr.length()) {
            val m = arr.getJSONObject(i)
            if (m.getInt("locationId") == cur) return cur
            if (m.optInt("catches") > bestCount) { bestCount = m.optInt("catches"); best = m.getInt("locationId") }
        }
        return best
    }

    fun updateFilter(f: AnalyticsFilterUi) {
        val scopeChanged = f.allSessions != filter.allSessions
        val onlyView = f.copy(percent = filter.percent) == filter
        filter = f
        if (scopeChanged) refreshAll() else if (!onlyView) loadAnalytics()
    }

    private var analyticsGen = 0
    fun loadAnalytics() {
        val gen = ++analyticsGen
        viewModelScope.launch {
            try {
                val f = filter
                val q = mapOf(
                    "scope" to if (f.allSessions) "all" else "current",
                    "series" to f.series, "species" to f.speciesKey, "loc" to f.loc,
                    "from" to f.from, "to" to f.to, "excludeOwn" to if (f.excludeOwn) "1" else "0"
                )
                val a = withContext(Dispatchers.IO) { api.getJson("/api/analytics", q) }
                if (gen == analyticsGen) {
                    analytics = a; analyticsError = null
                    withContext(Dispatchers.IO) { offline.putJson("analytics_" + q["scope"] + ".json", JSONObject(a.toString()).put("_filter", filterKey(f))) }
                }
            } catch (e: Exception) { analyticsError = msg(e) }
        }
    }

    fun loadMap(id: Int, force: Boolean = false) {
        if (selectedMap != id) { depthProbe = null; mapZoom.floatValue = 1f; mapOffX.floatValue = 0f; mapOffY.floatValue = 0f }
        selectedMap = id
        if (!mapZones.containsKey(id)) loadZones(id)
        if (showBathy) loadBathy(id)
        viewModelScope.launch {
            try {
                if (!mapImages.containsKey(id)) {
                    val sha = mapMeta(id)?.optString("sha256") ?: ""
                    val bytes = withContext(Dispatchers.IO) {
                        if (sha.isNotEmpty() && offline.text("map_$id.sha") == sha) offline.bytes("map_$id.png")
                        else api.getBytes("/api/maps/$id/image").also { if (sha.isNotEmpty()) { offline.putBytes("map_$id.png", it); offline.putText("map_$id.sha", sha) } }
                    } ?: throw ApiException(-1, "карта не сохранена на телефоне")
                    mapImages[id] = withContext(Dispatchers.Default) { BitmapFactory.decodeByteArray(bytes, 0, bytes.size) }
                }
            } catch (e: Exception) { mapImages[id] = null; if (online) connMessage = "Карта: " + msg(e) }
            try {
                if (force || !mapPoints.containsKey(id)) {
                    val scope = if (filter.allSessions) "all" else "current"
                    if (!online) offline.json("points_all_$id.json")?.let { mapPoints[id] = it }
                    else mapPoints[id] = withContext(Dispatchers.IO) { api.getJson("/api/maps/$id/points", mapOf("scope" to scope)) }
                }
            } catch (e: Exception) { offline.json("points_all_$id.json")?.let { mapPoints[id] = it } ?: run { connMessage = "Точки карты: " + msg(e) } }
        }
    }
}
