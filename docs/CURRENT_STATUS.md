# Current status

Status date: 18 July 2026

Project Orchard is in an **early native-Swift feasibility stage**. It has moved
beyond a .NET-only bootstrap: the repository now has a pinned Swift for Windows
toolchain, an Orchard-owned Swift SDK, strict cross-language contracts, a
revisioned state engine, and an authenticated .NET local transport. It is still
not an iOS compatibility runtime, and the Phase 5 persistent native child-to-host
slice has not yet passed its end-to-end gate.

## Evidence rule

This document distinguishes three states:

- **Verified** means the named suite or command completed successfully in this
  working program before this status snapshot.
- **Implemented, integration pending** means source and focused evidence exist,
  but the composite or process-level gate has not passed.
- **Planned** means the production execution plan owns the work and no completion
  is claimed here.

Source files, configured CI, or a test name are not by themselves verification.
The working tree is receiving concurrent native-session changes, so the complete
repository check must be rerun after that integration settles.

## Verified evidence

### Toolchains

- .NET SDK `10.0.203` is selected by `global.json` and agrees with the Orchard
  toolchain lock.
- Official Swift `6.3.3` for `x86_64-unknown-windows-msvc` is pinned by installer
  URL and SHA-256. The installed `swift.exe`, `swift-format.exe`,
  `sourcekit-lsp.exe`, `lldb.exe`, and `lldb-dap.exe` are also hash-pinned.
- The launcher verifies the selected binary, imports a compatible Visual Studio
  2022 C++ environment, verifies Windows SDK `10.0.26100.0`, and supplies the
  pinned Swift Windows SDK path to SwiftPM.
- Pinned version/tool launch probes and release builds of the Swift SDK and
  `NativeHello` sample have completed successfully.

This is local feasibility evidence, not a clean-machine installation, debugger,
language-server workflow, signed distribution, or production support matrix.

### Test suites

The latest independently verified focused results are:

| Suite | Verified result | Scope |
| --- | ---: | --- |
| `Orchard.Tests` | 39/39 | Bootstrap compiler/CLI/core hardening, bounded files and trees, strict JSON, deterministic IR, shared IR fixtures, and capability-profile parity |
| `Orchard.Protocol.Tests` | 42/42 | Version 1 message model, strict JSON, framing, negotiation, limits, Unicode, tree/event validation, and malformed-input rejection |
| `Orchard.Transport.Windows.Tests` | 9/9 | Current-user endpoint credentials, authentication, full-duplex frames, negotiation failure, timeouts, cancellation, fragmentation, disposal, and listener reuse |
| Swift SDK tests | 32/32 | Orchard IR validation/profile parity, deterministic encoding, state/binding/identity/revision behavior, stale-event rejection, actor isolation, and module loading |

The three .NET suites total 90 passing tests. Do not combine these focused runs
with the Swift result into a claim that the current live-session working tree has
passed one 122-test release gate: the composite post-integration check has not
yet been recorded.

### Strict data and capability contracts

- Orchard IR version `0.1.0` has bounded, strict UTF-8 readers in .NET and Swift.
  Unknown, wrong-case, duplicate, missing, malformed, oversized, unsafe-control,
  invalid-enum, excessive-depth, excessive-node, duplicate-ID, and invalid-state
  inputs fail closed.
- Accepted, rejected, and compact canonical fixtures are shared under
  `tests/fixtures/ir-v0.1` and exercised by both implementations.
- `schemas/orchard-capabilities-v1.json` is the single checked-in bootstrap
  profile. .NET and Swift tests verify the same classifications and weights;
  unknown source symbols are unsupported rather than silently scored as full.
- Reproducible native sample metadata supports `SOURCE_DATE_EPOCH` and keeps
  non-deterministic provenance outside canonical application output.

This remains an Orchard bootstrap contract. It is not an Apple SDK contract and
is not proof of behavioral compatibility.

### OrchardUI state and render model

The Swift SDK currently verifies an Orchard-owned declarative feasibility API
with:

- `View`, result builders, `Text`, `Button`, `TextField`, stacks, conditional
  content, loops, modifiers, `State`, and `Binding`;
- deterministic structural identity plus explicit keyed identity;
- session-owned nested and conditional state slots;
- collision-safe state names and fail-closed duplicate/unsafe identities;
- revisioned `RenderSnapshot` values;
- semantic `press` and `change` dispatch to Swift closures;
- rejection of stale revisions before handler lookup; and
- main-actor isolation for view, state, binding, rendering, and dispatch.

These are SDK-level semantics. They do not claim Apple SwiftUI identity, layout,
lifecycle, environment, animation, navigation, accessibility, or rendering
behavior.

### .NET process protocol and transport

- `Orchard.Protocol` defines strict version 1 typed messages with four-byte
  big-endian framing, a 4 MiB frame budget, bounded trees/strings/collections,
  version negotiation, session IDs, and sequence validation.
- `Orchard.Transport.Windows` uses random 128-bit pipe names, independent 256-bit
  authentication tokens, current-user-only named pipes, fixed-time token checks,
  bounded connection/handshake/I/O waits, cancellation, deterministic disposal,
  and no protocol data on application stdout.

That transport has focused .NET integration evidence. It is not a production
sandbox: a same-user credential theft threat remains, and AppContainer/equivalent
isolation plus a capability broker are later gates.

### Retained bootstrap

The legacy `Orchard.Compiler`, `Orchard.Cli`, and
`Orchard.Runtime.Windows` path still proves project loading, diagnostics, a small
SwiftUI-shaped parse, IR serialization, compatibility reporting, device-profile
selection, and an approximate WinForms preview. It remains explicitly disposable
bootstrap evidence and does not execute its input as Swift.

## Implemented, integration pending

The working tree is adding the missing live boundary between a native Windows
Swift child and a persistent .NET host. Native Swift transport and host source may
already be present while this work is underway; presence is not exit evidence.
The required vertical-slice boundary is recorded in
[ADR 0002](decisions/0002-native-application-process-and-local-protocol.md).

The integration remains open until a real process test proves all of the
following in one controlled flow:

1. The host creates an unguessable current-user endpoint and starts the native
   child without placing credentials in command-line arguments or logs.
2. Authentication and protocol-version negotiation complete within bounded
   timeouts.
3. The child publishes an initial render tree from a live Swift `RenderSession`.
4. A text-field change reaches the matching Swift binding and produces the next
   revision with the new value.
5. A button press invokes the stored Swift closure and publishes the expected
   subsequent state/revision.
6. An event for an old revision is rejected without mutation.
7. Ping/pong, sequence validation, fragmented framing, and full-duplex operation
   remain deterministic.
8. Application stdout/stderr are captured separately and cannot corrupt or spoof
   protocol frames.
9. Graceful shutdown completes without a leaked process or blocked I/O.
10. Bad token, handshake timeout, malformed message, peer close, and child crash
    leave the host alive and produce bounded, redacted diagnostics.

After those tests pass, the full `eng/check.ps1` run, formatting checks, Release
build, all focused suites, native deterministic-output checks, and a clean-machine
Windows CI run must also pass. Until then, the native live session is **not
verified**.

## Capability matrix

| Area | Current evidence | Next production gate |
| --- | --- | --- |
| Source input | Legacy single-file subset plus real SwiftPM packages for the Orchard-owned SDK/sample | Multi-module projects, packages, resources, generated source, compatibility target rules |
| Language handling | Stock Swift 6.3.3 compiles the native OrchardUI lane; legacy parser remains isolated | Supported Swift-version/ABI matrix, concurrency/language conformance, compiler diagnostics |
| Build output | Native Windows Swift executables and Orchard IR artifacts | Versioned runtime ABI, Orchard Application Bundle, signed/reproducible packaging |
| UI model | Typed Orchard IR plus deterministic identity, state slots, bindings, revisions, and semantic events | Environment, lifecycle, retained reconciliation, focus, accessibility, navigation, collection scale |
| Host protocol | Strict .NET protocol and transport; Swift live adapter under integration | Verified cross-process session, protocol evolution fixtures, fuzz/soak, sandbox identity binding |
| Rendering | Approximate legacy WinForms controls | Persistent native render adapter, then deterministic UI kernel and D3D/DirectWrite renderer |
| Compatibility | Versioned bootstrap profile with fail-closed unknowns | Counsel-approved specifications, behavior corpus, evidence recency, false-confidence calibration |
| Developer tools | Tool binaries pinned and launchable | Actual SourceKit-LSP session, LLDB/DAP breakpoint/stack/variables, IDE integration |
| Security | Strict bounded inputs and authenticated current-user pipe | AppContainer/equivalent, brokered capabilities, hostile-process testing, external penetration test |
| CI/release | Repository workflow configured; focused local evidence green | Full post-integration check, clean-machine run, SBOM, signatures, provenance, installer lifecycle |
| Apple workflow | None | Counsel-approved remote validation pilot, official builds/devices, signing and submission controls |

## Contracts and compatibility boundary

Orchard currently has two related but distinct contract layers:

- Orchard IR `0.1.0` is a bootstrap application/view document shared by the
  compiler, sample, tests, and compatibility reporting.
- Process protocol version `1` carries authentication, negotiation, simulator
  configuration, render, event/result, ping/pong, diagnostics, and shutdown
  messages between an application and host.

Neither contract is an IPA, an Apple application bundle, an Apple framework API,
or the final Orchard Runtime ABI. Breaking changes require an explicit new
version, fixtures, migration notes, and compatibility tests; existing fields must
not be silently reinterpreted.

## Known limitations and risks

### No iOS, SwiftUI, or UIKit implementation

The native sample compiles real Swift for Windows against `OrchardUI`. It does
not import Apple SwiftUI/UIKit SDK modules, load proprietary Apple platform
framework binaries, produce Mach-O or an IPA, or run an iOS application.
OrchardUI tests establish only the behavior named in those tests.

### No verified persistent native preview yet

State and event closures survive inside `RenderSession`, but the persistent
cross-process session has not yet passed the process gate above. The legacy
WinForms window consumes static bootstrap IR and is not evidence for this gate.

### No production renderer or device simulator

WinForms controls, Segoe UI, and preview dimensions are Windows approximations.
There is no iOS layout engine, Core Animation behavior, Apple font/asset set,
GPU-qualified renderer, sensor broker, or virtualized iPhone/iPad.

### No debugger or language-server workflow evidence

Pinned LLDB, LLDB-DAP, and SourceKit-LSP executables launch, but a real breakpoint,
stack, variable, completion, navigation, or diagnostic exchange has not been
recorded.

### No low-privilege application sandbox

Strict input validation and a current-user authenticated pipe reduce exposure,
but developer code is still hostile workload. Same-user token theft, child
privileges, package scripts, filesystem/network authority, resource exhaustion,
and capability brokerage remain production security work.

### Compatibility scores are not fidelity evidence

The current profile is a fail-closed engineering catalogue. A high score does
not establish layout, lifecycle, accessibility, performance, privacy, device,
or App Store behavior. No 90-95% product claim is authorized without the corpus,
weights, profile, test evidence, official validation, and confidence required by
the production plan.

### Clean-room and Apple workflows remain external gates

The repository policies prohibit proprietary Apple content, but a real corporate
clean-room organization, counsel opinions, approved behavior lab, Apple hardware
validation service, signing custody, and App Store workflow do not yet exist.

## Next gates

Work proceeds in this order without weakening the master plan:

1. Complete and independently verify the headless native Swift child-to-.NET host
   session, including hostile and lifecycle cases.
2. Run the complete local check on the settled tree and obtain a clean-machine
   Windows CI result with archived evidence.
3. Exercise SourceKit-LSP through a real protocol session and LLDB/DAP through a
   real breakpoint, stack, and variable workflow under the proposed
   [ADR 0005](decisions/0005-language-server-and-debug-adapter-boundaries.md).
4. Bind the verified live session to a Windows render/event adapter and perform
   interactive keyboard, accessibility, DPI, focus, and crash-recovery smoke tests.
5. Specify and implement the low-privilege application sandbox, capability
   broker, incremental build/restart model, and the proposed
   [bundle/runtime ABI](decisions/0003-application-bundle-and-runtime-abi.md) and
   [UI-kernel/render-graph](decisions/0004-ui-kernel-and-render-graph-boundary.md)
   boundaries.
6. Satisfy the legal, clean-room, architecture, corpus, security, and reference-
   validation gates in Phases 1-5 before any developer-preview or compatibility
   promotion.

The full 27-phase production program, including renderer, simulator services,
IDE/debugging, framework breadth, Apple validation, enterprise operations, and
GA criteria, remains authoritative in [EXECUTION_PLAN.md](EXECUTION_PLAN.md).
