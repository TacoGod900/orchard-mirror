import OrchardProtocol

public enum HorizontalAlignment: String, Sendable {
  case leading
  case center
  case trailing
}

public enum VerticalAlignment: String, Sendable {
  case top
  case center
  case bottom
  case firstTextBaseline
  case lastTextBaseline
}

public enum StackAlignment: String, Sendable {
  case topLeading
  case top
  case topTrailing
  case leading
  case center
  case trailing
  case bottomLeading
  case bottom
  case bottomTrailing
}

public enum ScrollAxis: String, Sendable {
  case vertical
  case horizontal
}

@MainActor
public struct Text: View {
  public typealias Body = Never
  private let content: String

  public init(_ content: String) {
    self.content = content
  }

  public init(verbatim content: String) {
    self.content = content
  }

  public func _render(in context: inout RenderContext) -> ViewNode {
    ViewNode(
      id: context.allocateNodeID(),
      type: "Text",
      arguments: ["_0": content]
    )
  }
}

@MainActor
public struct Image: View {
  public typealias Body = Never
  private let name: String
  private let isSystemName: Bool

  public init(_ name: String) {
    self.name = name
    isSystemName = false
  }

  public init(systemName: String) {
    name = systemName
    isSystemName = true
  }

  public func _render(in context: inout RenderContext) -> ViewNode {
    var arguments = ["_0": name]
    if isSystemName {
      arguments["systemName"] = name
    }
    return ViewNode(
      id: context.allocateNodeID(),
      type: "Image",
      arguments: arguments
    )
  }
}

@MainActor
public struct Button: View {
  public typealias Body = Never
  private let title: String
  private let semanticEvent: String
  private let action: () -> Void

  public init(
    _ title: String,
    event: String = "press",
    action: @escaping () -> Void
  ) {
    self.title = title
    semanticEvent = event
    self.action = action
  }

  public func _render(in context: inout RenderContext) -> ViewNode {
    let id = context.allocateNodeID()
    context.registerEvent(nodeID: id, name: semanticEvent) { _ in action() }
    return ViewNode(
      id: id,
      type: "Button",
      arguments: ["_0": title],
      events: [ViewEvent(name: semanticEvent, body: "\(id).\(semanticEvent)")]
    )
  }
}

@MainActor
public struct TextField: View {
  public typealias Body = Never
  private let title: String
  private let text: Binding<String>

  public init(_ title: String, text: Binding<String>) {
    self.title = title
    self.text = text
  }

  public func _render(in context: inout RenderContext) -> ViewNode {
    let id = context.allocateNodeID()
    var arguments = ["_0": title, "value": text.wrappedValue]
    if let stateName = text._register(in: &context) {
      arguments["text"] = "$\(stateName)"
    }
    context.registerEvent(nodeID: id, name: "change") { value in
      text.wrappedValue = try orchardValidatedTextChange(value)
    }
    return ViewNode(
      id: id,
      type: "TextField",
      arguments: arguments,
      events: [ViewEvent(name: "change", body: "\(id).change")]
    )
  }
}

@MainActor
public struct SecureField: View {
  public typealias Body = Never
  private let title: String
  private let text: Binding<String>

  public init(_ title: String, text: Binding<String>) {
    self.title = title
    self.text = text
  }

  public func _render(in context: inout RenderContext) -> ViewNode {
    let id = context.allocateNodeID()
    var arguments = ["_0": title, "value": text.wrappedValue]
    if let stateName = text._register(in: &context) {
      arguments["text"] = "$\(stateName)"
    }
    context.registerEvent(nodeID: id, name: "change") { value in
      text.wrappedValue = try orchardValidatedTextChange(value)
    }
    return ViewNode(
      id: id,
      type: "SecureField",
      arguments: arguments,
      events: [ViewEvent(name: "change", body: "\(id).change")]
    )
  }
}

private func orchardValidatedTextChange(_ value: String?) throws -> String {
  guard let value else { throw EventDispatchError.missingValue(event: "change") }
  guard value.utf16.count <= OrchardContractLimits.maximumTextLength else {
    throw EventDispatchError.invalidValue(event: "change", value: value)
  }
  for scalar in value.unicodeScalars {
    let codePoint = scalar.value
    if codePoint == 0x061C || codePoint == 0x200E || codePoint == 0x200F
      || (0x202A...0x202E).contains(codePoint) || (0x2066...0x2069).contains(codePoint)
      || (scalar.properties.generalCategory == .control
        && codePoint != 0x09 && codePoint != 0x0A && codePoint != 0x0D)
    {
      throw EventDispatchError.invalidValue(event: "change", value: value)
    }
  }
  return value
}

@MainActor
public struct Toggle: View {
  public typealias Body = Never
  private let title: String
  private let isOn: Binding<Bool>

  public init(_ title: String, isOn: Binding<Bool>) {
    self.title = title
    self.isOn = isOn
  }

  public func _render(in context: inout RenderContext) -> ViewNode {
    let id = context.allocateNodeID()
    var arguments = [
      "_0": title,
      "value": isOn.wrappedValue ? "true" : "false",
    ]
    if let stateName = isOn._register(in: &context) {
      arguments["isOn"] = "$\(stateName)"
    }
    context.registerEvent(nodeID: id, name: "change") { value in
      guard let value else { throw EventDispatchError.missingValue(event: "change") }
      switch value {
      case "true": isOn.wrappedValue = true
      case "false": isOn.wrappedValue = false
      default: throw EventDispatchError.invalidValue(event: "change", value: value)
      }
    }
    return ViewNode(
      id: id,
      type: "Toggle",
      arguments: arguments,
      events: [ViewEvent(name: "change", body: "\(id).change")]
    )
  }
}

@MainActor
public struct Spacer: View {
  public typealias Body = Never
  private let minimumLength: Double?

  public init(minLength: Double? = nil) {
    minimumLength = minLength
  }

  public func _render(in context: inout RenderContext) -> ViewNode {
    let arguments = minimumLength.map { ["minLength": orchardNumber($0)] } ?? [:]
    return ViewNode(id: context.allocateNodeID(), type: "Spacer", arguments: arguments)
  }
}

@MainActor
public struct Divider: View {
  public typealias Body = Never

  public init() {}

  public func _render(in context: inout RenderContext) -> ViewNode {
    ViewNode(id: context.allocateNodeID(), type: "Divider")
  }
}

@MainActor
public struct ProgressView: View {
  public typealias Body = Never
  private let title: String?
  private let value: Double?

  public init(_ title: String? = nil, value: Double? = nil) {
    self.title = title
    self.value = value
  }

  public func _render(in context: inout RenderContext) -> ViewNode {
    var arguments: [String: String] = [:]
    if let title { arguments["_0"] = title }
    if let value { arguments["value"] = orchardNumber(value) }
    return ViewNode(id: context.allocateNodeID(), type: "ProgressView", arguments: arguments)
  }
}

@MainActor
public struct VStack: View {
  public typealias Body = Never
  private let alignment: HorizontalAlignment
  private let spacing: Double?
  private let content: ViewBuilderContent

  public init(
    alignment: HorizontalAlignment = .center,
    spacing: Double? = nil,
    @ViewBuilder content: () -> ViewBuilderContent
  ) {
    self.alignment = alignment
    self.spacing = spacing
    self.content = content()
  }

  public func _render(in context: inout RenderContext) -> ViewNode {
    var arguments = ["alignment": ".\(alignment.rawValue)"]
    if let spacing { arguments["spacing"] = orchardNumber(spacing) }
    return makeContainerNode(
      type: "VStack", arguments: arguments, content: content, context: &context)
  }
}

@MainActor
public struct HStack: View {
  public typealias Body = Never
  private let alignment: VerticalAlignment
  private let spacing: Double?
  private let content: ViewBuilderContent

  public init(
    alignment: VerticalAlignment = .center,
    spacing: Double? = nil,
    @ViewBuilder content: () -> ViewBuilderContent
  ) {
    self.alignment = alignment
    self.spacing = spacing
    self.content = content()
  }

  public func _render(in context: inout RenderContext) -> ViewNode {
    var arguments = ["alignment": ".\(alignment.rawValue)"]
    if let spacing { arguments["spacing"] = orchardNumber(spacing) }
    return makeContainerNode(
      type: "HStack", arguments: arguments, content: content, context: &context)
  }
}

@MainActor
public struct ZStack: View {
  public typealias Body = Never
  private let alignment: StackAlignment
  private let content: ViewBuilderContent

  public init(
    alignment: StackAlignment = .center,
    @ViewBuilder content: () -> ViewBuilderContent
  ) {
    self.alignment = alignment
    self.content = content()
  }

  public func _render(in context: inout RenderContext) -> ViewNode {
    makeContainerNode(
      type: "ZStack",
      arguments: ["alignment": ".\(alignment.rawValue)"],
      content: content,
      context: &context
    )
  }
}

@MainActor
public struct NavigationStack: View {
  public typealias Body = Never
  private let content: ViewBuilderContent

  public init(@ViewBuilder content: () -> ViewBuilderContent) {
    self.content = content()
  }

  public func _render(in context: inout RenderContext) -> ViewNode {
    makeContainerNode(type: "NavigationStack", content: content, context: &context)
  }
}

@MainActor
public struct ScrollView: View {
  public typealias Body = Never
  private let axis: ScrollAxis
  private let showsIndicators: Bool
  private let content: ViewBuilderContent

  public init(
    _ axis: ScrollAxis = .vertical,
    showsIndicators: Bool = true,
    @ViewBuilder content: () -> ViewBuilderContent
  ) {
    self.axis = axis
    self.showsIndicators = showsIndicators
    self.content = content()
  }

  public func _render(in context: inout RenderContext) -> ViewNode {
    makeContainerNode(
      type: "ScrollView",
      arguments: [
        "axis": ".\(axis.rawValue)",
        "showsIndicators": showsIndicators ? "true" : "false",
      ],
      content: content,
      context: &context
    )
  }
}

@MainActor
public struct List: View {
  public typealias Body = Never
  private let content: ViewBuilderContent

  public init(@ViewBuilder content: () -> ViewBuilderContent) {
    self.content = content()
  }

  public func _render(in context: inout RenderContext) -> ViewNode {
    makeContainerNode(type: "List", content: content, context: &context)
  }
}

@MainActor
public struct Form: View {
  public typealias Body = Never
  private let content: ViewBuilderContent

  public init(@ViewBuilder content: () -> ViewBuilderContent) {
    self.content = content()
  }

  public func _render(in context: inout RenderContext) -> ViewNode {
    makeContainerNode(type: "Form", content: content, context: &context)
  }
}

@MainActor
public struct Section: View {
  public typealias Body = Never
  private let title: String?
  private let content: ViewBuilderContent

  public init(_ title: String? = nil, @ViewBuilder content: () -> ViewBuilderContent) {
    self.title = title
    self.content = content()
  }

  public func _render(in context: inout RenderContext) -> ViewNode {
    let arguments = title.map { ["_0": $0] } ?? [:]
    return makeContainerNode(
      type: "Section", arguments: arguments, content: content, context: &context)
  }
}

@MainActor
public struct Group: View {
  public typealias Body = Never
  private let content: ViewBuilderContent

  public init(@ViewBuilder content: () -> ViewBuilderContent) {
    self.content = content()
  }

  public func _render(in context: inout RenderContext) -> ViewNode {
    makeContainerNode(type: "Group", content: content, context: &context)
  }
}
