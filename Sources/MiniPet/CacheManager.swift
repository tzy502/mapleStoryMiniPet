import Foundation

// MARK: - Cache Manager

class CacheManager {
    let mobId: String
    let entityType: String  // "mob" or "npc"
    var sanitizedId: String { "\(entityType)_\(mobId.replacingOccurrences(of: ",", with: "_"))" }
    var dir: String { "\(cacheRoot)/\(sanitizedId)" }
    var configPath: String { "\(dir)/pet_config.json" }

    init(mobId: String, entityType: String = "mob") { self.mobId = mobId; self.entityType = entityType }

    var isCached: Bool {
        guard let data = try? Data(contentsOf: URL(fileURLWithPath: configPath)),
              let config = try? JSONDecoder().decode(PetConfig.self, from: data) else { return false }
        for (_, entry) in config.sprites {
            let path = "\(dir)/\(entry.file)"
            if !FileManager.default.fileExists(atPath: path) { return false }
        }
        return true
    }

    func loadConfig() -> PetConfig? {
        guard let data = try? Data(contentsOf: URL(fileURLWithPath: configPath)) else { return nil }
        return try? JSONDecoder().decode(PetConfig.self, from: data)
    }

    func saveConfig(_ config: PetConfig) {
        try? FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true, attributes: nil)
        if let data = try? JSONEncoder().encode(config) {
            try? data.write(to: URL(fileURLWithPath: configPath))
        }
    }

    func extractZIP(from data: Data) -> Bool {
        let tmpZip = "\(dir)/_tmp.zip"
        try? FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true, attributes: nil)
        try? data.write(to: URL(fileURLWithPath: tmpZip))

        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/usr/bin/unzip")
        task.arguments = ["-o", tmpZip, "-d", dir]
        task.standardOutput = FileHandle.nullDevice
        task.standardError = FileHandle.nullDevice

        do { try task.run(); task.waitUntilExit() } catch { return false }
        try? FileManager.default.removeItem(atPath: tmpZip)
        return task.terminationStatus == 0
    }
}
