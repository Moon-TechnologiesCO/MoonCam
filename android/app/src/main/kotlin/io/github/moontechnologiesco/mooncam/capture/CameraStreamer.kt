package io.github.moontechnologiesco.mooncam.capture

import android.annotation.SuppressLint
import android.content.Context
import android.hardware.camera2.CameraAccessException
import android.hardware.camera2.CameraCaptureSession
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraDevice
import android.hardware.camera2.CameraManager
import android.hardware.camera2.CaptureRequest
import android.hardware.camera2.params.OutputConfiguration
import android.hardware.camera2.params.SessionConfiguration
import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.os.Bundle
import android.os.Handler
import android.os.HandlerThread
import android.util.Range
import android.util.Size
import android.view.Surface
import io.github.moontechnologiesco.mooncam.protocol.Frame
import io.github.moontechnologiesco.mooncam.protocol.FrameFlags
import io.github.moontechnologiesco.mooncam.protocol.FrameType
import io.github.moontechnologiesco.mooncam.protocol.FrameWriter
import io.github.moontechnologiesco.mooncam.protocol.VideoCodec
import io.github.moontechnologiesco.mooncam.protocol.VideoConfig
import java.io.IOException
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.Executor
import kotlin.math.abs

/** What the desktop asked for. The streamer picks the closest size the camera really supports. */
data class StreamRequest(val cameraId: String, val width: Int, val height: Int, val fps: Int, val bitrateBps: Int)

/** Problems the streamer reports to the connection (and from there to the user). */
interface StreamerListener {
    fun onCameraBusy()
    fun onCameraResumed()
    fun onSendFailed(error: IOException)
}

/**
 * Camera2 → MediaCodec (H.264, hardware) → MoonLink.
 *
 * The camera renders straight into the encoder's input surface, so frames never touch the CPU. Encoded units
 * go to a small send queue drained by its own thread: if the network falls behind, the queue is emptied and
 * a key frame requested — old video is worse than missing video (docs/architecture.md §6).
 */
class CameraStreamer(
    context: Context,
    private val catalog: CameraCatalog,
    private val writer: FrameWriter,
    private val listener: StreamerListener,
) {
    private val cameraManager = context.getSystemService(CameraManager::class.java)
    private val thread = HandlerThread("mooncam-camera").apply { start() }
    private val handler = Handler(thread.looper)
    private val executor = Executor { handler.post(it) }
    private val queue = ArrayBlockingQueue<Frame>(QUEUE_CAPACITY)
    private var sender: Thread? = null
    private var encoder: MediaCodec? = null
    private var inputSurface: Surface? = null
    private var camera: CameraDevice? = null
    private var session: CameraCaptureSession? = null
    private var current: StreamRequest? = null
    @Volatile private var waitingForKeyFrame = true

    // Every start/stop begins a new generation. Camera2 delivers callbacks asynchronously, so a callback from
    // an older generation (e.g. "session configured" after we already switched cameras) must be ignored.
    private var generation = 0

    // True only when another app took the camera from us; our own close() must not trigger a reopen.
    private var cameraTaken = false

    private val availability = object : CameraManager.AvailabilityCallback() {
        override fun onCameraAvailable(cameraId: String) {
            val request = current ?: return
            if (cameraTaken && catalog.resolve(request.cameraId)?.first == cameraId) {
                listener.onCameraResumed()
                start(request)
            }
        }
    }

    init {
        cameraManager.registerAvailabilityCallback(availability, handler)
    }

    fun start(request: StreamRequest) = handler.post {
        if (request == current && camera != null) return@post // already streaming exactly this
        closeCapture()
        val gen = ++generation
        current = request
        cameraTaken = false
        val (logicalId, physicalId) = catalog.resolve(request.cameraId) ?: return@post
        val size = closestSize(catalog.characteristics(physicalId ?: logicalId), request)
        startEncoder(size, request)
        openCamera(gen, logicalId, physicalId, request.fps)
    }

    fun stop() = handler.post {
        generation++
        current = null
        closeCapture()
    }

    fun requestKeyFrame() = handler.post {
        encoder?.setParameters(Bundle().apply { putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0) })
    }

    fun release() {
        stop()
        handler.post {
            cameraManager.unregisterAvailabilityCallback(availability)
            thread.quitSafely()
        }
    }

    private fun startEncoder(size: Size, request: StreamRequest) {
        val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, size.width, size.height).apply {
            setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
            setInteger(MediaFormat.KEY_BIT_RATE, request.bitrateBps)
            setInteger(MediaFormat.KEY_FRAME_RATE, request.fps)
            setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 1)
            setInteger(MediaFormat.KEY_MAX_B_FRAMES, 0) // B-frames add a frame of delay
            setInteger(MediaFormat.KEY_PRIORITY, 0) // realtime
            setInteger(MediaFormat.KEY_LATENCY, 1)
            setLong(MediaFormat.KEY_REPEAT_PREVIOUS_FRAME_AFTER, 100_000L) // keep frames flowing on still scenes
        }
        val codec = MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
        codec.setCallback(EncoderCallback(size, request.fps), handler)
        codec.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
        inputSurface = codec.createInputSurface()
        codec.start()
        encoder = codec
        waitingForKeyFrame = true
        startSender()
    }

    @SuppressLint("MissingPermission") // the activity obtains CAMERA before starting the service
    private fun openCamera(gen: Int, logicalId: String, physicalId: String?, fps: Int) {
        val callback = object : CameraDevice.StateCallback() {
            override fun onOpened(device: CameraDevice) {
                if (gen != generation) {
                    device.close() // we already moved on to another camera
                    return
                }
                camera = device
                createSession(gen, device, physicalId, fps)
            }

            override fun onDisconnected(device: CameraDevice) = cameraLost(gen, device)

            override fun onError(device: CameraDevice, error: Int) = cameraLost(gen, device)
        }
        try {
            cameraManager.openCamera(logicalId, callback, handler)
        } catch (e: CameraAccessException) {
            markTaken()
        }
    }

    /** The camera stopped working for us. If it is the current one, another app took it: wait until it is free. */
    private fun cameraLost(gen: Int, device: CameraDevice) {
        device.close()
        if (gen != generation) return
        camera = null
        markTaken()
    }

    private fun markTaken() {
        cameraTaken = true
        listener.onCameraBusy()
    }

    private fun createSession(gen: Int, device: CameraDevice, physicalId: String?, fps: Int) {
        val surface = inputSurface ?: return
        val output = OutputConfiguration(surface).apply { physicalId?.let(::setPhysicalCameraId) }
        val config = SessionConfiguration(SessionConfiguration.SESSION_REGULAR, listOf(output), executor,
            object : CameraCaptureSession.StateCallback() {
                override fun onConfigured(captureSession: CameraCaptureSession) {
                    if (gen != generation || camera !== device) {
                        captureSession.close() // stale: the camera was switched or closed meanwhile
                        return
                    }
                    session = captureSession
                    try {
                        val request = device.createCaptureRequest(CameraDevice.TEMPLATE_RECORD).apply {
                            addTarget(surface)
                            bestFpsRange(device.id, fps)?.let { set(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, it) }
                        }
                        captureSession.setRepeatingRequest(request.build(), null, handler)
                    } catch (e: IllegalStateException) {
                        cameraLost(gen, device)
                    } catch (e: CameraAccessException) {
                        cameraLost(gen, device)
                    }
                }

                override fun onConfigureFailed(captureSession: CameraCaptureSession) = cameraLost(gen, device)
            })
        try {
            device.createCaptureSession(config)
        } catch (e: IllegalStateException) {
            cameraLost(gen, device)
        } catch (e: CameraAccessException) {
            cameraLost(gen, device)
        }
    }

    private fun bestFpsRange(cameraId: String, fps: Int): Range<Int>? =
        catalog.characteristics(cameraId).get(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES).orEmpty()
            .filter { it.upper <= fps }
            .maxWithOrNull(compareBy<Range<Int>> { it.upper }.thenBy { it.lower }) // fixed ranges avoid dark-scene slowdowns

    private fun closestSize(c: CameraCharacteristics, request: StreamRequest): Size {
        val wanted = request.width * request.height
        return CameraCatalog.encoderSizes(c)
            .filter { it.width * 9 == it.height * 16 }
            .minByOrNull { abs(it.width * it.height - wanted) }
            ?: Size(request.width, request.height)
    }

    private fun send(frame: Frame, essential: Boolean) {
        if (!essential) {
            if (waitingForKeyFrame && !frame.isKeyFrame) return
            waitingForKeyFrame = false
        }
        if (queue.offer(frame)) return
        // The network is behind: drop the backlog and resynchronise on the next key frame.
        queue.clear()
        if (essential) queue.offer(frame)
        waitingForKeyFrame = true
        requestKeyFrame()
    }

    private fun startSender() {
        if (sender?.isAlive == true) return
        sender = Thread({
            try {
                while (!Thread.currentThread().isInterrupted) writer.write(queue.take())
            } catch (_: InterruptedException) {
                // stopped
            } catch (e: IOException) {
                listener.onSendFailed(e)
            }
        }, "mooncam-sender").apply { start() }
    }

    private fun closeCapture() {
        session?.close()
        session = null
        camera?.close()
        camera = null
        encoder?.let {
            runCatching { it.stop() }
            it.release()
        }
        encoder = null
        inputSurface?.release()
        inputSurface = null
        sender?.interrupt()
        sender = null
        queue.clear()
    }

    private inner class EncoderCallback(private val size: Size, private val fps: Int) : MediaCodec.Callback() {
        override fun onInputBufferAvailable(codec: MediaCodec, index: Int) = Unit // input comes from the surface

        override fun onOutputBufferAvailable(codec: MediaCodec, index: Int, info: MediaCodec.BufferInfo) {
            val buffer = codec.getOutputBuffer(index)
            val data = ByteArray(info.size)
            buffer?.apply {
                position(info.offset)
                limit(info.offset + info.size)
                get(data)
            }
            codec.releaseOutputBuffer(index, false)
            if (info.size == 0) return

            if (info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG != 0) {
                send(VideoConfig(VideoCodec.H264, size.width, size.height, fps, data).toFrame(), essential = true)
            } else {
                val key = info.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME != 0
                send(Frame(FrameType.VIDEO_FRAME, data, if (key) FrameFlags.KEY_FRAME else FrameFlags.NONE, info.presentationTimeUs), essential = false)
            }
        }

        override fun onError(codec: MediaCodec, e: MediaCodec.CodecException) = listener.onCameraBusy()

        override fun onOutputFormatChanged(codec: MediaCodec, format: MediaFormat) = Unit
    }

    private companion object {
        const val QUEUE_CAPACITY = 6
    }
}
