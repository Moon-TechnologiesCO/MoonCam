# MoonCam — Security and privacy model

A webcam app sees your face and hears your room. MoonCam treats that seriously.

## Principles

1. **Local only.** No cloud servers, no accounts, no telemetry, no analytics. Traffic stays on the USB cable
   or the local network.
2. **The phone never listens.** It only connects out to a computer the user chose (decision D4).
3. **Authorised connections only.** Every session is authenticated before any media flows.
4. **Visible at all times.** When the camera or microphone is in use, both devices say so.
5. **Least privilege.** In stage 1 the Android app asks only for `CAMERA`, `INTERNET`,
   `FOREGROUND_SERVICE_CAMERA` and `POST_NOTIFICATIONS`; `RECORD_AUDIO` arrives with the microphone stage.

## Threats and mitigations

| Threat | Mitigation |
|---|---|
| Someone on the same Wi-Fi watches the stream | TLS 1.3; no plaintext media on the network |
| A rogue computer pretends to be yours | Certificate pinning after pairing; pairing shows the same 6-digit code on both screens (derived from both certificate fingerprints), so a man-in-the-middle produces different codes |
| A rogue phone or app connects to the desktop | Desktop accepts only paired certificates (Wi-Fi) or the one-time token it passed through ADB (USB); unpaired connections only while the pairing window is open, for 2 minutes |
| Another app on the phone connects to MoonCam locally | Impossible: the phone app has no listening socket |
| Another app on the computer connects to the USB tunnel | The desktop listens on `127.0.0.1` only and requires the one-time token, which travelled inside an explicit ADB intent that only MoonCam receives |
| Streaming without the user knowing | Android's privacy indicator + persistent MoonCam notification with **Stop**; red **LIVE** badge on the desktop |
| Another phone app forges the "connect over USB" intent to make MoonCam stream to a port it controls | MoonCam never connects silently: every request shows a consent dialog; the token format and port range are validated and the host is always 127.0.0.1 |
| Privilege escalation through the setup helper | The helper is root-owned in `/usr/libexec/mooncam`, does one fixed job, takes no arguments, and runs only through pkexec with the system password dialog |
| Malformed or hostile input crashes the desktop | Strict frame validation (types, reserved bits, 16 MiB limit), invalid-input tests on both readers (shared vectors), decoder errors isolated and recovered |

## Pairing (stage 3)

1. Each device generates a key pair once: the phone in the **Android Keystore** (non-exportable), the desktop
   in `~/.config/mooncam/` with `0600` permissions (libsecret later).
2. The user opens "Add phone" on the desktop; the phone discovers it with mDNS.
3. They connect over TLS with self-signed certificates; both compute
   `code = SHA-256(phoneCertFingerprint ‖ desktopCertFingerprint ‖ nonces) mod 10⁶` and show it.
4. The user confirms on both screens that the codes match. Each side stores the other's fingerprint.
5. Later connections require the pinned fingerprints; anything else is rejected.

Removing a paired device on either side revokes it immediately.

## Reporting vulnerabilities

See the organisation's [security policy](https://github.com/Moon-TechnologiesCO/.github/blob/main/SECURITY.md).
