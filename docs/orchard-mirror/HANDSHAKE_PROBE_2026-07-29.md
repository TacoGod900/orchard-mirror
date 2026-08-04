# Orchard Mirror handshake probe result

> Update, 2026-07-30: Current-iOS control-flow recovery subsequently proved
> that the official enablement path resolves an existing same-account IDS peer
> before consuming the passcode, and that MacUnlockPhone pairing type `5`
> requires attestation. See
> [`KEYBAG_IDENTITY_FINDINGS_2026-07-30.md`](KEYBAG_IDENTITY_FINDINGS_2026-07-30.md).
> The live test below remains useful for transport regression evidence.

## Probe boundary

The Windows probe generates independent ephemeral X25519 and long-term
Ed25519 key pairs, observes Apple BLE manufacturer frames, queries
`_companion-link._tcp.local`, and can send a correctly framed OPACK/TLV8
Pair-Verify M1 to an explicit or discovered endpoint.

It deliberately stops after M2. Handoff Pair-Verify is a transport and
authentication control, not the complete iPhone Mirroring enrollment protocol.
Static macOS evidence shows that Mirroring invokes ScreenSharingKit remote
authentication and checks `isMacUnlockiPhonePairingSupported`.

## Local validation

The probe self-test passed:

- independent X25519 peers derived the same shared secret;
- an ephemeral Ed25519 signature verified;
- Pair-Verify M1 encoded and decoded with the expected frame, OPACK, and TLV8
  fields.

The Release build completed with zero warnings and zero errors.

## First live Windows run

The first 25-second run generated a new in-memory identity and observed 35
redacted Apple BLE peers. It decoded several Apple Continuity TLV types,
including Handoff, Nearby Action, and Nearby Info advertisements. This
proves that the PC's Bluetooth adapter and Windows BLE API can observe the
discovery layer.

No `_companion-link._tcp` endpoint was returned over the PC's ordinary network
interfaces. The probe therefore returned `DISCOVERY_BLOCKED` and sent no
authentication bytes.

## Interpretation

This run did not accept or reject the fresh Orchard identity. It stopped one
layer earlier:

```text
Apple BLE visible
        |
        v
AWDL / eligible-peer activation unavailable
        |
        v
No IP endpoint
        |
        v
No M1 sent
```

The result is consistent with Apple Continuity discovery using BLE as a trigger
and mDNS over AWDL for the reachable service. It also matches the earlier
iPhone sysdiagnose, where AWDL remained stopped because no valid cloud-paired
peer was found.

The built-in Realtek transport spike subsequently proved that WDI can complete
an AWDL-shaped action-frame transmit on channels 6 and 149. It also proved the
opposite receive boundary: with the iPhone's AirDrop discovery UI active and a
strong type-`0x07` AirDrop BLE advertisement visible, Windows surfaced zero
AWDL action-frame indications on the physical STA port or either Wi-Fi Direct
virtual port. A final run still found zero Companion Link endpoints and sent no
M1.

The next decisive identity run therefore needs one of:

1. direct AWDL access through Linux and a compatible monitor-mode/injection
   Wi-Fi adapter;
2. an endpoint and transcript from a supported Mac first-pairing control; or
3. enough current ScreenSharingKit message-schema evidence to reproduce its
   Mac-unlock-iPhone discovery trigger and remote-authentication exchange.

The current internal Realtek adapter is not a viable OWL adapter. Its WDI
miniport can transmit the management body, but does not expose the receive and
IPv6 data paths. Option 1 requires different Wi-Fi hardware or a materially
different vendor/miniport driver.
