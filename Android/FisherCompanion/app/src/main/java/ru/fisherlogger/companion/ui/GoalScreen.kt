package ru.fisherlogger.companion.ui

import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Clear
import androidx.compose.material.icons.filled.Search
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlinx.coroutines.delay
import org.json.JSONObject
import ru.fisherlogger.companion.AppViewModel
import ru.fisherlogger.companion.ConnState
import ru.fisherlogger.companion.Screen

// ------------------------------------------------------------------ GOAL-PHONE-01: «Цель» — как поймать нужную рыбу
// План из 5 шагов и подсказка «Сейчас» приходят с ПК готовыми строками (/api/goal, тот же расчёт, что вкладка «Цель» в Windows).
// Телефон хранит последний ответ по каждой выбранной цели и показывает его без связи; подсказка «Сейчас» — только при связи.

private val goalClasses = listOf("Любой размер", "Зелёная и крупнее", "Крупная (синяя) и трофейная", "Трофейная (оранжевая)")

@Composable
fun GoalScreen(vm: AppViewModel, wide: Boolean) {
    // пока вкладка на экране — подсказка «Сейчас» обновляется сама
    LaunchedEffect(vm.goalFish, vm.goalCls, vm.goalLoc) { while (true) { vm.loadGoal(); delay(20000) } }
    if (vm.goalPick) { GoalFishPicker(vm); return }

    val g = vm.goal
    val none = g == null || g.optBoolean("none")
    val fish = if (vm.goalFish > 0) vm.goalFish else g?.optInt("fish") ?: 0
    val cls = if (vm.goalFish > 0) vm.goalCls else g?.optInt("cls", 2) ?: 2
    val loc = if (vm.goalFish > 0) vm.goalLoc else if (g == null || g.isNull("loc")) 0 else g.optInt("loc")
    val online = vm.conn == ConnState.Connected && vm.goalFresh
    var clsOpen by remember { mutableStateOf(false) }
    var locOpen by remember { mutableStateOf(false) }

    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(horizontal = 12.dp, vertical = 4.dp)) {
        // ---- выбор цели: рыба, размер, водоём
        val fishName = g?.optString("fishName")?.takeIf { it.isNotEmpty() && fish > 0 }
            ?: vm.advisor?.optJSONArray("species").objs().firstOrNull { it.optInt("id") == fish }?.optString("name")
        OutlinedButton({ vm.goalPick = true }, Modifier.fillMaxWidth().heightIn(min = 52.dp)) {
            val ic = if (fish > 0) IconCache.get(vm.icons, "fish", fish.toString()) else null
            if (ic != null) { Image(ic, null, Modifier.size(30.dp)); Spacer(Modifier.width(8.dp)) }
            Text(if (fishName != null) "Хочу поймать: $fishName" else "Выбрать рыбу", fontSize = 16.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
        if (fish > 0) {
            Row(Modifier.fillMaxWidth().padding(top = 6.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Box(Modifier.weight(1f)) {
                    OutlinedButton({ clsOpen = true }, Modifier.fillMaxWidth().heightIn(min = 46.dp), contentPadding = PaddingValues(horizontal = 8.dp)) {
                        Text(goalClasses[cls.coerceIn(0, 3)], fontSize = 13.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    }
                    DropdownMenu(clsOpen, { clsOpen = false }) {
                        goalClasses.forEachIndexed { i, t -> DropdownMenuItem({ Text(t) }, { clsOpen = false; vm.chooseGoal(fish, i, loc) }) }
                    }
                }
                Box(Modifier.weight(1f)) {
                    val locName = if (loc <= 0) "Любой водоём" else g?.optString("locName")?.takeIf { it.isNotEmpty() } ?: "водоём $loc"
                    OutlinedButton({ locOpen = true }, Modifier.fillMaxWidth().heightIn(min = 46.dp), contentPadding = PaddingValues(horizontal = 8.dp)) {
                        Text(locName, fontSize = 13.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    }
                    DropdownMenu(locOpen, { locOpen = false }) {
                        DropdownMenuItem({ Text("Любой водоём, где водится") }, { locOpen = false; vm.chooseGoal(fish, cls, 0) })
                        g?.optJSONArray("waters").objs().forEach { w ->
                            DropdownMenuItem({ Text(w.optString("name")) }, { locOpen = false; vm.chooseGoal(fish, cls, w.optInt("id")) })
                        }
                    }
                }
            }
        }

        if (none) {
            Spacer(Modifier.height(18.dp))
            if (g == null) Muted(if (vm.conn == ConnState.Connected) (vm.goalError ?: "Загрузка…")
                                 else if (fish > 0) "Для этой цели на телефоне ещё нет сохранённого плана. Подключитесь к ПК — план загрузится и будет виден без связи."
                                 else "Подключитесь к ПК — план сохранится на телефоне и будет виден без связи.", 16)
            else {
                Text("Выберите рыбу", fontSize = 22.sp, fontWeight = FontWeight.SemiBold)
                Muted("Нажмите кнопку выше — программа скажет, где её ловить, на что и чем прикормить.", 15)
            }
            return@Column
        }
        g!!

        // ---- ответ одной строкой
        Spacer(Modifier.height(12.dp))
        Text(g.optString("answer"), fontSize = 21.sp, fontWeight = FontWeight.SemiBold)
        Text(g.optString("answerSub"), fontSize = 13.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
        g.optString("note").takeIf { it.isNotEmpty() }?.let { Spacer(Modifier.height(4.dp)); Text(it, fontSize = 13.sp, color = Pal.Warn) }

        fun openMap(l: Int, x: Double?, z: Double?) {
            if (x != null && z != null) vm.mapFocusReq = Triple(l, x, z)
            vm.loadMap(l); vm.screen = Screen.Maps
        }

        // ---- «Сейчас»
        SectionHead("Сейчас", "подсказка во время рыбалки")
        val live = g.optJSONObject("live")
        if (!online || live == null) {
            Pill("Нет связи с ПК — подсказка появится при подключении", MaterialTheme.colorScheme.outline)
            Spacer(Modifier.height(4.dp))
            Muted("План ниже сохранён на телефоне: " + g.optString("generated").take(16), 12)
        } else {
            val tone = when (live.optString("tone")) { "ok" -> Pal.Mine; "move" -> Pal.Danger; "bait", "warn" -> Pal.Warn; else -> MaterialTheme.colorScheme.outline }
            Pill(live.optString("headline"), tone)
            live.optString("detail").takeIf { it.isNotEmpty() }?.let { Spacer(Modifier.height(6.dp)); Text(it, fontSize = 14.sp) }
            if (!live.isNull("moveLoc") && !live.isNull("moveX") && !live.isNull("moveZ")) {
                Spacer(Modifier.height(6.dp))
                OutlinedButton({ openMap(live.optInt("moveLoc"), live.optDouble("moveX"), live.optDouble("moveZ")) }, Modifier.fillMaxWidth().heightIn(min = 46.dp)) {
                    Text("Показать новое место на карте")
                }
            }
        }

        // ---- 5 шагов и справочник
        g.optJSONArray("sections").objs().forEach { s ->
            val rows = s.optJSONArray("rows").objs()
            val key = s.optString("key")
            if (key == "others" && rows.isEmpty() && s.optString("note").isEmpty()) return@forEach
            SectionHead(s.optString("title"), s.optString("hint"))
            if (rows.isEmpty()) Muted(s.optString("empty"), 13)
            else ListBlock {
                rows.forEach { r ->
                    val muted = r.optBoolean("muted")
                    val accent = if (r.optString("kind") == "ref") Pal.Warn else if (muted) Pal.Coverage else Pal.Mine
                    StatLine(r.optString("title"), r.optString("sub"), r.optString("value"), if (r.isNull("frac")) null else r.optDouble("frac"), accent, muted,
                        icon = if (key == "bait") IconCache.get(vm.icons, "bait", r.opt("baitTid")?.toString()) else null,
                        onClick = if (!r.isNull("locationId")) ({
                            // FIX-05: «Что известно от других игроков» — водоём с их точками поверх карты (как в «Рыба»)
                            val pts = r.optJSONArray("pts").objs().filter { !it.isNull("x") && !it.isNull("z") }
                            vm.refHighlight = if (pts.isEmpty()) null
                                else AppViewModel.RefHi(r.optInt("locationId"), g.optString("fishName").ifEmpty { g.optString("answer") }, pts.map { Triple(it.optDouble("x"), it.optDouble("z"), it.optString("g")) })
                            openMap(r.optInt("locationId"), if (r.isNull("x")) null else r.optDouble("x"), if (r.isNull("z")) null else r.optDouble("z"))
                        }) else null, titleLines = if (key == "ground" || key == "tackle") 3 else 1)
                }
            }
            s.optString("note").takeIf { it.isNotEmpty() }?.let { Spacer(Modifier.height(4.dp)); Muted(it, 12) }
        }
        Spacer(Modifier.height(12.dp))
        Muted(g.optString("foot"), 12)
        Spacer(Modifier.height(20.dp))
    }
}

/** Выбор рыбы для цели: поиск (в т. ч. латиницей) по тем же разделам, что вкладка «Рыба». */
@Composable
private fun GoalFishPicker(vm: AppViewModel) {
    val all = vm.advisor?.optJSONArray("species").objs()
    val q = vm.goalQuery.trim()
    val shown = if (q.isEmpty()) all else all.filter { nameMatches(it.optString("name"), q) }
    val groups = shown.groupBy { it.optString("section") }
    val focus = androidx.compose.ui.platform.LocalFocusManager.current
    val cls = if (vm.goalFish > 0) vm.goalCls else vm.goal?.optInt("cls", 2) ?: 2
    Column(Modifier.fillMaxSize()) {
        TextButton({ vm.goalPick = false; vm.goalQuery = "" }) { Icon(Icons.AutoMirrored.Filled.ArrowBack, null); Spacer(Modifier.width(6.dp)); Text("Назад", fontSize = 15.sp) }
        OutlinedTextField(vm.goalQuery, { vm.goalQuery = it }, Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp),
            placeholder = { Text("Какую рыбу хотите поймать?") }, singleLine = true, leadingIcon = { Icon(Icons.Filled.Search, null) },
            trailingIcon = { if (vm.goalQuery.isNotEmpty()) IconButton({ vm.goalQuery = "" }) { Icon(Icons.Filled.Clear, "Очистить") } },
            keyboardOptions = KeyboardOptions(imeAction = ImeAction.Search),
            keyboardActions = androidx.compose.foundation.text.KeyboardActions(onSearch = { focus.clearFocus() }))
        LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(start = 12.dp, end = 12.dp, bottom = 16.dp)) {
            if (all.isEmpty()) item { Muted("Подключитесь к ПК хотя бы один раз — список рыбы сохранится на телефоне.", 15) }
            else if (shown.isEmpty()) item { Muted("Ничего не найдено", 15) }
            if (q.isEmpty() && all.isNotEmpty()) item(key = "pc") {
                Row(Modifier.fillMaxWidth().clip(RoundedCornerShape(10.dp)).clickable { focus.clearFocus(); vm.chooseGoal(0, 2, 0) }
                    .heightIn(min = 50.dp).padding(horizontal = 8.dp, vertical = 6.dp), verticalAlignment = Alignment.CenterVertically) {
                    Column {
                        Text("Та же цель, что на ПК", fontSize = 16.sp, fontWeight = FontWeight.SemiBold, color = MaterialTheme.colorScheme.primary)
                        Text("выбрана во вкладке «Цель» Советника в Windows", fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
            }
            for (sec in listOf("Ловили вы", "Ловят другие игроки", "Справочник")) {
                val list = groups[sec] ?: continue
                item(key = "h:$sec") {
                    Text(sec.uppercase() + "  ·  " + list.size, fontSize = 12.sp, fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.primary,
                        modifier = Modifier.padding(top = 12.dp, bottom = 4.dp))
                }
                items(list, key = { "g:" + it.optInt("id") }) { s ->
                    val id = s.optInt("id")
                    val sel = vm.goalFish == id
                    val sub = if (s.optBoolean("refOnly")) "только справочник" else if (s.optInt("own") > 0) "мои ${s.optInt("own")} · у других ${s.optInt("pub")}" else "у других ${s.optInt("pub")}"
                    Row(Modifier.fillMaxWidth().clip(RoundedCornerShape(10.dp))
                        .background(if (sel) MaterialTheme.colorScheme.secondaryContainer else Color.Transparent)
                        .clickable { focus.clearFocus(); vm.chooseGoal(id, cls, 0) }.heightIn(min = 54.dp).padding(horizontal = 8.dp, vertical = 6.dp),
                        verticalAlignment = Alignment.CenterVertically) {
                        val ic = IconCache.get(vm.icons, "fish", id.toString())
                        if (ic != null) Image(ic, null, Modifier.size(36.dp)) else Spacer(Modifier.size(36.dp))
                        Spacer(Modifier.width(10.dp))
                        Column(Modifier.weight(1f)) {
                            Text(s.optString("name"), fontSize = 16.sp, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            Text(sub, fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                    }
                }
            }
        }
    }
}
