package ru.fisherlogger.companion.ui

import android.Manifest
import android.content.pm.PackageManager
import android.os.Build
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.material3.Switch
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.ContextCompat
import ru.fisherlogger.companion.AppViewModel

/** FEAT-06: вкладка «ПК» — включить уведомления о поклёвках (работают и при свёрнутом приложении). */
@Composable
fun NotifyCard(vm: AppViewModel) {
    val ctx = LocalContext.current
    val ask = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { ok -> if (ok) vm.setBiteAlerts(true) }
    fun turnOn() {
        if (Build.VERSION.SDK_INT >= 33 && ContextCompat.checkSelfPermission(ctx, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED)
            ask.launch(Manifest.permission.POST_NOTIFICATIONS)
        else vm.setBiteAlerts(true)
    }
    AppCard {
        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp)) {
            Text("Уведомлять о поклёвках", fontWeight = FontWeight.SemiBold, fontSize = 16.sp, modifier = Modifier.weight(1f))
            Switch(checked = vm.biteAlerts, onCheckedChange = { if (it) turnOn() else vm.setBiteAlerts(false) })
        }
        Muted("Номер удочки, рыба с картинкой, вес и класс — даже когда приложение свёрнуто. Телефон и ПК в одной Wi-Fi сети.", 13)
        if (vm.biteAlerts) {
            Spacer(Modifier.height(6.dp))
            Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp)) {
                Text("Также о пустых поклёвках", fontSize = 15.sp, modifier = Modifier.weight(1f))
                Switch(checked = vm.emptyAlerts, onCheckedChange = { vm.setBiteAlerts(true, it) })
            }
        }
    }
}
