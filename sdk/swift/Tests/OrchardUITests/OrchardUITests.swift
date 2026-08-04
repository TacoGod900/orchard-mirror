import OrchardProtocol
@testable import OrchardUI
import Testing

@MainActor
@Suite("OrchardUI reconciled rendering")
struct OrchardUITests {
  @Test("A semantic hierarchy has deterministic structural IDs")
  func deterministicHierarchy() throws {
    let first = try OrchardRenderer.render(StaticRoot(), metadata: metadata)
    let second = try OrchardRenderer.render(StaticRoot(), metadata: metadata)

    #expect(try OrchardJSON.encode(first) == OrchardJSON.encode(second))
    #expect(first.rootView.flattened.map(\.id) == second.rootView.flattened.map(\.id))
    #expect(Set(first.rootView.flattened.map(\.id)).count == first.rootView.flattened.count)
    #expect(first.rootView.type == "NavigationStack")
    #expect(first.rootView.children.first?.type == "VStack")
    #expect(first.compatibility.localCompatibilityPercent < 100)
    #expect(first.compatibility.apiUsages.contains { $0.status == .partial })
  }

  @Test("Modifiers retain source order and semantic arguments")
  func modifierOrder() throws {
    let application = try OrchardRenderer.render(StaticRoot(), metadata: metadata)
    let stack = try #require(application.rootView.children.first)

    #expect(stack.modifiers.map(\.name) == ["padding", "navigationTitle"])
    #expect(stack.modifiers[0].arguments["_0"] == "24")
    #expect(stack.modifiers[1].arguments["_0"] == "Native Orchard")

    let title = try #require(stack.children.first)
    #expect(title.modifiers.map(\.name) == ["font", "foregroundStyle"])
    #expect(title.modifiers[0].arguments["_0"] == ".largeTitle")
    #expect(title.modifiers[1].arguments["_0"] == ".green")
  }

  @Test("Root builder supports expressions, conditionals, and bounded ForEach content")
  func rootBuilderControlFlow() throws {
    let shown = try OrchardRenderer.render(
      RootBuilderRoot(showExtra: true, values: ["One", "Two"]),
      metadata: metadata
    )
    let hidden = try OrchardRenderer.render(
      RootBuilderRoot(showExtra: false, values: ["One", "Two"]),
      metadata: metadata
    )

    #expect(shown.rootView.type == "Group")
    #expect(
      shown.rootView.flattened.compactMap { $0.arguments["_0"] }
        == ["Header", "Extra", "One", "Two"])
    #expect(
      hidden.rootView.flattened.compactMap { $0.arguments["_0"] }
        == ["Header", "One", "Two"])

    let shownOne = try #require(shown.rootView.flattened.first { $0.arguments["_0"] == "One" })
    let hiddenOne = try #require(hidden.rootView.flattened.first { $0.arguments["_0"] == "One" })
    #expect(shownOne.id == hiddenOne.id)
  }

  @Test("State bindings accept change events and actions publish new revisions")
  func stateBindingAndEventDispatch() throws {
    let session = RenderSession(root: StatefulRoot(), metadata: metadata)
    let initial = try session.render()

    #expect(initial.revision == 1)
    let nameState = try #require(
      initial.application.state.first {
        $0.name.hasPrefix("name_") && $0.kind == .text
      })
    #expect(nameState.initialValue == "")
    #expect(
      initial.application.state.contains(
        StateDefinition(name: "notificationsEnabled", kind: .flag, initialValue: "true")))

    let initialField = try node(type: "TextField", in: initial)
    #expect(initialField.arguments["text"] == "$\(nameState.name)")
    #expect(initialField.arguments["value"] == "")
    #expect(initialField.events.map(\.name) == ["change"])

    let changed = try session.dispatch(
      revision: initial.revision,
      nodeID: initialField.id,
      event: "change",
      value: "Ada"
    )
    #expect(changed.revision == 2)
    #expect(try node(type: "TextField", in: changed).arguments["value"] == "Ada")
    #expect(
      changed.application.state.first { $0.name == nameState.name }?.initialValue == "")

    let button = try node(type: "Button", in: changed)
    #expect(button.events.map(\.name) == ["press"])
    let updated = try session.dispatch(
      revision: changed.revision,
      nodeID: button.id,
      event: "press"
    )
    #expect(updated.revision == 3)
    #expect(try node(type: "TextField", in: updated).arguments["value"] == "Orchard Developer")
    #expect(try node(type: "Toggle", in: updated).arguments["value"] == "false")
    #expect(
      updated.application.state.first { $0.name == "notificationsEnabled" }?.initialValue
        == "true")
  }

  @Test("A delayed event is rejected before handler lookup")
  func staleEventRejection() throws {
    let session = RenderSession(root: CounterRoot(), metadata: metadata)
    let initial = try session.render()
    let button = try node(type: "Button", in: initial)
    let updated = try session.dispatch(
      revision: initial.revision,
      nodeID: button.id,
      event: "press"
    )

    #expect(updated.revision == 2)
    #expect(throws: EventDispatchError.staleRevision(expected: 2, received: 1)) {
      try session.dispatch(revision: initial.revision, nodeID: button.id, event: "press")
    }
    #expect(session.revision == 2)
    #expect(updated.application.rootView.flattened.contains { $0.arguments["_0"] == "Count 1" })
  }

  @Test("Conditional insertion does not shift an unaffected sibling identity")
  func conditionalIdentityShift() throws {
    let session = RenderSession(root: ConditionalIdentityRoot(), metadata: metadata)
    let initial = try session.render()
    let stableBefore = try button(titled: "Stable", in: initial)
    let toggle = try button(titled: "Toggle extra", in: initial)

    let inserted = try session.dispatch(
      revision: initial.revision,
      nodeID: toggle.id,
      event: "press"
    )
    let stableAfter = try button(titled: "Stable", in: inserted)
    #expect(stableAfter.id == stableBefore.id)
    #expect(inserted.application.rootView.flattened.contains { $0.arguments["_0"] == "Inserted" })
  }

  @Test("Explicit keys preserve node and child state through reordering")
  func keyedReordering() throws {
    let session = RenderSession(root: ReorderRoot(), metadata: metadata)
    let initial = try session.render()
    let stateNamesBefore = Set(initial.application.state.map(\.name))
    let aBefore = try button(titled: "A 0", in: initial)

    let incremented = try session.dispatch(
      revision: initial.revision,
      nodeID: aBefore.id,
      event: "press"
    )
    let reverse = try button(titled: "Reverse", in: incremented)
    let reordered = try session.dispatch(
      revision: incremented.revision,
      nodeID: reverse.id,
      event: "press"
    )

    let aAfter = try button(titled: "A 1", in: reordered)
    #expect(aAfter.id == aBefore.id)
    #expect(Set(reordered.application.state.map(\.name)) == stateNamesBefore)
    let itemTitles = reordered.application.rootView.flattened.compactMap { node -> String? in
      guard node.type == "Button", let title = node.arguments["_0"], title != "Reverse" else {
        return nil
      }
      return title
    }
    #expect(itemTitles == ["B 0", "A 1"])
  }

  @Test("Nested state survives conditional removal and reinsertion")
  func nestedConditionalState() throws {
    let session = RenderSession(root: ConditionalChildRoot(), metadata: metadata)
    let initial = try session.render()
    let child = try button(titled: "Child 0", in: initial)
    let incremented = try session.dispatch(
      revision: initial.revision,
      nodeID: child.id,
      event: "press"
    )

    let hide = try button(titled: "Show or hide", in: incremented)
    let hidden = try session.dispatch(
      revision: incremented.revision,
      nodeID: hide.id,
      event: "press"
    )
    #expect(hidden.application.rootView.flattened.allSatisfy { $0.arguments["_0"] != "Child 1" })

    let show = try button(titled: "Show or hide", in: hidden)
    let restored = try session.dispatch(
      revision: hidden.revision,
      nodeID: show.id,
      event: "press"
    )
    #expect(try button(titled: "Child 1", in: restored).id == child.id)
  }

  @Test("Inferred state names are identity-derived and deterministic")
  func collisionFreeStateNames() throws {
    let first = try RenderSession(root: CollisionRoot(), metadata: metadata).render()
    let second = try RenderSession(root: CollisionRoot(), metadata: metadata).render()
    let names = first.application.state.map(\.name)
    #expect(names == second.application.state.map(\.name))
    #expect(Set(names).count == 3)
    #expect(names.allSatisfy { $0.contains("_") })
  }

  @Test("Duplicate explicit state names fail closed")
  func duplicateExplicitStateName() {
    let session = RenderSession(root: DuplicateNameRoot(), metadata: metadata)
    #expect(throws: RenderValidationError.duplicateExplicitStateName("same")) {
      try session.render()
    }
    #expect(session.revision == 0)
  }

  @Test("Invalid explicit state names fail closed")
  func invalidExplicitStateName() {
    let session = RenderSession(root: InvalidNameRoot(), metadata: metadata)
    #expect(throws: RenderValidationError.invalidStateName("bad-name")) {
      try session.render()
    }
    #expect(throws: RenderValidationError.invalidStateName("")) {
      try RenderSession(root: EmptyNameRoot(), metadata: metadata).render()
    }
  }

  @Test("Duplicate and unsafe view identities fail closed")
  func invalidViewIdentities() {
    #expect(
      throws: RenderValidationError.duplicateExplicitIdentity(
        "same".orchardViewIdentity)
    ) {
      try RenderSession(root: DuplicateIdentityRoot(), metadata: metadata).render()
    }
    #expect(throws: RenderValidationError.invalidExplicitIdentity("")) {
      try RenderSession(root: InvalidIdentityRoot(), metadata: metadata).render()
    }
  }

  @Test("Constant and custom bindings render and process change events")
  func constantAndCustomBindings() throws {
    let model = BindingModel()
    let session = RenderSession(root: BindingRoot(model: model), metadata: metadata)
    let initial = try session.render()
    let field = try node(type: "TextField", in: initial)
    let toggle = try node(type: "Toggle", in: initial)

    #expect(field.arguments["text"] == nil)
    #expect(field.arguments["value"] == "Initial")
    #expect(toggle.arguments["isOn"] == nil)
    #expect(toggle.arguments["value"] == "true")

    let changed = try session.dispatch(
      revision: initial.revision,
      nodeID: field.id,
      event: "change",
      value: "Custom"
    )
    #expect(model.text == "Custom")
    #expect(try node(type: "TextField", in: changed).arguments["value"] == "Custom")

    let unchanged = try session.dispatch(
      revision: changed.revision,
      nodeID: try node(type: "Toggle", in: changed).id,
      event: "change",
      value: "false"
    )
    #expect(try node(type: "Toggle", in: unchanged).arguments["value"] == "true")
  }

  @Test("Change handlers reject missing and malformed values without advancing")
  func changeValidation() throws {
    let session = RenderSession(root: StatefulRoot(), metadata: metadata)
    let initial = try session.render()
    let toggle = try node(type: "Toggle", in: initial)

    #expect(throws: EventDispatchError.missingValue(event: "change")) {
      try session.dispatch(revision: initial.revision, nodeID: toggle.id, event: "change")
    }
    #expect(
      throws: EventDispatchError.invalidValue(event: "change", value: "TRUE")
    ) {
      try session.dispatch(
        revision: initial.revision,
        nodeID: toggle.id,
        event: "change",
        value: "TRUE"
      )
    }
    #expect(session.revision == initial.revision)
  }

  @Test("Dispatch errors distinguish lifecycle, node, and event failures")
  func eventDispatchErrors() throws {
    let session = RenderSession(root: StatefulRoot(), metadata: metadata)
    #expect(throws: EventDispatchError.renderRequired) {
      try session.dispatch(revision: 0, nodeID: "missing", event: "press")
    }

    let initial = try session.render()
    #expect(throws: EventDispatchError.unknownNode("missing")) {
      try session.dispatch(revision: initial.revision, nodeID: "missing", event: "press")
    }
    let field = try node(type: "TextField", in: initial)
    #expect(throws: EventDispatchError.unknownEvent(nodeID: field.id, event: "press")) {
      try session.dispatch(revision: initial.revision, nodeID: field.id, event: "press")
    }
  }

  @Test("A failed replacement render rolls back state, new slots, and publication")
  func replacementRenderRollback() throws {
    let session = RenderSession(root: InvalidatingActionRoot(), metadata: metadata)
    let initial = try session.render()
    let retainedBefore = session.retainedStateSlotCount
    let trigger = try button(titled: "Break render", in: initial)

    #expect(
      throws: RenderValidationError.duplicateExplicitIdentity(
        "duplicate".orchardViewIdentity)
    ) {
      try session.dispatch(
        revision: initial.revision,
        nodeID: trigger.id,
        event: "press")
    }

    #expect(session.revision == initial.revision)
    #expect(session.phase == .idle)
    #expect(session.retainedStateSlotCount == retainedBefore)
    #expect(session.stateIdentityTombstoneCount == 0)

    let recovered = try session.render()
    #expect(recovered.revision == 2)
    #expect(recovered.application.rootView.flattened.contains { $0.arguments["_0"] == "Count 0" })
    #expect(recovered.application.rootView.flattened.allSatisfy { $0.arguments["_0"] != "Invalid" })
  }

  @Test("A throwing event handler rolls session state back")
  func throwingHandlerRollback() throws {
    let session = RenderSession(root: ThrowingHandlerRoot(), metadata: metadata)
    let initial = try session.render()
    let control = try #require(
      initial.application.rootView.flattened.first { $0.type == "ThrowingControl" })

    #expect(throws: TestEventError.failed) {
      try session.dispatch(
        revision: initial.revision,
        nodeID: control.id,
        event: "press")
    }
    #expect(session.revision == initial.revision)
    #expect(session.phase == .idle)

    let recovered = try session.render()
    #expect(recovered.application.rootView.flattened.contains { $0.arguments["_0"] == "Count 0" })
  }

  @Test("Synchronous session reentry fails closed and resets the phase")
  func synchronousReentry() throws {
    let harness = ReentryHarness()
    let session = RenderSession(root: ReentryRoot(harness: harness), metadata: metadata)
    let initial = try session.render()
    harness.attempt = {
      _ = try session.render()
    }
    let trigger = try button(titled: "Attempt reentry", in: initial)

    let updated = try session.dispatch(
      revision: initial.revision,
      nodeID: trigger.id,
      event: "press")
    #expect(
      harness.caughtError
        == .reentrantOperation(active: .dispatching, requested: .rendering))
    #expect(updated.revision == 2)
    #expect(session.phase == .idle)

    let renderingHarness = ReentryHarness()
    let renderingSession = RenderSession(
      root: RenderingReentryRoot(harness: renderingHarness),
      metadata: metadata)
    renderingHarness.attempt = {
      _ = try renderingSession.render()
    }
    #expect(try renderingSession.render().revision == 1)
    #expect(
      renderingHarness.caughtError
        == .reentrantOperation(active: .rendering, requested: .rendering))
    #expect(renderingSession.phase == .idle)
  }

  @Test("Invalid text changes are rejected before a custom setter")
  func textPayloadPrevalidation() throws {
    let model = BindingModel()
    let session = RenderSession(root: BindingRoot(model: model), metadata: metadata)
    let initial = try session.render()
    let field = try node(type: "TextField", in: initial)
    let invalid = "unsafe\u{0000}value"

    #expect(throws: EventDispatchError.invalidValue(event: "change", value: invalid)) {
      try session.dispatch(
        revision: initial.revision,
        nodeID: field.id,
        event: "change",
        value: invalid)
    }
    #expect(model.setterCalls == 0)
    #expect(model.text == "Initial")
    #expect(session.revision == initial.revision)
  }

  @Test("State retention and identity tombstones obey bounded LRU limits")
  func boundedStateRetention() throws {
    let model = ChurnModel()
    let limits = RenderSessionLimits(
      maximumRetainedStateSlots: 2,
      maximumStateIdentityTombstones: 2)
    let session = RenderSession(
      root: ChurnRoot(model: model),
      metadata: metadata,
      limits: limits)

    var snapshot = try session.render()
    let itemZero = try button(titled: "Item 0 count 0", in: snapshot)
    snapshot = try session.dispatch(
      revision: snapshot.revision,
      nodeID: itemZero.id,
      event: "press")
    #expect(try button(titled: "Item 0 count 1", in: snapshot).id == itemZero.id)

    model.current = 1
    snapshot = try session.render()
    model.current = 0
    snapshot = try session.render()
    #expect(try button(titled: "Item 0 count 1", in: snapshot).id == itemZero.id)

    model.current = 2
    snapshot = try session.render()
    #expect(session.retainedStateSlotCount == 2)
    #expect(session.stateIdentityTombstoneCount == 1)

    model.current = 1
    snapshot = try session.render()
    #expect(snapshot.application.rootView.flattened.contains { $0.arguments["_0"] == "Item 1 count 0" })

    for identity in 3...7 {
      model.current = identity
      snapshot = try session.render()
    }
    #expect(session.retainedStateSlotCount <= 2)
    #expect(session.stateIdentityTombstoneCount <= 2)
    #expect(snapshot.revision > 1)
  }

  @Test("State, node, tree-depth, and composition budgets fail before publication")
  func constructionBudgets() {
    let stateSession = RenderSession(
      root: TooManyStatesRoot(),
      metadata: metadata,
      limits: RenderSessionLimits(maximumRetainedStateSlots: 2))
    #expect(throws: RenderValidationError.stateSlotLimitExceeded(maximum: 2)) {
      try stateSession.render()
    }
    #expect(stateSession.retainedStateSlotCount == 0)

    let nodeSession = RenderSession(
      root: WideRoot(),
      metadata: metadata,
      limits: RenderSessionLimits(maximumNodes: 4))
    #expect(throws: RenderValidationError.nodeLimitExceeded(maximum: 4)) {
      try nodeSession.render()
    }
    #expect(nodeSession.revision == 0)

    let depthSession = RenderSession(
      root: DeepTreeRoot(),
      metadata: metadata,
      limits: RenderSessionLimits(maximumTreeDepth: 2))
    #expect(throws: RenderValidationError.treeDepthLimitExceeded(maximum: 2)) {
      try depthSession.render()
    }

    let compositionSession = RenderSession(
      root: CompositionChain(remaining: 5),
      metadata: metadata,
      limits: RenderSessionLimits(maximumCompositionDepth: 3))
    #expect(throws: RenderValidationError.compositionDepthLimitExceeded(maximum: 3)) {
      try compositionSession.render()
    }
  }

  @Test("An existing state slot rejects declaration-name changes")
  func stateNameChangeRevalidation() throws {
    let model = DynamicNameModel()
    let session = RenderSession(root: DynamicNameRoot(model: model), metadata: metadata)
    let initial = try session.render()

    model.name = "second"
    do {
      _ = try session.render()
      Issue.record("Expected a state-slot name change to fail.")
    } catch let error as RenderValidationError {
      guard case .stateSlotNameChanged(_, let expected, let received) = error else {
        Issue.record("Unexpected render error: \(error)")
        return
      }
      #expect(expected == "first")
      #expect(received == "second")
    }
    #expect(session.revision == initial.revision)
    #expect(session.phase == .idle)

    model.name = "bad-name"
    #expect(throws: RenderValidationError.invalidStateName("bad-name")) {
      try session.render()
    }

    model.name = "first"
    #expect(try session.render().revision == 2)
  }

  @Test("Requested framework symbols contribute truthful remote compatibility")
  func requestedSourceCapabilities() throws {
    let remoteMetadata = ApplicationMetadata(
      applicationID: "dev.orchard.tests",
      displayName: "OrchardUI Tests",
      sourceFile: "OrchardUITests.swift",
      compiledAtUTC: "2026-07-18T00:00:00Z",
      requestedSourceSymbols: ["ARKit", "ARKit"])
    let remote = try OrchardRenderer.render(Text("Remote"), metadata: remoteMetadata)
    #expect(remote.compatibility.requiredRemoteCapabilities == ["ARKit"])
    #expect(
      remote.compatibility.apiUsages.contains {
        $0.symbol == "ARKit" && $0.category == "framework" && $0.status == .remoteOnly
      })
    #expect(remote.compatibility.localCompatibilityPercent < 100)

    let local = try OrchardRenderer.render(Text("Local"), metadata: metadata)
    #expect(local.compatibility.apiUsages.allSatisfy { $0.category != "framework" })
  }

  @Test("Published eventless nodes report unknown events rather than unknown nodes")
  func eventlessNodeDispatch() throws {
    let session = RenderSession(root: Text("Eventless"), metadata: metadata)
    let initial = try session.render()
    #expect(
      throws: EventDispatchError.unknownEvent(
        nodeID: initial.application.rootView.id,
        event: "press")
    ) {
      try session.dispatch(
        revision: initial.revision,
        nodeID: initial.application.rootView.id,
        event: "press")
    }
  }

  @Test("Built-in view-ID namespaces prevent cross-type aliases")
  func namespacedViewIDs() throws {
    let application = try OrchardRenderer.render(NamespacedIdentityRoot(), metadata: metadata)
    let leaves = application.rootView.flattened.filter { $0.type == "Text" }
    #expect(leaves.count == 2)
    #expect(Set(leaves.map(\.id)).count == 2)
  }

  @Test("ForEach construction stops at the render node budget")
  func boundedForEach() {
    let session = RenderSession(
      root: BoundedCollectionRoot(),
      metadata: metadata,
      limits: RenderSessionLimits(maximumNodes: 3))
    #expect(throws: RenderValidationError.nodeLimitExceeded(maximum: 3)) {
      try session.render()
    }
    #expect(session.revision == 0)
  }

  @Test("A detached transport task must hop to MainActor")
  func actorIsolation() async throws {
    let metadata = metadata
    let revision = try await Task.detached { @Sendable in
      try await MainActor.run {
        try RenderSession(root: CounterRoot(), metadata: metadata).render().revision
      }
    }.value
    #expect(revision == 1)
  }

  private var metadata: ApplicationMetadata {
    ApplicationMetadata(
      applicationID: "dev.orchard.tests",
      displayName: "OrchardUI Tests",
      sourceFile: "OrchardUITests.swift",
      compiledAtUTC: "2026-07-18T00:00:00Z"
    )
  }

  private func node(type: String, in snapshot: RenderSnapshot) throws -> ViewNode {
    try #require(snapshot.application.rootView.flattened.first { $0.type == type })
  }

  private func button(titled title: String, in snapshot: RenderSnapshot) throws -> ViewNode {
    try #require(
      snapshot.application.rootView.flattened.first {
        $0.type == "Button" && $0.arguments["_0"] == title
      })
  }
}

@MainActor
private struct StaticRoot: View {
  var body: some View {
    NavigationStack {
      VStack(spacing: 18) {
        Text("Native Orchard")
          .font(.largeTitle)
          .foregroundStyle(.green)
        Image(systemName: "leaf.fill")
        Divider()
      }
      .padding(24)
      .navigationTitle("Native Orchard")
    }
  }
}

@MainActor
private struct RootBuilderRoot: View {
  let showExtra: Bool
  let values: [String]

  var body: some View {
    Text("Header")
    if showExtra {
      Text("Extra")
    }
    ForEach(values) { value in
      Text(value)
    }
  }
}

@MainActor
private struct StatefulRoot: View {
  @State private var name = ""
  @State("notificationsEnabled") private var notificationsEnabled = true

  var body: some View {
    VStack {
      TextField("Your name", text: $name)
      Toggle("Enable notifications", isOn: $notificationsEnabled)
      Button("Continue") {
        name = "Orchard Developer"
        notificationsEnabled = false
      }
    }
  }
}

@MainActor
private struct CounterRoot: View {
  @State private var count = 0

  var body: some View {
    Button("Count \(count)") { count += 1 }
  }
}

@MainActor
private struct ConditionalIdentityRoot: View {
  @State private var showExtra = false

  var body: some View {
    VStack {
      if showExtra {
        Text("Inserted")
      }
      Button("Stable") {}
      Button("Toggle extra") { showExtra.toggle() }
    }
  }
}

@MainActor
private struct ReorderRoot: View {
  @State private var reversed = false

  var body: some View {
    List {
      Button("Reverse") { reversed.toggle() }
      ForEach(reversed ? ["B", "A"] : ["A", "B"]) { item in
        ReorderItem(name: item)
      }
    }
  }
}

@MainActor
private struct ReorderItem: View {
  let name: String
  @State private var count = 0

  var body: some View {
    Button("\(name) \(count)") { count += 1 }
  }
}

@MainActor
private struct ConditionalChildRoot: View {
  @State private var isShown = true

  var body: some View {
    VStack {
      Button("Show or hide") { isShown.toggle() }
      if isShown {
        NestedCounter()
      }
    }
  }
}

@MainActor
private struct NestedCounter: View {
  @State private var count = 0

  var body: some View {
    Button("Child \(count)") { count += 1 }
  }
}

@MainActor
private struct CollisionRoot: View {
  @State private var x = "one"
  @State private var x2 = "two"

  var body: some View {
    VStack {
      TextField("First", text: $x)
      TextField("Second", text: $x2)
      CollisionChild()
    }
  }
}

@MainActor
private struct CollisionChild: View {
  @State private var x = "three"

  var body: some View {
    TextField("Third", text: $x)
  }
}

@MainActor
private struct DuplicateNameRoot: View {
  @State("same") private var first = "first"

  var body: some View {
    VStack {
      TextField("First", text: $first)
      DuplicateNameChild()
    }
  }
}

@MainActor
private struct DuplicateNameChild: View {
  @State("same") private var second = "second"

  var body: some View {
    TextField("Second", text: $second)
  }
}

@MainActor
private struct InvalidNameRoot: View {
  @State("bad-name") private var value = "value"

  var body: some View {
    TextField("Value", text: $value)
  }
}

@MainActor
private struct EmptyNameRoot: View {
  @State("") private var value = "value"

  var body: some View {
    TextField("Value", text: $value)
  }
}

@MainActor
private struct DuplicateIdentityRoot: View {
  var body: some View {
    List {
      ForEach(["same", "same"]) { value in
        Text(value)
      }
    }
  }
}

@MainActor
private struct InvalidIdentityRoot: View {
  var body: some View {
    Text("Invalid").id("")
  }
}

@MainActor
private final class BindingModel {
  var text = "Initial"
  var setterCalls = 0
}

@MainActor
private struct BindingRoot: View {
  let model: BindingModel

  var body: some View {
    VStack {
      TextField(
        "Custom",
        text: Binding(
          get: { model.text },
          set: {
            model.setterCalls += 1
            model.text = $0
          }
        )
      )
      Toggle("Read only", isOn: Binding<Bool>.constant(true))
    }
  }
}

@MainActor
private struct InvalidatingActionRoot: View {
  @State private var count = 0
  @State private var isInvalid = false

  var body: some View {
    VStack {
      Button("Break render") {
        count += 1
        isInvalid = true
      }
      Text("Count \(count)")
      if isInvalid {
        RollbackNewSlot()
        Text("Invalid").id("duplicate")
        Text("Invalid").id("duplicate")
      }
    }
  }
}

@MainActor
private struct RollbackNewSlot: View {
  @State private var value = "new"

  var body: some View {
    TextField("New slot", text: $value)
  }
}

private enum TestEventError: Error, Equatable, Sendable {
  case failed
}

@MainActor
private struct ThrowingHandlerRoot: View {
  @State private var count = 0

  var body: some View {
    VStack {
      ThrowingControl {
        count += 1
        throw TestEventError.failed
      }
      Text("Count \(count)")
    }
  }
}

@MainActor
private struct ThrowingControl: View {
  typealias Body = Never
  let action: () throws -> Void

  init(action: @escaping () throws -> Void) {
    self.action = action
  }

  func _render(in context: inout RenderContext) -> ViewNode {
    let id = context.allocateNodeID()
    context.registerEvent(nodeID: id, name: "press") { _ in
      try action()
    }
    return ViewNode(
      id: id,
      type: "ThrowingControl",
      events: [ViewEvent(name: "press", body: "\(id).press")])
  }
}

@MainActor
private final class ReentryHarness {
  var attempt: (() throws -> Void)?
  var caughtError: EventDispatchError?
}

@MainActor
private struct ReentryRoot: View {
  let harness: ReentryHarness

  var body: some View {
    Button("Attempt reentry") {
      do {
        try harness.attempt?()
      } catch let error as EventDispatchError {
        harness.caughtError = error
      } catch {
        Issue.record("Unexpected reentry error: \(error)")
      }
    }
  }
}

@MainActor
private struct RenderingReentryRoot: View {
  typealias Body = Never
  let harness: ReentryHarness

  func _render(in context: inout RenderContext) -> ViewNode {
    do {
      try harness.attempt?()
    } catch let error as EventDispatchError {
      harness.caughtError = error
    } catch {
      Issue.record("Unexpected render reentry error: \(error)")
    }
    return Text("Rendered")._render(in: &context)
  }
}

@MainActor
private final class ChurnModel {
  var current = 0
}

@MainActor
private struct ChurnRoot: View {
  let model: ChurnModel

  var body: some View {
    ForEach([model.current]) { identity in
      ChurnItem(identity: identity)
    }
  }
}

@MainActor
private struct ChurnItem: View {
  let identity: Int
  @State private var count = 0

  var body: some View {
    Button("Item \(identity) count \(count)") { count += 1 }
  }
}

@MainActor
private struct TooManyStatesRoot: View {
  @State private var first = "1"
  @State private var second = "2"
  @State private var third = "3"

  var body: some View {
    Text("\(first)\(second)\(third)")
  }
}

@MainActor
private struct WideRoot: View {
  var body: some View {
    VStack {
      Text("1")
      Text("2")
      Text("3")
      Text("4")
    }
  }
}

@MainActor
private struct DeepTreeRoot: View {
  var body: some View {
    VStack {
      VStack {
        Text("Too deep")
      }
    }
  }
}

@MainActor
private struct CompositionChain: View {
  typealias Body = Never
  let remaining: Int

  func _render(in context: inout RenderContext) -> ViewNode {
    context.withCompositionScope { nestedContext in
      if remaining == 0 {
        return Text("Leaf")._render(in: &nestedContext)
      }
      return CompositionChain(remaining: remaining - 1)._render(in: &nestedContext)
    }
  }
}

@MainActor
private final class DynamicNameModel {
  var name = "first"
}

@MainActor
private struct DynamicNameRoot: View {
  let model: DynamicNameModel

  var body: some View {
    DynamicNamedChild(name: model.name)
  }
}

@MainActor
private struct DynamicNamedChild: View {
  @State private var value: String

  init(name: String) {
    _value = State(wrappedValue: "", name)
  }

  var body: some View {
    TextField("Dynamic", text: $value)
  }
}

@MainActor
private struct NamespacedIdentityRoot: View {
  var body: some View {
    VStack {
      Text("String").id("Int:1")
      Text("Integer").id(1)
    }
  }
}

@MainActor
private struct BoundedCollectionRoot: View {
  var body: some View {
    ForEach(Array(0..<100)) { value in
      Text("\(value)")
    }
  }
}
