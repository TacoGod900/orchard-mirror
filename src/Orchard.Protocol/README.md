# Orchard process protocol v1

This project owns the local application/host wire contract. It has no transport authority and does not open listeners. A caller is expected to place it over an authenticated, current-user local transport and keep ordinary application stdout and stderr separate from protocol traffic.

## Framing

Each frame is exactly:

1. A four-byte unsigned, big-endian payload length.
2. That many bytes of strict UTF-8 JSON.

The JSON payload must contain between 1 byte and 4 MiB inclusive. The declared size is checked before allocation. Truncated headers and bodies, invalid UTF-8, comments, trailing commas, duplicate properties, unknown properties, case variants and unsupported enum encodings are rejected.

## Envelope

Every message is an object with exactly these properties, in protocol terms:

```json
{
  "version": 1,
  "type": "ping",
  "sessionId": "session-0001",
  "sequence": 4,
  "payload": {
    "nonce": "ping-4",
    "sentAtUnixMilliseconds": 100
  }
}
```

`version` is `1`; `sessionId` is a bounded safe identifier; and `sequence` is non-negative. `type` and the typed payload must agree. Version 1 defines `authenticate`, `hello`, `configure`, `render`, `event`, `eventResult`, `diagnostic`, `ping`, `pong` and `shutdown`.

`hello` advertises an inclusive minimum and maximum version. Negotiation selects the highest common version and fails if the ranges do not overlap.

## Render-tree profile

A render tree is limited to 128 node levels and 5,000 nodes. Node IDs are unique within the entire tree. Node kinds, properties and events use closed version 1 allowlists. IDs, names, values, collections and all other strings have explicit limits in `ProtocolConstants`; string limits are measured as encoded UTF-8 bytes. Validation uses an iterative traversal so the semantic depth check does not recursively consume the process stack.

Changing the meaning of a version 1 field is forbidden. Additive vocabulary changes require an explicit compatible profile decision; incompatible envelope or payload changes require a new protocol version and cross-language fixtures.
