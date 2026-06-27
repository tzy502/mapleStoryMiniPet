import AppKit
import Darwin

let sprites = "/Users/a502/IdeaProjects/mapleStoryMiniPet/sprites"
let anims: [(String, Int, Int)] = [("stand",16,312),("move",16,312),("attack1",16,679),("skill1",32,887)]
let sessionPath = (NSHomeDirectory() as NSString).appendingPathComponent(".pet/pet_session.jsonl")

// ── ANSI Escape Code Stripper ────────────────────────────────────────
func stripANSI(_ s: String) -> String {
    var result = ""
    result.reserveCapacity(s.utf8.count)
    var i = s.startIndex
    while i < s.endIndex {
        let c = s[i]
        if c == "\u{1B}" {
            i = s.index(after: i)
            guard i < s.endIndex else { break }
            let next = s[i]
            if next == "[" {
                // CSI: ESC [ params letter
                i = s.index(after: i)
                while i < s.endIndex {
                    let ch = s[i]; i = s.index(after: i)
                    if (ch >= "a" && ch <= "z") || (ch >= "A" && ch <= "Z") || ch == "~" { break }
                }
            } else if next == "]" {
                // OSC: ESC ] ... BEL or ESC \
                i = s.index(after: i)
                while i < s.endIndex {
                    let ch = s[i]; i = s.index(after: i)
                    if ch == "\u{07}" || ch == "\u{1B}" { break }
                }
            } else {
                i = s.index(after: i)
            }
        } else if c == "\r" {
            // Drop CR; keep LF for newlines
            i = s.index(after: i)
        } else {
            result.append(c)
            i = s.index(after: i)
        }
    }
    return result
}

// ── Panel ──────────────────────────────────────────────────────────
class PetPanel: NSPanel {
    override var canBecomeKey: Bool { true }
}

// ── PTY Manager ─────────────────────────────────────────────────────
class PTYManager {
    var masterFD: Int32 = -1
    var process: Process?
    weak var terminalView: TerminalView?
    private var source: DispatchSourceRead?

    func spawn(command: String, args: [String], env: [String: String] = [:]) -> Bool {
        // 1. Create PTY master/slave pair
        masterFD = posix_openpt(O_RDWR)
        guard masterFD >= 0 else {
            terminalView?.appendOutput("posix_openpt failed: \(String(cString: strerror(errno)))\n")
            return false
        }
        guard grantpt(masterFD) == 0 else {
            terminalView?.appendOutput("grantpt failed: \(String(cString: strerror(errno)))\n")
            close(masterFD); masterFD = -1; return false
        }
        guard unlockpt(masterFD) == 0 else {
            terminalView?.appendOutput("unlockpt failed: \(String(cString: strerror(errno)))\n")
            close(masterFD); masterFD = -1; return false
        }
        guard let slaveName = ptsname(masterFD) else {
            terminalView?.appendOutput("ptsname failed\n")
            close(masterFD); masterFD = -1; return false
        }
        let slavePath = String(cString: slaveName)

        // 2. Open slave PTY — this fd will be dup2'd into the child as stdin/stdout/stderr
        let slaveFD = open(slavePath, O_RDWR)
        guard slaveFD >= 0 else {
            terminalView?.appendOutput("open slave PTY failed: \(String(cString: strerror(errno)))\n")
            close(masterFD); masterFD = -1; return false
        }

        // 3. Build Process with slave PTY as standard I/O BEFORE launching
        let proc = Process()
        proc.executableURL = URL(fileURLWithPath: command)
        proc.arguments = args

        // closeOnDealloc: false — we manually close slaveFD after run() succeeds
        let slaveHandle = FileHandle(fileDescriptor: slaveFD, closeOnDealloc: false)
        proc.standardInput = slaveHandle
        proc.standardOutput = slaveHandle
        proc.standardError = slaveHandle

        // Merge environment
        var procEnv = ProcessInfo.processInfo.environment
        procEnv["TERM"] = "xterm-256color"
        for (k, v) in env { procEnv[k] = v }
        proc.environment = procEnv

        proc.terminationHandler = { [weak self] p in
            DispatchQueue.main.async {
                self?.terminalView?.appendOutput("\n--- 进程已退出 (code: \(p.terminationStatus)) ---\n")
                self?.source?.cancel()
            }
        }

        do {
            try proc.run()
            process = proc
        } catch {
            terminalView?.appendOutput("进程启动失败: \(error.localizedDescription)\n")
            close(slaveFD)
            close(masterFD); masterFD = -1
            return false
        }

        // Parent no longer needs the slave fd — child has its own copy after fork
        close(slaveFD)

        // 4. Set PTY window size on master
        var ws = winsize(ws_row: 24, ws_col: 80, ws_xpixel: 0, ws_ypixel: 0)
        _ = ioctl(masterFD, TIOCSWINSZ, &ws)

        // 5. Read output from master fd via DispatchSource.read → display in NSTextView
        let flags = fcntl(masterFD, F_GETFL)
        _ = fcntl(masterFD, F_SETFL, flags | O_NONBLOCK)

        source = DispatchSource.makeReadSource(fileDescriptor: masterFD, queue: .main)
        source?.setEventHandler { [weak self] in
            guard let self = self else { return }
            var buf = [UInt8](repeating: 0, count: 4096)
            let n = read(self.masterFD, &buf, 4096)
            if n > 0 {
                let data = Data(bytes: buf, count: n)
                if let text = String(data: data, encoding: .utf8) {
                    self.terminalView?.appendOutput(text)
                }
            } else if n == 0 {
                self.terminalView?.appendOutput("\n--- 进程已退出 ---\n")
                self.source?.cancel()
            } else if errno != EAGAIN {
                self.source?.cancel()
            }
        }
        source?.setCancelHandler { [weak self] in
            if let fd = self?.masterFD, fd >= 0 { close(fd) }
            self?.masterFD = -1
        }
        source?.resume()
        return true
    }

    // 6. Write user input from NSTextField to master fd
    func write(_ text: String) {
        guard masterFD >= 0, let data = text.data(using: .utf8) else { return }
        data.withUnsafeBytes { (buf: UnsafeRawBufferPointer) in
            _ = Darwin.write(masterFD, buf.baseAddress, buf.count)
        }
    }

    func resize(rows: UInt16, cols: UInt16) {
        guard masterFD >= 0 else { return }
        var ws = winsize(ws_row: rows, ws_col: cols, ws_xpixel: 0, ws_ypixel: 0)
        _ = ioctl(masterFD, TIOCSWINSZ, &ws)
    }

    func terminate() {
        source?.cancel(); source = nil
        if let p = process, p.isRunning {
            p.terminate()
        }
        process = nil
        if masterFD >= 0 { close(masterFD); masterFD = -1 }
    }
}

// ── Terminal View ────────────────────────────────────────────────────
class TerminalView: NSView {
    var ptyManager: PTYManager?
    let outputView: NSTextView
    let inputField: NSTextField
    let scrollView: NSScrollView
    private let inputHeight: CGFloat = 26

    override init(frame: NSRect) {
        // ScrollView wrapping the output NSTextView
        scrollView = NSScrollView(frame: .zero)
        scrollView.hasVerticalScroller = true
        scrollView.autohidesScrollers = true
        scrollView.drawsBackground = true
        scrollView.backgroundColor = NSColor.black
        scrollView.borderType = .noBorder

        // Output text view — readonly, green on black
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

        // Input field — green on black
        inputField = NSTextField(frame: .zero)
        inputField.font = NSFont.monospacedSystemFont(ofSize: 12, weight: .regular)
        inputField.textColor = NSColor(red: 0, green: 1, blue: 0, alpha: 1)
        inputField.backgroundColor = NSColor(red: 0.1, green: 0.1, blue: 0.1, alpha: 1)
        inputField.isBordered = false
        inputField.focusRingType = .none
        inputField.drawsBackground = true
        inputField.placeholderString = "输入消息…"

        super.init(frame: frame)

        wantsLayer = true
        layer?.backgroundColor = NSColor.black.cgColor

        addSubview(scrollView)
        addSubview(inputField)

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
        updatePTYSize()
    }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        if window != nil { updatePTYSize() }
    }

    private func updatePTYSize() {
        guard let font = outputView.font else { return }
        // Approximate cell size from font metrics
        let cellW = font.maximumAdvancement.width
        let cellH = font.boundingRectForFont.height + font.leading + 2
        guard cellW > 0, cellH > 0 else { return }
        let termW = scrollView.bounds.width - 8  // padding
        let termH = scrollView.bounds.height - 8
        let cols = UInt16(max(1, Int(termW / cellW)))
        let rows = UInt16(max(1, Int(termH / cellH)))
        ptyManager?.resize(rows: rows, cols: cols)
    }

    @objc func sendInput() {
        let text = inputField.stringValue
        guard !text.isEmpty else { return }
        inputField.stringValue = ""
        ptyManager?.write(text + "\r")
    }

    func appendOutput(_ raw: String) {
        let clean = stripANSI(raw)
        guard !clean.isEmpty else { return }
        let attr = NSAttributedString(string: clean, attributes: [
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

// ── Container View ───────────────────────────────────────────────────
class ContainerView: NSView {
    let petView: PetView
    let terminalView: TerminalView

    init(petView: PetView, terminalView: TerminalView) {
        self.petView = petView
        self.terminalView = terminalView
        let petSize = petView.frame.size
        let termSize = terminalView.frame.size
        super.init(frame: NSRect(x: 0, y: 0, width: petSize.width, height: petSize.height + termSize.height))
        addSubview(petView)
        addSubview(terminalView)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) not implemented") }

    override func layout() {
        super.layout()
        let petH = petView.frame.height
        let termH = terminalView.bounds.height
        petView.frame.origin = NSPoint(x: 0, y: bounds.height - petH)
        terminalView.frame = NSRect(x: 0, y: 0, width: bounds.width, height: termH)
    }

    func petDidResize() {
        let petSize = petView.frame.size
        let termH = terminalView.isHidden ? 0 : terminalView.bounds.height
        let newSize = NSSize(width: petSize.width, height: petSize.height + termH)
        frame.size = newSize
        window?.setContentSize(newSize)
        needsLayout = true
    }
}

// ── Pet View ───────────────────────────────────────────────────────
class PetView: NSView {
    var images: [String:[CGImage]] = [:]
    var cur = "stand"
    var fi = 0
    var lastSessionSize: Int = 0
    weak var terminalView: TerminalView?

    func load() {
        for (name, count, fw) in anims {
            guard let img = NSImage(contentsOfFile: "\(sprites)/\(name).png"),
                  let cg = img.cgImage(forProposedRect: nil, context: nil, hints: nil) else { continue }
            var frames: [CGImage] = []
            for i in 0..<count {
                if let crop = cg.cropping(to: CGRect(x: i*fw, y: 0, width: fw, height: cg.height)) { frames.append(crop) }
            }
            images[name] = frames
        }
        Timer.scheduledTimer(withTimeInterval: 0.24, repeats: true) { [weak self] _ in self?.tick() }
        // Hermes sensing: poll session file every 3 s → switch to 'move' when active
        Timer.scheduledTimer(withTimeInterval: 3.0, repeats: true) { [weak self] _ in self?.senseHermes() }
        tick()
    }

    func tick() {
        guard let f = images[cur], !f.isEmpty else { return }
        fi = (fi + 1) % f.count
        layer?.contents = f[fi]
    }

    func switchTo(_ n: String) {
        if images[n] != nil { cur = n; fi = 0; resize() }
    }

    func resize() {
        guard let f = images[cur], !f.isEmpty else { return }
        let fw = CGFloat(f[0].width); let fh = CGFloat(f[0].height)
        frame.size = NSSize(width: fw, height: fh)
        // Propagate to container so window grows/shrinks
        (superview as? ContainerView)?.petDidResize()
    }

    // ── Hermes sensing ──────────────────────────────────────────
    func senseHermes() {
        guard let data = try? Data(contentsOf: URL(fileURLWithPath: sessionPath)),
              let text = String(data: data, encoding: .utf8) else { return }
        let lines = text.split(separator: "\n", omittingEmptySubsequences: true)
        let size = lines.count
        defer { lastSessionSize = size }
        // First poll: just record baseline
        if lastSessionSize == 0 { return }
        // New activity detected → switch to move for 5 s
        if size > lastSessionSize {
            switchTo("move")
            DispatchQueue.main.asyncAfter(deadline: .now() + 5) { [weak self] in
                if self?.cur == "move" { self?.switchTo("stand") }
            }
        }
    }

    // ── Drag & menu ─────────────────────────────────────────────
    override func mouseDown(with e: NSEvent) { window?.performDrag(with: e) }

    override func rightMouseDown(with e: NSEvent) {
        let m = NSMenu()
        let animNames = ["stand", "move", "attack1", "skill1"]
        for name in animNames {
            let item = NSMenuItem(title: name, action: #selector(mSwitch(_:)), keyEquivalent: "")
            m.addItem(item)
        }
        m.addItem(.separator())
        let terminalOpen = !(terminalView?.isHidden ?? true)
        let toggleTitle = terminalOpen ? "关闭终端" : "💬 内嵌终端"
        m.addItem(NSMenuItem(title: toggleTitle, action: #selector(toggleTerminal), keyEquivalent: "t"))
        m.addItem(NSMenuItem(title: "✕ 关闭", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q"))
        NSMenu.popUpContextMenu(m, with: e, for: self)
    }

    @objc func mSwitch(_ s: NSMenuItem) { switchTo(s.title) }

    @objc func toggleTerminal() {
        guard let tv = terminalView else { return }
        tv.isHidden = !tv.isHidden
        (superview as? ContainerView)?.petDidResize()
        if !tv.isHidden {
            tv.focus()
        }
    }
}

// ── App ─────────────────────────────────────────────────────────
let app = NSApplication.shared
app.setActivationPolicy(.accessory)

// ── Build view hierarchy ────────────────────────────────────────
let petW: CGFloat = 312, petH: CGFloat = 355  // stand sprite size (sprites_v2)
let termH: CGFloat = 320

let pv = PetView(frame: NSRect(x: 0, y: 0, width: petW, height: petH))
pv.wantsLayer = true
pv.layer?.contentsGravity = .topLeft

let tv = TerminalView(frame: NSRect(x: 0, y: 0, width: petW, height: termH))
pv.terminalView = tv
tv.isHidden = true

let container = ContainerView(petView: pv, terminalView: tv)

let win = PetPanel(contentRect: NSRect(x: 100, y: 100, width: petW, height: petH),
                   styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
win.isFloatingPanel = true; win.isOpaque = false; win.backgroundColor = .clear; win.hasShadow = false
win.level = .floating; win.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
win.contentView = container

pv.load()
win.makeKeyAndOrderFront(nil)
win.center()

// ── Spawn hermes in embedded PTY ────────────────────────────────
let pty = PTYManager()
pty.terminalView = tv
tv.ptyManager = pty

let hermesPath = "/Users/a502/.hermes/hermes-agent/venv/bin/hermes"
if pty.spawn(command: hermesPath, args: ["--continue", "桌宠"]) {
    tv.appendOutput("hermes 已启动…\n")
} else {
    tv.appendOutput("无法启动 hermes\n")
}

// Give PTY a moment, then focus input
DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) {
    tv.focus()
}

// ── Cleanup on quit ────────────────────────────────────────────
class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationWillTerminate(_ notification: Notification) {
        pty.terminate()
    }
}
let delegate = AppDelegate()
app.delegate = delegate

app.run()
