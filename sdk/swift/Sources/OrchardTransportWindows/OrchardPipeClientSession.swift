import Foundation

/// An authenticated, version-1, full-duplex Orchard client session over a local Windows pipe.
/// Protocol bytes never touch standard input, output, or error.
public final class OrchardPipeClientSession: @unchecked Sendable {
  public let sessionId: String
  public let negotiatedVersion: Int32
  public let peerRole: ProtocolPeerRole
  public let peerCapabilities: [String]

  private let pipe: WindowsPipe
  private let sendGate = NSLock()
  private let receiveGate = NSLock()
  private var nextOutboundSequence: Int64 = 2
  private var nextInboundSequence: Int64 = 1

  private init(
    pipe: WindowsPipe,
    sessionId: String,
    peerRole: ProtocolPeerRole,
    peerCapabilities: [String]
  ) {
    self.pipe = pipe
    self.sessionId = sessionId
    negotiatedVersion = OrchardLiveProtocol.version
    self.peerRole = peerRole
    self.peerCapabilities = peerCapabilities
  }

  deinit { pipe.close() }

  public static func connect(
    pipeName: String,
    authenticationToken: String,
    connectionTimeoutMilliseconds: UInt32 = 10_000,
    operationTimeoutMilliseconds: UInt32 = 30_000,
    capabilities: [String] = ["render-v1", "events-v1", "ping-v1", "shutdown-v1"]
  ) throws -> OrchardPipeClientSession {
    try validateCredential(authenticationToken)
    let pipe = try WindowsPipe(
      pipeName: pipeName,
      connectionTimeoutMilliseconds: connectionTimeoutMilliseconds,
      operationTimeoutMilliseconds: operationTimeoutMilliseconds)
    var connected = false
    defer { if !connected { pipe.close() } }

    let sessionId =
      "session-\(UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased())"
    let nonce = "nonce-\(UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased())"
    let authenticate = AuthenticatePayload(token: authenticationToken, clientNonce: nonce)
    try LiveProtocolValidation.validate(authenticate)
    try sendHandshake(
      authenticate, sessionId: sessionId, sequence: 0, pipe: pipe)

    let localHello = HelloPayload(role: .application, capabilities: capabilities)
    try LiveProtocolValidation.validate(localHello)
    try sendHandshake(localHello, sessionId: sessionId, sequence: 1, pipe: pipe)

    let responseData = try ProtocolFrame.read(from: pipe)
    let header = try StrictWireJSON.validate(responseData)
    guard header.sessionId == sessionId else { throw OrchardTransportError.sessionMismatch }
    guard header.sequence == 0 else { throw OrchardTransportError.sequenceViolation }
    guard header.version == OrchardLiveProtocol.version else {
      throw OrchardTransportError.versionMismatch
    }

    if header.type == ShutdownPayload.messageType {
      let response = try StrictWireJSON.decode(
        ShutdownPayload.self, from: responseData, expectedType: ShutdownPayload.messageType)
      try LiveProtocolValidation.validate(response.payload)
      if response.payload.disposition == .authenticationFailed {
        throw OrchardTransportError.authenticationFailed
      }
      if response.payload.disposition == .protocolError {
        throw OrchardTransportError.versionMismatch
      }
      throw OrchardTransportError.unexpectedMessage(response.type)
    }

    let response = try StrictWireJSON.decode(
      HelloPayload.self, from: responseData, expectedType: HelloPayload.messageType)
    try LiveProtocolValidation.validate(response.payload)
    guard response.payload.minimumVersion <= OrchardLiveProtocol.version,
      response.payload.maximumVersion >= OrchardLiveProtocol.version
    else { throw OrchardTransportError.versionMismatch }
    guard response.payload.role == .host else {
      throw OrchardTransportError.unexpectedMessage("hello-role")
    }

    let session = OrchardPipeClientSession(
      pipe: pipe,
      sessionId: sessionId,
      peerRole: response.payload.role,
      peerCapabilities: response.payload.capabilities)
    connected = true
    return session
  }

  public func close() { pipe.close() }

  public func receive() throws -> OrchardInboundMessage {
    receiveGate.lock()
    defer { receiveGate.unlock() }
    let data = try ProtocolFrame.read(from: pipe)
    let header = try StrictWireJSON.validate(data)
    guard header.sessionId == sessionId else { throw OrchardTransportError.sessionMismatch }
    guard header.version == negotiatedVersion else { throw OrchardTransportError.versionMismatch }
    guard header.sequence == nextInboundSequence else {
      throw OrchardTransportError.sequenceViolation
    }
    let message = try decodeMessage(data, type: header.type)
    nextInboundSequence = try increment(nextInboundSequence)
    return message
  }

  public func send(render payload: RenderPayload) throws {
    try LiveProtocolValidation.validate(payload)
    try send(payload)
  }

  public func send(eventResult payload: EventResultPayload) throws {
    try LiveProtocolValidation.validate(payload)
    try send(payload)
  }

  public func send(pong payload: PongPayload) throws {
    try LiveProtocolValidation.validate(payload)
    try send(payload)
  }

  public func send(shutdown payload: ShutdownPayload) throws {
    try LiveProtocolValidation.validate(payload)
    try send(payload)
  }

  public func send(diagnostic payload: DiagnosticPayload) throws {
    try LiveProtocolValidation.validate(payload)
    try send(payload)
  }

  private func send<P: OrchardProtocolPayload>(_ payload: P) throws {
    sendGate.lock()
    defer { sendGate.unlock() }
    let envelope = ProtocolEnvelope(
      version: negotiatedVersion,
      type: P.messageType,
      sessionId: sessionId,
      sequence: nextOutboundSequence,
      payload: payload)
    let bytes = try StrictWireJSON.encode(envelope)
    try ProtocolFrame.write(bytes, to: pipe)
    nextOutboundSequence = try increment(nextOutboundSequence)
  }

  private func decodeMessage(_ data: Data, type: String) throws -> OrchardInboundMessage {
    switch type {
    case HelloPayload.messageType:
      let payload = try decode(HelloPayload.self, from: data)
      try LiveProtocolValidation.validate(payload)
      return .hello(payload)
    case ConfigurePayload.messageType:
      let payload = try decode(ConfigurePayload.self, from: data)
      try LiveProtocolValidation.validate(payload)
      return .configure(payload)
    case RenderPayload.messageType:
      let payload = try decode(RenderPayload.self, from: data)
      try LiveProtocolValidation.validate(payload)
      return .render(payload)
    case EventPayload.messageType:
      let payload = try decode(EventPayload.self, from: data)
      try LiveProtocolValidation.validate(payload)
      return .event(payload)
    case EventResultPayload.messageType:
      let payload = try decode(EventResultPayload.self, from: data)
      try LiveProtocolValidation.validate(payload)
      return .eventResult(payload)
    case DiagnosticPayload.messageType:
      let payload = try decode(DiagnosticPayload.self, from: data)
      try LiveProtocolValidation.validate(payload)
      return .diagnostic(payload)
    case PingPayload.messageType:
      let payload = try decode(PingPayload.self, from: data)
      try LiveProtocolValidation.validate(payload)
      return .ping(payload)
    case PongPayload.messageType:
      let payload = try decode(PongPayload.self, from: data)
      try LiveProtocolValidation.validate(payload)
      return .pong(payload)
    case ShutdownPayload.messageType:
      let payload = try decode(ShutdownPayload.self, from: data)
      try LiveProtocolValidation.validate(payload)
      return .shutdown(payload)
    default:
      throw OrchardTransportError.unexpectedMessage(type)
    }
  }

  private func decode<P: Codable & Sendable & OrchardProtocolPayload>(
    _ payloadType: P.Type,
    from data: Data
  ) throws -> P {
    try StrictWireJSON.decode(payloadType, from: data, expectedType: P.messageType).payload
  }

  private static func sendHandshake<P: OrchardProtocolPayload>(
    _ payload: P,
    sessionId: String,
    sequence: Int64,
    pipe: WindowsPipe
  ) throws {
    let envelope = ProtocolEnvelope(
      version: OrchardLiveProtocol.version,
      type: P.messageType,
      sessionId: sessionId,
      sequence: sequence,
      payload: payload)
    try ProtocolFrame.write(StrictWireJSON.encode(envelope), to: pipe)
  }

  private static func validateCredential(_ token: String) throws {
    guard token.utf8.count == 43,
      token.utf8.allSatisfy({ byte in
        (byte >= 65 && byte <= 90) || (byte >= 97 && byte <= 122) || (byte >= 48 && byte <= 57)
          || byte == 45 || byte == 95
      })
    else { throw OrchardTransportError.invalidCredentials }
    let base64 =
      token.replacingOccurrences(of: "-", with: "+")
      .replacingOccurrences(of: "_", with: "/") + "="
    guard let decoded = Data(base64Encoded: base64), decoded.count == 32 else {
      throw OrchardTransportError.invalidCredentials
    }
    let canonical = decoded.base64EncodedString().replacingOccurrences(of: "+", with: "-")
      .replacingOccurrences(of: "/", with: "_").trimmingCharacters(
        in: CharacterSet(charactersIn: "="))
    guard canonical == token else { throw OrchardTransportError.invalidCredentials }
  }

  private func increment(_ value: Int64) throws -> Int64 {
    guard value < Int64.max else { throw OrchardTransportError.sequenceViolation }
    return value + 1
  }
}
