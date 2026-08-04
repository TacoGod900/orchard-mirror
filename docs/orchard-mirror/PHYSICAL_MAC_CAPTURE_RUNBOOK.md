# Orchard Mirror physical-Mac capture runbook

> Status, 2026-07-30: A physical Mac is no longer required to answer the
> fresh-identity feasibility question. Current-iOS control flow shows that IDS
> lookup precedes passcode use and MacUnlockPhone pairing type `5` requires
> attestation. A Mac capture would now serve as a regression fixture and help
> map later encrypted messages; it would not remove those trust gates.

## Purpose

This procedure records one successful or failed first-time iPhone Mirroring
pairing attempt. The Mac is a laboratory control that reveals Apple's message
order and the point at which IDS identity and attested-key checks occur. It is
not part of Orchard Mirror's intended Windows architecture.

Use a Mac with Apple silicon or an Intel Mac with a T2 Security Chip, running
macOS Sequoia 15 or later. The iPhone must run iOS 18 or later.

## Privacy and safety

The capture is private security-research data from devices you control. It can
contain opaque device identifiers, local network addresses, packet timing,
nearby discovery traffic, and system log messages.

- Use your own iPhone and Apple Account.
- Close unrelated network-heavy and privacy-sensitive applications.
- Do not perform the test on a workplace or public network.
- Do not type a passcode or password into Terminal.
- Do not commit or publicly upload the generated archive.
- The scripts do not dump keychains, credentials, tokens, private keys, or
  environment variables.

## Prepare the devices

1. Put the repository on the physical Mac.
2. Ensure the Mac and iPhone use the same Apple Account with two-factor
   authentication.
3. Turn on Bluetooth and Wi-Fi on both devices.
4. Keep the iPhone locked, powered on, and next to the Mac.
5. Quit iPhone Mirroring.
6. If this Mac was previously enrolled, open iPhone Settings and go to
   **General > AirPlay & Continuity > iPhone Mirroring**, then revoke the Mac.
7. Confirm iPhone Mirroring does not already have an active session.

Revocation is essential: a repeat session does not exercise the first-pairing
path we need to observe.

## Run the capture

From the repository root:

```bash
chmod +x \
  eng/orchard-mirror/capture-first-pairing.sh \
  eng/orchard-mirror/summarize-capture.sh

bash eng/orchard-mirror/capture-first-pairing.sh
```

The script will:

1. perform a preflight check and request `sudo`;
2. record before-state metadata;
3. start targeted unified-log and packet captures;
4. open iPhone Mirroring;
5. wait while the user completes setup;
6. stop immediately when the screen appears or an error is shown;
7. record after-state metadata and a short operator observation;
8. build a summary and SHA-256 inventory; and
9. create a private `.tar.gz` archive under
   `artifacts/orchard-mirror/captures`.

Press Enter as soon as setup succeeds or fails. Allowing a mirrored video
session to continue only makes the capture larger and can overwrite the
earliest packets in the bounded packet-capture ring.

The script automatically stops after ten minutes if the operator forgets to
return to Terminal.

## Expected files

The archive contains:

- `private-raw/unified-log.stdout.txt`: filtered live unified logs;
- `private-raw/unified-log.stderr.txt`: logging diagnostics;
- `private-raw/network-awdl0.pcap*`: AWDL packets;
- `private-raw/network-llw0.pcap*`: low-latency Wi-Fi packets, if available;
- `private-raw/network-relevant-processes.pcap*`: process-attributed packets
  for Sharing, Rapport, IDS, and Wi-Fi P2P daemons;
- `private-raw/network-discovery-*.pcap*`: mDNS discovery packets;
- `private-raw/dns-sd-*.stdout.txt`: browsed Bonjour service types and
  Companion Link advertisements;
- `private-raw/process-sockets.txt`: one-second socket snapshots;
- before/after state reports;
- `summary/operator-observations.txt`;
- packet and authentication-keyword summaries; and
- a private-data warning.

## Minimum useful result

A capture is useful even if pairing fails, provided it contains:

- whether the iPhone passcode prompt appeared;
- the final Mac error;
- at least one non-empty unified-log or packet capture; and
- the macOS/iOS versions used.

The best result is a first-time pairing that reaches the mirrored screen.

## VM limitation

A Windows-hosted macOS VM is not a substitute because it lacks an Apple
Secure-Enclave-derived VM identity. A macOS 15+ guest on an Apple-silicon Mac
can receive an iCloud identity derived from the host Secure Enclave, but it
still requires physical Apple hardware and may not receive the Bluetooth/AWDL
interfaces needed by iPhone Mirroring. A native physical-Mac capture is the
least ambiguous control.

## After capture

Keep the raw archive private. Provide it directly to the Orchard Mirror
analysis environment rather than attaching it to a public pull request.

The first analysis pass will determine:

1. service advertisements, interfaces, addresses, and ports;
2. which process starts the `MacUnlockPhone` operation;
3. the boundary between discovery, IDS lookup, passcode approval, attested-LTK
   exchange, and media setup; and
4. the smallest message prefix needed for the non-Apple fresh-key probe.
