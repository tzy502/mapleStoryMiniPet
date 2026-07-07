import AppKit

// MARK: - AI Settings Subview

/// "AI 设置" 子视图，全宽单列滚动布局，包含三个卡片区块：
/// 1. 🔗 AI 设置 关联 — 连接状态 + Hermes 状态指示
/// 2. 📋 反应规则 — 关键词→动画映射表
/// 3. 💬 最近对话 — 最近对话记录
class AISettingsSubview: NSView {

    // MARK: - Subviews

    private let scrollView = NSScrollView()
    private let contentView = NSView()
    private let hermesCard = HermesConnectionCard()
    private let reactionCard = ReactionRuleCard()
    private let recentChatCard = RecentChatCard()

    // MARK: - Init

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true
        setupLayout()
        observeThemeChanges()
    }

    required init?(coder: NSCoder) { fatalError() }

    // MARK: - Layout

    private func setupLayout() {
        scrollView.hasVerticalScroller = true
        scrollView.hasHorizontalScroller = false
        scrollView.autohidesScrollers = true
        scrollView.drawsBackground = false
        scrollView.translatesAutoresizingMaskIntoConstraints = false
        addSubview(scrollView)

        contentView.wantsLayer = true
        contentView.translatesAutoresizingMaskIntoConstraints = false
        scrollView.documentView = contentView

        // 三个卡片垂直排列
        hermesCard.translatesAutoresizingMaskIntoConstraints = false
        reactionCard.translatesAutoresizingMaskIntoConstraints = false
        recentChatCard.translatesAutoresizingMaskIntoConstraints = false
        contentView.addSubview(hermesCard)
        contentView.addSubview(reactionCard)
        contentView.addSubview(recentChatCard)

        let padding: CGFloat = 14

        NSLayoutConstraint.activate([
            scrollView.topAnchor.constraint(equalTo: topAnchor),
            scrollView.leadingAnchor.constraint(equalTo: leadingAnchor),
            scrollView.trailingAnchor.constraint(equalTo: trailingAnchor),
            scrollView.bottomAnchor.constraint(equalTo: bottomAnchor),

            contentView.topAnchor.constraint(equalTo: scrollView.topAnchor),
            contentView.leadingAnchor.constraint(equalTo: scrollView.leadingAnchor),
            contentView.trailingAnchor.constraint(equalTo: scrollView.trailingAnchor),
            contentView.bottomAnchor.constraint(equalTo: scrollView.bottomAnchor),
            contentView.widthAnchor.constraint(equalTo: scrollView.widthAnchor),

            hermesCard.topAnchor.constraint(equalTo: contentView.topAnchor, constant: 0),
            hermesCard.leadingAnchor.constraint(equalTo: contentView.leadingAnchor, constant: padding),
            hermesCard.trailingAnchor.constraint(equalTo: contentView.trailingAnchor, constant: -padding),

            reactionCard.topAnchor.constraint(equalTo: hermesCard.bottomAnchor, constant: 12),
            reactionCard.leadingAnchor.constraint(equalTo: contentView.leadingAnchor, constant: padding),
            reactionCard.trailingAnchor.constraint(equalTo: contentView.trailingAnchor, constant: -padding),

            recentChatCard.topAnchor.constraint(equalTo: reactionCard.bottomAnchor, constant: 12),
            recentChatCard.leadingAnchor.constraint(equalTo: contentView.leadingAnchor, constant: padding),
            recentChatCard.trailingAnchor.constraint(equalTo: contentView.trailingAnchor, constant: -padding),
            recentChatCard.bottomAnchor.constraint(equalTo: contentView.bottomAnchor, constant: -12),
        ])
    }

    // MARK: - Theme

    private func observeThemeChanges() {
        NotificationCenter.default.addObserver(self, selector: #selector(themeChanged), name: .themeDidChange, object: nil)
    }

    @objc private func themeChanged() {
        hermesCard.updateColors()
        reactionCard.updateColors()
        recentChatCard.updateColors()
    }
}

// MARK: - Hermes Connection Card

private class HermesConnectionCard: NSView {

    private let headerLabel = NSTextField(labelWithString: "")
    private let badgeLabel = NSTextField(labelWithString: "")
    private let descLabel = NSTextField(labelWithString: "")
    private let statusDot = NSView()
    private let statusDotInner = NSView()
    private let nameLabel = NSTextField(labelWithString: "")
    private let statusLabel = NSTextField(labelWithString: "")
    private let modelTag = NSTextField(labelWithString: "")
    private let providerLabel = NSTextField(labelWithString: "")
    private let activityLabel = NSTextField(labelWithString: "")
    private let progressBar = NSView()
    private let progressFill = NSView()
    private let percentLabel = NSTextField(labelWithString: "")
    private let recentLabel = NSTextField(labelWithString: "")
    private let disconnectButton = NSButton()

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true
        layer?.cornerRadius = 8
        setupViews()
        updateColors()
    }

    required init?(coder: NSCoder) { fatalError() }

    func setupViews() {
        // Header
        headerLabel.stringValue = "🔗 AI 设置 关联"
        headerLabel.font = NSFont.systemFont(ofSize: 12, weight: .semibold)
        headerLabel.isEditable = false
        headerLabel.isBordered = false
        headerLabel.backgroundColor = .clear
        headerLabel.translatesAutoresizingMaskIntoConstraints = false
        addSubview(headerLabel)

        // Badge
        badgeLabel.stringValue = "已连接"
        badgeLabel.font = NSFont.systemFont(ofSize: 9, weight: .medium)
        badgeLabel.isEditable = false
        badgeLabel.isBordered = false
        badgeLabel.backgroundColor = .clear
        badgeLabel.translatesAutoresizingMaskIntoConstraints = false
        addSubview(badgeLabel)

        // Description
        descLabel.stringValue = "AI 浮窗与桌宠同级并排显示在桌面。"
        descLabel.font = NSFont.systemFont(ofSize: 10)
        descLabel.isEditable = false
        descLabel.isBordered = false
        descLabel.backgroundColor = .clear
        descLabel.translatesAutoresizingMaskIntoConstraints = false
        addSubview(descLabel)

        // --- Hermes Status Area ---

        // Status dot
        statusDot.wantsLayer = true
        statusDot.layer?.cornerRadius = 14
        statusDot.translatesAutoresizingMaskIntoConstraints = false
        addSubview(statusDot)

        statusDotInner.wantsLayer = true
        statusDotInner.layer?.cornerRadius = 10
        statusDotInner.layer?.borderWidth = 1
        statusDotInner.translatesAutoresizingMaskIntoConstraints = false
        statusDot.addSubview(statusDotInner)

        // Status info container
        let infoStack = NSStackView()
        infoStack.orientation = .vertical
        infoStack.spacing = 2
        infoStack.translatesAutoresizingMaskIntoConstraints = false
        addSubview(infoStack)

        // Row 1: Name + status
        let nameStack = NSStackView()
        nameStack.orientation = .horizontal
        nameStack.spacing = 4
        nameStack.translatesAutoresizingMaskIntoConstraints = false

        nameLabel.stringValue = "路西德"
        nameLabel.font = NSFont.systemFont(ofSize: 14, weight: .bold)
        nameLabel.isEditable = false
        nameLabel.isBordered = false
        nameLabel.backgroundColor = .clear
        nameLabel.translatesAutoresizingMaskIntoConstraints = false

        statusLabel.stringValue = "· 运行中"
        statusLabel.font = NSFont.systemFont(ofSize: 10)
        statusLabel.isEditable = false
        statusLabel.isBordered = false
        statusLabel.backgroundColor = .clear
        statusLabel.translatesAutoresizingMaskIntoConstraints = false

        nameStack.addArrangedSubview(nameLabel)
        nameStack.addArrangedSubview(statusLabel)

        // Row 2: Model tag + provider
        let modelStack = NSStackView()
        modelStack.orientation = .horizontal
        modelStack.spacing = 4
        modelStack.alignment = .centerY
        modelStack.translatesAutoresizingMaskIntoConstraints = false

        modelTag.stringValue = "gpt-4o"
        modelTag.font = NSFont.systemFont(ofSize: 8, weight: .semibold)
        modelTag.isEditable = false
        modelTag.isBordered = false
        modelTag.backgroundColor = .clear
        modelTag.translatesAutoresizingMaskIntoConstraints = false

        providerLabel.stringValue = "由 zai 提供 · 上下文 128K"
        providerLabel.font = NSFont.systemFont(ofSize: 10)
        providerLabel.isEditable = false
        providerLabel.isBordered = false
        providerLabel.backgroundColor = .clear
        providerLabel.translatesAutoresizingMaskIntoConstraints = false

        modelStack.addArrangedSubview(modelTag)
        modelStack.addArrangedSubview(providerLabel)

        // Row 3: Activity
        let activityStack = NSStackView()
        activityStack.orientation = .horizontal
        activityStack.spacing = 6
        activityStack.alignment = .centerY
        activityStack.translatesAutoresizingMaskIntoConstraints = false

        activityLabel.stringValue = "活动指标"
        activityLabel.font = NSFont.systemFont(ofSize: 9)
        activityLabel.isEditable = false
        activityLabel.isBordered = false
        activityLabel.backgroundColor = .clear
        activityLabel.translatesAutoresizingMaskIntoConstraints = false

        progressBar.wantsLayer = true
        progressBar.layer?.cornerRadius = 2
        progressBar.translatesAutoresizingMaskIntoConstraints = false

        progressFill.wantsLayer = true
        progressFill.layer?.cornerRadius = 2
        progressFill.translatesAutoresizingMaskIntoConstraints = false
        progressBar.addSubview(progressFill)

        percentLabel.stringValue = "60%"
        percentLabel.font = NSFont.systemFont(ofSize: 9)
        percentLabel.isEditable = false
        percentLabel.isBordered = false
        percentLabel.backgroundColor = .clear
        percentLabel.translatesAutoresizingMaskIntoConstraints = false

        activityStack.addArrangedSubview(activityLabel)
        activityStack.addArrangedSubview(progressBar)
        activityStack.addArrangedSubview(percentLabel)

        // Row 4: Recent activity
        recentLabel.stringValue = "最近活动: 收到用户消息 \"帮我查怪物8880150\""
        recentLabel.font = NSFont.systemFont(ofSize: 9)
        recentLabel.isEditable = false
        recentLabel.isBordered = false
        recentLabel.backgroundColor = .clear
        recentLabel.translatesAutoresizingMaskIntoConstraints = false
        recentLabel.lineBreakMode = .byTruncatingTail

        infoStack.addArrangedSubview(nameStack)
        infoStack.addArrangedSubview(modelStack)
        infoStack.addArrangedSubview(activityStack)
        infoStack.addArrangedSubview(recentLabel)

        // Disconnect button
        disconnectButton.title = "断开连接"
        disconnectButton.bezelStyle = .rounded
        disconnectButton.isBordered = false
        disconnectButton.wantsLayer = true
        disconnectButton.layer?.cornerRadius = 5
        disconnectButton.font = NSFont.systemFont(ofSize: 10, weight: .medium)
        disconnectButton.translatesAutoresizingMaskIntoConstraints = false
        addSubview(disconnectButton)

        // Layout
        NSLayoutConstraint.activate([
            // Header + badge
            headerLabel.topAnchor.constraint(equalTo: topAnchor, constant: 12),
            headerLabel.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 14),

            badgeLabel.centerYAnchor.constraint(equalTo: headerLabel.centerYAnchor),
            badgeLabel.leadingAnchor.constraint(equalTo: headerLabel.trailingAnchor, constant: 6),

            // Description
            descLabel.topAnchor.constraint(equalTo: headerLabel.bottomAnchor, constant: 2),
            descLabel.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 14),

            // Status dot
            statusDot.topAnchor.constraint(equalTo: descLabel.bottomAnchor, constant: 10),
            statusDot.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 14),
            statusDot.widthAnchor.constraint(equalToConstant: 28),
            statusDot.heightAnchor.constraint(equalToConstant: 28),

            statusDotInner.topAnchor.constraint(equalTo: statusDot.topAnchor, constant: 4),
            statusDotInner.leadingAnchor.constraint(equalTo: statusDot.leadingAnchor, constant: 4),
            statusDotInner.trailingAnchor.constraint(equalTo: statusDot.trailingAnchor, constant: -4),
            statusDotInner.bottomAnchor.constraint(equalTo: statusDot.bottomAnchor, constant: -4),

            // Info stack
            infoStack.topAnchor.constraint(equalTo: statusDot.topAnchor),
            infoStack.leadingAnchor.constraint(equalTo: statusDot.trailingAnchor, constant: 10),
            infoStack.trailingAnchor.constraint(equalTo: disconnectButton.leadingAnchor, constant: -10),

            // Progress bar
            progressBar.heightAnchor.constraint(equalToConstant: 4),
            progressBar.widthAnchor.constraint(equalToConstant: 80),

            progressFill.topAnchor.constraint(equalTo: progressBar.topAnchor),
            progressFill.leadingAnchor.constraint(equalTo: progressBar.leadingAnchor),
            progressFill.bottomAnchor.constraint(equalTo: progressBar.bottomAnchor),
            progressFill.widthAnchor.constraint(equalTo: progressBar.widthAnchor, multiplier: 0.6),

            // Disconnect button
            disconnectButton.centerYAnchor.constraint(equalTo: statusDot.centerYAnchor),
            disconnectButton.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -14),

            // Bottom
            bottomAnchor.constraint(equalTo: infoStack.bottomAnchor, constant: 12),
        ])
    }

    func updateColors() {
        let c = ThemeColors.current
        layer?.backgroundColor = c.ntBg.cgColor
        layer?.borderColor = c.ntBrd.cgColor
        layer?.borderWidth = 1

        headerLabel.textColor = c.txtPri
        badgeLabel.textColor = c.ntAct
        badgeLabel.layer?.cornerRadius = 4

        descLabel.textColor = c.txtDim

        statusDot.layer?.backgroundColor = NSColor(red: 52/255, green: 199/255, blue: 89/255, alpha: 1).cgColor
        statusDot.layer?.shadowColor = NSColor(red: 52/255, green: 199/255, blue: 89/255, alpha: 0.3).cgColor
        statusDot.layer?.shadowOffset = .zero
        statusDot.layer?.shadowRadius = 10
        statusDot.layer?.shadowOpacity = 1

        statusDotInner.layer?.backgroundColor = c.txtDim.cgColor
        statusDotInner.layer?.borderColor = c.txtMute.cgColor

        nameLabel.textColor = c.txtPri
        statusLabel.textColor = c.txtDim

        modelTag.textColor = c.ntAct
        modelTag.layer?.cornerRadius = 3

        providerLabel.textColor = NSColor(white: 1, alpha: 0.4)

        activityLabel.textColor = NSColor(white: 1, alpha: 0.3)
        progressBar.layer?.backgroundColor = c.ntBrd.cgColor
        progressFill.layer?.backgroundColor = c.ntAct.cgColor
        percentLabel.textColor = NSColor(white: 1, alpha: 0.4)

        recentLabel.textColor = NSColor(white: 1, alpha: 0.3)

        disconnectButton.layer?.backgroundColor = NSColor(white: 1, alpha: 0.07).cgColor
        disconnectButton.layer?.borderColor = NSColor(red: 100/255, green: 100/255, blue: 120/255, alpha: 0.25).cgColor
        disconnectButton.layer?.borderWidth = 1
        disconnectButton.contentTintColor = c.txtSec
    }
}

// MARK: - Reaction Rule Card

private class ReactionRuleCard: NSView {

    private let headerLabel = NSTextField(labelWithString: "")
    private let badgeLabel = NSTextField(labelWithString: "")
    private let addButton = NSButton()
    private let tableStack = NSStackView()
    private let autoDetectLabel = NSTextField(labelWithString: "")
    private let autoDetectSwitch = NSSwitch()

    // Reaction rule data
    private var rules: [ReactionRule] = SettingsManager.current.reactionRules
    private var ruleRows: [ReactionRuleRow] = []

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true
        layer?.cornerRadius = 8
        setupViews()
        updateColors()
    }

    required init?(coder: NSCoder) { fatalError() }

    private func setupViews() {
        // Header
        headerLabel.stringValue = "📋 反应规则"
        headerLabel.font = NSFont.systemFont(ofSize: 12, weight: .semibold)
        headerLabel.isEditable = false
        headerLabel.isBordered = false
        headerLabel.backgroundColor = .clear
        headerLabel.translatesAutoresizingMaskIntoConstraints = false
        addSubview(headerLabel)

        badgeLabel.stringValue = "\(rules.count) 条"
        badgeLabel.font = NSFont.systemFont(ofSize: 9, weight: .medium)
        badgeLabel.isEditable = false
        badgeLabel.isBordered = false
        badgeLabel.backgroundColor = .clear
        badgeLabel.translatesAutoresizingMaskIntoConstraints = false
        addSubview(badgeLabel)

        addButton.title = "+ 添加"
        addButton.bezelStyle = .rounded
        addButton.isBordered = false
        addButton.wantsLayer = true
        addButton.layer?.cornerRadius = 4
        addButton.font = NSFont.systemFont(ofSize: 10, weight: .medium)
        addButton.translatesAutoresizingMaskIntoConstraints = false
        addButton.target = self
        addButton.action = #selector(addRule)
        addSubview(addButton)

        // Table header
        let tableHeader = NSStackView()
        tableHeader.orientation = .horizontal
        tableHeader.spacing = 0
        tableHeader.translatesAutoresizingMaskIntoConstraints = false
        addSubview(tableHeader)

        let headerTitles = ["关键词", "动画", "延迟", "优先级", "启用", ""]
        let headerWidths: [CGFloat] = [80, 60, 44, 50, 44, 40]
        for (i, title) in headerTitles.enumerated() {
            let lbl = NSTextField(labelWithString: title)
            lbl.font = NSFont.systemFont(ofSize: 9, weight: .bold)
            lbl.isEditable = false
            lbl.isBordered = false
            lbl.backgroundColor = .clear
            lbl.translatesAutoresizingMaskIntoConstraints = false
            if i == 5 { lbl.alignment = .center }
            tableHeader.addArrangedSubview(lbl)
            lbl.widthAnchor.constraint(equalToConstant: headerWidths[i]).isActive = true
        }

        // Table rows
        tableStack.orientation = .vertical
        tableStack.spacing = 0
        tableStack.translatesAutoresizingMaskIntoConstraints = false
        addSubview(tableStack)

        // Bottom switch row
        let bottomRow = NSStackView()
        bottomRow.orientation = .horizontal
        bottomRow.spacing = 8
        bottomRow.alignment = .centerY
        bottomRow.translatesAutoresizingMaskIntoConstraints = false
        addSubview(bottomRow)

        autoDetectLabel.stringValue = "启用在 Hermes 对话中自动侦测关键词"
        autoDetectLabel.font = NSFont.systemFont(ofSize: 10)
        autoDetectLabel.isEditable = false
        autoDetectLabel.isBordered = false
        autoDetectLabel.backgroundColor = .clear
        autoDetectLabel.translatesAutoresizingMaskIntoConstraints = false

        autoDetectSwitch.state = SettingsManager.current.reactionEnabled ? .on : .off
        autoDetectSwitch.target = self
        autoDetectSwitch.action = #selector(autoDetectToggled)
        autoDetectSwitch.translatesAutoresizingMaskIntoConstraints = false

        bottomRow.addArrangedSubview(autoDetectLabel)
        bottomRow.addArrangedSubview(autoDetectSwitch)

        rebuildRows()

        NSLayoutConstraint.activate([
            headerLabel.topAnchor.constraint(equalTo: topAnchor, constant: 12),
            headerLabel.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 14),

            badgeLabel.centerYAnchor.constraint(equalTo: headerLabel.centerYAnchor),
            badgeLabel.leadingAnchor.constraint(equalTo: headerLabel.trailingAnchor, constant: 6),

            addButton.centerYAnchor.constraint(equalTo: headerLabel.centerYAnchor),
            addButton.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -14),

            tableHeader.topAnchor.constraint(equalTo: headerLabel.bottomAnchor, constant: 8),
            tableHeader.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 14),
            tableHeader.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -14),

            tableStack.topAnchor.constraint(equalTo: tableHeader.bottomAnchor, constant: 0),
            tableStack.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 14),
            tableStack.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -14),

            bottomRow.topAnchor.constraint(equalTo: tableStack.bottomAnchor, constant: 6),
            bottomRow.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 14),
            bottomRow.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -14),
            bottomAnchor.constraint(equalTo: bottomRow.bottomAnchor, constant: 12),
        ])
    }

    private func rebuildRows() {
        ruleRows.forEach { $0.removeFromSuperview() }
        ruleRows.removeAll()
        tableStack.arrangedSubviews.forEach { $0.removeFromSuperview() }

        for rule in rules {
            let row = ReactionRuleRow(rule: rule) { [weak self] idx, updatedRule in
                guard let self = self, idx < self.rules.count else { return }
                self.rules[idx] = updatedRule
                SettingsManager.current.reactionRules = self.rules
                SettingsManager.save(SettingsManager.current)
            } deleteHandler: { [weak self] idx in
                guard let self = self, idx < self.rules.count else { return }
                self.rules.remove(at: idx)
                SettingsManager.current.reactionRules = self.rules
                SettingsManager.save(SettingsManager.current)
                self.badgeLabel.stringValue = "\(self.rules.count) 条"
                self.rebuildRows()
            }
            row.translatesAutoresizingMaskIntoConstraints = false
            row.index = ruleRows.count
            tableStack.addArrangedSubview(row)
            ruleRows.append(row)
        }
    }

    @objc private func addRule() {
        rules.append(ReactionRule(keyword: "", animation: "stand", delay: 0.5, priority: 1, enabled: true))
        SettingsManager.current.reactionRules = rules
        SettingsManager.save(SettingsManager.current)
        badgeLabel.stringValue = "\(rules.count) 条"
        rebuildRows()
    }

    @objc private func autoDetectToggled() {
        SettingsManager.current.reactionEnabled = autoDetectSwitch.state == .on
        SettingsManager.save(SettingsManager.current)
    }

    func updateColors() {
        let c = ThemeColors.current
        layer?.backgroundColor = c.ntBg.cgColor
        layer?.borderColor = c.ntBrd.cgColor
        layer?.borderWidth = 1

        headerLabel.textColor = c.txtPri
        badgeLabel.textColor = c.ntAct
        badgeLabel.layer?.cornerRadius = 4

        addButton.layer?.backgroundColor = c.ntAct.cgColor
        addButton.contentTintColor = .white

        autoDetectLabel.textColor = NSColor(white: 1, alpha: 0.4)

        for row in ruleRows { row.updateColors() }
    }
}

// MARK: - Reaction Rule Row

private class ReactionRuleRow: NSView {

    var index: Int = 0
    private var rule: ReactionRule
    private let keywordField = NSTextField()
    private let animPopup = NSPopUpButton()
    private let delayField = NSTextField()
    private let priorityField = NSTextField()
    private let enabledSwitch = NSSwitch()
    private let editButton = NSButton()
    private let deleteButton = NSButton()
    private var changeHandler: ((Int, ReactionRule) -> Void)?
    private var deleteHandler: ((Int) -> Void)?

    init(rule: ReactionRule, changeHandler: @escaping (Int, ReactionRule) -> Void, deleteHandler: @escaping (Int) -> Void) {
        self.rule = rule
        self.changeHandler = changeHandler
        self.deleteHandler = deleteHandler
        super.init(frame: .zero)
        wantsLayer = true
        setupViews()
        updateColors()
    }

    required init?(coder: NSCoder) { fatalError() }

    private func setupViews() {
        let rowStack = NSStackView()
        rowStack.orientation = .horizontal
        rowStack.spacing = 0
        rowStack.alignment = .centerY
        rowStack.translatesAutoresizingMaskIntoConstraints = false
        addSubview(rowStack)

        // Keyword
        keywordField.stringValue = rule.keyword
        keywordField.font = NSFont.systemFont(ofSize: 10)
        keywordField.isEditable = true
        keywordField.isBordered = true
        keywordField.bezelStyle = .roundedBezel
        keywordField.translatesAutoresizingMaskIntoConstraints = false
        keywordField.target = self
        keywordField.action = #selector(fieldChanged)

        // Animation popup
        animPopup.addItems(withTitles: ["move", "stand", "attack", "skill", "die", "hit"])
        animPopup.selectItem(withTitle: rule.animation)
        animPopup.font = NSFont.systemFont(ofSize: 10)
        animPopup.bezelStyle = .rounded
        animPopup.translatesAutoresizingMaskIntoConstraints = false
        animPopup.target = self
        animPopup.action = #selector(popupChanged)

        // Delay
        delayField.stringValue = String(format: "%.1f", rule.delay)
        delayField.font = NSFont.systemFont(ofSize: 10)
        delayField.isEditable = true
        delayField.isBordered = true
        delayField.bezelStyle = .roundedBezel
        delayField.translatesAutoresizingMaskIntoConstraints = false
        delayField.target = self
        delayField.action = #selector(fieldChanged)

        // Priority
        priorityField.stringValue = "\(rule.priority)"
        priorityField.font = NSFont.systemFont(ofSize: 10)
        priorityField.isEditable = true
        priorityField.isBordered = true
        priorityField.bezelStyle = .roundedBezel
        priorityField.translatesAutoresizingMaskIntoConstraints = false
        priorityField.target = self
        priorityField.action = #selector(fieldChanged)

        // Enable switch
        enabledSwitch.state = rule.enabled ? .on : .off
        enabledSwitch.target = self
        enabledSwitch.action = #selector(switchChanged)
        enabledSwitch.translatesAutoresizingMaskIntoConstraints = false

        // Edit button
        editButton.title = "✏️"
        editButton.bezelStyle = .rounded
        editButton.isBordered = false
        editButton.font = NSFont.systemFont(ofSize: 10)
        editButton.translatesAutoresizingMaskIntoConstraints = false

        // Delete button
        deleteButton.title = "🗑️"
        deleteButton.bezelStyle = .rounded
        deleteButton.isBordered = false
        deleteButton.font = NSFont.systemFont(ofSize: 10)
        deleteButton.translatesAutoresizingMaskIntoConstraints = false
        deleteButton.target = self
        deleteButton.action = #selector(deleteRule)

        let headerWidths: [CGFloat] = [80, 60, 44, 50, 44, 40]
        let views: [NSView] = [keywordField, animPopup, delayField, priorityField, enabledSwitch, deleteButton]
        for (i, v) in views.enumerated() {
            rowStack.addArrangedSubview(v)
            v.widthAnchor.constraint(equalToConstant: headerWidths[i]).isActive = true
        }

        // Separator line
        let separator = NSView()
        separator.wantsLayer = true
        separator.translatesAutoresizingMaskIntoConstraints = false
        addSubview(separator)

        NSLayoutConstraint.activate([
            rowStack.topAnchor.constraint(equalTo: topAnchor),
            rowStack.leadingAnchor.constraint(equalTo: leadingAnchor),
            rowStack.trailingAnchor.constraint(equalTo: trailingAnchor),
            rowStack.bottomAnchor.constraint(equalTo: bottomAnchor),
            rowStack.heightAnchor.constraint(equalToConstant: 28),

            separator.leadingAnchor.constraint(equalTo: leadingAnchor),
            separator.trailingAnchor.constraint(equalTo: trailingAnchor),
            separator.bottomAnchor.constraint(equalTo: bottomAnchor),
            separator.heightAnchor.constraint(equalToConstant: 1),
        ])
    }

    @objc private func fieldChanged() {
        rule.keyword = keywordField.stringValue
        rule.delay = Double(delayField.stringValue) ?? rule.delay
        rule.priority = Int(priorityField.stringValue) ?? rule.priority
        changeHandler?(index, rule)
    }

    @objc private func popupChanged() {
        rule.animation = animPopup.titleOfSelectedItem ?? "stand"
        changeHandler?(index, rule)
    }

    @objc private func switchChanged() {
        rule.enabled = enabledSwitch.state == .on
        changeHandler?(index, rule)
    }

    @objc private func deleteRule() {
        deleteHandler?(index)
    }

    func updateColors() {
        let c = ThemeColors.current
        keywordField.backgroundColor = NSColor(red: 60/255, green: 60/255, blue: 72/255, alpha: 0.7)
        keywordField.textColor = NSColor(white: 1, alpha: 0.7)
        keywordField.layer?.borderColor = NSColor(red: 100/255, green: 100/255, blue: 120/255, alpha: 0.25).cgColor
        keywordField.layer?.borderWidth = 1
        keywordField.layer?.cornerRadius = 4

        delayField.backgroundColor = NSColor(red: 60/255, green: 60/255, blue: 72/255, alpha: 0.7)
        delayField.textColor = NSColor(white: 1, alpha: 0.7)
        delayField.layer?.borderColor = NSColor(red: 100/255, green: 100/255, blue: 120/255, alpha: 0.25).cgColor
        delayField.layer?.borderWidth = 1
        delayField.layer?.cornerRadius = 4

        priorityField.backgroundColor = NSColor(red: 60/255, green: 60/255, blue: 72/255, alpha: 0.7)
        priorityField.textColor = NSColor(white: 1, alpha: 0.7)
        priorityField.layer?.borderColor = NSColor(red: 100/255, green: 100/255, blue: 120/255, alpha: 0.25).cgColor
        priorityField.layer?.borderWidth = 1
        priorityField.layer?.cornerRadius = 4

        animPopup.contentTintColor = NSColor(white: 1, alpha: 0.7)

        editButton.contentTintColor = NSColor(white: 1, alpha: 0.3)
        deleteButton.contentTintColor = NSColor(white: 1, alpha: 0.3)
    }
}

// MARK: - Recent Chat Card

private class RecentChatCard: NSView {

    private let headerLabel = NSTextField(labelWithString: "")
    private let badgeLabel = NSTextField(labelWithString: "")
    private let clearButton = NSButton()
    private let chatStack = NSStackView()

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true
        layer?.cornerRadius = 8
        setupViews()
        updateColors()
    }

    required init?(coder: NSCoder) { fatalError() }

    private func setupViews() {
        // Header
        headerLabel.stringValue = "💬 最近对话"
        headerLabel.font = NSFont.systemFont(ofSize: 12, weight: .semibold)
        headerLabel.isEditable = false
        headerLabel.isBordered = false
        headerLabel.backgroundColor = .clear
        headerLabel.translatesAutoresizingMaskIntoConstraints = false
        addSubview(headerLabel)

        badgeLabel.stringValue = "5 条"
        badgeLabel.font = NSFont.systemFont(ofSize: 9, weight: .medium)
        badgeLabel.isEditable = false
        badgeLabel.isBordered = false
        badgeLabel.backgroundColor = .clear
        badgeLabel.translatesAutoresizingMaskIntoConstraints = false
        addSubview(badgeLabel)

        clearButton.title = "清空"
        clearButton.bezelStyle = .rounded
        clearButton.isBordered = false
        clearButton.wantsLayer = true
        clearButton.layer?.cornerRadius = 4
        clearButton.font = NSFont.systemFont(ofSize: 10, weight: .medium)
        clearButton.translatesAutoresizingMaskIntoConstraints = false
        addSubview(clearButton)

        // Chat entries
        chatStack.orientation = .vertical
        chatStack.spacing = 3
        chatStack.translatesAutoresizingMaskIntoConstraints = false
        addSubview(chatStack)

        let entries: [(time: String, sender: String, senderType: Int, message: String)] = [
            ("19:28", "你", 0, "帮我查一下怪物8880150"),
            ("19:28", "路西德", 1, "查到了！路西德 (8880150) 等级 230 Boss"),
            ("19:25", "你", 0, "切换怪物到拉图斯"),
            ("19:24", "你", 0, "今天天气怎么样？"),
        ]

        for entry in entries {
            let row = ChatEntryRow(time: entry.time, sender: entry.sender, senderType: entry.senderType, message: entry.message)
            chatStack.addArrangedSubview(row)
        }

        NSLayoutConstraint.activate([
            headerLabel.topAnchor.constraint(equalTo: topAnchor, constant: 12),
            headerLabel.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 14),

            badgeLabel.centerYAnchor.constraint(equalTo: headerLabel.centerYAnchor),
            badgeLabel.leadingAnchor.constraint(equalTo: headerLabel.trailingAnchor, constant: 6),

            clearButton.centerYAnchor.constraint(equalTo: headerLabel.centerYAnchor),
            clearButton.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -14),

            chatStack.topAnchor.constraint(equalTo: headerLabel.bottomAnchor, constant: 8),
            chatStack.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 14),
            chatStack.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -14),
            bottomAnchor.constraint(equalTo: chatStack.bottomAnchor, constant: 12),
        ])
    }

    func updateColors() {
        let c = ThemeColors.current
        layer?.backgroundColor = c.ntBg.cgColor
        layer?.borderColor = c.ntBrd.cgColor
        layer?.borderWidth = 1

        headerLabel.textColor = c.txtPri
        badgeLabel.textColor = c.ntAct
        badgeLabel.layer?.cornerRadius = 4

        clearButton.layer?.backgroundColor = c.ntAct.cgColor
        clearButton.contentTintColor = .white

        for case let row as ChatEntryRow in chatStack.arrangedSubviews {
            row.updateColors()
        }
    }
}

// MARK: - Chat Entry Row

private class ChatEntryRow: NSView {

    private let timeLabel = NSTextField(labelWithString: "")
    private let senderLabel = NSTextField(labelWithString: "")
    private let messageLabel = NSTextField(labelWithString: "")
    private let senderType: Int  // 0 = user, 1 = AI

    init(time: String, sender: String, senderType: Int, message: String) {
        self.senderType = senderType
        super.init(frame: .zero)
        wantsLayer = true
        layer?.cornerRadius = 4
        setupViews(time: time, sender: sender, message: message)
    }

    required init?(coder: NSCoder) { fatalError() }

    private func setupViews(time: String, sender: String, message: String) {
        let rowStack = NSStackView()
        rowStack.orientation = .horizontal
        rowStack.spacing = 6
        rowStack.alignment = .centerY
        rowStack.translatesAutoresizingMaskIntoConstraints = false
        addSubview(rowStack)

        timeLabel.stringValue = time
        timeLabel.font = NSFont.systemFont(ofSize: 10)
        timeLabel.isEditable = false
        timeLabel.isBordered = false
        timeLabel.backgroundColor = .clear
        timeLabel.translatesAutoresizingMaskIntoConstraints = false

        senderLabel.stringValue = "\(sender): "
        senderLabel.font = NSFont.systemFont(ofSize: 10)
        senderLabel.isEditable = false
        senderLabel.isBordered = false
        senderLabel.backgroundColor = .clear
        senderLabel.translatesAutoresizingMaskIntoConstraints = false

        messageLabel.stringValue = message
        messageLabel.font = NSFont.systemFont(ofSize: 10)
        messageLabel.isEditable = false
        messageLabel.isBordered = false
        messageLabel.backgroundColor = .clear
        messageLabel.translatesAutoresizingMaskIntoConstraints = false

        rowStack.addArrangedSubview(timeLabel)
        rowStack.addArrangedSubview(senderLabel)
        rowStack.addArrangedSubview(messageLabel)

        NSLayoutConstraint.activate([
            rowStack.topAnchor.constraint(equalTo: topAnchor, constant: 3),
            rowStack.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 6),
            rowStack.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -6),
            rowStack.bottomAnchor.constraint(equalTo: bottomAnchor, constant: -3),

            timeLabel.widthAnchor.constraint(equalToConstant: 50),
        ])
    }

    func updateColors() {
        let c = ThemeColors.current
        timeLabel.textColor = NSColor(white: 1, alpha: 0.3)

        if senderType == 0 {
            senderLabel.textColor = c.txtSec
        } else {
            senderLabel.textColor = c.ntAct
        }

        messageLabel.textColor = NSColor(white: 1, alpha: 0.7)

        if senderType == 1 {
            layer?.backgroundColor = NSColor.clear.cgColor
        } else {
            layer?.backgroundColor = NSColor(red: 94/255, green: 156/255, blue: 255/255, alpha: 0.04).cgColor
        }
    }
}