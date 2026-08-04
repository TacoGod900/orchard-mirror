import Foundation

public enum OrchardLiveProtocol {
  public static let version: Int32 = 1
  public static let maximumFrameBytes = 4 * 1024 * 1024
  public static let maximumJSONDepth = 384
  public static let maximumTreeDepth = 128
  public static let maximumNodeCount = 5_000
}

public enum ProtocolPeerRole: String, Codable, Sendable {
  case application
  case host
  case testAgent
}

public enum SimulatorAppearance: String, Codable, Sendable {
  case light
  case dark
}

public enum ShutdownDisposition: String, Codable, Sendable {
  case normal
  case restart
  case authenticationFailed
  case protocolError
  case hostRequested
}

public struct AuthenticatePayload: Codable, Equatable, Sendable {
  public var token: String
  public var clientNonce: String

  public init(token: String, clientNonce: String) {
    self.token = token
    self.clientNonce = clientNonce
  }
}

public struct HelloPayload: Codable, Equatable, Sendable {
  public var minimumVersion: Int32
  public var maximumVersion: Int32
  public var role: ProtocolPeerRole
  public var capabilities: [String]

  public init(
    minimumVersion: Int32 = OrchardLiveProtocol.version,
    maximumVersion: Int32 = OrchardLiveProtocol.version,
    role: ProtocolPeerRole,
    capabilities: [String] = []
  ) {
    self.minimumVersion = minimumVersion
    self.maximumVersion = maximumVersion
    self.role = role
    self.capabilities = capabilities
  }
}

public struct ConfigurePayload: Codable, Equatable, Sendable {
  public var deviceProfileId: String
  public var logicalWidth: Int32
  public var logicalHeight: Int32
  public var displayScale: Double
  public var appearance: SimulatorAppearance
  public var locale: String
  public var accessibilityEnabled: Bool

  public init(
    deviceProfileId: String,
    logicalWidth: Int32,
    logicalHeight: Int32,
    displayScale: Double,
    appearance: SimulatorAppearance,
    locale: String,
    accessibilityEnabled: Bool
  ) {
    self.deviceProfileId = deviceProfileId
    self.logicalWidth = logicalWidth
    self.logicalHeight = logicalHeight
    self.displayScale = displayScale
    self.appearance = appearance
    self.locale = locale
    self.accessibilityEnabled = accessibilityEnabled
  }
}

public struct ProtocolViewNode: Codable, Equatable, Sendable {
  public var id: String
  public var kind: String
  public var properties: [String: String]
  public var events: [String]
  public var children: [ProtocolViewNode]

  public init(
    id: String,
    kind: String,
    properties: [String: String] = [:],
    events: [String] = [],
    children: [ProtocolViewNode] = []
  ) {
    self.id = id
    self.kind = kind
    self.properties = properties
    self.events = events
    self.children = children
  }
}

public struct RenderPayload: Codable, Equatable, Sendable {
  public var revision: Int64
  public var root: ProtocolViewNode

  public init(revision: Int64, root: ProtocolViewNode) {
    self.revision = revision
    self.root = root
  }
}

public struct EventPayload: Codable, Equatable, Sendable {
  public var eventId: String
  public var renderRevision: Int64
  public var nodeId: String
  public var event: String
  public var value: String?

  public init(
    eventId: String,
    renderRevision: Int64,
    nodeId: String,
    event: String,
    value: String? = nil
  ) {
    self.eventId = eventId
    self.renderRevision = renderRevision
    self.nodeId = nodeId
    self.event = event
    self.value = value
  }
}

public struct EventResultPayload: Codable, Equatable, Sendable {
  public var eventId: String
  public var accepted: Bool
  public var renderRevision: Int64
  public var errorCode: String?
  public var message: String?

  public init(
    eventId: String,
    accepted: Bool,
    renderRevision: Int64,
    errorCode: String? = nil,
    message: String? = nil
  ) {
    self.eventId = eventId
    self.accepted = accepted
    self.renderRevision = renderRevision
    self.errorCode = errorCode
    self.message = message
  }
}

public struct DiagnosticPayload: Codable, Equatable, Sendable {
  public var code: String
  public var severity: String
  public var message: String
  public var nodeId: String?
  public var renderRevision: Int64?

  public init(
    code: String,
    severity: String,
    message: String,
    nodeId: String? = nil,
    renderRevision: Int64? = nil
  ) {
    self.code = code
    self.severity = severity
    self.message = message
    self.nodeId = nodeId
    self.renderRevision = renderRevision
  }
}

public struct PingPayload: Codable, Equatable, Sendable {
  public var nonce: String
  public var sentAtUnixMilliseconds: Int64

  public init(nonce: String, sentAtUnixMilliseconds: Int64) {
    self.nonce = nonce
    self.sentAtUnixMilliseconds = sentAtUnixMilliseconds
  }
}

public struct PongPayload: Codable, Equatable, Sendable {
  public var nonce: String
  public var sentAtUnixMilliseconds: Int64
  public var respondedAtUnixMilliseconds: Int64

  public init(
    nonce: String,
    sentAtUnixMilliseconds: Int64,
    respondedAtUnixMilliseconds: Int64
  ) {
    self.nonce = nonce
    self.sentAtUnixMilliseconds = sentAtUnixMilliseconds
    self.respondedAtUnixMilliseconds = respondedAtUnixMilliseconds
  }
}

public struct ShutdownPayload: Codable, Equatable, Sendable {
  public var disposition: ShutdownDisposition
  public var reason: String
  public var exitCode: Int32?

  public init(disposition: ShutdownDisposition, reason: String, exitCode: Int32? = nil) {
    self.disposition = disposition
    self.reason = reason
    self.exitCode = exitCode
  }
}

public enum OrchardInboundMessage: Equatable, Sendable {
  case hello(HelloPayload)
  case configure(ConfigurePayload)
  case event(EventPayload)
  case eventResult(EventResultPayload)
  case render(RenderPayload)
  case diagnostic(DiagnosticPayload)
  case ping(PingPayload)
  case pong(PongPayload)
  case shutdown(ShutdownPayload)

  public var type: String {
    switch self {
    case .hello: "hello"
    case .configure: "configure"
    case .event: "event"
    case .eventResult: "eventResult"
    case .render: "render"
    case .diagnostic: "diagnostic"
    case .ping: "ping"
    case .pong: "pong"
    case .shutdown: "shutdown"
    }
  }
}

protocol OrchardProtocolPayload: Codable, Sendable {
  static var messageType: String { get }
}

extension AuthenticatePayload: OrchardProtocolPayload { static let messageType = "authenticate" }
extension HelloPayload: OrchardProtocolPayload { static let messageType = "hello" }
extension ConfigurePayload: OrchardProtocolPayload { static let messageType = "configure" }
extension RenderPayload: OrchardProtocolPayload { static let messageType = "render" }
extension EventPayload: OrchardProtocolPayload { static let messageType = "event" }
extension EventResultPayload: OrchardProtocolPayload { static let messageType = "eventResult" }
extension DiagnosticPayload: OrchardProtocolPayload { static let messageType = "diagnostic" }
extension PingPayload: OrchardProtocolPayload { static let messageType = "ping" }
extension PongPayload: OrchardProtocolPayload { static let messageType = "pong" }
extension ShutdownPayload: OrchardProtocolPayload { static let messageType = "shutdown" }

struct ProtocolEnvelope<Payload: Codable & Sendable>: Codable, Sendable {
  var version: Int32
  var type: String
  var sessionId: String
  var sequence: Int64
  var payload: Payload
}
