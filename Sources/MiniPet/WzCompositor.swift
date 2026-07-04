import AppKit

// MARK: - Compositing Layer Item

/// 合成图层项，包含图片、origin 坐标、z-order 和翻转标志。
/// 与 MapleSalon2 的 `g.drawImage(im, x - originX, y - originY)` 模式匹配。
struct CompositingLayer {
    var image: NSImage
    var originX: Int
    var originY: Int
    var z: Int
    var flipX: Bool = false
}

// MARK: - Action Frame

/// 动作帧描述，包含帧索引、延迟、位移和旋转。
struct ActionFrame {
    var action: String       // 基础动作名（如 "stand", "swingOF"）
    var frame: Int = 0       // 帧索引
    var delay: Int = 100     // 帧延迟（毫秒）
    var move: (x: Int, y: Int) = (0, 0)
    var rotate: Int = 0
    var flip: Bool = false
}

// MARK: - Bone Offset

/// 骨骼偏移，描述子部位相对于父部位的偏移量。
struct BoneOffset {
    var name: String
    var offsetX: Int
    var offsetY: Int
}

// MARK: - Composited Result

/// 合成完成的图像 + 整体 origin 偏移。
struct CompositedResult {
    let image: NSImage
    let originX: Int
    let originY: Int
    let width: Int
    let height: Int
}

// MARK: - WzCompositor (Origin-based Compositing Engine)

/// WZ origin 合成引擎。
/// 匹配后端 `dr(g, im, x, y, ox, oy, flip)` 模式：
/// `g.drawImage(im, x - originX, y - originY, null)` + 水平翻转支持。
class WzCompositor {

    /// 合成多个图层为一张图片，按 z-order 排序。
    /// - 自动计算 bounds（取所有图层的并集）。
    /// - 返回合成后的 NSImage。
    static func composite(layers: [CompositingLayer], bundleX: CGFloat = 0, bundleY: CGFloat = 0) -> NSImage {
        guard !layers.isEmpty else {
            return NSImage(size: .zero)
        }

        // 计算所有图层的边界
        var minX: CGFloat = 0, minY: CGFloat = 0
        var maxX: CGFloat = 0, maxY: CGFloat = 0
        for layer in layers {
            let imgSize = layer.image.size
            let drawX = -CGFloat(layer.originX)
            let drawY = -CGFloat(layer.originY)
            minX = min(minX, drawX)
            minY = min(minY, drawY)
            maxX = max(maxX, drawX + imgSize.width)
            maxY = max(maxY, drawY + imgSize.height)
        }

        let w = max(maxX - minX, 1)
        let h = max(maxY - minY, 1)
        let ox = -minX + bundleX
        let oy = -minY + bundleY

        let result = NSImage(size: NSSize(width: w, height: h))
        result.lockFocusFlipped(false)

        // 按 z 排序后逐层绘制
        let sorted = layers.sorted { $0.z < $1.z }
        for layer in sorted {
            draw(image: layer.image,
                 at: NSPoint(x: ox, y: oy),
                 originX: CGFloat(layer.originX),
                 originY: CGFloat(layer.originY),
                 flipX: layer.flipX)
        }

        result.unlockFocus()
        return result
    }

    /// 合成完成后返回 CompositedResult（包含 origin 信息）。
    static func compositeWithOrigin(layers: [CompositingLayer], bundleX: CGFloat = 0, bundleY: CGFloat = 0) -> CompositedResult {
        guard !layers.isEmpty else {
            return CompositedResult(image: NSImage(size: .zero), originX: 0, originY: 0, width: 0, height: 0)
        }

        var minX: CGFloat = 0, minY: CGFloat = 0
        var maxX: CGFloat = 0, maxY: CGFloat = 0
        for layer in layers {
            let imgSize = layer.image.size
            let drawX = -CGFloat(layer.originX)
            let drawY = -CGFloat(layer.originY)
            minX = min(minX, drawX)
            minY = min(minY, drawY)
            maxX = max(maxX, drawX + imgSize.width)
            maxY = max(maxY, drawY + imgSize.height)
        }

        let w = max(maxX - minX, 1)
        let h = max(maxY - minY, 1)
        let ox = -minX + bundleX
        let oy = -minY + bundleY

        let result = NSImage(size: NSSize(width: w, height: h))
        result.lockFocusFlipped(false)

        let sorted = layers.sorted { $0.z < $1.z }
        for layer in sorted {
            draw(image: layer.image,
                 at: NSPoint(x: ox, y: oy),
                 originX: CGFloat(layer.originX),
                 originY: CGFloat(layer.originY),
                 flipX: layer.flipX)
        }

        result.unlockFocus()

        return CompositedResult(
            image: result,
            originX: Int(ox),
            originY: Int(oy),
            width: Int(w),
            height: Int(h)
        )
    }

    /// 用 origin 定位绘制单张图片。
    /// 等价于: `g.drawImage(im, x - originX, y - originY)`
    private static func draw(image: NSImage, at position: NSPoint,
                             originX: CGFloat, originY: CGFloat, flipX: Bool = false) {
        let drawPoint = NSPoint(x: position.x - originX, y: position.y - originY)
        if flipX {
            // 水平翻转：translate + scale(-1, 1)
            let transform = NSAffineTransform()
            transform.translateX(by: position.x + originX, yBy: position.y)
            transform.scaleX(by: -1, yBy: 1)
            transform.concat()
            image.draw(at: NSPoint(x: -originX, y: -originY),
                       from: .zero,
                       operation: .sourceOver,
                       fraction: 1.0)
            // 恢复变换
            transform.invert()
            transform.concat()
        } else {
            image.draw(at: drawPoint, from: .zero, operation: .sourceOver, fraction: 1.0)
        }
    }
}

// MARK: - AvatarCompositor (Paper Doll)

/// 角色纸娃娃合成器。
/// 根据 CharacterAppearance 加载身体模板和装备，按 zmap 定义的顺序合成全身像。
///
/// 流程：
/// 1. 加载 Base.wz/zmap.img/zmap → 获取 body slot 渲染顺序
/// 2. 根据性别加载 body 模板（00002000 male / 00012000 female）
/// 3. 按 zmap 顺序加载每个 body slot 对应的装备帧
/// 4. 用 origin 坐标定位，合成完整的角色帧
class AvatarCompositor {
    private let wzLoader: WzImageLoader
    private let wzParser: WzXmlParser

    // Body 模板代码（00002000 = 男性，00012000 = 女性）
    private let bodyMale = "00002000"
    private let bodyFemale = "00012000"

    init(loader: WzImageLoader = WzImageLoader(), parser: WzXmlParser = WzXmlParser()) {
        self.wzLoader = loader
        self.wzParser = parser
    }

    // MARK: - ZMap 加载

    /// 从 Base.wz/zmap.img/zmap 获取 body slot 渲染顺序。
    /// 返回 z-order 排序后的 slot 名称数组（从底层到上层）。
    func fetchZmap() async -> [String] {
        // 尝试从后端 API /mapping/zmap 获取
        if let base = await resolveBase() {
            let urlStr = "\(base)/mapping/zmap"
            if let url = URL(string: urlStr) {
                do {
                    let (data, _) = try await URLSession.shared.data(from: url)
                    if let json = try? JSONSerialization.jsonObject(with: data) as? [String],
                       !json.isEmpty {
                        return json
                    }
                } catch {
                    logDebug("fetchZmap via API failed: \(error)")
                }
            }
        }

        // 回退到 XML 解析
        guard let node = await wzParser.fetchNode(path: WzPaths.zmap) else { return [] }
        let children = node.children.values
            .filter { $0.type == .null || $0.type == .stringValue || $0.type == .dir }
        return children.map { $0.name }
            .filter { !$0.isEmpty }
    }

    // MARK: - Body Slot 映射

    /// 根据 body slot 名称确定对应的 Character 子目录和 item code。
    /// 返回 (category, itemCode) 或 nil（该 slot 无装备）。
    private func bodySlotToEquip(_ slot: String, appearance: CharacterAppearance) -> (String, String)? {
        switch slot {
        case "body":        return ("Body", appearance.bodyCode)
        case "head":        return ("Head", appearance.bodyCode)
        case "arm":         return ("Head", appearance.bodyCode) // 手臂跟随身体
        case "hair":        return ("Hair", appearance.hair)
        case "hairOverHead": return ("Hair", appearance.hair)
        case "face":        return ("Face", appearance.face)
        case "cap":         return appearance.cap.map { ("Cap", $0) }
        case "cape":        return appearance.cape.map { ("Cape", $0) }
        case "capeBack":    return appearance.cape.map { ("Cape", $0) }
        case "coat":        return appearance.coat.map { ("Coat", $0) }
        case "longcoat":    return appearance.longcoat.map { ("Longcoat", $0) }
        case "pants":       return appearance.pants.map { ("Pants", $0) }
        case "shoes":       return appearance.shoes.map { ("Shoes", $0) }
        case "weapon":      return appearance.weapon.map { ("Weapon", $0) }
        case "shield":      return appearance.shield.map { ("Shield", $0) }
        case "glove":       return appearance.glove.map { ("Glove", $0) }
        case "gloveOver":   return appearance.glove.map { ("Glove", $0) }
        case "hand":        return nil // 手部跟随身体
        case "ear":         return appearance.earring.map { ("Accessory", $0) }
        case "tail":        return nil
        default:
            logDebug("未知 body slot: \(slot)")
            return nil
        }
    }

    // MARK: - 动作帧加载

    /// 获取角色的可用动作列表。
    /// 从 body 模板中读取所有动作子节点，过滤掉战斗动作。
    func fetchActions(gender: Int) async -> [String] {
        let bodyCode = gender == 0 ? bodyMale : bodyFemale
        guard let base = await resolveBase() else { return [] }

        // 尝试从后端获取 body 模板的 JSON 结构
        let urlStr = "\(base)/node/json/Character/\(bodyCode).img?simple=true"
        if let url = URL(string: urlStr) {
            do {
                let (data, _) = try await URLSession.shared.data(from: url)
                if let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] {
                    let childKeys = json.keys
                    let blacklist: Set<String> = ["emotion", "default", "info", "heal",
                                                   "swingOF", "swingOB", "swingT1", "swingT2", "swingT3",
                                                   "swingP1", "swingP2", "swingPF", "stabOF", "stabOB",
                                                   "stabT1", "stabT2", "stabT3", "stabPF", "shootOF",
                                                   "shootOB", "shootF"]
                    return childKeys.filter { !blacklist.contains($0) }.sorted()
                }
            } catch {
                logDebug("fetchActions via API failed: \(error)")
            }
        }

        // 回退到 XML 解析
        guard let node = await wzParser.fetchNode(path: "Character/\(bodyCode).img") else { return [] }
        let blacklist: Set<String> = ["emotion", "default", "info", "heal",
                                       "swingOF", "swingOB", "swingT1", "swingT2", "swingT3",
                                       "swingP1", "swingP2", "swingPF", "stabOF", "stabOB",
                                       "stabT1", "stabT2", "stabT3", "stabPF", "shootOF",
                                       "shootOB", "shootF"]
        return node.children.keys
            .filter { !blacklist.contains($0) }
            .sorted()
    }

    /// 加载指定 slot 在某个动作帧中的图片。
    /// - Parameters:
    ///   - category: Character 子目录（如 "Cap", "Coat"）
    ///   - itemCode: 道具代码（8 位，如 "01001011"）
    ///   - action: 动作名（如 "stand", "walk"）
    ///   - frame: 帧索引
    ///   - bone: 子骨骼名（可选，用于 cape 等有多子帧的部件）
    /// - Returns: (image, originX, originY) 或 nil
    func loadBodyPartFrame(category: String, itemCode: String, action: String, frame: Int, bone: String? = nil) async -> (NSImage, Int, Int)? {
        let paddedCode = String(format: "%08d", Int(itemCode) ?? 0)
        var wzPath = "Character/\(category)/\(paddedCode).img/\(action)/\(frame)"
        if let b = bone { wzPath += "/\(b)" }

        // 下载 PNG 帧图片
        guard let data = await wzLoader.fetchImage(wzPath: wzPath) else {
            logDebug("loadBodyPartFrame 失败: \(wzPath)")
            return nil
        }
        guard let image = NSImage(data: data) else { return nil }

        // 获取 origin 坐标：从后端 /node/json 接口读取
        let originPath = "Character/\(category)/\(paddedCode).img/\(action)/\(frame)/origin"
        let (ox, oy) = await fetchOrigin(path: originPath)

        return (image, ox, oy)
    }

    /// 从后端 API 获取 origin 坐标。
    private func fetchOrigin(path: String) async -> (Int, Int) {
        guard let base = await resolveBase() else { return (0, 0) }
        let urlStr = "\(base)/node/json/\(path)?simple=true"
        guard let url = URL(string: urlStr) else { return (0, 0) }
        do {
            let (data, _) = try await URLSession.shared.data(from: url)
            if let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] {
                let ox = json["x"] as? Int ?? 0
                let oy = json["y"] as? Int ?? 0
                return (ox, oy)
            }
        } catch {
            logDebug("fetchOrigin 失败: \(path) - \(error)")
        }
        return (0, 0)
    }

    /// 解析帧延迟（delay 属性）。
    /// 默认 100ms，但许多动作有自定义延迟。
    private func fetchFrameDelay(category: String, itemCode: String, action: String, frame: Int) async -> Int {
        let paddedCode = String(format: "%08d", Int(itemCode) ?? 0)
        let path = "Character/\(category)/\(paddedCode).img/\(action)/\(frame)/delay"
        guard let base = await resolveBase() else { return 100 }
        let urlStr = "\(base)/node/json/\(path)?simple=true"
        guard let url = URL(string: urlStr) else { return 100 }
        do {
            let (data, _) = try await URLSession.shared.data(from: url)
            if let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] {
                return json["value"] as? Int ?? 100
            }
        } catch { }
        return 100
    }

    // MARK: - 角色全身合成

    /// 合成角色的单帧全身像。
    ///
    /// 流程：
    /// 1. 根据 zmap 获取 body slot 渲染顺序
    /// 2. 遍历每个 slot，加载对应的 body part 图片
    /// 3. 使用 WzCompositor 按 z-order 合成
    ///
    /// - Parameters:
    ///   - appearance: 角色外观配置
    ///   - action: 动作名（如 "stand", "walk"）
    ///   - frame: 帧索引
    /// - Returns: 合成完成的图像（nil 表示加载失败）
    func compositeCharacterFrame(appearance: CharacterAppearance, action: String, frame: Int) async -> NSImage? {
        let zmap = await fetchZmap()
        guard !zmap.isEmpty else {
            logDebug("compositeCharacterFrame: zmap 为空")
            return nil
        }

        var layers: [CompositingLayer] = []
        var zIndex = 0

        for slot in zmap {
            // 确定此 slot 对应的装备
            guard let (category, itemCode) = bodySlotToEquip(slot, appearance: appearance) else {
                zIndex += 1
                continue
            }

            // 处理特殊的 slot 类型
            let bone: String?
            switch slot {
            case "capeBack": bone = "back"
            case "cape": bone = "front"
            case "gloveOver": bone = "overGlove"
            case "hairOverHead": bone = "overHead"
            default: bone = nil
            }

            // 加载此 slot 的帧图片
            guard let (image, ox, oy) = await loadBodyPartFrame(
                category: category, itemCode: itemCode,
                action: action, frame: frame, bone: bone
            ) else {
                zIndex += 1
                continue
            }

            // 检查是否应翻转
            var flip = false
            if action == "walk" && (slot == "weapon" || slot == "shield") {
                // 行走时武器和盾牌可能翻转，具体取决于实际数据
                // 此处通过后端查询
                flip = false
            }

            layers.append(CompositingLayer(
                image: image,
                originX: ox,
                originY: oy,
                z: zIndex,
                flipX: flip
            ))
            zIndex += 1
        }

        guard !layers.isEmpty else { return nil }

        return WzCompositor.composite(layers: layers)
    }

    /// 合成完整的角色帧，返回 CompositedResult（包含 origin 信息）。
    func compositeCharacterFrameWithOrigin(appearance: CharacterAppearance, action: String, frame: Int) async -> CompositedResult? {
        let zmap = await fetchZmap()
        guard !zmap.isEmpty else { return nil }

        var layers: [CompositingLayer] = []
        var zIndex = 0

        for slot in zmap {
            guard let (category, itemCode) = bodySlotToEquip(slot, appearance: appearance) else {
                zIndex += 1
                continue
            }

            let bone: String?
            switch slot {
            case "capeBack": bone = "back"
            case "cape": bone = "front"
            case "gloveOver": bone = "overGlove"
            case "hairOverHead": bone = "overHead"
            default: bone = nil
            }

            guard let (image, ox, oy) = await loadBodyPartFrame(
                category: category, itemCode: itemCode,
                action: action, frame: frame, bone: bone
            ) else {
                zIndex += 1
                continue
            }

            layers.append(CompositingLayer(
                image: image,
                originX: ox,
                originY: oy,
                z: zIndex
            ))
            zIndex += 1
        }

        guard !layers.isEmpty else { return nil }

        return WzCompositor.compositeWithOrigin(layers: layers)
    }

    /// 加载一个动作的所有帧并返回帧数组。
    /// 用于动画播放。
    func loadActionFrames(appearance: CharacterAppearance, action: String) async -> [(NSImage, Int)] {
        // 先获取此动作的帧数
        let bodyCode = appearance.gender == 0 ? bodyMale : bodyFemale
        var frameCount = 0
        if let base = await resolveBase() {
            let urlStr = "\(base)/node/json/Character/\(bodyCode).img/\(action)?simple=true"
            if let url = URL(string: urlStr) {
                do {
                    let (data, _) = try await URLSession.shared.data(from: url)
                    if let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] {
                        // 找出所有帧编号
                        let keys = json.keys.compactMap { Int($0) }.sorted()
                        frameCount = keys.last.map { $0 + 1 } ?? 0
                    }
                } catch { }
            }
        }

        if frameCount <= 0 { return [] }

        // 并行加载所有帧
        var frames: [(NSImage, Int)] = []
        frames.reserveCapacity(frameCount)

        for i in 0..<frameCount {
            if let result = await compositeCharacterFrame(appearance: appearance, action: action, frame: i) {
                let delay = await fetchFrameDelay(category: "Body", itemCode: bodyCode, action: action, frame: i)
                frames.append((result, delay))
            }
        }

        return frames
    }

    // MARK: - Helpers

    private func resolveBase() async -> String? {
        return await APIClient().resolveBase()
    }

    /// 获取帧信息（origin、delay、move 等）。
    func fetchFrameInfo(category: String, itemCode: String, action: String, frame: Int) async -> ActionFrame? {
        let paddedCode = String(format: "%08d", Int(itemCode) ?? 0)
        let basePath = "Character/\(category)/\(paddedCode).img/\(action)/\(frame)"

        guard let base = await resolveBase() else { return nil }
        let urlStr = "\(base)/node/json/\(basePath)?simple=true"
        guard let url = URL(string: urlStr) else { return nil }
        do {
            let (data, _) = try await URLSession.shared.data(from: url)
            guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }

            let delay = json["delay"] as? Int ?? 100

            var moveX = 0, moveY = 0
            if let moveNode = json["move"] as? [String: Any] {
                moveX = moveNode["x"] as? Int ?? 0
                moveY = moveNode["y"] as? Int ?? 0
            } else if let moveVal = json["move"] as? String {
                let parts = moveVal.components(separatedBy: ",").map { $0.trimmingCharacters(in: .whitespaces) }
                moveX = Int(parts[safe: 0] ?? "0") ?? 0
                moveY = Int(parts[safe: 1] ?? "0") ?? 0
            }

            return ActionFrame(
                action: action,
                frame: frame,
                delay: delay,
                move: (moveX, moveY),
                flip: false
            )
        } catch {
            logDebug("fetchFrameInfo 失败: \(basePath) - \(error)")
            return nil
        }
    }
}

// MARK: - RideCompositor (Mount/Pet)

/// 骑宠合成器。
/// 匹配 MapleSalon2 mount.rs 模式。
///
/// 骑宠 WZ 结构：
/// TamingMob/{tamingMobId}.img/
///   ├── info/          → 骑宠信息（bodyRelMove, sitAction 等）
///   └── {action}/      → 动画动作（Stand1, Move, Sit 等）
///       ├── 0/         → 帧 0
///       ├── 1/         → 帧 1
///       └── ...
///
/// 合成方式：角色叠在骑宠之上，骑宠作为背景层。
class RideCompositor {
    private let wzLoader: WzImageLoader
    private let wzParser: WzXmlParser
    private let avatarCompositor: AvatarCompositor

    init(loader: WzImageLoader = WzImageLoader(), parser: WzXmlParser = WzXmlParser()) {
        self.wzLoader = loader
        self.wzParser = parser
        self.avatarCompositor = AvatarCompositor(loader: loader, parser: parser)
    }

    // MARK: - 骑宠名称（匹配 MapleSalon2 resolve_mount_string）

    /// 获取所有骑宠的名称映射。
    /// 数据来源：
    /// 1. String/Eqp.img/Eqp/Taming/{id}/name
    /// 2. Skill/RidingSkillInfo.img → vehicleID → String/Skill.img/{skillId}/name
    /// - Returns: [(mountId, name)]
    func fetchMountStrings() async -> [(String, String)] {
        // 尝试从后端 API /string/mount 获取
        if let base = await resolveBase() {
            let urlStr = "\(base)/string/mount"
            if let url = URL(string: urlStr) {
                do {
                    let (data, _) = try await URLSession.shared.data(from: url)
                    if let json = try? JSONSerialization.jsonObject(with: data) as? [[Any]] {
                        return json.compactMap { item in
                            guard item.count >= 2,
                                  let id = item[0] as? String,
                                  let name = item[1] as? String else { return nil }
                            return (id, name)
                        }
                    }
                } catch {
                    logDebug("fetchMountStrings via API failed: \(error)")
                }
            }
        }

        // 回退到 WZ 解析
        var result: [(String, String)] = []

        // 从 TamingMob 目录获取所有骑宠 ID
        let mountNode = await wzParser.fetchNode(path: WzPaths.mount)
        // 从 String/Eqp.img/Eqp/Taming 获取名称
        let stringNode = await wzParser.fetchNode(path: "String/Eqp.img/Eqp/Taming")

        guard let mountNode = mountNode else { return [] }

        for (key, _) in mountNode.children {
            guard key.hasSuffix(".img") else { continue }
            let mountId = key
                .trimmingCharacters(in: CharacterSet(charactersIn: "0"))
                .replacingOccurrences(of: ".img", with: "")

            var name = "null"
            if let stringNode = stringNode,
               let idNode = stringNode.children[mountId] {
                if let nameNode = idNode.children["name"] {
                    name = nameNode.stringValue
                }
            }

            result.append((mountId, name))
        }

        return result.sorted { $0.0 < $1.0 }
    }

    // MARK: - 骑宠动作

    /// 获取骑宠的可用动作列表。
    /// 过滤掉 "info" 节点，返回排序后的动作名数组。
    /// 常见动作：Stand1, Stand2, Move, Sit, Fly, AfterAttack 等。
    func fetchMountActions(mountId: String) async -> [String] {
        guard let node = await wzParser.fetchNode(path: "TamingMob/\(mountId).img") else {
            // 尝试从后端 API 获取
            if let base = await resolveBase() {
                let urlStr = "\(base)/node/json/Character/TamingMob/\(mountId).img?simple=true"
                if let url = URL(string: urlStr) {
                    do {
                        let (data, _) = try await URLSession.shared.data(from: url)
                        if let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] {
                            return json.keys.filter { $0 != "info" }.sorted()
                        }
                    } catch { }
                }
            }
            return []
        }
        return node.children.keys.filter { $0 != "info" }.sorted()
    }

    /// 检查某个道具是否为骑宠（通过检测 info/tamingMob 属性）。
    func fetchTamingMob(itemCode: String) async -> String? {
        let path = "Item/Install/\(itemCode).img/info/tamingMob"
        guard let val = await wzLoader.fetchProperty(wzPath: path) else { return nil }
        return "\(val)"
    }

    // MARK: - 骑宠单帧加载

    /// 加载骑宠的单帧图片。
    /// - Parameters:
    ///   - mountId: 骑宠 ID（如 "100003"）
    ///   - action: 动作名（如 "Stand1"）
    ///   - frame: 帧索引
    /// - Returns: (NSImage, originX, originY) 或 nil
    func loadMountFrame(mountId: String, action: String, frame: Int) async -> (NSImage, Int, Int)? {
        let wzPath = "TamingMob/\(mountId).img/\(action)/\(frame)"

        // 下载帧图片
        guard let data = await wzLoader.fetchImage(wzPath: wzPath) else {
            logDebug("loadMountFrame 失败: \(wzPath)")
            return nil
        }
        guard let image = NSImage(data: data) else { return nil }

        // 获取 origin 坐标
        let originPath = "TamingMob/\(mountId).img/\(action)/\(frame)/origin"
        let (ox, oy) = await fetchOrigin(path: originPath)

        return (image, ox, oy)
    }

    /// 获取帧延迟。
    func fetchMountFrameDelay(mountId: String, action: String, frame: Int) async -> Int {
        let path = "TamingMob/\(mountId).img/\(action)/\(frame)/delay"
        guard let base = await resolveBase() else { return 100 }
        let urlStr = "\(base)/node/json/\(path)?simple=true"
        guard let url = URL(string: urlStr) else { return 100 }
        do {
            let (data, _) = try await URLSession.shared.data(from: url)
            if let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] {
                return json["value"] as? Int ?? 100
            }
        } catch { }
        return 100
    }

    // MARK: - 角色 + 骑宠合成

    /// 合成角色骑乘骑宠的画面。
    ///
    /// 合成方式：
    /// 1. 先渲染骑宠（底层）
    /// 2. 再渲染角色叠在骑宠上（上层）
    /// 3. 角色位置根据骑宠的 bodyRelMove 偏移量调整（后续支持）
    ///
    /// - Parameters:
    ///   - appearance: 角色外观配置
    ///   - mountId: 骑宠 ID
    ///   - mountAction: 骑宠动作
    ///   - mountFrame: 骑宠帧索引
    ///   - characterAction: 角色动作
    ///   - characterFrame: 角色帧索引
    /// - Returns: 合成后的图像（nil 表示加载失败）
    func compositeWithMount(
        appearance: CharacterAppearance,
        mountId: String,
        mountAction: String,
        mountFrame: Int,
        characterAction: String,
        characterFrame: Int
    ) async -> NSImage? {
        // 并行加载骑宠和角色帧
        async let mountResult = loadMountFrame(mountId: mountId, action: mountAction, frame: mountFrame)
        async let characterResult = avatarCompositor.compositeCharacterFrameWithOrigin(
            appearance: appearance, action: characterAction, frame: characterFrame
        )

        guard let (mountImage, mountOX, mountOY) = await mountResult else {
            logDebug("compositeWithMount: 骑宠帧加载失败")
            // 骑宠加载失败，回退到仅显示角色
            return await characterResult?.image
        }

        guard let character = await characterResult else {
            logDebug("compositeWithMount: 角色帧加载失败，仅显示骑宠")
            return mountImage
        }

        // 合成：骑宠在底层，角色在顶层
        var layers: [CompositingLayer] = []

        // 骑宠（z=0）
        layers.append(CompositingLayer(
            image: mountImage,
            originX: mountOX,
            originY: mountOY,
            z: 0
        ))

        // 角色（z=1，在骑宠之上）
        // 角色相对于骑宠的偏移由骑宠 info/bodyRelMove 决定
        // 暂不使用偏移，后续可从 fetchMountInfo 获取
        layers.append(CompositingLayer(
            image: character.image,
            originX: character.originX,
            originY: character.originY,
            z: 1
        ))

        return WzCompositor.composite(layers: layers)
    }

    /// 获取骑宠信息（bodyRelMove, sitAction 等）。
    /// bodyRelMove: 角色在骑宠上的相对位移 (x, y)
    /// sitAction: 骑乘时的角色动作
    /// hideBody: 是否隐藏角色身体
    func fetchMountInfo(mountId: String) async -> (bodyRelMoveX: Int, bodyRelMoveY: Int, sitAction: String?, hideBody: Bool) {
        var relX = 0, relY = 0
        var sitAction: String? = nil
        var hideBody = false

        // 尝试从后端 API 获取 info 节点
        if let base = await resolveBase() {
            let urlStr = "\(base)/node/json/TamingMob/\(mountId).img/info?simple=true"
            if let url = URL(string: urlStr) {
                do {
                    let (data, _) = try await URLSession.shared.data(from: url)
                    if let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] {
                        if let moveNode = json["bodyRelMove"] as? [String: Any] {
                            relX = moveNode["x"] as? Int ?? 0
                            relY = moveNode["y"] as? Int ?? 0
                        } else if let moveVal = json["bodyRelMove"] as? String {
                            let parts = moveVal.components(separatedBy: ",").map { $0.trimmingCharacters(in: .whitespaces) }
                            relX = Int(parts[safe: 0] ?? "0") ?? 0
                            relY = Int(parts[safe: 1] ?? "0") ?? 0
                        }
                        if let sit = json["sitAction"] as? String { sitAction = sit }
                        if let hide = json["hideBody"] as? Int { hideBody = hide != 0 }
                        return (relX, relY, sitAction, hideBody)
                    }
                } catch { }
            }
        }

        // 回退到 XML 解析
        let infoPath = "TamingMob/\(mountId).img/info"
        guard let infoNode = await wzParser.fetchNode(path: infoPath) else {
            return (relX, relY, sitAction, hideBody)
        }

        if let bodyRelMove = infoNode.children["bodyRelMove"], bodyRelMove.type == .vector {
            relX = bodyRelMove.x
            relY = bodyRelMove.y
        }
        if let sitNode = infoNode.children["sitAction"] {
            sitAction = sitNode.stringValue
        }
        if let hideNode = infoNode.children["hideBody"] {
            hideBody = hideNode.intValue != 0
        }

        return (relX, relY, sitAction, hideBody)
    }

    // MARK: - Helpers

    private func resolveBase() async -> String? {
        return await APIClient().resolveBase()
    }

    private func fetchOrigin(path: String) async -> (Int, Int) {
        guard let base = await resolveBase() else { return (0, 0) }
        let urlStr = "\(base)/node/json/\(path)?simple=true"
        guard let url = URL(string: urlStr) else { return (0, 0) }
        do {
            let (data, _) = try await URLSession.shared.data(from: url)
            if let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] {
                let ox = json["x"] as? Int ?? 0
                let oy = json["y"] as? Int ?? 0
                return (ox, oy)
            }
        } catch { }
        return (0, 0)
    }
}

// MARK: - ChairCompositor

/// 椅子合成器。
/// 匹配 MapleSalon2 chair.rs 模式。
///
/// 椅子 WZ 结构：
/// Item/Install/{prefixId}.img/{chairId}/
///   ├── info/
///   │   ├── bodyRelMove    → 角色在椅子上的偏移 (x, y)
///   │   ├── sitAction      → 坐姿时角色动作名
///   │   ├── hideBody       → 是否隐藏角色身体
///   │   ├── invisibleWeapon → 是否隐藏武器
///   │   ├── invisibleCape  → 是否隐藏披风
///   │   └── tamingMob      → 关联的骑宠 ID（特殊椅子）
///   └── {action}/
///       └── {frame}/
///           ├── origin     → 帧原点
///           └── (PNG data) → 帧图片
///
/// 现金椅子：Item/Cash/0520.img/{chairId}（ID 以 05204xxx 开头）
class ChairCompositor {
    private let wzLoader: WzImageLoader
    private let wzParser: WzXmlParser
    private let avatarCompositor: AvatarCompositor

    init(loader: WzImageLoader = WzImageLoader(), parser: WzXmlParser = WzXmlParser()) {
        self.wzLoader = loader
        self.wzParser = parser
        self.avatarCompositor = AvatarCompositor(loader: loader, parser: parser)
    }

    // MARK: - 椅子名称列表（匹配 MapleSalon2 resolve_chair_string）

    /// 获取所有椅子的名称映射。
    /// 数据来源：
    /// 1. Item/Install/{0301xx|0302xx}.img/{chairId} + String/Ins.img/{id}/name
    /// 2. Item/Cash/0520.img/{05204xxx} + String/Cash.img/{id}/name
    /// - Returns: [(chairId, parentFolder, name)]
    func fetchChairStrings() async -> [(String, String, String)] {
        // 尝试从后端 API /string/chair 获取
        if let base = await resolveBase() {
            let urlStr = "\(base)/string/chair"
            if let url = URL(string: urlStr) {
                do {
                    let (data, _) = try await URLSession.shared.data(from: url)
                    if let json = try? JSONSerialization.jsonObject(with: data) as? [[Any]] {
                        return json.compactMap { item in
                            guard item.count >= 3,
                                  let id = item[0] as? String,
                                  let parent = item[1] as? String,
                                  let name = item[2] as? String else { return nil }
                            return (id, parent, name)
                        }
                    }
                } catch {
                    logDebug("fetchChairStrings via API failed: \(error)")
                }
            }
        }

        // 回退到 WZ 解析
        var result: [(String, String, String)] = []

        // 普通椅子：Item/Install/0301xx.img/ 和 0302xx.img/
        let installNode = await wzParser.fetchNode(path: WzPaths.chair)
        let stringNode = await wzParser.fetchNode(path: WzPaths.chairString)

        if let installNode = installNode {
            for (prefixId, prefixNode) in installNode.children {
                guard prefixId.hasPrefix("0301") || prefixId.hasPrefix("0302") else { continue }

                for (chairId, _) in prefixNode.children {
                    var name = "null"
                    let trimmedId = chairId.trimmingCharacters(in: CharacterSet(charactersIn: "0"))
                    if let stringNode = stringNode,
                       let idNode = stringNode.children[trimmedId],
                       let nameNode = idNode.children["name"] {
                        name = nameNode.stringValue
                    }
                    result.append((chairId, prefixId, name))
                }
            }
        }

        // 现金椅子：Item/Cash/0520.img/05204xxx
        let cashChairNode = await wzParser.fetchNode(path: WzPaths.cashChair)
        let cashStringNode = await wzParser.fetchNode(path: WzPaths.cashChairString)

        if let cashChairNode = cashChairNode {
            for (chairId, _) in cashChairNode.children {
                guard chairId.hasPrefix("05204") else { continue }

                var name = "null"
                let trimmedId = chairId.trimmingCharacters(in: CharacterSet(charactersIn: "0"))
                if let cashStringNode = cashStringNode,
                   let idNode = cashStringNode.children[trimmedId],
                   let nameNode = idNode.children["name"] {
                    name = nameNode.stringValue
                }
                result.append((chairId, "0520.img", name))
            }
        }

        return result.sorted { $0.0 < $1.0 }
    }

    // MARK: - 椅子信息

    /// 获取椅子 info 节点信息。
    /// - bodyRelMove: 角色坐在椅子上的偏移 (x, y)
    /// - sitAction: 坐姿时使用的角色动作名（如 "sit", "ladder"）
    /// - hideBody: 是否隐藏角色身体（如凿子椅）
    /// - invisibleWeapon: 是否隐藏武器
    /// - invisibleCape: 是否隐藏披风
    /// - tamingMobId: 关联的骑宠 ID（特殊椅子，如汽车椅）
    func fetchChairInfo(chairId: String) async -> (
        bodyRelMoveX: Int,
        bodyRelMoveY: Int,
        sitAction: String?,
        hideBody: Bool,
        invisibleWeapon: Bool,
        invisibleCape: Bool,
        tamingMobId: String?
    ) {
        var relX = 0, relY = 0
        var sitAction: String? = nil
        var hideBody = false
        var invWeapon = false
        var invCape = false
        var tamingMob: String? = nil

        // 尝试后端 API
        let path = resolveChairInfoPath(chairId: chairId)

        if let base = await resolveBase() {
            let urlStr = "\(base)/node/json/\(path)/info?simple=true"
            if let url = URL(string: urlStr) {
                do {
                    let (data, _) = try await URLSession.shared.data(from: url)
                    if let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] {
                        if let moveNode = json["bodyRelMove"] as? [String: Any] {
                            relX = moveNode["x"] as? Int ?? 0
                            relY = moveNode["y"] as? Int ?? 0
                        } else if let moveVal = json["bodyRelMove"] as? String {
                            let parts = moveVal.components(separatedBy: ",").map { $0.trimmingCharacters(in: .whitespaces) }
                            relX = Int(parts[safe: 0] ?? "0") ?? 0
                            relY = Int(parts[safe: 1] ?? "0") ?? 0
                        }
                        if let sit = json["sitAction"] as? String { sitAction = sit }
                        if let hide = json["hideBody"] as? Int { hideBody = hide != 0 }
                        if let invW = json["invisibleWeapon"] as? Int { invWeapon = invW != 0 }
                        if let invC = json["invisibleCape"] as? Int { invCape = invC != 0 }
                        if let taming = json["tamingMob"] as? String { tamingMob = taming }
                        return (relX, relY, sitAction, hideBody, invWeapon, invCape, tamingMob)
                    }
                } catch { }
            }
        }

        // 回退到 XML 解析
        guard let infoNode = await wzParser.fetchNode(path: "\(path)/info") else {
            return (relX, relY, sitAction, hideBody, invWeapon, invCape, tamingMob)
        }

        if let bodyRelMove = infoNode.children["bodyRelMove"], bodyRelMove.type == .vector {
            relX = bodyRelMove.x
            relY = bodyRelMove.y
        }
        if let sitNode = infoNode.children["sitAction"] {
            sitAction = sitNode.stringValue
        }
        if let hideNode = infoNode.children["hideBody"] {
            hideBody = hideNode.intValue != 0
        }
        if let invWNode = infoNode.children["invisibleWeapon"] {
            invWeapon = invWNode.intValue != 0
        }
        if let invCNode = infoNode.children["invisibleCape"] {
            invCape = invCNode.intValue != 0
        }
        if let tamingNode = infoNode.children["tamingMob"] {
            tamingMob = tamingNode.stringValue
        }

        return (relX, relY, sitAction, hideBody, invWeapon, invCape, tamingMob)
    }

    // MARK: - 椅子单帧加载

    /// 加载椅子的单帧图片。
    /// - Parameters:
    ///   - chairId: 椅子 ID（如 "30100000"）
    ///   - frame: 帧索引
    /// - Returns: (NSImage, originX, originY) 或 nil
    func loadChairFrame(chairId: String, frame: Int) async -> (NSImage, Int, Int)? {
        let path = resolveChairInfoPath(chairId: chairId)
        let wzPath = "\(path)/\(frame)"

        // 下载帧图片
        guard let data = await wzLoader.fetchImage(wzPath: wzPath) else {
            logDebug("loadChairFrame 失败: \(wzPath)")
            return nil
        }
        guard let image = NSImage(data: data) else { return nil }

        // 获取 origin
        let originPath = "\(wzPath)/origin"
        let (ox, oy) = await fetchOrigin(path: originPath)

        return (image, ox, oy)
    }

    /// 加载椅子指定动作/帧的图片（椅子可能有多个动作）。
    func loadChairActionFrame(chairId: String, action: String, frame: Int) async -> (NSImage, Int, Int)? {
        let path = resolveChairInfoPath(chairId: chairId)
        let wzPath = "\(path)/\(action)/\(frame)"

        guard let data = await wzLoader.fetchImage(wzPath: wzPath) else {
            logDebug("loadChairActionFrame 失败: \(wzPath)")
            return nil
        }
        guard let image = NSImage(data: data) else { return nil }

        let originPath = "\(wzPath)/origin"
        let (ox, oy) = await fetchOrigin(path: originPath)

        return (image, ox, oy)
    }

    // MARK: - 角色 + 椅子合成

    /// 合成角色坐在椅子上的画面。
    ///
    /// 合成方式：
    /// 1. 加载椅子帧（底层）
    /// 2. 加载角色 sit 动作帧（上层），根据 bodyRelMove 偏移定位
    /// 3. 处理 hideBody / invisibleWeapon / invisibleCape
    ///
    /// - Parameters:
    ///   - appearance: 角色外观配置（将根据椅子信息调整 sitAction）
    ///   - chairId: 椅子 ID
    ///   - chairAction: 椅子动作（如 "sit", "0" 等，多数椅子只有默认帧）
    ///   - chairFrame: 椅子帧索引
    ///   - characterAction: 角色动作（通常为 "sit" 或椅子指定的 sitAction）
    ///   - characterFrame: 角色帧索引
    /// - Returns: 合成后的图像（nil 表示加载失败）
    func compositeWithChair(
        appearance: CharacterAppearance,
        chairId: String,
        chairAction: String?,
        chairFrame: Int,
        characterAction: String,
        characterFrame: Int
    ) async -> NSImage? {
        // 获取椅子 info
        let chairInfo = await fetchChairInfo(chairId: chairId)
        let actualCharAction = chairInfo.sitAction ?? characterAction

        // 确定实际使用的角色动作和帧
        let effectiveAction = actualCharAction
        let effectiveFrame = characterFrame

        // 调整外观：如果椅子要求隐藏身体/武器/披风
        var adjustedAppearance = appearance
        if chairInfo.hideBody {
            // hideBody 时清空所有装备，只保留头发和脸
            adjustedAppearance = CharacterAppearance(
                gender: appearance.gender,
                skin: appearance.skin,
                hair: appearance.hair,
                face: appearance.face
            )
        } else {
            if chairInfo.invisibleWeapon {
                adjustedAppearance.weapon = nil
            }
            if chairInfo.invisibleCape {
                adjustedAppearance.cape = nil
            }
        }

        // 并行加载椅子帧和角色帧
        var chairImage: NSImage?
        var chairOX = 0, chairOY = 0

        if let action = chairAction {
            if let (img, ox, oy) = await loadChairActionFrame(chairId: chairId, action: action, frame: chairFrame) {
                chairImage = img
                chairOX = ox
                chairOY = oy
            }
        } else {
            if let (img, ox, oy) = await loadChairFrame(chairId: chairId, frame: chairFrame) {
                chairImage = img
                chairOX = ox
                chairOY = oy
            }
        }

        guard let chairImage = chairImage else {
            logDebug("compositeWithChair: 椅子帧加载失败")
            return await avatarCompositor.compositeCharacterFrame(
                appearance: adjustedAppearance, action: effectiveAction, frame: effectiveFrame
            )
        }

        let characterResult = await avatarCompositor.compositeCharacterFrameWithOrigin(
            appearance: adjustedAppearance, action: effectiveAction, frame: effectiveFrame
        )

        // 合成
        var layers: [CompositingLayer] = []

        // 椅子（z=0）
        layers.append(CompositingLayer(
            image: chairImage,
            originX: chairOX,
            originY: chairOY,
            z: 0
        ))

        // 角色（z=1）
        if let character = characterResult {
            // 角色位置 = 椅子 origin + bodyRelMove 偏移
            let charOX = character.originX + chairInfo.bodyRelMoveX
            let charOY = character.originY + chairInfo.bodyRelMoveY

            layers.append(CompositingLayer(
                image: character.image,
                originX: charOX,
                originY: charOY,
                z: 1
            ))
        }

        return WzCompositor.composite(layers: layers)
    }

    /// 获取椅子的可用动作列表。
    /// 多数普通椅子只有默认帧（无动作层次），
    /// 但部分椅子（如音乐椅子、动效椅子）有多个动作。
    func fetchChairActions(chairId: String) async -> [String] {
        let path = resolveChairInfoPath(chairId: chairId)
        guard let node = await wzParser.fetchNode(path: path) else { return [] }
        return node.children.keys
            .filter { $0 != "info" }
            .sorted()
    }

    // MARK: - Helpers

    /// 解析椅子的完整 WZ 路径。
    /// 判断椅子是普通安装道具椅（0301xx/0302xx）还是现金椅（05204xxx）。
    private func resolveChairInfoPath(chairId: String) -> String {
        let trimmed = chairId.trimmingCharacters(in: CharacterSet(charactersIn: "0"))
        if trimmed.hasPrefix("05204") {
            // 现金椅子
            return "Item/Cash/0520.img/\(chairId)"
        }
        // 普通椅子：父文件夹是 ID 的前 2 位前缀
        let prefix = String(chairId.prefix(4))
        if prefix.hasPrefix("0301") || prefix.hasPrefix("0302") {
            // 尝试构造标准路径
            let folderId = String(chairId.prefix(6)) + ".img"
            return "Item/Install/\(folderId)/\(chairId)"
        }
        // 尝试标准 6 位前缀 .img 路径
        let folderId = String(chairId.prefix(6)) + ".img"
        return "Item/Install/\(folderId)/\(chairId)"
    }

    private func resolveBase() async -> String? {
        return await APIClient().resolveBase()
    }

    private func fetchOrigin(path: String) async -> (Int, Int) {
        guard let base = await resolveBase() else { return (0, 0) }
        let urlStr = "\(base)/node/json/\(path)?simple=true"
        guard let url = URL(string: urlStr) else { return (0, 0) }
        do {
            let (data, _) = try await URLSession.shared.data(from: url)
            if let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] {
                let ox = json["x"] as? Int ?? 0
                let oy = json["y"] as? Int ?? 0
                return (ox, oy)
            }
        } catch { }
        return (0, 0)
    }
}

// MARK: - BackgroundCompositor

/// 地图背景合成器。
/// 从 Map.wz/Map/Map{type}/{mapId}.img/back 加载背景图层。
///
/// 背景属性（来自 MapleSalon2 / 冒险岛客户端）：
/// - bS: 背景集名称（如 "Back"、"Obj"）
/// - no: 背景编号
/// - front: 是否为前景层（true=前景，false=背景）
/// - type: 瓦片模式（0~7）
///   0: 静态（不滚动）
///   1: 水平滚动（根据 cx/cy 速度）
///   2: 垂直滚动
///   3: 水平+垂直滚动
///   4: 水平跟随（rx/ry 视差）
///   5: 垂直跟随
///   6: 水平+垂直跟随
///   7: 水平复制平铺
/// - cx, cy: 滚动速度
/// - rx, ry: 视差比例（相对于相机移动）
/// - x, y: 屏幕位置
/// - a: 透明度（0~255）
class BackgroundCompositor {
    private let wzLoader: WzImageLoader
    private let wzParser: WzXmlParser

    init(loader: WzImageLoader = WzImageLoader(), parser: WzXmlParser = WzXmlParser()) {
        self.wzLoader = loader
        self.wzParser = parser
    }

    // MARK: - 背景图片数据结构

    /// 背景层描述。
    struct BackgroundLayer {
        /// 背景编号
        let no: Int
        /// 是否为前景层
        let front: Bool
        /// 瓦片模式（0~7）
        let type: Int
        /// 背景集名称
        let bS: String
        /// 视差/滚动速度
        let cx: Int
        let cy: Int
        /// 视差比例
        let rx: Int
        let ry: Int
        /// 屏幕位置
        let x: Int
        let y: Int
        /// 透明度
        let alpha: Int
        /// 是否水平翻转
        let flip: Bool
        /// 背景图片路径
        let imagePath: String?
    }

    // MARK: - 背景层加载

    /// 获取地图的所有背景层。
    ///
    /// 冒险岛地图背景结构：
    /// Map/Map/Map{type}/{mapId}.img/back/
    ///   ├── 0/               → 背景层 0
    ///   │   └── info/
    ///   │       ├── front   → 前景/背景标志
    ///   │       ├── bS      → 背景集名
    ///   │       ├── no      → 编号
    ///   │       ├── type    → 瓦片模式
    ///   │       ├── cx/cy   → 速度
    ///   │       ├── rx/ry   → 视差
    ///   │       ├── x/y     → 位置
    ///   │       └── a       → 透明度
    ///   ├── 1/
    ///   └── ...
    ///
    /// 背景图片路径：Map/Back/{bS}.img/back/{no}
    ///
    /// - Parameter mapId: 地图 ID（如 "100000000"）
    /// - Returns: 排序后的背景层数组（按 no 升序）
    func fetchBackgrounds(mapId: String) async -> [BackgroundLayer] {
        let mapType = mapId.count >= 1 ? String(mapId[mapId.startIndex]) : "0"
        let path = "Map/Map/Map\(mapType)/\(mapId).img/back"

        guard let node = await wzParser.fetchNode(path: path) else {
            // 尝试后端 API
            if let base = await resolveBase() {
                let urlStr = "\(base)/node/json/\(path)?simple=true"
                if let url = URL(string: urlStr) {
                    do {
                        let (data, _) = try await URLSession.shared.data(from: url)
                        if let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] {
                            return parseBackgroundLayersFromJSON(json)
                        }
                    } catch { }
                }
            }
            return []
        }

        var layers: [BackgroundLayer] = []

        // 解析 XML 节点
        for (_, child) in node.children {
            guard child.type == .dir else { continue }
            let layerNo = Int(child.name) ?? 0

            guard let infoNode = child.children["info"] else {
                // 也可能 info 在直接子节点中
                let bS = child.children["bS"]?.stringValue ?? ""
                let no = child.children["no"]?.intValue ?? 0
                let front = child.children["front"]?.intValue ?? 0
                let type = child.children["type"]?.intValue ?? 0
                let cx = child.children["cx"]?.intValue ?? 0
                let cy = child.children["cy"]?.intValue ?? 0
                let rx = child.children["rx"]?.intValue ?? 100
                let ry = child.children["ry"]?.intValue ?? 100
                let x = child.children["x"]?.intValue ?? 0
                let y = child.children["y"]?.intValue ?? 0
                let alpha = child.children["a"]?.intValue ?? 255
                let flip = (child.children["flip"]?.intValue ?? 0) != 0

                let imagePath = bS.isEmpty ? nil : "Map/Back/\(bS).img/back/\(no)"
                layers.append(BackgroundLayer(
                    no: layerNo, front: front != 0, type: type,
                    bS: bS, cx: cx, cy: cy, rx: rx, ry: ry,
                    x: x, y: y, alpha: alpha, flip: flip,
                    imagePath: imagePath
                ))
                continue
            }

            let front = infoNode.children["front"]?.intValue ?? 0
            let type = infoNode.children["type"]?.intValue ?? 0
            let cx = infoNode.children["cx"]?.intValue ?? 0
            let cy = infoNode.children["cy"]?.intValue ?? 0
            let rx = infoNode.children["rx"]?.intValue ?? 100
            let ry = infoNode.children["ry"]?.intValue ?? 100
            let x = infoNode.children["x"]?.intValue ?? 0
            let y = infoNode.children["y"]?.intValue ?? 0
            let alpha = infoNode.children["a"]?.intValue ?? 255
            let flip = (infoNode.children["flip"]?.intValue ?? 0) != 0

            // bS 可能在 info 下或直接子节点
            var bS = infoNode.children["bS"]?.stringValue ?? ""
            if bS.isEmpty { bS = child.children["bS"]?.stringValue ?? "" }

            let no = infoNode.children["no"]?.intValue ?? layerNo

            let imagePath = bS.isEmpty ? nil : "Map/Back/\(bS).img/back/\(no)"

            layers.append(BackgroundLayer(
                no: layerNo, front: front != 0, type: type,
                bS: bS, cx: cx, cy: cy, rx: rx, ry: ry,
                x: x, y: y, alpha: alpha, flip: flip,
                imagePath: imagePath
            ))
        }

        return layers.sorted { $0.no < $1.no }
    }

    /// 从 JSON 解析背景层。
    private func parseBackgroundLayersFromJSON(_ json: [String: Any]) -> [BackgroundLayer] {
        var layers: [BackgroundLayer] = []

        for (key, value) in json {
            guard let layerDict = value as? [String: Any] else { continue }
            guard let layerNo = Int(key) else { continue }

            // 检查 info 子节点
            let info: [String: Any]
            if let infoDict = layerDict["info"] as? [String: Any] {
                info = infoDict
            } else {
                // 也可能 info 数据直接放在层中
                info = layerDict
            }

            let front = info["front"] as? Int ?? 0
            let type = info["type"] as? Int ?? 0
            let cx = info["cx"] as? Int ?? 0
            let cy = info["cy"] as? Int ?? 0
            let rx = info["rx"] as? Int ?? 100
            let ry = info["ry"] as? Int ?? 100
            let x = info["x"] as? Int ?? 0
            let y = info["y"] as? Int ?? 0
            let alpha = info["a"] as? Int ?? 255

            // 处理 move 类型（x/y 可能来自 vector）
            let actualX: Int
            if let moveDict = info["move"] as? [String: Any] {
                actualX = moveDict["x"] as? Int ?? x
            } else {
                actualX = x
            }

            let actualY: Int
            if let moveDict = info["move"] as? [String: Any] {
                actualY = moveDict["y"] as? Int ?? y
            } else {
                actualY = y
            }

            let flip = (info["x"] as? Int == -1) || (info["flip"] as? Int ?? 0) != 0
            var bS = info["bS"] as? String ?? ""

            // bS 可能在 info 外部
            if bS.isEmpty, let bsVal = layerDict["bS"] as? String { bS = bsVal }

            let no = info["no"] as? Int ?? layerNo
            let imagePath = bS.isEmpty ? nil : "Map/Back/\(bS).img/back/\(no)"

            layers.append(BackgroundLayer(
                no: layerNo, front: front != 0, type: type,
                bS: bS, cx: cx, cy: cy, rx: rx, ry: ry,
                x: actualX, y: actualY, alpha: alpha, flip: flip,
                imagePath: imagePath
            ))
        }

        return layers.sorted { $0.no < $1.no }
    }

    // MARK: - 背景图片加载

    /// 加载单个背景图片。
    /// - Parameter bS: 背景集名称（如 "Back", "Obj"）
    /// - Parameter no: 背景编号
    /// - Returns: NSImage（可能为 nil）
    func loadBackgroundImage(bS: String, no: Int) async -> NSImage? {
        let path = "Map/Back/\(bS).img/back/\(no)"
        guard let data = await wzLoader.fetchImage(wzPath: path) else { return nil }
        return NSImage(data: data)
    }

    /// 加载背景层的地图定义图片。
    /// 某些背景图片直接在地图 .img 文件的子节点中。
    func loadMapBackgroundImage(mapId: String, layerNo: Int) async -> NSImage? {
        let mapType = mapId.count >= 1 ? String(mapId[mapId.startIndex]) : "0"
        let path = "Map/Map/Map\(mapType)/\(mapId).img/back/\(layerNo)"
        guard let data = await wzLoader.fetchImage(wzPath: path) else {
            logDebug("loadMapBackgroundImage 失败: \(path)")
            return nil
        }
        return NSImage(data: data)
    }

    // MARK: - 背景渲染

    /// 将所有背景层合成到画布上。
    /// - 区分前景/背景：前景在角色之上，背景之下（已排序）
    /// - 处理瓦片模式（type 0~7）
    /// - 处理视差（rx, ry）
    ///
    /// - Parameters:
    ///   - layers: 背景层数组
    ///   - canvasWidth: 画布宽度
    ///   - canvasHeight: 画布高度
    ///   - cameraX: 相机 X 偏移（用于视差）
    ///   - cameraY: 相机 Y 偏移
    /// - Returns: (backImage, frontImage) 背景和前景层
    func compositeBackgrounds(
        layers: [BackgroundLayer],
        canvasWidth: Int,
        canvasHeight: Int,
        cameraX: Int = 0,
        cameraY: Int = 0
    ) async -> (backImage: NSImage?, frontImage: NSImage?) {
        var backLayers: [CompositingLayer] = []
        var frontLayers: [CompositingLayer] = []

        for (index, layer) in layers.enumerated() {
            guard layer.imagePath != nil else { continue }

            // 计算视差偏移（类型 4/5/6）
            var offsetX = layer.x
            var offsetY = layer.y

            switch layer.type {
            case 4: // 水平跟随
                let parallaxFactor = CGFloat(layer.rx) / 100.0
                offsetX = layer.x - Int(CGFloat(cameraX) * parallaxFactor)
            case 5: // 垂直跟随
                let parallaxFactor = CGFloat(layer.ry) / 100.0
                offsetY = layer.y - Int(CGFloat(cameraY) * parallaxFactor)
            case 6: // 水平+垂直跟随
                let parX = CGFloat(layer.rx) / 100.0
                let parY = CGFloat(layer.ry) / 100.0
                offsetX = layer.x - Int(CGFloat(cameraX) * parX)
                offsetY = layer.y - Int(CGFloat(cameraY) * parY)
            default:
                break
            }

            // 加载背景图片
            guard let image = await loadBackgroundImage(bS: layer.bS, no: layer.no) else {
                continue
            }

            let compositeLayer = CompositingLayer(
                image: image,
                originX: offsetX,
                originY: offsetY,
                z: index,
                flipX: layer.flip
            )

            if layer.front {
                frontLayers.append(compositeLayer)
            } else {
                backLayers.append(compositeLayer)
            }
        }

        // 合成背景和前景
        let backImage = backLayers.isEmpty ? nil : WzCompositor.composite(layers: backLayers)
        let frontImage = frontLayers.isEmpty ? nil : WzCompositor.composite(layers: frontLayers)

        return (backImage, frontImage)
    }

    /// 获取瓦片模式的文字描述。
    func tileModeDescription(_ type: Int) -> String {
        switch type {
        case 0: return "静态"
        case 1: return "水平滚动"
        case 2: return "垂直滚动"
        case 3: return "水平+垂直滚动"
        case 4: return "水平视差"
        case 5: return "垂直视差"
        case 6: return "视差"
        case 7: return "水平复制平铺"
        default: return "未知(\(type))"
        }
    }

    // MARK: - Helpers

    private func resolveBase() async -> String? {
        return await APIClient().resolveBase()
    }
}

// MARK: - WzCompositor 扩展（便捷方法）

extension WzCompositor {

    /// 将两个图像按 origin 合成。
    static func compositeTwoImages(
        bottom: (image: NSImage, originX: Int, originY: Int),
        top: (image: NSImage, originX: Int, originY: Int)
    ) -> NSImage {
        let layers = [
            CompositingLayer(image: bottom.image, originX: bottom.originX, originY: bottom.originY, z: 0),
            CompositingLayer(image: top.image, originX: top.originX, originY: top.originY, z: 1)
        ]
        return composite(layers: layers)
    }

    /// 将多个图像按 z-order 合成（便捷方法）。
    static func compositeImages(_ items: [(image: NSImage, originX: Int, originY: Int, z: Int)]) -> NSImage {
        let layers = items.map { CompositingLayer(image: $0.image, originX: $0.originX, originY: $0.originY, z: $0.z) }
        return composite(layers: layers)
    }
}

// MARK: - 日志辅助

#if DEBUG
private func compositorLog(_ msg: String) {
    print("[WzCompositor] \(msg)")
}
#else
private func compositorLog(_ msg: String) {}
#endif