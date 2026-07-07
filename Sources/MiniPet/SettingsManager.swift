import Foundation

// MARK: - Settings Persistence

/// 桌宠设置持久化管理器，保存到 ~/Library/Caches/MiniPet/settings.json
class SettingsManager {
    static let path = "\(cacheRoot)/settings.json"

    static var current = load()

    static func load() -> PetSettings {
        guard let data = try? Data(contentsOf: URL(fileURLWithPath: path)),
              let obj = try? JSONDecoder().decode(PetSettings.self, from: data) else {
            return PetSettings()
        }
        return obj
    }

    static func save(_ settings: PetSettings) {
        current = settings
        try? FileManager.default.createDirectory(atPath: cacheRoot, withIntermediateDirectories: true)
        if let data = try? JSONEncoder().encode(settings) {
            try? data.write(to: URL(fileURLWithPath: path))
        }
    }
}

// MARK: - Reaction Rule

struct ReactionRule: Codable, Equatable {
    var keyword: String = ""
    var animation: String = "stand"
    var delay: Double = 0.5
    var priority: Int = 1
    var enabled: Bool = true
}

// MARK: - Map Object

struct MapObject: Codable, Equatable {
    var id: String = ""
    var name: String = ""
    var icon: String = "📦"
    var x: Double = 0
    var y: Double = 0
}

// MARK: - Paperdoll Preset

struct PaperdollPreset: Codable, Equatable {
    var name: String = "新角色"
    var gender: Int = 0
    var skin: String = "0"
    var hair: String = "30000"
    var face: String = "20000"
    var cap: String = ""
    var coat: String = ""
    var pants: String = ""
    var shoes: String = ""
    var weapon: String = ""
    var cape: String = ""
    var shield: String = ""
    var gloves: String = ""
    var mountCode: String = ""
    var chairCode: String = ""
    var dyeHue: Double = 0
    var dyeEnabled: Bool = false
}

// MARK: - Shortcut

struct ShortcutEntry: Codable, Equatable {
    var key: String = ""
    var action: String = ""
}

// MARK: - Pet Settings

struct PetSettings: Codable {
    // Pet
    var mobId: String = "8880150"
    var entityType: String = "mob"

    // Appearance
    var mapId: String = ""
    var chairCode: String = ""
    var mountCode: String = ""

    // Animation
    var defaultAnim: String = ""

    // Personality
    var personality: String = "tsundere"
    var speakFrequency: Double = 60.0

    // Terminal
    var terminalFontSize: Double = 12.0
    var terminalColorScheme: String = "classic"
    var terminalShortcut: String = "Cmd+T"

    // ── New fields for Settings Center ──

    // Theme
    var theme: String = "dark"   // "dark", "light", "system"

    // Material favorites & recent
    var favoriteMobIds: [String] = []
    var recentMobIds: [String] = []
    // 收藏详情（按 code 存储收藏状态，用于素材桌宠设置）
    var materialFavorites: [String: Bool] = [:]

    // Paperdoll presets
    var paperdollPresets: [PaperdollPreset] = []
    var currentPaperdollIndex: Int = 0

    // Map objects (OBJ list on background)
    var objList: [MapObject] = []

    // Balloon
    var balloonId: Int = 560
    var balloonCustomBg: String? = nil
    var balloonCustomTextColor: String? = nil

    // Reaction rules
    var reactionRules: [ReactionRule] = [
        ReactionRule(keyword: "你好", animation: "move", delay: 0.5, priority: 1, enabled: true),
        ReactionRule(keyword: "再见", animation: "stand", delay: 0.3, priority: 2, enabled: true),
        ReactionRule(keyword: "打怪", animation: "attack", delay: 0.2, priority: 3, enabled: true),
        ReactionRule(keyword: "啦啦啦", animation: "stand", delay: 0.4, priority: 2, enabled: false),
    ]
    var reactionEnabled: Bool = true

    // Chat / Conversation
    var chatShortcutOpen: String = "Cmd+Shift+Space"
    var chatShortcutVoice: String = "Cmd+Shift+M"
    var chatTheme: String = "system"   // "system", "light", "dark"
    var chatFontSize: Double = 14.0
    var chatAlignment: String = "leftRight"  // "leftRight", "center"
    var chatOpacity: Double = 0.85
    var chatFontName: String = "SF Pro"

    // Voice input
    var voiceEnabled: Bool = false
    var voiceEngine: String = "macOS 系统语音"
    var voiceLanguage: String = "中文（普通话）"
}