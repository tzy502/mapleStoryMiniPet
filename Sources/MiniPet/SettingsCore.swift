import AppKit
import Foundation

// MARK: - Notification Names

extension Notification.Name {
    static let settingsDidClose = Notification.Name("settingsDidClose")
    static let themeDidChange = Notification.Name("themeDidChange")
}

// MARK: - Hex Color Helper

func hexColor(_ hex: String) -> NSColor {
    var s = hex.trimmingCharacters(in: .whitespacesAndNewlines)
    if s.hasPrefix("#") { s = String(s.dropFirst()) }
    guard s.count == 6 || s.count == 8,
          let val = UInt64(s, radix: 16) else {
        return .clear
    }
    if s.count == 6 {
        let r = CGFloat((val >> 16) & 0xFF) / 255
        let g = CGFloat((val >> 8) & 0xFF) / 255
        let b = CGFloat(val & 0xFF) / 255
        return NSColor(red: r, green: g, blue: b, alpha: 1)
    } else {
        let r = CGFloat((val >> 24) & 0xFF) / 255
        let g = CGFloat((val >> 16) & 0xFF) / 255
        let b = CGFloat((val >> 8) & 0xFF) / 255
        let a = CGFloat(val & 0xFF) / 255
        return NSColor(red: r, green: g, blue: b, alpha: a)
    }
}

// MARK: - MaterialItem

struct MaterialItem: Codable, Equatable {
    var code: String = ""
    var name: String = ""
    var type: String = ""
    var isFavorite: Bool = false
    var lastUsed: Date? = nil
}

// MARK: - ThemeColors

struct ThemeColors {
    // Dark
    static let dark = ThemeColors(
        bgBody: BodyBackground(
            gradientType: "radial",
            layers: [
                (color: NSColor(red: 0x1f/255, green: 0x23/255, blue: 0x35/255, alpha: 1), position: CGPoint(x: 0.2, y: 0.3), radius: 0.6),
                (color: NSColor(red: 0x1a/255, green: 0x1e/255, blue: 0x30/255, alpha: 1), position: CGPoint(x: 0.8, y: 0.2), radius: 0.5),
                (color: NSColor(red: 0x15/255, green: 0x18/255, blue: 0x24/255, alpha: 1), position: CGPoint(x: 0.5, y: 0.8), radius: 0.6),
                (color: NSColor(red: 0x0d/255, green: 0x0f/255, blue: 0x16/255, alpha: 1), position: nil, radius: nil),
                (color: NSColor(red: 0x14/255, green: 0x18/255, blue: 0x22/255, alpha: 1), position: nil, radius: nil),
                (color: NSColor(red: 0x18/255, green: 0x1c/255, blue: 0x2a/255, alpha: 1), position: nil, radius: nil),
                (color: NSColor(red: 0x0d/255, green: 0x0f/255, blue: 0x16/255, alpha: 1), position: nil, radius: nil),
            ]
        ),
        winBg: NSColor(red: 30/255, green: 30/255, blue: 38/255, alpha: 0.88),
        winBrd: NSColor(red: 80/255, green: 80/255, blue: 90/255, alpha: 0.6),
        txtPri: hexColor("#f0f0f4"),
        txtSec: hexColor("#a0a0b0"),
        txtDim: hexColor("#707080"),
        txtMute: NSColor(white: 1, alpha: 0.15),
        ntBg: NSColor(red: 28/255, green: 28/255, blue: 36/255, alpha: 0.6),
        ntBrd: NSColor(red: 120/255, green: 120/255, blue: 140/255, alpha: 0.3),
        ntAct: hexColor("#5E9CFF"),
        ptAct: NSColor(red: 94/255, green: 156/255, blue: 255/255, alpha: 0.15),
        ptHov: NSColor(white: 1, alpha: 0.04),
        liSel: NSColor(red: 94/255, green: 156/255, blue: 255/255, alpha: 0.22),
        liHov: NSColor(red: 94/255, green: 156/255, blue: 255/255, alpha: 0.12),
        inputBg: NSColor(red: 30/255, green: 30/255, blue: 36/255, alpha: 0.7),
        inputBrd: NSColor(red: 120/255, green: 120/255, blue: 140/255, alpha: 0.3),
        inputBrdFocus: hexColor("#5E9CFF"),
        btnScBg: NSColor(red: 60/255, green: 60/255, blue: 70/255, alpha: 0.5),
        btnScHov: NSColor(red: 94/255, green: 156/255, blue: 255/255, alpha: 0.15),
        btnPr: hexColor("#5E9CFF"),
        btnPrHov: hexColor("#7BB4FF")
    )

    // Light
    static let light = ThemeColors(
        bgBody: BodyBackground(
            gradientType: "radial",
            layers: [
                (color: NSColor(red: 0xe8/255, green: 0xec/255, blue: 0xf4/255, alpha: 1), position: CGPoint(x: 0.2, y: 0.3), radius: 0.6),
                (color: NSColor(red: 0xe2/255, green: 0xe6/255, blue: 0xf0/255, alpha: 1), position: CGPoint(x: 0.8, y: 0.2), radius: 0.5),
                (color: NSColor(red: 0xd4/255, green: 0xd8/255, blue: 0xe4/255, alpha: 1), position: CGPoint(x: 0.5, y: 0.8), radius: 0.6),
                (color: NSColor(red: 0xf0/255, green: 0xf2/255, blue: 0xf8/255, alpha: 1), position: nil, radius: nil),
                (color: NSColor(red: 0xe8/255, green: 0xeb/255, blue: 0xf2/255, alpha: 1), position: nil, radius: nil),
                (color: NSColor(red: 0xe0/255, green: 0xe4/255, blue: 0xee/255, alpha: 1), position: nil, radius: nil),
                (color: NSColor(red: 0xf0/255, green: 0xf2/255, blue: 0xf8/255, alpha: 1), position: nil, radius: nil),
            ]
        ),
        winBg: NSColor(red: 245/255, green: 247/255, blue: 252/255, alpha: 0.95),
        winBrd: NSColor(red: 200/255, green: 205/255, blue: 220/255, alpha: 0.7),
        txtPri: hexColor("#1a1e2e"),
        txtSec: hexColor("#5a5e70"),
        txtDim: hexColor("#8a8e9e"),
        txtMute: NSColor(white: 0, alpha: 0.12),
        ntBg: NSColor(red: 235/255, green: 238/255, blue: 245/255, alpha: 0.7),
        ntBrd: NSColor(red: 200/255, green: 205/255, blue: 220/255, alpha: 0.5),
        ntAct: hexColor("#4A7FD4"),
        ptAct: NSColor(red: 74/255, green: 127/255, blue: 212/255, alpha: 0.12),
        ptHov: NSColor(white: 0, alpha: 0.035),
        liSel: NSColor(red: 74/255, green: 127/255, blue: 212/255, alpha: 0.18),
        liHov: NSColor(red: 74/255, green: 127/255, blue: 212/255, alpha: 0.10),
        inputBg: NSColor(red: 245/255, green: 247/255, blue: 252/255, alpha: 0.85),
        inputBrd: NSColor(red: 200/255, green: 205/255, blue: 220/255, alpha: 0.6),
        inputBrdFocus: hexColor("#4A7FD4"),
        btnScBg: NSColor(red: 220/255, green: 225/255, blue: 240/255, alpha: 0.6),
        btnScHov: NSColor(red: 74/255, green: 127/255, blue: 212/255, alpha: 0.12),
        btnPr: hexColor("#4A7FD4"),
        btnPrHov: hexColor("#6B9DE8")
    )

    struct RadialGradientLayer {
        let color: NSColor
        let position: CGPoint?
        let radius: CGFloat?
    }

    struct BodyBackground {
        let gradientType: String
        let layers: [RadialGradientLayer]

        init(gradientType: String, layers: [(color: NSColor, position: CGPoint?, radius: CGFloat?)]) {
            self.gradientType = gradientType
            self.layers = layers.map { RadialGradientLayer(color: $0.color, position: $0.position, radius: $0.radius) }
        }
    }

    let bgBody: BodyBackground
    let winBg: NSColor
    let winBrd: NSColor
    let txtPri: NSColor
    let txtSec: NSColor
    let txtDim: NSColor
    let txtMute: NSColor
    let ntBg: NSColor
    let ntBrd: NSColor
    let ntAct: NSColor
    let ptAct: NSColor
    let ptHov: NSColor
    let liSel: NSColor
    let liHov: NSColor
    let inputBg: NSColor
    let inputBrd: NSColor
    let inputBrdFocus: NSColor
    let btnScBg: NSColor
    let btnScHov: NSColor
    let btnPr: NSColor
    let btnPrHov: NSColor

    static var isDark: Bool {
        return SettingsManager.current.theme != "light"
    }

    static var current: ThemeColors {
        return isDark ? ThemeColors.dark : ThemeColors.light
    }

    func drawBodyBackground(in rect: NSRect, ctx: CGContext) {
        ctx.saveGState()
        // Linear gradient base layers (last 4 layers in the CSS are linear gradient stops)
        let linearColors: [CGColor] = bgBody.layers.dropFirst(3).map { $0.color.cgColor }
        let linearLocations: [CGFloat] = [0, 0.4, 0.7, 1.0]
        if let gradient = CGGradient(colorsSpace: CGColorSpaceCreateDeviceRGB(), colors: linearColors as CFArray, locations: linearLocations) {
            ctx.drawLinearGradient(gradient, start: CGPoint(x: rect.minX, y: rect.minY), end: CGPoint(x: rect.maxX, y: rect.maxY), options: [])
        }

        // Radial gradient layers (first 3 layers)
        for layer in bgBody.layers.prefix(3) {
            guard let pos = layer.position, let radius = layer.radius else { continue }
            let center = CGPoint(x: rect.minX + rect.width * pos.x, y: rect.minY + rect.height * pos.y)
            let endRadius = max(rect.width, rect.height) * radius
            let innerColor = layer.color
            let outerColor = NSColor.clear
            let colors = [innerColor, outerColor].map { $0.cgColor }
            let locations: [CGFloat] = [0, 1]
            if let gradient = CGGradient(colorsSpace: CGColorSpaceCreateDeviceRGB(), colors: colors as CFArray, locations: locations) {
                ctx.drawRadialGradient(gradient, startCenter: center, startRadius: 0, endCenter: center, endRadius: endRadius, options: [])
            }
        }
        ctx.restoreGState()
    }
}

// MARK: - SettingsWindowFrameView

class SettingsWindowFrameView: NSVisualEffectView {
    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        material = .dark
        blendingMode = .withinWindow
        wantsLayer = true
        layer?.cornerRadius = 16
        layer?.masksToBounds = true
    }

    required init?(coder: NSCoder) { fatalError() }
}

// MARK: - SettingsBackgroundView

class SettingsBackgroundView: NSView {
    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true
    }

    required init?(coder: NSCoder) { fatalError() }

    override func draw(_ dirtyRect: NSRect) {
        guard let ctx = NSGraphicsContext.current?.cgContext else { return }
        let colors = ThemeColors.current
        colors.drawBodyBackground(in: bounds, ctx: ctx)
    }
}

// MARK: - SettingsTitleBar

class SettingsTitleBar: NSView {
    private enum ToggleState {
        case dark, light

        mutating func toggle() {
            self = (self == .dark) ? .light : .dark
        }
    }

    private let titleLabel = NSTextField(labelWithString: "")
    private let subtitleLabel = NSTextField(labelWithString: "")
    private let themeLabel = NSTextField(labelWithString: "")
    private let toggleContainer = NSView()
    private let toggleKnob = NSView()
    private var toggleDark = true
    private let borderLayer = CALayer()

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true
        setupViews()
        setupToggle()
        observeThemeChanges()
    }

    required init?(coder: NSCoder) { fatalError() }

    private func setupViews() {
        titleLabel.stringValue = "🐾 MiniPet 桌宠"
        titleLabel.font = NSFont.systemFont(ofSize: 14, weight: .semibold)
        titleLabel.isEditable = false
        titleLabel.isBordered = false
        titleLabel.backgroundColor = .clear
        titleLabel.translatesAutoresizingMaskIntoConstraints = false
        addSubview(titleLabel)

        subtitleLabel.stringValue = "— 设置中心"
        subtitleLabel.font = NSFont.systemFont(ofSize: 11, weight: .regular)
        subtitleLabel.isEditable = false
        subtitleLabel.isBordered = false
        subtitleLabel.backgroundColor = .clear
        subtitleLabel.translatesAutoresizingMaskIntoConstraints = false
        addSubview(subtitleLabel)

        themeLabel.stringValue = "暗黑"
        themeLabel.font = NSFont.systemFont(ofSize: 11, weight: .regular)
        themeLabel.isEditable = false
        themeLabel.isBordered = false
        themeLabel.backgroundColor = .clear
        themeLabel.translatesAutoresizingMaskIntoConstraints = false
        addSubview(themeLabel)

        toggleContainer.wantsLayer = true
        toggleContainer.translatesAutoresizingMaskIntoConstraints = false
        addSubview(toggleContainer)

        toggleKnob.wantsLayer = true
        toggleKnob.layer?.cornerRadius = 7
        toggleKnob.translatesAutoresizingMaskIntoConstraints = false
        toggleContainer.addSubview(toggleKnob)

        NSLayoutConstraint.activate([
            titleLabel.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 20),
            titleLabel.centerYAnchor.constraint(equalTo: centerYAnchor, constant: -1),

            subtitleLabel.leadingAnchor.constraint(equalTo: titleLabel.trailingAnchor, constant: 6),
            subtitleLabel.centerYAnchor.constraint(equalTo: titleLabel.centerYAnchor),

            themeLabel.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -20),
            themeLabel.centerYAnchor.constraint(equalTo: centerYAnchor),

            toggleContainer.trailingAnchor.constraint(equalTo: themeLabel.leadingAnchor, constant: -8),
            toggleContainer.centerYAnchor.constraint(equalTo: centerYAnchor),
            toggleContainer.widthAnchor.constraint(equalToConstant: 32),
            toggleContainer.heightAnchor.constraint(equalToConstant: 18),

            toggleKnob.widthAnchor.constraint(equalToConstant: 14),
            toggleKnob.heightAnchor.constraint(equalToConstant: 14),
            toggleKnob.centerYAnchor.constraint(equalTo: toggleContainer.centerYAnchor),
        ])
    }

    private func setupToggle() {
        toggleDark = ThemeColors.isDark
        applyColors()

        let tap = NSClickGestureRecognizer(target: self, action: #selector(toggleTheme))
        toggleContainer.addGestureRecognizer(tap)
    }

    private func observeThemeChanges() {
        NotificationCenter.default.addObserver(self, selector: #selector(themeChanged), name: .themeDidChange, object: nil)
    }

    @objc private func themeChanged() {
        toggleDark = ThemeColors.isDark
        applyColors()
    }

    private func applyColors() {
        let colors = ThemeColors.current
        titleLabel.textColor = colors.txtPri
        subtitleLabel.textColor = colors.txtDim
        themeLabel.textColor = colors.txtDim
        themeLabel.stringValue = toggleDark ? "暗黑" : "白天"

        toggleContainer.layer?.backgroundColor = toggleDark
            ? hexColor("#5E9CFF").cgColor
            : hexColor("#4A7FD4").cgColor
        toggleContainer.layer?.cornerRadius = 9
        toggleContainer.layer?.borderWidth = 0

        toggleKnob.layer?.backgroundColor = NSColor.white.cgColor

        // Update knob position
        toggleKnobLeadingConstraint?.constant = toggleDark ? 2 : 16
        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = 0.2
            ctx.allowsImplicitAnimation = true
            toggleContainer.layoutSubtreeIfNeeded()
        }

        // Bottom border
        borderLayer.backgroundColor = colors.ntBrd.cgColor
    }

    private var toggleKnobLeadingConstraint: NSLayoutConstraint?

    override func viewDidMoveToSuperview() {
        super.viewDidMoveToSuperview()
        guard superview != nil else { return }

        toggleKnobLeadingConstraint?.isActive = false
        toggleKnobLeadingConstraint = toggleKnob.leadingAnchor.constraint(equalTo: toggleContainer.leadingAnchor, constant: toggleDark ? 2 : 16)
        toggleKnobLeadingConstraint?.isActive = true

        // Setup bottom border
        borderLayer.frame = CGRect(x: 0, y: 0, width: bounds.width, height: 1)
        borderLayer.autoresizingMask = [.layerWidthSizable, .layerMaxYMargin]
        layer?.addSublayer(borderLayer)
    }

    @objc private func toggleTheme() {
        SettingsManager.current.theme = toggleDark ? "light" : "dark"
        SettingsManager.save(SettingsManager.current)
        toggleDark.toggle()
        applyColors()
        NotificationCenter.default.post(name: .themeDidChange, object: nil)

        // Update all window appearances
        NSApplication.shared.windows.forEach { win in
            win.appearance = toggleDark
                ? NSAppearance(named: .darkAqua)
                : NSAppearance(named: .aqua)
        }
    }

    override func updateLayer() {
        super.updateLayer()
        applyColors()
    }
}

// MARK: - SettingsTopTabBar

class SettingsTopTabBar: NSView {
    let labels = ["📦 桌宠设置", "🔗 AI 设置", "💬 对话记录"]
    var onSelect: ((Int) -> Void)?
    private(set) var selectedIndex: Int = 0
    private var tabLabels: [NSTextField] = []
    private var tabButtons: [NSView] = []
    private let indicatorLine = CALayer()
    private let bottomBorder = CALayer()

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true
        setupTabs()
        observeThemeChanges()
    }

    required init?(coder: NSCoder) { fatalError() }

    func selectTab(index: Int) {
        guard index >= 0, index < labels.count, index != selectedIndex else { return }
        selectedIndex = index
        updateTabColors()
        updateIndicator()
    }

    private func setupTabs() {
        var prev: NSView?
        for (i, label) in labels.enumerated() {
            let container = NSView()
            container.wantsLayer = true
            container.translatesAutoresizingMaskIntoConstraints = false
            addSubview(container)

            let textField = NSTextField(labelWithString: label)
            textField.font = NSFont.systemFont(ofSize: 12, weight: .medium)
            textField.isEditable = false
            textField.isBordered = false
            textField.backgroundColor = .clear
            textField.translatesAutoresizingMaskIntoConstraints = false
            container.addSubview(textField)
            tabLabels.append(textField)

            NSLayoutConstraint.activate([
                container.topAnchor.constraint(equalTo: topAnchor),
                container.bottomAnchor.constraint(equalTo: bottomAnchor),

                textField.centerXAnchor.constraint(equalTo: container.centerXAnchor),
                textField.centerYAnchor.constraint(equalTo: container.centerYAnchor),
                textField.leadingAnchor.constraint(greaterThanOrEqualTo: container.leadingAnchor, constant: 20),
                textField.trailingAnchor.constraint(lessThanOrEqualTo: container.trailingAnchor, constant: -20),
            ])

            if let prev = prev {
                container.leadingAnchor.constraint(equalTo: prev.trailingAnchor).isActive = true
                container.widthAnchor.constraint(equalTo: prev.widthAnchor).isActive = true
            } else {
                container.leadingAnchor.constraint(equalTo: leadingAnchor).isActive = true
            }
            prev = container

            let tap = NSClickGestureRecognizer(target: self, action: #selector(tabTapped(_:)))
            container.addGestureRecognizer(tap)
            tabButtons.append(container)
        }

        prev?.trailingAnchor.constraint(equalTo: trailingAnchor).isActive = true

        // Bottom border
        bottomBorder.frame = CGRect(x: 0, y: 0, width: bounds.width, height: 1)
        bottomBorder.autoresizingMask = [.layerWidthSizable, .layerMaxYMargin]
        layer?.addSublayer(bottomBorder)

        // Indicator line
        indicatorLine.frame = CGRect(x: 0, y: 0, width: 40, height: 2)
        indicatorLine.cornerRadius = 1
        layer?.addSublayer(indicatorLine)

        updateTabColors()
        updateIndicator()
    }

    @objc private func tabTapped(_ sender: NSClickGestureRecognizer) {
        guard let container = sender.view, let index = tabButtons.firstIndex(of: container) else { return }
        selectTab(index: index)
        onSelect?(index)
    }

    private func updateTabColors() {
        let colors = ThemeColors.current
        for (i, tf) in tabLabels.enumerated() {
            tf.textColor = (i == selectedIndex) ? colors.ntAct : NSColor(white: 1, alpha: 0.4)
        }
        bottomBorder.backgroundColor = colors.ntBrd.cgColor
    }

    private func updateIndicator() {
        guard selectedIndex < tabButtons.count else { return }
        let colors = ThemeColors.current
        let container = tabButtons[selectedIndex]
        let width: CGFloat = 40
        let xPos = container.frame.midX - width / 2
        indicatorLine.backgroundColor = colors.ntAct.cgColor
        indicatorLine.frame = CGRect(x: xPos, y: 0, width: width, height: 2)
    }

    override func layout() {
        super.layout()
        updateIndicator()
        bottomBorder.frame = CGRect(x: 0, y: 0, width: bounds.width, height: 1)
    }

    private func observeThemeChanges() {
        NotificationCenter.default.addObserver(self, selector: #selector(themeChanged), name: .themeDidChange, object: nil)
    }

    @objc private func themeChanged() {
        updateTabColors()
        updateIndicator()
    }
}

// MARK: - SettingsSubTabBar

class SettingsSubTabBar: NSView {
    var labels: [String] = [] {
        didSet { rebuildTabs() }
    }
    var onSelect: ((Int) -> Void)?
    private(set) var selectedIndex: Int = 0
    private var tabLabels: [NSTextField] = []
    private var tabButtons: [NSView] = []
    private let indicatorLine = CALayer()
    private let bottomBorder = CALayer()

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true
        observeThemeChanges()
    }

    required init?(coder: NSCoder) { fatalError() }

    func selectTab(index: Int) {
        guard index >= 0, index < labels.count, index != selectedIndex else { return }
        selectedIndex = index
        updateTabColors()
        updateIndicator()
    }

    private func rebuildTabs() {
        tabLabels.forEach { $0.removeFromSuperview() }
        tabButtons.forEach { $0.removeFromSuperview() }
        tabLabels.removeAll()
        tabButtons.removeAll()

        var prev: NSView?
        for (i, label) in labels.enumerated() {
            let container = NSView()
            container.wantsLayer = true
            container.translatesAutoresizingMaskIntoConstraints = false
            addSubview(container)

            let textField = NSTextField(labelWithString: label)
            textField.font = NSFont.systemFont(ofSize: 11, weight: .medium)
            textField.isEditable = false
            textField.isBordered = false
            textField.backgroundColor = .clear
            textField.translatesAutoresizingMaskIntoConstraints = false
            container.addSubview(textField)
            tabLabels.append(textField)

            NSLayoutConstraint.activate([
                container.topAnchor.constraint(equalTo: topAnchor),
                container.bottomAnchor.constraint(equalTo: bottomAnchor),

                textField.centerXAnchor.constraint(equalTo: container.centerXAnchor),
                textField.centerYAnchor.constraint(equalTo: container.centerYAnchor),
                textField.leadingAnchor.constraint(greaterThanOrEqualTo: container.leadingAnchor, constant: 14),
                textField.trailingAnchor.constraint(lessThanOrEqualTo: container.trailingAnchor, constant: -14),
            ])

            if let prev = prev {
                container.leadingAnchor.constraint(equalTo: prev.trailingAnchor).isActive = true
                container.widthAnchor.constraint(equalTo: prev.widthAnchor).isActive = true
            } else {
                container.leadingAnchor.constraint(equalTo: leadingAnchor).isActive = true
            }
            prev = container

            let tap = NSClickGestureRecognizer(target: self, action: #selector(tabTapped(_:)))
            container.addGestureRecognizer(tap)
            tabButtons.append(container)
        }
        prev?.trailingAnchor.constraint(equalTo: trailingAnchor).isActive = true

        // Re-add border/indicator
        if bottomBorder.superlayer == nil { layer?.addSublayer(bottomBorder) }
        if indicatorLine.superlayer == nil { layer?.addSublayer(indicatorLine) }

        updateTabColors()
        updateIndicator()
    }

    @objc private func tabTapped(_ sender: NSClickGestureRecognizer) {
        guard let container = sender.view, let index = tabButtons.firstIndex(of: container) else { return }
        selectTab(index: index)
        onSelect?(index)
    }

    private func updateTabColors() {
        let colors = ThemeColors.current
        for (i, tf) in tabLabels.enumerated() {
            tf.textColor = (i == selectedIndex) ? colors.ntAct : NSColor(white: 1, alpha: 0.4)
        }
        bottomBorder.backgroundColor = colors.ntBrd.cgColor
    }

    private func updateIndicator() {
        guard selectedIndex < tabButtons.count else { return }
        let colors = ThemeColors.current
        let container = tabButtons[selectedIndex]
        let width: CGFloat = 28
        let xPos = container.frame.midX - width / 2
        indicatorLine.backgroundColor = colors.ntAct.cgColor
        indicatorLine.frame = CGRect(x: xPos, y: 0, width: width, height: 2)
    }

    override func layout() {
        super.layout()
        updateIndicator()
        bottomBorder.frame = CGRect(x: 0, y: 0, width: bounds.width, height: 1)
    }

    private func observeThemeChanges() {
        NotificationCenter.default.addObserver(self, selector: #selector(themeChanged), name: .themeDidChange, object: nil)
    }

    @objc private func themeChanged() {
        updateTabColors()
        updateIndicator()
    }
}

// MARK: - Placeholder Content Views (real implementations in separate files)

// MARK: - SettingsFooterBar

class SettingsFooterBar: NSView {
    private let leftLabel = NSTextField(labelWithString: "")
    private let rightLabel = NSTextField(labelWithString: "")
    private let aiSettingsLabel = NSTextField(labelWithString: "")
    private let topBorder = CALayer()

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true
        setupViews()
        observeThemeChanges()
    }

    required init?(coder: NSCoder) { fatalError() }

    private func setupViews() {
        leftLabel.stringValue = "💡 桌宠浮于桌面 · Hermes AI 浮窗显示 · 对话快捷键呼出"
        leftLabel.font = NSFont.systemFont(ofSize: 9, weight: .regular)
        leftLabel.isEditable = false
        leftLabel.isBordered = false
        leftLabel.backgroundColor = .clear
        leftLabel.translatesAutoresizingMaskIntoConstraints = false
        addSubview(leftLabel)

        aiSettingsLabel.stringValue = "AI 设置"
        aiSettingsLabel.font = NSFont.systemFont(ofSize: 9, weight: .regular)
        aiSettingsLabel.isEditable = false
        aiSettingsLabel.isBordered = false
        aiSettingsLabel.backgroundColor = .clear
        aiSettingsLabel.translatesAutoresizingMaskIntoConstraints = false
        addSubview(aiSettingsLabel)

        rightLabel.stringValue = "v0.5 · macOS Native"
        rightLabel.font = NSFont.systemFont(ofSize: 9, weight: .regular)
        rightLabel.isEditable = false
        rightLabel.isBordered = false
        rightLabel.backgroundColor = .clear
        rightLabel.translatesAutoresizingMaskIntoConstraints = false
        addSubview(rightLabel)

        NSLayoutConstraint.activate([
            leftLabel.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 16),
            leftLabel.centerYAnchor.constraint(equalTo: centerYAnchor),

            rightLabel.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -16),
            rightLabel.centerYAnchor.constraint(equalTo: centerYAnchor),

            aiSettingsLabel.trailingAnchor.constraint(equalTo: rightLabel.leadingAnchor, constant: -12),
            aiSettingsLabel.centerYAnchor.constraint(equalTo: centerYAnchor),
        ])

        applyColors()
    }

    private func applyColors() {
        let colors = ThemeColors.current
        leftLabel.textColor = colors.txtDim
        rightLabel.textColor = colors.txtDim
        aiSettingsLabel.textColor = colors.ntAct
        topBorder.backgroundColor = colors.ntBrd.cgColor
    }

    override func viewDidMoveToSuperview() {
        super.viewDidMoveToSuperview()
        guard superview != nil else { return }
        topBorder.frame = CGRect(x: 0, y: bounds.height - 1, width: bounds.width, height: 1)
        topBorder.autoresizingMask = [.layerWidthSizable, .layerMinYMargin]
        layer?.addSublayer(topBorder)
    }

    private func observeThemeChanges() {
        NotificationCenter.default.addObserver(self, selector: #selector(themeChanged), name: .themeDidChange, object: nil)
    }

    @objc private func themeChanged() {
        applyColors()
    }
}

// MARK: - SettingsRootView

class SettingsRootView: NSVisualEffectView {
    let titleBar = SettingsTitleBar()
    let topTabs = SettingsTopTabBar()
    let subTabs = SettingsSubTabBar()
    let contentStack = NSStackView()
    let backgroundView = SettingsBackgroundView()
    let footerBar = SettingsFooterBar()

    private lazy var subViews: [[NSView]] = [
        [MaterialPetSettingsSubview(), PaperdollSettingsSubview(), BackgroundSettingsSubview(), BalloonSettingsSubview()],
        [AISettingsSubview()],
        [ChatLogSettingsSubview()],
    ]
    private var currentTopTab: Int = 0
    private var currentSubTab: Int = 0

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        commonInit()
    }

    required init?(coder: NSCoder) { fatalError() }

    private func commonInit() {
        // VisualEffect config
        material = .dark
        blendingMode = .withinWindow
        state = .followsWindowActiveState
        wantsLayer = true

        // Background view behind everything
        backgroundView.translatesAutoresizingMaskIntoConstraints = false
        addSubview(backgroundView, positioned: .below, relativeTo: nil)

        // Title bar
        titleBar.translatesAutoresizingMaskIntoConstraints = false
        addSubview(titleBar)

        // Top tabs
        topTabs.translatesAutoresizingMaskIntoConstraints = false
        addSubview(topTabs)

        // Sub tabs
        subTabs.translatesAutoresizingMaskIntoConstraints = false
        addSubview(subTabs)

        // Content stack
        contentStack.orientation = .vertical
        contentStack.spacing = 0
        contentStack.translatesAutoresizingMaskIntoConstraints = false
        addSubview(contentStack)

        // Footer
        footerBar.translatesAutoresizingMaskIntoConstraints = false
        addSubview(footerBar)

        // Layout
        NSLayoutConstraint.activate([
            backgroundView.topAnchor.constraint(equalTo: topAnchor),
            backgroundView.leadingAnchor.constraint(equalTo: leadingAnchor),
            backgroundView.trailingAnchor.constraint(equalTo: trailingAnchor),
            backgroundView.bottomAnchor.constraint(equalTo: bottomAnchor),

            titleBar.topAnchor.constraint(equalTo: topAnchor),
            titleBar.leadingAnchor.constraint(equalTo: leadingAnchor),
            titleBar.trailingAnchor.constraint(equalTo: trailingAnchor),
            titleBar.heightAnchor.constraint(equalToConstant: 50),

            topTabs.topAnchor.constraint(equalTo: titleBar.bottomAnchor),
            topTabs.leadingAnchor.constraint(equalTo: leadingAnchor),
            topTabs.trailingAnchor.constraint(equalTo: trailingAnchor),
            topTabs.heightAnchor.constraint(equalToConstant: 36),

            subTabs.topAnchor.constraint(equalTo: topTabs.bottomAnchor),
            subTabs.leadingAnchor.constraint(equalTo: leadingAnchor),
            subTabs.trailingAnchor.constraint(equalTo: trailingAnchor),
            subTabs.heightAnchor.constraint(equalToConstant: 28),

            contentStack.topAnchor.constraint(equalTo: subTabs.bottomAnchor, constant: 8),
            contentStack.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 16),
            contentStack.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -16),

            footerBar.topAnchor.constraint(equalTo: contentStack.bottomAnchor),
            footerBar.leadingAnchor.constraint(equalTo: leadingAnchor),
            footerBar.trailingAnchor.constraint(equalTo: trailingAnchor),
            footerBar.heightAnchor.constraint(equalToConstant: 28),
            footerBar.bottomAnchor.constraint(equalTo: bottomAnchor),
        ])

        // Callbacks
        topTabs.onSelect = { [weak self] index in
            self?.topTabSelected(index)
        }
        subTabs.onSelect = { [weak self] index in
            self?.subTabSelected(index)
        }

        // Initial state
        updateSubTabsForTopTab(0)
        showContentView(topIndex: 0, subIndex: 0)

        NotificationCenter.default.addObserver(self, selector: #selector(themeChanged), name: .themeDidChange, object: nil)
    }

    private func topTabSelected(_ index: Int) {
        currentTopTab = index
        currentSubTab = 0
        updateSubTabsForTopTab(index)
        showContentView(topIndex: index, subIndex: 0)
    }

    private func subTabSelected(_ index: Int) {
        currentSubTab = index
        showContentView(topIndex: currentTopTab, subIndex: index)
    }

    private func updateSubTabsForTopTab(_ index: Int) {
        switch index {
        case 0:
            subTabs.labels = ["素材桌宠设置", "纸娃娃桌宠设置", "背景设置", "聊天气泡设置"]
            subTabs.isHidden = false
        case 1:
            subTabs.labels = []
            subTabs.isHidden = true
        case 2:
            subTabs.labels = []
            subTabs.isHidden = true
        default:
            subTabs.labels = []
            subTabs.isHidden = true
        }
    }

    private func showContentView(topIndex: Int, subIndex: Int) {
        // Remove all arranged subviews from contentStack
        while contentStack.arrangedSubviews.count > 0 {
            contentStack.arrangedSubviews.first?.removeFromSuperview()
        }

        guard topIndex < subViews.count else { return }
        let group = subViews[topIndex]
        guard subIndex < group.count else { return }
        let view = group[subIndex]
        view.translatesAutoresizingMaskIntoConstraints = false
        contentStack.addArrangedSubview(view)

        // Make the content view fill the stack
        NSLayoutConstraint.activate([
            view.leadingAnchor.constraint(equalTo: contentStack.leadingAnchor),
            view.trailingAnchor.constraint(equalTo: contentStack.trailingAnchor),
        ])
    }

    @objc private func themeChanged() {
        backgroundView.needsDisplay = true
    }
}

// MARK: - SettingsWindowController

class SettingsWindowController: NSWindowController {
    private static var _shared: SettingsWindowController?

    static var shared: SettingsWindowController {
        if let existing = _shared { return existing }
        let wc = SettingsWindowController()
        _shared = wc
        return wc
    }

    static func show() {
        let wc = shared
        wc.showWindow(nil)
        wc.window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    static func destroyShared() {
        if let wc = _shared {
            wc.close()
            _shared = nil
        }
    }

    init() {
        let rect = NSRect(x: 0, y: 0, width: 1020, height: 740)
        let panel = NSPanel(
            contentRect: rect,
            styleMask: [.titled, .closable, .miniaturizable, .resizable, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        panel.title = "MiniPet 设置中心"
        panel.isFloatingPanel = false
        panel.isMovableByWindowBackground = true
        panel.hasShadow = true
        panel.titlebarAppearsTransparent = true
        panel.styleMask.insert(.fullSizeContentView)
        panel.appearance = ThemeColors.isDark
            ? NSAppearance(named: .darkAqua)
            : NSAppearance(named: .aqua)

        let rootView = SettingsRootView(frame: rect)
        panel.contentView = rootView
        panel.contentView?.wantsLayer = true
        panel.contentView?.layer?.cornerRadius = 16
        panel.contentView?.layer?.masksToBounds = true

        super.init(window: panel)

        // Window frame view wrapper for corner radius
        let frameView = SettingsWindowFrameView(frame: rect)
        panel.contentView = frameView
        rootView.frame = frameView.bounds
        rootView.autoresizingMask = [.width, .height]
        frameView.addSubview(rootView)

        // Observe theme changes to rebuild window appearance
        NotificationCenter.default.addObserver(self, selector: #selector(rebuildAppearance), name: .themeDidChange, object: nil)
    }

    required init?(coder: NSCoder) { fatalError() }

    @objc private func rebuildAppearance() {
        window?.appearance = ThemeColors.isDark
            ? NSAppearance(named: .darkAqua)
            : NSAppearance(named: .aqua)

        if let frameView = window?.contentView as? SettingsWindowFrameView {
            frameView.material = ThemeColors.isDark ? .dark : .light
        }
        if let rootView = window?.contentView?.subviews.first(where: { $0 is SettingsRootView }) as? SettingsRootView {
            rootView.material = ThemeColors.isDark ? .dark : .light
        }
    }

    override func close() {
        SettingsManager.save(SettingsManager.current)
        NotificationCenter.default.post(name: .settingsDidClose, object: nil)
        super.close()
    }

    deinit {
        NotificationCenter.default.removeObserver(self)
    }
}