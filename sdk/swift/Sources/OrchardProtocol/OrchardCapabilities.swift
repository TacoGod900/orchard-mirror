import Foundation

public enum OrchardCapabilityCategory: String, Codable, CaseIterable, Sendable {
  case view
  case modifier
  case framework
}

public struct OrchardCapabilityEntry: Codable, Equatable, Sendable {
  public let status: CompatibilityStatus
  public let notes: String?
  public let remoteCapability: String?

  public init(
    status: CompatibilityStatus,
    notes: String? = nil,
    remoteCapability: String? = nil
  ) {
    self.status = status
    self.notes = notes
    self.remoteCapability = remoteCapability
  }
}

public struct OrchardReproducibleBuildMetadata: Codable, Equatable, Sendable {
  public let timestampEnvironmentVariable: String
  public let timestampUnit: String
  public let fallbackTimestampUtc: String
  public let volatileProvenance: String

  public init(
    timestampEnvironmentVariable: String,
    timestampUnit: String,
    fallbackTimestampUtc: String,
    volatileProvenance: String
  ) {
    self.timestampEnvironmentVariable = timestampEnvironmentVariable
    self.timestampUnit = timestampUnit
    self.fallbackTimestampUtc = fallbackTimestampUtc
    self.volatileProvenance = volatileProvenance
  }
}

/// The declared compatibility boundary for the bootstrap runtime.
///
/// Unknown symbols fail closed as unsupported. The status weights and catalog contents are
/// versioned with `schemas/orchard-capabilities-v1.json` and match the authoritative .NET
/// `CompatibilityCatalog`.
public struct OrchardCapabilityProfile: Equatable, Sendable {
  public let profileVersion: String
  public let irSchemaVersion: String
  public let statusWeights: [CompatibilityStatus: Double]
  public let views: [String: OrchardCapabilityEntry]
  public let modifiers: [String: OrchardCapabilityEntry]
  public let sourceSymbols: [String: OrchardCapabilityEntry]
  public let reproducibleBuildMetadata: OrchardReproducibleBuildMetadata

  public init(
    profileVersion: String,
    irSchemaVersion: String,
    statusWeights: [CompatibilityStatus: Double],
    views: [String: OrchardCapabilityEntry],
    modifiers: [String: OrchardCapabilityEntry],
    sourceSymbols: [String: OrchardCapabilityEntry],
    reproducibleBuildMetadata: OrchardReproducibleBuildMetadata
  ) {
    self.profileVersion = profileVersion
    self.irSchemaVersion = irSchemaVersion
    self.statusWeights = statusWeights
    self.views = views
    self.modifiers = modifiers
    self.sourceSymbols = sourceSymbols
    self.reproducibleBuildMetadata = reproducibleBuildMetadata
  }

  public func entry(
    for symbol: String,
    category: OrchardCapabilityCategory
  ) -> OrchardCapabilityEntry {
    let entry =
      switch category {
      case .view: views[symbol]
      case .modifier: modifiers[symbol]
      case .framework: sourceSymbols[symbol]
      }
    if let entry { return entry }
    let notes =
      switch category {
      case .view:
        "View '\(symbol)' is not present in the current compatibility catalog."
      case .modifier:
        "Modifier '.\(symbol)' is not present in the current compatibility catalog."
      case .framework:
        "Source capability '\(symbol)' is not present in the current compatibility catalog."
      }
    return OrchardCapabilityEntry(status: .unsupported, notes: notes)
  }

  public func weight(for status: CompatibilityStatus) -> Double {
    statusWeights[status] ?? 0
  }

  /// Computes the truthful compatibility report for a retained view tree and explicitly
  /// requested source-level framework capabilities.
  public func report(
    for rootView: ViewNode,
    requestedSourceSymbols: [String] = []
  ) -> CompatibilityReport {
    var usages: [APIUsage] = []
    var remote = Set<String>()
    var unsupported = Set<String>()
    var stack = [rootView]

    func record(
      symbol: String,
      category: OrchardCapabilityCategory,
      entry: OrchardCapabilityEntry,
      location: SourceLocation?
    ) {
      usages.append(
        APIUsage(
          symbol: category == .modifier ? ".\(symbol)" : symbol,
          category: category.rawValue,
          status: entry.status,
          notes: entry.notes,
          location: location))
      if let capability = entry.remoteCapability {
        remote.insert(capability)
      }
      if entry.status == .unsupported {
        unsupported.insert(category == .modifier ? ".\(symbol)" : symbol)
      }
    }

    while let node = stack.popLast() {
      record(
        symbol: node.type,
        category: .view,
        entry: entry(for: node.type, category: .view),
        location: node.location)
      for modifier in node.modifiers {
        record(
          symbol: modifier.name,
          category: .modifier,
          entry: entry(for: modifier.name, category: .modifier),
          location: modifier.location)
      }
      for child in node.children.reversed() {
        stack.append(child)
      }
    }

    for symbol in Set(requestedSourceSymbols).sorted() {
      record(
        symbol: symbol,
        category: .framework,
        entry: entry(for: symbol, category: .framework),
        location: nil)
    }

    let score =
      usages.isEmpty
      ? 100
      : (usages.reduce(0) { $0 + weight(for: $1.status) } / Double(usages.count) * 10)
        .rounded(.toNearestOrAwayFromZero) / 10

    return CompatibilityReport(
      localCompatibilityPercent: score,
      apiUsages: usages,
      requiredRemoteCapabilities: remote.sorted(),
      unsupportedSymbols: unsupported.sorted())
  }

  public static let bootstrapV1 = OrchardCapabilityProfile(
    profileVersion: "1.0.0",
    irSchemaVersion: OrchardSchema.currentVersion,
    statusWeights: [
      .supported: 100,
      .partial: 65,
      .remoteOnly: 20,
      .unsupported: 0,
    ],
    views: [
      "VStack": .init(status: .supported),
      "HStack": .init(status: .supported),
      "ZStack": .init(status: .supported),
      "Text": .init(status: .supported),
      "Button": .init(status: .supported),
      "TextField": .init(status: .supported),
      "SecureField": .init(status: .supported),
      "Image": .init(
        status: .partial,
        notes: "System symbols render as Orchard placeholders in this milestone."),
      "Spacer": .init(status: .supported),
      "Divider": .init(status: .supported),
      "Toggle": .init(status: .supported),
      "ProgressView": .init(
        status: .partial,
        notes: "Indeterminate and linear styles are supported."),
      "Label": .init(
        status: .partial,
        notes: "Text and system-image labels are supported."),
      "ScrollView": .init(status: .supported),
      "NavigationStack": .init(
        status: .partial,
        notes: "Single-window navigation rendering is supported; route restoration is pending."),
      "List": .init(
        status: .partial,
        notes: "Static list content is supported; collection diffing is pending."),
      "Form": .init(status: .partial, notes: "Core form controls are supported."),
      "Section": .init(status: .partial, notes: "Headers and static children are supported."),
      "Group": .init(status: .supported),
      "NavigationLink": .init(
        status: .partial,
        notes: "Destination closures are parsed but not yet activated."),
      "Picker": .init(status: .partial, notes: "Static options are supported."),
      "DatePicker": .init(status: .partial, notes: "Date-only selection is supported."),
    ],
    modifiers: [
      "padding": .init(status: .supported),
      "font": .init(status: .supported),
      "foregroundStyle": .init(status: .partial, notes: "Solid named colours are supported."),
      "foregroundColor": .init(status: .partial, notes: "Solid named colours are supported."),
      "background": .init(status: .partial, notes: "Solid named colours are supported."),
      "cornerRadius": .init(status: .supported),
      "frame": .init(status: .partial, notes: "Fixed width and height are supported."),
      "navigationTitle": .init(status: .supported),
      "disabled": .init(status: .supported),
      "opacity": .init(status: .supported),
      "lineLimit": .init(status: .supported),
      "multilineTextAlignment": .init(status: .supported),
      "accessibilityLabel": .init(status: .supported),
      "accessibilityHint": .init(status: .supported),
      "accessibilityIdentifier": .init(status: .supported),
      "animation": .init(status: .partial, notes: "Basic property animations are supported."),
      "onAppear": .init(status: .partial, notes: "Synchronous local handlers are supported."),
      "tint": .init(status: .supported),
    ],
    sourceSymbols: [
      "ARKit": .init(
        status: .remoteOnly,
        notes: "ARKit requires physical Apple sensors.",
        remoteCapability: "ARKit"),
      "RealityKit": .init(
        status: .remoteOnly,
        notes: "RealityKit rendering requires Apple validation.",
        remoteCapability: "RealityKit"),
      "PKPaymentAuthorizationController": .init(
        status: .remoteOnly,
        notes: "Apple Pay flows require Apple-controlled entitlements and hardware.",
        remoteCapability: "Apple Pay"),
      "HKHealthStore": .init(
        status: .remoteOnly,
        notes: "HealthKit data and entitlements require Apple validation.",
        remoteCapability: "HealthKit"),
      "SecureEnclave": .init(
        status: .remoteOnly,
        notes: "Secure Enclave operations require Apple hardware.",
        remoteCapability: "Secure Enclave"),
      "CarPlay": .init(
        status: .remoteOnly,
        notes: "CarPlay requires Apple validation and approved entitlements.",
        remoteCapability: "CarPlay"),
      "MetalKit": .init(
        status: .remoteOnly,
        notes: "Metal translation is not part of the initial local runtime.",
        remoteCapability: "Metal"),
      "StoreKit": .init(
        status: .remoteOnly,
        notes: "StoreKit purchase completion requires Apple services.",
        remoteCapability: "StoreKit"),
    ],
    reproducibleBuildMetadata: OrchardReproducibleBuildMetadata(
      timestampEnvironmentVariable: "SOURCE_DATE_EPOCH",
      timestampUnit: "wholeSecondsSinceUnixEpoch",
      fallbackTimestampUtc: "1970-01-01T00:00:00Z",
      volatileProvenance: "outOfBand"))
}
