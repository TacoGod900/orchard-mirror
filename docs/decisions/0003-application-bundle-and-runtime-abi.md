# ADR 0003: Use an Orchard-owned bundle and narrow runtime ABI

Status: Proposed  
Date: 2026-07-18  
Decision owners: Toolchain and Runtime  
Required approvers: Architecture, Security, Release Engineering, and Clean-Room Governance

## Context

A loose executable is enough for a spike but not for deterministic installation, resource loading, capability admission, debugging, caching, updates, or provenance. Orchard also needs generated applications and independently versioned framework/runtime releases to communicate without binding product code to unstable Swift compiler internals.

The local artifact cannot be an IPA, Apple application bundle, Mach-O container, copied Xcode output, or a package that implies Apple signing validity. The Apple validation plane must rebuild the source independently with official tooling.

## Decision

The local linker and packager will emit an **Orchard Application Bundle (OAB)**. An OAB is an Orchard-owned, content-addressed directory or deterministic archive containing only authorized Windows artifacts and metadata. It is never submitted to App Store tooling.

The first stable bundle specification must include:

- a versioned canonical manifest;
- x64 and/or ARM64 Windows PE/COFF executables and approved native libraries;
- Orchard module/runtime compatibility requirements;
- immutable compatibility-profile and toolchain identities;
- application identity, entry point, resource index, localization index, and declared capabilities;
- per-file size, media type, executable classification, and SHA-256 digest;
- debug-symbol references separated from redistributable release content;
- dependency-license and SBOM references;
- build-manifest, source-graph, and provenance digests;
- optional Orchard publisher signature and policy attestations.

Unknown required manifest fields, unsupported versions, digest mismatches, path aliases, absolute paths, reparse points, alternate data streams, Apple binary formats, and undeclared executable content fail admission. Extraction is quota-bound and immune to traversal, case-folding collisions, symlink/reparse escapes, and archive expansion attacks.

Applications call a narrow **Orchard Runtime ABI** rather than compiler-private APIs. The ABI is C-compatible, explicitly versioned, and limited to bootstrap, lifecycle, allocator-safe buffer ownership, structured diagnostics, resource lookup, clock/task integration, capability-session establishment, and render/event exchange. Swift-facing APIs are overlays over that ABI.

ABI calls return explicit status values and use caller/callee ownership rules that work across runtime versions. No Swift object layout, metadata layout, mangled symbol, exception representation, or standard-library private entry point is declared stable by Orchard.

## Reproducibility and signing

Bundle identity is the digest of the canonical manifest plus enumerated file digests. Volatile provenance such as local build time and workstation path is stored out of band and cannot change the bundle identity. Timestamps inside deterministic artifacts use `SOURCE_DATE_EPOCH` or the Unix epoch fallback.

Signing establishes Orchard artifact integrity and publisher policy; it does not create Apple code-signing status. The launcher verifies the manifest and every executable file before session admission. Development mode may accept an unsigned local bundle only through an explicit policy that is visible to the user and audit log.

## Apple validation boundary

An OAB is never uploaded as the input to an Apple release build. The validation plane receives an immutable source snapshot, dependency lock, build intent, profile, and requested official destination. Official tooling independently produces the Apple artifact. Comparisons link both results through source and build-manifest digests, not binary identity.

## Alternatives considered

- **Loose executable plus adjacent files:** rejected because enumeration, integrity, capabilities, and reproducibility are ambiguous.
- **MSIX as the application contract:** not selected. MSIX may distribute Orchard itself, but application bundles need Orchard-specific multi-profile metadata and sandbox admission independent of Windows installation.
- **IPA-compatible local bundle:** rejected on technical, legal, and product-honesty grounds.
- **Stable Swift binary ABI between all Orchard components:** rejected because it unnecessarily couples releases to compiler implementation details.

## Exit criteria

This ADR can move to Accepted only after the manifest schema, canonicalization rules, hostile archive corpus, ABI header, compatibility tests, x64/ARM64 loading tests, signature policy, SBOM format, debug-symbol workflow, rollback behavior, and official-validation source linkage are implemented and reviewed.

