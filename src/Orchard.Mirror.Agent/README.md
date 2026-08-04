# Orchard Mirror CoreDevice agent

This process is Orchard Mirror's GPL-3.0 control-plane boundary. It imports
`pymobiledevice3`, owns the userspace RSD tunnel, and exposes a versioned JSON-lines
protocol over standard input/output. Logs go to standard error; standard output is
reserved for protocol messages.

The proprietary C# application starts this agent as a separate process. No Python or
`pymobiledevice3` code is linked into the application.

## Runtime

- Python 3.11 or newer
- `pymobiledevice3` 10.3 or newer
- Apple Devices (or another working `usbmuxd`) on Windows
- an iPhone with Developer Mode enabled

For development, create `.venv` at the repository root and install
`pymobiledevice3` into it. The Windows supervisor also accepts
`ORCHARD_MIRROR_PYTHON` and `ORCHARD_MIRROR_AGENT` overrides.

## Protocol

One UTF-8 JSON object is sent per line. Requests have `version`, `id`, `command`, and
an `arguments` object:

```json
{"version":1,"id":"1","command":"connect","arguments":{"udid":null}}
```

Responses echo `version` and `id`, and contain either `ok: true` plus `result`, or
`ok: false` plus an error with a stable `code` and a human-readable `message`.

The control command set is `hello`, `mount-ddi`, `connect`, `device-info`,
`lockstate`, `start-video`, `stop-video`, `request-keyframe`, `touch`, `drag`, `type`, `key`,
`button`, `screen-curtain`, `privacy-status`, `disconnect`, and `stop`. `start-video`
accepts a loopback host/port and relays every device RTP datagram verbatim to the C#
receiver. Non-loopback relay destinations are rejected.

The video negotiation mirrors Xcode DeviceHub: HEVC long-term-reference mode, its
private RCTL receiver reports, and a paired system-audio media session sharing the
same client session identifier. The agent drains that companion audio stream and
sends its keepalive reports but does not relay, decode, or play audio; Orchard Mirror
v1 remains video-only. Pairing is required for iOS to sustain a clean full-rate HEVC
stream under motion.

Input is accepted only while video is active because iOS authenticates the HID surfaces
against the media session. Touch coordinates are normalized unsigned 16-bit values, so
the protocol is independent of phone and window resolution.

`screen-curtain` is deliberately gated. It enables VoiceOver when necessary and sends
Apple's external-keyboard Screen Curtain chord, but iOS exposes no readable curtain
state. The response therefore says `verificationRequired`; the Windows UI verifies that
frames continue and asks the user to confirm VoiceOver tap behavior. Ordered teardown
always toggles an Orchard-enabled curtain off and restores the prior VoiceOver setting.

Run directly with:

```text
python src/Orchard.Mirror.Agent/agent.py
```

## Licensing

Copyright (C) 2026 Orchard contributors. This agent is free software licensed under
GNU GPL version 3.0 only. See `LICENSE` in this directory. Orchard's C# application is
a separate program communicating with this agent over the documented process
protocol; see ADR 0014.
