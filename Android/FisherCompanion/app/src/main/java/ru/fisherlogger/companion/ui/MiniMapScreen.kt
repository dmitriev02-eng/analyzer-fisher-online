package ru.fisherlogger.companion.ui

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Map
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Tab
import androidx.compose.material3.TabRow
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import ru.fisherlogger.companion.AppViewModel
import ru.fisherlogger.companion.ConnState

/** FEAT-07: кнопка в шапке «Сейчас» — открывает мини-карту на весь экран. */
@Composable
fun MiniMapButton(vm: AppViewModel) {
    OutlinedButton(onClick = { vm.miniMap = true }, modifier = Modifier.heightIn(min = 44.dp),
        contentPadding = PaddingValues(horizontal = 12.dp, vertical = 4.dp)) {
        Icon(Icons.Filled.Map, null, Modifier.size(18.dp)); Spacer(Modifier.size(6.dp)); Text("Мини-карта", fontSize = 14.sp)
    }
}

/**
 * FEAT-07: мини-карта на весь экран — как окно в игре на ПК: круг с вами, забросами и зонами клёва
 * (подпись — % от обычного для этой снасти), ниже вкладки «Мои уловы / Лента / Советы».
 */
@OptIn(ExperimentalLayoutApi::class, androidx.compose.foundation.ExperimentalFoundationApi::class)
@Composable
fun MiniMapScreen(vm: AppViewModel) {
    BackHandler { vm.miniMap = false }
    val live = vm.live
    val loc = live?.optInt("locationId", -1) ?: -1
    val player = live?.optJSONObject("player")
    val casts = (0 until (live?.optJSONArray("casts")?.length() ?: 0)).mapNotNull { live?.optJSONArray("casts")?.optJSONObject(it) }
    val spots = liveSpots(live)
    val meta = (0 until (vm.maps?.length() ?: 0)).mapNotNull { vm.maps?.optJSONObject(it) }.firstOrNull { it.optInt("locationId", -2) == loc }
    LaunchedEffect(loc) { if (loc >= 0 && meta != null) vm.loadMap(loc) }
    var radius by rememberSaveable { mutableIntStateOf(50) }
    val coach = vm.status?.optJSONObject("coach")
    Column(Modifier.fillMaxSize()) {
        Row(Modifier.fillMaxWidth().background(MaterialTheme.colorScheme.surface).padding(end = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = { vm.miniMap = false }) { Icon(Icons.AutoMirrored.Filled.ArrowBack, L.t("Назад")) }
            Column(Modifier.weight(1f)) {
                Text("Мини-карта", fontWeight = FontWeight.SemiBold, fontSize = 17.sp)
                val wname = live?.optString("locationName").orEmpty()
                if (wname.isNotEmpty()) Muted(wname, 12)
            }
            listOf(50, 100, 200).forEach { r ->
                FilterChip(selected = radius == r, onClick = { radius = r }, label = { Text("$r м") }, modifier = Modifier.padding(start = 4.dp).heightIn(min = 40.dp))
            }
        }
        // FEAT-07b: весь экран прокручивается одним списком — карта, легенда, вкладки (прилипают к верху), содержимое вкладки.
        // Раньше список вкладки получал остаток экрана под квадратной картой и на телефоне не помещался.
        val cfg = androidx.compose.ui.platform.LocalConfiguration.current
        val mapMax = (cfg.screenHeightDp * 0.55f).dp
        LazyColumn(Modifier.weight(1f).fillMaxWidth(), contentPadding = PaddingValues(bottom = 12.dp)) {
            item {
                if (vm.conn != ConnState.Connected) {
                    Box(Modifier.fillMaxWidth().padding(16.dp)) { Muted("Нет связи с ПК — мини-карта обновится, когда связь вернётся.", 15) }
                } else if (player == null && casts.isEmpty() && spots.isEmpty()) {
                    Box(Modifier.fillMaxWidth().padding(16.dp)) {
                        Muted(if (live == null) "Загружаю данные с ПК…" else "Пока пусто: вы и забросы появятся здесь после входа на водоём и первого заброса.", 15)
                    }
                } else {
                    val age = player?.optInt("ageSec", 9999) ?: 9999
                    Box(Modifier.fillMaxWidth(), contentAlignment = Alignment.Center) {
                        LiveMapCanvas(vm, meta, loc, player, casts, spots, radius, age <= 25,
                            Modifier.widthIn(max = mapMax).fillMaxWidth().aspectRatio(1f).padding(horizontal = 8.dp, vertical = 6.dp))
                    }
                }
            }
            item {
                FlowRow(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 2.dp), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    listOf("вы" to LiveCol.Player, "в воде" to LiveCol.Wait, "поклёвка" to LiveCol.Bite, "подсечка" to LiveCol.Hook, "улов" to LiveCol.Catch).forEach { (t, c) ->
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Box(Modifier.size(9.dp).background(c, CircleShape)); Spacer(Modifier.size(4.dp)); Text(t, fontSize = 12.sp)
                        }
                    }
                    if (spots.isNotEmpty()) Row(verticalAlignment = Alignment.CenterVertically) {
                        Box(Modifier.size(9.dp).background(LiveCol.spot(2), CircleShape)); Spacer(Modifier.size(4.dp)); Text("зона: % от обычного", fontSize = 12.sp)
                    }
                }
            }
            stickyHeader {
                TabRow(selectedTabIndex = vm.miniTab, containerColor = MaterialTheme.colorScheme.surface, modifier = Modifier.padding(top = 6.dp)) {
                    listOf("Мои уловы", "Лента", "Советы").forEachIndexed { i, t ->
                        Tab(vm.miniTab == i, { vm.miniTab = i }, modifier = Modifier.heightIn(min = 48.dp), text = { Text(t, fontSize = 14.sp, maxLines = 1) })
                    }
                }
            }
            val pad = Modifier.padding(horizontal = 12.dp, vertical = 4.dp)
            when (vm.miniTab) {
                0 -> {
                    val l = vm.catches.objs().take(50)
                    if (l.isEmpty()) item { Box(pad) { Muted("Уловов пока нет", 15) } } else items(l, key = { "c" + it.optString("key") }) { Box(pad) { CatchCard(it, vm.tech) } }
                }
                1 -> {
                    val l = vm.publicFeed.objs().take(50)
                    if (l.isEmpty()) item { Box(pad) { Muted("Ленты пока нет: уловы других игроков появятся при игре с включённым захватом на ПК.", 15) } }
                    else items(l, key = { "p" + it.optString("key") }) { Box(pad) { PublicCard(it, vm.tech) } }
                }
                else -> {
                    if (coach == null || !coach.optBoolean("fishing")) item { Box(pad) { Muted("Сейчас вы не рыбачите. Советы появятся после заброса.", 15) } }
                    else {
                        items(coach.optJSONArray("rods").objs(), key = { "r" + it.optInt("slot") }) { r ->
                            Box(pad) {
                                AppCard {
                                    Row(verticalAlignment = Alignment.CenterVertically) {
                                        Box(Modifier.size(12.dp).background(rodColor(r.optInt("level", -1)), CircleShape)); Spacer(Modifier.size(8.dp))
                                        Text("Удочка " + r.optInt("slot") + " · " + r.optString("rod"), fontWeight = FontWeight.SemiBold, fontSize = 15.sp, modifier = Modifier.weight(1f))
                                    }
                                    if (r.has("bites")) Muted("поклёвок " + r.optInt("bites") + ", пустых " + r.optInt("empty"), 12)
                                    Text(r.optString("status"), fontSize = 14.sp)
                                    EmptyBitesList(r)
                                }
                            }
                        }
                        val hints = coach.optJSONArray("hints")
                        if (hints != null) items((0 until hints.length()).map { hints.optString(it) }) { h -> Box(pad) { Text("• $h", fontSize = 13.sp, color = Pal.Warn) } }
                    }
                }
            }
        }
    }
}
