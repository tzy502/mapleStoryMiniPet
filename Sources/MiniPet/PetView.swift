import AppKit

// MARK: - Pet View (Animation Engine)

class PetView: NSView {
    // Sprite data
    var config: PetConfig?
    var images: [String: [CGImage]] = [:]
    var originX: Int = 0
    var originY: Int = 0

    var mobId: String = "8880150"
    var mobName: String = "路西德"

    // Animation state
    var cur = "stand"
    var fi = 0
    var isOneShot = false
    var dragActive = false
    var lastSessionSize: Int = 0

    // F9: Session-only temporary default animation (nil = use mob's persisted default -> stand)
    var defaultAnim: String?

    // Timers
    var frameTimer: Timer?
    var idleTimer: Timer?
    var randomSkillTimer: Timer?
    var hermesTimer: Timer?

    // Hermes activity tracking
    var hermesActive = false
    var hermesCooldown: DispatchWorkItem?

    // Map background
    var currentMapId: String? {
        didSet { statusBar?.refresh() }
    }
    var mapZoom: CGFloat = 1.0

    // References
    weak var terminalView: TerminalView?
    weak var statusBar: StatusBarController?
    weak var balloonView: ChatBalloonView?
    weak var hermesClient: HermesClient?

    // Panel controllers (lazily instantiated, held to keep them alive)
    private var settingsPanelController: SettingsPanelController?
    private var materialBrowserPanel: MaterialBrowserPanel?
    private var hermesPanel: HermesPanel?

    // Available mob list
    var mobList: [MobInfo] = MobStore.load() {
        didSet { MobStore.save(mobList) }
    }

    // Available NPC list
    var npcList: [MobInfo] = []

    // Current entity type: "mob" or "npc"
    var entityType: String = "mob"

    // Store strip CGImages + per-animation origin
    var strips: [String: (cg: CGImage, fw: Int, fh: Int, count: Int, ox: Int, oy: Int)] = [:]

    // Debug origin dot
    lazy var debugDot: CAShapeLayer = {
        let d = CAShapeLayer()
        d.path = CGPath(ellipseIn: CGRect(x: -3, y: -3, width: 6, height: 6), transform: nil)
        d.fillColor = NSColor.red.cgColor
        d.strokeColor = NSColor.white.cgColor
        d.lineWidth = 1
        d.isHidden = !cli.debugAPI
        return d
    }()

    // MARK: - Origin Positioning

    func originScreenPosition() -> CGPoint? {
        guard let win = window, let s = strips[cur] else { return nil }
        return CGPoint(x: win.frame.origin.x + CGFloat(s.ox),
                       y: win.frame.origin.y + (frame.height - CGFloat(s.oy)))
    }

    func restoreOriginScreenPosition(_ pos: CGPoint) {
        guard let win = window, let s = strips[cur] else { return }
        win.setFrameOrigin(NSPoint(x: pos.x - CGFloat(s.ox),
                                    y: pos.y - (frame.height - CGFloat(s.oy))))
    }

    @objc func centerOriginOnScreen() {
        guard let win = window, let screen = win.screen ?? NSScreen.main,
              let s = strips[cur] else { return }
        let originInView = CGPoint(x: CGFloat(s.ox), y: frame.height - CGFloat(s.oy))
        let screenCenter = CGPoint(x: screen.frame.midX, y: screen.frame.midY)
        win.setFrameOrigin(NSPoint(x: screenCenter.x - originInView.x,
                                    y: screenCenter.y - originInView.y))
        statusBar?.refresh()
    }

    // MARK: - Loading

    func loadInitial(mobId: String) {
        self.mobId = mobId
        logDebug("loadInitial: mob=\(mobId)")
        let preferredAnim = mobList.first(where: { $0.code == mobId })?.defaultAnim ?? ""
        Task {
            let loaded = await loadSprites(for: mobId, isInitial: true)
            if !loaded { loadBundledSprites() }
            await MainActor.run {
                let priority = ["stand", "say", "mouse", "move", "hand", "laugh", "eye", "fly", "die"]
                let anim: String
                if !preferredAnim.isEmpty, strips[preferredAnim] != nil {
                    anim = preferredAnim
                } else if let match = priority.lazy.compactMap({ p in
                    self.strips[p] != nil ? p : self.strips.keys.sorted().first(where: { $0.hasPrefix("\(p)-") })
                }).first {
                    anim = match
                } else {
                    anim = strips.keys.sorted().first ?? "stand"
                }
                self.switchTo(anim)
                self.startTimers()
                self.observeMaterialBrowserSendToPet()
                self.statusBar?.refresh()
            }
        }
    }

    // MARK: - Panel Wiring

    /// Listen for "MaterialBrowserSendToPet" notification to switch pet monster
    private func observeMaterialBrowserSendToPet() {
        NotificationCenter.default.addObserver(
            self,
            selector: #selector(handleMaterialBrowserSendToPet(_:)),
            name: Notification.Name("MaterialBrowserSendToPet"),
            object: nil
        )
    }

    @objc private func handleMaterialBrowserSendToPet(_ notification: Notification) {
        guard let userInfo = notification.userInfo,
              let mobId = userInfo["mobId"] as? String else { return }
        logDebug("MaterialBrowserSendToPet: mobId=\(mobId)")
        // Switch directly using mobId (if the mob is already in the list, reuse it)
        if mobList.contains(where: { $0.code == mobId }) {
            menuSwitchMobWithCode(mobId)
        } else {
            // If not in the list, add it first
            let onSuccess: (String) -> Void = { [weak self] code in
                self?.menuSwitchMobWithCode(code)
            }
            addMobWithCode(mobId, onSuccess: onSuccess)
        }
    }

    /// Switch to a mob by code (like menuSwitchMob but directly by code)
    private func menuSwitchMobWithCode(_ code: String) {
        guard code != mobId else { return }
        let previousMobId = mobId
        mobId = code
        statusBar?.refresh()
        Task {
            let loaded = await loadSprites(for: code)
            await MainActor.run {
                if loaded {
                    self.switchTo(self.bestDefaultAnim())
                    self.statusBar?.refresh()
                } else {
                    self.mobId = previousMobId
                    self.statusBar?.refresh()
                    logDebug("menuSwitchMobWithCode: failed to load \(code), restored \(previousMobId)")
                }
            }
        }
    }

    /// Add a mob by code and call onSuccess when done
    private func addMobWithCode(_ code: String, onSuccess: @escaping (String) -> Void) {
        DispatchQueue.main.async {
            Task {
                let api = APIClient()
                let apiName = await api.fetchMobName(mobId: code) ?? code
                let existing = self.mobList.first(where: { $0.name == apiName && $0.code != code })
                let displayName = existing != nil ? "\(apiName) (\(code))" : apiName
                _ = await api.fetchAndGenerateSprites(mobId: code, type: "mob")
                let cm = CacheManager(mobId: code, entityType: "mob")
                let priority = ["stand", "say", "mouse", "move", "hand", "laugh", "eye", "fly", "die"]
                let keys = cm.loadConfig()?.sprites.keys.map { $0 } ?? []
                let firstAnim = priority.first(where: { keys.contains($0) }) ?? keys.sorted().first ?? "stand"
                await MainActor.run {
                    if !self.mobList.contains(where: { $0.code == code }) {
                        self.mobList.append(MobInfo(code: code, name: displayName, defaultAnim: firstAnim, type: "mob"))
                        self.statusBar?.refresh()
                    }
                    onSuccess(code)
                }
            }
        }
    }

    // MARK: - Panel Menu Actions

    @objc private func openSettingsPanel() {
        if settingsPanelController == nil {
            settingsPanelController = SettingsPanelController(petView: self)
        }
        settingsPanelController?.window?.makeKeyAndOrderFront(nil)
    }

    @objc private func openMaterialBrowserPanel() {
        if materialBrowserPanel == nil {
            materialBrowserPanel = MaterialBrowserPanel(
                contentRect: NSRect(x: 0, y: 0, width: 1000, height: 640),
                styleMask: [.titled, .closable, .resizable, .miniaturizable, .nonactivatingPanel],
                backing: .buffered,
                defer: false
            )
        }
        materialBrowserPanel?.makeKeyAndOrderFront(nil)
    }

    @objc private func openHermesPanel() {
        if hermesPanel == nil {
            hermesPanel = HermesPanel(
                contentRect: NSRect(x: 0, y: 0, width: 520, height: 460),
                petView: self,
                hermesClient: hermesClient
            )
        }
        hermesPanel?.makeKeyAndOrderFront(nil)
    }

    func loadSprites(for mobId: String, isInitial: Bool = false) async -> Bool {
        let et = entityType
        let cm = CacheManager(mobId: mobId, entityType: et)

        if cm.isCached, let config = cm.loadConfig() {
            logDebug("Cache hit for \(et) \(mobId)")
            await MainActor.run { self.applyConfig(config, cacheDir: cm.dir, isInitial: isInitial) }
            return true
        }

        let api = APIClient()
        if await api.fetchAndGenerateSprites(mobId: mobId, type: et), let config = cm.loadConfig() {
            logDebug("API sprites generated for \(et) \(mobId)")
            await MainActor.run { self.applyConfig(config, cacheDir: cm.dir, isInitial: isInitial) }
            return true
        }

        if mobId == "8880150" {
            logDebug("Using bundled sprites for default mob")
            return false
        }

        logDebug("No sprites available for \(et) \(mobId)")
        return false
    }

    func applyConfig(_ config: PetConfig, cacheDir: String, isInitial: Bool = false) {
        let savedOrigin = isInitial ? nil : originScreenPosition()

        self.config = config
        self.mobName = config.name
        self.originX = config.originX ?? 0
        self.originY = config.originY ?? 0
        images.removeAll()
        strips.removeAll()

        for (animName, entry) in config.sprites {
            let filePath = "\(cacheDir)/\(entry.file)"
            guard let img = NSImage(contentsOfFile: filePath),
                  let cg = img.cgImage(forProposedRect: nil, context: nil, hints: nil) else {
                logDebug("applyConfig FAIL: \(filePath)")
                continue
            }
            strips[animName] = (cg, entry.frameWidth, entry.frameHeight, entry.frames,
                                entry.originX ?? 0, entry.originY ?? 0)
            let firstRect = CGRect(x: 0, y: 0, width: entry.frameWidth, height: entry.frameHeight)
            if let firstFrame = cg.cropping(to: firstRect) {
                images[animName] = [firstFrame]
            }
        }
        logDebug("applyConfig: \(config.name) → \(strips.count) animations")
        window?.title = "MiniPet - \(config.name)"

        if let firstKey = strips.keys.sorted().first {
            cur = firstKey
            fi = 0
            resize()
            let rect = CGRect(x: 0, y: 0, width: CGFloat(strips[firstKey]!.fw), height: CGFloat(strips[firstKey]!.fh))
            layer?.contents = strips[firstKey]!.cg.cropping(to: rect)

            if isInitial {
                centerOriginOnScreen()
            } else if let pos = savedOrigin {
                restoreOriginScreenPosition(pos)
            }
        }
        statusBar?.refresh()
    }

    func loadBundledSprites() {
        mobId = "8880150"
        let bundledDir = "\(projectDir)/sprites"
        guard let data = try? Data(contentsOf: URL(fileURLWithPath: "\(bundledDir)/pet_config.json")),
              let config = try? JSONDecoder().decode(PetConfig.self, from: data) else {
            loadHardcodedSprites()
            return
        }
        applyConfig(config, cacheDir: bundledDir, isInitial: true)
    }

    func loadHardcodedSprites() {
        let sprites = "\(projectDir)/sprites"
        let anims: [(String, Int, Int)] = [("stand", 16, 312), ("move", 16, 312), ("attack1", 16, 679), ("skill1", 32, 887)]
        images.removeAll()

        var spritesDict: [String: SpriteEntry] = [:]
        for (name, count, fw) in anims {
            guard let img = NSImage(contentsOfFile: "\(sprites)/\(name).png"),
                  let cg = img.cgImage(forProposedRect: nil, context: nil, hints: nil) else { continue }
            let cgH = cg.height
            var frames: [CGImage] = []
            for i in 0..<count {
                let rect = CGRect(x: CGFloat(i * fw), y: 0, width: CGFloat(fw), height: CGFloat(cgH))
                if let crop = cg.cropping(to: rect) { frames.append(crop) }
            }
            if !frames.isEmpty {
                images[name] = frames
                spritesDict[name] = SpriteEntry(file: "\(name).png", frames: count, frameWidth: fw, frameHeight: cgH)
            }
        }
        config = PetConfig(name: "MapleStory_Mob_8880150", version: 2, type: "sprite", sprites: spritesDict)
        mobName = "路西德"
    }

    // MARK: - Timers

    func startTimers() {
        frameTimer = Timer.scheduledTimer(withTimeInterval: 0.12, repeats: true) { [weak self] _ in self?.tick() }
        hermesTimer = Timer.scheduledTimer(withTimeInterval: 3.0, repeats: true) { [weak self] _ in self?.senseHermes() }
        scheduleRandomSkill()
    }

    func scheduleRandomSkill() {
        randomSkillTimer?.invalidate()
        let interval = TimeInterval.random(in: 15...30)
        randomSkillTimer = Timer.scheduledTimer(withTimeInterval: interval, repeats: false) { [weak self] _ in
            self?.triggerRandomSkill()
        }
    }

    func triggerRandomSkill() {
        guard !dragActive, !isOneShot else { scheduleRandomSkill(); return }
        var candidates: [String] = []
        for name in strips.keys.sorted() {
            if name.hasPrefix("attack") || name.hasPrefix("skill") { candidates.append(name) }
        }
        if let chosen = candidates.randomElement() { switchTo(chosen) }
        scheduleRandomSkill()
    }

    // MARK: - Frame Tick

    func tick() {
        guard let s = strips[cur] else { return }
        let frameCount = s.count

        if isOneShot {
            if fi < frameCount - 1 { fi += 1 }
            else { switchTo(bestDefaultAnim()); return }
        } else {
            fi = (fi + 1) % frameCount
        }
        let rect = CGRect(x: CGFloat(fi * s.fw), y: 0, width: CGFloat(s.fw), height: CGFloat(s.fh))
        layer?.contents = s.cg.cropping(to: rect)

        debugDot.position = CGPoint(x: CGFloat(s.ox), y: CGFloat(s.fh) - CGFloat(s.oy))
        if cli.debugAPI {
            let op = originScreenPosition() ?? .zero
            let msg = "\(cur)[\(fi)/\(frameCount)] canvas=\(s.fw)x\(s.fh) origin=(\(s.ox),\(s.oy)) win=(\(Int(window?.frame.origin.x ?? 0)),\(Int(window?.frame.origin.y ?? 0))) originScreen=(\(Int(op.x)),\(Int(op.y)))"
            FileHandle.standardError.write(Data(("[MiniPet] " + msg + "\n").utf8))
        }
    }

    // MARK: - Animation Switching

    func switchTo(_ name: String, animated: Bool = true) {
        guard let s = strips[name] else {
            switchTo(fallbackForMove(name), animated: animated)
            return
        }

        let savedOrigin = originScreenPosition()

        cur = name
        fi = 0
        isOneShot = (name.hasPrefix("attack") || name.hasPrefix("skill") || name.hasPrefix("die") || name.hasPrefix("hit"))
        if cli.debugAPI {
            var msg = "switchTo: \(name) canvas=\(s.fw)x\(s.fh) origin=(\(s.ox),\(s.oy)) isOneShot=\(isOneShot)"
            if let pos = savedOrigin { msg += " savedOrigin=(\(Int(pos.x)),\(Int(pos.y)))" }
            FileHandle.standardError.write(Data(("[MiniPet] " + msg + "\n").utf8))
        }

        if animated {
            resize()
            let rect = CGRect(x: 0, y: 0, width: CGFloat(s.fw), height: CGFloat(s.fh))
            layer?.contents = s.cg.cropping(to: rect)
            if let pos = savedOrigin { restoreOriginScreenPosition(pos) }
            layer?.opacity = 0.0
            CATransaction.begin()
            CATransaction.setAnimationDuration(0.12)
            layer?.opacity = 1.0
            CATransaction.commit()
        } else {
            resize()
            let rect = CGRect(x: 0, y: 0, width: CGFloat(s.fw), height: CGFloat(s.fh))
            layer?.contents = s.cg.cropping(to: rect)
            if let pos = savedOrigin { restoreOriginScreenPosition(pos) }
        }

        resetIdleTimer()
    }

    func fallbackForMove(_ name: String) -> String {
        if name.hasPrefix("move") {
            if let fly = strips.keys.first(where: { $0.hasPrefix("fly") }) { return fly }
            if let stand = strips.keys.first(where: { $0.hasPrefix("stand") }) { return stand }
            return strips.keys.first ?? "stand"
        }
        if let stand = strips.keys.first(where: { $0.hasPrefix("stand") }) { return stand }
        return strips.keys.first ?? "stand"
    }

    func resolveMoveAnimation() -> String {
        if let move = strips.keys.first(where: { $0.hasPrefix("move") }) { return move }
        if let fly = strips.keys.first(where: { $0.hasPrefix("fly") }) { return fly }
        return bestDefaultAnim()
    }

    func bestDefaultAnim() -> String {
        // F9: session-only defaultAnim takes highest priority
        if let da = defaultAnim, strips[da] != nil { return da }
        // Persisted defaultAnim per mob
        if let d = mobList.first(where: { $0.code == mobId })?.defaultAnim, strips[d] != nil { return d }
        let priority = ["stand", "fly", "say", "move", "hand", "die", "skill1", "attack1"]
        for prefix in priority {
            if strips[prefix] != nil { return prefix }
            if let match = strips.keys.sorted().first(where: { $0.hasPrefix("\(prefix)-") }) { return match }
        }
        return strips.keys.sorted().first ?? "stand"
    }

    func resize() {
        guard let s = strips[cur] else { return }
        frame.size = NSSize(width: CGFloat(s.fw), height: CGFloat(s.fh))
        (superview as? ContainerView)?.petDidResize()
    }

    // MARK: - Idle Detection

    func resetIdleTimer() {
        idleTimer?.invalidate()
        idleTimer = Timer.scheduledTimer(withTimeInterval: 5.0, repeats: false) { [weak self] _ in
            guard let self = self else { return }
            let defaultAnim = self.bestDefaultAnim()
            if !self.isOneShot && !self.dragActive && self.cur != defaultAnim {
                self.switchTo(defaultAnim)
            }
        }
    }

    // MARK: - Hermes Sensing

    func senseHermes() {
        guard let data = try? Data(contentsOf: URL(fileURLWithPath: sessionPath)),
              let text = String(data: data, encoding: .utf8) else { return }
        let lines = text.split(separator: "\n", omittingEmptySubsequences: true)
        let size = lines.count
        defer { lastSessionSize = size }

        if lastSessionSize == 0 { return }

        if size > lastSessionSize {
            hermesActive = true
            hermesCooldown?.cancel()

            if !isOneShot && !dragActive { switchTo(resolveMoveAnimation()) }

            let work = DispatchWorkItem { [weak self] in
                guard let self = self else { return }
                self.hermesActive = false
                if self.cur.hasPrefix("move") || self.cur.hasPrefix("fly") {
                    self.switchTo(self.bestDefaultAnim())
                }
            }
            hermesCooldown = work
            DispatchQueue.main.asyncAfter(deadline: .now() + 5, execute: work)
        }
    }

    // MARK: - Balloon

    var currentBalloonId: Int = 560 {
        didSet {
            if currentBalloonId != oldValue { loadBalloonTiles() }
        }
    }

    func loadBalloonTiles() {
        let bid = currentBalloonId
        if balloonView?.loadTilesFromCache(balloonId: bid) == true { return }
        // Cache miss — fetch from API async
        Task {
            let api = APIClient()
            // Fetch tiles, origins, and clr in parallel
            async let tilesResult = api.fetchBalloonTiles(balloonId: bid)
            async let originsResult = api.fetchBalloonOrigins(balloonId: bid)
            async let clrResult = api.fetchBalloonClr(balloonId: bid)

            if let tiles = await tilesResult {
                let origins = await originsResult
                var withOrigins: [String: BalloonTileInfo] = [:]
                let names = ["nw", "n", "head", "ne", "w", "c", "e", "sw", "s", "arrow", "se"]
                for name in names {
                    if let data = tiles[name] {
                        let origin = origins[name] ?? .zero
                        withOrigins[name] = BalloonTileInfo(data: data, origin: origin, url: "")
                    }
                }
                await MainActor.run {
                    self.balloonView?.loadTileDataWithInfo(withOrigins)
                    self.balloonView?.saveTilesToCache(balloonId: bid)
                }
            }
            if let clr = await clrResult {
                // MapleSalon2 getWzClrColor: 0xffffff + 1 + clr
                // Result is BGRA with alpha=0xFF (B=bits 0-7, G=bits 8-15, R=bits 16-23, A=bits 24-31)
                // For clr=-1, result = 0xffffff (white)
                let colorValue = UInt32(truncatingIfNeeded: 0xffffff + 1 + clr)
                let r = CGFloat((colorValue >> 16) & 0xFF) / 255.0
                let g = CGFloat((colorValue >> 8) & 0xFF) / 255.0
                let b = CGFloat(colorValue & 0xFF) / 255.0
                await MainActor.run {
                    self.balloonView?.textColor = NSColor(calibratedRed: r, green: g, blue: b, alpha: 1.0)
                }
            }
        }
    }

    func showBalloon(text: String) {
        guard let bv = balloonView, bv.hasTiles else { return }
        bv.show(text: text)
    }

    // MARK: - Mouse Events

    override func mouseDown(with event: NSEvent) {
        dragActive = true
        if !isOneShot { switchTo(resolveMoveAnimation(), animated: false) }
        window?.performDrag(with: event)
        dragActive = false
        if !isOneShot { switchTo(bestDefaultAnim(), animated: false) }
    }

    override func rightMouseDown(with event: NSEvent) {
        let menu = NSMenu()

        // Animation submenu
        let animItem = NSMenuItem(title: "切换动画", action: nil, keyEquivalent: "")
        let animMenu = NSMenu()
        for name in strips.keys.sorted() {
            let item = NSMenuItem(title: name, action: #selector(menuSwitchAnimation(_:)), keyEquivalent: "")
            let sub = NSMenu()
            let setDefaultItem = NSMenuItem(title: "设为默认动画", action: #selector(setDefaultAnim(_:)), keyEquivalent: "")
            setDefaultItem.representedObject = name
            if defaultAnim == name { setDefaultItem.state = .on }
            sub.addItem(setDefaultItem)
            item.submenu = sub
            animMenu.addItem(item)
        }
        if defaultAnim != nil {
            animMenu.addItem(.separator())
            let resetItem = NSMenuItem(title: "恢复默认(stand)", action: #selector(resetDefaultAnim(_:)), keyEquivalent: "")
            animMenu.addItem(resetItem)
        }
        animItem.submenu = animMenu
        menu.addItem(animItem)

        // Monster submenu
        let mobItem = NSMenuItem(title: "切换怪物", action: nil, keyEquivalent: "")
        let mobMenu = NSMenu()
        for mob in mobList {
            let item = NSMenuItem(title: "\(mob.name) (\(mob.code))",
                                  action: #selector(menuSwitchMob(_:)), keyEquivalent: "")
            item.representedObject = mob.code
            if mob.code == mobId, entityType == "mob" { item.state = .on }
            let renameItem = NSMenuItem(title: "重命名", action: #selector(renameMob(_:)), keyEquivalent: "")
            renameItem.representedObject = mob.code
            let deleteItem = NSMenuItem(title: "删除", action: #selector(deleteMob(_:)), keyEquivalent: "")
            deleteItem.representedObject = mob.code
            let sub = NSMenu(); sub.addItem(renameItem); sub.addItem(deleteItem)
            item.submenu = sub
            mobMenu.addItem(item)
        }
        mobMenu.addItem(.separator())
        mobMenu.addItem(NSMenuItem(title: "添加怪物…", action: #selector(addMob), keyEquivalent: "n"))
        mobItem.submenu = mobMenu
        menu.addItem(mobItem)

        // NPC submenu
        let npcItem = NSMenuItem(title: "切换NPC", action: nil, keyEquivalent: "")
        let npcMenu = NSMenu()
        for npc in npcList {
            let item = NSMenuItem(title: "\(npc.name) (\(npc.code))",
                                  action: #selector(menuSwitchNpc(_:)), keyEquivalent: "")
            item.representedObject = npc.code
            if npc.code == mobId, entityType == "npc" { item.state = .on }
            npcMenu.addItem(item)
        }
        npcMenu.addItem(.separator())
        npcMenu.addItem(NSMenuItem(title: "添加NPC…", action: #selector(addNpc), keyEquivalent: ""))
        npcItem.submenu = npcMenu
        menu.addItem(npcItem)

        menu.addItem(.separator())
        menu.addItem(NSMenuItem(title: "居中", action: #selector(centerOriginOnScreen), keyEquivalent: "c"))
        let terminalOpen = !(terminalView?.isHidden ?? true)
        menu.addItem(NSMenuItem(title: terminalOpen ? "关闭终端" : "内嵌终端", action: #selector(toggleTerminal), keyEquivalent: "t"))

        // Balloon submenu
        let balloonItem = NSMenuItem(title: "聊天气泡", action: nil, keyEquivalent: "")
        let balloonMenu = NSMenu()
        balloonMenu.addItem(NSMenuItem(title: "测试气泡", action: #selector(testBalloon), keyEquivalent: ""))
        balloonMenu.addItem(NSMenuItem(title: "隐藏气泡", action: #selector(hideBalloon), keyEquivalent: ""))
        balloonMenu.addItem(.separator())
        balloonMenu.addItem(NSMenuItem(title: "气球样式: \(currentBalloonId)", action: #selector(changeBalloonStyle), keyEquivalent: ""))
        balloonItem.submenu = balloonMenu
        menu.addItem(balloonItem)

        menu.addItem(.separator())
        menu.addItem(NSMenuItem(title: "设置面板", action: #selector(openSettingsPanel), keyEquivalent: ""))
        menu.addItem(NSMenuItem(title: "素材浏览器", action: #selector(openMaterialBrowserPanel), keyEquivalent: ""))
        menu.addItem(NSMenuItem(title: "Hermes AI", action: #selector(openHermesPanel), keyEquivalent: ""))

        // Background submenu
        let bgItem = NSMenuItem(title: "切换背景", action: nil, keyEquivalent: "")
        let bgMenu = NSMenu()
        let transparentItem = NSMenuItem(title: "透明背景", action: #selector(menuSwitchMap(_:)), keyEquivalent: "")
        transparentItem.representedObject = ""
        if currentMapId == nil || currentMapId?.isEmpty == true { transparentItem.state = .on }
        bgMenu.addItem(transparentItem)
        bgMenu.addItem(.separator())
        for map in classicMaps {
            let item = NSMenuItem(title: "\(map.name) (\(map.id))", action: #selector(menuSwitchMap(_:)), keyEquivalent: "")
            item.representedObject = map.id
            if currentMapId == map.id { item.state = .on }
            bgMenu.addItem(item)
        }
        bgMenu.addItem(.separator())
        // Zoom submenu
        let zoomItem = NSMenuItem(title: "缩放", action: nil, keyEquivalent: "")
        let zoomMenu = NSMenu()
        for (label, z) in [("0.5x", 0.5), ("1x", 1.0), ("2x", 2.0)] {
            let zi = NSMenuItem(title: label, action: #selector(menuSetMapZoom(_:)), keyEquivalent: "")
            zi.representedObject = z
            if abs(mapZoom - CGFloat(z)) < 0.01 { zi.state = .on }
            zoomMenu.addItem(zi)
        }
        zoomItem.submenu = zoomMenu
        bgMenu.addItem(zoomItem)
        bgItem.submenu = bgMenu
        menu.addItem(bgItem)

        menu.addItem(.separator())
        menu.addItem(NSMenuItem(title: "退出 MiniPet", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q"))

        NSMenu.popUpContextMenu(menu, with: event, for: self)
    }

    @objc func menuSwitchAnimation(_ sender: NSMenuItem) {
        switchTo(sender.title)
        statusBar?.refresh()
    }

    @objc func setDefaultAnim(_ sender: NSMenuItem) {
        guard let animName = sender.representedObject as? String else { return }

        // Prevent oneShot animations (attack*, skill*, hit, happy, alert, die) from being set as default
        let oneShotPrefixes = ["attack", "skill", "hit", "happy", "alert", "die"]
        let isOneShotAnim = oneShotPrefixes.contains { animName.hasPrefix($0) }
        if isOneShotAnim {
            logDebug("setDefaultAnim rejected: \(animName) is a oneShot animation")
            return
        }

        guard strips[animName] != nil else { return }
        defaultAnim = animName
        statusBar?.refresh()
    }

    @objc func resetDefaultAnim(_ sender: NSMenuItem) {
        defaultAnim = nil
        statusBar?.refresh()
    }

    @objc func menuSwitchMob(_ sender: NSMenuItem) {
        guard let code = sender.representedObject as? String, code != mobId else { return }
        let previousMobId = mobId
        mobId = code
        statusBar?.refresh()

        Task {
            let loaded = await loadSprites(for: code)
            await MainActor.run {
                if loaded {
                    self.switchTo(self.bestDefaultAnim())
                    self.statusBar?.refresh()
                } else {
                    self.mobId = previousMobId
                    self.statusBar?.refresh()
                    logDebug("menuSwitchMob: failed to load \(code), restored \(previousMobId)")
                }
            }
        }
    }

    @objc func refreshMobList() {
        Task {
            let api = APIClient()
            let remoteMobs = await api.fetchMobList()
            await MainActor.run { if !remoteMobs.isEmpty { self.mobList = remoteMobs } }
        }
    }

    @objc func addMob() {
        DispatchQueue.main.async {
            InputDialog.ask(message: "添加怪物",
                            info: "输入怪物 code，逗号/换行分隔（多个 code 自动合并）：") { raw in
                guard let raw, !raw.isEmpty else { return }
                let codes = raw.components(separatedBy: CharacterSet(charactersIn: ",\n;"))
                    .map { $0.trimmingCharacters(in: .whitespaces) }
                    .filter { !$0.isEmpty }
                guard !codes.isEmpty else { return }
                Task {
                    let api = APIClient()
                    if codes.count == 1 {
                        let code = codes[0]
                        let apiName = await api.fetchMobName(mobId: code) ?? code
                        let existing = self.mobList.first(where: { $0.name == apiName && $0.code != code })
                        let displayName = existing != nil ? "\(apiName) (\(code))" : apiName
                        _ = await api.fetchAndGenerateSprites(mobId: code)
                        let cm = CacheManager(mobId: code)
                        let priority = ["stand", "say", "mouse", "move", "hand", "laugh", "eye", "fly", "die"]
                        let keys = cm.loadConfig()?.sprites.keys.map { $0 } ?? []
                        let firstAnim = priority.first(where: { keys.contains($0) }) ?? keys.sorted().first ?? "stand"
                        await MainActor.run {
                            if !self.mobList.contains(where: { $0.code == code }) {
                                self.mobList.append(MobInfo(code: code, name: displayName, defaultAnim: firstAnim))
                                self.statusBar?.refresh()
                            }
                        }
                    } else {
                        let mergedCode = codes.joined(separator: ",")
                        let apiName = await api.fetchMobName(mobId: codes[0]) ?? codes[0]
                        let existing = self.mobList.first(where: { $0.name == apiName && $0.code != mergedCode })
                        let displayName = existing != nil ? "\(apiName) (\(codes[0])+\(codes.count-1))" : apiName
                        _ = await api.fetchAndGenerateSprites(mobId: mergedCode)
                        let cm = CacheManager(mobId: mergedCode)
                        let priority = ["stand", "say", "mouse", "move", "hand", "laugh", "eye", "fly", "die"]
                        let keys = cm.loadConfig()?.sprites.keys.map { $0 } ?? []
                        let firstAnim: String
                        if let match = priority.lazy.compactMap({ p in
                            keys.first(where: { $0 == p || $0.hasPrefix("\(p)-") })
                        }).first {
                            firstAnim = match
                        } else {
                            firstAnim = keys.sorted().first ?? "stand"
                        }
                        await MainActor.run {
                            if !self.mobList.contains(where: { $0.code == mergedCode }) {
                                self.mobList.append(MobInfo(code: mergedCode, name: displayName, defaultAnim: firstAnim))
                                self.statusBar?.refresh()
                            }
                        }
                    }
                }
            }
        }
    }

    @objc func renameMob(_ sender: NSMenuItem) {
        guard let code = sender.representedObject as? String,
              let idx = mobList.firstIndex(where: { $0.code == code }) else { return }
        DispatchQueue.main.async {
            InputDialog.ask(message: "重命名", info: "为 \(code) 设置新名称：",
                            defaultValue: self.mobList[idx].name) { newName in
                guard let newName, !newName.isEmpty else { return }
                self.mobList[idx].name = newName
                self.statusBar?.refresh()
            }
        }
    }

    @objc func deleteMob(_ sender: NSMenuItem) {
        guard let code = sender.representedObject as? String,
              let idx = mobList.firstIndex(where: { $0.code == code }) else { return }
        guard mobList.count > 1 else { return }
        mobList.remove(at: idx)
        let cm = CacheManager(mobId: code)
        try? FileManager.default.removeItem(atPath: cm.dir)
        if code == mobId, let first = mobList.first { menuSwitchMobInner(code: first.code) }
        statusBar?.refresh()
    }

    private func menuSwitchMobInner(code: String) {
        let previousMobId = mobId
        mobId = code
        statusBar?.refresh()
        Task {
            let loaded = await loadSprites(for: code)
            await MainActor.run {
                if loaded {
                    self.switchTo(self.bestDefaultAnim())
                    self.statusBar?.refresh()
                } else {
                    self.mobId = previousMobId
                    self.statusBar?.refresh()
                }
            }
        }
    }

    @objc func toggleTerminal() {
        guard let tv = terminalView else { return }
        let wasHidden = tv.isHidden
        tv.isHidden = !tv.isHidden
        if !tv.isHidden { superview?.addSubview(tv, positioned: .above, relativeTo: nil) }
        (superview as? ContainerView)?.petDidResize()
        if !tv.isHidden {
            tv.focus()
            // 首次打开终端时发送问候（静默 + 占位符）
            if wasHidden, let hermes = tv.hermesClient {
                tv.showPlaceholder()
                let greeting = "这是从桌宠来的自动化话语 介绍一下你自己，告诉我现在的本地时间和当地天气，然后打个招呼"
                hermes.send(greeting, silent: true, isGreeting: true)
            }
        }
    }

    // MARK: - Balloon Actions

    @objc func testBalloon() {
        let text = "倒到妈妈牛逼"
        showBalloon(text: text)
    }

    @objc func hideBalloon() {
        balloonView?.dismiss()
    }

    @objc func changeBalloonStyle() {
        // Simple: cycle through nearby IDs
        let ids = [5, 560, 3, 50, 100, 200, 300, 400]
        if let idx = ids.firstIndex(of: currentBalloonId) {
            currentBalloonId = ids[(idx + 1) % ids.count]
        } else {
            currentBalloonId = 560
        }
    }

    // MARK: - Map Background Actions

    @objc func menuSwitchMap(_ sender: NSMenuItem) {
        guard let mapId = sender.representedObject as? String else { return }

        if mapId.isEmpty {
            // 切换到透明背景（无地图）
            currentMapId = nil
            guard let container = superview as? ContainerView else { return }
            container.hideBackground()
            return
        }

        currentMapId = mapId
        guard let container = superview as? ContainerView else { return }
        container.showBackground(mapId: mapId)
    }

    @objc func menuSetMapZoom(_ sender: NSMenuItem) {
        guard let z = sender.representedObject as? Double else { return }
        mapZoom = CGFloat(z)
        guard let container = superview as? ContainerView else { return }
        container.setMapZoom(mapZoom)
    }

    @objc func addNpc() {
        DispatchQueue.main.async {
            InputDialog.ask(message: "添加NPC",
                            info: "输入NPC code，逗号/换行分隔：") { raw in
                guard let raw, !raw.isEmpty else { return }
                let codes = raw.components(separatedBy: CharacterSet(charactersIn: ",\n;"))
                    .map { $0.trimmingCharacters(in: .whitespaces) }
                    .filter { !$0.isEmpty }
                guard !codes.isEmpty else { return }
                Task {
                    let api = APIClient()
                    for code in codes {
                        let apiName = await api.fetchNpcName(npcId: code) ?? code
                        _ = await api.fetchAndGenerateSprites(mobId: code, type: "npc")
                        let cm = CacheManager(mobId: code, entityType: "npc")
                        let priority = ["stand", "say", "mouse", "move", "hand", "laugh", "eye", "fly", "die"]
                        let keys = cm.loadConfig()?.sprites.keys.map { $0 } ?? []
                        let firstAnim = priority.first(where: { keys.contains($0) }) ?? keys.sorted().first ?? "stand"
                        await MainActor.run {
                            if !self.npcList.contains(where: { $0.code == code }) {
                                self.npcList.append(MobInfo(code: code, name: apiName, defaultAnim: firstAnim, type: "npc"))
                                self.statusBar?.refresh()
                            }
                        }
                    }
                }
            }
        }
    }

    @objc func menuSwitchNpc(_ sender: NSMenuItem) {
        guard let code = sender.representedObject as? String, code != mobId || entityType != "npc" else { return }
        let previousMobId = mobId
        entityType = "npc"
        mobId = code
        statusBar?.refresh()
        Task {
            let loaded = await loadSprites(for: code)
            await MainActor.run {
                if loaded {
                    self.switchTo(self.bestDefaultAnim())
                    self.statusBar?.refresh()
                } else {
                    self.entityType = "mob"
                    self.mobId = previousMobId
                    logDebug("menuSwitchNpc: failed to load \(code), restored \(previousMobId)")
                }
            }
        }
    }
}
