package io.github.moontechnologiesco.mooncam.protocol

import java.nio.ByteBuffer
import java.nio.ByteOrder

/** MoonLink message types (docs/protocol.md §3). */
enum class FrameType(val code: Int) {
    HELLO(0x01),
    WELCOME(0x02),
    CAPABILITIES(0x03),
    CONTROL(0x04),
    VIDEO_CONFIG(0x10),
    VIDEO_FRAME(0x11),
    AUDIO_CONFIG(0x20),
    AUDIO_FRAME(0x21),
    PING(0x30),
    PONG(0x31),
    BYE(0x3F);

    companion object {
        private val byCode = entries.associateBy { it.code }

        fun fromCode(code: Int): FrameType =
            byCode[code] ?: throw ProtocolException("Unknown frame type 0x%02X.".format(code))
    }
}

object FrameFlags {
    const val NONE = 0
    const val KEY_FRAME = 1 shl 0
    const val END_OF_STREAM = 1 shl 1
    internal const val KNOWN = KEY_FRAME or END_OF_STREAM
}

/** Raised when the peer violates the protocol. The connection must be closed. */
class ProtocolException(message: String, cause: Throwable? = null) : Exception(message, cause)

/** The fixed 16-byte, big-endian header that precedes every payload. */
data class FrameHeader(val type: FrameType, val flags: Int, val length: Int, val timestampUs: Long) {

    fun encode(): ByteArray {
        if (length !in 0..MAX_PAYLOAD_LENGTH) throw ProtocolException("Payload length $length is outside 0..$MAX_PAYLOAD_LENGTH.")
        return ByteBuffer.allocate(SIZE).order(ByteOrder.BIG_ENDIAN)
            .put(type.code.toByte())
            .put(flags.toByte())
            .putShort(0)
            .putInt(length)
            .putLong(timestampUs)
            .array()
    }

    companion object {
        const val SIZE = 16
        const val MAX_PAYLOAD_LENGTH = 16 * 1024 * 1024

        /** Parses and validates a header. Strict on purpose: anything unexpected is a protocol error. */
        fun parse(bytes: ByteArray): FrameHeader {
            if (bytes.size < SIZE) throw ProtocolException("A frame header needs $SIZE bytes, got ${bytes.size}.")
            val buffer = ByteBuffer.wrap(bytes).order(ByteOrder.BIG_ENDIAN)
            val type = FrameType.fromCode(buffer.get().toInt() and 0xFF)
            val flags = buffer.get().toInt() and 0xFF
            if (flags and FrameFlags.KNOWN.inv() != 0) throw ProtocolException("Unknown flags 0x%02X.".format(flags))
            if (buffer.short.toInt() != 0) throw ProtocolException("Reserved header bits must be zero.")
            val length = buffer.int.toLong() and 0xFFFFFFFFL
            if (length > MAX_PAYLOAD_LENGTH) throw ProtocolException("Payload length $length exceeds $MAX_PAYLOAD_LENGTH.")
            return FrameHeader(type, flags, length.toInt(), buffer.long)
        }
    }
}

/** A complete MoonLink message. */
class Frame(
    val type: FrameType,
    val payload: ByteArray = ByteArray(0),
    val flags: Int = FrameFlags.NONE,
    val timestampUs: Long = 0,
) {
    val header: FrameHeader get() = FrameHeader(type, flags, payload.size, timestampUs)
    val isKeyFrame: Boolean get() = flags and FrameFlags.KEY_FRAME != 0
}

/** The 10 bytes that open every connection: ASCII "MOONLINK" + u16 version. */
object Preamble {
    const val SIZE = 10
    const val CURRENT_VERSION = 1
    private val MAGIC = "MOONLINK".toByteArray(Charsets.US_ASCII)

    fun encode(version: Int = CURRENT_VERSION): ByteArray =
        ByteBuffer.allocate(SIZE).order(ByteOrder.BIG_ENDIAN).put(MAGIC).putShort(version.toShort()).array()

    /** Returns the peer's version, or throws if the bytes are not a MoonLink preamble. */
    fun parse(bytes: ByteArray): Int {
        if (bytes.size < SIZE || !bytes.copyOfRange(0, 8).contentEquals(MAGIC)) {
            throw ProtocolException("Not a MoonLink connection (bad preamble).")
        }
        return ByteBuffer.wrap(bytes, 8, 2).order(ByteOrder.BIG_ENDIAN).short.toInt() and 0xFFFF
    }
}
