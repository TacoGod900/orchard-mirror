# Project Orchard proposal traceability matrix

Status date: 18 July 2026

This document is the requirements ledger between the original Project Orchard
proposal and the master execution program. It prevents roadmap completion from
being inferred from activity or API counts. A requirement is complete only when
the evidence named here exists, passes its governing phase gate, and remains
green in the release matrix.

Status values:

- **Bootstrap evidence**: a limited implementation exists, but it is not the
  production solution.
- **In progress**: production-directed implementation has started and its gate
  is not yet satisfied.
- **Planned**: owned and scheduled in the master plan.
- **Remote-only by design**: Orchard simulates or diagnoses the capability
  locally, while authoritative execution remains on permitted Apple hardware.

The authoritative phase definitions and gate thresholds are in
[EXECUTION_PLAN.md](EXECUTION_PLAN.md). Architecture boundaries are in
[ARCHITECTURE.md](ARCHITECTURE.md).

## Repository evidence snapshot

This snapshot records implementation evidence without changing any production
acceptance threshold below. A row remains **In progress** until all of its named
evidence passes the governing phase gate.

| Evidence ID | Verified or current evidence | Supports | Remaining blocker |
| --- | --- | --- | --- |
| EV-001 | Official Swift 6.3.3 Windows installer and compiler/formatter/LSP/LLDB binaries are hash-pinned; SDK/sample release builds and tool launch probes passed | RT-001, RT-002, DEV-001 | Clean-machine CI, supported matrix, debugger and real LSP/DAP workflows |
| EV-002 | Strict .NET/Swift IR readers, accepted/rejected shared fixtures, duplicate/unknown/wrong-case/limit rejection, deterministic encoding, and one fail-closed capability profile | RT-015, QA-005, QA-007, DEV-012 | Protocol evolution matrix, fuzz/soak, approved compatibility evidence and false-confidence calibration |
| EV-003 | Swift OrchardUI SDK unit evidence covers structural/keyed identity, nested/conditional state slots, bindings, semantic events, revisions, stale-event rejection, and main-actor isolation | RT-006, RT-016 | Persistent native process round-trip, retained UI kernel, lifecycle/focus/accessibility and conformance corpus |
| EV-004 | Typed process protocol and .NET current-user named-pipe transport verify random endpoint/token, authentication, negotiation, bounds, full-duplex I/O, fragmentation, timeout, cancellation, and disposal | RT-015, RT-017, QA-007 | Swift-to-.NET process gate, stdout/stderr stress, same-user threat decision, AppContainer/equivalent boundary |
| EV-005 | Focused suites last passed 39/39 bootstrap/core, 42/42 protocol, 9/9 .NET transport, and 32/32 Swift SDK tests | Cross-cutting Phase 4/5 evidence | Composite post-integration check and clean-machine Windows CI are not yet recorded |
| EV-006 | Native Swift pipe client and persistent .NET host are under integration | RT-001, RT-015-RT-018, DEV-006, DEV-008 | Initial render, change/press revisions, stale rejection, ping/pong, log separation, shutdown, bad-token, timeout and crash tests |

The native sample imports Orchard-owned `OrchardUI`, not Apple `SwiftUI`; none
of this evidence establishes iOS, SwiftUI, or UIKit compatibility.

## Product outcome and platform boundaries

| ID | Proposal requirement | Owning phases/workstreams | Required acceptance evidence | Current status |
| --- | --- | --- | --- | --- |
| PR-001 | Build, run, debug, and test supported iOS application source from Windows | Phases 5-12; WS-03, WS-04, WS-08 | Native Swift corpus applications build and execute in the isolated Windows runtime; debugger and test evidence pass G2 | In progress |
| PR-002 | Complete 90-95% of weighted daily development locally | Phases 18-26; WS-01, WS-09 | Approved corpus denominator, canonical journeys, at least 90% local GA completion and 95% post-GA profile completion | Planned |
| PR-003 | Do not require a Mac for ordinary local development | Phases 5-25 | Clean-machine Windows workflows pass without Apple binaries, services, or network access except explicitly remote operations | In progress |
| PR-004 | Retain Apple-controlled signing, device validation, and distribution boundaries | Phases 1, 3, 17, 20, 24-25; WS-02, WS-10 | Counsel-approved design; signed release builds originate only from official tooling on permitted Apple hardware; immutable provenance proves it | Planned |
| PR-005 | Never redistribute Apple SDKs, OS images, proprietary framework binaries, fonts, or assets | All phases; WS-02, WS-11, WS-12 | Source/artifact scanners, SBOMs, provenance ledger, clean-room audits, and release legal sign-off show no prohibited material | In progress |
| PR-006 | Report limitations without implying physical-device or App Store equivalence | Phases 0, 2, 9, 16, 18, 24 | Versioned compatibility profiles, deviation records, false-confidence metric, and product-copy review pass release gates | In progress |

## Native compatibility runtime

| ID | Proposal requirement | Owning phases/workstreams | Required acceptance evidence | Current status |
| --- | --- | --- | --- | --- |
| RT-001 | Execute real Swift application logic natively on Windows | Phases 4-7; WS-03, WS-04 | Pinned Swift compiler produces native x64/ARM64 child executables; process tests prove state, closures, concurrency, lifecycle, and crash isolation | In progress |
| RT-002 | Swift runtime and core language behavior | Phases 5-7; WS-03, WS-04 | Swift conformance suites and supported-version ABI checks pass the Windows matrix | In progress |
| RT-003 | Objective-C runtime subset and Swift interoperation | Phase 15; WS-04, WS-05 | Published subset passes selector, protocol, ownership, dispatch, dynamic-property, and interop tests | Planned |
| RT-004 | Foundation compatibility nucleus and breadth | Phases 7, 13; WS-04 | Approved value, error, URL, Codable, date, locale, collection, notification, filesystem, and lifecycle behavior cases pass declared thresholds | Planned |
| RT-005 | UIKit source-compatibility subset | Phase 15; WS-05 | Maintained UIKit/hybrid corpus completes canonical journeys; lifecycle, responder, Auto Layout, reuse, and accessibility suites pass | Planned |
| RT-006 | Orchard declarative surface targeting a declared SwiftUI source-compatibility profile | Phases 5, 9, 14; WS-05 | OrchardUI/native framework implements identity, state, environment, diffing, navigation, controls, gestures, animation, localization, and accessibility gates | In progress |
| RT-007 | Core Graphics and drawing behavior | Phase 8; WS-06 | Path, transform, clip, color, gradient, image, blend, and golden-render suites pass certified GPU/software matrices | Planned |
| RT-008 | Core Animation-style retained display and transaction behavior | Phases 8-9; WS-05, WS-06 | Display-tree diffing, frame scheduling, animation transactions, interruption, timing, and deterministic animation tests pass | Planned |
| RT-009 | Auto Layout and intrinsic sizing | Phases 8, 15; WS-05 | Constraint priority, ambiguity, safe-area, intrinsic-size, localization, and stress suites pass against approved specifications | Planned |
| RT-010 | Networking, REST, JSON, TLS, caching, streaming, and fault handling | Phase 13; WS-04 | CRUD/auth/offline/upload/download applications and TLS/error/fuzz suites complete canonical journeys | Planned |
| RT-011 | Local storage and per-application data containers | Phases 7, 13; WS-04, WS-11 | Isolation, quota, reset, snapshot, corruption, migration, backup, and traversal tests pass | Planned |
| RT-012 | Accessibility behavior and semantic tree | Phases 8-9, 14, 22; WS-05, WS-09 | Role/name/value/action/order/focus, keyboard, contrast, Dynamic Type, reduced motion, and independent disabled-user reviews pass | Planned |
| RT-013 | Notifications, location, sensor, camera, photo, and device-service simulation | Phase 10; WS-07 | Deterministic fixtures, permissions, event replay, reset, parallel isolation, and UI automation tests pass | Planned |
| RT-014 | Low-privilege application sandbox with brokered capabilities | Phases 7, 20; WS-04, WS-11 | AppContainer/equivalent policy, independent penetration test, broker authorization, data isolation, and escape tests pass | Planned |
| RT-015 | Typed, versioned, bounded local host protocol | Phases 5-7; WS-03, WS-04 | Cross-language fixtures prove framing, negotiation, authentication, limits, stale-event rejection, and forward/backward compatibility | In progress |
| RT-016 | Stable view identity, state invalidation, event dispatch, and reconciliation | Phases 5, 9; WS-04, WS-05 | Typing and actions invoke Swift closures and publish deterministic revisions without losing focus/state; replay and stale-event tests pass | In progress |
| RT-017 | Application stdout/stderr remain separate from protocol traffic | Phase 5; WS-03, WS-08 | Native child print/log stress tests cannot corrupt or spoof framed protocol messages | In progress |
| RT-018 | Crash containment and last-known-good preview | Phases 5, 11-12; WS-04, WS-08 | Child crash and compile-error process tests leave host alive, preserve last good render, and produce symbolized diagnostics | In progress |

## Simulator and developer experience

| ID | Proposal requirement | Owning phases/workstreams | Required acceptance evidence | Current status |
| --- | --- | --- | --- | --- |
| SIM-001 | Windows-native interactive iPhone/iPad preview host | Phases 5, 8-10; WS-06, WS-07 | Signed host passes display, device-shell, input, DPI, multi-monitor, GPU-loss, and accessibility matrices | Bootstrap evidence |
| SIM-002 | Touch, gestures, mouse, keyboard, and touchscreen input | Phases 8-10; WS-06, WS-07 | Gesture conflict, multi-touch, IME, focus, keyboard, pointer, replay, and latency suites pass | Planned |
| SIM-003 | Rotation and multiple iPhone/iPad display profiles | Phase 10; WS-07 | Approved geometry, scale, safe-area, orientation, DPI, and screenshot fixtures pass within tolerance | Bootstrap evidence |
| SIM-004 | Light/dark mode, Dynamic Type, contrast, locale, RTL, and accessibility settings | Phases 9-10, 14; WS-05, WS-07 | Deterministic setting injection and visual/semantic comparisons pass the profile matrix | Bootstrap evidence |
| SIM-005 | GPS routes, heading, motion, battery, time, and sensor fixtures | Phase 10; WS-07 | Scripted fixture/replay APIs, reset, permissions, and isolation tests pass | Planned |
| SIM-006 | Camera and photo-library test data | Phase 10; WS-07 | Deterministic media fixtures, consent behavior, reset, privacy, and malformed-input tests pass | Planned |
| SIM-007 | Network throttling, offline, proxy, DNS, and TLS failure simulation | Phases 10, 13; WS-04, WS-07 | Repeatable conditioning profiles and network fault journey tests pass | Planned |
| SIM-008 | Local push-notification simulation | Phase 10; WS-07 | Payload validation, delivery timing, lifecycle, permission, deep-link, and replay tests pass | Planned |
| SIM-009 | Screenshots and screen recording | Phases 8, 10; WS-06, WS-07 | Color-managed deterministic capture, recording integrity, privacy redaction, and CLI automation tests pass | Planned |
| SIM-010 | Automated unit, UI, headless, and parallel testing | Phases 10-11, 16; WS-08, WS-09 | Headless sharding, isolation, deterministic reset, UI queries/events, traces, screenshots, and CI reports pass | Planned |
| SIM-011 | Fast launch and interaction | Phases 5, 10, 22; WS-06, WS-07 | Warm launch and input-to-render p95 meet each phase and GA SLO on reference hardware | Planned |
| SIM-012 | Install, terminate, reset, erase, clone, and snapshot device state | Phase 10; WS-07 | Lifecycle commands remain deterministic across 1,000 repeated and parallel runs | Planned |

## Toolchain, IDE, debugger, and diagnostics

| ID | Proposal requirement | Owning phases/workstreams | Required acceptance evidence | Current status |
| --- | --- | --- | --- | --- |
| DEV-001 | Pinned open-source Swift compiler, linker, package manager, debugger, and language server | Phases 4-6, 11; WS-03, WS-08 | Hash-pinned toolchains, licenses, clean-machine install, reproducible builds, LLDB/DAP and SourceKit-LSP matrices pass | In progress |
| DEV-002 | Swift code editing, syntax, completion, errors, navigation, rename, and formatting | Phase 11; WS-08 | SourceKit-LSP performance and correctness tests plus moderated developer workflow pass | Planned |
| DEV-003 | Project creation, opening, dependency resolution, resources, and packaging | Phases 6, 11; WS-03, WS-08 | CLI/IDE workflows cover SwiftPM projects, modules, packages, resources, deterministic bundles, and diagnostics | Bootstrap evidence |
| DEV-004 | One-button Run workflow | Phases 5, 11-12; WS-08 | IDE task selects device, performs incremental build, swaps child after handshake/first render, attaches debugger, and preserves last good preview | Planned |
| DEV-005 | Incremental compilation, caching, and fast updates | Phases 6, 11, 22; WS-03, WS-08 | Hermetic content-addressed cache, invalidation, poisoning, one-file build p95, and reproducibility tests pass | Planned |
| DEV-006 | Visual interface previews and live updates | Phases 5, 9, 11; WS-05, WS-08 | Preview dependency tracking, state preservation rules, failed-build recovery, and interaction tests pass | In progress |
| DEV-007 | Breakpoints, stepping, watches, variables, exceptions, async tasks, and source maps | Phases 4, 11; WS-08 | LLDB/DAP conformance and usability workflows pass on x64/ARM64 | Planned |
| DEV-008 | Console logs separated by application, runtime, build, and validation source | Phases 5, 11, 17; WS-08, WS-10 | Structured streams, redaction, ordering, retention, export, and protocol-corruption tests pass | In progress |
| DEV-009 | Memory, CPU, frame, launch, network, and performance tooling | Phases 11, 22; WS-08, WS-09 | Inspector accuracy, overhead budgets, leak/soak cases, trace export, and support workflow pass | Planned |
| DEV-010 | Application packaging and reproducible artifacts | Phases 6, 12, 24-25; WS-03, WS-12 | Deterministic bundles, signatures, SBOM, provenance, installer/updater/rollback, and clean-machine tests pass | Planned |
| DEV-011 | Git and continuous-integration integration | Phases 11-12, 16, 21; WS-08, WS-12 | CLI is automation-safe; VS Code tasks and headless test/validation APIs work in supported CI systems | Planned |
| DEV-012 | Actionable unsupported-API diagnostics and compatibility report | Phases 6, 9, 16, 18; WS-03, WS-09 | Compile-time and project reports name exact symbol, status, reason, workaround, remote route, evidence version, and confidence | Bootstrap evidence |
| DEV-013 | Cross-platform project source strategy | Phases 2, 4-6; WS-01, WS-03 | Approved same-source and conditional-compilation profiles build on Windows and official Apple tooling without proprietary redistribution | Planned |

## Framework and hardware capability routing

| ID | Proposal requirement | Owning phases/workstreams | Required acceptance evidence | Current status |
| --- | --- | --- | --- | --- |
| FW-001 | Navigation, text, images, buttons, forms, lists, scrolling, animation, storage, REST, JSON, lifecycle, unit/UI tests | Phases 5, 9-14; WS-04, WS-05, WS-09 | Maintained reference apps and API conformance suites pass declared phase thresholds | Bootstrap evidence |
| FW-002 | Common third-party source Swift packages | Phases 6, 19; WS-03, WS-13 | Source-package build farm, licenses, malicious-package controls, certification, and corpus build-rate gates pass | Planned |
| FW-003 | ARKit and RealityKit simulation/remote validation | Phase 26; WS-06, WS-07, WS-10 | Separately approved feasibility profile; unsupported journeys are explicitly remote-only | Remote-only by design |
| FW-004 | Metal and advanced graphics translation | Phases 8, 26; WS-06 | Metal-to-D3D/Vulkan feasibility, shader/legal/security review, conformance, driver, performance, and fallback gates pass before support claims | Planned |
| FW-005 | Apple Pay | Phases 17, 26; WS-10 | Local UI fixture is labelled non-transactional; authoritative payment testing uses permitted official infrastructure and security review | Remote-only by design |
| FW-006 | HealthKit | Phases 17, 26; WS-07, WS-10, WS-11 | Synthetic local fixtures have privacy controls; authoritative behavior remains on permitted devices | Remote-only by design |
| FW-007 | CarPlay and specialized device integrations | Phases 17, 26; WS-07, WS-10 | Explicit device-lab profile, entitlement/legal review, and remote-only diagnostics | Remote-only by design |
| FW-008 | Advanced camera/media | Phases 10, 26; WS-06, WS-07 | Fixture-based local subset plus physical-device validation profile; fidelity and privacy limitations published | Planned |
| FW-009 | Secure Enclave operations | Phases 7, 17; WS-04, WS-10, WS-11 | Orchard Windows key store is never represented as Secure Enclave; official security semantics validated remotely | Remote-only by design |
| FW-010 | StoreKit | Phases 17, 26; WS-04, WS-10 | Local test subset is separately labelled; receipt, commerce, sandbox, and production flows use official validation | Remote-only by design |
| FW-011 | MapKit, Core ML, and selected advanced accessibility APIs | Phase 26; WS-04, WS-05, WS-06 | Each framework has a separately approved profile, corpus, conformance suite, performance/security budget, and fallback | Planned |

## Clean-room specification and compatibility program

| ID | Proposal requirement | Owning phases/workstreams | Required acceptance evidence | Current status |
| --- | --- | --- | --- | --- |
| QA-001 | Organizationally separated clean-room specification and implementation | Phases 1-3; WS-02 | Separate access groups, repositories, communication channels, signed training, immutable provenance, and counsel audit pass G0/G1 | In progress |
| QA-002 | Observe only legally approved public behavior | Phases 2-3, 16; WS-02, WS-09 | Every specification links approved evidence source, observer, tool, date, uncertainty, and legal classification | Planned |
| QA-003 | Compare layout, screenshots, animation, gestures, lifecycle, network, API results, accessibility, and performance | Phases 8-10, 16-17; WS-09, WS-10 | Normalized cross-run values/traces/images/semantics with tolerances, environment metadata, and reviewed diffs | Planned |
| QA-004 | Automated compatibility database and prioritization | Phases 16, 18-24; WS-09 | Versioned results, regression ownership, bisection, flake controls, corpus weighting, and public status provenance | Planned |
| QA-005 | Transparent local compatibility and required-validation reporting | Phases 2, 6, 16, 18; WS-08, WS-09 | Reports distinguish Full, Partial, Simulated, Remote-only, Unsupported, Untested; false-confidence rate meets release gate | Bootstrap evidence |
| QA-006 | Track annual Apple platform and Swift changes | Phase 26 and annual sustaining lane; WS-03, WS-09, WS-13 | Compatibility branches, deprecation policy, annual corpus refresh, toolchain qualification, and published support windows | Planned |
| QA-007 | Fuzz, property, boundary, stress, soak, visual, performance, installation, upgrade, security, and accessibility testing | Phases 6-25; WS-09, WS-11, WS-12 | Required matrix is green at each promotion gate with owned, expiring quarantine records | In progress |

## Apple validation, signing, devices, and publishing

| ID | Proposal requirement | Owning phases/workstreams | Required acceptance evidence | Current status |
| --- | --- | --- | --- | --- |
| VAL-001 | Official-toolchain builds on permitted Apple hardware | Phases 4, 17, 20; WS-10 | Ephemeral worker invokes official supported tools; job provenance records versions, inputs, outputs, and policy | Planned |
| VAL-002 | Official Simulator and physical iPhone/iPad tests | Phases 17, 20-22; WS-10 | Managed simulator/device pools, reset/data-remanence tests, inventory, health checks, and canonical journeys pass | Planned |
| VAL-003 | Paired Windows/Apple screenshots, traces, semantics, and behavioral differences | Phases 16-18; WS-09, WS-10 | Normalized paired artifacts and actionable divergence report generated from the same revision/test specification | Planned |
| VAL-004 | Signed App Store build creation | Phases 17, 20, 24-25; WS-10, WS-11 | User-authorized credentials, HSM/wrapped storage, separation of duties, complete audit, deletion, and official artifact verification | Planned |
| VAL-005 | Assisted App Store submission | Phases 17, 23-25; WS-10, WS-13 | Explicit user authorization, preflight, reviewable metadata/artifacts, official API/tool use, audit, retry, and rollback procedures | Planned |
| VAL-006 | Validation quotas, queues, teams, CI, and private pools | Phases 20-23, 26; WS-10, WS-12 | RBAC, quotas, reservations, webhooks, capacity/error budgets, tenant isolation, private networking, and SLA tests pass | Planned |
| VAL-007 | Ephemeral workspaces, retention, deletion, encryption, and tenant isolation | Phases 17, 20, 24; WS-10, WS-11 | Cross-tenant/red-team, deletion, key rotation, backup/restore, worker compromise, and device-remanence exercises pass | Planned |
| VAL-008 | High-availability production validation service | Phases 20, 22-25; WS-10, WS-12 | Multi-region control plane and worker fleets meet 99.95% GA SLO, RPO/RTO, 2x peak capacity, and staged-failure drills | Planned |

## Distribution, security, enterprise, and operations

| ID | Proposal requirement | Owning phases/workstreams | Required acceptance evidence | Current status |
| --- | --- | --- | --- | --- |
| OPS-001 | Signed Windows installer, updater, rollback, clean uninstall, and offline install | Phases 12, 22, 24-25; WS-12 | Full lifecycle matrix passes standard/admin, proxy, restricted network, offline, x64/ARM64, and enterprise deployment | Planned |
| OPS-002 | Reproducible signed releases, SBOM, provenance, and dependency policy | Phases 6, 12, 24-25; WS-11, WS-12 | Immutable promoted artifacts, two-person signing, SLSA-style attestations, SBOM, license notices, and rebuild comparison | Planned |
| OPS-003 | Product telemetry and diagnostics with consent and redaction | Phases 11-12, 18, 20; WS-08, WS-11 | Default/minimized data map, consent UI, secret/PII redaction, retention/deletion, export, and privacy review | Planned |
| OPS-004 | Security response, incident command, vulnerability intake, bug bounty, and release response | Phases 12, 20, 23-25; WS-11, WS-12 | PSIRT drills, on-call, SLAs, secure advisory/revocation/update paths, and executive incident exercises pass | Planned |
| OPS-005 | Organizations, projects, teams, permissions, service accounts, audit, SSO, SCIM, and RBAC | Phases 20-21, 26; WS-11, WS-13 | Authorization model, tenant tests, audit completeness, SSO/SCIM interop, break-glass, and offboarding tests pass | Planned |
| OPS-006 | Private infrastructure and dedicated Apple validation pools | Phases 20, 26; WS-10, WS-12 | Physical/logical isolation, customer acceptance, capacity/SLA, rebuild, access, audit, and private networking tests pass | Planned |
| OPS-007 | Enterprise compliance and regional/data controls | Phases 20, 23-26; WS-11 | SOC 2 Type II, disclosed ISO 27001 status, DPIAs, regional controls, retention/deletion, subprocessors, and contract evidence | Planned |
| OPS-008 | LTS, offline development, centralized deployment, and policy controls | Phase 26; WS-08, WS-12, WS-13 | LTS support policy, offline license leasing, air-gapped update procedure, rollback, audit export, and customer acceptance pass | Planned |
| OPS-009 | Availability, capacity, backup, restore, failover, and cost telemetry | Phases 20, 22-25; WS-12 | Error-budget dashboards, load at 2x forecast, RPO/RTO exercises, regional failover, restore, and unit-economics gates pass | Planned |

## Commercial editions, customers, and ecosystem

| ID | Proposal requirement | Owning phases/workstreams | Required acceptance evidence | Current status |
| --- | --- | --- | --- | --- |
| COM-001 | Community edition for individuals and students | Phases 18, 23-25; WS-01, WS-13 | Entitlement, local feature, fair-use validation quota, privacy, support, and educational onboarding tests pass | Planned |
| COM-002 | Professional edition with advanced tools and validation | Phases 21, 23-25; WS-08, WS-10, WS-13 | Metering, entitlements, billing, limits, upgrade/downgrade/refund, and feature-access tests pass | Planned |
| COM-003 | Team edition with CI, collaboration, permissions, and analytics | Phases 20-23; WS-10, WS-13 | Organization/RBAC, CI, shared results, audit, compatibility analytics, quota, and billing acceptance pass | Planned |
| COM-004 | Enterprise edition with private infrastructure, security, compliance, custom support, and SLA | Phases 20, 23-26; WS-10, WS-13 | Contracted controls, private-pool isolation, SSO/SCIM, SLA, support escalation, deployment, and customer acceptance pass | Planned |
| COM-005 | Schools and training programs | Phases 18, 21, 23; WS-13 | Managed-lab deployment, curriculum samples, offline/proxy modes, accessibility, privacy, and instructor administration pilots pass | Planned |
| COM-006 | Windows-first companies and workstation fleets | Phases 21-26; WS-08, WS-12, WS-13 | Central deployment, policy, proxy, restricted network, audit, CI, update rings, and support pilots pass | Planned |
| COM-007 | Cross-platform, game, testing, and cloud-development integrations | Phases 19, 21, 26; WS-03, WS-08, WS-13 | Published integration profiles, sample projects, package/tool certification, automation APIs, and partner acceptance pass | Planned |
| COM-008 | Regions where Apple hardware is costly or scarce | Phases 20-25; WS-10, WS-13 | Regional latency, data terms, validation capacity, pricing, payments, support, export/privacy, and accessibility review pass | Planned |
| COM-009 | Documentation, samples, migration guides, compatibility database, support, and developer relations | Phases 12, 18-26; WS-13 | Versioned docs tested against releases, searchable status data, sample CI, onboarding studies, support SLAs, and feedback loop | Planned |

## Program completion rules

Project Orchard is not production ready merely because every row has an owner.
Production readiness requires all of the following:

1. Every requirement applicable to GA Profile 1 has objective evidence and is
   marked complete by its accountable owner.
2. Every phase dependency and gate through G8 has passed; no gate may be waived
   by changing the denominator or relabelling a failure.
3. Remote-only, partial, simulated, unsupported, and untested behavior remains
   visible to developers and cannot be represented as locally compatible.
4. Legal, clean-room, security, accessibility, privacy, SRE, support, and
   finance approvals are independent of feature-team approval.
5. The release completion audit links each table row to immutable build,
   test, service, audit, legal, or customer-acceptance evidence.

Rows may be split as the implementation becomes more detailed, but no row may
be deleted without an approved product change record that explains the user
impact, compatibility denominator change, migration, and public disclosure.
