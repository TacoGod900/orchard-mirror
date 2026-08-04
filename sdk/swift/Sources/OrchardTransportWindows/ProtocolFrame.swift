import Foundation

enum ProtocolFrame {
  static func read(from pipe: WindowsPipe) throws -> Data {
    let deadline = pipe.makeDeadline()
    let header = try pipe.readExactly(count: 4, deadline: deadline)
    let length = try declaredLength(in: header)
    guard length > 0 else { throw OrchardTransportError.invalidFrame }
    guard length <= UInt32(OrchardLiveProtocol.maximumFrameBytes) else {
      throw OrchardTransportError.frameTooLarge
    }
    return try pipe.readExactly(count: Int(length), deadline: deadline)
  }

  static func write(_ payload: Data, to pipe: WindowsPipe) throws {
    let deadline = pipe.makeDeadline()
    try pipe.writeAll(encode(payload), deadline: deadline)
  }

  static func encode(_ payload: Data) throws -> Data {
    guard !payload.isEmpty else { throw OrchardTransportError.invalidFrame }
    guard payload.count <= OrchardLiveProtocol.maximumFrameBytes,
      let length = UInt32(exactly: payload.count)
    else { throw OrchardTransportError.frameTooLarge }
    var frame = Data(capacity: payload.count + 4)
    frame.append(UInt8((length >> 24) & 0xff))
    frame.append(UInt8((length >> 16) & 0xff))
    frame.append(UInt8((length >> 8) & 0xff))
    frame.append(UInt8(length & 0xff))
    frame.append(payload)
    return frame
  }

  static func declaredLength(in header: Data) throws -> UInt32 {
    guard header.count == 4 else { throw OrchardTransportError.invalidFrame }
    let bytes = Array(header)
    return
      (UInt32(bytes[0]) << 24) | (UInt32(bytes[1]) << 16) | (UInt32(bytes[2]) << 8)
      | UInt32(bytes[3])
  }
}
