package io.github.moontechnologiesco.mooncam.protocol

import kotlinx.serialization.KSerializer
import kotlinx.serialization.Serializable
import kotlinx.serialization.SerializationException
import kotlinx.serialization.json.Json
import java.nio.ByteBuffer
import java.nio.ByteOrder

@Serializable
data class HelloMessage(
    val deviceName: String,
    val manufacturer: String,
    val model: String,
    val androidVersion: String,
    val appVersion: String,
    val token: String,
    val pairing: Boolean? = null,
)

@Serializable
data class WelcomeMessage(
    val accepted: Boolean,
    val sessionId: String? = null,
    val serverName: String,
    val reason: String? = null,
    val pairing: Boolean? = null,
)

@Serializable
data class ByeMessage(val reason: String)

@Serializable
data class VideoSize(val width: Int, val height: Int)

/** One selectable lens. [physicalId] is set when the lens belongs to a logical multi-camera. */
@Serializable
data class CameraInfo(
    val id: String,
    val facing: String,
    val lens: String,
    val physicalId: String? = null,
    val focalLengthMm: Double? = null,
    val sizes: List<VideoSize>,
    val frameRates: List<Int>,
)

@Serializable
data class CapabilitiesMessage(val cameras: List<CameraInfo>)

object ControlOps {
    const val START = "start"
    const val STOP = "stop"
    const val KEY_FRAME = "keyFrame"
    const val STATUS = "status"
    const val PAIR_COMMIT = "pairCommit"
    const val PAIR_NONCE = "pairNonce"
    const val PAIR_REVEAL = "pairReveal"
    const val PAIR_CONFIRM = "pairConfirm"
    const val PAIRED = "paired"

    /** Either side: "I no longer trust you". The receiver forgets the sender too and the session ends. */
    const val UNPAIR = "unpair"
}

/** A command or event; fields that do not apply to an operation stay null and are not sent. */
@Serializable
data class ControlMessage(
    val op: String,
    val camera: String? = null,
    val width: Int? = null,
    val height: Int? = null,
    val fps: Int? = null,
    val bitrateBps: Int? = null,
    val codec: String? = null,
    val streaming: Boolean? = null,
    val message: String? = null,
    val data: String? = null,
)

/** Builds and reads the JSON-carrying frames. */
object Messages {
    private val json = Json {
        ignoreUnknownKeys = true // forward compatibility: newer desktops may add fields
        explicitNulls = false
    }

    fun hello(message: HelloMessage) = encode(FrameType.HELLO, message, HelloMessage.serializer())
    fun capabilities(message: CapabilitiesMessage) = encode(FrameType.CAPABILITIES, message, CapabilitiesMessage.serializer())
    fun control(message: ControlMessage) = encode(FrameType.CONTROL, message, ControlMessage.serializer())
    fun bye(reason: String) = encode(FrameType.BYE, ByeMessage(reason), ByeMessage.serializer())

    fun readWelcome(frame: Frame) = decode(frame, FrameType.WELCOME, WelcomeMessage.serializer())
    fun readControl(frame: Frame) = decode(frame, FrameType.CONTROL, ControlMessage.serializer())
    fun readBye(frame: Frame) = decode(frame, FrameType.BYE, ByeMessage.serializer())
    fun readHello(frame: Frame) = decode(frame, FrameType.HELLO, HelloMessage.serializer())

    private fun <T> encode(type: FrameType, message: T, serializer: KSerializer<T>) =
        Frame(type, json.encodeToString(serializer, message).toByteArray(Charsets.UTF_8))

    private fun <T> decode(frame: Frame, expected: FrameType, serializer: KSerializer<T>): T {
        if (frame.type != expected) throw ProtocolException("Expected $expected, got ${frame.type}.")
        return try {
            json.decodeFromString(serializer, frame.payload.toString(Charsets.UTF_8))
        } catch (e: SerializationException) {
            throw ProtocolException("$expected payload is not valid JSON.", e)
        } catch (e: IllegalArgumentException) {
            throw ProtocolException("$expected payload is not valid JSON.", e)
        }
    }
}

object VideoCodec {
    const val H264 = 1
    const val H265 = 2
}

/** Describes the video stream that follows; [codecData] holds Annex-B SPS/PPS. */
class VideoConfig(val codec: Int, val width: Int, val height: Int, val fps: Int, val codecData: ByteArray) {

    fun toFrame(): Frame {
        val buffer = ByteBuffer.allocate(6 + codecData.size).order(ByteOrder.BIG_ENDIAN)
            .put(codec.toByte()).putShort(width.toShort()).putShort(height.toShort()).put(fps.toByte()).put(codecData)
        return Frame(FrameType.VIDEO_CONFIG, buffer.array())
    }

    companion object {
        fun fromFrame(frame: Frame): VideoConfig {
            if (frame.type != FrameType.VIDEO_CONFIG || frame.payload.size < 6) throw ProtocolException("Malformed VIDEO_CONFIG.")
            val b = ByteBuffer.wrap(frame.payload).order(ByteOrder.BIG_ENDIAN)
            val codec = b.get().toInt() and 0xFF
            val width = b.short.toInt() and 0xFFFF
            val height = b.short.toInt() and 0xFFFF
            val fps = b.get().toInt() and 0xFF
            if (width == 0 || height == 0 || width % 2 != 0 || height % 2 != 0) throw ProtocolException("Invalid video size ${width}x$height.")
            return VideoConfig(codec, width, height, fps, frame.payload.copyOfRange(6, frame.payload.size))
        }
    }
}

/** Describes the audio stream that follows; [codecData] holds OpusHead for Opus. */
class AudioConfig(val codec: Int, val sampleRate: Int, val channels: Int, val codecData: ByteArray) {
    companion object {
        fun fromFrame(frame: Frame): AudioConfig {
            if (frame.type != FrameType.AUDIO_CONFIG || frame.payload.size < 6) throw ProtocolException("Malformed AUDIO_CONFIG.")
            val b = ByteBuffer.wrap(frame.payload).order(ByteOrder.BIG_ENDIAN)
            val codec = b.get().toInt() and 0xFF
            val rate = b.int
            val channels = b.get().toInt() and 0xFF
            if (channels !in 1..2) throw ProtocolException("Unsupported channel count $channels.")
            return AudioConfig(codec, rate, channels, frame.payload.copyOfRange(6, frame.payload.size))
        }
    }
}

/** PING and PONG carry the same 8-byte nonce; answering is just echoing. */
object Heartbeat {
    fun pong(ping: Frame) = Frame(FrameType.PONG, ping.payload, timestampUs = ping.timestampUs)
}
