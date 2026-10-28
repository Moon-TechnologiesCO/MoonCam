package io.github.moontechnologiesco.mooncam.ui

import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.moontechnologiesco.mooncam.R
import io.github.moontechnologiesco.mooncam.StreamingState
import io.github.moontechnologiesco.mooncam.link.ComputerEntry
import io.github.moontechnologiesco.mooncam.protocol.Pairing

@Composable
fun HomeScreen(
    state: StreamingState,
    hasCameraPermission: Boolean,
    hasNetworkPermission: Boolean,
    computers: List<ComputerEntry>,
    onRequestPermission: () -> Unit,
    onConnect: (ComputerEntry) -> Unit,
    onUnpair: (ComputerEntry) -> Unit,
    linkedOverWifi: Boolean,
    onUnpairLinked: () -> Unit,
    onConfirmPairing: () -> Unit,
    onStop: () -> Unit,
) {
    Box(
        Modifier
            .fillMaxSize()
            .background(Brush.verticalGradient(listOf(MoonColors.Night, MoonColors.DeepViolet)))
            .safeDrawingPadding(),
    ) {
        Column(
            Modifier
                .fillMaxWidth()
                .verticalScroll(rememberScrollState())
                .padding(24.dp),
            verticalArrangement = Arrangement.spacedBy(20.dp),
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Image(painterResource(R.drawable.ic_launcher_foreground), contentDescription = null, modifier = Modifier.size(72.dp))
                Column {
                    Text(stringResource(R.string.app_name), fontSize = 28.sp, fontWeight = FontWeight.SemiBold)
                    Text(stringResource(R.string.tagline), color = MoonColors.Mist, fontSize = 14.sp)
                }
            }

            GlassCard {
                Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    val live = state is StreamingState.Streaming
                    Box(Modifier.size(10.dp).background(if (live) MoonColors.Live else MoonColors.Mist, CircleShape))
                    Text(statusText(state), fontWeight = FontWeight.Medium, fontSize = 17.sp)
                }
            }

            if (!hasCameraPermission || !hasNetworkPermission) {
                GlassCard {
                    if (!hasCameraPermission) Text(stringResource(R.string.permission_needed), color = MoonColors.Mist)
                    if (!hasNetworkPermission) Text(stringResource(R.string.network_permission_needed), color = MoonColors.Mist)
                    Spacer(Modifier.height(12.dp))
                    Button(onClick = onRequestPermission) { Text(stringResource(R.string.grant_permission)) }
                }
            }

            when {
                state is StreamingState.Pairing -> PairingCard(state, onConfirmPairing, onStop)
                state.isActive -> Column(Modifier.fillMaxWidth()) {
                    Button(onClick = onStop, modifier = Modifier.fillMaxWidth()) { Text(stringResource(R.string.stop)) }
                    if (linkedOverWifi) {
                        TextButton(onClick = onUnpairLinked, modifier = Modifier.fillMaxWidth()) { Text(stringResource(R.string.unpair_linked)) }
                    }
                }
                else -> {
                    if (hasNetworkPermission) ComputersCard(computers, onConnect, onUnpair)
                    GlassCard {
                        Text(stringResource(R.string.how_to_title), fontWeight = FontWeight.SemiBold)
                        Spacer(Modifier.height(8.dp))
                        Text(stringResource(R.string.how_to_body), color = MoonColors.Mist, lineHeight = 22.sp)
                    }
                }
            }

            Text(
                stringResource(R.string.privacy_note),
                fontFamily = MoonMono,
                fontSize = 12.sp,
                color = MoonColors.Mist,
                textAlign = TextAlign.Center,
                modifier = Modifier.fillMaxWidth().padding(top = 8.dp),
            )
        }
    }
}

/** The same six digits must be on both screens; only then does each person confirm on their side. */
@Composable
private fun PairingCard(state: StreamingState.Pairing, onConfirm: () -> Unit, onCancel: () -> Unit) {
    GlassCard {
        Text(stringResource(R.string.pairing_title), fontWeight = FontWeight.SemiBold, fontSize = 17.sp)
        Text(state.computer, color = MoonColors.Mist)
        Text(
            Pairing.format(state.code),
            fontFamily = MoonMono,
            fontSize = 48.sp,
            color = MoonColors.Glow,
            textAlign = TextAlign.Center,
            modifier = Modifier.fillMaxWidth().padding(vertical = 16.dp),
        )
        if (state.confirmed) {
            Text(stringResource(R.string.pairing_waiting), color = MoonColors.Mist)
        } else {
            Text(stringResource(R.string.pairing_body), color = MoonColors.Mist, lineHeight = 22.sp)
            Spacer(Modifier.height(16.dp))
            Button(onClick = onConfirm, modifier = Modifier.fillMaxWidth()) { Text(stringResource(R.string.pairing_match)) }
        }
        TextButton(onClick = onCancel, modifier = Modifier.fillMaxWidth()) { Text(stringResource(R.string.cancel)) }
    }
}

@Composable
private fun ComputersCard(computers: List<ComputerEntry>, onConnect: (ComputerEntry) -> Unit, onUnpair: (ComputerEntry) -> Unit) {
    GlassCard {
        Text(stringResource(R.string.wifi_title), fontWeight = FontWeight.SemiBold)
        Spacer(Modifier.height(8.dp))
        if (computers.isEmpty()) {
            Text(stringResource(R.string.wifi_searching), color = MoonColors.Mist, lineHeight = 22.sp)
        }
        computers.forEach { entry ->
            Row(Modifier.fillMaxWidth().padding(vertical = 6.dp), verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text(entry.name, fontWeight = FontWeight.Medium)
                    Text(computerDetail(entry), color = MoonColors.Mist, fontSize = 13.sp)
                }
                if (entry.paired != null) {
                    TextButton(onClick = { onUnpair(entry) }) { Text(stringResource(R.string.forget)) }
                    Button(onClick = { onConnect(entry) }) { Text(stringResource(R.string.connect)) }
                } else {
                    OutlinedButton(onClick = { onConnect(entry) }) { Text(stringResource(R.string.pair)) }
                }
            }
        }
        Spacer(Modifier.height(8.dp))
        Text(stringResource(R.string.wifi_pair_hint), color = MoonColors.Mist, fontSize = 13.sp, lineHeight = 19.sp)
    }
}

/** Asked every time the desktop requests a USB connection: nothing streams without the user's yes. */
@Composable
fun ConsentDialog(onAllow: () -> Unit, onDeny: () -> Unit) {
    AlertDialog(
        onDismissRequest = onDeny,
        title = { Text(stringResource(R.string.consent_title)) },
        text = { Text(stringResource(R.string.consent_body)) },
        confirmButton = { Button(onClick = onAllow) { Text(stringResource(R.string.consent_allow)) } },
        dismissButton = { TextButton(onClick = onDeny) { Text(stringResource(R.string.consent_deny)) } },
    )
}

@Composable
private fun GlassCard(content: @Composable () -> Unit) {
    val shape = RoundedCornerShape(20.dp)
    Column(
        Modifier
            .fillMaxWidth()
            .background(MoonColors.Glass, shape)
            .border(1.dp, MoonColors.GlassBorder, shape)
            .padding(18.dp),
    ) { content() }
}

@Composable
private fun computerDetail(entry: ComputerEntry): String = when {
    entry.paired == null -> stringResource(R.string.computer_unpaired)
    entry.nearby != null -> stringResource(R.string.computer_paired_nearby)
    else -> stringResource(R.string.computer_paired_away)
}

@Composable
private fun statusText(state: StreamingState): String = when (state) {
    StreamingState.Idle -> stringResource(R.string.state_idle)
    StreamingState.Connecting -> stringResource(R.string.state_connecting)
    is StreamingState.Connected -> stringResource(R.string.state_connected, state.computer)
    is StreamingState.Streaming -> stringResource(R.string.state_streaming, state.computer)
    StreamingState.Reconnecting -> stringResource(R.string.state_reconnecting)
    StreamingState.CameraBusy -> stringResource(R.string.state_camera_busy)
    is StreamingState.Rejected -> stringResource(R.string.state_rejected, state.reason)
    is StreamingState.Pairing -> stringResource(R.string.state_pairing, state.computer)
    is StreamingState.Failed -> state.message
    is StreamingState.Disconnected ->
        stringResource(if (state.unpaired) R.string.state_unpaired else R.string.state_disconnected, state.computer)
}
