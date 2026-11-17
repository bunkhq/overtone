// swift-tools-version: 6.0
// Overtone for macOS. Build the core first: scripts/build-core-macos.sh
// (or scripts/build-macos.sh, which also bundles Overtone.app).
import PackageDescription

let package = Package(
    name: "Overtone",
    platforms: [.macOS(.v14)],
    products: [
        .executable(name: "Overtone", targets: ["Overtone"]),
    ],
    targets: [
        // Static Rust core + C header, produced by scripts/build-core-macos.sh.
        .binaryTarget(name: "overtone_coreFFI", path: "Frameworks/overtone_coreFFI.xcframework"),
        // UniFFI-generated Swift bindings for the core.
        .target(
            name: "OvertoneCore",
            dependencies: ["overtone_coreFFI"],
            path: "Sources/OvertoneCore",
            swiftSettings: [.swiftLanguageMode(.v5)]
        ),
        // The app itself: menu bar, overlay, now-playing sources.
        .target(
            name: "OvertoneMac",
            dependencies: ["OvertoneCore"],
            path: "Sources/OvertoneMac"
        ),
        .executableTarget(
            name: "Overtone",
            dependencies: ["OvertoneMac"],
            path: "Sources/Overtone"
        ),
        .testTarget(
            name: "OvertoneMacTests",
            dependencies: ["OvertoneMac", "OvertoneCore"],
            path: "Tests/OvertoneMacTests"
        ),
    ]
)
