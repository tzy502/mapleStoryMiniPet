import AppKit

// MARK: - Hermes Client (per-message, matches QQ/Feishu bot behavior)

class HermesClient {
    weak var terminalView: TerminalView?
    private var pendingProcess: Process?

    func send(_ text: String, silent: Bool = false, isGreeting: Bool = false) {
        pendingProcess?.terminate()

        let proc = Process()
        proc.executableURL = URL(fileURLWithPath: "/Users/a502/.hermes/hermes-agent/venv/bin/hermes")
        proc.arguments = ["-z", text, "--cli", "--resume", "20260626_200327_096c1d"]

        let outPipe = Pipe()
        proc.standardOutput = outPipe
        proc.standardError = FileHandle.nullDevice

        // 显示 thinking 指示器
        if !silent { DispatchQueue.main.async { self.terminalView?.thinkingLabel.isHidden = false } }

        if !silent {
            DispatchQueue.main.async {
                let userAttr = NSAttributedString(string: "> " + text + "\n", attributes: [
                    .foregroundColor: NSColor(red: 0.5, green: 0.5, blue: 0.5, alpha: 1),
                    .font: NSFont.monospacedSystemFont(ofSize: 12, weight: .regular),
                ])
                self.terminalView?.outputView.textStorage?.append(userAttr)
                self.terminalView?.outputView.scrollToEndOfDocument(nil)
            }
        }

        proc.terminationHandler = { [weak self] p in
            let data = outPipe.fileHandleForReading.readDataToEndOfFile()
            let output = String(data: data, encoding: .utf8) ?? ""
            DispatchQueue.main.async {
                guard !output.isEmpty else { return }
                if isGreeting {
                    self?.terminalView?.replacePlaceholder(with: output.trimmingCharacters(in: .whitespacesAndNewlines))
                } else {
                    self?.terminalView?.thinkingLabel.isHidden = true
                    let attr = NSAttributedString(string: output + "\n", attributes: [
                        .foregroundColor: NSColor(red: 0, green: 1, blue: 0, alpha: 1),
                        .font: NSFont.monospacedSystemFont(ofSize: 12, weight: .regular),
                    ])
                    self?.terminalView?.outputView.textStorage?.append(attr)
                    self?.terminalView?.outputView.scrollToEndOfDocument(nil)
                }
            }
        }

        do {
            try proc.run()
            pendingProcess = proc
        } catch {
            terminalView?.appendOutput("hermes 启动失败: \(error.localizedDescription)\n")
        }
    }

    func terminate() {
        pendingProcess?.terminate()
        pendingProcess = nil
    }
}
