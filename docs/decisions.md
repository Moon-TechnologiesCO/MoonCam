# MoonCam — Technical decisions

Each decision records the context, the choice, the alternatives considered and the trade-offs we accept.
Dates are when the decision was taken.

---

## D1. Android app in Kotlin, with Camera2 and MediaCodec (2026-10-23)

**Decision:** Kotlin, minSdk 29 (Android 10), Jetpack Compose UI, **Camera2** for capture and **MediaCodec**
for hardware encoding, inside a foreground service.

**Why:**
- Kotlin is Google's first-class language for Android; coroutines make the capture/encode/network pipeline
  readable.
- **Camera2** exposes what MoonCam needs and CameraX hides: physical cameras of a logical multi-camera
  (`getPhysicalCameraIds`, `OutputConfiguration.setPhysicalCameraId`), exact FPS ranges
  (`CONTROL_AE_TARGET_FPS_RANGE`), zoom ratio to switch lenses (`CONTROL_ZOOM_RATIO`), stabilisation, and
  free routing of the camera into several `Surface`s (encoder + preview + analysis).
- **MediaCodec** gives hardware H.264/H.265 encoding with an input `Surface`: frames never touch the CPU.
- minSdk 29 brings the Opus encoder, `setPreferredMicrophoneDirection`, and covers the vast majority of phones
  in use in 2026.

**Alternatives:** CameraX (simpler, but its `VideoCapture` writes files, not live streams, and physical-lens
control is limited); a native C++ app with NDK camera (more control, much more complexity); WebRTC's capturer
(brings a large library and its own pipeline).

**Accepted cost:** Camera2 is verbose and device quirks exist; we isolate it in `capture/` and test on real
devices.

## D2. Desktop app in C# / .NET 10 with Avalonia 12 (2026-10-23)

**Decision:** .NET 10 (LTS until November 2028), **Avalonia 12** UI, Clean Architecture projects
(`Core` → `Infrastructure` / `Platform.*` → `App`).

**Why:**
- One codebase for Linux now and Windows/macOS later. Avalonia renders with Skia, so the macOS-style design
  looks the same everywhere (we draw it; we do not depend on the GTK theme).
- C# is fast enough for the media plumbing (`Span<T>`, no allocations in hot paths) and talks to native
  libraries (FFmpeg, libc `ioctl`, PipeWire) through P/Invoke.
- The same stack as other Moon Technologies apps.

**Alternatives:** GTK4/libadwaita (most native on GNOME, weaker on Windows/macOS, and libadwaita resists custom
styling); Qt/C++ (excellent, but a second language and heavier licensing questions); Electron/Tauri (web UI,
but the media path still needs native code); Rust + GStreamer (great media story, steeper learning curve).

**Accepted cost:** media and virtual-device code needs native interop; we keep that layer thin and covered by
integration tests.

## D3. MoonLink: our own small protocol over TCP (2026-10-23)

**Decision:** a fixed preamble plus 16-byte framed messages over **TCP** (see [protocol.md](protocol.md)),
wrapped in **TLS 1.3** on Wi-Fi.

**Why:**
- One protocol for USB and Wi-Fi (USB is TCP through `adb reverse`).
- Low latency is achievable on TCP when the sender **drops frames instead of queueing**, uses `TCP_NODELAY`
  and small buffers — the approach proven by scrcpy.
- Tiny, documented and testable on both platforms with shared test vectors; easy for a learner to understand.

**Alternatives:**
- **WebRTC:** built-in congestion control, NACK/FEC and encryption, but huge native libraries on both sides,
  no official Android artifacts from Google any more, and a jitter buffer we cannot tune.
- **RTP/UDP:** loss-tolerant, but we would have to build jitter buffers, retransmission and encryption ourselves.
- **SRT:** strong on lossy networks, but no first-class Android library.
- **RTSP/HTTP-MJPEG** (like IP-camera apps): simple, but MJPEG needs 5–10× the bandwidth and RTSP adds a
  server on the phone.

**Revisit when:** measurements on busy Wi-Fi show head-of-line blocking hurting latency. The framing is
transport-agnostic, so media could move to QUIC datagrams later without changing the messages.

## D4. The phone is the client, the computer is the server (2026-10-23)

**Decision:** the phone always connects out; it never listens.

**Why:** the phone holds the sensitive sensors. If it listened, any device on the network — or any other
app on the phone, through `127.0.0.1` — could try to reach the camera. As a client, the phone only talks to a
computer it chose and paired with. It also fits the UX: you hold the phone and tap the computer you want.

**Cost:** the desktop must listen on the network for Wi-Fi mode; it only accepts paired certificates, and
unpaired connections only while the pairing window is open.

## D5. Video: H.264 by default, H.265 optional (2026-10-23)

**Decision:** H.264 (High profile, no B-frames, 1–2 s GOP plus on-demand key frames), H.265 as an option.

**Why:** every Android hardware encoder supports H.264 and every decoder (FFmpeg, VA-API on the AMD Radeon
610M) handles it with low latency. H.265 saves ~30 % bandwidth for Wi-Fi but some encoders add latency.
AV1 hardware encoding is still limited to recent phones.

## D6. Decoding with FFmpeg through a minimal P/Invoke layer (2026-10-23)

**Decision:** call `libavcodec`/`libswscale` (FFmpeg 8 on Ubuntu 26.04) directly through a handful of
P/Invoke declarations, isolated in `MoonCam.Infrastructure/Decoding`.

**Why:** FFmpeg is the most complete and battle-tested decoder, with hardware acceleration (VA-API) and every
codec we need. We only use ~15 functions and read only the first, ABI-stable fields of `AVFrame`, so we avoid
binding libraries that are tied to a specific FFmpeg major version.

**Alternatives:** FFmpeg.AutoGen (complete bindings, but pinned to FFmpeg majors); GStreamer through
gstreamer-sharp (weakly maintained bindings); LibVLC (playback-oriented, hard to get raw frames with low
latency).

## D7. Virtual camera on Linux: v4l2loopback (2026-10-23)

**Decision:** write decoded I420 frames to a **v4l2loopback** device created with
`exclusive_caps=1 card_label="MoonCam"`.

**Why:** it is the only virtual camera that **every** Linux app sees today: Chrome/Meet, Firefox, Zoom,
Discord, OBS, Teams. `exclusive_caps=1` is required for Chrome-based apps to list it.

**Signed, not compiled:** Ubuntu 26.04 ships v4l2loopback **prebuilt and signed by Canonical**
(`linux-modules-v4l2loopback-<flavour>`, e.g. `generic-hwe-26.04`). It loads with Secure Boot on — no DKMS build,
no MOK key enrollment, no reboot — and its metapackage follows kernel updates. The `.deb` depends on it and
replaces the unsigned `v4l2loopback-dkms` (which would shadow it). Verified on 2026-10-23 with `apt-get -s`.

**Cost:** installing a kernel module needs administrator approval once; it happens during the package install.

**Future:** a **PipeWire camera node** needs no kernel module and is the modern path (OBS, Firefox and
Chrome support it through the camera portal), but Zoom and Discord still read V4L2 only. We will add it as a
second backend and choose automatically.

## D8. Virtual microphone on Linux: a PipeWire node (2026-10-23)

**Decision:** expose MoonCam's audio as a PipeWire node of class `Audio/Source/Virtual` named
"MoonCam Microphone".

**Why:** Ubuntu 26.04 runs PipeWire 1.6 with WirePlumber; a node is visible to PipeWire-native,
PulseAudio and ALSA apps alike, with no kernel module and no root. It disappears automatically if MoonCam
exits, and when the phone disconnects MoonCam keeps the node and sends silence.

**Implementation path:** stage 3 starts with `pactl load-module module-null-sink
media.class=Audio/Source/Virtual` fed through `libpulse-simple` (small, stable C API, easy P/Invoke).
A native `libpipewire` stream (lower latency, no module) replaces it once measured.

## D9. Noise suppression on the computer, before the virtual mic (2026-10-23)

**Decision:** apply **RNNoise** (BSD licence, tiny, real-time on any CPU) on the desktop, after decoding and
before the virtual microphone. The phone side offers capture profiles (see research), and its built-in
`NoiseSuppressor` when the device has one.

**Why:** processing before the virtual mic means **every app** receives clean audio — Zoom, Discord, OBS
and the browser alike — and the quality is the same whatever the phone brand. Android's built-in effects vary
widely between manufacturers.

**Alternatives:** WebRTC Audio Processing (already on Ubuntu, used by PipeWire's echo-cancel module; good AEC,
weaker denoising); DeepFilterNet (better quality, heavier).

## D10. Phone mirroring through scrcpy (2026-10-23)

**Decision:** "Use your phone from the computer" (stage 5) launches **scrcpy** (Apache-2.0) over the same
ADB connection.

**Why:** scrcpy is the reference for low-latency Android mirroring with mouse and keyboard control. It runs
under the `shell` user, which has the permission to inject input events that a normal app cannot get. Reusing
it is safer and better than reinventing it with `MediaProjection` + an accessibility service.

**Compatibility:** MoonCam's camera stream keeps running in its foreground service while you use the phone
through scrcpy — unless the app you open on the phone takes the camera itself.

## D11. Licence: Apache-2.0 (2026-10-23)

Same as the other Moon Open Source projects. Compatible with the libraries we link dynamically (FFmpeg LGPL
builds, RNNoise BSD, PipeWire MIT). The **MoonCam** name and logo are trademarks and are not licensed.
