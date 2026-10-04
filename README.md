# Orchard Mirror

Mirror an iPhone's screen to a Windows PC and drive it with the mouse and keyboard.

Mirror talks to the phone over Apple's CoreDevice developer services (the same path Xcode uses), so it shows the phone's real screen and sends real touchscreen input. The phone has to be unlocked: this is not a lock-screen bypass, and the project is not affiliated with Apple.

## What works

Verified against a real iPhone:

- USB and Wi-Fi CoreDevice connection and developer-disk-image mount (discovery 0.2 s using `dnssd.dll`).
- Live HEVC screen stream decoded in hardware through D3D11/DXVA and presented with a flip-model swap chain, with encoder padding cropped to the real screen size.
- Touch, drag, scroll, typing and hardware buttons, including the exit-to-Home gesture mapped onto the HID Home button.
- Phone audio: the AAC-ELD stream is decoded with libfdk-aac and played through `waveOut`.
- Recovery from an unplugged cable: stall detection in 1.5 s, teardown in 0.2 s, retry every 250 ms.
- A borderless, rounded phone-shaped window that keeps streaming across the lock transition.

Not finished: frame pacing and queue drop policy for smoother video, anti-aliased window corners, and the toast surface for routine status messages.

## How it's built

| Part | Path | Notes |
| --- | --- | --- |
| Control plane | `src/Orchard.Mirror.Agent/` | A small Python agent on top of [pymobiledevice3](https://github.com/doronz88/pymobiledevice3). It runs in its own process and talks to the app over a line protocol on stdio. Licensed GPL-3.0 and shipped as source. |
| App | `src/Orchard.Mirror.Windows/` | The Windows Forms application: window, video present, input capture, reconnect logic. |
| Media | `src/Orchard.Mirror.Media/`, `Orchard.Mirror.Video.Windows/`, `Orchard.Mirror.Audio.Windows/` | HEVC depacketizing and parameter sets, D3D11 video decode, AAC-ELD audio decode and playback. |
| Shell | `src/Orchard.Mirror.Shell/` | Licence keys with offline validation and tiers, behind an `ILicenseGateway` seam. |
| Probe | `src/Orchard.Mirror.Probe/` | A diagnostic that reports what this PC can see of nearby Apple devices over BLE and mDNS. |
| Launch | `orchard-launch/` | Landing page, legal drafts and the Stripe licence-issuing webhook. |
| Tests | `tests/Orchard.Mirror.Tests/` | 59 tests covering depacketizing, DXVA submission, gestures, licensing and reconnect. |

The C# application and the GPL agent stay in separate processes and share only a documented byte protocol, which is what keeps the app's own licence independent. The design and its trade-offs are in [ADR 0014](docs/decisions/0014-orchard-mirror-coredevice-architecture.md). Vendored upstream sources are pinned, not committed; see [third_party/README.md](third_party/README.md).

The rest of this tree is the [Project Orchard](https://github.com/TacoGod900/Orchard) codebase that Mirror was branched from; Mirror has its own solution (`Orchard.Mirror.slnx`) and build gate so Orchard's dependency-free core never inherits a Python runtime or a media stack.

## Build and run

Requirements: Windows 11, the .NET SDK pinned in `global.json`, Python 3 for the agent, and an iPhone on iOS 17.4 or newer connected over USB.

```powershell
pwsh -NoProfile -File .\eng\check-mirror.ps1          # restore, format check, build, tests
dotnet run --project .\src\Orchard.Mirror.Windows      # launch the mirror window
```

## Security

Report vulnerabilities privately through **Security → Report a vulnerability** on GitHub; see [SECURITY.md](SECURITY.md).
