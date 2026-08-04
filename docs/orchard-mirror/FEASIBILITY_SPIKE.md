# Orchard Mirror feasibility spike

> **Superseded — closed 2026-08-01.** This track answered its question, and the answer was no. See
> [README.md](README.md) for the outcome and
> [../decisions/0014-orchard-mirror-coredevice-architecture.md](../decisions/0014-orchard-mirror-coredevice-architecture.md)
> for what replaced it. Retained as evidence; do not resume from here.

This spike tests whether a stock iPhone can enroll and trust a non-Apple
Orchard peer for the private iPhone Mirroring protocol. It does not assume a
phone-side app, a Mac in the final architecture, or copied Apple code.

## Decision tree

The central question is:

> Can a self-generated Orchard identity be registered as the Mac identity when
> the user enters their iPhone passcode, or must that identity already exist as
> an Apple Account/IDS/iCloud trusted device?

The possible outcomes are:

1. **Fresh local enrollment works.** The iPhone accepts a newly generated key
   after local passcode approval. A pure Orchard client remains genuinely
   possible.
2. **Same-account identity is mandatory, but reproducible.** Orchard must also
   reproduce RPIdentity/IDS/iCloud Keychain provisioning. This is substantially
   harder, but may remain technically possible.
3. **Apple hardware attestation is mandatory.** If enrollment requires a
   non-exportable, Apple-issued hardware identity or server-verified
   attestation, the pure no-Apple-hardware option is not viable.

The spike initially treated a fresh-key handshake against a physical iPhone as
the decisive test. Current-iOS control-flow recovery later made the ordering of
the official enrollment checks directly observable.

## iOS 26 control-flow result

Subsequent current-iOS control-flow analysis resolved the main identity
question more directly than the initial static-string pass.

The official enablement entry point resolves the supplied peer identifier to an
existing IDS device before it consumes the iPhone passcode or starts the
pairing-lock session. A missing peer returns the explicit same-iCloud-account
error immediately. The passcode therefore authorizes an existing
same-account peer; it does not locally mint an arbitrary fresh Mac identity.

The MacUnlockPhone pairing operation is authentication type `5`, which is also
explicitly in the Apple KeyStore pairing session's attestation-required set.
This ties the previously observed attested-LTK implementation to the exact
iPhone Mirroring pairing family.

The original dynamic test remains useful as a regression capture, but it is no
longer required to decide whether the current official enablement function
offers passcode-only promotion of an unknown identity: its control flow shows
that it does not.

The control flow, recovered message schema, exact Rapport/IDS pre-pairing
envelope, evidence limits, and product conclusion are documented in
[`KEYBAG_IDENTITY_FINDINGS_2026-07-30.md`](KEYBAG_IDENTITY_FINDINGS_2026-07-30.md).

The public-feature alternatives and why they cannot be composed into a true
locked-screen session are compared in
[`LOCKED_SCREEN_PATHS_2026-07-30.md`](LOCKED_SCREEN_PATHS_2026-07-30.md).

## GitHub Actions phase

The `Orchard Mirror feasibility` workflow runs on Apple-silicon macOS 15 and
macOS 26 GitHub-hosted runners. It gathers:

- system and hardware metadata;
- code-signing identities and entitlements;
- Mach-O dependencies and selected load commands;
- filtered symbols and strings related to pairing, same-account identities,
  Secure Enclave use, and attestation;
- availability of dyld shared-cache inspection tools; and
- the locations and hashes of relevant standalone components.

The workflow deliberately does not copy or upload Apple executables,
frameworks, or dyld caches. It also does not read keychains, Apple Account
records, environment variables, tokens, private keys, or user data.

Hosted Actions answers the static implementation question: what checks and
framework boundaries exist in currently shipped macOS builds? It cannot answer
the live enrollment question because its temporary VM has neither a nearby
iPhone nor a user-established Continuity trust relationship.

## Optional dynamic confirmation

The minimal non-Apple probe remains useful for confirming live behavior and
capturing a regression fixture. It should:

1. discover or directly contact the iPhone's relevant local service;
2. generate a fresh Ed25519/X25519-compatible identity;
3. send only the initial pairing/enrollment messages;
4. present no copied certificate, Apple private key, or Mac identity;
5. confirm that the unknown peer is rejected before passcode processing; and
6. stop before video, audio, remote input, or keybag unlock.

That probe should run on Linux first if AWDL access is required, because a
compatible Wi-Fi adapter can expose monitor mode and frame injection. The
protocol and cryptography can then be moved to Windows; only the link-layer
transport may need a platform-specific implementation.

The known-good first-pairing control is prepared in
[`PHYSICAL_MAC_CAPTURE_RUNBOOK.md`](PHYSICAL_MAC_CAPTURE_RUNBOOK.md). Its
one-command capture script records targeted logs, AWDL/discovery packets, and
process socket changes without reading keychains or uploading the result.

## Unpaired-iPhone sysdiagnose baseline

A local sysdiagnose from the target iPhone was inspected without committing or
publishing the archive or any device identifiers. The baseline provides three
useful results:

- The iPhone has seven synced, non-tombstoned
  `RPIdentity-SameAccountDevice` keychain records. They include a Mac-class
  identity as well as mobile-device identities. These records therefore exist
  independently of an iPhone Mirroring enrollment.
- A separate set of synced `com.apple.continuity.unlock` Auto Unlock records
  also exists, but the archive contains no enrolled `MacUnlockPhone`, iPhone
  Mirroring, or ScreenContinuity credential.
- During the capture, `sharingd` reported that it could not find a valid
  cloud-paired device. The account was nevertheless reported as HSA2/CDP
  eligible with Manatee available. AWDL remained stopped with no peer or active
  traffic registration.

This materially weakens the fresh-local-key hypothesis. The observed data model
has an account-synchronized same-account identity layer that precedes and is
distinct from Mirroring authorization. The likely first-pairing flow selects
and authorizes an already provisioned trusted-device identity, rather than
turning an arbitrary locally supplied public key into a Mac identity solely
because the user entered the iPhone passcode.

This baseline alone was architectural evidence rather than a protocol-level
disproof. The later iOS 26 control-flow result supplies the stronger evidence:
the IDS lookup precedes passcode use. Passive captures from the unpaired iPhone
still cannot produce a live fixture because, without an eligible nearby peer,
neither AWDL nor the Mirroring/remote-unlock session activates.

## Windows handshake probe

The clean-room probe in `src/Orchard.Mirror.Probe` now generates fresh X25519
and Ed25519 keys, observes redacted Apple BLE frames, discovers
`_companion-link._tcp.local`, and can send a correctly framed OPACK/TLV8
Pair-Verify M1.

Its first live Windows run observed Apple Continuity BLE traffic but discovered
no reachable companion-link endpoint over ordinary network interfaces. It
returned `DISCOVERY_BLOCKED` and sent no authentication bytes. This validates
the Windows BLE layer, but it neither accepts nor rejects the fresh identity:
AWDL or eligible-peer activation blocked the run before M1.

The detailed result and exact interpretation are recorded in
[`HANDSHAKE_PROBE_2026-07-29.md`](HANDSHAKE_PROBE_2026-07-29.md).

## Windows built-in Wi-Fi transport spike

The target PC uses a Realtek 8821CE WDI miniport. A deliberately test-signed
NDIS lightweight filter was installed for one boot and bound only to the WLAN
stack. Its source is in `native/orchard-mirror/wifi/driver`; the controller is
in `src/Orchard.Mirror.Probe`. Both native and managed Release builds completed
with zero warnings and zero errors.

The filter originated Microsoft's
`OID_WDI_TASK_SEND_REQUEST_ACTION_FRAME` with a clean-room 269-byte AWDL master
indication body. The body contains the Apple AWDL OUI plus synchronization,
election, channel-sequence, data-path, and version TLVs, but no Apple
credential or copied private key.

Runtime results:

- A self-addressed vendor action test completed.
- A broadcast AWDL-shaped task completed on channel 6 and channel 149 through
  the physical Realtek interface.
- Both the outer NDIS status and the 16-byte WDI result header status were
  `0x00000000`. This is a completed miniport task, not merely a successful
  user-to-driver IOCTL.
- Channel 44 returned inner status `0xC000009A` while the other two AWDL social
  channels completed.
- The two Wi-Fi Direct virtual adapters also completed channel-6 submissions.
  Holding a Windows Wi-Fi Direct publisher active did not bring those virtual
  adapters into a usable AWDL receive state.

The receive result was negative. During an iPhone AirDrop discovery window,
Windows BLE saw a very strong type-`0x07` AirDrop advertisement from the nearby
phone, proving that the phone-side test state was active. Nevertheless, the
physical STA port, both virtual Wi-Fi Direct ports, and the active publisher
window delivered zero generic or P2P action-frame receive indications. A final
Companion Link query again found zero `_companion-link._tcp` endpoints.

The resulting boundary is:

> The stock Realtek Windows driver can transmit protocol-shaped AWDL action
> frames, but it does not expose the management-frame receive and virtual IPv6
> data path needed to join AWDL.

An NDIS lightweight filter cannot manufacture frames that the underlying
miniport never indicates. Moving beyond this boundary requires a chipset/driver
path with active monitor-style receive and injection, a vendor-specific
miniport extension, or a replacement physical Wi-Fi driver. Transmit success
therefore does **not** remove the external-hardware/driver requirement for a
complete Orchard Mirror implementation on this PC.

This transport result remains independent of the Apple identity question. The
iPhone never exposed an IP endpoint, so Pair-Verify M1 and fresh-identity
enrollment were not reached.

## Feasibility verdict after the Windows spike

The pure target—stock iPhone, no phone app, no Mac or Apple hardware identity,
and ordinary Windows Wi-Fi—is a no-go on the current official protocol.

Two independent blockers remain:

1. **Transport:** the tested stock Windows miniport does not expose the AWDL
   receive/data path even though it can transmit AWDL-shaped action frames.
2. **Identity:** current iOS control flow resolves the supplied identifier to
   an existing same-account IDS device before using the passcode. MacUnlockPhone
   pairing is type `5`, and type `5` explicitly requests Apple KeyStore
   attestation. The local runtime and macOS static evidence independently match
   this design.

The current official fresh-local-enrollment hypothesis is disproved at its
first identity gate and blocked again at attestation. This is not a claim that
an undisclosed path or future vulnerability is mathematically impossible. A
commercial feasibility decision should classify the pure option as **no-go
unless a new supported path appears**.

## Evidence standards

- A string or symbol is a lead, not proof that a code path executes.
- Missing strings do not disprove server-side or obfuscated checks.
- A successful connection from a pre-enrolled Mac does not prove fresh
  identities can enroll.
- Rejection before a passcode prompt strongly supports mandatory pre-existing
  identity.
- A passcode prompt followed by persistence of Orchard's fresh public key is
  strong evidence that pure local enrollment works.
- A trace explicitly requesting Apple-issued attestation, verified by Apple
  infrastructure or a non-exportable hardware key, is strong evidence that the
  pure option is blocked.
