package ru.fisherlogger.companion.ui

import androidx.compose.foundation.background
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
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Switch
import androidx.compose.material3.Tab
import androidx.compose.material3.TabRow
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import org.json.JSONArray
import org.json.JSONObject
import ru.fisherlogger.companion.AppViewModel
import ru.fisherlogger.companion.ConnState
import ru.fisherlogger.companion.Screen

@Composable
fun SectionTitle(t: String) = Text(t, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold,
    color = MaterialTheme.colorScheme.primary, modifier = Modifier.padding(top = 12.dp, bottom = 6.dp))

@Composable
fun Muted(t: String, size: Int = 13) = Text(t, fontSize = size.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)

@Composable
fun AppCard(modifier: Modifier = Modifier, content: @Composable () -> Unit) {
    Card(modifier.fillMaxWidth(), shape = RoundedCornerShape(14.dp),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface)) {
        Column(Modifier.padding(14.dp)) { content() }
    }
}

fun JSONArray?.objs(): List<JSONObject> = if (this == null) emptyList() else (0 until length()).map { getJSONObject(it) }
fun JSONObject.str(k: String): String? = if (isNull(k) || !has(k)) null else optString(k)

// ------------------------------------------------------------------------ подключение
@Composable
fun ConnectionScreen(vm: AppViewModel) {
    var host by rememberSaveable { mutableStateOf(vm.store.host) }
    var port by rememberSaveable { mutableStateOf(vm.store.port.toString()) }
    var code by rememberSaveable { mutableStateOf("") }
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp).widthIn(max = 640.dp)) {
        Text("Подключение к FisherLogger", style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.SemiBold)
        Spacer(Modifier.height(8.dp))
        AboutCard(androidx.compose.ui.platform.LocalContext.current)   // RELEASE-03: язык и обновление
        Spacer(Modifier.height(8.dp))
        NotifyCard(vm)   // FEAT-06
        Spacer(Modifier.height(8.dp))
        AppCard {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(Modifier.size(14.dp).background(connColor(vm.conn), CircleShape))
                Spacer(Modifier.size(8.dp))
                Text(connText(vm.conn), fontWeight = FontWeight.SemiBold, fontSize = 17.sp)
            }
            if (vm.connMessage.isNotEmpty()) { Spacer(Modifier.height(6.dp)); Muted(vm.connMessage, 14) }
            vm.lastOk?.let { Muted("Последний ответ ПК: " + java.text.SimpleDateFormat("HH:mm:ss", java.util.Locale.US).format(java.util.Date(it))) }
        }
        // NEXT-ANDROID-03: сопряжённые ПК. Переподключение — автоматически, в том числе после смены IP и сети.
        if (vm.pairings.isNotEmpty()) {
            SectionTitle("Сопряжённые ПК")
            Muted("Телефон сам находит эти ПК в любой сети, где они уже работали, и после смены IP-адреса. " +
                  "ПК принимается только если отвечает секретом сопряжения — другой компьютер в той же сети не подойдёт.")
            Spacer(Modifier.height(8.dp))
            vm.pairings.forEach { p ->
                AppCard {
                    val isActive = p.deviceId == vm.activeDevice && vm.conn == ConnState.Connected
                    Text((p.serverName.ifEmpty { "ПК FisherLogger" }) + if (isActive) " — подключено" else "", fontWeight = FontWeight.SemiBold, fontSize = 16.sp)
                    val fmtT = java.text.SimpleDateFormat("dd.MM HH:mm", java.util.Locale.US)
                    Muted("Сетей: " + p.endpoints.map { it.network }.filter { it.isNotEmpty() }.distinct().size.coerceAtLeast(1) +
                          " · последний адрес " + (if (isActive) vm.activeHost else p.endpoints.firstOrNull()?.host ?: "—") +
                          (if (p.lastOk > 0) " · связь " + fmtT.format(java.util.Date(p.lastOk)) else ""))
                    TextButton(onClick = { vm.forget(p.deviceId) }, modifier = Modifier.heightIn(min = 44.dp)) {
                        Text("Забыть этот ПК на телефоне", color = Pal.Danger)
                    }
                }
                Spacer(Modifier.height(8.dp))
            }
            OutlinedButton(onClick = { vm.saveAddress(host, port.toIntOrNull() ?: 47811); vm.reconnect() }, modifier = Modifier.fillMaxWidth().heightIn(min = 52.dp)) { Text("Переподключиться", fontSize = 16.sp) }
            Muted("Обычно не нужно. Если ПК не находится сам, введите его адрес ниже и нажмите «Переподключиться» — код не потребуется.")
        }

        SectionTitle(if (vm.pairings.isEmpty()) "Сопряжение с ПК" else "Сопрячь ещё один ПК / повторить сопряжение")
        Muted("Нужно один раз для каждого ПК. В FisherLogger: кнопка «Телефон» → включить сервис → «Создать код сопряжения». " +
              "Телефон и ПК должны быть в одной сети Wi-Fi/LAN.")
        Spacer(Modifier.height(8.dp))
        OutlinedButton(onClick = { vm.discover() }, enabled = !vm.searching, modifier = Modifier.fillMaxWidth().heightIn(min = 52.dp)) {
            if (vm.searching) CircularProgressIndicator(Modifier.size(22.dp), strokeWidth = 2.dp) else Text("Найти ПК в сети", fontSize = 16.sp)
        }
        vm.discovered.forEach { r ->
            val known = r.serverId.isNotEmpty() && vm.pairings.any { it.serverId == r.serverId }
            TextButton(onClick = { host = r.host; port = r.port.toString() }, modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp)) {
                Text(r.host + ":" + r.port + (if (known) " · уже сопряжён" else if (r.pairing) " · ждёт кода" else "") + "  — выбрать")
            }
        }
        Spacer(Modifier.height(8.dp))
        OutlinedTextField(host, { host = it.trim() }, label = { Text("IP-адрес ПК") }, singleLine = true,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri), modifier = Modifier.fillMaxWidth())
        Spacer(Modifier.height(8.dp))
        OutlinedTextField(port, { port = it.filter(Char::isDigit).take(5) }, label = { Text("Порт") }, singleLine = true,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number), modifier = Modifier.fillMaxWidth())
        Spacer(Modifier.height(10.dp))
        OutlinedButton(onClick = { vm.testConnection(host, port.toIntOrNull() ?: 47811) }, enabled = host.isNotBlank() && !vm.busy,
            modifier = Modifier.fillMaxWidth().heightIn(min = 52.dp)) { Text("Проверить связь", fontSize = 16.sp) }
        Spacer(Modifier.height(8.dp))
        Muted("Код действует 2 минуты, один раз. Секрет устройства по сети не передаётся и хранится в защищённом хранилище ключей Android.")
        Spacer(Modifier.height(8.dp))
        OutlinedTextField(code, { code = it.filter(Char::isDigit).take(6) }, label = { Text("Код (6 цифр)") }, singleLine = true,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword), modifier = Modifier.fillMaxWidth())
        Spacer(Modifier.height(10.dp))
        Button(onClick = { vm.pair(host, port.toIntOrNull() ?: 47811, code); code = "" },
            enabled = host.isNotBlank() && code.length == 6 && !vm.busy, modifier = Modifier.fillMaxWidth().heightIn(min = 52.dp)) {
            if (vm.busy) CircularProgressIndicator(Modifier.size(22.dp), strokeWidth = 2.dp) else Text("Сопрячь", fontSize = 16.sp)
        }
        SectionTitle("Оформление")
        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth().heightIn(min = 52.dp)) {
            Text("Тёмная тема", modifier = Modifier.weight(1f), fontSize = 16.sp)
            Switch(checked = vm.darkTheme, onCheckedChange = { vm.toggleTheme() })
        }
        SectionTitle("Для разработчика")
        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth().heightIn(min = 52.dp)) {
            Column(Modifier.weight(1f)) {
                Text("Технические данные", fontSize = 16.sp)
                Muted("модель времени, сессия, сырые числа погоды, причины ошибок")
            }
            Switch(checked = vm.tech, onCheckedChange = { vm.toggleTech() })
        }
        Spacer(Modifier.height(16.dp))
        Muted("Только чтение: приложение не может изменить или удалить данные FisherLogger.")
    }
}

// ------------------------------------------------------------------------ сейчас (DESIGN-03)
/** Цвет точки удочки: 0 ждите, 1 клюёт реже, 2 меняйте, 3 поклёвка, -1 нет оценки / не в воде. */
fun rodColor(level: Int): Color = when (level) { 0 -> Pal.Mine; 1 -> Pal.Warn; 2 -> Pal.Danger; 3 -> Color(0xFF4CC9BF); else -> Color(0xFF6E807E) }

@Composable
fun DashboardScreen(vm: AppViewModel, wide: Boolean) {
    val st = vm.status
    val coach = st?.optJSONObject("coach")
    val stale = vm.conn != ConnState.Connected
    LazyColumn(Modifier.fillMaxSize(), contentPadding = androidx.compose.foundation.layout.PaddingValues(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp)) {
        // связь — одной строкой; подробности на вкладке «ПК»
        if (stale || vm.busy) item {
            AppCard {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Box(Modifier.size(12.dp).background(connColor(vm.conn), CircleShape))
                    Spacer(Modifier.size(8.dp))
                    Text(connText(vm.conn), fontWeight = FontWeight.SemiBold, fontSize = 17.sp, modifier = Modifier.weight(1f))
                    if (vm.busy) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                }
                if (stale && vm.cacheSavedAt > 0) Text("Показано то, что сохранено на телефоне " + savedText(vm.cacheSavedAt) +
                    ". «Куда идти» и карта работают без связи.", fontSize = 14.sp, color = Pal.Warn)
                if (vm.tech && vm.connMessage.isNotEmpty()) Muted(vm.connMessage)
                if (vm.tech && vm.syncText.isNotEmpty()) Muted(vm.syncText)
            }
        }
        // время в игре и водоём
        if (st != null) item {
            AppCard {
                val clock = coach?.str("clock")
                Text(clock ?: ("В игре ≈ " + st.optString("gameTime")), fontWeight = FontWeight.SemiBold, fontSize = 17.sp)
                if (stale) Muted("на момент последней связи с ПК")
                val loc = coach?.str("location")?.takeIf { it.isNotEmpty() } ?: st.str("currentLocationName")
                if (!loc.isNullOrEmpty()) Text("Водоём: $loc", fontSize = 15.sp)
                if (vm.tech) {
                    Muted("Модель времени " + st.optString("gameTimeModel") + (if (st.optBoolean("serverTimeKnown")) " · время сервера известно" else " · по часам ПК") +
                          " · сессия " + st.optString("session"), 12)
                    st.optJSONObject("weather")?.let { w ->
                        Muted("Погода (сырые числа игры): температура " + fmt(w, "temperature") + ", облачность " + fmt(w, "cloudly") +
                              ", туман " + fmt(w, "fog") + ", дождь " + fmt(w, "rain") + ", гроза " + fmt(w, "thunder") + ", в " + w.optString("time"), 12)
                    }
                }
            }
        }
        // FEAT-06: мини-карта — вы и забросы, как на ПК
        if (vm.live != null && !stale) item { LiveMapCard(vm) }
        // удочки
        item { SectionTitle("Ваши удочки") }
        if (coach == null) item {
            Muted(if (st == null || stale) "Нет связи с ПК. Когда FisherLogger на ПК запущен, здесь будет совет по каждой удочке."
                  else "Чтобы видеть советы по удочкам, обновите FisherLogger на ПК.", 15)
        } else if (!coach.optBoolean("fishing")) item {
            Muted("Сейчас вы не рыбачите. После первого заброса здесь будет совет по каждой удочке.", 15)
        } else {
            items(coach.optJSONArray("rods").objs()) { r ->
                AppCard {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Box(Modifier.size(12.dp).background(rodColor(r.optInt("level", -1)), CircleShape))
                        Spacer(Modifier.size(8.dp))
                        Text("Удочка " + r.optInt("slot") + " · " + r.optString("rod"), fontWeight = FontWeight.SemiBold, fontSize = 16.sp, modifier = Modifier.weight(1f))
                    }
                    r.str("bait")?.takeIf { it.isNotEmpty() }?.let { Muted("«$it»", 14) }
                    Spacer(Modifier.height(4.dp))
                    Text(r.optString("status"), fontSize = 15.sp)
                    EmptyBitesList(r)   // FEAT-06
                }
            }
            val hints = coach.optJSONArray("hints")
            if (hints != null && hints.length() > 0) item {
                AppCard {
                    (0 until hints.length()).forEach { i ->
                        Text("• " + hints.optString(i), fontSize = 14.sp, color = Pal.Warn, modifier = Modifier.padding(vertical = 3.dp))
                    }
                }
            }
        }
        item {
            Row(horizontalArrangement = Arrangement.spacedBy(10.dp), modifier = Modifier.fillMaxWidth()) {
                Button(onClick = { vm.screen = Screen.Analytics }, modifier = Modifier.weight(1f).heightIn(min = 56.dp)) { Text("Куда идти", fontSize = 16.sp) }
                Button(onClick = { vm.screen = Screen.Maps }, modifier = Modifier.weight(1f).heightIn(min = 56.dp)) { Text("Карта", fontSize = 16.sp) }
            }
        }
        item { SectionTitle("Последние поимки") }
        val mine = vm.catches.objs().take(3)
        if (mine.isEmpty()) item { Muted(if (vm.conn == ConnState.Connected) "Пока нет" else "Нет данных — нет связи с ПК") }
        items(mine) { CatchCard(it, vm.tech) }
        item {
            OutlinedButton(onClick = { vm.screen = Screen.History }, modifier = Modifier.fillMaxWidth().heightIn(min = 52.dp)) { Text("Весь журнал") }
        }
    }
}

/** DESIGN-03: «2026-11-15 01:47:40» → «15.11 01:47»; иначе как есть. */
fun shortTime(s: String): String = Regex("""^\d{4}-(\d{2})-(\d{2})[ T](\d{2}:\d{2})""").find(s)?.let { m -> m.groupValues[2] + "." + m.groupValues[1] + " " + m.groupValues[3] } ?: s

private fun fmt(w: JSONObject, k: String): String = if (w.isNull(k)) "—" else String.format(java.util.Locale.US, "%.2f", w.optDouble(k))

// ------------------------------------------------------------------------ карточки
@Composable
fun CatchCard(c: JSONObject, tech: Boolean = false) {
    AppCard {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.size(10.dp).background(Pal.Mine, CircleShape))
            Spacer(Modifier.size(8.dp))
            Text(c.optString("fish"), fontWeight = FontWeight.SemiBold, fontSize = 17.sp, modifier = Modifier.weight(1f),
                maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(c.optInt("weight").toString() + " г", fontWeight = FontWeight.SemiBold, fontSize = 17.sp)
        }
        // BRIDGE-FB-01: класс — из игры (windex: обычный/хороший/крупный/трофейный), а не старая метка по весу; нет windex — «класс неизвестен»
        val wx = if (c.has("windex") && !c.isNull("windex")) c.optInt("windex", -1) else -1
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(if (wx >= 0) gradeName(wx).ifEmpty { "класс неизвестен" } else "класс неизвестен", fontSize = 13.sp,
                fontWeight = if (wx >= 1) FontWeight.SemiBold else FontWeight.Normal,
                color = gradeColor(wx) ?: MaterialTheme.colorScheme.onSurfaceVariant)
            if (tech) c.str("status")?.takeIf { it.isNotEmpty() }?.let { Muted(" · $it") }
        }
        // DESIGN-03: время одной строкой; оценки и причины — только в технических данных
        val valid = c.optBoolean("realTimeValid")
        val gt = c.str("gameTime")
        Text(shortTime(c.optString("realTime")) + (if (!valid) " (время неточное)" else "") + (if (gt != null) " · в игре ≈$gt" else ""), fontSize = 14.sp)
        if (tech) { if (gt != null) Muted("±" + c.optInt("gameTimeUncMin") + " игр. мин (оценка)") else Muted("Игровое время недоступно: " + c.optString("timeIssue")) }
        val loc = c.str("locationName")
        if (!loc.isNullOrEmpty()) Muted("Водоём: $loc")
        val bait = c.str("bait")
        if (!bait.isNullOrEmpty()) Muted("Наживка: $bait")
    }
}

@Composable
fun PublicCard(p: JSONObject, tech: Boolean = false) {
    AppCard {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.size(10.dp).background(Pal.Public, CircleShape))
            Spacer(Modifier.size(8.dp))
            Text(p.optString("fish"), fontWeight = FontWeight.SemiBold, fontSize = 17.sp, modifier = Modifier.weight(1f),
                maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(p.optInt("weight").toString() + " г", fontWeight = FontWeight.SemiBold, fontSize = 17.sp)
        }
        Muted(p.optString("player") + if (p.optBoolean("matchedOwn")) " · это ваш улов" else "")
        val gt = p.str("gameTime")
        Text(shortTime(p.optString("receivedAt")) + (if (gt != null) " · в игре ≈$gt" else ""), fontSize = 14.sp)
        if (tech) { if (gt != null) Muted("±" + p.optInt("gameTimeUncMin") + " игр. мин (оценка)") else Muted("Игровое время недоступно: " + p.optString("timeIssue")); Muted("Место улова в объявлении не передаётся") }
    }
}

// ------------------------------------------------------------------------ история
@Composable
fun HistoryScreen(vm: AppViewModel, wide: Boolean) {
    Column(Modifier.fillMaxSize()) {
        TabRow(selectedTabIndex = vm.historyTab, containerColor = MaterialTheme.colorScheme.surface) {
            Tab(vm.historyTab == 0, { vm.historyTab = 0 }, modifier = Modifier.heightIn(min = 52.dp),
                text = { Text("Мои уловы (" + (vm.catches?.length() ?: 0) + ")", fontSize = 15.sp) })
            Tab(vm.historyTab == 1, { vm.historyTab = 1 }, modifier = Modifier.heightIn(min = 52.dp),
                text = { Text("Другие игроки (" + (vm.publicFeed?.length() ?: 0) + ")", fontSize = 15.sp) })
        }
        val list = if (vm.historyTab == 0) vm.catches.objs() else vm.publicFeed.objs()
        if (list.isEmpty()) Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { Muted("Нет данных", 16) }
        else LazyColumn(Modifier.fillMaxSize(), contentPadding = androidx.compose.foundation.layout.PaddingValues(12.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp)) {
            items(list, key = { it.optString("key") }) { if (vm.historyTab == 0) CatchCard(it, vm.tech) else PublicCard(it, vm.tech) }
        }
    }
}

@Composable
fun Legend(color: Color, text: String) {
    Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(end = 12.dp)) {
        Box(Modifier.size(12.dp).background(color, RoundedCornerShape(3.dp)))
        Spacer(Modifier.size(6.dp))
        Text(text, fontSize = 13.sp)
    }
}
