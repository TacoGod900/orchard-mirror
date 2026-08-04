#if os(Windows)
  import Foundation
  import WinSDK

  struct WindowsPipeDeadline: Sendable {
    private let expiresAtMilliseconds: UInt64

    init(timeoutMilliseconds: DWORD) {
      let now = GetTickCount64()
      let timeout = UInt64(timeoutMilliseconds)
      expiresAtMilliseconds = now > UInt64.max - timeout ? UInt64.max : now + timeout
    }

    func remainingMilliseconds() throws -> DWORD {
      let now = GetTickCount64()
      guard now < expiresAtMilliseconds else { throw OrchardTransportError.operationTimeout }
      return DWORD(min(expiresAtMilliseconds - now, UInt64(DWORD.max)))
    }
  }

  protocol WindowsPipeLifecycleProbe: AnyObject {
    func operationRegisteredBeforeIssue()
    func closeWillAcquireLifecycleLock()
  }

  /// A bounded, byte-mode, overlapped client connection to a local Windows named pipe.
  /// Every operation is completed or cancelled before its backing Swift buffer leaves scope.
  final class WindowsPipe: @unchecked Sendable {
    private let handle: HANDLE
    private let operationTimeoutMilliseconds: DWORD
    private let lifecycleProbe: (any WindowsPipeLifecycleProbe)?
    private let state = NSCondition()
    private let readGate = NSLock()
    private let writeGate = NSLock()
    private var closed = false
    private var activeOperations = 0

    init(
      pipeName: String,
      connectionTimeoutMilliseconds: DWORD,
      operationTimeoutMilliseconds: DWORD,
      lifecycleProbe: (any WindowsPipeLifecycleProbe)? = nil
    ) throws {
      guard Self.isValidPipeName(pipeName) else {
        throw OrchardTransportError.invalidEndpoint
      }
      guard connectionTimeoutMilliseconds > 0, operationTimeoutMilliseconds > 0 else {
        throw OrchardTransportError.invalidTimeout
      }

      let path = "\\\\.\\pipe\\\(pipeName)"
      let waitResult = path.withCString(encodedAs: UTF16.self) { pathPointer in
        WaitNamedPipeW(pathPointer, connectionTimeoutMilliseconds)
      }
      guard waitResult != false else {
        let error = GetLastError()
        if error == DWORD(ERROR_SEM_TIMEOUT) || error == DWORD(ERROR_FILE_NOT_FOUND) {
          throw OrchardTransportError.connectionTimeout
        }
        throw OrchardTransportError.windowsFailure(operation: "wait", code: error)
      }

      let opened = path.withCString(encodedAs: UTF16.self) { pathPointer in
        CreateFileW(
          pathPointer,
          DWORD(GENERIC_READ) | DWORD(GENERIC_WRITE),
          0,
          nil,
          DWORD(OPEN_EXISTING),
          DWORD(FILE_ATTRIBUTE_NORMAL) | DWORD(FILE_FLAG_OVERLAPPED),
          nil)
      }
      guard let opened, opened != INVALID_HANDLE_VALUE else {
        let error = GetLastError()
        if error == DWORD(ERROR_PIPE_BUSY) || error == DWORD(ERROR_FILE_NOT_FOUND) {
          throw OrchardTransportError.connectionTimeout
        }
        throw OrchardTransportError.windowsFailure(operation: "connect", code: error)
      }

      handle = opened
      self.operationTimeoutMilliseconds = operationTimeoutMilliseconds
      self.lifecycleProbe = lifecycleProbe
    }

    deinit {
      close()
    }

    func close() {
      lifecycleProbe?.closeWillAcquireLifecycleLock()
      state.lock()
      guard !closed else {
        state.unlock()
        return
      }
      closed = true
      _ = CancelIoEx(handle, nil)
      while activeOperations > 0 { state.wait() }
      _ = CloseHandle(handle)
      state.unlock()
    }

    func readExactly(count: Int) throws -> Data {
      try readExactly(count: count, deadline: makeDeadline())
    }

    func readExactly(count: Int, deadline: WindowsPipeDeadline) throws -> Data {
      guard count >= 0 else { throw OrchardTransportError.invalidFrame }
      if count == 0 { return Data() }

      readGate.lock()
      defer { readGate.unlock() }
      var result = Data(count: count)
      try result.withUnsafeMutableBytes { rawBuffer in
        guard let baseAddress = rawBuffer.baseAddress else {
          throw OrchardTransportError.invalidFrame
        }
        var offset = 0
        while offset < count {
          let read = try perform(
            operation: "read",
            buffer: baseAddress.advanced(by: offset),
            count: count - offset,
            isWrite: false,
            deadline: deadline)
          guard read > 0 else { throw OrchardTransportError.connectionClosed }
          offset += read
        }
      }
      return result
    }

    func writeAll(_ data: Data) throws {
      try writeAll(data, deadline: makeDeadline())
    }

    func writeAll(_ data: Data, deadline: WindowsPipeDeadline) throws {
      guard !data.isEmpty else { return }
      writeGate.lock()
      defer { writeGate.unlock() }
      try data.withUnsafeBytes { rawBuffer in
        guard let baseAddress = rawBuffer.baseAddress else { return }
        var offset = 0
        while offset < data.count {
          let written = try perform(
            operation: "write",
            buffer: UnsafeMutableRawPointer(mutating: baseAddress.advanced(by: offset)),
            count: data.count - offset,
            isWrite: true,
            deadline: deadline)
          guard written > 0 else { throw OrchardTransportError.connectionClosed }
          offset += written
        }
      }
    }

    private func perform(
      operation: String,
      buffer: UnsafeMutableRawPointer,
      count: Int,
      isWrite: Bool,
      deadline: WindowsPipeDeadline
    ) throws -> Int {
      guard count > 0, count <= Int(UInt32.max) else {
        throw OrchardTransportError.invalidFrame
      }

      _ = try deadline.remainingMilliseconds()

      guard let completionEvent = CreateEventW(nil, true, false, nil) else {
        throw OrchardTransportError.windowsFailure(operation: "event", code: GetLastError())
      }
      defer { _ = CloseHandle(completionEvent) }

      var overlapped = OVERLAPPED()
      overlapped.hEvent = completionEvent
      var transferred: DWORD = 0

      state.lock()
      guard !closed else {
        state.unlock()
        throw OrchardTransportError.connectionClosed
      }
      activeOperations += 1
      lifecycleProbe?.operationRegisteredBeforeIssue()
      let started: Bool
      if isWrite {
        started = WriteFile(handle, buffer, DWORD(count), nil, &overlapped)
      } else {
        started = ReadFile(handle, buffer, DWORD(count), nil, &overlapped)
      }
      let startError = started ? DWORD(ERROR_SUCCESS) : GetLastError()
      state.unlock()
      defer { endOperation() }

      if started == false {
        if startError != DWORD(ERROR_IO_PENDING) {
          if startError == DWORD(ERROR_BROKEN_PIPE)
            || startError == DWORD(ERROR_PIPE_NOT_CONNECTED)
            || startError == DWORD(ERROR_HANDLE_EOF)
          {
            throw OrchardTransportError.connectionClosed
          }
          throw OrchardTransportError.windowsFailure(operation: operation, code: startError)
        }

        let remaining: DWORD
        do {
          remaining = try deadline.remainingMilliseconds()
        } catch {
          _ = CancelIoEx(handle, &overlapped)
          _ = WaitForSingleObject(completionEvent, DWORD(INFINITE))
          throw error
        }
        let waitResult = WaitForSingleObject(completionEvent, remaining)
        if waitResult == DWORD(WAIT_TIMEOUT) {
          _ = CancelIoEx(handle, &overlapped)
          _ = WaitForSingleObject(completionEvent, DWORD(INFINITE))
          throw OrchardTransportError.operationTimeout
        }
        guard waitResult == DWORD(WAIT_OBJECT_0) else {
          _ = CancelIoEx(handle, &overlapped)
          _ = WaitForSingleObject(completionEvent, DWORD(INFINITE))
          throw OrchardTransportError.windowsFailure(operation: "wait", code: GetLastError())
        }
      }

      guard GetOverlappedResult(handle, &overlapped, &transferred, false) != false else {
        let completionError = GetLastError()
        if completionError == DWORD(ERROR_BROKEN_PIPE)
          || completionError == DWORD(ERROR_PIPE_NOT_CONNECTED)
          || completionError == DWORD(ERROR_HANDLE_EOF)
        {
          throw OrchardTransportError.connectionClosed
        }
        if completionError == DWORD(ERROR_OPERATION_ABORTED) {
          throw isClosed
            ? OrchardTransportError.connectionClosed : OrchardTransportError.operationTimeout
        }
        throw OrchardTransportError.windowsFailure(operation: operation, code: completionError)
      }
      return Int(transferred)
    }

    private func endOperation() {
      state.lock()
      activeOperations -= 1
      if activeOperations == 0 { state.broadcast() }
      state.unlock()
    }

    private var isClosed: Bool {
      state.lock()
      defer { state.unlock() }
      return closed
    }

    func makeDeadline() -> WindowsPipeDeadline {
      WindowsPipeDeadline(timeoutMilliseconds: operationTimeoutMilliseconds)
    }

    private static func isValidPipeName(_ value: String) -> Bool {
      guard value.hasPrefix("orchard-v1-"), value.utf8.count == 33 else { return false }
      return value.utf8.dropFirst(11).allSatisfy { byte in
        (byte >= 65 && byte <= 90) || (byte >= 97 && byte <= 122) || (byte >= 48 && byte <= 57)
          || byte == 45 || byte == 95
      }
    }
  }
#else
  #error("OrchardTransportWindows supports Windows only.")
#endif
