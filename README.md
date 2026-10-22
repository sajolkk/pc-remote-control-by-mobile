# PC-Remote

Securely monitor and control a Windows PC from a mobile app, over your own LAN. No cloud service, no
port forwarding, no account.

> **Status: Phase 4 implemented.** Discovery, pairing, authentication, status, power control,
> application and window management, browser control, live screen streaming, remote mouse and
> keyboard, and file management all work. Pairing, streaming and control have been run from an
> Android emulator against a real PC, and pairing and streaming on a physical phone. File transfer
> (upload/download) and clipboard sync are not implemented.

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
- control the PC from the Screen tab: tap to click, double-tap, long-press for right click, drag,
  two-finger scroll, a trackpad mode, and a keyboard with modifier keys and common shortcuts
- move a zoomed screen around with one finger using the Move tool on the Screen toolbar
- stay connected to several PCs at once: the Screen tab lists them and opens the one you pick, and
  the banner at the top switches which PC the System and Manage tabs work with
- list open windows and switch to, minimize, maximize, restore or close them
- browse the PC's folders and drives, and open, create, rename, copy, move and delete files
- reach the PC from any network (mobile data, another Wi-Fi) through your own VPN (Tailscale,
  WireGuard, ZeroTier) by giving the paired PC a remote address ([how](#from-any-network))
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
- **A fixed command allowlist.** Applications are launched by an id the PC itself generated, and
  file commands are confined to the allowed folders and drives. A device that may control the mouse
  and keyboard, or open files, can do what the signed-in user can, which is why those are
  permissions.
- **Permissions are granted on the PC, not requested by the phone.** A device starts with view and
  control only; apps, files, power and clipboard are opt-in: tick "full control" when approving the
  pairing, or tick them per device under Devices in the tray menu. Changes apply immediately.
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
  tests/                        642 tests, including real-TLS and loopback-WebRTC suites

/mobile/remote_control_app      Flutter 3.44 / Dart 3.12, Android and iOS

/shared/docs                    architecture and implementation notes
```

## Getting started

**Just want to use it?** Run [`PC-Remote-Setup.exe`](https://media.githubusercontent.com/media/sajolkk/pc-remote-control-by-mobile/main/artifacts/release/PC-Remote-Setup.exe)
on the PC and install [`PC-Remote.apk`](https://media.githubusercontent.com/media/sajolkk/pc-remote-control-by-mobile/main/artifacts/release/Android/PC-Remote.apk)
on the phone. In the app, tap **Pair** next to your PC and click **Allow** on the PC when both
screens show the same code. [Step by step](shared/docs/04-setup-and-connect.md).

> The binaries are stored in Git LFS. "Download ZIP", raw links, and a `git clone` without
> [Git LFS](https://git-lfs.com) give you a 133-byte pointer file instead of the program, and
> Windows reports it as "This app can't run on your PC". Use the links above, or run
> `git lfs install` then `git lfs pull` in your clone.

### From any network

Away from home, the phone reaches the PC through a VPN you run yourself; there is still no relay
server and no port forwarding. With Tailscale:

1. Pair the phone at home, on the same Wi-Fi as the PC (pairing needs you to click **Allow** on
   the PC).
2. Install [Tailscale](https://tailscale.com/download) on the PC and on the phone, and sign both
   in to the same account.
3. On the phone: **Devices → ⋮ next to the PC → Add remote address**, and enter the PC's
   Tailscale address (the `100.x.y.z` one shown in Tailscale on the PC).

The app tries the local network first and falls back to that address, on mobile data or any Wi-Fi.
The installer already allows Tailscale's range through the firewall. For WireGuard or ZeroTier,
add your VPN's subnet to `network.remoteAccessSubnets` and run the installer again.
[Details and troubleshooting](shared/docs/04-setup-and-connect.md#using-it-away-from-home).

To build from source you need the .NET 10 SDK, and Flutter for the mobile app.

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

Then tap **Pair** next to the PC in the app's Devices tab and allow it on the PC, or open PC-Remote
from the Start menu (or the tray icon) and scan its QR code. `windows/installer/build-installer.ps1`
builds `PC-Remote-Setup.exe`.

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
