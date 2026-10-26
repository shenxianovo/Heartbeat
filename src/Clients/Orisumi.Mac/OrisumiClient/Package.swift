// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "OrisumiClient",
    platforms: [.macOS(.v15)],
    products: [
        .library(name: "OrisumiClient", targets: ["OrisumiClient"]),
    ],
    targets: [
        .target(name: "OrisumiClient"),
        .testTarget(name: "OrisumiClientTests", dependencies: ["OrisumiClient"]),
    ]
)
