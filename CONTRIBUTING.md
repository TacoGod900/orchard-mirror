# Contributing to Project Orchard

Thank you for helping build Orchard. The project is currently establishing a trustworthy engineering baseline, so correctness, provenance, and honest capability reporting matter more than API breadth.

## Before starting

Read:

- [README.md](README.md) for the current workflow and limitations;
- [docs/CURRENT_STATUS.md](docs/CURRENT_STATUS.md) for implemented and planned architecture;
- [SECURITY.md](SECURITY.md) for reporting and trust boundaries;
- [schemas/orchard-ir-v0.1.schema.json](schemas/orchard-ir-v0.1.schema.json) before changing serialized models.

Do not begin compatibility work using proprietary or confidential implementation material. Ask maintainers privately if provenance is uncertain.

## Development setup

Install the .NET SDK selected by `global.json`. Swift is optional for the current bootstrap and must not be added as an unpinned build prerequisite.

Run the same check used by CI:

```powershell
pwsh -NoProfile -File .\eng\check.ps1
```

For a faster local iteration after a successful full check:

```powershell
dotnet build .\Orchard.slnx
dotnet run --project .\tests\Orchard.Tests\Orchard.Tests.csproj
```

The test project is a dependency-free executable harness rather than an adapter for `dotnet test`. Add each test to its named test table and make failures deterministic and self-describing.

## Change workflow

1. Start from a clean, buildable baseline.
2. Keep a change focused on one behavior or contract.
3. Add or update tests before calling the change complete.
4. Update schemas and documentation when externally observable behavior changes.
5. Run `eng/check.ps1` from the repository root.
6. Review generated files, local paths, secrets, and third-party material before submitting.

Do not commit `artifacts/`, `bin/`, `obj/`, IDE state, dumps, traces, credentials, signing material, or locally generated application IR.

## Coding expectations

- Preserve nullable analysis and warnings-as-errors.
- Prefer explicit data contracts and deterministic output.
- Use ordinal comparisons for protocol identifiers and paths only with the appropriate platform-aware comparison.
- Avoid shell interpolation. Use `ProcessStartInfo.ArgumentList` or equivalent structured APIs.
- Resolve and validate paths before file operations.
- Bound input size, recursion, collections, and process waits where untrusted data is involved.
- Keep UI framework details out of shared IR contracts.
- Include actionable error codes and remediation when adding diagnostics.
- Keep accessibility behavior part of control implementation and tests.
- Avoid silent fallbacks that make unsupported behavior appear compatible.

## Tests required by change type

### Lexer or parser changes

Add positive and negative cases, source-location assertions where relevant, and a regression test for malformed input. Remember that the current parser is a subset parser; accepting syntax without modeling its meaning can be worse than an explicit diagnostic.

### Runtime or control changes

Test state effects, disabled behavior, accessibility metadata, unsupported fallbacks, and re-render behavior. Do not infer production iOS fidelity from a Windows control mapping.

### Compatibility-catalogue changes

Document the public observation or specification that supports the classification. Add a test for the symbol and describe important behavioral gaps. A compatibility percentage must remain an engineering hint, never a conformance claim.

### IR changes

Update all of the following together:

- `Orchard.Core` models and serialization tests;
- the versioned JSON Schema;
- checked-in or generated fixtures;
- CLI inspect/build behavior where applicable;
- `docs/CURRENT_STATUS.md` and migration notes.

Adding an optional backward-compatible field still requires tests. Renaming, removing, changing meaning, or changing the type of a field requires a new schema version. Never silently reuse `0.1.0` for an incompatible document.

### Toolchain or dependency changes

Update the toolchain lock and source-of-truth configuration in the same change. External packages require exact versions, lock files, license and vulnerability review, notices, and SBOM metadata. A Swift distribution additionally requires an official source location and SHA-256 before CI or bootstrap scripts may download it.

## Clean-room contribution rules

Allowed inputs include independently written code, public Swift language material under applicable terms, documented public APIs, and black-box behavioral observations collected through lawful use.

Do not contribute:

- Apple SDK, framework, simulator, or operating-system binaries;
- copied non-redistributable headers or generated metadata dumps;
- decrypted or extracted system assets;
- proprietary fonts, icons, device artwork, or trade dress;
- disassembly or decompiled implementation code;
- material learned through confidentiality obligations;
- code copied from a project whose license is incompatible or unknown.

For future clean-room specifications, record the observer, date, public API under test, toolchain/device version, inputs, observable outputs, and source provenance. Implementation authors should work from approved behavioral specifications rather than proprietary implementation material.

## Pull request description

Describe:

- the problem and intended outcome;
- the chosen design and alternatives considered;
- tests run and their results;
- user-visible limitations or compatibility impact;
- schema, dependency, security, accessibility, and provenance implications;
- follow-up work intentionally left out.

Screenshots are useful for Windows preview changes but must not contain private data and must not be described as iOS reference output.

## Security fixes

Do not open a public pull request for an unfixed vulnerability before coordinating with maintainers. Follow [SECURITY.md](SECURITY.md).

