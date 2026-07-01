import AppKit

// MARK: - Input Dialog

class InputDialog {
    static func ask(message: String, info: String, defaultValue: String = "",
                    onDone: @escaping (String?) -> Void) {
        NSApp.activate(ignoringOtherApps: true)
        if NSApp.mainMenu == nil {
            let mainMenu = NSMenu()
            let editMenu = NSMenu(title: "Edit")
            editMenu.addItem(NSMenuItem(title: "Cut", action: #selector(NSText.cut(_:)), keyEquivalent: "x"))
            editMenu.addItem(NSMenuItem(title: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c"))
            editMenu.addItem(NSMenuItem(title: "Paste", action: #selector(NSText.paste(_:)), keyEquivalent: "v"))
            editMenu.addItem(NSMenuItem(title: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a"))
            let editItem = NSMenuItem(title: "Edit", action: nil, keyEquivalent: "")
            editItem.submenu = editMenu
            mainMenu.addItem(editItem)
            NSApp.mainMenu = mainMenu
        }

        let alert = NSAlert()
        alert.messageText = message
        alert.informativeText = info

        let scrollView = NSScrollView(frame: NSRect(x: 0, y: 0, width: 400, height: 80))
        scrollView.hasVerticalScroller = true
        scrollView.borderType = .bezelBorder
        let textView = NSTextView(frame: scrollView.bounds)
        textView.font = NSFont.monospacedSystemFont(ofSize: 13, weight: .regular)
        textView.string = defaultValue
        textView.isRichText = false
        textView.isEditable = true
        textView.isSelectable = true
        textView.textContainer?.containerSize = NSSize(width: 380, height: CGFloat.greatestFiniteMagnitude)
        textView.textContainer?.widthTracksTextView = true
        scrollView.documentView = textView
        alert.accessoryView = scrollView

        alert.addButton(withTitle: "确定")
        alert.addButton(withTitle: "取消")
        alert.window.initialFirstResponder = textView
        alert.window.setFrame(NSRect(x: 0, y: 0, width: 480, height: 220), display: false)

        let r = alert.runModal()
        if r == .alertFirstButtonReturn {
            let raw = textView.string
            let codes = raw.components(separatedBy: CharacterSet(charactersIn: ",\n;"))
                .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
                .filter { !$0.isEmpty }
                .prefix(10)
            onDone(codes.isEmpty ? nil : codes.joined(separator: ","))
        } else {
            onDone(nil)
        }
    }
}
