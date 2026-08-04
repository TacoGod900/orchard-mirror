#if os(Windows)
  import Foundation
  import Testing
  import WinSDK

  @testable import OrchardTransportWindows

  @Suite("Orchard Windows live transport")
  struct OrchardTransportWindowsTests {
    @Test("frames use a four-byte big-endian bounded length")
    func frameHeaderIsBigEndian() throws {
      let frame = try ProtocolFrame.encode(Data([0x61, 0x62, 0x63]))
      #expect(Array(frame.prefix(4)) == [0, 0, 0, 3])
      #expect(try ProtocolFrame.declaredLength(in: frame.prefix(4)) == 3)
      #expect(throws: OrchardTransportError.invalidFrame) {
        _ = try ProtocolFrame.encode(Data())
      }
      #expect(throws: OrchardTransportError.frameTooLarge) {
        _ = try ProtocolFrame.encode(Data(count: OrchardLiveProtocol.maximumFrameBytes + 1))
      }
    }

    @Test("strict wire JSON rejects duplicate, unknown, malformed, and oversized input")
    func strictJSONFailsClosed() throws {
      let canonical = Data(
        """
        {"version":1,"type":"ping","sessionId":"session-test","sequence":2,"payload":{"nonce":"ping-1","sentAtUnixMilliseconds":1}}
        """.utf8)
      let header = try StrictWireJSON.validate(canonical)
      #expect(
        header == WireHeader(version: 1, type: "ping", sessionId: "session-test", sequence: 2))

      let duplicate = Data(
        """
        {"version":1,"version":1,"type":"ping","sessionId":"session-test","sequence":2,"payload":{"nonce":"ping-1","sentAtUnixMilliseconds":1}}
        """.utf8)
      #expect(throws: OrchardTransportError.self) { _ = try StrictWireJSON.validate(duplicate) }

      let unknown = Data(
        """
        {"version":1,"type":"ping","sessionId":"session-test","sequence":2,"payload":{"nonce":"ping-1","sentAtUnixMilliseconds":1,"extra":true}}
        """.utf8)
      #expect(throws: OrchardTransportError.self) { _ = try StrictWireJSON.validate(unknown) }
      #expect(throws: OrchardTransportError.invalidUTF8OrJSON) {
        _ = try StrictWireJSON.validate(Data([0x7B, 0x22, 0xFF, 0x22, 0x7D]))
      }
      #expect(throws: OrchardTransportError.frameTooLarge) {
        _ = try StrictWireJSON.validate(Data(count: OrchardLiveProtocol.maximumFrameBytes + 1))
      }

      let nestedUnknown = Data(
        """
        {"version":1,"type":"render","sessionId":"session-test","sequence":2,"payload":{"revision":1,"root":{"id":"root-1","kind":"root","properties":{},"events":[],"children":[],"extra":true}}}
        """.utf8)
      #expect(throws: OrchardTransportError.self) {
        _ = try StrictWireJSON.validate(nestedUnknown)
      }

      let wrongCaseEnum = Data(
        """
        {"version":1,"type":"configure","sessionId":"session-test","sequence":2,"payload":{"deviceProfileId":"iphone-15-pro","logicalWidth":393,"logicalHeight":852,"displayScale":3,"appearance":"Light","locale":"en-AU","accessibilityEnabled":false}}
        """.utf8)
      #expect(throws: OrchardTransportError.self) {
        _ = try StrictWireJSON.decode(
          ConfigurePayload.self,
          from: wrongCaseEnum,
          expectedType: ConfigurePayload.messageType)
      }
    }

    @Test("stable Swift transport errors match the .NET ORT registry")
    func errorCodesMatchDotNetRegistry() {
      #expect(OrchardTransportError.invalidEndpoint.code == "ORT1001")
      #expect(OrchardTransportError.connectionTimeout.code == "ORT1002")
      #expect(OrchardTransportError.authenticationFailed.code == "ORT1004")
      #expect(OrchardTransportError.versionMismatch.code == "ORT1005")
      #expect(OrchardTransportError.sessionMismatch.code == "ORT1007")
      #expect(OrchardTransportError.sequenceViolation.code == "ORT1008")
      #expect(OrchardTransportError.connectionClosed.code == "ORT1009")
      #expect(OrchardTransportError.operationTimeout.code == "ORT1012")
      #expect(OrchardTransportError.invalidTimeout.code == "ORT1013")
    }

    @Test("version bounds and shutdown exit codes use the .NET Int32 wire domain")
    func int32WireDomain() throws {
      let hello = HelloPayload(
        minimumVersion: 1,
        maximumVersion: .max,
        role: .application)
      try LiveProtocolValidation.validate(hello)
      let helloEnvelope = ProtocolEnvelope(
        version: OrchardLiveProtocol.version,
        type: HelloPayload.messageType,
        sessionId: "session-int32",
        sequence: 1,
        payload: hello)
      let helloData = try StrictWireJSON.encode(helloEnvelope)
      #expect(
        try StrictWireJSON.decode(
          HelloPayload.self, from: helloData, expectedType: HelloPayload.messageType
        ).payload.maximumVersion == Int32.max)

      for exitCode in [Int32.min, Int32.max] {
        let shutdown = ShutdownPayload(
          disposition: .normal,
          reason: "Boundary exit code.",
          exitCode: exitCode)
        let envelope = ProtocolEnvelope(
          version: OrchardLiveProtocol.version,
          type: ShutdownPayload.messageType,
          sessionId: "session-int32",
          sequence: 2,
          payload: shutdown)
        let data = try StrictWireJSON.encode(envelope)
        let decoded = try StrictWireJSON.decode(
          ShutdownPayload.self,
          from: data,
          expectedType: ShutdownPayload.messageType)
        #expect(decoded.payload.exitCode == exitCode)
      }

      let helloOverflow = Data(
        """
        {"version":1,"type":"hello","sessionId":"session-int32","sequence":1,"payload":{"minimumVersion":1,"maximumVersion":2147483648,"role":"application","capabilities":[]}}
        """.utf8)
      #expect(throws: OrchardTransportError.self) {
        _ = try StrictWireJSON.decode(
          HelloPayload.self,
          from: helloOverflow,
          expectedType: HelloPayload.messageType)
      }

      let shutdownOverflow = Data(
        """
        {"version":1,"type":"shutdown","sessionId":"session-int32","sequence":2,"payload":{"disposition":"normal","reason":"Overflow.","exitCode":-2147483649}}
        """.utf8)
      #expect(throws: OrchardTransportError.self) {
        _ = try StrictWireJSON.decode(
          ShutdownPayload.self,
          from: shutdownOverflow,
          expectedType: ShutdownPayload.messageType)
      }
    }

    @Test("pipe permits concurrent read and write")
    func pipeIsFullDuplex() async throws {
      let server = try TestPipeServer()
      let serverTask = Task.detached {
        try server.exchange(request: Data("ping".utf8), response: Data("pong".utf8))
      }
      let client = try WindowsPipe(
        pipeName: server.pipeName,
        connectionTimeoutMilliseconds: 2_000,
        operationTimeoutMilliseconds: 2_000)
      defer { client.close() }

      let readTask = Task.detached { try client.readExactly(count: 4) }
      try client.writeAll(Data("ping".utf8))
      let response = try await readTask.value
      try await serverTask.value
      #expect(response == Data("pong".utf8))
    }

    @Test("close cancels an outstanding overlapped read")
    func closeCancelsRead() async throws {
      let server = try TestPipeServer()
      let serverTask = Task.detached { try server.acceptAndWaitForRelease() }
      let barrier = LifecycleBarrier()
      let client = try WindowsPipe(
        pipeName: server.pipeName,
        connectionTimeoutMilliseconds: 2_000,
        operationTimeoutMilliseconds: 10_000,
        lifecycleProbe: barrier)
      let readTask = Task.detached { try client.readExactly(count: 4) }
      barrier.waitUntilOperationRegistered()

      let closeTask = Task.detached { client.close() }
      barrier.waitUntilCloseAttempted()

      let started = ContinuousClock.now
      barrier.releaseIssue()
      await closeTask.value
      do {
        _ = try await readTask.value
        Issue.record("The cancelled pipe read unexpectedly succeeded.")
      } catch let error as OrchardTransportError {
        #expect(error == .connectionClosed)
      }
      #expect(started.duration(to: .now) < .seconds(1))
      server.release()
      try await serverTask.value
    }

    @Test("one frame uses one aggregate monotonic deadline")
    func dribbleFrameTimesOutInAggregate() async throws {
      let server = try TestPipeServer()
      let serverTask = Task.detached {
        try server.dribbleFrame(payload: Data("slow".utf8), intervalMilliseconds: 90)
      }
      let client = try WindowsPipe(
        pipeName: server.pipeName,
        connectionTimeoutMilliseconds: 2_000,
        operationTimeoutMilliseconds: 200)
      defer { client.close() }

      let started = ContinuousClock.now
      do {
        _ = try ProtocolFrame.read(from: client)
        Issue.record("A dribbled frame exceeded its aggregate deadline without timing out.")
      } catch let error as OrchardTransportError {
        #expect(error == .operationTimeout)
      }
      #expect(started.duration(to: .now) < .seconds(1))
      client.close()
      try await serverTask.value
    }
  }

  private final class LifecycleBarrier: WindowsPipeLifecycleProbe, @unchecked Sendable {
    private let condition = NSCondition()
    private var operationRegistered = false
    private var closeAttempted = false
    private var issueReleased = false

    func operationRegisteredBeforeIssue() {
      condition.lock()
      operationRegistered = true
      condition.broadcast()
      while !issueReleased { condition.wait() }
      condition.unlock()
    }

    func closeWillAcquireLifecycleLock() {
      condition.lock()
      closeAttempted = true
      condition.broadcast()
      condition.unlock()
    }

    func waitUntilOperationRegistered() {
      condition.lock()
      while !operationRegistered { condition.wait() }
      condition.unlock()
    }

    func waitUntilCloseAttempted() {
      condition.lock()
      while !closeAttempted { condition.wait() }
      condition.unlock()
    }

    func releaseIssue() {
      condition.lock()
      issueReleased = true
      condition.broadcast()
      condition.unlock()
    }
  }

  private final class TestPipeServer: @unchecked Sendable {
    let pipeName: String
    private let handle: HANDLE
    private let releaseEvent: HANDLE

    init() throws {
      pipeName =
        "orchard-v1-" + UUID().uuidString.replacingOccurrences(of: "-", with: "").prefix(22)
      let path = "\\\\.\\pipe\\\(pipeName)"
      let created = path.withCString(encodedAs: UTF16.self) { pointer in
        CreateNamedPipeW(
          pointer,
          DWORD(PIPE_ACCESS_DUPLEX),
          DWORD(PIPE_TYPE_BYTE) | DWORD(PIPE_READMODE_BYTE) | DWORD(PIPE_WAIT),
          1,
          64 * 1024,
          64 * 1024,
          0,
          nil)
      }
      guard let created, created != INVALID_HANDLE_VALUE else {
        throw OrchardTransportError.windowsFailure(operation: "test-server", code: GetLastError())
      }
      guard let event = CreateEventW(nil, true, false, nil) else {
        _ = CloseHandle(created)
        throw OrchardTransportError.windowsFailure(operation: "test-event", code: GetLastError())
      }
      handle = created
      releaseEvent = event
    }

    deinit {
      _ = DisconnectNamedPipe(handle)
      _ = CloseHandle(handle)
      _ = CloseHandle(releaseEvent)
    }

    func exchange(request: Data, response: Data) throws {
      try accept()
      var received = Data(count: request.count)
      var read: DWORD = 0
      let readOK = received.withUnsafeMutableBytes { buffer in
        ReadFile(handle, buffer.baseAddress, DWORD(buffer.count), &read, nil)
      }
      guard readOK != false, Int(read) == request.count, received == request else {
        throw OrchardTransportError.windowsFailure(operation: "test-read", code: GetLastError())
      }
      var written: DWORD = 0
      let writeOK = response.withUnsafeBytes { buffer in
        WriteFile(handle, buffer.baseAddress, DWORD(buffer.count), &written, nil)
      }
      guard writeOK != false, Int(written) == response.count else {
        throw OrchardTransportError.windowsFailure(operation: "test-write", code: GetLastError())
      }
    }

    func acceptAndWaitForRelease() throws {
      try accept()
      let result = WaitForSingleObject(releaseEvent, 5_000)
      guard result == DWORD(WAIT_OBJECT_0) else {
        throw OrchardTransportError.windowsFailure(operation: "test-release", code: GetLastError())
      }
    }

    func dribbleFrame(payload: Data, intervalMilliseconds: UInt32) throws {
      try accept()
      let count = UInt32(payload.count)
      let header = Data([
        UInt8((count >> 24) & 0xff),
        UInt8((count >> 16) & 0xff),
        UInt8((count >> 8) & 0xff),
        UInt8(count & 0xff),
      ])
      guard write(header) else { return }
      for byte in payload {
        Thread.sleep(forTimeInterval: Double(intervalMilliseconds) / 1_000)
        if !write(Data([byte])) { return }
      }
    }

    func release() { _ = SetEvent(releaseEvent) }

    private func write(_ data: Data) -> Bool {
      var written: DWORD = 0
      let result = data.withUnsafeBytes { buffer in
        WriteFile(handle, buffer.baseAddress, DWORD(buffer.count), &written, nil)
      }
      return result != false && Int(written) == data.count
    }

    private func accept() throws {
      if ConnectNamedPipe(handle, nil) == false, GetLastError() != DWORD(ERROR_PIPE_CONNECTED) {
        throw OrchardTransportError.windowsFailure(operation: "test-accept", code: GetLastError())
      }
    }
  }
#endif
