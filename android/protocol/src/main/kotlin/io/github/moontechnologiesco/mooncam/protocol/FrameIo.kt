package io.github.moontechnologiesco.mooncam.protocol

import java.io.DataInputStream
import java.io.EOFException
import java.io.InputStream
import java.io.OutputStream

/** Reads preambles and frames. Blocking: call it from a background thread. */
class FrameReader(input: InputStream) {
    private val input = DataInputStream(input)
    private val header = ByteArray(FrameHeader.SIZE)

    fun readPreamble(): Int {
        val bytes = ByteArray(Preamble.SIZE)
        input.readFully(bytes)
        return Preamble.parse(bytes)
    }

    /**
     * Reads the next frame. Returns null when the peer closes the connection cleanly between frames;
     * throws [EOFException] if it closes in the middle of one.
     */
    fun read(): Frame? {
        val first = input.read()
        if (first < 0) return null
        header[0] = first.toByte()
        input.readFully(header, 1, FrameHeader.SIZE - 1)
        val parsed = FrameHeader.parse(header)
        val payload = ByteArray(parsed.length)
        input.readFully(payload)
        return Frame(parsed.type, payload, parsed.flags, parsed.timestampUs)
    }
}

/**
 * Writes preambles and frames. Thread-safe: each frame is written atomically, so a PONG can never land in
 * the middle of a video frame.
 */
class FrameWriter(private val output: OutputStream) {
    private val lock = Any()

    fun writePreamble(version: Int = Preamble.CURRENT_VERSION) = synchronized(lock) {
        output.write(Preamble.encode(version))
        output.flush()
    }

    fun write(frame: Frame) = synchronized(lock) {
        output.write(frame.header.encode())
        output.write(frame.payload)
        output.flush()
    }
}
