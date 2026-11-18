package ru.fisherlogger.companion

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.os.Build
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.net.HttpURLConnection
import java.net.Inet4Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.net.URL
import java.net.URLEncoder
import java.security.KeyFactory
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.spec.ECGenParameterSpec
import java.security.spec.X509EncodedKeySpec
import javax.crypto.Cipher
import javax.crypto.KeyAgreement
import javax.crypto.KeyGenerator
import javax.crypto.Mac
import javax.crypto.SecretKey
import javax.crypto.SecretKeyFactory
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.PBEKeySpec
import javax.crypto.spec.SecretKeySpec

/**
 * Клиент сервиса FisherLogger (только чтение) с протоколом сопряжения/подписи, совпадающим с CompanionServer.cs:
 *  - сопряжение: GET /api/pair/hello → ECDH P-256 + PBKDF2-SHA256(код) → HMAC-доказательство → POST /api/pair;
 *  - секрет устройства = HMAC(Kc, ECDH_x || "fl-companion-v1"); по сети не передаётся;
 *  - каждый запрос: X-FL-Device / X-FL-Time / X-FL-Nonce / X-FL-Sig = HMAC(secret, "GET\n" + target + "\n" + time + "\n" + nonce);
 *  - ответ проверяется по X-FL-Sig = HMAC(secret, nonce + "\n" + status + "\n" + sha256hex(body)).
 * Секреты хранятся зашифрованными ключом AES-GCM из Android Keystore (ключ не извлекается из устройства).
 *
 * NEXT-ANDROID-03: несколько сопряжённых ПК (Pairing). У каждого — список известных адресов (Endpoint) с пометкой сети,
 * в которой адрес работал. Сеть определяется по подсети и шлюзу (SSID не используется: на Android 8.1+ он требует
 * разрешения на геолокацию). Сеть — только подсказка, какой адрес пробовать первым: «свой» ПК подтверждается ТОЛЬКО
 * подписью ответа секретом этого сопряжения. ServerId из /api/ping — несекретный фильтр, чтобы не слать подписанные
 * запросы заведомо чужим ПК. Секреты и подписи никогда не пишутся в журнал.
 */
class ApiException(val status: Int, message: String) : Exception(message)

private fun b64(b: ByteArray): String = Base64.encodeToString(b, Base64.NO_WRAP)
private fun unb64(s: String): ByteArray = Base64.decode(s, Base64.NO_WRAP)
private fun hmac(key: ByteArray, data: ByteArray): ByteArray =
    Mac.getInstance("HmacSHA256").run { init(SecretKeySpec(key, "HmacSHA256")); doFinal(data) }
private fun hex(b: ByteArray) = b.joinToString("") { "%02x".format(it) }

/** Известный адрес ПК. network — ключ сети, в которой адрес работал ("" — неизвестно). */
data class Endpoint(val host: String, val network: String, val lastOk: Long)

/** Одно сопряжение с одним ПК. serverId = "" — перенесено из версии 0.2.0, ещё не подтверждено ответом ПК. */
data class Pairing(
    val deviceId: String,
    val secretEnc: String,
    val secretIv: String,
    val port: Int,
    val serverId: String,
    val serverName: String,
    val endpoints: List<Endpoint>,
    val lastOk: Long,
    val created: Long
) {
    fun toJson(): JSONObject = JSONObject()
        .put("deviceId", deviceId).put("secretEnc", secretEnc).put("secretIv", secretIv).put("port", port)
        .put("serverId", serverId).put("serverName", serverName).put("lastOk", lastOk).put("created", created)
        .put("endpoints", JSONArray().also { a -> endpoints.forEach { a.put(JSONObject().put("host", it.host).put("network", it.network).put("lastOk", it.lastOk)) } })

    companion object {
        fun fromJson(j: JSONObject): Pairing {
            val eps = mutableListOf<Endpoint>()
            val a = j.optJSONArray("endpoints") ?: JSONArray()
            for (i in 0 until a.length()) { val e = a.getJSONObject(i); eps += Endpoint(e.getString("host"), e.optString("network"), e.optLong("lastOk")) }
            return Pairing(j.getString("deviceId"), j.getString("secretEnc"), j.getString("secretIv"), j.optInt("port", 47811),
                j.optString("serverId"), j.optString("serverName"), eps, j.optLong("lastOk"), j.optLong("created"))
        }
    }
}

/** Сеть, в которой сейчас телефон: ключ (подсеть + шлюз) и собственный IPv4-адрес с длиной префикса. */
data class NetInfo(val key: String, val ip: Inet4Address, val prefix: Int)

class PairingStore(context: Context) {
    private val prefs = context.getSharedPreferences("fl_companion", Context.MODE_PRIVATE)
    private val alias = "fl_companion_secret_key"
    private val lock = Any()

    private fun key(): SecretKey {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (ks.getEntry(alias, null) as? KeyStore.SecretKeyEntry)?.let { return it.secretKey }
        val gen = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        gen.init(
            KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build()
        )
        return gen.generateKey()
    }

    // поля ручного ввода (подсказка в форме сопряжения)
    var host: String
        get() = prefs.getString("host", "") ?: ""
        set(v) = prefs.edit().putString("host", v).apply()
    var port: Int
        get() = prefs.getInt("port", 47811)
        set(v) = prefs.edit().putInt("port", v).apply()
    var darkTheme: Boolean
        get() = prefs.getBoolean("dark", true)
        set(v) = prefs.edit().putBoolean("dark", v).apply()
    /** DESIGN-03: показывать технические данные (модель времени, сессия, сырые числа погоды, причины). По умолчанию выкл. */
    var tech: Boolean
        get() = prefs.getBoolean("tech", false)
        set(v) = prefs.edit().putBoolean("tech", v).apply()

    init { migrateLegacy() }

    /** Версия 0.2.0 хранила одно сопряжение в полях deviceId/secretEnc/secretIv/host/port. Переносим его без потерь.
     *  Старые поля не удаляются (откат на 0.2.0 продолжит работать), пока это сопряжение не забыто. */
    private fun migrateLegacy() = synchronized(lock) {
        if (prefs.contains("pairings_v1")) return
        val id = prefs.getString("deviceId", "") ?: ""
        val enc = prefs.getString("secretEnc", null)
        val iv = prefs.getString("secretIv", null)
        val list = JSONArray()
        if (id.isNotEmpty() && enc != null && iv != null) {
            val h = host.trim()
            val p = Pairing(id, enc, iv, port, "", "", if (h.isNotEmpty()) listOf(Endpoint(h, "", 0)) else emptyList(), 0, System.currentTimeMillis())
            list.put(p.toJson())
        }
        prefs.edit().putString("pairings_v1", list.toString()).apply()
    }

    fun all(): List<Pairing> = synchronized(lock) {
        val a = try { JSONArray(prefs.getString("pairings_v1", "[]")) } catch (e: Exception) { JSONArray() }
        (0 until a.length()).mapNotNull { try { Pairing.fromJson(a.getJSONObject(it)) } catch (e: Exception) { null } }
    }

    private fun write(list: List<Pairing>) {
        val a = JSONArray(); list.forEach { a.put(it.toJson()) }
        prefs.edit().putString("pairings_v1", a.toString()).apply()
    }

    val hasPairings: Boolean get() = all().isNotEmpty()

    fun secretOf(p: Pairing): ByteArray? = try {
        val c = Cipher.getInstance("AES/GCM/NoPadding")
        c.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, unb64(p.secretIv)))
        c.doFinal(unb64(p.secretEnc))
    } catch (e: Exception) { null }

    /** Новое сопряжение. Сопряжение с тем же ПК (тот же serverId) заменяется, его известные адреса сохраняются. */
    fun addPairing(deviceId: String, secret: ByteArray, port: Int, serverId: String, serverName: String, host: String, network: String): Pairing = synchronized(lock) {
        val c = Cipher.getInstance("AES/GCM/NoPadding")
        c.init(Cipher.ENCRYPT_MODE, key())
        val enc = c.doFinal(secret)
        val now = System.currentTimeMillis()
        val old = all()
        val same = old.filter { serverId.isNotEmpty() && it.serverId == serverId }
        val eps = (listOf(Endpoint(host, network, now)) + same.flatMap { it.endpoints }).distinctBy { it.host + "|" + it.network }.take(12)
        val p = Pairing(deviceId, b64(enc), b64(c.iv), port, serverId, serverName, eps, now, now)
        same.forEach { clearLegacyIf(it.deviceId) }
        write(listOf(p) + old.filter { it !in same && it.deviceId != deviceId })
        p
    }

    /** Успешное подключение: запомнить адрес для текущей сети и подтверждённый serverId/имя ПК. */
    fun recordSuccess(deviceId: String, host: String, network: String, serverId: String, serverName: String) = synchronized(lock) {
        val now = System.currentTimeMillis()
        write(all().map { p ->
            if (p.deviceId != deviceId) p else {
                val eps = (listOf(Endpoint(host, network, now)) + p.endpoints.filterNot { it.host == host && it.network == network }).take(12)
                p.copy(endpoints = eps, lastOk = now,
                    serverId = if (p.serverId.isEmpty()) serverId else p.serverId,
                    serverName = serverName.ifEmpty { p.serverName })
            }
        })
    }

    fun byId(deviceId: String): Pairing? = all().firstOrNull { it.deviceId == deviceId }

    /** Удалить одно сопряжение (только по явному действию пользователя — не при ошибках связи). */
    fun remove(deviceId: String) = synchronized(lock) {
        write(all().filter { it.deviceId != deviceId })
        clearLegacyIf(deviceId)
    }

    private fun clearLegacyIf(deviceId: String) {
        if ((prefs.getString("deviceId", "") ?: "") == deviceId)
            prefs.edit().remove("deviceId").remove("secretEnc").remove("secretIv").apply()
    }
}

/** Активное подключение: сопряжение + рабочий адрес + поправка часов этого ПК. */
data class Active(val pairing: Pairing, val host: String, val clockOffsetMs: Long, val network: String)

/** Ответ /api/ping (без подписи — только для поиска). */
data class Responder(val host: String, val port: Int, val serverId: String, val pairing: Boolean, val api: Int)

sealed class Probe {
    data class Ok(val status: JSONObject, val offsetMs: Long) : Probe()
    object NotOurs : Probe()
    object Revoked : Probe()
    object Unreachable : Probe()
}

/** AUDIT-04 (A4): частный адрес (10/8, 172.16/12, 192.168/16, 169.254/16, 127/8, fe80::/10, fc00::/7). Имена не разрешаются — только IP. */
fun isPrivateHost(host: String): Boolean {
    val h = host.removePrefix("[").removeSuffix("]")
    val numeric = h.isNotEmpty() && (h.all { it.isDigit() || it == '.' } || (h.contains(':') && h.all { it.isLetterOrDigit() || it == ':' || it == '.' || it == '%' }))
    if (!numeric) return false
    return try {
        val a = InetAddress.getByName(h)
        val b = a.address
        a.isSiteLocalAddress || a.isLinkLocalAddress || a.isLoopbackAddress || (b.size == 16 && (b[0].toInt() and 0xFE) == 0xFC)
    } catch (e: Exception) { false }
}

class FisherApi(private val store: PairingStore) {
    @Volatile var active: Active? = null
    private val rnd = SecureRandom()

    private class Raw(val status: Int, val body: ByteArray, val sig: String?)

    private fun send(host: String, port: Int, method: String, target: String, headers: Map<String, String>, body: ByteArray? = null,
                     timeoutMs: Int = 15000, connectTimeoutMs: Int = 5000): Raw {
        // AUDIT-04 (A4): незашифрованные запросы — только к ПК в локальной сети (частные адреса), никогда в интернет
        require(isPrivateHost(host.trim())) { "адрес вне локальной сети: $host" }
        val conn = URL("http://" + host.trim() + ":" + port + target).openConnection() as HttpURLConnection
        conn.requestMethod = method
        conn.connectTimeout = connectTimeoutMs
        conn.readTimeout = timeoutMs
        conn.useCaches = false
        headers.forEach { (k, v) -> conn.setRequestProperty(k, v) }
        if (body != null) {
            conn.doOutput = true
            conn.setRequestProperty("Content-Type", "application/json")
            conn.outputStream.use { it.write(body) }
        }
        try {
            val status = conn.responseCode
            val stream = if (status >= 400) conn.errorStream else conn.inputStream
            val out = ByteArrayOutputStream()
            stream?.use { it.copyTo(out) }
            return Raw(status, out.toByteArray(), conn.getHeaderField("X-FL-Sig"))
        } finally {
            conn.disconnect()
        }
    }

    private fun errorText(r: Raw): String = try { JSONObject(String(r.body)).optString("error", "HTTP ${r.status}") } catch (e: Exception) { "HTTP ${r.status}" }

    /** Без авторизации: доступность ПК и разница часов (второе значение: время ПК − время телефона). */
    fun ping(host: String, port: Int, timeoutMs: Int = 5000, connectTimeoutMs: Int = 5000): Pair<JSONObject, Long> {
        val t0 = System.currentTimeMillis()
        val r = send(host, port, "GET", "/api/ping", emptyMap(), timeoutMs = timeoutMs, connectTimeoutMs = connectTimeoutMs)
        if (r.status != 200) throw ApiException(r.status, errorText(r))
        val j = JSONObject(String(r.body))
        val t1 = System.currentTimeMillis()
        val off = if (j.has("now")) j.getLong("now") - (t0 + t1) / 2 else 0L
        return j to off
    }

    /** Сопряжение по коду. Возвращает (deviceId, secret); сохраняет вызывающий. */
    fun pair(host: String, port: Int, code: String): Pair<String, ByteArray> {
        val hello = send(host, port, "GET", "/api/pair/hello", emptyMap())
        if (hello.status != 200) throw ApiException(hello.status, errorText(hello))
        val h = JSONObject(String(hello.body))
        val pid = h.getString("pairingId")
        val serverPub = h.getString("serverPub")
        val salt = unb64(h.getString("salt"))
        val iterations = h.getInt("iterations")

        val kpg = KeyPairGenerator.getInstance("EC")
        kpg.initialize(ECGenParameterSpec("secp256r1"))
        val kp = kpg.generateKeyPair()
        val clientPub = b64(kp.public.encoded)   // X.509 SubjectPublicKeyInfo, как ExportSubjectPublicKeyInfo в .NET

        val kc = SecretKeyFactory.getInstance("PBKDF2WithHmacSHA256")
            .generateSecret(PBEKeySpec(code.toCharArray(), salt, iterations, 256)).encoded
        val proof = b64(hmac(kc, "pair1|$serverPub|$clientPub".toByteArray(Charsets.UTF_8)))
        val name = (Build.MANUFACTURER + " " + Build.MODEL).trim()
        val body = JSONObject().put("pairingId", pid).put("clientPub", clientPub).put("deviceName", name).put("proof", proof)
        val res = send(host, port, "POST", "/api/pair", emptyMap(), body.toString().toByteArray(Charsets.UTF_8))
        if (res.status != 200) throw ApiException(res.status, errorText(res))
        val rj = JSONObject(String(res.body))
        val deviceId = rj.getString("deviceId")

        val peer = KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(unb64(serverPub)))
        val ka = KeyAgreement.getInstance("ECDH")
        ka.init(kp.private)
        ka.doPhase(peer, true)
        val shared = ka.generateSecret()
        val secret = hmac(kc, shared + "fl-companion-v1".toByteArray(Charsets.UTF_8))
        val confirm = b64(hmac(secret, "confirm|$deviceId".toByteArray(Charsets.UTF_8)))
        if (confirm != rj.getString("confirm")) throw ApiException(0, "Подтверждение сервера не совпало — сопряжение отклонено")
        return deviceId to secret
    }

    /** Подписанный GET к конкретному ПК; ответ принимается только с верной подписью этого сопряжения. */
    private fun signedRawAt(deviceId: String, secret: ByteArray, host: String, port: Int, offsetMs: Long, target: String,
                            timeoutMs: Int = 15000): Raw {
        val time = (System.currentTimeMillis() + offsetMs).toString()
        val nonceBytes = ByteArray(16).also { rnd.nextBytes(it) }
        val nonce = hex(nonceBytes)
        val sig = b64(hmac(secret, "GET\n$target\n$time\n$nonce".toByteArray(Charsets.UTF_8)))
        val r = send(host, port, "GET", target, mapOf("X-FL-Device" to deviceId, "X-FL-Time" to time, "X-FL-Nonce" to nonce, "X-FL-Sig" to sig),
            timeoutMs = timeoutMs)
        if (r.status == 401) throw ApiException(401, errorText(r))
        val bodyHash = hex(MessageDigest.getInstance("SHA-256").digest(r.body))
        val expect = b64(hmac(secret, "$nonce\n${r.status}\n$bodyHash".toByteArray(Charsets.UTF_8)))
        if (r.sig == null || r.sig != expect) throw ApiException(0, "Подпись ответа не совпала — данные отклонены")
        if (r.status != 200) throw ApiException(r.status, errorText(r))
        return r
    }

    /**
     * Проверка адреса для сопряжения: ping (быстрый фильтр по serverId) → подписанный /api/status.
     * Ok — только при верной подписи ответа и совпадающем serverId (если он уже известен).
     * Revoked — ПК ответил 401 (сопряжение отозвано/неверно). Ответ 401 не подписан, поэтому Revoked лишь показывает
     * сообщение и замедляет повторы; сопряжение и показанные данные при этом не удаляются.
     */
    fun probe(p: Pairing, host: String, connectTimeoutMs: Int = 2500): Probe {
        val secret = store.secretOf(p) ?: return Probe.NotOurs
        try {
            val (pj, off) = try { ping(host, p.port, timeoutMs = 4000, connectTimeoutMs = connectTimeoutMs) } catch (e: Exception) { return Probe.Unreachable }
            if (pj.optString("app") != "FisherLogger") return Probe.NotOurs
            val pingId = pj.optString("serverId")
            if (p.serverId.isNotEmpty() && pingId.isNotEmpty() && pingId != p.serverId) return Probe.NotOurs
            return try {
                val st = JSONObject(String(signedRawAt(p.deviceId, secret, host, p.port, off, "/api/status", timeoutMs = 8000).body, Charsets.UTF_8))
                val sid = st.optString("serverId")
                if (p.serverId.isNotEmpty() && sid.isNotEmpty() && sid != p.serverId) Probe.NotOurs else Probe.Ok(st, off)
            } catch (e: ApiException) {
                val ours = if (p.serverId.isNotEmpty()) pingId == p.serverId else p.endpoints.any { it.host == host }
                if (e.status == 401 && ours) Probe.Revoked else Probe.NotOurs
            } catch (e: Exception) { Probe.Unreachable }
        } finally { java.util.Arrays.fill(secret, 0) }
    }

    private fun signedRaw(target: String): Raw {
        val a = active ?: throw ApiException(-1, "нет подключения")
        val secret = store.secretOf(a.pairing) ?: throw ApiException(401, "Устройство не сопряжено")
        try { return signedRawAt(a.pairing.deviceId, secret, a.host, a.pairing.port, a.clockOffsetMs, target) }
        finally { java.util.Arrays.fill(secret, 0) }
    }

    fun getJson(path: String, query: Map<String, String?> = emptyMap()): JSONObject {
        val q = query.filterValues { !it.isNullOrEmpty() }.entries.joinToString("&") { it.key + "=" + URLEncoder.encode(it.value, "UTF-8") }
        val target = if (q.isEmpty()) path else "$path?$q"
        return JSONObject(String(signedRaw(target).body, Charsets.UTF_8))
    }

    fun getBytes(path: String): ByteArray = signedRaw(path).body
}

object Net {
    /** Текущая сеть Wi-Fi/Ethernet: ключ «подсеть/префикс|шлюз» и собственный IPv4. null — нет локальной сети. */
    @Suppress("DEPRECATION")
    fun current(context: Context): NetInfo? = try {
        val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
        val candidates = cm.allNetworks.filter { n ->
            val caps = cm.getNetworkCapabilities(n)
            caps != null && (caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) || caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET))
        }
        candidates.firstNotNullOfOrNull { n ->
            val lp = cm.getLinkProperties(n) ?: return@firstNotNullOfOrNull null
            val la = lp.linkAddresses.firstOrNull { it.address is Inet4Address && !it.address.isLoopbackAddress } ?: return@firstNotNullOfOrNull null
            val ip = la.address as Inet4Address
            val prefix = la.prefixLength
            val gw = lp.routes.firstOrNull { it.isDefaultRoute && it.gateway is Inet4Address }?.gateway?.hostAddress ?: "-"
            NetInfo(subnet(ip, prefix) + "/" + prefix + "|" + gw, ip, prefix)
        }
    } catch (e: Exception) { null }

    private fun toInt(a: Inet4Address): Int = a.address.fold(0) { acc, b -> (acc shl 8) or (b.toInt() and 0xFF) }
    private fun fromInt(v: Int): String = "${(v ushr 24) and 255}.${(v ushr 16) and 255}.${(v ushr 8) and 255}.${v and 255}"
    private fun subnet(ip: Inet4Address, prefix: Int): String {
        val mask = if (prefix <= 0) 0 else (-1 shl (32 - prefix))
        return fromInt(toInt(ip) and mask)
    }

    /** Адреса для поиска: подсеть телефона (/24 и уже — целиком; шире /24 — только своя /24, не более 254 адресов). */
    fun sweepHosts(n: NetInfo): List<String> {
        val p = if (n.prefix in 24..30) n.prefix else 24
        val mask = -1 shl (32 - p)
        val base = toInt(n.ip) and mask
        val count = (1 shl (32 - p)) - 2
        val own = toInt(n.ip)
        return (1..count).map { base + it }.filter { it != own }.map { fromInt(it) }
    }

    /** Быстрый поиск FisherLogger: TCP-соединение (300 мс) → /api/ping. Не более 48 соединений одновременно. */
    suspend fun sweep(api: FisherApi, n: NetInfo, ports: Collection<Int>): List<Responder> = withContext(Dispatchers.IO) {
        val sem = Semaphore(48)
        coroutineScope {
            sweepHosts(n).flatMap { h -> ports.map { h to it } }.map { (h, port) ->
                async {
                    sem.withPermit {
                        try {
                            Socket().use { s -> s.connect(InetSocketAddress(InetAddress.getByName(h), port), 300) }
                            val (j, _) = api.ping(h, port, timeoutMs = 2000, connectTimeoutMs = 1000)
                            if (j.optString("app") == "FisherLogger")
                                Responder(h, port, j.optString("serverId"), j.optBoolean("pairing"), j.optInt("api")) else null
                        } catch (e: Exception) { null }
                    }
                }
            }.awaitAll().filterNotNull()
        }
    }
}
