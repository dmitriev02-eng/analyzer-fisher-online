package ru.fisherlogger.companion.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Lightbulb
import androidx.compose.material.icons.filled.Home
import androidx.compose.material.icons.automirrored.filled.List
import androidx.compose.material.icons.filled.Map
import androidx.compose.material.icons.filled.Wifi
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.NavigationRail
import androidx.compose.material3.NavigationRailItem
import androidx.compose.material3.Surface
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import ru.fisherlogger.companion.AppViewModel
import ru.fisherlogger.companion.ConnState
import ru.fisherlogger.companion.Screen

object Pal {
    // DESIGN-01: палитра «ночная вода / эхолот», как в Windows (Theme.cs)
    val Mine = Color(0xFF7FCB8F)     // мои уловы — AccentGreen
    val Public = Color(0xFF5AA9E6)   // другие игроки — Theme.Others
    val Coverage = Color(0xFF2FB5A8) // AccentTeal
    val Warn = Color(0xFFEDB84E)
    val Danger = Color(0xFFE2606B)
}

private val Dark = darkColorScheme(
    primary = Color(0xFF4CC9BF), onPrimary = Color(0xFF06201D),
    secondary = Color(0xFF2FB5A8), background = Color(0xFF0B1416), onBackground = Color(0xFFE5EDEB),
    surface = Color(0xFF111E21), onSurface = Color(0xFFE5EDEB), surfaceVariant = Color(0xFF172629),
    onSurfaceVariant = Color(0xFF8EA4A1), outline = Color(0xFF243A3E), secondaryContainer = Color(0xFF19514F),
    onSecondaryContainer = Color(0xFFE5EDEB), surfaceContainer = Color(0xFF111E21)
)
private val Light = lightColorScheme(
    primary = Color(0xFF13756E), secondary = Color(0xFF1F8F85), background = Color(0xFFF2F6F5),
    surface = Color.White, surfaceVariant = Color(0xFFE3ECEA), onSurfaceVariant = Color(0xFF4B5F5C), outline = Color(0xFFB5C6C3)
)

@Composable
fun FisherApp(vm: AppViewModel) {
    MaterialTheme(colorScheme = if (vm.darkTheme) Dark else Light) {
        Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
            if (!L.chosen) LanguageChooser(androidx.compose.ui.platform.LocalContext.current)   // RELEASE-03
            else BoxWithConstraints(Modifier.fillMaxSize()) {
                val wide = maxWidth >= 600.dp
                val items = listOf(
                    // DESIGN-03: как в окне ПК — «Сейчас / Куда идти / Журнал», плюс карта и связь с ПК
                    Triple(Screen.Dashboard, "Сейчас", Icons.Filled.Home),
                    Triple(Screen.Analytics, "Куда идти", Icons.Filled.Lightbulb),
                    Triple(Screen.Maps, "Карта", Icons.Filled.Map),
                    Triple(Screen.History, "Журнал", Icons.AutoMirrored.Filled.List),
                    Triple(Screen.Connection, "ПК", Icons.Filled.Wifi)
                )
                if (wide) {
                    Row(Modifier.fillMaxSize().windowInsetsPadding(WindowInsets.safeDrawing)) {
                        NavigationRail(containerColor = MaterialTheme.colorScheme.surface) {
                            Spacer(Modifier.padding(top = 8.dp))
                            ConnDot(vm.conn)
                            items.forEach { (s, label, icon) ->
                                NavigationRailItem(selected = vm.screen == s, onClick = { vm.miniMap = false; vm.screen = s },
                                    icon = { Icon(icon, label) }, label = { Text(label, fontSize = 12.sp) })
                            }
                        }
                        Box(Modifier.weight(1f).fillMaxSize()) { Content(vm, wide = true) }
                    }
                } else {
                    Column(Modifier.fillMaxSize().windowInsetsPadding(WindowInsets.safeDrawing)) {
                        StatusStrip(vm)
                        Box(Modifier.weight(1f).fillMaxWidth()) { Content(vm, wide = false) }
                        NavigationBar(containerColor = MaterialTheme.colorScheme.surface) {
                            items.forEach { (s, label, icon) ->
                                NavigationBarItem(selected = vm.screen == s, onClick = { vm.miniMap = false; vm.screen = s },
                                    icon = { Icon(icon, label) }, label = { Text(label, fontSize = 11.sp, maxLines = 1) })
                            }
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun Content(vm: AppViewModel, wide: Boolean) {
    if (vm.miniMap) { MiniMapScreen(vm); return }   // FEAT-07: мини-карта на весь экран поверх «Сейчас»
    when (vm.screen) {
        Screen.Dashboard -> DashboardScreen(vm, wide)
        Screen.Analytics -> AdvisorScreen(vm, wide)
        Screen.Maps -> MapsScreen(vm, wide)
        Screen.History -> HistoryScreen(vm, wide)
        Screen.Connection -> ConnectionScreen(vm)
    }
}

fun savedText(t: Long): String = java.text.SimpleDateFormat("dd.MM HH:mm", java.util.Locale("ru")).format(java.util.Date(t))

fun connText(s: ConnState): String = when (s) {
    ConnState.NotConfigured -> "Не настроено"
    ConnState.NotPaired -> "Не сопряжено"
    ConnState.Searching -> "Поиск ПК…"
    ConnState.Connecting -> "Подключение…"
    ConnState.Connected -> "Подключено"
    ConnState.Reconnecting -> "Связь потеряна, переподключение…"
    ConnState.Offline -> "ПК недоступен"
    ConnState.AuthFailed -> "Доступ отклонён"
}

fun connColor(s: ConnState): Color = when (s) {
    ConnState.Connected -> Pal.Mine
    ConnState.Connecting, ConnState.Reconnecting, ConnState.Searching -> Pal.Warn
    ConnState.NotConfigured, ConnState.NotPaired -> Color(0xFF879BB2)
    else -> Pal.Danger
}

@Composable
fun ConnDot(s: ConnState) {
    Box(Modifier.padding(8.dp).size(12.dp).background(connColor(s), CircleShape))
}

@Composable
private fun StatusStrip(vm: AppViewModel) {
    Row(
        Modifier.fillMaxWidth().background(MaterialTheme.colorScheme.surface).padding(horizontal = 12.dp, vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)
    ) {
        Box(Modifier.size(10.dp).background(connColor(vm.conn), CircleShape))
        Text(connText(vm.conn), fontWeight = FontWeight.SemiBold, fontSize = 13.sp)
        // ANDROID-UX-01: без связи — показаны данные, сохранённые на телефоне
        if (vm.conn != ConnState.Connected && vm.cacheSavedAt > 0) {
            Text("· данные от " + savedText(vm.cacheSavedAt), fontSize = 13.sp, color = Pal.Warn, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
        val st = vm.status
        if (st != null && vm.conn == ConnState.Connected) {
            Spacer(Modifier.width(4.dp))
            // DESIGN-03: точные часы игры с ПК (игровой час = МСК×12); старый ПК — оценка «≈»
            val clock = st.optJSONObject("coach")?.str("clock")?.substringBefore(",")?.removePrefix("В игре ")
            Text(if (clock != null) "в игре $clock" else "в игре ≈" + st.optString("gameTime"),
                fontSize = 13.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
    }
}
