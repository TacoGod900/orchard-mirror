import Foundation

enum StrictJSONValue: Equatable {
  case object([String: StrictJSONValue])
  case array([StrictJSONValue])
  case string(String)
  case number(String)
  case bool(Bool)
  case null
}

struct StrictJSONParser {
  private let bytes: [UInt8]
  private var offset = 0

  init(data: Data) throws {
    guard !data.isEmpty else { throw OrchardTransportError.invalidFrame }
    guard data.count <= OrchardLiveProtocol.maximumFrameBytes else {
      throw OrchardTransportError.frameTooLarge
    }
    guard String(data: data, encoding: .utf8) != nil else {
      throw OrchardTransportError.invalidUTF8OrJSON
    }
    bytes = Array(data)
  }

  mutating func parse() throws -> StrictJSONValue {
    skipWhitespace()
    let value = try parseValue(depth: 1)
    skipWhitespace()
    guard offset == bytes.count else { throw OrchardTransportError.invalidUTF8OrJSON }
    return value
  }

  private mutating func parseValue(depth: Int) throws -> StrictJSONValue {
    guard depth <= OrchardLiveProtocol.maximumJSONDepth, offset < bytes.count else {
      throw OrchardTransportError.invalidUTF8OrJSON
    }
    switch bytes[offset] {
    case 0x7B: return try parseObject(depth: depth)
    case 0x5B: return try parseArray(depth: depth)
    case 0x22: return .string(try parseString())
    case 0x74:
      try consumeLiteral("true")
      return .bool(true)
    case 0x66:
      try consumeLiteral("false")
      return .bool(false)
    case 0x6E:
      try consumeLiteral("null")
      return .null
    case 0x2D, 0x30...0x39: return .number(try parseNumber())
    default: throw OrchardTransportError.invalidUTF8OrJSON
    }
  }

  private mutating func parseObject(depth: Int) throws -> StrictJSONValue {
    offset += 1
    skipWhitespace()
    var values: [String: StrictJSONValue] = [:]
    if consume(0x7D) { return .object(values) }

    while true {
      guard offset < bytes.count, bytes[offset] == 0x22 else {
        throw OrchardTransportError.invalidUTF8OrJSON
      }
      let key = try parseString()
      guard values[key] == nil else {
        throw OrchardTransportError.invalidEnvelope("duplicate JSON property")
      }
      skipWhitespace()
      guard consume(0x3A) else { throw OrchardTransportError.invalidUTF8OrJSON }
      skipWhitespace()
      values[key] = try parseValue(depth: depth + 1)
      skipWhitespace()
      if consume(0x7D) { return .object(values) }
      guard consume(0x2C) else { throw OrchardTransportError.invalidUTF8OrJSON }
      skipWhitespace()
      guard offset < bytes.count, bytes[offset] != 0x7D else {
        throw OrchardTransportError.invalidUTF8OrJSON
      }
    }
  }

  private mutating func parseArray(depth: Int) throws -> StrictJSONValue {
    offset += 1
    skipWhitespace()
    var values: [StrictJSONValue] = []
    if consume(0x5D) { return .array(values) }

    while true {
      values.append(try parseValue(depth: depth + 1))
      skipWhitespace()
      if consume(0x5D) { return .array(values) }
      guard consume(0x2C) else { throw OrchardTransportError.invalidUTF8OrJSON }
      skipWhitespace()
      guard offset < bytes.count, bytes[offset] != 0x5D else {
        throw OrchardTransportError.invalidUTF8OrJSON
      }
    }
  }

  private mutating func parseString() throws -> String {
    let start = offset
    offset += 1
    while offset < bytes.count {
      let byte = bytes[offset]
      if byte == 0x22 {
        offset += 1
        let encoded = Data(bytes[start..<offset])
        do {
          return try JSONDecoder().decode(String.self, from: encoded)
        } catch {
          throw OrchardTransportError.invalidUTF8OrJSON
        }
      }
      if byte < 0x20 { throw OrchardTransportError.invalidUTF8OrJSON }
      if byte == 0x5C {
        offset += 1
        guard offset < bytes.count else { throw OrchardTransportError.invalidUTF8OrJSON }
        let escape = bytes[offset]
        if escape == 0x75 {
          guard offset + 4 < bytes.count else { throw OrchardTransportError.invalidUTF8OrJSON }
          for index in (offset + 1)...(offset + 4) where !Self.isHex(bytes[index]) {
            throw OrchardTransportError.invalidUTF8OrJSON
          }
          offset += 5
          continue
        }
        guard [0x22, 0x5C, 0x2F, 0x62, 0x66, 0x6E, 0x72, 0x74].contains(escape) else {
          throw OrchardTransportError.invalidUTF8OrJSON
        }
      }
      offset += 1
    }
    throw OrchardTransportError.invalidUTF8OrJSON
  }

  private mutating func parseNumber() throws -> String {
    let start = offset
    _ = consume(0x2D)
    guard offset < bytes.count else { throw OrchardTransportError.invalidUTF8OrJSON }
    if consume(0x30) {
      if offset < bytes.count, Self.isDigit(bytes[offset]) {
        throw OrchardTransportError.invalidUTF8OrJSON
      }
    } else {
      guard consumeDigit(1...9) else { throw OrchardTransportError.invalidUTF8OrJSON }
      while consumeDigit(0...9) {}
    }
    if consume(0x2E) {
      guard consumeDigit(0...9) else { throw OrchardTransportError.invalidUTF8OrJSON }
      while consumeDigit(0...9) {}
    }
    if offset < bytes.count, bytes[offset] == 0x65 || bytes[offset] == 0x45 {
      offset += 1
      if offset < bytes.count, bytes[offset] == 0x2B || bytes[offset] == 0x2D { offset += 1 }
      guard consumeDigit(0...9) else { throw OrchardTransportError.invalidUTF8OrJSON }
      while consumeDigit(0...9) {}
    }
    return String(decoding: bytes[start..<offset], as: UTF8.self)
  }

  private mutating func consumeLiteral(_ value: StaticString) throws {
    let expected = Array(String(describing: value).utf8)
    guard offset + expected.count <= bytes.count,
      Array(bytes[offset..<(offset + expected.count)]) == expected
    else {
      throw OrchardTransportError.invalidUTF8OrJSON
    }
    offset += expected.count
  }

  private mutating func consume(_ byte: UInt8) -> Bool {
    guard offset < bytes.count, bytes[offset] == byte else { return false }
    offset += 1
    return true
  }

  private mutating func consumeDigit(_ range: ClosedRange<UInt8>) -> Bool {
    guard offset < bytes.count else { return false }
    let digit = bytes[offset] &- 0x30
    guard range.contains(digit) else { return false }
    offset += 1
    return true
  }

  private mutating func skipWhitespace() {
    while offset < bytes.count, [0x20, 0x09, 0x0A, 0x0D].contains(bytes[offset]) {
      offset += 1
    }
  }

  private static func isDigit(_ byte: UInt8) -> Bool { byte >= 0x30 && byte <= 0x39 }
  private static func isHex(_ byte: UInt8) -> Bool {
    isDigit(byte) || (byte >= 0x41 && byte <= 0x46) || (byte >= 0x61 && byte <= 0x66)
  }
}

struct WireHeader: Equatable {
  let version: Int32
  let type: String
  let sessionId: String
  let sequence: Int64
}

enum StrictWireJSON {
  private static let envelopeKeys: Set<String> = [
    "version", "type", "sessionId", "sequence", "payload",
  ]
  private static let payloadKeys: [String: (required: Set<String>, optional: Set<String>)] = [
    "authenticate": (["token", "clientNonce"], []),
    "hello": (["minimumVersion", "maximumVersion", "role", "capabilities"], []),
    "configure": (
      [
        "deviceProfileId", "logicalWidth", "logicalHeight", "displayScale", "appearance",
        "locale", "accessibilityEnabled",
      ], []
    ),
    "render": (["revision", "root"], []),
    "event": (["eventId", "renderRevision", "nodeId", "event"], ["value"]),
    "eventResult": (
      ["eventId", "accepted", "renderRevision"], ["errorCode", "message"]
    ),
    "diagnostic": (["code", "severity", "message"], ["nodeId", "renderRevision"]),
    "ping": (["nonce", "sentAtUnixMilliseconds"], []),
    "pong": (["nonce", "sentAtUnixMilliseconds", "respondedAtUnixMilliseconds"], []),
    "shutdown": (["disposition", "reason"], ["exitCode"]),
  ]

  static func validate(_ data: Data) throws -> WireHeader {
    var parser = try StrictJSONParser(data: data)
    let root = try parser.parse()
    let object = try root.object(named: "envelope")
    try requireExactKeys(object, required: envelopeKeys, optional: [])

    let version = try object.requiredInt32("version")
    guard version == OrchardLiveProtocol.version else {
      throw OrchardTransportError.unsupportedVersion
    }
    let type = try object.requiredString("type")
    guard let keyContract = payloadKeys[type] else {
      throw OrchardTransportError.unexpectedMessage(type)
    }
    let sessionId = try object.requiredString("sessionId")
    try validateIdentifier(sessionId, maximumBytes: 128)
    let sequence = try object.requiredInt64("sequence")
    guard sequence >= 0 else { throw OrchardTransportError.invalidEnvelope("negative sequence") }
    let payload = try object.required("payload").object(named: "payload")
    try requireExactKeys(
      payload, required: keyContract.required, optional: keyContract.optional)
    if type == RenderPayload.messageType {
      try validateRenderTreeShape(payload)
    }
    return WireHeader(version: version, type: type, sessionId: sessionId, sequence: sequence)
  }

  static func decode<P: Codable & Sendable>(
    _ type: P.Type,
    from data: Data,
    expectedType: String
  ) throws -> ProtocolEnvelope<P> {
    let header = try validate(data)
    guard header.type == expectedType else {
      throw OrchardTransportError.unexpectedMessage(header.type)
    }
    do {
      return try JSONDecoder().decode(ProtocolEnvelope<P>.self, from: data)
    } catch {
      throw OrchardTransportError.invalidEnvelope("payload shape or value type is invalid")
    }
  }

  static func encode<P: Codable & Sendable>(_ envelope: ProtocolEnvelope<P>) throws -> Data {
    let encoder = JSONEncoder()
    encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
    let data: Data
    do {
      data = try encoder.encode(envelope)
    } catch {
      throw OrchardTransportError.invalidEnvelope("outbound payload could not be encoded")
    }
    _ = try validate(data)
    guard data.count <= OrchardLiveProtocol.maximumFrameBytes else {
      throw OrchardTransportError.frameTooLarge
    }
    return data
  }

  private static func requireExactKeys(
    _ object: [String: StrictJSONValue],
    required: Set<String>,
    optional: Set<String>
  ) throws {
    let keys = Set(object.keys)
    guard required.isSubset(of: keys), keys.isSubset(of: required.union(optional)) else {
      throw OrchardTransportError.invalidEnvelope("unknown or missing JSON property")
    }
  }

  private static func validateIdentifier(_ value: String, maximumBytes: Int) throws {
    guard !value.isEmpty, value.utf8.count <= maximumBytes,
      value.utf8.allSatisfy({ byte in
        (byte >= 65 && byte <= 90) || (byte >= 97 && byte <= 122) || (byte >= 48 && byte <= 57)
          || byte == 46 || byte == 95 || byte == 45 || byte == 58
      })
    else {
      throw OrchardTransportError.invalidEnvelope("invalid identifier")
    }
  }

  private static func validateRenderTreeShape(_ payload: [String: StrictJSONValue]) throws {
    let nodeKeys: Set<String> = ["id", "kind", "properties", "events", "children"]
    var pending: [(StrictJSONValue, Int)] = [(try payload.required("root"), 1)]
    var count = 0
    while let (value, depth) = pending.popLast() {
      count += 1
      guard count <= OrchardLiveProtocol.maximumNodeCount,
        depth <= OrchardLiveProtocol.maximumTreeDepth
      else { throw OrchardTransportError.invalidEnvelope("render tree limit exceeded") }
      let node = try value.object(named: "render node")
      try requireExactKeys(node, required: nodeKeys, optional: [])
      _ = try node.requiredString("id")
      _ = try node.requiredString("kind")
      _ = try node.required("properties").object(named: "render properties")
      let events = try node.required("events").array(named: "render events")
      for event in events {
        guard case .string = event else {
          throw OrchardTransportError.invalidEnvelope("render event must be a string")
        }
      }
      let children = try node.required("children").array(named: "render children")
      for child in children.reversed() { pending.append((child, depth + 1)) }
    }
  }
}

extension StrictJSONValue {
  fileprivate func object(named name: String) throws -> [String: StrictJSONValue] {
    guard case .object(let value) = self else {
      throw OrchardTransportError.invalidEnvelope("\(name) must be an object")
    }
    return value
  }

  fileprivate func array(named name: String) throws -> [StrictJSONValue] {
    guard case .array(let value) = self else {
      throw OrchardTransportError.invalidEnvelope("\(name) must be an array")
    }
    return value
  }
}

extension Dictionary where Key == String, Value == StrictJSONValue {
  fileprivate func required(_ key: String) throws -> StrictJSONValue {
    guard let value = self[key] else {
      throw OrchardTransportError.invalidEnvelope("missing '\(key)'")
    }
    return value
  }

  fileprivate func requiredString(_ key: String) throws -> String {
    guard case .string(let value) = try required(key) else {
      throw OrchardTransportError.invalidEnvelope("'\(key)' must be a string")
    }
    return value
  }

  fileprivate func requiredInt32(_ key: String) throws -> Int32 {
    let value = try requiredInt64(key)
    guard let result = Int32(exactly: value) else {
      throw OrchardTransportError.invalidEnvelope("'\(key)' is out of range")
    }
    return result
  }

  fileprivate func requiredInt64(_ key: String) throws -> Int64 {
    guard case .number(let text) = try required(key), let value = Int64(text) else {
      throw OrchardTransportError.invalidEnvelope("'\(key)' must be an integer")
    }
    return value
  }
}
