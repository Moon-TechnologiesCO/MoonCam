package io.github.moontechnologiesco.mooncam

import android.content.Intent
import io.github.moontechnologiesco.mooncam.link.LinkTarget
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update

/** What the phone is doing, as shown in the app and the notification. */
sealed interface StreamingState {
    data object Idle : StreamingState
    data object Connecting : StreamingState
    data class Connected(val computer: String) : StreamingState
    data class Streaming(val computer: String) : StreamingState
    data object Reconnecting : StreamingState
    data object CameraBusy : StreamingState
    data class Rejected(val reason: String) : StreamingState

    /** Both screens show [code] and the person compares them; [confirmed] once they said it matches here. */
    data class Pairing(val computer: String, val code: String, val confirmed: Boolean = false) : StreamingState

    /** The phone gave up on its own, for a reason retrying would not fix. [message] is ready to show. */
    data class Failed(val message: String) : StreamingState

    /** The computer ended the connection (Disconnect, or MoonCam closed there). [unpaired] when it also forgot this phone. */
    data class Disconnected(val computer: String, val unpaired: Boolean = false) : StreamingState

    /** Something is running, so the screen offers Stop instead of ways to connect. */
    val isActive: Boolean get() = this !is Idle && this !is Rejected && this !is Failed && this !is Disconnected
}

/** Single source of truth for the streaming state, shared by the service and the UI. */
object StreamingStatus {
    private val mutableState = MutableStateFlow<StreamingState>(StreamingState.Idle)
    val state: StateFlow<StreamingState> = mutableState.asStateFlow()

    private val mutableWifiComputer = MutableStateFlow<String?>(null)

    /** Fingerprint of the paired computer linked over Wi-Fi right now (null over USB or when not linked). */
    val wifiComputer: StateFlow<String?> = mutableWifiComputer.asStateFlow()

    internal fun linkedOverWifi(fingerprintHex: String?) {
        mutableWifiComputer.value = fingerprintHex
    }

    internal fun update(state: StreamingState) {
        mutableState.value = state
    }

    /** The link stopped: back to Idle, unless the screen still has to say why it stopped. */
    internal fun settle() = mutableState.update { if (it.isActive) StreamingState.Idle else it }
}

/**
 * A connection request from the desktop, delivered through `adb shell am start` with an explicit intent.
 *
 * Any app on the phone could send the same intent, so the request is only *proposed* to the user (consent
 * dialog) and its fields are validated strictly: the token must look like a desktop token and the port must
 * be unprivileged. The host is always 127.0.0.1 (the adb tunnel), never taken from the intent.
 */
data class ConnectRequest(val token: String, val port: Int) {
    fun toTarget() = LinkTarget.Usb(port, token)

    companion object {
        const val ACTION = "io.github.moontechnologiesco.mooncam.action.CONNECT_USB"
        private val tokenPattern = Regex("^[A-Za-z0-9_-]{32,128}$")

        fun from(intent: Intent?): ConnectRequest? {
            if (intent?.action != ACTION) return null
            val token = intent.getStringExtra("token") ?: return null
            val port = intent.getIntExtra("port", -1)
            if (!tokenPattern.matches(token) || port !in 1024..65535) return null
            return ConnectRequest(token, port)
        }
    }
}
