import Foundation

// MARK: - WZ Image Loader (Single Frame)

class WzImageLoader {
    private let api: APIClient
    private var imageCache: [String: Data] = [:]
    private let cacheQueue = DispatchQueue(label: "com.minipet.wzimage", attributes: .concurrent)

    init(api: APIClient = APIClient()) {
        self.api = api
    }

    /// Download a single WZ frame PNG. Cached in memory + disk.
    func fetchImage(wzPath: String) async -> Data? {
        // In-memory cache
        if let cached = readCache(key: wzPath) { return cached }

        guard let base = await api.resolveBase(),
              let url = URL(string: "\(base)/api/wz/image?path=\(wzPath)") else { return nil }
        do {
            let (data, _) = try await URLSession.shared.data(from: url)
            guard !data.isEmpty else { return nil }
            writeCache(key: wzPath, data: data)
            return data
        } catch {
            logDebug("WzImageLoader failed: \(wzPath) - \(error)")
            return nil
        }
    }

    /// Fetch multiple frames for an animation action in parallel.
    func fetchFrames(paths: [String]) async -> [String: Data] {
        await withTaskGroup(of: (String, Data?).self) { group in
            for path in paths {
                group.addTask { [self] in (path, await fetchImage(wzPath: path)) }
            }
            var results: [String: Data] = [:]
            for await (path, data) in group {
                if let d = data { results[path] = d }
            }
            return results
        }
    }

    // MARK: - Cache

    private func readCache(key: String) -> Data? {
        cacheQueue.sync { imageCache[key] }
    }

    private func writeCache(key: String, data: Data) {
        cacheQueue.async(flags: .barrier) { self.imageCache[key] = data }
    }

    func clearCache() {
        cacheQueue.async(flags: .barrier) { self.imageCache.removeAll() }
    }
}

// MARK: - Frame Path Discovery

extension WzImageLoader {
    /// Discover all canvas frame paths under a WZ node (e.g. "Mob/_Canvas/8880150.img/stand").
    func discoverFramePaths(wzPath: String) async -> [String] {
        guard let base = await api.resolveBase(),
              let url = URL(string: "\(base)/api/wz/renderImgPath") else { return [] }
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let body: [String: String] = ["path": wzPath]
        req.httpBody = try? JSONEncoder().encode(body)
        do {
            let (data, _) = try await URLSession.shared.data(for: req)
            guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let images = json["images"] as? [String] else { return [] }
            return images
        } catch {
            logDebug("discoverFramePaths failed: \(wzPath) - \(error)")
            return []
        }
    }
}

// MARK: - Property Fetch (single int/string value)

extension WzImageLoader {
    /// Fetch a single property value from backend (e.g. clr, tamingMob, etc.).
    func fetchProperty(wzPath: String) async -> Any? {
        guard let base = await api.resolveBase(),
              let url = URL(string: "\(base)/api/wz/property") else { return nil }
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let body: [String: String] = ["path": wzPath]
        req.httpBody = try? JSONEncoder().encode(body)
        do {
            let (data, _) = try await URLSession.shared.data(for: req)
            guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
            return json["value"]
        } catch {
            logDebug("fetchProperty failed: \(wzPath) - \(error)")
            return nil
        }
    }
}