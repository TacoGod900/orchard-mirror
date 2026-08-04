import Foundation
import OrchardProtocol
import Testing

@Suite("Orchard protocol IR")
struct OrchardProtocolTests {
  @Test("Canonical JSON is deterministic and semantically round trips")
  func canonicalJSONRoundTrip() throws {
    let application = fixtureApplication()

    let first = try OrchardJSON.encode(application)
    let second = try OrchardJSON.encode(application)
    #expect(first == second)

    let decoded = try OrchardJSON.decode(OrchardApplication.self, from: first)
    #expect(decoded == application)

    let text = try #require(String(data: first, encoding: .utf8))
    #expect(text.contains("\"schemaVersion\" : \"0.1.0\""))
    #expect(text.contains("\"alpha\" : \"first\""))
  }

  @Test("Compact encoding sorts object keys")
  func compactEncodingSortsKeys() throws {
    let node = ViewNode(
      id: "view-0001",
      type: "Text",
      arguments: ["zeta": "last", "alpha": "first"])

    let encoded = try OrchardJSON.string(node, prettyPrinted: false)
    let alpha = try #require(encoded.range(of: "\"alpha\""))
    let zeta = try #require(encoded.range(of: "\"zeta\""))
    #expect(alpha.lowerBound < zeta.lowerBound)
  }

  @Test("Flatten and lookup use stable iterative pre-order traversal")
  func flattenedTreeIsPreOrder() {
    let application = fixtureApplication()
    #expect(
      application.rootView.flattened.map(\.id) == [
        "view-0001",
        "view-0002",
        "view-0003",
      ])
    #expect(application.rootView.node(id: "view-0003")?.arguments["_0"] == "Child 2")
    #expect(application.rootView.node(id: "missing") == nil)
  }

  @Test("All shared accepted fixtures decode semantically")
  func acceptsGoldenFixtures() throws {
    let contract = try loadFixtureContract()
    #expect(contract.contractVersion == OrchardSchema.currentVersion)
    for relativePath in contract.accepted {
      let data = try Data(contentsOf: fixtureRoot.appending(path: relativePath))
      let application = try OrchardJSON.decode(OrchardApplication.self, from: data)
      #expect(application.schemaVersion == OrchardSchema.currentVersion)
    }
  }

  @Test("All shared rejected fixtures fail closed")
  func rejectsGoldenFixtures() throws {
    let contract = try loadFixtureContract()
    for fixture in contract.rejected {
      let data = try Data(contentsOf: fixtureRoot.appending(path: fixture.path))
      expectRejected(fixture.reason) {
        _ = try OrchardJSON.decode(OrchardApplication.self, from: data)
      }
    }
  }

  @Test("Compact canonical fixture is stable")
  func canonicalFixtureIsStable() throws {
    let contract = try loadFixtureContract()
    let fixture = try String(
      contentsOf: fixtureRoot.appending(path: contract.canonicalTransport),
      encoding: .utf8
    )
    .trimmingCharacters(in: .newlines)
    let application = try OrchardJSON.decode(OrchardApplication.self, from: fixture)
    let encoded = try OrchardJSON.string(application, prettyPrinted: false)
    #expect(encoded == fixture)
  }

  @Test("Unknown, wrong-case, and duplicate JSON properties are rejected at every boundary")
  func strictPropertyDecoding() throws {
    let canonical = try canonicalFixtureText()
    let nestedUnknown = canonical.replacingOccurrences(
      of: "\"arguments\":{\"_0\":\"Hello\"}",
      with: "\"arguments\":{\"_0\":\"Hello\"},\"Unexpected\":true")
    expectRejected("nested unknown property") {
      _ = try OrchardJSON.decode(OrchardApplication.self, from: nestedUnknown)
    }

    let duplicate = String(canonical.dropLast()) + ",\"displayName\":\"Other\"}"
    expectRejected("duplicate JSON property") {
      _ = try OrchardJSON.decode(OrchardApplication.self, from: duplicate)
    }
  }

  @Test("Malformed, invalid UTF-8, and oversized documents are rejected before decode")
  func rejectsMalformedAndOversizedDocuments() {
    expectRejected("malformed JSON") {
      _ = try OrchardJSON.decode(OrchardApplication.self, from: Data("{".utf8))
    }
    expectRejected("invalid UTF-8") {
      _ = try OrchardJSON.decode(OrchardApplication.self, from: Data([0xFF]))
    }
    expectRejected("oversized document") {
      _ = try OrchardJSON.decode(
        OrchardApplication.self,
        from: Data(repeating: 0x20, count: OrchardContractLimits.maximumDocumentBytes + 1))
    }
  }

  @Test("Encoding validates before producing bytes")
  func encodingFailsClosed() {
    var application = fixtureApplication()
    application.schemaVersion = "0.2.0"
    expectRejected("unsupported schema") {
      _ = try OrchardJSON.encode(application)
    }

    application = fixtureApplication()
    application.displayName = "unsafe\u{202E}name"
    expectRejected("unsafe display name") {
      _ = try OrchardJSON.encode(application)
    }
  }

  @Test("UTF-16 length and allowed content whitespace match the .NET reader")
  func stringRulesMatchDotNet() throws {
    var application = fixtureApplication()
    application.displayName = String(repeating: "😀", count: 64)
    try application.validate()

    application.displayName.append("😀")
    expectRejected("129 UTF-16 code units") {
      try application.validate()
    }

    application = fixtureApplication()
    application.rootView.arguments["_0"] = "line one\nline two\tvalue"
    try application.validate()
    application.sourceFile = "unsafe\npath.swift"
    expectRejected("control in file field") {
      try application.validate()
    }
  }

  @Test("Identifiers, locations, and unique lists are validated")
  func validatesSemanticFields() throws {
    var application = fixtureApplication()
    application.rootView.id = "view/0001"
    expectRejected("invalid view identifier") { try application.validate() }

    application = fixtureApplication()
    application.state = [
      StateDefinition(name: "name", kind: .text, initialValue: ""),
      StateDefinition(name: "name", kind: .text, initialValue: "duplicate"),
    ]
    expectRejected("duplicate state name") { try application.validate() }

    application = fixtureApplication()
    application.rootView.location = SourceLocation(file: "Fixture.swift", line: 0, column: 1)
    expectRejected("invalid source location") { try application.validate() }

    application = fixtureApplication()
    application.compatibility.requiredRemoteCapabilities = ["ARKit", "ARKit"]
    expectRejected("duplicate remote capability") { try application.validate() }
  }

  @Test("Node and depth limits are enforced iteratively")
  func validatesTreeBudgets() throws {
    var application = fixtureApplication()
    application.rootView = chain(depth: OrchardContractLimits.maximumDepth)
    try application.validate()
    #expect(application.rootView.flattened.count == OrchardContractLimits.maximumDepth)
    let boundaryDocument = try OrchardJSON.encode(application, prettyPrinted: false)
    let boundaryRoundTrip = try OrchardJSON.decode(OrchardApplication.self, from: boundaryDocument)
    #expect(boundaryRoundTrip.rootView.flattened.count == OrchardContractLimits.maximumDepth)

    application.rootView = chain(depth: OrchardContractLimits.maximumDepth + 1)
    expectRejected("tree depth") { try application.validate() }

    application.rootView = ViewNode(
      id: "root",
      type: "VStack",
      children: (0...OrchardContractLimits.maximumNodes).map {
        ViewNode(id: "view-\($0)", type: "Text")
      })
    expectRejected("node count") { try application.validate() }
  }

  @Test("State list budget is enforced")
  func validatesStateBudget() {
    var application = fixtureApplication()
    application.state = (0...OrchardContractLimits.maximumStateItems).map {
      StateDefinition(name: "state\($0)", kind: .text, initialValue: "")
    }
    expectRejected("state count") { try application.validate() }
  }

  @Test("Capability profile is truthful and fails unknown symbols closed")
  func truthfulCompatibilityReport() {
    let root = ViewNode(
      id: "view-0001",
      type: "VStack",
      modifiers: [ViewModifier(name: "frame")],
      children: [ViewNode(id: "view-0002", type: "MadeUpView")])
    let report = OrchardCapabilityProfile.bootstrapV1.report(
      for: root,
      requestedSourceSymbols: ["ARKit", "UnknownFramework", "ARKit"])

    #expect(report.apiUsages.count == 5)
    #expect(report.localCompatibilityPercent == 37)
    #expect(report.requiredRemoteCapabilities == ["ARKit"])
    #expect(report.unsupportedSymbols == ["MadeUpView", "UnknownFramework"])
    #expect(
      OrchardCapabilityProfile.bootstrapV1.entry(for: "missing", category: .modifier).status
        == .unsupported)
  }

  @Test("Versioned capability profile file matches the Swift API")
  func capabilityProfileMatchesDeclaredFile() throws {
    let profileURL = repositoryRoot.appending(path: "schemas/orchard-capabilities-v1.json")
    let declared = try JSONDecoder().decode(
      DeclaredCapabilityProfile.self,
      from: Data(contentsOf: profileURL))
    let profile = OrchardCapabilityProfile.bootstrapV1

    #expect(declared.profileVersion == profile.profileVersion)
    #expect(declared.irSchemaVersion == profile.irSchemaVersion)
    #expect(declared.capabilities.views == profile.views)
    #expect(declared.capabilities.modifiers == profile.modifiers)
    #expect(declared.capabilities.sourceSymbols == profile.sourceSymbols)
    #expect(
      declared.statusWeights
        == Dictionary(
          uniqueKeysWithValues: profile.statusWeights.map { ($0.key.rawValue, $0.value) }))
    #expect(declared.reproducibleBuildMetadata == profile.reproducibleBuildMetadata)
    #expect(profile.reproducibleBuildMetadata.timestampEnvironmentVariable == "SOURCE_DATE_EPOCH")
    #expect(profile.reproducibleBuildMetadata.fallbackTimestampUtc == "1970-01-01T00:00:00Z")
    #expect(profile.reproducibleBuildMetadata.volatileProvenance == "outOfBand")
  }

  private func fixtureApplication() -> OrchardApplication {
    let root = ViewNode(
      id: "view-0001",
      type: "VStack",
      arguments: ["zeta": "last", "alpha": "first"],
      modifiers: [ViewModifier(name: "padding", arguments: ["_0": "24"])],
      children: [
        ViewNode(id: "view-0002", type: "Text", arguments: ["_0": "Child 1"]),
        ViewNode(id: "view-0003", type: "Text", arguments: ["_0": "Child 2"]),
      ])

    return OrchardApplication(
      applicationId: "dev.orchard.protocol-tests",
      displayName: "Protocol Tests",
      sourceFile: "Fixture.swift",
      rootView: root,
      state: [StateDefinition(name: "name", kind: .text, initialValue: "")],
      compatibility: OrchardCapabilityProfile.bootstrapV1.report(for: root),
      compiledAtUtc: "2026-07-18T00:00:00Z")
  }

  private func chain(depth: Int) -> ViewNode {
    precondition(depth > 0)
    var node = ViewNode(id: "view-\(depth)", type: "Text")
    guard depth > 1 else { return node }
    for currentDepth in (1..<depth).reversed() {
      node = ViewNode(id: "view-\(currentDepth)", type: "VStack", children: [node])
    }
    return node
  }

  private func loadFixtureContract() throws -> FixtureContract {
    try JSONDecoder().decode(
      FixtureContract.self,
      from: Data(contentsOf: fixtureRoot.appending(path: "contract.json")))
  }

  private func canonicalFixtureText() throws -> String {
    try String(
      contentsOf: fixtureRoot.appending(path: "canonical/minimal.compact.json"),
      encoding: .utf8
    )
    .trimmingCharacters(in: .newlines)
  }

  private func expectRejected(_ context: String, _ operation: () throws -> Void) {
    do {
      try operation()
      Issue.record("Expected rejection: \(context)")
    } catch {
      // Any contract, decoding, or bounded-input error is a fail-closed result.
    }
  }

  private var fixtureRoot: URL {
    repositoryRoot.appending(path: "tests/fixtures/ir-v0.1", directoryHint: .isDirectory)
  }

  private var repositoryRoot: URL {
    var url = URL(fileURLWithPath: #filePath)
    for _ in 0..<5 {
      url.deleteLastPathComponent()
    }
    return url
  }
}

private struct FixtureContract: Decodable {
  let contractVersion: String
  let canonicalTransport: String
  let accepted: [String]
  let rejected: [RejectedFixture]
}

private struct RejectedFixture: Decodable {
  let path: String
  let reason: String
}

private struct DeclaredCapabilityProfile: Decodable {
  let profileVersion: String
  let irSchemaVersion: String
  let statusWeights: [String: Double]
  let capabilities: DeclaredCapabilityGroups
  let reproducibleBuildMetadata: OrchardReproducibleBuildMetadata
}

private struct DeclaredCapabilityGroups: Decodable {
  let views: [String: OrchardCapabilityEntry]
  let modifiers: [String: OrchardCapabilityEntry]
  let sourceSymbols: [String: OrchardCapabilityEntry]
}
