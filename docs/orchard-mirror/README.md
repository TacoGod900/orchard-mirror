# Orchard Mirror documents

## Status

The **keybag / locked-screen track is closed** (1 August 2026). Everything in this directory is
retained as evidence for *why* it closed, not as work in progress. Do not resume from these
documents.

The question was whether a self-generated Orchard identity on Windows could enroll through Apple's
private iPhone Mirroring path and obtain a genuinely locked-keybag session. It cannot:

- iOS resolves the proposed Mac as an existing same-account **IDS device before it consumes the
  passcode**, so the passcode screen authorizes an already-trusted peer rather than enrolling a new
  one;
- `MacUnlockPhone` pairing is authentication **type 5**, which is in the `requiresAttestation` set
  and binds to a `localAttestedLTK` produced by Apple's keystore; and
- a live Windows probe never reached a handshake at all — no `_companion-link._tcp` endpoint was
  exposed to ordinary interfaces (`DISCOVERY_BLOCKED`), and the built-in Wi-Fi adapter can transmit
  AWDL-shaped frames but cannot receive management frames, so it cannot join AWDL.

Orchard Mirror now runs on Apple's **CoreDevice** developer services instead, which give the real
screen and real touchscreen input on an **unlocked** device. That is a different and weaker privacy
story than a locked phone, and it is stated plainly in
[../decisions/0014-orchard-mirror-coredevice-architecture.md](../decisions/0014-orchard-mirror-coredevice-architecture.md).

## Index

The first two documents are live. Everything below them is closed-track evidence.

| Document | What it is |
| --- | --- |
| [V1_STATUS_2026-08-02.md](V1_STATUS_2026-08-02.md) | **Live.** What works, what was verified on the device versus offline, and what is deliberately unfinished. |
| [AUDIO_2026-08-02.md](AUDIO_2026-08-02.md) | **Live.** The paired audio leg is AAC-ELD, not Opus. The wire format, why the published magic cookie is wrong, and an open ADR 0014 §6 deviation. |
| [KEYBAG_IDENTITY_FINDINGS_2026-07-30.md](KEYBAG_IDENTITY_FINDINGS_2026-07-30.md) | The document that closed the track. iOS 26 control-flow analysis: IDS lookup precedes the passcode; pairing requires an attested long-term key. |
| [LOCKED_SCREEN_PATHS_2026-07-30.md](LOCKED_SCREEN_PATHS_2026-07-30.md) | Every public path compared against the four product requirements. None satisfies all of them. |
| [FEASIBILITY_SPIKE.md](FEASIBILITY_SPIKE.md) | The original spike: the decision tree, the hosted-macOS collector, and the Windows AWDL transport result. |
| [ACTIONS_COMPARISON_2026-07-29.md](ACTIONS_COMPARISON_2026-07-29.md) | Static macOS 15 vs 26 evidence from GitHub-hosted runners. macOS 26 *added* Octagon/iCloud-health checks. |
| [HANDSHAKE_PROBE_2026-07-29.md](HANDSHAKE_PROBE_2026-07-29.md) | The first live Windows probe run: 35 Apple BLE peers seen, no companion-link endpoint, no bytes sent. |
| [PHYSICAL_MAC_CAPTURE_RUNBOOK.md](PHYSICAL_MAC_CAPTURE_RUNBOOK.md) | Procedure for capturing one first-time pairing on a real Mac. Never needed — the control-flow result made it a regression fixture rather than the decisive test. |

## What carried forward

The probe (`src/Orchard.Mirror.Probe/`) kept only its BLE scanner and mDNS discovery, as a
"why can't Windows see my phone?" diagnostic for the Wi-Fi transport gate. The keybag protocol
work, the AWDL frame builder, the NDIS filter driver, and the Bluetooth-HID mouse were all removed;
they are recoverable from commit `fe71088` if ever needed.
