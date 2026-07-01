import AppKit

// MARK: - Terminal View

class TerminalView: NSView {
    var hermesClient: HermesClient?
    let outputView: NSTextView
    let inputField: NSTextField
    let scrollView: NSScrollView
    private let inputHeight: CGFloat = 26
    let thinkingLabel = NSTextField(labelWithString: "thinking...")
    private var placeholderRange: NSRange?  // "AI启动中..." 的位置，回复后移除
    
    override init(frame: NSRect) {
        scrollView = NSScrollView(frame: .zero)
        scrollView.hasVerticalScroller = true
        scrollView.autohidesScrollers = true
        scrollView.drawsBackground = true
        scrollView.backgroundColor = NSColor.black
        scrollView.borderType = .noBorder

        outputView = NSTextView(frame: .zero)
        outputView.isEditable = false
        outputView.isSelectable = true
        outputView.font = NSFont.monospacedSystemFont(ofSize: 12, weight: .regular)
        outputView.textColor = NSColor(red: 0, green: 1, blue: 0, alpha: 1)
        outputView.backgroundColor = NSColor.black
        outputView.insertionPointColor = NSColor.green
        outputView.linkTextAttributes = [:]
        outputView.isAutomaticQuoteSubstitutionEnabled = false
        outputView.isAutomaticDashSubstitutionEnabled = false
        outputView.isAutomaticSpellingCorrectionEnabled = false
        outputView.textContainerInset = NSSize(width: 4, height: 4)
        scrollView.documentView = outputView

        inputField = NSTextField(frame: .zero)
        inputField.font = NSFont.monospacedSystemFont(ofSize: 12, weight: .regular)
        inputField.textColor = NSColor(red: 0, green: 1, blue: 0, alpha: 1)
        inputField.backgroundColor = NSColor(red: 0.1, green: 0.1, blue: 0.1, alpha: 1)
        inputField.isBordered = false
        inputField.focusRingType = .none
        inputField.drawsBackground = true
        inputField.placeholderString = "输入消息…"
        
        // thinking 指示器（右下角，初始隐藏）
        thinkingLabel.font = NSFont.monospacedSystemFont(ofSize: 10, weight: .regular)
        thinkingLabel.textColor = NSColor(red: 0.4, green: 0.4, blue: 0.4, alpha: 1)
        thinkingLabel.isHidden = true
        thinkingLabel.alignment = .right

        super.init(frame: frame)
        wantsLayer = true
        layer?.backgroundColor = NSColor.black.cgColor

        addSubview(scrollView)
        addSubview(inputField)
        addSubview(thinkingLabel)

        inputField.target = self
        inputField.action = #selector(sendInput)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) not implemented") }

    override func layout() {
        super.layout()
        let w = bounds.width
        let h = bounds.height
        inputField.frame = NSRect(x: 4, y: 4, width: w - 8, height: inputHeight)
        scrollView.frame = NSRect(x: 0, y: inputHeight + 6, width: w, height: h - inputHeight - 10)
        thinkingLabel.frame = NSRect(x: w - 120, y: h - inputHeight - 24, width: 116, height: 16)
    }

    @objc func sendInput() {
        let text = inputField.stringValue
        guard !text.isEmpty else { return }
        inputField.stringValue = ""
        hermesClient?.send(text)
    }

    /// 显示占位文字（AI启动中...）和 thinking 指示器
    func showPlaceholder() {
        removePlaceholder()
        let msg = "AI启动中...\n"
        let attr = NSAttributedString(string: msg, attributes: [
            .foregroundColor: NSColor(red: 0.4, green: 0.4, blue: 0.4, alpha: 1),
            .font: NSFont.monospacedSystemFont(ofSize: 12, weight: .regular),
        ])
        let loc = outputView.textStorage?.length ?? 0
        outputView.textStorage?.append(attr)
        outputView.scrollToEndOfDocument(nil)
        placeholderRange = NSRange(location: loc, length: attr.length)
        thinkingLabel.isHidden = false
    }
    
    /// 移除占位文字和 thinking，追加正式回复
    func replacePlaceholder(with text: String) {
        removePlaceholder()
        let attr = NSAttributedString(string: text + "\n", attributes: [
            .foregroundColor: NSColor(red: 0, green: 1, blue: 0, alpha: 1),
            .font: NSFont.monospacedSystemFont(ofSize: 12, weight: .regular),
        ])
        outputView.textStorage?.append(attr)
        outputView.scrollToEndOfDocument(nil)
        thinkingLabel.isHidden = true
    }
    
    private func removePlaceholder() {
        guard let range = placeholderRange,
              let ts = outputView.textStorage,
              range.location < ts.length else { return }
        ts.deleteCharacters(in: range)
        placeholderRange = nil
    }

    func appendOutput(_ text: String) {
        let attr = NSAttributedString(string: text, attributes: [
            .foregroundColor: NSColor(red: 0, green: 1, blue: 0, alpha: 1),
            .font: NSFont.monospacedSystemFont(ofSize: 12, weight: .regular),
        ])
        outputView.textStorage?.append(attr)
        outputView.scrollToEndOfDocument(nil)
    }

    func focus() {
        window?.makeFirstResponder(inputField)
    }
}
