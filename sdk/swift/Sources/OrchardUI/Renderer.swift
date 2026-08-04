import OrchardProtocol

public struct ApplicationMetadata: Equatable, Sendable {
  public var applicationID: String
  public var displayName: String
  public var sourceFile: String
  public var compiledAtUTC: String
  public var requestedSourceSymbols: [String]

  public init(
    applicationID: String,
    displayName: String,
    sourceFile: String,
    compiledAtUTC: String,
    requestedSourceSymbols: [String] = []
  ) {
    self.applicationID = applicationID
    self.displayName = displayName
    self.sourceFile = sourceFile
    self.compiledAtUTC = compiledAtUTC
    self.requestedSourceSymbols = requestedSourceSymbols
  }
}

public struct RenderSnapshot: Equatable, Sendable {
  public let revision: Int64
  public let application: OrchardApplication

  public init(revision: Int64, application: OrchardApplication) {
    self.revision = revision
    self.application = application
  }
}

public enum EventDispatchError: Error, Equatable, Sendable {
  case renderRequired
  case staleRevision(expected: Int64, received: Int64)
  case unknownNode(String)
  case unknownEvent(nodeID: String, event: String)
  case missingValue(event: String)
  case invalidValue(event: String, value: String)
  case renderRevisionOverflow
  case reentrantOperation(active: RenderSessionPhase, requested: RenderSessionPhase)
}

public enum RenderSessionPhase: String, Equatable, Sendable {
  case idle
  case rendering
  case dispatching
}

/// Owns a view graph, its reconciled state slots, and its published event table.
///
/// All methods are main-actor isolated. A transport must hop to `MainActor` before dispatching.
/// Synchronous reentry is rejected explicitly, and session-owned state/storage is rolled back if
/// a handler or replacement render fails. Orchard cannot roll back arbitrary side effects made by
/// action closures or custom binding setters to objects outside this session; those callbacks
/// should validate first and perform external effects only after a successful snapshot is observed.
@MainActor
public final class RenderSession<Root: View> {
  public let root: Root
  public let metadata: ApplicationMetadata
  public let limits: RenderSessionLimits

  public private(set) var revision: Int64 = 0
  public private(set) var phase: RenderSessionPhase = .idle
  private let storage: RenderStorage
  private var eventHandlers: [EventKey: (String?) throws -> Void] = [:]
  private var publishedNodeIDs: Set<String> = []
  private var hasPublished = false

  public init(
    root: Root,
    metadata: ApplicationMetadata,
    limits: RenderSessionLimits = .default
  ) {
    self.root = root
    self.metadata = metadata
    self.limits = limits
    storage = RenderStorage(limits: limits)
  }

  /// Publishes a fresh snapshot. Revisions start at one and increase for every successful
  /// render; failed validation never publishes a tree or advances the revision.
  @discardableResult
  public func render() throws -> RenderSnapshot {
    try withPhase(.rendering) {
      let staged = try withStorageTransaction {
        try stageNextPublication()
      }
      publish(staged)
      return staged.snapshot
    }
  }

  /// Applies an event only to the exact tree revision that registered it, then atomically
  /// publishes the resulting tree. A delayed event can never reach a new closure at the same ID.
  @discardableResult
  public func dispatch(
    revision receivedRevision: Int64,
    nodeID: String,
    event: String,
    value: String? = nil
  ) throws -> RenderSnapshot {
    try withPhase(.dispatching) {
      guard hasPublished else {
        throw EventDispatchError.renderRequired
      }
      guard receivedRevision == revision else {
        throw EventDispatchError.staleRevision(expected: revision, received: receivedRevision)
      }

      let key = EventKey(nodeID: nodeID, name: event)
      guard let handler = eventHandlers[key] else {
        if publishedNodeIDs.contains(nodeID) {
          throw EventDispatchError.unknownEvent(nodeID: nodeID, event: event)
        }
        throw EventDispatchError.unknownNode(nodeID)
      }

      let staged = try withStorageTransaction {
        try handler(value)
        return try stageNextPublication()
      }
      publish(staged)
      return staged.snapshot
    }
  }

  var retainedStateSlotCount: Int { storage.retainedSlotCount }
  var stateIdentityTombstoneCount: Int { storage.tombstoneCount }

  private struct StagedPublication {
    let snapshot: RenderSnapshot
    let eventHandlers: [EventKey: (String?) throws -> Void]
    let nodeIDs: Set<String>
  }

  private func stageNextPublication() throws -> StagedPublication {
    guard revision < Int64.max else {
      throw EventDispatchError.renderRevisionOverflow
    }

    var context = RenderContext(storage: storage, limits: limits)
    let rootNode = root._render(in: &context)
    if let failure = context.failure {
      throw failure
    }

    let application = OrchardApplication(
      applicationId: metadata.applicationID,
      displayName: metadata.displayName,
      sourceFile: metadata.sourceFile,
      rootView: rootNode,
      state: context.stateDefinitions,
      compatibility: OrchardCapabilityProfile.bootstrapV1.report(
        for: rootNode,
        requestedSourceSymbols: metadata.requestedSourceSymbols),
      compiledAtUtc: metadata.compiledAtUTC
    )
    try application.validate()
    let snapshot = RenderSnapshot(revision: revision + 1, application: application)
    return StagedPublication(
      snapshot: snapshot,
      eventHandlers: context.eventHandlers,
      nodeIDs: context.publishedNodeIDs)
  }

  private func publish(_ staged: StagedPublication) {
    eventHandlers = staged.eventHandlers
    publishedNodeIDs = staged.nodeIDs
    revision = staged.snapshot.revision
    hasPublished = true
  }

  private func withPhase<T>(
    _ requested: RenderSessionPhase,
    operation: () throws -> T
  ) throws -> T {
    guard phase == .idle else {
      throw EventDispatchError.reentrantOperation(active: phase, requested: requested)
    }
    phase = requested
    defer { phase = .idle }
    return try operation()
  }

  private func withStorageTransaction<T>(_ operation: () throws -> T) throws -> T {
    let checkpoint = storage.beginTransaction()
    do {
      let result = try operation()
      storage.commit(checkpoint)
      return result
    } catch {
      storage.rollback(checkpoint)
      throw error
    }
  }
}

@MainActor
public enum OrchardRenderer {
  public static func render<Root: View>(
    _ root: Root,
    metadata: ApplicationMetadata
  ) throws -> OrchardApplication {
    try RenderSession(root: root, metadata: metadata).render().application
  }
}
