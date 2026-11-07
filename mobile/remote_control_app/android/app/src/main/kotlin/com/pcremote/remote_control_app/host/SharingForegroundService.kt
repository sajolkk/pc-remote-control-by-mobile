package com.pcremote.remote_control_app.host

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import com.pcremote.remote_control_app.MainActivity

/**
 * Keeps the host alive while the app is in the background.
 *
 * Android stops screen capture the moment the capturing app has no foreground service of type
 * `mediaProjection`, and from Android 14 that service may only start *after* the owner has
 * granted capture in the system dialog. The Dart side therefore asks for consent first, starts
 * this service second, and begins capturing third.
 *
 * The notification is the owner's permanent reminder that the phone is being shared, and its
 * action stops sharing without opening the app.
 */
class SharingForegroundService : Service() {
    companion object {
        private const val CHANNEL_ID = "pcremote_sharing"
        private const val NOTIFICATION_ID = 4710
        const val ACTION_STOP = "com.pcremote.remote_control_app.host.STOP_SHARING"

        /** Set by the plugin so the Stop action can reach the Dart side. */
        var onStopRequested: (() -> Unit)? = null

        /**
         * Called once `startForeground` has run. The Dart side waits for this before it begins the
         * capture: `getMediaProjection` throws unless the service is already in the foreground, and
         * `startForegroundService` returns before the service has had a chance to get there.
         */
        var onForegroundStarted: (() -> Unit)? = null

        fun start(context: Context) {
            val intent = Intent(context, SharingForegroundService::class.java)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                context.startForegroundService(intent)
            } else {
                context.startService(intent)
            }
        }

        fun stop(context: Context) {
            context.stopService(Intent(context, SharingForegroundService::class.java))
        }
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            onStopRequested?.invoke()
            stopSelf()
            return START_NOT_STICKY
        }

        createChannel()
        val notification = buildNotification()

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION)
        } else {
            startForeground(NOTIFICATION_ID, notification)
        }

        onForegroundStarted?.invoke()
        return START_NOT_STICKY
    }

    private fun createChannel() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return

        val manager = getSystemService(NotificationManager::class.java)
        if (manager.getNotificationChannel(CHANNEL_ID) != null) return

        manager.createNotificationChannel(
            NotificationChannel(CHANNEL_ID, "Sharing this phone", NotificationManager.IMPORTANCE_LOW).apply {
                description = "Shown while another device can see and control this phone."
                setShowBadge(false)
            },
        )
    }

    private fun buildNotification(): Notification {
        val flags = PendingIntent.FLAG_UPDATE_CURRENT or
            (if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) PendingIntent.FLAG_IMMUTABLE else 0)

        val open = PendingIntent.getActivity(
            this,
            0,
            Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP),
            flags,
        )

        val stop = PendingIntent.getService(
            this,
            1,
            Intent(this, SharingForegroundService::class.java).setAction(ACTION_STOP),
            flags,
        )

        @Suppress("DEPRECATION")
        val builder = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            Notification.Builder(this, CHANNEL_ID)
        } else {
            Notification.Builder(this)
        }

        return builder
            .setContentTitle("This phone is being shared")
            .setContentText("Another device can see the screen and control it. Tap Stop to end.")
            .setSmallIcon(android.R.drawable.ic_menu_share)
            .setOngoing(true)
            .setContentIntent(open)
            .addAction(Notification.Action.Builder(null, "Stop sharing", stop).build())
            .build()
    }
}
