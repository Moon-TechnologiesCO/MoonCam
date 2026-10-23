package io.github.moontechnologiesco.mooncam

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import io.github.moontechnologiesco.mooncam.service.StreamingService
import io.github.moontechnologiesco.mooncam.ui.ConsentDialog
import io.github.moontechnologiesco.mooncam.ui.HomeScreen
import io.github.moontechnologiesco.mooncam.ui.MoonCamTheme

class MainActivity : ComponentActivity() {
    private var pendingRequest by mutableStateOf<ConnectRequest?>(null)
    private var approvedRequest: ConnectRequest? = null
    private var hasCameraPermission by mutableStateOf(false)

    private val permissions = registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { result ->
        hasCameraPermission = result[Manifest.permission.CAMERA] == true || cameraGranted()
        approvedRequest?.takeIf { hasCameraPermission }?.let(::startStreaming)
        approvedRequest = null
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        hasCameraPermission = cameraGranted()
        pendingRequest = ConnectRequest.from(intent)
        setContent {
            MoonCamTheme {
                val state by StreamingStatus.state.collectAsStateWithLifecycle()
                HomeScreen(
                    state = state,
                    hasCameraPermission = hasCameraPermission,
                    onRequestPermission = ::requestPermissions,
                    onStop = { StreamingService.stop(this) },
                )
                pendingRequest?.let { request ->
                    ConsentDialog(
                        onAllow = {
                            pendingRequest = null
                            if (cameraGranted()) startStreaming(request) else requestPermissions(request)
                        },
                        onDeny = { pendingRequest = null },
                    )
                }
            }
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        ConnectRequest.from(intent)?.let { pendingRequest = it }
    }

    override fun onResume() {
        super.onResume()
        hasCameraPermission = cameraGranted()
    }

    private fun startStreaming(request: ConnectRequest) = StreamingService.start(this, request)

    private fun requestPermissions(afterGrant: ConnectRequest? = null) {
        approvedRequest = afterGrant
        val wanted = buildList {
            add(Manifest.permission.CAMERA)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) add(Manifest.permission.POST_NOTIFICATIONS)
        }
        permissions.launch(wanted.toTypedArray())
    }

    private fun cameraGranted() =
        ContextCompat.checkSelfPermission(this, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED
}
