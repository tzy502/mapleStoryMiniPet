import AppKit

// MARK: - Bubble Style Data

private struct BubbleStyle {
    let id: Int
    let name: String
    let skin: Int
    let gradientColors: (start: NSColor, end: NSColor)
    let arrowColor: NSColor
}

private let bubbleStyles: [BubbleStyle] = [
    BubbleStyle(id: 560, name: "经典蓝", skin: 1,
                gradientColors: (start: NSColor(red: 0.369, green: 0.612, blue: 1, alpha: 1),
                                 end: NSColor(red: 0.290, green: 0.541, blue: 0.933, alpha: 1)),
                arrowColor: NSColor(red: 0.369, green: 0.612, blue: 1, alpha: 1)),
    BubbleStyle(id: 3, name: "暗黑玻璃", skin: 2,
                gradientColors: (start: NSColor(red: 0.157, green: 0.157, blue: 0.196, alpha: 0.9),
                                 end: NSColor(red: 0.157, green: 0.157, blue: 0.196, alpha: 0.9)),
                arrowColor: NSColor(red: 0.157, green: 0.157, blue: 0.196, alpha: 0.9)),
    BubbleStyle(id: 5, name: "樱花粉", skin: 3,
                gradientColors: (start: NSColor(red: 1, green: 0.420, blue: 0.541, alpha: 1),
                                 end: NSColor(red: 1, green: 0.541, blue: 0.671, alpha: 1)),
                arrowColor: NSColor(red: 1, green: 0.420, blue: 0.541, alpha: 1)),
    BubbleStyle(id: 50, name: "自然绿", skin: 4,
                gradientColors: (start: NSColor(red: 0.204, green: 0.780, blue: 0.349, alpha: 1),
                                 end: NSColor(red: 0.157, green: 0.655, blue: 0.271, alpha: 1)),
                arrowColor: NSColor(red: 0.204, green: 0.780, blue: 0.349, alpha: 1)),
    BubbleStyle(id: 100, name: "紫罗兰", skin: 5,
                gradientColors: (start: NSColor(red: 0.545, green: 0.361, blue: 0.965, alpha: 1),
                                 end: NSColor(red: 0.420, green: 0.247, blue: 0.627, alpha: 1)),
                arrowColor: NSColor(red: 0.545, green: 0.361, blue: 0.965, alpha: 1)),
    BubbleStyle(id: 200, name: "暖阳橙", skin: 6,
                gradientColors: (start: NSColor(red: 1, green: 0.584, blue: 0, alpha: 1),
                                 end: NSColor(red: 0.902, green: 0.525, blue: 0, alpha: 1)),
                arrowColor: NSColor(red: 1, green: 0.584, blue: 0, alpha: 1)),
]

// MARK: - Bubble Item View

private class BubbleItemView: NSView {
    let style: BubbleStyle
    private let swatchLayer = CALayer()
    private let checkmarkLayer = CAShapeLayer()
    private let nameLabel = NSTextField(labelWithString: "")
    private let idLabel = NSTextField(labelWithString: "")

    var isSelected = false {
        didSet { updateAppearance() }
    }

    var onClick: ((BubbleStyle) -> Void)?

    init(style: BubbleStyle) {
        self.style = style
        super.init(frame: .zero)
        wantsLayer = true
        layer?.cornerRadius = 5
        translatesAutoresizingMaskIntoConstraints = false

        swatchLayer.cornerRadius = 4
        swatchLayer.masksToBounds = true
        layer?.addSublayer(swatchLayer)

        nameLabel.stringValue = style.name
        nameLabel.font = NSFont.systemFont(ofSize: 10)
        nameLabel.textColor = ThemeColors.current.txtSec
        nameLabel.isBezeled = false
        nameLabel.drawsBackground = false
        nameLabel.isEditable = false
        nameLabel.translatesAutoresizingMaskIntoConstraints = false
        addSubview(nameLabel)

        idLabel.stringValue = "#\(style.id)"
        idLabel.font = NSFont.monospacedSystemFont(ofSize: 9, weight: .regular)
        idLabel.textColor = ThemeColors.current.txtDim
        idLabel.isBezeled = false
        idLabel.drawsBackground = false
        idLabel.isEditable = false
        idLabel.translatesAutoresizingMaskIntoConstraints = false
        addSubview(idLabel)

        checkmarkLayer.cornerRadius = 7
        checkmarkLayer.borderWidth = 1
        checkmarkLayer.borderColor = ThemeColors.current.ntBrd.cgColor
        checkmarkLayer.backgroundColor = NSColor.clear.cgColor
        layer?.addSublayer(checkmarkLayer)

        let checkPath = CGMutablePath()
        checkPath.move(to: CGPoint(x: 4, y: 7))
        checkPath.addLine(to: CGPoint(x: 6.5, y: 10))
        checkPath.addLine(to: CGPoint(x: 10.5, y: 4.5))
        let checkShape = CAShapeLayer()
        checkShape.path = checkPath
        checkShape.strokeColor = NSColor.clear.cgColor
        checkShape.fillColor = NSColor.clear.cgColor
        checkShape.lineWidth = 1.5
        checkShape.lineCap = .round
        checkShape.lineJoin = .round
        checkmarkLayer.addSublayer(checkShape)

        let gradient = CAGradientLayer()
        gradient.colors = [style.gradientColors.start.cgColor, style.gradientColors.end.cgColor]
        gradient.startPoint = CGPoint(x: 0, y: 0)
        gradient.endPoint = CGPoint(x: 1, y: 1)
        gradient.frame = CGRect(x: 0, y: 0, width: 50, height: 22)
        swatchLayer.addSublayer(gradient)

        if style.id == 3 {
            swatchLayer.borderWidth = 1
            swatchLayer.borderColor = NSColor(white: 1, alpha: 0.1).cgColor
        }

        let click = NSClickGestureRecognizer(target: self, action: #selector(didClick))
        addGestureRecognizer(click)
    }

    required init?(coder: NSCoder) { fatalError() }

    override func layout() {
        super.layout()
        let bounds = self.bounds
        swatchLayer.frame = CGRect(x: 8, y: (bounds.height - 22) / 2, width: 50, height: 22)
        nameLabel.frame = CGRect(x: 66, y: (bounds.height - 22) / 2 + 2, width: bounds.width - 100, height: 14)
        idLabel.frame = CGRect(x: 66, y: (bounds.height - 22) / 2 + 16, width: bounds.width - 100, height: 12)
        checkmarkLayer.frame = CGRect(x: bounds.width - 24, y: (bounds.height - 14) / 2, width: 14, height: 14)
    }

    private func updateAppearance() {
        let theme = ThemeColors.current
        if isSelected {
            layer?.borderWidth = 1
            layer?.borderColor = theme.ntAct.cgColor
            layer?.backgroundColor = NSColor(red: 0.369, green: 0.612, blue: 1, alpha: 0.06).cgColor
            checkmarkLayer.backgroundColor = theme.ntAct.cgColor
            checkmarkLayer.borderColor = theme.ntAct.cgColor
            if let checkShape = checkmarkLayer.sublayers?.first as? CAShapeLayer {
                checkShape.strokeColor = NSColor.white.cgColor
            }
        } else {
            layer?.borderWidth = 0
            layer?.borderColor = nil
            layer?.backgroundColor = nil
            checkmarkLayer.backgroundColor = NSColor.clear.cgColor
            checkmarkLayer.borderColor = theme.ntBrd.cgColor
            if let checkShape = checkmarkLayer.sublayers?.first as? CAShapeLayer {
                checkShape.strokeColor = NSColor.clear.cgColor
            }
        }
    }

    @objc private func didClick() {
        onClick?(style)
    }

    func updateTheme() {
        let theme = ThemeColors.current
        nameLabel.textColor = theme.txtSec
        idLabel.textColor = theme.txtDim
        updateAppearance()
    }
}

// MARK: - Balloon Settings Subview

class BalloonSettingsSubview: NSView {
    // MARK: - Subviews

    private let sidebarView = NSView()
    private let previewArea = NSView()

    private let searchField = NSSearchField()

    private let countLabel = NSTextField(labelWithString: "共 6 个样式")
    private let importLabel = NSTextField(labelWithString: "从后台导入")

    private let scrollView = NSScrollView()
    private let listStack = NSStackView()
    private var itemViews: [BubbleItemView] = []

    private let idTextField = NSTextField()
    private let textColorWell = NSColorWell()
    private let bubbleColorWell = NSColorWell()
    private let applyButton = NSButton()

    private let demoBubbleView = NSView()
    private let petNameLabel = NSTextField(labelWithString: "🐾 路西德")
    private let demoTextLabel = NSTextField(labelWithString: "你好呀胶水！今天想让我帮你做什么呀？🎀")
    private let timestampLabel = NSTextField(labelWithString: "刚刚")
    private let arrowLayer = CAShapeLayer()
    private let bubbleGradientLayer = CAGradientLayer()

    private let previewButton = NSButton()
    private let hideButton = NSButton()
    private let applyConfirmButton = NSButton()
    private let hintLabel = NSTextField(labelWithString: "选择一种样式或自定义颜色 → 确定应用")

    private let divider = NSView()

    private var selectedStyle: BubbleStyle = bubbleStyles[0]
    private var useCustomColors = false

    // MARK: - Init

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        commonInit()
    }

    required init?(coder: NSCoder) { fatalError() }

    private func commonInit() {
        wantsLayer = true
        translatesAutoresizingMaskIntoConstraints = false

        setupSidebar()
        setupDivider()
        setupPreviewArea()
        setupConstraints()
        updateDemoBubble()

        DistributedNotificationCenter.default.addObserver(
            self, selector: #selector(themeChanged),
            name: NSNotification.Name(rawValue: "AppleInterfaceThemeChangedNotification"),
            object: nil
        )
        NotificationCenter.default.addObserver(
            self, selector: #selector(themeChanged),
            name: .themeDidChange, object: nil
        )
    }

    deinit {
        NotificationCenter.default.removeObserver(self)
        DistributedNotificationCenter.default.removeObserver(self)
    }

    // MARK: - Sidebar

    private func setupSidebar() {
        sidebarView.wantsLayer = true
        sidebarView.translatesAutoresizingMaskIntoConstraints = false
        addSubview(sidebarView)

        searchField.placeholderString = "搜索气泡 ID 或名称…"
        searchField.font = NSFont.systemFont(ofSize: 12)
        searchField.translatesAutoresizingMaskIntoConstraints = false
        searchField.sendsSearchStringImmediately = true
        searchField.target = self
        searchField.action = #selector(searchChanged)
        sidebarView.addSubview(searchField)

        countLabel.font = NSFont.systemFont(ofSize: 9)
        countLabel.textColor = ThemeColors.current.txtDim
        countLabel.isBezeled = false
        countLabel.drawsBackground = false
        countLabel.isEditable = false
        countLabel.translatesAutoresizingMaskIntoConstraints = false
        sidebarView.addSubview(countLabel)

        importLabel.font = NSFont.systemFont(ofSize: 9)
        importLabel.textColor = ThemeColors.current.txtMute
        importLabel.isBezeled = false
        importLabel.drawsBackground = false
        importLabel.isEditable = false
        importLabel.alignment = .right
        importLabel.translatesAutoresizingMaskIntoConstraints = false
        sidebarView.addSubview(importLabel)

        scrollView.hasVerticalScroller = true
        scrollView.drawsBackground = false
        scrollView.translatesAutoresizingMaskIntoConstraints = false
        scrollView.autohidesScrollers = true
        sidebarView.addSubview(scrollView)

        listStack.orientation = .vertical
        listStack.spacing = 3
        listStack.translatesAutoresizingMaskIntoConstraints = false
        scrollView.documentView = listStack

        for (idx, style) in bubbleStyles.enumerated() {
            let item = BubbleItemView(style: style)
            item.isSelected = (idx == 0)
            item.onClick = { [weak self] clickedStyle in
                self?.selectBubbleStyle(clickedStyle)
            }
            itemViews.append(item)
            listStack.addArrangedSubview(item)
        }

        let controlsView = NSView()
        controlsView.wantsLayer = true
        controlsView.layer?.cornerRadius = 5
        controlsView.layer?.backgroundColor = ThemeColors.current.ntBrd.cgColor
        controlsView.translatesAutoresizingMaskIntoConstraints = false
        sidebarView.addSubview(controlsView)

        let idLabel = NSTextField(labelWithString: "ID")
        idLabel.font = NSFont.systemFont(ofSize: 9)
        idLabel.textColor = ThemeColors.current.txtMute
        idLabel.isBezeled = false
        idLabel.drawsBackground = false
        idLabel.isEditable = false
        idLabel.translatesAutoresizingMaskIntoConstraints = false
        controlsView.addSubview(idLabel)

        idTextField.stringValue = "560"
        idTextField.font = NSFont.monospacedSystemFont(ofSize: 9, weight: .regular)
        idTextField.textColor = ThemeColors.current.txtSec
        idTextField.isBezeled = false
        idTextField.isBordered = true
        idTextField.wantsLayer = true
        idTextField.layer?.cornerRadius = 3
        idTextField.layer?.borderWidth = 1
        idTextField.layer?.borderColor = NSColor(white: 1, alpha: 0.05).cgColor
        idTextField.layer?.backgroundColor = NSColor(white: 1, alpha: 0.03).cgColor
        idTextField.translatesAutoresizingMaskIntoConstraints = false
        controlsView.addSubview(idTextField)

        let textLabel = NSTextField(labelWithString: "文字")
        textLabel.font = NSFont.systemFont(ofSize: 9)
        textLabel.textColor = ThemeColors.current.txtMute
        textLabel.isBezeled = false
        textLabel.drawsBackground = false
        textLabel.isEditable = false
        textLabel.translatesAutoresizingMaskIntoConstraints = false
        controlsView.addSubview(textLabel)

        textColorWell.color = .white
        textColorWell.translatesAutoresizingMaskIntoConstraints = false
        textColorWell.target = self
        textColorWell.action = #selector(colorWellChanged)
        controlsView.addSubview(textColorWell)

        let bubbleLabel = NSTextField(labelWithString: "气泡")
        bubbleLabel.font = NSFont.systemFont(ofSize: 9)
        bubbleLabel.textColor = ThemeColors.current.txtMute
        bubbleLabel.isBezeled = false
        bubbleLabel.drawsBackground = false
        bubbleLabel.isEditable = false
        bubbleLabel.translatesAutoresizingMaskIntoConstraints = false
        controlsView.addSubview(bubbleLabel)

        bubbleColorWell.color = ThemeColors.current.ntAct
        bubbleColorWell.translatesAutoresizingMaskIntoConstraints = false
        bubbleColorWell.target = self
        bubbleColorWell.action = #selector(colorWellChanged)
        controlsView.addSubview(bubbleColorWell)

        applyButton.title = "应用"
        applyButton.bezelStyle = .rounded
        applyButton.font = NSFont.systemFont(ofSize: 9, weight: .semibold)
        applyButton.contentTintColor = .white
        applyButton.isBordered = false
        applyButton.wantsLayer = true
        applyButton.layer?.cornerRadius = 3
        applyButton.layer?.backgroundColor = ThemeColors.current.ntAct.cgColor
        applyButton.translatesAutoresizingMaskIntoConstraints = false
        applyButton.target = self
        applyButton.action = #selector(applyCustomColors)
        controlsView.addSubview(applyButton)

        setupControlsLayout(controlsView, idLabel: idLabel, textLabel: textLabel, bubbleLabel: bubbleLabel)
    }

    private func setupControlsLayout(_ controlsView: NSView, idLabel: NSTextField, textLabel: NSTextField, bubbleLabel: NSTextField) {
        controlsView.addConstraint(idLabel.leadingAnchor.constraint(equalTo: controlsView.leadingAnchor, constant: 8))
        controlsView.addConstraint(idLabel.centerYAnchor.constraint(equalTo: idTextField.centerYAnchor))

        controlsView.addConstraint(idTextField.leadingAnchor.constraint(equalTo: idLabel.trailingAnchor, constant: 4))
        controlsView.addConstraint(idTextField.centerYAnchor.constraint(equalTo: controlsView.centerYAnchor))
        controlsView.addConstraint(idTextField.widthAnchor.constraint(equalToConstant: 50))
        controlsView.addConstraint(idTextField.heightAnchor.constraint(equalToConstant: 20))

        controlsView.addConstraint(textLabel.leadingAnchor.constraint(equalTo: idTextField.trailingAnchor, constant: 6))
        controlsView.addConstraint(textLabel.centerYAnchor.constraint(equalTo: textColorWell.centerYAnchor))

        controlsView.addConstraint(textColorWell.leadingAnchor.constraint(equalTo: textLabel.trailingAnchor, constant: 2))
        controlsView.addConstraint(textColorWell.centerYAnchor.constraint(equalTo: controlsView.centerYAnchor))
        controlsView.addConstraint(textColorWell.widthAnchor.constraint(equalToConstant: 20))
        controlsView.addConstraint(textColorWell.heightAnchor.constraint(equalToConstant: 20))

        controlsView.addConstraint(bubbleLabel.leadingAnchor.constraint(equalTo: textColorWell.trailingAnchor, constant: 6))
        controlsView.addConstraint(bubbleLabel.centerYAnchor.constraint(equalTo: bubbleColorWell.centerYAnchor))

        controlsView.addConstraint(bubbleColorWell.leadingAnchor.constraint(equalTo: bubbleLabel.trailingAnchor, constant: 2))
        controlsView.addConstraint(bubbleColorWell.centerYAnchor.constraint(equalTo: controlsView.centerYAnchor))
        controlsView.addConstraint(bubbleColorWell.widthAnchor.constraint(equalToConstant: 20))
        controlsView.addConstraint(bubbleColorWell.heightAnchor.constraint(equalToConstant: 20))

        controlsView.addConstraint(applyButton.leadingAnchor.constraint(equalTo: bubbleColorWell.trailingAnchor, constant: 6))
        controlsView.addConstraint(applyButton.centerYAnchor.constraint(equalTo: controlsView.centerYAnchor))
        controlsView.addConstraint(applyButton.heightAnchor.constraint(equalToConstant: 20))
        controlsView.addConstraint(applyButton.widthAnchor.constraint(greaterThanOrEqualToConstant: 30))
    }

    // MARK: - Divider

    private func setupDivider() {
        divider.wantsLayer = true
        divider.layer?.backgroundColor = ThemeColors.current.ntBrd.cgColor
        divider.translatesAutoresizingMaskIntoConstraints = false
        addSubview(divider)
    }

    // MARK: - Preview Area

    private func setupPreviewArea() {
        previewArea.wantsLayer = true
        previewArea.translatesAutoresizingMaskIntoConstraints = false
        addSubview(previewArea)

        let centerContainer = NSView()
        centerContainer.translatesAutoresizingMaskIntoConstraints = false
        previewArea.addSubview(centerContainer)

        demoBubbleView.wantsLayer = true
        demoBubbleView.layer?.cornerRadius = 12
        demoBubbleView.layer?.masksToBounds = true
        demoBubbleView.translatesAutoresizingMaskIntoConstraints = false
        centerContainer.addSubview(demoBubbleView)

        petNameLabel.font = NSFont.systemFont(ofSize: 9)
        petNameLabel.textColor = NSColor(white: 1, alpha: 0.3)
        petNameLabel.isBezeled = false
        petNameLabel.drawsBackground = false
        petNameLabel.isEditable = false
        petNameLabel.translatesAutoresizingMaskIntoConstraints = false
        demoBubbleView.addSubview(petNameLabel)

        demoTextLabel.font = NSFont.systemFont(ofSize: 12)
        demoTextLabel.textColor = .white
        demoTextLabel.isBezeled = false
        demoTextLabel.drawsBackground = false
        demoTextLabel.isEditable = false
        demoTextLabel.translatesAutoresizingMaskIntoConstraints = false
        demoBubbleView.addSubview(demoTextLabel)

        timestampLabel.font = NSFont.systemFont(ofSize: 8)
        timestampLabel.textColor = NSColor(white: 1, alpha: 0.5)
        timestampLabel.isBezeled = false
        timestampLabel.drawsBackground = false
        timestampLabel.isEditable = false
        timestampLabel.translatesAutoresizingMaskIntoConstraints = false
        demoBubbleView.addSubview(timestampLabel)

        bubbleGradientLayer.startPoint = CGPoint(x: 0, y: 0)
        bubbleGradientLayer.endPoint = CGPoint(x: 1, y: 1)
        demoBubbleView.layer?.insertSublayer(bubbleGradientLayer, at: 0)

        arrowLayer.fillColor = ThemeColors.current.ntAct.cgColor
        let arrowPath = CGMutablePath()
        arrowPath.move(to: CGPoint(x: 0, y: 0))
        arrowPath.addLine(to: CGPoint(x: 8, y: -8))
        arrowPath.addLine(to: CGPoint(x: 16, y: 0))
        arrowPath.closeSubpath()
        arrowLayer.path = arrowPath
        arrowLayer.frame = CGRect(x: 0, y: 0, width: 16, height: 8)
        demoBubbleView.layer?.addSublayer(arrowLayer)

        previewButton.title = "预览测试"
        previewButton.bezelStyle = .rounded
        previewButton.font = NSFont.systemFont(ofSize: 11, weight: .medium)
        previewButton.isBordered = false
        previewButton.wantsLayer = true
        previewButton.layer?.cornerRadius = 5
        previewButton.layer?.borderWidth = 1
        previewButton.translatesAutoresizingMaskIntoConstraints = false
        previewButton.target = self
        previewButton.action = #selector(previewTest)
        centerContainer.addSubview(previewButton)

        hideButton.title = "隐藏"
        hideButton.bezelStyle = .rounded
        hideButton.font = NSFont.systemFont(ofSize: 11, weight: .medium)
        hideButton.isBordered = false
        hideButton.wantsLayer = true
        hideButton.layer?.cornerRadius = 5
        hideButton.layer?.borderWidth = 1
        hideButton.translatesAutoresizingMaskIntoConstraints = false
        hideButton.target = self
        hideButton.action = #selector(hideAction)
        centerContainer.addSubview(hideButton)

        applyConfirmButton.title = "确定应用"
        applyConfirmButton.bezelStyle = .rounded
        applyConfirmButton.font = NSFont.systemFont(ofSize: 11, weight: .medium)
        applyConfirmButton.isBordered = false
        applyConfirmButton.wantsLayer = true
        applyConfirmButton.layer?.cornerRadius = 5
        applyConfirmButton.translatesAutoresizingMaskIntoConstraints = false
        applyConfirmButton.target = self
        applyConfirmButton.action = #selector(applyConfirm)
        centerContainer.addSubview(applyConfirmButton)

        hintLabel.font = NSFont.systemFont(ofSize: 9)
        hintLabel.textColor = ThemeColors.current.txtMute
        hintLabel.isBezeled = false
        hintLabel.drawsBackground = false
        hintLabel.isEditable = false
        hintLabel.alignment = .center
        hintLabel.translatesAutoresizingMaskIntoConstraints = false
        centerContainer.addSubview(hintLabel)

        updateActionButtonThemes()

        NSLayoutConstraint.activate([
            centerContainer.centerXAnchor.constraint(equalTo: previewArea.centerXAnchor),
            centerContainer.centerYAnchor.constraint(equalTo: previewArea.centerYAnchor),
            centerContainer.leadingAnchor.constraint(greaterThanOrEqualTo: previewArea.leadingAnchor, constant: 20),
            centerContainer.trailingAnchor.constraint(lessThanOrEqualTo: previewArea.trailingAnchor, constant: -20),
            centerContainer.topAnchor.constraint(greaterThanOrEqualTo: previewArea.topAnchor, constant: 20),
            centerContainer.bottomAnchor.constraint(lessThanOrEqualTo: previewArea.bottomAnchor, constant: -20),

            demoBubbleView.topAnchor.constraint(equalTo: centerContainer.topAnchor),
            demoBubbleView.centerXAnchor.constraint(equalTo: centerContainer.centerXAnchor),
            demoBubbleView.widthAnchor.constraint(lessThanOrEqualToConstant: 300),

            petNameLabel.topAnchor.constraint(equalTo: demoBubbleView.topAnchor, constant: 12),
            petNameLabel.leadingAnchor.constraint(equalTo: demoBubbleView.leadingAnchor, constant: 16),
            petNameLabel.trailingAnchor.constraint(equalTo: demoBubbleView.trailingAnchor, constant: -16),

            demoTextLabel.topAnchor.constraint(equalTo: petNameLabel.bottomAnchor, constant: 2),
            demoTextLabel.leadingAnchor.constraint(equalTo: demoBubbleView.leadingAnchor, constant: 16),
            demoTextLabel.trailingAnchor.constraint(equalTo: demoBubbleView.trailingAnchor, constant: -16),
            demoTextLabel.bottomAnchor.constraint(equalTo: timestampLabel.topAnchor, constant: -2),

            timestampLabel.leadingAnchor.constraint(equalTo: demoBubbleView.leadingAnchor, constant: 16),
            timestampLabel.trailingAnchor.constraint(equalTo: demoBubbleView.trailingAnchor, constant: -16),
            timestampLabel.bottomAnchor.constraint(equalTo: demoBubbleView.bottomAnchor, constant: -8),

            previewButton.topAnchor.constraint(equalTo: demoBubbleView.bottomAnchor, constant: 22),
            previewButton.leadingAnchor.constraint(equalTo: centerContainer.leadingAnchor),
            previewButton.heightAnchor.constraint(equalToConstant: 28),

            hideButton.topAnchor.constraint(equalTo: demoBubbleView.bottomAnchor, constant: 22),
            hideButton.leadingAnchor.constraint(equalTo: previewButton.trailingAnchor, constant: 6),
            hideButton.heightAnchor.constraint(equalToConstant: 28),

            applyConfirmButton.topAnchor.constraint(equalTo: demoBubbleView.bottomAnchor, constant: 22),
            applyConfirmButton.leadingAnchor.constraint(equalTo: hideButton.trailingAnchor, constant: 6),
            applyConfirmButton.trailingAnchor.constraint(equalTo: centerContainer.trailingAnchor),
            applyConfirmButton.heightAnchor.constraint(equalToConstant: 28),

            hintLabel.topAnchor.constraint(equalTo: applyConfirmButton.bottomAnchor, constant: 8),
            hintLabel.centerXAnchor.constraint(equalTo: centerContainer.centerXAnchor),
            hintLabel.bottomAnchor.constraint(equalTo: centerContainer.bottomAnchor),
        ])
    }

    // MARK: - Constraints

    private func setupConstraints() {
        NSLayoutConstraint.activate([
            sidebarView.leadingAnchor.constraint(equalTo: leadingAnchor),
            sidebarView.topAnchor.constraint(equalTo: topAnchor),
            sidebarView.bottomAnchor.constraint(equalTo: bottomAnchor),
            sidebarView.widthAnchor.constraint(equalToConstant: 240),

            divider.leadingAnchor.constraint(equalTo: sidebarView.trailingAnchor),
            divider.topAnchor.constraint(equalTo: topAnchor),
            divider.bottomAnchor.constraint(equalTo: bottomAnchor),
            divider.widthAnchor.constraint(equalToConstant: 1),

            previewArea.leadingAnchor.constraint(equalTo: divider.trailingAnchor),
            previewArea.topAnchor.constraint(equalTo: topAnchor),
            previewArea.bottomAnchor.constraint(equalTo: bottomAnchor),
            previewArea.trailingAnchor.constraint(equalTo: trailingAnchor),

            searchField.topAnchor.constraint(equalTo: sidebarView.topAnchor, constant: 10),
            searchField.leadingAnchor.constraint(equalTo: sidebarView.leadingAnchor, constant: 10),
            searchField.trailingAnchor.constraint(equalTo: sidebarView.trailingAnchor, constant: -10),
            searchField.heightAnchor.constraint(equalToConstant: 30),

            countLabel.topAnchor.constraint(equalTo: searchField.bottomAnchor, constant: 6),
            countLabel.leadingAnchor.constraint(equalTo: sidebarView.leadingAnchor, constant: 10),

            importLabel.topAnchor.constraint(equalTo: searchField.bottomAnchor, constant: 6),
            importLabel.trailingAnchor.constraint(equalTo: sidebarView.trailingAnchor, constant: -10),
            importLabel.leadingAnchor.constraint(greaterThanOrEqualTo: countLabel.trailingAnchor, constant: 4),

            scrollView.topAnchor.constraint(equalTo: countLabel.bottomAnchor, constant: 6),
            scrollView.leadingAnchor.constraint(equalTo: sidebarView.leadingAnchor, constant: 10),
            scrollView.trailingAnchor.constraint(equalTo: sidebarView.trailingAnchor, constant: -10),
            scrollView.bottomAnchor.constraint(equalTo: sidebarView.bottomAnchor, constant: -50),
        ])

        for subview in sidebarView.subviews {
            if subview is NSView && subview != searchField && subview != scrollView && subview != countLabel && subview != importLabel {
                if subview.layer?.cornerRadius == 5 {
                    NSLayoutConstraint.activate([
                        subview.leadingAnchor.constraint(equalTo: sidebarView.leadingAnchor, constant: 10),
                        subview.trailingAnchor.constraint(equalTo: sidebarView.trailingAnchor, constant: -10),
                        subview.bottomAnchor.constraint(equalTo: sidebarView.bottomAnchor, constant: -10),
                        subview.heightAnchor.constraint(equalToConstant: 32),
                    ])
                    break
                }
            }
        }
    }

    // MARK: - Selection Logic

    private func selectBubbleStyle(_ style: BubbleStyle) {
        selectedStyle = style
        useCustomColors = false

        for item in itemViews {
            item.isSelected = (item.style.id == style.id)
        }

        idTextField.stringValue = "\(style.id)"
        updateDemoBubble()
    }

    private func updateDemoBubble() {
        let colors: (start: NSColor, end: NSColor)
        let arrowColor: NSColor

        if useCustomColors {
            colors = (start: bubbleColorWell.color, end: bubbleColorWell.color)
            arrowColor = bubbleColorWell.color
        } else {
            colors = selectedStyle.gradientColors
            arrowColor = selectedStyle.arrowColor
        }

        bubbleGradientLayer.colors = [colors.start.cgColor, colors.end.cgColor]
        bubbleGradientLayer.frame = demoBubbleView.bounds

        arrowLayer.fillColor = arrowColor.cgColor
        positionArrow()
    }

    private func positionArrow() {
        let bubbleBounds = demoBubbleView.bounds
        if bubbleBounds.width > 0 {
            arrowLayer.position = CGPoint(x: bubbleBounds.midX - 8, y: bubbleBounds.height - 8)
        }
    }

    override func layout() {
        super.layout()
        bubbleGradientLayer.frame = demoBubbleView.bounds
        positionArrow()
    }

    // MARK: - Actions

    @objc private func searchChanged() {
        let query = searchField.stringValue.lowercased()
        for item in itemViews {
            let matches = query.isEmpty ||
                "\(item.style.id)".contains(query) ||
                item.style.name.localizedLowercase.contains(query)
            item.isHidden = !matches
        }
    }

    @objc private func colorWellChanged() {
        useCustomColors = true
        updateDemoBubble()
    }

    @objc private func applyCustomColors() {
        useCustomColors = true
        updateDemoBubble()
    }

    @objc private func previewTest() {}

    @objc private func hideAction() {}

    @objc private func applyConfirm() {
        var settings = SettingsManager.current
        settings.balloonId = selectedStyle.id
        if useCustomColors {
            settings.balloonCustomBg = bubbleColorWell.color.hexString
            settings.balloonCustomTextColor = textColorWell.color.hexString
        } else {
            settings.balloonCustomBg = nil
            settings.balloonCustomTextColor = nil
        }
        SettingsManager.save(settings)
    }

    // MARK: - Theme

    @objc private func themeChanged() {
        let theme = ThemeColors.current

        sidebarView.layer?.backgroundColor = theme.winBg.cgColor

        countLabel.textColor = theme.txtDim
        importLabel.textColor = theme.txtMute

        divider.layer?.backgroundColor = theme.ntBrd.cgColor

        for subview in sidebarView.subviews {
            if subview is NSView && subview.layer?.cornerRadius == 5 {
                subview.layer?.backgroundColor = theme.ntBrd.cgColor
                break
            }
        }

        for item in itemViews {
            item.updateTheme()
        }

        updateActionButtonThemes()
        hintLabel.textColor = theme.txtMute

        if useCustomColors {
            bubbleColorWell.color = theme.ntAct
        }

        needsDisplay = true
    }

    private func updateActionButtonThemes() {
        let theme = ThemeColors.current

        for btn in [previewButton, hideButton] {
            btn.layer?.backgroundColor = theme.ntBrd.cgColor
            btn.layer?.borderColor = theme.ntBrd.cgColor
            btn.contentTintColor = theme.txtSec
        }

        applyConfirmButton.layer?.backgroundColor = theme.ntAct.cgColor
        applyConfirmButton.contentTintColor = .white

        applyButton.layer?.backgroundColor = theme.ntAct.cgColor
    }
}

// MARK: - NSColor Hex Extension

private extension NSColor {
    var hexString: String {
        guard let rgb = usingColorSpace(.sRGB) else { return "#FFFFFF" }
        let r = Int(rgb.redComponent * 255)
        let g = Int(rgb.greenComponent * 255)
        let b = Int(rgb.blueComponent * 255)
        return String(format: "#%02X%02X%02X", r, g, b)
    }
}