import AppKit
import Foundation

// MARK: - Player Paperdoll View

/// Renders a MapleStory character player alongside the pet using layered compositing.
///
/// This view manages an independent WzCharacterCompositor and renders the
/// composited character as a single frame, refreshed at the pet's animation tick.
/// It supports the same animation actions (stand, walk, etc.) as the pet sprite
/// system, but renders each frame via the compositor rather than sprite strips.
///
/// Architecture:
/// - Holds a CharacterAppearance (outfit config)
/// - Uses WzCharacterCompositor to load and composite per-frame images
/// - Renders into a single NSView with a CALayer backing
/// - Animates via tick() called from PetView's frame timer
/// - Persists outfit config to ~/Library/Caches/MiniPet/player/dress.json
class PlayerPaperdollView: NSView {

    // MARK: - Appearance

    /// Current outfit configuration
    var characterAppearance: CharacterAppearance {
        didSet { saveOutfit(); invalidateCompositedFrames() }
    }

    /// Current animation action (e.g. "stand", "walk")
    var currentAction: String = "stand" {
        didSet { fi = 0; frameImages.removeAll() }
    }

    /// Current frame index within the animation
    private var fi: Int = 0

    /// Whether the current action is a one-shot animation
    private var isOneShot: Bool {
        let oneShotPrefixes = ["attack", "skill", "die", "hit", "happy", "alert"]
        return oneShotPrefixes.contains { currentAction.hasPrefix($0) }
    }

    /// Cached frame images for the current action+appearance
    private var frameImages: [NSImage] = []

    /// The compositor for assembling character frames
    private let compositor: WzCharacterCompositor

    /// Whether the player is visible
    var isPlayerVisible: Bool = true {
        didSet { isHidden = !isPlayerVisible; needsDisplay = true }
    }

    /// The overall origin offset for the current frame (used for alignment with pet)
    var currentOriginX: Int = 0
    var currentOriginY: Int = 0

    /// Callback when the view needs to notify the container of a size change
    var onResize: (() -> Void)?

    // MARK: - Init

    init(frame frameRect: NSRect, appearance: CharacterAppearance? = nil) {
        self.characterAppearance = appearance ?? PlayerPaperdollView.defaultAppearance()
        self.compositor = WzCharacterCompositor()
        super.init(frame: frameRect)
        wantsLayer = true
        layer?.backgroundColor = NSColor.clear.cgColor
        layer?.contentsGravity = .topLeft
        // Load saved outfit
        loadOutfit()
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) not implemented") }

    // MARK: - Default Appearance

    /// Returns a reasonable default player appearance.
    static func defaultAppearance() -> CharacterAppearance {
        CharacterAppearance(
            gender: 0, skin: "0", hair: "30000", face: "20000",
            cap: nil, cape: "1102088", coat: "1402000",
            longcoat: nil, pants: "1060006", shoes: "1072001",
            weapon: "1302000", shield: nil, glove: "1082002"
        )
    }

    // MARK: - Frame Composition

    /// Load frames for the current action and appearance, caching them.
    /// Returns true if at least one frame was loaded.
    func loadActionFrames() async -> Bool {
        frameImages.removeAll()

        // Use loadCharacterFrame to load frames one by one, then composite them into NSImages.
        // First, discover how many frames exist by loading frame 0 and checking.
        var frameIndex = 0
        var loadedFrames: [NSImage] = []
        var loadedOriginX = 0
        var loadedOriginY = 0

        while true {
            let bodyFrames = await compositor.loadCharacterFrame(
                appearance: characterAppearance,
                action: currentAction,
                frame: frameIndex
            )
            if bodyFrames.isEmpty { break }

            // Composite all body parts into a single NSImage for this frame
            let composited = compositor.compositeCharacter(frames: bodyFrames)
            loadedFrames.append(composited.image)

            // Track origin values
            let minOx = bodyFrames.map(\.originX).min() ?? 0
            let minOy = bodyFrames.map(\.originY).min() ?? 0
            if frameIndex == 0 {
                loadedOriginX = minOx
                loadedOriginY = minOy
            }

            frameIndex += 1
            // Safety limit: max 100 frames
            if frameIndex > 100 { break }
        }

        self.frameImages = loadedFrames
        self.currentOriginX = loadedOriginX
        self.currentOriginY = loadedOriginY
        return !frameImages.isEmpty
    }

    /// Invalidate cached frames (call when appearance changes).
    private func invalidateCompositedFrames() {
        frameImages.removeAll()
    }

    /// Called from PetView's tick() to advance the animation.
    func tick() {
        guard isPlayerVisible, !frameImages.isEmpty else { return }

        if isOneShot {
            if fi < frameImages.count - 1 {
                fi += 1
            } else {
                // Return to default action
                currentAction = "stand"
                invalidateCompositedFrames()
                Task { await loadActionFrames(); await MainActor.run { renderCurrentFrame() } }
                return
            }
        } else {
            fi = (fi + 1) % frameImages.count
        }
        renderCurrentFrame()
    }

    /// Render the current frame into the layer.
    private func renderCurrentFrame() {
        guard fi < frameImages.count else { return }
        let img = frameImages[fi]
        // Resize view to match frame
        let newSize = img.size
        if frame.size != newSize {
            frame.size = newSize
            onResize?()
        }
        layer?.contents = img
    }

    /// Switch to a different animation action.
    func switchToAction(_ action: String) {
        guard action != currentAction || frameImages.isEmpty else { return }
        currentAction = action
        invalidateCompositedFrames()
        Task {
            let loaded = await loadActionFrames()
            await MainActor.run {
                if loaded {
                    self.renderCurrentFrame()
                }
            }
        }
    }

    /// Load the initial stand action.
    func loadInitial() {
        currentAction = "stand"
        let appearanceSnapshot = characterAppearance
        Task {
            self.characterAppearance = appearanceSnapshot
            let loaded = await loadActionFrames()
            await MainActor.run {
                if loaded {
                    self.renderCurrentFrame()
                }
            }
        }
    }

    /// Reload current action (call after appearance changes).
    func reloadCurrentAction() {
        invalidateCompositedFrames()
        Task {
            let loaded = await loadActionFrames()
            await MainActor.run {
                if loaded {
                    self.renderCurrentFrame()
                }
            }
        }
    }

    // MARK: - Outfit Persistence

    private var outfitDir: String {
        "\(cacheRoot)/player"
    }

    private var outfitPath: String {
        "\(outfitDir)/dress.json"
    }

    /// Save current outfit to disk.
    func saveOutfit() {
        try? FileManager.default.createDirectory(atPath: outfitDir, withIntermediateDirectories: true)
        if let data = try? JSONEncoder().encode(characterAppearance) {
            try? data.write(to: URL(fileURLWithPath: outfitPath))
        }
    }

    /// Load saved outfit from disk.
    @discardableResult
    func loadOutfit() -> Bool {
        guard let data = try? Data(contentsOf: URL(fileURLWithPath: outfitPath)),
              let loaded = try? JSONDecoder().decode(CharacterAppearance.self, from: data) else {
            return false
        }
        characterAppearance = loaded
        return true
    }

    /// Get the origin screen position for balloon alignment.
    func originScreenPosition() -> CGPoint? {
        guard let win = window else { return nil }
        return CGPoint(x: win.frame.origin.x + CGFloat(currentOriginX),
                       y: win.frame.origin.y + (frame.height - CGFloat(currentOriginY)))
    }
}

// MARK: - ContainerView Extension for PlayerPaperdollView

extension ContainerView {

    /// The player paperdoll view, stored as an associated object.
    weak var playerPaperdollView: PlayerPaperdollView? {
        get {
            objc_getAssociatedObject(self, &Self.playerPaperdollAssociationKey) as? PlayerPaperdollView
        }
        set {
            if let old = playerPaperdollView {
                old.removeFromSuperview()
            }
            objc_setAssociatedObject(self, &Self.playerPaperdollAssociationKey, newValue, .OBJC_ASSOCIATION_RETAIN_NONATOMIC)
            if let newView = newValue {
                addSubview(newView, positioned: .above, relativeTo: petView)
            }
        }
    }

    private static var playerPaperdollAssociationKey: UInt8 = 0

    /// Show or hide the player paperdoll.
    func setPlayerVisible(_ visible: Bool) {
        playerPaperdollView?.isPlayerVisible = visible
        if visible && playerPaperdollView == nil {
            showPlayerPaperdoll()
        }
    }

    /// Create and show the player paperdoll alongside the pet.
    func showPlayerPaperdoll() {
        if playerPaperdollView != nil { return }

        let pv = PlayerPaperdollView(frame: NSRect(x: petView.frame.width + 20, y: 0, width: 200, height: 300))
        pv.onResize = { [weak self] in self?.petDidResize() }
        addSubview(pv, positioned: .above, relativeTo: petView)
        objc_setAssociatedObject(self, &Self.playerPaperdollAssociationKey, pv, .OBJC_ASSOCIATION_RETAIN_NONATOMIC)
        pv.loadInitial()

        // Adjust container size to fit both pet and player
        adjustForPlayer()
    }

    /// Hide and remove the player paperdoll.
    func hidePlayerPaperdoll() {
        playerPaperdollView?.removeFromSuperview()
        objc_setAssociatedObject(self, &Self.playerPaperdollAssociationKey, nil, .OBJC_ASSOCIATION_RETAIN_NONATOMIC)
        adjustForPlayer()
    }

    /// Adjust window size to accommodate the player view alongside the pet.
    private func adjustForPlayer() {
        guard let window = window else { return }
        let petFrame = petView.frame
        let playerFrame = playerPaperdollView?.frame ?? .zero
        let termH: CGFloat = terminalView.isHidden ? 0 : terminalView.bounds.height
        let totalW = max(petFrame.width + (playerFrame.width > 0 ? playerFrame.width + 20 : 0),
                         balloonView.frame.width)
        let totalH = max(petFrame.height, playerFrame.height) + termH
        let newSize = NSSize(width: totalW, height: totalH)

        // Adjust player X position to be right of pet
        if let pv = playerPaperdollView, !pv.isHidden {
            pv.frame.origin.x = petFrame.width + 20
        }

        let oldFrame = window.frame
        window.setFrame(NSRect(origin: NSPoint(x: oldFrame.origin.x,
                                                y: oldFrame.origin.y + (oldFrame.height - newSize.height)),
                                size: newSize), display: true)
        frame.size = newSize
        needsLayout = true
    }
}