# Project Orchard clean-room and legal operating model

Status: mandatory program control; counsel approval required before compatibility implementation expands beyond independently authored bootstrap code.

This document is an engineering control model, not legal advice. External intellectual-property, licensing, privacy, employment, export, and platform counsel must approve the final policy in each operating jurisdiction. If counsel prohibits an activity, this document does not authorize it.

## 1. Non-negotiable product boundary

The Windows distribution may contain only Orchard-owned code and assets, approved third-party open-source components, and redistributable tools whose licence permits the intended commercial use. It must not contain or extract:

- Apple SDK binaries, frameworks, simulator runtimes, operating-system images, device images, or signing components;
- proprietary Apple fonts, symbols, icons, artwork, sounds, or UI resources;
- Xcode components or files copied from an Apple installation;
- Mach-O frameworks or iOS application binaries offered as locally executable content;
- confidential material, leaked source, material obtained in breach of contract, or implementation notes derived from such material;
- credentials, provisioning profiles, signing identities, or customer secrets in source control, shared caches, logs, telemetry, crash dumps, or test fixtures.

The local product recompiles customer source for a Windows execution target against independently implemented Orchard modules. Official Apple builds, signing, device tests, and submission remain on permitted Apple infrastructure.

## 2. Organisational separation

Four roles are separated by access controls and reporting procedures.

### Specification team

The specification team produces approved behavioural contracts from counsel-approved sources. It records inputs, outputs, error behaviour, lifecycle transitions, timing tolerances, threading rules, accessibility semantics, and version availability. It does not write implementation code.

### Implementation team

The implementation team receives only approved specifications, test vectors, public Orchard contracts, and cleared dependencies. It must not receive raw observation notebooks, Apple binaries, decompiled material, screenshots containing restricted assets, or confidential information.

### Conformance operations

Conformance operations run approved probes on permitted Apple environments and Orchard. They normalise results before transfer. Access to the Apple environment does not automatically grant access to the Orchard implementation repository.

### Legal and provenance authority

The authority classifies sources and experiments, approves transfers, audits access, quarantines questionable material, and signs release gates. It can block a release independently of schedule or commercial pressure.

Small teams may not collapse these roles without written counsel approval and an equivalent control design.

## 3. Repository and identity boundaries

Production deployment uses separate systems of record:

| System | Contains | Implementation access |
| --- | --- | --- |
| `orchard-specifications` | Approved behavioural specifications and provenance IDs | Read-only after approval |
| `orchard-observations` | Raw probes and restricted evidence | Denied by default |
| `orchard-conformance` | Normalised test vectors and comparison tooling | Approved subsets only |
| `orchard-platform` | Compiler, runtime, frameworks, device host, tools | Normal engineering access |
| `orchard-legal-register` | Decisions, exceptions, attestations, audits | Legal/provenance authority |

Service accounts, CI credentials, backups, search indexes, AI coding tools, chat systems, and analytics exports must preserve the same separation. Repository separation without data-plane separation is not a clean-room control.

## 4. Source classification

Counsel maintains a versioned permitted-input matrix. Every source is classified before use.

| Class | Example | Default disposition |
| --- | --- | --- |
| A | Orchard-authored public requirements | Permitted |
| B | Approved permissive open-source dependency | Permitted with licence/provenance record |
| C | Public platform documentation | Legal review required for specification use |
| D | Observable behaviour from an approved black-box probe | Legal and experiment approval required |
| E | Public talks, samples, headers, interface metadata, bug reports | Individually classified; public availability is not automatic permission |
| F | Proprietary binary, SDK, system image, font, asset, confidential or leaked material | Prohibited unless counsel creates a narrowly scoped written exception |

Every behavioural specification includes:

- stable specification ID and revision;
- API symbol and compatibility-profile version;
- source classification and provenance references;
- author, reviewer, legal approver, and approval timestamp;
- permitted implementation detail level;
- test-vector identifiers and permitted tolerances;
- expiration or re-review trigger;
- contamination notes and linked exceptions.

## 5. API specification workflow

1. Product analytics selects an API or workflow from the consented target corpus.
2. Legal classifies proposed sources and experiments.
3. The specification team drafts an observation plan before running it.
4. Conformance operations execute deterministic probes in approved environments.
5. Raw results enter the restricted evidence store.
6. The specification team writes a behaviour-only contract.
7. Legal/provenance review rejects or approves the transfer.
8. Approved specifications and normalised vectors are published to the implementation boundary.
9. The implementation team independently designs and codes the behaviour.
10. CI runs Orchard tests and separately scheduled differential tests.
11. A support status is promoted only after the API definition of done passes.

The implementation team must never be asked to “make it look like this decompiled code,” reproduce private layout constants from restricted material, or copy a platform-owned asset.

## 6. Contamination response

Anyone can open a contamination incident. The response is immediate:

1. Stop access and automated propagation of the suspect material.
2. Preserve an immutable audit record without spreading the content.
3. Identify repositories, caches, build artefacts, logs, backups, model prompts, and people exposed.
4. Revoke affected credentials and quarantine derived changes.
5. Legal determines whether code, tests, specifications, or staff assignments require removal or re-performance.
6. A non-exposed team recreates affected work from approved inputs when required.
7. Security verifies deletion from active systems; backup retention is documented.
8. The authority records the disposition, corrective actions, and release impact.

No employee is penalised for good-faith reporting. Concealing or informally “cleaning up” contamination is a release-blocking process failure.

## 7. Automated controls

CI and release engineering must provide:

- licence, copyright, binary signature, archive-content, high-entropy secret, and prohibited-file scanning;
- SBOM generation and dependency provenance attestations;
- allowlisted assets and font inventory;
- signed, immutable build inputs and artifact manifests;
- repository access logs retained according to counsel-approved policy;
- approval checks that link every promoted compatibility symbol to a specification revision;
- a hard failure for unclassified external fixtures;
- a hard failure when a Windows release contains an Apple binary signature, Mach-O object, mobile provisioning profile, or prohibited path pattern;
- human legal review for scanner exceptions.

Scanners support but do not replace organisational separation or counsel review.

## 8. Mandatory legal gates

| Gate | Required decision | Blocks |
| --- | --- | --- |
| L0 | Product naming, marketing, and “simulator” terminology | External naming |
| L1 | Source-compatible public API declarations | Framework implementation |
| L2 | Permitted behavioural observation and benchmarking | Conformance factory |
| L3 | Open-source compiler/runtime licences and distribution | Toolchain preview |
| L4 | Hosted Xcode, Simulator, and physical-device commercial model | Validation pilot |
| L5 | Signing identity custody and App Store Connect authorization | Signed builds |
| L6 | Clean-room audit of private alpha artefacts | Private alpha |
| L7 | Jurisdictional privacy, employment, export, and customer terms | Public beta |
| L8 | Independent final release opinion | GA |

An unapproved gate cannot be waived by engineering. If L1 or L2 fails, the planned pivot is an Orchard-native Swift UI platform with source migration tooling and an official remote-build lane.

## 9. Contributor and supplier controls

Employees, contractors, vendors, and external contributors must:

- sign invention-assignment and confidentiality agreements appropriate to their role;
- attest to conflicts, prior exposure, and prohibited-source rules before access;
- complete annual clean-room and secure-development training;
- disclose relevant prior work before joining a compatibility implementation lane;
- use only approved development and collaboration systems;
- attach provenance to new external code, assets, fixtures, or generated material;
- accept quarantine and independent reimplementation when exposure creates risk.

Third-party libraries require licence review, security review, maintenance ownership, version pinning, SBOM inclusion, and a replacement plan. Copyleft obligations and patent clauses are evaluated against the distribution model before adoption.

## 10. Claims and customer communication

Marketing claims must name the Orchard profile, corpus, measurement date, and meaning. “90% local compatibility” is prohibited unless it means a disclosed weighted result over a defined application/workflow corpus.

Customer-facing statuses are:

1. **Local compatible** — implemented and qualified for the named profile.
2. **Local simulated** — deterministic development behaviour with mandatory official validation for production semantics.
3. **Apple validation required** — no local production-equivalent implementation.
4. **Unavailable** — rejected before execution with a diagnostic and migration guidance.

Silent no-ops and false-success responses are prohibited. Production readiness is honest routing as much as it is API breadth.

