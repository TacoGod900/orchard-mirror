# Security policy

Project Orchard is an early proof of concept. It has not received a production security audit and must not be used as a security boundary or for processing untrusted projects in a privileged environment.

## Reporting a vulnerability

Do not disclose a suspected vulnerability in a public issue, discussion, pull request, sample, or test case.

When this repository is hosted on GitHub with private vulnerability reporting enabled, use **Security → Report a vulnerability**. If that option is unavailable, contact the maintainers through the private channel by which you received access and request a secure reporting route. Include only enough non-sensitive detail in the first message to identify the affected Orchard component.

A useful report contains:

- affected revision and environment;
- impact and realistic attack scenario;
- minimal reproduction steps or a private proof of concept;
- whether untrusted project content, a local attacker, or a remote service is required;
- suggested mitigations, if known.

Maintainers should acknowledge a complete private report within five business days, provide a triage result or request for more information within ten business days, and coordinate disclosure after a fix is available. These are best-effort targets while the project is pre-release.

## Supported versions

There are no supported production releases yet. Security fixes are made on the current development line. Old commits, local forks, generated artifacts, and unofficial packages are not maintained.

## Current trust model

The current bootstrap reads project manifests and SwiftUI-shaped source as untrusted data and writes JSON IR. It does not invoke a Swift compiler or execute arbitrary source as Swift. The preview does interpret a narrowly defined set of action patterns; this must not be expanded into general evaluation, dynamic compilation, reflection-based activation, or shell execution.

Important current boundaries include:

- Project and entry-point paths must remain inside the selected project directory.
- CLI child commands must use argument-list APIs rather than shell-built command strings.
- IR node names and properties must map through explicit allowlists; they must never name arbitrary .NET types or executable commands.
- Diagnostics and IR can contain absolute local paths. Review them before sharing.
- The preview is a desktop process running with the current user's privileges and is not sandboxed.

## Native-Swift milestone requirements

The planned native-Swift preview will execute developer code. It may not ship as a public preview until it has:

- a documented threat model and trust prompt;
- a separate child process for application code;
- authenticated current-user local IPC with bounded framing and strict message validation;
- process-tree containment and reliable termination;
- time, memory, node-count, recursion-depth, and message-size limits;
- crash-loop backoff and a host that survives child failure;
- canonical project, asset, toolchain, and staging paths;
- no implicit forwarding of host secrets or credentials;
- dependency checksums, SBOM data, and release provenance;
- malformed-client, path-traversal, resource-exhaustion, and process-lifecycle tests.

The local preview must not be presented as a sandbox for malicious Swift packages. Package manifests and build plugins can execute code during normal toolchain operations and require an explicit trust decision.

## Dependency and release security

- The required .NET SDK is pinned in both `global.json` and the Orchard toolchain lock.
- No Swift distribution is currently pinned or downloaded by repository scripts.
- New external dependencies require an owner, an exact version, license review, vulnerability review, and lock-file/SBOM updates.
- CI and release automation should use least-privilege permissions and must not expose secrets to pull-requested code.
- Public installers and binaries require code signing, provenance, checksum publication, clean-machine verification, and upgrade/uninstall tests.
- Never commit credentials, signing certificates, private keys, provisioning data, `.env` files, crash dumps, or production validation data.

## Clean-room and proprietary material

Do not submit Apple binaries, SDK content, operating-system images, confidential materials, unlawfully redistributed headers, decrypted assets, proprietary fonts or icons, disassembly, or code derived from non-public implementations. Treat an uncertain provenance issue as both a legal and a supply-chain security concern and report it privately.

