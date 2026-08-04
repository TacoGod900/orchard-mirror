# Orchard Mirror locked-screen paths

Date: 2026-07-30

> **Superseded — closed 2026-08-01.** The path comparison below stands, including the finding that
> no composable public feature gives a locked keybag with a blank display and system-wide input.
> Orchard Mirror now targets an unlocked session over CoreDevice instead. See [README.md](README.md).

## Required outcome

The desired product state combines all four properties:

1. the iPhone keybag is remotely unlocked for the session;
2. the physical iPhone remains on its Lock Screen with its display off;
3. Windows receives the complete live iPhone UI; and
4. Windows can inject system-wide pointer, gesture, and keyboard input.

Only Apple's private iPhone Mirroring stack currently provides that exact
combination on a stock iPhone. The feasibility spike found no composable public
feature that reproduces it.

## Path comparison

| Path | Keybag/Lock Screen | PC sees full UI | PC controls system-wide | Stock/no app | Result |
|---|---|---:|---:|---:|---|
| Apple iPhone Mirroring protocol | Remains locked; remote keybag session | Yes | Yes | Yes, but requires eligible Mac identity | Blocked on pure Windows by IDS and attestation |
| AirPlay screen mirroring | Phone must remain active/unlocked | Usually, with protected-content exceptions | No | Yes | Useful video transport, not locked-screen mirroring |
| Bluetooth HID / Switch Control | Can operate the visible unlocked UI | No independent video path | Partial accessibility-style control | Yes | Useful input transport only |
| AirPlay + Bluetooth HID | Active/unlocked | Yes, subject to AirPlay behavior | Partial | Yes | Current Orchard prototype |
| VoiceOver Screen Curtain | Device is active, not keybag-locked | AirPlay became black in the live test | Accessibility control remains possible | Yes | Hides both the phone and the needed capture |
| ReplayKit | App-scoped capture; not a remote keybag session | App/broadcast-dependent | No system-wide input API | Requires phone software | Does not meet product constraints |
| Genuine Apple relay | Official Mac owns the remote-unlock identity | Potentially relayable | Potentially relayable | No | Technically plausible, violates no-Mac requirement |

## Screen Curtain is not a lock

Apple describes VoiceOver Screen Curtain as keeping the iPhone active while the
display is off. That is useful for privacy, but it is deliberately not the Lock
Screen/keybag state:

- [Apple: Keep the screen off when using VoiceOver](https://support.apple.com/en-mide/guide/iphone/iph756788a12/ios)

The live Orchard test also found that enabling Screen Curtain made the AirPlay
capture black. It therefore cannot hide the physical display while leaving the
existing AirPlay receiver usable.

## Switch Control is not a video or unlock channel

Apple documents that Switch Control can tap, drag, type, scroll, and invoke
device actions through an external Bluetooth or wired switch:

- [Apple: Use Switch Control to navigate iPhone](https://support.apple.com/en-ca/119835)
- [Apple: Intro to Switch Control on iPhone](https://support.apple.com/guide/iphone/intro-to-switch-control-iphc9d32b862/26/ios/26)

This supports Orchard's Bluetooth-input direction, but it does not provide a
screen stream or a public remote-keybag-unlock capability. Pairing it with
AirPlay produces an unlocked-phone remote-control product, not iPhone
Mirroring's locked-phone security model.

## Engineering conclusion

There is no missing glue layer between the current AirPlay and Bluetooth
components that can make the iPhone genuinely remain locked. The missing layer
is the private remote-unlock trust relationship itself.

For a pure Windows, stock-iPhone, no-phone-app product:

- improving video latency, audio quality, window UI, and HID behavior remains
  worthwhile for the unlocked-phone mode;
- dimming or hiding the physical display can be offered only as a clearly
  labeled active-device privacy mode if a capture-compatible mechanism is ever
  found;
- the product must not describe Screen Curtain or an always-awake black display
  as “locked”; and
- true locked-screen mode should remain disabled unless Apple exposes a public
  authorization path or Orchard uses an explicit genuine-Apple relay.
