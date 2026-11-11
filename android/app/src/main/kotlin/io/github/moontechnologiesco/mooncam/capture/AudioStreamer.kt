package io.github.moontechnologiesco.mooncam.capture

import android.annotation.SuppressLint
import android.content.Context
import android.content.pm.PackageManager
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import android.os.SystemClock
import android.Manifest
import io.github.moontechnologiesco.mooncam.protocol.AudioConfig
import io.github.moontechnologiesco.mooncam.protocol.Frame
import io.github.moontechnologiesco.mooncam.protocol.Messages
import java.io.IOException

/**
 * Sends the phone's microphone to the computer as PCM (16-bit, mono, 48 kHz), in 20 ms packets.
 *
 * It uses the raw voice-recognition source, so the phone does not process the sound; noise reduction happens
 * on the computer (see MoonCam's VirtualMicrophone). Sending runs on its own thread; [send] must be thread-safe
 * (FrameWriter is).
 */
class AudioStreamer(
    private val context: Context,
    private val send: (Frame) -> Unit,
    private val onFailed: (IOException) -> Unit,
) {
    private var record: AudioRecord? = null
    private var thread: Thread? = null

    @Volatile private var running = false

    /** Returns false when the microphone permission is missing or the device cannot record. */
    fun start(): Boolean {
        if (running) return true
        if (context.checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) return false
        val recorder = createRecord() ?: return false
        record = recorder
        send(Messages.audioConfig(AudioConfig(PCM_CODEC, SAMPLE_RATE, CHANNELS, ByteArray(0))))
        running = true
        recorder.startRecording()
        thread = Thread({ pump(recorder) }, "mooncam-audio").apply { isDaemon = true; start() }
        return true
    }

    fun stop() {
        running = false
        thread?.interrupt()
        thread = null
        record?.let {
            runCatching { it.stop() }
            it.release()
        }
        record = null
    }

    @SuppressLint("MissingPermission") // checked in start()
    private fun createRecord(): AudioRecord? {
        val minBuffer = AudioRecord.getMinBufferSize(SAMPLE_RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT)
        if (minBuffer <= 0) return null
        val bufferBytes = maxOf(minBuffer, PACKET_BYTES * 4)
        return try {
            AudioRecord(MediaRecorder.AudioSource.VOICE_RECOGNITION, SAMPLE_RATE, AudioFormat.CHANNEL_IN_MONO,
                AudioFormat.ENCODING_PCM_16BIT, bufferBytes).takeIf { it.state == AudioRecord.STATE_INITIALIZED }
        } catch (e: SecurityException) {
            null
        }
    }

    private fun pump(recorder: AudioRecord) {
        val packet = ByteArray(PACKET_BYTES)
        try {
            while (running && !Thread.currentThread().isInterrupted) {
                var filled = 0
                while (filled < packet.size) {
                    val read = recorder.read(packet, filled, packet.size - filled)
                    if (read <= 0) throw IOException("The microphone stopped (code $read).")
                    filled += read
                }
                send(Messages.audioFrame(packet.copyOf(), SystemClock.elapsedRealtimeNanos() / 1000))
            }
        } catch (e: IOException) {
            if (running) onFailed(e)
        }
    }

    private companion object {
        const val PCM_CODEC = 2 // docs/protocol.md: 2 = PCM signed 16-bit little-endian
        const val SAMPLE_RATE = 48_000
        const val CHANNELS = 1
        const val PACKET_BYTES = SAMPLE_RATE / 50 * 2 // 20 ms of 16-bit mono
    }
}
