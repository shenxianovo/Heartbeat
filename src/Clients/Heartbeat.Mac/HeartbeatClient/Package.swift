// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "HeartbeatClient",
    platforms: [.macOS(.v15)],
    products: [
        .library(name: "HeartbeatClient", targets: ["HeartbeatClient"]),
    ],
    targets: [
        .target(name: "HeartbeatClient"),
        .testTarget(name: "HeartbeatClientTests", dependencies: ["HeartbeatClient"]),
    ]
)
