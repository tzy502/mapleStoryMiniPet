import AppKit
import Foundation

// MARK: - Settings Panel (桌宠设置面板)

/// 桌宠设置面板的主窗口控制器。
/// 包含两个 tab：
///   1. 游戏内素材（怪物/NPC）
///   2. Player（纸娃娃角色）
///
/// 使用 NSTabView 分页，左侧列表 + 右侧详情/预览布局。
/// 风格对齐项目已有的 AppKit + NSPanel 模式。
///
/// 用法：
///   let sp = SettingsPanelController(petView: petView)
///   sp.showWindow(nil)
class SettingsPanelController: NSWindowController, NSCollectionViewDataSource, NSCollectionViewDelegate {

    // MARK: - 内部控件

    private let tabView = NSTabView()
    private let materialTab = NSTabViewItem(identifier: "material")
    private let playerTab = NSTabViewItem(identifier: "player")

    /// 弱引用 PetView（用于同步怪物列表等）
    weak var petView: PetView?
    /// 能引用 compositor 以便预览合成
    private let compositor = WzCharacterCompositor()

    // ---- Material Tab 控件 ----
    private let materialSearchField = NSSearchField()
    private let materialCollectionView: NSCollectionView = {
        let cv = NSCollectionView()
        cv.wantsLayer = true
        cv.layer?.backgroundColor = NSColor.controlBackgroundColor.cgColor
        return cv
    }()
    private let materialPreviewImageView = NSImageView()
    private let materialIdTextField = NSTextField()
    private let materialNameLabel = NSTextField.label(wrapping: false)
    private let materialFavoriteButton = NSButton(title: "☆ 收藏", target: nil, action: nil)
    private let materialMobRadio = NSButton(radioButtonWithTitle: "怪物", target: nil, action: nil)
    private let materialNpcRadio = NSButton(radioButtonWithTitle: "NPC", target: nil, action: nil)
    // ID 标签（不使用 NSTextField.label，直接创建）
    private let materialIdLabel: NSTextField = {
        let f = NSTextField()
        f.isEditable = false
        f.isBordered = false
        f.backgroundColor = .clear
        f.drawsBackground = false
        f.stringValue = "ID:"
        f.font = NSFont.systemFont(ofSize: 12)
        f.setContentHuggingPriority(.required, for: .horizontal)
        return f
    }()

    // ---- Player Tab 控件 ----
    private let playerPreviewContainer = NSView()
    private let playerPreviewImageView = NSImageView()
    private let playerGenderSegments = NSSegmentedControl(labels: ["♂ 男", "♀ 女"], trackingMode: .selectOne, target: nil, action: nil)
    private let playerHairField = NSTextField()
    private let playerFaceField = NSTextField()
    private let playerSkinField = NSTextField()
    private let playerNameField = NSTextField()
    private let playerMountCombo = NSComboBox()
    private let playerChairCombo = NSComboBox()
    private let randomButton = NSButton(title: "🎲 随机搭配", target: nil, action: nil)
    private let importButton = NSButton(title: "📥 导入", target: nil, action: nil)
    private let exportButton = NSButton(title: "📤 导出", target: nil, action: nil)
    private let equipColorvarCheckbox = NSButton(checkboxWithTitle: "启用 Colorvar", target: nil, action: nil)

    /// 8 个主要装备槽位的控件组
    private var equipSlotControls: [EquipCategory: EquipSlotControl] = [:]

    /// 当前外观配置
    private var currentAppearance = CharacterAppearance() {
        didSet { refreshPlayerPreview() }
    }

    /// 缓存的数据
    private var cachedEquipItems: [EquipCategory: [WzStringItem]] = [:]
    private var cachedMobItems: [WzStringItem] = []
    private var cachedNpcItems: [WzStringItem] = []
    private var cachedMountItems: [(id: String, name: String)] = []
    private var cachedChairItems: [(id: String, parentFolder: String, name: String)] = []
    private var allMaterialItems: [WzStringItem] = []
    private var filteredMaterialItems: [WzStringItem] = [] {
        didSet { materialCollectionView.reloadData() }
    }

    // MARK: - 初始化

    init(petView: PetView? = nil) {
        self.petView = petView
        let window = SettingsPanelController.makePanel()
        super.init(window: window)

        // 直接将 tabView 添加到 window.contentView，不使用 contentViewController
        setupTabs()
        setupMaterialTab()
        setupPlayerTab()
        tabView.selectTabViewItem(at: 0)

        // 加载数据
        Task { await loadAllData() }
    }

    required init?(coder: NSCoder) { nil }

    private static func makePanel() -> NSPanel {
        let panel = PetPanel(
            contentRect: NSRect(x: 0, y: 0, width: 960, height: 680),
            styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
            backing: .buffered,
            defer: false
        )
        panel.title = "MiniPet 设置"
        panel.minSize = NSSize(width: 800, height: 560)
        panel.isFloatingPanel = false
        return panel
    }

    /// MARK: - Tab 设置

    private func setupTabs() {
        guard let contentView = window?.contentView else { return }

        materialTab.label = "游戏内素材"
        materialTab.identifier = "material"
        playerTab.label = "Player (纸娃娃)"
        playerTab.identifier = "player"

        tabView.tabViewType = .topTabsBezelBorder
        tabView.addTabViewItem(materialTab)
        tabView.addTabViewItem(playerTab)
        tabView.translatesAutoresizingMaskIntoConstraints = false

        contentView.addSubview(tabView)
        NSLayoutConstraint.activate([
            tabView.topAnchor.constraint(equalTo: contentView.topAnchor),
            tabView.leadingAnchor.constraint(equalTo: contentView.leadingAnchor),
            tabView.trailingAnchor.constraint(equalTo: contentView.trailingAnchor),
            tabView.bottomAnchor.constraint(equalTo: contentView.bottomAnchor),
        ])
    }

    // MARK: - Tab 1: 游戏内素材（怪物/NPC）

    private func setupMaterialTab() {
        let view = NSView()
        materialTab.view = view

        // --- 左侧搜索 + 列表区域 ---
        let leftPanel = NSView()
        leftPanel.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(leftPanel)

        // 搜索栏
        materialSearchField.translatesAutoresizingMaskIntoConstraints = false
        materialSearchField.placeholderString = "搜索怪物/NPC ID 或名称…"
        materialSearchField.target = self
        materialSearchField.action = #selector(materialSearchAction)
        leftPanel.addSubview(materialSearchField)

        // 类型选择: 怪物 / NPC
        let typeStack = NSStackView(views: [materialMobRadio, materialNpcRadio])
        typeStack.orientation = .horizontal
        typeStack.spacing = 12
        typeStack.translatesAutoresizingMaskIntoConstraints = false
        leftPanel.addSubview(typeStack)

        materialMobRadio.target = self; materialMobRadio.action = #selector(materialTypeChanged)
        materialNpcRadio.target = self; materialNpcRadio.action = #selector(materialTypeChanged)
        materialMobRadio.state = .on

        // 收藏/最近使用分段按钮
        let favSeg = NSSegmentedControl(labels: ["收藏", "最近使用"], trackingMode: .selectOne, target: self, action: #selector(materialFavSegmentChanged))
        favSeg.translatesAutoresizingMaskIntoConstraints = false
        leftPanel.addSubview(favSeg)

        // 集合视图（素材网格）
        let flowLayout = NSCollectionViewFlowLayout()
        flowLayout.itemSize = NSSize(width: 120, height: 140)
        flowLayout.minimumInteritemSpacing = 8
        flowLayout.minimumLineSpacing = 8
        flowLayout.sectionInset = NSEdgeInsets(top: 4, left: 4, bottom: 4, right: 4)

        materialCollectionView.collectionViewLayout = flowLayout
        materialCollectionView.dataSource = self
        materialCollectionView.delegate = self
        materialCollectionView.register(MaterialGridItem.self, forItemWithIdentifier: MaterialGridItem.identifier)
        materialCollectionView.translatesAutoresizingMaskIntoConstraints = false
        materialCollectionView.isSelectable = true
        materialCollectionView.allowsMultipleSelection = false
        leftPanel.addSubview(materialCollectionView)

        // --- 右侧详情/预览区域 ---
        let rightPanel = NSView()
        rightPanel.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(rightPanel)

        // 预览图
        materialPreviewImageView.translatesAutoresizingMaskIntoConstraints = false
        materialPreviewImageView.imageScaling = .scaleProportionallyUpOrDown
        materialPreviewImageView.wantsLayer = true
        materialPreviewImageView.layer?.backgroundColor = NSColor.gridColor.withAlphaComponent(0.15).cgColor
        materialPreviewImageView.layer?.cornerRadius = 6
        rightPanel.addSubview(materialPreviewImageView)

        // ID 输入 + 名称显示
        materialIdTextField.placeholderString = "输入素材 ID"
        materialIdTextField.target = self
        materialIdTextField.action = #selector(materialIdEntered)

        let idRow = NSStackView(views: [
            materialIdLabel,
            materialIdTextField,
            materialNameLabel,
        ])
        idRow.orientation = .horizontal
        idRow.spacing = 6
        idRow.translatesAutoresizingMaskIntoConstraints = false
        rightPanel.addSubview(idRow)

        materialFavoriteButton.target = self
        materialFavoriteButton.action = #selector(materialFavoriteToggled)
        rightPanel.addSubview(materialFavoriteButton)

        // 布局约束
        NSLayoutConstraint.activate([
            // Left panel: 60% width
            leftPanel.topAnchor.constraint(equalTo: view.topAnchor),
            leftPanel.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            leftPanel.widthAnchor.constraint(equalTo: view.widthAnchor, multiplier: 0.6),
            leftPanel.bottomAnchor.constraint(equalTo: view.bottomAnchor),

            materialSearchField.topAnchor.constraint(equalTo: leftPanel.topAnchor, constant: 8),
            materialSearchField.leadingAnchor.constraint(equalTo: leftPanel.leadingAnchor, constant: 8),
            materialSearchField.trailingAnchor.constraint(equalTo: leftPanel.trailingAnchor, constant: -8),

            typeStack.topAnchor.constraint(equalTo: materialSearchField.bottomAnchor, constant: 6),
            typeStack.leadingAnchor.constraint(equalTo: leftPanel.leadingAnchor, constant: 8),

            favSeg.topAnchor.constraint(equalTo: typeStack.bottomAnchor, constant: 6),
            favSeg.leadingAnchor.constraint(equalTo: leftPanel.leadingAnchor, constant: 8),

            materialCollectionView.topAnchor.constraint(equalTo: favSeg.bottomAnchor, constant: 6),
            materialCollectionView.leadingAnchor.constraint(equalTo: leftPanel.leadingAnchor, constant: 8),
            materialCollectionView.trailingAnchor.constraint(equalTo: leftPanel.trailingAnchor, constant: -8),
            materialCollectionView.bottomAnchor.constraint(equalTo: leftPanel.bottomAnchor, constant: -8),

            // Right panel: 40% width
            rightPanel.topAnchor.constraint(equalTo: view.topAnchor),
            rightPanel.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            rightPanel.widthAnchor.constraint(equalTo: view.widthAnchor, multiplier: 0.4),
            rightPanel.bottomAnchor.constraint(equalTo: view.bottomAnchor),

            materialPreviewImageView.topAnchor.constraint(equalTo: rightPanel.topAnchor, constant: 8),
            materialPreviewImageView.centerXAnchor.constraint(equalTo: rightPanel.centerXAnchor),
            materialPreviewImageView.widthAnchor.constraint(equalToConstant: 200),
            materialPreviewImageView.heightAnchor.constraint(equalToConstant: 200),

            idRow.topAnchor.constraint(equalTo: materialPreviewImageView.bottomAnchor, constant: 12),
            idRow.leadingAnchor.constraint(equalTo: rightPanel.leadingAnchor, constant: 12),
            idRow.trailingAnchor.constraint(equalTo: rightPanel.trailingAnchor, constant: -12),

            materialIdTextField.widthAnchor.constraint(equalToConstant: 90),

            materialFavoriteButton.topAnchor.constraint(equalTo: idRow.bottomAnchor, constant: 8),
            materialFavoriteButton.centerXAnchor.constraint(equalTo: rightPanel.centerXAnchor),
        ])

        // 默认选择怪物
        currentMaterialType = .mob
    }

    /// 当前素材浏览类型
    private var currentMaterialType: MaterialType = .mob
    private var currentFavFilter: Int = 0 // 0=收藏, 1=最近
    private var selectedMaterialItem: WzStringItem?

    // MARK: - Tab 2: Player（纸娃娃角色）

    private func setupPlayerTab() {
        let scrollView = NSScrollView()
        scrollView.hasVerticalScroller = true
        scrollView.borderType = .noBorder
        scrollView.translatesAutoresizingMaskIntoConstraints = false

        let contentView = NSView()
        scrollView.documentView = contentView
        playerTab.view = scrollView

        // 使用 NSGridView 组织布局（左侧预览 + 右侧控件）
        // 初始化空 grid，后续用 addRow 追加行
        let dummyRow = NSView(), dummyCol = NSView()
        // 先添加一个 2 列的空行占位，后续会用 addRow 填充
        let mainGrid = NSGridView(views: [[dummyRow, dummyCol]])
        // 移除第一行占位——我们需要一个 clean 的 grid
        mainGrid.removeRow(at: 0)

        mainGrid.column(at: 0).width = 320
        mainGrid.xPlacement = .center
        mainGrid.translatesAutoresizingMaskIntoConstraints = false
        contentView.addSubview(mainGrid)

        // 第 0 行: 预览 + 控制面板
        let controlsView = buildPlayerControlsGrid()
        mainGrid.addRow(with: [playerPreviewContainer, controlsView])

        // 预览容器
        playerPreviewContainer.translatesAutoresizingMaskIntoConstraints = false
        playerPreviewContainer.wantsLayer = true
        playerPreviewContainer.layer?.backgroundColor = NSColor.gridColor.withAlphaComponent(0.08).cgColor
        playerPreviewContainer.layer?.cornerRadius = 8

        playerPreviewImageView.translatesAutoresizingMaskIntoConstraints = false
        playerPreviewImageView.imageScaling = .scaleProportionallyUpOrDown
        playerPreviewContainer.addSubview(playerPreviewImageView)

        NSLayoutConstraint.activate([
            playerPreviewImageView.centerXAnchor.constraint(equalTo: playerPreviewContainer.centerXAnchor),
            playerPreviewImageView.bottomAnchor.constraint(equalTo: playerPreviewContainer.bottomAnchor, constant: -20),
            playerPreviewImageView.widthAnchor.constraint(lessThanOrEqualToConstant: 280),
            playerPreviewImageView.heightAnchor.constraint(lessThanOrEqualToConstant: 320),

            mainGrid.topAnchor.constraint(equalTo: contentView.topAnchor, constant: 12),
            mainGrid.leadingAnchor.constraint(equalTo: contentView.leadingAnchor, constant: 12),
            mainGrid.trailingAnchor.constraint(equalTo: contentView.trailingAnchor, constant: -12),
            mainGrid.bottomAnchor.constraint(equalTo: contentView.bottomAnchor, constant: -12),

            playerPreviewContainer.heightAnchor.constraint(greaterThanOrEqualToConstant: 360),
        ])

        // 默认初始外观
        currentAppearance = CharacterAppearance(
            gender: 0, skin: "0", hair: "30000", face: "20000",
            cap: "1002401", cape: "1102088", coat: "1402000",
            longcoat: nil, pants: "1060006", shoes: "1072001",
            weapon: "1302000", shield: nil, glove: "1082002"
        )
    }

    /// 构建 Player 右侧控制面板（内嵌 NSGridView）
    private func buildPlayerControlsGrid() -> NSView {
        let container = NSView()
        container.translatesAutoresizingMaskIntoConstraints = false

        let grid = NSGridView(views: [[]])
        grid.removeRow(at: 0) // 清除初始空行

        grid.column(at: 0).width = 150
        grid.xPlacement = .leading
        grid.translatesAutoresizingMaskIntoConstraints = false
        container.addSubview(grid)

        // === 身体基础 ===
        let section1 = grid.addRow(with: [sectionLabel("🧍 身体基础")])
        section1.mergeCells(in: NSRange(location: 0, length: 2))

        // 性别
        playerGenderSegments.target = self
        playerGenderSegments.action = #selector(genderChanged)
        playerGenderSegments.selectedSegment = 0
        _ = grid.addRow(with: [fieldLabel("性别"), playerGenderSegments])

        // 发型
        let hairRow = buildEquipIdRow(field: playerHairField, action: #selector(hairIdEntered), browseAction: #selector(browseHair))
        _ = grid.addRow(with: [fieldLabel("发型"), hairRow])

        // 脸型
        let faceRow = buildEquipIdRow(field: playerFaceField, action: #selector(faceIdEntered), browseAction: #selector(browseFace))
        _ = grid.addRow(with: [fieldLabel("脸型"), faceRow])

        // 皮肤
        let skinRow = buildEquipIdRow(field: playerSkinField, action: #selector(skinIdEntered), browseAction: #selector(browseSkin))
        _ = grid.addRow(with: [fieldLabel("皮肤"), skinRow])

        // 角色名称
        playerNameField.placeholderString = "角色名称"
        playerNameField.target = self
        playerNameField.action = #selector(playerNameChanged)
        _ = grid.addRow(with: [fieldLabel("名称"), playerNameField])

        // === 装备槽位 ===
        let section2 = grid.addRow(with: [sectionLabel("🎽 装备槽位")])
        section2.mergeCells(in: NSRange(location: 0, length: 2))

        // 8 个主要装备槽
        let mainSlots: [(EquipCategory, String)] = [
            (.cap, "帽子 Cap"),
            (.cape, "披风 Cape"),
            (.coat, "上衣 Coat"),
            (.pants, "裤子 Pants"),
            (.shoes, "鞋子 Shoes"),
            (.weapon, "武器 Weapon"),
            (.shield, "盾牌 Shield"),
            (.glove, "手套 Glove"),
        ]
        for (cat, label) in mainSlots {
            let control = EquipSlotControl(category: cat, label: label)
            control.onIdChanged = { [weak self] id in self?.onEquipSlotChanged(category: cat, id: id) }
            control.onBrowse = { [weak self] in self?.browseEquipSlot(category: cat, control: control) }
            equipSlotControls[cat] = control
            control.translatesAutoresizingMaskIntoConstraints = false
            _ = grid.addRow(with: [fieldLabel(label), control])
        }

        // === 染色 ===
        let section3 = grid.addRow(with: [sectionLabel("🎨 染色")])
        section3.mergeCells(in: NSRange(location: 0, length: 2))

        equipColorvarCheckbox.target = self
        equipColorvarCheckbox.action = #selector(colorvarToggled)
        _ = grid.addRow(with: [fieldLabel("Colorvar"), equipColorvarCheckbox])

        // === 坐骑/椅子 ===
        let section4 = grid.addRow(with: [sectionLabel("🐴 坐骑 / 椅子")])
        section4.mergeCells(in: NSRange(location: 0, length: 2))

        playerMountCombo.placeholderString = "选择坐骑"
        playerMountCombo.isEditable = true
        playerMountCombo.target = self
        playerMountCombo.action = #selector(mountSelected)
        _ = grid.addRow(with: [fieldLabel("坐骑"), playerMountCombo])

        playerChairCombo.placeholderString = "选择椅子"
        playerChairCombo.isEditable = true
        playerChairCombo.target = self
        playerChairCombo.action = #selector(chairSelected)
        _ = grid.addRow(with: [fieldLabel("椅子"), playerChairCombo])

        // === 操作按钮 ===
        let buttonRow = NSStackView(views: [randomButton, importButton, exportButton])
        buttonRow.orientation = .horizontal
        buttonRow.spacing = 8
        buttonRow.distribution = .fillEqually

        randomButton.target = self; randomButton.action = #selector(randomizeAppearance)
        importButton.target = self; importButton.action = #selector(importAppearance)
        exportButton.target = self; exportButton.action = #selector(exportAppearance)

        _ = grid.addRow(with: [fieldLabel("操作"), buttonRow])

        NSLayoutConstraint.activate([
            grid.topAnchor.constraint(equalTo: container.topAnchor, constant: 4),
            grid.leadingAnchor.constraint(equalTo: container.leadingAnchor, constant: 8),
            grid.trailingAnchor.constraint(equalTo: container.trailingAnchor, constant: -8),
            grid.bottomAnchor.constraint(equalTo: container.bottomAnchor, constant: -4),
        ])

        return container
    }

    // MARK: - 数据加载

    private func loadAllData() async {
        let api = APIClient()

        // 并行加载各类数据
        async let equipItems = api.fetchEquipStrings(extraInfo: true)
        async let mobItems = api.fetchMobList()
        async let npcItems = api.fetchWzStrings(type: "npc")
        async let mountItems = api.fetchMountStrings()
        async let chairItems = api.fetchChairStrings()

        let (equips, mobs, npcs, mounts, chairs) = await (equipItems, mobItems, npcItems, mountItems, chairItems)

        // 按分类缓存装备
        var equipByCat: [EquipCategory: [WzStringItem]] = [:]
        for item in equips {
            if let catStr = item.category {
                let cat = EquipCategory.from(wzDirectory: catStr)
                if cat != .unknown { equipByCat[cat, default: []].append(item) }
            }
        }
        cachedEquipItems = equipByCat

        // 怪物
        cachedMobItems = mobs.map { WzStringItem(code: $0.code, name: $0.name) }

        // NPC
        cachedNpcItems = npcs

        // 坐骑
        cachedMountItems = mounts
        await MainActor.run {
            playerMountCombo.removeAllItems()
            for (id, name) in mounts {
                playerMountCombo.addItem(withObjectValue: "\(name) (\(id))")
            }
            playerMountCombo.numberOfVisibleItems = min(mounts.count, 20)
        }

        // 椅子
        cachedChairItems = chairs
        await MainActor.run {
            playerChairCombo.removeAllItems()
            for (id, _, name) in chairs {
                playerChairCombo.addItem(withObjectValue: "\(name) (\(id))")
            }
            playerChairCombo.numberOfVisibleItems = min(chairs.count, 20)
        }

        // 刷新素材列表
        await MainActor.run {
            applyMaterialTypeFilter()
        }
    }

    // MARK: - 素材 Tab Actions

    @objc private func materialSearchAction() {
        applyMaterialTypeFilter()
    }

    @objc private func materialTypeChanged() {
        currentMaterialType = materialMobRadio.state == .on ? .mob : .npc
        currentFavFilter = 0
        applyMaterialTypeFilter()
    }

    @objc private func materialFavSegmentChanged(_ sender: NSSegmentedControl) {
        currentFavFilter = sender.selectedSegment
        applyMaterialTypeFilter()
    }

    private func applyMaterialTypeFilter() {
        let searchText = materialSearchField.stringValue.lowercased()

        let sourceItems: [WzStringItem]
        switch currentMaterialType {
        case .mob: sourceItems = cachedMobItems
        case .npc: sourceItems = cachedNpcItems
        default:   sourceItems = []
        }

        if searchText.isEmpty {
            filteredMaterialItems = sourceItems
        } else {
            filteredMaterialItems = sourceItems.filter {
                $0.code.lowercased().contains(searchText) ||
                $0.name.lowercased().contains(searchText)
            }
        }

        // 限制显示数量（防止网格渲染过慢）
        if filteredMaterialItems.count > 500 {
            filteredMaterialItems = Array(filteredMaterialItems.prefix(500))
        }
    }

    @objc private func materialIdEntered() {
        let id = materialIdTextField.stringValue.trimmingCharacters(in: .whitespaces)
        guard !id.isEmpty else { return }
        loadMaterialPreview(id: id)
    }

    @objc private func materialFavoriteToggled() {
        guard let item = selectedMaterialItem else { return }
        let fm = FavoriteManager.shared
        if fm.contains(code: item.code) {
            // 移除收藏不通过公开 API，直接用 UserDefaults 重写
            var favs = UserDefaults.standard.array(forKey: "minipet_favorites") as? [[String: String]] ?? []
            favs.removeAll { $0["code"] == item.code }
            UserDefaults.standard.set(favs, forKey: "minipet_favorites")
            materialFavoriteButton.title = "☆ 收藏"
        } else {
            var favs = UserDefaults.standard.array(forKey: "minipet_favorites") as? [[String: String]] ?? []
            favs.append(["code": item.code, "name": item.name, "type": currentMaterialType.rawValue])
            UserDefaults.standard.set(favs, forKey: "minipet_favorites")
            materialFavoriteButton.title = "★ 已收藏"
        }
    }

    private func loadMaterialPreview(id: String) {
        // 查找名称
        let items = currentMaterialType == .mob ? cachedMobItems : cachedNpcItems
        if let match = items.first(where: { $0.code == id }) {
            materialNameLabel.stringValue = match.name
            selectedMaterialItem = match
            materialFavoriteButton.title = FavoriteManager.shared.contains(code: id) ? "★ 已收藏" : "☆ 收藏"
        } else {
            materialNameLabel.stringValue = "（未知）"
            selectedMaterialItem = nil
        }

        // 尝试加载预览图
        let wzPath = currentMaterialType == .mob
            ? "Mob/_Canvas/\(id.paddedId).img/stand/0"
            : "Npc/\(id.paddedId).img/stand/0"

        Task {
            let loader = WzImageLoader()
            if let data = await loader.fetchImage(wzPath: wzPath),
               let img = NSImage(data: data) {
                await MainActor.run { materialPreviewImageView.image = img }
            }
        }
    }

    // MARK: - Player Tab Actions

    @objc private func genderChanged() {
        currentAppearance.gender = playerGenderSegments.selectedSegment
    }

    @objc private func hairIdEntered() {
        let id = playerHairField.stringValue.trimmingCharacters(in: .whitespaces)
        guard !id.isEmpty else { return }
        currentAppearance.hair = id
    }

    @objc private func faceIdEntered() {
        let id = playerFaceField.stringValue.trimmingCharacters(in: .whitespaces)
        guard !id.isEmpty else { return }
        currentAppearance.face = id
    }

    @objc private func skinIdEntered() {
        let id = playerSkinField.stringValue.trimmingCharacters(in: .whitespaces)
        guard !id.isEmpty else { return }
        currentAppearance.skin = id
    }

    @objc private func browseHair() { browseEquipCategory("Hair") }
    @objc private func browseFace() { browseEquipCategory("Face") }
    @objc private func browseSkin() { browseEquipCategory("Skin") }

    private func browseEquipCategory(_ category: String) {
        let panel = EquipBrowserPanel(category: category, items: cachedEquipItems) { [weak self] selectedId in
            guard let self = self else { return }
            switch category {
            case "Hair":
                self.playerHairField.stringValue = selectedId
                self.currentAppearance.hair = selectedId
            case "Face":
                self.playerFaceField.stringValue = selectedId
                self.currentAppearance.face = selectedId
            case "Skin":
                self.playerSkinField.stringValue = selectedId
                self.currentAppearance.skin = selectedId
            default:
                break
            }
        }
        panel.makeKeyAndOrderFront(nil)
    }

    @objc private func playerNameChanged() {
        // 角色名称 — 存下来供导出使用
    }

    @objc private func mountSelected() {
        // 从 combo 文本中提取 ID
        let text = playerMountCombo.stringValue
        if let id = extractId(from: text) {
            currentAppearance.mount = id
        }
    }

    @objc private func chairSelected() {
        let text = playerChairCombo.stringValue
        if let id = extractId(from: text) {
            currentAppearance.chair = id
        }
    }

    @objc private func colorvarToggled() {
        currentAppearance.useColorvar = equipColorvarCheckbox.state == .on
    }

    @objc private func randomizeAppearance() {
        // 随机搭配：从缓存中随机选取各槽位的装备
        var newAppearance = CharacterAppearance()
        newAppearance.gender = Int.random(in: 0...1)

        // 随机基础部位
        let hairItems = cachedEquipItems[.hair] ?? []
        let faceItems = cachedEquipItems[.face] ?? []
        let skinItems = cachedEquipItems[.skin] ?? []

        if let randHair = hairItems.randomElement() { newAppearance.hair = randHair.code }
        if let randFace = faceItems.randomElement() { newAppearance.face = randFace.code }
        if let randSkin = skinItems.randomElement() { newAppearance.skin = randSkin.code }

        // 随机装备
        newAppearance.cap = cachedEquipItems[.cap]?.randomElement()?.code
        newAppearance.cape = cachedEquipItems[.cape]?.randomElement()?.code
        newAppearance.coat = cachedEquipItems[.coat]?.randomElement()?.code
        newAppearance.pants = cachedEquipItems[.pants]?.randomElement()?.code
        newAppearance.shoes = cachedEquipItems[.shoes]?.randomElement()?.code
        newAppearance.weapon = cachedEquipItems[.weapon]?.randomElement()?.code
        newAppearance.shield = cachedEquipItems[.shield]?.randomElement()?.code
        newAppearance.glove = cachedEquipItems[.glove]?.randomElement()?.code

        currentAppearance = newAppearance
        syncControlsToAppearance()
    }

    @objc private func importAppearance() {
        let openPanel = NSOpenPanel()
        openPanel.allowedContentTypes = [.json]
        openPanel.message = "选择搭配 JSON 文件"
        guard openPanel.runModal() == .OK, let url = openPanel.url else { return }

        do {
            let data = try Data(contentsOf: url)
            let appearance = try JSONDecoder().decode(CharacterAppearance.self, from: data)
            currentAppearance = appearance
            syncControlsToAppearance()
        } catch {
            let alert = NSAlert(error: error)
            alert.runModal()
        }
    }

    @objc private func exportAppearance() {
        let savePanel = NSSavePanel()
        savePanel.allowedContentTypes = [.json]
        savePanel.nameFieldStringValue = "appearance.json"
        guard savePanel.runModal() == .OK, let url = savePanel.url else { return }

        do {
            let data = try JSONEncoder().encode(currentAppearance)
            try data.write(to: url)
        } catch {
            let alert = NSAlert(error: error)
            alert.runModal()
        }
    }

    // MARK: - 装备槽位管理

    private func onEquipSlotChanged(category: EquipCategory, id: String?) {
        switch category {
        case .cap:     currentAppearance.cap = id
        case .cape:    currentAppearance.cape = id
        case .coat:    currentAppearance.coat = id
        case .pants:   currentAppearance.pants = id
        case .shoes:   currentAppearance.shoes = id
        case .weapon:  currentAppearance.weapon = id
        case .shield:  currentAppearance.shield = id
        case .glove:   currentAppearance.glove = id
        default: break
        }
    }

    private func browseEquipSlot(category: EquipCategory, control: EquipSlotControl) {
        let catName = category.wzDirectoryName ?? "\(category)"
        let panel = EquipBrowserPanel(category: catName, items: cachedEquipItems) { [weak self] selectedId in
            guard let self = self else { return }
            control.setId(selectedId)
            self.onEquipSlotChanged(category: category, id: selectedId)
        }
        panel.makeKeyAndOrderFront(nil)
    }

    // MARK: - 预览刷新

    /// 刷新 Player tab 中的角色预览图
    private func refreshPlayerPreview() {
        Task {
            let frames = await compositor.loadCharacterFrame(
                appearance: currentAppearance,
                action: "stand",
                frame: 0
            )
            guard !frames.isEmpty else { return }
            let composited = compositor.compositeCharacter(frames: frames)
            await MainActor.run {
                playerPreviewImageView.image = composited.image
            }
        }
    }

    /// 将外观同步到控件显示
    private func syncControlsToAppearance() {
        playerGenderSegments.selectedSegment = currentAppearance.gender
        playerHairField.stringValue = currentAppearance.hair
        playerFaceField.stringValue = currentAppearance.face
        playerSkinField.stringValue = currentAppearance.skin
        equipColorvarCheckbox.state = currentAppearance.useColorvar ? .on : .off

        equipSlotControls[.cap]?.setId(currentAppearance.cap)
        equipSlotControls[.cape]?.setId(currentAppearance.cape)
        equipSlotControls[.coat]?.setId(currentAppearance.coat)
        equipSlotControls[.pants]?.setId(currentAppearance.pants)
        equipSlotControls[.shoes]?.setId(currentAppearance.shoes)
        equipSlotControls[.weapon]?.setId(currentAppearance.weapon)
        equipSlotControls[.shield]?.setId(currentAppearance.shield)
        equipSlotControls[.glove]?.setId(currentAppearance.glove)

        // 坐骑
        if let mount = currentAppearance.mount,
           let item = cachedMountItems.first(where: { $0.id == mount }) {
            playerMountCombo.stringValue = "\(item.name) (\(item.id))"
        } else {
            playerMountCombo.stringValue = ""
        }

        // 椅子
        if let chair = currentAppearance.chair,
           let item = cachedChairItems.first(where: { $0.id == chair }) {
            playerChairCombo.stringValue = "\(item.name) (\(item.id))"
        } else {
            playerChairCombo.stringValue = ""
        }
    }

    // MARK: - NSCollectionViewDataSource

    func collectionView(_ collectionView: NSCollectionView, numberOfItemsInSection section: Int) -> Int {
        if collectionView == materialCollectionView {
            return filteredMaterialItems.count
        }
        return 0
    }

    func collectionView(_ collectionView: NSCollectionView, itemForRepresentedObjectAt indexPath: IndexPath) -> NSCollectionViewItem {
        let item = collectionView.makeItem(withIdentifier: MaterialGridItem.identifier, for: indexPath) as! MaterialGridItem
        let wzItem = filteredMaterialItems[indexPath.item]
        item.configure(with: wzItem, type: currentMaterialType)
        return item
    }

    // MARK: - NSCollectionViewDelegate

    func collectionView(_ collectionView: NSCollectionView, didSelectItemsAt indexPaths: Set<IndexPath>) {
        guard let idx = indexPaths.first, collectionView == materialCollectionView else { return }
        let wzItem = filteredMaterialItems[idx.item]
        selectedMaterialItem = wzItem
        materialNameLabel.stringValue = wzItem.name
        materialIdTextField.stringValue = wzItem.code
        materialFavoriteButton.title = FavoriteManager.shared.contains(code: wzItem.code) ? "★ 已收藏" : "☆ 收藏"
        loadMaterialPreview(id: wzItem.code)
    }

    // MARK: - 辅助方法

    private func extractId(from text: String) -> String? {
        // 从 "名称 (123456)" 格式中提取 ID
        if let range = text.range(of: #"\((\d+)\)"#, options: .regularExpression) {
            let id = text[range].trimmingCharacters(in: CharacterSet(charactersIn: "()"))
            return id.isEmpty ? nil : id
        }
        // 也可能是纯数字 ID
        let trimmed = text.trimmingCharacters(in: .whitespaces)
        if Int(trimmed) != nil { return trimmed }
        return nil
    }

    private func buildEquipIdRow(field: NSTextField, action: Selector, browseAction: Selector) -> NSView {
        let stack = NSStackView(views: [field, NSButton(title: "浏览…", target: self, action: browseAction)])
        stack.orientation = .horizontal
        stack.spacing = 4
        field.placeholderString = "输入 ID"
        field.target = self
        field.action = action
        field.widthAnchor.constraint(equalToConstant: 90).isActive = true
        return stack
    }
}

// MARK: - 辅助标签工厂

private func fieldLabel(_ text: String) -> NSTextField {
    let label = NSTextField.label(wrapping: false)
    label.stringValue = text
    label.font = NSFont.systemFont(ofSize: 12)
    return label
}

private func sectionLabel(_ text: String) -> NSTextField {
    let label = NSTextField.label(wrapping: false)
    label.stringValue = text
    label.font = NSFont.boldSystemFont(ofSize: 13)
    return label
}

extension NSTextField {
    /// 创建一个非编辑的标签 NSTextField。
    /// - Parameter wrapping: 是否允许换行
    static func label(wrapping: Bool) -> NSTextField {
        let f = NSTextField()
        f.isEditable = false
        f.isBordered = false
        f.backgroundColor = .clear
        f.drawsBackground = false
        f.lineBreakMode = wrapping ? .byWordWrapping : .byTruncatingTail
        f.setContentHuggingPriority(.required, for: .horizontal)
        f.setContentHuggingPriority(.required, for: .vertical)
        return f
    }
}

// MARK: - 单个装备槽位控件

/// 一个装备槽位的组合控件，包含 ID 输入框 + 浏览按钮 + 缩略图预览
class EquipSlotControl: NSView {
    let category: EquipCategory
    let idField = NSTextField()
    let browseButton = NSButton(title: "浏览", target: nil, action: nil)
    let thumbnailView = NSImageView()

    var onIdChanged: ((String?) -> Void)?
    var onBrowse: (() -> Void)?

    init(category: EquipCategory, label: String) {
        self.category = category
        super.init(frame: .zero)

        let row = NSStackView(views: [idField, browseButton, thumbnailView])
        row.orientation = .horizontal
        row.spacing = 4
        row.translatesAutoresizingMaskIntoConstraints = false
        addSubview(row)

        idField.placeholderString = "装备 ID"
        idField.target = self
        idField.action = #selector(idFieldAction)
        idField.widthAnchor.constraint(equalToConstant: 80).isActive = true

        thumbnailView.imageScaling = .scaleProportionallyUpOrDown
        thumbnailView.wantsLayer = true
        thumbnailView.layer?.backgroundColor = NSColor.gridColor.withAlphaComponent(0.12).cgColor
        thumbnailView.layer?.cornerRadius = 4
        thumbnailView.widthAnchor.constraint(equalToConstant: 36).isActive = true
        thumbnailView.heightAnchor.constraint(equalToConstant: 36).isActive = true

        browseButton.target = self
        browseButton.action = #selector(browseAction)

        NSLayoutConstraint.activate([
            row.topAnchor.constraint(equalTo: topAnchor),
            row.leadingAnchor.constraint(equalTo: leadingAnchor),
            row.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -4),
            row.bottomAnchor.constraint(equalTo: bottomAnchor),
        ])
    }

    required init?(coder: NSCoder) { nil }

    @objc private func idFieldAction() {
        let text = idField.stringValue.trimmingCharacters(in: .whitespaces)
        onIdChanged?(text.isEmpty ? nil : text)
        if !text.isEmpty { loadThumbnail(id: text) }
    }

    @objc private func browseAction() {
        onBrowse?()
    }

    func setId(_ id: String?) {
        idField.stringValue = id ?? ""
        if let id = id, !id.isEmpty { loadThumbnail(id: id) }
    }

    private func loadThumbnail(id: String) {
        guard let dir = category.wzDirectoryName else { return }
        let wzPath = "Character/\(dir)/\(id.paddedId).img/stand/0"
        Task {
            let loader = WzImageLoader()
            if let data = await loader.fetchImage(wzPath: wzPath),
               let img = NSImage(data: data) {
                await MainActor.run { thumbnailView.image = img }
            }
        }
    }
}

// MARK: - 装备浏览弹窗

/// 装备分类浏览弹窗，使用 NSCollectionView 展示该分类下所有装备
class EquipBrowserPanel: NSPanel, NSCollectionViewDataSource, NSCollectionViewDelegate {
    private let collectionView = NSCollectionView()
    private var items: [WzStringItem] = []
    private let onSelect: (String) -> Void

    init(category: String, items: [EquipCategory: [WzStringItem]], onSelect: @escaping (String) -> Void) {
        self.onSelect = onSelect

        // 查找该分类的装备
        let cat = EquipCategory.from(wzDirectory: category)
        self.items = items[cat] ?? []

        super.init(contentRect: NSRect(x: 0, y: 0, width: 500, height: 400),
                   styleMask: [.titled, .closable, .resizable],
                   backing: .buffered, defer: false)
        title = "浏览 \(category)"
        isFloatingPanel = false

        setupUI()
    }

    private func setupUI() {
        guard let contentView = contentView else { return }
        let scrollView = NSScrollView()
        scrollView.hasVerticalScroller = true
        scrollView.borderType = .noBorder
        scrollView.translatesAutoresizingMaskIntoConstraints = false
        contentView.addSubview(scrollView)

        let layout = NSCollectionViewFlowLayout()
        layout.itemSize = NSSize(width: 130, height: 50)
        layout.minimumInteritemSpacing = 4
        layout.minimumLineSpacing = 2
        layout.sectionInset = NSEdgeInsets(top: 4, left: 4, bottom: 4, right: 4)

        collectionView.collectionViewLayout = layout
        collectionView.dataSource = self
        collectionView.delegate = self
        collectionView.register(EquipBrowserItem.self, forItemWithIdentifier: EquipBrowserItem.identifier)
        collectionView.isSelectable = true
        collectionView.allowsMultipleSelection = false
        collectionView.translatesAutoresizingMaskIntoConstraints = false
        scrollView.documentView = collectionView

        NSLayoutConstraint.activate([
            scrollView.topAnchor.constraint(equalTo: contentView.topAnchor),
            scrollView.leadingAnchor.constraint(equalTo: contentView.leadingAnchor),
            scrollView.trailingAnchor.constraint(equalTo: contentView.trailingAnchor),
            scrollView.bottomAnchor.constraint(equalTo: contentView.bottomAnchor),
        ])

        collectionView.reloadData()
    }

    func collectionView(_ collectionView: NSCollectionView, numberOfItemsInSection section: Int) -> Int {
        items.count
    }

    func collectionView(_ collectionView: NSCollectionView, itemForRepresentedObjectAt indexPath: IndexPath) -> NSCollectionViewItem {
        let item = collectionView.makeItem(withIdentifier: EquipBrowserItem.identifier, for: indexPath) as! EquipBrowserItem
        let wzItem = items[indexPath.item]
        item.textField?.stringValue = "\(wzItem.code) - \(wzItem.name)"
        return item
    }

    func collectionView(_ collectionView: NSCollectionView, didSelectItemsAt indexPaths: Set<IndexPath>) {
        guard let idx = indexPaths.first else { return }
        let selected = items[idx.item]
        onSelect(selected.code)
        close()
    }
}

// MARK: - NSCollectionViewItem 子类

/// 素材网格项（怪物/NPC 缩略图）
class MaterialGridItem: NSCollectionViewItem {
    static let identifier = NSUserInterfaceItemIdentifier("MaterialGridItem")

    private let imageView_ = NSImageView()
    private let nameLabel = NSTextField.label(wrapping: true)

    override func loadView() {
        view = NSView()
        view.wantsLayer = true
        view.layer?.cornerRadius = 4

        imageView_.translatesAutoresizingMaskIntoConstraints = false
        imageView_.imageScaling = .scaleProportionallyUpOrDown
        view.addSubview(imageView_)

        nameLabel.translatesAutoresizingMaskIntoConstraints = false
        nameLabel.alignment = .center
        nameLabel.font = NSFont.systemFont(ofSize: 10)
        view.addSubview(nameLabel)

        NSLayoutConstraint.activate([
            imageView_.topAnchor.constraint(equalTo: view.topAnchor, constant: 4),
            imageView_.centerXAnchor.constraint(equalTo: view.centerXAnchor),
            imageView_.widthAnchor.constraint(equalToConstant: 96),
            imageView_.heightAnchor.constraint(equalToConstant: 96),

            nameLabel.topAnchor.constraint(equalTo: imageView_.bottomAnchor, constant: 2),
            nameLabel.leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: 2),
            nameLabel.trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -2),
            nameLabel.bottomAnchor.constraint(lessThanOrEqualTo: view.bottomAnchor, constant: -2),
        ])
    }

    func configure(with item: WzStringItem, type: MaterialType) {
        nameLabel.stringValue = "\(item.code)\n\(item.name)"
        nameLabel.toolTip = item.name

        // 加载缩略图
        let wzPath = type == .mob
            ? "Mob/_Canvas/\(item.code.paddedId).img/stand/0"
            : "Npc/\(item.code.paddedId).img/stand/0"

        Task {
            let loader = WzImageLoader()
            if let data = await loader.fetchImage(wzPath: wzPath),
               let img = NSImage(data: data) {
                await MainActor.run { imageView_.image = img }
            }
        }
    }
}

/// 装备浏览项
class EquipBrowserItem: NSCollectionViewItem {
    static let identifier = NSUserInterfaceItemIdentifier("EquipBrowserItem")

    override func loadView() {
        view = NSView()
        view.wantsLayer = true
        view.layer?.cornerRadius = 2

        let textField = NSTextField.label(wrapping: false)
        textField.translatesAutoresizingMaskIntoConstraints = false
        textField.font = NSFont.systemFont(ofSize: 11)
        view.addSubview(textField)
        self.textField = textField

        NSLayoutConstraint.activate([
            textField.centerYAnchor.constraint(equalTo: view.centerYAnchor),
            textField.leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: 4),
            textField.trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -4),
        ])
    }

    override var isSelected: Bool {
        didSet {
            view.layer?.backgroundColor = isSelected
                ? NSColor.selectedControlColor.cgColor
                : NSColor.clear.cgColor
        }
    }
}

// MARK: - APIClient 扩展（通用字符串查询）

extension APIClient {
    /// 通用 WZ 字符串查询，适用于 mob/npc/item/map/skill 等类型。
    /// 对应后端 POST /api/wz/data/query/string 接口。
    func fetchWzStrings(type: String) async -> [WzStringItem] {
        guard let base = await resolveBase() else { return [] }
        guard let url = URL(string: "\(base)/api/wz/data/query/string") else { return [] }
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let body: [String: String] = ["type": type, "name": ""]
        req.httpBody = try? JSONEncoder().encode(body)
        do {
            let (data, _) = try await session.data(for: req)
            guard let list = try? JSONSerialization.jsonObject(with: data) as? [[String: Any]] else { return [] }
            return list.compactMap { dict in
                guard let code = dict["code"] as? String ?? dict["id"] as? String else { return nil }
                let name = dict["name"] as? String ?? ""
                let category = dict["category"] as? String
                return WzStringItem(code: code, name: name, category: category)
            }
        } catch {
            logDebug("fetchWzStrings(\(type)) failed: \(error)")
            return []
        }
    }
}