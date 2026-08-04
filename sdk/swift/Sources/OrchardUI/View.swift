import Foundation
import OrchardProtocol

/// Fail-closed errors discovered while reconciling a declarative view graph.
public enum RenderValidationError: Error, Equatable, Sendable {
  case invalidStateName(String)
  case duplicateExplicitStateName(String)
  case stateSlotTypeChanged(String)
  case stateSlotNameChanged(identity: String, expected: String?, received: String?)
  case stateSlotLimitExceeded(maximum: Int)
  case inferredStateNameCollision(String)
  case invalidExplicitIdentity(String)
  case duplicateExplicitIdentity(String)
  case structuralIdentityCollision(String)
  case nodeLimitExceeded(maximum: Int)
  case treeDepthLimitExceeded(maximum: Int)
  case compositionDepthLimitExceeded(maximum: Int)
}

/// Per-session safety limits. Values may tighten, but never exceed, the IR contract.
/// Smaller limits are useful for constrained hosts and deterministic stress testing.
public struct RenderSessionLimits: Equatable, Sendable {
  public var maximumNodes: Int
  public var maximumTreeDepth: Int
  public var maximumCompositionDepth: Int
  public var maximumRetainedStateSlots: Int
  public var maximumStateIdentityTombstones: Int

  public init(
    maximumNodes: Int = OrchardContractLimits.maximumNodes,
    maximumTreeDepth: Int = OrchardContractLimits.maximumDepth,
    maximumCompositionDepth: Int = OrchardContractLimits.maximumDepth,
    maximumRetainedStateSlots: Int = OrchardContractLimits.maximumStateItems,
    maximumStateIdentityTombstones: Int = OrchardContractLimits.maximumStateItems
  ) {
    precondition((1...OrchardContractLimits.maximumNodes).contains(maximumNodes))
    precondition((1...OrchardContractLimits.maximumDepth).contains(maximumTreeDepth))
    precondition((1...OrchardContractLimits.maximumDepth).contains(maximumCompositionDepth))
    precondition(
      (1...OrchardContractLimits.maximumStateItems).contains(maximumRetainedStateSlots))
    precondition(
      (1...OrchardContractLimits.maximumStateItems).contains(maximumStateIdentityTombstones))
    self.maximumNodes = maximumNodes
    self.maximumTreeDepth = maximumTreeDepth
    self.maximumCompositionDepth = maximumCompositionDepth
    self.maximumRetainedStateSlots = maximumRetainedStateSlots
    self.maximumStateIdentityTombstones = maximumStateIdentityTombstones
  }

  public static let `default` = RenderSessionLimits()
}

/// The OrchardUI view protocol. The result builder is part of the protocol contract, so
/// conditionals and multiple expressions work at a composite view's root as well as inside
/// containers. Dynamic collections use the context-bounded `ForEach` view.
@MainActor
public protocol View {
  associatedtype Body: View
  @ViewBuilder var body: Body { get }
  func _render(in context: inout RenderContext) -> ViewNode
}

extension View {
  public func _render(in context: inout RenderContext) -> ViewNode {
    context.withCompositionScope { nestedContext in
      nestedContext.bindStateProperties(from: self)
      guard nestedContext.canContinueRendering else {
        return nestedContext.abortedNode()
      }
      return body._render(in: &nestedContext)
    }
  }
}

extension View where Body == Never {
  public var body: Never {
    fatalError("Primitive OrchardUI views do not have a body.")
  }
}

extension Never: View {
  public typealias Body = Never
}

@MainActor
protocol _ExplicitlyIdentifiedView {
  var _explicitIdentityKey: String { get }
}

/// Values accepted by `.id(_:)`. Conformances must return a stable, process-independent value.
/// Custom conformances must namespace values with a stable type identifier and byte length so
/// two unrelated ID types cannot alias the same structural identity.
public protocol OrchardViewID {
  var orchardViewIdentity: String { get }
}

extension String: OrchardViewID {
  public var orchardViewIdentity: String {
    isEmpty ? "" : "Swift.String:\(utf8.count):\(self)"
  }
}

extension Int: OrchardViewID {
  public var orchardViewIdentity: String {
    let value = String(self)
    return "Swift.Int:\(value.utf8.count):\(value)"
  }
}

extension UInt: OrchardViewID {
  public var orchardViewIdentity: String {
    let value = String(self)
    return "Swift.UInt:\(value.utf8.count):\(value)"
  }
}

extension UUID: OrchardViewID {
  public var orchardViewIdentity: String {
    let value = uuidString.lowercased()
    return "Foundation.UUID:\(value.utf8.count):\(value)"
  }
}

/// Type erasure at result-builder collection boundaries.
@MainActor
public struct AnyView: View {
  public typealias Body = Never
  private let renderer: (inout RenderContext) -> ViewNode
  let explicitIdentityKey: String?

  public init<Content: View>(_ content: Content) {
    renderer = { context in
      content._render(in: &context)
    }
    explicitIdentityKey =
      (content as? any _ExplicitlyIdentifiedView)?._explicitIdentityKey
  }

  public func _render(in context: inout RenderContext) -> ViewNode {
    context.withCompositionScope { nestedContext in
      renderer(&nestedContext)
    }
  }
}

/// An identity-preserving component produced by `ViewBuilder`.
@MainActor
public struct ViewBuilderContent: View {
  public typealias Body = Never

  fileprivate indirect enum Storage {
    case empty
    case expression(AnyView)
    case block([ViewBuilderContent])
    case optional(ViewBuilderContent?)
    case either(branch: Int, ViewBuilderContent)
  }

  private let storage: Storage

  fileprivate init(storage: Storage) {
    self.storage = storage
  }

  fileprivate var explicitIdentityKey: String? {
    switch storage {
    case .expression(let view):
      return view.explicitIdentityKey
    case .block(let components):
      guard components.count == 1 else { return nil }
      return components[0].explicitIdentityKey
    case .optional(let component):
      return component?.explicitIdentityKey
    case .either(_, let component):
      return component.explicitIdentityKey
    case .empty:
      return nil
    }
  }

  private var rendersExactlyOneNode: Bool {
    switch storage {
    case .empty:
      return false
    case .expression:
      return true
    case .block(let components):
      return components.count == 1 && components[0].rendersExactlyOneNode
    case .optional(let component):
      return component?.rendersExactlyOneNode ?? false
    case .either(_, let component):
      return component.rendersExactlyOneNode
    }
  }

  fileprivate var producesAnyNode: Bool {
    switch storage {
    case .empty:
      return false
    case .expression:
      return true
    case .block(let components):
      return components.contains { $0.producesAnyNode }
    case .optional(let component):
      return component?.producesAnyNode ?? false
    case .either(_, let component):
      return component.producesAnyNode
    }
  }

  public func _render(in context: inout RenderContext) -> ViewNode {
    context.withCompositionScope { nestedContext in
      _renderContent(in: &nestedContext)
    }
  }

  private func _renderContent(in context: inout RenderContext) -> ViewNode {
    if rendersExactlyOneNode {
      let children = renderChildren(in: &context)
      return children.first ?? context.abortedNode()
    }

    guard context.requireNodeCapacity() else { return context.abortedNode() }
    let id = context.allocateNodeID()
    guard context.canContinueRendering else { return context.abortedNode() }
    let children =
      producesAnyNode
      ? context.withChildNodeDepth { nestedContext in
        renderChildren(in: &nestedContext)
      } : []
    return ViewNode(
      id: id,
      type: "Group",
      children: children
    )
  }

  func renderChildren(in context: inout RenderContext) -> [ViewNode] {
    context.withCompositionCollectionScope { nestedContext in
      _renderChildren(in: &nestedContext)
    }
  }

  private func _renderChildren(in context: inout RenderContext) -> [ViewNode] {
    switch storage {
    case .empty:
      return []
    case .expression(let view):
      return [view._render(in: &context)]
    case .block(let components):
      return Self.renderSiblings(components, marker: "b", in: &context)
    case .optional(let component):
      guard let component else { return [] }
      return context.withPathSegment("o1", fallback: []) { nestedContext in
        component.renderChildren(in: &nestedContext)
      }
    case .either(let branch, let component):
      return context.withPathSegment("c\(branch)", fallback: []) { nestedContext in
        component.renderChildren(in: &nestedContext)
      }
    }
  }

  private static func renderSiblings(
    _ components: [ViewBuilderContent],
    marker: String,
    in context: inout RenderContext
  ) -> [ViewNode] {
    var nodes: [ViewNode] = []
    var explicitKeys: Set<String> = []
    for (index, component) in components.enumerated() {
      if component.producesAnyNode, !context.requireNodeCapacity() { break }
      let segment: String
      if let key = component.explicitIdentityKey {
        context.validateExplicitIdentity(key)
        if !explicitKeys.insert(key).inserted {
          context.recordFailure(.duplicateExplicitIdentity(key))
        }
        segment = Self.keySegment(key)
      } else {
        segment = "\(marker)\(index)"
      }
      let rendered = context.withPathSegment(segment, fallback: []) { nestedContext in
        component.renderChildren(in: &nestedContext)
      }
      guard context.canContinueRendering else { break }
      nodes.append(contentsOf: rendered)
    }
    return nodes
  }

  private static func keySegment(_ key: String) -> String {
    "k\(key.utf8.count):\(key)"
  }
}

/// Collects heterogeneous semantic views while retaining structural control-flow boundaries.
@MainActor
@resultBuilder
public enum ViewBuilder {
  public static func buildExpression<Content: View>(_ expression: Content) -> ViewBuilderContent {
    ViewBuilderContent(storage: .expression(AnyView(expression)))
  }

  public static func buildBlock(_ components: ViewBuilderContent...) -> ViewBuilderContent {
    ViewBuilderContent(storage: components.isEmpty ? .empty : .block(components))
  }

  public static func buildOptional(_ component: ViewBuilderContent?) -> ViewBuilderContent {
    ViewBuilderContent(storage: .optional(component))
  }

  public static func buildEither(first component: ViewBuilderContent) -> ViewBuilderContent {
    ViewBuilderContent(storage: .either(branch: 0, component))
  }

  public static func buildEither(second component: ViewBuilderContent) -> ViewBuilderContent {
    ViewBuilderContent(storage: .either(branch: 1, component))
  }

  public static func buildLimitedAvailability(
    _ component: ViewBuilderContent
  ) -> ViewBuilderContent {
    component
  }
}

/// A context-bounded collection view. Unlike result-builder `for` expansion, `ForEach` does not
/// eagerly materialize one builder node per element before render limits can be enforced.
@MainActor
public struct ForEach<Data: RandomAccessCollection, ID: OrchardViewID, Content: View>: View {
  public typealias Body = Never

  private let data: Data
  private let id: KeyPath<Data.Element, ID>
  private let content: (Data.Element) -> Content

  public init(
    _ data: Data,
    id: KeyPath<Data.Element, ID>,
    @ViewBuilder content: @escaping (Data.Element) -> Content
  ) {
    self.data = data
    self.id = id
    self.content = content
  }

  public func _render(in context: inout RenderContext) -> ViewNode {
    context.withCompositionScope { nestedContext in
      guard nestedContext.requireNodeCapacity() else {
        return nestedContext.abortedNode()
      }
      let groupID = nestedContext.allocateNodeID()
      guard nestedContext.canContinueRendering else {
        return nestedContext.abortedNode()
      }
      guard !data.isEmpty else {
        return ViewNode(id: groupID, type: "Group")
      }

      let children = nestedContext.withChildNodeDepth { childContext in
        var nodes: [ViewNode] = []
        nodes.reserveCapacity(min(data.count, OrchardContractLimits.maximumNodes))
        var identities = Set<String>()
        for element in data {
          guard childContext.requireNodeCapacity() else { break }
          let identity = element[keyPath: id].orchardViewIdentity
          childContext.validateExplicitIdentity(identity)
          guard identities.insert(identity).inserted else {
            childContext.recordFailure(.duplicateExplicitIdentity(identity))
            break
          }
          let segment = "foreach\(identity.utf8.count):\(identity)"
          let node = childContext.withPathSegment(
            segment,
            fallback: childContext.abortedNode()
          ) { itemContext in
            content(element)._render(in: &itemContext)
          }
          guard childContext.canContinueRendering else { break }
          nodes.append(node)
        }
        return nodes
      }
      return ViewNode(id: groupID, type: "Group", children: children)
    }
  }
}

extension ForEach where Data.Element == ID {
  public init(
    _ data: Data,
    @ViewBuilder content: @escaping (Data.Element) -> Content
  ) {
    self.init(data, id: \.self, content: content)
  }
}

@MainActor
final class RenderStorage {
  // Slots are retained by stable identity and evicted by least-recent successful generation.
  // Tombstones preserve deterministic names/types after value eviction, with an independent cap.
  private final class WeakDescriptor {
    weak var value: (any _AnyStateDescriptor)?

    init(_ value: any _AnyStateDescriptor) {
      self.value = value
    }
  }

  private struct StoredSlot {
    var slot: any _AnyStateSlot
    var lastActiveGeneration: Int64
    var descriptors: [WeakDescriptor]
  }

  private struct IdentityTombstone {
    let name: String
    let declaredName: String?
    let kind: StateValueKind
    let lastActiveGeneration: Int64
  }

  private final class Checkpoint {
    let stateSlotsByIdentity: [String: StoredSlot]
    let identityTombstones: [String: IdentityTombstone]
    let stateNameOwners: [String: String]
    let activeIdentities: Set<String>
    let generation: Int64
    let nextGeneration: Int64
    let slotValues: [(slot: any _AnyStateSlot, value: Any)]
    var descriptorSnapshots:
      [ObjectIdentifier: (descriptor: any _AnyStateDescriptor, value: Any)] = [:]

    init(
      stateSlotsByIdentity: [String: StoredSlot],
      identityTombstones: [String: IdentityTombstone],
      stateNameOwners: [String: String],
      activeIdentities: Set<String>,
      generation: Int64
    ) {
      self.stateSlotsByIdentity = stateSlotsByIdentity
      self.identityTombstones = identityTombstones
      self.stateNameOwners = stateNameOwners
      self.activeIdentities = activeIdentities
      self.generation = generation
      nextGeneration = generation + 1
      slotValues = stateSlotsByIdentity.values.map { record in
        (record.slot, record.slot.captureValue())
      }
    }
  }

  private let limits: RenderSessionLimits
  private var stateSlotsByIdentity: [String: StoredSlot] = [:]
  private var identityTombstones: [String: IdentityTombstone] = [:]
  private var stateNameOwners: [String: String] = [:]
  private var activeIdentities: Set<String> = []
  private var generation: Int64 = 0
  private var activeCheckpoint: Checkpoint?

  init(limits: RenderSessionLimits = .default) {
    self.limits = limits
  }

  func beginTransaction() -> AnyObject {
    precondition(activeCheckpoint == nil, "RenderStorage does not support nested transactions.")
    precondition(generation < Int64.max, "RenderStorage generation overflowed.")
    let checkpoint = Checkpoint(
      stateSlotsByIdentity: stateSlotsByIdentity,
      identityTombstones: identityTombstones,
      stateNameOwners: stateNameOwners,
      activeIdentities: activeIdentities,
      generation: generation)
    activeCheckpoint = checkpoint
    activeIdentities.removeAll(keepingCapacity: true)
    return checkpoint
  }

  func commit(_ opaqueCheckpoint: AnyObject) {
    guard let checkpoint = opaqueCheckpoint as? Checkpoint,
      checkpoint === activeCheckpoint
    else {
      preconditionFailure("RenderStorage committed an inactive transaction.")
    }

    generation = checkpoint.nextGeneration
    for identity in activeIdentities {
      guard var record = stateSlotsByIdentity[identity] else { continue }
      record.lastActiveGeneration = generation
      stateSlotsByIdentity[identity] = record
    }
    activeCheckpoint = nil
  }

  func rollback(_ opaqueCheckpoint: AnyObject) {
    guard let checkpoint = opaqueCheckpoint as? Checkpoint,
      checkpoint === activeCheckpoint
    else {
      preconditionFailure("RenderStorage rolled back an inactive transaction.")
    }

    for snapshot in checkpoint.slotValues {
      snapshot.slot.restoreValue(snapshot.value)
    }
    stateSlotsByIdentity = checkpoint.stateSlotsByIdentity
    identityTombstones = checkpoint.identityTombstones
    stateNameOwners = checkpoint.stateNameOwners
    activeIdentities = checkpoint.activeIdentities
    generation = checkpoint.generation
    for snapshot in checkpoint.descriptorSnapshots.values {
      snapshot.descriptor.restoreTransactionState(snapshot.value)
    }
    activeCheckpoint = nil
  }

  func bind(
    _ descriptor: any _AnyStateDescriptor,
    identity: String,
    suggestedName: String?,
    context: inout RenderContext
  ) {
    if let explicitName = descriptor.requestedName,
      !Self.isPortableStateName(explicitName)
    {
      context.recordFailure(.invalidStateName(explicitName))
      return
    }

    captureDescriptorIfNeeded(descriptor)

    if var existing = stateSlotsByIdentity[identity] {
      guard existing.slot.declaredName == descriptor.requestedName else {
        context.recordFailure(
          .stateSlotNameChanged(
            identity: identity,
            expected: existing.slot.declaredName,
            received: descriptor.requestedName))
        return
      }
      guard existing.slot.kind == descriptor.kind, descriptor.attach(to: existing.slot) else {
        context.recordFailure(.stateSlotTypeChanged(identity))
        return
      }
      existing.descriptors.removeAll { $0.value == nil }
      if !existing.descriptors.contains(where: { item in
        item.value.map { ObjectIdentifier($0) } == ObjectIdentifier(descriptor)
      }) {
        existing.descriptors.append(WeakDescriptor(descriptor))
      }
      stateSlotsByIdentity[identity] = existing
      activeIdentities.insert(identity)
      return
    }

    let resolvedName: String
    if let tombstone = identityTombstones[identity] {
      guard tombstone.declaredName == descriptor.requestedName else {
        context.recordFailure(
          .stateSlotNameChanged(
            identity: identity,
            expected: tombstone.declaredName,
            received: descriptor.requestedName))
        return
      }
      guard tombstone.kind == descriptor.kind else {
        context.recordFailure(.stateSlotTypeChanged(identity))
        return
      }
      resolvedName = tombstone.name
      identityTombstones.removeValue(forKey: identity)
    } else if let explicitName = descriptor.requestedName {
      if let owner = stateNameOwners[explicitName], owner != identity {
        context.recordFailure(.duplicateExplicitStateName(explicitName))
        return
      }
      resolvedName = explicitName
    } else {
      let base = Self.inferredName(from: suggestedName)
      resolvedName = Self.deterministicInferredName(base: base, identity: identity)
      if let owner = stateNameOwners[resolvedName], owner != identity {
        context.recordFailure(.inferredStateNameCollision(resolvedName))
        return
      }
    }

    guard makeRoomForSlot(context: &context) else {
      return
    }

    let slot = descriptor.makeSlot(named: resolvedName)
    guard descriptor.attach(to: slot) else {
      context.recordFailure(.stateSlotTypeChanged(identity))
      return
    }
    stateSlotsByIdentity[identity] = StoredSlot(
      slot: slot,
      lastActiveGeneration: activeCheckpoint?.nextGeneration ?? generation + 1,
      descriptors: [WeakDescriptor(descriptor)])
    stateNameOwners[resolvedName] = identity
    activeIdentities.insert(identity)
  }

  var stateDefinitions: [StateDefinition] {
    activeIdentities.compactMap { identity -> StateDefinition? in
      guard let slot = stateSlotsByIdentity[identity]?.slot else { return nil }
      // `initialValue` is declaration/reset metadata. Live values belong in rendered control
      // arguments, so a replacement snapshot never misrepresents a declaration as current state.
      return StateDefinition(name: slot.name, kind: slot.kind, initialValue: slot.initialValue)
    }
      .sorted { $0.name < $1.name }
  }

  var retainedSlotCount: Int { stateSlotsByIdentity.count }
  var tombstoneCount: Int { identityTombstones.count }

  private func captureDescriptorIfNeeded(_ descriptor: any _AnyStateDescriptor) {
    guard let activeCheckpoint else { return }
    let identifier = ObjectIdentifier(descriptor)
    guard activeCheckpoint.descriptorSnapshots[identifier] == nil else { return }
    activeCheckpoint.descriptorSnapshots[identifier] = (
      descriptor,
      descriptor.captureTransactionState())
  }

  private func makeRoomForSlot(context: inout RenderContext) -> Bool {
    guard stateSlotsByIdentity.count >= limits.maximumRetainedStateSlots else {
      return true
    }

    let candidate = stateSlotsByIdentity
      .filter { !activeIdentities.contains($0.key) }
      .min { left, right in
        if left.value.lastActiveGeneration != right.value.lastActiveGeneration {
          return left.value.lastActiveGeneration < right.value.lastActiveGeneration
        }
        return left.key < right.key
      }
    guard let candidate else {
      context.recordFailure(
        .stateSlotLimitExceeded(maximum: limits.maximumRetainedStateSlots))
      return false
    }

    stateSlotsByIdentity.removeValue(forKey: candidate.key)
    for weakDescriptor in candidate.value.descriptors {
      guard let descriptor = weakDescriptor.value else { continue }
      captureDescriptorIfNeeded(descriptor)
      descriptor.detachAndReset()
    }
    retainTombstone(
      identity: candidate.key,
      slot: candidate.value.slot,
      lastActiveGeneration: candidate.value.lastActiveGeneration)
    return true
  }

  private func retainTombstone(
    identity: String,
    slot: any _AnyStateSlot,
    lastActiveGeneration: Int64
  ) {
    if identityTombstones.count >= limits.maximumStateIdentityTombstones,
      let victim = identityTombstones.min(by: { left, right in
        if left.value.lastActiveGeneration != right.value.lastActiveGeneration {
          return left.value.lastActiveGeneration < right.value.lastActiveGeneration
        }
        return left.key < right.key
      })
    {
      identityTombstones.removeValue(forKey: victim.key)
      if stateNameOwners[victim.value.name] == victim.key {
        stateNameOwners.removeValue(forKey: victim.value.name)
      }
    }

    identityTombstones[identity] = IdentityTombstone(
      name: slot.name,
      declaredName: slot.declaredName,
      kind: slot.kind,
      lastActiveGeneration: lastActiveGeneration)
  }

  private static func nonEmpty(_ value: String?) -> String? {
    guard let value else { return nil }
    let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
    return trimmed.isEmpty ? nil : trimmed
  }

  private static func inferredName(from suggestedName: String?) -> String {
    guard let suggestedName = nonEmpty(suggestedName) else {
      return "state"
    }
    let ascii = suggestedName.unicodeScalars.map { scalar -> Character in
      if scalar.value < 128,
        CharacterSet.alphanumerics.contains(scalar) || scalar == "_"
      {
        return Character(String(scalar))
      }
      return "_"
    }
    let value = String(ascii.prefix(239))
    return value.isEmpty ? "state" : value
  }

  private static func deterministicInferredName(base: String, identity: String) -> String {
    "\(base)_\(stableHexDigest(identity))"
  }

  private static func isPortableStateName(_ value: String) -> Bool {
    guard !value.isEmpty, value.utf16.count <= 256 else { return false }
    return value.unicodeScalars.allSatisfy { scalar in
      scalar.value < 128
        && (CharacterSet.alphanumerics.contains(scalar) || scalar == "_")
    }
  }

  private static func stableHexDigest(_ value: String) -> String {
    var hash: UInt64 = 14_695_981_039_346_656_037
    for byte in value.utf8 {
      hash ^= UInt64(byte)
      hash = hash &* 1_099_511_628_211
    }
    let digest = String(hash, radix: 16, uppercase: false)
    return String(repeating: "0", count: max(0, 16 - digest.count)) + digest
  }
}

/// Mutable state for one render pass. The backing `RenderStorage` belongs to the session.
@MainActor
public struct RenderContext {
  private let storage: RenderStorage
  private let limits: RenderSessionLimits
  private var path: [String] = ["root"]
  private var allocatedNodeIDs: [String: String] = [:]
  private var allocatedNodeCount = 0
  private var currentNodeDepth = 1
  private var compositionDepth = 0
  private var handlers: [EventKey: (String?) throws -> Void] = [:]
  private(set) var failure: RenderValidationError?

  public init() {
    limits = .default
    storage = RenderStorage(limits: limits)
  }

  init(storage: RenderStorage, limits: RenderSessionLimits) {
    self.storage = storage
    self.limits = limits
  }

  var canContinueRendering: Bool { failure == nil }

  mutating func requireNodeCapacity() -> Bool {
    guard canContinueRendering else { return false }
    guard allocatedNodeCount < limits.maximumNodes else {
      recordFailure(.nodeLimitExceeded(maximum: limits.maximumNodes))
      return false
    }
    return true
  }

  mutating func allocateNodeID() -> String {
    guard requireNodeCapacity() else { return "view-aborted" }
    guard currentNodeDepth <= limits.maximumTreeDepth else {
      recordFailure(.treeDepthLimitExceeded(maximum: limits.maximumTreeDepth))
      return "view-aborted"
    }
    allocatedNodeCount += 1
    let identity = path.joined(separator: "/")
    let id = "view-\(Self.stableHexDigest(identity))"
    if let existing = allocatedNodeIDs[id], existing != identity {
      recordFailure(.structuralIdentityCollision(id))
    } else if allocatedNodeIDs[id] != nil {
      recordFailure(.structuralIdentityCollision(identity))
    } else {
      allocatedNodeIDs[id] = identity
    }
    return id
  }

  mutating func bindStateProperties<Content>(from content: Content) {
    let mirror = Mirror(reflecting: content)
    var index = 0
    for child in mirror.children {
      guard canContinueRendering else { break }
      guard let state = child.value as? any _AnyStateProperty else {
        continue
      }
      let label = child.label.map(Self.stateName(fromReflectedLabel:))
      let propertyIdentity = label ?? "property\(index)"
      let identity = [
        path.joined(separator: "/"),
        String(reflecting: type(of: content)),
        propertyIdentity,
      ].joined(separator: "|")
      storage.bind(
        state._stateDescriptor,
        identity: identity,
        suggestedName: label,
        context: &self
      )
      index += 1
    }
  }

  mutating func registerAttachedState(_ descriptor: any _AnyStateDescriptor) -> String? {
    descriptor.attachedSlot?.name
  }

  mutating func registerEvent(
    nodeID: String,
    name: String,
    handler: @escaping (String?) throws -> Void
  ) {
    handlers[EventKey(nodeID: nodeID, name: name)] = handler
  }

  var stateDefinitions: [StateDefinition] { storage.stateDefinitions }
  var eventHandlers: [EventKey: (String?) throws -> Void] { handlers }
  var publishedNodeIDs: Set<String> { Set(allocatedNodeIDs.keys) }

  mutating func withPathSegment<T>(
    _ segment: String,
    fallback: @autoclosure () -> T,
    operation: (inout RenderContext) -> T
  ) -> T {
    guard canContinueRendering else { return fallback() }
    path.append(segment)
    defer { path.removeLast() }
    return operation(&self)
  }

  mutating func withChildNodeDepth(
    operation: (inout RenderContext) -> [ViewNode]
  ) -> [ViewNode] {
    guard canContinueRendering else { return [] }
    guard currentNodeDepth < limits.maximumTreeDepth else {
      recordFailure(.treeDepthLimitExceeded(maximum: limits.maximumTreeDepth))
      return []
    }
    currentNodeDepth += 1
    defer { currentNodeDepth -= 1 }
    return operation(&self)
  }

  mutating func withCompositionScope(
    operation: (inout RenderContext) -> ViewNode
  ) -> ViewNode {
    guard canContinueRendering else { return abortedNode() }
    guard compositionDepth < limits.maximumCompositionDepth else {
      recordFailure(
        .compositionDepthLimitExceeded(maximum: limits.maximumCompositionDepth))
      return abortedNode()
    }
    compositionDepth += 1
    defer { compositionDepth -= 1 }
    return operation(&self)
  }

  mutating func withCompositionCollectionScope(
    operation: (inout RenderContext) -> [ViewNode]
  ) -> [ViewNode] {
    guard canContinueRendering else { return [] }
    guard compositionDepth < limits.maximumCompositionDepth else {
      recordFailure(
        .compositionDepthLimitExceeded(maximum: limits.maximumCompositionDepth))
      return []
    }
    compositionDepth += 1
    defer { compositionDepth -= 1 }
    return operation(&self)
  }

  func abortedNode() -> ViewNode {
    ViewNode(id: "view-aborted", type: "Group")
  }

  mutating func validateExplicitIdentity(_ key: String) {
    guard !key.isEmpty, key.utf8.count <= 256,
      key.unicodeScalars.allSatisfy({ scalar in
        !CharacterSet.controlCharacters.contains(scalar)
          && scalar.value != 0x061C && scalar.value != 0x200E && scalar.value != 0x200F
          && !(0x202A...0x202E).contains(scalar.value)
          && !(0x2066...0x2069).contains(scalar.value)
      })
    else {
      recordFailure(.invalidExplicitIdentity(key))
      return
    }
  }

  mutating func recordFailure(_ error: RenderValidationError) {
    if failure == nil {
      failure = error
    }
  }

  private static func stateName(fromReflectedLabel label: String) -> String {
    var name = label
    while name.first == "_" {
      name.removeFirst()
    }
    return name
  }

  private static func stableHexDigest(_ value: String) -> String {
    var hash: UInt64 = 14_695_981_039_346_656_037
    for byte in value.utf8 {
      hash ^= UInt64(byte)
      hash = hash &* 1_099_511_628_211
    }
    let value = String(hash, radix: 16, uppercase: false)
    return String(repeating: "0", count: max(0, 16 - value.count)) + value
  }
}

@MainActor
struct EventKey: Hashable {
  let nodeID: String
  let name: String
}

@MainActor
func renderChildren(_ content: ViewBuilderContent, in context: inout RenderContext) -> [ViewNode] {
  content.renderChildren(in: &context)
}

@MainActor
func makeContainerNode(
  type: String,
  arguments: [String: String] = [:],
  content: ViewBuilderContent,
  context: inout RenderContext
) -> ViewNode {
  guard context.requireNodeCapacity() else { return context.abortedNode() }
  let id = context.allocateNodeID()
  guard context.canContinueRendering else { return context.abortedNode() }
  let children =
    content.producesAnyNode
    ? context.withChildNodeDepth { nestedContext in
      renderChildren(content, in: &nestedContext)
    } : []
  return ViewNode(
    id: id,
    type: type,
    arguments: arguments,
    children: children
  )
}

func orchardNumber(_ value: Double) -> String {
  if value.isFinite,
    value.rounded(.towardZero) == value,
    value >= Double(Int64.min),
    value <= Double(Int64.max)
  {
    return String(Int64(value))
  }
  return String(value)
}
