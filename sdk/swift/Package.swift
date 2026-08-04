// swift-tools-version: 6.0

import PackageDescription

let package = Package(
  name: "OrchardSwiftSDK",
  products: [
    .library(name: "OrchardProtocol", targets: ["OrchardProtocol"]),
    .library(name: "OrchardTransportWindows", targets: ["OrchardTransportWindows"]),
    .library(name: "OrchardUI", targets: ["OrchardUI"]),
  ],
  targets: [
    .target(name: "OrchardProtocol"),
    .target(
      name: "OrchardTransportWindows",
      dependencies: ["OrchardProtocol"]
    ),
    .target(
      name: "OrchardUI",
      dependencies: ["OrchardProtocol"]
    ),
    .testTarget(
      name: "OrchardProtocolTests",
      dependencies: ["OrchardProtocol"]
    ),
    .testTarget(
      name: "OrchardTransportWindowsTests",
      dependencies: ["OrchardProtocol", "OrchardTransportWindows"]
    ),
    .testTarget(
      name: "OrchardUITests",
      dependencies: ["OrchardProtocol", "OrchardUI"]
    ),
  ],
  swiftLanguageModes: [.v6]
)
