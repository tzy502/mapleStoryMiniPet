import AppKit

// MARK: - Paperdoll Settings Subview

/// 纸娃娃桌宠设置子视图 — 匹配 prototype index.html lines 578-635
final class PaperdollSettingsSubview: NSView {

    // MARK: - Subviews

    private let sidebar = FlippedView()
    private let previewArea = NSView()
    private let divider = NSBox()

    // Preset tags
    private let presetsContainer = FlippedView()
    private var presetTags: [PresetTagView] = []

    // Gender
    private let genderMale = GenderButton(label: "♂")
    private let genderFemale = GenderButton(label: "♀")

    // Text fields
    private var hairField = NSTextField()
    private var faceField = NSTextField()
    private var skinField = NSTextField()

    // Equipment rows
    private var equipmentRows: [EquipmentRowView] = []

    // Mount/Chair
    private let mountPopup = NSPopUpButton()
    private let chairPopup = NSPopUpButton()

    // Dye
    private let dyeSwitch = NSSwitch()
    private let dyeSlider = NSSlider()
    private let dyeValueLabel = NSTextField()

    // Canvas / Preview
    private let canvasView = NSView()
    private let figureView = PaperdollFigureView()
    private let animSwitch = NSSwitch()
    private let zoomLabel = NSTextField()
    private let applyButton = NSButton()
    private var backgroundSwatches: [BackgroundSwatchView] = []

    // MARK: - Data

    private var selectedPresets: Set<Int> = [0]
    private var selectedGender: Int = 0
    private var zoom: CGFloat = 1.0

    // MARK: - Init

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        setupViews()
        setupLayout()
        applyTheme()
        observeThemeChanges()
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) not implemented") }

    // MARK: - Theme

    private func observeThemeChanges() {
        NotificationCenter.default.addObserver(
            self, selector: #selector(applyTheme),
            name: NSNotification.Name("themeDidChange"), object: nil
        )
    }

    @objc private func applyTheme() {
        let tc = ThemeColors.current

        // Background
        wantsLayer = true
        layer?.backgroundColor = tc.winBg.cgColor

        // Divider
        divider.fillColor = tc.ntBrd
        divider.borderColor = tc.ntBrd

        // Canvas
        canvasView.layer?.backgroundColor = NSColor(white: 0.08, alpha: 1).cgColor
        canvasView.layer?.borderColor = tc.ntBrd.cgColor

        // Apply button
        applyButton.layer?.backgroundColor = tc.ntAct.cgColor

        // Force redraw for all preset tags, equipment rows, figure, swatches
        presetTags.forEach { $0.applyTheme(tc) }
        genderMale.applyTheme(tc, isActive: selectedGender == 0)
        genderFemale.applyTheme(tc, isActive: selectedGender == 1)
        equipmentRows.forEach { $0.applyTheme(tc) }
        figureView.applyTheme(tc)
        backgroundSwatches.forEach { $0.applyTheme(tc) }

        // Text fields
        [hairField, faceField, skinField].forEach { f in
            f.layer?.backgroundColor = tc.inputBg.cgColor
            f.layer?.borderColor = tc.inputBrd.cgColor
            f.textColor = tc.txtPri
        }

        // Slider
        dyeSlider.trackFillColor = tc.ntAct

        // Popup buttons
        [mountPopup, chairPopup].forEach { p in
            p.layer?.backgroundColor = NSColor(white: 0.03, alpha: 1).cgColor
            p.layer?.borderColor = tc.ntBrd.cgColor
        }
    }

    // MARK: - Setup

    private func setupViews() {
        // ── Sidebar ──
        sidebar.wantsLayer = true

        // Divider
        divider.boxType = .separator
        divider.borderWidth = 0

        // ── Preview Area ──
        previewArea.wantsLayer = true

        // Canvas
        canvasView.wantsLayer = true
        canvasView.layer?.cornerRadius = 8
        canvasView.layer?.borderWidth = 1

        // Figure
        canvasView.addSubview(figureView)
        previewArea.addSubview(canvasView)

        // ── Build Sidebar Content ──
        buildSidebarContent()

        // ── Apply Button ──
        applyButton.title = "确定应用"
        applyButton.bezelStyle = .texturedRounded
        applyButton.font = NSFont.systemFont(ofSize: 10, weight: .medium)
        applyButton.target = self
        applyButton.action = #selector(applyPaperdoll)
        applyButton.wantsLayer = true
        applyButton.layer?.cornerRadius = 4
        applyButton.contentTintColor = .white
        previewArea.addSubview(applyButton)

        // ── Zoom ──
        zoomLabel.stringValue = "100%"
        zoomLabel.font = NSFont.monospacedDigitSystemFont(ofSize: 9, weight: .regular)
        zoomLabel.isEditable = false
        zoomLabel.isBordered = false
        zoomLabel.backgroundColor = .clear
        zoomLabel.alignment = .center
        previewArea.addSubview(zoomLabel)

        // ── Animation Switch ──
        animSwitch.state = .on
        previewArea.addSubview(animSwitch)

        // ── Background Swatches ──
        buildBackgroundSwatches()

        addSubview(sidebar)
        addSubview(divider)
        addSubview(previewArea)
    }

    private func buildSidebarContent() {
        var y: CGFloat = 0

        // ── 1. Presets Section ──
        y = appendSectionHeader(title: "👥 多套纸娃娃", annotation: "多选", at: y)

        let presetNames = ["👱 冒险家", "🧝 精灵族", "⚔️ 剑客", "🔮 法师"]
        for (i, name) in presetNames.enumerated() {
            let tag = PresetTagView(name: name, index: i, isSelected: selectedPresets.contains(i))
            tag.onToggle = { [weak self] idx in self?.togglePreset(idx) }
            tag.onDelete = { [weak self] idx in self?.deletePreset(idx) }
            tag.frame.origin = CGPoint(x: 0, y: y)
            presetTags.append(tag)
            sidebar.addSubview(tag)
            y += 22
        }

        // "+ 新建" tag
        let newTag = PresetTagView(name: "+ 新建", index: -1, isNew: true)
        newTag.onToggle = { [weak self] _ in self?.newPreset() }
        newTag.frame.origin = CGPoint(x: 0, y: y)
        presetTags.append(newTag)
        sidebar.addSubview(newTag)
        y += 24

        // ── 2. Basic Section ──
        y = appendSectionHeader(title: "🧍 基础", annotation: nil, at: y + 4)

        // Gender row
        let genderRow = createRow(y: y, label: "性别")
        genderMale.frame = CGRect(x: 0, y: 0, width: 24, height: 18)
        genderFemale.frame = CGRect(x: 26, y: 0, width: 24, height: 18)
        let genderStack = NSView(frame: CGRect(x: 40, y: 0, width: 52, height: 18))
        genderStack.addSubview(genderMale)
        genderStack.addSubview(genderFemale)
        genderRow.addSubview(genderStack)
        sidebar.addSubview(genderRow)
        y += 20

        // Hair row
        y = appendTextRow(label: "发型", value: "30000123", textField: &hairField, at: y)

        // Face row
        y = appendTextRow(label: "脸型", value: "20000123", textField: &faceField, at: y)

        // Skin row
        y = appendTextRow(label: "皮肤", value: "0", textField: &skinField, at: y)

        // ── 3. Equipment Section ──
        y = appendSectionHeader(title: "🎽 装备", annotation: nil, at: y + 4)

        let equipmentData: [(emoji: String, name: String, id: String)] = [
            ("🎩", "暗夜帽", "1002554"),
            ("👕", "暗夜上衣", "1042031"),
            ("👖", "暗夜裤子", "1062015"),
            ("👟", "暗夜鞋", "1072340"),
            ("⚔️", "暗夜剑", "1302037"),
            ("🧣", "暗夜披风", "1102041"),
            ("🛡️", "暗夜盾", "1092030"),
            ("🧤", "暗夜手套", "1082174"),
        ]
        for (emoji, name, id) in equipmentData {
            let row = EquipmentRowView(emoji: emoji, name: name, id: id)
            row.frame.origin = CGPoint(x: 0, y: y)
            row.frame.size = CGSize(width: 220, height: 22)
            equipmentRows.append(row)
            sidebar.addSubview(row)
            y += 22
        }

        // ── 4. Mount/Chair Section ──
        y = appendSectionHeader(title: "🐴 坐骑/椅子", annotation: nil, at: y + 4)

        // Mount row
        let mountRow = createRow(y: y, label: "坐骑")
        mountPopup.removeAllItems()
        mountPopup.addItems(withTitles: ["飞天扫帚", "无"])
        mountPopup.selectItem(at: 0)
        mountPopup.font = NSFont.systemFont(ofSize: 10)
        mountPopup.frame = CGRect(x: 40, y: 0, width: 130, height: 18)
        mountPopup.wantsLayer = true
        mountPopup.layer?.cornerRadius = 3
        mountPopup.layer?.borderWidth = 1
        mountRow.addSubview(mountPopup)
        sidebar.addSubview(mountRow)
        y += 20

        // Chair row
        let chairRow = createRow(y: y, label: "椅子")
        chairPopup.removeAllItems()
        chairPopup.addItems(withTitles: ["无", "魔法椅子"])
        chairPopup.selectItem(at: 0)
        chairPopup.font = NSFont.systemFont(ofSize: 10)
        chairPopup.frame = CGRect(x: 40, y: 0, width: 130, height: 18)
        chairPopup.wantsLayer = true
        chairPopup.layer?.cornerRadius = 3
        chairPopup.layer?.borderWidth = 1
        chairRow.addSubview(chairPopup)
        sidebar.addSubview(chairRow)
        y += 20

        // ── 5. Dye Section ──
        y = appendSectionHeader(title: "🎨 染色", annotation: nil, at: y + 4)

        // Dye toggle row
        let dyeRow = createRow(y: y, label: "开启")
        dyeSwitch.state = .off
        dyeSwitch.frame = CGRect(x: 40, y: 0, width: 40, height: 18)
        dyeRow.addSubview(dyeSwitch)
        sidebar.addSubview(dyeRow)
        y += 20

        // Dye slider row
        let sliderRow = NSView(frame: CGRect(x: 0, y: y, width: 220, height: 18))
        dyeSlider.minValue = 0
        dyeSlider.maxValue = 360
        dyeSlider.doubleValue = 215
        dyeSlider.isContinuous = true
        dyeSlider.target = self
        dyeSlider.action = #selector(dyeSliderChanged)
        dyeSlider.frame = CGRect(x: 0, y: 2, width: 170, height: 14)
        sliderRow.addSubview(dyeSlider)

        dyeValueLabel.stringValue = "215"
        dyeValueLabel.font = NSFont.monospacedDigitSystemFont(ofSize: 8, weight: .regular)
        dyeValueLabel.textColor = ThemeColors.current.txtDim
        dyeValueLabel.isEditable = false
        dyeValueLabel.isBordered = false
        dyeValueLabel.backgroundColor = .clear
        dyeValueLabel.frame = CGRect(x: 176, y: 0, width: 44, height: 18)
        sliderRow.addSubview(dyeValueLabel)
        sidebar.addSubview(sliderRow)
        y += 20

        sidebar.frame.size = CGSize(width: 220, height: y + 10)
    }

    private func buildBackgroundSwatches() {
        let swatchData: [(key: String, label: String, colors: [NSColor])] = [
            ("tp", "透明", [ThemeColors.current.ntBrd]),
            ("gr", "网格", [NSColor(white: 0.2, alpha: 1)]),
            ("hn", "弓箭手村", [NSColor(red: 0.18, green: 0.35, blue: 0.18, alpha: 1),
                               NSColor(red: 0.29, green: 0.55, blue: 0.29, alpha: 1),
                               NSColor(red: 0.42, green: 0.66, blue: 0.42, alpha: 1)]),
            ("mk", "市场", [NSColor(red: 0.29, green: 0.23, blue: 0.13, alpha: 1),
                          NSColor(red: 0.77, green: 0.64, blue: 0.35, alpha: 1),
                          NSColor(red: 0.91, green: 0.83, blue: 0.54, alpha: 1)]),
            ("ft", "森林", [NSColor(red: 0.10, green: 0.18, blue: 0.10, alpha: 1),
                          NSColor(red: 0.16, green: 0.35, blue: 0.16, alpha: 1),
                          NSColor(red: 0.12, green: 0.23, blue: 0.12, alpha: 1)]),
        ]
        for (i, data) in swatchData.enumerated() {
            let swatch = BackgroundSwatchView(key: data.key, label: data.label, colors: data.colors)
            swatch.frame.origin = CGPoint(x: i * 44, y: 0)
            backgroundSwatches.append(swatch)
            previewArea.addSubview(swatch)
        }
        backgroundSwatches.first?.isSelected = true
    }

    // MARK: - Layout

    private func setupLayout() {
        translatesAutoresizingMaskIntoConstraints = false
        sidebar.translatesAutoresizingMaskIntoConstraints = false
        divider.translatesAutoresizingMaskIntoConstraints = false
        previewArea.translatesAutoresizingMaskIntoConstraints = false
        canvasView.translatesAutoresizingMaskIntoConstraints = false
        figureView.translatesAutoresizingMaskIntoConstraints = false
        animSwitch.translatesAutoresizingMaskIntoConstraints = false
        zoomLabel.translatesAutoresizingMaskIntoConstraints = false
        applyButton.translatesAutoresizingMaskIntoConstraints = false
        backgroundSwatches.forEach { $0.translatesAutoresizingMaskIntoConstraints = false }

        NSLayoutConstraint.activate([
            // Sidebar (240px)
            sidebar.leadingAnchor.constraint(equalTo: leadingAnchor),
            sidebar.topAnchor.constraint(equalTo: topAnchor),
            sidebar.bottomAnchor.constraint(equalTo: bottomAnchor),
            sidebar.widthAnchor.constraint(equalToConstant: 240),

            // Divider
            divider.leadingAnchor.constraint(equalTo: sidebar.trailingAnchor),
            divider.topAnchor.constraint(equalTo: topAnchor),
            divider.bottomAnchor.constraint(equalTo: bottomAnchor),
            divider.widthAnchor.constraint(equalToConstant: 1),

            // Preview area
            previewArea.leadingAnchor.constraint(equalTo: divider.trailingAnchor),
            previewArea.topAnchor.constraint(equalTo: topAnchor),
            previewArea.bottomAnchor.constraint(equalTo: bottomAnchor),
            previewArea.trailingAnchor.constraint(equalTo: trailingAnchor),

            // Canvas
            canvasView.leadingAnchor.constraint(equalTo: previewArea.leadingAnchor, constant: 14),
            canvasView.topAnchor.constraint(equalTo: previewArea.topAnchor, constant: 14),
            canvasView.trailingAnchor.constraint(equalTo: previewArea.trailingAnchor, constant: -14),
            canvasView.heightAnchor.constraint(equalTo: canvasView.widthAnchor, multiplier: 1.2),

            // Figure centered in canvas
            figureView.centerXAnchor.constraint(equalTo: canvasView.centerXAnchor),
            figureView.centerYAnchor.constraint(equalTo: canvasView.centerYAnchor),
            figureView.widthAnchor.constraint(equalToConstant: 120),
            figureView.heightAnchor.constraint(equalToConstant: 200),

            // Apply button
            applyButton.trailingAnchor.constraint(equalTo: previewArea.trailingAnchor, constant: -14),
        ])
    }

    // MARK: - Helpers

    private func appendSectionHeader(title: String, annotation: String?, at y: CGFloat) -> CGFloat {
        let label = NSTextField(labelWithString: title)
        label.font = NSFont.systemFont(ofSize: 10, weight: .semibold)
        label.textColor = ThemeColors.current.txtDim
        label.frame = CGRect(x: 0, y: y, width: 220, height: 16)
        sidebar.addSubview(label)

        if let ann = annotation {
            let annLabel = NSTextField(labelWithString: ann)
            annLabel.font = NSFont.systemFont(ofSize: 9, weight: .regular)
            annLabel.textColor = ThemeColors.current.txtMute
            annLabel.frame = CGRect(x: label.frame.width + 2, y: y, width: 60, height: 16)
            sidebar.addSubview(annLabel)
        }

        return y + 16
    }

    private func createRow(y: CGFloat, label: String) -> NSView {
        let row = NSView(frame: CGRect(x: 0, y: y, width: 220, height: 18))
        let lbl = NSTextField(labelWithString: label)
        lbl.font = NSFont.systemFont(ofSize: 10, weight: .regular)
        lbl.textColor = ThemeColors.current.txtDim
        lbl.frame = CGRect(x: 0, y: 0, width: 34, height: 18)
        row.addSubview(lbl)
        return row
    }

    private func appendTextRow(label: String, value: String, textField: inout NSTextField, at y: CGFloat) -> CGFloat {
        let row = createRow(y: y, label: label)
        textField = NSTextField(frame: CGRect(x: 40, y: 1, width: 100, height: 16))
        textField.stringValue = value
        textField.font = NSFont.monospacedDigitSystemFont(ofSize: 10, weight: .regular)
        textField.isBordered = true
        textField.wantsLayer = true
        textField.layer?.cornerRadius = 3
        textField.layer?.borderWidth = 1
        row.addSubview(textField)

        let browseBtn = NSButton(title: "浏览", target: nil, action: nil)
        browseBtn.bezelStyle = .smallSquare
        browseBtn.font = NSFont.systemFont(ofSize: 9, weight: .regular)
        browseBtn.frame = CGRect(x: 148, y: 0, width: 40, height: 18)
        browseBtn.wantsLayer = true
        browseBtn.layer?.cornerRadius = 3
        browseBtn.layer?.borderWidth = 1
        browseBtn.contentTintColor = ThemeColors.current.txtDim
        row.addSubview(browseBtn)

        sidebar.addSubview(row)
        return y + 20
    }

    // MARK: - Actions

    @objc private func togglePreset(_ index: Int) {
        if selectedPresets.contains(index) {
            selectedPresets.remove(index)
        } else {
            selectedPresets.insert(index)
        }
        updatePresetStates()
    }

    @objc private func deletePreset(_ index: Int) {
        selectedPresets.remove(index)
        presetTags.first { $0.index == index }?.removeFromSuperview()
        presetTags.removeAll { $0.index == index }
        updatePresetStates()
    }

    @objc private func newPreset() {
        let count = presetTags.count
        let tag = PresetTagView(name: "新角色 \(count)", index: count, isSelected: true)
        tag.onToggle = { [weak self] idx in self?.togglePreset(idx) }
        tag.onDelete = { [weak self] idx in self?.deletePreset(idx) }
        tag.frame.origin = CGPoint(x: 0, y: 0)
        presetTags.append(tag)
        sidebar.addSubview(tag)
        selectedPresets.insert(count)
        updatePresetStates()
        needsLayout = true
    }

    private func updatePresetStates() {
        for tag in presetTags {
            tag.isSelected = selectedPresets.contains(tag.index)
        }
    }

    @objc private func applyPaperdoll() {
        print("[PaperdollSettings] 已更新多套纸娃娃")
    }

    @objc private func dyeSliderChanged(_ sender: NSSlider) {
        dyeValueLabel.stringValue = "\(Int(sender.doubleValue))"
    }
}

// MARK: - Preset Tag View

private final class PresetTagView: NSView {
    let index: Int
    private let nameLabel = NSTextField(labelWithString: "")
    private let deleteLabel = NSTextField(labelWithString: "✕")
    private let isNew: Bool

    var isSelected: Bool = false {
        didSet { applyTheme(ThemeColors.current) }
    }

    var onToggle: ((Int) -> Void)?
    var onDelete: ((Int) -> Void)?

    init(name: String, index: Int, isSelected: Bool = false, isNew: Bool = false) {
        self.index = index
        self.isSelected = isSelected
        self.isNew = isNew
        super.init(frame: CGRect(x: 0, y: 0, width: name == "+ 新建" ? 60 : 120, height: 20))
        wantsLayer = true
        layer?.cornerRadius = 4

        nameLabel.stringValue = name
        nameLabel.font = NSFont.systemFont(ofSize: 10)
        nameLabel.isEditable = false
        nameLabel.isBordered = false
        nameLabel.backgroundColor = .clear
        nameLabel.sizeToFit()
        nameLabel.frame.origin = CGPoint(x: 6, y: 2)
        addSubview(nameLabel)

        if !isNew {
            deleteLabel.font = NSFont.systemFont(ofSize: 7)
            deleteLabel.isEditable = false
            deleteLabel.isBordered = false
            deleteLabel.backgroundColor = .clear
            deleteLabel.sizeToFit()
            deleteLabel.frame.origin = CGPoint(x: bounds.width - deleteLabel.frame.width - 4, y: 3)
            addSubview(deleteLabel)

            let deleteGesture = NSClickGestureRecognizer(target: self, action: #selector(handleDelete))
            deleteLabel.addGestureRecognizer(deleteGesture)
        }

        let tapGesture = NSClickGestureRecognizer(target: self, action: #selector(handleTap))
        addGestureRecognizer(tapGesture)

        if isNew {
            layer?.borderWidth = 1
        } else {
            layer?.borderWidth = 1
        }

        applyTheme(ThemeColors.current)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) not implemented") }

    @objc private func handleTap() {
        onToggle?(index)
    }

    @objc private func handleDelete() {
        onDelete?(index)
    }

    func applyTheme(_ tc: ThemeColors) {
        if isNew {
            layer?.borderColor = tc.txtMute.cgColor
            layer?.backgroundColor = NSColor.clear.cgColor
            nameLabel.textColor = tc.txtDim
        } else if isSelected {
            layer?.backgroundColor = tc.ptAct.cgColor
            layer?.borderColor = tc.ntAct.cgColor
            nameLabel.textColor = tc.ntAct
            deleteLabel.textColor = tc.txtDim
        } else {
            layer?.backgroundColor = NSColor(white: 0.03, alpha: 1).cgColor
            layer?.borderColor = tc.ntBrd.cgColor
            nameLabel.textColor = tc.txtSec
            deleteLabel.textColor = tc.txtDim
        }
    }
}

// MARK: - Gender Button

private final class GenderButton: NSView {
    private let label = NSTextField(labelWithString: "")
    private var isActive: Bool = false

    init(label text: String) {
        super.init(frame: .zero)
        wantsLayer = true
        layer?.cornerRadius = 2

        label.stringValue = text
        label.font = NSFont.systemFont(ofSize: 10)
        label.isEditable = false
        label.isBordered = false
        label.backgroundColor = .clear
        label.textColor = ThemeColors.current.txtDim
        label.alignment = .center
        addSubview(label)

        let tap = NSClickGestureRecognizer(target: self, action: #selector(handleTap))
        addGestureRecognizer(tap)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) not implemented") }

    @objc private func handleTap() { }

    func applyTheme(_ tc: ThemeColors, isActive: Bool) {
        self.isActive = isActive
        if isActive {
            layer?.backgroundColor = tc.ptAct.cgColor
            label.textColor = tc.ntAct
        } else {
            layer?.backgroundColor = NSColor(white: 0.03, alpha: 1).cgColor
            label.textColor = tc.txtDim
        }
    }
}

// MARK: - Equipment Row

private final class EquipmentRowView: NSView {
    private let emojiLabel = NSTextField(labelWithString: "")
    private let nameLabel = NSTextField(labelWithString: "")
    private let idLabel = NSTextField(labelWithString: "")
    private let browseLabel = NSTextField(labelWithString: "🔍 浏览")

    init(emoji: String, name: String, id: String) {
        super.init(frame: .zero)
        wantsLayer = true
        layer?.cornerRadius = 3

        emojiLabel.stringValue = emoji
        emojiLabel.font = NSFont.systemFont(ofSize: 12)
        emojiLabel.isEditable = false
        emojiLabel.isBordered = false
        emojiLabel.backgroundColor = .clear
        emojiLabel.frame = CGRect(x: 2, y: 2, width: 18, height: 18)
        addSubview(emojiLabel)

        nameLabel.stringValue = name
        nameLabel.font = NSFont.systemFont(ofSize: 10)
        nameLabel.isEditable = false
        nameLabel.isBordered = false
        nameLabel.backgroundColor = .clear
        nameLabel.textColor = ThemeColors.current.txtSec
        nameLabel.frame = CGRect(x: 22, y: 2, width: 80, height: 18)
        addSubview(nameLabel)

        idLabel.stringValue = id
        idLabel.font = NSFont.monospacedDigitSystemFont(ofSize: 8, weight: .regular)
        idLabel.isEditable = false
        idLabel.isBordered = false
        idLabel.backgroundColor = .clear
        idLabel.textColor = ThemeColors.current.txtMute
        idLabel.frame = CGRect(x: 104, y: 2, width: 60, height: 18)
        addSubview(idLabel)

        browseLabel.font = NSFont.systemFont(ofSize: 10)
        browseLabel.isEditable = false
        browseLabel.isBordered = false
        browseLabel.backgroundColor = .clear
        browseLabel.sizeToFit()
        browseLabel.frame.origin = CGPoint(x: 170, y: 2)
        addSubview(browseLabel)

        // Tracking area for hover
        let trackingArea = NSTrackingArea(rect: bounds, options: [.mouseEnteredAndExited, .activeInActiveApp], owner: self, userInfo: nil)
        addTrackingArea(trackingArea)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) not implemented") }

    override func mouseEntered(with event: NSEvent) {
        layer?.backgroundColor = ThemeColors.current.ptHov.cgColor
    }

    override func mouseExited(with event: NSEvent) {
        layer?.backgroundColor = NSColor.clear.cgColor
    }

    func applyTheme(_ tc: ThemeColors) {
        nameLabel.textColor = tc.txtSec
        idLabel.textColor = tc.txtMute
        browseLabel.textColor = tc.ntAct
    }
}

// MARK: - Paperdoll Figure View

private final class PaperdollFigureView: NSView {
    private let bodyLayer = CALayer()
    private let headLayer = CALayer()
    private let hairLayer = CALayer()
    private let capeLayer = CALayer()
    private let weaponLayer = CALayer()

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true

        // Body: 40x60 rounded rect
        bodyLayer.frame = CGRect(x: 40, y: 20, width: 40, height: 60)
        bodyLayer.cornerRadius = 8
        bodyLayer.maskedCorners = [.layerMinXMinYCorner, .layerMaxXMinYCorner, .layerMinXMaxYCorner]
        bodyLayer.masksToBounds = true
        layer?.addSublayer(bodyLayer)

        // Head: 32x32 circle
        headLayer.frame = CGRect(x: 44, y: 78, width: 32, height: 32)
        headLayer.cornerRadius = 16
        headLayer.masksToBounds = true
        layer?.addSublayer(headLayer)

        // Hair: 60x8 rounded rect
        hairLayer.frame = CGRect(x: 30, y: 96, width: 60, height: 8)
        hairLayer.cornerRadius = 4
        hairLayer.masksToBounds = true
        layer?.addSublayer(hairLayer)

        // Cape: small accent shapes
        capeLayer.frame = CGRect(x: 52, y: 20, width: 16, height: 24)
        capeLayer.cornerRadius = 4
        capeLayer.maskedCorners = [.layerMinXMinYCorner, .layerMaxXMinYCorner]
        capeLayer.masksToBounds = true
        layer?.addSublayer(capeLayer)

        // Weapon: small rotated rect
        weaponLayer.frame = CGRect(x: 60, y: 22, width: 10, height: 30)
        weaponLayer.cornerRadius = 3
        weaponLayer.masksToBounds = true
        weaponLayer.transform = CATransform3DMakeRotation(-0.25, 0, 0, 1)
        layer?.addSublayer(weaponLayer)

        applyTheme(ThemeColors.current)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) not implemented") }

    func applyTheme(_ tc: ThemeColors) {
        // Skin-tone gradient for body
        bodyLayer.backgroundColor = nil
        let bodyGradient = CAGradientLayer()
        bodyGradient.frame = bodyLayer.bounds
        bodyGradient.colors = [
            NSColor(red: 1.0, green: 0.78, blue: 0.59, alpha: 0.8).cgColor,
            NSColor(red: 1.0, green: 0.71, blue: 0.51, alpha: 0.8).cgColor,
        ]
        bodyLayer.addSublayer(bodyGradient)

        // Head
        headLayer.backgroundColor = nil
        let headGradient = CAGradientLayer()
        headGradient.frame = headLayer.bounds
        headGradient.colors = [
            NSColor(red: 1.0, green: 0.82, blue: 0.63, alpha: 0.9).cgColor,
            NSColor(red: 1.0, green: 0.75, blue: 0.55, alpha: 0.9).cgColor,
        ]
        headLayer.addSublayer(headGradient)

        // Hair: brown gradient
        hairLayer.backgroundColor = nil
        let hairGradient = CAGradientLayer()
        hairGradient.frame = hairLayer.bounds
        hairGradient.colors = [
            NSColor(red: 0.24, green: 0.16, blue: 0.12, alpha: 0.6).cgColor,
            NSColor(red: 0.39, green: 0.27, blue: 0.20, alpha: 0.8).cgColor,
            NSColor(red: 0.24, green: 0.16, blue: 0.12, alpha: 0.6).cgColor,
        ]
        hairGradient.startPoint = CGPoint(x: 0, y: 0.5)
        hairGradient.endPoint = CGPoint(x: 1, y: 0.5)
        hairLayer.addSublayer(hairGradient)

        // Cape accent
        capeLayer.backgroundColor = NSColor(red: 1.0, green: 0.78, blue: 0.59, alpha: 0.8).cgColor

        // Weapon
        weaponLayer.backgroundColor = NSColor(red: 0.71, green: 0.63, blue: 0.55, alpha: 0.6).cgColor
    }
}

// MARK: - Background Swatch View

private final class BackgroundSwatchView: NSView {
    let key: String
    private let label = NSTextField(labelWithString: "")
    private let swatchLayer = CALayer()
    var isSelected: Bool = false {
        didSet {
            swatchLayer.borderWidth = isSelected ? 1 : 1
            swatchLayer.borderColor = isSelected
                ? ThemeColors.current.ntAct.cgColor
                : ThemeColors.current.ntBrd.cgColor
            if isSelected {
                swatchLayer.shadowColor = ThemeColors.current.ntAct.withAlphaComponent(0.2).cgColor
                swatchLayer.shadowRadius = 1
                swatchLayer.shadowOpacity = 1
            } else {
                swatchLayer.shadowOpacity = 0
            }
        }
    }

    init(key: String, label text: String, colors: [NSColor]) {
        self.key = key
        super.init(frame: CGRect(x: 0, y: 0, width: 40, height: 40))
        wantsLayer = true

        swatchLayer.frame = CGRect(x: 0, y: 14, width: 40, height: 26)
        swatchLayer.cornerRadius = 3
        swatchLayer.masksToBounds = true
        swatchLayer.borderWidth = 1
        swatchLayer.borderColor = ThemeColors.current.ntBrd.cgColor

        if colors.count == 1 {
            swatchLayer.backgroundColor = colors[0].cgColor
        } else if colors.count >= 2 {
            let gradient = CAGradientLayer()
            gradient.frame = swatchLayer.bounds
            gradient.colors = colors.map(\.cgColor)
            swatchLayer.addSublayer(gradient)
        }
        layer?.addSublayer(swatchLayer)

        label.stringValue = text
        label.font = NSFont.systemFont(ofSize: 7)
        label.isEditable = false
        label.isBordered = false
        label.backgroundColor = .clear
        label.textColor = ThemeColors.current.txtDim
        label.alignment = .center
        label.frame = CGRect(x: 0, y: 0, width: 40, height: 12)
        addSubview(label)

        let tap = NSClickGestureRecognizer(target: self, action: #selector(handleTap))
        addGestureRecognizer(tap)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) not implemented") }

    @objc private func handleTap() {
        // Deselect all, select self
        if let superview = superview {
            for case let swatch as BackgroundSwatchView in superview.subviews {
                swatch.isSelected = false
            }
        }
        isSelected = true
    }

    func applyTheme(_ tc: ThemeColors) {
        label.textColor = tc.txtDim
        if !isSelected {
            swatchLayer.borderColor = tc.ntBrd.cgColor
        }
    }
}

// MARK: - Flipped View

/// NSView subclass that uses flipped coordinates (y=0 at top) for manual layout
private class FlippedView: NSView {
    override var isFlipped: Bool { true }
}