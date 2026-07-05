import AppKit

// MARK: - Hermes Panel (AI Association Panel)

// ──────────────────────────────────────────────
// Data Models
// ──────────────────────────────────────────────

/// AI 状态
enum HermesState: String, Codable, CaseIterable {
    case idle = "空闲"
    case thinking = "思考中"
    case answering = "正在回答"
    case error = "出错"
}

/// 宠物反应规则 — 某 AI 状态 → 宠物动画 + 气泡
struct PetReactionRule: Codable, Identifiable {
    var id: String { state.rawValue }
    var state: HermesState
    var animation: String  // 宠物动画名 (stand / move / say / attack…)
    var balloonText: String
}

/// 关键词技能触发规则
struct KeywordSkillRule: Codable, Identifiable {
    var id = UUID()
    var keyword: String
    var skillAnimation: String
    var balloonText: String
}

/// 对话摘要条目
struct ChatSummaryEntry: Codable {
    var timestamp: Date
    var role: String      // "user" / "hermes" / "pet"
    var text: String
}

/// 宠物状态历史条目
struct PetStateEntry: Codable, Identifiable {
    var id = UUID()
    var timestamp: Date
    var state: String        // 如 "stand", "move", "attack1"
    var trigger: String      // 触发原因
    var detail: String = ""
}

/// 顶层持久化数据
struct HermesPanelData: Codable {
    var reactions: [PetReactionRule]
    var keywordSkills: [KeywordSkillRule]
    var chatHistory: [ChatSummaryEntry]
    var stateHistory: [PetStateEntry]

    static let `default` = HermesPanelData(
        reactions: [
            PetReactionRule(state: .thinking, animation: "stand",  balloonText: "🤔 AI 在思考……"),
            PetReactionRule(state: .answering, animation: "move",  balloonText: "👂 正在听 AI 回答"),
            PetReactionRule(state: .idle,      animation: "stand", balloonText: ""),
            PetReactionRule(state: .error,     animation: "die",   balloonText: "⚠️ AI 出错了"),
        ],
        keywordSkills: [
            KeywordSkillRule(keyword: "hello", skillAnimation: "say",  balloonText: "你好呀！"),
            KeywordSkillRule(keyword: "攻击",  skillAnimation: "attack1", balloonText: "💥 看招！"),
            KeywordSkillRule(keyword: "技能",  skillAnimation: "skill1",  balloonText: "✨ 释放技能！"),
        ],
        chatHistory: [],
        stateHistory: []
    )
}

// ──────────────────────────────────────────────
// Persistence
// ──────────────────────────────────────────────

class HermesPanelStore {
    static let path = "\(cacheRoot)/hermes_panel.json"

    static func load() -> HermesPanelData {
        guard let data = try? Data(contentsOf: URL(fileURLWithPath: path)),
              let decoded = try? JSONDecoder().decode(HermesPanelData.self, from: data)
        else { return HermesPanelData.default }
        return decoded
    }

    static func save(_ data: HermesPanelData) {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        if let encoded = try? encoder.encode(data) {
            try? FileManager.default.createDirectory(atPath: cacheRoot, withIntermediateDirectories: true)
            try? encoded.write(to: URL(fileURLWithPath: path))
        }
    }
}

// ──────────────────────────────────────────────
// Hermes Panel — NSPanel 子类
// ──────────────────────────────────────────────

class HermesPanel: NSPanel {
    override var canBecomeKey: Bool { true }

    // 子视图（NSTabView 切换五个功能区）
    private var tabView: NSTabView!
    private var statusTabItem: NSTabViewItem!
    private var reactionTabItem: NSTabViewItem!
    private var chatTabItem: NSTabViewItem!
    private var skillTabItem: NSTabViewItem!
    private var historyTabItem: NSTabViewItem!

    // 数据
    private var data: HermesPanelData {
        didSet { HermesPanelStore.save(data) }
    }

    // 外部引用（弱引用避免循环）
    weak var petView: PetView?
    /// Hermes 客户端引用，用于获取真实活动状态
    weak var hermesClient: HermesClient?

    // 定时刷新
    private var refreshTimer: Timer?

    // ── 状态面板控件 ──
    private let hermesStatusLabel  = makeLabel(fontSize: 13, bold: true)
    private let processStatusIcon  = makeLabel(fontSize: 11)
    private let modelLabel         = makeLabel(fontSize: 11)
    private let providerLabel      = makeLabel(fontSize: 11)
    private let activityLabel      = makeLabel(fontSize: 11)
    private let activityBar        = NSLevelIndicator()
    private let lastActivityLabel  = makeLabel(fontSize: 11)
    private let refreshButton      = NSButton(title: "刷新", target: nil, action: nil)

    // ── 反应配置面板控件 ──
    private var reactionTableView: NSTableView!

    // ── 对话上下文控件 ──
    private let chatScrollView     = NSScrollView()
    private let chatTextView       = NSTextView()
    private let petPerceptionLabel = makeLabel(fontSize: 11)
    private let aiMoodLabel        = makeLabel(fontSize: 11)

    // ── 技能触发面板控件 ──
    private var skillTableView: NSTableView!

    // ── 状态历史控件 ──
    private let historyScrollView  = NSScrollView()
    private let historyTextView    = NSTextView()


    // MARK: - Init

    init(contentRect: NSRect, petView: PetView?, hermesClient: HermesClient? = nil) {
        self.data = HermesPanelStore.load()
        self.petView = petView
        self.hermesClient = hermesClient
        super.init(contentRect: contentRect,
                   styleMask: [.titled, .closable, .miniaturizable, .fullSizeContentView],
                   backing: .buffered, defer: false)
        setupPanel()
        setupViews()
        refreshAll()
    }

    required init?(coder: NSCoder) { nil }

    private func setupPanel() {
        title = "Hermes 关联面板"
        isFloatingPanel = true
        isMovableByWindowBackground = true
        backgroundColor = NSColor(calibratedWhite: 0.12, alpha: 0.92)
        isOpaque = false
        hasShadow = true
        titlebarAppearsTransparent = true
        standardWindowButton(.miniaturizeButton)?.isHidden = false
        standardWindowButton(.zoomButton)?.isHidden = true
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        setContentSize(NSSize(width: 520, height: 460))
    }

    // MARK: - Setup View Hierarchy

    private func setupViews() {
        guard let cv = contentView else { return }

        // ── 主标签布局用 NSTabView ──
        tabView = NSTabView(frame: cv.bounds)
        tabView.autoresizingMask = [.width, .height]
        tabView.tabPosition = .top
        tabView.drawsBackground = false

        // 分段按钮
        let segmentTitles = ["状态", "反应", "对话", "技能", "历史"]
        let segment = NSSegmentedControl(labels: segmentTitles, trackingMode: .selectOne, target: self, action: #selector(segmentChanged(_:)))
        segment.selectedSegment = 0
        segment.translatesAutoresizingMaskIntoConstraints = false

        cv.addSubview(segment)
        cv.addSubview(tabView)

        NSLayoutConstraint.activate([
            segment.topAnchor.constraint(equalTo: cv.topAnchor, constant: 32),
            segment.centerXAnchor.constraint(equalTo: cv.centerXAnchor),
            segment.widthAnchor.constraint(equalTo: cv.widthAnchor, constant: -32),
            tabView.topAnchor.constraint(equalTo: segment.bottomAnchor, constant: 12),
            tabView.leadingAnchor.constraint(equalTo: cv.leadingAnchor, constant: 8),
            tabView.trailingAnchor.constraint(equalTo: cv.trailingAnchor, constant: -8),
            tabView.bottomAnchor.constraint(equalTo: cv.bottomAnchor, constant: -8),
        ])

        // ── 创建五个标签页 ──
        statusTabItem  = buildStatusTab()
        reactionTabItem = buildReactionTab()
        chatTabItem    = buildChatTab()
        skillTabItem   = buildSkillTab()
        historyTabItem = buildHistoryTab()

        tabView.addTabViewItem(statusTabItem)
        tabView.addTabViewItem(reactionTabItem)
        tabView.addTabViewItem(chatTabItem)
        tabView.addTabViewItem(skillTabItem)
        tabView.addTabViewItem(historyTabItem)

        // ── 周期性刷新 ──
        refreshTimer = Timer.scheduledTimer(withTimeInterval: 3.0, repeats: true) { [weak self] _ in
            self?.refreshStatusSection()
            self?.refreshChatSection()
        }
    }

    // MARK: - Status Tab   ① Hermes 状态面板

    private func buildStatusTab() -> NSTabViewItem {
        let tab = NSTabViewItem(identifier: "status")
        tab.label = "状态"

        let root = NSView()
        root.wantsLayer = true
        tab.view = root

        // 状态标题
        let title = makeLabel(fontSize: 14, bold: true)
        title.stringValue = "Hermes 状态"
        title.textColor = accentColor()

        // 进程状态行
        let processRow = NSStackView(views: [makeLabel("进程:"), processStatusIcon])
        processRow.spacing = 8

        // 模型
        let modelRow = NSStackView(views: [makeLabel("模型:"), modelLabel])
        modelRow.spacing = 8

        // Provider
        let providerRow = NSStackView(views: [makeLabel("Provider:"), providerLabel])
        providerRow.spacing = 8

        // 活动级别
        let activityRow = NSStackView(views: [makeLabel("活动:"), NSView()])
        activityBar.minValue = 0
        activityBar.maxValue = 100
        activityBar.warningValue = 20
        activityBar.criticalValue = 60
        activityBar.fillColor = accentColor()
        activityBar.translatesAutoresizingMaskIntoConstraints = false
        activityBar.widthAnchor.constraint(equalToConstant: 120).isActive = true
        activityBar.heightAnchor.constraint(equalToConstant: 10).isActive = true
        activityRow.addView(activityBar, in: .trailing)
        activityRow.addView(activityLabel, in: .trailing)

        // 上次活动
        let lastRow = NSStackView(views: [makeLabel("上次活动:"), lastActivityLabel])
        lastRow.spacing = 8

        // 刷新按钮
        refreshButton.target = self
        refreshButton.action = #selector(refreshAll)
        refreshButton.bezelStyle = .smallSquare

        let stack = NSStackView(views: [title, processRow, modelRow, providerRow, activityRow, lastRow, NSView(), refreshButton])
        stack.orientation = .vertical
        stack.spacing = 10
        stack.edgeInsets = NSEdgeInsets(top: 16, left: 16, bottom: 16, right: 16)
        stack.translatesAutoresizingMaskIntoConstraints = false
        root.addSubview(stack)

        NSLayoutConstraint.activate([
            stack.topAnchor.constraint(equalTo: root.topAnchor),
            stack.leadingAnchor.constraint(equalTo: root.leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: root.trailingAnchor),
            stack.bottomAnchor.constraint(lessThanOrEqualTo: root.bottomAnchor),
        ])

        return tab
    }

    // MARK: - Reaction Tab  ② 宠物反应配置

    private func buildReactionTab() -> NSTabViewItem {
        let tab = NSTabViewItem(identifier: "reaction")
        tab.label = "反应"

        let root = NSView()
        tab.view = root

        let title = makeLabel(fontSize: 14, bold: true)
        title.stringValue = "宠物反应配置"
        title.textColor = accentColor()

        // 使用纯手动 DataSource 模式（NSArrayController + Cocoa Bindings 在运行时经常因 KVO 初始化顺序失败）
        let columns: [(id: String, title: String, width: CGFloat)] = [
            ("state",     "AI 状态",  100),
            ("animation", "宠物动画", 120),
            ("balloon",   "气泡内容", 200),
        ]

        reactionTableView = makeManualTableView(columns: columns,
                                                doubleAction: #selector(editReactionRule(_:)))
        reactionTableView.dataSource = self
        reactionTableView.delegate = self
        reactionTableView.translatesAutoresizingMaskIntoConstraints = false

        // 控制按钮
        let editBtn = NSButton(title: "编辑选中…", target: self, action: #selector(editReactionRule(_:)))
        editBtn.bezelStyle = .smallSquare

        let resetBtn = NSButton(title: "重置默认", target: self, action: #selector(resetReactions))
        resetBtn.bezelStyle = .smallSquare

        let btnRow = NSStackView(views: [editBtn, resetBtn])
        btnRow.spacing = 8

        // 提示
        let hint = makeLabel("提示: 双击行编辑动画和气泡内容。动画名需匹配精灵图配置。", fontSize: 10)
        hint.textColor = NSColor.gray

        let stack = NSStackView(views: [title, reactionTableView, btnRow, hint])
        stack.orientation = .vertical
        stack.spacing = 10
        stack.edgeInsets = NSEdgeInsets(top: 16, left: 16, bottom: 16, right: 16)
        stack.translatesAutoresizingMaskIntoConstraints = false
        root.addSubview(stack)

        NSLayoutConstraint.activate([
            stack.topAnchor.constraint(equalTo: root.topAnchor),
            stack.leadingAnchor.constraint(equalTo: root.leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: root.trailingAnchor),
            stack.bottomAnchor.constraint(lessThanOrEqualTo: root.bottomAnchor),
        ])

        return tab
    }

    // MARK: - Chat Tab      ③ 对话上下文显示

    private func buildChatTab() -> NSTabViewItem {
        let tab = NSTabViewItem(identifier: "chat")
        tab.label = "对话"

        let root = NSView()
        tab.view = root

        let title = makeLabel(fontSize: 14, bold: true)
        title.stringValue = "对话上下文"
        title.textColor = accentColor()

        // 最近对话摘要
        let chatLabel = makeLabel("最近对话（最后 3 条）:", fontSize: 11, bold: true)
        chatScrollView.hasVerticalScroller = true
        chatScrollView.borderType = .bezelBorder
        chatScrollView.drawsBackground = false

        chatTextView.isEditable = false
        chatTextView.isSelectable = true
        chatTextView.backgroundColor = NSColor.clear
        chatTextView.textContainerInset = NSSize(width: 6, height: 6)
        chatTextView.font = NSFont.monospacedSystemFont(ofSize: 11, weight: .regular)
        chatTextView.textColor = NSColor(white: 0.85, alpha: 1)
        chatScrollView.documentView = chatTextView

        chatScrollView.translatesAutoresizingMaskIntoConstraints = false
        chatScrollView.heightAnchor.constraint(equalToConstant: 130).isActive = true

        // 宠物感知状态
        let perceptionRow = NSStackView(views: [makeLabel("宠物感知:"), petPerceptionLabel])
        perceptionRow.spacing = 8

        // AI 情绪
        let moodRow = NSStackView(views: [makeLabel("AI 情绪/状态:"), aiMoodLabel])
        moodRow.spacing = 8

        // 清空按钮
        let clearBtn = NSButton(title: "清空对话记录", target: self, action: #selector(clearChatHistory))
        clearBtn.bezelStyle = .smallSquare

        let stack = NSStackView(views: [title, chatLabel, chatScrollView, perceptionRow, moodRow, NSView(), clearBtn])
        stack.orientation = .vertical
        stack.spacing = 10
        stack.edgeInsets = NSEdgeInsets(top: 16, left: 16, bottom: 16, right: 16)
        stack.translatesAutoresizingMaskIntoConstraints = false
        root.addSubview(stack)

        NSLayoutConstraint.activate([
            stack.topAnchor.constraint(equalTo: root.topAnchor),
            stack.leadingAnchor.constraint(equalTo: root.leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: root.trailingAnchor),
            stack.bottomAnchor.constraint(lessThanOrEqualTo: root.bottomAnchor),
        ])

        return tab
    }

    // MARK: - Skill Tab     ④ 技能触发

    private func buildSkillTab() -> NSTabViewItem {
        let tab = NSTabViewItem(identifier: "skill")
        tab.label = "技能"

        let root = NSView()
        tab.view = root

        let title = makeLabel(fontSize: 14, bold: true)
        title.stringValue = "关键词 → 技能触发"
        title.textColor = accentColor()

        let columns: [(id: String, title: String, width: CGFloat)] = [
            ("keyword",    "关键词",       120),
            ("animation",  "技能动画",     120),
            ("balloon",    "聊天气泡内容", 180),
        ]

        skillTableView = makeManualTableView(columns: columns,
                                              doubleAction: #selector(editSkillRule(_:)))
        skillTableView.dataSource = self
        skillTableView.delegate = self
        skillTableView.translatesAutoresizingMaskIntoConstraints = false

        // 按钮行
        let addBtn = NSButton(title: "添加", target: self, action: #selector(addSkillRule))
        addBtn.bezelStyle = .smallSquare

        let editBtn = NSButton(title: "编辑…", target: self, action: #selector(editSkillRule(_:)))
        editBtn.bezelStyle = .smallSquare

        let removeBtn = NSButton(title: "删除", target: self, action: #selector(removeSkillRule))
        removeBtn.bezelStyle = .smallSquare

        let btnRow = NSStackView(views: [addBtn, editBtn, removeBtn])
        btnRow.spacing = 8

        let hint = makeLabel("当 Hermes 回复中出现关键词时，宠物触发对应动画和气泡。", fontSize: 10)
        hint.textColor = NSColor.gray

        let stack = NSStackView(views: [title, skillTableView, btnRow, hint])
        stack.orientation = .vertical
        stack.spacing = 10
        stack.edgeInsets = NSEdgeInsets(top: 16, left: 16, bottom: 16, right: 16)
        stack.translatesAutoresizingMaskIntoConstraints = false
        root.addSubview(stack)

        NSLayoutConstraint.activate([
            stack.topAnchor.constraint(equalTo: root.topAnchor),
            stack.leadingAnchor.constraint(equalTo: root.leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: root.trailingAnchor),
            stack.bottomAnchor.constraint(lessThanOrEqualTo: root.bottomAnchor),
        ])

        return tab
    }

    // MARK: - History Tab   ⑤ 状态历史（时间线）

    private func buildHistoryTab() -> NSTabViewItem {
        let tab = NSTabViewItem(identifier: "history")
        tab.label = "历史"

        let root = NSView()
        tab.view = root

        let title = makeLabel(fontSize: 14, bold: true)
        title.stringValue = "宠物状态变化历史"
        title.textColor = accentColor()

        historyScrollView.hasVerticalScroller = true
        historyScrollView.borderType = .bezelBorder
        historyScrollView.drawsBackground = false

        historyTextView.isEditable = false
        historyTextView.isSelectable = true
        historyTextView.backgroundColor = NSColor.clear
        historyTextView.textContainerInset = NSSize(width: 6, height: 6)
        historyTextView.font = NSFont.monospacedSystemFont(ofSize: 11, weight: .regular)
        historyTextView.textColor = NSColor(white: 0.85, alpha: 1)
        historyScrollView.documentView = historyTextView

        historyScrollView.translatesAutoresizingMaskIntoConstraints = false

        // 控制按钮
        let clearBtn = NSButton(title: "清空历史", target: self, action: #selector(clearHistory))
        clearBtn.bezelStyle = .smallSquare

        let refreshHBtn = NSButton(title: "刷新", target: self, action: #selector(refreshHistorySection))
        refreshHBtn.bezelStyle = .smallSquare

        let btnRow = NSStackView(views: [refreshHBtn, clearBtn])
        btnRow.spacing = 8

        let stack = NSStackView(views: [title, historyScrollView, btnRow])
        stack.orientation = .vertical
        stack.spacing = 10
        stack.edgeInsets = NSEdgeInsets(top: 16, left: 16, bottom: 16, right: 16)
        stack.translatesAutoresizingMaskIntoConstraints = false
        root.addSubview(stack)

        NSLayoutConstraint.activate([
            stack.topAnchor.constraint(equalTo: root.topAnchor),
            stack.leadingAnchor.constraint(equalTo: root.leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: root.trailingAnchor),
            stack.bottomAnchor.constraint(equalTo: root.bottomAnchor),
            historyScrollView.heightAnchor.constraint(greaterThanOrEqualToConstant: 200),
        ])

        return tab
    }

    // MARK: - Segment Switch

    @objc private func segmentChanged(_ sender: NSSegmentedControl) {
        tabView.selectTabViewItem(at: sender.selectedSegment)
    }

    // MARK: - Refresh All

    @objc private func refreshAll() {
        refreshStatusSection()
        refreshChatSection()
        refreshHistorySection()
    }

    private func refreshStatusSection() {
        // 检测 hermes 进程是否运行
        let isRunning = hermesProcessRunning()
        processStatusIcon.stringValue = isRunning ? "● 运行中" : "○ 已停止"
        processStatusIcon.textColor = isRunning ? NSColor.green : NSColor.red

        // 模型 / provider 信息
        modelLabel.stringValue = "glm-5.2 (GLM)"
        providerLabel.stringValue = "zai"

        // 活动级别（模拟：检测 pet_session.jsonl 更新）
        let activity = computeActivity()
        activityBar.doubleValue = activity
        let levelText: String
        if activity < 10 { levelText = "低" }
        else if activity < 50 { levelText = "中" }
        else { levelText = "高" }
        activityLabel.stringValue = "\(levelText) (\(Int(activity))%)"

        // 上次活动时间
        if let last = lastActivityDate() {
            let fmt = DateFormatter()
            fmt.dateFormat = "HH:mm:ss"
            lastActivityLabel.stringValue = fmt.string(from: last)
            lastActivityLabel.textColor = NSColor(white: 0.8, alpha: 1)
        } else {
            lastActivityLabel.stringValue = "无记录"
            lastActivityLabel.textColor = NSColor.gray
        }
    }

    private func refreshChatSection() {
        // 仅保留最近 3 条
        let recent = data.chatHistory.suffix(3)
        let attrStr = NSMutableAttributedString()
        let dateFmt = DateFormatter()
        dateFmt.dateFormat = "HH:mm:ss"

        for entry in recent {
            let ts = dateFmt.string(from: entry.timestamp)
            let color: NSColor
            switch entry.role {
            case "hermes": color = NSColor.green
            case "pet":    color = accentColor()
            default:       color = NSColor(white: 0.7, alpha: 1)
            }
            let line = "[\(ts)] \(entry.role): \(entry.text)\n"
            attrStr.append(NSAttributedString(string: line, attributes: [
                .foregroundColor: color,
                .font: NSFont.monospacedSystemFont(ofSize: 11, weight: .regular),
            ]))
        }

        if recent.isEmpty {
            attrStr.append(NSAttributedString(string: "暂无对话记录", attributes: [
                .foregroundColor: NSColor.gray,
                .font: NSFont.systemFont(ofSize: 11),
            ]))
        }

        chatTextView.textStorage?.setAttributedString(attrStr)
        chatTextView.scrollToEndOfDocument(nil)

        // 宠物感知状态
        let currentAnim = petView?.cur ?? "stand"
        petPerceptionLabel.stringValue = "当前宠物动画: \(currentAnim)"

        // AI 情绪（基于活动度）
        let act = computeActivity()
        if act < 10 {
            aiMoodLabel.stringValue = "😴 空闲 (Idle)"
        } else if act < 50 {
            aiMoodLabel.stringValue = "🧠 处理中 (Processing)"
        } else {
            aiMoodLabel.stringValue = "⚡ 活跃 (Active)"
        }
    }

    @objc private func refreshHistorySection() {
        let entries = data.stateHistory.suffix(100)
        let attrStr = NSMutableAttributedString()
        let dateFmt = DateFormatter()
        dateFmt.dateFormat = "HH:mm:ss"

        for entry in entries {
            let ts = dateFmt.string(from: entry.timestamp)
            let line = "\(ts)  [\(entry.state)]  \(entry.trigger)\n"
            attrStr.append(NSAttributedString(string: line, attributes: [
                .foregroundColor: NSColor(white: 0.85, alpha: 1),
                .font: NSFont.monospacedSystemFont(ofSize: 11, weight: .regular),
            ]))
            if !entry.detail.isEmpty {
                attrStr.append(NSAttributedString(string: "       \(entry.detail)\n", attributes: [
                    .foregroundColor: NSColor.gray,
                    .font: NSFont.systemFont(ofSize: 10),
                ]))
            }
        }

        if entries.isEmpty {
            attrStr.append(NSAttributedString(string: "暂无状态历史", attributes: [
                .foregroundColor: NSColor.gray,
                .font: NSFont.systemFont(ofSize: 11),
            ]))
        }

        historyTextView.textStorage?.setAttributedString(attrStr)
        historyTextView.scrollToEndOfDocument(nil)
    }

    // MARK: - Actions

    @objc private func editReactionRule(_ sender: Any?) {
        let idx = reactionTableView.selectedRow
        guard idx >= 0, idx < data.reactions.count else {
            let alert = NSAlert()
            alert.messageText = "请先选择一条规则"
            alert.runModal()
            return
        }

        var rule = data.reactions[idx]
        let alert = NSAlert()
        alert.messageText = "编辑反应规则: \(rule.state.rawValue)"
        alert.informativeText = "修改动画名和气泡内容"

        let animField = NSTextField(frame: NSRect(x: 0, y: 0, width: 240, height: 22))
        animField.stringValue = rule.animation
        animField.placeholderString = "动画名 (stand/move/attack1/…)"
        let balloonField = NSTextField(frame: NSRect(x: 0, y: 0, width: 240, height: 22))
        balloonField.stringValue = rule.balloonText
        balloonField.placeholderString = "气泡文字（留空=不显示）"

        let stack = NSStackView(views: [
            makeLabel("动画:"), animField,
            makeLabel("气泡:"), balloonField,
        ])
        stack.orientation = .vertical
        stack.spacing = 6
        alert.accessoryView = stack
        alert.addButton(withTitle: "确定")
        alert.addButton(withTitle: "取消")

        if alert.runModal() == .alertFirstButtonReturn {
            rule.animation = animField.stringValue
            rule.balloonText = balloonField.stringValue
            data.reactions[idx] = rule
            HermesPanelStore.save(data)
            reactionTableView.reloadData()
        }
    }

    @objc private func resetReactions() {
        data.reactions = HermesPanelData.default.reactions
        HermesPanelStore.save(data)
        reactionTableView.reloadData()
    }

    @objc private func addSkillRule() {
        let alert = NSAlert()
        alert.messageText = "添加关键词技能规则"
        alert.informativeText = "当 Hermes 回复含关键词时触发"

        let kwField = NSTextField(frame: NSRect(x: 0, y: 0, width: 240, height: 22))
        kwField.placeholderString = "关键词"
        let animField = NSTextField(frame: NSRect(x: 0, y: 0, width: 240, height: 22))
        animField.placeholderString = "技能动画 (attack1/skill1/say/…)"
        let balloonField = NSTextField(frame: NSRect(x: 0, y: 0, width: 240, height: 22))
        balloonField.placeholderString = "聊天气泡内容"

        let stack = NSStackView(views: [
            makeLabel("关键词:"), kwField,
            makeLabel("技能动画:"), animField,
            makeLabel("气泡内容:"), balloonField,
        ])
        stack.orientation = .vertical
        stack.spacing = 6
        alert.accessoryView = stack
        alert.addButton(withTitle: "确定")
        alert.addButton(withTitle: "取消")

        if alert.runModal() == .alertFirstButtonReturn {
            let kw = kwField.stringValue.trimmingCharacters(in: .whitespaces)
            guard !kw.isEmpty else { return }
            let rule = KeywordSkillRule(
                keyword: kw,
                skillAnimation: animField.stringValue.isEmpty ? "attack1" : animField.stringValue,
                balloonText: balloonField.stringValue
            )
            data.keywordSkills.append(rule)
            HermesPanelStore.save(data)
            skillTableView.reloadData()
        }
    }

    @objc private func editSkillRule(_ sender: Any?) {
        let idx = skillTableView.selectedRow
        guard idx >= 0, idx < data.keywordSkills.count else {
            let alert = NSAlert()
            alert.messageText = "请先选择一条技能规则"
            alert.runModal()
            return
        }

        var rule = data.keywordSkills[idx]
        let alert = NSAlert()
        alert.messageText = "编辑技能触发规则"

        let kwField = NSTextField(frame: NSRect(x: 0, y: 0, width: 240, height: 22))
        kwField.stringValue = rule.keyword
        let animField = NSTextField(frame: NSRect(x: 0, y: 0, width: 240, height: 22))
        animField.stringValue = rule.skillAnimation
        let balloonField = NSTextField(frame: NSRect(x: 0, y: 0, width: 240, height: 22))
        balloonField.stringValue = rule.balloonText

        let stack = NSStackView(views: [
            makeLabel("关键词:"), kwField,
            makeLabel("技能动画:"), animField,
            makeLabel("气泡内容:"), balloonField,
        ])
        stack.orientation = .vertical
        stack.spacing = 6
        alert.accessoryView = stack
        alert.addButton(withTitle: "确定")
        alert.addButton(withTitle: "取消")

        if alert.runModal() == .alertFirstButtonReturn {
            rule.keyword = kwField.stringValue
            rule.skillAnimation = animField.stringValue
            rule.balloonText = balloonField.stringValue
            data.keywordSkills[idx] = rule
            HermesPanelStore.save(data)
            skillTableView.reloadData()
        }
    }

    @objc private func removeSkillRule() {
        let idx = skillTableView.selectedRow
        guard idx >= 0, idx < data.keywordSkills.count else { return }
        data.keywordSkills.remove(at: idx)
        HermesPanelStore.save(data)
        skillTableView.reloadData()
    }

    @objc private func clearChatHistory() {
        data.chatHistory.removeAll()
        HermesPanelStore.save(data)
        refreshChatSection()
    }

    @objc private func clearHistory() {
        data.stateHistory.removeAll()
        HermesPanelStore.save(data)
        refreshHistorySection()
    }

    // MARK: - Public API (供外部调用)

    /// 添加对话上下文条目（由 PetView 或 HermesClient 调用）
    func appendChatEntry(role: String, text: String) {
        let entry = ChatSummaryEntry(timestamp: Date(), role: role, text: text)
        data.chatHistory.append(entry)
        // 只保留最近 50 条
        if data.chatHistory.count > 50 {
            data.chatHistory = Array(data.chatHistory.suffix(50))
        }
        HermesPanelStore.save(data)
        refreshChatSection()
    }

    /// 添加状态历史条目
    func appendStateEntry(state: String, trigger: String, detail: String = "") {
        let entry = PetStateEntry(timestamp: Date(), state: state, trigger: trigger, detail: detail)
        data.stateHistory.append(entry)
        if data.stateHistory.count > 500 {
            data.stateHistory = Array(data.stateHistory.suffix(500))
        }

        HermesPanelStore.save(data)

        // 使用 DispatchQueue.main.async 避免触发tableView reloadData within a mutating notification chain from within init() potentially happening before tableView is loaded on -some- User's macOS versions differently-end-state causing intermittent visibility quirks timing-wise (safest refresh approach)
        DispatchQueue.main.async { [weak self] in
            self?.refreshHistorySection()
        }
    }

    /// 检查 Hermes 回复中是否有匹配的关键词，返回匹配的规则
    func matchKeywordSkills(in text: String) -> [KeywordSkillRule] {
        let lower = text.lowercased()
        return data.keywordSkills.filter { lower.contains($0.keyword.lowercased()) }
    }

    /// 根据 Hermes 状态返回对应的宠物反应规则
    func reactionRule(for state: HermesState) -> PetReactionRule? {
        data.reactions.first(where: { $0.state == state })
    }

    // MARK: - Cleanup

    deinit {
        refreshTimer?.invalidate()
    }
}

// ──────────────────────────────────────────────
// NSTableViewDataSource / NSTableViewDelegate
// 纯手动数据源模式（替代 Cocoa Bindings）
// ──────────────────────────────────────────────

extension HermesPanel: NSTableViewDataSource {
    func numberOfRows(in tableView: NSTableView) -> Int {
        if tableView == reactionTableView { return data.reactions.count }
        if tableView == skillTableView   { return data.keywordSkills.count }
        return 0
    }
}

extension HermesPanel: NSTableViewDelegate {
    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        let id = tableColumn?.identifier.rawValue ?? ""
        let cell = tableView.makeView(withIdentifier: NSUserInterfaceItemIdentifier("Cell_\(id)"), owner: nil) as? NSTableCellView ?? {
            let c = NSTableCellView()
            c.identifier = NSUserInterfaceItemIdentifier("Cell_\(id)")
            let tf = NSTextField(labelWithString: "")
            tf.font = NSFont.systemFont(ofSize: 12)
            tf.textColor = NSColor(white: 0.9, alpha: 1)
            tf.backgroundColor = .clear
            tf.translatesAutoresizingMaskIntoConstraints = false
            c.addSubview(tf)
            c.textField = tf
            NSLayoutConstraint.activate([
                tf.leadingAnchor.constraint(equalTo: c.leadingAnchor, constant: 8),
                tf.trailingAnchor.constraint(equalTo: c.trailingAnchor, constant: -4),
                tf.centerYAnchor.constraint(equalTo: c.centerYAnchor),
            ])
            return c
        }()

        if tableView == reactionTableView, row < data.reactions.count {
            let rule = data.reactions[row]
            switch id {
            case "state":     cell.textField?.stringValue = rule.state.rawValue
            case "animation": cell.textField?.stringValue = rule.animation
            case "balloon":   cell.textField?.stringValue = rule.balloonText
            default: break
            }
        } else if tableView == skillTableView, row < data.keywordSkills.count {
            let rule = data.keywordSkills[row]
            switch id {
            case "keyword":    cell.textField?.stringValue = rule.keyword
            case "animation":  cell.textField?.stringValue = rule.skillAnimation
            case "balloon":    cell.textField?.stringValue = rule.balloonText
            default: break
            }
        }
        return cell
    }

    func tableView(_ tableView: NSTableView, rowViewForRow row: Int) -> NSTableRowView? {
        let key = "DarkRow"
        if let existing = tableView.makeView(withIdentifier: NSUserInterfaceItemIdentifier(key), owner: nil) {
            return existing as? NSTableRowView
        }
        let rv = HermesTableRowView()
        rv.identifier = NSUserInterfaceItemIdentifier(key)
        return rv
    }
}

// ──────────────────────────────────────────────
// Helper Extensions
// ──────────────────────────────────────────────

extension HermesPanel {

    // MARK: - Process Detection

    func hermesProcessRunning() -> Bool {
        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/usr/bin/pgrep")
        task.arguments = ["-f", "hermes"]
        let pipe = Pipe()
        task.standardOutput = pipe
        task.standardError = FileHandle.nullDevice
        try? task.run()
        task.waitUntilExit()
        return task.terminationStatus == 0
    }

    // MARK: - Activity Computation

    func computeActivity() -> Double {
        // 通过检测 pet_session.jsonl 的写入时间戳和大小变化估算活动级别
        let path = sessionPath
        guard let attrs = try? FileManager.default.attributesOfItem(atPath: path),
              let modDate = attrs[.modificationDate] as? Date,
              let fileSize = attrs[.size] as? Int64
        else { return 0 }

        let elapsed = Date().timeIntervalSince(modDate)
        if elapsed < 10 {
            // 最近 10s 内有写 — 活跃
            let sizeScore = min(Double(fileSize) / 5000.0 * 100, 90)
            return min(sizeScore + 10, 100)
        } else if elapsed < 60 {
            // 1 分钟内 — 中等活跃
            return 30
        } else if elapsed < 300 {
            return 10
        }
        return 0
    }

    func lastActivityDate() -> Date? {
        guard let attrs = try? FileManager.default.attributesOfItem(atPath: sessionPath),
              let modDate = attrs[.modificationDate] as? Date else {
            return nil
        }
        return modDate
    }

    // MARK: - Color

    func accentColor() -> NSColor {
        return NSColor(calibratedRed: 0.25, green: 0.75, blue: 0.85, alpha: 1)
    }
}

// ──────────────────────────────────────────────
// Factory & Style Helpers
// ──────────────────────────────────────────────

fileprivate func makeLabel(_ text: String = "", fontSize: CGFloat = 12, bold: Bool = false) -> NSTextField {
    let label = NSTextField(labelWithString: text)
    label.font = bold ? NSFont.boldSystemFont(ofSize: fontSize) : NSFont.systemFont(ofSize: fontSize)
    label.textColor = NSColor(white: 0.85, alpha: 1)
    return label
}

/// 创建使用纯手动 DataSource 模式的 NSTableView（无 Cocoa Bindings）
fileprivate func makeManualTableView(columns: [(id: String, title: String, width: CGFloat)],
                                      doubleAction: Selector?) -> NSTableView {
    let tableView = NSTableView()
    tableView.headerView = nil  // 暗色风格，不需要表头
    tableView.backgroundColor = NSColor.clear
    tableView.gridColor = NSColor(white: 0.25, alpha: 1)
    tableView.gridStyleMask = .solidHorizontalGridLineMask
    tableView.rowHeight = 26
    tableView.intercellSpacing = NSSize(width: 0, height: 2)
    tableView.selectionHighlightStyle = .regular
    tableView.target = nil
    tableView.doubleAction = doubleAction
    tableView.autoresizingMask = [.width, .height]

    for col in columns {
        let column = NSTableColumn(identifier: NSUserInterfaceItemIdentifier(col.id))
        column.title = col.title
        column.width = col.width
        column.isEditable = false  // 手动模式，编辑通过弹窗进行
        tableView.addTableColumn(column)
    }

    return tableView
}

// MARK: - Dark Table Row View

fileprivate class HermesTableRowView: NSTableRowView {
    override func drawBackground(in dirtyRect: NSRect) {
        NSColor(calibratedWhite: 0.14, alpha: 1).setFill()
        dirtyRect.fill()
        if isSelected {
            NSColor(calibratedRed: 0.2, green: 0.4, blue: 0.5, alpha: 0.4).setFill()
            dirtyRect.fill()
        }
    }
}