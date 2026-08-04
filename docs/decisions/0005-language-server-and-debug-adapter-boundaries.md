# ADR 0005: Standardize editor and debugger integration on LSP and DAP

Status: Proposed  
Date: 2026-07-18  
Decision owners: Developer Tools and Toolchain  
Required approvers: Architecture, Security, and Developer Experience

## Context

Orchard needs code completion, diagnostics, navigation, formatting, breakpoints, variables, stack traces, test discovery, device control, and preview inspection. Building editor-specific language and debugger engines would duplicate upstream work and create inconsistent CLI, VS Code, Visual Studio, and future IDE behavior.

The selected Swift toolchain already includes SourceKit-LSP, LLDB, and `lldb-dap`, but merely probing their versions does not establish usable project indexing, Windows process launch, source mapping, breakpoint binding, cancellation, or hostile-project isolation.

## Decision

SourceKit-LSP is the authoritative language-service process and Debug Adapter Protocol over `lldb-dap` is the authoritative source-debugging boundary. Orchard adds only versioned, namespaced extensions where a standard capability cannot represent an Orchard project, device, render revision, capability diagnostic, or async/runtime inspection need.

The CLI and IDE clients use an Orchard daemon for project graph, toolchain/profile selection, build scheduling, device lifecycle, and policy. The daemon launches pinned SourceKit-LSP and LLDB/DAP processes with explicit workspaces and bounded environment. Editors do not discover arbitrary tools from `PATH`, and project content cannot select an unverified debugger binary.

Language and debugger stdout are protocol-only. Human-readable diagnostics go to structured logs or stderr with bounds and redaction. JSON-RPC/DAP messages are framed, size-limited, correlated, cancellable, and subject to startup and operation deadlines.

Source paths are represented through a reversible source-map table from immutable build manifests. Breakpoints bind to the exact executable, symbol digest, source digest, and build identity. A stale binary or mismatched source produces a visible unverified breakpoint or launch error, not a silently shifted breakpoint.

## Required baseline scenarios

- LSP initialize/shutdown/exit and capability negotiation;
- document open/change/close with UTF-16 position correctness;
- diagnostics, completion, hover, definition, references, rename, symbols, and formatting;
- workspace cancellation, restart, crash recovery, and index invalidation;
- DAP initialize, launch, configurationDone, breakpoint binding, continue/pause, stackTrace, scopes, variables, evaluate, exception, disconnect, and terminated;
- a breakpoint and stack trace in a native Swift Orchard sample at its original source line;
- paths containing spaces and non-ASCII characters;
- deterministic selection of the pinned toolchain and Windows SDK;
- no protocol corruption from application stdout/stderr;
- hostile message, oversized frame, process crash, timeout, and orphan cleanup tests.

## Security boundary

Language servers, compilers, package plugins, build scripts, debugger expression evaluation, debugged applications, and debug adapters process untrusted content. They run outside the IDE process with least privilege, constrained inheritance, explicit workspace access, resource limits, and auditable tool identities. Debugger attach to an arbitrary process requires an explicit user action and policy; the normal workflow may attach only to a process launched for the selected Orchard session.

## Alternatives considered

- **Custom parser-backed editor intelligence:** rejected for production because it cannot match Swift semantics.
- **Editor-specific debugger APIs:** rejected because they fragment behavior and testing.
- **Direct LLDB library embedding in the IDE:** deferred; it increases crash and ABI coupling. DAP process isolation is the default.
- **Unpinned tools found on PATH:** rejected for reproducibility and supply-chain integrity.

## Exit criteria

Acceptance requires protocol harnesses, real Swift sample indexing, a verified source breakpoint and stack trace, debugger-variable inspection, process isolation, crash recovery, latency measurements, security review of expression evaluation, and the supported client capability matrix.

