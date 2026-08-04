# Orchard toolchain governance

The machine-readable source of truth is
[orchard-toolchain.lock.json](orchard-toolchain.lock.json). A developer,
automation job, or release process must not silently select a newer available
toolchain.

## Native Windows slice

The current native slice is validated with:

- .NET SDK 10.0.203;
- Swift 6.3.3 release with assertions, targeting
  x86_64-unknown-windows-msvc;
- Visual Studio 2022 C++ Build Tools in the pinned major-version range;
- Windows SDK 10.0.26100.0; and
- the Swift 6.3.3 Windows platform SDK.

The Swift installer URL and SHA-256, installed compiler/tool hashes, required
Winget dependencies, Visual Studio component IDs, platform SDK location, and
validated target are all recorded in the lock. The installed Swift compiler,
formatter, SourceKit-LSP, LLDB, and LLDB DAP binaries are hash-checked before
the repository invokes them.

Use the exact launcher from the repository:

    powershell -NoProfile -ExecutionPolicy Bypass -File eng/invoke-swift.ps1 --version

The launcher:

1. reads the repository lock;
2. verifies the selected executable hash;
3. locates the required Visual Studio components through vswhere;
4. initializes an x64 C++ build environment;
5. verifies the selected Windows SDK;
6. selects Swift's Windows platform SDK for SwiftPM build tools;
7. removes ambiguous duplicate PATH environment keys; and
8. restores the caller's process environment after the command.

The launcher also accepts an explicit Tool parameter for swift-format,
sourcekit-lsp, lldb, and lldb-dap. Ordinary application stdout and stderr are
not repurposed as Orchard protocol transport.

## Installation

The supported automated installation channel for this milestone is the exact
Winget package:

    winget install --id Swift.Toolchain --exact --version 6.3.3 --source winget --silent --disable-interactivity --accept-source-agreements --accept-package-agreements

Winget verifies the package-manifest installer hash. Orchard then independently
verifies the installed executable hashes. A future offline installer must
verify the downloaded installer against the locked SHA-256 before execution.

Installing a toolchain changes the developer machine and must remain an
explicit user or administrator action. Repository verification never upgrades,
repairs, or installs a missing dependency.

## Upgrade procedure

A toolchain update is a reviewed platform change:

1. Open a dedicated qualification branch and record the candidate release,
   official distribution URL, installer hash, installed binary hashes, target,
   licenses, dependencies, SDKs, and release date.
2. Rebuild from a clean supported Windows image.
3. Run formatting, compiler, SwiftPM, protocol, transport, debugger, language
   server, deterministic-build, security, and native-process matrices.
4. Compare IR/protocol fixtures and explicitly version every incompatible
   change.
5. Run the maintained application and package corpus.
6. Complete open-source, security, clean-room, and release-engineering review.
7. Promote the lock and CI image together. Do not mutate a released lock.
8. Retain the previous supported profile for the published overlap window and
   document rollback.

## Remaining release gates

The current lock proves local selection and native compilation; it does not by
itself authorize product distribution. Packaging remains blocked until:

- the full transitive license and notice inventory is approved;
- the configured clean-machine CI job succeeds;
- real SourceKit-LSP and LLDB/DAP workflow probes pass;
- signed installer, upgrade, rollback, uninstall, and offline tests pass;
- an SBOM and provenance attestation include every shipped tool and runtime;
- x64 and ARM64 release profiles have separate evidence; and
- security and legal owners sign the applicable phase gate.
