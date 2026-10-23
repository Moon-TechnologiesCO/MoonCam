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
import io.github.moontechnologiesco.mooncam.ConnectRequest
import io.github.moontechnologiesco.mooncam.MainActivity
import io.github.moontechnologiesco.mooncam.R
import io.github.moontechnologiesco.mooncam.StreamingState
import io.github.moontechnologiesco.mooncam.StreamingStatus
import io.github.moontechnologiesco.mooncam.link.LinkSession
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch

/**
 * Keeps the camera streaming while the user switches to other apps. Android only allows background camera use
 * from a foreground service of type "camera", which also guarantees a visible notification with a Stop action
 * for as long as the camera is in use.
 */
class StreamingService : LifecycleService() {
    private var link: Job? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        super.onStartCommand(intent, flags, startId)
        if (intent?.action == ACTION_STOP) {
            stopStreaming()
            return START_NOT_STICKY
        }
        val token = intent?.getStringExtra(EXTRA_TOKEN)
        val port = intent?.getIntExtra(EXTRA_PORT, -1) ?: -1
        if (token == null || port !in 1024..65535) {
            stopSelf()
            return START_NOT_STICKY
        }
        startInForeground()
        link?.cancel()
        link = lifecycleScope.launch { LinkSession(applicationContext, port, token).runWithReconnect() }
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
        link = null
        StreamingStatus.update(StreamingState.Idle)
        ServiceCompat.stopForeground(this, ServiceCompat.STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    override fun onDestroy() {
        link?.cancel()
        StreamingStatus.update(StreamingState.Idle)
        super.onDestroy()
    }

    companion object {
        private const val ACTION_STOP = "io.github.moontechnologiesco.mooncam.action.STOP"
        private const val EXTRA_TOKEN = "token"
        private const val EXTRA_PORT = "port"
        private const val CHANNEL_ID = "streaming"
        private const val NOTIFICATION_ID = 1

        /** Starts streaming for a request the user has approved. */
        fun start(context: Context, request: ConnectRequest) {
            val intent = Intent(context, StreamingService::class.java)
                .putExtra(EXTRA_TOKEN, request.token)
                .putExtra(EXTRA_PORT, request.port)
            ContextCompat.startForegroundService(context, intent)
        }

        fun stop(context: Context) {
            context.startService(Intent(context, StreamingService::class.java).setAction(ACTION_STOP))
        }
    }
}
