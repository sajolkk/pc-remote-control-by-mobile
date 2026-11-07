package com.pcremote.remote_control_app

import com.pcremote.remote_control_app.host.HostPlugin
import io.flutter.embedding.android.FlutterActivity
import io.flutter.embedding.engine.FlutterEngine

class MainActivity : FlutterActivity() {
    private var host: HostPlugin? = null

    override fun configureFlutterEngine(flutterEngine: FlutterEngine) {
        super.configureFlutterEngine(flutterEngine)
        host = HostPlugin(this, flutterEngine.dartExecutor.binaryMessenger)
    }

    override fun cleanUpFlutterEngine(flutterEngine: FlutterEngine) {
        host?.dispose()
        host = null
        super.cleanUpFlutterEngine(flutterEngine)
    }
}
