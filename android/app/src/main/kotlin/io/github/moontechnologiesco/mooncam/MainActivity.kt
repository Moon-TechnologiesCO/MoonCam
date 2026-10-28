package io.github.moontechnologiesco.mooncam

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import io.github.moontechnologiesco.mooncam.link.ComputerEntry
import io.github.moontechnologiesco.mooncam.link.LinkTarget
import io.github.moontechnologiesco.mooncam.link.NearbyComputer
import io.github.moontechnologiesco.mooncam.link.NearbyComputers
import io.github.moontechnologiesco.mooncam.link.PairedComputers
import io.github.moontechnologiesco.mooncam.service.StreamingService
import io.github.moontechnologiesco.mooncam.ui.ConsentDialog
import io.github.moontechnologiesco.mooncam.ui.HomeScreen
import io.github.moontechnologiesco.mooncam.ui.MoonCamTheme
import kotlinx.coroutines.flow.flowOf

class MainActivity : ComponentActivity() {
    private val paired by lazy { PairedComputers.get(this) }
    private val nearby by lazy { NearbyComputers(this) }
    private var pendingRequest by mutableStateOf<ConnectRequest?>(null)
    private var afterGrant: LinkTarget? = null
    private var hasCameraPermission by mutableStateOf(false)
    private var hasNetworkPermission by mutableStateOf(false)

    /** Connect to a paired computer on its own once each time the app is opened, never again after Stop. */
    private var autoConnect by mutableStateOf(true)

    private val permissions = registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) {
        refreshPermissions()
        afterGrant?.takeIf { hasCameraPermission && (it is LinkTarget.Usb || hasNetworkPermission) }?.let(::startStreaming)
        afterGrant = null
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        refreshPermissions()
        autoConnect = savedInstanceState?.getBoolean(KEY_AUTO_CONNECT) ?: true
        pendingRequest = ConnectRequest.from(intent)
        setContent {
            MoonCamTheme {
                val state by StreamingStatus.state.collectAsStateWithLifecycle()
                val wifiComputer by StreamingStatus.wifiComputer.collectAsStateWithLifecycle()
                val pairedComputers by paired.all.collectAsStateWithLifecycle()
                // Discovery runs only while the app is on screen, and only once the network may be used.
                val discovered by remember(hasNetworkPermission) { if (hasNetworkPermission) nearby.computers else flowOf(emptyList<NearbyComputer>()) }
                    .collectAsStateWithLifecycle(emptyList())
                val computers = remember(discovered, pairedComputers) { ComputerEntry.merge(discovered, pairedComputers) }

                LaunchedEffect(computers) {
                    computers.forEach { entry ->
                        val seen = entry.nearby ?: return@forEach
                        entry.paired?.let { paired.seenAt(it.fingerprintHex, seen.host, seen.port) }
                    }
                }
                LaunchedEffect(computers, state, autoConnect, hasCameraPermission, pendingRequest) {
                    if (!autoConnect || state != StreamingState.Idle || !hasCameraPermission || pendingRequest != null) return@LaunchedEffect
                    computers.firstOrNull { it.paired != null && it.nearby != null }?.let {
                        autoConnect = false
                        startStreaming(it.target())
                    }
                }

                HomeScreen(
                    state = state,
                    hasCameraPermission = hasCameraPermission,
                    hasNetworkPermission = hasNetworkPermission,
                    computers = computers,
                    onRequestPermission = { requestPermissions() },
                    onConnect = { connect(it.target()) },
                    onUnpair = { entry -> entry.paired?.let { unpair(it.fingerprintHex) } },
                    linkedOverWifi = wifiComputer != null,
                    onUnpairLinked = { wifiComputer?.let(::unpair) },
                    onConfirmPairing = { StreamingService.confirmPairing(this) },
                    onStop = {
                        autoConnect = false
                        StreamingService.stop(this)
                    },
                )
                pendingRequest?.let { request ->
                    ConsentDialog(
                        onAllow = {
                            pendingRequest = null
                            connect(request.toTarget())
                        },
                        onDeny = { pendingRequest = null },
                    )
                }
            }
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        ConnectRequest.from(intent)?.let { pendingRequest = it }
    }

    override fun onRestart() {
        super.onRestart()
        autoConnect = true // the app was opened again (not just rotated)
    }

    override fun onResume() {
        super.onResume()
        refreshPermissions()
    }

    override fun onSaveInstanceState(outState: Bundle) {
        super.onSaveInstanceState(outState)
        outState.putBoolean(KEY_AUTO_CONNECT, autoConnect)
    }

    /** Forgets the computer here and, if linked to it now, tells it to forget this phone as well. */
    private fun unpair(fingerprintHex: String) {
        StreamingService.unpair(this, fingerprintHex)
        paired.remove(fingerprintHex)
    }

    private fun connect(target: LinkTarget) {
        val ready = hasCameraPermission && (target is LinkTarget.Usb || hasNetworkPermission)
        if (ready) startStreaming(target) else requestPermissions(target)
    }

    private fun startStreaming(target: LinkTarget) = StreamingService.start(this, target)

    private fun requestPermissions(then: LinkTarget? = null) {
        afterGrant = then
        val wanted = buildList {
            add(Manifest.permission.CAMERA)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) add(Manifest.permission.POST_NOTIFICATIONS)
            if (Build.VERSION.SDK_INT >= LOCAL_NETWORK_SDK) add(ACCESS_LOCAL_NETWORK)
        }
        permissions.launch(wanted.toTypedArray())
    }

    private fun refreshPermissions() {
        hasCameraPermission = granted(Manifest.permission.CAMERA)
        // Before Android 17 the local network comes with INTERNET; from 17 on it is a runtime permission.
        hasNetworkPermission = Build.VERSION.SDK_INT < LOCAL_NETWORK_SDK || granted(ACCESS_LOCAL_NETWORK)
    }

    private fun granted(permission: String) =
        ContextCompat.checkSelfPermission(this, permission) == PackageManager.PERMISSION_GRANTED

    private companion object {
        const val KEY_AUTO_CONNECT = "autoConnect"
        const val ACCESS_LOCAL_NETWORK = "android.permission.ACCESS_LOCAL_NETWORK"
        const val LOCAL_NETWORK_SDK = 37
    }
}
