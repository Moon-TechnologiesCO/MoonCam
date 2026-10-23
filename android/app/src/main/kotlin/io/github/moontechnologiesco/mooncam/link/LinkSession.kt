package io.github.moontechnologiesco.mooncam.link

import android.content.Context
import android.os.Build
import android.provider.Settings
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
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket

/** The desktop refused this phone (wrong or expired token). Retrying would not help. */
class RejectedException(reason: String) : Exception(reason)

/**
 * One phone-to-desktop link over the adb tunnel (127.0.0.1): handshake, capabilities, then commands from the
 * desktop while the camera streams. Reconnects with exponential back-off until cancelled or rejected.
 */
class LinkSession(private val context: Context, private val port: Int, private val token: String) {

    suspend fun runWithReconnect() {
        var backoffMs = INITIAL_BACKOFF_MS
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
            } catch (_: IOException) {
                // fall through to reconnect
            } catch (_: ProtocolException) {
                // fall through to reconnect
            }
            StreamingStatus.update(StreamingState.Reconnecting)
            delay(backoffMs)
            backoffMs = (backoffMs * 2).coerceAtMost(MAX_BACKOFF_MS)
        }
    }

    private suspend fun runOnce(onConnected: () -> Unit) = withContext(Dispatchers.IO) {
        Socket().use { socket ->
            // Blocking reads ignore coroutine cancellation; closing the socket unblocks them.
            val closer = currentCoroutineContext().job.invokeOnCompletion { socket.close() }
            try {
                socket.tcpNoDelay = true
                socket.connect(InetSocketAddress(InetAddress.getLoopbackAddress(), port), CONNECT_TIMEOUT_MS)
                socket.soTimeout = READ_TIMEOUT_MS // the desktop pings every second
                val reader = FrameReader(BufferedInputStream(socket.getInputStream()))
                val writer = FrameWriter(BufferedOutputStream(socket.getOutputStream()))

                writer.writePreamble()
                reader.readPreamble()
                writer.write(Messages.hello(hello()))
                val welcome = Messages.readWelcome(reader.read() ?: throw EOFException("No WELCOME."))
                if (!welcome.accepted) throw RejectedException(welcome.reason ?: "rejected")
                onConnected()
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
            } finally {
                closer.dispose()
            }
        }
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

    private fun hello(): HelloMessage {
        val name = Settings.Global.getString(context.contentResolver, Settings.Global.DEVICE_NAME) ?: Build.MODEL
        val version = context.packageManager.getPackageInfo(context.packageName, 0).versionName ?: "0"
        return HelloMessage(name, Build.MANUFACTURER, Build.MODEL, Build.VERSION.RELEASE, version, token)
    }

    private class Listener(private val computer: String, private val socket: Socket) : StreamerListener {
        override fun onCameraBusy() = StreamingStatus.update(StreamingState.CameraBusy)
        override fun onCameraResumed() = StreamingStatus.update(StreamingState.Streaming(computer))
        override fun onSendFailed(error: IOException) = socket.close() // the read loop ends and we reconnect
    }

    private companion object {
        const val CONNECT_TIMEOUT_MS = 3_000
        const val READ_TIMEOUT_MS = 5_000
        const val INITIAL_BACKOFF_MS = 500L
        const val MAX_BACKOFF_MS = 8_000L
    }
}
