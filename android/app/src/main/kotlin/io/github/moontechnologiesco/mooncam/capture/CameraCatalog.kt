package io.github.moontechnologiesco.mooncam.capture

import android.content.Context
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraManager
import android.media.MediaCodec
import android.util.Size
import io.github.moontechnologiesco.mooncam.protocol.CameraInfo
import io.github.moontechnologiesco.mooncam.protocol.VideoSize

/**
 * Lists every lens the phone lets apps use: separate camera ids, and the physical lenses (ultra-wide,
 * telephoto) hidden behind a logical multi-camera. Lens type is inferred from focal length relative to the
 * main lens, which is how manufacturers' own camera apps label them.
 */
class CameraCatalog(context: Context) {
    private val manager = context.getSystemService(CameraManager::class.java)

    fun list(): List<CameraInfo> = manager.cameraIdList.flatMap(::describe)

    /** Resolves an id sent by the desktop (logical or physical) to (logical camera to open, physical lens). */
    fun resolve(id: String): Pair<String, String?>? {
        if (id in manager.cameraIdList) return id to null
        val logical = manager.cameraIdList.firstOrNull { id in manager.getCameraCharacteristics(it).physicalCameraIds }
        return logical?.let { it to id }
    }

    fun characteristics(id: String): CameraCharacteristics = manager.getCameraCharacteristics(id)

    private fun describe(id: String): List<CameraInfo> {
        val c = manager.getCameraCharacteristics(id)
        val facing = when (c.get(CameraCharacteristics.LENS_FACING)) {
            CameraCharacteristics.LENS_FACING_FRONT -> "front"
            CameraCharacteristics.LENS_FACING_EXTERNAL -> "external"
            else -> "back"
        }
        val mainFocal = focalLength(c)
        val main = CameraInfo(id, facing, "wide", null, mainFocal, sizes(c), frameRates(c))

        val capabilities = c.get(CameraCharacteristics.REQUEST_AVAILABLE_CAPABILITIES) ?: intArrayOf()
        if (mainFocal == null || CameraCharacteristics.REQUEST_AVAILABLE_CAPABILITIES_LOGICAL_MULTI_CAMERA !in capabilities) {
            return listOf(main)
        }
        val lenses = c.physicalCameraIds.mapNotNull { physicalId ->
            val pc = manager.getCameraCharacteristics(physicalId)
            val focal = focalLength(pc) ?: return@mapNotNull null
            val lens = when {
                focal < mainFocal * 0.7 -> "ultraWide"
                focal > mainFocal * 1.6 -> "telephoto"
                else -> return@mapNotNull null
            }
            CameraInfo(id, facing, lens, physicalId, focal, sizes(pc).ifEmpty { sizes(c) }, frameRates(pc).ifEmpty { frameRates(c) })
        }
        return listOf(main) + lenses.distinctBy { it.lens }
    }

    private fun focalLength(c: CameraCharacteristics): Double? =
        c.get(CameraCharacteristics.LENS_INFO_AVAILABLE_FOCAL_LENGTHS)?.minOrNull()?.toDouble()

    private fun sizes(c: CameraCharacteristics): List<VideoSize> = encoderSizes(c)
        .filter { it.width * 9 == it.height * 16 && it.height in 360..1080 } // 16:9 webcam sizes
        .sortedByDescending { it.width * it.height }
        .map { VideoSize(it.width, it.height) }

    private fun frameRates(c: CameraCharacteristics): List<Int> =
        c.get(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES).orEmpty()
            .map { it.upper }.filter { it in 15..60 }.distinct().sorted()

    companion object {
        fun encoderSizes(c: CameraCharacteristics): List<Size> =
            c.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP)?.getOutputSizes(MediaCodec::class.java)?.toList().orEmpty()
    }
}
