# Orchard Mirror probe

A local diagnostic for what this PC can observe of nearby Apple devices. It opens no session, sends
no authentication bytes, and cannot mirror or control a phone.

Two layers remain:

1. passive Apple BLE manufacturer-frame observation; and
2. `_companion-link._tcp.local` discovery on ordinary local interfaces.

Observed Bluetooth addresses are hashed, manufacturer payloads are never printed, and nothing is
persisted.

## Why it still exists

Mirror's device work runs over Apple's CoreDevice services through the Python agent
([ADR 0014](../../docs/decisions/0014-orchard-mirror-coredevice-architecture.md)), not through
anything here. What this probe is good for is the question that actually bites during the Wi-Fi
transport gate: *can this PC see the phone at all?* Discovery is the layer that fails first, and it
fails silently, so a tool that answers it directly is worth keeping.

Everything else the probe once did — the Handoff Pair-Verify exchange, the `MacUnlockPhone`
pre-pairing schema, the keybag evidence scanner, the AWDL frame builder, the NDIS filter client, and
the Bluetooth-LE HID mouse — belonged to the closed keybag track or to the superseded AirPlay
prototype. See [docs/orchard-mirror/README.md](../../docs/orchard-mirror/README.md) for why that
track closed. The code is recoverable from commit `fe71088`.

## Run

```powershell
dotnet run --project src\Orchard.Mirror.Probe -- self-test
dotnet run --project src\Orchard.Mirror.Probe -- scan --seconds 20
dotnet run --project src\Orchard.Mirror.Probe -- discover --seconds 12
```

`self-test` is offline: it checks the DNS query encoding against RFC 1035 and RFC 6762 without
touching a network or a radio.
