import Foundation

/// Constants shared by producers and consumers of Orchard's bootstrap IR.
public enum OrchardSchema: Sendable {
  public static let currentVersion = "0.1.0"
}

public enum CompatibilityStatus: String, Codable, CaseIterable, Sendable {
  case supported
  case partial
  case remoteOnly
  case unsupported
}

public enum StateValueKind: String, Codable, CaseIterable, Sendable {
  case text
  case flag
  case wholeNumber
  case decimalNumber
  case unknown
}

public struct SourceLocation: Codable, Equatable, Sendable {
  public var file: String
  public var line: Int
  public var column: Int
  public var length: Int

  public init(file: String, line: Int, column: Int, length: Int = 1) {
    self.file = file
    self.line = line
    self.column = column
    self.length = length
  }
}

public struct ViewModifier: Codable, Equatable, Sendable {
  public var name: String
  public var arguments: [String: String]
  public var location: SourceLocation?

  public init(
    name: String,
    arguments: [String: String] = [:],
    location: SourceLocation? = nil
  ) {
    self.name = name
    self.arguments = arguments
    self.location = location
  }
}

public struct ViewEvent: Codable, Equatable, Sendable {
  public var name: String
  public var body: String
  public var location: SourceLocation?

  public init(name: String, body: String, location: SourceLocation? = nil) {
    self.name = name
    self.body = body
    self.location = location
  }
}

/// A complete retained view subtree. Children and modifiers preserve semantic order.
public struct ViewNode: Codable, Equatable, Sendable {
  public var id: String
  public var type: String
  public var arguments: [String: String]
  public var modifiers: [ViewModifier]
  public var events: [ViewEvent]
  public var children: [ViewNode]
  public var location: SourceLocation?

  public init(
    id: String,
    type: String,
    arguments: [String: String] = [:],
    modifiers: [ViewModifier] = [],
    events: [ViewEvent] = [],
    children: [ViewNode] = [],
    location: SourceLocation? = nil
  ) {
    self.id = id
    self.type = type
    self.arguments = arguments
    self.modifiers = modifiers
    self.events = events
    self.children = children
    self.location = location
  }
}

public struct StateDefinition: Codable, Equatable, Sendable {
  public var name: String
  public var kind: StateValueKind
  /// Declaration/reset metadata captured from the source-level `@State` initializer.
  /// This is not the live value of a replacement render. Current bound control values are
  /// published in the corresponding `ViewNode.arguments` entries.
  public var initialValue: String
  public var location: SourceLocation?

  public init(
    name: String,
    kind: StateValueKind,
    initialValue: String,
    location: SourceLocation? = nil
  ) {
    self.name = name
    self.kind = kind
    self.initialValue = initialValue
    self.location = location
  }
}

public struct APIUsage: Codable, Equatable, Sendable {
  public var symbol: String
  public var category: String
  public var status: CompatibilityStatus
  public var notes: String?
  public var location: SourceLocation?

  public init(
    symbol: String,
    category: String,
    status: CompatibilityStatus,
    notes: String? = nil,
    location: SourceLocation? = nil
  ) {
    self.symbol = symbol
    self.category = category
    self.status = status
    self.notes = notes
    self.location = location
  }
}

public struct CompatibilityReport: Codable, Equatable, Sendable {
  public var localCompatibilityPercent: Double
  public var apiUsages: [APIUsage]
  public var requiredRemoteCapabilities: [String]
  public var unsupportedSymbols: [String]

  public init(
    localCompatibilityPercent: Double,
    apiUsages: [APIUsage] = [],
    requiredRemoteCapabilities: [String] = [],
    unsupportedSymbols: [String] = []
  ) {
    self.localCompatibilityPercent = localCompatibilityPercent
    self.apiUsages = apiUsages
    self.requiredRemoteCapabilities = requiredRemoteCapabilities
    self.unsupportedSymbols = unsupportedSymbols
  }
}

/// The full document consumed by the Orchard runtime bootstrap.
///
/// `compiledAtUtc` is deliberately represented as an RFC 3339 string. The caller owns
/// the build clock, which keeps rendering deterministic and avoids encoder-specific date
/// precision differences across platforms.
public struct OrchardApplication: Codable, Equatable, Sendable {
  public var schemaVersion: String
  public var applicationId: String
  public var displayName: String
  public var sourceFile: String
  public var rootView: ViewNode
  public var state: [StateDefinition]
  public var compatibility: CompatibilityReport
  public var compiledAtUtc: String

  public init(
    schemaVersion: String = OrchardSchema.currentVersion,
    applicationId: String,
    displayName: String,
    sourceFile: String,
    rootView: ViewNode,
    state: [StateDefinition] = [],
    compatibility: CompatibilityReport,
    compiledAtUtc: String
  ) {
    self.schemaVersion = schemaVersion
    self.applicationId = applicationId
    self.displayName = displayName
    self.sourceFile = sourceFile
    self.rootView = rootView
    self.state = state
    self.compatibility = compatibility
    self.compiledAtUtc = compiledAtUtc
  }

  public var isLocallyRunnable: Bool {
    compatibility.unsupportedSymbols.isEmpty
  }
}

extension ViewNode {
  /// Pre-order traversal is stable and mirrors deterministic node ID assignment.
  ///
  /// The traversal is iterative so a hostile or generated hierarchy cannot exhaust the
  /// process stack. Validation applies the contract depth and node limits before IR is
  /// encoded or after it is decoded.
  public var flattened: [ViewNode] {
    var result: [ViewNode] = []
    result.reserveCapacity(min(OrchardContractLimits.maximumNodes, children.count + 1))
    var stack = [self]

    while let current = stack.popLast() {
      result.append(current)
      for child in current.children.reversed() {
        stack.append(child)
      }
    }

    return result
  }

  public func node(id searchedID: String) -> ViewNode? {
    var stack = [self]

    while let current = stack.popLast() {
      if current.id == searchedID {
        return current
      }

      for child in current.children.reversed() {
        stack.append(child)
      }
    }

    return nil
  }
}
