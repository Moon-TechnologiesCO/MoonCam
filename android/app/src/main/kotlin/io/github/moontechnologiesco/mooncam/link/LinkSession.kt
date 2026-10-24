package io.github.moontechnologiesco.mooncam.link

import android.content.Context
import android.net.wifi.WifiManager
import android.os.Build
import android.provider.Settings
import io.github.moontechnologiesco.mooncam.R
import io.github.moontechnologiesco.mooncam.StreamingState
import io.github.moontechnologiesco.mooncam.StreamingStatus
import io.github.moontechnologiesco.mooncam.capture.CameraCatalog
import io.github.moontechnologiesco.mooncam.capture.CameraStreamer
import io.github.moontechnologiesco.mooncam.capture.StreamRequest
import io.github.moontechnologiesco.mooncam.capture.StreamerListener
import io.github.moontechnologiesco.mooncam.protocol.CapabilitiesMessage
import io.github.moontechnologiesco.mooncam.protocol.ControlMessage
import io.github.moontechnologiesco.mooncam.protocol.ControlOps
import io.github.moontechnologiesco.mooncam.protocol.FrameReader
import io.github.moontechnologiesco.mooncam.protocol.FrameType
import io.github.moontechnologiesco.mooncam.protocol.FrameWriter
import io.github.moontechnologiesco.mooncam.protocol.Heartbeat
import io.github.moontechnologiesco.mooncam.protocol.HelloMessage
import io.github.moontechnologiesco.mooncam.protocol.Messages
import io.github.moontechnologiesco.mooncam.protocol.Pairing
import io.github.moontechnologiesco.mooncam.protocol.ProtocolException
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.job
import kotlinx.coroutines.withContext
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.EOFException
import java.io.IOException
import java.net.Socket
import java.util.Base64

/** The desktop refused this phone (wrong or expired token, not paired, pairing cancelled). Retrying would not help. */
class RejectedException(reason: String) : Exception(reason)

/**
 * One phone-to-desktop link, over the adb tunnel or over Wi-Fi: handshake, pairing when the computer is new,
 * capabilities, then commands from the desktop while the camera streams. Reconnects with exponential back-off
 * until cancelled or refused; a pairing attempt is never retried on its own, so the code is never shown twice.
 */
class LinkSession(private val context: Context, target: LinkTarget) {
    private val paired = PairedComputers.get(context)

    @Volatile private var target = target

    /** Set while a pairing code is on screen, so [confirmPairing] can answer from another thread. */
    @Volatile private var pairingWriter: FrameWriter? = null

    suspend fun runWithReconnect() {
        var backoffMs = INITIAL_BACKOFF_MS
        val wifiLock = if (target is LinkTarget.WiFi) lowLatencyWifiLock() else null
        try {
            while (true) {
                currentCoroutineContext().ensureActive()
                StreamingStatus.update(StreamingState.Connecting)
                try {
                    runOnce { backoffMs = INITIAL_BACKOFF_MS } // a successful handshake resets the back-off
                } catch (e: CancellationException) {
                    throw e
                } catch (e: RejectedException) {
                    StreamingStatus.update(StreamingState.Rejected(e.message.orEmpty()))
                    return
                } catch (e: IdentityChangedException) {
                    StreamingStatus.update(StreamingState.Failed(context.getString(R.string.identity_changed, e.computer)))
                    return
                } catch (e: Exception) {
                    if (e !is IOException && e !is ProtocolException) throw e
                    if ((target as? LinkTarget.WiFi)?.isPairing == true) {
                        StreamingStatus.update(StreamingState.Failed(context.getString(R.string.pairing_interrupted)))
                        return
                    }
                    // otherwise fall through to reconnect
                }
                StreamingStatus.update(StreamingState.Reconnecting)
                delay(backoffMs)
                backoffMs = (backoffMs * 2).coerceAtMost(MAX_BACKOFF_MS)
            }
        } finally {
            wifiLock?.release()
        }
    }

    /** The person saw the same code on both screens. Blocking: call it from a background thread. */
    fun confirmPairing() {
        val writer = pairingWriter ?: return
        val showing = StreamingStatus.state.value as? StreamingState.Pairing ?: return
        try {
            writer.write(Messages.control(ControlMessage(ControlOps.PAIR_CONFIRM)))
            StreamingStatus.update(showing.copy(confirmed = true))
        } catch (_: IOException) {
            // The connection is gone; the read loop reports it.
        }
    }

    private suspend fun runOnce(onConnected: () -> Unit) = withContext(Dispatchers.IO) {
        val target = currentTarget()
        val raw = Socket()
        // Blocking connects and reads ignore coroutine cancellation; closing the socket unblocks them.
        val closer = currentCoroutineContext().job.invokeOnCompletion { raw.close() }
        try {
            raw.tcpNoDelay = true
            Connector.open(target, raw).use { connection -> talk(target, connection, onConnected) }
        } finally {
            closer.dispose()
            raw.close()
        }
    }

    private fun talk(target: LinkTarget, connection: Connection, onConnected: () -> Unit) {
        val socket = connection.socket
        socket.soTimeout = READ_TIMEOUT_MS // the desktop pings every second
        val reader = FrameReader(BufferedInputStream(socket.getInputStream()))
        val writer = FrameWriter(BufferedOutputStream(socket.getOutputStream()))

        writer.writePreamble()
        reader.readPreamble()
        writer.write(Messages.hello(hello(target)))
        val welcome = Messages.readWelcome(reader.read() ?: throw EOFException("No WELCOME."))
        if (!welcome.accepted) throw RejectedException(welcome.reason ?: "rejected")
        onConnected()
        if (welcome.pairing == true) {
            val wifi = (target as? LinkTarget.WiFi)?.takeIf { it.isPairing }
                ?: throw ProtocolException("The computer asked to pair on a connection that cannot pair.")
            pair(wifi, connection.desktopFingerprint!!, welcome.serverName, reader, writer)
        }
        StreamingStatus.update(StreamingState.Connected(welcome.serverName))

        val catalog = CameraCatalog(context)
        writer.write(Messages.capabilities(CapabilitiesMessage(catalog.list())))
        val streamer = CameraStreamer(context, catalog, writer, Listener(welcome.serverName, socket))
        try {
            while (true) {
                val frame = reader.read() ?: break
                when (frame.type) {
                    FrameType.PING -> writer.write(Heartbeat.pong(frame))
                    FrameType.CONTROL -> handle(Messages.readControl(frame), streamer, welcome.serverName)
                    FrameType.BYE -> break
                    else -> Unit // nothing else is meant for the phone; ignore politely
                }
            }
        } finally {
            streamer.release()
        }
    }

    /**
     * Commit → nonce → reveal (docs/security.md §Pairing), then shows the code and waits until the desktop says
     * both people confirmed it. The phone's confirmation is sent by [confirmPairing] while this keeps reading.
     */
    private fun pair(target: LinkTarget.WiFi, desktopFingerprint: ByteArray, computer: String, reader: FrameReader, writer: FrameWriter) {
        val nonce = Pairing.newNonce()
        writer.write(Messages.control(ControlMessage(ControlOps.PAIR_COMMIT, data = encode(Pairing.commit(nonce)))))
        val desktopNonce = decode(awaitControl(reader, writer, ControlOps.PAIR_NONCE).data)
        writer.write(Messages.control(ControlMessage(ControlOps.PAIR_REVEAL, data = encode(nonce))))

        val code = Pairing.code(PhoneIdentity.fingerprint, desktopFingerprint, nonce, desktopNonce)
        pairingWriter = writer
        StreamingStatus.update(StreamingState.Pairing(computer, code))
        try {
            awaitControl(reader, writer, ControlOps.PAIRED)
        } finally {
            pairingWriter = null
        }

        val fingerprint = desktopFingerprint.toHex()
        paired.save(PairedComputer(computer, fingerprint, target.host, target.port))
        this.target = target.copy(computer = computer, desktopFingerprintHex = fingerprint) // reconnects pin it
    }

    /** Reads until the wanted control message, answering pings. A BYE means the desktop ended it on purpose. */
    private fun awaitControl(reader: FrameReader, writer: FrameWriter, op: String): ControlMessage {
        while (true) {
            val frame = reader.read() ?: throw EOFException("The computer closed the connection.")
            when (frame.type) {
                FrameType.PING -> writer.write(Heartbeat.pong(frame))
                FrameType.CONTROL -> Messages.readControl(frame).takeIf { it.op == op }?.let { return it }
                FrameType.BYE -> throw RejectedException(Messages.readBye(frame).reason)
                else -> Unit
            }
        }
    }

    /** A paired computer may have a new address since the link started: use the latest one discovery saw. */
    private fun currentTarget(): LinkTarget = when (val t = target) {
        is LinkTarget.WiFi -> t.desktopFingerprintHex?.let(paired::find)?.let { t.copy(host = it.host, port = it.port) } ?: t
        is LinkTarget.Usb -> t
    }

    private fun handle(message: ControlMessage, streamer: CameraStreamer, computer: String) {
        when (message.op) {
            ControlOps.START -> {
                streamer.start(
                    StreamRequest(
                        cameraId = message.camera ?: "0",
                        width = message.width ?: 1280,
                        height = message.height ?: 720,
                        fps = (message.fps ?: 30).coerceIn(5, 60),
                        bitrateBps = (message.bitrateBps ?: 3_000_000).coerceIn(250_000, 40_000_000),
                    ),
                )
                StreamingStatus.update(StreamingState.Streaming(computer))
            }
            ControlOps.STOP -> {
                streamer.stop()
                StreamingStatus.update(StreamingState.Connected(computer))
            }
            ControlOps.KEY_FRAME -> streamer.requestKeyFrame()
        }
    }

    private fun hello(target: LinkTarget): HelloMessage {
        val name = Settings.Global.getString(context.contentResolver, Settings.Global.DEVICE_NAME) ?: Build.MODEL
        val version = context.packageManager.getPackageInfo(context.packageName, 0).versionName ?: "0"
        val token = (target as? LinkTarget.Usb)?.token.orEmpty()
        val pairing = (target as? LinkTarget.WiFi)?.isPairing?.takeIf { it }
        return HelloMessage(name, Build.MANUFACTURER, Build.MODEL, Build.VERSION.RELEASE, version, token, pairing)
    }

    /** Keeps Wi-Fi out of power saving while streaming; otherwise frames arrive in bursts. */
    private fun lowLatencyWifiLock(): WifiManager.WifiLock? =
        context.getSystemService(WifiManager::class.java)
            ?.createWifiLock(WifiManager.WIFI_MODE_FULL_LOW_LATENCY, "MoonCam:stream")
            ?.apply {
                setReferenceCounted(false)
                acquire()
            }

    private class Listener(private val computer: String, private val socket: Socket) : StreamerListener {
        override fun onCameraBusy() = StreamingStatus.update(StreamingState.CameraBusy)
        override fun onCameraResumed() = StreamingStatus.update(StreamingState.Streaming(computer))
        override fun onSendFailed(error: IOException) = socket.close() // the read loop ends and we reconnect
    }

    private companion object {
        const val READ_TIMEOUT_MS = 5_000
        const val INITIAL_BACKOFF_MS = 500L
        const val MAX_BACKOFF_MS = 8_000L

        fun encode(bytes: ByteArray): String = Base64.getEncoder().encodeToString(bytes)

        fun decode(base64: String?): ByteArray {
            val bytes = try {
                Base64.getDecoder().decode(base64.orEmpty())
            } catch (e: IllegalArgumentException) {
                throw ProtocolException("Pairing data is not valid base64.", e)
            }
            if (bytes.size != Pairing.NONCE_SIZE) throw ProtocolException("The computer's pairing nonce has the wrong size.")
            return bytes
        }
    }
}
