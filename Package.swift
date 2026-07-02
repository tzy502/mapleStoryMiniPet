// swift-tools-version:5.9
import PackageDescription
let package = Package(
    name: "MiniPet",
    platforms: [.macOS(.v14)],
    targets: [.executableTarget(name: "MiniPet", path: "Sources/MiniPet")]
)
