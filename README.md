# Project Orchard

Project Orchard is an experimental Windows development platform for exploring how a clean-room, Swift-oriented application workflow could work without redistributing Apple software.

The repository contains two deliberately separate implementation lanes. The
legacy .NET bootstrap reads a limited SwiftUI-shaped source subset, lowers it to
Orchard JSON IR, and renders it with a Windows Forms preview. The
production-directed feasibility lane uses the pinned open-source Swift compiler for Windows,
an Orchard-owned `OrchardUI` package, strict cross-language contracts, and a
separate-process host design.

```text
ContentView.swift
        |
        v
Orchard lexer and subset parser (.NET)
        |
        v
Orchard IR 0.1 JSON
        |
        v
Windows Forms preview runtime
```

The checked-in `NativeHello` sample and Swift SDK are compiled and type-checked
by Swift 6.3.3 for Windows, but they import `OrchardUI`, not Apple `SwiftUI`.
Neither lane loads Apple frameworks, produces an iOS binary, or establishes
SwiftUI/UIKit/iOS compatibility. The persistent native child-to-host live session
is under integration and has not yet passed its complete process gate. See
[Current status](docs/CURRENT_STATUS.md) before evaluating any capability claim.

## Production program

The repository carries the implementation together with the controls required
to prevent a prototype from being mistaken for a production compatibility
platform:

- [Master production execution plan](docs/EXECUTION_PLAN.md) — 27 governed
  phases, workstreams, staffing, cost, gates, SLOs, and GA criteria.
- [Proposal traceability](docs/PROPOSAL_TRACEABILITY.md) — every original
  runtime, simulator, IDE, framework, validation, enterprise, and commercial
  requirement mapped to objective acceptance evidence.
- [Architecture](docs/ARCHITECTURE.md), the accepted
  [source-target decision](docs/decisions/0001-source-compatible-windows-target.md),
  and the native-slice
  [process/IPC decision](docs/decisions/0002-native-application-process-and-local-protocol.md).
  Proposed follow-on decisions cover the
  [bundle/runtime ABI](docs/decisions/0003-application-bundle-and-runtime-abi.md),
  [UI kernel/render graph](docs/decisions/0004-ui-kernel-and-render-graph-boundary.md),
  and [LSP/DAP boundaries](docs/decisions/0005-language-server-and-debug-adapter-boundaries.md).
- [RAID register](docs/RAID_REGISTER.md) — initial risks, assumptions, active
  issues, dependencies, triggers, owners, mitigations, and contingencies.
- [Clean-room/legal](docs/CLEAN_ROOM_AND_LEGAL.md),
  [threat model](docs/THREAT_MODEL.md), and
  [quality/compatibility](docs/QUALITY_AND_COMPATIBILITY.md) policies.

## What works today

- A dependency-light .NET 10 solution targeting Windows.
- CLI commands for project creation, diagnostics, compilation, compatibility reporting, IR inspection, device listing, and preview launch.
- The retained bootstrap lexer and structural parser for a small SwiftUI-shaped subset.
- A hash-pinned official Swift 6.3.3 Windows toolchain plus checked probes for
  `swift`, `swift-format`, SourceKit-LSP, LLDB, and LLDB-DAP.
- Swift packages for strict Orchard IR handling and an Orchard-owned declarative
  `OrchardUI` API. `OrchardUI` currently has deterministic structural/keyed
  identity, session-owned state and bindings, revisioned rendering, semantic
  events, stale-event rejection, and main-actor isolation.
- Orchard IR containing view nodes, modifiers, events, state declarations,
  source locations, and a fail-closed versioned compatibility report.
- Shared accepted/rejected IR fixtures and one capability-profile file consumed
  by the .NET and Swift implementations.
- A typed, versioned, bounded .NET process protocol and a current-user named-pipe
  transport with random endpoints, per-session authentication, protocol
  negotiation, bounded waits, cancellation, and full-duplex frames.
- A Windows Forms preview for common static controls and a narrowly interpreted set of state updates.
- Independently verified suites of 39 bootstrap/core tests, 42 protocol tests,
  9 .NET transport tests, and 32 Swift SDK tests. These results predate completion
  of the current native live-session integration; the full post-integration gate
  still has to be run.
- Repository verification through `eng/check.ps1` and a configured Windows CI
  workflow. A clean-machine CI run for the complete Swift lane is not yet recorded.

## Prerequisites

- Windows 11 for supported preview-host use.
- The .NET SDK version pinned in [global.json](global.json) and [toolchains/orchard-toolchain.lock.json](toolchains/orchard-toolchain.lock.json).
- The official Swift 6.3.3 Windows toolchain, Visual Studio 2022 C++ tools, and
  Windows 11 SDK versions pinned in the toolchain lock when exercising the native
  Swift lane or the full repository check.
- PowerShell 7 is recommended for the verification script; Windows PowerShell 5.1 is also supported.
- Git for normal contribution workflows.

Swift is not required to inspect or run only the legacy .NET bootstrap. It is
required for the native lane and the default full verification. Do not replace
the pinned version or hashes with a floating/latest toolchain.

## Verify the repository

From the repository root:

```powershell
pwsh -NoProfile -File .\eng\check.ps1
```

If PowerShell 7 is not installed, use the inbox Windows PowerShell host:

```powershell
powershell.exe -NoProfile -File .\eng\check.ps1
```

The full check is intended to perform the following operations:

1. Confirms that `global.json` and the Orchard toolchain lock agree.
2. Verifies the active .NET SDK and pinned Swift/Visual Studio/Windows SDK tools.
3. Parses the checked-in JSON documents.
4. Restores and formatting-checks the solution.
5. Builds the entire solution in Release with warnings treated as errors.
6. Runs the .NET bootstrap/core, protocol, and transport suites.
7. Strictly formats, tests, and release-builds the Swift SDK and native sample.
8. Builds the legacy sample through the CLI, validates native and legacy IR, and
   checks deterministic native output.

Generated build and validation output is written beneath `artifacts/`, which is ignored by Git.

The latest individual suites are green, but the composite check has not yet been
recorded after the in-progress native live-session changes. Until it is, do not
treat a partial command or configured CI workflow as release evidence.

## Use the bootstrap CLI

Until packaging is implemented, invoke the CLI through `dotnet run`:

```powershell
dotnet run --project .\src\Orchard.Cli\Orchard.Cli.csproj -- doctor
dotnet run --project .\src\Orchard.Cli\Orchard.Cli.csproj -- devices
dotnet run --project .\src\Orchard.Cli\Orchard.Cli.csproj -- compatibility .\samples\HelloOrchard
dotnet run --project .\src\Orchard.Cli\Orchard.Cli.csproj -- build .\samples\HelloOrchard
dotnet run --project .\src\Orchard.Cli\Orchard.Cli.csproj -- run .\samples\HelloOrchard
```

`run` opens a desktop window and therefore requires an interactive Windows session. `build` writes `artifacts/app.orchard.json` beneath the selected project unless `--output` is supplied.

Other available commands are:

```text
orchard init [directory] [--force]
orchard inspect <compiled.orchard.json>
orchard compatibility [project] [--json]
orchard version
```

Set `ORCHARD_TRACE=1` when diagnosing an unexpected CLI failure. Trace output may contain local file paths and should be reviewed before sharing.

## Repository map

```text
src/Orchard.Core             IR, manifest, JSON, diagnostics, and device models
src/Orchard.Compiler         Subset lexer/parser and compatibility catalogue
src/Orchard.Runtime.Windows  Windows Forms preview runtime
src/Orchard.Cli              Command-line entry point
src/Orchard.Protocol         Strict versioned host/application messages and framing
src/Orchard.Transport.Windows Authenticated current-user named-pipe transport
sdk/swift                    OrchardProtocol, OrchardUI, and native transport work
tests/Orchard.Tests          Bootstrap/core and cross-language IR tests
tests/Orchard.Protocol.Tests Protocol validation and framing tests
tests/Orchard.Transport.Windows.Tests Named-pipe integration tests
tests/fixtures               Shared accepted/rejected cross-language fixtures
samples/HelloOrchard         Checked-in bootstrap sample
samples/NativeHello          Native Windows Swift/OrchardUI sample
schemas/                     Versioned Orchard data contracts
eng/                         Reproducible repository checks
toolchains/                  Toolchain selection and pinning state
```

## Important limitations

- In the legacy lane, `import SwiftUI` is treated as source text; no Apple module
  is loaded. Its parser is not a complete Swift grammar and does not perform Swift
  name lookup, type checking, macro expansion, result-builder evaluation, or
  package resolution.
- The native lane compiles real Swift, but its API is the small Orchard-owned
  `OrchardUI` feasibility surface. Familiar declarative spelling is not evidence
  of Apple SwiftUI behavior.
- Control flow, closures, bindings, interpolation, overloads, and modifiers are supported only where explicitly implemented.
- In the legacy WinForms preview, button bodies are not arbitrary Swift
  execution; it recognizes a narrow set of print and string-assignment patterns.
  Native OrchardUI buttons store Swift closures, but cross-process event dispatch
  is still inside the unverified live-session gate.
- Layout and appearance are Windows approximations and are not evidence of iOS fidelity.
- Compatibility percentages are catalogue-based engineering signals, not proof that an application behaves correctly on Apple hardware.
- Device profiles are preview dimensions, not virtualized devices.
- LLDB, LLDB-DAP, and SourceKit-LSP binaries are pinned and launchable, but real
  breakpoint, stack, completion, and diagnostic workflows are not yet verified.
- The current-user transport is not the planned low-privilege AppContainer and
  brokered-capability production sandbox.
- There is no verified persistent native GUI preview, hot reload, iOS binary
  support, signing, physical-device execution, Apple validation service, or App
  Store workflow.

## Current vertical-slice gate

The open vertical-slice architecture moves production-directed execution away
from source interpretation and through an Orchard-owned declarative Swift package
compiled by the stock open-source Swift compiler for Windows:

```text
Swift source importing OrchardUI
        |
        v
Pinned SwiftPM / swiftc toolchain
        |
        v
Native Windows child process
        |
        v
Versioned Orchard view IR over a secured local transport
        |
        v
Persistent Windows preview host
```

The compiler, `OrchardProtocol`, `OrchardUI` state engine, shared fixtures, and
.NET authenticated transport provide foundations for this slice. The native
Swift pipe client and persistent .NET host are currently being integrated. The
slice remains open until one end-to-end test proves initial render, text change,
button closure execution, revision advancement, stale-event rejection, ping/pong,
stdout/stderr separation, deterministic shutdown, bad-token handling, timeout,
and child-crash containment. The exact gates are maintained in
[Current status](docs/CURRENT_STATUS.md).

## Clean-room boundary

Contributions must not include Apple SDKs, operating-system images, extracted framework metadata, copied headers that are not lawfully redistributable, proprietary fonts or icons, disassembled code, or confidential documentation. Public API observation and compatibility work must follow the provenance rules in [CONTRIBUTING.md](CONTRIBUTING.md).

Project Orchard does not remove the need for Apple-controlled signing, real-device validation, or App Store submission infrastructure. Final release compatibility must be established on supported Apple hardware using lawful tooling.

## Contributing and security

Read [CONTRIBUTING.md](CONTRIBUTING.md) before submitting changes. Report security issues using the private process in [SECURITY.md](SECURITY.md), not a public issue.
