import AppKit

// MARK: - Material Browser Data Types

/// 素材类型枚举，用于分类查询结果 & 树形分组
enum MaterialType: String, CaseIterable, Codable {
    case item      = "道具"       // 道具/装备
    case equip     = "装备"
    case mob       = "怪物"
    case npc       = "NPC"
    case map       = "地图"
    case skill     = "技能"

    /// 后端 query string 用的 type 参数值
    var queryType: String {
        switch self {
        case .item:  return "item"
        case .equip: return "equip"
        case .mob:   return "mob"
        case .npc:   return "npc"
        case .map:   return "map"
        case .skill: return "skill"
        }
    }

    /// 显示在结果列表左侧的图标字符（例如类型指示符）
    var iconChar: String {
        switch self {
        case .item:  return "🟢"
        case .equip: return "🔵"
        case .mob:   return "🔴"
        case .npc:   return "🟡"
        case .map:   return "🟣"
        case .skill: return "🟠"
        }
    }
}

/// 搜索结果统一模型
struct MaterialItem: Codable {
    let code: String
    var name: String
    var type: MaterialType?
    var category: String?
    var parentFolder: String?
    var description: String?
    var isCash: Bool?

    /// 预览图缓存路径（本地）
    var thumbnailPath: String?

    enum CodingKeys: String, CodingKey {
        case code, name, type, category, parentFolder, description, isCash
    }
}

/// 分类树节点（用于 NSOutlineView）
class CatalogNode {
    let title: String
    let type: MaterialType?
    let icon: String?
    var children: [CatalogNode]

    init(title: String, type: MaterialType? = nil, icon: String? = nil, children: [CatalogNode] = []) {
        self.title = title
        self.type = type
        self.icon = icon
        self.children = children
    }

    var isLeaf: Bool { children.isEmpty }
}

// MARK: - 收藏管理器

class FavoriteManager {
    static let shared = FavoriteManager()

    private var favorites: [MaterialItem] = []
    private let savePath: String = {
        let dir = (NSHomeDirectory() as NSString).appendingPathComponent("Library/Caches/MiniPet")
        return (dir as NSString).appendingPathComponent("favorites.json")
    }()

    private init() {
        load()
    }

    // MARK: - 查询

    var all: [MaterialItem] { favorites }

    func contains(code: String) -> Bool {
        favorites.contains { $0.code == code }
    }

    func item(for code: String) -> MaterialItem? {
        favorites.first { $0.code == code }
    }

    // MARK: - 增删

    func add(_ item: MaterialItem) {
        guard !contains(code: item.code) else { return }
        favorites.append(item)
        save()
    }

    func remove(code: String) {
        favorites.removeAll { $0.code == code }
        save()
    }

    func toggle(_ item: MaterialItem) -> Bool {
        if contains(code: item.code) {
            remove(code: item.code)
            return false
        } else {
            add(item)
            return true
        }
    }

    // MARK: - Persistence

    private func save() {
        guard let data = try? JSONEncoder().encode(favorites) else { return }
        try? FileManager.default.createDirectory(
            at: URL(fileURLWithPath: savePath).deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try? data.write(to: URL(fileURLWithPath: savePath))
    }

    private func load() {
        guard let data = try? Data(contentsOf: URL(fileURLWithPath: savePath)),
              let items = try? JSONDecoder().decode([MaterialItem].self, from: data) else { return }
        favorites = items
    }
}

// MARK: - 搜索历史管理器

class SearchHistoryManager {
    static let shared = SearchHistoryManager()

    private var history: [String] = []
    private let maxCount = 20
    private let savePath: String = {
        let dir = (NSHomeDirectory() as NSString).appendingPathComponent("Library/Caches/MiniPet")
        return (dir as NSString).appendingPathComponent("search_history.json")
    }()

    private init() { load() }

    var all: [String] { history }

    func add(_ query: String) {
        let trimmed = query.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty else { return }
        history.removeAll { $0 == trimmed }
        history.insert(trimmed, at: 0)
        if history.count > maxCount { history = Array(history.prefix(maxCount)) }
        save()
    }

    func clear() {
        history.removeAll()
        save()
    }

    private func save() {
        guard let data = try? JSONEncoder().encode(history) else { return }
        try? FileManager.default.createDirectory(
            at: URL(fileURLWithPath: savePath).deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try? data.write(to: URL(fileURLWithPath: savePath))
    }

    private func load() {
        guard let data = try? Data(contentsOf: URL(fileURLWithPath: savePath)),
              let items = try? JSONDecoder().decode([String].self, from: data) else { return }
        history = items
    }
}

// MARK: - Material Browser Panel

/// 素材查询面板 — macOS AppKit 浮动面板，用于在游戏过程中快速查询和浏览冒险岛素材。
///
/// 布局 (NSSplitView 三栏):
/// ┌────────────────────────────────────────────────┐
/// │               NSSearchField (顶部)              │
/// ├────────┬────────────────────┬──────────────────┤
/// │ 分类树  │     结果列表       │    详情视图      │
/// │ 左侧   │     中间           │    右侧          │
/// │Outline │   TableView        │    详情          │
/// └────────┴────────────────────┴──────────────────┘
///
class MaterialBrowserPanel: NSPanel {

    // MARK: - 子视图引用

    private let searchField = NSSearchField()
    private let searchSuggestionsPopover = NSPopover()

    private let splitView = NSSplitView()
    private let leftOutlineView = NSOutlineView()
    private let leftScrollView = NSScrollView()
    private let middleTableView = NSTableView()
    private let middleScrollView = NSScrollView()
    private let rightDetailView = NSView()
    private let rightScrollView = NSScrollView()

    // 详情视图的子控件
    private let detailImageView = NSImageView()
    private let detailNameLabel = NSTextField(labelWithString: "")
    private let detailIdLabel = NSTextField(labelWithString: "")
    private let detailTypeLabel = NSTextField(labelWithString: "")
    private let detailDescLabel = NSTextField(labelWithString: "")
    private let detailRelatedLabel = NSTextField(labelWithString: "")

    // 分类树数据源
    private var catalogRootNodes: [CatalogNode] = []
    // 搜索结果 & 浏览结果
    private var searchResults: [MaterialItem] = []
    private var currentTypeResults: [String: [MaterialItem]] = [:]
    // 当前选中的素材
    private var selectedItem: MaterialItem?
    // 选中项的预览图缓存
    private var selectedThumbnail: NSImage?

    // 批量操作辅助
    private var batchSelection: Set<String> = []

    // 共享的 API 客户端和图片加载器（复用连接和缓存）
    private let apiClient = APIClient()
    private let imageLoader = WzImageLoader()

    // MARK: - 初始化

    override init(contentRect: NSRect, styleMask style: NSWindow.StyleMask, backing backingStoreType: NSWindow.BackingStoreType, defer flag: Bool) {
        // 使用带标题栏和可调整大小的面板样式，同时保持浮动
        let panelStyle: NSWindow.StyleMask = [.titled, .closable, .resizable, .miniaturizable, .nonactivatingPanel]
        super.init(contentRect: contentRect, styleMask: panelStyle, backing: backingStoreType, defer: flag)
        commonInit()
    }

    private func commonInit() {
        isFloatingPanel = true
        isMovableByWindowBackground = true
        title = "素材浏览器"
        minSize = NSSize(width: 860, height: 520)
        setFrame(NSRect(x: 200, y: 200, width: 1000, height: 640), display: false)
        level = .floating
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]

        buildUI()
        buildCatalogTree()
    }

    // MARK: - UI 构建

    private func buildUI() {
        // ── 整体垂直布局 ──
        // 顶层容器
        let container = NSView(frame: contentView?.bounds ?? .zero)
        container.autoresizingMask = [.width, .height]
        contentView = container

        // 搜索栏（顶部）
        searchField.translatesAutoresizingMaskIntoConstraints = false
        searchField.placeholderString = "搜索道具、怪物、NPC、地图、技能…"
        searchField.sendsWholeSearchString = true
        searchField.sendsSearchStringImmediately = true
        searchField.bezelStyle = .roundedBezel
        searchField.font = NSFont.systemFont(ofSize: 14)
        searchField.target = self
        searchField.action = #selector(searchFieldAction(_:))
        searchField.delegate = self
        container.addSubview(searchField)

        // 分割视图（三栏）
        splitView.translatesAutoresizingMaskIntoConstraints = false
        splitView.isVertical = true
        splitView.dividerStyle = .thin
        splitView.autoresizingMask = [.width, .height]
        container.addSubview(splitView)

        // 左侧：分类树（20%）
        leftScrollView.hasVerticalScroller = true
        leftScrollView.autohidesScrollers = true
        leftScrollView.borderType = .bezelBorder
        leftScrollView.translatesAutoresizingMaskIntoConstraints = false

        leftOutlineView.delegate = self
        leftOutlineView.dataSource = self
        leftOutlineView.headerView = nil
        leftOutlineView.autoresizingMask = [.width, .height]
        leftOutlineView.columnAutoresizingStyle = .uniformColumnAutoresizingStyle
        let leftCol = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("catalogCol"))
        leftCol.title = "分类"
        leftCol.isEditable = false
        leftOutlineView.addTableColumn(leftCol)
        leftOutlineView.outlineTableColumn = leftCol
        leftScrollView.documentView = leftOutlineView
        splitView.addArrangedSubview(leftScrollView)

        // 中间：结果列表（45%）
        middleScrollView.hasVerticalScroller = true
        middleScrollView.autohidesScrollers = true
        middleScrollView.borderType = .bezelBorder
        middleScrollView.translatesAutoresizingMaskIntoConstraints = false

        middleTableView.delegate = self
        middleTableView.dataSource = self
        middleTableView.allowsMultipleSelection = true
        middleTableView.allowsColumnReordering = false
        middleTableView.usesAlternatingRowBackgroundColors = true
        middleTableView.autoresizingMask = [.width, .height]
        middleTableView.doubleAction = #selector(tableViewDoubleClick(_:))

        // 类型图标列
        let iconCol = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("iconCol"))
        iconCol.title = ""
        iconCol.width = 28
        iconCol.minWidth = 24
        iconCol.maxWidth = 32
        iconCol.isEditable = false
        middleTableView.addTableColumn(iconCol)

        // 名称列
        let nameCol = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("nameCol"))
        nameCol.title = "名称"
        nameCol.width = 180
        nameCol.minWidth = 100
        nameCol.isEditable = false
        middleTableView.addTableColumn(nameCol)

        // ID 列
        let idCol = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("idCol"))
        idCol.title = "ID"
        idCol.width = 100
        idCol.minWidth = 80
        idCol.isEditable = false
        middleTableView.addTableColumn(idCol)

        middleScrollView.documentView = middleTableView
        splitView.addArrangedSubview(middleScrollView)

        // 右侧：详情视图（35%）
        rightScrollView.hasVerticalScroller = true
        rightScrollView.autohidesScrollers = true
        rightScrollView.borderType = .noBorder
        rightScrollView.autoresizingMask = [.width, .height]
        rightScrollView.translatesAutoresizingMaskIntoConstraints = false

        buildDetailView()
        rightScrollView.documentView = rightDetailView
        splitView.addArrangedSubview(rightScrollView)

        // 设置分割比例
        splitView.adjustSubviews()

        // 设置左侧（~200px）和右侧（~300px）的 holding priority 阻止折叠
        leftScrollView.setContentHuggingPriority(.required, for: .horizontal)
        rightScrollView.setContentHuggingPriority(.required, for: .horizontal)

        // 显式设置初始分隔线位置：左 200px，右 300px，中间填充剩余
        let totalWidth = splitView.bounds.width
        let dividerThickness = splitView.dividerThickness
        if totalWidth > 0 {
            let rightPosition = totalWidth - 300 - dividerThickness
            splitView.setPosition(200, ofDividerAt: 0)
            splitView.setPosition(rightPosition, ofDividerAt: 1)
        }

        // Auto Layout 约束
        NSLayoutConstraint.activate([
            searchField.topAnchor.constraint(equalTo: container.topAnchor, constant: 8),
            searchField.leadingAnchor.constraint(equalTo: container.leadingAnchor, constant: 8),
            searchField.trailingAnchor.constraint(equalTo: container.trailingAnchor, constant: -8),

            splitView.topAnchor.constraint(equalTo: searchField.bottomAnchor, constant: 6),
            splitView.leadingAnchor.constraint(equalTo: container.leadingAnchor, constant: 4),
            splitView.trailingAnchor.constraint(equalTo: container.trailingAnchor, constant: -4),
            splitView.bottomAnchor.constraint(equalTo: container.bottomAnchor, constant: -4),
        ])
    }

    /// 构建右侧详情容器
    private func buildDetailView() {
        rightDetailView.translatesAutoresizingMaskIntoConstraints = false
        rightDetailView.wantsLayer = true
        rightDetailView.layer?.backgroundColor = NSColor.controlBackgroundColor.cgColor

        // 预览图
        detailImageView.translatesAutoresizingMaskIntoConstraints = false
        detailImageView.imageScaling = .scaleProportionallyDown
        detailImageView.wantsLayer = true
        detailImageView.layer?.backgroundColor = NSColor.darkGray.withAlphaComponent(0.1).cgColor
        detailImageView.layer?.cornerRadius = 6
        detailImageView.layer?.borderWidth = 1
        detailImageView.layer?.borderColor = NSColor.separatorColor.cgColor
        rightDetailView.addSubview(detailImageView)

        // 名称
        detailNameLabel.translatesAutoresizingMaskIntoConstraints = false
        detailNameLabel.font = NSFont.boldSystemFont(ofSize: 16)
        detailNameLabel.lineBreakMode = .byTruncatingTail
        rightDetailView.addSubview(detailNameLabel)

        // ID
        detailIdLabel.translatesAutoresizingMaskIntoConstraints = false
        detailIdLabel.font = NSFont.monospacedDigitSystemFont(ofSize: 12, weight: .regular)
        detailIdLabel.textColor = .secondaryLabelColor
        rightDetailView.addSubview(detailIdLabel)

        // 类型
        detailTypeLabel.translatesAutoresizingMaskIntoConstraints = false
        detailTypeLabel.font = NSFont.systemFont(ofSize: 12)
        detailTypeLabel.textColor = .tertiaryLabelColor
        rightDetailView.addSubview(detailTypeLabel)

        // 描述
        detailDescLabel.translatesAutoresizingMaskIntoConstraints = false
        detailDescLabel.font = NSFont.systemFont(ofSize: 12)
        detailDescLabel.textColor = .labelColor
        detailDescLabel.preferredMaxLayoutWidth = 280
        detailDescLabel.maximumNumberOfLines = 6
        detailDescLabel.lineBreakMode = .byWordWrapping
        detailDescLabel.isEditable = false
        detailDescLabel.isSelectable = false
        rightDetailView.addSubview(detailDescLabel)

        // 关联数据标签
        detailRelatedLabel.translatesAutoresizingMaskIntoConstraints = false
        detailRelatedLabel.font = NSFont.systemFont(ofSize: 11)
        detailRelatedLabel.textColor = .tertiaryLabelColor
        detailRelatedLabel.preferredMaxLayoutWidth = 280
        detailRelatedLabel.maximumNumberOfLines = 0
        detailRelatedLabel.isEditable = false
        detailRelatedLabel.isSelectable = false
        rightDetailView.addSubview(detailRelatedLabel)

        // —— 操作按钮 ——

        // "发送到桌宠" 按钮
        let sendToPetButton = NSButton(title: "🐾 发送到桌宠", target: self, action: #selector(sendToPetAction(_:)))
        sendToPetButton.translatesAutoresizingMaskIntoConstraints = false
        sendToPetButton.bezelStyle = .rounded
        sendToPetButton.controlSize = .regular
        rightDetailView.addSubview(sendToPetButton)

        // 收藏按钮
        let favButton = NSButton(title: "⭐ 收藏", target: self, action: #selector(toggleFavoriteAction(_:)))
        favButton.translatesAutoresizingMaskIntoConstraints = false
        favButton.bezelStyle = .rounded
        favButton.controlSize = .regular
        rightDetailView.addSubview(favButton)

        // 外部链接按钮
        let externalLinkButton = NSButton(title: "🌐 查看详情", target: self, action: #selector(openExternalLinkAction(_:)))
        externalLinkButton.translatesAutoresizingMaskIntoConstraints = false
        externalLinkButton.bezelStyle = .rounded
        externalLinkButton.controlSize = .regular
        rightDetailView.addSubview(externalLinkButton)

        // 批量操作按钮
        let batchPreviewButton = NSButton(title: "📥 批量预览", target: self, action: #selector(batchPreviewAction(_:)))
        batchPreviewButton.translatesAutoresizingMaskIntoConstraints = false
        batchPreviewButton.bezelStyle = .rounded
        batchPreviewButton.controlSize = .small
        rightDetailView.addSubview(batchPreviewButton)

        // Auto Layout
        let margin: CGFloat = 12
        let buttonHeight: CGFloat = 28
        NSLayoutConstraint.activate([
            detailImageView.topAnchor.constraint(equalTo: rightDetailView.topAnchor, constant: margin),
            detailImageView.centerXAnchor.constraint(equalTo: rightDetailView.centerXAnchor),
            detailImageView.widthAnchor.constraint(equalToConstant: 160),
            detailImageView.heightAnchor.constraint(equalToConstant: 160),

            detailNameLabel.topAnchor.constraint(equalTo: detailImageView.bottomAnchor, constant: 10),
            detailNameLabel.leadingAnchor.constraint(equalTo: rightDetailView.leadingAnchor, constant: margin),
            detailNameLabel.trailingAnchor.constraint(equalTo: rightDetailView.trailingAnchor, constant: -margin),

            detailIdLabel.topAnchor.constraint(equalTo: detailNameLabel.bottomAnchor, constant: 4),
            detailIdLabel.leadingAnchor.constraint(equalTo: rightDetailView.leadingAnchor, constant: margin),
            detailIdLabel.trailingAnchor.constraint(equalTo: rightDetailView.trailingAnchor, constant: -margin),

            detailTypeLabel.topAnchor.constraint(equalTo: detailIdLabel.bottomAnchor, constant: 2),
            detailTypeLabel.leadingAnchor.constraint(equalTo: rightDetailView.leadingAnchor, constant: margin),
            detailTypeLabel.trailingAnchor.constraint(equalTo: rightDetailView.trailingAnchor, constant: -margin),

            detailDescLabel.topAnchor.constraint(equalTo: detailTypeLabel.bottomAnchor, constant: 8),
            detailDescLabel.leadingAnchor.constraint(equalTo: rightDetailView.leadingAnchor, constant: margin),
            detailDescLabel.trailingAnchor.constraint(equalTo: rightDetailView.trailingAnchor, constant: -margin),

            detailRelatedLabel.topAnchor.constraint(equalTo: detailDescLabel.bottomAnchor, constant: 8),
            detailRelatedLabel.leadingAnchor.constraint(equalTo: rightDetailView.leadingAnchor, constant: margin),
            detailRelatedLabel.trailingAnchor.constraint(equalTo: rightDetailView.trailingAnchor, constant: -margin),

            // 按钮行
            sendToPetButton.topAnchor.constraint(equalTo: detailRelatedLabel.bottomAnchor, constant: 14),
            sendToPetButton.leadingAnchor.constraint(equalTo: rightDetailView.leadingAnchor, constant: margin),
            sendToPetButton.trailingAnchor.constraint(equalTo: rightDetailView.trailingAnchor, constant: -margin),
            sendToPetButton.heightAnchor.constraint(equalToConstant: buttonHeight),

            favButton.topAnchor.constraint(equalTo: sendToPetButton.bottomAnchor, constant: 6),
            favButton.leadingAnchor.constraint(equalTo: rightDetailView.leadingAnchor, constant: margin),
            favButton.trailingAnchor.constraint(equalTo: rightDetailView.trailingAnchor, constant: -margin),
            favButton.heightAnchor.constraint(equalToConstant: buttonHeight),

            externalLinkButton.topAnchor.constraint(equalTo: favButton.bottomAnchor, constant: 6),
            externalLinkButton.leadingAnchor.constraint(equalTo: rightDetailView.leadingAnchor, constant: margin),
            externalLinkButton.trailingAnchor.constraint(equalTo: rightDetailView.trailingAnchor, constant: -margin),
            externalLinkButton.heightAnchor.constraint(equalToConstant: buttonHeight),

            batchPreviewButton.topAnchor.constraint(equalTo: externalLinkButton.bottomAnchor, constant: 12),
            batchPreviewButton.leadingAnchor.constraint(equalTo: rightDetailView.leadingAnchor, constant: margin),
            batchPreviewButton.trailingAnchor.constraint(equalTo: rightDetailView.trailingAnchor, constant: -margin),
            batchPreviewButton.heightAnchor.constraint(equalToConstant: buttonHeight - 4),
        ])
    }

    // MARK: - 分类树构建

    /// 构建左侧分类目录树
    private func buildCatalogTree() {
        // 根节点：所有分类
        let allNode = CatalogNode(title: "全部素材", icon: "📦")

        // 1. 道具分类
        let itemNode = CatalogNode(title: "道具", type: .item, icon: "🟢", children: [
            CatalogNode(title: "消耗", type: .item, icon: "💊"),
            CatalogNode(title: "装备", type: .equip, icon: "🔵"),
            CatalogNode(title: "设置道具", type: .item, icon: "🛋️"),
            CatalogNode(title: "现金道具", type: .item, icon: "💎"),
        ])

        // 2. 怪物目录
        let mobNode = CatalogNode(title: "怪物", type: .mob, icon: "🔴", children: [
            CatalogNode(title: "全部怪物", type: .mob, icon: "🔴"),
        ])

        // 3. NPC 目录
        let npcNode = CatalogNode(title: "NPC", type: .npc, icon: "🟡", children: [
            CatalogNode(title: "按地图", icon: "📍"),
            CatalogNode(title: "按地区", icon: "🗺️"),
        ])

        // 4. 地图目录
        let mapNode = CatalogNode(title: "地图", type: .map, icon: "🟣", children: [
            CatalogNode(title: "全部地图", type: .map),
        ])

        // 5. 技能目录
        let skillNode = CatalogNode(title: "技能", type: .skill, icon: "🟠", children: [
            CatalogNode(title: "战士", type: .skill, icon: "⚔️"),
            CatalogNode(title: "魔法师", type: .skill, icon: "🔮"),
            CatalogNode(title: "弓箭手", type: .skill, icon: "🏹"),
            CatalogNode(title: "飞侠", type: .skill, icon: "🗡️"),
            CatalogNode(title: "海盗", type: .skill, icon: "🏴‍☠️"),
            CatalogNode(title: "其他", type: .skill, icon: "❓"),
        ])

        // 6. 收藏目录
        let favNode = CatalogNode(title: "⭐ 收藏", icon: "⭐")

        catalogRootNodes = [allNode, itemNode, mobNode, npcNode, mapNode, skillNode, favNode]
        leftOutlineView.reloadData()
        // 展开所有节点
        for i in 0..<catalogRootNodes.count {
            leftOutlineView.expandItem(catalogRootNodes[i])
        }
    }

    // MARK: - 搜索

    /// 执行搜索：调用 POST /api/wz/data/query/string
    @objc private func searchFieldAction(_ sender: NSSearchField) {
        let query = sender.stringValue.trimmingCharacters(in: .whitespaces)
        guard !query.isEmpty else { return }
        performSearch(query: query)
    }

    private func performSearch(query: String) {
        // 记录搜索历史
        SearchHistoryManager.shared.add(query)

        // 清空旧数据
        searchResults = []
        currentTypeResults = [:]

        Task {
            await performSearchAsync(query: query)
        }
    }

    /// 异步搜索：跨所有类型并行查询
    private func performSearchAsync(query: String) async {
        await withTaskGroup(of: (MaterialType, [MaterialItem]).self) { group in
            for type in MaterialType.allCases {
                group.addTask {
                    let items = await self.queryType(type: type, query: query)
                    return (type, items)
                }
            }

            var allItems: [MaterialItem] = []
            var typeItems: [String: [MaterialItem]] = [:]

            for await (type, items) in group {
                typeItems[type.rawValue] = items
                allItems.append(contentsOf: items)
            }

            // 按类型排序分组显示
            allItems.sort { ($0.type?.rawValue ?? "") < ($1.type?.rawValue ?? "") }

            await MainActor.run {
                self.searchResults = allItems
                self.currentTypeResults = typeItems
                self.middleTableView.reloadData()

                // 如果有搜索结果，滚动到顶部
                if !allItems.isEmpty {
                    self.middleTableView.scrollRowToVisible(0)
                }
            }
        }
    }

    /// 查询单个类型
    private func queryType(type: MaterialType, query: String) async -> [MaterialItem] {
        guard let base = await apiClient.resolveBase() else {
            logDebug("MaterialBrowser: resolveBase returned nil, cannot query type=\(type.rawValue)")
            return []
        }
        guard let url = URL(string: "\(base)/api/wz/data/query/string") else {
            logDebug("MaterialBrowser: invalid URL for query/string")
            return []
        }

        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")

        let body: [String: String] = [
            "type": type.queryType,
            "name": query,
        ]
        req.httpBody = try? JSONEncoder().encode(body)

        do {
            let (data, response) = try await apiClient.session.data(for: req)
            if let httpResponse = response as? HTTPURLResponse {
                logDebug("MaterialBrowser: queryType(\(type.rawValue), \"\(query)\") HTTP \(httpResponse.statusCode)")
            }
            guard let list = try? JSONSerialization.jsonObject(with: data) as? [[String: Any]] else {
                logDebug("MaterialBrowser: queryType(\(type.rawValue)) failed to parse JSON")
                return []
            }
            logDebug("MaterialBrowser: queryType(\(type.rawValue)) got \(list.count) results")
            return list.compactMap { dict in
                guard let code = dict["code"] as? String ?? dict["id"] as? String else { return nil }
                let name = dict["name"] as? String ?? ""
                let category = dict["category"] as? String
                let parentFolder = dict["parentFolder"] as? String
                let isCash = dict["isCash"] as? Bool
                let description = dict["description"] as? String ?? dict["desc"] as? String
                return MaterialItem(
                    code: code,
                    name: name,
                    type: type,
                    category: category,
                    parentFolder: parentFolder,
                    description: description,
                    isCash: isCash
                )
            }
        } catch {
            logDebug("MaterialBrowser: queryType(\(type.rawValue)) failed: \(error)")
            return []
        }
    }

    /// 分类浏览：按分类树节点查询
    private func browseCategory(node: CatalogNode) {
        guard let type = node.type else {
            // 无 type 的节点（如"按等级""收藏"）特殊处理
            if node.title.contains("收藏") {
                loadFavorites()
            }
            return
        }

        Task {
            let items = await queryType(type: type, query: "")
            await MainActor.run {
                self.searchResults = items
                self.currentTypeResults = [type.rawValue: items]
                self.middleTableView.reloadData()
            }
        }
    }

    /// 加载收藏列表
    private func loadFavorites() {
        searchResults = FavoriteManager.shared.all
        currentTypeResults = [:]
        middleTableView.reloadData()
    }

    // MARK: - WZ 路径构建

    /// 根据素材类型和 code 构造 WZ 图片路径。
    /// 使用 `_Canvas` 子目录用于怪物，`paddedId`（8 位补零）用于 ID 格式化。
    private func wzImagePath(for item: MaterialItem) -> String? {
        switch item.type {
        case .mob:
            // 怪物路径: Mob/_Canvas/{paddedId}.img/stand/0
            return "Mob/_Canvas/\(item.code.paddedId).img/stand/0"
        case .npc:
            // NPC 路径: Npc/{paddedId}.img/stand/0
            return "Npc/\(item.code.paddedId).img/stand/0"
        case .map:
            // 地图路径: Map/Map/Map{firstDigit}/{code}.img/back/0
            return "Map/Map/Map\(String(item.code.prefix(1)))/\(item.code).img/back/0"
        case .item, .equip:
            if let cat = item.category {
                return "Character/\(cat)/\(item.code.paddedId).img/icon"
            }
            return nil
        case .skill:
            // 技能路径: Skill/{code}.img/icon
            return "Skill/\(item.code).img/icon"
        case nil:
            return nil
        }
    }

    // MARK: - 详情展示

    /// 展示选中素材的详情
    private func showDetail(for item: MaterialItem) {
        selectedItem = item

        detailNameLabel.stringValue = item.name
        detailIdLabel.stringValue = "ID: \(item.code)"
        if let type = item.type {
            detailTypeLabel.stringValue = "类型: \(type.rawValue)"
        } else {
            detailTypeLabel.stringValue = "类型: --"
        }

        // 描述文本
        var desc = item.description ?? "暂无描述"
        if let cat = item.category {
            desc += "\n分类: \(cat)"
        }
        if let isCash = item.isCash, isCash {
            desc += "\n💎 现金道具"
        }
        detailDescLabel.stringValue = desc

        // 关联数据
        var related = "关联数据:\n"
        switch item.type {
        case .mob:
            related += "• 掉落道具: 点击加载\n"
            related += "• 出现地图: 点击加载"
        case .npc:
            related += "• 售卖道具: 点击加载\n"
            related += "• 所在地图: 点击加载"
        case .map:
            related += "• 连接地图: 点击加载\n"
            related += "• 怪物刷新: 点击加载\n"
            related += "• NPC: 点击加载"
        case .item, .equip:
            related += "• 掉落怪物: 点击加载\n"
            related += "• NPC 售卖: 点击加载"
        case .skill:
            related += "• 学习职业: \(item.category ?? "未知")\n"
            related += "• 前置技能: 点击加载"
        case nil:
            related += "暂无"
        }
        detailRelatedLabel.stringValue = related

        // 加载预览图
        loadThumbnail(for: item)
    }

    /// 加载预览图（使用 WzImageLoader，与 SettingsPanel 一致的调用方式）
    private func loadThumbnail(for item: MaterialItem) {
        detailImageView.image = nil
        selectedThumbnail = nil

        guard let wzPath = wzImagePath(for: item) else {
            logDebug("MaterialBrowser: no WZ path for item code=\(item.code) type=\(String(describing: item.type))")
            return
        }

        logDebug("MaterialBrowser: loading thumbnail for code=\(item.code) type=\(String(describing: item.type)) path=\(wzPath)")

        Task {
            if let data = await imageLoader.fetchImage(wzPath: wzPath),
               let img = NSImage(data: data) {
                await MainActor.run {
                    self.selectedThumbnail = img
                    self.detailImageView.image = img
                }
            } else {
                logDebug("MaterialBrowser: failed to load thumbnail for path=\(wzPath)")
            }
        }
    }

    // MARK: - 操作按钮 Action

    /// "发送到桌宠" — 通过 Notification 将选中的怪物/外观发送给 PetView
    @objc private func sendToPetAction(_ sender: NSButton) {
        guard let item = selectedItem else { return }

        // 桌面宠物仅支持怪物发送，发送 mobId 过去即可
        if item.type == .mob {
            NotificationCenter.default.post(
                name: Notification.Name("MaterialBrowserSendToPet"),
                object: nil,
                userInfo: ["mobId": item.code, "name": item.name]
            )
            logDebug("发送到桌宠: \(item.name) (ID: \(item.code))")
        } else {
            // 其他类型通知但暂不支持
            logDebug("仅支持发送怪物到桌宠")
        }
    }

    /// 收藏 / 取消收藏
    @objc private func toggleFavoriteAction(_ sender: NSButton) {
        guard let item = selectedItem else { return }
        let isFav = FavoriteManager.shared.toggle(item)
        sender.title = isFav ? "⭐ 已收藏" : "⭐ 收藏"
        logDebug("收藏: \(item.name) = \(isFav)")
    }

    /// 打开外部链接（mxd.dvg.cn 等）
    @objc private func openExternalLinkAction(_ sender: NSButton) {
        guard let item = selectedItem else { return }

        // 根据素材类型跳转到不同的外部站点
        let urlStr: String = {
            switch item.type {
            case .mob:
                return "https://mxd.dvg.cn/mob/\(item.code)"
            case .npc:
                return "https://mxd.dvg.cn/npc/\(item.code)"
            case .map:
                return "https://mxd.dvg.cn/map/\(item.code)"
            case .item, .equip:
                return "https://mxd.dvg.cn/item/\(item.code)"
            case .skill:
                return "https://mxd.dvg.cn/skill/\(item.code)"
            case nil:
                return "https://mxd.dvg.cn"
            }
        }()

        guard let url = URL(string: urlStr) else { return }
        NSWorkspace.shared.open(url)
    }

    /// 批量预览 — 下载选中行的所有素材预览图，显示数量 + 第一个可用缩略图
    @objc private func batchPreviewAction(_ sender: NSButton) {
        let selectedRows = middleTableView.selectedRowIndexes
        guard !selectedRows.isEmpty else { return }

        let items: [MaterialItem] = selectedRows.compactMap { idx in
            guard idx < searchResults.count else { return nil }
            return searchResults[idx]
        }

        let totalCount = items.count

        // 先显示数量信息
        detailNameLabel.stringValue = "批量预览 (\(totalCount) 项)"
        detailIdLabel.stringValue = ""
        detailTypeLabel.stringValue = "选中 \(totalCount) 个素材"
        detailDescLabel.stringValue = items.prefix(20).map { "\($0.type?.iconChar ?? "•") \($0.name)" }.joined(separator: "\n")
        detailRelatedLabel.stringValue = totalCount > 20 ? "... 还有 \(totalCount - 20) 项" : ""

        // 仅加载第一个素材的缩略图
        guard let firstItem = items.first else { return }

        guard let wzPath = wzImagePath(for: firstItem) else {
            logDebug("MaterialBrowser: batchPreview - no WZ path for firstItem code=\(firstItem.code)")
            return
        }

        logDebug("MaterialBrowser: batchPreview loading thumbnail for path=\(wzPath)")

        Task {
            if let data = await imageLoader.fetchImage(wzPath: wzPath),
               let img = NSImage(data: data) {
                await MainActor.run {
                    self.selectedThumbnail = img
                    self.detailImageView.image = img
                }
            } else {
                logDebug("MaterialBrowser: batchPreview failed to load thumbnail for path=\(wzPath)")
            }
        }
    }

    // MARK: - 右键菜单

    /// 配置表格视图的右键菜单
    private func setupContextMenu(for tableView: NSTableView) -> NSMenu {
        let menu = NSMenu()

        menu.addItem(NSMenuItem(title: "收藏", action: #selector(contextMenuFavorite(_:)), keyEquivalent: ""))
        menu.addItem(NSMenuItem(title: "发送到桌宠", action: #selector(contextMenuSendToPet(_:)), keyEquivalent: ""))
        menu.addItem(NSMenuItem.separator())
        menu.addItem(NSMenuItem(title: "打开外部链接", action: #selector(contextMenuOpenExternal(_:)), keyEquivalent: ""))
        menu.addItem(NSMenuItem(title: "复制 ID", action: #selector(contextMenuCopyID(_:)), keyEquivalent: "c"))

        return menu
    }

    @objc private func contextMenuFavorite(_ sender: NSMenuItem) {
        let row = middleTableView.clickedRow
        guard row >= 0, row < searchResults.count else { return }
        let item = searchResults[row]
        _ = FavoriteManager.shared.toggle(item)
    }

    @objc private func contextMenuSendToPet(_ sender: NSMenuItem) {
        let row = middleTableView.clickedRow
        guard row >= 0, row < searchResults.count else { return }
        let item = searchResults[row]
        selectedItem = item
        sendToPetAction(NSButton())
    }

    @objc private func contextMenuOpenExternal(_ sender: NSMenuItem) {
        let row = middleTableView.clickedRow
        guard row >= 0, row < searchResults.count else { return }
        selectedItem = searchResults[row]
        openExternalLinkAction(NSButton())
    }

    @objc private func contextMenuCopyID(_ sender: NSMenuItem) {
        let row = middleTableView.clickedRow
        guard row >= 0, row < searchResults.count else { return }
        let item = searchResults[row]
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(item.code, forType: .string)
    }

    // MARK: - 表格双击

    @objc private func tableViewDoubleClick(_ sender: NSTableView) {
        let row = sender.clickedRow
        guard row >= 0, row < searchResults.count else { return }
        let item = searchResults[row]
        showDetail(for: item)
    }
}

// MARK: - NSSearchFieldDelegate

extension MaterialBrowserPanel: NSSearchFieldDelegate {

    func searchFieldDidStartSearching(_ sender: NSSearchField) {
        // 当用户开始输入时，可以展示搜索建议 popover
    }

    func searchFieldDidEndSearching(_ sender: NSSearchField) {
        // 搜索结束
    }

    func controlTextDidChange(_ obj: Notification) {
        guard let field = obj.object as? NSSearchField else { return }
        let text = field.stringValue

        // 当输入文本非空时，展示实时建议
        if !text.isEmpty && text.count >= 1 {
            showSuggestions(for: text, from: field)
        } else {
            dismissSuggestions()
        }
    }

    /// 展示实时建议 Popover
    private func showSuggestions(for text: String, from field: NSSearchField) {
        // 获取搜索历史建议
        let historySuggestions = SearchHistoryManager.shared.all.filter { $0.lowercased().contains(text.lowercased()) }

        // 如果还没有执行搜索，这里只是建议展示
        if historySuggestions.isEmpty { return }

        // 使用 popover 或独立窗口展示建议
        if !searchSuggestionsPopover.isShown {
            let suggestionVC = SuggestionViewController()
            suggestionVC.suggestions = historySuggestions
            suggestionVC.onSelect = { [weak self] selected in
                self?.searchField.stringValue = selected
                self?.dismissSuggestions()
                self?.performSearch(query: selected)
            }
            searchSuggestionsPopover.contentViewController = suggestionVC
            searchSuggestionsPopover.behavior = .transient
            searchSuggestionsPopover.show(relativeTo: field.bounds, of: field, preferredEdge: .minY)
        }
    }

    private func dismissSuggestions() {
        if searchSuggestionsPopover.isShown {
            searchSuggestionsPopover.performClose(nil)
        }
    }
}

// MARK: - NSOutlineViewDataSource / NSOutlineViewDelegate

extension MaterialBrowserPanel: NSOutlineViewDataSource, NSOutlineViewDelegate {

    func outlineView(_ outlineView: NSOutlineView, numberOfChildrenOfItem item: Any?) -> Int {
        if item == nil {
            return catalogRootNodes.count
        }
        guard let node = item as? CatalogNode else { return 0 }
        return node.children.count
    }

    func outlineView(_ outlineView: NSOutlineView, child index: Int, ofItem item: Any?) -> Any {
        if item == nil {
            return catalogRootNodes[index]
        }
        guard let node = item as? CatalogNode else { return catalogRootNodes[index] }
        return node.children[index]
    }

    func outlineView(_ outlineView: NSOutlineView, isItemExpandable item: Any) -> Bool {
        guard let node = item as? CatalogNode else { return false }
        return !node.children.isEmpty
    }

    func outlineView(_ outlineView: NSOutlineView, viewFor tableColumn: NSTableColumn?, item: Any) -> NSView? {
        guard let node = item as? CatalogNode else { return nil }

        let identifier = NSUserInterfaceItemIdentifier("catalogCell")
        var cell = outlineView.makeView(withIdentifier: identifier, owner: self) as? NSTableCellView
        if cell == nil {
            cell = NSTableCellView()
            cell?.identifier = identifier

            let textField = NSTextField(labelWithString: "")
            textField.translatesAutoresizingMaskIntoConstraints = false
            textField.font = NSFont.systemFont(ofSize: 13)
            textField.lineBreakMode = .byTruncatingTail
            cell?.addSubview(textField)
            cell?.textField = textField

            NSLayoutConstraint.activate([
                textField.leadingAnchor.constraint(equalTo: cell!.leadingAnchor, constant: 20),
                textField.trailingAnchor.constraint(equalTo: cell!.trailingAnchor, constant: -4),
                textField.centerYAnchor.constraint(equalTo: cell!.centerYAnchor),
            ])
        }

        // 设置图标 + 标题
        var displayTitle = ""
        if let icon = node.icon {
            displayTitle = "\(icon) "
        }
        displayTitle += node.title
        cell?.textField?.stringValue = displayTitle

        return cell
    }

    func outlineViewSelectionDidChange(_ notification: Notification) {
        let selectedRow = leftOutlineView.selectedRow
        guard selectedRow >= 0,
              let node = leftOutlineView.item(atRow: selectedRow) as? CatalogNode else { return }

        if node.isLeaf {
            // 叶子节点：执行分类浏览
            browseCategory(node: node)
        }
    }
}

// MARK: - NSTableViewDataSource / NSTableViewDelegate

extension MaterialBrowserPanel: NSTableViewDataSource, NSTableViewDelegate {

    func numberOfRows(in tableView: NSTableView) -> Int {
        return searchResults.count
    }

    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        guard row < searchResults.count else { return nil }
        let item = searchResults[row]

        let identifier = tableColumn?.identifier ?? NSUserInterfaceItemIdentifier("")
        var cell = tableView.makeView(withIdentifier: identifier, owner: self) as? NSTableCellView
        if cell == nil {
            cell = NSTableCellView()
            cell?.identifier = identifier

            let textField = NSTextField(labelWithString: "")
            textField.translatesAutoresizingMaskIntoConstraints = false
            textField.font = NSFont.systemFont(ofSize: 12)
            textField.lineBreakMode = .byTruncatingTail
            cell?.addSubview(textField)
            cell?.textField = textField

            NSLayoutConstraint.activate([
                textField.leadingAnchor.constraint(equalTo: cell!.leadingAnchor, constant: 4),
                textField.trailingAnchor.constraint(equalTo: cell!.trailingAnchor, constant: -4),
                textField.centerYAnchor.constraint(equalTo: cell!.centerYAnchor),
            ])
        }

        switch identifier.rawValue {
        case "iconCol":
            cell?.textField?.stringValue = item.type?.iconChar ?? "❓"
            cell?.textField?.font = NSFont.systemFont(ofSize: 14)

        case "nameCol":
            cell?.textField?.stringValue = item.name
            cell?.textField?.font = NSFont.systemFont(ofSize: 12)

        case "idCol":
            cell?.textField?.stringValue = item.code
            cell?.textField?.font = NSFont.monospacedDigitSystemFont(ofSize: 11, weight: .regular)
            cell?.textField?.textColor = .secondaryLabelColor

        default:
            cell?.textField?.stringValue = ""
        }

        return cell
    }

    func tableViewSelectionDidChange(_ notification: Notification) {
        let selectedRow = middleTableView.selectedRow
        guard selectedRow >= 0, selectedRow < searchResults.count else {
            // 清除详情
            if let favButton = rightDetailView.subviews.compactMap({ $0 as? NSButton }).first(where: { $0.title.contains("收藏") }) {
                favButton.title = "⭐ 收藏"
            }
            return
        }

        let item = searchResults[selectedRow]
        showDetail(for: item)

        // 更新收藏按钮状态
        let isFav = FavoriteManager.shared.contains(code: item.code)
        if let favButton = rightDetailView.subviews.compactMap({ $0 as? NSButton }).first(where: { $0.title.contains("收藏") }) {
            favButton.title = isFav ? "⭐ 已收藏" : "⭐ 收藏"
        }
    }

    // 右键菜单
    func tableView(_ tableView: NSTableView, menuForRows rows: IndexSet) -> NSMenu? {
        return setupContextMenu(for: tableView)
    }
}

// MARK: - 建议视图控制器

/// 用于搜索建议 Popover 的简易 ViewController
class SuggestionViewController: NSViewController {
    var suggestions: [String] = []
    var onSelect: ((String) -> Void)?

    private let tableView = NSTableView()
    private let scrollView = NSScrollView()

    override func loadView() {
        view = NSView(frame: NSRect(x: 0, y: 0, width: 280, height: min(CGFloat(suggestions.count) * 24 + 8, 240)))
    }

    override func viewDidLoad() {
        super.viewDidLoad()

        scrollView.translatesAutoresizingMaskIntoConstraints = false
        scrollView.hasVerticalScroller = true
        scrollView.autohidesScrollers = true
        scrollView.borderType = .noBorder
        view.addSubview(scrollView)

        tableView.dataSource = self
        tableView.delegate = self
        tableView.headerView = nil
        tableView.usesAlternatingRowBackgroundColors = false
        tableView.rowHeight = 24
        let col = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("suggestionCol"))
        col.isEditable = false
        tableView.addTableColumn(col)
        scrollView.documentView = tableView

        NSLayoutConstraint.activate([
            scrollView.topAnchor.constraint(equalTo: view.topAnchor),
            scrollView.bottomAnchor.constraint(equalTo: view.bottomAnchor),
            scrollView.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            scrollView.trailingAnchor.constraint(equalTo: view.trailingAnchor),
        ])

        tableView.reloadData()
    }
}

extension SuggestionViewController: NSTableViewDataSource, NSTableViewDelegate {
    func numberOfRows(in tableView: NSTableView) -> Int { suggestions.count }

    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        guard row < suggestions.count else { return nil }
        let identifier = NSUserInterfaceItemIdentifier("suggestionCell")
        var cell = tableView.makeView(withIdentifier: identifier, owner: self) as? NSTableCellView
        if cell == nil {
            cell = NSTableCellView()
            cell?.identifier = identifier
            let textField = NSTextField(labelWithString: "")
            textField.translatesAutoresizingMaskIntoConstraints = false
            textField.font = NSFont.systemFont(ofSize: 12)
            textField.lineBreakMode = .byTruncatingTail
            cell?.addSubview(textField)
            cell?.textField = textField
            NSLayoutConstraint.activate([
                textField.leadingAnchor.constraint(equalTo: cell!.leadingAnchor, constant: 8),
                textField.trailingAnchor.constraint(equalTo: cell!.trailingAnchor, constant: -4),
                textField.centerYAnchor.constraint(equalTo: cell!.centerYAnchor),
            ])
        }
        cell?.textField?.stringValue = "🔍 \(suggestions[row])"
        return cell
    }

    func tableViewSelectionDidChange(_ notification: Notification) {
        let row = tableView.selectedRow
        guard row >= 0, row < suggestions.count else { return }
        onSelect?(suggestions[row])
    }
}
