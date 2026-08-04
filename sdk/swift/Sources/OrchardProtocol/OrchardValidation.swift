import Foundation

/// Resource and semantic limits shared with `OrchardApplicationReader` in the .NET host.
/// String lengths are UTF-16 code-unit counts to match `System.String.Length` exactly.
public enum OrchardContractLimits: Sendable {
  public static let maximumDocumentBytes = 16 * 1024 * 1024
  public static let maximumNodes = 5_000
  public static let maximumDepth = 128
  public static let maximumStateItems = 10_000
  public static let maximumTextLength = 65_536
}

public struct OrchardValidationError: Error, Equatable, Sendable, CustomStringConvertible {
  public let path: String
  public let reason: String

  public init(path: String, reason: String) {
    self.path = path
    self.reason = reason
  }

  public var description: String {
    "Invalid Orchard IR at \(path): \(reason)"
  }
}

extension OrchardApplication {
  /// Validates the complete semantic IR contract without recursive tree traversal.
  public func validate() throws {
    guard schemaVersion == OrchardSchema.currentVersion else {
      throw invalid("$.schemaVersion", "expected '\(OrchardSchema.currentVersion)'")
    }

    try validateText(applicationId, path: "$.applicationId", maximum: 255)
    try validateText(displayName, path: "$.displayName", maximum: 128)
    try validateText(sourceFile, path: "$.sourceFile", maximum: 1_024)
    try validateTimestamp(compiledAtUtc, path: "$.compiledAtUtc")

    guard state.count <= OrchardContractLimits.maximumStateItems else {
      throw invalid(
        "$.state",
        "must contain at most \(OrchardContractLimits.maximumStateItems) items")
    }

    var stateNames = Set<String>()
    stateNames.reserveCapacity(state.count)
    for (index, definition) in state.enumerated() {
      let path = "$.state[\(index)]"
      try validateSymbol(definition.name, path: "\(path).name")
      try validateText(
        definition.initialValue,
        path: "\(path).initialValue",
        maximum: OrchardContractLimits.maximumTextLength,
        allowEmpty: true,
        allowContentWhitespace: true)
      try validateLocation(definition.location, path: "\(path).location")
      guard stateNames.insert(definition.name).inserted else {
        throw invalid("\(path).name", "state name '\(definition.name)' is duplicated")
      }
    }

    try validateTree(rootView)
    try validateCompatibility(compatibility)
  }
}

private struct PendingNode {
  let node: ViewNode
  let depth: Int
  let path: String
}

private func validateTree(_ root: ViewNode) throws {
  var identifiers = Set<String>()
  identifiers.reserveCapacity(OrchardContractLimits.maximumNodes)
  var stack = [PendingNode(node: root, depth: 1, path: "$.rootView")]
  var count = 0

  while let pending = stack.popLast() {
    count += 1
    guard count <= OrchardContractLimits.maximumNodes else {
      throw invalid("$.rootView", "exceeds \(OrchardContractLimits.maximumNodes) nodes")
    }
    guard pending.depth <= OrchardContractLimits.maximumDepth else {
      throw invalid("$.rootView", "exceeds a depth of \(OrchardContractLimits.maximumDepth)")
    }

    let node = pending.node
    let path = pending.path
    try validateSymbol(node.id, path: "\(path).id", permitHyphen: true)
    try validateSymbol(node.type, path: "\(path).type", permitDot: true)
    guard identifiers.insert(node.id).inserted else {
      throw invalid("\(path).id", "view ID '\(node.id)' is duplicated")
    }

    for (key, value) in node.arguments {
      try validateText(key, path: "\(path).arguments key", maximum: 128)
      try validateText(
        value,
        path: "\(path).arguments[\(key)]",
        maximum: OrchardContractLimits.maximumTextLength,
        allowEmpty: true,
        allowContentWhitespace: true)
    }

    for (index, modifier) in node.modifiers.enumerated() {
      let modifierPath = "\(path).modifiers[\(index)]"
      try validateSymbol(modifier.name, path: "\(modifierPath).name")
      for (key, value) in modifier.arguments {
        try validateText(key, path: "\(modifierPath).arguments key", maximum: 128)
        try validateText(
          value,
          path: "\(modifierPath).arguments[\(key)]",
          maximum: OrchardContractLimits.maximumTextLength,
          allowEmpty: true,
          allowContentWhitespace: true)
      }
      try validateLocation(modifier.location, path: "\(modifierPath).location")
    }

    for (index, event) in node.events.enumerated() {
      let eventPath = "\(path).events[\(index)]"
      try validateSymbol(event.name, path: "\(eventPath).name")
      try validateText(
        event.body,
        path: "\(eventPath).body",
        maximum: OrchardContractLimits.maximumTextLength,
        allowEmpty: true,
        allowContentWhitespace: true)
      try validateLocation(event.location, path: "\(eventPath).location")
    }

    try validateLocation(node.location, path: "\(path).location")
    for index in node.children.indices.reversed() {
      stack.append(
        PendingNode(
          node: node.children[index],
          depth: pending.depth + 1,
          path: "\(path).children[\(index)]"))
    }
  }
}

private func validateCompatibility(_ report: CompatibilityReport) throws {
  guard report.localCompatibilityPercent.isFinite,
    (0...100).contains(report.localCompatibilityPercent)
  else {
    throw invalid(
      "$.compatibility.localCompatibilityPercent", "must be finite and between 0 and 100")
  }

  for (index, usage) in report.apiUsages.enumerated() {
    let path = "$.compatibility.apiUsages[\(index)]"
    try validateText(usage.symbol, path: "\(path).symbol", maximum: 512)
    try validateText(usage.category, path: "\(path).category", maximum: 128)
    if let notes = usage.notes {
      try validateText(
        notes,
        path: "\(path).notes",
        maximum: OrchardContractLimits.maximumTextLength,
        allowEmpty: true,
        allowContentWhitespace: true)
    }
    try validateLocation(usage.location, path: "\(path).location")
  }

  try validateUniqueTextList(
    report.requiredRemoteCapabilities,
    path: "$.compatibility.requiredRemoteCapabilities")
  try validateUniqueTextList(
    report.unsupportedSymbols,
    path: "$.compatibility.unsupportedSymbols")
}

private func validateUniqueTextList(_ values: [String], path: String) throws {
  var unique = Set<String>()
  unique.reserveCapacity(values.count)
  for (index, value) in values.enumerated() {
    try validateText(value, path: "\(path)[\(index)]", maximum: 512)
    guard unique.insert(value).inserted else {
      throw invalid("\(path)[\(index)]", "value '\(value)' is duplicated")
    }
  }
}

private func validateLocation(_ location: SourceLocation?, path: String) throws {
  guard let location else { return }
  try validateText(location.file, path: "\(path).file", maximum: 1_024)
  guard location.line >= 1, location.column >= 1, location.length >= 0 else {
    throw invalid(path, "line and column must be positive and length must be non-negative")
  }
}

private func validateSymbol(
  _ value: String,
  path: String,
  permitHyphen: Bool = false,
  permitDot: Bool = false
) throws {
  try validateText(value, path: path, maximum: 256)
  let isValid = value.unicodeScalars.allSatisfy { scalar in
    let value = scalar.value
    return (65...90).contains(value) || (97...122).contains(value) || (48...57).contains(value)
      || scalar == "_" || (permitHyphen && scalar == "-") || (permitDot && scalar == ".")
  }
  guard isValid else {
    throw invalid(path, "contains unsupported identifier characters")
  }
}

private func validateText(
  _ value: String,
  path: String,
  maximum: Int,
  allowEmpty: Bool = false,
  allowContentWhitespace: Bool = false
) throws {
  let emptyOrWhitespace =
    value.isEmpty || value.unicodeScalars.allSatisfy { $0.properties.isWhitespace }
  guard allowEmpty || !emptyOrWhitespace, value.utf16.count <= maximum else {
    throw invalid(path, "is empty or exceeds \(maximum) UTF-16 code units")
  }

  for scalar in value.unicodeScalars
  where isUnsafeControl(scalar, allowContentWhitespace: allowContentWhitespace) {
    throw invalid(path, "contains an unsafe control or bidirectional formatting character")
  }
}

private func isUnsafeControl(
  _ scalar: Unicode.Scalar,
  allowContentWhitespace: Bool
) -> Bool {
  let codePoint = scalar.value
  if codePoint == 0x061C || codePoint == 0x200E || codePoint == 0x200F
    || (0x202A...0x202E).contains(codePoint) || (0x2066...0x2069).contains(codePoint)
  {
    return true
  }

  guard scalar.properties.generalCategory == .control else { return false }
  return !allowContentWhitespace || (codePoint != 0x0A && codePoint != 0x0D && codePoint != 0x09)
}

private func validateTimestamp(_ value: String, path: String) throws {
  try validateText(value, path: path, maximum: 64)
  let formatter = ISO8601DateFormatter()
  formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
  if formatter.date(from: value) != nil {
    return
  }

  formatter.formatOptions = [.withInternetDateTime]
  guard formatter.date(from: value) != nil else {
    throw invalid(path, "must be an RFC 3339 timestamp with an explicit UTC offset")
  }
}

private func invalid(_ path: String, _ reason: String) -> OrchardValidationError {
  OrchardValidationError(path: path, reason: reason)
}
