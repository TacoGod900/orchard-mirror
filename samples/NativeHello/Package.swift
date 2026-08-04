// swift-tools-version: 6.0

import PackageDescription

let package = Package(
  name: "NativeHello",
  dependencies: [
    .package(path: "../../sdk/swift")
  ],
  targets: [
    .executableTarget(
      name: "NativeHello",
      dependencies: [
        .product(name: "OrchardProtocol", package: "swift"),
        .product(name: "OrchardTransportWindows", package: "swift"),
        .product(name: "OrchardUI", package: "swift"),
      ]
    )
  ],
  swiftLanguageModes: [.v6]
)
