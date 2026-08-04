import OrchardProtocol

public struct Font: Equatable, Sendable {
  fileprivate let protocolValue: String

  private init(_ protocolValue: String) {
    self.protocolValue = protocolValue
  }

  public static let largeTitle = Font(".largeTitle")
  public static let title = Font(".title")
  public static let title2 = Font(".title2")
  public static let headline = Font(".headline")
  public static let subheadline = Font(".subheadline")
  public static let body = Font(".body")
  public static let caption = Font(".caption")

  public static func system(size: Double, weight: Weight = .regular) -> Font {
    Font(".system(size: \(orchardNumber(size)), weight: .\(weight.rawValue))")
  }

  public enum Weight: String, Sendable {
    case ultraLight
    case thin
    case light
    case regular
    case medium
    case semibold
    case bold
    case heavy
    case black
  }
}

public struct SemanticStyle: Equatable, Sendable {
  fileprivate let protocolValue: String

  public init(named name: String) {
    protocolValue = name.first == "." ? name : ".\(name)"
  }

  public static let primary = SemanticStyle(named: "primary")
  public static let secondary = SemanticStyle(named: "secondary")
  public static let accent = SemanticStyle(named: "accent")
  public static let red = SemanticStyle(named: "red")
  public static let orange = SemanticStyle(named: "orange")
  public static let yellow = SemanticStyle(named: "yellow")
  public static let green = SemanticStyle(named: "green")
  public static let blue = SemanticStyle(named: "blue")
  public static let purple = SemanticStyle(named: "purple")
  public static let clear = SemanticStyle(named: "clear")
}

public struct Alignment: Equatable, Sendable {
  fileprivate let protocolValue: String

  private init(_ value: String) {
    protocolValue = value
  }

  public static let center = Alignment(".center")
  public static let leading = Alignment(".leading")
  public static let trailing = Alignment(".trailing")
  public static let top = Alignment(".top")
  public static let bottom = Alignment(".bottom")
  public static let topLeading = Alignment(".topLeading")
  public static let topTrailing = Alignment(".topTrailing")
  public static let bottomLeading = Alignment(".bottomLeading")
  public static let bottomTrailing = Alignment(".bottomTrailing")
}

public struct EdgeSet: OptionSet, Sendable {
  public let rawValue: Int

  public init(rawValue: Int) {
    self.rawValue = rawValue
  }

  public static let top = EdgeSet(rawValue: 1 << 0)
  public static let leading = EdgeSet(rawValue: 1 << 1)
  public static let bottom = EdgeSet(rawValue: 1 << 2)
  public static let trailing = EdgeSet(rawValue: 1 << 3)
  public static let horizontal: EdgeSet = [.leading, .trailing]
  public static let vertical: EdgeSet = [.top, .bottom]
  public static let all: EdgeSet = [.top, .leading, .bottom, .trailing]

  fileprivate var protocolValue: String {
    if self == .all { return ".all" }
    if self == .horizontal { return ".horizontal" }
    if self == .vertical { return ".vertical" }

    var names: [String] = []
    if contains(.top) { names.append(".top") }
    if contains(.leading) { names.append(".leading") }
    if contains(.bottom) { names.append(".bottom") }
    if contains(.trailing) { names.append(".trailing") }
    return "[\(names.joined(separator: ", "))]"
  }
}

@MainActor
public struct ModifiedView<Content: View>: View {
  public typealias Body = Never
  private let content: Content
  private let modifier: ViewModifier

  init(content: Content, modifier: ViewModifier) {
    self.content = content
    self.modifier = modifier
  }

  public func _render(in context: inout RenderContext) -> ViewNode {
    context.withCompositionScope { nestedContext in
      var node = content._render(in: &nestedContext)
      guard nestedContext.canContinueRendering else {
        return nestedContext.abortedNode()
      }
      node.modifiers.append(modifier)
      return node
    }
  }
}

extension ModifiedView: _ExplicitlyIdentifiedView where Content: _ExplicitlyIdentifiedView {
  var _explicitIdentityKey: String { content._explicitIdentityKey }
}

/// A structural identity boundary. In collection builders this key replaces positional
/// identity, so state and event targets follow the logical item through reordering.
@MainActor
public struct IdentifiedView<Content: View>: View, _ExplicitlyIdentifiedView {
  public typealias Body = Never
  private let content: Content
  let _explicitIdentityKey: String

  init(content: Content, key: String) {
    self.content = content
    _explicitIdentityKey = key
  }

  public func _render(in context: inout RenderContext) -> ViewNode {
    context.withCompositionScope { nestedContext in
      nestedContext.validateExplicitIdentity(_explicitIdentityKey)
      let segment =
        "identity\(_explicitIdentityKey.utf8.count):\(_explicitIdentityKey)"
      return nestedContext.withPathSegment(
        segment,
        fallback: nestedContext.abortedNode()
      ) { contentContext in
        content._render(in: &contentContext)
      }
    }
  }
}

@MainActor
extension View {
  public func id<ID: OrchardViewID>(_ key: ID) -> IdentifiedView<Self> {
    IdentifiedView(content: self, key: key.orchardViewIdentity)
  }

  public func font(_ font: Font) -> ModifiedView<Self> {
    modifier("font", ["_0": font.protocolValue])
  }

  public func foregroundStyle(_ style: SemanticStyle) -> ModifiedView<Self> {
    modifier("foregroundStyle", ["_0": style.protocolValue])
  }

  public func foregroundColor(_ style: SemanticStyle) -> ModifiedView<Self> {
    modifier("foregroundColor", ["_0": style.protocolValue])
  }

  public func tint(_ style: SemanticStyle) -> ModifiedView<Self> {
    modifier("tint", ["_0": style.protocolValue])
  }

  public func background(_ style: SemanticStyle) -> ModifiedView<Self> {
    modifier("background", ["_0": style.protocolValue])
  }

  public func padding(_ length: Double = 16) -> ModifiedView<Self> {
    modifier("padding", ["_0": orchardNumber(length)])
  }

  public func padding(_ edges: EdgeSet, _ length: Double? = nil) -> ModifiedView<Self> {
    var arguments = ["edges": edges.protocolValue]
    if let length { arguments["_0"] = orchardNumber(length) }
    return modifier("padding", arguments)
  }

  public func frame(
    width: Double? = nil,
    height: Double? = nil,
    alignment: Alignment = .center
  ) -> ModifiedView<Self> {
    var arguments = ["alignment": alignment.protocolValue]
    if let width { arguments["width"] = orchardNumber(width) }
    if let height { arguments["height"] = orchardNumber(height) }
    return modifier("frame", arguments)
  }

  public func cornerRadius(_ radius: Double) -> ModifiedView<Self> {
    modifier("cornerRadius", ["_0": orchardNumber(radius)])
  }

  public func opacity(_ value: Double) -> ModifiedView<Self> {
    modifier("opacity", ["_0": orchardNumber(value)])
  }

  public func disabled(_ isDisabled: Bool) -> ModifiedView<Self> {
    modifier("disabled", ["_0": isDisabled ? "true" : "false"])
  }

  public func hidden(_ isHidden: Bool = true) -> ModifiedView<Self> {
    modifier("hidden", ["_0": isHidden ? "true" : "false"])
  }

  public func navigationTitle(_ title: String) -> ModifiedView<Self> {
    modifier("navigationTitle", ["_0": title])
  }

  public func accessibilityLabel(_ label: String) -> ModifiedView<Self> {
    modifier("accessibilityLabel", ["_0": label])
  }

  public func accessibilityHint(_ hint: String) -> ModifiedView<Self> {
    modifier("accessibilityHint", ["_0": hint])
  }

  public func lineLimit(_ limit: Int?) -> ModifiedView<Self> {
    modifier("lineLimit", ["_0": limit.map(String.init) ?? "nil"])
  }

  private func modifier(_ name: String, _ arguments: [String: String]) -> ModifiedView<Self> {
    ModifiedView(content: self, modifier: ViewModifier(name: name, arguments: arguments))
  }
}
