# ADR 0002: Isolate native applications behind an authenticated local protocol

Status: Accepted for the native vertical slice; production hardening required  
Date: 2026-07-18  
Decision owners: Orchard Architecture Council  
Required approvers: Security Engineering and Runtime

## Context

Orchard applications execute developer-controlled Swift code. That code can crash, hang, emit arbitrary output, consume resources, and attempt to interfere with the development host. Running it in the CLI, IDE, device shell, or renderer would turn an application defect into a trusted-process failure and would make later sandboxing impractical.

The host and application also need a low-latency bidirectional channel for configuration, render snapshots, input events, diagnostics, health checks, and lifecycle control. Standard input/output cannot be the application protocol because those streams belong to application bootstrap and logs. An unauthenticated TCP listener would unnecessarily expose a network endpoint and make local session ownership ambiguous.

## Decision

Every native Orchard application runs as a separate child process. A trusted Windows host creates a unique local named-pipe endpoint, launches exactly one child, authenticates that child, negotiates a versioned protocol, and then exchanges bounded typed messages. Application stdout and stderr remain independent bounded log streams and never carry protocol frames.

The version 1 vertical-slice transport has these properties:

- the server pipe is restricted to the current Windows user;
- each launch receives a random 128-bit endpoint suffix and independent 256-bit authentication token;
- the token is delivered through redirected standard input, not command-line arguments, environment variables, stdout, stderr, or persistent files;
- authentication comparison is fixed-time;
- the child sends `authenticate` sequence 0 and `hello` sequence 1;
- the host replies with `hello` sequence 0 and then begins the negotiated session;
- each direction has an independent monotonic sequence;
- frames use a four-byte big-endian length followed by strict UTF-8 JSON, with a 4 MiB maximum;
- unknown, missing, duplicate, wrong-case, malformed, over-depth, over-count, and out-of-range data fails closed;
- read, write, connect, handshake, shutdown, and process-wait operations are bounded and cancellable;
- normal shutdown is acknowledged; crash, timeout, protocol failure, and host disposal terminate or reap the child without terminating the host.

Protocol v1 contains typed messages for authentication, version/capability negotiation, device configuration, immutable render snapshots, revision-bound events, event results, diagnostics, ping/pong, and shutdown. It is not a general object-remoting protocol.

## Session invariants

1. A session ID is chosen by the application during authentication and is immutable after the handshake.
2. Authentication and hello messages cannot be replayed as ordinary session messages.
3. A message with the wrong version, session, sequence, direction, type, or payload is rejected before application dispatch.
4. An input event names the exact render revision, stable node ID, and semantic event. A stale event never reaches a closure registered by a newer tree.
5. Accepted state-changing events produce an event result and a strictly newer render snapshot.
6. Rejected events do not advance the published render revision.
7. The protocol carries semantic data only; it cannot grant ambient filesystem, device, credential, process, or network authority.
8. Protocol bytes, authentication material, and control messages are excluded from application log streams.

## Production hardening obligations

The current-user pipe and child-process boundary are necessary but not sufficient for production. Before untrusted third-party applications are supported, the launcher must additionally provide:

- AppContainer or a documented equivalent low-privilege token;
- a Job Object with process-tree, memory, CPU, handle, and termination policy;
- explicit capability broker handles rather than inherited ambient access;
- a per-application data container and virtualized resource roots;
- restricted handle inheritance and a verified startup attribute list;
- network denial by default with brokered, declared exceptions;
- executable, bundle, profile, and policy digest binding in the session admission record;
- crash capture and redaction that do not expose source, credentials, or unrelated host state;
- resource-exhaustion, race, cancellation, fuzz, soak, and sandbox-escape qualification.

The authentication token proves possession for one launch. It is not user authentication, tenant authentication, code signing, or authorization to capabilities.

## Alternatives considered

### In-process application hosting

Rejected. A crash, infinite loop, unsafe extension, or memory corruption would compromise trusted host availability and prevent a defensible sandbox boundary.

### Protocol over stdout/stdin

Rejected. It corrupts ordinary application logging, makes malformed output security-sensitive, complicates debugger use, and prevents clean separation between bootstrap credentials and long-lived traffic.

### Loopback TCP

Rejected for the local default. It exposes a network listener, requires additional firewall and port-collision handling, and offers no advantage over a current-user Windows IPC primitive.

### Shared memory as the only transport

Deferred. Shared memory may later carry large immutable render or media buffers behind an authenticated control channel, but it is too complex for the control protocol and requires separate lifetime and access-control rules.

## Consequences

- The host survives ordinary child crashes and can report bounded diagnostics.
- Swift closures and state remain in the native application process across render revisions.
- Renderer and device code can evolve independently behind a versioned contract.
- Every launch pays process and handshake overhead, which must be measured against the warm-launch SLO.
- Protocol compatibility, cancellation, and process cleanup become permanent test surfaces.
- AppContainer and capability-broker work remain hard gates; the vertical-slice pipe alone is not a production sandbox.

## Verification

The vertical-slice gate requires automated tests for valid handshake, invalid token, strict framing, wrong sequence/session/version, first render, text-field change, button press, stale-event rejection, ping/pong, graceful shutdown, child crash, timeout, cancellation, output truncation, stdout separation, and concurrent full-duplex I/O. Production gates add sandbox escape, resource policy, inheritance, and fuzz/soak suites.

