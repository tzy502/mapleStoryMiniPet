import AppKit
import Foundation

// MARK: - Skill Effect Frame

/// 技能特效单帧数据。
struct SkillEffectFrame {
    let image: NSImage
    let originX: Int
    let originY: Int
    let delay: Int
    let z: Int
    var flipX: Bool = false
}

// MARK: - Skill Effect Renderer

/// 技能特效渲染器。
/// 从 Skill/{job}/{skillId}.img/effect/ 加载帧序列，
/// 支持解析 delay、origin，以及叠加合成到角色上。
///
/// WZ 技能特效路径结构：
/// Skill/{jobFolder}/{skillId}.img/
///   ├── info/                    → 技能信息（非特效用）
///   ├── effect/                  → 特效根节点
///   │   ├── 0/                   → 帧 0（最常见，单帧或多帧）
///   │   │   ├── origin           → (ox, oy) vector
///   │   │   ├── delay            → 帧延迟（毫秒）
///   │   │   └── (PNG data)
///   │   └── 1/                   → 帧 1（多帧动画）
///   ├── action/                  → 动作根节点（部分技能）
///   │   └── {actionName}/
///   │       └── 0/               → 动作的特效帧
///   └── level/                   → 等级节点（部分技能）
///       └── {level}/
///           └── effect/
///               └── 0/           → 等级对应的特效
///
/// 注意：部分技能的特效路径为 `effect/{frame}`，部分为 `action/{actionName}/{frame}`。
/// 此渲染器优先尝试 effect/ 路径，回退到 action/ 路径。
class WzSkillEffectRenderer {

    private let wzLoader: WzImageLoader
    private let wzParser: WzXmlParser

    init(loader: WzImageLoader = WzImageLoader(),
         parser: WzXmlParser = WzXmlParser()) {
        self.wzLoader = loader
        self.wzParser = parser
    }

    // MARK: - 帧序列加载

    /// 加载技能特效帧序列。
    ///
    /// 搜索顺序：
    /// 1. Skill/{job}/{skillId}.img/effect/ 下的所有帧（最常用）
    /// 2. Skill/{job}/{skillId}.img/action/{action}/（部分技能使用）
    /// 3. Skill/{job}/{skillId}.img/level/{lvl}/effect/（等级技能）
    ///
    /// - Parameters:
    ///   - skillId: 技能 ID（如 "1001001"）
    ///   - jobFolder: 职业文件夹路径（如 "Skill/100.img"）
    ///   - preferredAction: 可选，指定动作名（部分技能有多个动作特效）
    /// - Returns: 按帧索引排序的 SkillEffectFrame 数组
    func loadEffectFrames(skillId: String,
                          jobFolder: String,
                          preferredAction: String? = nil) async -> [SkillEffectFrame] {
        let basePath = "\(jobFolder)/\(skillId).img"

        // 尝试路径 1: effect/ 下的逐帧
        if let frames = await loadFramesFromNode(path: "\(basePath)/effect") {
            return frames
        }

        // 尝试路径 2: action/{action}/0/（动作特效）
        if let action = preferredAction {
            let actionPath = "\(basePath)/action/\(action)"
            if let frames = await loadFramesFromNode(path: actionPath) {
                return frames
            }
        }

        // 尝试路径 3: 遍历 action/ 下的所有动作
        if let actionNode = await wzParser.fetchNode(path: "\(basePath)/action") {
            for (actionName, _) in actionNode.children {
                if actionName == "info" { continue }
                if let frames = await loadFramesFromNode(path: "\(basePath)/action/\(actionName)") {
                    return frames
                }
            }
        }

        // 尝试路径 4: level/1/effect/
        if let levelNode = await wzParser.fetchNode(path: "\(basePath)/level") {
            let sortedLevels = levelNode.children.keys.compactMap(Int.init).sorted()
            if let firstLevel = sortedLevels.first {
                let levelPath = "\(basePath)/level/\(firstLevel)/effect"
                if let frames = await loadFramesFromNode(path: levelPath) {
                    return frames
                }
            }
        }

        return []
    }

    /// 从指定 WZ 节点路径加载所有帧（子节点按数字 key 排序）。
    private func loadFramesFromNode(path: String) async -> [SkillEffectFrame]? {
        guard let node = await wzParser.fetchNode(path: path) else { return nil }

        // 收集所有数字子节点（帧索引）
        let frameKeys = node.children.keys
            .compactMap { Int($0) }
            .filter { $0 >= 0 }
            .sorted()

        guard !frameKeys.isEmpty else { return nil }

        var frames: [SkillEffectFrame] = []
        for key in frameKeys {
            guard let child = node.children["\(key)"] else { continue }

            // 获取帧图片
            let framePath = "\(path)/\(key)"
            guard let image = await loadFrameImage(wzPath: framePath) else {
                // 如果这个帧没有直接图片，尝试加载子节点下的图片
                continue
            }

            // 获取 delay
            let delay: Int
            if let delayNode = child.children["delay"] {
                delay = delayNode.intValue > 0 ? delayNode.intValue : 100
            } else {
                delay = 100
            }

            // 获取 origin
            let (ox, oy): (Int, Int)
            if let originNode = child.children["origin"],
               case .vector = originNode.type {
                ox = originNode.x
                oy = originNode.y
            } else {
                ox = 0
                oy = 0
            }

            frames.append(SkillEffectFrame(
                image: image,
                originX: ox,
                originY: oy,
                delay: delay,
                z: key
            ))
        }

        return frames.isEmpty ? nil : frames
    }

    /// 加载单帧图片，尝试多个可能的 PNG 位置。
    private func loadFrameImage(wzPath: String) async -> NSImage? {
        // 直接路径
        if let data = await wzLoader.fetchImage(wzPath: wzPath),
           let image = NSImage(data: data) {
            return image
        }
        // 部分帧在有子节点的情况下将图片放在帧编号的子节点
        // 如 effect/0/0 （某些技能用两层数字表示子帧）
        guard let node = await wzParser.fetchNode(path: wzPath) else { return nil }
        for (subKey, _) in node.children {
            let subPath = "\(wzPath)/\(subKey)"
            if let data = await wzLoader.fetchImage(wzPath: subPath),
               let image = NSImage(data: data) {
                return image
            }
        }
        return nil
    }

    // MARK: - 特效帧信息查询

    /// 获取技能特效的总帧数（用于预先了解动画长度）。
    func effectFrameCount(skillId: String, jobFolder: String) async -> Int {
        let basePath = "\(jobFolder)/\(skillId).img"
        guard let node = await wzParser.fetchNode(path: "\(basePath)/effect") else {
            return 0
        }
        return node.children.keys.compactMap { Int($0) }.count
    }

    /// 获取指定帧的延迟。
    func frameDelay(skillId: String, jobFolder: String, frame: Int) async -> Int {
        let path = "\(jobFolder)/\(skillId).img/effect/\(frame)/delay"
        guard let base = await resolveBase() else { return 100 }
        let urlStr = "\(base)/node/json/\(path)?simple=true"
        guard let url = URL(string: urlStr) else { return 100 }
        do {
            let (data, _) = try await URLSession.shared.data(from: url)
            if let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] {
                return json["value"] as? Int ?? 100
            }
        } catch { }
        return 100
    }

    // MARK: - 合成到角色

    /// 将技能特效帧合成到角色帧上。
    ///
    /// - Parameters:
    ///   - character: 角色合成结果（图像 + origin）
    ///   - effectFrames: 技能特效帧序列
    ///   - frameIndex: 当前特效帧索引（循环取模）
    ///   - characterOffset: 角色位置偏移（用于在场景中定位）
    /// - Returns: 合成后的图像
    static func compositeWithCharacter(
        character: CompositedCharacter,
        effectFrames: [SkillEffectFrame],
        frameIndex: Int,
        characterOffset: (x: Int, y: Int) = (0, 0)
    ) -> NSImage {
        guard !effectFrames.isEmpty else { return character.image }

        let efIndex = frameIndex % effectFrames.count
        let effect = effectFrames[efIndex]

        var layers: [CompositingLayer] = []

        // 角色层（z=0）
        layers.append(CompositingLayer(
            image: character.image,
            originX: character.originX + characterOffset.x,
            originY: character.originY + characterOffset.y,
            z: 0
        ))

        // 特效层（z=1，在角色之上）
        // 特效 origin 相对于角色的整体 origin
        let effectOX = character.originX + characterOffset.x - effect.originX
        let effectOY = character.originY + characterOffset.y - effect.originY
        layers.append(CompositingLayer(
            image: effect.image,
            originX: effectOX,
            originY: effectOY,
            z: 1,
            flipX: effect.flipX
        ))

        return WzCompositor.composite(layers: layers)
    }

    /// 将多帧特效序列合成到角色上（全帧合成）。
    static func compositeWithCharacterAllFrames(
        character: CompositedCharacter,
        effectFrames: [SkillEffectFrame],
        characterOffset: (x: Int, y: Int) = (0, 0)
    ) -> [NSImage] {
        return effectFrames.indices.map { i in
            compositeWithCharacter(
                character: character,
                effectFrames: effectFrames,
                frameIndex: i,
                characterOffset: characterOffset
            )
        }
    }

    /// 将特效叠加到已有精灵图条上（用于 PetView 兼容模式）。
    ///
    /// 将特效帧绘制到精灵图条对应帧的底部。
    /// 适用于现有 sprite-strip 渲染管线的快速增强。
    static func compositeOnSpriteStrip(
        stripImage: CGImage,
        stripFrameIndex: Int,
        frameWidth: Int,
        effectFrame: SkillEffectFrame,
        stripOriginX: Int,
        stripOriginY: Int
    ) -> CGImage? {
        let canvasW = max(frameWidth, effectFrame.image.size.width.rounded())
        let canvasH = max(CGFloat(stripImage.height), effectFrame.image.size.height.rounded())

        let rep = NSBitmapImageRep(
            bitmapDataPlanes: nil,
            pixelsWide: Int(canvasW),
            pixelsHigh: Int(canvasH),
            bitsPerSample: 8,
            samplesPerPixel: 4,
            hasAlpha: true,
            isPlanar: false,
            colorSpaceName: .deviceRGB,
            bytesPerRow: 0,
            bitsPerPixel: 0
        )!
        rep.size = NSSize(width: canvasW, height: canvasH)

        guard let ctx = NSGraphicsContext(bitmapImageRep: rep)?.cgContext else { return nil }

        // 绘制精灵图条帧
        let clipRect = CGRect(x: CGFloat(stripFrameIndex * frameWidth), y: 0,
                              width: CGFloat(frameWidth), height: CGFloat(stripImage.height))
        ctx.draw(stripImage, in: CGRect(x: 0, y: 0, width: CGFloat(frameWidth), height: CGFloat(stripImage.height)),
                 byTiling: false)
        ctx.clip(to: clipRect)

        // 绘制特效
        let effSize = effectFrame.image.size
        let effX = CGFloat(stripOriginX - effectFrame.originX)
        let effY = CGFloat(stripOriginY - effectFrame.originY)
        // 特效在精灵图条坐标系中的位置，需要做 y 轴翻转
        ctx.saveGState()
        ctx.translateBy(x: 0, y: canvasH)
        ctx.scaleBy(x: 1, y: -1)
        let flipY = canvasH - (effY + effSize.height)
        if let effCG = effectFrame.image.cgImage(forProposedRect: nil, context: nil, hints: nil) {
            ctx.draw(effCG, in: CGRect(x: effX, y: flipY, width: effSize.width, height: effSize.height))
        }
        ctx.restoreGState()

        return rep.cgImage
    }

    // MARK: - 直接加载（完整流程便捷方法）

    /// 一站式加载角色 + 特效合成帧。
    ///
    /// - Parameters:
    ///   - appearance: 角色外观
    ///   - action: 角色动作
    ///   - frame: 角色帧索引
    ///   - skillId: 技能 ID
    ///   - jobFolder: 职业文件夹
    ///   - avatarCompositor: 角色合成器实例
    /// - Returns: 合成后的帧序列（角色 + 特效逐帧匹配）
    func loadCharacterWithEffect(
        appearance: CharacterAppearance,
        action: String,
        frame: Int,
        skillId: String,
        jobFolder: String,
        avatarCompositor: AvatarCompositor
    ) async -> NSImage? {
        async let characterResult = avatarCompositor.compositeCharacterFrameWithOrigin(
            appearance: appearance,
            action: action,
            frame: frame
        )
        async let effectFrames = loadEffectFrames(skillId: skillId, jobFolder: jobFolder)

        guard let character = await characterResult else { return nil }
        let frames = await effectFrames

        guard !frames.isEmpty else { return character.image }

        // 特效帧按角色帧循环匹配
        let efIndex = frame % frames.count
        return Self.compositeWithCharacter(
            character: character,
            effectFrames: frames,
            frameIndex: efIndex
        )
    }

    // MARK: - Helpers

    private func resolveBase() async -> String? {
        return await APIClient().resolveBase()
    }
}

// MARK: - EffectLayer (CALayer 特效播放器)

/// CALayer 特效播放器。
/// 将技能特效作为 CALayer 播放，支持 loop/oneShot 模式。
class EffectLayer: CALayer {

    /// 播放模式
    enum PlayMode {
        case loop      // 循环播放
        case oneShot   // 播放一次后移除
    }

    private var frames: [SkillEffectFrame] = []
    private var currentFrame: Int = 0
    private var displayLink: CVDisplayLink?
    private var lastFrameTime: CFTimeInterval = 0
    private var frameDelay: CFTimeInterval = 0.1
    private var mode: PlayMode = .loop
    private var baseOrigin: CGPoint = .zero

    override init() {
        super.init()
        isOpaque = false
        backgroundColor = NSColor.clear.cgColor
    }

    override init(layer: Any) {
        super.init(layer: layer)
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) not implemented")
    }

    /// 加载并播放技能特效。
    /// - Parameters:
    ///   - frames: 特效帧序列
    ///   - mode: 播放模式（默认 loop）
    ///   - originOffset: 偏移量（相对于父 layer 的 origin）
    func play(frames: [SkillEffectFrame], mode: PlayMode = .loop, originOffset: CGPoint = .zero) {
        stop()
        self.frames = frames
        self.mode = mode
        self.baseOrigin = originOffset
        self.currentFrame = 0
        self.frameDelay = frames.isEmpty ? 0.1 : CFTimeInterval(frames[0].delay) / 1000.0
        self.lastFrameTime = CACurrentMediaTime()

        if !frames.isEmpty {
            displayFirstFrame()
            startDisplayLink()
        }
    }

    /// 停止播放并清除。
    func stop() {
        stopDisplayLink()
        contents = nil
        frames = []
        currentFrame = 0
    }

    // MARK: - Private

    private func displayFirstFrame() {
        guard let first = frames.first else { return }
        displayFrame(first)
    }

    private func displayFrame(_ frame: SkillEffectFrame) {
        contents = frame.image.cgImage(forProposedRect: nil, context: nil, hints: nil)

        let size = frame.image.size
        bounds = CGRect(origin: .zero, size: size)
        position = CGPoint(
            x: baseOrigin.x - CGFloat(frame.originX) + size.width / 2,
            y: baseOrigin.y - CGFloat(frame.originY) + size.height / 2
        )
    }

    private func startDisplayLink() {
        CVDisplayLinkCreateWithActiveCGDisplays(&displayLink)
        guard let link = displayLink else { return }

        let unmanaged = Unmanaged.passUnretained(self)
        CVDisplayLinkSetOutputCallback(link, { (_, _, _, _, displayLinkContext) -> CVReturn in
            let layer = Unmanaged<EffectLayer>.fromOpaque(displayLinkContext!).takeUnretainedValue()
            layer.tick()
            return kCVReturnSuccess
        }, unmanaged.toOpaque())
        CVDisplayLinkStart(link)
    }

    private func stopDisplayLink() {
        if let link = displayLink {
            CVDisplayLinkStop(link)
            displayLink = nil
        }
    }

    @objc private func tick() {
        let now = CACurrentMediaTime()
        guard now - lastFrameTime >= frameDelay else { return }
        lastFrameTime = now

        currentFrame += 1
        if currentFrame >= frames.count {
            switch mode {
            case .loop:
                currentFrame = 0
            case .oneShot:
                stop()
                DispatchQueue.main.async { [weak self] in
                    self?.removeFromSuperlayer()
                }
                return
            }
        }

        guard currentFrame < frames.count else { return }
        let frameDelayMs = frames[currentFrame].delay
        frameDelay = CFTimeInterval(max(frameDelayMs, 30)) / 1000.0

        DispatchQueue.main.async { [weak self] in
            guard let self = self, self.currentFrame < self.frames.count else { return }
            self.displayFrame(self.frames[self.currentFrame])
        }
    }
}