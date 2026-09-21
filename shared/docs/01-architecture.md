# PC-Remote — Phase 1 Architecture Analysis

LAN-only remote monitoring and control of a Windows PC from a mobile app.

| | |
|---|---|
| Document status | Phase 1 design. **Several decisions here were corrected during implementation** — see [02-implementation-notes.md](02-implementation-notes.md) for what changed and why. |
| Target PC | Windows 10 Pro 22H2 (build 19045) and Windows 11 |
| Target mobile | Android 8.0+ / iOS 14+ (Flutter) |
| Network scope | Single LAN. **No** cloud relay, **no** port forwarding, **no** internet exposure. |
| Threat model | Untrusted LAN peers (guest Wi-Fi, IoT devices, roommates), passive/active on-path attackers |
| Portability | **Hard requirement.** The same binaries must work on any Windows PC with no code change, no rebuild, and no per-machine editing. See §0. |

---

## 0. Portability requirements (hard constraint)

The shipped artifacts must run on an arbitrary Windows PC — different name, user, IP, subnet, GPU, monitor count, installed apps, browsers and Windows build — with **no code change and no rebuild**. This is a design constraint, not an aspiration, and it drives concrete rules:

| Rule | Implementation |
|---|---|
| Nothing machine-specific is compiled in | No literal machine names, IPs, MACs, user profile paths, monitor ids, GPU names, app paths, or ports as *values*. Defaults exist in code only as overridable defaults. |
| Zero-config first run | On first start the service generates its own device id (GUID) and identity key pair, reads the computer name from the OS, and writes a default config to `%ProgramData%\PCRemote\`. Nothing is pre-baked and no manual setup step exists. |
| All machine facts discovered at runtime | NICs/MACs and IPs via `GetAdaptersAddresses` (re-read on `NetworkAddressChanged`), user folders via `SHGetKnownFolderPath`/`Environment.GetFolderPath` (never `C:\Users\<name>\...`), monitors via DXGI/`EnumDisplayMonitors`, encoders via `MFTEnumEx`, apps and browsers via registry + Start Menu enumeration. |
| Feature detection, never version assumption | Capture (Desktop Duplication vs WGC), TLS version, hardware encoder vendor, codec support, WoL capability, and power-state support are all *probed* and reported. A PC with no NVENC, one monitor, no WoL and TLS 1.2 gets a working system with a smaller capability set — not an error. |
| Capability negotiation with the phone | `hello.ack` carries the PC's actual capability list. The mobile app renders from that list, so the same app build adapts to any PC and never offers a control the PC can't perform. |
| Ports are configurable and self-healing | Defaults are defaults; if the control port is occupied the service tries the next free port in a configured range and advertises the actual port via discovery. The phone never assumes a port number — it learns it from discovery or the QR payload. |
| No baked-in PC address on mobile | The app ships knowing nothing about any PC. Discovery + pairing is the only path in. IP is never an identity; reconnect is by device id. |
| CPU architectures | Published for `win-x64` **and** `win-arm64`. This is an additional argument for pure-managed dependencies (SIPSorcery over `libdatachannel`, MFT over vendor SDKs): a native per-arch binary doubles the portability surface. |
| No runtime prerequisite on the target PC | Hosts are published **self-contained, single-file** so the target machine does not need the .NET runtime installed. WPF is chosen partly because it needs no Windows App SDK deployment. |
| Deployment modes | (a) MSI/MSIX install — registers the service, ACLs, firewall rule; (b) **portable/console mode** — `RemoteAgent.Service.exe --console` runs the whole system without installing a service, for testing or a no-install machine. Same binary, different host lifetime. |
| Windows version floor | Windows 10 1809 → Windows 11, all builds. Anything newer than the floor is used only behind a capability probe. |

---

## 1. System architecture

### 1.1 Design principles

1. **Three processes, three trust levels.** A SYSTEM service for privileged and always-on work, a per-session agent running as the logged-in user for everything that touches the desktop, and a normal (non-elevated) management UI. Nothing runs elevated that does not have to.
2. **One authentication boundary.** Exactly one network listener accepts remote connections, in the service. Everything else is reachable only through it.
3. **Control plane and media plane are separate.** Control is a request/response + event protocol over TLS 1.3. Media is WebRTC (SRTP) straight from the session agent to the phone, so video frames never cross a process boundary on the PC.
4. **Capability-based authorization.** Every command maps to a declared permission group; the dispatcher rejects anything not granted to that specific paired device. There is no shell passthrough — ever.
5. **Fail closed.** No pairing without explicit local human approval. No command without a live authenticated session. No unauthenticated port, not even a "status" port.
6. **Platform isolation.** Win32/WinRT/COM calls live only in dedicated projects, behind interfaces, so core logic is testable without a desktop.

### 1.2 Process model

```
Windows boot
  └─ services.exe
       └─ RemoteAgent.Service          (LocalSystem, Session 0, Automatic-Delayed)
            ├─ TLS 1.3 listener        :47800/tcp      ← the only remote entry point
            ├─ mDNS/DNS-SD advertiser  :5353/udp       + UDP discovery fallback :47801/udp
            ├─ Pairing store (DPAPI/CNG protected, ACL: SYSTEM + Administrators)
            ├─ Command dispatcher + authorization + audit log
            ├─ Privileged executors    (lock, logoff, sleep, restart, shutdown, WoL info)
            ├─ Session manager         (WTS session change notifications)
            └─ Named-pipe IPC hub      \\.\pipe\pcremote-agent-{sessionId}
                   ▲                                ▲
                   │ (spawned via CreateProcessAsUser)
                   │                                │
  Interactive logon                                 │
  └─ RemoteAgent.Session (logged-in user, medium IL, Session N)
       ├─ Desktop capture     (DXGI Desktop Duplication / Windows.Graphics.Capture)
       ├─ HW video encoder    (NVENC / QuickSync / AMF via MFT, D3D11 zero-copy)
       ├─ WebRTC peer         DTLS-SRTP video + DataChannel  ──── direct UDP ───┐
       ├─ Input injection     (SendInput)                                       │
       ├─ Clipboard, volume, app registry, app launch/close, browser control    │
       └─ Screenshot / monitor enumeration                                      │
                                                                               │
  └─ RemoteAgent.UI (logged-in user, medium IL, autostart at logon, tray)       │
       └─ management UI; talks to the service over the same named-pipe IPC      │
          (UAC elevation requested only for: install/uninstall, service         │
           start/stop, firewall rule changes, Start-with-Windows toggle)        │
                                                                               │
══════════════════════ LAN (same subnet) ══════════════════════════════════════│
                                                                               │
  Mobile app (Flutter)                                                         │
       ├─ Discovery (mDNS browse + UDP broadcast fallback)                      │
       ├─ Control channel: TLS 1.3 + mutual pinned-key auth → :47800           │
       └─ Media: WebRTC PeerConnection ◄──────────────────────────────────────┘
```

### 1.3 Why the service owns the network listener

| Alternative | Verdict |
|---|---|
| Session agent listens; service does power ops on request | Rejected. The port disappears at logoff, so the phone cannot show status, wake, or power-cycle the PC when nobody is logged in. It also puts the pairing key store in a user-writable process. |
| Service listens and relays *everything*, including video, to/from the session agent | Rejected for media. Proxying 60 fps encoded frames through a named pipe adds a copy, a queue, and jitter for no security benefit. |
| **Service listens for control; session agent peers directly for media** | **Chosen.** Single auth boundary, single pairing store, service survives logoff; media path is direct and zero-copy. WebRTC's own security model (DTLS fingerprint delivered over an already-authenticated signaling channel) covers the media plane. |

### 1.4 Connection lifecycle

```
1. discover      mobile browses _pcremote._tcp.local → host, port, deviceId, protoVersion
2. connect       TLS 1.3 to service; both sides present Ed25519-backed identity certs
3. verify        mobile pins PC SPKI fingerprint from its pairing record; PC looks up the
                 client key fingerprint in the pairing store → resolves device + permissions
4. hello         hello / hello.ack: protocol version negotiation, capability list,
                 permission set echo, short-lived session token issued (TTL 10 min, renewable)
5. subscribe     mobile subscribes to the event topics it is permitted to see
                 (system.state, session.state, monitors.changed, clipboard.changed)
6. media (opt)   mobile sends media.offer → service forwards to session agent over IPC →
                 agent answers with SDP (its own DTLS fingerprint) + ICE host candidates →
                 DTLS-SRTP established directly agent ↔ phone
7. operate       commands request/response; events pushed; keepalive ping every 5 s
8. teardown      explicit disconnect, TLS close, or 15 s keepalive timeout → media torn
                 down, input state released (all held keys/buttons lifted)
```

---

## 2. Component diagram

```
┌─────────────────────────────── MOBILE (Flutter / Dart) ───────────────────────────────┐
│  UI layer        Home │ Devices │ Remote Desktop │ Apps │ Files │ System │ Settings    │
│  State           Riverpod providers; per-device connection state machine              │
│  Services        DiscoveryService · ControlClient · MediaClient · FileTransferClient   │
│                  IdentityStore (Keystore/Keychain) · PairingStore · TelemetryCollector │
│  Platform        flutter_webrtc · nsd/bonsoir · raw datagram sockets · secure storage  │
└────────────┬──────────────────────────────────────────────┬───────────────────────────┘
             │ TLS 1.3 control (TCP 47800)                  │ WebRTC DTLS-SRTP (UDP)
             │ framed JSON envelopes                        │ H.264 video + DataChannel
┌────────────▼──────────────────────────────────────────────│───────────────────────────┐
│ RemoteAgent.Service  (LocalSystem)                        │                           │
│  ┌───────────────┐ ┌───────────────┐ ┌──────────────────┐ │                           │
│  │ TlsListener   │ │ Discovery     │ │ NamedPipeHub     │ │                           │
│  │ SessionMgr    │ │ Advertiser    │ │ (per WTS session)│ │                           │
│  └──────┬────────┘ └───────────────┘ └────────┬─────────┘ │                           │
│  ┌──────▼──────────────────────────────────────▼────────┐  │                          │
│  │ CommandDispatcher → AuthorizationGate → Registry     │  │                          │
│  │   local handlers:     system.* power, device.*, wol.*│  │                          │
│  │   forwarded handlers: input.* app.* screen.* clip.*  │  │                          │
│  │                       file.* browser.* media.*       │  │                          │
│  └──────┬─────────────────┬─────────────────┬───────────┘  │                          │
│  ┌──────▼──────┐ ┌────────▼───────┐ ┌───────▼──────────┐   │                          │
│  │ PairingStore│ │ PolicyStore    │ │ AuditLog (Serilog│   │                          │
│  │ (CNG/DPAPI) │ │ (permissions)  │ │  JSON + EventLog)│   │                          │
│  └─────────────┘ └────────────────┘ └──────────────────┘   │                          │
└───────────────────────────┬───────────────────────────────┬┘                          │
                            │ named pipe (SYSTEM + owner)   │ named pipe                │
┌───────────────────────────▼──────────────────┐ ┌──────────▼──────────────────────────┐│
│ RemoteAgent.Session  (interactive user)      │ │ RemoteAgent.UI (WPF, tray)          ││
│  CaptureEngine  ── DXGI DesktopDuplication   │ │  Dashboard · Paired Devices ·        ││
│                  └ WGC fallback              │ │  Permissions · Streaming · Network · ││
│  EncoderPipeline── MFT H.264 (NVENC/QSV/AMF) │ │  Logs · Pairing QR · Security        ││
│                  └ OpenH264 SW fallback      │ │  (elevates only for service/firewall)││
│  RtcPeer        ── SIPSorcery | libdatachannel├─┘                                     │
│  InputInjector  ── SendInput + coord mapping │                                        │
│  ShellServices  ── AppRegistry · Browser ·   │                                        │
│                    Clipboard · Volume · Files│                                        │
└──────────────────────┬───────────────────────┘                                        │
                       │ Win32 / WinRT / COM (P-Invoke, isolated in RemoteAgent.Windows)│
                       └───────────────────────────────────────────────────────────────┘
```

### 2.1 Shared layers (referenced by all Windows projects)

- **RemoteAgent.Protocol** — envelope types, command/event DTOs, permission enum, version negotiation, serializer. Mirrored 1:1 into Dart by a generator so the two sides cannot drift.
- **RemoteAgent.Core** — domain models, interfaces (`ICommandHandler`, `IScreenCapture`, `IInputInjector`, `IPairingStore`), config binding, result/error types. No Win32.
- **RemoteAgent.Security** — identity keys, cert generation, TLS configuration, pairing state machine, token issuance/validation, rate limiting, secret-at-rest protection.
- **RemoteAgent.Windows** — the only project allowed to P/Invoke. Thin typed wrappers over user32/kernel32/wtsapi32/powrprof/dxgi/mfplat/Core Audio.

---

## 3. Windows Service responsibilities

`RemoteAgent.Service` — `LocalSystem`, Session 0, `Automatic (Delayed Start)`, SCM recovery = restart after 5 s / 10 s / 60 s.

**Owns**

| Area | Detail |
|---|---|
| Network entry point | Single TLS 1.3 TCP listener on the configured port (default 47800), bound to LAN interfaces only, optionally restricted by a subnet allowlist |
| Discovery | mDNS/DNS-SD advertisement + UDP broadcast responder; disableable in config |
| Cryptographic identity | PC long-term Ed25519 key pair; X.509 wrapper cert for TLS; key material protected with CNG/DPAPI at machine scope, file ACL `SYSTEM:F, Administrators:F` only |
| Pairing store | Paired device records: device id, name, public-key fingerprint, paired-at, last-seen, permission bitmap, revocation state |
| Pairing state machine | Token generation, TTL, single-use enforcement, attempt rate limiting and lockout |
| Authorization | Per-device permission evaluation for **every** command before dispatch |
| Session lifecycle | `SERVICE_ACCEPT_SESSIONCHANGE`; on `WTS_SESSION_LOGON` / `SESSION_UNLOCK` / `CONSOLE_CONNECT`, launches the session agent into that session via `WTSQueryUserToken` + `CreateEnvironmentBlock` + `CreateProcessAsUser`; restarts it with backoff if it dies; reports session state (active / locked / logged-out) to clients |
| Privileged operations | Lock (delegated to the session agent for `LockWorkStation`), sign out (`WTSLogoffSession`), sleep (`SetSuspendState` with `SeShutdownPrivilege`), restart / shut down (`InitiateShutdownW` / `ExitWindowsEx`), abort-shutdown window |
| Wake-on-LAN support data | Reads and caches NIC MACs; reports whether WoL will actually work (Magic Packet enabled, device-wake permitted, Fast Startup state) |
| Audit log | Append-only structured log of connections, auth outcomes, pairings, revocations, every authorized command and every rejection |
| Configuration | Machine-scoped config (`%ProgramData%\PCRemote\config.json`), hot-reload where safe |
| Firewall | At install time only: inbound rules scoped to the Private profile for the control port and the media UDP range |

**Explicitly does NOT**

- capture the screen, inject input, read the clipboard, enumerate/launch apps, change volume, or touch any user-visible object. **Session 0 isolation makes these either impossible or wrong**: DXGI Desktop Duplication and `Windows.Graphics.Capture` cannot see a user's desktop from Session 0, `SendInput` in Session 0 targets an invisible desktop, and clipboard/shell COM calls need an interactive window station.
- read or write files on behalf of a client — file transfer runs as the user, so NTFS permissions apply naturally instead of being bypassed by SYSTEM.
- host any UI. Interactive services have been dead since Vista and `NoInteractiveServices` is enforced; approval dialogs belong to the tray UI, which the service asks over IPC.

---

## 4. User Session Agent responsibilities

`RemoteAgent.Session` — runs as the interactive user, medium integrity, one instance per active session, no visible window. Launched and supervised by the service; when it is not running, session-scoped commands return `E_SESSION_UNAVAILABLE` rather than silently failing.

| Area | Detail |
|---|---|
| Capture | Monitor enumeration (`EnumDisplayMonitors` + DXGI adapters/outputs), Desktop Duplication primary, `Windows.Graphics.Capture` fallback / per-window path, cursor composition, dirty-rect and move-rect handling, DPI-aware geometry |
| Encode | D3D11 texture → Media Foundation H.264 encoder MFT (NVENC / QuickSync / AMF), zero-copy where the MFT accepts D3D11 surfaces; adaptive resolution, FPS and bitrate; forced IDR on request or loss |
| Media transport | WebRTC peer: DTLS-SRTP, one video track, one low-latency DataChannel for input and telemetry; ICE host candidates only |
| Input | `SendInput` for absolute/relative movement, buttons, double click, drag (explicit down→move→up state machine), wheel scroll, Unicode and virtual-key keyboard, modifier tracking, shortcut sequences, dead-man release of all held keys and buttons on disconnect |
| Coordinate mapping | Touch point (normalized to the streamed monitor) → monitor rect → virtual-desktop coordinates → `SendInput` 0–65535 absolute space, correct across mixed-DPI and negative-origin layouts |
| Applications | App registry build (Start Menu `.lnk` + `HKLM`/`HKCU` Uninstall keys + UWP `PackageManager` / AppsFolder AUMIDs), icon extraction and caching, running-state correlation by process/AUMID, launch **by registry id only**, focus (`SetForegroundWindow` + `AllowSetForegroundWindow`), graceful close (`WM_CLOSE` → timeout → optional `TerminateProcess`) |
| Browser | Detect installed Chrome/Edge/Firefox and the default handler; open, open validated URL, close |
| Clipboard | Read/write text with an `OpenClipboard` retry loop, change notifications via `AddClipboardFormatListener`, size cap, opt-in per device, contents never logged |
| Audio | Core Audio `IAudioEndpointVolume` — get/set master volume, mute/unmute, default-device change notifications |
| Files | Chunked transfer inside configured allowed roots, path canonicalization, reparse-point rejection, size/name/extension validation, temp-then-rename writes |
| Screenshot | One-off PNG/JPEG of a chosen monitor, independent of the stream |
| Session state | Reports lock/unlock, desktop switch, monitor topology change and display sleep to the service so clients can react |

**Why not one process?** Desktop access requires a user token in an interactive session; network listening, the pairing store and power control require SYSTEM and must outlive every logon. Merging them means either running capture/input as SYSTEM in Session 0 (broken) or running the listener and key store as the user (weaker, and dead at the logon screen).

---

## 5. Mobile responsibilities

| Layer | Responsibility |
|---|---|
| Discovery | Browse `_pcremote._tcp`; fall back to a UDP broadcast probe when mDNS is filtered (common on some Android Wi-Fi stacks, and on iOS without the Bonjour usage declaration); merge results with the local paired-device list |
| Identity | Generate its own Ed25519 identity key **on device, in hardware-backed storage** (Android Keystore StrongBox/TEE, iOS Keychain with Secure Enclave). The private key never leaves the device and is never transmitted, backed up or logged |
| Pairing | QR scan → parse payload → TLS connect with fingerprint pinning → submit pairing token → wait for the human to approve on the PC → persist the PC's fingerprint and the issued device record |
| Connection management | Per-device state machine: `idle → discovering → connecting → authenticating → ready → degraded → reconnecting`. Exponential backoff with jitter, immediate retry on network-change events, resume without re-pairing |
| Control | Typed command client with correlation ids, timeouts and cancellation, plus an event stream; surfaces permission-denied distinctly from failure |
| Media | Offer/answer via the control channel, render the remote video texture, gesture recognition, input batching (coalesce moves to one per frame, send over DataChannel), adaptive requests based on measured RTT/jitter/decode time |
| Telemetry | Round-trip latency, rendered FPS, bitrate, packet loss, decode time → connection-quality indicator |
| Local policy | Optional biometric gate before power commands or file access; per-device nicknames; no caching of clipboard or file contents beyond what the user explicitly saved |

---

## 6. Networking architecture

### 6.1 Ports and protocols (all configurable; nothing hardcoded)

| Purpose | Default | Protocol | Notes |
|---|---|---|---|
| Control channel | 47800/tcp | TLS 1.3 over TCP, length-prefixed frames carrying JSON | Mutual authentication. The only remote listener. |
| mDNS/DNS-SD | 5353/udp | Multicast DNS | Standard, shared with other services |
| Discovery fallback | 47801/udp | Signed challenge/response probe | For networks where multicast is filtered |
| Media | ephemeral UDP range (e.g. 47810–47850) | ICE / DTLS / SRTP | Session agent's WebRTC; range configurable to keep firewall rules narrow |
| Wake-on-LAN | 9/udp | Magic packet, sent **by the phone** | The PC is asleep; nothing on the PC participates |

### 6.2 Frame format (control channel)

```
[ 4 bytes  big-endian payload length (max 1 MiB) ]
[ 1 byte   frame type: 0x01 request 0x02 response 0x03 event 0x04 ping 0x05 pong ]
[ payload  UTF-8 JSON envelope ]
```

Request envelope:

```json
{ "v": 1, "id": "c-8f2a", "ts": 1758300000123, "seq": 412,
  "cmd": "input.mouse_click", "args": {}, "tok": "<session token>" }
```

Error response:

```json
{ "v": 1, "id": "c-8f2a", "ok": false,
  "err": { "code": "E_PERMISSION_DENIED", "msg": "ControlInput not granted",
           "retryable": false } }
```

- `seq` is a strictly increasing per-session counter — a gap or a repeat kills the session (anti-replay and anti-injection defence in depth on top of TLS).
- `ts` outside a ±30 s window is rejected.
- Unknown `cmd` → `E_UNKNOWN_COMMAND`. Unknown fields are ignored for forward compatibility; an unsupported protocol version → `E_VERSION_UNSUPPORTED` carrying the supported range.

### 6.3 Why two channels

Input latency and video latency have different requirements. Video wants unreliable, unordered, congestion-controlled UDP. Input wants the lowest possible latency and tolerates loss badly for button state but well for move events. Control (pairing, power, app list, file transfer) needs reliability and ordering, and must work when no video session exists.

| Traffic | Channel | Reason |
|---|---|---|
| Pairing, auth, power, apps, clipboard, files, monitor list, SDP signaling | TLS/TCP control | Reliable, ordered, single auth point, works with no video |
| Video | WebRTC video track (SRTP) | Pacing, NACK/PLI, congestion control, hardware decode path |
| Mouse move/click/scroll/keys, latency probes, quality feedback | WebRTC DataChannel (unordered, tuned `maxRetransmits`) | Avoids TCP head-of-line blocking; roughly one RTT less than the control path |
| Input when no media session exists | TLS control channel (degraded mode) | Correctness over latency |

Authoritative state (permissions, session validity) is always enforced on the control side. The DataChannel is bound to a control session by the session token exchanged in the offer, so it can never be used standalone.

### 6.4 Resilience matrix

| Event | Detection | Behaviour |
|---|---|---|
| PC powered off or asleep | Discovery miss + connect timeout | Show last-known state; offer WoL if capability was recorded |
| Mobile loses Wi-Fi or switches network | Connectivity events + socket error | Tear down media, keep UI state, re-discover on the new network |
| PC IP changes (DHCP) | mDNS record update; socket reset | Reconnect by device id, never by IP — IP is not the identity |
| Windows locked | Session agent reports lock | Screen stream stops (OS restriction); control channel stays up; power/lock/WoL/status still work; the UI explains why the view is blank |
| User logs out | `WTS_SESSION_LOGOFF` | Session-scoped commands return `E_SESSION_UNAVAILABLE`; the service stays reachable |
| Session agent crash | Pipe disconnect | Service respawns with backoff; the client re-offers media |
| Service stopped | Connection refused | Mobile shows "agent not running"; SCM recovery restarts it |
| Capture unavailable (`DXGI_ERROR_ACCESS_LOST`, secure desktop, driver reset) | Capture error | Rebuild duplication, then fall back to WGC, then to periodic screenshots **only** as an explicitly-labelled degraded mode |
| Monitor disconnected | Display-change event | Re-enumerate, emit `monitors.changed`, auto-switch to primary |
| Keepalive timeout (15 s) | Missed pongs | Release all held input, close the session; the mobile app reconnects |

---

## 7. Security architecture

### 7.1 Identity and transport

- Both sides hold a long-term **ECDSA P-256** key pair. The PC's lives in CNG at machine scope (non-exportable where the provider allows); the phone's lives in the platform keystore.

  *Corrected from Ed25519 during Phase 2.* Windows SChannel — which backs `SslStream` — does not support Ed25519 certificates for TLS, and Dart's BoringSSL-backed TLS is likewise happiest with P-256. ECDSA P-256 is supported end to end on both platforms with first-class API support (`ECDsa` + `CertificateRequest` in .NET), and offers equivalent security for this threat model. Ed25519 would have forced a non-TLS handshake — exactly the hand-rolled crypto this design refuses.
- Transport is TLS with mutual certificate authentication, no renegotiation, and no session tickets reused across pairings. **TLS 1.3 is used wherever the OS provides it; TLS 1.2 restricted to ECDHE + AES-GCM/ChaCha20-Poly1305 is the floor.**

  *Corrected from "TLS 1.3 only" during Phase 2.* SChannel only gained TLS 1.3 in Windows 11 / Server 2022; Windows 10 (including 22H2) caps at TLS 1.2, so a 1.3-only requirement would make the system non-portable on a large share of target PCs — a direct violation of §0. Because certificates are **pinned**, TLS 1.2's real weaknesses do not apply here: there is no CA to mis-issue, no downgrade path to a weaker credential, and no RSA key exchange. Forward secrecy, AEAD and mutual authentication are all present at 1.2. The negotiated protocol is probed at runtime, reported in `device.info`, and surfaced in the Windows UI.
- There is no CA, and CA validation is deliberately replaced by **certificate fingerprint pinning** on both sides: a certificate is trusted if and only if `SHA-256(DER)` matches a stored pairing record.

  Fingerprint is defined as the SHA-256 of the whole DER-encoded certificate (base64url on the wire, colon-hex short form in UI) rather than of the SPKI. Reason: the mobile side can compute it from `X509Certificate.der` with no ASN.1 parsing and therefore no extra dependency. Certificates are long-lived (10 years) and rotation is handled explicitly by an authenticated, old-key-signed `identity.rotate` command, so a fingerprint that changes with the certificate rather than with the key costs nothing.
- Media uses **DTLS-SRTP**; the remote fingerprint is delivered inside the already-authenticated control channel, so an attacker cannot substitute a peer without first breaking TLS.

### 7.2 Session tokens

Short-lived (10 min) opaque random tokens bound to the device id, the TLS channel binding (exporter value), and a permission snapshot. Renewed over the live channel. A token presented on a different connection is invalid, so a stolen token is not a usable credential on its own. Revoking a device invalidates its tokens immediately.

### 7.3 Attack by attack

| Attack | Mitigation |
|---|---|
| Unauthorized LAN user connects | TLS client-certificate auth against the pairing store; an unknown fingerprint is dropped before any command parsing. No anonymous endpoint exists — discovery answers only with non-sensitive metadata. |
| MITM / on-path attacker | Mutual pinned-key TLS 1.3. The phone learns the PC's fingerprint out of band from the QR code, so there is no trust-on-first-use window. |
| Replay | TLS 1.3 sequencing + per-session monotonic `seq` + timestamp window + single-use pairing tokens. |
| Command spoofing / injection | Commands exist only inside an authenticated TLS session; the dispatcher accepts only registry-known names with schema-validated arguments. No string is ever passed to a shell, `cmd.exe`, or `ShellExecute` with client-controlled content. |
| Session hijacking | Tokens bound to the TLS channel; no bearer token works out of context; input state reset on any channel change. |
| Malicious pairing | Pairing requires all three of: pairing mode explicitly enabled (auto-expires, default 5 min), a QR-delivered single-use token, **and** an interactive local approval dialog showing the requesting device name, IP and key fingerprint. Headless approval is impossible. |
| Brute-force pairing | 128-bit token entropy, single use, 5-minute TTL, at most 5 attempts per window, exponential backoff per source IP, pairing mode auto-disabled after repeated failures, every attempt audited. |
| Privilege escalation via the agent | The session agent runs as the user at medium IL — it cannot drive elevated windows or write protected paths. Power commands go through the service's fixed, argument-free executors. The service never accepts a path, command line or script from the network. |
| Named-pipe squatting / local tampering | Pipes created with an explicit DACL (SYSTEM + the session's user SID only), `FILE_FLAG_FIRST_PIPE_INSTANCE`, and client-side verification of the server process identity. |
| Secret theft at rest | Pairing database and keys under `%ProgramData%\PCRemote` with inherited ACLs removed and DPAPI/CNG machine-scope protection. Mobile secrets in hardware-backed storage, biometric-gated for sensitive permission groups. |
| Denial of service | Connection cap, per-IP rate limits, 1 MiB frame cap, bounded queues, handshake timeout, file-transfer concurrency cap. |

### 7.4 Permission model

Enforced in `AuthorizationGate` before dispatch, per paired device, toggleable in the Windows UI:

| Group | Covers |
|---|---|
| `ViewScreen` | `screen.*`, media offer, screenshots |
| `ControlInput` | `input.*` |
| `LaunchApps` | `app.launch`, `app.focus`, `app.close`, `browser.*` |
| `ManageFiles` | `file.*` |
| `PowerControls` | `system.lock`, `system.signout`, `system.sleep`, `system.restart`, `system.shutdown` |
| `Clipboard` | `clipboard.get`, `clipboard.set` |
| always allowed once paired | `device.info`, `system.state`, `ping`, `permissions.list` |

Defaults on first pairing: `ViewScreen` + `ControlInput` only; everything else is opt-in. `PowerControls` and `ManageFiles` additionally support an "ask me on the PC each time" mode.

### 7.5 Logging rules

**Log:** connection open/close with device id and IP, auth success/failure with reason, pairing lifecycle, permission grants and revocations, every command name + device + outcome + duration, service and session-agent lifecycle, capture/encode/network errors with codes.

**Never log:** Windows credentials of any kind, private keys, session or pairing tokens (log a short non-reversible id instead), clipboard contents, file contents, full file paths outside allowed roots (log the root alias plus relative name), typed text. Key **names** only, at debug level, off by default. A Serilog redaction enricher enforces this rather than relying on discipline at each call site.

---

## 8. Pairing architecture

### 8.1 Flow

```
PC                                          Mobile
──                                          ──────
"Allow pairing" ON (5 min window)
generate token T (128-bit CSPRNG, single-use)
render QR:
  { "v":1, "id":"<deviceId>", "n":"<pcName>",
    "fp":"<SPKI SHA-256 b64u>", "t":"<T>",
    "p":47800, "h":["192.168.1.20"] }
                                            scan QR
                                            TLS connect, pin fp  ──────────────►
                                            (own client cert presented)
◄─── pair.request { deviceId, name, platform, clientFp, token: T }
verify T (valid, unused, unexpired, attempts OK)
verify clientFp == TLS peer fingerprint
  ┌─────────────────────────────────────────┐
  │ Local approval dialog                   │
  │  "Pixel 8 (192.168.1.44) wants access"  │
  │  fingerprint: 4F:2A:…  [Deny] [Allow]   │
  └─────────────────────────────────────────┘
on Allow: burn T, store record
  { deviceId, name, clientFp, pairedAt, permissions = default }
────────────────────────────────────────────────► pair.accepted
                                            { pcDeviceId, pcFp, permissions, protoVersion }
                                            persist record in secure storage
```

Note what the QR does **not** contain: no password, no long-term secret, no permission grant. The token alone is useless without also passing the local approval dialog, and the fingerprint is public information whose only job is to eliminate the trust-on-first-use window.

### 8.2 Reconnect

No QR. TLS mutual auth resolves the client fingerprint to the stored record; the PC updates `lastSeen` and issues a fresh session token. If the record is missing or revoked: `E_NOT_PAIRED`, and the mobile app moves that device back to "needs pairing".

### 8.3 Paired Devices page (Windows UI)

Per device: name (editable), platform/model, short key fingerprint, paired date, last connection, current status (connected / idle / never), permission toggles, connection history, **Revoke** (immediate — record deleted, tokens invalidated, live session dropped) and **Rename**. Plus a global "revoke all" and the "Allow pairing" toggle with a countdown.

### 8.4 Key rotation and recovery

- Either side may rotate its identity key; the rotation is announced over an authenticated session and signed by the old key, so no re-pairing is needed.
- Phone lost → revoke on the PC. PC reinstalled → its key changes, every phone sees a fingerprint mismatch and refuses to connect until re-paired, which is the correct loud failure.

---

## 9. Streaming architecture

### 9.1 Capture: Desktop Duplication primary, WGC secondary

| | Desktop Duplication (DXGI 1.2) | Windows.Graphics.Capture |
|---|---|---|
| Availability | Windows 8+, including the Win10 22H2 target | Win10 1803+; the nicer knobs need 1903/2004/Win11 |
| Output | Full monitor, GPU texture, **dirty rects + move rects + cursor metadata separately** | Monitor or single window, GPU texture, no dirty rects |
| Capture indicator | None | A capture border on newer builds; suppressing it needs `IsBorderRequired` (Win11 22000+) |
| Per-window capture | No | Yes |
| Multi-GPU / driver resets | Must handle `ACCESS_LOST` and hybrid-GPU output ownership | Handled by the framework |

**Decision:** Desktop Duplication is the primary path. It is full-monitor capture on Windows 10 22H2, and the dirty-rect metadata is genuinely useful — it drives encoder ROI hints and lets us skip idle frames entirely, which is a large CPU/GPU and bandwidth win on a mostly-static desktop. WGC sits behind the same `IScreenCapture` interface as the fallback and future per-window path, because it recovers more gracefully from mode changes and is the strategic API on Windows 11.

Neither can capture the secure desktop — see §12.

### 9.2 Encode pipeline

```
DXGI AcquireNextFrame → ID3D11Texture2D (BGRA)
  → [if scaling needed] D3D11 VideoProcessor or compute shader → NV12 texture
  → Media Foundation H.264 encoder MFT bound to the same D3D11 device
     (NVENC / Intel QuickSync / AMD AMF chosen by MFT enumeration)
     low-latency config: no B-frames, slice-per-row, CBR or CQP,
     infinite GOP with on-demand IDR, correct VUI range flags
  → Annex-B bitstream → RTP packetizer (STAP-A / FU-A) → SRTP → UDP
  Fallback chain: vendor MFT → generic MFT → OpenH264 software (720p30 cap, flagged in UI)
```

Zero-copy from capture texture to encoder input wherever the MFT accepts D3D11 surfaces; no CPU readback on the fast path. When the encoder falls behind, frames are **dropped, never queued** — latency beats completeness for interactive control.

### 9.3 Codec choice

| Codec | Verdict |
|---|---|
| **H.264** | **Primary.** Universal hardware encode on any PC GPU from the last decade, universal hardware decode on Android and iOS, first-class in libwebrtc and therefore in `flutter_webrtc`. Best latency per engineering hour. |
| H.265 | Better quality per bit, but libwebrtc / `flutter_webrtc` support is inconsistent and mobile negotiation is fiddly. Deferred: it would need a custom MediaCodec/VideoToolbox decode route through a platform channel plus a Flutter external texture. |
| AV1 | Best compression, but hardware encode needs RTX 40+ / Arc / RDNA3, mobile hardware decode is still patchy, and realtime software AV1 is not worth the CPU on a LAN. Behind a capability flag, later. |

On a LAN bandwidth is cheap and latency is the product. H.264 at 15–40 Mbit/s for 1080p60 is comfortable and keeps every hardware path on the happy road.

### 9.4 Transport: WebRTC

Chosen for what comes for free: DTLS-SRTP encryption, NACK/PLI/FIR loss recovery, RTCP receiver reports to feed the adaptive loop, congestion control, jitter buffering, and a battle-tested mobile hardware-decode path via `flutter_webrtc`. ICE is restricted to **host candidates only** — no STUN, no TURN, no external service of any kind.

**.NET WebRTC library trade-off** (to be settled by a spike at the start of Phase 3):

| Option | Pros | Cons |
|---|---|---|
| **SIPSorcery** (pure C#) | No native build, solid DataChannel + RTP/SRTP, easy to feed an external encoder's Annex-B output, actively maintained | Congestion control is simpler than libwebrtc's; we must drive pacing and bitrate adaptation ourselves |
| **libdatachannel** via P/Invoke | Small, fast, solid DTLS/SRTP/SCTP, easy C API to bind | A native binary to build and ship per architecture; still bring-your-own pacing |
| **libwebrtc** via a native shim | Reference-grade congestion control and loss recovery | Heavy build, large binary, awkward custom-encoder injection, big maintenance surface |

Leaning **SIPSorcery**: we want to own the adaptive controller anyway, because a LAN's objectives differ from libwebrtc's internet-tuned defaults, and avoiding a native toolchain keeps the build simple. The `IMediaTransport` interface keeps the decision reversible.

**Decided in Phase 3: SIPSorcery.** The spike passed, and the stack was then verified against libwebrtc itself (headless Chrome) streaming this PC's real desktop: 1080p, zero loss. Results, and the reasons `IMediaTransport` was not built, are in [03-phase3-streaming-notes.md §1.1](03-phase3-streaming-notes.md).

### 9.5 Adaptive controller

Inputs: RTCP loss, jitter and RTT; encoder queue depth; capture-to-send latency; client-reported decode time and render FPS; frame dirty ratio.

Outputs, in order of preference: bitrate → FPS cap → resolution scale → codec profile. Targets on a healthy LAN: 1080p60 when content moves, near-zero bitrate when the desktop is static (unchanged frames are skipped entirely), glass-to-glass under roughly 60 ms. Degradation ladder: 1080p60 → 1080p30 → 900p30 → 720p30 → 720p20 → explicitly-labelled degraded mode.

Telemetry surfaced in the mobile UI: FPS, latency in ms, bitrate, loss %, codec, hardware-vs-software encoder, and a single green/amber/red quality indicator.

### 9.6 Multi-monitor

`screen.monitor_list` returns, per monitor: id, adapter, device name, friendly name, bounds in virtual-desktop coordinates, DPI scale, refresh rate, primary flag, rotation. `screen.select_monitor` re-targets duplication and renegotiates track resolution. Hot-plug raises `monitors.changed`. Phase 3 streams one monitor at a time (lowest latency and CPU); a combined virtual-desktop mode is a later option.

---

## 10. Windows API requirements

| Capability | APIs | Process | Notes |
|---|---|---|---|
| Service host | `Microsoft.Extensions.Hosting.WindowsServices`, SCM, `RegisterServiceCtrlHandlerEx` | Service | Delayed auto-start + recovery actions |
| Session enumeration / launch | `WTSEnumerateSessions`, `WTSQueryUserToken`, `WTSGetActiveConsoleSessionId`, `DuplicateTokenEx`, `CreateEnvironmentBlock`, `CreateProcessAsUser` | Service | The core of crossing Session 0 isolation |
| Session events | `SERVICE_ACCEPT_SESSIONCHANGE`, `WTS_SESSION_*` | Service | Lock/unlock/logon/logoff/console-connect |
| Lock | `LockWorkStation` (session), `WTSDisconnectSession` (service) | Session / Service | `LockWorkStation` must run in the interactive session |
| Sign out | `WTSLogoffSession`, or `ExitWindowsEx(EWX_LOGOFF)` | Service / Session | The service path works even with no session agent |
| Sleep | `SetSuspendState` (powrprof) with `SeShutdownPrivilege`; `GetPwrCapabilities` | Service | Capability reported, not assumed |
| Restart / shutdown | `InitiateShutdownW` / `ExitWindowsEx` with `SE_SHUTDOWN_NAME` enabled | Service | Reason codes set; grace period and abort supported |
| Wake capability | `GetAdaptersAddresses` for MACs, device-wake queries, Fast Startup registry state | Service | Report-only; the magic packet comes from the phone |
| Capture | `IDXGIOutputDuplication` (`AcquireNextFrame`, `GetFrameDirtyRects`, `GetFrameMoveRects`, `GetFramePointerShape`), `Windows.Graphics.Capture` + `Direct3D11CaptureFramePool` | Session | Requires `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)` |
| Encode | Media Foundation (`MFTEnumEx`, `IMFTransform`, `MFCreateDXGIDeviceManager`), D3D11 | Session | Vendor SDKs (NVENC/AMF/oneVPL) only if MFT proves insufficient |
| Input | `SendInput` (`INPUT_MOUSE` / `INPUT_KEYBOARD`, `MOUSEEVENTF_ABSOLUTE\|VIRTUALDESK`, `KEYEVENTF_UNICODE`, `KEYEVENTF_SCANCODE`), `GetSystemMetrics(SM_*VIRTUALSCREEN)`, `MapVirtualKey`, `VkKeyScanEx` | Session | Absolute coordinates are 0–65535 across the **virtual desktop**, not per monitor |
| Window / process control | `EnumWindows`, `GetWindowThreadProcessId`, `SetForegroundWindow`, `AllowSetForegroundWindow`, `PostMessage(WM_CLOSE)`, `QueryFullProcessImageName` | Session | Graceful then forced close |
| Installed apps | Start Menu `.lnk` enumeration via `IShellLink`, `HKLM`/`HKCU` Uninstall keys, `Windows.Management.Deployment.PackageManager`, `shell:AppsFolder\<AUMID>`, `SHGetFileInfo` / `ExtractIconEx` | Session | UWP launch via `IApplicationActivationManager` or `explorer.exe shell:AppsFolder\<AUMID>` |
| Browser detection | `HKCU\...\UrlAssociations\https\UserChoice`, App Paths, `AssocQueryString` | Session | Never build a command line from client input |
| Clipboard | `OpenClipboard` / `GetClipboardData` / `SetClipboardData`, `AddClipboardFormatListener` | Session | Needs a message pump plus retry — the clipboard is a shared lock |
| Volume | Core Audio `IMMDeviceEnumerator`, `IAudioEndpointVolume`, `IMMNotificationClient` | Session | STA COM |
| Secrets at rest | CNG (`NCryptCreatePersistedKey`), DPAPI (`ProtectedData`, machine scope), explicit DACLs | Service | Non-exportable keys where the provider supports it |
| Discovery | `DnsServiceRegister` / `DnsServiceBrowse` (Win10 1703+) or a managed mDNS library; `UdpClient` broadcast | Service | A managed library is more portable and testable |
| Firewall | `INetFwPolicy2` COM, or `netsh advfirewall` at install time | Installer (elevated) | Private profile only |
| Autostart of the tray UI | `HKCU\...\Run` or a per-user scheduled task | UI (non-elevated) | The **service** is what autostarts the system; the tray UI is a convenience |

---

## 11. Technology choices

| Layer | Choice | Rationale | Rejected alternatives |
|---|---|---|---|
| Windows runtime | **.NET 8 (LTS), C# 12** | First-class Windows service hosting, DI, async, `Microsoft.Windows.CsWin32` for generated P/Invoke, strong structured logging, and a memory-safe network parser. Native AOT for the service is a later option. | C++/WinRT: best API access, far slower to build safely, and a memory-safety liability in the network parser. Rust: excellent safety and capture crates, but weaker WPF/Media Foundation ergonomics. Node/Python: wrong tool for a service plus realtime encode. |
| Capture | **DXGI Desktop Duplication**, WGC fallback | §9.1 | Repeated GDI `BitBlt` screenshots: high CPU, no dirty rects, explicitly excluded as a primary path |
| Encode | **Media Foundation H.264 MFT** (NVENC/QSV/AMF) + OpenH264 software fallback | One code path across all three GPU vendors; zero-copy from D3D11 | Vendor SDKs directly: three code paths — kept as an escape hatch. FFmpeg: a 30 MB+ native dependency for what MFT already does |
| Media transport | **WebRTC** (SIPSorcery, confirmed by the Phase 3 spike) | §9.4 | Raw RTP/UDP: hand-rolling SRTP, NACK, pacing and congestion control. RTSP: no built-in security, poor mobile story |
| Control transport | **TLS (1.3 where available, 1.2 ECDHE+AEAD floor) over TCP**, framed JSON | `SslStream` gives mutual auth and channel binding with zero dependencies; JSON keeps the protocol debuggable, and MessagePack is a drop-in later if framing cost matters | HTTPS + WebSocket: adds an HTTP surface we do not need and complicates client-cert auth. gRPC: heavier, and its streaming semantics fit the event model poorly |
| Management UI | **WPF (.NET 8)** | Runs on Win10 22H2 with zero runtime prerequisites, mature, trivial interop, easy MVVM, small installer | WinUI 3: nicer visuals but drags Windows App SDK deployment onto a Win10 target for no functional gain. WinForms: weaker binding. Electron: absurd footprint for a settings panel |
| Discovery | **mDNS/DNS-SD** + signed UDP fallback | Zero-config, already spoken by phones, good Flutter plugins; the fallback covers multicast-filtered networks | SSDP: legacy and noisy. Broadcast only: does not survive mDNS-only or routed networks |
| Crypto | **ECDSA P-256** identity certificates, AEAD via TLS, HKDF, SHA-256 certificate fingerprints | The only modern curve supported for TLS by *both* Windows SChannel and mobile BoringSSL, with hardware keystore support on Android and iOS | Ed25519: unsupported by SChannel for TLS (see §7.1). Raw RSA: bigger, slower, no benefit. A custom handshake: never hand-roll one when TLS exists |
| IPC | **Named pipes** with explicit DACLs, reusing the wire envelope format | Kernel-enforced ACLs, no port, reuses the protocol serializer and its tests | Loopback TCP: an unnecessary local attack surface. COM: heavier and harder to secure correctly |
| Persistence | JSON config plus a small embedded store (JSON, or SQLite only if history volume justifies it) for pairings, history and audit | Human-inspectable, no server dependency | Registry for structured data: awkward and hard to back up |
| Logging | **Serilog** → rolling JSON files + Windows Event Log for lifecycle and security events | Structured and queryable, with a redaction enricher enforcing §7.5 | Plain text files: unqueryable. ETW: great, but poor ergonomics at this scale |
| Mobile | **Flutter 3.44 / Dart 3.12** (already installed) | One codebase; `flutter_webrtc` is mature and gives hardware H.264 decode into a GPU texture; excellent custom-gesture and painter story for the remote-desktop surface; strong secure-storage and mDNS plugins | React Native: WebRTC is fine but the high-FPS gesture surface and texture rendering are noticeably more painful, with no upside here. Native twice: double the work for one user |
| Mobile state | Riverpod with immutable state; `freezed` DTOs generated from the shared protocol schema | Testable, and keeps connection state machines explicit | Ad-hoc `setState`: unmanageable for multi-channel connection state |
| Installer | MSI (WiX) or MSIX | Service registration, ACLs, firewall rule, clean uninstall, code signing | Hand-rolled `sc create` scripts: fragile, unsigned, no clean uninstall |

**Prerequisite gap:** the .NET 8 SDK is **not installed** on this machine (Flutter 3.44.5 / Dart 3.12.2 is). Phase 2 begins with installing .NET 8 LTS and the Windows 10 SDK.

---

## 12. Important Windows limitations

These are OS-enforced. None can be worked around without shipping something insecure, and the design accounts for each.

### 12.1 The secure desktop and the lock screen — the big one

Winlogon's secure desktop (the lock screen, Ctrl+Alt+Del, and UAC consent prompts) lives on a separate desktop in a separate window station, protected specifically so that no ordinary process can read or write it. Concretely:

- **Capture stops.** Desktop Duplication returns `DXGI_ERROR_ACCESS_LOST` when the session switches to the secure desktop, and WGC cannot capture it either. The stream must detect this and report "locked" rather than showing a frozen last frame.
- **Input cannot be injected.** `SendInput` targets the calling thread's desktop, and a medium-IL user process cannot attach to Winlogon's. The phone therefore cannot type a password into the lock screen.
- **UAC prompts are invisible and uncontrollable.** With the default secure-desktop prompting, a consent dialog blanks the remote view and swallows input until someone acts *physically* at the PC. Mitigation: detect the desktop switch and show an explicit "UAC prompt on the PC — action required at the machine" banner. We will **not** recommend disabling secure-desktop prompting.
- **Elevated windows ignore injected input.** User Interface Privilege Isolation blocks a medium-IL process from sending input to a high-IL window, so remotely clicking a button inside an elevated app (admin Task Manager, regedit, an installer) does nothing. The Microsoft-sanctioned escape is a `uiAccess="true"` manifest on the session agent, which requires the binary to be Authenticode-signed and installed under `%ProgramFiles%`. That grants input to elevated windows on the *user's* desktop; it still does **not** grant access to the secure desktop. Plan: ship unsigned with the limitation documented in Phase 4, and enable `uiAccess` once code signing exists.

**Conclusion on remote unlock.** Locking is fully supported (`LockWorkStation`). *Unlocking* from the phone is not achievable by a normal application. The only architecturally correct mechanism is a **Windows Credential Provider** — an in-process COM DLL loaded by `LogonUI.exe` on the secure desktop, which can present a custom credential tile and complete authentication through the real `LsaLogonUser` path. Such a provider could in principle accept a cryptographically signed approval from the paired phone (the same Ed25519 identity, challenge-response — conceptually what Windows Hello companion devices did before that framework was deprecated) and then hand LSA a genuine credential.

That is a serious undertaking with a serious risk surface: native C++ COM running inside the logon UI, where a crash can make the machine unloggable and a flaw is a full authentication bypass. It also requires credential handling that never stores a reusable password, which in practice means enrolling the phone as a real Windows Hello factor or brokering a proper LSA logon.

**Decision: out of scope for this project.** The phone can lock, sleep, restart, shut down, sign out and wake the PC, and can resume control *after* a human unlocks it at the keyboard. We will not store the Windows password, will not type it automatically, and will not build any bypass. The mobile UI states this plainly: *"Unlocking requires you at the PC — by design."* If remote unlock is ever pursued it becomes its own independently reviewed sub-project (`RemoteAgent.CredentialProvider`, native C++), not a feature bolted onto this one.

### 12.2 Other limitations

| Limitation | Impact | Handling |
|---|---|---|
| Session 0 isolation | A service cannot capture, inject input, or touch the clipboard | The service/session-agent split (§3, §4) exists precisely for this |
| No interactive services | A service cannot show UI or prompts | Approval dialogs belong to the tray UI; the service asks it over IPC |
| No logged-on user | Capture, input, apps, clipboard and files are unavailable at the logon screen | Those commands return `E_SESSION_UNAVAILABLE`; power, status and WoL still work |
| Fast user switching / RDP | Multiple sessions exist; only one is the console session | The service tracks sessions, targets the active console session, and reports changes |
| Display asleep or monitor powered off | Duplication may stall or deliver no frames | Detect and report; optionally wake the display via input; never silently freeze |
| Protected/DRM content | Protected surfaces may capture as black | Documented as expected behaviour, not a bug to fix |
| Hybrid GPUs | Duplication must run on the adapter that owns the output | Enumerate adapter→output ownership and recreate on change |
| Driver reset or resolution change | `ACCESS_LOST` / `UNSUPPORTED` | Rebuild the duplication object with backoff, then fall back |
| Wake-on-LAN reality | With Fast Startup enabled, "shut down" is a hybrid S4 and WoL often fails; S5 wake needs explicit NIC support; wireless WoWLAN rarely works | Detect and report actual capability; prefer Sleep (S3) when wake matters; explain *why* WoL is unavailable instead of failing mysteriously |
| Windows Firewall | Blocks the listener by default | The installer adds narrow Private-profile rules; the UI shows rule status |
| mDNS on mobile | Some Android Wi-Fi stacks and battery optimizers drop multicast; iOS needs the local-network permission and a Bonjour usage declaration | UDP fallback plus clear permission prompts |
| Mobile background limits | Android Doze and iOS backgrounding kill sockets | Streaming is a foreground activity; reconnect on resume; no pretence of background control |
| DPI and negative coordinates | Multi-monitor layouts have negative origins and mixed scaling | Per-monitor-v2 DPI awareness and virtual-desktop-space math everywhere |
| `SetForegroundWindow` restrictions | Windows refuses focus changes from background processes | `AllowSetForegroundWindow` / attach-thread-input techniques, with honest failure reporting |
| Store-app (UWP) lifecycle | Cannot be closed reliably with `WM_CLOSE` | Use the app-lifecycle/AUMID path and report partial support |

---

## 13. Recommended folder structure

```
/pc-control
├─ README.md
├─ .editorconfig  .gitignore  .gitattributes
│
├─ /shared
│   ├─ /protocol                      single source of truth for the wire format
│   │   ├─ protocol.schema.json        commands, events, errors, permissions
│   │   ├─ CHANGELOG.md                protocol version history
│   │   └─ /tools/generate.ps1         → C# DTOs + Dart freezed models
│   └─ /docs
│       ├─ 01-architecture.md          (this document)
│       ├─ 02-protocol.md              command reference
│       ├─ 03-security.md              threat model, key management, review notes
│       ├─ 04-streaming.md             capture/encode/adaptive design notes
│       ├─ 05-windows-limitations.md   the user-facing version of §12
│       └─ 06-operations.md            install, firewall, logs, troubleshooting
│
├─ /windows
│   ├─ PCRemote.sln
│   ├─ Directory.Build.props           shared TFM, nullable, analyzers, version
│   │
│   ├─ /RemoteAgent.Protocol           envelopes, DTOs, permissions, versioning (no I/O)
│   ├─ /RemoteAgent.Core               domain models, interfaces, config, results
│   │     /Abstractions  /Configuration  /Commands  /Diagnostics
│   ├─ /RemoteAgent.Security           identity, TLS setup, pairing, tokens, rate limits,
│   │                                  secret protection, redaction enricher
│   ├─ /RemoteAgent.Windows            the ONLY P/Invoke project (CsWin32 generated)
│   │     /Session  /Power  /Input  /Shell  /Audio  /Clipboard  /Display  /Network
│   ├─ /RemoteAgent.Streaming          capture, encode, RTP/WebRTC, adaptive controller
│   │     /Capture  /Encoding  /Transport  /Adaptation
│   ├─ /RemoteAgent.Ipc                named-pipe server/client, DACLs, framing
│   │
│   ├─ /RemoteAgent.Service            SYSTEM host: listener, discovery, dispatcher,
│   │                                  pairing store, power executors, session manager
│   ├─ /RemoteAgent.Session            user host: capture, input, shell, media peer
│   ├─ /RemoteAgent.UI                 WPF management UI + tray
│   │     /Views  /ViewModels  /Services
│   │
│   ├─ /RemoteAgent.Installer          WiX/MSIX: service reg, ACLs, firewall, signing
│   └─ /tests
│         /RemoteAgent.Protocol.Tests     round-trip, version negotiation, parser fuzzing
│         /RemoteAgent.Security.Tests     pairing state machine, token binding, rate limits
│         /RemoteAgent.Core.Tests         dispatcher, authorization matrix, validation
│         /RemoteAgent.Windows.Tests      coordinate mapping, path sandboxing, URL validation
│         /RemoteAgent.Integration.Tests  service↔session IPC, end-to-end handshake
│
└─ /mobile
    └─ /remote_control_app
        ├─ pubspec.yaml
        └─ /lib
            ├─ main.dart
            ├─ /app                  routing, theme, bootstrap
            ├─ /core                 result types, logging, errors, extensions
            ├─ /protocol             generated DTOs + codec (mirrors /shared/protocol)
            ├─ /data
            │    /discovery  /control  /media  /files  /identity  /storage
            ├─ /domain               entities, permission model, connection state machine
            └─ /features
                 /home  /devices  /pairing  /remote_desktop  /apps
                 /files  /system  /settings  /logs
```

**Boundary rules** (enforced by review, and by analyzer rules where possible)

1. `RemoteAgent.Windows` is the only project that may reference Win32/WinRT/COM. Everything else depends on interfaces from `Core`.
2. `Protocol` depends on nothing. `Core` depends only on `Protocol`. `Security` depends on `Core` + `Protocol`.
3. Hosts (`Service`, `Session`, `UI`) contain composition and hosting only — no business logic, no P/Invoke.
4. No project references another host project.
5. Every command is a DI-registered `ICommandHandler` declaring its name, permission group, argument schema and target (service-local or session-forwarded). Adding a command must never mean editing a switch statement.
6. All I/O is async; every handler takes a `CancellationToken`.
7. No hardcoded ports, paths or device names — everything through `IOptions<T>` bound to configuration.
8. Ship no command that accepts an executable path, command line or script from the network.

---

## Appendix A — Command registry (Phase 2 baseline)

| Command | Permission | Target | Args |
|---|---|---|---|
| `ping` | — | service | — |
| `device.info` | — | service | — |
| `system.state` | — | service | — |
| `permissions.list` | — | service | — |
| `system.lock` | PowerControls | session → service fallback | — |
| `system.signout` | PowerControls | service | `{ force?: bool }` |
| `system.sleep` | PowerControls | service | — |
| `system.restart` | PowerControls | service | `{ delaySec?: int, force?: bool }` |
| `system.shutdown` | PowerControls | service | `{ delaySec?: int, force?: bool }` |
| `system.abort_shutdown` | PowerControls | service | — |
| `app.list` | LaunchApps | session | `{ runningOnly?: bool, query?: string }` |
| `app.launch` | LaunchApps | session | `{ appId: string }` — registry id only, never a path |
| `app.focus` | LaunchApps | session | `{ appId: string }` |
| `app.close` | LaunchApps | session | `{ appId: string, force?: bool }` |
| `browser.list` | LaunchApps | session | — |
| `browser.open` | LaunchApps | session | `{ browserId?: string }` |
| `browser.open_url` | LaunchApps | session | `{ url: string, browserId?: string }` — http/https only |
| `browser.close` | LaunchApps | session | `{ browserId?: string }` |

Phase 3 adds `screen.monitor_list`, `screen.select_monitor`, `screen.screenshot`, `media.offer` / `ice` / `stop`, `media.set_quality`. (The answer is the response to `media.offer`, not a separate command — see [03-phase3-streaming-notes.md §1.4](03-phase3-streaming-notes.md).)
Phase 4 adds `input.mouse_move`, `input.mouse_button`, `input.mouse_click`, `input.scroll`, `input.key`, `input.text`, `input.shortcut`, `input.release_all`.
Phase 5 adds `clipboard.get` / `set`, `file.list` / `upload` / `download` / `cancel`, `volume.get` / `set` / `mute`, `wol.info`, `device.history`, `permissions.set`.

Events: `system.state_changed`, `session.state_changed`, `monitors.changed`, `clipboard.changed`, `app.state_changed`, `transfer.progress`, `media.quality`.

## Appendix B — Configuration skeleton

```jsonc
// %ProgramData%\PCRemote\config.json   (machine scope, ACL: SYSTEM + Administrators)
{
  "device":    { "name": "<computer name>", "id": "<generated GUID>" },
  "network":   { "controlPort": 47800, "bindInterfaces": ["lan"],
                 "subnetAllowlist": [], "mediaUdpPortRange": [47810, 47850],
                 "maxConnections": 4 },
  "discovery": { "enabled": true, "mdns": true, "udpFallbackPort": 47801,
                 "serviceType": "_pcremote._tcp", "ttlSeconds": 120 },
  "pairing":   { "allowPairing": false, "windowMinutes": 5, "maxAttempts": 5,
                 "requireLocalApproval": true },
  "streaming": { "maxWidth": 1920, "maxHeight": 1080, "fpsLimit": 60,
                 "minFps": 15, "bitrateKbps": { "min": 2000, "max": 40000 },
                 "codecPreference": ["h264"], "hardwareEncode": "auto",
                 "defaultMonitorId": null, "captureCursor": true },
  "files":     { "allowedRoots": [
                   { "alias": "Downloads", "path": "%USERPROFILE%\\Downloads", "write": true },
                   { "alias": "Documents", "path": "%USERPROFILE%\\Documents", "write": false },
                   { "alias": "Desktop",   "path": "%USERPROFILE%\\Desktop",   "write": false }],
                 "maxFileSizeMb": 2048,
                 "blockedExtensions": [".ps1", ".bat", ".cmd", ".scr"] },
  "power":     { "allowShutdown": true, "allowRestart": true, "allowSleep": true,
                 "allowSignout": true, "confirmOnPc": false },
  "wol":       { "adapters": [] },   // discovered at runtime, cached for reporting
  "logging":   { "level": "Information", "retainDays": 14, "maxFileMb": 20,
                 "eventLogSecurityEvents": true },
  "startup":   { "serviceAutoStart": true, "trayUiAtLogon": true }
}
```

Per-device permissions live in the pairing store, not here. Nothing in this file is a secret; keys and pairing records are stored separately and protected.

---

## Phase gate — what Phase 2 does, in order

1. Install the .NET 8 SDK and Windows 10 SDK (currently missing on this machine).
2. Solution skeleton with the project boundaries above plus the analyzer and DI conventions.
3. `Protocol` + `Core`, the command registry and the authorization gate, with tests, **before** any networking.
4. Security: identity keys, TLS 1.3 mutual auth with fingerprint pinning, the pairing state machine, the pairing store.
5. Service host: listener, dispatcher, session manager, session-agent spawning, power executors.
6. Session agent: app registry, launch/focus/close, browser control, lock.
7. WPF UI: dashboard, pairing QR, paired devices, permissions, logs.
8. Flutter app: discovery, pairing, device list, status, system controls, apps screen.
9. Installer: service registration, ACLs, firewall rule.

Open questions to settle before **Phase 3** (not blocking Phase 2): the WebRTC library spike (SIPSorcery vs libdatachannel), whether code signing and `uiAccess` are in scope, and whether SQLite is warranted for audit history.
