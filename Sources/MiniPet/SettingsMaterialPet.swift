import AppKit
import Foundation

// MARK: - Material Canvas View

fileprivate class MaterialCanvasView: NSView {
    var bgMode: String = "transparent" {
        didSet { needsDisplay = true }
    }

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true
        observeThemeChanges()
    }

    required init?(coder: NSCoder) { fatalError() }

    override func draw(_ dirtyRect: NSRect) {
        guard let ctx = NSGraphicsContext.current?.cgContext else { return }
        let colors = ThemeColors.current

        switch bgMode {
        case "transparent":
            // 20px checkerboard using ntBrd at 25% pattern
            let size: CGFloat = 20
            ctx.setFillColor(colors.ntBrd.withAlphaComponent(0.25).cgColor)
            for row in 0..<Int(ceil(bounds.height / size)) {
                for col in 0..<Int(ceil(bounds.width / size)) {
                    if (row + col) % 2 == 0 {
                        ctx.fill(CGRect(x: CGFloat(col) * size, y: CGFloat(row) * size, width: size, height: size))
                    }
                }
            }

        case "grid":
            // 8px grid
            let gridSize: CGFloat = 8
            ctx.setStrokeColor(colors.ntBrd.withAlphaComponent(0.25).cgColor)
            ctx.setLineWidth(0.5)
            var x: CGFloat = 0
            while x < bounds.width {
                ctx.move(to: CGPoint(x: x, y: 0))
                ctx.addLine(to: CGPoint(x: x, y: bounds.height))
                x += gridSize
            }
            var y: CGFloat = 0
            while y < bounds.height {
                ctx.move(to: CGPoint(x: 0, y: y))
                ctx.addLine(to: CGPoint(x: bounds.width, y: y))
                y += gridSize
            }
            ctx.strokePath()

        case "henesy":
            // Linear gradient dark green — 弓箭手村
            let gColors = [
                hexColor("#2d5a2d").cgColor,
                hexColor("#4a8c4a").cgColor,
                hexColor("#6ba86b").cgColor,
            ]
            drawLinearGradient(ctx: ctx, colors: gColors)

        case "market":
            // Warm brown gradient — 市场
            let gColors = [
                hexColor("#4a3a20").cgColor,
                hexColor("#c4a45a").cgColor,
                hexColor("#e8d48a").cgColor,
            ]
            drawLinearGradient(ctx: ctx, colors: gColors)

        case "forest":
            // Forest green gradient — 森林
            let gColors = [
                hexColor("#1a2e1a").cgColor,
                hexColor("#2a5a2a").cgColor,
                hexColor("#1e3a1e").cgColor,
            ]
            drawLinearGradient(ctx: ctx, colors: gColors)

        default:
            break
        }

        // Center placeholder figure
        let figureCenterX = bounds.midX
        let figureCenterY = bounds.midY

        // Body: blue rounded rect
        let bodyRect = CGRect(x: figureCenterX - 12, y: figureCenterY - 16, width: 24, height: 32)
        let bodyPath = NSBezierPath(roundedRect: bodyRect, xRadius: 6, yRadius: 3)
        let bodyColor = NSColor(red: 0x6B/255, green: 0x8D/255, blue: 0xFF/255, alpha: 0.45)
        bodyColor.setFill()
        bodyPath.fill()

        // Head: blue circle
        let headRect = CGRect(x: figureCenterX - 13, y: figureCenterY + 8, width: 26, height: 26)
        let headPath = NSBezierPath(ovalIn: headRect)
        let headColor = NSColor(red: 0x7D/255, green: 0x9D/255, blue: 0xFF/255, alpha: 0.5)
        headColor.setFill()
        headPath.fill()

        // "WZ 素材展示" text below
        let text = "WZ 素材展示"
        let attrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: 10),
            .foregroundColor: colors.txtMute,
        ]
        let textSize = (text as NSString).size(withAttributes: attrs)
        let textRect = CGRect(
            x: figureCenterX - textSize.width / 2,
            y: figureCenterY - 16 - textSize.height - 4,
            width: textSize.width,
            height: textSize.height
        )
        (text as NSString).draw(at: textRect.origin, withAttributes: attrs)
    }

    private func drawLinearGradient(ctx: CGContext, colors: [CGColor]) {
        guard let gradient = CGGradient(
            colorsSpace: CGColorSpaceCreateDeviceRGB(),
            colors: colors as CFArray,
            locations: [0, 0.5, 1.0]
        ) else { return }
        ctx.drawLinearGradient(
            gradient,
            start: CGPoint(x: bounds.midX, y: bounds.minY),
            end: CGPoint(x: bounds.midX, y: bounds.maxY),
            options: []
        )
    }

    // MARK: - Theme observation

    private func observeThemeChanges() {
        NotificationCenter.default.addObserver(
            self, selector: #selector(themeChanged),
            name: .themeDidChange, object: nil
        )
    }

    @objc private func themeChanged() {
        needsDisplay = true
    }
}

// MARK: - MaterialItemView (single list row)

fileprivate class MaterialItemView: NSView {
    let iconLabel = NSTextField(labelWithString: "")
    let nameLabel = NSTextField(labelWithString: "")
    let codeLabel = NSTextField(labelWithString: "")
    let starButton = NSButton()

    var isSelected: Bool = false {
        didSet { needsDisplay = true }
    }
    var isFavorite: Bool = false {
        didSet {
            starButton.title = isFavorite ? "★" : "☆"
            starButton.contentTintColor = isFavorite
                ? NSColor(red: 1, green: 0.843, blue: 0, alpha: 1) // #FFD700
                : nil
        }
    }
    var onStarClick: (() -> Void)?
    var onClick: (() -> Void)?

    private var trackingArea: NSTrackingArea?
    private var isHovering = false

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true
        setupViews()
    }

    required init?(coder: NSCoder) { fatalError() }

    private func setupViews() {
        translatesAutoresizingMaskIntoConstraints = false

        iconLabel.font = NSFont.systemFont(ofSize: 13)
        iconLabel.isEditable = false
        iconLabel.isBordered = false
        iconLabel.backgroundColor = .clear
        iconLabel.translatesAutoresizingMaskIntoConstraints = false
        iconLabel.setContentHuggingPriority(.required, for: .horizontal)
        addSubview(iconLabel)

        nameLabel.font = NSFont.systemFont(ofSize: 11, weight: .medium)
        nameLabel.isEditable = false
        nameLabel.isBordered = false
        nameLabel.backgroundColor = .clear
        nameLabel.translatesAutoresizingMaskIntoConstraints = false
        addSubview(nameLabel)

        codeLabel.font = NSFont.monospacedSystemFont(ofSize: 9, weight: .regular)
        codeLabel.isEditable = false
        codeLabel.isBordered = false
        codeLabel.backgroundColor = .clear
        codeLabel.translatesAutoresizingMaskIntoConstraints = false
        codeLabel.setContentHuggingPriority(.defaultLow, for: .horizontal)
        addSubview(codeLabel)

        starButton.bezelStyle = .shadowlessSquare
        starButton.isBordered = false
        starButton.font = NSFont.systemFont(ofSize: 10)
        starButton.translatesAutoresizingMaskIntoConstraints = false
        starButton.setContentHuggingPriority(.required, for: .horizontal)
        starButton.target = self
        starButton.action = #selector(starClicked)
        addSubview(starButton)

        NSLayoutConstraint.activate([
            iconLabel.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 8),
            iconLabel.centerYAnchor.constraint(equalTo: centerYAnchor),
            iconLabel.widthAnchor.constraint(equalToConstant: 26),
            iconLabel.heightAnchor.constraint(equalToConstant: 26),

            nameLabel.leadingAnchor.constraint(equalTo: iconLabel.trailingAnchor, constant: 8),
            nameLabel.centerYAnchor.constraint(equalTo: centerYAnchor, constant: -4),

            codeLabel.leadingAnchor.constraint(equalTo: iconLabel.trailingAnchor, constant: 8),
            codeLabel.topAnchor.constraint(equalTo: nameLabel.bottomAnchor, constant: 1),

            starButton.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -8),
            starButton.centerYAnchor.constraint(equalTo: centerYAnchor),
            starButton.widthAnchor.constraint(equalToConstant: 20),
        ])

        // Click gesture
        let click = NSClickGestureRecognizer(target: self, action: #selector(viewClicked))
        addGestureRecognizer(click)
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let ta = trackingArea {
            removeTrackingArea(ta)
        }
        trackingArea = NSTrackingArea(
            rect: bounds,
            options: [.mouseEnteredAndExited, .activeInActiveApp],
            owner: self,
            userInfo: nil
        )
        addTrackingArea(trackingArea!)
    }

    override func mouseEntered(with event: NSEvent) {
        isHovering = true
        needsDisplay = true
    }

    override func mouseExited(with event: NSEvent) {
        isHovering = false
        needsDisplay = true
    }

    override func draw(_ dirtyRect: NSRect) {
        guard let ctx = NSGraphicsContext.current?.cgContext else { return }
        let colors = ThemeColors.current

        if isSelected {
            ctx.setFillColor(colors.liSel.withAlphaComponent(0.22).cgColor)
            ctx.fill(bounds)
        } else if isHovering {
            ctx.setFillColor(colors.liHov.cgColor)
            ctx.fill(bounds)
        }

        // Icon background
        let iconRect = CGRect(x: 8, y: (bounds.height - 26) / 2, width: 26, height: 26)
        let iconBg = NSBezierPath(roundedRect: iconRect, xRadius: 4, yRadius: 4)
        ctx.setFillColor(NSColor(white: 1, alpha: 0.03).cgColor)
        iconBg.fill()
    }

    @objc private func starClicked() {
        onStarClick?()
    }

    @objc private func viewClicked() {
        onClick?()
    }
}

// MARK: - Material Pet Settings Subview

class MaterialPetSettingsSubview: NSView {
    // ── Data ──
    private var allItems: [MaterialItem] = []
    private var filteredItems: [MaterialItem] = []
    private var currentSegment: Int = 0 // 0 = 怪物, 1 = NPC
    private var selectedCode: String = "8880150"
    private var isPlaying: Bool = false
    private var zoomValue: CGFloat = 1.0

    // ── UI Elements ──
    private let sidebarView = NSView()
    private let previewView = NSView()
    private let searchField = NSSearchField()
    private let segmentControl = NSSegmentedControl()
    private let browseButton = NSButton()
    private let listScrollView = NSScrollView()
    private let listStackView = NSStackView()

    private let canvasView = MaterialCanvasView()
    private let nameLabel = NSTextField(labelWithString: "")
    private let idTextField = NSTextField()
    private let favoriteButton = NSButton()
    private let playButton = NSButton()
    private let copyObjButton = NSButton()
    private let applyButton = NSButton()
    private let zoomSlider = NSSlider()
    private let zoomValueLabel = NSTextField(labelWithString: "100%")
    private let zoomLabel = NSTextField(labelWithString: "缩放")
    private let bgLabel = NSTextField(labelWithString: "背景设置")
    private var bgSwatchViews: [NSView] = []
    private var selectedBgIndex: Int = 0

    // ── Theme observation ──
    private var themeObserver: NSObjectProtocol?

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true
        setupMockData()
        setupViews()
        layoutViews()
        updateColors()
        observeThemeChanges()
        filterItems()
    }

    required init?(coder: NSCoder) { fatalError() }

    deinit {
        if let obs = themeObserver {
            NotificationCenter.default.removeObserver(obs)
        }
    }

    // MARK: - Data

    private func setupMockData() {
        let favorites = SettingsManager.current.materialFavorites
        allItems = [
            MaterialItem(code: "8880150", name: "路西德", type: "mob"),
            MaterialItem(code: "8860000", name: "品克缤", type: "mob"),
            MaterialItem(code: "9300573", name: "狐狸", type: "mob"),
            MaterialItem(code: "8600001", name: "龙", type: "mob"),
            MaterialItem(code: "2230000", name: "机器人", type: "mob"),
            MaterialItem(code: "9300001", name: "石像怪", type: "mob"),
            MaterialItem(code: "9300002", name: "幽灵", type: "mob"),
            MaterialItem(code: "2100100", name: "NPC 向导", type: "npc"),
            MaterialItem(code: "2100101", name: "商人", type: "npc"),
        ]
        for i in allItems.indices {
            allItems[i].isFavorite = favorites[allItems[i].code] ?? false
        }
    }

    private func sortedItems() -> [MaterialItem] {
        let typeFiltered = allItems.filter { item in
            let typeMatch: Bool
            if currentSegment == 0 {
                typeMatch = (item.type == "mob")
            } else {
                typeMatch = (item.type == "npc")
            }
            return typeMatch
        }

        let query = searchField.stringValue.trimmingCharacters(in: .whitespaces)
        let searched: [MaterialItem]
        if query.isEmpty {
            searched = typeFiltered
        } else {
            searched = typeFiltered.filter {
                $0.name.localizedCaseInsensitiveContains(query) || $0.code.contains(query)
            }
        }

        return searched.sorted { a, b in
            if a.isFavorite != b.isFavorite { return a.isFavorite }
            return a.name.localizedCaseInsensitiveCompare(b.name) == .orderedAscending
        }
    }

    private func filterItems() {
        filteredItems = sortedItems()
        rebuildList()
    }

    // MARK: - Views Setup

    private func setupViews() {
        // Sidebar
        sidebarView.wantsLayer = true
        sidebarView.translatesAutoresizingMaskIntoConstraints = false

        // Search field
        searchField.placeholderString = "过滤当前列表…"
        searchField.font = NSFont.systemFont(ofSize: 12)
        searchField.translatesAutoresizingMaskIntoConstraints = false
        searchField.target = self
        searchField.action = #selector(searchChanged)
        searchField.sendsWholeSearchString = false
        searchField.sendsSearchStringImmediately = true
        sidebarView.addSubview(searchField)

        // Segment control
        segmentControl.segmentCount = 2
        segmentControl.setLabel("怪物", forSegment: 0)
        segmentControl.setLabel("NPC", forSegment: 1)
        segmentControl.selectedSegment = 0
        segmentControl.target = self
        segmentControl.action = #selector(segChanged)
        segmentControl.translatesAutoresizingMaskIntoConstraints = false
        sidebarView.addSubview(segmentControl)

        // Browse button
        browseButton.title = "🔍 浏览素材库"
        browseButton.bezelStyle = .shadowlessSquare
        browseButton.isBordered = false
        browseButton.font = NSFont.systemFont(ofSize: 11)
        browseButton.target = self
        browseButton.action = #selector(openMaterialPicker)
        browseButton.translatesAutoresizingMaskIntoConstraints = false
        sidebarView.addSubview(browseButton)

        // List scroll view
        listScrollView.hasVerticalScroller = true
        listScrollView.borderType = .noBorder
        listScrollView.translatesAutoresizingMaskIntoConstraints = false
        sidebarView.addSubview(listScrollView)

        // List stack view
        listStackView.orientation = .vertical
        listStackView.spacing = 0
        listStackView.translatesAutoresizingMaskIntoConstraints = false
        listScrollView.documentView = listStackView

        // Preview
        previewView.wantsLayer = true
        previewView.translatesAutoresizingMaskIntoConstraints = false

        // Canvas
        canvasView.translatesAutoresizingMaskIntoConstraints = false
        previewView.addSubview(canvasView)

        // Parameter bar (pbar)
        nameLabel.font = NSFont.systemFont(ofSize: 12, weight: .medium)
        nameLabel.isEditable = false
        nameLabel.isBordered = false
        nameLabel.backgroundColor = .clear
        nameLabel.stringValue = "路西德"
        nameLabel.translatesAutoresizingMaskIntoConstraints = false
        previewView.addSubview(nameLabel)

        idTextField.font = NSFont.monospacedSystemFont(ofSize: 11, weight: .regular)
        idTextField.stringValue = "8880150"
        idTextField.bezelStyle = .roundedBezel
        idTextField.translatesAutoresizingMaskIntoConstraints = false
        previewView.addSubview(idTextField)

        favoriteButton.title = "★ 收藏"
        favoriteButton.bezelStyle = .shadowlessSquare
        favoriteButton.isBordered = false
        favoriteButton.font = NSFont.systemFont(ofSize: 10, weight: .medium)
        favoriteButton.target = self
        favoriteButton.action = #selector(toggleFavorite)
        favoriteButton.translatesAutoresizingMaskIntoConstraints = false
        previewView.addSubview(favoriteButton)

        playButton.title = "▶ 播放"
        playButton.bezelStyle = .shadowlessSquare
        playButton.isBordered = false
        playButton.font = NSFont.systemFont(ofSize: 10, weight: .medium)
        playButton.target = self
        playButton.action = #selector(togglePlay)
        playButton.translatesAutoresizingMaskIntoConstraints = false
        previewView.addSubview(playButton)

        copyObjButton.title = "📋 复制为 OBJ"
        copyObjButton.bezelStyle = .shadowlessSquare
        copyObjButton.isBordered = false
        copyObjButton.font = NSFont.systemFont(ofSize: 10, weight: .medium)
        copyObjButton.target = self
        copyObjButton.action = #selector(copyAsObj)
        copyObjButton.translatesAutoresizingMaskIntoConstraints = false
        previewView.addSubview(copyObjButton)

        applyButton.title = "确定应用"
        applyButton.bezelStyle = .shadowlessSquare
        applyButton.isBordered = false
        applyButton.font = NSFont.systemFont(ofSize: 10, weight: .medium)
        applyButton.target = self
        applyButton.action = #selector(applyMaterial)
        applyButton.translatesAutoresizingMaskIntoConstraints = false
        previewView.addSubview(applyButton)

        // Zoom row
        zoomLabel.font = NSFont.systemFont(ofSize: 9)
        zoomLabel.isEditable = false
        zoomLabel.isBordered = false
        zoomLabel.backgroundColor = .clear
        zoomLabel.translatesAutoresizingMaskIntoConstraints = false
        previewView.addSubview(zoomLabel)

        zoomSlider.minValue = 0.3
        zoomSlider.maxValue = 2.0
        zoomSlider.numberOfTickMarks = 0
        zoomSlider.allowsTickMarkValuesOnly = false
        zoomSlider.floatValue = 1.0
        zoomSlider.target = self
        zoomSlider.action = #selector(zoomChanged)
        zoomSlider.translatesAutoresizingMaskIntoConstraints = false
        previewView.addSubview(zoomSlider)

        zoomValueLabel.font = NSFont.monospacedSystemFont(ofSize: 9, weight: .regular)
        zoomValueLabel.isEditable = false
        zoomValueLabel.isBordered = false
        zoomValueLabel.backgroundColor = .clear
        zoomValueLabel.stringValue = "100%"
        zoomValueLabel.translatesAutoresizingMaskIntoConstraints = false
        previewView.addSubview(zoomValueLabel)

        // Background swatches row
        bgLabel.font = NSFont.systemFont(ofSize: 9)
        bgLabel.isEditable = false
        bgLabel.isBordered = false
        bgLabel.backgroundColor = .clear
        bgLabel.translatesAutoresizingMaskIntoConstraints = false
        previewView.addSubview(bgLabel)

        let bgConfigs: [(label: String, mode: String, color1: NSColor, color2: NSColor?)] = [
            ("透明", "transparent", ThemeColors.current.ntBrd, nil),
            ("网格", "grid", NSColor(white: 0.784, alpha: 0.06), nil),
            ("弓箭手村", "henesy", hexColor("#2d5a2d"), hexColor("#6ba86b")),
            ("市场", "market", hexColor("#4a3a20"), hexColor("#e8d48a")),
            ("森林", "forest", hexColor("#1a2e1a"), hexColor("#1e3a1e")),
        ]

        for (i, cfg) in bgConfigs.enumerated() {
            let container = NSView()
            container.wantsLayer = true
            container.translatesAutoresizingMaskIntoConstraints = false
            previewView.addSubview(container)

            let swatch = NSView()
            swatch.wantsLayer = true
            swatch.layer?.cornerRadius = 3
            swatch.translatesAutoresizingMaskIntoConstraints = false
            container.addSubview(swatch)

            if cfg.mode == "transparent" {
                swatch.layer?.backgroundColor = cfg.color1.cgColor
                swatch.layer?.borderWidth = 1
                swatch.layer?.borderColor = ThemeColors.current.ntBrd.cgColor
            } else if cfg.mode == "grid" {
                // Draw grid pattern via layer contents if needed; for simplicity, use background color
                swatch.layer?.backgroundColor = NSColor(white: 0.1, alpha: 0.3).cgColor
            } else if let c2 = cfg.color2 {
                // Gradient background
                let gradientLayer = CAGradientLayer()
                gradientLayer.colors = [cfg.color1.cgColor, c2.cgColor]
                gradientLayer.startPoint = CGPoint(x: 0.5, y: 0)
                gradientLayer.endPoint = CGPoint(x: 0.5, y: 1)
                gradientLayer.frame = CGRect(x: 0, y: 0, width: 40, height: 26)
                swatch.layer?.addSublayer(gradientLayer)
            }

            let bl = NSTextField(labelWithString: cfg.label)
            bl.font = NSFont.systemFont(ofSize: 7)
            bl.isEditable = false
            bl.isBordered = false
            bl.backgroundColor = .clear
            bl.alignment = .center
            bl.translatesAutoresizingMaskIntoConstraints = false
            container.addSubview(bl)

            NSLayoutConstraint.activate([
                swatch.topAnchor.constraint(equalTo: container.topAnchor),
                swatch.centerXAnchor.constraint(equalTo: container.centerXAnchor),
                swatch.widthAnchor.constraint(equalToConstant: 40),
                swatch.heightAnchor.constraint(equalToConstant: 26),

                bl.topAnchor.constraint(equalTo: swatch.bottomAnchor, constant: 1),
                bl.centerXAnchor.constraint(equalTo: container.centerXAnchor),
                bl.bottomAnchor.constraint(equalTo: container.bottomAnchor),
            ])

            let tap = NSClickGestureRecognizer(target: self, action: #selector(bgCardClicked(_:)))
            container.addGestureRecognizer(tap)
            container.identifier = NSUserInterfaceItemIdentifier("bg_\(i)")

            bgSwatchViews.append(container)
        }

        // Mark first swatch as selected
        updateBgSelection(index: 0)
    }

    private func layoutViews() {
        // Add divider between sidebar and preview
        let divider = NSView()
        divider.wantsLayer = true
        divider.layer?.backgroundColor = ThemeColors.current.ntBrd.cgColor
        divider.translatesAutoresizingMaskIntoConstraints = false
        addSubview(divider)
        addSubview(sidebarView)
        addSubview(previewView)

        NSLayoutConstraint.activate([
            sidebarView.topAnchor.constraint(equalTo: topAnchor),
            sidebarView.leadingAnchor.constraint(equalTo: leadingAnchor),
            sidebarView.bottomAnchor.constraint(equalTo: bottomAnchor),
            sidebarView.widthAnchor.constraint(equalToConstant: 240),

            divider.topAnchor.constraint(equalTo: topAnchor),
            divider.leadingAnchor.constraint(equalTo: sidebarView.trailingAnchor),
            divider.bottomAnchor.constraint(equalTo: bottomAnchor),
            divider.widthAnchor.constraint(equalToConstant: 1),

            previewView.topAnchor.constraint(equalTo: topAnchor),
            previewView.leadingAnchor.constraint(equalTo: divider.trailingAnchor),
            previewView.trailingAnchor.constraint(equalTo: trailingAnchor),
            previewView.bottomAnchor.constraint(equalTo: bottomAnchor),
        ])

        // Sidebar sub-layout
        NSLayoutConstraint.activate([
            searchField.topAnchor.constraint(equalTo: sidebarView.topAnchor, constant: 10),
            searchField.leadingAnchor.constraint(equalTo: sidebarView.leadingAnchor, constant: 10),
            searchField.trailingAnchor.constraint(equalTo: sidebarView.trailingAnchor, constant: -10),
            searchField.heightAnchor.constraint(equalToConstant: 30),

            segmentControl.topAnchor.constraint(equalTo: searchField.bottomAnchor, constant: 8),
            segmentControl.leadingAnchor.constraint(equalTo: sidebarView.leadingAnchor, constant: 10),
            segmentControl.trailingAnchor.constraint(equalTo: sidebarView.trailingAnchor, constant: -10),
            segmentControl.heightAnchor.constraint(equalToConstant: 22),

            browseButton.topAnchor.constraint(equalTo: segmentControl.bottomAnchor, constant: 8),
            browseButton.leadingAnchor.constraint(equalTo: sidebarView.leadingAnchor, constant: 10),
            browseButton.trailingAnchor.constraint(equalTo: sidebarView.trailingAnchor, constant: -10),
            browseButton.heightAnchor.constraint(equalToConstant: 26),

            listScrollView.topAnchor.constraint(equalTo: browseButton.bottomAnchor, constant: 6),
            listScrollView.leadingAnchor.constraint(equalTo: sidebarView.leadingAnchor),
            listScrollView.trailingAnchor.constraint(equalTo: sidebarView.trailingAnchor),
            listScrollView.bottomAnchor.constraint(equalTo: sidebarView.bottomAnchor),
        ])

        // Preview sub-layout
        NSLayoutConstraint.activate([
            canvasView.topAnchor.constraint(equalTo: previewView.topAnchor, constant: 14),
            canvasView.leadingAnchor.constraint(equalTo: previewView.leadingAnchor, constant: 14),
            canvasView.trailingAnchor.constraint(equalTo: previewView.trailingAnchor, constant: -14),

            nameLabel.topAnchor.constraint(equalTo: canvasView.bottomAnchor, constant: 8),
            nameLabel.leadingAnchor.constraint(equalTo: previewView.leadingAnchor, constant: 14),

            idTextField.centerYAnchor.constraint(equalTo: nameLabel.centerYAnchor),
            idTextField.leadingAnchor.constraint(equalTo: nameLabel.trailingAnchor, constant: 8),
            idTextField.widthAnchor.constraint(equalToConstant: 80),

            favoriteButton.centerYAnchor.constraint(equalTo: nameLabel.centerYAnchor),
            favoriteButton.leadingAnchor.constraint(equalTo: idTextField.trailingAnchor, constant: 8),

            playButton.centerYAnchor.constraint(equalTo: nameLabel.centerYAnchor),
            playButton.leadingAnchor.constraint(equalTo: favoriteButton.trailingAnchor, constant: 4),

            copyObjButton.centerYAnchor.constraint(equalTo: nameLabel.centerYAnchor),
            applyButton.centerYAnchor.constraint(equalTo: nameLabel.centerYAnchor),

            // Zoom row: zoomLabel - slider - zoomValueLabel
            zoomLabel.topAnchor.constraint(equalTo: nameLabel.bottomAnchor, constant: 4),
            zoomLabel.leadingAnchor.constraint(equalTo: previewView.leadingAnchor, constant: 14),

            zoomSlider.centerYAnchor.constraint(equalTo: zoomLabel.centerYAnchor),
            zoomSlider.leadingAnchor.constraint(equalTo: zoomLabel.trailingAnchor, constant: 6),
            zoomSlider.widthAnchor.constraint(equalToConstant: 80),

            zoomValueLabel.centerYAnchor.constraint(equalTo: zoomLabel.centerYAnchor),
            zoomValueLabel.leadingAnchor.constraint(equalTo: zoomSlider.trailingAnchor, constant: 6),
            zoomValueLabel.widthAnchor.constraint(equalToConstant: 32),

            // Background swatches row
            bgLabel.topAnchor.constraint(equalTo: zoomLabel.bottomAnchor, constant: 4),
            bgLabel.leadingAnchor.constraint(equalTo: previewView.leadingAnchor, constant: 14),
            bgLabel.bottomAnchor.constraint(lessThanOrEqualTo: previewView.bottomAnchor, constant: -14),
        ])

        // Layout copyObj and apply at trailing
        NSLayoutConstraint.activate([
            applyButton.trailingAnchor.constraint(equalTo: previewView.trailingAnchor, constant: -14),
            applyButton.centerYAnchor.constraint(equalTo: nameLabel.centerYAnchor),

            copyObjButton.trailingAnchor.constraint(equalTo: applyButton.leadingAnchor, constant: -4),
            copyObjButton.centerYAnchor.constraint(equalTo: nameLabel.centerYAnchor),
        ])

        // Layout bg swatches after bgLabel
        var prevBg: NSView?
        for swatch in bgSwatchViews {
            NSLayoutConstraint.activate([
                swatch.topAnchor.constraint(equalTo: bgLabel.topAnchor, constant: -2),
                swatch.widthAnchor.constraint(equalToConstant: 40),
            ])
            if let prev = prevBg {
                swatch.leadingAnchor.constraint(equalTo: prev.trailingAnchor, constant: 3).isActive = true
            } else {
                swatch.leadingAnchor.constraint(equalTo: bgLabel.trailingAnchor, constant: 6).isActive = true
            }
            prevBg = swatch
        }
    }

    // MARK: - List Rebuild

    private func rebuildList() {
        // Remove all arranged subviews
        while listStackView.arrangedSubviews.count > 0 {
            listStackView.arrangedSubviews.first?.removeFromSuperview()
        }

        let colors = ThemeColors.current
        var favorites: [MaterialItem] = []
        var recents: [MaterialItem] = []
        var others: [MaterialItem] = []

        for item in filteredItems {
            if item.isFavorite {
                favorites.append(item)
            } else {
                others.append(item)
            }
        }

        // Add section headers and items
        if !favorites.isEmpty {
            let header = makeSectionHeader("★ 收藏", color: NSColor(red: 1, green: 0.843, blue: 0, alpha: 1))
            listStackView.addArrangedSubview(header)
            for item in favorites {
                listStackView.addArrangedSubview(makeListItem(item))
            }
        }

        if !recents.isEmpty {
            let header = makeSectionHeader("最近使用", color: colors.txtDim)
            listStackView.addArrangedSubview(header)
            for item in recents {
                listStackView.addArrangedSubview(makeListItem(item))
            }
        }

        if !others.isEmpty {
            if !favorites.isEmpty {
                let header = makeSectionHeader("全部", color: colors.txtDim)
                listStackView.addArrangedSubview(header)
            }
            for item in others {
                listStackView.addArrangedSubview(makeListItem(item))
            }
        }
    }

    private func makeSectionHeader(_ title: String, color: NSColor) -> NSView {
        let view = NSView()
        view.translatesAutoresizingMaskIntoConstraints = false

        let label = NSTextField(labelWithString: title)
        label.font = NSFont.systemFont(ofSize: 10, weight: .semibold)
        label.textColor = color
        label.isEditable = false
        label.isBordered = false
        label.backgroundColor = .clear
        label.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(label)

        NSLayoutConstraint.activate([
            view.heightAnchor.constraint(equalToConstant: 22),
            label.leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: 12),
            label.centerYAnchor.constraint(equalTo: view.centerYAnchor),
            label.trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -4),
        ])

        return view
    }

    private func makeListItem(_ item: MaterialItem) -> NSView {
        let row = MaterialItemView(frame: .zero)
        row.iconLabel.stringValue = emojiForCode(item.code)
        row.nameLabel.stringValue = item.name
        row.codeLabel.stringValue = item.code
        row.isFavorite = item.isFavorite
        row.isSelected = (item.code == selectedCode)

        row.onClick = { [weak self] in
            self?.listItemClicked(item)
        }
        row.onStarClick = { [weak self] in
            self?.toggleFavoriteForItem(item, view: row)
        }

        return row
    }

    private func emojiForCode(_ code: String) -> String {
        switch code {
        case "8880150": return "👾"
        case "8860000": return "👹"
        case "9300573": return "🦊"
        case "8600001": return "🐉"
        case "2230000": return "🤖"
        case "9300001": return "🗿"
        case "9300002": return "👻"
        case "2100100": return "🧙"
        case "2100101": return "🧑‍💼"
        default: return "📦"
        }
    }

    // MARK: - Actions

    @objc private func toggleFavorite() {
        toggleFavoriteForCode(selectedCode)
        updateFavoriteButton()
    }

    private func toggleFavoriteForCode(_ code: String) {
        var favs = SettingsManager.current.materialFavorites
        let current = favs[code] ?? false
        favs[code] = !current
        SettingsManager.current.materialFavorites = favs
        SettingsManager.save(SettingsManager.current)

        // Update allItems
        if let idx = allItems.firstIndex(where: { $0.code == code }) {
            allItems[idx].isFavorite = !current
        }
        filterItems()
    }

    private func toggleFavoriteForItem(_ item: MaterialItem, view: MaterialItemView) {
        toggleFavoriteForCode(item.code)
        updateFavoriteButton()
    }

    private func updateFavoriteButton() {
        let isFav = SettingsManager.current.materialFavorites[selectedCode] ?? false
        favoriteButton.title = isFav ? "★ 收藏" : "☆ 收藏"
    }

    @objc private func togglePlay() {
        isPlaying.toggle()
        playButton.title = isPlaying ? "⏸ 暂停" : "▶ 播放"
    }

    @objc private func copyAsObj() {
        let name = nameLabel.stringValue
        let code = idTextField.stringValue
        logDebug("copyAsObj: \(code) \(name)")
    }

    @objc private func applyMaterial() {
        let code = idTextField.stringValue
        SettingsManager.current.mobId = code
        SettingsManager.current.entityType = currentSegment == 0 ? "mob" : "npc"
        SettingsManager.save(SettingsManager.current)
        NotificationCenter.default.post(name: Notification.Name("materialDidApply"), object: nil, userInfo: [
            "mobId": code,
        ])
    }

    @objc private func zoomChanged() {
        zoomValue = CGFloat(zoomSlider.floatValue)
        let pct = Int(round(zoomValue * 100))
        zoomValueLabel.stringValue = "\(pct)%"
    }

    @objc private func bgCardClicked(_ sender: NSClickGestureRecognizer) {
        guard let container = sender.view,
              let idStr = container.identifier?.rawValue,
              let idx = Int(idStr.replacingOccurrences(of: "bg_", with: "")) else { return }
        updateBgSelection(index: idx)
    }

    private func updateBgSelection(index: Int) {
        selectedBgIndex = index
        let modes = ["transparent", "grid", "henesy", "market", "forest"]
        guard index < modes.count else { return }
        canvasView.bgMode = modes[index]

        let colors = ThemeColors.current
        for (i, swatch) in bgSwatchViews.enumerated() {
            guard let swatchLayer = swatch.layer else { continue }
            // Find the inner swatch view (first subview)
            if let innerSwatch = swatch.subviews.first {
                innerSwatch.layer?.borderWidth = (i == index) ? 1 : 0
                innerSwatch.layer?.borderColor = (i == index) ? colors.ntAct.cgColor : nil
            }
            if i == index {
                swatchLayer.shadowColor = colors.ntAct.withAlphaComponent(0.2).cgColor
                swatchLayer.shadowOffset = .zero
                swatchLayer.shadowRadius = 2
                swatchLayer.shadowOpacity = 1
            } else {
                swatchLayer.shadowOpacity = 0
            }
        }
    }

    @objc private func searchChanged() {
        filterItems()
    }

    @objc private func segChanged() {
        currentSegment = segmentControl.selectedSegment
        filterItems()
    }

    private func listItemClicked(_ item: MaterialItem) {
        selectedCode = item.code
        nameLabel.stringValue = item.name
        idTextField.stringValue = item.code
        updateFavoriteButton()

        // Update selection highlighting
        for case let row as MaterialItemView in listStackView.arrangedSubviews {
            row.isSelected = (row.codeLabel.stringValue == item.code)
        }
    }

    @objc private func openMaterialPicker() {
        logDebug("openMaterialPicker")
    }

    // MARK: - Async API

    func fetchMaterialList() async {
        let client = APIClient()
        let mobs = await client.fetchMobList()
        // Update allItems with fetched data
    }

    func fetchMaterialName(code: String) async -> String? {
        let client = APIClient()
        return await client.fetchMobName(mobId: code)
    }

    // MARK: - Colors & Theme

    private func updateColors() {
        let colors = ThemeColors.current
        nameLabel.textColor = colors.txtPri.withAlphaComponent(0.7)
        idTextField.textColor = colors.txtPri.withAlphaComponent(0.7)
        zoomValueLabel.textColor = colors.txtMute

        for v in subviews {
            if let tf = v as? NSTextField, tf != nameLabel, tf != idTextField, tf != zoomValueLabel {
                // Only update dim labels
            }
        }
    }

    private func observeThemeChanges() {
        themeObserver = NotificationCenter.default.addObserver(
            forName: .themeDidChange, object: nil, queue: .main
        ) { [weak self] _ in
            self?.updateColors()
            self?.needsDisplay = true
            // Update divider color
            for v in self?.subviews ?? [] {
                if let dv = v as? NSView, dv.wantsLayer == true,
                   dv.layer?.backgroundColor != nil,
                   dv.frame.width < 2 {
                    dv.layer?.backgroundColor = ThemeColors.current.ntBrd.cgColor
                }
            }
        }
    }
}