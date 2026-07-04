import AppKit
import Foundation

// MARK: - WzCharacterCompositor

/// 纸娃娃（Paper Doll）合成引擎。
/// 完整实现 MapleSalon2 的角色合成逻辑，支持多帧动画。
///
/// 合成流程：
/// 1. 加载 zmap（渲染顺序）
/// 2. 加载 body 模板（00002000.img male / 00012000.img female）
/// 3. 逐个 slot 加载装备部位图片
/// 4. 按 z-order 合成到单个 NSImage
///
/// MapleSalon2 对比参考：
/// - handlers/zmap.rs → get_zmap() / resolve_zmap()
/// - handlers/string.rs → get_equip_node() / resolve_equip_string()
/// - handlers/item.rs → get_item_info_node() / get_is_cash_item() / get_is_colorvar()
/// - handlers/png.rs → resolve_png() / resolve_png_unparsed()
/// - handlers/path.rs → 所有 WZ 路径常量
class WzCharacterCompositor {
    let wzLoader: WzImageLoader
    let wzParser: WzXmlParser
    let api: APIClient

    /// 缓存的 zmap 顺序
    private var cachedZmap: [BodySlot]?
    /// 缓存的 smap 映射（未实现）
    private var cachedSmap: [String: String]?

    init(loader: WzImageLoader = WzImageLoader(),
         parser: WzXmlParser = WzXmlParser(),
         api: APIClient = APIClient()) {
        self.wzLoader = loader
        self.wzParser = parser
        self.api = api
    }

    // MARK: - Z-Map / S-Map

    /// 获取身体部位渲染顺序。
    /// 对应 MapleSalon2 zmap.rs → resolve_zmap()。
    /// zmap.img/zmap 中按渲染顺序列出所有 slot 名称。
    func fetchZmap() async -> [BodySlot] {
        if let cached = cachedZmap { return cached }

        guard let node = await wzParser.fetchNode(path: WzPaths.zmap) else {
            logDebug("[WzCharacterCompositor] zmap 加载失败，使用默认顺序")
            cachedZmap = defaultZmapOrder
            return cachedZmap!
        }

        var slots: [BodySlot] = []
        // zmap 的子节点顺序即渲染顺序
        let sortedKeys = node.children.keys.sorted { a, b in
            // 按数值 key 排序（zmap 的子节点通常为 "0", "1", "2"...）
            let ai = Int(a) ?? 0
            let bi = Int(b) ?? 0
            return ai < bi
        }

        for key in sortedKeys {
            guard let child = node.children[key] else { continue }
            let slotName = child.name.isEmpty ? child.stringValue : child.name
            guard let slot = parseBodySlot(slotName) else { continue }
            slots.append(slot)
        }

        if slots.isEmpty {
            logDebug("[WzCharacterCompositor] zmap 为空，使用默认顺序")
            cachedZmap = defaultZmapOrder
        } else {
            cachedZmap = slots
        }
        return cachedZmap!
    }

    /// 获取 smap（子部位映射）。
    /// MapleSalon2 中 SMAP_PATH = "smap.img"，可用于子部位查找。
    func fetchSmap() async -> [String: String] {
        if let cached = cachedSmap { return cached }

        guard let node = await wzParser.fetchNode(path: WzPaths.smap) else { return [:] }

        var map: [String: String] = [:]
        for (_, child) in node.children {
            let name = child.name
            let value = child.stringValue
            if !name.isEmpty && !value.isEmpty {
                map[name] = value
            }
        }

        cachedSmap = map
        return map
    }

    // MARK: - Character Frame Loading

    /// 加载角色一帧的所有身体部位图像。
    ///
    /// - Parameters:
    ///   - appearance: 角色外观配置
    ///   - action: 动作名称（如 "stand", "walk", "swingOF"）
    ///   - frame: 帧索引（从 0 开始）
    /// - Returns: 按 z-order 排序的 BodyPartFrame 数组
    func loadCharacterFrame(appearance: CharacterAppearance,
                           action: String,
                           frame: Int) async -> [BodyPartFrame] {
        let zmap = await fetchZmap()

        // 并行加载所有 slot
        return await withTaskGroup(of: BodyPartFrame?.self) { group in
            for slot in zmap {
                group.addTask { [weak self] in
                    guard let self = self else { return nil }
                    return await self.loadBodySlotFrame(
                        slot: slot,
                        appearance: appearance,
                        action: action,
                        frame: frame
                    )
                }
            }

            var frames: [BodyPartFrame] = []
            for await frame in group {
                if let f = frame { frames.append(f) }
            }
            return frames.sorted { $0.z < $1.z }
        }
    }

    /// 加载单个 body slot 的帧图像。
    /// 根据 slot 类型决定加载哪个部位（body/hair/face/equip）。
    private func loadBodySlotFrame(slot: BodySlot,
                                   appearance: CharacterAppearance,
                                   action: String,
                                   frame: Int) async -> BodyPartFrame? {
        let actionStr = action
        let frameStr = "\(frame)"

        switch slot {
        case .body, .arm, .hand, .head:
            // 身体模板部位：00002000.img（男）或 00012000.img（女）
            let bodyCode = appearance.bodyCode
            return await loadSlotPart(
                code: bodyCode,
                slot: slot,
                wzPath: "\(WzPaths.character)/\(bodyCode).img/\(actionStr)/\(frameStr)/\(slot.rawValue)",
                z: slot.defaultZ
            )

        case .hairOverHead, .hairBelowHead:
            // 发型：通常 Hair/发型ID.img
            let hair = appearance.hair
            guard !hair.isEmpty else { return nil }
            // 发型 slot 使用对应的子部位名称
            let partName: String = (slot == .hairOverHead) ? "hairOverHead" : "hairBelowHead"
            return await loadSlotPart(
                code: hair,
                slot: slot,
                wzPath: "\(WzPaths.character)/Hair/\(hair.paddedId).img/\(actionStr)/\(frameStr)/\(partName)",
                z: slot.defaultZ
            )

        case .face:
            // 脸型：通常 Face/脸型ID.img
            let face = appearance.face
            guard !face.isEmpty else { return nil }
            return await loadSlotPart(
                code: face,
                slot: slot,
                wzPath: "\(WzPaths.character)/Face/\(face.paddedId).img/\(actionStr)/\(frameStr)/\(slot.rawValue)",
                z: slot.defaultZ
            )

        case .cap:
            return await loadEquipSlotPart(
                id: appearance.cap,
                category: "Cap",
                slot: slot,
                action: actionStr,
                frame: frameStr
            )

        case .cape, .capeBack:
            return await loadEquipSlotPart(
                id: appearance.cape,
                category: "Cape",
                slot: slot,
                action: actionStr,
                frame: frameStr
            )

        case .coat:
            return await loadEquipSlotPart(
                id: appearance.coat,
                category: "Coat",
                slot: .body,
                action: actionStr,
                frame: frameStr
            )

        case .glove, .gloveOver:
            return await loadEquipSlotPart(
                id: appearance.glove,
                category: "Glove",
                slot: slot,
                action: actionStr,
                frame: frameStr
            )

        case .pants:
            return await loadEquipSlotPart(
                id: appearance.pants,
                category: "Pants",
                slot: .body,
                action: actionStr,
                frame: frameStr
            )

        case .shoe:
            return await loadEquipSlotPart(
                id: appearance.shoes,
                category: "Shoes",
                slot: .shoe,
                action: actionStr,
                frame: frameStr
            )

        case .weapon:
            return await loadEquipSlotPart(
                id: appearance.weapon,
                category: "Weapon",
                slot: slot,
                action: actionStr,
                frame: frameStr
            )

        case .shield:
            return await loadEquipSlotPart(
                id: appearance.shield,
                category: "Shield",
                slot: slot,
                action: actionStr,
                frame: frameStr
            )

        case .ear:
            // 耳环：Accessory/耳环ID.img，使用 ear 子部位
            return await loadEquipSlotPart(
                id: appearance.earring,
                category: "Accessory",
                slot: slot,
                action: actionStr,
                frame: frameStr
            )

        case .tail:
            // 尾巴（某些装备有尾部效果）
            return nil

        default:
            return nil
        }
    }

    /// 加载装备的某个 slot 部位。
    private func loadEquipSlotPart(id: String?,
                                   category: String,
                                   slot: BodySlot,
                                   action: String,
                                   frame: String) async -> BodyPartFrame? {
        guard let id = id, !id.isEmpty else { return nil }
        let paddedId = id.paddedId
        // 不使用 slot.rawValue 作为子路径，而是直接使用 action/frame
        let wzPath = "\(WzPaths.character)/\(category)/\(paddedId).img/\(action)/\(frame)"
        return await loadSlotPart(code: id, slot: slot, wzPath: wzPath, z: slot.defaultZ)
    }

    /// 从指定 WZ 路径加载一个部位帧。
    /// 先尝试直接下载 PNG，若失败则尝试解析 origin/z/offset。
    private func loadSlotPart(code: String,
                              slot: BodySlot,
                              wzPath: String,
                              z: Int) async -> BodyPartFrame? {
        // 1. 尝试下载 PNG
        guard let imageData = await wzLoader.fetchImage(wzPath: wzPath),
              let image = NSImage(data: imageData) else {
            return nil
        }

        // 2. 解析 origin（从 WZ 节点的 vector 子节点获取）
        let (originX, originY) = await fetchOrigin(wzPath: wzPath)

        return BodyPartFrame(
            slot: slot,
            image: image,
            originX: originX,
            originY: originY,
            z: z,
            flip: false
        )
    }

    /// 从 WZ 节点解析 origin（ox, oy）。
    /// 对应 MapleSalon2 中 frame 节点下的 origin vector。
    func fetchOrigin(wzPath: String) async -> (x: Int, y: Int) {
        // 尝试获取 origin 节点
        guard let node = await wzParser.fetchNode(path: "\(wzPath)/origin") else {
            return (0, 0)
        }

        if case .vector = node.type {
            return (node.x, node.y)
        }
        return (0, 0)
    }

    // MARK: - Compositing

    /// 合成所有身体部位为一幅图像。
    ///
    /// - Parameter frames: 按 z-order 排序的 BodyPartFrame 数组
    /// - Returns: 合成后的 CompositedCharacter（图像 + 整体 origin）
    func compositeCharacter(frames: [BodyPartFrame]) -> CompositedCharacter {
        guard !frames.isEmpty else {
            return CompositedCharacter(image: NSImage(size: .zero), originX: 0, originY: 0)
        }

        // 转换为 WzCompositor 的 CompositingLayer 格式
        let layers = frames.map { frame -> CompositingLayer in
            CompositingLayer(
                image: frame.image,
                originX: frame.originX,
                originY: frame.originY,
                z: frame.z,
                flipX: frame.flip
            )
        }

        // 合成前计算整体 origin
        // 所有 layer 的 origin 取最小值作为整体 origin
        let minOriginX = frames.map(\.originX).min() ?? 0
        let minOriginY = frames.map(\.originY).min() ?? 0

        let composited = WzCompositor.composite(layers: layers)

        return CompositedCharacter(
            image: composited,
            originX: minOriginX,
            originY: minOriginY
        )
    }

    // MARK: - Animation

    /// 获取某个动作的帧列表（带延迟）。
    ///
    /// 从 WZ 结构读取动作下的子节点，每个子节点代表一帧。
    /// 每帧可能包含 delay 属性（毫秒）。
    ///
    /// - Parameters:
    ///   - appearance: 角色外观配置
    ///   - action: 动作名称
    /// - Returns: (frameIndex, delay) 数组
    func fetchActionTimeline(appearance: CharacterAppearance,
                            action: String) async -> [(frame: Int, delay: Int)] {
        let bodyCode = appearance.bodyCode
        let actionPath = "\(WzPaths.character)/\(bodyCode).img/\(action)"

        guard let node = await wzParser.fetchNode(path: actionPath) else {
            // 若 body 模板没有此动作，尝试从装备加载（武器动作等）
            return await fetchEquipActionTimeline(appearance: appearance, action: action)
        }

        // 收集所有帧节点（按数字 key 排序）
        let frameKeys = node.children.keys.compactMap { Int($0) }.sorted()

        var result: [(Int, Int)] = []
        for key in frameKeys {
            guard let frameNode = node.children["\(key)"] else { continue }

            // 读取 delay（默认为 100ms）
            let delay: Int
            if let delayNode = frameNode.children["delay"] {
                delay = delayNode.intValue > 0 ? delayNode.intValue : 100
            } else {
                delay = 100
            }

            result.append((key, delay))
        }

        guard !result.isEmpty else {
            // 后备：尝试从装备加载
            return await fetchEquipActionTimeline(appearance: appearance, action: action)
        }

        return result
    }

    /// 从装备中获取动作帧列表（武器动作等）。
    private func fetchEquipActionTimeline(appearance: CharacterAppearance,
                                         action: String) async -> [(Int, Int)] {
        // 尝试从武器获取帧信息
        guard let weapon = appearance.weapon else { return [(0, 100)] }

        let paddedId = weapon.paddedId
        let actionPath = "\(WzPaths.character)/Weapon/\(paddedId).img/\(action)"

        guard let node = await wzParser.fetchNode(path: actionPath) else {
            return [(0, 100)]
        }

        let frameKeys = node.children.keys.compactMap { Int($0) }.sorted()

        return frameKeys.map { key in
            let delay: Int
            if let frameNode = node.children["\(key)"],
               let delayNode = frameNode.children["delay"] {
                delay = delayNode.intValue > 0 ? delayNode.intValue : 100
            } else {
                delay = 100
            }
            return (key, delay)
        }
    }

    /// 获取该外观支持的所有动作列表（来自 body 模板）。
    func fetchActions(appearance: CharacterAppearance) async -> [String] {
        let bodyCode = appearance.bodyCode
        guard let node = await wzParser.fetchNode(path: "\(WzPaths.character)/\(bodyCode).img") else {
            return ["stand", "walk"]
        }

        // 排除非动作节点
        let blacklist: Set<String> = [
            "emotion", "default", "info", "heal",
            "swingOF", "swingOB", "swingT1", "swingT2", "swingT3",
            "swingP1", "swingP2", "swingPF", "stabOF", "stabOB",
            "stabT1", "stabT2", "stabT3", "stabPF", "shootOF",
            "shootOB", "shootOB", "shootF"
        ]

        return node.children.keys
            .filter { !blacklist.contains($0) && !$0.hasPrefix(".") }
            .sorted()
    }

    // MARK: - Equipment Lookup

    /// 获取装备列表（含名称）。
    /// 对应 MapleSalon2 string.rs → resolve_equip_string()。
    ///
    /// - Parameter includeExtra: 是否包含 extra info（cash/colorvar/effect 检测）
    /// - Returns: EquippedItem 数组
    func fetchEquipList(includeExtra: Bool = true) async -> [EquippedItem] {
        var result: [EquippedItem] = []

        // 1. 获取 String/Eqp.img 下的装备字符串
        if let stringNode = await wzParser.fetchNode(path: WzPaths.equipString) {
            // 遍历 EQUIP_CATEGORY_NEEDS 中的分类
            for categoryName in EquipCategory.equipCategories {
                guard let categoryNode = stringNode.children[categoryName] else { continue }
                let category = EquipCategory.from(wzDirectory: categoryName)

                for (id, itemNode) in categoryNode.children {
                    if itemNode.type != .dir { continue }
                    guard let nameNode = itemNode.children["name"] else { continue }

                    let name = nameNode.stringValue
                    // 双刃剑（134xxx）应归类为 Shield
                    let actualCategory = id.hasPrefix("134") ? EquipCategory.shield : category

                    result.append(EquippedItem(
                        category: actualCategory,
                        id: id,
                        name: name
                    ))
                }
            }

            // 2. Skin
            if let skinNode = stringNode.children["Skin"] {
                let category = EquipCategory.skin
                for (id, itemNode) in skinNode.children {
                    if itemNode.type != .dir { continue }
                    guard let nameNode = itemNode.children["name"] else { continue }
                    result.append(EquippedItem(
                        category: category,
                        id: id,
                        name: nameNode.stringValue
                    ))
                }
            }
        }

        // 3. extra_info: cash/colorvar/effect 检测
        if includeExtra {
            await enrichEquipInfo(&result)
        }

        // 4. 现金特效 (Item/Cash/0501.img)
        if let cashEffects = await fetchCashEffects() {
            result.append(contentsOf: cashEffects)
        }

        // 5. 昵称标签 (Item/Install/0370.img)
        if let nickTags = await fetchNickTags() {
            result.append(contentsOf: nickTags)
        }

        result.sort { a, b in a.id < b.id }
        return result
    }

    /// 为装备列表补充 cash/colorvar/effect 信息。
    /// 对应 MapleSalon2 string.rs 中 extra_info 逻辑。
    private func enrichEquipInfo(_ items: inout [EquippedItem]) async {
        // 获取 Effect/ItemEff.img 节点（用于检测 hasEffect）
        let effectNode = await wzParser.fetchNode(path: WzPaths.equipEffect)

        // 获取 Character 节点（用于读取 info）
        let characterNode = await wzParser.fetchNode(path: WzPaths.character)

        for i in items.indices {
            let item = items[i]
            guard let dir = item.category.wzDirectoryName else { continue }

            // 读取道具 info 节点
            let infoPath = "\(WzPaths.character)/\(dir)/\(item.paddedId).img/info"
            guard let infoNode = await wzParser.fetchNode(path: infoPath) else { continue }

            // 检测 cash
            if let cashNode = infoNode.children[WzItemKeys.cash] {
                items[i].isCash = cashNode.intValue == 1
            }

            // 检测 colorvar
            if infoNode.children[WzItemKeys.colorvar] != nil {
                items[i].hasColorvar = true
            }

            // 检测 nameTag / chatBalloon / medalTag
            if infoNode.children[WzItemKeys.nameTag] != nil {
                items[i].category = .nameTag
            }
            if infoNode.children[WzItemKeys.chatBalloon] != nil {
                items[i].category = .chatBalloon
            }
            if infoNode.children[WzItemKeys.medalTag] != nil {
                items[i].category = .medal
            }

            // 武器现金判定
            if item.category == .weapon && items[i].isCash {
                items[i].hasEffect = true
            }

            // effect 检测：查看 Effect/ItemEff.img 中是否有此 ID
            if let eNode = effectNode, eNode.children[item.id] != nil {
                if let effectChild = eNode.children[item.id],
                   effectChild.children["effect"] != nil {
                    items[i].hasEffect = true
                }
            }

            // 分类重映射（Effect 分类）
            if items[i].category == .ring && items[i].hasEffect {
                items[i].category = .ringEffect
            } else if items[i].category == .accessory && item.id.hasPrefix("112") && items[i].hasEffect {
                items[i].category = .necklessEffect
            } else if items[i].category == .accessory && item.id.hasPrefix("113") && items[i].hasEffect {
                items[i].category = .beltEffect
            }
        }
    }

    /// 获取现金道具特效列表。
    /// 对应 MapleSalon2 string.rs → resolve_cash_effect_string()。
    private func fetchCashEffects() async -> [EquippedItem]? {
        guard let effectNode = await wzParser.fetchNode(path: WzPaths.cashEffect),
              let stringNode = await wzParser.fetchNode(path: WzPaths.cashEffectString) else {
            return nil
        }

        var result: [EquippedItem] = []
        for (fullId, effectChild) in effectNode.children {
            if effectChild.children["effect"] == nil { continue }

            let id = fullId.trimmingCharacters(in: CharacterSet(charactersIn: "0"))
            let name: String
            if let nameNode = stringNode.children[id]?.children["name"] {
                name = nameNode.stringValue
            } else {
                name = "null"
            }

            result.append(EquippedItem(
                category: .effect,
                id: id,
                name: name,
                isCash: true,
                hasEffect: true
            ))
        }

        result.sort { a, b in a.id < b.id }
        return result
    }

    /// 获取昵称标签列表。
    /// 对应 MapleSalon2 string.rs → resolve_nicktag_string()。
    private func fetchNickTags() async -> [EquippedItem]? {
        guard let nicktagNode = await wzParser.fetchNode(path: WzPaths.nickTag),
              let stringNode = await wzParser.fetchNode(path: WzPaths.nickTagString) else {
            return nil
        }

        var result: [EquippedItem] = []
        for (fullId, nickChild) in nicktagNode.children {
            let infoNode = nickChild.children["info"]
            if infoNode?.children[WzItemKeys.nickTag] == nil { continue }

            let id = fullId.trimmingCharacters(in: CharacterSet(charactersIn: "0"))
            let name: String
            if let nameNode = stringNode.children[id]?.children["name"] {
                name = nameNode.stringValue
            } else {
                name = "null"
            }

            result.append(EquippedItem(
                category: .nickTag,
                id: id,
                name: name,
                isCash: false,
                hasColorvar: false,
                hasEffect: false
            ))
        }

        result.sort { a, b in a.id < b.id }
        return result
    }

    // MARK: - Character Frame Generation (Spritesheet)

    /// 生成角色精灵图条（等同于 mob 精灵图生成逻辑）。
    /// 为给定外观的每个动作生成水平条带 PNG。
    ///
    /// - Parameters:
    ///   - appearance: 角色外观配置
    ///   - actions: 要生成的动作列表（nil = 所有动作）
    /// - Returns: 动作名 → 精灵图配置 的字典
    func generateCharacterSpritesheet(appearance: CharacterAppearance,
                                      actions: [String]? = nil) async -> [String: SpriteEntry] {
        let allActions: [String]
        if let acts = actions {
            allActions = acts
        } else {
            allActions = await fetchActions(appearance: appearance)
        }
        var result: [String: SpriteEntry] = [:]

        for action in allActions {
            guard let timeline = await fetchActionTimeline(appearance: appearance, action: action).nilIfEmpty else {
                continue
            }

            // 逐帧合成
            var frameImages: [NSImage] = []
            var origins: [(x: Int, y: Int)] = []
            var maxZ: Int = 0

            for (frameIndex, delay) in timeline {
                let frames = await loadCharacterFrame(
                    appearance: appearance,
                    action: action,
                    frame: frameIndex
                )
                guard !frames.isEmpty else { continue }

                // 追踪最大 z 值
                for f in frames {
                    maxZ = max(maxZ, f.z)
                }

                let composited = compositeCharacter(frames: frames)
                frameImages.append(composited.image)
                origins.append((composited.originX, composited.originY))
            }

            guard !frameImages.isEmpty else { continue }

            // 确定统一尺寸
            let frameWidth = Int(frameImages.map(\.size.width).max() ?? 0)
            let frameHeight = Int(frameImages.map(\.size.height).max() ?? 0)

            // 水平拼接精灵图条
            let totalWidth = frameWidth * frameImages.count
            let stripImage = NSImage(size: NSSize(width: totalWidth, height: frameHeight))
            stripImage.lockFocusFlipped(false)

            for (i, img) in frameImages.enumerated() {
                let x = CGFloat(i * frameWidth)
                let y = CGFloat(frameHeight - Int(img.size.height)) // 底部对齐
                img.draw(at: NSPoint(x: x, y: y),
                         from: .zero,
                         operation: .sourceOver,
                         fraction: 1.0)
            }

            stripImage.unlockFocus()

            // 计算整体 origin
            let overallOx = origins.map(\.x).min() ?? 0
            let overallOy = origins.map(\.y).min() ?? 0

            result[action] = SpriteEntry(
                file: "\(action).png",
                frames: frameImages.count,
                frameWidth: frameWidth,
                frameHeight: frameHeight,
                originX: overallOx,
                originY: overallOy
            )

            // 保存 PNG 到缓存
            // TODO: 使用 CacheManager 写入
        }

        return result
    }

    // MARK: - Convenience

    /// 默认 zmap 渲染顺序（当 WZ 加载失败时使用）。
    private var defaultZmapOrder: [BodySlot] {
        [
            .back,          // z=0
            .hairBelowHead, // z=1
            .body,          // z=2
            .head,          // z=3
            .ear,           // z=4
            .face,          // z=5
            .tail,          // z=6
            .capeBack,      // z=7
            .arm,           // z=8
            .hand,          // z=9
            .shoe,          // z=10
            .pants,         // z=11
            .coat,          // z=12
            .glove,         // z=13
            .gloveOver,     // z=14
            .shield,        // z=15
            .weapon,        // z=16
            .cape,          // z=17
            .hairOverHead,  // z=18
            .cap            // z=19
        ]
    }

    /// 将字符串名称转为 BodySlot。
    private func parseBodySlot(_ name: String) -> BodySlot? {
        switch name {
        case "back":              return .back
        case "body":              return .body
        case "arm":               return .arm
        case "armBelow":          return .arm // armBelow 映射到 arm
        case "head":              return .head
        case "hand":              return .hand
        case "handBelow":         return .hand // handBelow 映射到 hand
        case "weapon":            return .weapon
        case "weaponBelow":       return .weapon
        case "cap":               return .cap
        case "cape":              return .cape
        case "capeBack":          return .capeBack
        case "glove":             return .glove
        case "gloveOver":         return .gloveOver
        case "gloveBelow":        return .glove
        case "shoe":              return .shoe
        case "shoes":             return .shoe
        case "shield":            return .shield
        case "shieldBelow":       return .shield
        case "hair":              return .hairOverHead
        case "hairOverHead":      return .hairOverHead
        case "hairBelowHead":     return .hairBelowHead
        case "face":              return .face
        case "ear":               return .ear
        case "earBelow":          return .ear
        case "tail":              return .tail
        default:
            return nil
        }
    }
}

// MARK: - BodySlot Z-Order Defaults

extension BodySlot {
    /// 默认 z-order 值（当 WZ 不提供时使用）。
    var defaultZ: Int {
        switch self {
        case .back:              return 0
        case .hairBelowHead:     return 1
        case .body:              return 2
        case .head:              return 3
        case .ear:               return 4
        case .face:              return 5
        case .tail:              return 6
        case .capeBack:          return 7
        case .arm:               return 8
        case .hand:              return 9
        case .shoe:              return 10
        case .pants:             return 11
        case .coat:              return 12
        case .glove:             return 13
        case .gloveOver:         return 14
        case .shield:            return 15
        case .weapon:            return 16
        case .cape:              return 17
        case .hairOverHead:      return 18
        case .cap:               return 19
        }
    }
}

// MARK: - ID Padding Extension

extension String {
    /// 将 ID 补零到 8 位（如 "30000" → "00030000"）。
    /// MapleSalon2 中使用 {:0>8} 格式。
    var paddedId: String {
        String(format: "%08d", Int(self) ?? 0)
    }
}

// MARK: - Array Nil-If-Empty

extension Array {
    var nilIfEmpty: Self? {
        isEmpty ? nil : self
    }
}
