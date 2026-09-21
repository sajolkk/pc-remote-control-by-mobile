# Phase 3 — Screen streaming: implementation notes

What was built for §9 of [01-architecture.md](01-architecture.md), the decisions the design left
open, and where the design was changed. Same convention as
[02-implementation-notes.md](02-implementation-notes.md): the architecture document stays the
design; this is where reality pushed back.

---

## 1. Decisions the design left open

### 1.1 WebRTC library: SIPSorcery

§9.4 left SIPSorcery vs libdatachannel vs libwebrtc to a spike. The spike ran two SIPSorcery peers
in one process — a receive-only "phone" offering H.264 and a sending "PC" answering — and it now
lives on as the loopback test suite rather than as throwaway code. What it established:

| Question | Result |
|---|---|
| Host candidates without STUN | Present in the SDP immediately; no trickle needed from the PC |
| Port range | Honoured: the PC side bound 47810, the first port of `mediaPortRange` |
| DTLS-SRTP | Completed in-process |
| H.264 packetization | A 30 KB IDR went out as FU-A fragments; 30/30 frames, 900,900 bytes, reassembled intact |
| RTCP feedback | A PLI from the receiver reached the sender, which is what drives on-demand keyframes |

**Then against libwebrtc itself.** No phone was attached to the development machine, so the
interoperability that matters — SIPSorcery talking to the engine `flutter_webrtc` wraps — was
tested against headless Chrome, which is libwebrtc. The PC side was the real pipeline: Desktop
Duplication, the NVIDIA hardware encoder, the adaptive controller and SIPSorcery. Result over 30 s:
**946 frames decoded at 1920×1080, zero packets lost, one keyframe**, RTCP round-trip time
measured at 0.5 ms. That is the strongest evidence available short of a phone, and it is not the
same as a phone — see §6.

libdatachannel and libwebrtc were not needed: the risk they were insurance against (SIPSorcery
not interoperating with libwebrtc) did not materialise, and both mean shipping a native binary per
architecture. The `IMediaTransport` indirection the architecture proposed was not built — the
SIPSorcery surface used is small and confined to `MediaSession`, so replacing it later is a change
to one class.

### 1.2 COM interop through Vortice

`RemoteAgent.Windows` hand-writes its P/Invoke, and the Phase 2 notes treat that as a virtue: small
enough to read in full. That does not scale to Direct3D 11, DXGI duplication and Media Foundation,
which are COM vtable APIs with hundreds of methods between them. **Vortice** (`Vortice.Direct3D11`,
`Vortice.DXGI`, `Vortice.MediaFoundation`, 3.8.3) is a source-generated managed projection with no
native binaries of its own. It is the one new dependency class in this phase.

It does not project `ICodecAPI`, which is how an encoder is told to force a keyframe, change
bitrate at runtime and enter low-latency mode. That one interface is called through its raw vtable
in `Media/CodecApi.cs` — one method, one `VARIANT` layout — in keeping with the rest of the project.

### 1.3 The pixel path runs on the CPU

§9.2 describes the ideal: capture texture → GPU scale and colour-convert → hardware encoder on
the same D3D11 device, with no CPU readback. What is built instead: capture texture → staging copy
→ BGRA in system memory → managed box-filter scaler and BT.601 NV12 converter across all cores →
encoder input sample. Reasons:

- It works with **every** encoder, including the software one, which has no GPU input path.
- It keeps the entire conversion testable without a GPU, and it is pinned by exact colour tests.
It is not free. In the live run the whole capture-to-send pipeline took 12–19 ms per 1080p frame
against ~7 ms for the hardware encode, so readback plus conversion is roughly half the cost — and
it is why this machine settled at 1080p30 rather than 1080p60 while also decoding the stream
itself (§5). The GPU path is therefore the first optimisation to make, for 1080p60 on modest
hardware and for 4K. The capture and encoder interfaces do not preclude it. It is not built.

### 1.4 Signalling shape

- **The phone offers, the PC answers.** `media.answer` in Appendix A is not a separate command: the
  answer is the response to `media.offer`. The PC's candidates are inline in it, because
  host-only gathering is instant — so `media.ice` only ever flows phone → PC.
- **One stream per control connection.** A new offer from a connection that is already streaming
  replaces the old stream. That is what a phone does after a Wi-Fi blip.
- **Changing monitor does not renegotiate.** H.264 carries resolution in-band; the new monitor
  starts with a keyframe whose SPS describes it, and the decoder follows. §9.6 said
  `select_monitor` "renegotiates track resolution" — it does not need to.

---

## 2. Corrections to the design

### 2.1 Colour: BT.601 limited range, not "correct VUI range flags" in general

WebRTC's Android and iOS renderers convert YUV to RGB with BT.601 coefficients. Encoding BT.709
(the usual choice for HD) would shift every colour slightly on the phone. The converter produces
BT.601 limited range and the encoder's input type says so, so the bitstream's VUI agrees with the
pixels.

### 2.2 The adaptive controller's step-down rule

As first written it stepped down a rung after two lossy reports whenever the bitrate was below 35%
of the rung's ceiling — which it always is at the start of a stream, since bitrate ramps up from a
quarter of the ceiling. Two bad reports on Wi-Fi would have cost resolution before bitrate had been
tried at all, contradicting §9.5's "bitrate first". It now requires three consecutive congested
reports **and** a bitrate within 25% of the floor. Pinned by `BriefLoss_IsAnsweredWithBitrateNotResolution`.

### 2.3 Output size is rounded, not truncated

1920 × (720 / 1080) is 1279.9999… in floating point. Truncating streamed 1278×720. Pinned by a test.

### 2.4 The session agent reads `config.json` only if it may

Installed as a service, the data directory belongs to SYSTEM and Administrators. The agent runs as
the signed-in user and may not be able to read the file. It now loads it when readable and uses
defaults otherwise; every streaming setting has a safe default and is clamped by its consumer.

---

## 3. Bugs found by running the system

| # | Bug | How it presented | Fix |
|---|---|---|---|
| 1 | **ICE accepted a peer-reflexive candidate on an address the policy had refused** | The negative test "candidates on another address are never used" received frames. Filtering the offer's `a=candidate` lines is not enough: ICE learns a *peer-reflexive* candidate from whoever sends it a valid connectivity check, bypassing the filter entirely | On `connected`, the session checks the pair ICE actually selected against the authenticated address and closes before a single frame is sent if they differ |
| 2 | Async (hardware) encoder took 14.7 ms per 1080p frame | Suspiciously constant — it was Windows' 15.6 ms timer tick. `Thread.Sleep(1)` in the event-queue poll slept a whole tick | `timeBeginPeriod(1)` for the encoder's lifetime (process-local since Windows 10 2004). Now ~7 ms |
| 3 | `E_OUTOFMEMORY` from `MFCreateMemoryBuffer` after ~60 frames | Input and output samples were allocated per frame — 180 MB/s of native allocation at 1080p60 — and the output wrapper was compared by object identity, so a pooled sample returned as a new wrapper would have been released twice | Samples pooled and reused; identity compared by native pointer. Memory now flat (14 MB hardware, ~176 MB plateau for the software encoder's own working set) |
| 4 | Software encoder `MF_E_NOT_INITIALIZED` on the first frame, once | Seen once in about twenty runs; not reproducible in twelve consecutive attempts | Not root-caused. The pipeline replaces a failing encoder and ends the stream only after three consecutive failures, so a one-off start failure costs a frame, not the stream |
| 5 | `IpcEvent` telemetry would have been broadcast | Every agent event was relayed to every connected phone. Media telemetry says what another device is watching | Events carry an optional `connectionId`; targeted events go to that connection alone and are dropped if it has gone |

---

## 4. Security properties of the media plane

In addition to §7 of the architecture:

| Property | How it holds |
|---|---|
| Only the phone that authenticated can receive the stream | Its DTLS fingerprint arrives in the offer over the pinned, mutually authenticated control channel. A tampered fingerprint never connects — `TamperedDtlsFingerprint_NeverDeliversMedia` |
| The PC sends media only to that phone's address | Offer candidates filtered to UDP host candidates on the control connection's address; the selected ICE pair re-checked on connect (bug 1) — `CandidatesOnAnotherAddress_AreNeverUsed` |
| No external servers | Empty ICE server list on both sides; srflx, relay, TCP and mDNS candidates refused |
| A stream never outlives its authorization | The service notifies the agent (`ipc.connection_changed`) when a connection closes or its permissions change; losing `ViewScreen` or the connection ends the stream. If the service itself goes away, the agent ends every stream |
| One phone cannot steer another's stream | Session ids are scoped to their connection; another connection's id reads as "not found", never "forbidden" |
| Telemetry is private | Delivered to the owning connection only |
| Bounded cost | At most `streaming.maxConcurrentStreams` (default 2) streams; SDP capped at 64 KB; a peer that never completes ICE/DTLS is dropped after 20 s |
| `monitors.changed` leaks nothing | Broadcast with a count only; details stay behind `screen.monitor_list`, which requires `ViewScreen` |

---

## 5. What is implemented

### PC

| Component | Where |
|---|---|
| Monitor enumeration via DXGI (ids, bounds, DPI, refresh, primary, rotation) | `RemoteAgent.Windows/Display/DxgiMonitorProvider.cs` |
| Desktop Duplication capture with pointer shape and position, rotation undo, secure-desktop handling | `RemoteAgent.Windows/Display/DesktopDuplicationCapture.cs` |
| H.264 via Media Foundation — hardware (async MFT) and software (sync MFT), probed, with automatic fallback; constrained baseline, CBR, low latency, keyframes on demand, runtime bitrate | `RemoteAgent.Windows/Media/MediaFoundationH264Encoder.cs` |
| JPEG screenshots | `RemoteAgent.Windows/Media/JpegImageEncoder.cs` |
| BGRA → NV12 box-filter scaler, cursor compositing with save/restore | `RemoteAgent.Streaming/Video/` |
| Adaptive controller: bitrate AIMD, RTT inflation, encoder-overload detection, 6-rung ladder | `RemoteAgent.Streaming/Adaptation/AdaptiveController.cs` |
| ICE candidate policy | `RemoteAgent.Streaming/Transport/IceCandidatePolicy.cs` |
| Media session (WebRTC peer + pump thread) and per-connection manager | `RemoteAgent.Streaming/MediaSession*.cs` |
| Handlers for all seven `screen.*` / `media.*` commands; capability probing; `monitors.changed` | `RemoteAgent.Session/` |
| Targeted events, connection-change notification to the agent | `RemoteAgent.Service`, `RemoteAgent.Ipc` |
| Firewall rules on `--install` (control TCP, discovery UDP, media UDP; per-program, Private profile only), removed on `--uninstall` | `RemoteAgent.Service/Hosting/ServiceInstaller.cs` |

New settings under `streaming`: `maxConcurrentStreams` (2), `screenshotMaxWidth` (1920). New error
code `E_BUSY`.

### Mobile

A **Screen** tab: pinch-zoom video (double-tap resets), telemetry overlay with the green/amber/red
indicator and the explicitly-labelled degraded state, monitor picker, quality presets, screenshot
viewer, full screen (landscape, immersive), and a specific explanation for each reason the screen
cannot be shown — not connected, `ViewScreen` not granted, nobody signed in, PC cannot capture,
locked or secure prompt. The stream runs only while the tab is visible and the app is in the
foreground, and resumes after a reconnect.

### Measured on the development machine

Windows 10 22H2, Ryzen 7 3800X, GeForce GT 710 (Kepler NVENC), two 1920×1080 monitors.

| | |
|---|---|
| Hardware encode, 1080p | ~7 ms/frame after the timer fix |
| Software encode, 1080p | ~4–5 ms/frame on this 16-thread CPU |
| Screenshot, 1080p JPEG | ~170 KB |
| Live stream to libwebrtc | 1080p, 0% loss; stepped to 1080p30 by the encoder-overload rule while Chrome was decoding on the same machine (pipeline 15–19 ms against a 60 fps budget) |

### Tests

| Suite | Tests | Covers |
|---|---|---|
| `RemoteAgent.Streaming.Tests` | 64 | Annex-B parsing; exact BT.601 values; box filtering; stride handling; cursor blending, clipping, XOR, save/restore; the ladder, bitrate, step-down and step-up rules; ICE policy; loopback WebRTC including tampered fingerprint, foreign address, secure desktop, stream limit and session isolation; real DXGI capture, real encoders and a real-desktop stream on hardware that has them |
| `remote_control_app` (Dart) | 29 (+9) | Telemetry and monitor parsing against the PC's field names, the Auto preset, and a check of every Phase 3 wire name against the C# sources |

Existing suites unchanged and passing: 88 protocol, 21 integration. `flutter analyze` clean;
`flutter build apk --debug` succeeds with `flutter_webrtc` 1.6.2.

---

## 6. Not verified, and known limits

Stated plainly, because each is a place where "the tests pass" is not the same as "it works":

- **Not yet run on a phone.** Interoperability was proven against libwebrtc in Chrome (§1.1), and
  the app builds and analyzes cleanly, but the Flutter viewer has not been driven on an Android or
  iOS device. The first real-device run is the next thing to do. Two things to watch there: that
  the phone's H.264 decoder accepts the negotiated profile (Chrome chose constrained-baseline
  `42001f`; mobile hardware decoders should too), and that the phone's host candidate is on the
  same address as its control connection — the policy requires it.
- **Rotated monitors are untested.** The rotation undo in `CopyMapped` follows the documented
  semantics but has not met a portrait monitor.
- **The GPU zero-copy path (§9.2) is not built** — see §1.3.
- **No DataChannel.** It arrives with remote input in Phase 4.
- **Hardware encoders other than NVIDIA are untested.** The async MFT protocol is generic, but
  Intel Quick Sync and AMD AMF each have their own quirks, and some may demand a D3D device manager
  rather than system-memory samples. The probe will report them unusable rather than crash, and the
  software encoder takes over.
- **Windows N editions** without the Media Feature Pack have no H.264 encoder; streaming is simply
  not advertised there. The OpenH264 fallback in §9.2 is not built — Cisco's binary licence
  requires it to be downloaded from Cisco at install time, which is an installer concern.
- **The firewall rules are added by `--install` only.** Portable and console mode add none; there,
  Windows may show its firewall prompt the first time a stream starts. Streaming usually works
  regardless, because the PC's outbound ICE checks open a path for the phone's replies.
- Bug 4 (§3) is mitigated, not understood.

---

## 7. Trying it

Build and run as in [02-implementation-notes.md §4](02-implementation-notes.md#4-running-it). With
the service running, a session agent connected and the phone paired:

1. Grant **View screen** to the device on the PC if it does not already have it — it is part of
   the default grant.
2. Open the **Screen** tab. The stream starts by itself.
3. Lock the PC: the phone should say why the picture stopped, and resume by itself on unlock.

Screen streaming is advertised only when a monitor, Desktop Duplication and an H.264 encoder all
work; check the session agent's log for the probe result if the tab says the PC cannot stream.
