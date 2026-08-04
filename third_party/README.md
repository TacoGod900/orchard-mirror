# Orchard Mirror vendored third-party sources

The trees under `third_party/` are **not committed**. They are large upstream checkouts, and one of
them (`BonjourSdk`) contains an Apple-supplied binary, which `docs/CLEAN_ROOM_AND_LEGAL.md` §1
prohibits carrying in the repository. What is committed instead is this provenance record plus the
Orchard patches under `patches/`, which is enough to reconstruct the tree exactly.

This follows the convention already used for the guest CPU engine: vendor from a specific commit,
keep the local modifications as a checked-in patch (`runtime/patches/dynarmic-pac-neutralize.patch`),
and pin the upstream revision.

## Pins

| Tree | Upstream | Revision | Licence |
| --- | --- | --- | --- |
| `UxPlay` | https://github.com/FDH2/UxPlay | `1e9cf2457825a70bf6ab9274a33918324df3c691` | GPL-3.0 |
| `mDNSResponder` | Apple open source | `d4658af3f5f291311c6aee4210aa6d39bda82bbe` (tag `mDNSResponder-2881.0.25`) | Apache-2.0 |
| `BonjourSdk` | Apple Bonjour SDK for Windows | n/a — `dns_sd.h`, `dnssd.def`, `dnssd.lib` | Apple SDK licence — **binary, never committed** |

## Reconstructing

```sh
git clone https://github.com/FDH2/UxPlay third_party/UxPlay
git -C third_party/UxPlay checkout 1e9cf2457825a70bf6ab9274a33918324df3c691
git -C third_party/UxPlay apply ../patches/uxplay-orchard-frame-source.patch
```

`BonjourSdk` is only needed for the alternate `build-bonjour` UxPlay configuration and is installed
separately from Apple's Bonjour SDK for Windows.

## The Orchard patch

`patches/uxplay-orchard-frame-source.patch` (352 added lines across 4 files) is what turns UxPlay from
a standalone AirPlay receiver into an Orchard frame source:

- `renderers/video_renderer.c` — writes decoded BGRA frames into the shared memory mapping named by
  `ORCHARD_FRAME_MAP`, using the `state=0 → memcpy → MemoryBarrier → InterlockedIncrement(sequence) →
  state=1` publish protocol that `MirrorForm.ReadSharedFrame` consumes. Falls back to embedding via
  `ORCHARD_VIDEO_HWND` when no mapping is set.
- `renderers/audio_renderer.c` — bounds the AAC queue and raises resample quality.
- `uxplay.cpp`, `lib/http_handlers.h` — `ORCHARD_MIRROR_ONLY` restricts advertised AirPlay features to
  screen mirroring.

UxPlay is GPL-3.0. These modifications are therefore GPL-3.0, and must be published as source if a
patched UxPlay binary is ever distributed. See ADR 0014.

> **Status.** This whole tree belongs to the AirPlay-based Mirror prototype, which the CoreDevice
> rebuild replaces. It is preserved so the working build can be reconstructed, not because it is on
> the forward path.
