# ADR 0014: Orchard Mirror runs on CoreDevice, with pymobiledevice3 as a separate-process control plane

Status: Accepted  
Date: 2026-08-01  
Decision owners: Project owner  
Required approvers: Project owner (recorded in-session, 2026-08-01)  
Relates to: ADR 0011 (CPU engine licence constraint) and `docs/CLEAN_ROOM_AND_LEGAL.md`.

> **Branch note.** This branch is based on `main`, which carries ADRs 0001-0005. ADRs 0006-0013 exist
> on `feature/tooling-and-ui-kernel` and are not visible here. 0014 is nonetheless the next free number
> project-wide, so it stays collision-free after any merge.

## Context

Orchard Mirror's first prototype composed two public mechanisms: **AirPlay** for video, via a patched
UxPlay receiver, and a **Bluetooth-LE HID mouse** advertised from Windows for control. It worked
against a real device, but its limits are structural rather than defects:

- AirPlay permits an application to send different content to an external display, so apps like TikTok
  present a bare video feed and their real UI never reaches the receiver.
- AirPlay mirroring audio is lossy AAC-ELD encoded and buffered on the device; roughly two seconds of
  latency and audible quality loss originate upstream of the receiver.
- iOS routes an external mouse through AssistiveTouch, so wheel input is honoured inconsistently, the
  pointer ring is permanently visible, and Windows cannot toggle it.

A separate research track asked whether Orchard could instead speak Apple's private **iPhone
Mirroring** protocol and obtain a genuinely locked-keybag session. That track is **closed**, with a
firm negative result recorded in `docs/orchard-mirror/KEYBAG_IDENTITY_FINDINGS_2026-07-30.md`: iOS
resolves the proposed Mac as an existing same-account IDS device *before* it consumes the passcode, and
`MacUnlockPhone` pairing is authentication type 5, which is in the `requiresAttestation` set and binds
to a `localAttestedLTK` produced by Apple's keystore. A software identity generated on Windows cannot
satisfy that path. No composable public feature reproduces locked-keybag plus display-off plus full UI
plus system-wide input (`docs/orchard-mirror/LOCKED_SCREEN_PATHS_2026-07-30.md`).

Since iOS 17, Apple's **CoreDevice** developer services expose exactly the two capabilities Mirror
needs, over an RSD tunnel: `DisplayService` streams the device's real screen as RTP/HEVC, and
`UniversalHIDService`/`IndigoHIDService` accept genuine touchscreen, keyboard and hardware-button
reports. These are the mechanisms Xcode's own device mirroring uses. They are reached through
**pymobiledevice3**, the reverse-engineered open implementation of RemoteXPC, RSD, DDI mounting and the
CoreDevice services. pymobiledevice3 is **GPL-3.0** and written in Python.

Orchard Mirror is intended for distribution, so the licence question is real rather than academic.

## Decision

1. **CoreDevice replaces AirPlay and Bluetooth HID as Mirror's video and input source.** The device
   streams its real screen and accepts real touchscreen events, which removes the per-app content
   substitution, the AssistiveTouch dependency and the scroll-wheel unreliability at the source rather
   than compensating for them in the receiver.

2. **pymobiledevice3 runs as a separate process, never linked into Orchard.** A small Python agent
   (`src/Orchard.Mirror.Agent/`) imports it and exposes a line protocol over stdio. That agent is
   itself a derived work of a GPL-3.0 library, so **the agent is licensed GPL-3.0 and published as
   source**. The C# application is a distinct program in a distinct process that exchanges bytes with
   it over a documented protocol; no Orchard code is linked into pymobiledevice3 and none of its code
   is copied into Orchard.

3. **ADR 0011 is distinguished, not overridden.** ADR 0011 excludes GPL or copyleft **CPU cores**,
   "whether linked or run out-of-process". Two things made out-of-process separation unconvincing
   there: the guest calls into the CPU on every `objc_msgSend`, syscall and memory hook, so the
   boundary was described as "technically wrong for this design" as much as legally grey; and a CPU
   core is an intimate, hot-path component of the product. Mirror's control plane is the opposite on
   both counts. It is contacted a handful of times per session — open tunnel, mount DDI, negotiate a
   stream, forward input — it is an independently useful program with its own CLI and users, and it is
   invoked across a process boundary that exists for architectural reasons rather than as a licence
   manoeuvre. ADR 0011's exclusion of GPL CPU cores stands unchanged.

4. **The media path carries no copyleft at all.** HEVC is decoded with `ID3D11VideoDevice`, which is
   driver-provided and needs no third-party library and no Store extension. Audio is deferred, and when
   it lands it will use **Orchard's own AAC-ELD decoder** rather than a vendored codec (decision 6).

5. **The patched UxPlay is GPL-3.0 and its modifications are published as source.** The prototype's 352
   lines of changes live in `third_party/patches/uxplay-orchard-frame-source.patch`. If a patched
   UxPlay binary is ever distributed, that patch is its corresponding source. The vendored upstream
   trees are pinned by revision in `third_party/README.md` rather than committed, because
   `BonjourSdk` contains an Apple-supplied `dnssd.lib` that `docs/CLEAN_ROOM_AND_LEGAL.md` §1 forbids
   carrying in the repository.

6. **Audio is out of v1, and will be Orchard's own decoder.** Windows ships no AAC-ELD decoder — Media
   Foundation's AAC decoder covers LC and HE only — so the device's audio format has no OS-provided
   path. Orchard will implement an MPEG-4 ER AAC ELD decoder (48 kHz stereo, 480-sample frames, ASC
   magic cookie `F8 E6 40 00`), which keeps the media path free of copyleft and is consistent with a
   repository that already hand-rolls an ARM64 decoder and an LLVM bitstream encoder.

7. **Mirror has its own solution and gate.** `Orchard.Mirror.slnx` and `eng/check-mirror.ps1` are
   separate from `Orchard.slnx` and `eng/check.ps1`, so a Python runtime and a media stack never become
   dependencies of Orchard's deliberately dependency-free core. Mirror's projects are removed from
   `Orchard.slnx`.

8. **v1 is USB-tethered.** The unprivileged userspace tunnel is supported on Windows for iOS 17.4+ over
   USB. The Wi-Fi RemotePairing route is described by its own maintainers as fragile and additionally
   wants elevation and a TUN driver; it ships only if it survives reboot and network-change testing.

## Consequences

- **Orchard Mirror gains a Python runtime dependency.** It is confined to one process behind one
  protocol, and to one solution that the core does not reference, but it is a real packaging and
  support cost.
- **CoreDevice is a developer service.** It requires Developer Mode and a Developer Disk Image mounted
  once per boot — a genuine onboarding cost — and Apple may change these protocols between iOS
  releases. Expect ongoing maintenance against a fast-moving target.
- **The product is an unlocked phone with an optionally blanked display, not a locked one.** Blanking
  depends on VoiceOver's Screen Curtain, which has no API and must be driven through injected keyboard
  shortcuts, and VoiceOver may reinterpret injected taps. That is gated work, deliberately not on the
  critical path.
- **Wireless is a regression from the prototype**, which is wireless today over AirPlay, until the
  Wi-Fi tunnel gate passes.
- **The media stack is new ground for this repository.** No swapchain, video decode, NV12 handling or
  audio exists anywhere in it today.

## Review

Revisited if the owner changes the distribution posture; if pymobiledevice3 changes licence or is
abandoned; if Apple removes or gates the CoreDevice media/HID services; or if the Wi-Fi tunnel gate
fails badly enough that a USB-only product is judged not worth shipping.
