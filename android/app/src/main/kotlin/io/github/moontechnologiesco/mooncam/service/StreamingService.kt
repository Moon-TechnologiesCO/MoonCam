package io.github.moontechnologiesco.mooncam.service

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat
import androidx.lifecycle.LifecycleService
import androidx.lifecycle.lifecycleScope
import io.github.moontechnologiesco.mooncam.MainActivity
import io.github.moontechnologiesco.mooncam.R
import io.github.moontechnologiesco.mooncam.StreamingState
import io.github.moontechnologiesco.mooncam.StreamingStatus
import io.github.moontechnologiesco.mooncam.link.LinkSession
import io.github.moontechnologiesco.mooncam.link.LinkTarget
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch

/**
 * Keeps the camera streaming while the user switches to other apps. Android only allows background camera use
 * from a foreground service of type "camera", which also guarantees a visible notification with a Stop action
 * for as long as the camera is in use.
 */
class StreamingService : LifecycleService() {
    private var link: Job? = null
    private var session: LinkSession? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        super.onStartCommand(intent, flags, startId)
        when (intent?.action) {
            ACTION_STOP -> {
                stopStreaming()
                return START_NOT_STICKY
            }
            ACTION_CONFIRM_PAIRING -> {
                session?.let { lifecycleScope.launch(Dispatchers.IO) { it.confirmPairing() } } ?: stopSelf()
                return START_NOT_STICKY
            }
            ACTION_UNPAIR -> {
                val fingerprint = intent.getStringExtra(EXTRA_FINGERPRINT)
                session?.takeIf { it.wifiFingerprint == fingerprint }?.let { lifecycleScope.launch(Dispatchers.IO) { it.unpair() } }
                    ?: run { if (link == null) stopSelf() }
                return START_NOT_STICKY
            }
        }
        val target = intent?.let { targetFrom(it) }
        if (target == null) {
            if (link == null) stopSelf()
            return START_NOT_STICKY
        }
        startInForeground()
        link?.cancel()
        val next = LinkSession(applicationContext, target).also { session = it }
        link = lifecycleScope.launch {
            next.runWithReconnect()
            // Refused or failed for good: nothing streams, so the camera notification goes (the screen says why).
            if (session === next) endService()
        }
        return START_NOT_STICKY // never restart the camera on our own after the system killed us
    }

    private fun startInForeground() {
        val manager = getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL_ID, getString(R.string.notification_channel), NotificationManager.IMPORTANCE_LOW),
        )
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        val stop = PendingIntent.getService(
            this, 1, Intent(this, StreamingService::class.java).setAction(ACTION_STOP), PendingIntent.FLAG_IMMUTABLE,
        )
        val notification = NotificationCompat.Builder(this, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(getString(R.string.notification_title))
            .setContentText(getString(R.string.notification_text))
            .setContentIntent(open)
            .addAction(0, getString(R.string.stop), stop)
            .setOngoing(true)
            .setCategory(NotificationCompat.CATEGORY_SERVICE)
            .build()
        ServiceCompat.startForeground(this, NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_CAMERA)
    }

    private fun stopStreaming() {
        link?.cancel()
        StreamingStatus.update(StreamingState.Idle)
        endService()
    }

    private fun endService() {
        link = null
        session = null
        ServiceCompat.stopForeground(this, ServiceCompat.STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    override fun onDestroy() {
        link?.cancel()
        StreamingStatus.settle()
        super.onDestroy()
    }

    companion object {
        private const val ACTION_STOP = "io.github.moontechnologiesco.mooncam.action.STOP"
        private const val ACTION_CONFIRM_PAIRING = "io.github.moontechnologiesco.mooncam.action.CONFIRM_PAIRING"
        private const val ACTION_UNPAIR = "io.github.moontechnologiesco.mooncam.action.UNPAIR"
        private const val EXTRA_TOKEN = "token"
        private const val EXTRA_PORT = "port"
        private const val EXTRA_HOST = "host"
        private const val EXTRA_COMPUTER = "computer"
        private const val EXTRA_FINGERPRINT = "fingerprint"
        private const val CHANNEL_ID = "streaming"
        private const val NOTIFICATION_ID = 1
        private val fingerprintPattern = Regex("^[0-9a-f]{64}$")

        /** Starts streaming to a computer the user chose (Wi-Fi) or approved (USB). */
        fun start(context: Context, target: LinkTarget) {
            val intent = Intent(context, StreamingService::class.java).putExtra(EXTRA_PORT, target.port())
            when (target) {
                is LinkTarget.Usb -> intent.putExtra(EXTRA_TOKEN, target.token)
                is LinkTarget.WiFi -> intent
                    .putExtra(EXTRA_HOST, target.host)
                    .putExtra(EXTRA_COMPUTER, target.computer)
                    .putExtra(EXTRA_FINGERPRINT, target.desktopFingerprintHex)
            }
            ContextCompat.startForegroundService(context, intent)
        }

        /** The person saw the same pairing code on both screens. */
        fun confirmPairing(context: Context) {
            context.startService(Intent(context, StreamingService::class.java).setAction(ACTION_CONFIRM_PAIRING))
        }

        /** The person unpaired a computer: if the phone is linked to it right now, the computer is told too. */
        fun unpair(context: Context, fingerprintHex: String) {
            if (StreamingStatus.wifiComputer.value != fingerprintHex) return
            context.startService(
                Intent(context, StreamingService::class.java).setAction(ACTION_UNPAIR).putExtra(EXTRA_FINGERPRINT, fingerprintHex),
            )
        }

        fun stop(context: Context) {
            context.startService(Intent(context, StreamingService::class.java).setAction(ACTION_STOP))
        }

        private fun LinkTarget.port() = when (this) {
            is LinkTarget.Usb -> port
            is LinkTarget.WiFi -> port
        }

        /** The service is not exported, but its extras are still checked before anything connects. */
        private fun targetFrom(intent: Intent): LinkTarget? {
            val port = intent.getIntExtra(EXTRA_PORT, -1).takeIf { it in 1024..65535 } ?: return null
            intent.getStringExtra(EXTRA_TOKEN)?.let { return LinkTarget.Usb(port, it) }
            val host = intent.getStringExtra(EXTRA_HOST)?.takeIf { it.isNotBlank() } ?: return null
            val fingerprint = intent.getStringExtra(EXTRA_FINGERPRINT)
            if (fingerprint != null && !fingerprintPattern.matches(fingerprint)) return null
            return LinkTarget.WiFi(intent.getStringExtra(EXTRA_COMPUTER) ?: host, host, port, fingerprint)
        }
    }
}
