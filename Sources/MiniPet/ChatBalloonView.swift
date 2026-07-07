import AppKit

// MARK: - Chat Balloon View (冒险岛聊天戒指风格)
// 对照 MapleSalon2 完整重写
// 参考: MapleSalon2/src/renderer/chatBalloon/chatBalloonBackground.ts
//       MapleSalon2/src/renderer/chatBalloon/chatBalloon.ts
//       MapleSalon2/src/renderer/chatBalloon/chatBalloonPiece.ts

class ChatBalloonView: NSView {
    private(set) var tileData: [String: Data] = [:]
    private var origins: [String: CGPoint] = [:]
    private var balloonText: NSAttributedString?
    private var autoDismissWork: DispatchWorkItem?
    private var tileImages: [String: CGImage] = [:]

    // 对照 MapleSalon2 的 11 个瓦片字段
    private static let pieceFields = ["nw", "n", "head", "ne", "w", "c", "e", "sw", "s", "arrow", "se"]

    var hasTiles: Bool { tileData.count >= 9 }
    var textColor: NSColor = .white
    var balloonId: Int = 560

    // ---- 九宫格核心属性 ----

    /// colWidth = c 瓦片的宽度 (MapleSalon2: assets[4].width)
    private var colWidth: CGFloat {
        guard let c = cgImage(for: "c") else { return 1 }
        return CGFloat(c.width)
    }

    /// colHeight = c 瓦片的高度 (MapleSalon2: assets[4].height)
    private var colHeight: CGFloat {
        guard let c = cgImage(for: "c") else { return 1 }
        return CGFloat(c.height)
    }

    /// topOffset = max(nw.origin.y, n.origin.y) (MapleSalon2: math.max(pieces[0]?.origin.y, pieces[1]?.origin.y))
    private var topOffset: CGFloat {
        max(origin(for: "nw").y, origin(for: "n").y)
    }

    /// leftOffset = nw.origin.x (MapleSalon2: Math.min(pieces[0]?.origin.x))
    private var leftOffset: CGFloat {
        origin(for: "nw").x
    }

    /// topLeftPadding = 文字左上角内边距 (MapleSalon2: {x: nw.width - leftOffset, y: nw.height - topOffset})
    private var topLeftPadding: NSPoint {
        let nw = cgImage(for: "nw")
        return NSPoint(
            x: (nw.map { CGFloat($0.width) } ?? 5) - leftOffset,
            y: (nw.map { CGFloat($0.height) } ?? 5) - topOffset
        )
    }

    /// xOffsetByArrow: 当 arrow 比 s 窄时，右边需要补偿 (MapleSalon2 逻辑)
    private var xOffsetByArrow: CGFloat {
        guard origins["arrow"] != nil else { return 0 }
        let sW = cgImage(for: "s").map { CGFloat($0.width) } ?? 0
        let arrW = cgImage(for: "arrow").map { CGFloat($0.width) } ?? 0
        return sW > arrW ? sW - arrW : 0
    }

    // MARK: - Init

    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        layer?.backgroundColor = NSColor.clear.cgColor
        isHidden = true
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) not implemented") }

    // MARK: - Public API

    func show(text: String, autoDismiss: TimeInterval = 4.0) {
        let attr = NSAttributedString(string: text, attributes: [
            .foregroundColor: textColor,
            .font: NSFont.systemFont(ofSize: 12),
        ])
        show(attributedText: attr, autoDismiss: autoDismiss)
    }

    func show(attributedText: NSAttributedString, autoDismiss: TimeInterval = 4.0) {
        balloonText = attributedText
        autoDismissWork?.cancel()
        isHidden = false
        layer?.opacity = 0.0
        tileImages = [:]

        let sz = desiredSize(for: attributedText)
        frame.size = sz
        setNeedsDisplay(bounds)

        if let sv = superview { sv.needsLayout = true }

        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = 0.2
            layer?.opacity = 1.0
        }

        let work = DispatchWorkItem { [weak self] in
            guard let self = self else { return }
            NSAnimationContext.runAnimationGroup { ctx in
                ctx.duration = 0.3
                self.layer?.opacity = 0.0
            } completionHandler: {
                self.isHidden = true
            }
        }
        autoDismissWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + autoDismiss, execute: work)
    }

    func dismiss() {
        autoDismissWork?.cancel()
        isHidden = true
        layer?.opacity = 0.0
    }

    // MARK: - 瓦片 + origin 缓存

    private func cgImage(for name: String) -> CGImage? {
        if let cached = tileImages[name] { return cached }
        guard let data = tileData[name], let img = loadCGImage(from: data) else { return nil }
        tileImages[name] = img
        return img
    }

    private func origin(for name: String) -> CGPoint {
        origins[name] ?? .zero
    }

    /// MapleSalon2 特殊处理: origin.x === -1 且 xOffsetByArrow === 0 时 pivot.x = 0
    private func pivotOrigin(for name: String) -> CGPoint {
        let o = origin(for: name)
        if o.x == -1 && xOffsetByArrow == 0 {
            return CGPoint(x: 0, y: o.y)
        }
        return o
    }

    private func loadCGImage(from data: Data) -> CGImage? {
        guard let img = NSImage(data: data),
              let cg = img.cgImage(forProposedRect: nil, context: nil, hints: nil) else { return nil }
        return cg
    }

    // MARK: - 尺寸计算

    /// 文字测量
    private func textSize(for text: NSAttributedString) -> NSSize {
        let tc = NSTextContainer(size: NSSize(width: 400, height: CGFloat.greatestFiniteMagnitude))
        let ts = NSTextStorage(attributedString: text)
        let lm = NSLayoutManager()
        lm.addTextContainer(tc)
        ts.addLayoutManager(lm)
        tc.lineFragmentPadding = 0
        _ = lm.glyphRange(for: tc)
        return lm.usedRect(for: tc).size
    }

    func desiredSize(for text: NSAttributedString) -> NSSize {
        guard !tileData.isEmpty else { return .zero }
        let textSize = self.textSize(for: text)
        return canvasSize(textContentSize: textSize)
    }

    /// 对照 MapleSalon2 chatBalloonBackground.ts:
    ///   colCount = ceil(minWidth / colWidth) (最小 3)
    ///   rowCount = ceil(minHeight / colHeight) (最小 2)
    ///   totalW = (nw.width - nw.origin.x) + colCount * colWidth + (ne.width - ne.origin.x) - xOffsetByArrow
    ///   totalH = max(nw.height, n.height) + rowCount * colHeight + max(sw.height, s.height, arrow.height)
    private func canvasSize(textContentSize: NSSize) -> NSSize {
        let cW = colWidth
        let cH = colHeight

        let colCount = max(3, Int(ceil(textContentSize.width / cW)))
        let rowCount = max(2, Int(ceil(textContentSize.height / cH)))

        let nw = cgImage(for: "nw")
        let ne = cgImage(for: "ne")
        let n = cgImage(for: "n")
        let sw = cgImage(for: "sw")
        let s = cgImage(for: "s")

        let nwW = nw.map { CGFloat($0.width) } ?? 0
        let neW = ne.map { CGFloat($0.width) } ?? 0
        let nwH = nw.map { CGFloat($0.height) } ?? 0
        let nH = n.map { CGFloat($0.height) } ?? 0
        let swH = sw.map { CGFloat($0.height) } ?? 0
        let sH = s.map { CGFloat($0.height) } ?? 0
        let arrH = cgImage(for: "arrow").map { CGFloat($0.height) } ?? 0

        let leftPad = nwW - leftOffset        // nw.width - nw.origin.x
        let rightPad = neW - origin(for: "ne").x  // ne.width - ne.origin.x

        let totalW = leftPad + CGFloat(colCount) * cW + rightPad - xOffsetByArrow
        let topH = max(nwH, nH)
        var botH = max(swH, sH)
        if origins["arrow"] != nil { botH = max(botH, arrH) }
        let totalH = topH + CGFloat(rowCount) * cH + botH

        return NSSize(width: totalW, height: totalH)
    }

    // MARK: - Arrow Tip X (气泡箭头尖端在气泡坐标系中的 X 位置)

    /// MapleSalon2: Arrow 在底部行最左侧 (i=0)，替换 S 瓦片
    /// 尖端 = arrow 绘制位置的 x + arrow.width/2
    func arrowTipX() -> CGFloat {
        guard origins["arrow"] != nil else { return bounds.width / 2 }

        let sw = cgImage(for: "sw")
        let swW = sw.map { CGFloat($0.width) } ?? 0
        let afterSW = swW - origin(for: "sw").x  // 放完 sw 后的 x 坐标

        // Arrow 放在 i=0: x = afterSW, 绘制位置 = afterSW - arrow.origin.x
        let arrW = cgImage(for: "arrow").map { CGFloat($0.width) } ?? 0
        let drawX = afterSW - origin(for: "arrow").x
        let tip = drawX + arrW / 2

        // 如果 arrow 比 colWidth 窄，居中调整
        let cW = colWidth
        if cW > arrW {
            return tip + (cW - arrW) / 2
        }
        return tip
    }

    // MARK: - Drawing

    override func draw(_ dirtyRect: NSRect) {
        guard let attr = balloonText, !tileData.isEmpty else { return }
        guard let ctx = NSGraphicsContext.current?.cgContext else { return }

        let canvasSz = self.canvasSize(textContentSize: attr.size())
        let textOrigin = topLeftPadding
        let canvasW = canvasSz.width
        let canvasH = canvasSz.height

        drawNineSlice(in: ctx, canvasSize: canvasSz)

        // 内容区 = 文字在气泡内的可用区域
        // 左 = textOrigin.x (nw.width - nw.origin.x)
        // 上 = textOrigin.y (nw.height - topOffset)
        // 右 = ne.width - ne.origin.x
        // 下 = max(sw.height, s.height, arrow.height)
        let rightPad = (cgImage(for: "ne").map { CGFloat($0.width) } ?? 0) - origin(for: "ne").x
        var botH: CGFloat = 0
        if let sw = cgImage(for: "sw") { botH = max(botH, CGFloat(sw.height)) }
        if let s  = cgImage(for: "s")  { botH = max(botH, CGFloat(s.height)) }
        if origins["arrow"] != nil, let a = cgImage(for: "arrow") { botH = max(botH, CGFloat(a.height)) }

        let contentW = canvasW - textOrigin.x - rightPad
        let contentH = canvasH - textOrigin.y - botH

        // 用 contentW 限制宽度重新测量文字（自动折行）
        let tc = NSTextContainer(size: NSSize(width: contentW, height: CGFloat.greatestFiniteMagnitude))
        let ts = NSTextStorage(attributedString: attr)
        let lm = NSLayoutManager()
        lm.addTextContainer(tc)
        ts.addLayoutManager(lm)
        tc.lineFragmentPadding = 0
        _ = lm.glyphRange(for: tc)
        let textSz = lm.usedRect(for: tc).size
        let textW = textSz.width
        let textH = textSz.height

        // 文字在内容区内水平垂直居中
        let textX = textOrigin.x + (contentW - textW) / 2
        // AppKit Y-up: 文字底部坐标，内容区底部 = canvasH - botH
        let textY = canvasH - botH - (contentH - textH) / 2 - textH

        let tc2 = NSTextContainer(size: NSSize(width: textW, height: textH))
        let ts2 = NSTextStorage(attributedString: attr)
        let lm2 = NSLayoutManager()
        lm2.addTextContainer(tc2)
        ts2.addLayoutManager(lm2)
        tc2.lineFragmentPadding = 0
        let glyphRange = lm2.glyphRange(for: tc2)
        lm2.drawGlyphs(forGlyphRange: glyphRange, at: NSPoint(x: textX, y: textY))
    }

    // MARK: - 九宫格绘制 (对照 MapleSalon2 chatBalloonBackground.ts)

    private func drawPiece(_ ctx: CGContext, _ img: CGImage, pixiX x: CGFloat, pixiY y: CGFloat, origin o: CGPoint, canvasH: CGFloat) {
        ctx.draw(img, in: CGRect(
            x: x - o.x,
            y: canvasH - y + o.y - CGFloat(img.height),
            width: CGFloat(img.width),
            height: CGFloat(img.height)
        ))
    }

    private func drawNineSlice(in ctx: CGContext, canvasSize: NSSize) {
        guard let text = balloonText else { return }
        let textSz = self.textSize(for: text)

        let cW = colWidth
        let cH = colHeight
        let colCount = max(3, Int(ceil(textSz.width / cW)))
        let rowCount = max(2, Int(ceil(textSz.height / cH)))

        guard let nwImg = cgImage(for: "nw"),
              let nImg  = cgImage(for: "n"),
              let neImg = cgImage(for: "ne"),
              let wImg  = cgImage(for: "w"),
              let cImg  = cgImage(for: "c"),
              let eImg  = cgImage(for: "e"),
              let swImg = cgImage(for: "sw"),
              let sImg  = cgImage(for: "s"),
              let seImg = cgImage(for: "se") else { return }

        let headImg = cgImage(for: "head")
        let arrowImg = cgImage(for: "arrow")
        let oxa = xOffsetByArrow

        let canvasH = canvasSize.height

        // === 所有坐标运算在 Pixi Y-down 中进行 ===

        // --- 1. 顶部行 ---
        var px: CGFloat = 0
        var py: CGFloat = 0

        // nw
        drawPiece(ctx, nwImg, pixiX: px, pixiY: py, origin: origin(for: "nw"), canvasH: canvasH)
        px += CGFloat(nwImg.width) - origin(for: "nw").x

        let half = colCount / 2
        let nImgW = CGFloat(nImg.width)
        for i in 0..<colCount {
            if i == half, let h = headImg {
                drawPiece(ctx, h, pixiX: px, pixiY: py, origin: origin(for: "head"), canvasH: canvasH)
            } else {
                drawPiece(ctx, nImg, pixiX: px, pixiY: py, origin: origin(for: "n"), canvasH: canvasH)
            }
            px += nImgW
        }
        px -= oxa

        // ne
        drawPiece(ctx, neImg, pixiX: px, pixiY: py, origin: origin(for: "ne"), canvasH: canvasH)

        // y 往下移到中间区域顶部
        px = 0
        py += CGFloat(nwImg.height) - topOffset

        // --- 2. 中间行 ---
        let leftEdgeX = CGFloat(wImg.width) - origin(for: "w").x
        let rightEdgeX = leftEdgeX + cW * CGFloat(colCount) - oxa

        for _ in 0..<rowCount {
            // w
            drawPiece(ctx, wImg, pixiX: px, pixiY: py, origin: origin(for: "w"), canvasH: canvasH)

            // c 平铺 (不带 origin 偏移)
            for i in 0..<colCount {
                ctx.draw(cImg, in: CGRect(
                    x: leftEdgeX + CGFloat(i) * cW,
                    y: canvasH - py - cH,  // Pixi y→AppKit, 无 pivot
                    width: cW,
                    height: cH
                ))
            }

            // e
            drawPiece(ctx, eImg, pixiX: rightEdgeX, pixiY: py, origin: origin(for: "e"), canvasH: canvasH)

            py += cH
        }

        // --- 3. 底部行 ---
        // sw
        drawPiece(ctx, swImg, pixiX: 0, pixiY: py, origin: origin(for: "sw"), canvasH: canvasH)

        var bx = CGFloat(swImg.width) - origin(for: "sw").x
        let sImgW = CGFloat(sImg.width)

        for i in 0..<colCount {
            if i == 0, let arr = arrowImg {
                drawPiece(ctx, arr, pixiX: bx, pixiY: py, origin: origin(for: "arrow"), canvasH: canvasH)
                if sImgW > CGFloat(arr.width) {
                    bx -= sImgW - CGFloat(arr.width)
                }
            } else {
                drawPiece(ctx, sImg, pixiX: bx, pixiY: py, origin: origin(for: "s"), canvasH: canvasH)
            }
            bx += cW
        }

        // se
        drawPiece(ctx, seImg, pixiX: bx, pixiY: py, origin: origin(for: "se"), canvasH: canvasH)
    }

    // MARK: - Tile Loading

    func loadTileData(_ newTiles: [String: Data], origins newOrigins: [String: CGPoint] = [:]) {
        tileData = newTiles
        tileImages = [:]
        if !newOrigins.isEmpty {
            origins = newOrigins
        }
    }

    func setOrigins(_ newOrigins: [String: CGPoint]) {
        origins = newOrigins
    }

    func loadOriginsFromCache(balloonId: Int) -> Bool {
        let path = "\(cacheRoot)/balloons/\(balloonId)/origins.json"
        guard let data = try? Data(contentsOf: URL(fileURLWithPath: path)),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: [String: CGFloat]] else { return false }
        var loaded: [String: CGPoint] = [:]
        for (key, dict) in json {
            if let x = dict["x"], let y = dict["y"] {
                loaded[key] = CGPoint(x: x, y: y)
            }
        }
        if !loaded.isEmpty {
            origins = loaded
            return true
        }
        return false
    }

    func loadTilesFromCache(balloonId: Int) -> Bool {
        let dir = "\(cacheRoot)/balloons/\(balloonId)"
        var loaded: [String: Data] = [:]
        for name in Self.pieceFields {
            let path = "\(dir)/\(name).png"
            if let data = try? Data(contentsOf: URL(fileURLWithPath: path)) {
                loaded[name] = data
            }
        }
        if loaded.count >= 9 {
            tileData = loaded
            tileImages = [:]
            _ = loadOriginsFromCache(balloonId: balloonId)
            return true
        }
        return false
    }

    func saveTilesToCache(balloonId: Int) {
        let dir = "\(cacheRoot)/balloons/\(balloonId)"
        try? FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        for (name, data) in tileData {
            try? data.write(to: URL(fileURLWithPath: "\(dir)/\(name).png"))
        }
        if !origins.isEmpty {
            var json: [String: [String: CGFloat]] = [:]
            for (key, pt) in origins {
                json[key] = ["x": pt.x, "y": pt.y]
            }
            if let data = try? JSONSerialization.data(withJSONObject: json, options: .sortedKeys) {
                try? data.write(to: URL(fileURLWithPath: "\(dir)/origins.json"))
            }
        }
    }
}