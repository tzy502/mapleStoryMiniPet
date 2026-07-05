import Foundation

// MARK: - Data Models

struct SpriteEntry: Codable {
    let file: String
    let frames: Int
    let frameWidth: Int
    let frameHeight: Int
    var originX: Int?
    var originY: Int?
}

struct PetConfig: Codable {
    let name: String
    let version: Int
    let type: String?
    let sprites: [String: SpriteEntry]
    var originX: Int?
    var originY: Int?
}

struct MobInfo: Codable {
    let code: String
    var name: String
    var defaultAnim: String = "stand"
}

// MARK: - Mob Store (persistent config)

class MobStore {
    static var path: String { "\(cacheRoot)/mobs.json" }

    static func load() -> [MobInfo] {
        guard let data = try? Data(contentsOf: URL(fileURLWithPath: path)),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let arr = json["mobs"] as? [[String: Any]] else {
            return fallbackMobs.map { MobInfo(code: $0.code, name: $0.name) }
        }
        return arr.compactMap { dict in
            guard let code = dict["code"] as? String else { return nil }
            let name = dict["name"] as? String ?? code
            let defaultAnim = dict["defaultAnim"] as? String ?? "stand"
            return MobInfo(code: code, name: name, defaultAnim: defaultAnim)
        }
    }

    static func save(_ mobs: [MobInfo]) {
        let arr: [[String: Any]] = mobs.map {
            ["code": $0.code, "name": $0.name, "defaultAnim": $0.defaultAnim]
        }
        let json: [String: Any] = ["mobs": arr]
        if let data = try? JSONSerialization.data(withJSONObject: json, options: .prettyPrinted) {
            try? FileManager.default.createDirectory(atPath: cacheRoot, withIntermediateDirectories: true)
            try? data.write(to: URL(fileURLWithPath: path))
        }
    }
}
