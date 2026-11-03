// swift-tools-version: 6.2
import PackageDescription

// Socle partagé des apps maison. Un module n'entre ici qu'à la 2e app qui en a besoin.
let package = Package(
    name: "Socle",
    platforms: [.macOS(.v26)],
    products: [
        .library(name: "SocleCore", targets: ["SocleCore"]),
        .library(name: "SocleBarreDeMenus", targets: ["SocleBarreDeMenus"]),
        .library(name: "SocleEnergie", targets: ["SocleEnergie"]),
    ],
    targets: [
        .target(name: "SocleCore"),
        .target(name: "SocleBarreDeMenus", dependencies: ["SocleCore"]),
        .target(name: "SocleEnergie"),
        .testTarget(name: "SocleCoreTests", dependencies: ["SocleCore"]),
    ]
)
