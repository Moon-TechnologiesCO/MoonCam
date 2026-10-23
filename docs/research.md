# MoonCam — Research notes

Answers to the open questions behind MoonCam's design, for **Android** (phone) and **Linux** (computer).
Last reviewed: October 2026 (Android 16, Ubuntu 26.04, PipeWire 1.6, kernel 7.0).

---

## 1. USB versus Wi-Fi

| | USB (ADB) | Wi-Fi |
|---|---|---|
| Latency | Lowest and stable (USB 2.0 is ~10× what 1080p H.264 needs) | Good on 5/6 GHz; spikes on busy 2.4 GHz |
| Setup | Developer options → USB debugging, accept the computer's RSA key once | Same network; pair once with a 6-digit code |
| Charging | Phone charges while streaming | Needs a charger for long calls |
| Security | ADB channel already authorised; nothing on the network | TLS 1.3 + pinned certificates |

**How USB works without a network:** `adb reverse tcp:47321 tcp:47321` makes the phone's `127.0.0.1:47321`
reach the computer through the cable. The app's socket code is identical for both transports.

**Best practice:** offer both, prefer USB automatically when a cable is plugged in (like Apple's Continuity
Camera prefers the cable), fall back to Wi-Fi when it is unplugged.

## 2. Cameras: front, back, ultra-wide, telephoto

Android exposes cameras in three ways, depending on the manufacturer:

1. **Separate camera IDs** (`CameraManager.getCameraIdList()`): e.g. `0` back, `1` front, `2` ultra-wide.
2. **A logical multi-camera** (capability `LOGICAL_MULTI_CAMERA`) that groups physical lenses; their IDs come
   from `getPhysicalCameraIds()` and can be streamed with `OutputConfiguration.setPhysicalCameraId()`.
3. **Zoom ratio** on the logical camera (`CONTROL_ZOOM_RATIO`, Android 11+): `0.5×` switches to the
   ultra-wide, `3×` to the telephoto. Many Samsung and Pixel phones only expose extra lenses this way.

MoonCam combines the three into one list of "lenses" with a friendly name (focal length from
`LENS_INFO_AVAILABLE_FOCAL_LENGTHS` tells ultra-wide from tele) and the right way to open each one.
Supported sizes and FPS ranges come from `SCALER_STREAM_CONFIGURATION_MAP` and
`CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES`.

## 3. The phone's two (or more) microphones

What Android allows, honestly:

- **You cannot pick an individual physical microphone** with a public API on most phones. The built-in mics
  appear as one input device (`TYPE_BUILTIN_MIC`). `AudioManager.getMicrophones()` (Android 9+) *describes*
  each physical mic (position, direction, sensitivity) but does not let you open one alone.
- **You can use them together** in three useful ways:
  - **`CHANNEL_IN_STEREO`** with the `CAMCORDER` source: on most phones the left/right channels come from two
    different mics — real stereo, as in video recording.
  - **`VOICE_COMMUNICATION`** source: the manufacturer's voice pipeline uses all mics for **beamforming, echo
    cancellation and noise reduction** — ideal for calls.
  - **`setPreferredMicrophoneDirection()`** (Android 10+): hint "towards the user" (front camera) or
    "away from the user" (back camera) so the device focuses the right mics.
- **`UNPROCESSED`** / `VOICE_RECOGNITION` give the cleanest raw signal for desktop-side processing, when
  supported (`PROPERTY_SUPPORT_AUDIO_SOURCE_UNPROCESSED`).

MoonCam therefore offers **capture profiles** instead of "mic 1 / mic 2": *Voice (calls)*, *Stereo (video)*,
*Raw (studio, desktop noise suppression)*, and a **Mic lab** screen that lists `getMicrophones()` and lets you
hear each profile, because results differ between phone models.

## 4. Noise cancellation that works in every app

Processing happens **before** the virtual microphone, so every app — Zoom, Meet, Discord, OBS — receives
the already-clean signal. Two layers:

1. **On the phone (optional):** the `VOICE_COMMUNICATION` profile and Android's `NoiseSuppressor`,
   `AcousticEchoCanceler`, `AutomaticGainControl` when the device provides them.
2. **On the computer (default):** **RNNoise**, a small neural network (BSD licence) that removes keyboard,
   fan and street noise in real time. Consistent quality on any phone. A toggle and a strength slider in the UI.

## 5. Virtual camera on Linux

| Option | Seen by | Needs |
|---|---|---|
| **v4l2loopback** (chosen) | All apps: Chrome/Meet, Firefox, Zoom, Discord, OBS, Teams | Kernel module (`sudo` once; DKMS; MOK enrollment with Secure Boot) |
| PipeWire camera node | OBS, Firefox, Chrome via the camera portal | Nothing extra — but Zoom and Discord do not support it yet |

Module options MoonCam uses: `devices=1 video_nr=10 card_label="MoonCam" exclusive_caps=1`.
`exclusive_caps=1` is required for Chrome and Electron apps to list the camera. Persisted in
`/etc/modprobe.d/mooncam.conf` and `/etc/modules-load.d/mooncam.conf` by the setup assistant.

**Secure Boot:** the DKMS package compiles the module on your PC and signs it with a local key that Secure Boot
does not trust until you enroll it (password + reboot + blue "Enroll MOK" screen). Ubuntu 26.04 avoids all of
that: `linux-modules-v4l2loopback-generic-hwe-26.04` (or `-generic`) is **signed by Canonical** and loads
immediately. MoonCam's package depends on it, so users never see this step.

## 6. Virtual microphone, headphones and default devices on Linux

- MoonCam creates **"MoonCam Microphone"**, a PipeWire source. Apps list it next to the laptop's mic.
- **Headphones on the PC:** they are an *output*; MoonCam does not touch outputs. You hear the call in your
  headphones and speak into the phone.
- **Default device:** while the phone is connected MoonCam can (optionally) make "MoonCam Microphone" the
  default input; when the phone disconnects it restores the previous default, so apps fall back to the
  laptop's own microphone. The same logic applies to the camera: apps simply see the device appear and
  disappear.

## 7. Using the phone from the computer, with the mouse (Continuity-style)

**scrcpy** mirrors the phone's screen to the computer with mouse and keyboard control, over the same ADB
connection, with ~35–70 ms latency. MoonCam will launch it from a "Show my phone" button.

Can the camera keep streaming while you use another app on the phone? **Yes**, because MoonCam streams from a
**foreground service** of type `camera|microphone`: Android keeps it running when you switch apps, unlock with
a fingerprint, or answer a message. Two exceptions, handled with clear messages and automatic recovery:

- **Another app opens the camera** (e.g. scanning a QR code): Android gives it priority and closes MoonCam's
  camera; MoonCam reopens it as soon as it is free.
- **Another app records audio** while visible: Android may silence MoonCam's mic (detected with
  `AudioManager.AudioRecordingCallback` → `isClientSilenced`) until that app stops.

Other Continuity-style touches planned: automatic connection when the cable is plugged in, the phone
appearing by name on the computer, and "Desk View"-style use of the ultra-wide camera.

## 8. Hand gestures

The phone manufacturer's air gestures are **not available to other apps**. MoonCam can still recognise
gestures itself with **MediaPipe Gesture Recognizer** (Apache-2.0, on-device): it runs on a small 640×480
copy of the camera feed at ~15 fps and recognises open palm, closed fist, pointing up, thumbs up/down,
victory and "I love you".

Proposed uses (all optional, all processed on the phone, nothing leaves the device except the event):

| Gesture | Action |
|---|---|
| ✋ Open palm, held 1 s | Pause camera ("privacy curtain") |
| ✊ Fist, held 1 s | Mute / unmute the microphone |
| ✌️ Victory | Switch front/back camera |
| 👍 Thumbs up | Reaction overlay on the video |

## 9. Audio/video sync

Covered in [architecture.md §7](architecture.md#7-audiovideo-synchronisation): both streams carry phone-side
`CLOCK_BOOTTIME` timestamps; the desktop delays the earlier stream inside a small adaptive jitter buffer.
