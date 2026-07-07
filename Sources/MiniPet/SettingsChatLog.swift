import AppKit

// MARK: - Custom Segmented Control (匹配 .gens CSS)

class SettingsSegmentedControl: NSView {
    private var segments: [SettingsSegmentButton] = []
    var selectedIndex: Int = 0
    var onSelect: ((Int) -> Void)?

    init(titles: [String], selected: Int = 0) {
        selectedIndex = selected
        super.init(frame: .zero)
        translatesAutoresizingMaskIntoConstraints = false

        let container = NSView()
        container.translatesAutoresizingMaskIntoConstraints = false
        container.wantsLayer = true
        container.layer?.cornerRadius = 3
        container.layer?.borderWidth = 1
        addSubview(container)

        NSLayoutConstraint.activate([
            container.topAnchor.constraint(equalTo: topAnchor),
            container.bottomAnchor.constraint(equalTo: bottomAnchor),
            container.leadingAnchor.constraint(equalTo: leadingAnchor),
            container.trailingAnchor.constraint(equalTo: trailingAnchor)
        ])

        var prev: NSView?
        for (i, title) in titles.enumerated() {
            let btn = SettingsSegmentButton(title: title, index: i, isActive: i == selected)
            btn.translatesAutoresizingMaskIntoConstraints = false
            btn.target = self
            btn.action = #selector(segmentTapped(_:))
            container.addSubview(btn)
            segments.append(btn)

            NSLayoutConstraint.activate([
                btn.topAnchor.constraint(equalTo: container.topAnchor),
                btn.bottomAnchor.constraint(equalTo: container.bottomAnchor)
            ])

            if let prev = prev {
                btn.leadingAnchor.constraint(equalTo: prev.trailingAnchor).isActive = true
                btn.widthAnchor.constraint(equalTo: prev.widthAnchor).isActive = true
            } else {
                btn.leadingAnchor.constraint(equalTo: container.leadingAnchor).isActive = true
            }
            prev = btn
        }
        prev?.trailingAnchor.constraint(equalTo: container.trailingAnchor).isActive = true
        updateColors()
    }

    required init?(coder: NSCoder) { fatalError() }

    @objc private func segmentTapped(_ sender: SettingsSegmentButton) {
        guard sender.index != selectedIndex else { return }
        segments[selectedIndex].isActive = false
        sender.isActive = true
        selectedIndex = sender.index
        onSelect?(selectedIndex)
    }

    func updateColors() {
        let tc = ThemeColors.current
        layer?.borderColor = tc.ntBrd.cgColor
        segments.forEach { $0.updateColors() }
    }
}

private class SettingsSegmentButton: NSButton {
    let index: Int
    var isActive: Bool {
        didSet { updateColors() }
    }

    init(title: String, index: Int, isActive: Bool) {
        self.index = index
        self.isActive = isActive
        super.init(frame: .zero)
        self.title = title
        self.font = NSFont.systemFont(ofSize: 10)
        self.isBordered = false
        self.wantsLayer = true
        self.layer?.cornerRadius = 0
        updateColors()
    }

    required init?(coder: NSCoder) { fatalError() }

    func updateColors() {
        let tc = ThemeColors.current
        if isActive {
            layer?.backgroundColor = NSColor(calibratedRed: 94/255, green: 156/255, blue: 255/255, alpha: 0.15).cgColor
            attributedTitle = NSAttributedString(string: title, attributes: [
                .foregroundColor: tc.ntAct,
                .font: NSFont.systemFont(ofSize: 10)
            ])
        } else {
            layer?.backgroundColor = NSColor(calibratedWhite: 1, alpha: 0.03).cgColor
            attributedTitle = NSAttributedString(string: title, attributes: [
                .foregroundColor: tc.txtDim,
                .font: NSFont.systemFont(ofSize: 10)
            ])
        }
    }
}

// MARK: - Card View (匹配 .hc CSS)

private class SettingsCardView: NSView {
    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        layer?.cornerRadius = 8
        layer?.borderWidth = 1
    }

    required init?(coder: NSCoder) { fatalError() }

    func updateColors() {
        let tc = ThemeColors.current
        layer?.backgroundColor = tc.ntBg.cgColor
        layer?.borderColor = tc.ntBrd.cgColor
    }
}

// MARK: - Popup Button (匹配 .pop CSS)

private class SettingsPopupButton: NSPopUpButton {
    override init(frame: NSRect, pullsDown: Bool = false) {
        super.init(frame: frame, pullsDown: pullsDown)
        wantsLayer = true
        layer?.cornerRadius = 3
        layer?.borderWidth = 1
        font = NSFont.systemFont(ofSize: 10)
        updateColors()
    }

    required init?(coder: NSCoder) { fatalError() }

    override func updateLayer() { updateColors() }

    func updateColors() {
        let tc = ThemeColors.current
        layer?.backgroundColor = NSColor(calibratedWhite: 1, alpha: 0.03).cgColor
        layer?.borderColor = tc.ntBrd.cgColor
    }

    override func draw(_ dirtyRect: NSRect) {
        // Override default drawing to prevent system styling
    }
}

// MARK: - Action Button (匹配 .ab CSS)

private class SettingsActionButton: NSButton {
    enum Style { case primary, secondary }

    private let btnStyle: Style

    init(title: String, style: Style = .secondary) {
        self.btnStyle = style
        super.init(frame: .zero)
        self.title = title
        self.font = NSFont.systemFont(ofSize: 10, weight: .medium)
        self.isBordered = false
        self.wantsLayer = true
        self.layer?.cornerRadius = 3
        updateColors()
    }

    required init?(coder: NSCoder) { fatalError() }

    func updateColors() {
        let tc = ThemeColors.current
        switch btnStyle {
        case .primary:
            layer?.backgroundColor = tc.ntAct.cgColor
            layer?.borderColor = nil
            layer?.borderWidth = 0
            attributedTitle = NSAttributedString(string: title, attributes: [
                .foregroundColor: NSColor.white,
                .font: NSFont.systemFont(ofSize: 10, weight: .medium)
            ])
        case .secondary:
            layer?.backgroundColor = tc.ntBg.cgColor
            layer?.borderColor = tc.ntBrd.cgColor
            layer?.borderWidth = 1
            attributedTitle = NSAttributedString(string: title, attributes: [
                .foregroundColor: tc.txtSec,
                .font: NSFont.systemFont(ofSize: 10, weight: .medium)
            ])
        }
    }
}

// MARK: - Toggle Switch (匹配 .nss CSS)

private class SettingsToggle: NSView {
    private let checkbox = NSButton(checkboxWithTitle: "", target: nil, action: nil)
    private let trackView = NSView()
    private let knobView = NSView()

    var isOn: Bool {
        get { checkbox.state == .on }
        set { checkbox.state = newValue ? .on : .off; updateKnob() }
    }
    var action: (() -> Void)?

    override init(frame: NSRect) {
        super.init(frame: frame)
        translatesAutoresizingMaskIntoConstraints = false

        checkbox.isHidden = true
        checkbox.target = self
        checkbox.action = #selector(toggleChanged)
        addSubview(checkbox)

        trackView.wantsLayer = true
        trackView.layer?.cornerRadius = 8
        addSubview(trackView)

        knobView.wantsLayer = true
        knobView.layer?.cornerRadius = 6
        addSubview(knobView)

        trackView.translatesAutoresizingMaskIntoConstraints = false
        knobView.translatesAutoresizingMaskIntoConstraints = false

        NSLayoutConstraint.activate([
            trackView.widthAnchor.constraint(equalToConstant: 28),
            trackView.heightAnchor.constraint(equalToConstant: 16),
            trackView.centerYAnchor.constraint(equalTo: centerYAnchor),
            trackView.centerXAnchor.constraint(equalTo: centerXAnchor),

            knobView.widthAnchor.constraint(equalToConstant: 12),
            knobView.heightAnchor.constraint(equalToConstant: 12),
            knobView.centerYAnchor.constraint(equalTo: centerYAnchor),
            knobView.leadingAnchor.constraint(equalTo: trackView.leadingAnchor, constant: 2)
        ])

        updateColors()
    }

    required init?(coder: NSCoder) { fatalError() }

    override func mouseDown(with event: NSEvent) {
        isOn.toggle()
        action?()
    }

    @objc private func toggleChanged() {
        updateKnob()
        action?()
    }

    private func updateKnob() {
        let tc = ThemeColors.current
        trackView.layer?.backgroundColor = isOn ? tc.ntAct.cgColor : tc.ntBrd.cgColor
        knobView.layer?.backgroundColor = isOn ? NSColor.white.cgColor : NSColor(calibratedWhite: 1, alpha: 0.3).cgColor
        knobView.frame.origin.x = isOn ? 14 : 2
    }

    func updateColors() {
        updateKnob()
    }
}

// MARK: - ChatLogSettingsSubview

class ChatLogSettingsSubview: NSView {
    // Shortcut section
    private let openHotkeyButton = SettingsPopupButton(frame: .zero)
    private let hideHotkeyButton = SettingsPopupButton(frame: .zero)
    private let voiceHotkeyButton = SettingsPopupButton(frame: .zero)

    // Appearance section
    private let themeSegmented = SettingsSegmentedControl(titles: ["跟随系统", "浅色", "深色"])
    private let fontPopup = SettingsPopupButton(frame: .zero)
    private let alignmentSegmented = SettingsSegmentedControl(titles: ["左右", "居中"])
    private let opacitySlider = NSSlider(value: 0.85, minValue: 0.3, maxValue: 1.0, target: nil, action: nil)
    private let opacityLabel = NSTextField(labelWithString: "85%")

    // Voice input section
    private let voiceToggle = SettingsToggle(frame: .zero)
    private let enginePopup = SettingsPopupButton(frame: .zero)
    private let languagePopup = SettingsPopupButton(frame: .zero)
    private var voiceControlRows: [NSView] = []

    override init(frame: NSRect) {
        super.init(frame: frame)
        translatesAutoresizingMaskIntoConstraints = false
        setupUI()
        loadSettings()
        setupBindings()

        NotificationCenter.default.addObserver(
            self, selector: #selector(themeUpdated),
            name: .themeDidChange, object: nil
        )
    }

    required init?(coder: NSCoder) { fatalError() }

    deinit {
        NotificationCenter.default.removeObserver(self)
    }

    // MARK: - UI Setup

    private func setupUI() {
        let scrollView = NSScrollView()
        scrollView.translatesAutoresizingMaskIntoConstraints = false
        scrollView.hasVerticalScroller = true
        scrollView.hasHorizontalScroller = false
        scrollView.borderType = .noBorder
        scrollView.autohidesScrollers = true
        addSubview(scrollView)

        let contentView = NSView()
        contentView.translatesAutoresizingMaskIntoConstraints = false
        scrollView.documentView = contentView

        NSLayoutConstraint.activate([
            scrollView.topAnchor.constraint(equalTo: topAnchor),
            scrollView.bottomAnchor.constraint(equalTo: bottomAnchor),
            scrollView.leadingAnchor.constraint(equalTo: leadingAnchor),
            scrollView.trailingAnchor.constraint(equalTo: trailingAnchor),

            contentView.topAnchor.constraint(equalTo: scrollView.topAnchor),
            contentView.bottomAnchor.constraint(equalTo: scrollView.bottomAnchor),
            contentView.leadingAnchor.constraint(equalTo: scrollView.leadingAnchor),
            contentView.trailingAnchor.constraint(equalTo: scrollView.trailingAnchor),
            contentView.widthAnchor.constraint(equalTo: scrollView.widthAnchor)
        ])

        let stack = NSStackView()
        stack.translatesAutoresizingMaskIntoConstraints = false
        stack.orientation = .vertical
        stack.spacing = 12
        stack.edgeInsets = NSEdgeInsets(top: 14, left: 14, bottom: 14, right: 14)
        contentView.addSubview(stack)

        NSLayoutConstraint.activate([
            stack.topAnchor.constraint(equalTo: contentView.topAnchor),
            stack.bottomAnchor.constraint(equalTo: contentView.bottomAnchor),
            stack.leadingAnchor.constraint(equalTo: contentView.leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: contentView.trailingAnchor)
        ])

        // Card 1: 快捷键
        let shortcutCard = buildShortcutCard()
        stack.addArrangedSubview(shortcutCard)

        // Card 2: 外观
        let appearanceCard = buildAppearanceCard()
        stack.addArrangedSubview(appearanceCard)

        // Card 3: 语音输入
        let voiceCard = buildVoiceCard()
        stack.addArrangedSubview(voiceCard)
    }

    // MARK: - Card 1: 快捷键

    private func buildShortcutCard() -> NSView {
        let card = SettingsCardView(frame: .zero)

        // Header
        let header = NSTextField(labelWithString: "⌨️ 快捷键")
        header.font = NSFont.systemFont(ofSize: 12, weight: .semibold)
        header.translatesAutoresizingMaskIntoConstraints = false

        // Description
        let desc = NSTextField(labelWithString: "对话记录通过快捷键从桌面呼出，轻量独立。")
        desc.font = NSFont.systemFont(ofSize: 10)
        desc.translatesAutoresizingMaskIntoConstraints = false

        // Shortcut rows
        let openRow = buildShortcutRow(label: "呼出", popup: openHotkeyButton, hasRecord: true)
        let hideRow = buildShortcutRow(label: "隐藏", popup: hideHotkeyButton, hasRecord: false)
        let voiceRow = buildShortcutRow(label: "语音输入", popup: voiceHotkeyButton, hasRecord: true)

        card.addSubview(header)
        card.addSubview(desc)
        card.addSubview(openRow)
        card.addSubview(hideRow)
        card.addSubview(voiceRow)

        header.translatesAutoresizingMaskIntoConstraints = false
        desc.translatesAutoresizingMaskIntoConstraints = false
        openRow.translatesAutoresizingMaskIntoConstraints = false
        hideRow.translatesAutoresizingMaskIntoConstraints = false
        voiceRow.translatesAutoresizingMaskIntoConstraints = false

        NSLayoutConstraint.activate([
            header.topAnchor.constraint(equalTo: card.topAnchor, constant: 12),
            header.leadingAnchor.constraint(equalTo: card.leadingAnchor, constant: 14),
            header.trailingAnchor.constraint(equalTo: card.trailingAnchor, constant: -14),

            desc.topAnchor.constraint(equalTo: header.bottomAnchor, constant: -4),
            desc.leadingAnchor.constraint(equalTo: card.leadingAnchor, constant: 14),
            desc.trailingAnchor.constraint(equalTo: card.trailingAnchor, constant: -14),

            openRow.topAnchor.constraint(equalTo: desc.bottomAnchor, constant: 8),
            openRow.leadingAnchor.constraint(equalTo: card.leadingAnchor, constant: 14),
            openRow.trailingAnchor.constraint(equalTo: card.trailingAnchor, constant: -14),

            hideRow.topAnchor.constraint(equalTo: openRow.bottomAnchor, constant: 4),
            hideRow.leadingAnchor.constraint(equalTo: card.leadingAnchor, constant: 14),
            hideRow.trailingAnchor.constraint(equalTo: card.trailingAnchor, constant: -14),

            voiceRow.topAnchor.constraint(equalTo: hideRow.bottomAnchor, constant: 4),
            voiceRow.leadingAnchor.constraint(equalTo: card.leadingAnchor, constant: 14),
            voiceRow.trailingAnchor.constraint(equalTo: card.trailingAnchor, constant: -14),
            voiceRow.bottomAnchor.constraint(equalTo: card.bottomAnchor, constant: -12)
        ])

        return card
    }

    private func buildShortcutRow(label: String, popup: SettingsPopupButton, hasRecord: Bool) -> NSView {
        let row = NSView()
        row.translatesAutoresizingMaskIntoConstraints = false

        let labelField = NSTextField(labelWithString: label)
        labelField.font = NSFont.systemFont(ofSize: 10)
        labelField.translatesAutoresizingMaskIntoConstraints = false
        labelField.setContentHuggingPriority(.required, for: .horizontal)
        labelField.setContentCompressionResistancePriority(.required, for: .horizontal)

        row.addSubview(labelField)
        row.addSubview(popup)

        var constraints = [
            labelField.centerYAnchor.constraint(equalTo: row.centerYAnchor),
            labelField.leadingAnchor.constraint(equalTo: row.leadingAnchor),
            labelField.widthAnchor.constraint(greaterThanOrEqualToConstant: 34),

            popup.centerYAnchor.constraint(equalTo: row.centerYAnchor),
            popup.leadingAnchor.constraint(equalTo: labelField.trailingAnchor, constant: 10),
            popup.heightAnchor.constraint(equalToConstant: 22)
        ]

        var lastView: NSView = popup

        if hasRecord {
            let recordBtn = SettingsActionButton(title: "录制", style: .secondary)
            recordBtn.translatesAutoresizingMaskIntoConstraints = false
            row.addSubview(recordBtn)

            constraints.append(contentsOf: [
                recordBtn.centerYAnchor.constraint(equalTo: row.centerYAnchor),
                recordBtn.leadingAnchor.constraint(equalTo: popup.trailingAnchor, constant: 6),
                recordBtn.trailingAnchor.constraint(equalTo: row.trailingAnchor),
                recordBtn.heightAnchor.constraint(equalToConstant: 22),
                recordBtn.widthAnchor.constraint(greaterThanOrEqualToConstant: 44)
            ])
            lastView = recordBtn
        } else {
            constraints.append(popup.trailingAnchor.constraint(equalTo: row.trailingAnchor))
        }

        NSLayoutConstraint.activate(constraints)
        return row
    }

    // MARK: - Card 2: 外观

    private func buildAppearanceCard() -> NSView {
        let card = SettingsCardView(frame: .zero)

        // Header
        let header = NSTextField(labelWithString: "🎨 外观")
        header.font = NSFont.systemFont(ofSize: 12, weight: .semibold)
        header.translatesAutoresizingMaskIntoConstraints = false

        // Row: 主题
        let themeRow = buildSegmentedRow(label: "主题", control: themeSegmented)
        themeRow.translatesAutoresizingMaskIntoConstraints = false

        // Row: 字体
        fontPopup.addItems(withTitles: ["SF Pro · 14px", "SF Pro · 12px", "SF Pro · 16px", "Helvetica · 14px", "PingFang · 14px"])
        fontPopup.selectItem(at: 0)
        let fontRow = buildPopupRow(label: "字体", popup: fontPopup)
        fontRow.translatesAutoresizingMaskIntoConstraints = false

        // Row: 消息对齐
        let alignRow = buildSegmentedRow(label: "消息对齐", control: alignmentSegmented)
        alignRow.translatesAutoresizingMaskIntoConstraints = false

        // Row: 透明度 (matching .zr CSS)
        let opacityRow = buildSliderRow()
        opacityRow.translatesAutoresizingMaskIntoConstraints = false

        card.addSubview(header)
        card.addSubview(themeRow)
        card.addSubview(fontRow)
        card.addSubview(alignRow)
        card.addSubview(opacityRow)

        header.translatesAutoresizingMaskIntoConstraints = false

        NSLayoutConstraint.activate([
            header.topAnchor.constraint(equalTo: card.topAnchor, constant: 12),
            header.leadingAnchor.constraint(equalTo: card.leadingAnchor, constant: 14),
            header.trailingAnchor.constraint(equalTo: card.trailingAnchor, constant: -14),

            themeRow.topAnchor.constraint(equalTo: header.bottomAnchor, constant: 8),
            themeRow.leadingAnchor.constraint(equalTo: card.leadingAnchor, constant: 14),
            themeRow.trailingAnchor.constraint(equalTo: card.trailingAnchor, constant: -14),

            fontRow.topAnchor.constraint(equalTo: themeRow.bottomAnchor, constant: 4),
            fontRow.leadingAnchor.constraint(equalTo: card.leadingAnchor, constant: 14),
            fontRow.trailingAnchor.constraint(equalTo: card.trailingAnchor, constant: -14),

            alignRow.topAnchor.constraint(equalTo: fontRow.bottomAnchor, constant: 4),
            alignRow.leadingAnchor.constraint(equalTo: card.leadingAnchor, constant: 14),
            alignRow.trailingAnchor.constraint(equalTo: card.trailingAnchor, constant: -14),

            opacityRow.topAnchor.constraint(equalTo: alignRow.bottomAnchor, constant: 4),
            opacityRow.leadingAnchor.constraint(equalTo: card.leadingAnchor, constant: 14),
            opacityRow.trailingAnchor.constraint(equalTo: card.trailingAnchor, constant: -14),
            opacityRow.bottomAnchor.constraint(equalTo: card.bottomAnchor, constant: -12)
        ])

        return card
    }

    private func buildSegmentedRow(label: String, control: SettingsSegmentedControl) -> NSView {
        let row = NSView()

        let labelField = NSTextField(labelWithString: label)
        labelField.font = NSFont.systemFont(ofSize: 10)
        labelField.translatesAutoresizingMaskIntoConstraints = false
        labelField.setContentHuggingPriority(.required, for: .horizontal)
        labelField.setContentCompressionResistancePriority(.required, for: .horizontal)

        row.addSubview(labelField)
        row.addSubview(control)

        NSLayoutConstraint.activate([
            labelField.centerYAnchor.constraint(equalTo: row.centerYAnchor),
            labelField.leadingAnchor.constraint(equalTo: row.leadingAnchor),
            labelField.widthAnchor.constraint(greaterThanOrEqualToConstant: 34),

            control.centerYAnchor.constraint(equalTo: row.centerYAnchor),
            control.leadingAnchor.constraint(equalTo: labelField.trailingAnchor, constant: 10),
            control.trailingAnchor.constraint(lessThanOrEqualTo: row.trailingAnchor),
            control.heightAnchor.constraint(equalToConstant: 20),

            row.heightAnchor.constraint(greaterThanOrEqualToConstant: 24)
        ])

        return row
    }

    private func buildPopupRow(label: String, popup: SettingsPopupButton) -> NSView {
        let row = NSView()

        let labelField = NSTextField(labelWithString: label)
        labelField.font = NSFont.systemFont(ofSize: 10)
        labelField.translatesAutoresizingMaskIntoConstraints = false
        labelField.setContentHuggingPriority(.required, for: .horizontal)
        labelField.setContentCompressionResistancePriority(.required, for: .horizontal)

        row.addSubview(labelField)
        row.addSubview(popup)

        NSLayoutConstraint.activate([
            labelField.centerYAnchor.constraint(equalTo: row.centerYAnchor),
            labelField.leadingAnchor.constraint(equalTo: row.leadingAnchor),
            labelField.widthAnchor.constraint(greaterThanOrEqualToConstant: 34),

            popup.centerYAnchor.constraint(equalTo: row.centerYAnchor),
            popup.leadingAnchor.constraint(equalTo: labelField.trailingAnchor, constant: 10),
            popup.trailingAnchor.constraint(equalTo: row.trailingAnchor),
            popup.heightAnchor.constraint(equalToConstant: 22),

            row.heightAnchor.constraint(greaterThanOrEqualToConstant: 26)
        ])

        return row
    }

    private func buildSliderRow() -> NSView {
        let row = NSView()

        let labelField = NSTextField(labelWithString: "透明度")
        labelField.font = NSFont.systemFont(ofSize: 10)
        labelField.translatesAutoresizingMaskIntoConstraints = false
        labelField.setContentHuggingPriority(.required, for: .horizontal)
        labelField.setContentCompressionResistancePriority(.required, for: .horizontal)

        opacitySlider.translatesAutoresizingMaskIntoConstraints = false
        opacitySlider.isContinuous = true
        opacitySlider.target = self
        opacitySlider.action = #selector(opacityChanged)

        opacityLabel.font = NSFont.systemFont(ofSize: 9)
        opacityLabel.textColor = ThemeColors.current.txtDim
        opacityLabel.translatesAutoresizingMaskIntoConstraints = false
        opacityLabel.setContentHuggingPriority(.required, for: .horizontal)

        row.addSubview(labelField)
        row.addSubview(opacitySlider)
        row.addSubview(opacityLabel)

        NSLayoutConstraint.activate([
            labelField.centerYAnchor.constraint(equalTo: row.centerYAnchor),
            labelField.leadingAnchor.constraint(equalTo: row.leadingAnchor),
            labelField.widthAnchor.constraint(greaterThanOrEqualToConstant: 34),

            opacitySlider.centerYAnchor.constraint(equalTo: row.centerYAnchor),
            opacitySlider.leadingAnchor.constraint(equalTo: labelField.trailingAnchor, constant: 10),
            opacitySlider.widthAnchor.constraint(equalToConstant: 100),

            opacityLabel.centerYAnchor.constraint(equalTo: row.centerYAnchor),
            opacityLabel.leadingAnchor.constraint(equalTo: opacitySlider.trailingAnchor, constant: 6),
            opacityLabel.trailingAnchor.constraint(equalTo: row.trailingAnchor),

            row.heightAnchor.constraint(greaterThanOrEqualToConstant: 24)
        ])

        return row
    }

    // MARK: - Card 3: 语音输入

    private func buildVoiceCard() -> NSView {
        let card = SettingsCardView(frame: .zero)

        // Header with badge and toggle
        let headerRow = NSView()
        headerRow.translatesAutoresizingMaskIntoConstraints = false

        let headerLabel = NSTextField(labelWithString: "🎤 语音输入")
        headerLabel.font = NSFont.systemFont(ofSize: 12, weight: .semibold)
        headerLabel.translatesAutoresizingMaskIntoConstraints = false
        headerLabel.setContentHuggingPriority(.defaultLow, for: .horizontal)
        headerLabel.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)

        let badgeLabel = NSTextField(labelWithString: "可选")
        badgeLabel.font = NSFont.systemFont(ofSize: 9, weight: .medium)
        badgeLabel.translatesAutoresizingMaskIntoConstraints = false
        badgeLabel.setContentHuggingPriority(.required, for: .horizontal)
        badgeLabel.setContentCompressionResistancePriority(.required, for: .horizontal)

        let spacer = NSView()
        spacer.translatesAutoresizingMaskIntoConstraints = false
        spacer.setContentHuggingPriority(.defaultLow, for: .horizontal)

        let switchView = NSView()
        switchView.translatesAutoresizingMaskIntoConstraints = false
        switchView.addSubview(voiceToggle)

        NSLayoutConstraint.activate([
            voiceToggle.topAnchor.constraint(equalTo: switchView.topAnchor),
            voiceToggle.bottomAnchor.constraint(equalTo: switchView.bottomAnchor),
            voiceToggle.leadingAnchor.constraint(equalTo: switchView.leadingAnchor),
            voiceToggle.trailingAnchor.constraint(equalTo: switchView.trailingAnchor)
        ])

        headerRow.addSubview(headerLabel)
        headerRow.addSubview(badgeLabel)
        headerRow.addSubview(spacer)
        headerRow.addSubview(switchView)

        NSLayoutConstraint.activate([
            headerLabel.topAnchor.constraint(equalTo: headerRow.topAnchor),
            headerLabel.bottomAnchor.constraint(equalTo: headerRow.bottomAnchor),
            headerLabel.leadingAnchor.constraint(equalTo: headerRow.leadingAnchor),

            badgeLabel.centerYAnchor.constraint(equalTo: headerRow.centerYAnchor),
            badgeLabel.leadingAnchor.constraint(equalTo: headerLabel.trailingAnchor, constant: 6),

            spacer.leadingAnchor.constraint(equalTo: badgeLabel.trailingAnchor),
            spacer.trailingAnchor.constraint(equalTo: switchView.leadingAnchor),

            switchView.centerYAnchor.constraint(equalTo: headerRow.centerYAnchor),
            switchView.trailingAnchor.constraint(equalTo: headerRow.trailingAnchor),
            switchView.widthAnchor.constraint(equalToConstant: 28),
            switchView.heightAnchor.constraint(equalToConstant: 16)
        ])

        // Engine row
        enginePopup.addItems(withTitles: ["macOS 系统语音", "Whisper", "Azure 语音"])
        enginePopup.selectItem(at: 0)
        let engineRow = buildPopupRow(label: "引擎", popup: enginePopup)
        engineRow.translatesAutoresizingMaskIntoConstraints = false

        // Language row
        languagePopup.addItems(withTitles: ["中文（普通话）", "中文（粤语）", "English", "日本語"])
        languagePopup.selectItem(at: 0)
        let languageRow = buildPopupRow(label: "语言", popup: languagePopup)
        languageRow.translatesAutoresizingMaskIntoConstraints = false

        card.addSubview(headerRow)
        card.addSubview(engineRow)
        card.addSubview(languageRow)

        voiceControlRows = [engineRow, languageRow]

        NSLayoutConstraint.activate([
            headerRow.topAnchor.constraint(equalTo: card.topAnchor, constant: 12),
            headerRow.leadingAnchor.constraint(equalTo: card.leadingAnchor, constant: 14),
            headerRow.trailingAnchor.constraint(equalTo: card.trailingAnchor, constant: -14),

            engineRow.topAnchor.constraint(equalTo: headerRow.bottomAnchor, constant: 8),
            engineRow.leadingAnchor.constraint(equalTo: card.leadingAnchor, constant: 14),
            engineRow.trailingAnchor.constraint(equalTo: card.trailingAnchor, constant: -14),

            languageRow.topAnchor.constraint(equalTo: engineRow.bottomAnchor, constant: 4),
            languageRow.leadingAnchor.constraint(equalTo: card.leadingAnchor, constant: 14),
            languageRow.trailingAnchor.constraint(equalTo: card.trailingAnchor, constant: -14),
            languageRow.bottomAnchor.constraint(equalTo: card.bottomAnchor, constant: -12)
        ])

        return card
    }

    // MARK: - Load Settings

    private func loadSettings() {
        let settings = SettingsManager.current

        openHotkeyButton.removeAllItems()
        openHotkeyButton.addItems(withTitles: ["⌘ + ⇧ + Space", "⌘ + ⇧ + O", "⌘ + ⇧ + L", "⌥ + Space", "F1"])
        openHotkeyButton.selectItem(withTitle: settings.chatShortcutOpen) ?? openHotkeyButton.selectItem(at: 0)

        hideHotkeyButton.removeAllItems()
        hideHotkeyButton.addItems(withTitles: ["Esc", "⌘ + W", "⌘ + ⇧ + H"])
        hideHotkeyButton.selectItem(at: 0)

        voiceHotkeyButton.removeAllItems()
        voiceHotkeyButton.addItems(withTitles: ["⌘ + ⇧ + M", "⌘ + ⇧ + V", "⌥ + M", "F2"])
        voiceHotkeyButton.selectItem(withTitle: settings.chatShortcutVoice) ?? voiceHotkeyButton.selectItem(at: 0)

        // Appearance
        switch settings.chatTheme {
        case "light": themeSegmented.selectedIndex = 1
        case "dark": themeSegmented.selectedIndex = 2
        default: themeSegmented.selectedIndex = 0
        }
        themeSegmented.updateColors()

        alignmentSegmented.selectedIndex = settings.chatAlignment == "center" ? 1 : 0
        alignmentSegmented.updateColors()

        opacitySlider.doubleValue = settings.chatOpacity
        opacityLabel.stringValue = "\(Int(round(settings.chatOpacity * 100)))%"

        // Voice
        voiceToggle.isOn = settings.voiceEnabled
        updateVoiceControlVisibility()

        enginePopup.selectItem(withTitle: settings.voiceEngine) ?? enginePopup.selectItem(at: 0)
        languagePopup.selectItem(withTitle: settings.voiceLanguage) ?? languagePopup.selectItem(at: 0)
    }

    // MARK: - Bindings

    private func setupBindings() {
        themeSegmented.onSelect = { [weak self] index in
            let map = ["system", "light", "dark"]
            SettingsManager.current.chatTheme = map[index]
            SettingsManager.save(SettingsManager.current)
        }

        alignmentSegmented.onSelect = { [weak self] index in
            SettingsManager.current.chatAlignment = index == 0 ? "leftRight" : "center"
            SettingsManager.save(SettingsManager.current)
        }

        voiceToggle.action = { [weak self] in
            SettingsManager.current.voiceEnabled = self?.voiceToggle.isOn ?? false
            SettingsManager.save(SettingsManager.current)
            self?.updateVoiceControlVisibility()
        }

        openHotkeyButton.target = self
        openHotkeyButton.action = #selector(shortcutChanged)

        voiceHotkeyButton.target = self
        voiceHotkeyButton.action = #selector(shortcutChanged)
    }

    @objc private func shortcutChanged(_ sender: NSPopUpButton) {
        if sender == openHotkeyButton {
            SettingsManager.current.chatShortcutOpen = sender.titleOfSelectedItem ?? "⌘ + ⇧ + Space"
        } else if sender == voiceHotkeyButton {
            SettingsManager.current.chatShortcutVoice = sender.titleOfSelectedItem ?? "⌘ + ⇧ + M"
        }
        SettingsManager.save(SettingsManager.current)
    }

    @objc private func opacityChanged(_ sender: NSSlider) {
        let val = round(sender.doubleValue / 0.05) * 0.05
        sender.doubleValue = val
        let pct = Int(round(val * 100))
        opacityLabel.stringValue = "\(pct)%"
        SettingsManager.current.chatOpacity = val
        SettingsManager.save(SettingsManager.current)
    }

    private func updateVoiceControlVisibility() {
        voiceControlRows.forEach { $0.isHidden = !voiceToggle.isOn }
    }

    @objc private func themeUpdated() {
        updateColors()
    }

    func updateColors() {
        let tc = ThemeColors.current
        opacityLabel.textColor = tc.txtDim

        // Recursively update all subviews that support color updates
        func updateSubview(_ view: NSView) {
            if let card = view as? SettingsCardView { card.updateColors() }
            if let popup = view as? SettingsPopupButton { popup.updateColors() }
            if let btn = view as? SettingsActionButton { btn.updateColors() }
            if let seg = view as? SettingsSegmentedControl { seg.updateColors() }
            if let toggle = view as? SettingsToggle { toggle.updateColors() }

            // Update label colors based on context
            if let label = view as? NSTextField, !label.isEditable {
                // Reset colors where needed; label color is intrinsic
            }

            for sub in view.subviews {
                updateSubview(sub)
            }
        }
        updateSubview(self)
    }
}

// MARK: - Appearance Observation (handled by NSView's inherent NSAppearanceCustomization)