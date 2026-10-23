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
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.moontechnologiesco.mooncam.R
import io.github.moontechnologiesco.mooncam.StreamingState

@Composable
fun HomeScreen(
    state: StreamingState,
    hasCameraPermission: Boolean,
    onRequestPermission: () -> Unit,
    onStop: () -> Unit,
) {
    Box(
        Modifier
            .fillMaxSize()
            .background(Brush.verticalGradient(listOf(MoonColors.Night, MoonColors.DeepViolet)))
            .safeDrawingPadding()
            .padding(24.dp),
    ) {
        Column(Modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(20.dp)) {
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

            if (!hasCameraPermission) {
                GlassCard {
                    Text(stringResource(R.string.permission_needed), color = MoonColors.Mist)
                    Spacer(Modifier.height(12.dp))
                    Button(onClick = onRequestPermission) { Text(stringResource(R.string.grant_permission)) }
                }
            }

            when (state) {
                StreamingState.Idle, is StreamingState.Rejected -> GlassCard {
                    Text(stringResource(R.string.how_to_title), fontWeight = FontWeight.SemiBold)
                    Spacer(Modifier.height(8.dp))
                    Text(stringResource(R.string.how_to_body), color = MoonColors.Mist, lineHeight = 22.sp)
                }
                else -> Button(onClick = onStop, modifier = Modifier.fillMaxWidth()) { Text(stringResource(R.string.stop)) }
            }
        }

        Text(
            stringResource(R.string.privacy_note),
            fontFamily = MoonMono,
            fontSize = 12.sp,
            color = MoonColors.Mist,
            modifier = Modifier.align(Alignment.BottomCenter),
        )
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
private fun statusText(state: StreamingState): String = when (state) {
    StreamingState.Idle -> stringResource(R.string.state_idle)
    StreamingState.Connecting -> stringResource(R.string.state_connecting)
    is StreamingState.Connected -> stringResource(R.string.state_connected, state.computer)
    is StreamingState.Streaming -> stringResource(R.string.state_streaming, state.computer)
    StreamingState.Reconnecting -> stringResource(R.string.state_reconnecting)
    StreamingState.CameraBusy -> stringResource(R.string.state_camera_busy)
    is StreamingState.Rejected -> stringResource(R.string.state_rejected, state.reason)
}

