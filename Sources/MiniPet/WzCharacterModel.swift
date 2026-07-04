import AppKit
import Foundation

// MARK: - Equipment Category (匹配 MapleSalon2 handlers/string.rs EquipCategory)

/// 装备分类枚举，与 MapleSalon2 完全对齐。
/// WZ 中 Character 目录下的每个子目录（Cap/Cape/Coat 等）对应一个分类。
/// 另包含 Effect/NickTag/NameTag/ChatBalloon 等特殊分类。
enum EquipCategory: String, Codable, CaseIterable, CustomStringConvertible {
    case cap          // 帽子 (Cap)
    case cape         // 披风 (Cape)
    case coat         // 上衣 (Coat)
    case dragon       // 龙（Evan 龙装备）
    case mechanic     // 机械师装备
    case face         // 脸饰 (Face)
    case glove        // 手套 (Glove)
    case hair         // 发型 (Hair)
    case longcoat     // 长袍 (Longcoat)
    case pants        // 裤子 (Pants)
    case petEquip     // 宠物装备 (PetEquip)
    case ring         // 戒指 (Ring)
    case shield       // 盾牌 (Shield)
    case shoes        // 鞋子 (Shoes)
    case taming       // 骑宠 (Taming / TamingMob)
    case weapon       // 武器 (Weapon)
    case android      // 机器人 (Android)
    case accessory    // 饰品 (Accessory)
    case bit          // 碎片 (Bit, 拼图类道具)
    case arcaneForce  // 神秘之力 (ArcaneForce)
    case authenticForce // 真实之力 (AuthenticForce)
    case skin         // 皮肤 (Skin)
    case skillSkin    // 技能皮肤 (SkillSkin)
    case unknown      // 未知
    case ringEffect   // 戒指特效（Ring + hasEffect）
    case necklessEffect // 项链特效（Accessory 112xxx + hasEffect）
    case beltEffect   // 腰带特效（Accessory 113xxx + hasEffect）
    case medal        // 勋章 (Medal)
    case nickTag      // 昵称标签 (NickTag)
    case nameTag      // 名字标签 (NameTag)
    case chatBalloon  // 聊天气泡 (ChatBalloon)
    case effect       // 特效（现金道具特效，Item/Cash/0501.img）

    /// MapleSalon2 中的整型表示 (Display trait)，用于序列化/匹配。
    var description: String {
        switch self {
        case .cap:              return "1"
        case .cape:             return "2"
        case .coat:             return "3"
        case .dragon:           return "4"
        case .mechanic:         return "5"
        case .face:             return "6"
        case .glove:            return "7"
        case .hair:             return "8"
        case .longcoat:         return "9"
        case .pants:            return "10"
        case .petEquip:         return "11"
        case .ring:             return "12"
        case .shield:           return "13"
        case .shoes:            return "14"
        case .taming:           return "15"
        case .weapon:           return "16"
        case .android:          return "17"
        case .accessory:        return "18"
        case .bit:              return "19"
        case .arcaneForce:      return "20"
        case .authenticForce:   return "21"
        case .skin:             return "22"
        case .skillSkin:        return "23"
        case .unknown:          return "24"
        case .effect:           return "25"
        case .ringEffect:       return "26"
        case .necklessEffect:   return "27"
        case .medal:            return "28"
        case .nickTag:          return "29"
        case .nameTag:          return "30"
        case .chatBalloon:      return "31"
        case .beltEffect:       return "32"
        }
    }

    /// WZ 目录名 → EquipCategory
    /// 对应 MapleSalon2 get_equip_category_from_str()
    static func from(wzDirectory: String) -> EquipCategory {
        switch wzDirectory {
        case "Cap":          return .cap
        case "Cape":         return .cape
        case "Coat":         return .coat
        case "Dragon":       return .dragon
        case "Mechanic":     return .mechanic
        case "Face":         return .face
        case "Glove":        return .glove
        case "Hair":         return .hair
        case "Longcoat":     return .longcoat
        case "Pants":        return .pants
        case "PetEquip":     return .petEquip
        case "Ring":         return .ring
        case "Shield":       return .shield
        case "Shoes":        return .shoes
        case "Taming":       return .taming
        case "Weapon":       return .weapon
        case "Android":      return .android
        case "Accessory":    return .accessory
        case "Bit":          return .bit
        case "ArcaneForce":  return .arcaneForce
        case "AuthenticForce": return .authenticForce
        case "Skin":         return .skin
        case "SkillSkin":    return .skillSkin
        default:             return .unknown
        }
    }

    /// Equipment category 的 WZ 目录名（MapleSalon2 EQUIP_CATEGORY_NEEDS 数组中的 14 个）。
    static let equipCategories: [String] = [
        "Cap", "Cape", "Coat", "Face", "Glove", "Hair", "Longcoat",
        "Pants", "Ring", "Shield", "Shoes", "Weapon", "Accessory", "Skin"
    ]

    /// 此分类在 Character 目录下对应的 WZ 目录名
    var wzDirectoryName: String? {
        switch self {
        case .cap:              return "Cap"
        case .cape:             return "Cape"
        case .coat:             return "Coat"
        case .dragon:           return "Dragon"
        case .mechanic:         return "Mechanic"
        case .face:             return "Face"
        case .glove:            return "Glove"
        case .hair:             return "Hair"
        case .longcoat:         return "Longcoat"
        case .pants:            return "Pants"
        case .petEquip:         return "PetEquip"
        case .ring:             return "Ring"
        case .shield:           return "Shield"
        case .shoes:            return "Shoes"
        case .taming:           return nil // 来自 Character/TamingMob
        case .weapon:           return "Weapon"
        case .android:          return "Android"
        case .accessory:        return "Accessory"
        case .bit:              return "Bit"
        case .arcaneForce:      return "ArcaneForce"
        case .authenticForce:   return "AuthenticForce"
        case .skin:             return "Skin"
        case .skillSkin:        return "SkillSkin"
        case .ringEffect:       return "Ring"
        case .necklessEffect:   return "Accessory"
        case .beltEffect:       return "Accessory"
        case .medal:            return nil
        case .nickTag:          return nil
        case .nameTag:          return nil
        case .chatBalloon:      return nil
        case .effect:           return nil
        case .unknown:          return nil
        }
    }
}

// MARK: - Body Slot (Z-Order 身体部位)

/// 身体部位映射。对应 zmap.img/zmap 中定义的名称。
/// 渲染时按此顺序确定图层上下关系（z-order）。
enum BodySlot: String, Codable, CaseIterable {
    /// 背面装饰品（耳环等）
    case back
    /// 身体主体
    case body
    /// 手臂（在身体之后，武器之前）
    case arm
    /// 头部
    case head
    /// 手（左手）
    case hand
    /// 武器
    case weapon
    /// 帽子
    case cap
    /// 披风背部（后面层）
    case capeBack = "capeBack"
    /// 披风（前面层）
    case cape
    /// 手套覆盖层（手套之上）
    case gloveOver = "gloveOver"
    /// 手套
    case glove
    /// 鞋子
    case shoe
    /// 盾牌
    case shield
    /// 头发覆盖层（在帽子下面，头发上面）
    case hairOverHead = "hairOverHead"
    /// 头发底层（在头部下面）
    case hairBelowHead = "hairBelowHead"
    /// 脸饰
    case face
    /// 耳朵 / 耳环
    case ear
    /// 尾巴
    case tail
    /// 上衣
    case coat
    /// 裤子
    case pants
}

// MARK: - Body Part Frame

/// 单帧中一个身体部位的渲染数据。
/// 包含图片、origin 坐标、z-order、是否水平翻转。
struct BodyPartFrame {
    let slot: BodySlot
    let image: NSImage
    let originX: Int
    let originY: Int
    let z: Int
    var flip: Bool = false
}

// MARK: - Equipped Item

/// 一件已装备的道具（包含名称、分类、属性）。
struct EquippedItem: Codable {
    var category: EquipCategory
    let id: String
    let name: String
    var isCash: Bool = false
    var hasColorvar: Bool = false
    var hasEffect: Bool = false

    /// WZ 中的 8 位补零 ID (MapleSalon2 使用 {:0>8}.img 格式)。
    var paddedId: String {
        String(format: "%08d", Int(id) ?? 0)
    }

    /// Character 目录下的 WZ path 前缀。
    /// 例如: "Character/Cap/01001011.img"
    var wzItemPath: String? {
        guard let dir = category.wzDirectoryName else { return nil }
        return "\(WzPaths.character)/\(dir)/\(paddedId).img"
    }
}

// MARK: - Character Appearance

/// 角色外观配置，包含所有当前装备的部件 ID。
/// 与 MapleSalon2 前端穿戴状态对应。
struct CharacterAppearance: Codable {
    /// 性别：0=男性（Male），1=女性（Female）
    var gender: Int = 0
    /// 皮肤代码，例如 "0"、"10"、"11"
    var skin: String = "0"
    /// 发型代码，例如 "30000"
    var hair: String = "30000"
    /// 脸型代码，例如 "20000"
    var face: String = "20000"

    // ---- 装备 ----
    var cap: String?
    var cape: String?
    var coat: String?
    var longcoat: String?
    var pants: String?
    var shoes: String?
    var weapon: String?
    var shield: String?
    var glove: String?

    /// 戒指（最多 4 个）
    var ring: [String] = []
    /// 项链（可多个）
    var pendant: [String] = []
    /// 腰带
    var belt: String?
    /// 勋章
    var medal: String?
    /// 耳环
    var earring: String?
    /// 肩饰
    var shoulder: String?
    /// 口袋道具
    var pocket: String?
    /// 机器人
    var android: String?
    /// 特效（现金道具特效）
    var effect: String?
    /// 骑宠 ID
    var mount: String?
    /// 椅子 ID
    var chair: String?
    /// 名称标签
    var nameTag: String?
    /// 昵称标签
    var nickTag: String?
    /// 聊天气泡 ID
    var chatBalloon: String?

    // ---- 状态 ----
    /// 武器是否带特效（现金武器）
    var weaponEffect: Bool = false
    /// 是否启用 colorvar（染色变体）
    var useColorvar: Bool = false

    /// body 模板代码（根据性别）
    var bodyCode: String {
        gender == 0 ? "00002000" : "00012000"
    }

    /// 获取此外观中所有非空的装备 slot 列表
    var equippedSlots: [(EquipCategory, String)] {
        var slots: [(EquipCategory, String)] = []
        if let v = cap { slots.append((.cap, v)) }
        if let v = cape { slots.append((.cape, v)) }
        if let v = coat { slots.append((.coat, v)) }
        if let v = longcoat { slots.append((.longcoat, v)) }
        if let v = pants { slots.append((.pants, v)) }
        if let v = shoes { slots.append((.shoes, v)) }
        if let v = weapon { slots.append((.weapon, v)) }
        if let v = shield { slots.append((.shield, v)) }
        if let v = glove { slots.append((.glove, v)) }
        if let v = earring { slots.append((.accessory, v)) }
        if let v = shoulder { slots.append((.accessory, v)) }
        if let v = belt { slots.append((.accessory, v)) }
        if let v = medal { slots.append((.medal, v)) }
        if let v = effect { slots.append((.effect, v)) }
        for r in ring { slots.append((.ring, r)) }
        for p in pendant { slots.append((.accessory, p)) }
        return slots
    }
}

// MARK: - Composited Character

/// 合成完成后的角色图像和整体 origin 偏移。
struct CompositedCharacter {
    let image: NSImage
    let originX: Int
    let originY: Int
}

// MARK: - WZ Path Constants

/// WZ 路径常量，完全匹配 MapleSalon2 handlers/path.rs
struct WzPaths {
    /// zmap.img - 身体部位渲染顺序
    static let zmap = "Base/zmap.img/zmap"
    /// smap.img - 子部位映射
    static let smap = "Base/smap.img"
    /// Character - 角色模型根目录
    static let character = "Character"
    /// String/Eqp.img - 装备名称字符串
    static let equipString = "String/Eqp.img"
    /// Effect/ItemEff.img - 装备特效
    static let equipEffect = "Effect/ItemEff.img"
    /// Effect/SetEff.img - 套装特效
    static let equipSetEffect = "Effect/SetEff.img"
    /// Item/Cash/0501.img - 现金特效
    static let cashEffect = "Item/Cash/0501.img"
    /// String/Cash.img - 现金道具名称
    static let cashEffectString = "String/Cash.img"
    /// Item/Install/0370.img - 昵称标签
    static let nickTag = "Item/Install/0370.img"
    /// String/Ins.img - 安装道具名称
    static let nickTagString = "String/Ins.img"
    /// Character/TamingMob - 骑宠
    static let mount = "Character/TamingMob"
    /// String/Eqp.img/Eqp/Taming - 骑宠名称
    static let mountString = "String/Eqp.img/Eqp/Taming"
    /// Skill/RidingSkillInfo.img - 骑宠技能
    static let mountSkill = "Skill/RidingSkillInfo.img"
    /// Item/Install - 椅子/安装道具
    static let chair = "Item/Install"
    /// Item/Cash/0520.img - 现金椅子
    static let cashChair = "Item/Cash/0520.img"
    /// String/Ins.img - 安装道具名称
    static let chairString = "String/Ins.img"
    /// String/Item.img/Ins - 旧版安装道具名称
    static let chairStringOld = "String/Item.img/Ins"
    /// String/Cash.img - 现金道具名称
    static let cashChairString = "String/Cash.img"
    /// Skill - 技能
    static let skill = "Skill"
    /// String/Skill.img - 技能名称
    static let skillString = "String/Skill.img"
    /// Map/Map - 地图
    static let map = "Map/Map"
    /// String/Map.img - 地图名称
    static let mapString = "String/Map.img"
}

// MARK: - Item Info Keys (匹配 MapleSalon2 handlers/item.rs)

struct WzItemKeys {
    /// 是否为现金道具
    static let cash = "cash"
    /// 是否有染色变体
    static let colorvar = "colorvar"
    /// 是否为名字标签
    static let nameTag = "nameTag"
    /// 是否为聊天气泡
    static let chatBalloon = "chatBalloon"
    /// 是否为勋章标签
    static let medalTag = "medalTag"
    /// 是否为昵称标签
    static let nickTag = "nickTag"
}

// MARK: - Action Frame (帧描述)

/// 单个动作帧的描述，包含帧索引、延迟、位移和旋转。
struct ActionFrameDescription {
    /// 动作名称（如 "stand", "walk", "swingOF"）
    let action: String
    /// 帧索引（从 0 开始）
    let frame: Int
    /// 帧延迟（毫秒，默认 100ms）
    let delay: Int
    /// 帧偏移（move 位移）
    let move: (x: Int, y: Int) = (0, 0)
    /// 旋转角度
    let rotate: Int = 0
    /// 是否水平翻转
    let flip: Bool = false
}

// MARK: - 便捷扩展

extension EquipCategory {
    /// 是否是身体部位（非装备类）
    var isBodyPart: Bool {
        switch self {
        case .hair, .face, .skin, .skillSkin:
            return true
        default:
            return false
        }
    }

    /// 是否是特效类
    var isEffect: Bool {
        switch self {
        case .effect, .ringEffect, .necklessEffect, .beltEffect:
            return true
        default:
            return false
        }
    }

    /// 是否在 zmap 中有对应的渲染槽位
    var hasBodySlot: Bool {
        switch self {
        case .cap:        return true
        case .cape:       return true
        case .coat:       return true
        case .glove:      return true
        case .hair:       return true
        case .face:       return true
        case .shoes:      return true
        case .weapon:     return true
        case .shield:     return true
        case .pants:      return true
        default:          return false
        }
    }
}