# Project Orchard Master Production Execution Plan

**Document status:** Proposed program baseline  
**Baseline date:** 18 July 2026  
**Planning horizon:** 60 months to General Availability and first enterprise release  
**Currency:** 2026 USD unless stated otherwise  
**Decision rule:** Dates are forecasts. Legal, security, compatibility, and reliability gates are mandatory and may move a release.

## 1. Purpose and executive decision

Project Orchard aims to let developers compile source-based Swift applications, run and debug them in a Windows-native compatibility runtime, and use Apple hardware only for final official-toolchain validation, signing, physical-device testing, and App Store submission.

This plan turns that goal into a measurable production program. It covers the compiler and build system, clean-room framework implementations, rendering and simulation, development tools, conformance engineering, the Apple-hardware validation service, security, compliance, commercial operations, support, and long-term platform maintenance.

The program must not describe an unbounded reproduction of all of iOS as a deliverable. Production readiness is defined against a **versioned Orchard Compatibility Profile** and a published application corpus. General Availability, or GA, occurs only when the declared profile meets every exit gate in this document. Unsupported and hardware-rooted APIs remain visible, diagnosable, and routed to Apple validation rather than being silently stubbed.

Companion control ledgers:

- [Proposal traceability](PROPOSAL_TRACEABILITY.md) maps every proposal
  commitment to its phase owner and required completion evidence.
- [RAID register](RAID_REGISTER.md) owns the initial risks, assumptions,
  active issues, dependencies, triggers, mitigations, and contingencies.
- [ADR 0002](decisions/0002-native-application-process-and-local-protocol.md)
  is accepted for the native vertical slice. The bundle/runtime ABI,
  render-graph, and LSP/DAP decisions are still proposed in
  [ADR 0003](decisions/0003-application-bundle-and-runtime-abi.md),
  [ADR 0004](decisions/0004-ui-kernel-and-render-graph-boundary.md), and
  [ADR 0005](decisions/0005-language-server-and-debug-adapter-boundaries.md).

### 1.1 Product contract

The GA product is intended to provide:

- Windows 11 x64 and ARM64 development on a published CPU, GPU, memory, and driver matrix.
- Source-based Swift application compilation against an independently implemented Orchard SDK.
- SwiftUI-first support and a specified UIKit/Objective-C source-interop subset.
- Source Swift packages when their licenses and platform dependencies allow local compilation.
- Local build, run, debug, preview, test, screenshot, accessibility inspection, simulation, and profiling.
- Device profiles, rotation, touch and pointer input, keyboard input, theme, locale, Dynamic Type, location, sensor, media, notification, permission, and network-condition simulation.
- Static and dynamic compatibility reports with exact unsupported and remote-only capabilities.
- Official Apple-toolchain validation on permitted Apple hardware.
- Optional signing and App Store submission assistance under explicit customer authorization.
- Team, CI, enterprise administration, private validation-pool, offline, audit, and support capabilities in the enterprise profile.

The initial product does **not** promise:

- Local execution of arbitrary `.ipa` files or Apple-compiled binary frameworks.
- Redistribution of Apple SDKs, Xcode components, operating-system images, proprietary fonts, system symbols, or other Apple-controlled assets.
- Exact physical-device behavior for Secure Enclave, Apple Pay, HealthKit device data, CarPlay, advanced camera hardware, cellular hardware, or full AR/spatial features.
- Pixel identity where licensed fonts, proprietary assets, hardware color pipelines, or GPU implementations differ.
- App Store acceptance. Apple retains final control over signing, review, and distribution.
- Compatibility based on a source file merely compiling. Orchard counts behavior, workflows, visuals, accessibility, and validation escapes.

### 1.2 Interpretation of the 90-95% objective

The objective is not 90-95% of the raw number of symbols Apple publishes. That denominator is unstable and would reward shallow stubs. The program target is:

- At GA, at least 90% of weighted canonical journeys in the GA application corpus complete locally without an Orchard compatibility blocker.
- Within 12 months after GA, at least 95% of weighted canonical journeys in the then-current declared profile complete locally.
- At GA, at least 99.7% of deterministic behavior tests for APIs labelled `Full` pass.
- At GA, fewer than 1% of locally passing canonical journeys fail official Apple validation because of an Orchard compatibility difference.

No percentage may be published without its corpus, weights, profile version, test date, tolerances, exclusions, and confidence level.

## 2. Phase 0 and current feasibility evidence

Phase 0 is the retained repository bootstrap. It is useful evidence that the
project structure and a narrow interaction loop can work; it is not evidence of
iOS compatibility. Production-directed Phase 4/5 feasibility work has begun in
the same repository, but no program phase gate is satisfied merely because its
component tests pass.

The retained bootstrap includes:

- a .NET 10 Core, subset compiler, CLI, and Windows Runtime lane;
- a hand-written lexer/parser for a small SwiftUI-shaped source subset;
- Orchard IR `0.1.0` for views, state, modifiers, events, diagnostics, source
  locations, and bootstrap compatibility metadata;
- a WinForms preview shell with basic controls and several display profiles;
- CLI commands for `doctor`, `init`, `build`, `run`, `compatibility`, `inspect`,
  `devices`, and version/help output; and
- the checked-in `HelloOrchard` bootstrap sample.

Current production-directed feasibility evidence, last recorded on 18 July 2026,
adds:

- the official Swift 6.3.3 Windows compiler, SwiftPM, formatter, language server,
  LLDB, and LLDB-DAP pinned by distribution and installed-binary hashes;
- a native Windows Swift SDK with strict `OrchardProtocol` IR handling and an
  Orchard-owned `OrchardUI` declarative/state feasibility surface;
- deterministic structural/keyed identity, session-owned nested state, bindings,
  revisioned render snapshots, semantic event dispatch, stale-event rejection,
  and main-actor isolation at the Swift SDK level;
- shared accepted/rejected cross-language IR fixtures and a versioned fail-closed
  capability profile consumed by .NET and Swift;
- a strict typed .NET process protocol and authenticated current-user named-pipe
  transport with bounded framing, negotiation, timeouts, cancellation, and
  full-duplex messaging; and
- focused green evidence of 39 bootstrap/core tests, 42 protocol tests, 9 .NET
  transport tests, and 32 Swift SDK tests.

Those focused results do not yet constitute a composite release run. The native
Swift pipe adapter and persistent .NET host are being integrated, and the full
post-integration check and clean-machine Windows CI run have not been recorded.

Current evidence does **not** yet demonstrate:

- a verified persistent native Swift child-to-host session covering initial
  render, real binding and closure events, revision reconciliation, stdout/stderr
  isolation, hostile authentication, timeouts, shutdown, and crash containment;
- Swift ABI/language conformance across a supported matrix, Objective-C, UIKit,
  Apple SwiftUI, or iOS semantic compatibility;
- a real SourceKit-LSP editor exchange or LLDB/DAP breakpoint, stack, and variable
  workflow;
- a clean-room specification and evidence-control organization approved by
  counsel or an Apple-reference differential test lab;
- a low-privilege application sandbox and capability broker;
- a production renderer, Auto Layout engine, Core Animation model, accessibility
  parity, device simulator, or qualified GPU performance; or
- Apple-hardware validation, device-farm orchestration, signing, publishing,
  cloud isolation, billing, support, or operational SLOs.

Phase 0 remains a disposable learning scaffold. The newer contract, state, and
transport work is Phase 4/5 feasibility evidence only. Production architecture
decisions may replace any of it, and no compatibility claim may rely on it until
the applicable phase gate and API Definition of Done are satisfied. The detailed
evidence ledger is [CURRENT_STATUS.md](CURRENT_STATUS.md).

## 3. Program principles

1. **Source recompilation, not binary emulation.** Orchard compiles customer source for an Orchard Windows target and maps supported APIs into independently implemented components.
2. **Clean-room provenance is a release artifact.** An API without approved provenance cannot ship.
3. **No silent stubs.** Unsupported behavior produces a compile-time diagnostic where possible and a clear runtime failure where dynamic use prevents compile-time detection.
4. **Official Apple tooling remains the release authority.** App Store builds and device validation are produced through official tooling on permitted Apple hardware.
5. **A compatibility claim is testable.** Every claim has a profile, corpus, denominator, evidence, owner, and expiry.
6. **Vertical slices precede breadth.** Each early phase proves a complete edit-build-run-debug-validate path before adding many APIs.
7. **Security boundaries precede public execution.** Public previews do not execute untrusted applications without a reviewed local sandbox and cloud tenant isolation.
8. **Accessibility, localization, and diagnostics are part of implementation.** They are not deferred polish.
9. **Upstream before permanent forks.** Swift, LLDB, SourceKit-LSP, SwiftPM, and open-source dependencies should be extended upstream where practical.
10. **Scope can contract; gates cannot.** The supported profile may be narrowed to meet quality, but legal, security, and reliability thresholds are not negotiable.

## 4. Governance and decision rights

### 4.1 Program leadership

The program requires:

- General Manager with profit-and-loss and funding accountability.
- Chief Technology Officer/Chief Architect.
- Vice President of Engineering.
- Vice President of Product and Developer Experience.
- General Counsel and an independent clean-room/IP officer.
- Chief Information Security Officer.
- Head of Compatibility and Quality.
- Head of Apple Validation and Site Reliability Engineering.
- Program Management Office.
- Developer Relations, Support, and Enterprise Operations leaders.

### 4.2 Councils

- **Steering Committee:** approves funding, phase gates, scope, and kill/pivot decisions monthly.
- **Architecture Council:** approves target model, runtime boundaries, public contracts, data formats, and cross-team changes biweekly.
- **Clean-Room Review Board:** approves sources, specifications, evidence handling, repository access, and contamination incidents weekly.
- **Compatibility Claims Board:** approves every published percentage, API status, marketing comparison, and known-deviation statement.
- **Security and Privacy Council:** owns threat models, exception expiry, incident readiness, and compliance monthly.
- **Release Readiness Board:** Product, Engineering, Quality, SRE, Security, Support, Legal, and Finance each provide an independent sign-off.

Legal, Security, and Quality have veto authority for their hard gates. A schedule or revenue commitment cannot override a veto.

### 4.3 Delivery cadence

- Two-week team execution cycles.
- Twelve-week integrated program increments.
- Weekly dependency, compatibility-regression, and release-risk reviews.
- Monthly cost, hiring, capacity, SLO, and error-budget reviews.
- Quarterly phase-gate and funding decisions.
- Annual external clean-room, security, and financial-control audits.

Every work item has a directly responsible individual, acceptance tests, security and legal classification, documentation impact, compatibility-profile impact, telemetry requirement, and operational owner.

## 5. Integrated five-year roadmap

The phases overlap because compiler, runtime, UI, tools, validation, and operations must advance in parallel. Staffing is total active program staffing, not the number added by that phase.

| # | Phase | Window | Duration | Total program FTE | Hard dependencies |
|---:|---|---:|---:|---:|---|
| 0 | Bootstrap evidence baseline | Month 0 | Point in time | 4-8 | None |
| 1 | Charter, funding, and legal preflight | M0-M2 | 3 months | 30-40 | Phase 0 |
| 2 | Product profile and reference corpus | M0-M3 | 4 months | 40-50 | Phase 1 starts |
| 3 | Clean-room operating system | M1-M4 | 4 months | 50-60 | Phase 1 |
| 4 | Architecture and feasibility spikes | M1-M4 | 4 months | 60-70 | Phases 1-2 |
| 5 | Native Swift end-to-end vertical slice | M3-M7 | 5 months | 75-90 | Phases 3-4 |
| 6 | SDK, build graph, linker, and package foundation | M5-M9 | 5 months | 95-115 | Phase 5 |
| 7 | Runtime sandbox and Foundation nucleus | M5-M10 | 6 months | 110-130 | Phases 3, 5 |
| 8 | Graphics, text, layout, and input substrate | M6-M11 | 6 months | 130-150 | Phases 4-5 |
| 9 | SwiftUI core compatibility slice | M8-M13 | 6 months | 150-170 | Phases 6-8 |
| 10 | Simulator core and device profiles | M9-M14 | 6 months | 165-185 | Phases 7-9 |
| 11 | CLI, IDE, debugger, and preview core | M10-M15 | 6 months | 180-205 | Phases 5-10 |
| 12 | Integrated internal alpha | M14-M17 | 4 months | 205-225 | Phases 6-11 |
| 13 | Foundation, networking, data, and lifecycle breadth | M15-M21 | 7 months | 225-250 | Phase 12 |
| 14 | SwiftUI application-pattern breadth | M16-M23 | 8 months | 250-280 | Phases 9, 12-13 |
| 15 | UIKit and Objective-C source subset | M18-M26 | 9 months | 280-315 | Phases 7-8, 12 |
| 16 | Automation and conformance scale-out | M18-M27 | 10 months | 310-335 | Phases 3, 12-15 |
| 17 | Apple-hardware validation service MVP | M19-M27 | 9 months | 325-355 | Phases 1, 3, 12, 16 |
| 18 | External developer preview | M26-M30 | 5 months | 345-370 | Phases 13-17 |
| 19 | Package ecosystem and application-corpus scale | M28-M36 | 9 months | 365-395 | Phase 18 |
| 20 | Production cloud, security, and enterprise foundation | M28-M37 | 10 months | 390-425 | Phases 17-18 |
| 21 | Private beta | M36-M40 | 5 months | 420-450 | Phases 19-20 |
| 22 | Reliability, performance, accessibility, and parity hardening | M38-M46 | 9 months | 445-485 | Phase 21 |
| 23 | Public beta and commercial operations | M43-M48 | 6 months | 475-515 | Phases 20-22 |
| 24 | GA scope freeze, audit, and capacity proof | M47-M53 | 7 months | 510-555 | Phases 22-23 |
| 25 | GA release candidate and launch | M53-M57 | 5 months | 545-585 | Phase 24 |
| 26 | Enterprise/LTS release and 95% journey program | M55-M60 | 6 months | 570-620 | Phases 24-25 |

## 6. Detailed phase plans and exit gates

### Phase 0 - Bootstrap evidence baseline

**Objective:** Preserve the current proof of concept as evidence and prevent its shortcuts from becoming unreviewed production contracts.

**Deliverables**

- Reproducible bootstrap build and test commands.
- Capability and limitation inventory from Section 2.
- Architecture notes for components likely to be replaced.
- A rule that bootstrap compatibility percentages are development diagnostics only.

**Exit gate**

- Build and tests reproduce on a clean supported machine.
- No external product claim treats the parser, WinForms controls, or current score as native Swift/iOS compatibility.

### Phase 1 - Charter, funding, and legal preflight

**Objective:** Decide whether the intended business and technical method can legally and financially proceed.

**Deliverables**

- Product charter, decision rights, non-goals, target jurisdictions, and five-year financing model.
- Outside-counsel opinions covering clean-room observation, Apple agreements, Xcode use, hosted Apple hardware, redistribution, patents, trademarks, privacy, export controls, and App Store workflows.
- Initial patent landscape and open-source obligations inventory.
- Recruitment plan for compiler, runtime, graphics, legal, security, and developer-tool specialists.
- Initial threat model and critical-risk register.

**Exit gate G0: legal and commercial viability**

- Counsel identifies no unresolved red prohibition for the proposed first profile.
- Board funds Phases 2-5 plus at least 25% risk reserve.
- Named executive, legal, architecture, quality, and security owners accept the charter.
- If legal viability fails, execute Pivot A in Section 20 rather than beginning compatibility implementation.

### Phase 2 - Product profile and reference corpus

**Objective:** Define exactly what Orchard will support and how success will be measured.

**Deliverables**

- GA Profile 1 draft: Windows versions, architectures, Swift version, application types, framework subset, package policy, and remote-only categories.
- Legally usable corpus: at least 200 applications for feasibility, stratified across SwiftUI, UIKit, networking, storage, accessibility, localization, media, packages, and testing.
- Canonical journeys, expected outputs, critical APIs, complexity tiers, and usage weights for each corpus application.
- Developer personas, reference projects, reference Windows hardware, and performance benchmarks.
- Compatibility-status taxonomy and score specification.

**Exit gate**

- Product, Quality, Legal, and Architecture approve the denominator and sampling method.
- Every corpus item has source/license provenance and repeatable canonical journeys.
- Marketing agrees not to use a raw API-count percentage.

### Phase 3 - Clean-room operating system

**Objective:** Make clean-room separation an enforceable engineering system.

**Deliverables**

- Separate specification and implementation teams, repositories, identity groups, devices, communication channels, and management controls.
- Approved-source taxonomy, prohibited-source list, evidence intake, immutable provenance ledger, and contamination procedure.
- Specification schema covering signature, inputs, outputs, errors, ordering, lifecycle, concurrency, timing tolerances, accessibility, and known uncertainty.
- Legal training and signed acknowledgements for employees and contractors.
- Automated scans for prohibited strings, headers, assets, binaries, licenses, and suspicious code similarity.
- Independent quarterly audit procedure.

**Exit gate**

- Counsel approves the process in writing.
- A sample API moves from approved observation through specification, implementation, test, and audit without information-boundary breach.
- Repository access and evidence lineage can be reconstructed from audit logs.

### Phase 4 - Architecture and feasibility spikes

**Objective:** Retire the highest technical risks before scaling teams.

**Deliverables**

- Architecture Decision Records for target triple, SDK shape, application bundle, process model, UI tree, layout, renderer, input, IPC, sandbox, debugger, package model, test trace, and versioning.
- Spikes for open-source Swift on Windows, SourceKit-LSP, LLDB/DAP, SwiftPM, Direct3D/DirectComposition, DirectWrite, accessibility, AppContainer, and ARM64.
- Same-source minimal project built on Windows against an Orchard module and on Mac against official tooling.
- Apple validation pilot on permitted Apple hardware without SDK extraction.
- Performance model and memory/resource budgets.

**Exit gate G1: architecture feasibility**

- A minimal native Swift program calls an Orchard-owned module and renders an interactive Windows surface.
- Source breakpoints and stack traces work in a minimal program.
- The process can run at low privilege with brokered capabilities.
- Architecture Council accepts a path to the Month 17 alpha without Apple proprietary artifacts.
- Failure invokes Pivot B or C rather than expanding the bootstrap parser.

### Phase 5 - Native Swift end-to-end vertical slice

**Objective:** Prove the real edit-build-run-debug-validate path.

**Deliverables**

- Pinned, reproducible open-source Swift Windows toolchain.
- Minimal Orchard SDK/module, linker integration, bundle, launcher, runtime bootstrap, and resource loading.
- Native Swift implementation of `Text`, `Button`, one stack, state mutation, and basic navigation.
- Direct rendering, touch/mouse input, console logging, breakpoint, screenshot, and application reset.
- Paired reference test on Apple hardware and normalized comparison.
- CLI build/run/test and VS Code launch configuration.

**Exit gate**

- Ten small same-source reference apps compile using native Swift and run without the bootstrap parser.
- At least 1,000 approved behavior cases pass at 95% or better for declared functionality.
- Warm launch p95 is at most 8 seconds on reference hardware.
- Simple interactions sustain 60 fps and input-to-render p95 below 75 ms.
- No Apple-controlled content is present in shipped artifacts.

### Phase 6 - SDK, build graph, linker, and package foundation

**Objective:** Create a maintainable, incremental, diagnosable build platform.

**Deliverables**

- Orchard target discovery, platform conditions, system modules, SDK manifest, bundle schema, resources, and debug symbols.
- Hermetic build graph, content-addressed cache, incremental compiler integration, and deterministic outputs.
- SwiftPM resolution, lockfiles, package cache, mirrors, proxy behavior, license inventory, and source-only policy enforcement.
- Compile-time compatibility diagnostics and capability manifest.
- x64/ARM64 artifact production and ABI checks.
- Reproducible toolchain bootstrap, SBOM, and provenance attestation.

**Exit gate**

- Clean and incremental builds reproduce on the supported Windows matrix.
- Reference medium project one-file warm build p95 is at most 8 seconds at this stage.
- Unsupported binary frameworks fail with actionable remote-validation guidance.
- Cache poisoning, path traversal, package substitution, and untrusted build-script threats have mitigations and tests.

### Phase 7 - Runtime sandbox and Foundation nucleus

**Objective:** Establish language/runtime correctness and the local security boundary.

**Deliverables**

- Process bootstrap, lifecycle, crash capture, termination, run loop, timers, tasks, cancellation, and thread-affinity rules.
- Foundation value types, errors, URL, data, JSON/Codable, dates, locale, formatting, preferences, and virtual filesystem nucleus.
- AppContainer or equivalent low-privilege process with brokered file, network, clipboard, camera, microphone, location, and notification access.
- Per-application data container, quotas, reset, snapshot, and deterministic test clock.
- Orchard key store backed by Windows security and clearly distinguished from Secure Enclave.

**Exit gate**

- Sandbox escape review and first independent penetration test have no unresolved critical/high finding.
- Lifecycle, task, timer, filesystem, and serialization tests pass at 97% for declared APIs.
- The app cannot access host files, devices, secrets, or network outside its declared policy.

### Phase 8 - Graphics, text, layout, and input substrate

**Objective:** Build the platform-independent visual and semantic engine.

**Deliverables**

- Display tree, semantics tree, retained resources, scene diffing, and frame scheduler.
- Direct3D/DirectComposition rendering with software fallback and GPU-loss recovery.
- Paths, transforms, clipping, color, gradients, images, blending, opacity, and animation primitives.
- DirectWrite text shaping, wrapping, truncation, bidirectional text, IMEs, selection, fallback, and legal font strategy.
- Constraint and intrinsic-size primitives used by SwiftUI layout and Auto Layout.
- Mouse, touch, multi-touch, keyboard, focus, gesture, pointer, drag-and-drop, and accessibility input routing.

**Exit gate**

- Golden image, layout, text, gesture, accessibility-tree, GPU recovery, and memory tests pass on the certified driver matrix.
- No proprietary font or system asset is bundled.
- Simple benchmark sustains 60 fps; renderer memory remains inside published budgets.

### Phase 9 - SwiftUI core compatibility slice

**Objective:** Support complete small SwiftUI applications rather than isolated controls.

**Deliverables**

- View identity, state graph, environment, preference, diffing, transactions, update propagation, and safe invalidation.
- Text, image, button, toggle, text field, secure field, picker, slider, stacks, scroll view, list nucleus, navigation, tabs, sheets, alerts, menus, modifiers, gestures, animations, theme, and Dynamic Type.
- Accessibility role, name, value, action, order, focus, contrast, and reduced-motion behavior.
- Localization, RTL, locale, time-zone, and pseudolocalization.

**Exit gate**

- At least 50 maintained reference apps complete their core journey.
- At least 10,000 conformance cases run continuously.
- Declared `Full` APIs pass behavior tests at 98% or better.
- No silent no-op modifier or control is labelled supported.

### Phase 10 - Simulator core and device profiles

**Objective:** Make simulation repeatable, scriptable, and credible for daily development.

**Deliverables**

- Device shell, safe areas, rotation, scale, orientation, theme, locale, permissions, and display profiles.
- Install, launch, terminate, reset, clone, snapshot, erase, screenshot, recording, and diagnostics.
- GPS routes, heading, motion, battery, time, camera fixtures, photo-library fixtures, notification payloads, deep links, network conditioning, proxy, DNS/TLS failure, and offline mode.
- Headless execution, parallel instances, deterministic event injection, and replay.
- Accessibility inspector and UI automation transport.

**Exit gate**

- Simulator state reset is deterministic across 1,000 repeated runs.
- Parallel simulators cannot read or modify each other's state.
- Device-profile geometry and safe areas match approved reference specifications within tolerance.
- Warm launch p95 is at most 6 seconds.

### Phase 11 - CLI, IDE, debugger, and preview core

**Objective:** Deliver a productive edit-build-debug loop.

**Deliverables**

- Stable CLI commands for create, resolve, build, run, test, compatibility, diagnose, validate, package, and clean.
- VS Code project, task, device, test, console, diagnostics, and debug integration.
- SourceKit-LSP completion, diagnostics, navigation, indexing, rename, and formatting.
- LLDB/DAP breakpoints, watches, variables, exceptions, async task inspection, and source mapping.
- Incremental relaunch and preview with explicit state-preservation rules.
- View hierarchy, layout, accessibility, state, network, memory, CPU, frame, and log inspection foundations.

**Exit gate**

- A new developer completes create-edit-build-debug-test in a moderated usability test without command-line repair.
- Warm completion p95 is at most 300 ms; debugger attach p95 at most 5 seconds.
- Crash symbols and diagnostic bundles reproduce failures in support triage.

### Phase 12 - Integrated internal alpha

**Objective:** Stop feature development long enough to prove that the first platform slice operates as one product.

**Deliverables**

- Signed internal installer, updater, rollback, channel manifest, telemetry controls, and release notes.
- Continuous Windows x64/ARM64 build, test, conformance, security, and performance pipelines.
- Internal documentation and sample suite.
- Dogfood program, issue severity taxonomy, support rotation, crash triage, and daily compatibility dashboard.

**Exit gate G2: internal alpha**

- Thirty-day internal dogfood with zero unresolved Severity 0/1 defects.
- Simulator crash-free sessions at least 99%.
- At least 50 reference apps and 10,000 conformance cases remain green.
- Update, rollback, clean uninstall, offline install, proxy, and restricted-network tests pass.
- Security, Legal, Architecture, and Quality approve expansion to broader APIs.

### Phase 13 - Foundation, networking, data, and lifecycle breadth

**Objective:** Support production-style data and service applications.

**Deliverables**

- URLSession-compatible requests, TLS, cookies, authentication challenges, redirects, cache, streaming, uploads, downloads, cancellation, and network fault simulation.
- File management, documents, preferences, archives, notifications, background-task simulation, pasteboard, and process information.
- Date/calendar/time-zone, formatting, locale, Unicode, regular expressions, and common Foundation collections.
- Unit-test support, dependency injection fixtures, deterministic clocks, and mock service tooling.

**Exit gate**

- Representative CRUD, authentication, offline-cache, upload/download, and document applications complete canonical journeys.
- Networking fuzzing and TLS/error-path tests have no unresolved critical defect.
- Local behavior differences are documented and appear in compatibility reports.

### Phase 14 - SwiftUI application-pattern breadth

**Objective:** Cover the common application patterns in the target corpus.

**Deliverables**

- Navigation stacks, split views, tabs, sheets, popovers, alerts, search, refresh, focus, commands, menus, drag-and-drop, lists, grids, lazy containers, forms, validation, and complex gestures.
- State, bindings, observable models, environment values, scene/application lifecycle, task modifiers, and error presentation.
- Accessibility navigation, keyboard-only operation, screen-reader semantics, high contrast, reduce motion, larger text, and switch-control-compatible automation tree.
- Animation transactions, transitions, matched geometry subset, and deterministic animation test mode.

**Exit gate**

- At least 100 maintained apps cover 70% of weighted canonical journeys.
- At least 25,000 conformance cases run nightly.
- Accessibility critical journeys pass automated tests and manual review by disabled users.
- Implemented behavior pass rate is at least 98.5%.

### Phase 15 - UIKit and Objective-C source subset

**Objective:** Support high-value UIKit source applications and hybrid SwiftUI/UIKit projects.

**Deliverables**

- Objective-C runtime subset for selectors, protocols, message dispatch, object lifecycle, dynamic properties, and source interop required by the profile.
- Views, view controllers, responder chain, containment, presentation, navigation, tab and split controllers.
- Auto Layout constraints, priorities, intrinsic sizes, safe areas, diagnostics, and ambiguity detection.
- Table and collection views, cell reuse, diffable data, gestures, text input, images, menus, and accessibility.
- SwiftUI hosting and UIKit representable bridges.
- Explicit rejection or remote-only handling for unsupported runtime dynamism and binary frameworks.

**Exit gate**

- At least 30 UIKit or hybrid corpus apps complete canonical journeys.
- Constraint, responder, lifecycle, collection-reuse, memory, and accessibility conformance pass at 98% or better for declared APIs.
- The supported Objective-C boundary is published and does not imply arbitrary binary compatibility.

### Phase 16 - Automation and conformance scale-out

**Objective:** Make correctness measurable at industrial scale.

**Deliverables**

- Approved behavior-specification DSL and generators.
- Apple reference runner, Orchard runner, normalized values/events/layout/lifecycle/accessibility traces, visual diff, animation diff, and report service.
- Application-corpus harness, canonical journey orchestration, test sharding, flake detection, and historical trend database.
- API usage weighting, package compatibility certification, and escape analysis.
- Fuzz, property, stress, soak, performance, installation, upgrade, localization, and security matrices.

**Exit gate**

- At least 100,000 cases can run on schedule with less than 0.5% unexplained flake.
- A quarantined test has an owner, defect, expiry, and release-impact classification.
- Compatibility regressions can be bisected to a build and owning team.

### Phase 17 - Apple-hardware validation service MVP

**Objective:** Prove official-toolchain validation without compromising licensing or tenant security.

**Deliverables**

- Job API, scheduler, quotas, ephemeral workspaces, permitted Mac worker images, official build/test invocation, Apple Simulator runner, and physical-device pilot.
- Paired results, screenshots, accessibility trees, event traces, test bundles, compatibility report, and artifact provenance.
- Signing-credential import pilot with wrapped storage, separation of duties, complete audit, and immediate deletion option.
- Explicit user-authorized App Store Connect sandbox workflow; no automatic production submission.
- Retention, encryption, data deletion, worker rebuild, device reset, and incident procedures.

**Exit gate G3: validation feasibility**

- At least 1,000 pilot jobs complete with infrastructure-caused success of 98.5% or better.
- No Apple SDK/tooling is extracted into a Windows artifact.
- Cross-tenant, compromised-worker, credential-theft, and device-data-remanence tests have no unresolved critical/high issue.
- Counsel approves the worker and licensing model.

### Phase 18 - External developer preview

**Objective:** Validate product usefulness and reveal gaps under controlled external use.

**Deliverables**

- Preview SDK, CLI, simulator, VS Code extension, documentation, samples, migration guide, known deviations, and support channel.
- Public compatibility profile, API status database, package status, issue templates, privacy statement, and diagnostic consent.
- Controlled enrollment, entitlement service, crash/feedback triage, and preview update channel.

**Exit gate G4: developer preview**

- At least 250 external developers complete onboarding.
- At least 100 maintained reference apps; 70% of weighted journeys complete locally.
- Simulator crash-free sessions at least 99.2%.
- Validation control plane reaches 99.5% availability for 60 days.
- There are no unresolved Severity 0, critical security, clean-room, or data-loss defects.
- Compatibility reports accurately predict sampled outcomes.

### Phase 19 - Package ecosystem and application-corpus scale

**Objective:** Move from curated demos to representative production projects.

**Deliverables**

- Source-package compatibility scanner, package build farm, maintainer certification, mirrors, proxy, license policy, malicious-package response, and package status catalog.
- Corpus expansion to at least 500 maintained applications and thousands of package builds.
- Migration aids for conditional compilation, unsupported dependencies, binary frameworks, and Apple-service APIs.
- Static call graph plus dynamic trace coverage in per-app compatibility reports.

**Exit gate**

- At least 80% of the private-beta corpus builds.
- Package failures are classified as Orchard defect, package defect, platform-specific design, licensing issue, remote-only dependency, or unsupported binary.
- No uncertified package can bypass the sandbox or access shared tenant data.

### Phase 20 - Production cloud, security, and enterprise foundation

**Objective:** Convert the validation MVP into an operable multi-tenant service.

**Deliverables**

- Multi-region control plane, worker fleet management, device inventory, admission control, queue priorities, reservations, capacity forecast, cost telemetry, backup, restore, and regional failover.
- Organizations, projects, RBAC, SSO pilot, service accounts, quotas, audit log, retention, deletion, and regional controls.
- Central secrets system, signing audit, software supply-chain attestations, SIEM, incident command, status page, and on-call.
- SOC 2 Type I assessment and ISO 27001 control implementation plan.

**Exit gate**

- 90-day availability at least 99.9%.
- Restore, regional failover, key rotation, malicious source, worker compromise, tenant escape, and data deletion exercises pass.
- RPO at most 15 minutes and regional RTO at most 4 hours are demonstrated.
- Cost per validation minute is measured and fits the planned gross-margin model.

### Phase 21 - Private beta

**Objective:** Prove the platform with production-shaped applications and organizations.

**Deliverables**

- Invitations for at least 1,000 developers and selected teams, schools, package maintainers, and enterprises.
- Team CI, headless simulator, test sharding, validation webhooks/API, audit, quotas, billing shadow mode, and support escalation.
- Compatibility database, status page, limitations UI, privacy controls, and customer success playbooks.

**Exit gate G5: private beta**

- At least 500 maintained applications and 100,000 conformance cases.
- At least 80% of weighted canonical journeys complete locally.
- APIs labelled `Full` pass at least 99% of deterministic behavior tests.
- False-confidence rate is below 3%.
- Cloud availability is at least 99.9%; simulator crash-free sessions at least 99.5%.
- External penetration test, clean-room audit, backup restoration, and incident simulation pass.

### Phase 22 - Reliability, performance, accessibility, and parity hardening

**Objective:** Spend a dedicated release cycle reducing uncertainty rather than adding framework breadth.

**Deliverables**

- Performance profiling and budget enforcement across compiler, cache, startup, layout, rendering, scrolling, memory, debug, and validation queue.
- Long-duration soak, GPU/driver, ARM64, multi-monitor, DPI, locale, IME, RTL, accessibility, offline, proxy, update, rollback, and enterprise policy testing.
- Escape-rate root-cause program and top compatibility-difference burn-down.
- Documentation, diagnostics, remediation links, and known-deviation quality review.

**Exit gate**

- At least 88% of weighted journeys complete locally.
- Declared behavior pass rate at least 99.5%; false-confidence below 2%.
- Warm launch p95 at most 5 seconds; one-file build p95 at most 5 seconds; completion p95 at most 200 ms; debugger attach p95 at most 4 seconds.
- Accessibility critical journeys pass automated and independent manual review.
- No Severity 0/1 defect remains.

### Phase 23 - Public beta and commercial operations

**Objective:** Prove scale, support, billing, and self-service operations.

**Deliverables**

- Community, Professional, and Team plan enforcement; metering, quotas, invoices, tax handling, refunds, and abuse controls.
- Self-service onboarding, organization management, support portal, service health, transparent status, and public documentation.
- Production preview/beta channels, signed updater, release transparency, SBOM access, bug bounty, PSIRT, and privacy requests.
- SOC 2 Type II evidence period and enterprise contracting package.

**Exit gate G6: public beta**

- At least 10,000 active beta developers.
- At least 250,000 conformance cases and 88% weighted journey completion.
- Simulator crash-free sessions at least 99.5% and public service availability at least 99.9% for six months.
- False-confidence rate below 2%.
- Billing accuracy, entitlement rollback, support escalation, security response, and data deletion pass audit sampling.

### Phase 24 - GA scope freeze, audit, and capacity proof

**Objective:** Freeze breadth and prove every production control.

**Deliverables**

- Final GA Profile 1, compatibility corpus, package policy, Windows matrix, support matrix, deviations, deprecations, pricing, service terms, and data terms.
- Final legal/IP audit, clean-room audit, penetration test, red-team, supply-chain audit, privacy review, accessibility review, and architecture review.
- Load test at 2x forecast peak, queue degradation behavior, spare-device and Mac-worker capacity, regional failover, restore, key rotation, worker compromise, and release-key recovery exercises.
- GA support, SRE, PSIRT, legal response, customer communications, and executive incident command rosters.

**Exit gate G7: GA candidate authorization**

- At least 90% of weighted GA journeys work locally.
- At least 95% weighted coverage inside the declared profile, excluding stubs and remote-only APIs.
- Behavior pass rate at least 99.7%; false-confidence below 1%.
- Simulator crash-free sessions at least 99.7%.
- Validation availability at least 99.95% for the final 90 days.
- Zero known critical security issue, unresolved Severity 0/1 defect, clean-room exception, or legal blocker.
- SOC 2 Type II is complete; enterprise ISO 27001 status is disclosed accurately.

### Phase 25 - GA release candidate and launch

**Objective:** Release the supported production profile without weakening the frozen gate.

**Deliverables**

- Two release candidates, each promoted from immutable signed artifacts rather than rebuilt.
- Upgrade, rollback, clean install, offline install, enterprise deployment, proxy, restricted-network, x64, ARM64, GPU, locale, accessibility, and validation tests.
- Public status page, compatibility dashboard, support portal, security page, release notes, known issues, service documentation, and migration guidance.
- Controlled rollout: employees, design partners, 5%, 25%, 50%, 100%, with automatic pause criteria.

**Exit gate G8: GA**

- All Section 19 checklist items are signed independently by Product, Engineering, Quality, Legal, Security, SRE, Support, and Finance.
- Two consecutive release candidates pass the full matrix without a release-blocking regression.
- Canary and staged rollouts remain inside error budgets.
- App Store artifacts are demonstrably produced only through official Apple tooling on permitted Apple hardware.

### Phase 26 - Enterprise/LTS release and 95% journey program

**Objective:** Complete the enterprise proposition and advance local coverage toward 95% without destabilizing GA.

**Deliverables**

- SAML/OIDC, SCIM, granular RBAC, customer-managed retention, regional controls, exportable audit, private pools, private networking, central deployment, offline license leasing, and LTS release.
- Map, StoreKit test, Core ML, camera/media, graphics/Metal feasibility, and selected AR simulation workstreams, each separately gated.
- GA stable branch and next-profile branch with explicit resource allocation.
- Compatibility corpus expansion and annual Apple/Swift update lane.

**Exit gate**

- Enterprise service meets contractual 99.95% SLO and private-pool isolation commitments.
- LTS update/rollback and offline operations pass customer acceptance.
- At least 95% weighted journeys in the post-GA declared profile work locally, or the exact shortfall and remote-only categories are published without changing the denominator.

## 7. Critical path and dependency rules

The primary critical path is:

`Legal viability -> clean-room system -> native Swift target -> runtime/rendering vertical slice -> behavior specifications -> integrated alpha -> application breadth -> Apple validation -> private beta -> hardening -> public beta -> audits/capacity -> GA`

Mandatory dependency rules:

- No framework implementation begins before its specification source is approved.
- No public preview begins before local application sandboxing, updater signing, diagnostic privacy controls, and cloud tenant isolation pass review.
- No compatibility percentage is public before the corpus and score method are approved.
- No signing feature is public before secret-storage, audit, deletion, and incident exercises pass.
- No framework is labelled `Full` before its Definition of Done in Section 9 is satisfied.
- No phase adds breadth while the preceding phase has exhausted its security, reliability, or compatibility error budget.
- The official validation lane may validate unsupported local APIs, but it may not be used to conceal local incompatibility in product reporting.

## 8. Workstreams and epic backlog

### WS-01 Program, product, and governance

- `PRG-001` Product charter, scope contract, and non-goals.
- `PRG-002` Target personas and customer research.
- `PRG-003` Versioned compatibility profiles.
- `PRG-004` Reference corpus and canonical-journey ownership.
- `PRG-005` Quarterly program increments and dependency plan.
- `PRG-006` Architecture Decision Record process.
- `PRG-007` Risk, assumption, issue, and dependency register.
- `PRG-008` Compatibility Claims Board workflow.
- `PRG-009` Annual Apple/Swift release response train.
- `PRG-010` Pricing, packaging, capacity, and unit economics.

### WS-02 Legal, clean room, licensing, and IP

- `LEG-001` Jurisdiction and launch-country legal opinions.
- `LEG-002` Apple agreement and hosted-hardware use matrix.
- `LEG-003` Specification/implementation team separation.
- `LEG-004` Approved-source taxonomy and prohibited-material controls.
- `LEG-005` Immutable evidence and provenance registry.
- `LEG-006` Employee/contractor training, access, and invention assignment.
- `LEG-007` Contamination quarantine and remediation procedure.
- `LEG-008` Patent landscape and freedom-to-operate reviews.
- `LEG-009` Trademark, UI asset, screenshot, and marketing review.
- `LEG-010` Open-source license inventory, notices, and source obligations.
- `LEG-011` Third-party package license and redistribution policy.
- `LEG-012` Quarterly independent audit and annual external audit.

### WS-03 Swift toolchain, build, linker, and packages

- `TC-001` Pinned reproducible upstream Swift Windows toolchain.
- `TC-002` Orchard target triple/platform condition and SDK discovery.
- `TC-003` Approved framework/module interface generation.
- `TC-004` Linker, application bundle, resources, and debug symbols.
- `TC-005` Swift standard library and concurrency qualification.
- `TC-006` SwiftPM resolution, lockfiles, mirrors, proxies, and caches.
- `TC-007` Incremental build graph and content-addressed cache.
- `TC-008` Unsupported/constrained/remote-only diagnostics.
- `TC-009` DAP/LLDB debug information and source mapping.
- `TC-010` x64/ARM64 ABI and calling-convention tests.
- `TC-011` Toolchain SBOM, provenance, signing, and reproducibility.
- `TC-012` Upstream contribution and fork-minimization policy.
- `TC-013` Toolchain version upgrade automation.
- `TC-014` Binary package rejection and official-validation route.

### WS-04 Runtime, Foundation, and system services

- `RT-001` Process startup, lifecycle, run loop, crash, and termination.
- `RT-002` Swift metadata, reflection, concurrency, actors, and cancellation.
- `RT-003` Objective-C source-interop runtime subset.
- `RT-004` Foundation values, collections, errors, dates, locale, and formatting.
- `RT-005` Codable, JSON, property lists, URL, data, and MIME behavior.
- `RT-006` Networking, TLS, cookies, cache, redirects, auth, and streaming.
- `RT-007` Virtual filesystem, FileManager, containers, quotas, and reset.
- `RT-008` Notifications, timers, operations, preferences, and pasteboard.
- `RT-009` Orchard Windows-backed key store and explicit limitations.
- `RT-010` Entitlements, capabilities, permissions, and broker.
- `RT-011` Background-task and lifecycle simulation.
- `RT-012` Localization, calendar, time-zone, and Unicode conformance.
- `RT-013` Runtime version negotiation and compatibility shims.

### WS-05 SwiftUI, UIKit, layout, and accessibility

- `UI-001` Display and semantics trees.
- `UI-002` SwiftUI identity, state, dependency tracking, and transactions.
- `UI-003` Primitive views, controls, modifiers, environment, and preferences.
- `UI-004` Stacks, grids, geometry, safe areas, scrolling, and lazy layout.
- `UI-005` Navigation, tabs, split views, sheets, popovers, alerts, and menus.
- `UI-006` Lists, forms, search, selection, refresh, reorder, and swipe.
- `UI-007` Animation, transitions, gesture composition, and hit testing.
- `UI-008` Text input, focus, keyboard, pointer, touch, and drag/drop.
- `UI-009` Dynamic Type, contrast, themes, reduce motion, and localization.
- `UI-010` Accessibility roles, values, actions, ordering, focus, and automation.
- `UI-011` UIKit view/controller hierarchy and responder chain.
- `UI-012` Auto Layout constraints, priorities, intrinsic size, and diagnostics.
- `UI-013` Table/collection views, cells, reuse, and diffable data.
- `UI-014` UIKit/SwiftUI hosting and lifecycle bridges.
- `UI-015` Safe preview/hot-reload state restoration.

### WS-06 Graphics, text, images, audio, and media

- `GFX-001` Direct3D/DirectComposition renderer and resource lifecycle.
- `GFX-002` Paths, transforms, clipping, gradients, color, and blend modes.
- `GFX-003` Layer, transaction, timing, and presentation model.
- `GFX-004` DirectWrite shaping, fallback, wrapping, RTL, and selection.
- `GFX-005` Legally safe font and system-symbol substitute strategy.
- `GFX-006` Image decode, animation, scaling, and color management.
- `GFX-007` Audio playback/record simulation, routes, and interruptions.
- `GFX-008` Video playback and deterministic test media.
- `GFX-009` GPU detection, fallback, recovery, and diagnostics.
- `GFX-010` Frame pacing, memory budgets, and GPU telemetry.
- `GFX-011` Metal-to-Windows feasibility and separately gated implementation.

### WS-07 Simulator and device services

- `SIM-001` Window, shell, safe areas, profiles, and rotation.
- `SIM-002` Install, launch, terminate, reset, clone, snapshot, and erase.
- `SIM-003` Touch, gestures, keyboard, pointer, stylus, and controllers.
- `SIM-004` GPS, route, heading, motion, battery, and time simulation.
- `SIM-005` Camera, photo library, microphone, and media fixtures.
- `SIM-006` Network conditioning, proxy, DNS/TLS faults, and offline state.
- `SIM-007` Notification payloads, deep links, and permissions.
- `SIM-008` Locale, time-zone, accessibility, appearance, and text size.
- `SIM-009` Screenshot, recording, event trace, and replay.
- `SIM-010` Headless parallel isolation and CI orchestration.
- `SIM-011` Accessibility inspector and automation transport.
- `SIM-012` Privacy-scrubbed diagnostic bundle.

### WS-08 Developer tools

- `DEV-001` Stable create/build/run/test/diagnose/validate CLI.
- `DEV-002` VS Code project, device, task, test, and debug experience.
- `DEV-003` SourceKit-LSP completion, indexing, navigation, and refactoring.
- `DEV-004` LLDB/DAP breakpoints, watches, async state, and exceptions.
- `DEV-005` Structured logs, signposts, filtering, and export.
- `DEV-006` Preview and incremental relaunch.
- `DEV-007` View, state, layout, environment, and accessibility inspectors.
- `DEV-008` CPU, memory, allocation, network, frame, and rendering tools.
- `DEV-009` Editor/build compatibility report and remediation links.
- `DEV-010` Crash symbolication and reproducible diagnostic capture.
- `DEV-011` Templates, samples, migration, and documentation search.
- `DEV-012` Visual Studio integration after CLI/VS Code stability.

### WS-09 Specification, conformance, and quality

- `CON-001` Behavior specification schema and approval workflow.
- `CON-002` Official Apple reference-test authoring kit.
- `CON-003` Orchard test runner with canonical inputs.
- `CON-004` Normalized values, events, lifecycle, layout, and error traces.
- `CON-005` Visual/perceptual diff with masks and tolerances.
- `CON-006` Accessibility-tree and animation/gesture comparison.
- `CON-007` Reference application corpus harness and journeys.
- `CON-008` API usage weighting and privacy-safe measurement.
- `CON-009` Compatibility database, dashboard, and regression history.
- `CON-010` Flake detection, quarantine budget, ownership, and expiry.
- `CON-011` Annual SDK delta ingestion.
- `CON-012` Local-pass/Apple-fail escape analysis.
- `CON-013` Package compatibility certification.

### WS-10 Apple-hardware validation service

- `VAL-001` Job API, scheduler, quotas, priorities, and reservations.
- `VAL-002` Permitted Mac worker-image lifecycle and patching.
- `VAL-003` Ephemeral workspace, dependency policy, and destruction.
- `VAL-004` Official build, XCTest, Apple Simulator, and result bundles.
- `VAL-005` Physical-device inventory, provisioning, reset, and health.
- `VAL-006` Paired screenshot, accessibility, trace, and test comparison.
- `VAL-007` Signing identity storage, use, rotation, and deletion.
- `VAL-008` Explicitly authorized App Store Connect workflow.
- `VAL-009` Artifact attestation, retention, encrypted download, and deletion.
- `VAL-010` Capacity forecast, admission control, and cost allocation.
- `VAL-011` Multi-region failover and private enterprise pool.
- `VAL-012` Worker compromise, tenant escape, and secret-theft exercises.
- `VAL-013` Official-toolchain provenance report.

### WS-11 Security, privacy, and compliance

- `SEC-001` Threat models for local apps, packages, IDE, updater, cloud, signing, and devices.
- `SEC-002` Low-privilege app process and brokered capabilities.
- `SEC-003` Package scanning, dependency policy, and malicious-build defenses.
- `SEC-004` Signed boot/update chain for toolchain, SDK, runtime, simulator, and IDE.
- `SEC-005` Offline release root, intermediates, transparency, and key rotation.
- `SEC-006` Cloud secrets, tenant isolation, and dedicated-worker controls.
- `SEC-007` Encryption, retention, deletion, backup, and audit.
- `SEC-008` Detection, SIEM, incident command, and postmortem system.
- `SEC-009` NIST SSDF-aligned secure development lifecycle.
- `SEC-010` SLSA-style provenance, SBOM, vulnerability, and dependency response.
- `SEC-011` Privacy inventory, DPIAs, consent, telemetry, and data rights.
- `SEC-012` SOC 2, ISO 27001, privacy-law, education-data, and export reviews.
- `SEC-013` Pen tests, red team, bug bounty, PSIRT, and advisories.

### WS-12 CI/CD, release, SRE, and operations

- `OPS-001` Hermetic Windows, macOS, and service build environments.
- `OPS-002` Compiler-to-SDK-to-runtime-to-simulator artifact graph.
- `OPS-003` Protected trunk, code owners, approvals, and provenance.
- `OPS-004` Pull request, merge, nightly, weekly, and release pipelines.
- `OPS-005` Canary, internal, preview, beta, stable, and LTS channels.
- `OPS-006` Installer, updater, rollback, proxy, offline, and enterprise deployment.
- `OPS-007` Metrics, logs, traces, dashboards, and alerting.
- `OPS-008` SLOs, error budgets, capacity, and feature-freeze policy.
- `OPS-009` Incident command, status page, postmortems, and corrections.
- `OPS-010` Backup, restore, disaster recovery, and regional failover.
- `OPS-011` Cost telemetry and per-job unit economics.
- `OPS-012` Release notes, compatibility deltas, and deprecations.

### WS-13 Documentation, ecosystem, support, and commercial operations

- `DX-001` Versioned documentation and API compatibility pages.
- `DX-002` First-run tutorial and complete sample suite.
- `DX-003` Migration and unsupported-dependency guidance.
- `DX-004` Package-maintainer and educator programs.
- `DX-005` Diagnostic bundle and reproducible issue intake.
- `DX-006` Support tiers, runbooks, response targets, and escalation.
- `COM-001` Community, Professional, Team, and Enterprise entitlements.
- `COM-002` Metering, quotas, billing, tax, refund, and abuse systems.
- `COM-003` Organizations, RBAC, SSO, SCIM, and service accounts.
- `COM-004` Audit export, retention, residency, and legal hold.
- `COM-005` Private validation pools and customer device matrices.
- `COM-006` Enterprise fleet, update rings, offline licenses, and SLAs.

## 9. API Definition of Done

An API or behavior cannot be labelled `Full` until all of the following are complete:

1. Approved source and provenance record.
2. Versioned public signature and behavioral specification.
3. x64 and ARM64 implementation using no prohibited material.
4. Positive, negative, boundary, error, lifecycle, concurrency, cancellation, and resource tests as applicable.
5. Differential evidence against the approved Apple reference environment.
6. Accessibility, localization, RTL, appearance, and Dynamic Type behavior where applicable.
7. Performance, memory, handle, and GPU budgets.
8. Threat-model and privacy review for sensitive behavior.
9. Compile-time/runtime diagnostics for unsupported subcases.
10. Public documentation, example, limitations, and compatibility entry.
11. Telemetry or test evidence sufficient to identify regression.
12. Two release trains without an unresolved regression.

A placeholder, silent no-op, visual mock, or API that only compiles counts as zero local compatibility.

## 10. Compatibility measurement and reporting

### 10.1 Required dimensions

Orchard reports these separately:

- **Availability:** source compiles and links without a stub.
- **Behavior:** approved input, output, event, ordering, error, and lifecycle tests pass.
- **Journey:** canonical end-to-end application workflows complete.
- **Visual:** layout and perceptual screenshots fall inside published tolerances.
- **Accessibility:** roles, names, values, actions, order, focus, and settings conform.
- **Performance:** the workflow stays within supported hardware budgets.
- **Validation escape:** local pass followed by Apple failure caused by Orchard divergence.
- **Confidence:** test count, recency, profiles, dynamic path coverage, and flake rate.

### 10.2 Status taxonomy

- `Full`: all Definition of Done requirements pass for the named profile.
- `Constrained`: useful local implementation with explicit limits and tested failure behavior.
- `Remote Validation Required`: local substitute would be misleading or unsafe; official validation is required.
- `Unavailable`: no supported local or integrated remote workflow yet.

### 10.3 Optional summary score

If a summary is required, use:

`15% availability + 35% behavior + 30% journeys + 10% visual + 10% accessibility`

Hard caps override the average:

- A missing API on a critical journey prevents the app from being called locally compatible.
- Remote-only and unavailable behavior never counts as local coverage.
- Data loss, security, privacy, accessibility, or crash blockers fail a journey regardless of score.
- Untested dynamic paths are shown as unknown, not supported.

## 11. Test and quality strategy

### 11.1 Test layers

- Compiler parser, type checker, optimizer, linker, ABI, module, debug information, and diagnostic tests.
- API signature and availability compile tests.
- Unit, property, model, and golden tests for runtime/framework behavior.
- Differential Apple-versus-Orchard behavior tests.
- Layout, screenshot, text, animation, accessibility-tree, and event-trace tests.
- Cross-component integration and reference-application journeys.
- Package builds and compatibility certification.
- UI automation, deterministic replay, and headless simulator tests.
- Locale, calendar, time-zone, RTL, IME, Dynamic Type, contrast, keyboard, screen reader, and reduced-motion tests.
- Fuzzing of source/project parsers, packages, archives, images, media, URLs, network protocols, IPC, result bundles, and simulator control.
- Stress and soak tests for repeated launch/reset, parallel simulators, memory/handle leaks, GPU loss, network loss, and long sessions.
- Performance regression tests on reserved reference hardware.
- Installer install/upgrade/downgrade/repair/rollback/uninstall/offline/proxy tests.
- Cloud chaos tests for worker, rack, queue, region, cache, identity, database, key, and device failures.
- Security static/dynamic analysis, dependency scanning, pen tests, red teams, and tenant-escape exercises.
- Moderated usability and accessibility research.

### 11.2 Matrix

- Supported Windows 11 releases on x64 and ARM64.
- Intel, AMD, and Qualcomm CPUs.
- Intel, AMD, and NVIDIA integrated/discrete graphics.
- Certified driver versions plus prior-version upgrade cases.
- Touch and non-touch, common DPI/scaling, multiple monitors, HDR on/off.
- Locales, calendars, time zones, RTL, IMEs, and accessibility settings.
- Clean, upgraded, offline, proxied, domain-managed, WDAC-restricted, and low-resource hosts.
- Current and previous supported Orchard/Swift profiles.
- Approved Apple reference OS, Simulator, and device profiles.

### 11.3 Pipeline cadence

- Developer pre-submit: affected tests, target under 5 minutes.
- Pull request: affected unit/integration/security/license/provenance checks, p95 under 15 minutes.
- Merge: representative cross-component and conformance suite, p95 under 45 minutes.
- Nightly: full Windows matrix and Apple differential suite.
- Weekly: application corpus, package farm, physical devices, fuzz, soak, upgrade, accessibility, and performance.
- Release candidate: entire matrix, legal artifact scan, security scan, failover/restore evidence, and signed provenance.

Quarantined tests require an owner, defect, expiry, and release classification. A quarantined release-critical test remains a release failure.

### 11.4 Severity gates

- **Severity 0:** active compromise, cross-tenant disclosure, signing-key compromise, widespread data loss, or unusable release. Immediate halt.
- **Severity 1:** reliable crash/data corruption, sandbox escape, major compatibility false positive, or core workflow unavailable. No phase exit.
- **Severity 2:** important defect with bounded impact or documented workaround. Capped and explicitly accepted for beta/GA.
- **Severity 3/4:** non-critical defect or enhancement. Managed by normal backlog.

GA permits zero open Severity 0/1 defects. Every accepted Severity 2 has an owner, workaround, customer impact, and target release.

## 12. Service-level and performance objectives

### 12.1 Local developer experience at GA

| Measure | GA objective |
|---|---:|
| Warm simulator launch | p50 <= 2 s; p95 <= 5 s |
| Cold simulator launch | p95 <= 15 s |
| One-file incremental build, medium reference app | p50 <= 2 s; p95 <= 5 s |
| Clean build, medium reference app | p95 <= 90 s |
| Warm code completion | p95 <= 200 ms |
| Debugger attach | p95 <= 4 s |
| Input-to-render latency | p95 <= 50 ms |
| Simulator crash-free sessions | >= 99.7% |
| Deterministic semantic replay | >= 99.5% |
| `Full` API deterministic behavior tests | >= 99.7% |
| Local-pass/Apple-fail compatibility escape | < 1% |

All latency targets name a published reference machine, project, cache state, power mode, and measurement method.

### 12.2 Apple-hardware validation service at GA

| Measure | GA objective |
|---|---:|
| Control-plane availability | 99.95% monthly |
| Job admission API availability | 99.95% monthly |
| Infrastructure-caused Apple Simulator job success | >= 99.7% |
| Infrastructure-caused physical-device job success | >= 99.0% |
| Professional queue start | p95 <= 10 min under contracted load |
| Enterprise reserved-pool queue start | p95 <= 3 min |
| Retained-artifact durability | >= 99.999999999% during retention |
| Control-data RPO | <= 15 min |
| Regional RTO | <= 4 h |
| Unattributed signing operation | Zero |
| Cross-tenant disclosure | Zero |

### 12.3 Support and security

- Enterprise Severity 1 acknowledgement: 30 minutes, 24x7.
- Professional Severity 1 acknowledgement: four business hours.
- Confirmed critical vulnerability containment target: 24 hours.
- Critical patch target: 72 hours where technically feasible.
- Public status update during an active major incident: at least every 30 minutes.

SLOs have error budgets. Exhausting a budget freezes feature promotion until reliability recovers and corrective actions are accepted.

## 13. CI/CD, artifacts, and release management

Use protected trunk-based development with short-lived branches, code owners, required review, change provenance, secret scanning, license/provenance scanning, and component ownership.

Artifact promotion order:

1. Bootstrap compiler.
2. Orchard Swift toolchain.
3. SDK contracts and compatibility database snapshot.
4. Runtime/framework binaries.
5. Simulator and device-service binaries.
6. CLI and IDE extension.
7. Apple validation worker.
8. Installer/updater.
9. Documentation and public compatibility metadata.

Artifacts are immutable, signed, SBOM-attested, and promoted between environments rather than rebuilt. Production manifests record the exact compiler, SDK, runtime, simulator, worker, dependencies, provenance, and test evidence.

Release channels:

- Canary: every qualifying mainline build.
- Internal: daily.
- Developer Preview: every two to four weeks.
- Beta: monthly.
- Stable: quarterly after GA.
- Enterprise LTS: annually, supported 24-36 months.
- Emergency security release: out of band.

Support the current stable and one previous normal profile. Give at least 12 months' notice before breaking SDK removal and longer notice for LTS. Runtime, CLI, IDE, and service protocols negotiate versions and fail with actionable messages during partial upgrades.

## 14. Clean-room, legal, and licensing controls

Clean-room engineering reduces risk but is not, by itself, a legal conclusion. Counsel determines permissible sources, observation methods, agreements, jurisdictions, retention, and team separation.

Required controls include:

- Independent specification and implementation roles with least-privilege access.
- Approved public source list and prohibited-material policy.
- Immutable provenance for each behavior specification.
- No copying of Apple headers, binaries, private symbols, disassembly, fonts, system assets, SDK files, or implementation details unless counsel explicitly approves a distinct lawful source and use.
- No macOS or Apple SDK image distributed with Orchard for Windows.
- Automatic and manual scans of source, binaries, assets, documentation, and release packages.
- Contributor and contractor declarations, training, access termination, and audit.
- Contamination quarantine: stop work, preserve evidence, revoke access, legal review, remove/reimplement affected material, and repeat conformance independently.
- Patent and trademark review before each major feature and public campaign.
- Open-source notices, source obligations, attribution, license compatibility, and dependency approval.
- Review of Apple Developer Program, Xcode, App Store Connect, hardware-hosting, device-lab, and cloud-provider terms before service changes.

The service should be described as **Orchard's Apple-hardware validation service**, not an Apple-endorsed or Apple-operated service unless an actual agreement permits that claim.

## 15. Security, privacy, and compliance program

### 15.1 Local threat model

Customer source, packages, build scripts, generated executables, resources, network payloads, and simulator inputs are untrusted. Controls include:

- Low-privilege app processes and per-app containers.
- Brokered, revocable capabilities.
- Filesystem, registry, process, IPC, device, network, CPU, memory, GPU, handle, and time limits.
- Explicit developer action for host filesystem/network exceptions.
- Signed toolchain, SDK, runtime, simulator, IDE, and update manifests.
- Dependency allow/deny policy, malware scanning, provenance, and sandboxed build scripts.
- Crash/diagnostic scrubbing and opt-in telemetry.

### 15.2 Cloud and signing threat model

- Ephemeral tenant workspaces and least-privilege identities.
- Encrypted transport and storage; tenant-scoped keys where appropriate.
- Wrapped customer signing keys, decrypted only for the assigned operation on the assigned Mac.
- Separation between build, signing, submission, support, and audit roles.
- Complete, immutable audit for credential and artifact access.
- Device erase/reset and verification between tenants.
- No shared mutable dependency cache across tenants unless its contents are public, verified, and non-secret.
- Customer-selectable retention and verified deletion.
- Dedicated workers and private pools for regulated customers.

### 15.3 Compliance milestones

- NIST SSDF-aligned lifecycle from Phase 4.
- SBOM and provenance for every preview build from Phase 12.
- SOC 2 Type I by private beta foundation.
- SOC 2 Type II by GA.
- ISO 27001 for enterprise launch or a public, contractually accurate completion date.
- Privacy impact assessments for GDPR, UK GDPR, CPRA/CCPA, Australian Privacy Act, and target jurisdictions.
- Specific education/minor-data assessment before school deployments.
- Export-control and encryption classification before international distribution.
- Annual independent penetration test, continuous vulnerability management, public security contact, bug bounty, PSIRT, and advisories.

## 16. Apple-hardware validation service design

### 16.1 Job lifecycle

1. Customer uploads source or authorizes a source checkout.
2. Orchard scans size, content, malware, packages, licenses, and policy.
3. Scheduler creates an ephemeral tenant workspace.
4. Dependencies resolve under a locked and auditable policy.
5. Official Apple tooling compiles and tests on permitted Apple hardware.
6. Tests run on selected Apple Simulator and physical-device profiles.
7. Orchard normalizes results, traces, screenshots, accessibility trees, and test bundles.
8. Results compare against the local Orchard run.
9. Signing occurs only when explicitly requested and authorized.
10. App Store submission requires a separate explicit confirmation.
11. Results, provenance, and artifacts are returned under the retention policy.
12. Decrypted credentials and ephemeral workspaces are destroyed and audited.

### 16.2 Capacity progression

- Developer preview: 20-40 Mac workers and 30-60 physical devices.
- Private beta: 100-250 Mac workers and 100-250 devices.
- Public beta: 300-800 Mac workers and 250-600 devices.
- GA: demand-modelled capacity, likely 1,000 or more workers and 500 or more devices across at least two independent failure domains.

Normal utilization should remain below approximately 65-70% so Apple SDK launches, customer releases, hardware failures, and conformance workloads do not collapse queue time. Capacity is reserved separately for conformance, customer validation, signing/publishing, and enterprise pools.

### 16.3 Required safeguards

- Apple tooling and SDKs remain in permitted Apple environments.
- Worker images are patched, immutable per release, scanned, and frequently rebuilt.
- Customer source and secrets never appear in logs or shared caches.
- Every official build carries provenance naming the exact official toolchain and worker image.
- Physical devices have automated health, reservation, reset, wipe, and quarantine states.
- Submission actions are idempotent, attributable, cancellable where possible, and protected from accidental production release.

## 17. Staffing and organization

### 17.1 Mature GA organization

| Area | Approximate FTE at GA |
|---|---:|
| Swift toolchain, build, linker, packages | 40-50 |
| Runtime, Foundation, Objective-C | 65-80 |
| SwiftUI, UIKit, layout, accessibility | 95-115 |
| Graphics, text, audio, media | 40-55 |
| Simulator and device services | 30-40 |
| IDE, debugger, preview, profiling | 45-55 |
| Clean-room specifications, conformance, corpus QA | 55-70 |
| Apple validation, cloud platform, SRE | 50-65 |
| Security, privacy, compliance | 18-25 |
| Installer, release, enterprise platform | 18-25 |
| Product, design, docs, DevRel, support | 45-60 |
| Legal, PMO, finance, operations | 20-30 |
| **Total** | **approximately 560-620** |

### 17.2 Hiring sequence

- Months 0-6: senior architecture, compiler, runtime, graphics, legal/IP, security, conformance, product, and program leaders.
- Months 6-18: platform squads, Windows renderer, simulator, IDE, quality infrastructure, release engineering, and early SRE.
- Months 18-30: UIKit, packages, validation service, physical-device operations, documentation, DevRel, support, privacy, and enterprise platform.
- Months 30-48: scale SRE, compatibility squads, security operations, commercial platform, customer success, and 24x7 support.
- Months 48-60: GA/LTS operations, regional capacity, advanced frameworks, and annual platform-response teams.

Clean-room specification staff remain organizationally and technically separated from implementation staff according to counsel-approved rules.

## 18. Cost envelope

Assumptions:

- Fully loaded technical FTE: approximately $230,000-$320,000 annually, varying by location and role.
- Estimates include engineering, product, quality, legal, security, basic go-to-market readiness, hardware labs, cloud, compliance, and contingency categories described below.
- Estimates exclude acquisitions, major patent litigation, and unusually large worldwide sales/marketing campaigns.

| Program year | Average FTE | Estimated spend |
|---|---:|---:|
| Year 1 | 80-120 | $30M-$45M |
| Year 2 | 180-280 | $65M-$90M |
| Year 3 | 300-420 | $105M-$145M |
| Year 4 | 420-520 | $145M-$195M |
| Year 5 | 520-620 | $175M-$235M |
| **Five-year base** | | **$520M-$710M** |

Authorize an additional 25% program contingency for compiler/runtime unknowns, legal changes, Apple release churn, hiring, hardware supply, and validation demand. Total funding capacity should therefore be approximately **$650M-$890M**.

Major non-payroll costs:

- Mac worker fleets, iPhones/iPads, Windows hardware/GPU labs, racks, networking, spares, and depreciation.
- Cloud control plane, artifact storage, telemetry, security systems, bandwidth, backup, and regional failover.
- Outside IP/licensing/privacy counsel and recurring clean-room audits.
- SOC 2/ISO audits, penetration tests, red teams, bug bounty, cyber insurance, and incident response retainers.
- Documentation, developer programs, support platforms, education materials, and package-maintainer grants.

Expected GA annual run rate is approximately $150M-$230M before a major global sales expansion. Funding is released by hard phase gate. A missed gate causes delay, rescope, pivot, or termination, not a lower quality threshold.

## 19. GA production checklist

GA is authorized only when every item below has an owner, evidence link, date, and independent sign-off.

### Product and compatibility

- [ ] GA Compatibility Profile is frozen, versioned, and public.
- [ ] At least 90% weighted canonical journey completion.
- [ ] At least 95% weighted coverage inside the declared profile, with no stubs counted.
- [ ] At least 99.7% deterministic behavior conformance for `Full` APIs.
- [ ] False-confidence rate below 1%.
- [ ] Remote-only and unavailable APIs are visible in editor, build, runtime, CI, and reports.
- [ ] Supported Windows/CPU/GPU/driver/Swift/profile/package matrices are public.

### Quality and performance

- [ ] Zero Severity 0/1 product defects.
- [ ] Every accepted Severity 2 has owner, workaround, customer impact, and target release.
- [ ] Local latency, build, debugger, frame, memory, and crash-free objectives pass.
- [ ] Full Windows, locale, accessibility, install/update/rollback, offline, and proxy matrices pass.
- [ ] Two consecutive release candidates pass without release-blocking regression.

### Legal and clean room

- [ ] Final external legal and clean-room audits pass.
- [ ] Every shipped API and asset has approved provenance.
- [ ] Automated and manual prohibited-material scans pass.
- [ ] All open-source obligations and notices are satisfied.
- [ ] Product naming and marketing claims are approved.
- [ ] Official-validation and signing workflows remain within current applicable agreements.

### Security and privacy

- [ ] Zero known critical vulnerability and no unmitigated high-severity exploit path.
- [ ] Local sandbox and cloud tenant-isolation tests pass.
- [ ] Release-key, signing-key, worker-compromise, malicious-package, and cross-tenant exercises pass.
- [ ] SBOM, provenance, signed manifests, transparency, and rollback are operational.
- [ ] Privacy inventories, DPIAs, consent, telemetry, retention, deletion, and data-rights workflows pass.
- [ ] SOC 2 Type II is complete; ISO status is represented accurately.

### Validation service and operations

- [ ] 99.95% validation control-plane availability for the final 90 days.
- [ ] Simulator and physical-device infrastructure success targets pass.
- [ ] Queue, load, admission, and degradation tests pass at 2x forecast peak.
- [ ] Restore, regional failover, RPO/RTO, device wipe, and worker rebuild exercises pass.
- [ ] Official-toolchain provenance exists for every release artifact.
- [ ] App Store submission always requires explicit authorized customer action.
- [ ] 24x7 SRE, security, incident command, status communication, and enterprise support are staffed.

### Commercial and support

- [ ] Entitlements, metering, billing, quota, refund, abuse, and tax paths pass audit.
- [ ] Service terms, privacy terms, data processing terms, subprocessors, SLAs, and support policy are public.
- [ ] Unit economics support advertised validation quotas and reserved capacity.
- [ ] Documentation, samples, migration, known deviations, status, support, and security pages are live.
- [ ] Stable/LTS branch ownership and next annual platform-update funding are committed.

## 20. Kill, pause, and pivot gates

### Kill Gate A - legal infeasibility

**Trigger:** Counsel concludes the intended clean-room implementation, official validation, or required distribution cannot operate lawfully or contractually in the target market.

**Action:** Stop affected implementation, preserve evidence, quarantine artifacts, and either terminate the program or pivot to a remote official-toolchain orchestration product that does not ship a compatibility runtime.

### Pivot B - native Swift target infeasibility

**Trigger:** By Month 7, representative native Swift source cannot compile, link, run, and debug on the Orchard target without prohibited Apple material, or the permanent compiler fork is operationally unsustainable.

**Action:** Consider an Orchard-native Swift UI framework with explicit source-porting requirements. Do not scale the bootstrap parser into a fake Swift compiler.

### Pivot C - renderer/layout infeasibility

**Trigger:** By Month 13, representative UI cannot meet correctness, accessibility, input, memory, and frame budgets after two architecture remediation cycles.

**Action:** Replace the rendering/layout substrate, narrow the certified hardware matrix, or narrow the supported UI profile. Pause breadth until the vertical slice passes.

### Pause D - compatibility growth failure

**Trigger:** Weighted journey coverage improves by less than five percentage points across two consecutive program increments while the escape rate remains above 5%.

**Action:** Freeze new APIs, redirect staff to top corpus blockers and specification gaps, review score weights, and reassess the GA profile. Never change the denominator merely to improve the reported percentage.

### Pause E - security or clean-room breach

**Trigger:** Sandbox escape, tenant disclosure, signing-key compromise, prohibited-source contamination, unapproved Apple material, or unverifiable provenance.

**Action:** Stop distribution and affected development, invoke incident command, preserve evidence, notify counsel/security, revoke credentials, quarantine artifacts, remediate independently, and repeat audit before resumption.

### Pivot F - Apple validation policy or capacity change

**Trigger:** Applicable terms, hardware availability, official-toolchain operation, signing, or App Store APIs make the planned multi-tenant service impermissible or uneconomic.

**Action:** Move to customer-owned Mac workers, private dedicated pools, an on-premises coordinator, or validation-provider partnerships as counsel permits. Keep local compatibility claims separate from validation availability.

### Pause G - unacceptable false confidence

**Trigger:** At private beta or later, more than 3% of locally passing canonical journeys fail Apple validation because of Orchard divergence for two consecutive monthly windows.

**Action:** Pause external expansion and GA schedule, make official validation mandatory for affected profiles, downgrade inaccurate API statuses, and run escape root-cause work until the gate is restored.

### Pivot H - validation unit economics failure

**Trigger:** At sustained beta scale, validation costs cannot support the target plan at an acceptable gross margin after two capacity/price remediation cycles.

**Action:** Adjust quotas and prices, increase reservations/batching, offer customer-owned/private capacity, or separate validation as pass-through usage. Do not underprovision SLO capacity to hide cost.

### Kill Gate I - funding or staffing failure

**Trigger:** Funding cannot cover the next two hard gates plus contingency, or critical compiler/runtime/legal/security leadership remains unfilled for two consecutive quarters.

**Action:** Stop hiring breadth, narrow to a coherent developer-preview profile, seek strategic partnership/acquisition, or terminate. Do not launch an unsupported production product.

## 21. First 90 days from plan approval

1. Appoint the General Manager, Chief Architect, General Counsel/clean-room officer, CISO, Head of Compatibility, and PMO lead.
2. Commission legal opinions and freeze compatibility implementation that lacks approved provenance.
3. Establish specification and implementation access boundaries.
4. Convert the Phase 0 bootstrap into a reproducible baseline with explicit experimental labels.
5. Select the reference Windows hardware and collect clean build/startup/render baselines.
6. Build the first 200-application licensed corpus and canonical journeys.
7. Finalize GA Profile 1 draft and compatibility score methodology.
8. Run native Swift, module, linker, LLDB, SourceKit-LSP, SwiftPM, sandbox, and renderer feasibility spikes.
9. Define the official Apple-reference lab pilot and obtain counsel approval before operation.
10. Approve the first two program increments, headcount plan, budget, risk register, and Gate G1 evidence package.

## 22. Final execution rule

Project Orchard is production ready only when the supported profile, not the aspiration, passes the production checklist. A smaller honestly supported profile is acceptable. An unexplained percentage, silent stub, unreviewed clean-room shortcut, insecure execution boundary, or non-official release artifact is not.
