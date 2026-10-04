# ADR 0001: Use a source-compatible Windows target

Status: Accepted  
Date: 2026-07-18  
Decision owners: Orchard Architecture Council  
Required approvers: Security and Clean-Room Governance

## Context

Project Orchard aims to let developers perform most ordinary Swift application development on Windows while preserving official Apple builds and device validation for release. The product needs a low-latency local edit, build, run, debug, and test loop. It must not require a remote Mac for every execution and must not redistribute Apple proprietary software.

There are several materially different ways to pursue that goal:

- emulate an Apple device and run existing Apple binaries;
- reproduce an Apple simulator environment on Windows;
- stream or remotely control a Mac;
- translate a small source subset into another UI technology;
- compile Swift normally for Windows and require applications to adopt a new API;
- compile source for a Windows execution ABI against independently implemented, source-compatible Orchard frameworks.

The current repository is a bootstrap experiment. It parses a small SwiftUI-shaped source subset into a JSON view model and renders that model with WinForms. That proves a CLI and simulator interaction, but it cannot provide full Swift semantics, Swift package compatibility, debugger fidelity, concurrency behavior, or production framework behavior.

Apple application binaries and official framework binaries target Apple runtimes and binary formats. Apple SDKs, simulator runtimes, system images, platform assets, signing tools, and distribution processes are subject to ownership and license constraints. Building a binary-compatible emulator would therefore add substantial technical and legal risk while still leaving signing and release dependent on official infrastructure.

## Decision drivers

- Local run and debug latency must be comparable to a native Windows development tool.
- Local applications must execute ordinary compiled Swift semantics, not a partial interpretation.
- Orchard must not require Apple proprietary binaries on Windows.
- Release artifacts must be produced by official Apple tooling on licensed Apple hardware.
- The system must provide honest, versioned compatibility rather than claiming universal parity.
- Framework and runtime work must be testable independently of the editor and cloud service.
- The design must support source-level third-party packages where technically and legally possible.
- The compiler delta must remain smaller than a new compiler or full device-emulation program.
- The product must have a defensible clean-room implementation boundary.

## Decision

Orchard will implement a **source-compatible Windows target**.

Application source is compiled with the upstream Swift compiler architecture, plus narrowly scoped Orchard extensions, into Windows PE/COFF code for x64 or ARM64. The resulting application links to the open-source Swift runtime, independently implemented Orchard framework modules, and approved third-party Windows components. It runs as a sandboxed Windows process inside the Orchard compatibility device.

The compiler model separates two concepts:

1. **Execution target:** Windows ABI, object format, calling convention, loader, debugging, and native dependencies.
2. **Source compatibility profile:** available Orchard framework declarations, compatibility behavior, availability rules, supported language/package assumptions, and an explicit Orchard target environment.

A conceptual compiler invocation might express both a Windows target and an Orchard compatibility profile. The exact command-line syntax is a later compiler ADR and is not fixed by this decision.

SwiftPM will resolve packages for an Orchard virtual destination rather than pretending that a Windows executable is an Apple binary. Package analysis will identify Darwin-only source, unsupported binary artifacts, build plugins, conditional-compilation branches, and framework requirements before execution.

An application may import source-compatible module names only when Clean-Room Governance and counsel approve those names and declarations. If a name is not approved, Orchard will provide an approved module name and migration tooling rather than bypassing the legal gate.

The local linker emits an **Orchard Application Bundle**, an Orchard-owned signed format containing Windows code, resources, capability declarations, compatibility-profile identity, and build provenance. It is not an IPA and must not be accepted by the Apple release path.

For official validation and release, Orchard sends an immutable source snapshot and dependency/build manifest to the Apple Validation Plane. Official Apple tooling independently compiles the source on licensed Apple hardware. That official build, not the local Orchard binary, is tested, signed, and submitted.

### Three-plane enforcement

The decision fixes responsibility across three planes:

- the **Local Windows Development Plane** builds and executes only Windows-targeted Orchard artifacts and provides the daily developer loop;
- the **Clean-Room Compatibility Plane** is the sole source of approved framework declarations, behavioral specifications, evidence metadata, and differential test definitions used by implementation;
- the **Apple Validation Plane** performs official builds, Apple simulator/device execution, signing, comparison, and release operations without exporting Apple runtime material.

No component may collapse these planes for convenience. In particular, the local runtime cannot query a remote Mac to supply missing framework behavior, implementation engineers cannot consume raw restricted observations, and validation workers cannot return Apple SDK contents to clients.

### Build-versus-buy consequence

Orchard builds and owns the compatibility target, runtime ABI, framework behavior, UI kernel, sandbox/capability system, compatibility analysis, and validation attestations. It reuses reviewed upstream Swift/LLVM tooling and Windows platform services. It initially buys or partners for Apple hardware capacity, identity, KMS/HSM, object storage, and independent security/legal audits behind Orchard-owned contracts. A managed service cannot become a requirement for an already provisioned local build.

## Required compatibility behavior

Every API and application capability is placed in one of four execution dispositions:

- **Local compatible:** implemented and qualified for the selected profile.
- **Local simulated:** deterministic development behavior exists, but physical or service behavior requires validation.
- **Apple-validation-only:** the application can reference the capability, but its real build or execution occurs only in the Apple plane.
- **Unsupported:** compilation or launch fails with a stable diagnostic and remediation.

Disposition is distinct from conformance qualification. A locally executable behavior may be `Full` or `Constrained` depending on its evidence and known differences.

Silent no-ops, invented success responses, and unqualified compatibility percentages are forbidden. Security-, identity-, health-, payment-, entitlement-, and hardware-backed APIs cannot be represented as real merely because a local test double exists.

## Compiler and package consequences

The selected design requires a maintained Swift and SwiftPM delta. At minimum, Orchard expects to need:

- compatibility-profile availability data;
- a virtual package destination;
- explicit `targetEnvironment(orchard)`-style detection;
- analysis of platform conditionals and Darwin-only dependencies;
- source maps and debug metadata usable on Windows;
- deterministic toolchain/profile locking;
- a source-only package compatibility policy;
- a vendor format for separately built Orchard Windows slices.

Orchard must not globally make every compiler query report iOS while emitting Windows code. Doing so would cause packages to select Darwin system calls, Mach assumptions, Apple assembly, and unavailable binary dependencies. Where existing source uses `#if os(iOS)`, the package analyzer and compatibility profile decide whether the selected branch is implementable and produce an explicit result.

Binary CocoaPods, Apple-platform XCFramework slices, Mach-O frameworks, and precompiled Apple modules are outside the local target. A package vendor may provide an Orchard Windows slice under a separate license and manifest.

## Runtime and framework consequences

The application uses a versioned Orchard Runtime ABI and independently implemented framework modules. SwiftUI and UIKit adapters feed a shared Orchard UI kernel, which in turn produces an Orchard render graph for Windows graphics backends. Public framework objects do not directly wrap arbitrary WinForms, WPF, or WinUI widgets because host widget semantics cannot define the compatibility contract.

Objective-C source compatibility is not implied by selecting a Swift source-compatible target. General Objective-C interoperability requires its own compiler/runtime program and ADR. Arbitrary Objective-C or Apple binary compatibility is explicitly not accepted here.

Local application code is untrusted. It runs in a separate AppContainer or documented equivalent and accesses simulated device services only through a capability-scoped broker. It never executes inside the CLI, build daemon, device shell, renderer authority, or cloud control plane.

## Clean-room and legal consequences

This decision is conditional on continuing legal approval. Framework declarations and behavior are implemented only from signed, approved clean-room specification releases. Implementation teams cannot use Apple binaries, extracted SDK content, private headers, decompiled material, restricted assets, leaked sources, or unapproved observations.

The decision does not assert that every public declaration or black-box experiment is legally reusable. Clean-Room Governance maintains an input classification and may narrow the compatibility profile. A rejected API must be renamed, redesigned, kept validation-only, or excluded.

Apple tooling remains confined to licensed Apple hardware in the validation plane. No validation worker may export SDKs, simulator runtimes, signing tools, fonts, system assets, or other Apple runtime material to a local client.

## Positive consequences

- The daily execution path is local, low latency, debuggable, and independent of network quality.
- Orchard does not need to emulate an Apple kernel or execute Apple application binaries.
- Official release builds remain grounded in the toolchain Apple requires.
- The open-source Swift ecosystem can be reused where licenses and platform assumptions allow.
- Framework compatibility can advance incrementally through explicit profiles.
- Windows process isolation, graphics, accessibility, and tooling can be used directly.
- The architecture has clear ownership and test boundaries.
- Compatibility limitations can be discovered before a developer reaches release week.

## Negative consequences

- Orchard must continuously rebase compiler and SwiftPM changes.
- Source compatibility does not provide binary package compatibility.
- Packages with Darwin assumptions require patches, vendor participation, or Apple-only validation.
- Independently implementing Foundation, SwiftUI, UIKit, lifecycle, layout, text, animation, and accessibility behavior is a large permanent program.
- Exact visual output may differ where Apple fonts, assets, or system-controlled UI cannot be redistributed.
- Objective-C, Metal, AR, payment, health, enclave, and device-specific features require separate decisions and may remain validation-only.
- A local success never eliminates the need for official release validation.
- Versioned compatibility profiles create a deliberate lag behind newly released Apple platform behavior.

## Alternatives considered

### Full hardware or operating-system emulation

Rejected. It would require an Apple runtime or a reimplementation of a far larger operating-system surface, complicate graphics and device support, perform worse, and create unacceptable licensing and legal exposure. It does not remove official signing and release requirements.

### Apple simulator binary compatibility on Windows

Rejected. Official simulator runtimes and Apple frameworks are not the Orchard distribution boundary. Reproducing their binary ABI would make the project dependent on Apple binary details while offering little benefit for source-first development.

### Remote Mac execution or screen streaming

Rejected as the primary run loop. It preserves Mac dependence, latency, offline limitations, and fleet cost. Remote Apple execution is retained only for validation, comparison, signing, and release.

### Source-to-JSON translation or interpretation

Rejected for production. A partial parser cannot preserve Swift type checking, generics, concurrency, macros, package behavior, ABI, debugging, and application logic. The current bootstrap may remain a disposable prototype and test fixture.

### Transpile Swift into C#, TypeScript, or another UI language

Rejected. Translation would create a second language-semantics implementation, poor debugger fidelity, complex package incompatibility, and a larger maintenance surface than using Swift's compiler.

### Ordinary Windows Swift target with an unrelated Orchard UI API

Not selected as the primary product because it would require substantial application rewrites and would not meet the source-compatibility proposition. It remains the legal fallback if compatible public API names or behaviors cannot be implemented.

### Local compilation against copied Apple SDK declarations with runtime stubs

Rejected. It creates licensing and provenance risks and encourages silent or incorrect behavior. Orchard declarations must come through the approved clean-room process and every behavior must have an explicit disposition.

## Implementation obligations

This decision is not satisfied until the program provides:

1. a compiler ADR defining the execution-target/source-profile split;
2. a signed toolchain and compatibility-profile lock;
3. a versioned Orchard Runtime ABI;
4. an Orchard Application Bundle specification that rejects Apple binary payloads;
5. a source/package compatibility analyzer;
6. a sandboxed application process and capability broker;
7. a clean-room specification release and provenance mechanism;
8. an immutable Apple validation job and signed attestation format;
9. differential tests proving that the same source can be evaluated locally and officially;
10. user-facing diagnostics for local, simulated, validation-only, and unsupported behavior.

The first technical gate must demonstrate an ordinary Swift application compiled into Windows code, run in a separate process, debugged at its original source line, and rebuilt independently with official Apple tooling from the same source snapshot.

## Prohibited interpretations

This ADR does not authorize:

- distribution or local loading of Apple proprietary runtime material;
- execution of IPAs or Mach-O application code on Windows;
- extraction of Apple compiler, linker, simulator, signing, fonts, symbols, or framework assets;
- submission of the Orchard Windows bundle as an Apple release artifact;
- a universal compiler lie that selects iOS branches without dependency analysis;
- silent framework stubs;
- running untrusted project code in a trusted Orchard process;
- using a remote video stream while claiming local execution;
- claiming compatibility outside a published Orchard profile.

## Validation and review

The Architecture Council reviews this decision at each of these gates:

- completion of the compiler/runtime vertical slice;
- first source-package compatibility cohort;
- private alpha;
- public beta;
- first production Apple validation and signing workflow;
- every proposed Objective-C or binary-package expansion;
- any material change to Apple licensing, developer terms, or distribution policy.

The decision must be superseded or narrowed if:

- counsel no longer approves the clean-room or validation model;
- the compiler delta cannot be kept maintainable across supported Swift releases;
- target applications require extensive source rewrites despite the compatibility profile;
- sandboxed Windows execution cannot meet security or performance requirements;
- official validation cannot independently rebuild the locked source graph.

## Relationship to the bootstrap

The bootstrap compiler and JSON view model may continue to support demonstrations while the native Swift lane is being established. They must be labeled as bootstrap components, cannot produce production-qualified bundles, and cannot become an alternate compatibility path. The production milestone replaces source parsing with the supported Swift compiler path and replaces in-process WinForms rendering with the process and UI-kernel boundaries defined in the architecture.
