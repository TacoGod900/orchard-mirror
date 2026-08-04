import Foundation

public enum OrchardTransportError: Error, Equatable, Sendable, CustomStringConvertible {
  case invalidEndpoint
  case invalidCredentials
  case invalidTimeout
  case connectionTimeout
  case operationTimeout
  case connectionClosed
  case invalidFrame
  case frameTooLarge
  case invalidUTF8OrJSON
  case invalidEnvelope(String)
  case unsupportedVersion
  case versionMismatch
  case authenticationFailed
  case sessionMismatch
  case sequenceViolation
  case unexpectedMessage(String)
  case windowsFailure(operation: String, code: UInt32)

  public var code: String {
    switch self {
    case .invalidEndpoint, .invalidCredentials: "ORT1001"
    case .invalidTimeout: "ORT1013"
    case .connectionTimeout: "ORT1002"
    case .operationTimeout: "ORT1012"
    case .connectionClosed: "ORT1009"
    case .invalidFrame: "ORP1001"
    case .frameTooLarge: "ORP1002"
    case .invalidUTF8OrJSON: "ORP1003"
    case .invalidEnvelope: "ORP1004"
    case .unsupportedVersion: "ORP1007"
    case .versionMismatch: "ORT1005"
    case .authenticationFailed: "ORT1004"
    case .sessionMismatch: "ORT1007"
    case .sequenceViolation: "ORT1008"
    case .unexpectedMessage: "ORT1006"
    case .windowsFailure: "ORT1010"
    }
  }

  public var description: String {
    switch self {
    case .invalidEnvelope(let reason): "\(code): \(reason)"
    case .unexpectedMessage(let type): "\(code): unexpected message type '\(type)'"
    case .windowsFailure(let operation, let windowsCode):
      "\(code): Windows \(operation) failed with code \(windowsCode)"
    default: code
    }
  }
}
