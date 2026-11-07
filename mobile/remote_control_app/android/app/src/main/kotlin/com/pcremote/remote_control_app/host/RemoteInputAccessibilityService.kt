package com.pcremote.remote_control_app.host

import android.accessibilityservice.AccessibilityService
import android.accessibilityservice.GestureDescription
import android.content.Context
import android.graphics.Bitmap
import android.graphics.Path
import android.media.AudioManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.util.Log
import android.view.Display
import android.view.accessibility.AccessibilityEvent
import android.view.accessibility.AccessibilityNodeInfo
import android.view.accessibility.AccessibilityWindowInfo
import java.io.ByteArrayOutputStream
import java.util.ArrayDeque

/**
 * Performs a viewer's input on this phone, and reports which app is in front.
 *
 * This is the only way an ordinary app can touch another app's screen on Android: no root, no
 * device-owner provisioning, just the accessibility service the owner switches on once in
 * Settings. Everything here is a gesture or a node action; nothing is a key event, because an
 * accessibility service cannot inject those.
 *
 * **Foreground tracking** reads the window list rather than trusting each event's package. When
 * the keyboard opens, the IME's window appears and a window-state event names the keyboard's
 * package; the app behind it is still what is on the screen, and the block list must still see it.
 * Only `TYPE_APPLICATION` windows count.
 *
 * **Drags** are continued strokes: the finger goes down with `willContinue`, each move continues
 * the stroke from where the last segment ended, and the release ends it. Android cancels a gesture
 * in progress when another is dispatched, so segments are queued and dispatched one at a time from
 * the completion callback; segments that pile up while one is in flight are merged into one.
 */
class RemoteInputAccessibilityService : AccessibilityService() {
    companion object {
        private const val TAG = "PCRemoteHost"

        /** The running service, or null while it is off. */
        @Volatile
        var instance: RemoteInputAccessibilityService? = null
            private set

        /** The package of the application window in front, as last seen. */
        @Volatile
        var foregroundPackage: String? = null
            private set

        /** Told of foreground and connection changes, on the main thread. */
        var listener: Listener? = null

        private val mainHandler = Handler(Looper.getMainLooper())

        fun isEnabled(context: Context): Boolean {
            if (instance != null) return true

            val enabled = android.provider.Settings.Secure.getString(
                context.contentResolver,
                android.provider.Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES,
            ) ?: return false

            val component = "${context.packageName}/${RemoteInputAccessibilityService::class.java.name}"
            return enabled.split(':').any { it.equals(component, ignoreCase = true) }
        }
    }

    interface Listener {
        fun onForegroundChanged(packageName: String?)
        fun onServiceStateChanged(connected: Boolean)
    }

    // --- Lifecycle ---

    override fun onServiceConnected() {
        super.onServiceConnected()
        instance = this
        Log.i(TAG, "Accessibility service connected.")
        updateForeground()
        mainHandler.post { listener?.onServiceStateChanged(true) }
    }

    override fun onUnbind(intent: android.content.Intent?): Boolean {
        instance = null
        Log.i(TAG, "Accessibility service disconnected.")
        mainHandler.post { listener?.onServiceStateChanged(false) }
        return super.onUnbind(intent)
    }

    override fun onInterrupt() {}

    override fun onAccessibilityEvent(event: AccessibilityEvent?) {
        if (event == null) return
        when (event.eventType) {
            AccessibilityEvent.TYPE_WINDOW_STATE_CHANGED,
            AccessibilityEvent.TYPE_WINDOWS_CHANGED,
            -> updateForeground(event.packageName?.toString())
        }
    }

    private fun updateForeground(hint: String? = null) {
        var found: String? = null

        try {
            val windows = windows
            // The active application window first, then any application window with focus.
            found = windows.firstOrNull { it.type == AccessibilityWindowInfo.TYPE_APPLICATION && it.isActive }
                ?.root?.packageName?.toString()
                ?: windows.firstOrNull { it.type == AccessibilityWindowInfo.TYPE_APPLICATION && it.isFocused }
                    ?.root?.packageName?.toString()
        } catch (e: Exception) {
            Log.d(TAG, "Could not read the window list.", e)
        }

        // Without a window list (older phones, or none available yet) the event's own package is
        // the best there is, unless it is a keyboard or the system UI drawing over the app.
        if (found == null && hint != null && !hint.startsWith("com.android.systemui") &&
            !hint.contains("inputmethod", ignoreCase = true)
        ) {
            found = hint
        }

        if (found == null) return
        if (found == foregroundPackage) return

        foregroundPackage = found
        mainHandler.post { listener?.onForegroundChanged(found) }
    }

    // --- Taps and swipes ---

    fun tap(x: Float, y: Float, durationMs: Long): Boolean {
        val path = Path().apply { moveTo(x, y) }
        return dispatchSingle(GestureDescription.StrokeDescription(path, 0, durationMs.coerceAtLeast(1)))
    }

    fun swipe(x1: Float, y1: Float, x2: Float, y2: Float, durationMs: Long): Boolean {
        val path = Path().apply {
            moveTo(x1, y1)
            lineTo(x2, y2)
        }
        return dispatchSingle(GestureDescription.StrokeDescription(path, 0, durationMs.coerceAtLeast(1)))
    }

    private fun dispatchSingle(stroke: GestureDescription.StrokeDescription): Boolean {
        endDragIfAny()
        val gesture = GestureDescription.Builder().addStroke(stroke).build()
        return dispatchGesture(gesture, null, null)
    }

    // --- Continued drags ---

    private var dragStroke: GestureDescription.StrokeDescription? = null
    private var dragLast: Pair<Float, Float>? = null
    private var dragStart: Pair<Float, Float>? = null
    private var dragInFlight = false
    private val dragQueue = ArrayDeque<Pair<Float, Float>>()
    private var dragEndRequested = false

    fun dragStart(x: Float, y: Float): Boolean {
        endDragIfAny()
        dragStart = x to y
        dragLast = x to y
        dragQueue.clear()
        dragEndRequested = false

        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) {
            // Continued strokes need API 26; the drag is performed as one swipe on release.
            return true
        }

        val path = Path().apply { moveTo(x, y) }
        val stroke = GestureDescription.StrokeDescription(path, 0, 1, true)
        dragStroke = stroke
        dragInFlight = true
        return dispatchGesture(GestureDescription.Builder().addStroke(stroke).build(), dragCallback, null)
    }

    fun dragMove(x2: Float, y2: Float): Boolean {
        if (dragStart == null) return false

        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) {
            dragLast = x2 to y2
            return true
        }

        // Merge: while a segment is in flight, only the newest target matters.
        dragQueue.clear()
        dragQueue.add(x2 to y2)
        pumpDrag()
        return true
    }

    fun dragEnd(): Boolean {
        val start = dragStart ?: return false

        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) {
            val last = dragLast ?: start
            dragStart = null
            return swipe(start.first, start.second, last.first, last.second, 200)
        }

        dragEndRequested = true
        pumpDrag()
        return true
    }

    private fun endDragIfAny() {
        if (dragStart != null && Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            dragQueue.clear()
            dragEndRequested = true
            pumpDrag()
        }
        dragStart = null
    }

    private fun pumpDrag() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        if (dragInFlight) return
        val previous = dragStroke ?: return
        val from = dragLast ?: return

        val target = dragQueue.pollFirst()
        if (target != null) {
            val path = Path().apply {
                moveTo(from.first, from.second)
                lineTo(target.first, target.second)
            }
            val next = previous.continueStroke(path, 0, 16, true)
            dragStroke = next
            dragLast = target
            dragInFlight = true
            dispatchGesture(GestureDescription.Builder().addStroke(next).build(), dragCallback, null)
            return
        }

        if (dragEndRequested) {
            val path = Path().apply { moveTo(from.first, from.second) }
            val last = previous.continueStroke(path, 0, 1, false)
            dragStroke = null
            dragStart = null
            dragEndRequested = false
            dragInFlight = true
            dispatchGesture(GestureDescription.Builder().addStroke(last).build(), dragCallback, null)
        }
    }

    private val dragCallback = object : GestureResultCallback() {
        override fun onCompleted(gestureDescription: GestureDescription?) {
            dragInFlight = false
            pumpDrag()
        }

        override fun onCancelled(gestureDescription: GestureDescription?) {
            // Something else took the touch (the owner's own finger, say). The drag is over.
            dragInFlight = false
            dragStroke = null
            dragStart = null
            dragQueue.clear()
            dragEndRequested = false
        }
    }

    // --- Text and keys ---

    private fun focusedEditable(): AccessibilityNodeInfo? {
        val root = rootInActiveWindow ?: return null
        val node = root.findFocus(AccessibilityNodeInfo.FOCUS_INPUT) ?: return null
        return if (node.isEditable) node else null
    }

    private fun selectionOf(node: AccessibilityNodeInfo, length: Int): Pair<Int, Int> {
        var start = node.textSelectionStart
        var end = node.textSelectionEnd
        if (start < 0 || start > length) start = length
        if (end < 0 || end > length) end = length
        if (end < start) { val t = start; start = end; end = t }
        return start to end
    }

    private fun setText(node: AccessibilityNodeInfo, text: String, cursor: Int): Boolean {
        val args = Bundle().apply {
            putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE, text)
        }
        val ok = node.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT, args)
        if (ok) {
            val sel = Bundle().apply {
                putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_START_INT, cursor)
                putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_END_INT, cursor)
            }
            node.performAction(AccessibilityNodeInfo.ACTION_SET_SELECTION, sel)
        }
        return ok
    }

    /** Inserts [text] at the cursor of the focused field. True when there was one. */
    fun typeText(text: String): Boolean {
        val node = focusedEditable() ?: return true // Nothing to type into: a no-op, as on a PC.

        if (text == "\n") return enter(node)

        val current = node.text?.toString() ?: ""
        val (start, end) = selectionOf(node, current.length)
        val next = current.substring(0, start) + text + current.substring(end)
        return setText(node, next, start + text.length)
    }

    private fun enter(node: AccessibilityNodeInfo): Boolean {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R &&
            node.actionList.any { it.id == AccessibilityNodeInfo.AccessibilityAction.ACTION_IME_ENTER.id }
        ) {
            return node.performAction(AccessibilityNodeInfo.AccessibilityAction.ACTION_IME_ENTER.id)
        }

        if (node.isMultiLine) {
            val current = node.text?.toString() ?: ""
            val (start, end) = selectionOf(node, current.length)
            return setText(node, current.substring(0, start) + "\n" + current.substring(end), start + 1)
        }

        return true
    }

    fun keyAction(action: String): Boolean = when (action) {
        "back" -> performGlobalAction(GLOBAL_ACTION_BACK)
        "home" -> performGlobalAction(GLOBAL_ACTION_HOME)
        "recents" -> performGlobalAction(GLOBAL_ACTION_RECENTS)
        "deleteBackward" -> deleteAtCursor(backward = true)
        "deleteForward" -> deleteAtCursor(backward = false)
        "enter" -> focusedEditable()?.let { enter(it) } ?: true
        "tab" -> true
        "moveLeft" -> moveCursor(-1)
        "moveRight" -> moveCursor(1)
        "moveUp" -> moveByLine(forward = false)
        "moveDown" -> moveByLine(forward = true)
        "lineStart" -> moveCursorTo(0)
        "lineEnd" -> moveCursorTo(Int.MAX_VALUE)
        "volumeUp" -> adjustVolume(AudioManager.ADJUST_RAISE)
        "volumeDown" -> adjustVolume(AudioManager.ADJUST_LOWER)
        "volumeMute" -> adjustVolume(AudioManager.ADJUST_TOGGLE_MUTE)
        else -> false
    }

    private fun deleteAtCursor(backward: Boolean): Boolean {
        val node = focusedEditable() ?: return true
        val current = node.text?.toString() ?: ""
        var (start, end) = selectionOf(node, current.length)

        if (start == end) {
            if (backward) {
                if (start == 0) return true
                start = if (start >= 2 && Character.isLowSurrogate(current[start - 1])) start - 2 else start - 1
            } else {
                if (end >= current.length) return true
                end = if (end + 1 < current.length && Character.isHighSurrogate(current[end])) end + 2 else end + 1
            }
        }

        return setText(node, current.substring(0, start) + current.substring(end), start)
    }

    private fun moveCursor(delta: Int): Boolean {
        val node = focusedEditable() ?: return true
        val length = node.text?.length ?: 0
        val (start, end) = selectionOf(node, length)
        val position = (if (delta < 0) start else end) + delta
        return moveCursorTo(position.coerceIn(0, length), node)
    }

    private fun moveCursorTo(position: Int, target: AccessibilityNodeInfo? = null): Boolean {
        val node = target ?: focusedEditable() ?: return true
        val length = node.text?.length ?: 0
        val cursor = position.coerceIn(0, length)
        val args = Bundle().apply {
            putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_START_INT, cursor)
            putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_END_INT, cursor)
        }
        return node.performAction(AccessibilityNodeInfo.ACTION_SET_SELECTION, args)
    }

    private fun moveByLine(forward: Boolean): Boolean {
        val node = focusedEditable() ?: return true
        val args = Bundle().apply {
            putInt(
                AccessibilityNodeInfo.ACTION_ARGUMENT_MOVEMENT_GRANULARITY_INT,
                AccessibilityNodeInfo.MOVEMENT_GRANULARITY_LINE,
            )
            putBoolean(AccessibilityNodeInfo.ACTION_ARGUMENT_EXTEND_SELECTION_BOOLEAN, false)
        }
        val action = if (forward) {
            AccessibilityNodeInfo.ACTION_NEXT_AT_MOVEMENT_GRANULARITY
        } else {
            AccessibilityNodeInfo.ACTION_PREVIOUS_AT_MOVEMENT_GRANULARITY
        }
        return node.performAction(action, args)
    }

    private fun adjustVolume(direction: Int): Boolean {
        val audio = getSystemService(Context.AUDIO_SERVICE) as AudioManager
        audio.adjustStreamVolume(AudioManager.STREAM_MUSIC, direction, AudioManager.FLAG_SHOW_UI)
        return true
    }

    // --- Navigation ---

    fun navAction(action: String): Boolean = when (action) {
        "back" -> performGlobalAction(GLOBAL_ACTION_BACK)
        "home" -> performGlobalAction(GLOBAL_ACTION_HOME)
        "recents" -> performGlobalAction(GLOBAL_ACTION_RECENTS)
        "notifications" -> performGlobalAction(GLOBAL_ACTION_NOTIFICATIONS)
        "lock" -> if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            performGlobalAction(GLOBAL_ACTION_LOCK_SCREEN)
        } else {
            false
        }
        else -> false
    }

    // --- Screenshots ---

    /**
     * A JPEG of the screen no wider than [maxWidth], delivered on the main thread, or null when
     * this Android version cannot take one (before 11) or the system refused.
     */
    fun screenshot(maxWidth: Int, onResult: (ByteArray?) -> Unit) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.R) {
            onResult(null)
            return
        }

        takeScreenshot(
            Display.DEFAULT_DISPLAY,
            mainExecutor,
            object : TakeScreenshotCallback {
                override fun onSuccess(screenshot: ScreenshotResult) {
                    var bitmap: Bitmap? = null
                    try {
                        val hardware = Bitmap.wrapHardwareBuffer(screenshot.hardwareBuffer, screenshot.colorSpace)
                        screenshot.hardwareBuffer.close()
                        if (hardware == null) {
                            onResult(null)
                            return
                        }

                        var software = hardware.copy(Bitmap.Config.ARGB_8888, false)
                        hardware.recycle()

                        if (software.width > maxWidth) {
                            val height = (software.height.toLong() * maxWidth / software.width).toInt().coerceAtLeast(1)
                            val scaled = Bitmap.createScaledBitmap(software, maxWidth, height, true)
                            software.recycle()
                            software = scaled
                        }
                        bitmap = software

                        val out = ByteArrayOutputStream()
                        software.compress(Bitmap.CompressFormat.JPEG, 75, out)
                        onResult(out.toByteArray())
                    } catch (e: Exception) {
                        Log.w(TAG, "Screenshot failed.", e)
                        onResult(null)
                    } finally {
                        bitmap?.recycle()
                    }
                }

                override fun onFailure(errorCode: Int) {
                    Log.w(TAG, "Screenshot refused: $errorCode")
                    onResult(null)
                }
            },
        )
    }
}
