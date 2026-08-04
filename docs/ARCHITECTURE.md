# Project Orchard Architecture

Status: Target architecture  
Last updated: 2026-07-18  
Owners: Orchard Architecture Council, Security, and Clean-Room Governance

## 1. Purpose

Project Orchard is a Windows-native development environment for building, running, debugging, and testing a documented subset of Swift applications that are ultimately intended for Apple platforms. Orchard is a source-compatibility system. It is not an Apple operating-system emulator, a redistribution of an Apple SDK, or a replacement for official Apple signing and release tooling.

This document defines the production architecture, trust boundaries, subsystem contracts, and constraints that all implementation work must follow. The source-compatible target decision is recorded in [ADR 0001](decisions/0001-source-compatible-windows-target.md).

[ADR 0002](decisions/0002-native-application-process-and-local-protocol.md)
is accepted for the native vertical slice and requires further production
hardening. The Orchard bundle/runtime ABI, UI kernel/render graph, and LSP/DAP
boundaries remain proposed in [ADR 0003](decisions/0003-application-bundle-and-runtime-abi.md),
[ADR 0004](decisions/0004-ui-kernel-and-render-graph-boundary.md), and
[ADR 0005](decisions/0005-language-server-and-debug-adapter-boundaries.md).

Related controls are defined in:

- [Clean-room and legal operating model](CLEAN_ROOM_AND_LEGAL.md);
- [Threat model](THREAT_MODEL.md);
- [Quality, compatibility, and release policy](QUALITY_AND_COMPATIBILITY.md);
- [Diagnostic catalogue](DIAGNOSTICS.md).

## 2. Architectural assertions

The following are invariants rather than implementation preferences:

1. A local Orchard application is compiled from source into Windows PE/COFF code and links only to redistributable Swift components, Orchard-owned components, and approved third-party dependencies.
2. Orchard does not execute IPAs, Mach-O application binaries, Apple framework binaries, simulator runtimes, or Apple operating-system images on Windows.
3. Daily local development does not require an online service after the selected toolchain and packages have been installed.
4. Official Apple builds, signing, device execution, and distribution occur only in the Apple Validation Plane, on licensed Apple hardware using official tooling.
5. Clean-room specifications are the only route by which protected compatibility behavior enters implementation teams. Legal approval and provenance are part of the build inputs.
6. Unsupported behavior is never silently accepted. A capability has an execution disposition of local, simulated, Apple-validation-only, or unavailable, and a separate evidence-backed qualification level. The tools report both.
7. Application code is untrusted. Local and cloud build execution is isolated from control services, credentials, other applications, and other tenants.
8. Compatibility is versioned by an Orchard profile. It is not an unqualified claim of compatibility with every API or application on an Apple platform.
9. Stable, versioned contracts connect major subsystems. Framework implementations must not depend on editor, simulator-shell, or cloud implementation details.
10. The production compiler path uses the upstream Swift compiler architecture. The bootstrap parser in this repository is not a production compiler design.

## 3. Product and compatibility boundary

The production local path supports source projects that can be compiled for an Orchard Windows execution target. Source-only Swift packages can participate when their platform assumptions and dependencies are compatible. Binary-only CocoaPods, Mach-O frameworks, and Apple-platform XCFramework slices cannot run locally unless their vendor provides a separately licensed Orchard Windows slice.

Every compatibility profile publishes:

- supported declarations and behavioral level;
- source and package restrictions;
- locally simulated capabilities;
- validation-only frameworks;
- known differences and confidence;
- supported Windows versions and architectures;
- supported Swift and package-tool versions;
- upgrade and retirement dates.

Production readiness means reliable behavior within that published profile and an explicit route for everything outside it. It does not mean that all Apple APIs are implemented locally.

## 4. Current repository state

The repository now has a retained bootstrap lane and an early production-directed
feasibility lane. Neither is the production architecture described by the rest
of this document.

The retained bootstrap lane contains:

- `Orchard.Core`, which defines the early project manifest, Orchard IR `0.1.0`,
  diagnostics, strict bounded JSON/file handling, and device profiles;
- `Orchard.Compiler`, which lexes and parses a small SwiftUI-shaped source subset;
- `Orchard.Runtime.Windows`, which renders that bootstrap model in a WinForms
  host; and
- `Orchard.Cli`, which demonstrates project creation, analysis, build, run,
  compatibility, and inspection commands.

The feasibility lane currently contains:

- an installer- and binary-hash-pinned official Swift 6.3.3 Windows toolchain,
  including SwiftPM, `swift-format`, SourceKit-LSP, LLDB, and LLDB-DAP;
- Swift `OrchardProtocol` and `OrchardUI` modules plus a native `NativeHello`
  sample compiled for Windows;
- strict cross-language Orchard IR fixtures and a shared, fail-closed versioned
  bootstrap capability profile;
- a main-actor `RenderSession` with structural/keyed identity, session state
  slots, bindings, semantic events, monotonic revisions, and stale-event
  rejection;
- `.NET` `Orchard.Protocol`, which defines typed version 1 process messages and
  bounded framing; and
- `.NET` `Orchard.Transport.Windows`, which implements random current-user named
  pipes, independent per-session authentication, negotiation, bounded waits,
  cancellation, and full-duplex messaging.

The native Swift pipe adapter and persistent .NET host are under integration.
Their source presence is not architectural exit evidence: the repository has not
yet recorded one complete process test covering live render, change and press
events, revision reconciliation, stdout/stderr separation, hostile handshake,
timeout, shutdown, and crash containment.

The following current details are explicitly non-production:

- parsing Swift source with an Orchard-authored partial parser;
- treating Orchard IR as an executable application binary or final runtime ABI;
- using WinForms controls as the final UIKit, SwiftUI, or UI-kernel implementation;
- treating the Orchard-owned `OrchardUI` feasibility API as Apple SwiftUI;
- treating a current-user named pipe as the planned AppContainer/equivalent
  sandbox and brokered-capability boundary;
- deriving a compatibility claim from the bootstrap capability score; and
- inferring debugger or language-server readiness from successful executable
  launch probes.

Focused evidence is recorded in [CURRENT_STATUS.md](CURRENT_STATUS.md). Code may
be retained as a test fixture or migration scaffold, but it must not constrain
the production compiler, process model, compatibility model, or renderer.

## 5. Three-plane architecture

```text
+----------------------------+        approved, versioned specifications
| Clean-Room Compatibility   | ------------------------------------------+
| Plane                      |                                           |
|                            |                                           v
| public-source provenance   |                              +---------------------------+
| behavioral specifications |                              | Local Windows Development |
| differential test designs |                              | Plane                     |
| legal approvals            |                              |                           |
+-------------+--------------+                              | compiler, SDK, runtime    |
              |                                             | device, debugger, tests   |
              | approved tests and expected behavior        +-------------+-------------+
              v                                                           |
+----------------------------+                                            | immutable source and
| Apple Validation Plane     | <------------------------------------------+ build manifest
|                            |
| official build workers     | ------------------------------------------> signed attestations,
| official simulators        |                results and artifacts        differences, release build
| physical Apple devices     |
+----------------------------+
```

### 5.1 Local Windows Development Plane

This plane owns the edit/build/run/debug/test loop. It contains the signed toolchain, project and package resolver, compiler driver, Orchard SDK, runtime, renderer, compatibility device, developer tools, and local test infrastructure.

Its required properties are:

- offline operation after dependencies are present;
- reproducible and pinned builds;
- per-application process isolation;
- no Apple proprietary runtime dependency;
- deterministic compatibility diagnostics;
- explicit network and telemetry consent;
- side-by-side profile and toolchain installation;
- signed update with rollback.

### 5.2 Clean-Room Compatibility Plane

This plane owns the permitted description of compatible behavior. It is organizationally and technically separated from framework implementation.

It contains:

- a counsel-approved input registry;
- API declarations and behavioral specifications with immutable provenance;
- black-box experiments that counsel has explicitly permitted;
- expected lifecycle, layout, error, accessibility, and performance behavior;
- differential test definitions;
- review and quarantine workflows;
- implementation-ready specification releases.

The data flow is one way: implementation teams receive approved specifications and tests. They do not receive Apple binaries, extracted headers, private symbols, decompiled material, restricted assets, or unapproved observation notes. A specification release is signed and identified by digest so an Orchard compatibility profile can identify exactly which approved material it implements.

### 5.3 Apple Validation Plane

This plane is optional for most daily operations and mandatory for release qualification. It runs on licensed Apple hardware and uses official Apple tooling under a counsel-approved commercial model.

It owns:

- official compiler and linker builds;
- official simulator tests;
- physical device tests;
- screenshot, accessibility, lifecycle, and trace comparisons;
- signing with customer-authorized credentials;
- release artifact creation;
- validation attestations and audit records.

The Apple plane never supplies Apple SDK artifacts to the local plane. Results cross the boundary as structured observations, approved compatibility findings, customer artifacts, and signed attestations.

## 6. Local logical architecture

### 6.1 Toolchain manager

The toolchain manager installs signed, content-addressed bundles containing the supported Swift, LLVM, Clang, LLDB, SourceKit-LSP, SwiftPM, Orchard SDK, runtime, and device profiles. It supports stable and preview channels, side-by-side versions, installation verification, and rollback.

It must not mutate a selected toolchain in place. A project lock records the complete toolchain and compatibility-profile identity.

### 6.2 Project service and build daemon

`orchardd` is a user-scoped daemon that owns project graphs, package resolution, the incremental build graph, content-addressed local caches, device lifecycle, and tool coordination. The CLI and editor extension are clients; neither contains authoritative build logic.

Build workers are separate, constrained processes. Package plugins, build scripts, compiler plugins, and user build steps execute with declared filesystem and network capabilities. A build worker cannot access validation credentials, another project's private cache, or the device broker unless its manifest grants that ability.

### 6.3 Compiler driver and compatibility target

Orchard minimally extends upstream Swift and SwiftPM to separate the execution ABI from the source compatibility profile:

- code generation targets Windows x64 or ARM64 and emits PE/COFF;
- source availability and framework imports target a versioned Orchard compatibility profile;
- packages resolve for an Orchard virtual destination;
- Orchard-specific behavior is detectable with an explicit target-environment condition;
- conditional compilation that would choose Darwin-only code is analyzed and diagnosed;
- source locations and symbol information remain usable by LLDB and official validation builds.

The compiler produces an Orchard Application Bundle. This is an Orchard-defined format and must never be presented as an IPA or Apple application bundle.

### 6.4 Orchard runtime

Generated applications link to a versioned Orchard Runtime ABI. The ABI is deliberately narrow and C-compatible so framework releases do not bind directly to unstable compiler internals.

The runtime owns application entry, lifecycle, main-thread and run-loop rules, Swift concurrency integration, resource lookup, localization, structured logging, crash capture, capability negotiation, and connection to brokered device services.

### 6.5 Framework compatibility layers

Foundation-compatible functionality is built from approved open-source components and Orchard-owned overlays. UIKit and SwiftUI are independently implemented against approved behavioral specifications. They share a UI kernel rather than wrapping Windows widgets directly.

The shared UI kernel owns view identity, state transactions, layout, constraint solving, text, focus, responder routing, gestures, animation, scrolling, accessibility semantics, resource resolution, and the retained render graph.

UIKit and SwiftUI adapters may depend on the UI kernel. The UI kernel must not depend on either public framework adapter.

### 6.6 Renderer

The production renderer targets Direct3D and DirectComposition, with DirectWrite for text and approved Windows or portable libraries for media and image decoding. It provides a deterministic software path for tests and a GPU path for interactive use.

The renderer consumes the Orchard render graph. It does not receive SwiftUI or UIKit objects, access application files, or call cloud services. GPU faults must be isolated from the application and device shell where supported.

### 6.7 Compatibility device

The compatibility device is a Windows host for one or more sandboxed Orchard application processes. Device profiles describe logical display dimensions, scale, safe areas, appearance, locale, accessibility settings, memory policy, and available simulated capabilities.

Brokered services include location routes, motion, virtual camera fixtures, photo-library fixtures, notifications, deep links, pasteboard, permissions, background transitions, network conditioning, memory pressure, and test biometrics. A simulated service must be marked as simulated and cannot produce a real payment, health, enclave, or entitlement result.

### 6.8 Developer tools

The first supported IDE surface is a thin VS Code extension over stable protocols:

- SourceKit-LSP for language features;
- Debug Adapter Protocol and LLDB for debugging;
- a test adapter for unit and UI tests;
- the Orchard Device Control Protocol for device operations;
- the Orchard Inspection Protocol for view, layout, accessibility, task, and performance inspection.

The CLI uses the same daemon and contracts. A future Visual Studio extension or standalone IDE must also use these contracts rather than creating a second build system.

## 7. Process and trust boundaries

### 7.1 Local process model

```text
Untrusted project content
          |
          v
+-------------------+       versioned RPC       +------------------------+
| CLI / IDE client  | <------------------------> | orchardd               |
+-------------------+                            | graph and orchestration |
                                                  +---+-----------------+--+
                                                      |                 |
                                             constrained build    device control
                                                      |                 |
                                                      v                 v
                                             +---------------+   +----------------+
                                             | build worker  |   | device shell   |
                                             +---------------+   +-------+--------+
                                                                           |
                                                              authenticated local IPC
                                                                           |
                                        +------------------+---------------+----------------+
                                        v                  v                                v
                                +---------------+  +------------------+             +---------------+
                                | app process   |  | capability broker|             | renderer/GPU  |
                                | AppContainer  |  | policy boundary  |             | isolated path |
                                +---------------+  +------------------+             +---------------+
```

The application process is always treated as hostile. It is placed in a Windows AppContainer or a documented equivalent and constrained by Job Objects and resource limits. It cannot directly access arbitrary host files, devices, credentials, the daemon's cache, or other application processes.

The capability broker is the sole authority for simulated device services and user-mediated host access. Each IPC connection is bound to a launched application identity, device session, short-lived token, declared capability set, and protocol version. Local IPC uses named pipes or another Windows primitive with access control lists; it must not expose unauthenticated TCP listeners.

### 7.2 Validation-service process model

The validation control plane is separated from execution workers and secrets:

- the API accepts an immutable source snapshot and build manifest;
- an admission service verifies tenant, policy, digest, and requested capabilities;
- a scheduler allocates a freshly provisioned Mac worker and, when requested, a physical device;
- a secret broker grants only the per-job credentials required, for the job lifetime;
- workers have restricted egress and no cross-tenant mutable cache;
- results are signed, encrypted, uploaded, and then the worker is destroyed or securely reimaged;
- the control plane never executes project code.

Enterprise private pools use the same contracts and isolation model. A private pool changes tenancy, not the security design.

### 7.3 Clean-room boundary

The compatibility specification repository and the implementation source repository have separate access groups. Automated export validates legal approval, strips non-approved working notes, assigns immutable provenance, and signs the released specification bundle. Direct implementation access to observation workspaces is prohibited.

## 8. Versioned subsystem contracts

| Contract | Producers | Consumers | Stability rule |
|---|---|---|---|
| Orchard Project Manifest | project tools and users | daemon, build planner, IDE | JSON schema versioned; unknown required fields fail closed |
| Toolchain Lock | resolver | daemon, CI, validation service | immutable digests; no floating production versions |
| Compatibility Profile | clean-room release and framework teams | compiler, analyzer, runtime, IDE, validation | signed and immutable; additive patch releases only |
| Build Manifest | build planner | local workers and validation service | canonical serialization and digest; inputs fully enumerated |
| Orchard Application Bundle | compiler/linker | device launcher | Orchard-owned signed format; never accepts Apple binary payloads |
| Orchard Runtime ABI | runtime team | generated code and frameworks | C ABI, semantic versioning, negotiated minimum/maximum version |
| UI Kernel Contract | framework adapters | UI kernel | internal versioned interface; adapters cannot bypass it |
| Render Graph Contract | UI kernel | renderer and inspector | immutable frame snapshots; renderer has no app authority |
| Capability Protocol | application runtime | broker and device services | authenticated, capability-scoped, backward compatible within profile |
| Device Control Protocol | CLI, IDE, test runner | daemon and device shell | request IDs, cancellation, idempotent lifecycle operations |
| Debug/Test Protocols | developer tools | LLDB, runtime, test agent | standards first; Orchard extensions are namespaced and negotiated |
| Validation Job | local/CI client | validation admission and scheduler | immutable source digest, explicit retention and signing request |
| Validation Attestation | validation workers | portal, CI, release workflow | signed, tamper-evident, references exact toolchain and artifacts |

Contracts are defined in dedicated schema packages, tested with compatibility fixtures, and reviewed before breaking changes. A source project, application bundle, capability service, and validation result may evolve at different rates; they must not share one catch-all schema version.

## 9. Primary flows

### 9.1 Local build and run

1. The client opens a project through `orchardd`.
2. The daemon resolves the locked toolchain, compatibility profile, source packages, and declared capabilities.
3. The compatibility analyzer reports unsupported, simulated, and validation-only dependencies before execution.
4. Constrained workers compile changed sources for the Windows ABI against Orchard SDK modules.
5. The linker emits a signed Orchard Application Bundle and build manifest.
6. The daemon creates a device session, app sandbox, broker policy, and runtime token.
7. The application starts in its own process; the debugger and test agent attach through negotiated protocols.
8. Runtime and broker events are structured and attributed to the build, app, and device session.

### 9.2 Validate and publish

1. The client freezes a source snapshot, dependency lock, requested Apple destination, and retention policy.
2. The validation admission service authenticates and records immutable digests.
3. An ephemeral Apple worker obtains source and short-lived secrets.
4. Official tooling resolves permitted dependencies and builds independently; local Orchard binaries are never submitted as the Apple application.
5. Tests run on official simulators and selected devices.
6. Comparators produce behavioral, visual, accessibility, and performance differences.
7. If authorized, official tooling signs and exports the release artifact.
8. The service returns encrypted artifacts and a signed attestation, then destroys job secrets and worker state.

### 9.3 Compatibility specification release

1. The specification team records behavior using only approved sources and methods.
2. Legal/provenance automation and reviewers approve or reject each input.
3. Approved declarations, behavior, expected outcomes, and tests form an immutable specification bundle.
4. Implementation teams implement against that bundle.
5. Differential tests verify behavior in the Apple plane without transferring proprietary implementation material.
6. The compatibility profile records evidence, confidence, limitations, and validation recency.

## 10. Compatibility model

Execution disposition and conformance qualification are separate dimensions. Disposition answers where a behavior can execute: local, simulated, Apple-validation-only, or unavailable. Qualification answers how much evidence supports it: full, constrained, simulated, Apple validation required, or unavailable, as defined by the [quality and compatibility policy](QUALITY_AND_COMPATIBILITY.md).

A raw symbol count is insufficient. Each API or behavior records independently:

- declaration availability;
- compile support;
- functional behavior;
- lifecycle and threading behavior;
- error behavior;
- visual qualification;
- accessibility qualification;
- performance qualification;
- differential-validation evidence and recency;
- local, simulated, validation-only, or unsupported disposition.

An application report is weighted by statically reachable and dynamically exercised behavior, package risk, capability severity, test coverage, and evidence confidence. A percentage, if shown, is accompanied by its corpus, profile, confidence, and blocking differences.

Unknown or unsupported APIs fail with stable diagnostics by default. Diagnostic placeholders require an explicit development option and cannot be used for release qualification.

## 11. Build versus buy

### Build and own

- compatibility-platform compiler and SwiftPM changes;
- Orchard SDK modules and runtime ABI;
- clean-room specifications, provenance, and compatibility catalog;
- UI kernel, SwiftUI/UIKit adapters, layout semantics, and accessibility mapping;
- capability broker and compatibility device behavior;
- developer protocols and inspection tools;
- application compatibility analysis and reporting;
- validation scheduling, comparison, attestation, and customer workflow;
- tenant isolation policy and release provenance.

### Reuse with license and security review

- upstream Swift, LLVM, Clang, LLDB, SourceKit-LSP, and SwiftPM;
- Direct3D, DirectComposition, DirectWrite, Windows Imaging Component, Media Foundation, and Windows security APIs;
- Skia or other permissively licensed graphics primitives where they do not define public behavior;
- ICU/CLDR, SQLite, OpenTelemetry, and established cryptographic libraries where required;
- standard LSP, DAP, and test-adapter protocols;
- managed object storage, KMS/HSM, identity, observability, and artifact-registry infrastructure.

### Buy or partner initially

- licensed Apple hardware capacity and physical device-lab operations;
- enterprise identity and billing services;
- external penetration testing, compliance audits, and clean-room legal audits;
- code-signing certificate custody and managed secret infrastructure.

Any acquired component must be replaceable behind an Orchard-owned contract. Managed services must not become necessary for offline local development.

## 12. Security, privacy, and supply chain

- Every production binary, toolchain bundle, profile, and schema package is signed.
- Dependencies are pinned, scanned, licensed, and represented in an SBOM.
- Toolchains install side by side and update through staged channels with rollback.
- Project code, package scripts, compiler plugins, applications, and validation jobs are untrusted workloads.
- Application host access is capability-based, least-privilege, user-visible where appropriate, and auditable.
- Telemetry is documented, minimized, and consented to; source code, UI content, filesystem paths, and credentials are excluded by default.
- Cloud artifacts are encrypted in transit and at rest with tenant-aware retention and deletion.
- Signing credentials are customer-authorized, short-lived where possible, brokered from KMS/HSM storage, redacted from logs, and unavailable to control-plane services.
- Validation workers are ephemeral or securely reimaged and never reuse mutable cross-tenant caches.
- Local crash collection works without upload; upload is explicit or governed by enterprise policy.

## 13. Reliability and evolution

The local edit/run path must continue during cloud outages. Validation is asynchronous and idempotent. Clients can retry using the same source digest without producing ambiguous duplicate release jobs.

Compatibility profiles and toolchains are immutable after release. Corrections are new patch identities. Projects can pin old profiles during a published support window. The system supports stable, preview, and internal qualification channels.

Core service objectives are defined per release, including compiler crash frequency, incremental build latency, launch latency, frame misses, runtime crash-free sessions, memory growth, update rollback success, validation job success, queue time, and service availability.

## 14. Prohibited architecture

The following designs must not be introduced without superseding this document and ADR 0001 through architecture, security, and legal review:

1. Shipping or loading Apple SDK binaries, framework binaries, simulator runtimes, operating-system images, fonts, symbols, or protected assets on Windows.
2. Executing IPAs or Mach-O application code in the local runtime.
3. Extracting Apple tools from Xcode for local compilation, linking, signing, or simulation.
4. Describing a remote Mac desktop or video stream as the local Orchard runtime.
5. Submitting an Orchard Windows application bundle to App Store tooling as though it were an official Apple build.
6. Using a partial source parser, source-to-JSON transpiler, or interpreter as the production Swift compiler.
7. Hosting untrusted application code, package plugins, or build scripts inside the CLI, daemon, device shell, broker, cloud control plane, or another tenant's worker.
8. Granting applications ambient host filesystem, network, device, credential, or process access.
9. Exposing unauthenticated local or cloud control endpoints.
10. Sharing mutable compiler, package, signing, or source caches across cloud tenants.
11. Requiring a cloud connection for ordinary local builds after dependencies are installed.
12. Updating a selected toolchain or compatibility profile in place or using floating versions for production builds.
13. Reporting unsupported behavior as success, silently no-oping an API, or substituting a security-sensitive result.
14. Representing a symbol-count average as application compatibility without evidence, weighting, and limitations.
15. Treating `os(iOS)` as a universal compiler lie without package analysis and an explicit Orchard target environment.
16. Loading binary Apple-platform packages locally unless the vendor supplies a separately authorized Orchard Windows artifact.
17. Sending project source, application content, credentials, or user data through telemetry by default.
18. Allowing implementation teams direct access to unapproved clean-room observations, private API material, leaked sources, or decompiled artifacts.

## 15. Consequences and trade-offs

This architecture enables a fast local Windows loop without redistributing Apple software and preserves an official release path. It also imposes permanent costs:

- Orchard maintains a compiler/SwiftPM delta and must rebase it continuously.
- Source compatibility is attainable only within versioned profiles; binary compatibility is intentionally excluded.
- Third-party packages with Darwin assumptions need source changes, overlays, or vendor support.
- Objective-C interoperability is a separately gated, high-risk program.
- Exact visual identity may be impossible where fonts and system assets cannot be redistributed; official validation remains the release truth.
- Hardware-backed and entitlement-controlled functionality remains simulated or validation-only.
- The clean-room organization and compatibility lab are permanent product infrastructure.
- Apple platform changes create a documented compatibility lag; day-zero full parity is not a support promise.

These costs are accepted because the alternatives either retain daily Mac dependence, create unacceptable legal and licensing exposure, or cannot meet the local performance and debugging goals.

## 16. Required follow-up decisions

Separate ADRs are required before implementation for:

- runtime ABI and Orchard Application Bundle format;
- compiler compatibility-platform mechanics and conditional-compilation rules;
- local sandbox and capability protocol;
- UI kernel and render-graph boundary;
- package overlay and vendor-slice policy;
- Objective-C scope and go/no-go criteria;
- compatibility scoring and evidence model;
- validation credential custody and worker isolation;
- telemetry and customer-source retention;
- profile versioning and support lifecycle.
