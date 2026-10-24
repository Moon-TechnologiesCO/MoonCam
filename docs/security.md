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

## Wi-Fi pairing

1. Each device creates its identity once: the phone an EC P-256 key in the **Android Keystore** (it can sign
   but can never be exported) with a self-signed certificate; the desktop an EC P-256 certificate in
   `~/.config/mooncam/identity.p12` with `0600` permissions.
2. The person clicks **Pair over Wi-Fi** on the desktop, which opens a 2-minute window (5 attempts, one phone
   at a time). The phone finds the computer with mDNS and the person taps **Pair**.
3. They connect over TLS 1.3 with both certificates and run the commit–reveal exchange in
   [protocol.md §7](protocol.md#7-wi-fi-pairing). Both screens show a six-digit code derived from both
   certificate fingerprints and both nonces.
4. The person confirms on **both** screens that the codes match. Only then does each side store the other's
   fingerprint: the desktop in `~/.config/mooncam/paired-phones.json` (`0600`), the phone in app-private
   storage that is excluded from backups.
5. Later connections are pinned: the desktop accepts only stored phone fingerprints, and the phone accepts only
   the stored computer fingerprint. If the computer presents another certificate (for example after MoonCam
   was reinstalled on it), the phone stops and asks the person to forget it and pair again.

Removing a paired device on either side ("Remove" on the desktop, "Forget" on the phone) revokes it
immediately. The phone connects on its own only to a paired computer, only while MoonCam is open on screen,
and with the camera notification and Android's camera indicator visible the whole time.

**Android 17 and later** treat the local network as a runtime permission (`ACCESS_LOCAL_NETWORK`, "Nearby
devices"). MoonCam asks for it together with the camera; USB keeps working without it.

**Firewall.** Wi-Fi needs incoming TCP 47322 on the computer. MoonCam does not change firewall rules on its
own; if ufw is enabled, allow the port for your local network.

## Reporting vulnerabilities

See the organisation's [security policy](https://github.com/Moon-TechnologiesCO/.github/blob/main/SECURITY.md).
