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
}
