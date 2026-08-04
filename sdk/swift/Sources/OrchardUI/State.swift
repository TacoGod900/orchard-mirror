import Foundation
import OrchardProtocol

/// Values that can cross the Orchard state boundary without lossy coercion.
///
/// Custom conformances must be immutable value-semantic `Sendable` values. Render transactions
/// snapshot values by assignment; reference-backed interior mutation is an external side effect
/// and cannot be rolled back by Orchard.
public protocol OrchardStateValue: Sendable {
  static var orchardStateKind: StateValueKind { get }
  var orchardStateString: String { get }
}

extension String: OrchardStateValue {
  public static var orchardStateKind: StateValueKind { .text }
  public var orchardStateString: String { self }
}

extension Bool: OrchardStateValue {
  public static var orchardStateKind: StateValueKind { .flag }
  public var orchardStateString: String { self ? "true" : "false" }
}

extension Int: OrchardStateValue {
  public static var orchardStateKind: StateValueKind { .wholeNumber }
  public var orchardStateString: String { String(self) }
}

extension Double: OrchardStateValue {
  public static var orchardStateKind: StateValueKind { .decimalNumber }
  public var orchardStateString: String { String(self) }
}

@MainActor
protocol _AnyStateSlot: AnyObject {
  var name: String { get }
  var declaredName: String? { get }
  var kind: StateValueKind { get }
  var initialValue: String { get }
  var currentValue: String { get }
  func captureValue() -> Any
  func restoreValue(_ snapshot: Any)
}

@MainActor
private final class StateSlot<Value: OrchardStateValue>: _AnyStateSlot {
  let name: String
  let declaredName: String?
  let initial: Value
  var value: Value

  init(name: String, declaredName: String?, initial: Value) {
    self.name = name
    self.declaredName = declaredName
    self.initial = initial
    value = initial
  }

  var kind: StateValueKind { Value.orchardStateKind }
  var initialValue: String { initial.orchardStateString }
  var currentValue: String { value.orchardStateString }

  func captureValue() -> Any { value }

  func restoreValue(_ snapshot: Any) {
    guard let restored = snapshot as? Value else {
      preconditionFailure("Orchard restored a state snapshot using the wrong value type.")
    }
    value = restored
  }
}

@MainActor
protocol _AnyStateDescriptor: AnyObject {
  var requestedName: String? { get }
  var kind: StateValueKind { get }
  var initialValue: String { get }
  var currentValue: String { get }
  var attachedSlot: (any _AnyStateSlot)? { get }
  func attach(to slot: any _AnyStateSlot) -> Bool
  func makeSlot(named name: String) -> any _AnyStateSlot
  func captureTransactionState() -> Any
  func restoreTransactionState(_ snapshot: Any)
  func detachAndReset()
}

private struct StateStorageSnapshot<Value: OrchardStateValue> {
  let localValue: Value
  let slot: StateSlot<Value>?
}

@MainActor
private final class StateStorage<Value: OrchardStateValue>: _AnyStateDescriptor {
  private var localValue: Value
  private let originalValue: Value
  private var slot: StateSlot<Value>?
  let requestedName: String?

  init(value: Value, requestedName: String?) {
    localValue = value
    originalValue = value
    self.requestedName = requestedName
  }

  var value: Value {
    get { slot?.value ?? localValue }
    set {
      if let slot {
        slot.value = newValue
      } else {
        localValue = newValue
      }
    }
  }

  var kind: StateValueKind { Value.orchardStateKind }
  var initialValue: String { originalValue.orchardStateString }
  var currentValue: String { value.orchardStateString }
  var attachedSlot: (any _AnyStateSlot)? { slot }

  func attach(to slot: any _AnyStateSlot) -> Bool {
    guard let typedSlot = slot as? StateSlot<Value> else {
      return false
    }
    self.slot = typedSlot
    return true
  }

  func makeSlot(named name: String) -> any _AnyStateSlot {
    StateSlot(name: name, declaredName: requestedName, initial: originalValue)
  }

  func captureTransactionState() -> Any {
    StateStorageSnapshot(localValue: localValue, slot: slot)
  }

  func restoreTransactionState(_ snapshot: Any) {
    guard let restored = snapshot as? StateStorageSnapshot<Value> else {
      preconditionFailure("Orchard restored a state descriptor using the wrong value type.")
    }
    localValue = restored.localValue
    slot = restored.slot
  }

  func detachAndReset() {
    localValue = originalValue
    slot = nil
  }
}

@MainActor
protocol _AnyStateProperty {
  var _stateDescriptor: any _AnyStateDescriptor { get }
}

/// Session-persistent state for an Orchard view.
///
/// A declaration starts with local storage so ordinary view initialization remains cheap.
/// During rendering, `RenderSession` binds the declaration to a session-owned slot keyed by
/// the view's stable structural identity and reflected property identity. Consequently state
/// survives recreation of nested values, conditional removal/reinsertion, and keyed reordering
/// while the slot remains inside the session's explicitly bounded LRU retention window.
@MainActor
@propertyWrapper
public struct State<Value: OrchardStateValue>: _AnyStateProperty {
  private let storage: StateStorage<Value>

  public init(wrappedValue: Value) {
    storage = StateStorage(value: wrappedValue, requestedName: nil)
  }

  public init(wrappedValue: Value, _ name: String) {
    storage = StateStorage(value: wrappedValue, requestedName: name)
  }

  public var wrappedValue: Value {
    get { storage.value }
    nonmutating set { storage.value = newValue }
  }

  public var projectedValue: Binding<Value> {
    Binding(
      get: { storage.value },
      set: { storage.value = $0 },
      descriptor: storage
    )
  }

  var _stateDescriptor: any _AnyStateDescriptor { storage }
}

/// A main-actor-isolated two-way value projection.
///
/// Both state-backed and custom bindings participate in `change` event dispatch. A constant
/// binding intentionally accepts the event and retains its original value, matching SwiftUI's
/// read-only binding semantics.
@MainActor
@propertyWrapper
public struct Binding<Value> {
  private let getter: () -> Value
  private let setter: (Value) -> Void
  private let descriptor: (any _AnyStateDescriptor)?

  public init(get: @escaping () -> Value, set: @escaping (Value) -> Void) {
    getter = get
    setter = set
    descriptor = nil
  }

  init(
    get: @escaping () -> Value,
    set: @escaping (Value) -> Void,
    descriptor: any _AnyStateDescriptor
  ) {
    getter = get
    setter = set
    self.descriptor = descriptor
  }

  public var wrappedValue: Value {
    get { getter() }
    nonmutating set { setter(newValue) }
  }

  public var projectedValue: Binding<Value> { self }

  public static func constant(_ value: Value) -> Binding<Value> {
    Binding(get: { value }, set: { _ in })
  }

  func _register(in context: inout RenderContext) -> String? {
    guard let descriptor else {
      return nil
    }
    return context.registerAttachedState(descriptor)
  }
}
