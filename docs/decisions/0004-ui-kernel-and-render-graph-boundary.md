# ADR 0004: Separate framework semantics from an immutable render graph

Status: Proposed  
Date: 2026-07-18  
Decision owners: UI Kernel and Graphics  
Required approvers: Architecture, Accessibility, Performance, and Clean-Room Governance

## Context

The bootstrap preview maps a small source-shaped tree to WinForms controls. That is useful evidence, but native Windows widgets cannot define source-compatible UIKit or SwiftUI behavior. They differ in layout, text, focus, accessibility, animation, lifecycle, styling, event ordering, and pixel output. Sending public framework objects directly to the renderer would also couple API compatibility to a graphics implementation and expose too much application authority.

## Decision

Orchard will use four distinct layers:

1. independently implemented public framework adapters;
2. an Orchard UI kernel that owns identity, state transactions, environment, layout, focus, gestures, lifecycle, accessibility semantics, and animation intent;
3. immutable versioned render and semantics snapshots;
4. renderer backends that consume snapshots and return presentation/input timing, never framework objects.

SwiftUI-compatible and UIKit-compatible adapters may depend on the UI kernel. The UI kernel cannot depend on either adapter. The production renderer cannot execute application closures, access application files or credentials, call cloud services, or make compatibility decisions.

Each published frame carries a monotonic revision and contains stable node/resource IDs, typed properties, resolved geometry, clip/transform/opacity, draw operations, semantics nodes, hit-test regions, animation transactions, and explicit resource references. Input is returned as semantic events against an exact published revision. Stale events fail closed or are explicitly re-hit-tested according to a versioned policy; they are never silently delivered to a new closure at a reused position.

Large resources may be transferred by digest-bound shared buffers after control-channel authorization. A snapshot is immutable after publication. Renderer caches are disposable derived state and cannot become the source of framework truth.

## Renderer strategy

- Direct3D and DirectComposition are the interactive Windows path.
- DirectWrite provides text shaping and metrics under a documented legal font/fallback policy.
- A deterministic software renderer is mandatory for conformance fixtures and headless CI.
- GPU loss, device reset, malformed graphs, resource exhaustion, and renderer crashes are isolated from the application and device shell where Windows permits.
- Screenshots record backend, device profile, scale, color space, font set, profile version, and normalization policy.

## Compatibility and accessibility

Public behavior is qualified against approved clean-room specifications, not against incidental Windows widget behavior. Layout, visual, lifecycle, input, and accessibility qualification are separate dimensions. A visual match cannot compensate for an incorrect accessibility tree, focus order, or event sequence.

System-controlled Apple surfaces and protected assets are not reproduced by copying them. Where a lawful local representation cannot be qualified, the capability is simulated, validation-only, or unsupported and is reported as such.

## Alternatives considered

- **Wrap WinForms/WPF/WinUI controls directly:** rejected as the production semantic model; retained only as bootstrap scaffolding.
- **Render inside the application process:** rejected because GPU/decoder faults and application authority would share a trust boundary.
- **Serialize SwiftUI/UIKit object graphs:** rejected because public object layout is not the contract and closures/state cannot safely cross the boundary.
- **Pixel streaming from Apple hardware:** rejected as the local runtime; it remains an optional validation artifact only.

## Exit criteria

Acceptance requires a typed render-graph schema, reference interpreter, deterministic software backend, D3D/DirectComposition spike, DirectWrite/IME spike, revision-bound input, accessibility tree, resource lifecycle, graph fuzzer, GPU reset tests, screenshot normalization, performance budgets, and at least one approved framework behavior traced end to end from specification to rendered and accessible output.

