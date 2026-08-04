# Quality, compatibility, and release policy

## 1. Quality contract

Orchard is production-ready only for a published, versioned compatibility profile. A profile identifies the Orchard runtime/SDK, Swift toolchain, supported Windows builds and architectures, reference Apple SDK generation, supported API statuses, device profiles, qualified packages, known deviations, and end-of-support date.

There is no unqualified “supports iOS” claim. Every symbol and user journey has evidence and a status. A compile-only stub counts as unsupported.

## 2. API support states

The compatibility database tracks multiple dimensions rather than one flag:

- declaration specified;
- source compiles;
- links without a placeholder;
- functional behaviour verified;
- errors and negative paths verified;
- lifecycle and threading verified;
- visual geometry verified;
- accessibility semantics verified;
- performance qualified;
- differential evidence current;
- package/application corpus exercised;
- simulation-only, Apple-validation-only, or unavailable.

Public roll-up states are:

| Status | Meaning |
| --- | --- |
| Full | All applicable definition-of-done gates pass for the profile |
| Constrained | Useful local implementation with documented bounded differences |
| Simulated | Deterministic development substitute; not production-equivalent |
| Apple validation required | Official Apple execution is required for meaningful behaviour |
| Unavailable | Compilation or execution stops with a diagnostic |

## 3. Definition of done for an API

An API is `Full` only when all applicable items are complete:

1. Counsel-approved provenance and behaviour specification.
2. Versioned public declaration and availability policy.
3. x64 implementation; ARM64 implementation before ARM64 is in the same profile.
4. Unit, property, boundary, negative, error, lifecycle, cancellation, and concurrency tests.
5. Differential or otherwise approved reference evidence.
6. Deterministic behaviour under the simulator clock, network, and input controls.
7. Accessibility roles, names, values, actions, order, focus, and automation semantics.
8. Localization, Unicode, bidirectional layout, locale, calendar, and time-zone behaviour where relevant.
9. Security and privacy review for capability-bearing or data-handling APIs.
10. CPU, memory, allocation, handle, GPU, latency, and energy-simulation budgets.
11. Stable diagnostic codes for unsupported or invalid use.
12. Documentation, example, compatibility entry, and known deviations.
13. Upgrade, state migration, and rollback behaviour.
14. Named support owner and incident runbook.
15. Two release trains without an unresolved protected-corpus regression.

No schedule exception can relabel a constrained or simulated implementation as full.

## 4. Compatibility scorecard

Per-application reports show each component independently:

| Dimension | Weight in optional roll-up | Evidence |
| --- | ---: | --- |
| Static API and package availability | 15% | Compiler and package graph |
| Behavioural conformance | 30% | Approved API and integration tests |
| Canonical journey completion | 30% | Automated reference workflows |
| Visual/layout confidence | 10% | Geometry, semantic, and perceptual comparison |
| Accessibility confidence | 10% | Normalised accessibility-tree comparison |
| Performance suitability | 5% | Published workload budgets |

Hard caps override the arithmetic:

- A missing critical-path API prevents “locally compatible.”
- A silent no-op, fake security result, data-loss defect, or critical accessibility blocker scores the affected journey as failed.
- `Simulated`, `Apple validation required`, and `Unavailable` do not count as full local coverage.
- Untested dynamic paths reduce confidence and are listed separately.
- Evidence older than the profile’s permitted validation age is stale and cannot support GA.

The original 90–95% product goal means at least 90% of weighted canonical journeys in the declared target corpus complete locally, not 90% of all public API names.

## 5. Test architecture

### Presubmit

- formatting, compiler warnings, deterministic build, source analysis;
- affected unit and parser/compiler tests;
- schema fixtures and serialization round trips;
- licence, secret, prohibited-material, SBOM, and provenance checks;
- targeted fuzz regression cases;
- target p95 below five minutes.

### Pull request

- all affected component suites;
- protocol compatibility and malformed-input tests;
- Windows x64 integration smoke test;
- application IR and compatibility-diagnostic snapshots;
- target p95 below fifteen minutes.

### Merge and nightly

- complete Windows architecture/OS matrix as available;
- renderer structure and deterministic screenshot suites;
- compiler/runtime/framework integration;
- package qualification builds;
- official Apple differential tests in the separated validation lab;
- installer upgrade/repair/uninstall checks;
- target merge p95 below 45 minutes; nightly may run for hours.

### Weekly and release candidate

- maintained reference-application corpus and canonical journeys;
- physical Apple device validation;
- fuzzing, soak, GPU reset, low-memory, network failure, clock skew, and parallel-device tests;
- accessibility, localization, RTL, Dynamic Type, keyboard, high-contrast, and reduced-motion matrix;
- malicious project/package, tenant isolation, credential rotation, backup restore, and regional failover exercises at the required cadence;
- clean install, N-1 upgrade, rollback, offline, proxy, enterprise policy, and restricted-network tests.

Quarantined tests require an owner, defect, reason, expiry, and release impact. Quarantining a release-critical conformance test does not turn the release green.

## 6. Reference matrix

At GA the maintained matrix includes:

- supported Windows 11 releases on x64 and ARM64;
- Intel, AMD, and Qualcomm CPU families;
- Intel, AMD, and NVIDIA graphics, plus deterministic software rendering;
- common DPI levels, touch and non-touch, multiple displays, HDR modes, and remote-session constraints;
- current and previous supported Orchard/Swift profiles;
- supported locales, calendars, time zones, RTL languages, IMEs, and accessibility configurations;
- official Apple Simulator and physical-device profiles used by the validation service;
- clean, upgraded, enterprise-managed, offline, proxied, and security-restricted machines.

The exact supported matrix is version-controlled and published. Hardware outside it can run in preview status but does not inherit GA guarantees.

## 7. Local SLOs

| Measure | Developer preview | GA objective |
| --- | ---: | ---: |
| Warm device-shell launch p95 | ≤ 8 s | ≤ 5 s |
| Warm one-file build p95 | ≤ 5 s | ≤ 3 s |
| Debugger attach p95 | ≤ 8 s | ≤ 4 s |
| Input-to-render p95 | ≤ 100 ms | ≤ 50 ms |
| Warm completion p95 | ≤ 350 ms | ≤ 200 ms |
| Baseline UI frame rate | 60 fps target | sustained 60 fps under published workload |
| Crash-free local sessions | ≥ 99.0% | ≥ 99.7% |
| Supported-API deterministic pass rate | ≥ 99.0% | ≥ 99.7% |
| Local-pass/Apple-fail escape rate | < 3% | < 1% |
| Deterministic semantic replay | ≥ 99.0% | ≥ 99.5% |

Metrics are measured on published reference hardware. Median numbers never substitute for tail latency.

## 8. Validation-service SLOs

| Measure | Beta | GA objective |
| --- | ---: | ---: |
| Control-plane availability | 99.9% | 99.95% |
| Infrastructure-caused Simulator job success | 99.5% | 99.7% |
| Infrastructure-caused physical-device job success | 98.5% | 99.0% |
| Professional queue start p95 | ≤ 20 min | ≤ 10 min |
| Reserved enterprise queue start p95 | ≤ 5 min | ≤ 3 min |
| Control-data RPO | ≤ 30 min | ≤ 15 min |
| Regional RTO | ≤ 8 h | ≤ 4 h |
| Unattributed signing operations | 0 | 0 |
| Cross-tenant disclosure | 0 | 0 |

Error-budget exhaustion freezes feature promotion in the affected service until reliability recovers.

## 9. Severity and release policy

| Severity | Example | Release treatment |
| --- | --- | --- |
| S0 | Credential exposure, cross-tenant leak, destructive update, legal prohibition | Stop service/release; executive incident command |
| S1 | Common app crash/data loss, sandbox escape, false signing result | Release blocker |
| S2 | Material compatibility failure with workaround, serious accessibility regression | Block unless bounded and explicitly accepted for pre-GA; no unbounded GA exception |
| S3 | Localised defect, diagnostic/documentation gap | Scheduled by impact |
| S4 | Cosmetic improvement | Normal backlog |

GA requires zero open S0/S1 defects. S2 defects need documented scope, workaround, owner, correction date, and Compatibility Claims Board approval.

## 10. Release evidence bundle

Every promoted artifact is immutable and carries:

- source revision and reviewed change set;
- compiler, SDK, runtime, simulator, IDE, and protocol versions;
- reproducible build manifest and signed provenance attestation;
- SBOM, licence notices, dependency vulnerability report, and prohibited-material scan;
- unit/integration/conformance/corpus/performance/security results;
- compatibility delta and known deviations;
- clean-room specification revisions used;
- installer/update/rollback evidence;
- approval records for quality, security, legal, accessibility, privacy, and release operations.

