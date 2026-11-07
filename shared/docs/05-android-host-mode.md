# Android host mode: controlling one phone from another

The Android app can be the thing being controlled. Phone A opens **Share this phone**, phone B
scans its QR code exactly as it would a PC's, A approves, and B sees A's screen and taps, swipes
and types on it. Nothing changes on the PC side, and B's app treats A like a PC that happens to be
tall: the same pairing, the same connection, the same commands, with a bar of phone buttons added.

This document covers what it does, what Android requires for it, the owner's block list, where
the design departs from the PC agent and why, and what it cannot do.

---

## 1. Using it

### On the phone to be controlled (the host)

1. **Settings → Share this phone**, or the share icon on the Devices tab.
2. **Turn on remote control** once: the card explains what the accessibility service does, then
   opens Settings → Accessibility, where **PC Remote: remote control** is switched on. Without it
   viewers can watch but not touch. Android asks for this once; it stays on until switched off.
3. **Start sharing.** Android shows its own screen-capture consent dialog (every time; it cannot be
   remembered). Choose *Start now* / *Entire screen*. A notification, *This phone is being shared*,
   stays up with a **Stop sharing** action.
4. A QR code appears, with the phone's address, port and certificate fingerprint. The code works
   once and expires after five minutes; **New code** makes another.
5. When another phone scans it, a dialog shows that phone's name, address and fingerprint, with an
   optional tick for *also let it open apps and lock this phone*. **Allow** or **Decline**.
6. **Paired devices** on the same screen lists everyone approved so far, with per-device switches
   (see the screen, touch and type, open apps, lock), **Disconnect** (kept but refused until allowed
   again) and **Forget**. Changes apply to a live connection at once.
7. **Stop sharing** ends everything: the capture, the listener, every connection.

### On the phone doing the controlling (the viewer)

Exactly as for a PC: **Devices → Pair a PC → Scan QR code**, or tap the phone under *Found on this
network* and compare the six-digit code. The paired phone appears with a phone icon. Opening its
**Screen** shows the picture upright, with a bar underneath: volume down, **Back**, **Home**,
**Recents**, notifications, lock, volume up.

Touch defaults to **Phone touch**: a tap taps, a moving finger drags from the moment it moves (so a
swipe is a swipe and scrolling works as on the phone itself), and holding still then lifting is a
long press. Two fingers scroll; a pinch zooms the picture. The keyboard button types; Backspace,
Enter, arrows and Escape (= Back) do what they say. The Touch and Trackpad styles are still offered
from the toolbar.

**Manage → Apps** lists the host's apps with icons. Apps the owner has blocked are marked *Blocked
by the phone's owner* and cannot be launched.

### The block list

**Settings → Blocked apps** (also the block icon on the Share screen) lists every app with a launcher
icon and a switch each. While a blocked app is in front on the host:

- viewers get a placeholder (*Blocked by the owner*) instead of the picture;
- every touch, key and navigation command from a viewer is refused with `E_BLOCKED_BY_OWNER` before
  it reaches the accessibility service, and a drag in progress is ended;
- screenshots are refused;
- `app.launch` of anything is refused.

Blocked apps also cannot be launched remotely at any time, and are marked in the app list. The list
can be changed while sharing; blocking the app that is in front takes effect on the next frame.

---

## 2. What Android requires, and how the app meets it

| Need | How |
|---|---|
| Capture the screen | `MediaProjection`, through `flutter_webrtc`'s `getDisplayMedia`. The owner consents in the system dialog each time sharing starts; Android 14 makes each consent single-use. |
| Keep capturing in the background | A foreground service of type `mediaProjection` (`SharingForegroundService`). Android 10+ refuses the projection without one; Android 14+ allows that service to start only *after* consent. So the order is: consent → service → capture, and the Dart side waits for the service to report it is in the foreground before starting the capture. |
| Perform touches and typing | An `AccessibilityService` (`RemoteInputAccessibilityService`) with `canPerformGestures` and `canRetrieveWindowContent`. Taps and swipes are `GestureDescription` strokes; drags are continued strokes; text goes into the focused field with `ACTION_SET_TEXT` at the cursor; Back/Home/Recents/notifications/lock are global actions; volume goes through `AudioManager`. An accessibility service cannot inject key events, so there are none. |
| Know which app is in front | The same service reads the window list on every window change and reports the active `TYPE_APPLICATION` window's package. The window list, not the event's package: when the keyboard opens, the IME's window would otherwise hide the app behind it from the block list. |
| List apps | A `<queries>` filter for `MAIN`/`LAUNCHER` intents, the documented way for launchers, instead of `QUERY_ALL_PACKAGES`. |
| Screenshots | `AccessibilityService.takeScreenshot` (Android 11+). Older versions return `E_NOT_SUPPORTED`. |
| Notification | `POST_NOTIFICATIONS` is requested on Android 13+ when sharing starts, so the *This phone is being shared* notification shows. |

Permissions added to the manifest: `FOREGROUND_SERVICE`, `FOREGROUND_SERVICE_MEDIA_PROJECTION`,
`POST_NOTIFICATIONS`, and `BIND_ACCESSIBILITY_SERVICE` on the service itself (held by the system).

### Google Play's accessibility declaration

Play requires an app that uses an accessibility service for anything other than helping people with
disabilities to declare it. For this app:

- **Declaration (Play Console → App content → Accessibility API usage):** the service is a core
  feature of a remote-control app. It performs touches and typing that the owner's own paired phone
  sends while the owner is sharing the screen, reports the foreground app so the owner's block list
  can hide apps, and reads the focused text field so typing lands at the cursor. It is used only
  while the owner has started sharing and only for devices the owner approved on this phone.
- **Prominent disclosure:** before the app sends the owner to Settings, a dialog states what the
  service does and does not do (`_AccessibilityCard._disclose` in `share_phone_screen.dart`).
- **Service description** shown by Android: `accessibility_service_description` in `strings.xml`.
- **Data:** the service never stores or transmits screen text; the only thing that leaves the phone
  is the encrypted video and command responses to the approved viewer.

---

## 3. Where the host departs from the PC, and why

The intent was to mirror the PC agent command for command, and the host does: the same framing,
envelopes, replay guard, rate limiter, session tokens, pairing gates, command catalogue with stages
and permissions, discovery beacon and QR payload. Three things had to differ.

### 3.1 The TLS listener is native (Kotlin), not Dart

The protocol identifies a viewer by the fingerprint of the self-signed client certificate it
presents in the TLS handshake. Dart's `SecureServerSocket` has no server-side equivalent of
`onBadCertificate`: it accepts a client certificate only if it chains to a trusted issuer, and a
self-signed leaf without `keyCertSign` cannot be made one (tested: BoringSSL fails with "unable to
get local issuer certificate" even with the leaf in the trust store). So Dart can never complete the
handshake, let alone read the fingerprint.

`TlsControlServer.kt` therefore owns the socket: an `SSLServerSocket` requiring client authentication,
TLS 1.2 as the floor, the PC's cipher-suite allowlist, and a trust manager that accepts any
time-valid certificate because, as on the PC, identity is decided one layer up by matching the
fingerprint against a pairing record. For each authenticated viewer it opens a loopback connection
to the Dart side, writes one header line (`PCRH1 <secret> <fingerprint> <address> <tls>`) and pumps
the decrypted bytes both ways. The secret is random per run and known only to this process, so
another app on the phone cannot connect to the loopback port and forge a viewer. Everything from the
first frame onwards is Dart (`lib/host/`).

### 3.2 The blocked-app placeholder is the disabled track

The host does not draw a placeholder frame. While a blocked app is in front the single shared video
track is disabled, which makes libwebrtc send black frames to every viewer at once, so nothing of the
blocked app leaves the phone. Each viewer is told why with a `media.quality` event in the `paused`
state and the new reason `blocked_by_owner`, which the viewer draws as the placeholder with its own
words, the same way it draws a locked PC. A frame or two of the blocked app may be captured in the
tens of milliseconds between the window change and the track being disabled; the capture itself
cannot be paused from the app.

### 3.3 The host has its own certificate

The certificate a phone presents to PCs was issued for client authentication only, and its
fingerprint is pinned by every PC the phone is paired with. The host presents a second certificate
(`HostIdentityStore`), issued with both `serverAuth` and `clientAuth`, under the same device id.
Replacing the first would have meant pairing with every PC again.

### Protocol additions (all optional fields; the PC ignores or omits them)

| Where | Addition |
|---|---|
| QR payload | `k: "phone"` |
| Discovery beacon | `kind: "phone"` |
| `hello`, `pair.request`, `device.info` results | `deviceKind: "phone"` |
| Capabilities | `phone.nav` |
| Command | `phone.nav {action: back\|home\|recents\|notifications\|lock}` (`ControlInput`) |
| Error code | `E_BLOCKED_BY_OWNER` |
| `media.quality` pause reason | `blocked_by_owner` |
| `app.list` entries | `blocked: true` |
| `system.state` | `blockedByOwner: true` |

A phone host advertises only what it has: `screen.capture`, `encode.h264.hardware`, `input`,
`input.mouse`, `input.keyboard`, `phone.nav`, `volume` (the last five only while the accessibility
service is on), `apps` and `session.agent`. No files, windows, browsers, clipboard or power beyond
`system.lock`.

---

## 4. Limits

- **Apps that forbid capture are black.** Banking apps, password managers, private browsing and
  anything setting `FLAG_SECURE` are shown black by Android itself. Nothing can change that.
- **No power off, restart or sleep.** An ordinary app cannot do these. Lock is the only power action.
- **Lock works only while the phone is unlocked, and the lock screen cannot be unlocked remotely.**
  The accessibility service's gestures do not reach the keyguard, and the app never handles the
  owner's PIN. A viewer of a locked phone sees the lock screen and can do nothing until the owner
  unlocks it. The viewer's banner says so.
- **Typing goes into the focused field.** If no field has focus, text is dropped silently, as keys
  typed at a PC with nothing focused are. Tab does nothing.
- **Right click is a long press**; the middle button does nothing.
- **Consent every time.** Android does not let the capture consent be remembered.
- **Drags need Android 8.0;** on 7.x a drag is performed as one swipe on release.
- **Screenshots need Android 11.**
- **Running state is approximate.** Without usage-access permission the host cannot tell which apps
  are running; the app list marks only the one in front.
- **Android only.** iOS has no way for an app to inject input into other apps.

---

## 5. Where the code is

| Part | Files |
|---|---|
| Protocol additions | `lib/protocol/protocol.dart`, `input_messages.dart`, `screen_messages.dart`, `frame_codec.dart` |
| Host core (Dart) | `lib/host/`: `host_controller.dart` (lifecycle), `host_session.dart` (one connection, the router), `host_handlers.dart` (every command), `host_pairing.dart`, `session_security.dart` (replay guard, rate limit, tokens), `host_catalog.dart`, `host_input.dart` + `input_mapper.dart` (input and its geometry), `block_list.dart`, `screen_share.dart` (WebRTC), `discovery_responder.dart`, `tls_bridge.dart`, `host_identity.dart`, `paired_viewer_store.dart`, `android_host_platform.dart` |
| Android | `android/app/src/main/kotlin/.../host/`: `TlsControlServer.kt`, `SharingForegroundService.kt`, `RemoteInputAccessibilityService.kt`, `InstalledApps.kt`, `HostPlugin.kt`; `res/xml/accessibility_service_config.xml` |
| Owner UI | `lib/features/host/`: `share_phone_screen.dart`, `blocked_apps_screen.dart`, `pairing_approval_dialog.dart` |
| Viewer changes | `remote_desktop_screen.dart` (phone layout, nav bar, placeholder), `control_gestures.dart` (`PointerStyle.touch`), `apps_screen.dart`, `devices_screen.dart`, pairing screens, `paired_pc_store.dart` (`kind`) |
| Tests | `test/host_pairing_test.dart`, `host_session_test.dart`, `host_input_mapping_test.dart`, `host_block_list_test.dart`, `host_bridge_test.dart`, `device_kind_test.dart`, phone-touch cases in `remote_desktop_gestures_test.dart` |

The host's networking, pairing, handshake, input gating and block list are tested with in-memory
transports and a fake phone; the loopback bridge is tested with real sockets. What is not covered by
tests, because it needs two devices: the MediaProjection capture, the accessibility gestures, and the
WebRTC stream between two phones. The Android code compiles (`flutter build apk --debug`).
