import Foundation

// MARK: - API Client

class APIClient {
    let session = URLSession(configuration: {
        let c = URLSessionConfiguration.default
        c.timeoutIntervalForRequest = 10
        c.timeoutIntervalForResource = 30
        return c
    }())

    var resolvedBase: String?

    func resolveBase() async -> String? {
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

    func fetchNpcName(npcId: String) async -> String? {
        guard let base = await resolveBase() else { return nil }
        guard let url = URL(string: "\(base)/api/wz/data/query/string") else { return nil }
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let body: [String: String] = ["type": "npc", "code": npcId]
        req.httpBody = try? JSONEncoder().encode(body)

        do {
            let (data, _) = try await session.data(for: req)
            if let list = try? JSONSerialization.jsonObject(with: data) as? [[String: Any]],
               let name = list.first?["name"] as? String {
                return name
            }
        } catch {
            logDebug("fetchNpcName failed: \(error)")
        }
        return nil
    }

    func fetchNpcList() async -> [MobInfo] {
        guard let base = await resolveBase() else { return [] }
        guard let url = URL(string: "\(base)/api/wz/data/query/string") else { return [] }
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let body: [String: String] = ["type": "npc", "name": ""]
        req.httpBody = try? JSONEncoder().encode(body)

        do {
            let (data, _) = try await session.data(for: req)
            guard let list = try? JSONSerialization.jsonObject(with: data) as? [[String: Any]] else { return [] }
            return list.compactMap { dict in
                guard let code = dict["code"] as? String ?? dict["id"] as? String else { return nil }
                let name = dict["name"] as? String ?? ""
                return MobInfo(code: code, name: name, type: "npc")
            }
        } catch {
            logDebug("fetchNpcList failed: \(error)")
            return []
        }
    }

    func fetchAndGenerateSprites(mobId: String, type: String = "mob") async -> Bool {
        let cm = CacheManager(mobId: mobId, entityType: type)

        if cm.isCached {
            logDebug("Sprites already cached for \(type) \(mobId)")
            return true
        }

        let scriptPath = "\(projectDir)/fetch_and_generate.py"
        guard FileManager.default.fileExists(atPath: scriptPath) else {
            logDebug("Python helper not found at \(scriptPath)")
            return false
        }

        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/usr/bin/python3")
        task.arguments = [scriptPath, mobId, "--type", type]
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
        guard tiles.count >= 9 else { return nil }
        return tiles
    }

    // MARK: - Balloon Origins & Properties

    /// Fetch origin data for all balloon tiles by parsing the XML endpoint.
    /// Uses POST /api/wz/xml which returns XML with embedded <vector name="origin" value="x, y"/>.
    func fetchBalloonOrigins(balloonId: Int) async -> [String: CGPoint] {
        let names = ["nw", "n", "head", "ne", "w", "c", "e", "sw", "s", "arrow", "se"]
        var origins: [String: CGPoint] = [:]
        for name in names {
            if let pt = await fetchPieceOrigin(balloonId: balloonId, piece: name) {
                origins[name] = pt
            }
        }
        return origins
    }

    /// Fetch a single tile piece's origin via the XML endpoint.
    private func fetchPieceOrigin(balloonId: Int, piece: String) async -> CGPoint? {
        guard let base = await resolveBase() else { return nil }
        let path = "UI/ChatBalloon.img/\(balloonId)/\(piece)"
        guard let url = URL(string: "\(base)/api/wz/xml") else { return nil }
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let body: [String: String] = ["path": path]
        req.httpBody = try? JSONEncoder().encode(body)
        do {
            let (data, _) = try await session.data(for: req)
            // API returns JSON: {"name":"nw","path":"...","xml":"<?xml ...>"}
            guard let json = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let xml = json["xml"] as? String else { return nil }
            // Parse: <vector name="origin" value="x, y"/>
            let vecPattern = try NSRegularExpression(pattern: #"<vector name="origin" value="(\d+),\s*(\d+)"/>"#)
            let nsRange = NSRange(xml.startIndex..<xml.endIndex, in: xml)
            if let match = vecPattern.firstMatch(in: xml, range: nsRange) {
                if let xRange = Range(match.range(at: 1), in: xml),
                   let yRange = Range(match.range(at: 2), in: xml) {
                    let x = CGFloat(Int(xml[xRange]) ?? 0)
                    let y = CGFloat(Int(xml[yRange]) ?? 0)
                    return CGPoint(x: x, y: y)
                }
            }
            return nil
        } catch {
            logDebug("fetchPieceOrigin(\(balloonId), \(piece)) failed: \(error)")
            return nil
        }
    }

    /// Fetch balloon clr value from the parent balloon node's XML (MapleSalon2 getWzClrColor).
    /// The /api/wz/xml endpoint returns the full balloon node including clr as a property.
    func fetchBalloonClr(balloonId: Int) async -> Int? {
        guard let base = await resolveBase() else { return nil }
        let path = "UI/ChatBalloon.img/\(balloonId)"
        guard let url = URL(string: "\(base)/api/wz/xml") else { return nil }
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let body: [String: String] = ["path": path]
        req.httpBody = try? JSONEncoder().encode(body)
        do {
            let (data, _) = try await session.data(for: req)
            guard let json = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let xml = json["xml"] as? String else { return nil }
            // Parse: <wznumberproperty name="clr" value="-1"/>
            let intPattern = try NSRegularExpression(pattern: #"<wznumberproperty name="clr" value="(-?\d+)"/>"#)
            let nsRange = NSRange(xml.startIndex..<xml.endIndex, in: xml)
            if let match = intPattern.firstMatch(in: xml, range: nsRange),
               let valRange = Range(match.range(at: 1), in: xml) {
                return Int(xml[valRange])
            }
            return nil
        } catch {
            logDebug("fetchBalloonClr(\(balloonId)) failed: \(error)")
            return nil
        }
    }
}
