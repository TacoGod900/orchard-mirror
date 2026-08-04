# Screen Curtain compatibility gate

Status: **pending live iOS 27 verification**

Screen Curtain is an optional privacy mode, not a phone lock. Apple documents that the
iPhone remains active while its display is black. Orchard enables VoiceOver when needed
and sends the documented external-keyboard Screen Curtain chord. Because iOS exposes no
readable Screen Curtain state, Orchard never treats sending the shortcut as proof.

The Windows UI performs the first gate automatically: it records the presented-frame
counter, enables Screen Curtain, waits two seconds, and immediately turns the feature
back off if frames stop. If frames continue, it asks the user to perform the second gate:
click a harmless mirrored control and record whether one injected tap activates it or
VoiceOver only selects it. A translation mode must be added before shipment if selection
replaces activation.

The lighter fallback remains minimum brightness plus Zoom's Low Light filter. iOS does
not expose either setting through the verified CoreDevice configuration actions, so V1
does not mutate them blindly. Users may configure Zoom Low Light as an Accessibility
Shortcut; Orchard labels it as a manual fallback until a byte-verified action exists.

Safety invariants:

- teardown toggles off a curtain that Orchard enabled;
- teardown restores the prior VoiceOver setting;
- a media restart disables privacy mode rather than risking a black, uncontrolled phone;
- the UI describes the phone as active and unlocked while privacy mode is enabled.
