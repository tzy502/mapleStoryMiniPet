import AppKit
import Foundation

// MARK: - Chair/Mount Compositing View

/// Manages chair and mount sprite compositing below the pet.
/// - Chair: rendered below pet, pet sits on it (sit animation)
/// - Mount: rendered below pet, pet rides on top (ride-stand/ride-move animations)
/// Chair and mount are mutually exclusive.
class ChairMountView: NSView {

    // MARK: - Types

    enum Mode: Equatable {
        case none
        case chair(code: String, name: String)
        case mount(code: String, name: String)
    }

    // MARK: - Properties

    private(set) var mode: Mode = .none

    /// The sprite strip data for the current chair/mount: {animName: (cg, fw, fh, count, ox, oy)}
    var strips: [String: (cg: CGImage, fw: Int, fh: Int, count: Int, ox: Int, oy: Int)] = [:]
    var cur: String = "stand"
    var fi: Int = 0

    /// Current chair/mount code (e.g. "30100000" for chair, or a mount code)
    private(set) var currentCode: String = ""

    // MARK: - Display Layer

    private let spriteLayer: CALayer = {
        let layer = CALayer()
        layer.contentsGravity = .topLeft
        return layer
    }()

    // MARK: - Init

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true
        layer?.backgroundColor = NSColor.clear.cgColor
        layer?.addSublayer(spriteLayer)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) not implemented") }

    // MARK: - Layout

    override func layout() {
        super.layout()
        spriteLayer.frame = bounds
    }

    // MARK: - Loading

    /// Load chair sprites from cache or API.
    /// Returns true on success.
    func loadChair(code: String, name: String) async -> Bool {
        mode = .chair(code: code, name: name)
        currentCode = code
        return await loadEntity(code: code, entityType: "chair", displayName: name)
    }

    /// Load mount sprites from cache or API.
    /// Returns true on success.
    func loadMount(code: String, name: String) async -> Bool {
        mode = .mount(code: code, name: name)
        currentCode = code
        return await loadEntity(code: code, entityType: "mount", displayName: name)
    }

    private func loadEntity(code: String, entityType: String, displayName: String) async -> Bool {
        let cm = CacheManager(mobId: code)

        // Try cache first
        if cm.isCached, let config = cm.loadConfig() {
            logDebug("ChairMountView: cache hit for \(entityType) \(code)")
            await MainActor.run { self.applyConfig(config, cacheDir: cm.dir) }
            return true
        }

        // Try API generation — only mob/npc types are supported by the Python script
        if entityType == "mob" || entityType == "npc" {
            let api = APIClient()
            if await api.fetchAndGenerateSprites(mobId: code, type: entityType), let config = cm.loadConfig() {
                logDebug("ChairMountView: API sprites for \(entityType) \(code)")
                await MainActor.run { self.applyConfig(config, cacheDir: cm.dir) }
                return true
            }
        } else {
            logDebug("ChairMountView: \(entityType) sprites not available via API, only cache supported")
        }

        logDebug("ChairMountView: no sprites for \(entityType) \(code)")
        return false
    }

    func applyConfig(_ config: PetConfig, cacheDir: String) {
        strips.removeAll()

        for (animName, entry) in config.sprites {
            let filePath = "\(cacheDir)/\(entry.file)"
            guard let img = NSImage(contentsOfFile: filePath),
                  let cg = img.cgImage(forProposedRect: nil, context: nil, hints: nil) else {
                logDebug("ChairMountView applyConfig FAIL: \(filePath)")
                continue
            }
            strips[animName] = (cg, entry.frameWidth, entry.frameHeight, entry.frames,
                                entry.originX ?? 0, entry.originY ?? 0)
        }
        logDebug("ChairMountView: \(config.name) -> \(strips.count) animations")

        // Pick best animation
        let priority = ["stand", "sit", "ride-stand", "ride-move", "move", "fly"]
        let best = priority.first(where: { strips[$0] != nil })
            ?? strips.keys.sorted().first ?? "stand"

        cur = best
        fi = 0
        resize()
        updateDisplay()
    }

    // MARK: - Display

    func tick() {
        guard let s = strips[cur] else { return }
        fi = (fi + 1) % s.count
        updateDisplay()
    }

    private func updateDisplay() {
        guard let s = strips[cur] else { return }
        let rect = CGRect(x: CGFloat(fi * s.fw), y: 0, width: CGFloat(s.fw), height: CGFloat(s.fh))
        spriteLayer.contents = s.cg.cropping(to: rect)
    }

    func switchTo(_ name: String) {
        guard strips[name] != nil else { return }
        cur = name
        fi = 0
        resize()
        updateDisplay()
    }

    func resize() {
        guard let s = strips[cur] else { return }
        frame.size = NSSize(width: CGFloat(s.fw), height: CGFloat(s.fh))
    }

    /// Get the origin point (in view coordinates) for positioning the pet relative to this view.
    func originPoint() -> CGPoint {
        guard let s = strips[cur] else { return .zero }
        return CGPoint(x: CGFloat(s.ox), y: frame.height - CGFloat(s.oy))
    }

    /// Get the bottom-anchor point (ground contact point) for aligning the pet on top.
    func groundAnchorPoint() -> CGPoint {
        guard let s = strips[cur] else { return .zero }
        // The origin is typically at the seat/riding position.
        // Return origin point as the anchor where pet should be placed.
        return CGPoint(x: CGFloat(s.ox), y: frame.height - CGFloat(s.oy))
    }

    // MARK: - Clear

    func clear() {
        mode = .none
        currentCode = ""
        strips.removeAll()
        spriteLayer.contents = nil
        frame.size = .zero
    }
}

// MARK: - Known Chair IDs

let knownChairIds: [(code: String, name: String)] = [
    ("30100000", "黄铜椅"),
    ("30100001", "红漆椅"),
    ("30100002", "蓝色靠背椅"),
    ("30100003", "黑色椅"),
    ("30100004", "柔软椅"),
    ("30100005", "温暖椅"),
    ("30100006", "沙发"),
    ("30100007", "藤椅"),
    ("30100008", "国王椅"),
    ("30100009", "心型椅"),
]

// MARK: - Known Mount IDs (placeholder — known ride items)

let knownMountIds: [(code: String, name: String)] = [
    ("1022000", "小黄鸭"),
    ("1022001", "木马"),
    ("1022002", "云朵"),
    ("1022003", "摩托"),
    ("1022004", "飞毯"),
]