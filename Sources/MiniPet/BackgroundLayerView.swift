import AppKit
import Foundation

// MARK: - Classic Maps

/// 经典冒险岛地图列表（因为 API 的 query/string for maps 返回空）。
let classicMaps: [(id: String, name: String)] = [
    ("100000000", "射手村"),
    ("101000000", "魔法密林"),
    ("103000000", "废弃都市"),
    ("200000000", "天空之城"),
    ("220000000", "玩具城"),
    ("240000000", "水下世界"),
    ("251000000", "神木村"),
    ("260000000", "阿里安特"),
]

// MARK: - Map Cache

/// 地图缓存管理器。
class MapCacheManager {
    static let mapsDir = "\(cacheRoot)/maps"

    /// 获取地图缓存目录。
    static func cacheDir(mapId: String) -> String {
        "\(mapsDir)/\(mapId)"
    }

    /// 判断地图背景是否已缓存。
    static func isCached(mapId: String) -> Bool {
        let dir = cacheDir(mapId: mapId)
        return FileManager.default.fileExists(atPath: "\(dir)/back.png")
    }

    /// 从缓存加载背景合成图。
    static func loadCached(mapId: String) -> (back: NSImage?, front: NSImage?) {
        let dir = cacheDir(mapId: mapId)
        let back = NSImage(contentsOfFile: "\(dir)/back.png")
        let front = NSImage(contentsOfFile: "\(dir)/front.png")
        return (back, front)
    }

    /// 缓存背景合成图到本地。
    static func saveToCache(mapId: String, back: NSImage?, front: NSImage?) {
        let dir = cacheDir(mapId: mapId)
        try? FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)

        if let back = back, let data = back.tiffRepresentation {
            // 保存为 PNG
            if let bitmap = NSBitmapImageRep(data: data),
               let pngData = bitmap.representation(using: .png, properties: [:]) {
                try? pngData.write(to: URL(fileURLWithPath: "\(dir)/back.png"))
            }
        }

        if let front = front, let data = front.tiffRepresentation {
            if let bitmap = NSBitmapImageRep(data: data),
               let pngData = bitmap.representation(using: .png, properties: [:]) {
                try? pngData.write(to: URL(fileURLWithPath: "\(dir)/front.png"))
            }
        }

        // 保存元数据 JSON
        let meta: [String: Any] = ["mapId": mapId]
        if let metaData = try? JSONSerialization.data(withJSONObject: meta, options: .prettyPrinted) {
            try? metaData.write(to: URL(fileURLWithPath: "\(dir)/meta.json"))
        }
    }
}

// MARK: - BackgroundLayerView

/// 地图背景图层视图。
///
/// 将 BackgroundCompositor 合成的 back/front 图像渲染为两个独立的 CALayer：
/// - `backLayer` (z=0)：背景层（角色背后）
/// - `frontLayer` (z=2)：前景层（角色前面）
///
/// 初始状态为透明，调用 `loadMap(mapId:)` 后加载地图背景。
/// 支持通过 `setCanvasSize(_:)` 调整画布大小。
class BackgroundLayerView: NSView {

    // MARK: Properties

    /// 背景合成器（lazy init，持有 WzImageLoader 和 WzXmlParser）
    lazy var compositor: BackgroundCompositor = {
        let loader = WzImageLoader()
        let parser = WzXmlParser()
        return BackgroundCompositor(loader: loader, parser: parser)
    }()

    /// 当前画布大小（默认 800x600）
    var canvasSize: NSSize = NSSize(width: 800, height: 600) {
        didSet { updateLayerFrames() }
    }

    /// 缩放比例（0.5, 1.0, 2.0 等），默认 1.0
    var zoom: CGFloat = 1.0 {
        didSet {
            updateLayerFrames()
            // 触发父视图布局
            if let superview = superview {
                superview.needsLayout = true
            }
        }
    }

    /// 背景层 CALayer（z=0 底层）
    let backLayer: CALayer = {
        let layer = CALayer()
        layer.zPosition = 0
        layer.contentsGravity = .resizeAspect
        return layer
    }()

    /// 前景层 CALayer（z=2 顶层）
    let frontLayer: CALayer = {
        let layer = CALayer()
        layer.zPosition = 2
        layer.contentsGravity = .resizeAspect
        return layer
    }()

    // MARK: Init

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        commonInit()
    }

    required init?(coder: NSCoder) {
        super.init(coder: coder)
        commonInit()
    }

    private func commonInit() {
        wantsLayer = true
        guard let rootLayer = layer else { return }

        // 默认透明背景
        rootLayer.backgroundColor = NSColor.clear.cgColor

        // 子图层使用 topLeft 重力（不要拉伸）
        backLayer.contentsGravity = .topLeft
        frontLayer.contentsGravity = .topLeft

        // 添加子图层
        rootLayer.addSublayer(backLayer)
        rootLayer.addSublayer(frontLayer)

        updateLayerFrames()
    }

    // MARK: Layer Layout

    private func updateLayerFrames() {
        let scaledSize = NSSize(width: canvasSize.width * zoom, height: canvasSize.height * zoom)
        backLayer.frame = CGRect(origin: .zero, size: scaledSize)
        frontLayer.frame = CGRect(origin: .zero, size: scaledSize)
    }

    // MARK: Canvas Size

    /// 更新画布大小。
    func setCanvasSize(_ size: NSSize) {
        canvasSize = size
    }

    /// 获取缩放后的实际显示尺寸。
    var scaledSize: NSSize {
        NSSize(width: canvasSize.width * zoom, height: canvasSize.height * zoom)
    }

    // MARK: Map Loading

    /// 异步加载指定地图的背景。
    ///
    /// 流程：
    /// 1. 检查本地缓存 ~/Library/Caches/MiniPet/maps/{mapId}/
    /// 2. 缓存命中 → 直接加载
    /// 3. 缓存未命中 → 调用 `compositor` 从 API 加载并合成 → 写入缓存
    /// 4. 将 backImage 设置到 backLayer.contents，frontImage 设置到 frontLayer.contents
    ///
    /// - Parameter mapId: 地图 ID（如 "100000000"）
    func loadMap(mapId: String) async {
        logDebug("[BackgroundLayerView] loadMap: mapId=\(mapId)")

        // 1. 尝试从缓存加载
        if MapCacheManager.isCached(mapId: mapId) {
            let cached = MapCacheManager.loadCached(mapId: mapId)
            if cached.back != nil || cached.front != nil {
                logDebug("[BackgroundLayerView] cache hit for map \(mapId)")
                await MainActor.run {
                    self.backLayer.contents = cached.back
                    self.frontLayer.contents = cached.front
                }
                return
            }
        }

        // 2. 缓存未命中，从 API 加载并合成
        let layers = await compositor.fetchBackgrounds(mapId: mapId)
        guard !layers.isEmpty else {
            logDebug("[BackgroundLayerView] no backgrounds found for map \(mapId)")
            clear()
            return
        }

        logDebug("[BackgroundLayerView] loaded \(layers.count) background layers for map \(mapId)")

        let (backImage, frontImage) = await compositor.compositeBackgrounds(
            layers: layers,
            canvasWidth: Int(canvasSize.width),
            canvasHeight: Int(canvasSize.height)
        )

        // 3. 写入缓存
        MapCacheManager.saveToCache(mapId: mapId, back: backImage, front: frontImage)

        await MainActor.run {
            if let back = backImage {
                self.backLayer.contents = back
                logDebug("[BackgroundLayerView] backLayer set (size: \(back.size.width)x\(back.size.height))")
            } else {
                self.backLayer.contents = nil
                logDebug("[BackgroundLayerView] no back image")
            }

            if let front = frontImage {
                self.frontLayer.contents = front
                logDebug("[BackgroundLayerView] frontLayer set (size: \(front.size.width)x\(front.size.height))")
            } else {
                self.frontLayer.contents = nil
                logDebug("[BackgroundLayerView] no front image")
            }
        }
    }

    /// 清空背景图层。
    func clear() {
        backLayer.contents = nil
        frontLayer.contents = nil
    }
}

// MARK: - BackgroundSceneView

/// 背景场景视图，组合背景图层 + 宠物/角色渲染区域。
///
/// 混合渲染策略（z-order 从低到高）：
/// - backLayer (z=0)：背景合成图（静态）
/// - petsLayer (z=1)：宠物/角色动画层
/// - frontLayer (z=2)：前景合成图（静态）
///
/// `petOrigin` 用于定位宠物相对于背景的位置。
class BackgroundSceneView: NSView {

    // MARK: Properties

    /// 背景图层视图
    let backgroundView: BackgroundLayerView

    /// 宠物原点偏移（相对于背景画布的左上角）
    var petOrigin: CGPoint = .zero

    /// 宠物/角色渲染层（z=1）
    let petsLayer: CALayer = {
        let layer = CALayer()
        layer.zPosition = 1
        return layer
    }()

    // MARK: Init

    override init(frame frameRect: NSRect) {
        self.backgroundView = BackgroundLayerView(frame: frameRect)
        super.init(frame: frameRect)
        commonInit()
    }

    required init?(coder: NSCoder) {
        self.backgroundView = BackgroundLayerView(coder: coder) ?? BackgroundLayerView(frame: .zero)
        super.init(coder: coder)
        commonInit()
    }

    private func commonInit() {
        wantsLayer = true
        guard let rootLayer = layer else { return }

        rootLayer.backgroundColor = NSColor.clear.cgColor

        // 添加 backgroundView 作为子视图（它自己管理 backLayer 和 frontLayer）
        backgroundView.frame = bounds
        addSubview(backgroundView)

        // 宠物层在背景之上、前景之下
        rootLayer.addSublayer(petsLayer)
        updatePetsLayerFrame()
    }

    // MARK: Layout

    override func layout() {
        super.layout()
        backgroundView.frame = bounds
        updatePetsLayerFrame()
    }

    private func updatePetsLayerFrame() {
        petsLayer.frame = bounds
    }

    // MARK: Map Loading

    /// 异步加载地图背景到 backgroundView。
    func loadMap(mapId: String) async {
        await backgroundView.loadMap(mapId: mapId)
    }

    // MARK: Pet Origin

    /// 设置宠物原点偏移。
    /// 该偏移用于将 PetView 定位到地图背景上的特定坐标。
    func setPetOrigin(_ origin: CGPoint) {
        petOrigin = origin
    }

    // MARK: Cleanup

    /// 清空所有图层。
    func clear() {
        backgroundView.clear()
        petsLayer.sublayers?.forEach { $0.removeFromSuperlayer() }
    }
}

// MARK: - ContainerView Extension

extension ContainerView {

    /// 弱引用持有的背景视图（由 PetView 直接管理）。
    weak var backgroundView: BackgroundLayerView? {
        get {
            objc_getAssociatedObject(self, &Self.backgroundViewAssociationKey) as? BackgroundLayerView
        }
        set {
            if let old = backgroundView {
                old.removeFromSuperview()
            }
            objc_setAssociatedObject(self, &Self.backgroundViewAssociationKey, newValue, .OBJC_ASSOCIATION_RETAIN_NONATOMIC)

            if let newView = newValue {
                newView.translatesAutoresizingMaskIntoConstraints = false
                // 插入到 petView 下面（底层层级）
                addSubview(newView, positioned: .below, relativeTo: petView)
            }
        }
    }

    private static var backgroundViewAssociationKey: UInt8 = 0

    /// 显示指定地图的背景。
    func showBackground(mapId: String) {
        let bgView: BackgroundLayerView
        if let existing = backgroundView {
            bgView = existing
        } else {
            bgView = BackgroundLayerView(frame: bounds)
            bgView.translatesAutoresizingMaskIntoConstraints = false
            addSubview(bgView, positioned: .below, relativeTo: petView)
            objc_setAssociatedObject(self, &Self.backgroundViewAssociationKey, bgView, .OBJC_ASSOCIATION_RETAIN_NONATOMIC)
        }

        bgView.frame = bounds
        bgView.setCanvasSize(bounds.size)

        // 调整窗口大小以适应地图，宠物保持在左下角位置
        adjustWindowForMap(bgView: bgView)

        Task {
            await bgView.loadMap(mapId: mapId)
        }
    }

    private func adjustWindowForMap(bgView: BackgroundLayerView) {
        guard let window = window else { return }
        let mapScaled = bgView.scaledSize
        let petFrame = petView.frame

        // 新窗口大小：地图宽度（但至少为宠物宽度），高度 = 地图 + 终端（如果有）
        let termH: CGFloat = terminalView.isHidden ? 0 : terminalView.bounds.height
        let totalW = max(mapScaled.width, petFrame.width)
        let totalH = mapScaled.height + termH

        let oldFrame = window.frame
        let newSize = NSSize(width: totalW, height: totalH)
        let newOrigin = NSPoint(x: oldFrame.origin.x, y: oldFrame.origin.y + (oldFrame.height - newSize.height))
        window.setFrame(NSRect(origin: newOrigin, size: newSize), display: true)

        // 设置容器视图大小
        frame.size = newSize
        needsLayout = true
    }

    /// 移除背景视图。
    func hideBackground() {
        backgroundView?.clear()
        backgroundView?.removeFromSuperview()
        objc_setAssociatedObject(self, &Self.backgroundViewAssociationKey, nil, .OBJC_ASSOCIATION_RETAIN_NONATOMIC)

        // 恢复窗口大小为宠物大小
        guard let window = window else { return }
        let petFrame = petView.frame
        let termH: CGFloat = terminalView.isHidden ? 0 : terminalView.bounds.height
        let newSize = NSSize(width: petFrame.width, height: petFrame.height + termH)
        let oldFrame = window.frame
        let newOrigin = NSPoint(x: oldFrame.origin.x, y: oldFrame.origin.y + (oldFrame.height - newSize.height))
        window.setFrame(NSRect(origin: newOrigin, size: newSize), display: true)
        frame.size = newSize
        needsLayout = true
    }

    /// 设置缩放。
    func setMapZoom(_ zoom: CGFloat) {
        guard let bgView = backgroundView else { return }
        bgView.zoom = zoom
        adjustWindowForMap(bgView: bgView)
    }
}