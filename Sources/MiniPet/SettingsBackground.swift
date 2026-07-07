import Cocoa

// MARK: - Map Tree Node

/// 地图树的节点数据模型
fileprivate class MapTreeNode {
    let icon: String
    let name: String
    let locationId: String
    var level: Int
    var isExpanded: Bool
    var hasChildren: Bool
    var isLeaf: Bool                // 叶子节点（没有子节点）
    var parent: MapTreeNode?
    var children: [MapTreeNode]

    init(icon: String, name: String, locationId: String, level: Int = 0,
         hasChildren: Bool = false, isLeaf: Bool = false) {
        self.icon = icon
        self.name = name
        self.locationId = locationId
        self.level = level
        self.isExpanded = false
        self.hasChildren = hasChildren
        self.isLeaf = isLeaf
        self.parent = nil
        self.children = []
    }
}

// MARK: - Map Tree Row View

/// 地图树中单行的视图
fileprivate class MapTreeRowView: NSView {
    let node: MapTreeNode
    let arrowLabel: NSTextField  = NSTextField(labelWithString: "")
    let iconLabel: NSTextField  = NSTextField(labelWithString: "")
    let nameLabel: NSTextField  = NSTextField(labelWithString: "")
    let locLabel: NSTextField   = NSTextField(labelWithString: "")

    var isSelected: Bool = false {
        didSet { updateAppearance() }
    }
    var isHovered: Bool = false {
        didSet { updateAppearance() }
    }
    var onClick: ((MapTreeRowView) -> Void)?
    var onToggle: ((MapTreeRowView) -> Void)?

    private var trackingArea: NSTrackingArea?

    init(node: MapTreeNode) {
        self.node = node
        super.init(frame: .zero)
        translatesAutoresizingMaskIntoConstraints = false
        wantsLayer = true
        setupViews()
        updateContent()
    }

    required init?(coder: NSCoder) { fatalError() }

    private func setupViews() {
        // Arrow
        arrowLabel.font = NSFont.systemFont(ofSize: 8)
        arrowLabel.textColor = ThemeColors.current.txtDim
        arrowLabel.alignment = .center
        arrowLabel.translatesAutoresizingMaskIntoConstraints = false

        // Icon
        iconLabel.font = NSFont.systemFont(ofSize: 11)
        iconLabel.alignment = .center
        iconLabel.translatesAutoresizingMaskIntoConstraints = false

        // Name
        nameLabel.font = NSFont.systemFont(ofSize: 10, weight: .medium)
        nameLabel.textColor = ThemeColors.current.txtSec
        nameLabel.lineBreakMode = .byTruncatingTail
        nameLabel.translatesAutoresizingMaskIntoConstraints = false

        // Location ID (right-aligned, monospace, dim)
        locLabel.font = NSFont.monospacedDigitSystemFont(ofSize: 8, weight: .regular)
        locLabel.textColor = ThemeColors.current.txtMute
        locLabel.alignment = .right
        locLabel.translatesAutoresizingMaskIntoConstraints = false

        addSubview(arrowLabel)
        addSubview(iconLabel)
        addSubview(nameLabel)
        addSubview(locLabel)

        let leftPad = CGFloat(node.level * 14 + 8)
        NSLayoutConstraint.activate([
            arrowLabel.leadingAnchor.constraint(equalTo: leadingAnchor, constant: leftPad),
            arrowLabel.centerYAnchor.constraint(equalTo: centerYAnchor),
            arrowLabel.widthAnchor.constraint(equalToConstant: 10),

            iconLabel.leadingAnchor.constraint(equalTo: arrowLabel.trailingAnchor, constant: 2),
            iconLabel.centerYAnchor.constraint(equalTo: centerYAnchor),
            iconLabel.widthAnchor.constraint(equalToConstant: 14),

            nameLabel.leadingAnchor.constraint(equalTo: iconLabel.trailingAnchor, constant: 2),
            nameLabel.centerYAnchor.constraint(equalTo: centerYAnchor),

            locLabel.leadingAnchor.constraint(greaterThanOrEqualTo: nameLabel.trailingAnchor, constant: 4),
            locLabel.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -6),
            locLabel.centerYAnchor.constraint(equalTo: centerYAnchor),
        ])
    }

    fileprivate func updateContent() {
        iconLabel.stringValue = node.icon
        nameLabel.stringValue = node.name
        locLabel.stringValue = node.locationId

        if node.hasChildren || !node.children.isEmpty {
            arrowLabel.stringValue = "▶"
            arrowLabel.isHidden = false
            updateArrowRotation()
        } else {
            arrowLabel.stringValue = ""
            arrowLabel.isHidden = true
        }
    }

    private func updateArrowRotation() {
        // 展开时旋转 90°
        if node.isExpanded {
            arrowLabel.wantsLayer = true
            arrowLabel.layer?.setAffineTransform(CGAffineTransform(rotationAngle: CGFloat.pi / 2))
        } else {
            arrowLabel.layer?.setAffineTransform(.identity)
        }
    }

    func updateAppearance() {
        if isSelected {
            layer?.backgroundColor = ThemeColors.current.liSel.cgColor
            nameLabel.textColor = ThemeColors.current.ntAct
            iconLabel.textColor = ThemeColors.current.ntAct
        } else {
            layer?.backgroundColor = isHovered
                ? NSColor(calibratedWhite: 1.0, alpha: 0.03).cgColor
                : NSColor.clear.cgColor
            nameLabel.textColor = node.isLeaf ? ThemeColors.current.txtDim : ThemeColors.current.txtSec
            iconLabel.textColor = node.isLeaf ? ThemeColors.current.txtDim : ThemeColors.current.txtSec
        }
        arrowLabel.textColor = ThemeColors.current.txtDim
        locLabel.textColor = ThemeColors.current.txtMute
        layer?.cornerRadius = 4
    }

    func refreshTheme() {
        updateAppearance()
        updateContent()
    }

    // MARK: - Mouse Events

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let ta = trackingArea { removeTrackingArea(ta) }
        trackingArea = NSTrackingArea(rect: bounds, options: [.mouseEnteredAndExited, .activeInActiveApp], owner: self, userInfo: nil)
        addTrackingArea(trackingArea!)
    }

    override func mouseEntered(with event: NSEvent) {
        isHovered = true
    }

    override func mouseExited(with event: NSEvent) {
        isHovered = false
    }

    override func mouseDown(with event: NSEvent) {
        if node.hasChildren || !node.children.isEmpty {
            onToggle?(self)
        } else {
            onClick?(self)
        }
    }
}

// MARK: - Map Preview View

/// 地图预览视图 — 绘制径向+线性渐变背景、宠物位置指示、地图标签
fileprivate class MapPreviewView: NSView {
    var mapLabel: String = "射手村 · 100000000" {
        didSet { labelField.stringValue = mapLabel }
    }

    private let labelField: NSTextField
    private let petBodyView: NSView
    private let petHeadView: NSView

    override init(frame: NSRect) {
        labelField = NSTextField(labelWithString: "")
        petBodyView = NSView()
        petHeadView = NSView()

        super.init(frame: frame)
        translatesAutoresizingMaskIntoConstraints = false
        wantsLayer = true
        layer?.cornerRadius = 8
        layer?.borderWidth = 1
        layer?.borderColor = ThemeColors.current.ntBrd.cgColor
        // minimum height = 160 (enforced by external constraints)

        setupSubviews()
    }

    required init?(coder: NSCoder) { fatalError() }

    private func setupSubviews() {
        // Pet body: 20x28 rounded rect, bottom center, blue gradient
        petBodyView.translatesAutoresizingMaskIntoConstraints = false
        petBodyView.wantsLayer = true
        petBodyView.layer?.cornerRadius = 6
        addSubview(petBodyView)

        // Pet head: 22x22 circle, above body
        petHeadView.translatesAutoresizingMaskIntoConstraints = false
        petHeadView.wantsLayer = true
        petHeadView.layer?.cornerRadius = 11
        addSubview(petHeadView)

        // Map label at bottom
        labelField.font = NSFont.systemFont(ofSize: 8)
        labelField.textColor = ThemeColors.current.txtMute
        labelField.alignment = .center
        labelField.translatesAutoresizingMaskIntoConstraints = false
        labelField.stringValue = mapLabel
        labelField.wantsLayer = true
        labelField.layer?.backgroundColor = NSColor(calibratedWhite: 0, alpha: 0.3).cgColor
        labelField.layer?.cornerRadius = 4
        addSubview(labelField)

        NSLayoutConstraint.activate([
            petBodyView.bottomAnchor.constraint(equalTo: bottomAnchor, constant: -24),
            petBodyView.centerXAnchor.constraint(equalTo: centerXAnchor),
            petBodyView.widthAnchor.constraint(equalToConstant: 20),
            petBodyView.heightAnchor.constraint(equalToConstant: 28),

            petHeadView.bottomAnchor.constraint(equalTo: petBodyView.topAnchor, constant: 2),
            petHeadView.centerXAnchor.constraint(equalTo: centerXAnchor),
            petHeadView.widthAnchor.constraint(equalToConstant: 22),
            petHeadView.heightAnchor.constraint(equalToConstant: 22),

            labelField.bottomAnchor.constraint(equalTo: bottomAnchor, constant: -6),
            labelField.centerXAnchor.constraint(equalTo: centerXAnchor),
        ])

        updatePetColors()
    }

    private func updatePetColors() {
        let actColor = ThemeColors.current.ntAct
        // Body gradient: #6B8DFF -> ntAct, opacity 0.5
        let bodyGrad = CAGradientLayer()
        bodyGrad.frame = CGRect(x: 0, y: 0, width: 20, height: 28)
        bodyGrad.colors = [
            NSColor(calibratedRed: 0.42, green: 0.553, blue: 1.0, alpha: 0.5).cgColor,
            actColor.withAlphaComponent(0.5).cgColor,
        ]
        bodyGrad.startPoint = CGPoint(x: 0, y: 0)
        bodyGrad.endPoint = CGPoint(x: 1, y: 1)
        bodyGrad.cornerRadius = 6
        petBodyView.layer?.sublayers?.forEach { $0.removeFromSuperlayer() }
        petBodyView.layer?.addSublayer(bodyGrad)

        // Head gradient: #7D9DFF -> ntAct, opacity 0.55
        let headGrad = CAGradientLayer()
        headGrad.frame = CGRect(x: 0, y: 0, width: 22, height: 22)
        headGrad.colors = [
            NSColor(calibratedRed: 0.49, green: 0.616, blue: 1.0, alpha: 0.55).cgColor,
            actColor.withAlphaComponent(0.55).cgColor,
        ]
        headGrad.startPoint = CGPoint(x: 0, y: 0)
        headGrad.endPoint = CGPoint(x: 1, y: 1)
        headGrad.cornerRadius = 11
        petHeadView.layer?.sublayers?.forEach { $0.removeFromSuperlayer() }
        petHeadView.layer?.addSublayer(headGrad)
    }

    override func draw(_ dirtyRect: NSRect) {
        super.draw(dirtyRect)

        guard let ctx = NSGraphicsContext.current?.cgContext else { return }

        // 1. Radial gradient (ellipse at 50% 80%, rgba(94,156,255,0.03) to transparent)
        let radialLocations: [CGFloat] = [0.0, 1.0]
        let radialColors = [
            NSColor(calibratedRed: 0.369, green: 0.612, blue: 1.0, alpha: 0.03).cgColor,
            NSColor.clear.cgColor,
        ]
        if let radialGrad = CGGradient(colorsSpace: CGColorSpaceCreateDeviceRGB(),
                                       colors: radialColors as CFArray,
                                       locations: radialLocations) {
            let centerX = bounds.width * 0.5
            let centerY = bounds.height * 0.8
            // Ellipse: 60% of width, 40% of height as starting radius
            let startRadius: CGFloat = 0
            let endRadius = max(bounds.width * 0.6, bounds.height * 0.4)
            ctx.drawRadialGradient(radialGrad,
                                   startCenter: CGPoint(x: centerX, y: centerY),
                                   startRadius: startRadius,
                                   endCenter: CGPoint(x: centerX, y: centerY),
                                   endRadius: endRadius,
                                   options: [])
        }

        // 2. Linear gradient: 180deg, rgba(30,30,50,0.9) -> rgba(40,80,50,0.6) -> rgba(60,100,60,0.4)
        let linearLocations: [CGFloat] = [0.0, 0.4, 1.0]
        let linearColors = [
            NSColor(calibratedRed: 0.118, green: 0.118, blue: 0.196, alpha: 0.9).cgColor,
            NSColor(calibratedRed: 0.157, green: 0.314, blue: 0.196, alpha: 0.6).cgColor,
            NSColor(calibratedRed: 0.235, green: 0.392, blue: 0.235, alpha: 0.4).cgColor,
        ]
        if let linearGrad = CGGradient(colorsSpace: CGColorSpaceCreateDeviceRGB(),
                                        colors: linearColors as CFArray,
                                        locations: linearLocations) {
            ctx.drawLinearGradient(linearGrad,
                                   start: CGPoint(x: 0, y: bounds.height),
                                   end: CGPoint(x: 0, y: 0),
                                   options: [])
        }
    }

    func refreshTheme() {
        layer?.borderColor = ThemeColors.current.ntBrd.cgColor
        labelField.textColor = ThemeColors.current.txtMute
        labelField.layer?.backgroundColor = NSColor(calibratedWhite: 0, alpha: 0.3).cgColor
        updatePetColors()
        needsDisplay = true
    }
}

// MARK: - OBJ List Row View

/// 对象列表中单行的视图
fileprivate class ObjRowView: NSView {
    let iconLabel: NSTextField   = NSTextField(labelWithString: "")
    let nameLabel: NSTextField   = NSTextField(labelWithString: "")
    let idLabel: NSTextField     = NSTextField(labelWithString: "")
    let deleteBtn: NSButton      = NSButton()

    var onDelete: (() -> Void)?

    override init(frame: NSRect) {
        super.init(frame: frame)
        translatesAutoresizingMaskIntoConstraints = false
        wantsLayer = true
        layer?.cornerRadius = 4
        layer?.borderWidth = 1
        layer?.borderColor = NSColor(calibratedWhite: 1.0, alpha: 0.03).cgColor
        layer?.backgroundColor = NSColor(calibratedWhite: 1.0, alpha: 0.015).cgColor

        iconLabel.font = NSFont.systemFont(ofSize: 16)
        iconLabel.alignment = .center
        iconLabel.translatesAutoresizingMaskIntoConstraints = false

        nameLabel.font = NSFont.systemFont(ofSize: 9)
        nameLabel.textColor = NSColor(calibratedWhite: 1.0, alpha: 0.6)
        nameLabel.lineBreakMode = .byTruncatingTail
        nameLabel.translatesAutoresizingMaskIntoConstraints = false

        idLabel.font = NSFont.monospacedDigitSystemFont(ofSize: 8, weight: .regular)
        idLabel.textColor = ThemeColors.current.txtMute
        idLabel.translatesAutoresizingMaskIntoConstraints = false

        deleteBtn.title = "✕"
        deleteBtn.font = NSFont.systemFont(ofSize: 7)
        deleteBtn.isBordered = false
        deleteBtn.target = self
        deleteBtn.action = #selector(deleteTapped)
        deleteBtn.translatesAutoresizingMaskIntoConstraints = false
        let deleteAttr: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: 7),
            .foregroundColor: ThemeColors.current.txtMute,
        ]
        deleteBtn.attributedTitle = NSAttributedString(string: "✕", attributes: deleteAttr)

        addSubview(iconLabel)
        addSubview(nameLabel)
        addSubview(idLabel)
        addSubview(deleteBtn)

        NSLayoutConstraint.activate([
            iconLabel.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 6),
            iconLabel.centerYAnchor.constraint(equalTo: centerYAnchor),
            iconLabel.widthAnchor.constraint(equalToConstant: 20),

            nameLabel.leadingAnchor.constraint(equalTo: iconLabel.trailingAnchor, constant: 6),
            nameLabel.centerYAnchor.constraint(equalTo: centerYAnchor),

            idLabel.leadingAnchor.constraint(greaterThanOrEqualTo: nameLabel.trailingAnchor, constant: 6),
            idLabel.trailingAnchor.constraint(equalTo: deleteBtn.leadingAnchor, constant: -6),
            idLabel.centerYAnchor.constraint(equalTo: centerYAnchor),

            deleteBtn.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -6),
            deleteBtn.centerYAnchor.constraint(equalTo: centerYAnchor),
            deleteBtn.widthAnchor.constraint(equalToConstant: 14),
            deleteBtn.heightAnchor.constraint(equalToConstant: 14),
        ])

        // Hover tracking
        let ta = NSTrackingArea(rect: bounds, options: [.mouseEnteredAndExited, .activeInActiveApp], owner: self, userInfo: nil)
        addTrackingArea(ta)
    }

    required init?(coder: NSCoder) { fatalError() }

    func configure(icon: String, name: String, id: String) {
        iconLabel.stringValue = icon
        nameLabel.stringValue = name
        idLabel.stringValue = id
    }

    @objc private func deleteTapped() {
        onDelete?()
    }

    override func mouseEntered(with event: NSEvent) {
        layer?.backgroundColor = NSColor(calibratedWhite: 1.0, alpha: 0.03).cgColor
        layer?.borderColor = NSColor(calibratedRed: 0.369, green: 0.612, blue: 1.0, alpha: 0.15).cgColor
        // Delete button turns red on hover
        let attr: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: 7),
            .foregroundColor: NSColor(calibratedRed: 1.0, green: 0.271, blue: 0.227, alpha: 1.0),
        ]
        deleteBtn.attributedTitle = NSAttributedString(string: "✕", attributes: attr)
    }

    override func mouseExited(with event: NSEvent) {
        layer?.backgroundColor = NSColor(calibratedWhite: 1.0, alpha: 0.015).cgColor
        layer?.borderColor = NSColor(calibratedWhite: 1.0, alpha: 0.03).cgColor
        let attr: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: 7),
            .foregroundColor: ThemeColors.current.txtMute,
        ]
        deleteBtn.attributedTitle = NSAttributedString(string: "✕", attributes: attr)
    }

    func refreshTheme() {
        idLabel.textColor = ThemeColors.current.txtMute
    }
}

// MARK: - Background Settings Subview

/// 背景设置子视图 — 左侧地图树 + 右侧预览区 + OBJ 列表
class BackgroundSettingsSubview: NSView {

    // Left sidebar
    private let searchField: NSSearchField
    private let treeScrollView: NSScrollView
    private let treeStackView: NSStackView
    private var treeRows: [MapTreeRowView] = []

    // Divider
    private let divider: NSView

    // Right preview area
    private let mapPreview: MapPreviewView
    private let objListContainer: NSView

    // Add row
    private let popUpButton: NSPopUpButton
    private let addButton: NSButton

    // OBJ rows
    private var objRows: [ObjRowView] = []

    override init(frame: NSRect) {
        searchField = NSSearchField()
        treeScrollView = NSScrollView()
        treeStackView = NSStackView()
        divider = NSView()
        mapPreview = MapPreviewView()
        objListContainer = NSView()
        popUpButton = NSPopUpButton()
        addButton = NSButton()

        super.init(frame: frame)
        translatesAutoresizingMaskIntoConstraints = false
        wantsLayer = true
        setupViews()
        buildTreeData()
        populateObjList()
        NotificationCenter.default.addObserver(self,
                                               selector: #selector(themeChanged),
                                               name: .themeDidChange,
                                               object: nil)
    }

    required init?(coder: NSCoder) { fatalError() }

    deinit {
        NotificationCenter.default.removeObserver(self)
    }

    // MARK: - Setup

    private func setupViews() {
        // ── Left sidebar (240px) ──
        // Search field
        searchField.placeholderString = "搜索地图…"
        searchField.font = NSFont.systemFont(ofSize: 12)
        searchField.translatesAutoresizingMaskIntoConstraints = false
        searchField.bezelStyle = .roundedBezel  // approximation
        addSubview(searchField)

        // Map tree scroll view
        treeScrollView.hasVerticalScroller = true
        treeScrollView.drawsBackground = false
        treeScrollView.translatesAutoresizingMaskIntoConstraints = false
        treeScrollView.autohidesScrollers = true

        treeStackView.orientation = .vertical
        treeStackView.alignment = .leading
        treeStackView.spacing = 1
        treeStackView.edgeInsets = NSEdgeInsets(top: 2, left: 2, bottom: 2, right: 2)
        treeStackView.translatesAutoresizingMaskIntoConstraints = false

        // Document view for scroll
        let clipView = NSView()
        clipView.translatesAutoresizingMaskIntoConstraints = false
        clipView.addSubview(treeStackView)
        NSLayoutConstraint.activate([
            treeStackView.topAnchor.constraint(equalTo: clipView.topAnchor),
            treeStackView.leadingAnchor.constraint(equalTo: clipView.leadingAnchor),
            treeStackView.trailingAnchor.constraint(equalTo: clipView.trailingAnchor),
        ])
        treeScrollView.documentView = clipView
        addSubview(treeScrollView)

        // ── Divider (1px) ──
        divider.wantsLayer = true
        divider.layer?.backgroundColor = ThemeColors.current.ntBrd.cgColor
        divider.translatesAutoresizingMaskIntoConstraints = false
        addSubview(divider)

        // ── Right preview area ──
        addSubview(mapPreview)

        // OBJ list container
        objListContainer.translatesAutoresizingMaskIntoConstraints = false
        addSubview(objListContainer)

        // Pop up button for adding objects
        popUpButton.font = NSFont.systemFont(ofSize: 9)
        popUpButton.translatesAutoresizingMaskIntoConstraints = false
        popUpButton.addItems(withTitles: [
            "— 选择素材添加 —",
            "👾 路西德 (8880150)",
            "🌲 大树 (obj_3500)",
            "🏠 房子 (obj_1002)",
            "🪨 岩石 (obj_2010)",
            "🌺 花 (obj_0800)",
        ])

        // Add button
        addButton.title = "+ 添加"
        addButton.font = NSFont.systemFont(ofSize: 9, weight: .medium)
        addButton.bezelStyle = .smallSquare
        addButton.target = self
        addButton.action = #selector(addObjTapped)
        addButton.translatesAutoresizingMaskIntoConstraints = false

        objListContainer.addSubview(popUpButton)
        objListContainer.addSubview(addButton)

        // ── Layout ──
        NSLayoutConstraint.activate([
            // Search field
            searchField.topAnchor.constraint(equalTo: topAnchor, constant: 10),
            searchField.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 10),
            searchField.widthAnchor.constraint(equalToConstant: 214),
            searchField.heightAnchor.constraint(equalToConstant: 30),

            // Tree scroll
            treeScrollView.topAnchor.constraint(equalTo: searchField.bottomAnchor, constant: 8),
            treeScrollView.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 10),
            treeScrollView.widthAnchor.constraint(equalToConstant: 218),
            treeScrollView.bottomAnchor.constraint(equalTo: bottomAnchor, constant: -10),

            // Divider
            divider.topAnchor.constraint(equalTo: topAnchor),
            divider.leadingAnchor.constraint(equalTo: treeScrollView.trailingAnchor, constant: 6),
            divider.widthAnchor.constraint(equalToConstant: 1),
            divider.bottomAnchor.constraint(equalTo: bottomAnchor),

            // Map preview
            mapPreview.topAnchor.constraint(equalTo: topAnchor, constant: 14),
            mapPreview.leadingAnchor.constraint(equalTo: divider.trailingAnchor, constant: 14),
            mapPreview.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -14),
            mapPreview.heightAnchor.constraint(greaterThanOrEqualToConstant: 160),

            // OBJ list
            objListContainer.topAnchor.constraint(equalTo: mapPreview.bottomAnchor, constant: 6),
            objListContainer.leadingAnchor.constraint(equalTo: divider.trailingAnchor, constant: 14),
            objListContainer.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -14),
            objListContainer.bottomAnchor.constraint(equalTo: bottomAnchor, constant: -10),
        ])
    }

    // MARK: - Tree Data

    private func buildTreeData() {
        // Level 0
        let world = makeNode("🌍", "冒险岛世界", "world", level: 0, hasChildren: true)
        let leafre = makeNode("🌋", "神木村", "leafre", level: 0, hasChildren: true)
        let edelstein = makeNode("🏰", "埃德尔斯坦", "edelstein", level: 0, hasChildren: true)

        // Level 1 (children of world)
        let mapleIsland = makeNode("🏝️", "金银岛", "maple_island", level: 1, hasChildren: true)
        mapleIsland.parent = world; world.children.append(mapleIsland)

        // Level 1 (children of leafre)
        let dragonValley = makeNode("🏔️", "龙之峡谷", "dragon_valley", level: 1, hasChildren: true)
        dragonValley.parent = leafre; leafre.children.append(dragonValley)

        // Level 2 (children of mapleIsland)
        let henesysR = makeNode("🏘️", "射手村区域", "henesys_r", level: 2, hasChildren: true)
        henesysR.parent = mapleIsland; mapleIsland.children.append(henesysR)

        // Level 3 (leaves under henesysR)
        let henesys400 = makeNode("📍", "射手村", "100000000", level: 3, isLeaf: true)
        let henesys401 = makeNode("📍", "射手村东部", "100000001", level: 3, isLeaf: true)
        let gorgeEntry = makeNode("📍", "峡谷入口", "240010500", level: 3, isLeaf: true)
        henesys400.parent = henesysR; henesysR.children.append(henesys400)
        henesys401.parent = henesysR; henesysR.children.append(henesys401)
        gorgeEntry.parent = henesysR; henesysR.children.append(gorgeEntry)

        // Leaf under edelstein
        let wasteCity = makeNode("📍", "废城广场", "310000000", level: 1, isLeaf: true)
        wasteCity.parent = edelstein; edelstein.children.append(wasteCity)

        // Custom map (no children, special item)
        let customMap = makeNode("🖼️", "自定义地图", "", level: 0, hasChildren: false)

        let allNodes = [world, mapleIsland, henesysR, henesys400, henesys401, gorgeEntry,
                        leafre, dragonValley, edelstein, wasteCity, customMap]

        for node in allNodes {
            addTreeRow(node)
        }
    }

    private func makeNode(_ icon: String, _ name: String, _ locId: String,
                          level: Int, hasChildren: Bool = false, isLeaf: Bool = false) -> MapTreeNode {
        MapTreeNode(icon: icon, name: name, locationId: locId,
                    level: level, hasChildren: hasChildren, isLeaf: isLeaf)
    }

    private func addTreeRow(_ node: MapTreeNode) {
        let row = MapTreeRowView(node: node)
        row.onClick = { [weak self] r in
            self?.selectMap(r)
        }
        row.onToggle = { [weak self] r in
            self?.toggleTreeItem(r)
        }
        treeRows.append(row)
        treeStackView.addArrangedSubview(row)

        // Constrain row width to treeStackView
        NSLayoutConstraint.activate([
            row.widthAnchor.constraint(equalTo: treeStackView.widthAnchor, constant: -4),
        ])
    }

    // MARK: - Tree Actions

    fileprivate func toggleTreeItem(_ row: MapTreeRowView) {
        let node = row.node
        node.isExpanded = !node.isExpanded
        row.updateContent()

        // Show/hide child rows
        for child in node.children {
            if let childRow = treeRows.first(where: { $0.node === child }) {
                childRow.isHidden = !node.isExpanded
            }
        }
        // Recursively: if collapsing, hide all descendants; if expanding, only show direct children
        toggleDescendants(node, visible: node.isExpanded)
    }

    private func toggleDescendants(_ node: MapTreeNode, visible: Bool) {
        for child in node.children {
            if let childRow = treeRows.first(where: { $0.node === child }) {
                childRow.isHidden = !visible
                if !visible {
                    // Collapse children too
                    child.isExpanded = false
                    childRow.updateContent()
                    toggleDescendants(child, visible: false)
                }
            }
        }
    }

    fileprivate func selectMap(_ row: MapTreeRowView) {
        // Deselect all
        for r in treeRows { r.isSelected = false }
        row.isSelected = true

        let node = row.node
        let label = "\(node.name) · \(node.locationId)"
        mapPreview.mapLabel = label
    }

    // MARK: - OBJ List

    private func populateObjList() {
        // Clear existing rows
        for r in objRows { r.removeFromSuperview() }
        objRows.removeAll()

        // Default objects matching prototype
        addObjRow(icon: "👾", name: "路西德 (复制)", id: "8880150")
        addObjRow(icon: "🌲", name: "大树", id: "obj_3500")

        layoutObjList()
    }

    private func addObjRow(icon: String, name: String, id: String) {
        let row = ObjRowView()
        row.configure(icon: icon, name: name, id: id)
        row.onDelete = { [weak self] in
            self?.removeObjRow(row)
        }
        objRows.append(row)
        objListContainer.addSubview(row)
    }

    private func layoutObjList() {
        var prevAnchor = objListContainer.topAnchor

        for (i, row) in objRows.enumerated() {
            NSLayoutConstraint.activate([
                row.topAnchor.constraint(equalTo: i == 0 ? prevAnchor : objListContainer.topAnchor, constant: CGFloat(i) * 28),
                row.leadingAnchor.constraint(equalTo: objListContainer.leadingAnchor),
                row.trailingAnchor.constraint(equalTo: objListContainer.trailingAnchor),
                row.heightAnchor.constraint(equalToConstant: 26),
            ])
            if i == objRows.count - 1 {
                prevAnchor = row.bottomAnchor
            }
        }

        // Pop up button and add button
        NSLayoutConstraint.activate([
            popUpButton.topAnchor.constraint(equalTo: prevAnchor, constant: 6),
            popUpButton.leadingAnchor.constraint(equalTo: objListContainer.leadingAnchor),
            popUpButton.heightAnchor.constraint(equalToConstant: 22),

            addButton.topAnchor.constraint(equalTo: popUpButton.topAnchor),
            addButton.leadingAnchor.constraint(equalTo: popUpButton.trailingAnchor, constant: 4),
            addButton.trailingAnchor.constraint(equalTo: objListContainer.trailingAnchor),
            addButton.heightAnchor.constraint(equalToConstant: 22),
        ])
    }

    private func removeObjRow(_ row: ObjRowView) {
        row.removeFromSuperview()
        objRows.removeAll { $0 === row }
        layoutObjList()
    }

    @objc private func addObjTapped() {
        guard popUpButton.indexOfSelectedItem > 0 else { return }
        let title = popUpButton.titleOfSelectedItem ?? ""
        // Parse: "👾 路西德 (8880150)" → icon="👾", name="路西德", id="8880150"
        let icon = String(title.prefix(1))
        var rest = String(title.dropFirst(1).trimmingCharacters(in: .whitespaces))
        var idStr = ""
        if let parenStart = rest.lastIndex(of: "("), let parenEnd = rest.lastIndex(of: ")") {
            idStr = String(rest[rest.index(after: parenStart) ..< parenEnd])
            rest = String(rest[..<parenStart]).trimmingCharacters(in: .whitespaces)
        }
        addObjRow(icon: icon, name: rest, id: idStr)
        layoutObjList()
        popUpButton.selectItem(at: 0)
    }

    // MARK: - Theme

    @objc private func themeChanged() {
        divider.layer?.backgroundColor = ThemeColors.current.ntBrd.cgColor
        mapPreview.refreshTheme()
        for row in treeRows { row.refreshTheme() }
        for row in objRows { row.refreshTheme() }
    }
}
