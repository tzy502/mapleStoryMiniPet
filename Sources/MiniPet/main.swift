import AppKit

// MARK: - App Entry

runCLIAdminCommands()

let app = NSApplication.shared
app.setActivationPolicy(.accessory)

let initialMobId = cli.mobId ?? "8880150"

// Build view hierarchy
let petW: CGFloat = 312, petH: CGFloat = 355, termH: CGFloat = 320

let pv = PetView(frame: NSRect(x: 0, y: 0, width: petW, height: petH))
pv.wantsLayer = true
pv.layer?.contentsGravity = .topLeft
pv.layer?.addSublayer(pv.debugDot)
pv.layer?.borderWidth = 1
pv.layer?.borderColor = NSColor.magenta.cgColor

let tv = TerminalView(frame: NSRect(x: 0, y: 0, width: petW, height: termH))
pv.terminalView = tv
tv.isHidden = true

let statusBar = StatusBarController(petView: pv)
pv.statusBar = statusBar

let container = ContainerView(petView: pv, terminalView: tv)
pv.balloonView = container.balloonView
pv.loadBalloonTiles()

let win = PetPanel(contentRect: NSRect(x: 100, y: 100, width: petW, height: petH),
                   styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
win.isFloatingPanel = true
win.isMovableByWindowBackground = true
win.isOpaque = false
win.backgroundColor = .clear
win.hasShadow = false
win.level = .statusBar
win.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
win.contentView = container

pv.loadInitial(mobId: initialMobId)
win.makeKeyAndOrderFront(nil)

let hermes = HermesClient()
hermes.terminalView = tv
tv.hermesClient = hermes

DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) { tv.focus() }

let delegate = AppDelegate()
delegate.hermes = hermes
delegate.petView = pv
app.delegate = delegate

app.run()
