# MoonCam — Roadmap

MoonCam is built in small, verifiable stages. Each stage ends with something you can run and a checklist that
proves it works. Platforms: **Android** (phone) and **Linux / Ubuntu 26.04** (computer).

| Stage | Goal | Done when |
|---|---|---|
| **0. Foundations** ✅ | Architecture, decisions, protocol spec, repository, CI | Docs reviewed; `dotnet test` and the Kotlin protocol tests pass in CI |
| **1. First light** 🚧 | USB connection and live preview | Plug the phone in → the desktop launches the app → the back camera shows in the MoonCam window at 720p30. *Desktop done and verified end to end with FakePhone; Android app written, pending first build on a device.* |
| **2. Real webcam** | Virtual camera + camera choice | "MoonCam" appears in Meet, Zoom, Discord and OBS; switch front/back/ultra-wide/tele; choose resolution, FPS, quality |
| **3. Sound & Wi-Fi** | Microphone, sync, Wi-Fi | "MoonCam Microphone" in every app with RNNoise; lip sync within ±40 ms; pair over Wi-Fi with a 6-digit code; auto-reconnect |
| **4. Polish** | Product quality | macOS-style UI finished, tray icon, Spanish/English, release on GitHub with demo video. *Already done early: `.deb` that installs every dependency (signed driver, no Secure Boot step).* |
| **5. Continuity** | Use the phone from the computer | "Show my phone" opens scrcpy; camera keeps streaming; auto-connect when the cable is plugged in |
| **6. Gestures** | Hands-free control | MediaPipe gestures: privacy curtain, mute, switch camera, reactions |
| Later | More platforms | Windows and macOS virtual devices (see architecture §10) |

## Stage 1 — "First light" in detail

1. `MoonCam.Core`: MoonLink framing, messages, session state machine (unit-tested, shared vectors).
2. Android `protocol` module: the same framing in Kotlin, tested against the same vectors.
3. Desktop: TCP server on `127.0.0.1:47321`, `adb reverse`, handshake with a one-time token, FFmpeg H.264
   decoder, preview in the Avalonia window.
4. Android: foreground service, Camera2 → MediaCodec H.264 → MoonLink client to `127.0.0.1:47321`.
5. Dev tool `FakePhone`: streams a test pattern exactly like the phone, so the desktop can be tested
   without a phone and in CI.
6. Manual check on the real phone: latency measured with a stopwatch on screen (target < 150 ms).
