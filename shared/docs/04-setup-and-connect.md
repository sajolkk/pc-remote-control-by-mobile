# Setting up PC-Remote and connecting your phone

Step by step: prepare the PC, install the mobile app, pair the two once, then connect and use it.
Everything happens on your own network. There is no account, cloud service or port forwarding.

> **Current status:** everything below works on the PC side. The screen-streaming tab has not yet
> been tried on a physical phone ([Phase 3 notes §6](03-phase3-streaming-notes.md)), so treat that
> part as a first test and report what you see.

---

## Quick start with the ready-made builds

Both halves are already built in this repository. Nothing needs compiling to try it:

| For | File | Size |
|---|---|---|
| PC (Windows 10/11, x64) | folder [`artifacts/release/PC-Remote/`](../../artifacts/release/PC-Remote) — `RemoteAgent.Service.exe` + `RemoteAgent.Session.exe` | ~305 MB |
| Phone (Android, 64-bit) | [`artifacts/release/Android/PC-Remote.apk`](../../artifacts/release/Android/PC-Remote.apk) | 66 MB |

In five steps:

1. Copy the `PC-Remote` folder to the PC, e.g. to `C:\Program Files\PC-Remote` (§1.1).
2. In an **administrator** PowerShell there, run `.\RemoteAgent.Service.exe --install` (§1.2).
3. Install `PC-Remote.apk` on the phone (§2.1).
4. PC tray icon → **Allow pairing and show QR code**; phone → **Devices → Pair a PC** → scan →
   approve on the PC (Part 3).
5. Tap the PC in **Devices**, then open the **Screen** tab (Part 4).

The rest of this document explains each step, how to build from source instead, and what to do
when something does not work.

---

## What you need

| | Requirement |
|---|---|
| PC | Windows 10 1809 or later, or Windows 11, x64 or ARM64 |
| Phone | Android with a **64-bit (arm64) processor** for the ready-made APK — practically every phone from the last seven years; iOS only by building it yourself |
| Network | PC and phone on the **same Wi-Fi / LAN**, with the network set to **Private** on the PC |
| To build from source | .NET 10 SDK on the PC; Flutter 3.44 (Dart 3.12) for the app |

The target PC needs no .NET runtime. The published executables are self-contained.

---

## Part 1 — Set up the PC

### 1.1 Get the two executables onto the PC

PC-Remote is two programs that must sit **in the same folder**:

- `RemoteAgent.Service.exe` — the always-on background service
- `RemoteAgent.Session.exe` — the agent that runs on your desktop (tray icon, screen, apps)

**Using the ready-made build.** Copy the whole folder
[`artifacts/release/PC-Remote/`](../../artifacts/release/PC-Remote) to the PC. `C:\Program Files\PC-Remote`
is a good permanent home: copying there asks for administrator approval, which is what keeps
other users from replacing the executables. The `.pdb` files beside them are debugging symbols;
they are harmless and can be deleted.

The executables are self-contained x64 builds, so the PC needs no .NET installed. They are
**not code-signed** yet, so the first time you run one, Windows SmartScreen may show *"Windows
protected your PC"*. Choose **More info → Run anyway**. Only do that for files you copied from
this repository yourself.

**Building it yourself instead.** From the repository root, publish both into one folder:

```powershell
dotnet publish windows/RemoteAgent.Service/RemoteAgent.Service.csproj -c Release -r win-x64 -o C:\PCRemote
dotnet publish windows/RemoteAgent.Session/RemoteAgent.Session.csproj -c Release -r win-x64 -o C:\PCRemote
```

Use `-r win-arm64` on an ARM PC. `C:\PCRemote` is only an example. `C:\Program Files\PC-Remote`
is a better long-term home.

> On a PC with little free memory, a solution-wide build can fail with `OutOfMemoryException`.
> Publishing the two projects one at a time, as above, avoids that.

### 1.2 Choose how to run it

**Option A — install as a Windows service (recommended).** It starts with Windows, keeps running
when you sign out, and restarts itself if it crashes. From an **administrator** PowerShell:

```powershell
cd "C:\Program Files\PC-Remote"
.\RemoteAgent.Service.exe --install
```

To open an administrator PowerShell: Start menu → type *PowerShell* → **Run as administrator**.

This registers and starts the service. It also adds three Windows Firewall rules, each limited to
**Private** networks and to PC-Remote's own executables:

| Rule | Port | Why |
|---|---|---|
| PC-Remote control channel (TCP) | 47800–47815 | The encrypted connection the phone talks to |
| PC-Remote discovery (UDP) | 47801 | Lets the phone find the PC automatically |
| PC-Remote screen streaming (UDP) | 47810–47850 | The live screen video |

**Keep the folder where it is after installing.** The service runs the executable from that exact
path; moving or deleting the folder breaks it. To move it: `--uninstall`, move, `--install` again.

To remove it later: `RemoteAgent.Service.exe --uninstall`. That also removes the firewall rules;
your settings and pairings are kept.

**Option B — run without installing (for trying it out).**

```powershell
& "C:\Program Files\PC-Remote\RemoteAgent.Service.exe" --console
```

It runs until you close the window and logs to the console. No firewall rules are added, so the
first time the phone connects Windows may ask whether to allow PC-Remote. Choose **Private
networks** and allow it.

**Option C — portable.** Create an empty file named `portable.txt` next to the executables. All
data then lives in a `data` folder beside them instead of `%ProgramData%\PCRemote`.

### 1.3 Check it is running

A few seconds after starting, a **PC-Remote icon appears in the system tray** (near the clock; it
may be inside the `^` overflow). That icon is the session agent. If it does not appear:

- make sure both `.exe` files are in the same folder;
- look in the log folder, `%ProgramData%\PCRemote\logs` (or `data\logs` in portable mode).

> **Running a development build straight from `artifacts\bin\…`?** The two executables land in
> different folders there, so tell the service where the agent is. Add this to
> `%ProgramData%\PCRemote\config.json`:
>
> ```jsonc
> { "agent": { "startup": { "sessionAgentPath": "E:\\my\\pc-control\\artifacts\\bin\\RemoteAgent.Session\\debug\\RemoteAgent.Session.exe" } } }
> ```

### 1.4 Make sure the network is Private

**Settings → Network & Internet →** your Wi-Fi or Ethernet **→ Network profile: Private.** The
firewall rules deliberately do not apply on Public networks, so on a network marked Public the
phone will not be able to connect.

---

## Part 2 — Set up the phone

### 2.1 Android — install the ready-made APK

The file is [`artifacts/release/Android/PC-Remote.apk`](../../artifacts/release/Android/PC-Remote.apk)
(66 MB), next to the PC build. It is a copy of Flutter's own output,
`mobile/remote_control_app/build/app/outputs/flutter-apk/app-release.apk`, placed there because
`build/` is git-ignored and editors tend to hide it. Two ways to get it onto the phone:

**Without a cable**

1. Copy `PC-Remote.apk` to the phone — by USB file transfer, Google Drive, Telegram "Saved
   Messages", or any way you like.
2. On the phone, open the file (from **Files** / **Downloads**).
3. Android asks whether to allow installing from that app (Files, Chrome, Drive…). Tap
   **Settings → Allow from this source**, go back, and tap **Install**.
4. Play Protect may warn that the app is from an unknown developer. Choose **Install anyway**. The
   APK is signed with a development key, not a Play Store key, which is why the warning appears.

**With a USB cable** (USB debugging enabled on the phone):

```powershell
adb install -r artifacts\release\Android\PC-Remote.apk
```

`-r` replaces an older version while keeping the app's pairing data.

About this APK:

- **64-bit phones only (arm64).** Nearly every Android phone from the last seven years qualifies.
  A very old 32-bit phone reports "App not installed", and needs an APK built without
  `--target-platform android-arm64`.
- **Signed with the debug key.** Fine for your own phones. Publishing to the Play Store needs a
  proper release signing key, which is not set up yet.
- **Updating later:** install the new APK over the old one. Pairings survive. Uninstalling the app
  deletes its identity, so the phone must be paired again.

### 2.2 Android — build the APK yourself

```powershell
cd mobile/remote_control_app
flutter build apk --release --target-platform android-arm64
```

The result lands in `build\app\outputs\flutter-apk\app-release.apk`. With a phone connected,
`flutter install --release` builds and installs in one step.

> **On a PC that is short of memory**, the release build can die with *"Gradle build daemon
> disappeared unexpectedly"* or *"insufficient memory for the Java Runtime Environment"*. Release
> builds run R8 shrinking and need noticeably more memory than debug builds. What worked here:
>
> - put Gradle's cache off a full C: drive: `$env:GRADLE_USER_HOME = "E:\my\pc-control\.gradle-home"`;
> - in that folder's `gradle.properties`, lower the heap and parallelism for this machine only:
>
>   ```properties
>   org.gradle.jvmargs=-Xmx1024m -XX:MaxMetaspaceSize=512m -XX:ReservedCodeCacheSize=128m
>   org.gradle.daemon=false
>   org.gradle.parallel=false
>   org.gradle.workers.max=1
>   kotlin.compiler.execution.strategy=in-process
>   ```
>
> - build for arm64 only, as above;
> - close browsers during the build if it still fails.

### 2.3 iOS

Open `mobile/remote_control_app/ios/Runner.xcworkspace` in Xcode on a Mac, pick your team for
signing, and run it on the device. iOS has not been tested yet.

### 2.4 Permissions the app asks for

- **Camera** — only to scan the pairing QR code.
- **Local network** (iOS) — to find the PC on your Wi-Fi.

It never asks for the microphone, and the camera is never used for streaming: the stream is
receive-only.

---

## Part 3 — Pair the phone with the PC (once)

Pairing needs three things at once: pairing switched on at the PC, a QR code scanned from the PC's
screen, and **you approving it on the PC**. A photo of the QR code alone is not enough.

1. **On the PC:** right-click the PC-Remote tray icon → **Allow pairing and show QR code**. A
   window shows a QR code and a countdown; the code expires after 5 minutes and works only once.
2. **On the phone:** open the app → **Devices** tab → **Pair a PC** → point the camera at the QR
   code.
3. **On the PC:** an approval dialog appears. **Check that the fingerprint matches the one shown
   on the phone**, then approve. If they differ, reject: something other than your PC is answering.
4. The phone shows the PC as paired, and the QR window closes.

You only do this once per phone. To pair another phone, repeat it.

---

## Part 4 — Connect and use it

### Connecting

Open the app and tap your PC in the **Devices** tab. The app finds the PC by its identity, not its
IP address, so a changed IP after a router restart does not matter. The banner at the top shows
the connection state, whether the PC is locked, and the round-trip time.

After the first time, the app reconnects by itself whenever the PC is reachable.

### What each tab does

| Tab | What you can do |
|---|---|
| **Devices** | Pair, connect, see which PCs are nearby |
| **Screen** | Watch the PC's screen live. Pinch to zoom, double-tap to reset. The toolbar has monitor switching, quality (Auto / 1080p / 720p / data saver), screenshot, stream details and full screen |
| **System** | Lock, sign out, sleep, restart, shut down, cancel a pending shutdown |
| **Apps** | Browse installed apps, launch, focus or close them; open a browser or a URL |
| **Settings** | This device's identity, the permissions the PC has granted it, what the PC supports, and "Reset this device" (forget every PC) |

The screen stream runs only while the **Screen** tab is open and the app is in the foreground. It
stops by itself when you switch away, so it costs the PC nothing when you are not watching.

### Permissions

A newly paired phone can **view the screen** and **control input** (input arrives in a later
phase). Power controls and app launching must be granted on the PC. The **Settings** tab shows
what this phone currently has.

---

## Troubleshooting

| Symptom | Likely cause and fix |
|---|---|
| "Windows protected your PC" when starting the `.exe` | SmartScreen: the executables are not code-signed yet. **More info → Run anyway** (§1.1) |
| Phone says "App not installed" | A 32-bit-only phone (the APK is arm64), or a copy of the app signed with a different key is already installed. Uninstall the old app first; this also removes its pairings |
| The PC does not appear in **Devices** | Phone and PC on different networks (e.g. a guest Wi-Fi), or the PC's network set to **Public**. Fix the network profile (§1.4). Some routers block devices from seeing each other ("AP/client isolation"); turn that off |
| No tray icon | The two executables are not in the same folder, or no one is signed in. Check the logs (§1.3) |
| QR scan says it cannot reach the PC | Firewall: install with `--install`, or allow the prompt in console mode, and check the network is Private |
| Fingerprint mismatch after reinstalling Windows or deleting the data folder | The PC has a new identity. Remove it from the app and pair again |
| **Screen** says "Nobody is signed in" | Streaming needs a signed-in desktop. Sign in at the PC; power controls still work meanwhile |
| **Screen** says the PC is locked | Windows does not allow the lock screen or UAC prompts to be captured by any app. Unlock at the PC; the picture comes back by itself. Remote unlock is deliberately not possible |
| **Screen** says the PC cannot stream | No usable H.264 encoder (Windows "N" editions need the Media Feature Pack) or a display driver without screen capture. The session agent's log says which |
| **Screen** stays on "Connecting video…" | UDP 47810–47850 blocked. Check the "PC-Remote screen streaming (UDP)" firewall rule exists, or allow the Windows prompt |
| Stream looks soft or says **Degraded** | Weak Wi-Fi or a busy PC. The stream lowers quality by itself and recovers after about 10 clean seconds. Moving closer to the router helps most |
| "Access was revoked" | The PC's owner removed this phone. Pair again if that was a mistake |

### Where things live on the PC

| | Location |
|---|---|
| Settings | `%ProgramData%\PCRemote\config.json` |
| Logs | `%ProgramData%\PCRemote\logs` |
| Keys and pairings | `%ProgramData%\PCRemote` (protected; do not copy between PCs) |

In portable mode all of these are under `data\` beside the executables.
