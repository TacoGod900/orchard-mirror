import Foundation

enum LiveProtocolValidation {
  private static let messageTypes: Set<String> = [
    "authenticate", "hello", "configure", "render", "event", "eventResult", "diagnostic",
    "ping", "pong", "shutdown",
  ]
  private static let nodeKinds: Set<String> = [
    "root", "text", "button", "textField", "secureField", "vStack", "hStack", "zStack",
    "group", "conditional", "spacer", "divider", "image", "scrollView", "list",
    "navigationStack", "toggle", "slider", "progressView",
  ]
  private static let propertyNames: Set<String> = [
    "text", "value", "placeholder", "systemName", "resourceName", "axis", "alignment",
    "spacing", "padding", "font", "fontSize", "fontWeight", "foregroundColor",
    "backgroundColor", "width", "height", "minWidth", "minHeight", "maxWidth", "maxHeight",
    "enabled", "hidden", "opacity", "cornerRadius", "accessibilityLabel",
    "accessibilityHint", "accessibilityValue", "accessibilityIdentifier",
  ]
  private static let events: Set<String> = [
    "press", "change", "submit", "focus", "blur", "appear", "disappear",
  ]

  static func validate(type: String) throws {
    guard messageTypes.contains(type) else { throw OrchardTransportError.unexpectedMessage(type) }
  }

  static func validate(_ payload: AuthenticatePayload) throws {
    try text(payload.token, maximumBytes: 4 * 1024)
    try identifier(payload.clientNonce, maximumBytes: 128)
  }

  static func validate(_ payload: HelloPayload) throws {
    guard payload.minimumVersion >= 1, payload.maximumVersion >= payload.minimumVersion else {
      throw OrchardTransportError.invalidEnvelope("invalid version range")
    }
    guard payload.capabilities.count <= 128,
      Set(payload.capabilities).count == payload.capabilities.count
    else { throw OrchardTransportError.invalidEnvelope("invalid capabilities") }
    for capability in payload.capabilities { try identifier(capability, maximumBytes: 128) }
  }

  static func validate(_ payload: ConfigurePayload) throws {
    try identifier(payload.deviceProfileId, maximumBytes: 128)
    guard (1...16_384).contains(payload.logicalWidth),
      (1...16_384).contains(payload.logicalHeight), payload.displayScale.isFinite,
      (0.25...8).contains(payload.displayScale)
    else { throw OrchardTransportError.invalidEnvelope("invalid device configuration") }
    guard !payload.locale.isEmpty, payload.locale.utf8.count <= 64,
      payload.locale.utf8.allSatisfy({ byte in
        (byte >= 65 && byte <= 90) || (byte >= 97 && byte <= 122) || (byte >= 48 && byte <= 57)
          || byte == 45
      })
    else { throw OrchardTransportError.invalidEnvelope("invalid locale") }
  }

  static func validate(_ payload: RenderPayload) throws {
    guard payload.revision >= 0 else {
      throw OrchardTransportError.invalidEnvelope("negative revision")
    }
    try validateTree(payload.root)
  }

  static func validate(_ payload: EventPayload) throws {
    try identifier(payload.eventId, maximumBytes: 128)
    try identifier(payload.nodeId, maximumBytes: 128)
    guard payload.renderRevision >= 0, events.contains(payload.event) else {
      throw OrchardTransportError.invalidEnvelope("invalid event")
    }
    if let value = payload.value { try text(value, maximumBytes: 64 * 1024, allowEmpty: true) }
  }

  static func validate(_ payload: EventResultPayload) throws {
    try identifier(payload.eventId, maximumBytes: 128)
    guard payload.renderRevision >= 0 else {
      throw OrchardTransportError.invalidEnvelope("negative event-result revision")
    }
    if payload.accepted {
      guard payload.errorCode == nil, payload.message == nil else {
        throw OrchardTransportError.invalidEnvelope("accepted result carries an error")
      }
    } else {
      guard let code = payload.errorCode else {
        throw OrchardTransportError.invalidEnvelope("rejected result has no error code")
      }
      try identifier(code, maximumBytes: 64)
    }
    if let message = payload.message { try text(message, maximumBytes: 16 * 1024) }
  }

  static func validate(_ payload: DiagnosticPayload) throws {
    try identifier(payload.code, maximumBytes: 64)
    guard ["info", "warning", "error", "fatal"].contains(payload.severity) else {
      throw OrchardTransportError.invalidEnvelope("invalid diagnostic severity")
    }
    try text(payload.message, maximumBytes: 16 * 1024)
    if let nodeId = payload.nodeId { try identifier(nodeId, maximumBytes: 128) }
    if let revision = payload.renderRevision, revision < 0 {
      throw OrchardTransportError.invalidEnvelope("negative diagnostic revision")
    }
  }

  static func validate(_ payload: PingPayload) throws {
    try identifier(payload.nonce, maximumBytes: 128)
    guard payload.sentAtUnixMilliseconds >= 0 else {
      throw OrchardTransportError.invalidEnvelope("negative ping timestamp")
    }
  }

  static func validate(_ payload: PongPayload) throws {
    try identifier(payload.nonce, maximumBytes: 128)
    guard payload.sentAtUnixMilliseconds >= 0,
      payload.respondedAtUnixMilliseconds >= payload.sentAtUnixMilliseconds
    else { throw OrchardTransportError.invalidEnvelope("invalid pong timestamps") }
  }

  static func validate(_ payload: ShutdownPayload) throws {
    try text(payload.reason, maximumBytes: 4 * 1024)
  }

  static func validateTree(_ root: ProtocolViewNode) throws {
    var pending: [(ProtocolViewNode, Int)] = [(root, 1)]
    var identifiers = Set<String>()
    var count = 0
    while let (node, depth) = pending.popLast() {
      count += 1
      guard count <= OrchardLiveProtocol.maximumNodeCount,
        depth <= OrchardLiveProtocol.maximumTreeDepth
      else { throw OrchardTransportError.invalidEnvelope("render tree limit exceeded") }
      try identifier(node.id, maximumBytes: 128)
      guard identifiers.insert(node.id).inserted, nodeKinds.contains(node.kind),
        node.properties.count <= 64, node.events.count <= 16,
        node.children.count <= OrchardLiveProtocol.maximumNodeCount,
        Set(node.events).count == node.events.count
      else { throw OrchardTransportError.invalidEnvelope("invalid render tree node") }
      for (name, value) in node.properties {
        guard propertyNames.contains(name) else {
          throw OrchardTransportError.invalidEnvelope("unknown render property")
        }
        try text(value, maximumBytes: 64 * 1024, allowEmpty: true)
      }
      guard node.events.allSatisfy(events.contains) else {
        throw OrchardTransportError.invalidEnvelope("unknown render event")
      }
      for child in node.children.reversed() { pending.append((child, depth + 1)) }
    }
  }

  private static func identifier(_ value: String, maximumBytes: Int) throws {
    try text(value, maximumBytes: maximumBytes)
    guard
      value.utf8.allSatisfy({ byte in
        (byte >= 65 && byte <= 90) || (byte >= 97 && byte <= 122) || (byte >= 48 && byte <= 57)
          || byte == 46 || byte == 95 || byte == 45 || byte == 58
      })
    else { throw OrchardTransportError.invalidEnvelope("invalid identifier") }
  }

  private static func text(_ value: String, maximumBytes: Int, allowEmpty: Bool = false) throws {
    guard allowEmpty || !value.isEmpty, value.utf8.count <= maximumBytes,
      !value.unicodeScalars.contains(where: { $0.value == 0 })
    else { throw OrchardTransportError.invalidEnvelope("invalid bounded string") }
  }
}
