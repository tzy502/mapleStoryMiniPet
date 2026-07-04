import Foundation

// MARK: - WZ String Query Response

/// 通用字符串查询响应条目（对应 MapleSalon2 StringDictItem 结构）。
/// 后端 POST /api/wz/data/query/string 的响应格式。
struct WzStringItem: Codable {
    let code: String
    var name: String
    var category: String?
    var parentFolder: String?
    var isCash: Bool?
    var hasColorvar: Bool?
    var hasEffect: Bool?
}

// MARK: - WZ Path Helpers

/// WZ 路径拼接辅助，避免手写字符串硬编码。
enum WzPathBuilder {
    /// 8 位补零 ID
    static func padded(_ id: String) -> String {
        String(format: "%08d", Int(id) ?? 0)
    }

    /// 移除前导零和 .img 后缀，得到纯净 ID
    static func cleanId(_ raw: String) -> String {
        raw.trimmingCharacters(in: CharacterSet(charactersIn: "0"))
            .trimmingCharacters(in: CharacterSet(charactersIn: "."))
    }

    /// Character 分类下道具的 WZ 路径
    /// e.g. "Character/Cap/01001011.img"
    static func characterItem(category: String, id: String) -> String {
        "\(WzPaths.character)/\(category)/\(padded(id)).img"
    }

    /// 安装道具路径
    /// e.g. "Item/Install/03010001.img"
    static func installItem(id: String) -> String {
        "\(WzPaths.chair)/\(padded(id)).img"
    }
}

// MARK: - APIClient+WZ

extension APIClient {

    // MARK: - Equipment String (对应 MapleSalon2 resolve_equip_string)

    /// 获取装备字符串列表，按分类分组。
    /// 匹配 MapleSalon2 的 resolve_equip_string() 逻辑：
    /// - 遍历 EQUIP_CATEGORY_NEEDS (Cap/Cape/Coat/Face/Glove/Hair/Longcoat/Pants/Ring/Shield/Shoes/Weapon/Accessory/Skin)
    /// - 对每个分类查询 String/Eqp.img/{category} 下的 name 字段
    /// - 额外查询不在字符串中的装备节点（只存在于 Character 目录但不在 String 中）
    /// - 额外查询 nicktag / skin / cash effect
    /// - extraInfo=true 时还会判断 isCash / hasColorvar / hasEffect 并重分类
    func fetchEquipStrings(extraInfo: Bool = true) async -> [WzStringItem] {
        guard let base = await resolveBase() else { return [] }
        guard let url = URL(string: "\(base)/api/wz/data/query/string") else { return [] }

        var results: [WzStringItem] = []

        // 1. 查询各分类装备字符串
        for category in EquipCategory.equipCategories {
            let items = await queryWzString(base: base, type: "equip", category: category, name: "")
            results.append(contentsOf: items)
        }

        // 2. 额外查询 Skin 分类
        let skinItems = await queryWzString(base: base, type: "equip", category: "Skin", name: "")
        results.append(contentsOf: skinItems)

        // 3. 查询昵称标签 (NickTag)
        let nickTagItems = await queryWzString(base: base, type: "equip", category: "NickTag", name: "")
        results.append(contentsOf: nickTagItems)

        // 4. 查询现金特效 (Cash Effect - 0501.img)
        let cashEffectItems = await queryWzString(base: base, type: "cashEffect", category: "", name: "")
        results.append(contentsOf: cashEffectItems)

        // 去重（按 code 去重，保留后出现的）
        var seen = Set<String>()
        var deduped: [WzStringItem] = []
        for item in results.reversed() {
            if !seen.contains(item.code) {
                seen.insert(item.code)
                deduped.append(item)
            }
        }

        // 按 code 排序
        deduped.sort { a, b in
            let aInt = Int(a.code) ?? 0
            let bInt = Int(b.code) ?? 0
            return aInt < bInt
        }

        return deduped
    }

    /// 查询单个装备分类的字符串
    /// 对应 MapleSalon2 resolve_equip_string_by_category()
    private func queryWzString(base: String, type: String, category: String, name: String) async -> [WzStringItem] {
        guard let url = URL(string: "\(base)/api/wz/data/query/string") else { return [] }
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")

        let body: [String: String] = [
            "type": type,
            "category": category,
            "name": name,
        ]
        req.httpBody = try? JSONEncoder().encode(body)

        do {
            let (data, _) = try await session.data(for: req)
            guard let list = try? JSONSerialization.jsonObject(with: data) as? [[String: Any]] else { return [] }
            return list.compactMap { dict in
                guard let code = dict["code"] as? String ?? dict["id"] as? String else { return nil }
                let name = dict["name"] as? String ?? ""
                let categoryStr = dict["category"] as? String ?? category
                let parentFolder = dict["parentFolder"] as? String
                let isCash = dict["isCash"] as? Bool
                let hasColorvar = dict["hasColorvar"] as? Bool
                let hasEffect = dict["hasEffect"] as? Bool
                return WzStringItem(
                    code: code,
                    name: name,
                    category: categoryStr,
                    parentFolder: parentFolder,
                    isCash: isCash,
                    hasColorvar: hasColorvar,
                    hasEffect: hasEffect
                )
            }
        } catch {
            logDebug("queryWzString(\(type), \(category)) failed: \(error)")
            return []
        }
    }

    // MARK: - Chair String (对应 MapleSalon2 resolve_chair_string)

    /// 获取椅子字符串列表。
    /// 匹配 MapleSalon2 resolve_chair_string():
    /// - 扫描 Item/Install 下 0301xx / 0302xx 前缀的文件夹
    /// - 从 String/Ins.img 或 String/Item.img/Ins 获取名称
    /// - 额外扫描 Item/Cash/0520.img 下 05204x 前缀的现金椅子
    /// - 从 String/Cash.img 获取现金椅子名称
    /// 返回 (id, parentFolder, name) 三元组。
    func fetchChairStrings() async -> [(id: String, parentFolder: String, name: String)] {
        guard let base = await resolveBase() else { return [] }
        guard let url = URL(string: "\(base)/api/wz/data/query/string") else { return [] }
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let body: [String: String] = ["type": "chair", "name": ""]
        req.httpBody = try? JSONEncoder().encode(body)

        do {
            let (data, _) = try await session.data(for: req)
            guard let list = try? JSONSerialization.jsonObject(with: data) as? [[String: Any]] else { return [] }
            return list.compactMap { dict in
                guard let id = dict["code"] as? String ?? dict["id"] as? String else { return nil }
                let name = dict["name"] as? String ?? ""
                let parentFolder = dict["parentFolder"] as? String ?? ""
                return (id: id, parentFolder: parentFolder, name: name)
            }.sorted { a, b in (Int(a.id) ?? 0) < (Int(b.id) ?? 0) }
        } catch {
            logDebug("fetchChairStrings failed: \(error)")
            return []
        }
    }

    // MARK: - Mount String (对应 MapleSalon2 resolve_mount_string)

    /// 获取骑宠字符串列表。
    /// 匹配 MapleSalon2 resolve_mount_string():
    /// - 扫描 Character/TamingMob 下所有 .img 节点（排除 0191xx / 0198xx 前缀）
    /// - 从 String/Eqp.img/Eqp/Taming 获取名称
    /// - 从 Skill/RidingSkillInfo.img 获取 vehicleID→skillID 映射，回退到 Skill 名称
    /// 返回 (id, name) 对。
    func fetchMountStrings() async -> [(id: String, name: String)] {
        guard let base = await resolveBase() else { return [] }
        guard let url = URL(string: "\(base)/api/wz/data/query/string") else { return [] }
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let body: [String: String] = ["type": "mount", "name": ""]
        req.httpBody = try? JSONEncoder().encode(body)

        do {
            let (data, _) = try await session.data(for: req)
            guard let list = try? JSONSerialization.jsonObject(with: data) as? [[String: Any]] else { return [] }
            return list.compactMap { dict in
                guard let id = dict["code"] as? String ?? dict["id"] as? String else { return nil }
                let name = dict["name"] as? String ?? ""
                return (id: id, name: name)
            }.sorted { a, b in (Int(a.id) ?? 0) < (Int(b.id) ?? 0) }
        } catch {
            logDebug("fetchMountStrings failed: \(error)")
            return []
        }
    }

    // MARK: - Skill String (对应 MapleSalon2 resolve_skill_string)

    /// 获取技能字符串列表。
    /// 匹配 MapleSalon2 resolve_skill_string():
    /// - 扫描 Skill 目录下 0-7 前缀的文件夹（job folders）
    /// - 对每个 job 文件夹下的 skill 子节点
    /// - 过滤有 effect 或 keydown 子节点的技能
    /// - 从 String/Skill.img 获取名称
    /// 返回 (id, parentFolder, name) 三元组。
    func fetchSkillStrings() async -> [(id: String, parentFolder: String, name: String)] {
        guard let base = await resolveBase() else { return [] }
        guard let url = URL(string: "\(base)/api/wz/data/query/string") else { return [] }
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let body: [String: String] = ["type": "skill", "name": ""]
        req.httpBody = try? JSONEncoder().encode(body)

        do {
            let (data, _) = try await session.data(for: req)
            guard let list = try? JSONSerialization.jsonObject(with: data) as? [[String: Any]] else { return [] }
            return list.compactMap { dict in
                guard let id = dict["code"] as? String ?? dict["id"] as? String else { return nil }
                let name = dict["name"] as? String ?? ""
                let parentFolder = dict["parentFolder"] as? String ?? ""
                return (id: id, parentFolder: parentFolder, name: name)
            }.sorted { a, b in (Int(a.id) ?? 0) < (Int(b.id) ?? 0) }
        } catch {
            logDebug("fetchSkillStrings failed: \(error)")
            return []
        }
    }

    // MARK: - Map String (对应 MapleSalon2 resolve_map_string)

    /// 获取地图字符串列表。
    /// 匹配 MapleSalon2 resolve_map_string():
    /// - 先解析 String/Map.img 得到 mapName / streetName
    /// - 再扫描 Map/Map 下的 Map0-Map9 文件夹和 .img 文件
    /// 返回 (id, mapName, streetName) 三元组。
    func fetchMapStrings() async -> [(id: String, mapName: String, streetName: String)] {
        guard let base = await resolveBase() else { return [] }
        guard let url = URL(string: "\(base)/api/wz/data/query/string") else { return [] }
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let body: [String: String] = ["type": "map", "name": ""]
        req.httpBody = try? JSONEncoder().encode(body)

        do {
            let (data, _) = try await session.data(for: req)
            guard let list = try? JSONSerialization.jsonObject(with: data) as? [[String: Any]] else { return [] }
            return list.compactMap { dict in
                guard let id = dict["code"] as? String ?? dict["id"] as? String else { return nil }
                let mapName = dict["mapName"] as? String ?? ""
                let streetName = dict["streetName"] as? String ?? ""
                return (id: id, mapName: mapName, streetName: streetName)
            }.sorted { a, b in (Int(a.id) ?? 0) < (Int(b.id) ?? 0) }
        } catch {
            logDebug("fetchMapStrings failed: \(error)")
            return []
        }
    }

    // MARK: - Item Info (对应 MapleSalon2 handlers/item.rs)

    /// 查询某个装备的 info 属性。
    /// 对应 MapleSalon2 get_item_info_node() — 从 Character/{category}/{id padded}.img/info 获取。
    /// 返回 info 节点下的所有属性（cash, colorvar, nameTag, chatBalloon, medalTag, nickTag 等）。
    func fetchItemInfo(category: String, itemId: String) async -> [String: Any]? {
        let path = "Character/\(category)/\(WzPathBuilder.padded(itemId)).img/info"
        return await fetchInfoProperties(wzPath: path)
    }

    /// 判断某个道具是否为现金道具（info/cash == 1）
    /// 对应 MapleSalon2 get_is_cash_item()
    func isCashItem(category: String, itemId: String) async -> Bool {
        let path = "Character/\(category)/\(WzPathBuilder.padded(itemId)).img/info/cash"
        guard let base = await resolveBase(),
              let url = URL(string: "\(base)/api/wz/property") else { return false }
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let body: [String: String] = ["path": path]
        req.httpBody = try? JSONEncoder().encode(body)
        do {
            let (data, _) = try await session.data(for: req)
            guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let val = json["value"] else { return false }
            return "\(val)" == "1"
        } catch {
            return false
        }
    }

    /// 判断某个道具是否有 colorvar（info/colorvar 存在）
    /// 对应 MapleSalon2 get_is_colorvar()
    func hasColorvar(category: String, itemId: String) async -> Bool {
        let path = "Character/\(category)/\(WzPathBuilder.padded(itemId)).img/info/colorvar"
        guard let base = await resolveBase(),
              let url = URL(string: "\(base)/api/wz/property") else { return false }
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let body: [String: String] = ["path": path]
        req.httpBody = try? JSONEncoder().encode(body)
        do {
            let (data, _) = try await session.data(for: req)
            guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return false }
            return json["value"] != nil
        } catch {
            return false
        }
    }

    /// 判断某个道具是否为名字标签（info/nameTag 存在）
    /// 对应 MapleSalon2 get_is_name_tag()
    func isNameTag(category: String, itemId: String) async -> Bool {
        let path = "Character/\(category)/\(WzPathBuilder.padded(itemId)).img/info/nameTag"
        return await hasProperty(path: path)
    }

    /// 判断某个道具是否为聊天气泡（info/chatBalloon 存在）
    /// 对应 MapleSalon2 get_is_chat_balloon()
    func isChatBalloon(category: String, itemId: String) async -> Bool {
        let path = "Character/\(category)/\(WzPathBuilder.padded(itemId)).img/info/chatBalloon"
        return await hasProperty(path: path)
    }

    /// 判断某个道具是否为勋章标签（info/medalTag 存在）
    /// 对应 MapleSalon2 get_is_medal()
    func isMedalTag(category: String, itemId: String) async -> Bool {
        let path = "Character/\(category)/\(WzPathBuilder.padded(itemId)).img/info/medalTag"
        return await hasProperty(path: path)
    }

    /// 判断某个道具是否为昵称标签（info/info/nickTag 存在）
    /// 对应 MapleSalon2 get_is_nick_tag()
    func isNickTag(category: String, itemId: String) async -> Bool {
        let path = "Character/\(category)/\(WzPathBuilder.padded(itemId)).img/info/info/nickTag"
        return await hasProperty(path: path)
    }

    /// 检查某个装备是否有效果（info 节点存在，或在 Effect/ItemEff.img 下有同名节点）
    /// 对应 MapleSalon2 中 resolve_equip_string 的 effect 判断逻辑
    func hasEquipEffect(category: String, itemId: String) async -> Bool {
        // 检查 Effect/ItemEff.img/{itemId}/effect 是否存在
        let path = "Effect/ItemEff.img/\(itemId)/effect"
        return await hasProperty(path: path)
    }

    // MARK: - Zmap / Smap

    /// 获取 zmap.img/zmap 下的身体部位列表（渲染顺序）。
    /// 对应 MapleSalon2 中从 Base/zmap.img 获取 z-order 的流程。
    func fetchZmap() async -> [String] {
        guard let base = await resolveBase() else { return [] }
        guard let url = URL(string: "\(base)/api/wz/property") else { return [] }
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let body: [String: String] = ["path": "Base/zmap.img/zmap"]
        req.httpBody = try? JSONEncoder().encode(body)

        do {
            let (data, _) = try await session.data(for: req)
            guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return [] }
            if let value = json["value"] as? [String] {
                return value
            }
            // 如果 value 不是数组，尝试从 children 中获取
            if let children = json["children"] as? [String: Any] {
                return Array(children.keys).sorted()
            }
            return []
        } catch {
            logDebug("fetchZmap failed: \(error)")
            return []
        }
    }

    /// 获取 smap.img 下的子部位映射。
    /// 对应 MapleSalon2 中从 Base/smap.img 获取子部位关系的流程。
    func fetchSmap() async -> [String: [String]] {
        guard let base = await resolveBase() else { return [:] }
        guard let url = URL(string: "\(base)/api/wz/property") else { return [:] }
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let body: [String: String] = ["path": "Base/smap.img"]
        req.httpBody = try? JSONEncoder().encode(body)

        do {
            let (data, _) = try await session.data(for: req)
            guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return [:] }
            // 尝试 children 模式
            if let children = json["children"] as? [String: Any] {
                var result: [String: [String]] = [:]
                for (key, val) in children {
                    if let subArr = val as? [String] {
                        result[key] = subArr
                    } else if let subDict = val as? [String: Any],
                              let subChildren = subDict["children"] as? [String: Any] {
                        result[key] = Array(subChildren.keys)
                    }
                }
                return result
            }
            return [:]
        } catch {
            logDebug("fetchSmap failed: \(error)")
            return [:]
        }
    }

    // MARK: - Set Effect Map (对应 MapleSalon2 EQUIP_SET_EFFECT_PATH)

    /// 获取套装特效映射（Effect/SetEff.img）。
    /// 返回每个套装 ID 及其子节点（effect 名称等）。
    func fetchSetEffectMap() async -> [String: [String: Any]] {
        guard let base = await resolveBase() else { return [:] }
        guard let url = URL(string: "\(base)/api/wz/property") else { return [:] }
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let body: [String: String] = ["path": "Effect/SetEff.img"]
        req.httpBody = try? JSONEncoder().encode(body)

        do {
            let (data, _) = try await session.data(for: req)
            guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return [:] }
            if let children = json["children"] as? [String: Any] {
                var result: [String: [String: Any]] = [:]
                for (key, val) in children {
                    if let dict = val as? [String: Any] {
                        result[key] = dict
                    }
                }
                return result
            }
            return [:]
        } catch {
            logDebug("fetchSetEffectMap failed: \(error)")
            return [:]
        }
    }

    // MARK: - Image Path Discovery (桥接到 WzImageLoader)

    /// 发现某个 WZ 节点下的所有 Canvas 帧路径。
    /// 对应 MapleSalon2 中 renderImgPath 的查询逻辑。
    /// 已由 WzImageLoader.discoverFramePaths 实现，这里提供便捷封装。
    func discoverFramePaths(wzPath: String) async -> [String] {
        guard let base = await resolveBase(),
              let url = URL(string: "\(base)/api/wz/renderImgPath") else { return [] }
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let body: [String: String] = ["path": wzPath]
        req.httpBody = try? JSONEncoder().encode(body)
        do {
            let (data, _) = try await session.data(for: req)
            guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let images = json["images"] as? [String] else { return [] }
            return images
        } catch {
            logDebug("discoverFramePaths failed: \(wzPath) - \(error)")
            return []
        }
    }

    // MARK: - Private Helpers

    /// 查询 info 节点下的所有属性
    private func fetchInfoProperties(wzPath: String) async -> [String: Any]? {
        guard let base = await resolveBase(),
              let url = URL(string: "\(base)/api/wz/property") else { return nil }
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let body: [String: String] = ["path": wzPath]
        req.httpBody = try? JSONEncoder().encode(body)
        do {
            let (data, _) = try await session.data(for: req)
            guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
            return json
        } catch {
            logDebug("fetchInfoProperties failed: \(wzPath) - \(error)")
            return nil
        }
    }

    /// 检查某个属性路径是否存在（即有值）
    private func hasProperty(path: String) async -> Bool {
        guard let base = await resolveBase(),
              let url = URL(string: "\(base)/api/wz/property") else { return false }
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let body: [String: String] = ["path": path]
        req.httpBody = try? JSONEncoder().encode(body)
        do {
            let (data, _) = try await session.data(for: req)
            guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return false }
            return json["value"] != nil
        } catch {
            return false
        }
    }
}