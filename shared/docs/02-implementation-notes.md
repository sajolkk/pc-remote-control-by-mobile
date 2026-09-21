# Phase 2 — Implementation notes and design corrections

Everything here is a change from, or an addition to, [01-architecture.md](01-architecture.md),
recorded with the reason. The architecture document remains the design; this is where reality
pushed back.

---

## 1. Corrections to the Phase 1 design

These were all found by building and running the system, not by review. Each one is a case where
the architecture was wrong or unachievable as written.

### 1.1 .NET 8 → .NET 10

**Changed because** .NET 8's LTS support ends in November 2026, two months after this work started.
Shipping on a framework about to leave support would mean a migration before the project is
finished. .NET 10 is LTS to November 2028.

### 1.2 Ed25519 → ECDSA P-256

**Changed because Windows SChannel does not support Ed25519 certificates for TLS at all.** Since
`SslStream` is backed by SChannel, an Ed25519 identity would have forced a hand-rolled handshake —
exactly the thing §7.1 refuses to do. Dart's BoringSSL-backed TLS is likewise most reliable with
P-256.

P-256 gives equivalent security for this threat model, is supported end to end, and has
first-class API support on both sides (`ECDsa` + `CertificateRequest` in .NET,
`CryptoUtils.generateEcKeyPair` in Dart).

### 1.3 "TLS 1.3 only" → TLS 1.3 where available, TLS 1.2 floor

**Changed because SChannel only gained TLS 1.3 in Windows 11 / Server 2022.** Measured on the
development machine (Windows 10 22H2, build 19045): requesting `Tls13` alone fails the handshake
outright. Requesting `Tls12 | Tls13` negotiates 1.2 there and would negotiate 1.3 on Windows 11.

A 1.3-only requirement would therefore have made the system non-functional on a large share of
target PCs, violating the portability rule in §0.

The security cost is small and specific. Because certificates are **pinned**, TLS 1.2's real
weaknesses do not apply: there is no CA to mis-issue, no downgrade to a weaker credential, and no
RSA key exchange. The measured session was
`TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384` — forward secrecy and AEAD, with mutual authentication.

**Cipher suites cannot be restricted in code on Windows.** `CipherSuitesPolicy` throws
`PlatformNotSupportedException` there. Rather than pretend otherwise, `TlsTransport.Validate`
inspects the negotiated suite after the handshake and drops the connection if it is not
forward-secret and AEAD. Same outcome, by a route that works.

### 1.4 Fingerprints are over the certificate DER, not the SPKI

**Changed because** the mobile client can obtain the DER bytes directly from its TLS stack
(`X509Certificate.der`) and hash them with no ASN.1 parsing and therefore no extra dependency.
Extracting an SPKI in Dart would have meant an ASN.1 library for no benefit.

Certificates are long-lived (10 years) and rotation is an explicit, signed operation, so tying the
fingerprint to the certificate rather than the key costs nothing. The definition is pinned by a test
on both sides against an independently computed value, because a disagreement here would present as
every pairing failing with a fingerprint mismatch — which looks exactly like an attack.

### 1.5 Mobile private key is not hardware-backed

**Changed because it is not achievable with Dart's TLS stack.**
`SecurityContext.usePrivateKeyBytes` requires the key material as PEM, so the key must be readable
by the app's process. A genuinely non-exportable key would mean doing TLS natively through OkHttp or
NSURLSession with a keystore-backed identity, and bridging the entire control channel over a
platform channel.

What is done instead: the key is generated on the device, never transmitted, and stored through
`flutter_secure_storage` — hardware-encrypted on Android, Keychain on iOS. It is protected at rest
against another app or someone holding the device, but extractable by this app's own process.

The consequence is bounded and worth stating plainly: stealing that key lets an attacker impersonate
*that one phone* to a PC it is already paired with, and the PC's owner can revoke it. It does not
expose the PC's key, any Windows credential, or any other paired device.

### 1.6 Discovery: UDP broadcast is primary, mDNS is the addition

**Reversed from the architecture**, which had mDNS primary with UDP as fallback.

On Android, multicast reception is unreliable in ways an app cannot control: several Wi-Fi chipsets
drop multicast in power save, battery optimisation can suspend a listener, and many consumer routers
filter multicast between wireless clients. A directed UDP broadcast to a known port has none of those
failure modes and is under our control end to end.

mDNS remains worth adding — it makes the PC visible to generic service browsers — but making it the
only path would mean discovery that works on a laptop and fails on a phone. Not yet implemented.

### 1.7 The approval dialog lives in the session agent, not a separate tray UI

**Changed because the session agent is the process that actually owns a desktop.** The architecture
had the management UI showing approval prompts, which would have required a third process and a
third IPC channel to secure, for a dialog the agent can already display.

The same reasoning moved "Allow pairing" and the QR code into the agent's tray icon. That keeps the
number of privileged channels at one.

### 1.8 A command may be served by either host

Added `CommandTarget.Either` for `system.lock`, which both hosts can perform by genuinely different
mechanisms: the agent calls `LockWorkStation` on the real desktop, while the service can only
disconnect the session. The service handler tries the agent first and falls back, so one command
works whether or not anybody is signed in.

---

## 2. Bugs found by running the system

Recorded because each was invisible in review and each would have affected any machine.

| # | Bug | How it presented | Fix |
|---|---|---|---|
| 1 | `SYSTEM_POWER_CAPABILITIES.BatteryScale` declared as 12 bytes instead of 24 (`BATTERY_REPORTING_SCALE[3]`, two `DWORD`s each) | Process died instantly with `STATUS_STACK_BUFFER_OVERRUN` — no exception, no log, no `finally`. Only the Windows event log showed it. | Corrected the layout, and added a `Marshal.SizeOf` guard so a future mistake degrades to "sleep unavailable" rather than corrupting the stack |
| 2 | ACL hardening granted SYSTEM + Administrators only | Correct as a service; in console/portable mode the agent runs as an ordinary user and immediately lost the ability to create its own `keys` directory | The running identity is granted explicitly |
| 3 | `DisposeAsync` ran twice — once from ordered shutdown, once from the DI container | `ObjectDisposedException` masked the real shutdown reason | Every component's disposal is idempotent |
| 4 | Pipe DACL did not grant its creator `CreateNewInstance` | Implicit for SYSTEM, so a service never hits it. As an interactive user the first client connected and every later accept failed with access denied — portable mode could never reconnect | The hosting identity is granted full control |
| 5 | `JsonSerializerOptions.MakeReadOnly()` throws without a `TypeInfoResolver` | `ProtocolJson`'s static initializer threw on **both** hosts. The service had not reached it only because no client had connected yet | `MakeReadOnly(populateMissingResolver: true)` |
| 6 | Session-agent supervisor spawned duplicates | The agent takes seconds to start (it builds the application catalog first — 2.6s under load), and the 3-second monitor tick fired again before the handshake completed. Three agents were launched, each adding its own tray icon | A startup grace period, cleared on a definite disconnect |
| 7 | Circular dependency `DeviceInfoHandler → ControlChannelListener → ICommandDispatcher → CommandRegistry → handlers` | The container normally reports a cycle clearly, but the services involved were registered with factory lambdas, which are opaque to its call-site chain. So it recursed to a stack overflow and killed the process silently | The port is published through a small `ListenerEndpointStatus` holder, which also removes the command layer's dependency on the transport |

The general lesson from 1, 5 and 7: three separate defects all presented as *the process vanishing
with no diagnostic*. Hand-written P/Invoke, static initializers and DI factory lambdas each defeat
the normal error-reporting path, and the Windows event log was the only source of truth.

---

## 3. What is implemented

### Working and verified end to end

- **Service and session agent**, with the service spawning the agent across the Session 0 boundary
  (`WTSQueryUserToken` → `DuplicateTokenEx` → `CreateProcessAsUser`), plus a same-session launch path
  for console and portable mode.
- **Zero-config first run**: device id, display name and identity certificate all generated on first
  start; nothing to edit before it works.
- **Mutually authenticated TLS** with certificate pinning, post-handshake cipher validation, and
  session tokens bound to the connection.
- **Pairing** with all three gates: pairing mode, single-use token, and a human approving at the PC.
- **Command pipeline**: catalog, authorization gate, dispatcher, IPC forwarding.
- **Power**: lock, sign out, sleep, restart, shut down, abort shutdown.
- **Applications**: 132 discovered on the development machine; launch, focus, graceful close with
  opt-in force.
- **Browsers**: detection, open, open validated URL, close.
- **Discovery** over UDP with a rate-limited, minimal beacon.
- **Wake-on-LAN reporting**, including honest caveats about Fast Startup and Wi-Fi.
- **Tray icon** with Allow pairing and a QR window that counts down and closes on expiry.
- **Mobile app**: identity generation, discovery, pairing by QR with pinning, reconnect by device id,
  status, power controls, application list with icons, browser and URL control, permission display.

### Test coverage

| Suite | Tests | Covers |
|---|---|---|
| `RemoteAgent.Protocol.Tests` | 88 | Framing against malformed input, the authorization matrix for every command, replay guard, URL validation, token binding, permission mapping |
| `RemoteAgent.Integration.Tests` | 21 | A real TLS client driving the real listener through pairing, authentication, replay rejection, revocation and permission changes |
| `remote_control_app` (Dart) | 20 | QR payload parsing, wire-shape pinning, cross-language fingerprint agreement |

### Not yet implemented

- ~~**Screen streaming** (Phase 3)~~ — done; see [03-phase3-streaming-notes.md](03-phase3-streaming-notes.md).
- **Remote input** (Phase 4).
- **Clipboard, file transfer, volume, connection history** (Phase 5).
- **mDNS advertisement** as an additional discovery path.
- **The full management UI** — device list, permission toggles, revocation, streaming settings, log
  viewer. Currently only the tray icon exists, which is enough to pair but not to administer.
- **Installer** (MSI/MSIX), service registration is available through `--install` in the meantime.
  Since Phase 3, `--install` also adds scoped firewall rules (Private profile, per program) and
  `--uninstall` removes them.
- **`uiAccess`** on the session agent, which needs code signing before it can be enabled (§12.1).

---

## 4. Running it

```powershell
# Build
dotnet build windows/PCRemote.slnx

# Run in the foreground, no installation. Logs to the console and to the data directory.
windows/RemoteAgent.Service/bin/Debug/net10.0-windows10.0.19041.0/RemoteAgent.Service.exe --console

# Install as a Windows service (needs an elevated prompt)
RemoteAgent.Service.exe --install
RemoteAgent.Service.exe --uninstall
```

Data lives in `%ProgramData%\PCRemote` by default. Two ways to change that, both covered by §0:

- set `PCREMOTE_DATA_DIR`, or
- place a `portable.txt` file beside the executable, which switches to a `data` folder next to it.

In a development build the two hosts land in separate output directories, so the service cannot find
the agent beside itself. Point it at the agent explicitly:

```jsonc
{ "agent": { "startup": { "sessionAgentPath": "…/RemoteAgent.Session.exe" } } }
```

A real installation puts both executables in one directory and needs no such setting.

### Pairing

1. Right-click the PC-Remote tray icon → **Allow pairing and show QR code**.
2. In the mobile app: **Devices** → **Pair a PC**, and scan.
3. Approve the request on the PC, checking that the fingerprint matches the one the phone shows.

The QR code contains a single-use token valid for five minutes. It is not sufficient on its own —
the approval dialog is a separate gate.

---

## 5. Android build fixes

The Flutter template's defaults and this machine's state combined to make the Android build fail four
different ways. All four fixes are committed, and three of them make the build more portable rather
than merely working here.

| Symptom | Cause | Fix |
|---|---|---|
| Gradle daemon JVM crashed on startup | The template requests `-Xmx8G` and a 4 GB metaspace, which cannot be satisfied on a machine with a browser and an IDE open | Lowered to 2 GB heap / 1 GB metaspace in `android/gradle.properties`. An Android build of this size needs nothing like 8 GB |
| `Failed to install NDK 28.2` / `did not have a source.properties file` | The requested NDK directory existed but was an empty, failed download — and nothing in this project compiles native code, so the NDK is only satisfying a toolchain check | `ndkVersion` pinned to the installed 25.1 for **every** subproject, not just `:app`. Two ordering constraints matter and are commented in `android/build.gradle.kts`: it must be in `afterEvaluate` (a plugin sets its own value during evaluation), and it must appear before the `evaluationDependsOn(":app")` block (which forces evaluation, after which registering the hook throws) |
| `java.io.IOException: There is not enough space on the disk` | C: was full | Caches cleared with the owner's approval; Gradle's cache also relocated off the system drive via `GRADLE_USER_HOME` |
| `Daemon compilation failed` → `Could not close incremental caches … .tab` | Kotlin's incremental caches are memory-mapped, and under memory pressure closing them fails. It surfaces with no source-level error, so there is nothing to chase | `kotlin.incremental=false` and a 1 GB Kotlin daemon heap. Incremental compilation only accelerates rebuilds of plugin Kotlin that we never edit, so the cost is negligible |

Result: `flutter build apk --debug` succeeds, `flutter analyze` is clean, and the Dart tests pass.

### One forward-compatibility warning, not yet addressed

```
WARNING: Your app uses the following plugins that apply Kotlin Gradle Plugin (KGP):
         device_info_plus, mobile_scanner
Future versions of Flutter will fail to build if your app uses plugins that apply KGP.
```

Both are pinned to versions that predate Flutter's built-in Kotlin migration. Newer releases exist
(`mobile_scanner` 7.x, `device_info_plus` 13.x) but need their own compatibility check, so the upgrade
is deliberately a separate change rather than folded into this one. It is not urgent — the current
Flutter builds fine — but it will become a hard failure eventually.

## 6. Environment notes for this machine

Recorded because they shaped several decisions above:

- **C: was full** (0–1.3 GB of 246 GB), which is what blocked the Android build. Roughly 29 GB free
  after clearing the Gradle and Temp caches.
- **Low free RAM** (~2.4 GB of 16 GB). A solution-wide `dotnet build` hits `OutOfMemoryException` in
  MSBuild's `ResolveAssemblyReference`; per-project builds succeed, and the tests run fine. This is
  also the root cause of the Kotlin incremental-cache failure above.
