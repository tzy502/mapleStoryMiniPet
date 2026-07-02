import Foundation

// MARK: - API Client

class APIClient {
    let session = URLSession(configuration: {
        let c = URLSessionConfiguration.default
        c.timeoutIntervalForRequest = 10
        c.timeoutIntervalForResource = 30
        return c
    }())

    private var resolvedBase: String?

    private func resolveBase() async -> String? {
        if let cached = resolvedBase { return cached }
        for base in apiBases {
            guard let url = URL(string: "\(base)/api/health") else { continue }
            do {
                let (_, _) = try await session.data(from: url)
                resolvedBase = base
                logDebug("API resolved: \(base)")
                return base
            } catch {
                logDebug("API unreachable: \(base)")
            }
        }
        return nil
    }

    func fetchMobList() async -> [MobInfo] {
        guard let base = await resolveBase() else { return [] }
        guard let url = URL(string: "\(base)/api/wz/data/query/string") else { return [] }
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let body: [String: String] = ["type": "mob", "name": ""]
        req.httpBody = try? JSONEncoder().encode(body)

        do {
            let (data, _) = try await session.data(for: req)
            return (try? JSONDecoder().decode([MobInfo].self, from: data)) ?? []
        } catch {
            logDebug("fetchMobList failed: \(error)")
            return []
        }
    }

    func fetchMobName(mobId: String) async -> String? {
        guard let base = await resolveBase() else { return nil }
        guard let url = URL(string: "\(base)/api/wz/data/query/string") else { return nil }
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let body: [String: String] = ["type": "mob", "code": mobId]
        req.httpBody = try? JSONEncoder().encode(body)

        do {
            let (data, _) = try await session.data(for: req)
            if let list = try? JSONSerialization.jsonObject(with: data) as? [[String: Any]],
               let name = list.first?["name"] as? String {
                return name
            }
        } catch {
            logDebug("fetchMobName failed: \(error)")
        }
        return nil
    }

    func fetchAndGenerateSprites(mobId: String) async -> Bool {
        let cm = CacheManager(mobId: mobId)

        if cm.isCached {
            logDebug("Sprites already cached for mob \(mobId)")
            return true
        }

        let scriptPath = "\(projectDir)/fetch_and_generate.py"
        guard FileManager.default.fileExists(atPath: scriptPath) else {
            logDebug("Python helper not found at \(scriptPath)")
            return false
        }

        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/usr/bin/python3")
        task.arguments = [scriptPath, mobId]
        task.standardOutput = FileHandle.nullDevice
        task.standardError = FileHandle.nullDevice

        do {
            try task.run()
            task.waitUntilExit()
            logDebug("fetch_and_generate.py exit: \(task.terminationStatus)")
        } catch {
            logDebug("fetch_and_generate.py failed: \(error)")
            return false
        }

        return cm.isCached
    }

    // MARK: - Balloon Resources

    func fetchBalloonList() async -> [Int] {
        guard let base = await resolveBase() else { return [] }
        guard let url = URL(string: "\(base)/api/wz/tree") else { return [] }
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let body: [String: String] = ["path": "UI/ChatBalloon.img"]
        req.httpBody = try? JSONEncoder().encode(body)

        do {
            let (data, _) = try await session.data(for: req)
            guard let nodes = try? JSONSerialization.jsonObject(with: data) as? [[String: Any]] else { return [] }
            var ids: [Int] = []
            for node in nodes {
                if node["type"] as? String == "容器", let name = node["name"], let id = Int("\(name)") {
                    ids.append(id)
                }
            }
            return ids.sorted()
        } catch {
            logDebug("fetchBalloonList failed: \(error)")
            return []
        }
    }

    func fetchBalloonTileImage(balloonId: Int, tileName: String) async -> Data? {
        guard let base = await resolveBase() else { return nil }
        let path = "UI/ChatBalloon.img/\(balloonId)/\(tileName)"
        guard let url = URL(string: "\(base)/api/wz/image?path=\(path)") else { return nil }
        do {
            let (data, _) = try await session.data(from: url)
            return data
        } catch {
            logDebug("fetchBalloonTileImage(\(balloonId), \(tileName)) failed: \(error)")
            return nil
        }
    }

    func fetchBalloonTiles(balloonId: Int) async -> [String: Data]? {
        let names = ["nw", "n", "head", "ne", "w", "c", "e", "sw", "s", "arrow", "se"]
        var tiles: [String: Data] = [:]
        for name in names {
            if let data = await fetchBalloonTileImage(balloonId: balloonId, tileName: name) {
                tiles[name] = data
            }
        }
        guard tiles.count >= 10 else { return nil }
        return tiles
    }

    func fetchBalloonClr(balloonId: Int) async -> Int? {
        guard let base = await resolveBase() else { return nil }
        let path = "UI/ChatBalloon.img/\(balloonId)/info/clr"
        guard let url = URL(string: "\(base)/api/wz/property") else { return nil }
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let body: [String: String] = ["path": path]
        req.httpBody = try? JSONEncoder().encode(body)
        do {
            let (data, _) = try await session.data(for: req)
            guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let value = json["value"] as? Int else { return nil }
            return value
        } catch {
            logDebug("fetchBalloonClr(\(balloonId)) failed: \(error)")
            return nil
        }
    }
}
