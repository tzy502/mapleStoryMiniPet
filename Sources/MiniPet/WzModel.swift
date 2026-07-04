import Foundation

// MARK: - WZ Node Types (from XML)

enum WzNodeType: Equatable {
    case dir, png, vector, uol, sound, convex, rawdata, video
    case intValue, stringValue, floatValue, doubleValue, null
    case unknown(String)

    static func == (lhs: WzNodeType, rhs: WzNodeType) -> Bool {
        switch (lhs, rhs) {
        case (.dir, .dir), (.png, .png), (.vector, .vector), (.uol, .uol),
            (.sound, .sound), (.convex, .convex), (.rawdata, .rawdata), (.video, .video),
            (.intValue, .intValue), (.stringValue, .stringValue), (.floatValue, .floatValue),
            (.doubleValue, .doubleValue), (.null, .null):
            return true
        case (.unknown(let a), .unknown(let b)):
            return a == b
        default:
            return false
        }
    }
}

class WzNodeModel {
    let name: String
    var type: WzNodeType
    var children: [String: WzNodeModel] = [:]
    weak var parent: WzNodeModel?

    // Canvas
    var width: Int = 0
    var height: Int = 0
    var format: Int = 0
    var scale: Int = 0
    var pages: Int = 0

    // Vector
    var x: Int = 0
    var y: Int = 0

    // UOL
    var link: String = ""

    // Value types
    var intValue: Int = 0
    var stringValue: String = ""
    var floatValue: Float = 0
    var doubleValue: Double = 0

    // Sound / RawData / Video
    var dataLength: Int = 0
    var base64Data: String = ""

    init(name: String, type: WzNodeType) {
        self.name = name
        self.type = type
    }
}

// MARK: - WZ XML Parser

class WzXmlParser {
    private let api: APIClient

    init(api: APIClient = APIClient()) {
        self.api = api
    }

    /// Fetch WZ node XML from backend and parse into tree.
    func fetchNode(path: String) async -> WzNodeModel? {
        guard let base = await api.resolveBase() else { return nil }
        guard let url = URL(string: "\(base)/api/wz/xml") else { return nil }
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let body: [String: String] = ["path": path]
        req.httpBody = try? JSONEncoder().encode(body)

        do {
            let (data, _) = try await URLSession.shared.data(for: req)
            guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let xmlString = json["xml"] as? String else { return nil }
            return parse(xml: xmlString)
        } catch {
            logDebug("WzXmlParser fetch failed: \(error)")
            return nil
        }
    }

    /// Parse XML string into WzNodeModel tree.
    func parse(xml: String) -> WzNodeModel? {
        let root = WzNodeModel(name: "_root", type: .dir)
        var stack: [WzNodeModel] = [root]
        var i = xml.startIndex

        while i < xml.endIndex {
            if xml[i] == "<" {
                let endTag = xml[i...].hasPrefix("</")
                if endTag {
                    // find '>'
                    if let close = xml[i...].firstIndex(of: ">") {
                        stack.removeLast()
                        i = xml.index(after: close)
                        continue
                    }
                }

                // Check if it's a self-closing tag or has children
                let isSelfClosing = xml[i...].hasPrefix("<?")
                if isSelfClosing {
                    if let close = xml[i...].firstIndex(of: ">") {
                        i = xml.index(after: close)
                        continue
                    }
                }

                // Parse tag
                guard let tagEnd = xml[i...].firstIndex(of: ">") else { i = xml.endIndex; break }
                let tagContent = String(xml[xml.index(after: i)..<tagEnd])
                let isClosing = tagContent.hasPrefix("/")
                let isSelfClose = tagContent.hasSuffix("/") || tagContent.hasSuffix("/")

                if isClosing {
                    stack.removeLast()
                    i = xml.index(after: tagEnd)
                    continue
                }

                // Extract tag name and attributes
                let tagName: String
                let attrs: [String: String]
                if let spaceIdx = tagContent.firstIndex(of: " ") {
                    tagName = String(tagContent[tagContent.startIndex..<spaceIdx])
                    attrs = parseAttributes(String(tagContent[spaceIdx...]))
                } else {
                    tagName = tagContent
                    attrs = [:]
                }

                let node = buildNode(tagName: tagName, attrs: attrs)
                if let parent = stack.last {
                    parent.children[node.name] = node
                    node.parent = parent
                }
                stack.append(node)

                if tagContent.hasSuffix("/") {
                    stack.removeLast()
                }

                i = xml.index(after: tagEnd)
            } else {
                i = xml.index(after: i)
            }
        }

        return root.children.first?.value
    }

    private func buildNode(tagName: String, attrs: [String: String]) -> WzNodeModel {
        let name = attrs["name"] ?? ""
        switch tagName {
        case "dir":
            return WzNodeModel(name: name, type: .dir)
        case "png":
            let node = WzNodeModel(name: name, type: .png)
            node.width = Int(attrs["width"] ?? "0") ?? 0
            node.height = Int(attrs["height"] ?? "0") ?? 0
            node.format = Int(attrs["format"] ?? "0") ?? 0
            node.scale = Int(attrs["scale"] ?? "0") ?? 0
            node.pages = Int(attrs["pages"] ?? "0") ?? 0
            node.base64Data = attrs["value"] ?? ""
            return node
        case "vector":
            let node = WzNodeModel(name: name, type: .vector)
            if let val = attrs["value"] {
                let parts = val.components(separatedBy: ",").map { $0.trimmingCharacters(in: .whitespaces) }
                node.x = Int(parts[safe: 0] ?? "0") ?? 0
                node.y = Int(parts[safe: 1] ?? "0") ?? 0
            }
            return node
        case "uol":
            let node = WzNodeModel(name: name, type: .uol)
            node.link = attrs["value"] ?? ""
            return node
        case "sound":
            let node = WzNodeModel(name: name, type: .sound)
            node.base64Data = attrs["value"] ?? ""
            return node
        case "convex":
            return WzNodeModel(name: name, type: .convex)
        case "rawdata":
            let node = WzNodeModel(name: name, type: .rawdata)
            node.dataLength = Int(attrs["length"] ?? "0") ?? 0
            node.base64Data = attrs["value"] ?? ""
            return node
        case "video":
            let node = WzNodeModel(name: name, type: .video)
            node.dataLength = Int(attrs["length"] ?? "0") ?? 0
            node.base64Data = attrs["value"] ?? ""
            return node
        default:
            // Fallback value types: int, string, float, double, null
            let node = WzNodeModel(name: name, type: .unknown(tagName))
            if let val = attrs["value"] {
                switch tagName {
                case "int": node.intValue = Int(val) ?? 0; node.type = .intValue
                case "string": node.stringValue = val; node.type = .stringValue
                case "float": node.floatValue = Float(val) ?? 0; node.type = .floatValue
                case "double": node.doubleValue = Double(val) ?? 0; node.type = .doubleValue
                case "null": node.type = .null
                default: node.stringValue = val
                }
            }
            return node
        }
    }

    private func parseAttributes(_ s: String) -> [String: String] {
        var attrs: [String: String] = [:]
        var remaining = s.trimmingCharacters(in: .whitespaces)
        // Remove trailing /
        if remaining.hasSuffix("/") { remaining = String(remaining.dropLast()).trimmingCharacters(in: .whitespaces) }
        let pattern = #"(\w+)\s*=\s*"([^"]*?)""#
        guard let regex = try? NSRegularExpression(pattern: pattern) else { return attrs }
        let matches = regex.matches(in: remaining, range: NSRange(remaining.startIndex..., in: remaining))
        for match in matches {
            if let keyRange = Range(match.range(at: 1), in: remaining),
               let valRange = Range(match.range(at: 2), in: remaining) {
                attrs[String(remaining[keyRange])] = String(remaining[valRange])
            }
        }
        return attrs
    }
}

// MARK: - Convenience

extension Array {
    subscript(safe index: Int) -> Element? {
        indices.contains(index) ? self[index] : nil
    }
}