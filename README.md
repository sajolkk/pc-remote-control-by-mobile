# PC-Remote

Securely monitor and control a Windows PC from a mobile app, over your own LAN. No cloud service, no
port forwarding, no account.

> **Status: Phase 3 implemented.** Discovery, pairing, authentication, status, power control,
> application launching, browser control and live screen streaming all work on the PC side. The
> stream has been verified end to end against libwebrtc (the engine inside the mobile app's WebRTC
> library) but **not yet on a physical phone**. Remote input is Phase 4 and is not implemented — see
> the [Phase 2](shared/docs/02-implementation-notes.md) and
> [Phase 3](shared/docs/03-phase3-streaming-notes.md) notes for exactly what does and does not exist.

## What it does today

From your phone, on the same Wi-Fi:

- find your PC automatically and see whether it is online, locked, or signed out
- pair once by scanning a QR code, then reconnect without scanning again
- see what Windows it runs, its addresses, and how the connection is encrypted
- lock, sign out, sleep, restart, shut down, and cancel a pending shutdown
- browse installed applications with their icons, and launch, focus or close them
- open a browser, or open a specific URL
- watch the PC's screen live — pinch to zoom, switch monitors, pick a quality, take a screenshot —
  with hardware H.264 encoding where the GPU has it and a software fallback where it does not
- see exactly which permissions the PC has granted this device

## What it deliberately will not do

**It cannot unlock your PC, and it never will.** Windows puts the lock screen on a separate protected
desktop that no ordinary application can draw on or send input to — that boundary is what stops any
program on your PC from capturing your password. The only sanctioned way in is a Windows Credential
Provider, which is a separate and much higher-risk project.

So: this app will never store your Windows password and never type it for you. Locking works;
unlocking needs you at the keyboard. The reasoning is in
[§12.1 of the architecture](shared/docs/01-architecture.md).

## How the security works

- **One listener, always authenticated.** A single TLS port that requires a client certificate. No
  status port, no debug port, nothing an unpaired device can reach except the pairing request itself.
- **Pinned certificates, no CA.** Your phone learns the PC's certificate fingerprint from the QR code
  before it ever connects, so there is no window in which something on your network can impersonate
  your PC.
- **Pairing needs three things at once**: pairing mode switched on at the PC, a single-use token from
  the QR code, and a human approving the request on the PC's own screen. Any one alone is useless —
  including to somebody who photographed your QR code.
- **Least privilege by construction.** The privileged service does power control and networking; a
  separate non-elevated agent touches the desktop; nothing runs elevated that does not have to.
- **No arbitrary execution, structurally.** Commands come from a fixed allowlist, and applications are
  launched by an id the PC itself generated — there is no field in the protocol through which a path
  or a command line could arrive.
- **Permissions are granted on the PC, not requested by the phone.** A device starts with view and
  control only; everything else is opt-in, and revoking drops a live connection immediately.
- **The video stream is bound to the authenticated connection.** It is negotiated over the pinned
  control channel, encrypted with DTLS-SRTP, sent only to the address that authenticated, uses no
  STUN or TURN server, and stops the moment that connection closes or loses permission to view.

## Layout

```
/windows                        .NET 10, Windows 10 1809+ and Windows 11
  RemoteAgent.Protocol          the wire format — no dependencies, no I/O
  RemoteAgent.Core              domain logic, command pipeline, config (platform-neutral)
  RemoteAgent.Security          identity, pairing, tokens, abuse limits, TLS policy
  RemoteAgent.Windows           the only project allowed to call Win32 (incl. DXGI capture, H.264)
  RemoteAgent.Streaming         screen-stream pipeline and WebRTC transport (platform-neutral)
  RemoteAgent.Ipc               named-pipe channel between the two hosts
  RemoteAgent.Diagnostics       structured logging with redaction shared by all hosts
  RemoteAgent.Service           the privileged host: LocalSystem, Session 0
  RemoteAgent.Session           the user-session host: desktop, tray icon, dialogs
  tests/                        173 tests, including real-TLS and loopback-WebRTC suites

/mobile/remote_control_app      Flutter 3.44 / Dart 3.12, Android and iOS

/shared/docs                    architecture and implementation notes
```

## Getting started

Requires the .NET 10 SDK, and Flutter for the mobile app.

```powershell
# PC side
dotnet build windows/PCRemote.slnx
dotnet test  windows/PCRemote.slnx

# Run without installing anything
RemoteAgent.Service.exe --console

# Or install the Windows service (elevated prompt)
RemoteAgent.Service.exe --install
```

```powershell
# Mobile side
cd mobile/remote_control_app
flutter test
flutter run
```

Then right-click the PC-Remote tray icon, choose **Allow pairing and show QR code**, and scan it from
the app's Devices tab.

[Full instructions, including portable mode](shared/docs/02-implementation-notes.md#4-running-it).

## Documentation

- [Setup and connecting](shared/docs/04-setup-and-connect.md) — step by step: install on the PC,
  install the app, pair once, connect, and troubleshooting.

- [Architecture](shared/docs/01-architecture.md) — the design: components, security model, pairing,
  streaming plan, Windows limitations, and why each technology was chosen.
- [Implementation notes](shared/docs/02-implementation-notes.md) — what was built, the seven design
  corrections reality forced, and the bugs that only showed up when it ran.
- [Phase 3 streaming notes](shared/docs/03-phase3-streaming-notes.md) — capture, encoding and
  WebRTC: the library spike, the media plane's security properties, measurements, and what is
  still unverified.
