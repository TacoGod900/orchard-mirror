import Foundation
import OrchardProtocol
import OrchardTransportWindows
import OrchardUI

@MainActor
private struct ContentView: View {
  @State private var name = ""
  @State private var notificationsEnabled = true

  var body: some View {
    NavigationStack {
      VStack(spacing: 18) {
        Image(systemName: "leaf.fill")
          .foregroundStyle(.green)

        Text("Welcome to Orchard")
          .font(.largeTitle)

        Text(name.isEmpty ? "A native Swift app running on Windows" : "Hello, \(name)")
          .font(.subheadline)
          .foregroundStyle(.secondary)

        TextField("Your name", text: $name)

        Toggle("Enable preview notifications", isOn: $notificationsEnabled)

        Button("Continue") {
          name = "Orchard Developer"
        }
        .accessibilityLabel("Continue to the sample application")
      }
      .padding(24)
      .navigationTitle("Hello Orchard")
    }
  }
}

private enum LiveAdapterError: Error {
  case unsupportedNode(String)
  case invalidBootstrap
}

@main
private enum NativeHello {
  private static let metadata = ApplicationMetadata(
    applicationID: "dev.orchard.native-hello",
    displayName: "Native Hello",
    sourceFile: "Sources/NativeHello/main.swift",
    compiledAtUTC: "2026-07-18T00:00:00Z")

  @MainActor
  static func main() {
    let arguments = Array(CommandLine.arguments.dropFirst())
    if arguments.isEmpty || arguments == ["--emit-ir"] {
      emitDeterministicIR()
      return
    }
    if arguments == ["--orchard-test-no-connect"] {
      runNoConnectFixture()
      return
    }

    let allowedLiveArguments: Set<String> = [
      "--orchard-live", "--orchard-test-exit-after-render", "--orchard-test-replay-after-event",
      "--orchard-test-zero-first-render", "--orchard-test-rejected-result-rollback",
      "--orchard-test-exit-on-shutdown", "--orchard-test-adaptation-failure-after-event",
    ]
    guard arguments.contains("--orchard-live"),
      arguments.allSatisfy(allowedLiveArguments.contains)
    else {
      writeStatus("NativeHello: invalid invocation")
      Foundation.exit(64)
    }

    runLive(
      exitAfterFirstRender: arguments.contains("--orchard-test-exit-after-render"),
      replayAfterEvent: arguments.contains("--orchard-test-replay-after-event"),
      zeroFirstRender: arguments.contains("--orchard-test-zero-first-render"),
      rejectedResultRollback: arguments.contains("--orchard-test-rejected-result-rollback"),
      exitOnShutdown: arguments.contains("--orchard-test-exit-on-shutdown"),
      adaptationFailureAfterEvent: arguments.contains(
        "--orchard-test-adaptation-failure-after-event"))
  }

  private static func runNoConnectFixture() {
    guard readLine(strippingNewline: true) == "ORCHARD-LIVE-V1",
      readLine(strippingNewline: true) != nil,
      var authenticationToken = readLine(strippingNewline: true)
    else {
      Foundation.exit(65)
    }
    authenticationToken = String(repeating: "0", count: authenticationToken.count)
    _ = authenticationToken
    Thread.sleep(forTimeInterval: 10)
  }

  @MainActor
  private static func emitDeterministicIR() {
    do {
      let application = try OrchardRenderer.render(ContentView(), metadata: metadata)
      print(try OrchardJSON.string(application))
    } catch {
      writeStatus("NativeHello: deterministic render failed")
      Foundation.exit(70)
    }
  }

  @MainActor
  private static func runLive(
    exitAfterFirstRender: Bool,
    replayAfterEvent: Bool,
    zeroFirstRender: Bool,
    rejectedResultRollback: Bool,
    exitOnShutdown: Bool,
    adaptationFailureAfterEvent: Bool
  ) {
    guard readLine(strippingNewline: true) == "ORCHARD-LIVE-V1",
      let pipeName = readLine(strippingNewline: true),
      var authenticationToken = readLine(strippingNewline: true)
    else {
      writeStatus("NativeHello: live bootstrap was invalid")
      Foundation.exit(65)
    }

    let transport: OrchardPipeClientSession
    do {
      transport = try OrchardPipeClientSession.connect(
        pipeName: pipeName,
        authenticationToken: authenticationToken,
        connectionTimeoutMilliseconds: 5_000,
        operationTimeoutMilliseconds: 5_000)
      authenticationToken = String(repeating: "0", count: authenticationToken.count)
    } catch let error as OrchardTransportError {
      authenticationToken = String(repeating: "0", count: authenticationToken.count)
      writeStatus("NativeHello live transport failed: \(error.code)")
      Foundation.exit(66)
    } catch {
      authenticationToken = String(repeating: "0", count: authenticationToken.count)
      writeStatus("NativeHello live transport failed")
      Foundation.exit(66)
    }
    defer { transport.close() }

    let renderSession = RenderSession(root: ContentView(), metadata: metadata)
    var configured = false
    var currentSnapshot: RenderSnapshot?

    do {
      while true {
        switch try transport.receive() {
        case .configure:
          if !configured {
            let snapshot = try renderSession.render()
            currentSnapshot = snapshot
            configured = true
            var firstRender = try liveRender(snapshot)
            if zeroFirstRender { firstRender.revision = 0 }
            try transport.send(render: firstRender)
            if exitAfterFirstRender { Foundation.exit(86) }
          }

        case .event(let event):
          guard configured else {
            try transport.send(
              eventResult: EventResultPayload(
                eventId: event.eventId,
                accepted: false,
                renderRevision: renderSession.revision,
                errorCode: "ORL1001",
                message: "The application has not published a render."))
            continue
          }
          if rejectedResultRollback {
            try transport.send(
              eventResult: EventResultPayload(
                eventId: event.eventId,
                accepted: false,
                renderRevision: 0,
                errorCode: "ORL1002",
                message: "The event was rejected."))
            continue
          }
          let snapshot: RenderSnapshot
          var replacement: RenderPayload
          do {
            snapshot = try renderSession.dispatch(
              revision: event.renderRevision,
              nodeID: event.nodeId,
              event: event.event,
              value: event.value)
            if adaptationFailureAfterEvent {
              throw LiveAdapterError.unsupportedNode("fixture")
            }
            replacement = try liveRender(snapshot)
          } catch let error as EventDispatchError {
            try transport.send(
              eventResult: EventResultPayload(
                eventId: event.eventId,
                accepted: false,
                renderRevision: renderSession.revision,
                errorCode: dispatchErrorCode(error),
                message: "The event was rejected."))
            continue
          }
          currentSnapshot = snapshot
          let publishedRevision = replayAfterEvent ? event.renderRevision : snapshot.revision
          replacement.revision = publishedRevision
          try transport.send(
            eventResult: EventResultPayload(
              eventId: event.eventId,
              accepted: true,
              renderRevision: publishedRevision))
          try transport.send(render: replacement)

        case .ping(let ping):
          let now = Int64(Date().timeIntervalSince1970 * 1_000)
          try transport.send(
            pong: PongPayload(
              nonce: ping.nonce,
              sentAtUnixMilliseconds: ping.sentAtUnixMilliseconds,
              respondedAtUnixMilliseconds: max(ping.sentAtUnixMilliseconds, now)))

        case .shutdown:
          if exitOnShutdown { Foundation.exit(86) }
          try transport.send(
            shutdown: ShutdownPayload(
              disposition: .normal,
              reason: "Native application stopped.",
              exitCode: 0))
          return

        case .hello, .render, .eventResult, .diagnostic, .pong:
          throw OrchardTransportError.unexpectedMessage("invalid-direction")
        }
        _ = currentSnapshot
      }
    } catch let error as OrchardTransportError {
      writeStatus("NativeHello live session ended: \(error.code)")
      Foundation.exit(67)
    } catch {
      writeStatus("NativeHello live session failed")
      Foundation.exit(70)
    }
  }

  private static func dispatchErrorCode(_ error: Error) -> String {
    guard let dispatch = error as? EventDispatchError else { return "ORL1099" }
    switch dispatch {
    case .staleRevision: return "ORL1002"
    case .unknownNode: return "ORL1003"
    case .unknownEvent: return "ORL1004"
    case .missingValue: return "ORL1005"
    case .invalidValue: return "ORL1006"
    case .renderRequired: return "ORL1001"
    case .renderRevisionOverflow: return "ORL1007"
    case .reentrantOperation: return "ORL1008"
    }
  }

  private static func liveRender(_ snapshot: RenderSnapshot) throws -> RenderPayload {
    RenderPayload(revision: snapshot.revision, root: try adapt(snapshot.application.rootView))
  }

  private static func adapt(_ node: ViewNode) throws -> ProtocolViewNode {
    let kind: String
    switch node.type {
    case "Text": kind = "text"
    case "Button": kind = "button"
    case "TextField": kind = "textField"
    case "SecureField": kind = "secureField"
    case "VStack": kind = "vStack"
    case "HStack": kind = "hStack"
    case "ZStack": kind = "zStack"
    case "Group": kind = "group"
    case "Spacer": kind = "spacer"
    case "Divider": kind = "divider"
    case "Image": kind = "image"
    case "ScrollView": kind = "scrollView"
    case "List": kind = "list"
    case "NavigationStack": kind = "navigationStack"
    case "Toggle": kind = "toggle"
    case "ProgressView": kind = "progressView"
    default: throw LiveAdapterError.unsupportedNode(node.type)
    }

    var properties: [String: String] = [:]
    for (name, value) in node.arguments {
      switch name {
      case "_0":
        if kind == "text" || kind == "button" || kind == "toggle" {
          properties["text"] = value
        } else if kind == "textField" || kind == "secureField" {
          properties["placeholder"] = value
        } else if kind == "image", node.arguments["systemName"] == nil {
          properties["resourceName"] = value
        }
      case "value", "systemName", "axis", "alignment", "spacing":
        properties[name] = value
      default: break
      }
    }
    for modifier in node.modifiers {
      let first = modifier.arguments["_0"]
      switch modifier.name {
      case "font": properties["font"] = first
      case "foregroundStyle", "foregroundColor", "tint":
        properties["foregroundColor"] = first
      case "background": properties["backgroundColor"] = first
      case "padding": properties["padding"] = first
      case "cornerRadius": properties["cornerRadius"] = first
      case "opacity": properties["opacity"] = first
      case "disabled": properties["enabled"] = first == "true" ? "false" : "true"
      case "hidden": properties["hidden"] = first
      case "accessibilityLabel": properties["accessibilityLabel"] = first
      case "accessibilityHint": properties["accessibilityHint"] = first
      case "frame":
        properties["width"] = modifier.arguments["width"]
        properties["height"] = modifier.arguments["height"]
        properties["alignment"] = modifier.arguments["alignment"]
      default: break
      }
    }
    return ProtocolViewNode(
      id: node.id,
      kind: kind,
      properties: properties,
      events: node.events.map(\.name),
      children: try node.children.map(adapt))
  }

  private static func writeStatus(_ message: String) {
    guard let data = (message + "\n").data(using: .utf8) else { return }
    FileHandle.standardError.write(data)
  }
}
