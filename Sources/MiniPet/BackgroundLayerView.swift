import AppKit
import Foundation

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

        // 添加子图层
        rootLayer.addSublayer(backLayer)
        rootLayer.addSublayer(frontLayer)

        updateLayerFrames()
    }

    // MARK: Layer Layout

    private func updateLayerFrames() {
        backLayer.frame = CGRect(origin: .zero, size: canvasSize)
        frontLayer.frame = CGRect(origin: .zero, size: canvasSize)
    }

    // MARK: Canvas Size

    /// 更新画布大小。
    func setCanvasSize(_ size: NSSize) {
        canvasSize = size
    }

    // MARK: Map Loading

    /// 异步加载指定地图的背景。
    ///
    /// 流程：
    /// 1. 调用 `compositor.fetchBackgrounds(mapId:)` 获取背景层列表
    /// 2. 调用 `compositor.compositeBackgrounds(layers:canvasWidth:canvasHeight:)` 合成
    /// 3. 将 backImage 设置到 backLayer.contents，frontImage 设置到 frontLayer.contents
    ///
    /// - Parameter mapId: 地图 ID（如 "100000000"）
    func loadMap(mapId: String) async {
        logDebug("[BackgroundLayerView] loadMap: mapId=\(mapId)")

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

    /// 可选的背景图层视图。
    /// 设置时会被插入到 petView 和 terminalView 之间的 z-order。
    private static var backgroundViewAssociationKey: UInt8 = 0

    var backgroundView: BackgroundLayerView? {
        get {
            objc_getAssociatedObject(self, &Self.backgroundViewAssociationKey) as? BackgroundLayerView
        }
        set {
            // 移除旧的 backgroundView
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

    /// 显示指定地图的背景。
    /// 将创建一个 BackgroundLayerView 并插入到层级中。
    func showBackground(mapId: String) {
        let bgView: BackgroundLayerView
        if let existing = backgroundView {
            bgView = existing
        } else {
            bgView = BackgroundLayerView(frame: bounds)
            bgView.translatesAutoresizingMaskIntoConstraints = false
            addSubview(bgView, positioned: .below, relativeTo: petView)
            // 使用 associated object 存储
            objc_setAssociatedObject(self, &Self.backgroundViewAssociationKey, bgView, .OBJC_ASSOCIATION_RETAIN_NONATOMIC)
        }

        // 让背景视图铺满容器
        bgView.frame = bounds
        bgView.setCanvasSize(bounds.size)

        Task {
            await bgView.loadMap(mapId: mapId)
        }
    }

    /// 隐藏背景图层。
    func hideBackground() {
        backgroundView?.clear()
        backgroundView?.removeFromSuperview()
        objc_setAssociatedObject(self, &Self.backgroundViewAssociationKey, nil, .OBJC_ASSOCIATION_RETAIN_NONATOMIC)
    }
}