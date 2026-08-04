import Foundation

/// Orchard IR JSON support.
///
/// Interoperability is semantic: producers may choose whitespace and property order, while
/// consumers enforce the same case-sensitive fields and meanings. `encode(...,
/// prettyPrinted: false)` is Orchard's compact canonical transport representation: sorted
/// object keys, no insignificant whitespace, and no slash escaping.
public enum OrchardJSON: Sendable {
  public static func encode<T: Encodable>(
    _ value: T,
    prettyPrinted: Bool = true
  ) throws -> Data {
    if let application = value as? OrchardApplication {
      try application.validate()
    }

    let encoder = JSONEncoder()
    var formatting: JSONEncoder.OutputFormatting = [.sortedKeys, .withoutEscapingSlashes]
    if prettyPrinted {
      formatting.insert(.prettyPrinted)
    }
    encoder.outputFormatting = formatting
    let data = try encoder.encode(value)

    if value is OrchardApplication, data.count > OrchardContractLimits.maximumDocumentBytes {
      throw OrchardJSONError.documentTooLarge(
        actualBytes: data.count,
        maximumBytes: OrchardContractLimits.maximumDocumentBytes)
    }
    return data
  }

  public static func string<T: Encodable>(
    _ value: T,
    prettyPrinted: Bool = true
  ) throws -> String {
    let data = try encode(value, prettyPrinted: prettyPrinted)
    guard let string = String(data: data, encoding: .utf8) else {
      throw OrchardJSONError.invalidUTF8
    }
    return string
  }

  public static func decode<T: Decodable>(_ type: T.Type, from data: Data) throws -> T {
    if ObjectIdentifier(T.self) == ObjectIdentifier(OrchardApplication.self) {
      try preflightApplicationDocument(data)
    }

    let decoded = try JSONDecoder().decode(type, from: data)
    if let application = decoded as? OrchardApplication {
      try application.validate()
    }
    return decoded
  }

  public static func decode<T: Decodable>(_ type: T.Type, from string: String) throws -> T {
    guard let data = string.data(using: .utf8) else {
      throw OrchardJSONError.invalidUTF8
    }
    return try decode(type, from: data)
  }
}

public enum OrchardJSONError: Error, Equatable, Sendable, CustomStringConvertible {
  case invalidUTF8
  case documentTooLarge(actualBytes: Int, maximumBytes: Int)
  case malformedDocument(String)
  case duplicateProperty(String)
  case unknownProperty(path: String, property: String)
  case missingProperty(path: String, property: String)
  case invalidShape(path: String, expected: String)

  public var description: String {
    switch self {
    case .invalidUTF8:
      return "Orchard IR must be valid UTF-8."
    case .documentTooLarge(let actual, let maximum):
      return "Orchard IR is \(actual) bytes; the maximum is \(maximum) bytes."
    case .malformedDocument(let reason):
      return "Malformed Orchard IR: \(reason)"
    case .duplicateProperty(let property):
      return "Duplicate JSON property '\(property)' is rejected."
    case .unknownProperty(let path, let property):
      return "Unknown property '\(property)' at \(path)."
    case .missingProperty(let path, let property):
      return "Missing property '\(property)' at \(path)."
    case .invalidShape(let path, let expected):
      return "Invalid value at \(path); expected \(expected)."
    }
  }
}

private func preflightApplicationDocument(_ data: Data) throws {
  guard data.count <= OrchardContractLimits.maximumDocumentBytes else {
    throw OrchardJSONError.documentTooLarge(
      actualBytes: data.count,
      maximumBytes: OrchardContractLimits.maximumDocumentBytes)
  }
  guard String(data: data, encoding: .utf8) != nil else {
    throw OrchardJSONError.invalidUTF8
  }

  var scanner = JSONDuplicateKeyScanner(data: data)
  try scanner.scan()

  let value: Any
  do {
    value = try JSONSerialization.jsonObject(with: data)
  } catch {
    throw OrchardJSONError.malformedDocument(String(describing: error))
  }
  try validateApplicationShape(value)
}

private enum IRShape {
  case application
  case viewNode
  case modifier
  case event
  case location
  case state
  case compatibility
  case apiUsage
  case stringMap
}

private struct ShapeFrame {
  let value: Any
  let shape: IRShape
  let path: String
  let nodeDepth: Int
}

private func validateApplicationShape(_ value: Any) throws {
  var stack = [ShapeFrame(value: value, shape: .application, path: "$", nodeDepth: 0)]
  var nodeCount = 0

  while let frame = stack.popLast() {
    guard let object = frame.value as? [String: Any] else {
      throw OrchardJSONError.invalidShape(path: frame.path, expected: "an object")
    }

    switch frame.shape {
    case .application:
      try validateKeys(
        object,
        path: frame.path,
        required: [
          "schemaVersion", "applicationId", "displayName", "sourceFile", "rootView", "state",
          "compatibility", "compiledAtUtc",
        ])
      stack.append(
        ShapeFrame(
          value: try requiredValue(object, "rootView", at: frame.path),
          shape: .viewNode,
          path: "$.rootView",
          nodeDepth: 1))
      let states = try arrayValue(object, "state", at: frame.path)
      guard states.count <= OrchardContractLimits.maximumStateItems else {
        throw OrchardJSONError.invalidShape(
          path: "$.state",
          expected: "at most \(OrchardContractLimits.maximumStateItems) items")
      }
      for index in states.indices.reversed() {
        stack.append(
          ShapeFrame(
            value: states[index], shape: .state, path: "$.state[\(index)]", nodeDepth: 0))
      }
      stack.append(
        ShapeFrame(
          value: try requiredValue(object, "compatibility", at: frame.path),
          shape: .compatibility,
          path: "$.compatibility",
          nodeDepth: 0))

    case .viewNode:
      nodeCount += 1
      guard nodeCount <= OrchardContractLimits.maximumNodes else {
        throw OrchardJSONError.invalidShape(
          path: "$.rootView", expected: "at most \(OrchardContractLimits.maximumNodes) nodes")
      }
      guard frame.nodeDepth <= OrchardContractLimits.maximumDepth else {
        throw OrchardJSONError.invalidShape(
          path: "$.rootView", expected: "a maximum depth of \(OrchardContractLimits.maximumDepth)")
      }
      try validateKeys(
        object,
        path: frame.path,
        required: ["id", "type", "arguments", "modifiers", "events", "children"],
        optional: ["location"])
      stack.append(
        ShapeFrame(
          value: try requiredValue(object, "arguments", at: frame.path),
          shape: .stringMap,
          path: "\(frame.path).arguments",
          nodeDepth: 0))
      let modifiers = try arrayValue(object, "modifiers", at: frame.path)
      for index in modifiers.indices.reversed() {
        stack.append(
          ShapeFrame(
            value: modifiers[index],
            shape: .modifier,
            path: "\(frame.path).modifiers[\(index)]",
            nodeDepth: 0))
      }
      let events = try arrayValue(object, "events", at: frame.path)
      for index in events.indices.reversed() {
        stack.append(
          ShapeFrame(
            value: events[index],
            shape: .event,
            path: "\(frame.path).events[\(index)]",
            nodeDepth: 0))
      }
      let children = try arrayValue(object, "children", at: frame.path)
      for index in children.indices.reversed() {
        stack.append(
          ShapeFrame(
            value: children[index],
            shape: .viewNode,
            path: "\(frame.path).children[\(index)]",
            nodeDepth: frame.nodeDepth + 1))
      }
      try appendOptionalLocation(object, path: frame.path, to: &stack)

    case .modifier:
      try validateKeys(
        object,
        path: frame.path,
        required: ["name", "arguments"],
        optional: ["location"])
      stack.append(
        ShapeFrame(
          value: try requiredValue(object, "arguments", at: frame.path),
          shape: .stringMap,
          path: "\(frame.path).arguments",
          nodeDepth: 0))
      try appendOptionalLocation(object, path: frame.path, to: &stack)

    case .event:
      try validateKeys(
        object,
        path: frame.path,
        required: ["name", "body"],
        optional: ["location"])
      try appendOptionalLocation(object, path: frame.path, to: &stack)

    case .location:
      try validateKeys(
        object,
        path: frame.path,
        required: ["file", "line", "column", "length"])

    case .state:
      try validateKeys(
        object,
        path: frame.path,
        required: ["name", "kind", "initialValue"],
        optional: ["location"])
      try appendOptionalLocation(object, path: frame.path, to: &stack)

    case .compatibility:
      try validateKeys(
        object,
        path: frame.path,
        required: [
          "localCompatibilityPercent", "apiUsages", "requiredRemoteCapabilities",
          "unsupportedSymbols",
        ])
      let usages = try arrayValue(object, "apiUsages", at: frame.path)
      for index in usages.indices.reversed() {
        stack.append(
          ShapeFrame(
            value: usages[index],
            shape: .apiUsage,
            path: "\(frame.path).apiUsages[\(index)]",
            nodeDepth: 0))
      }
      _ = try arrayValue(object, "requiredRemoteCapabilities", at: frame.path)
      _ = try arrayValue(object, "unsupportedSymbols", at: frame.path)

    case .apiUsage:
      try validateKeys(
        object,
        path: frame.path,
        required: ["symbol", "category", "status"],
        optional: ["notes", "location"])
      try appendOptionalLocation(object, path: frame.path, to: &stack)

    case .stringMap:
      break
    }
  }
}

private func validateKeys(
  _ object: [String: Any],
  path: String,
  required: Set<String>,
  optional: Set<String> = []
) throws {
  for key in required where object[key] == nil {
    throw OrchardJSONError.missingProperty(path: path, property: key)
  }
  let allowed = required.union(optional)
  if let key = object.keys.first(where: { !allowed.contains($0) }) {
    throw OrchardJSONError.unknownProperty(path: path, property: key)
  }
}

private func requiredValue(_ object: [String: Any], _ key: String, at path: String) throws -> Any {
  guard let value = object[key] else {
    throw OrchardJSONError.missingProperty(path: path, property: key)
  }
  return value
}

private func arrayValue(_ object: [String: Any], _ key: String, at path: String) throws -> [Any] {
  let value = try requiredValue(object, key, at: path)
  guard let array = value as? [Any] else {
    throw OrchardJSONError.invalidShape(path: "\(path).\(key)", expected: "an array")
  }
  return array
}

private func appendOptionalLocation(
  _ object: [String: Any],
  path: String,
  to stack: inout [ShapeFrame]
) throws {
  guard let location = object["location"] else { return }
  if location is NSNull {
    return
  }
  stack.append(
    ShapeFrame(
      value: location, shape: .location, path: "\(path).location", nodeDepth: 0))
}

private struct JSONDuplicateKeyScanner {
  private enum State {
    case objectFirstKeyOrEnd
    case objectKey
    case objectColon
    case objectValue
    case objectCommaOrEnd
    case arrayFirstValueOrEnd
    case arrayValue
    case arrayCommaOrEnd
  }

  private struct Frame {
    var state: State
    var keys: Set<String>
  }

  private let bytes: [UInt8]
  private var index = 0
  private var stack: [Frame] = []
  private var consumedRoot = false

  init(data: Data) {
    bytes = Array(data)
  }

  mutating func scan() throws {
    while true {
      skipWhitespace()
      if !consumedRoot {
        consumedRoot = true
        try consumeValue()
        continue
      }

      guard var frame = stack.popLast() else {
        skipWhitespace()
        guard index == bytes.count else {
          throw OrchardJSONError.malformedDocument("trailing content after the root value")
        }
        return
      }

      switch frame.state {
      case .objectFirstKeyOrEnd:
        if consumeIf(0x7D) { continue }
        try consumeObjectKey(into: &frame)
        stack.append(frame)
      case .objectKey:
        try consumeObjectKey(into: &frame)
        stack.append(frame)
      case .objectColon:
        guard consumeIf(0x3A) else {
          throw OrchardJSONError.malformedDocument("expected ':' after an object key")
        }
        frame.state = .objectValue
        stack.append(frame)
      case .objectValue:
        frame.state = .objectCommaOrEnd
        stack.append(frame)
        try consumeValue()
      case .objectCommaOrEnd:
        if consumeIf(0x7D) { continue }
        guard consumeIf(0x2C) else {
          throw OrchardJSONError.malformedDocument("expected ',' or '}' in an object")
        }
        frame.state = .objectKey
        stack.append(frame)
      case .arrayFirstValueOrEnd:
        if consumeIf(0x5D) { continue }
        frame.state = .arrayCommaOrEnd
        stack.append(frame)
        try consumeValue()
      case .arrayValue:
        frame.state = .arrayCommaOrEnd
        stack.append(frame)
        try consumeValue()
      case .arrayCommaOrEnd:
        if consumeIf(0x5D) { continue }
        guard consumeIf(0x2C) else {
          throw OrchardJSONError.malformedDocument("expected ',' or ']' in an array")
        }
        frame.state = .arrayValue
        stack.append(frame)
      }
    }
  }

  private mutating func consumeObjectKey(into frame: inout Frame) throws {
    guard current == 0x22 else {
      throw OrchardJSONError.malformedDocument("expected a quoted object key")
    }
    let key = try consumeString(decode: true)
    guard frame.keys.insert(key).inserted else {
      throw OrchardJSONError.duplicateProperty(key)
    }
    frame.state = .objectColon
  }

  private mutating func consumeValue() throws {
    skipWhitespace()
    guard let byte = current else {
      throw OrchardJSONError.malformedDocument("expected a JSON value")
    }
    switch byte {
    case 0x7B:
      index += 1
      try push(Frame(state: .objectFirstKeyOrEnd, keys: []))
    case 0x5B:
      index += 1
      try push(Frame(state: .arrayFirstValueOrEnd, keys: []))
    case 0x22:
      _ = try consumeString(decode: false)
    case 0x74:
      try consumeLiteral("true")
    case 0x66:
      try consumeLiteral("false")
    case 0x6E:
      try consumeLiteral("null")
    case 0x2D, 0x30...0x39:
      consumeNumberToken()
    default:
      throw OrchardJSONError.malformedDocument("unexpected byte while reading a value")
    }
  }

  private mutating func push(_ frame: Frame) throws {
    guard stack.count < 384 else {
      throw OrchardJSONError.malformedDocument("JSON nesting exceeds 384 levels")
    }
    stack.append(frame)
  }

  private mutating func consumeString(decode: Bool) throws -> String {
    let start = index
    index += 1
    var escaped = false
    while index < bytes.count {
      let byte = bytes[index]
      index += 1
      if escaped {
        escaped = false
        continue
      }
      if byte == 0x5C {
        escaped = true
      } else if byte == 0x22 {
        if !decode { return "" }
        let slice = Data(bytes[start..<index])
        do {
          return try JSONDecoder().decode(String.self, from: slice)
        } catch {
          throw OrchardJSONError.malformedDocument("invalid JSON object key")
        }
      } else if byte < 0x20 {
        throw OrchardJSONError.malformedDocument("unescaped control character in a string")
      }
    }
    throw OrchardJSONError.malformedDocument("unterminated JSON string")
  }

  private mutating func consumeLiteral(_ literal: StaticString) throws {
    let expected = Array(String(describing: literal).utf8)
    guard index + expected.count <= bytes.count,
      Array(bytes[index..<(index + expected.count)]) == expected
    else {
      throw OrchardJSONError.malformedDocument("invalid JSON literal")
    }
    index += expected.count
  }

  private mutating func consumeNumberToken() {
    while let byte = current,
      byte != 0x20, byte != 0x09, byte != 0x0A, byte != 0x0D,
      byte != 0x2C, byte != 0x5D, byte != 0x7D
    {
      index += 1
    }
  }

  private mutating func consumeIf(_ byte: UInt8) -> Bool {
    skipWhitespace()
    guard current == byte else { return false }
    index += 1
    return true
  }

  private mutating func skipWhitespace() {
    while let byte = current, byte == 0x20 || byte == 0x09 || byte == 0x0A || byte == 0x0D {
      index += 1
    }
  }

  private var current: UInt8? {
    index < bytes.count ? bytes[index] : nil
  }
}
