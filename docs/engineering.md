# MoonCam — Engineering standards

How MoonCam's code is written, tested and kept secure. Applies to both the Android (Kotlin) and desktop (C#) code.

## Design

- **Clean Architecture.** Dependencies point inwards: `Core` (domain + protocol) has no I/O and no platform code;
  `Infrastructure` and `Platform.*` implement `Core` interfaces; `App` is the only composition root.
  On Android, `protocol` is a pure-Kotlin module with no Android dependency.
- **SOLID.** One reason to change per class (e.g. `FrameReader` only reads frames; `ServerSession` only runs a
  session; `VideoPipeline` only schedules decoding). New platforms are added by implementing interfaces
  (`IVirtualCamera`, `IVirtualCameraProvider`, `ISystemSetup`), not by editing existing code.
- **Explicit state.** Connection states are a guarded state machine; illegal transitions are refused, not thrown.
- **Back-pressure over buffering.** Real-time paths drop data and resynchronise on key frames instead of queueing.

## Code

- Names say what things are; functions are small; comments explain *why*, not *what*.
- C#: nullable reference types on, warnings are errors, analyzers at the latest level.
- Kotlin: official code style, warnings are errors in the protocol module.
- No secrets, tokens or personal data in logs, code or commits.

## Security (secure by design)

Aligned with **OWASP MASVS** (Android app), **OWASP ASVS** level 2 (network-facing desktop component) and the
**CWE Top 25**:

| Practice | Where |
|---|---|
| Strict input validation: unknown types, reserved bits and oversized frames close the connection (CWE-20, CWE-400) | `FrameHeader.Parse`, `FrameHeader.parse` |
| Secrets compared in constant time (CWE-208) | `SessionToken.Matches` |
| Least privilege: phone never listens; desktop binds 127.0.0.1 in USB mode | `MoonLinkServer`, `LinkSession` |
| No command injection: external tools receive argument lists, never shell strings (CWE-78) | `AdbClient`, `LinuxSystemSetup` |
| Privileged code is minimal, root-owned and reached only through pkexec | `packaging/linux/mooncam-setup` |
| Explicit user consent before any camera stream; intent data validated (MASVS-PLATFORM) | `MainActivity`, `ConnectRequest` |
| No backups or cloud transfer of app data (MASVS-STORAGE) | `data_extraction_rules.xml` |
| Development backdoors excluded from release builds | `--dev-token` is `#if DEBUG` only |
| Dependencies watched and updated | Dependabot, CodeQL in CI |

Report vulnerabilities privately — see the organisation's security policy.

## Testing

| Level | What | Tools |
|---|---|---|
| Unit | Protocol, state machine, settings, Annex-B parsing, colour conversion | xUnit, JUnit 6 |
| Contract | The same MoonLink test vectors on both platforms | `protocol/test-vectors` |
| Integration | Real FFmpeg decoding, real TCP sessions, server rules | xUnit + ffmpeg |
| End-to-end | Simulated phone → TCP → decode → sink | `FakePhone` |
| UI | Headless rendering of the real window, screenshots | Avalonia Headless + Skia |

Every pull request runs all of them in CI.

## Process

- Small, verifiable stages ([roadmap](roadmap.md)); each ends with something runnable.
- Decisions recorded with alternatives in [decisions.md](decisions.md).
- Commits in the imperative mood; one topic per pull request.
- Semantic Versioning for releases.
