# Project Orchard threat model

Status: baseline; every major subsystem requires a deeper model before external release.

## 1. Protected assets

- Developer source code, assets, tests, package credentials, and intellectual property.
- Local workstation files, credentials, browser sessions, network access, camera, microphone, clipboard, and devices.
- Orchard toolchain, runtime, compatibility database, update/signing keys, and release provenance.
- Customer signing identities, App Store Connect credentials, provisioning data, and release artifacts.
- Tenant source, caches, logs, screenshots, recordings, and physical-device state in the validation service.
- Clean-room evidence, legal decisions, and access/audit records.

## 2. Trust boundaries

```text
Untrusted project/package source
        ↓
Build worker / compiler process
        ↓ signed protocol
Application sandbox ↔ capability broker ↔ Windows host resources
        ↓
Device shell / debugger / IDE

Customer workstation ↔ authenticated control plane ↔ tenant scheduler
                                                ↓
                                     ephemeral Apple worker
                                                ↓
                                   signing service / device lab
                                                ↓
                                      encrypted result storage
```

Project code and third-party build tools are arbitrary executable code. The developer intentionally authorizes local execution when building, but Orchard must minimise accidental access and make capability escalation visible. Validation jobs are hostile multi-tenant workloads even when the customer is authenticated.

## 3. Principal threats and controls

### Malicious local project or package

Threats: filesystem or credential theft, persistence, host process escape, resource exhaustion, debugger abuse, malicious compiler plugins, symlink/reparse traversal, and deceptive permission prompts.

Controls:

- low-privilege AppContainer or equivalent process token;
- per-app filesystem root and canonical-path/reparse-point enforcement;
- brokered file, network, location, media, clipboard, notification, and host-launch capabilities;
- Job Object process, CPU, memory, handle, and child-process limits;
- no inherited secrets or broad environment by default;
- argument-safe process launch without shell interpolation;
- signed compiler/plugin policy and explicit developer consent for arbitrary build scripts;
- clear distinction between deterministic simulation and host-resource access;
- full process-tree termination and reset.

### Malformed application IR or simulator protocol

Threats: parser denial of service, allocation bombs, deep recursion, type confusion, arbitrary type/assembly loading, command execution, replay, and cross-session event injection.

Controls:

- authenticated per-session named pipe with current-user ACL;
- version negotiation, monotonic revisions/sequences, and session binding;
- framed messages capped initially at 4 MiB;
- maximum tree depth 128, maximum 5,000 nodes for the first profile, bounded strings and collections;
- unique node identifiers and allowlisted node/property/event types;
- no arbitrary CLR type, assembly, path, URL, executable, or script names in IR;
- stale-event rejection, handshake timeout, render timeout, and crash backoff;
- fuzzing and malformed-client suites.

### Toolchain or updater compromise

Threats: dependency substitution, build-worker compromise, signing-key theft, downgrade, malicious update, and compromised package mirror.

Controls:

- pinned source and binary hashes, reproducible builds where practical, and SLSA-aligned provenance;
- offline release root, scoped intermediates, dual control, rotation, and revocation;
- signed manifests and artifacts verified before execution;
- transparent release log and SBOM;
- staged update rings, anti-downgrade policy with explicit recovery path, and tested rollback;
- independent verification builders and dependency review SLAs.

### Validation tenant escape

Threats: reading another tenant’s source/cache/artifact, attacking the control plane, persisting on Mac workers or devices, exfiltrating credentials, mining shared dependency data, and abusing outbound network access.

Controls:

- ephemeral worker per job or an independently approved isolation boundary;
- tenant-scoped encryption and cache policy;
- minimal worker identity, authenticated job manifest, restricted egress, and no control-plane credentials on workers;
- immutable base images, verified teardown, device erase/health checks, and periodic rebuild;
- malware/policy scan at admission and artifact scan at egress;
- private pools for regulated tenants;
- external penetration tests, tenant-escape exercises, compromised-worker drills, and zero-tolerance cross-tenant SLO.

### Signing and publishing compromise

Threats: unapproved signing, key export, credential leakage in logs/cache, artifact substitution, confused-deputy submission, and privilege abuse by employees.

Controls:

- customer-owned keys where possible; KMS/HSM-backed wrapping otherwise;
- per-job short-lived authorization and separation of build, sign, and submit roles;
- signing only against an immutable source/build manifest;
- non-exportable or minimally exposed key handling on the assigned Apple worker;
- explicit second confirmation before App Store submission;
- attributable tamper-evident audit event for every secret use and signing operation;
- just-in-time privileged access, dual control for administrative operations, revocation and deletion workflows.

### Telemetry, diagnostics, and support leakage

Threats: source paths, user content, tokens, screenshots, crash memory, PII, minors’ data, and enterprise secrets entering logs or support systems.

Controls:

- telemetry off by default in the bootstrap and consented/configurable thereafter;
- structured allowlisted fields, path hashing/redaction, secret scanning, bounded retention;
- local preview of diagnostic bundle content before upload;
- no raw memory dumps by default;
- tenant-region and deletion policy enforcement;
- enterprise disable/export controls and role-restricted support access.

### Clean-room contamination

Threats: restricted Apple material reaches implementation code, build artifacts, prompts, search indexes, or generated tests.

Controls: the organisational and technical separation in `CLEAN_ROOM_AND_LEGAL.md`, immutable provenance, restricted evidence stores, artifact scanning, contributor attestations, quarantine, and independent reimplementation.

## 4. Required security gates

- Threat model approved before each public subsystem design freezes.
- Static analysis, dependency scanning, secret scanning, fuzzing, and hardening checks in CI.
- Sandbox and broker penetration test before private alpha.
- Updater, signing, and validation-service external penetration test before public beta.
- Red-team exercises for malicious packages, tenant escape, credential theft, compromised worker, update compromise, insider signing abuse, deletion failure, and region loss before GA.
- PSIRT, vulnerability intake, severity model, security advisory, supported-version, and emergency release procedures staffed before beta.
- No known critical vulnerability or unmitigated high-severity exploit path at release.

## 5. Open design questions

- Exact Windows AppContainer capability model for local app and compiler plugin processes.
- Whether the renderer runs in the application, a dedicated GPU process, or both by profile.
- Permitted hosted Apple worker isolation mechanisms and licence constraints.
- Customer signing-key custody model by service tier and jurisdiction.
- Shared public package cache design that cannot leak private dependency names or timing.
- Remote debugger and team collaboration authentication protocol.
- Data residency, backup deletion, legal hold, and school/minor-data product boundaries.

Each question has an owner and a resolution gate in the master execution plan; none may silently become a default production assumption.
