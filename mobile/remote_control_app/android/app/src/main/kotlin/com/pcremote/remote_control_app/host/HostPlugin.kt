package com.pcremote.remote_control_app.host

import android.Manifest
import android.app.Activity
import android.app.KeyguardManager
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Point
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.view.Surface
import android.view.WindowManager
import io.flutter.plugin.common.BinaryMessenger
import io.flutter.plugin.common.EventChannel
import io.flutter.plugin.common.MethodCall
import io.flutter.plugin.common.MethodChannel

/**
 * The method channel behind `AndroidHostPlatform` in Dart: everything host mode needs from Android.
 */
class HostPlugin(private val activity: Activity, messenger: BinaryMessenger) :
    MethodChannel.MethodCallHandler, EventChannel.StreamHandler, RemoteInputAccessibilityService.Listener {

    companion object {
        private const val CHANNEL = "com.pcremote/host"
        private const val EVENTS = "com.pcremote/host/events"
        private const val PREFS = "pcremote_host"
        private const val KEY_BLOCKED = "blocked_packages"
        private const val NOTIFICATION_PERMISSION_REQUEST = 4711
    }

    private val methods = MethodChannel(messenger, CHANNEL).also { it.setMethodCallHandler(this) }
    private val events = EventChannel(messenger, EVENTS).also { it.setStreamHandler(this) }
    private val main = Handler(Looper.getMainLooper())
    private val apps = InstalledApps(activity)

    private var sink: EventChannel.EventSink? = null
    private var tls: TlsControlServer? = null

    init {
        RemoteInputAccessibilityService.listener = this
        SharingForegroundService.onStopRequested = {
            main.post { sink?.success(mapOf("type" to "stopRequested")) }
        }
    }

    fun dispose() {
        tls?.stop()
        tls = null
        RemoteInputAccessibilityService.listener = null
        SharingForegroundService.onStopRequested = null
        SharingForegroundService.onForegroundStarted = null
        methods.setMethodCallHandler(null)
        events.setStreamHandler(null)
    }

    // --- Events to Dart ---

    override fun onListen(arguments: Any?, events: EventChannel.EventSink?) {
        sink = events
        events?.success(mapOf("type" to "foreground", "package" to RemoteInputAccessibilityService.foregroundPackage))
        events?.success(mapOf("type" to "accessibility", "enabled" to RemoteInputAccessibilityService.isEnabled(activity)))
    }

    override fun onCancel(arguments: Any?) {
        sink = null
    }

    override fun onForegroundChanged(packageName: String?) {
        sink?.success(mapOf("type" to "foreground", "package" to packageName))
    }

    override fun onServiceStateChanged(connected: Boolean) {
        sink?.success(mapOf("type" to "accessibility", "enabled" to connected))
    }

    // --- Methods from Dart ---

    override fun onMethodCall(call: MethodCall, result: MethodChannel.Result) {
        try {
            when (call.method) {
                "startTlsServer" -> startTls(call, result)
                "stopTlsServer" -> {
                    tls?.stop()
                    tls = null
                    result.success(null)
                }
                "startForegroundService" -> startForegroundService(result)
                "stopForegroundService" -> {
                    SharingForegroundService.stop(activity)
                    result.success(null)
                }
                "requestNotificationPermission" -> {
                    requestNotificationPermission()
                    result.success(null)
                }
                "openAccessibilitySettings" -> {
                    activity.startActivity(
                        Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
                    )
                    result.success(null)
                }
                "isAccessibilityEnabled" -> result.success(RemoteInputAccessibilityService.isEnabled(activity))
                "foregroundPackage" -> result.success(RemoteInputAccessibilityService.foregroundPackage)
                "isLocked" -> {
                    val keyguard = activity.getSystemService(Context.KEYGUARD_SERVICE) as KeyguardManager
                    result.success(keyguard.isKeyguardLocked)
                }
                "screenSize" -> result.success(screenSize())
                "tap" -> withService(result) {
                    it.tap(call.float("x"), call.float("y"), call.long("durationMs", 40))
                }
                "swipe" -> withService(result) {
                    it.swipe(call.float("x1"), call.float("y1"), call.float("x2"), call.float("y2"), call.long("durationMs", 200))
                }
                "dragStart" -> withService(result) { it.dragStart(call.float("x"), call.float("y")) }
                "dragMove" -> withService(result) { it.dragMove(call.float("x2"), call.float("y2")) }
                "dragEnd" -> withService(result) { it.dragEnd() }
                "typeText" -> withService(result) { it.typeText(call.argument<String>("text") ?: "") }
                "keyAction" -> withService(result) { it.keyAction(call.argument<String>("action") ?: "") }
                "navAction" -> withService(result) { it.navAction(call.argument<String>("action") ?: "") }
                "listApps" -> {
                    val includeIcons = call.argument<Boolean>("includeIcons") ?: false
                    Thread {
                        val listed = apps.list(includeIcons).map {
                            mapOf("package" to it.packageName, "name" to it.name, "iconPng" to it.iconPng)
                        }
                        main.post { result.success(listed) }
                    }.start()
                }
                "launchApp" -> result.success(apps.launch(call.argument<String>("package") ?: ""))
                "screenshot" -> {
                    val service = RemoteInputAccessibilityService.instance
                    if (service == null) {
                        result.error("unavailable", "The accessibility service is off.", null)
                    } else {
                        service.screenshot(call.argument<Int>("maxWidth") ?: 1280) { bytes ->
                            if (bytes == null) result.error("unavailable", "No screenshot.", null) else result.success(bytes)
                        }
                    }
                }
                "getBlockedPackages" -> result.success(blockedPackages().toList())
                "setBlockedPackages" -> {
                    val packages = call.argument<List<String>>("packages") ?: emptyList()
                    prefs().edit().putStringSet(KEY_BLOCKED, packages.toSet()).apply()
                    result.success(null)
                }
                else -> result.notImplemented()
            }
        } catch (e: Exception) {
            result.error("failed", e.message, null)
        }
    }

    /** Answers only once the service is actually in the foreground, or after a short grace period. */
    private fun startForegroundService(result: MethodChannel.Result) {
        var answered = false
        val answer = {
            if (!answered) {
                answered = true
                SharingForegroundService.onForegroundStarted = null
                result.success(null)
            }
        }

        SharingForegroundService.onForegroundStarted = { main.post(answer) }
        SharingForegroundService.start(activity)

        // If the service never reports (an old Android that refused to start it, say) the capture
        // is attempted anyway and fails with a clear error instead of hanging the Start button.
        main.postDelayed(answer, 3_000)
    }

    private fun startTls(call: MethodCall, result: MethodChannel.Result) {
        tls?.stop()

        val server = TlsControlServer(
            certificatePem = call.argument<String>("certPem") ?: "",
            privateKeyPem = call.argument<String>("keyPem") ?: "",
            preferredPort = call.argument<Int>("port") ?: 47800,
            loopbackPort = call.argument<Int>("loopbackPort") ?: 0,
            secret = call.argument<String>("secret") ?: "",
        )

        // Binding is quick but blocking; keep it off the UI thread anyway.
        Thread {
            try {
                val port = server.start()
                tls = server
                main.post { result.success(mapOf("port" to port)) }
            } catch (e: Exception) {
                main.post { result.error("bind_failed", e.message, null) }
            }
        }.start()
    }

    private fun withService(result: MethodChannel.Result, action: (RemoteInputAccessibilityService) -> Boolean) {
        val service = RemoteInputAccessibilityService.instance
        if (service == null) {
            result.success(false)
            return
        }
        result.success(action(service))
    }

    private fun screenSize(): Map<String, Any> {
        val wm = activity.getSystemService(Context.WINDOW_SERVICE) as WindowManager
        val display = @Suppress("DEPRECATION") wm.defaultDisplay
        val size = Point()
        @Suppress("DEPRECATION")
        display.getRealSize(size)

        val rotation = when (display.rotation) {
            Surface.ROTATION_90 -> 90
            Surface.ROTATION_180 -> 180
            Surface.ROTATION_270 -> 270
            else -> 0
        }

        return mapOf("width" to size.x, "height" to size.y, "rotation" to rotation)
    }

    private fun requestNotificationPermission() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU) return
        if (activity.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED) return
        activity.requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), NOTIFICATION_PERMISSION_REQUEST)
    }

    private fun prefs() = activity.getSharedPreferences(PREFS, Context.MODE_PRIVATE)

    private fun blockedPackages(): Set<String> = prefs().getStringSet(KEY_BLOCKED, emptySet()) ?: emptySet()

    private fun MethodCall.float(name: String): Float = (argument<Number>(name) ?: 0).toFloat()

    private fun MethodCall.long(name: String, default: Long): Long = argument<Number>(name)?.toLong() ?: default
}
