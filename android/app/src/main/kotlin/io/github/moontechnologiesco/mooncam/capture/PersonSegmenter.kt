package io.github.moontechnologiesco.mooncam.capture

import android.content.Context
import android.graphics.Bitmap
import android.media.Image
import android.media.ImageReader
import android.os.Handler
import android.os.HandlerThread
import android.os.SystemClock
import android.util.Log
import com.google.mediapipe.framework.image.BitmapImageBuilder
import com.google.mediapipe.framework.image.ByteBufferExtractor
import com.google.mediapipe.tasks.core.BaseOptions
import com.google.mediapipe.tasks.vision.core.RunningMode
import com.google.mediapipe.tasks.vision.imagesegmenter.ImageSegmenter
import io.github.moontechnologiesco.mooncam.protocol.Frame
import io.github.moontechnologiesco.mooncam.protocol.Messages

/**
 * Finds the person in a small copy of the camera picture with MediaPipe's selfie segmenter (landscape model,
 * 256x144) and sends the result to the computer as a MASK frame. The computer blurs or replaces everything
 * else, so the phone only does the cheap part. Runs on its own thread, at most [MAX_FPS] times a second;
 * frames that arrive while it is busy are skipped.
 */
class PersonSegmenter(context: Context, private val send: (Frame) -> Unit) {
    private val thread = HandlerThread("mooncam-segmenter").apply { start() }
    private val handler = Handler(thread.looper)
    private val segmenter: ImageSegmenter = ImageSegmenter.createFromOptions(
        context,
        ImageSegmenter.ImageSegmenterOptions.builder()
            .setBaseOptions(BaseOptions.builder().setModelAssetPath(MODEL).build())
            .setRunningMode(RunningMode.VIDEO)
            .setOutputConfidenceMasks(true)
            .setOutputCategoryMask(false)
            .build(),
    )
    private val pixels = IntArray(WIDTH * HEIGHT)
    private val bitmap = Bitmap.createBitmap(WIDTH, HEIGHT, Bitmap.Config.ARGB_8888)
    private val mask = ByteArray(WIDTH * HEIGHT)
    private var lastRunMs = 0L
    private var lastTimestampMs = 0L

    /** A small YUV reader to add to the capture session; its pictures feed the segmenter. */
    fun createReader(width: Int, height: Int): ImageReader =
        ImageReader.newInstance(width, height, android.graphics.ImageFormat.YUV_420_888, 2).apply {
            setOnImageAvailableListener({ reader -> onImage(reader) }, handler)
        }

    private fun onImage(reader: ImageReader) {
        val image = reader.acquireLatestImage() ?: return
        try {
            val now = SystemClock.elapsedRealtime()
            if (now - lastRunMs < 1000L / MAX_FPS) return
            lastRunMs = now
            toBitmap(image)
            val timestampMs = maxOf(image.timestamp / 1_000_000, lastTimestampMs + 1) // VIDEO mode needs increasing times
            lastTimestampMs = timestampMs
            val result = segmenter.segmentForVideo(BitmapImageBuilder(bitmap).build(), timestampMs)
            val masks = result.confidenceMasks().orElse(null) ?: return
            val person = masks.last() // one mask (person) for the selfie model; if two, the second is the person
            val confidence = ByteBufferExtractor.extract(person).asFloatBuffer()
            for (i in mask.indices) mask[i] = (confidence.get(i).coerceIn(0f, 1f) * 255f).toInt().toByte()
            masks.forEach { it.close() }
            send(Messages.mask(WIDTH, HEIGHT, mask.copyOf(), image.timestamp / 1000))
        } catch (e: RuntimeException) {
            Log.w(TAG, "Segmentation failed", e)
        } finally {
            image.close()
        }
    }

    /** YUV_420_888 to ARGB at 256x144, sampling the nearest pixel: cheap and plenty for a mask. */
    private fun toBitmap(image: Image) {
        val (yPlane, uPlane, vPlane) = image.planes
        val yBuf = yPlane.buffer; val uBuf = uPlane.buffer; val vBuf = vPlane.buffer
        val yRow = yPlane.rowStride; val uvRow = uPlane.rowStride; val uvPixel = uPlane.pixelStride
        val sx = image.width.toFloat() / WIDTH; val sy = image.height.toFloat() / HEIGHT
        for (y in 0 until HEIGHT) {
            val srcY = (y * sy).toInt()
            for (x in 0 until WIDTH) {
                val srcX = (x * sx).toInt()
                val luma = (yBuf.get(srcY * yRow + srcX).toInt() and 0xFF) - 16
                val uvIndex = (srcY / 2) * uvRow + (srcX / 2) * uvPixel
                val u = (uBuf.get(uvIndex).toInt() and 0xFF) - 128
                val v = (vBuf.get(uvIndex).toInt() and 0xFF) - 128
                val c = 1.164f * luma
                val r = (c + 1.596f * v).toInt().coerceIn(0, 255)
                val g = (c - 0.392f * u - 0.813f * v).toInt().coerceIn(0, 255)
                val b = (c + 2.017f * u).toInt().coerceIn(0, 255)
                pixels[y * WIDTH + x] = (0xFF shl 24) or (r shl 16) or (g shl 8) or b
            }
        }
        bitmap.setPixels(pixels, 0, WIDTH, 0, 0, WIDTH, HEIGHT)
    }

    fun release() {
        handler.post {
            segmenter.close()
            thread.quitSafely()
        }
    }

    companion object {
        const val WIDTH = 256
        const val HEIGHT = 144
        private const val MAX_FPS = 15
        private const val MODEL = "selfie_segmenter_landscape.tflite"
        private const val TAG = "MoonCamSegmenter"

        /** True when this build carries the model (always, unless someone stripped the asset). */
        fun isAvailable(context: Context): Boolean =
            runCatching { context.assets.openFd(MODEL).close(); true }.getOrDefault(false)
    }
}
