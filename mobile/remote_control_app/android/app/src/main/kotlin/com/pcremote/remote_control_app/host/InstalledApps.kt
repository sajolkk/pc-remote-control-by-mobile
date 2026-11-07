package com.pcremote.remote_control_app.host

import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.drawable.BitmapDrawable
import android.graphics.drawable.Drawable
import android.util.Base64
import java.io.ByteArrayOutputStream

/**
 * The apps a viewer may be shown and the owner may block: everything with a launcher icon.
 *
 * Listed through a `<queries>` filter for launcher intents rather than `QUERY_ALL_PACKAGES`, which
 * is the way Android asks launchers to do it and needs no restricted permission. Package names are
 * the ids: opaque enough, and the only thing `launchApp` will accept.
 */
class InstalledApps(private val context: Context) {
    companion object {
        private const val ICON_SIZE = 48
    }

    private val iconCache = HashMap<String, String>()

    data class App(val packageName: String, val name: String, val iconPng: String?)

    fun list(includeIcons: Boolean): List<App> {
        val pm = context.packageManager
        val intent = Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER)

        @Suppress("DEPRECATION")
        val activities = pm.queryIntentActivities(intent, 0)

        return activities
            .asSequence()
            .map { it.activityInfo }
            .filter { it.packageName != context.packageName }
            .distinctBy { it.packageName }
            .map { info ->
                val label = info.loadLabel(pm)?.toString()?.takeIf { it.isNotBlank() } ?: info.packageName
                App(info.packageName, label, if (includeIcons) icon(pm, info.packageName) else null)
            }
            .sortedBy { it.name.lowercase() }
            .toList()
    }

    fun launch(packageName: String): Boolean {
        val intent = context.packageManager.getLaunchIntentForPackage(packageName) ?: return false
        intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_RESET_TASK_IF_NEEDED)
        return try {
            context.startActivity(intent)
            true
        } catch (_: Exception) {
            false
        }
    }

    private fun icon(pm: PackageManager, packageName: String): String? {
        iconCache[packageName]?.let { return it }

        val drawable: Drawable = try {
            pm.getApplicationIcon(packageName)
        } catch (_: PackageManager.NameNotFoundException) {
            return null
        }

        val bitmap = Bitmap.createBitmap(ICON_SIZE, ICON_SIZE, Bitmap.Config.ARGB_8888)
        if (drawable is BitmapDrawable && drawable.bitmap != null) {
            Canvas(bitmap).drawBitmap(
                drawable.bitmap,
                null,
                android.graphics.Rect(0, 0, ICON_SIZE, ICON_SIZE),
                null,
            )
        } else {
            drawable.setBounds(0, 0, ICON_SIZE, ICON_SIZE)
            drawable.draw(Canvas(bitmap))
        }

        val out = ByteArrayOutputStream()
        bitmap.compress(Bitmap.CompressFormat.PNG, 100, out)
        bitmap.recycle()

        val encoded = Base64.encodeToString(out.toByteArray(), Base64.NO_WRAP)
        iconCache[packageName] = encoded
        return encoded
    }
}
