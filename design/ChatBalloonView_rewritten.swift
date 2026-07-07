import AppKit

// MARK: - Chat Balloon View (冒险岛聊天戒指风格)
// 按照 MapleSalon2 的 chatBalloonBackground.ts 逻辑重写

class ChatBalloonView: NSView {
    private(set) var tileData: [String: Data] = [:]
    /// WZ origin 坐标缓存（从缓存 origins.json 或 API 获取）
    private var origins: [String: CGPoint] = [:]
    private var balloonText: NSAttributedString?
    private var autoDismissWork: DispatchWorkItem?

    var hasTiles: Bool { tileData.count >= 9 }
    var textColor: NSColor = .white
    var balloonId: Int = 560

    // 瓦片 image 缓存，避免重复 loadCGImage
    private var tileImages: [String: CGImage] = [:]

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
        frame.size = desiredSize(for: attributedText)
        // 清除瓦片缓存，让下次 draw 重新加载
        tileImages = [:]
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

    private func loadCGImage(from data: Data) -> CGImage? {
        guard let img = NSImage(data: data),
              let cg = img.cgImage(forProposedRect: nil, context: nil, hints: nil) else { return nil }
        return cg
    }

    // MARK: - 关键尺寸计算（对照 MapleSalon2）

    /// colWidth = c 瓦片的宽度
    private var colWidth: CGFloat {
        guard let c = cgImage(for: "c") else { return 1 }
        return CGFloat(c.width)
    }

    /// colHeight = c 瓦片的高度
    private var colHeight: CGFloat {
        guard let c = cgImage(for: "c") else { return 1 }
        return CGFloat(c.height)
    }

    /// topOffset = max(nw.origin.y, n.origin.y)
    private var topOffset: CGFloat {
        max(origin(for: "nw").y, origin(for: "n").y)
    }

    /// leftOffset = nw.origin.x
    private var leftOffset: CGFloat {
        origin(for: "nw").x
    }

    /// 文字左上角内边距 = (nw.width - nw.origin.x, nw.height - topOffset)
    private var topLeftPadding: CGPoint {
        let nw = cgImage(for: "nw")
        return CGPoint(
            x: (nw.map { CGFloat($0.width) } ?? 5) - leftOffset,
            y: (nw.map { CGFloat($0.height) } ?? 5) - topOffset
        )
    }

    /// xOffsetByArrow: 当 arrow 瓦片比 s 瓦片窄时，右边需要补偿
    private var xOffsetByArrow: CGFloat {
        guard origins["arrow"] != nil else { return 0 }
        let sW = cgImage(for: "s").map { CGFloat($0.width) } ?? 0
        let arrW = cgImage(for: "arrow").map { CGFloat($0.width) } ?? 0
        return sW > arrW ? sW - arrW : 0
    }

    /// 带 origin.x === -1 特殊处理的 origin 获取
    private func originWithNegFix(for name: String) -> CGPoint {
        let o = origin(for: name)
        // MapleSalon2: pivot.x = (offset.x === -1 && xOffsetByArrow === 0) ? 0 : offset.x
        if o.x == -1 && xOffsetByArrow == 0 {
            return CGPoint(x: 0, y: o.y)
        }
        return o
    }

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

    // MARK: - Canvas 尺寸计算

    /// 对照 MapleSalon2:
    ///   colCount = ceil(minWidth / colWidth)  (最小 3)
    ///   rowCount = ceil(minHeight / colHeight) (最小 2)
    ///   totalW = nw.width - nw.origin.x + colCount * colWidth + ne.width - ne.origin.x - xOffsetByArrow
    ///   totalH = max(nw.height, n.height) + rowCount * colHeight + max(sw.height, s.height, arrow.height)
    private func canvasSize(textContentSize: NSSize) -> NSSize {
        let contentW = textContentSize.width
        let contentH = textContentSize.height

        let cW = colWidth
        let cH = colHeight

        let colCount = max(3, Int(ceil(contentW / cW)))
        let rowCount = max(2, Int(ceil(contentH / cH)))

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

        let leftPad = (nwW - leftOffset)   // nw.width - nw.origin.x
        let rightPad = neW - origin(for: "ne").x  // ne.width - ne.origin.x

        let totalW = leftPad + CGFloat(colCount) * cW + rightPad - xOffsetByArrow
        let topH = max(nwH, nH)
        var botH = max(swH, sH)
        if origins["arrow"] != nil { botH = max(botH, arrH) }
        let totalH = topH + CGFloat(rowCount) * cH + botH

        return NSSize(width: totalW, height: totalH)
    }

    // MARK: - Arrow Tip X（气泡箭头尖端在气泡坐标系中的 X 位置）

    /// Arrow 在底部行的最左侧（i=0），替换 S 瓦片
    /// 尖端位置 = arrow 放置位置 + arrow.width/2
    func arrowTipX() -> CGFloat {
        guard origins["arrow"] != nil else { return bounds.width / 2 }

        let arrW = cgImage(for: "arrow").map { CGFloat($0.width) } ?? 0

        // SW 放置位置: x=0, y=0
        // 然后 x += sw.width - sw.origin.x
        let sw = cgImage(for: "sw")
        let swW = sw.map { CGFloat($0.width) } ?? 0
        let afterSW = swW - origin(for: "sw").x

        // Arrow 放在 i=0 时，x = afterSW
        let arrX = afterSW

        // 箭头的绘制位置：arrX - arrow.origin.x
        // 尖端 = 绘制位置 + 箭头宽度/2
        let drawX = arrX - origin(for: "arrow").x
        let tip = drawX + arrW / 2

        // 还要考虑 colWidth - arrWidth 的居中调整（目前 arrow 居中在 colWidth 内）
        let cW = colWidth
        if cW > arrW {
            // arrow 居中在列内：偏移 (cW - arrW) / 2
            return tip + (cW - arrW) / 2
        }
        return tip
    }

    // MARK: - Drawing

    override func draw(_ dirtyRect: NSRect) {
        guard let attr = balloonText, !tileData.isEmpty else { return }
        guard let ctx = NSGraphicsContext.current?.cgContext else { return }

        let textSz = self.textSize(for: attr)
        let canvasSz = self.canvasSize(textContentSize: textSz)

        drawNineSlice(in: ctx, canvasSize: canvasSz)

        // 文字位置 = topLeftPadding（MapleSalon2 做法）
        let textOrigin = topLeftPadding
        let textX = textOrigin.x
        // AppKit 坐标系: y 从 bottom 往上，topLeftPadding.y 是从顶部往下的偏移
        // 所以文字的底部 = canvasHeight - topLeftPadding.y - textHeight
        let textY = canvasSz.height - textOrigin.y - textSz.height

        let tc = NSTextContainer(size: NSSize(width: textSz.width, height: textSz.height))
        let ts = NSTextStorage(attributedString: attr)
        let lm = NSLayoutManager()
        lm.addTextContainer(tc)
        ts.addLayoutManager(lm)
        tc.lineFragmentPadding = 0
        let glyphRange = lm.glyphRange(for: tc)
        lm.drawGlyphs(forGlyphRange: glyphRange, at: NSPoint(x: textX, y: textY))
    }

    // MARK: - 九宫格绘制

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

        // origins
        let nwO = origin(for: "nw"), nO = origin(for: "n"), neO = origin(for: "ne")
        let wO  = origin(for: "w"),  eO = origin(for: "e")
        let swO = origin(for: "sw"), sO = origin(for: "s"), seO = origin(for: "se")
        let headO = origin(for: "head"), arrO = origin(for: "arrow")

        func draw(_ img: CGImage, at pos: CGPoint, origin o: CGPoint) {
            ctx.draw(img, in: CGRect(x: pos.x - o.x, y: pos.y - o.y,
                                     width: CGFloat(img.width), height: CGFloat(img.height)))
        }

        // 坐标系统: MapleSalon2 (Pixi) Y-down，AppKit Y-up
        // 映射: Pixi y→canvasHeight - Pixi y
        // 实际上我们直接用 AppKit Y-up 坐标系即可:
        //   顶部行 y=canvasHeight
        //   中间行 y=canvasHeight - topHeight
        //   底部行 y=0

        // --- 顶部行 (y = canvas height, top-aligned) ---
        let topBaseY = canvasSize.height

        draw(nwImg, at: CGPoint(x: 0, y: topBaseY), origin: nwO)
        var tx = CGFloat(nwImg.width) - nwO.x
        let half = colCount / 2
        for i in 0..<colCount {
            if i == half, let h = headImg {
                draw(h, at: CGPoint(x: tx, y: topBaseY), origin: headO)
            } else {
                draw(nImg, at: CGPoint(x: tx, y: topBaseY), origin: nO)
            }
            tx += CGFloat(nImg.width)
        }
        tx -= oxa
        draw(neImg, at: CGPoint(x: tx, y: topBaseY), origin: neO)

        // --- 中间行 ---
        // Pixi: y += nw.height - topOffset → new y
        // topOffset = max(nw.origin.y, n.origin.y)
        // 在 AppKit Y-up 中: midTopY = canvasHeight - (nwH - topOffset)
        let nwH = CGFloat(nwImg.height)
        let topOffsetY = max(nwO.y, nO.y)
        let midTopY = topBaseY - (nwH - topOffsetY)
        let leftEdgeX = CGFloat(wImg.width) - wO.x   // = w.width - w.origin.x
        let rightEdgeX = leftEdgeX + cW * CGFloat(colCount) - oxa

        for r in 0..<rowCount {
            let rowY = midTopY - CGFloat(r) * cH
            // W
            draw(wImg, at: CGPoint(x: 0, y: rowY), origin: wO)
            // C tiling (no origin offset for center)
            for i in 0..<colCount {
                ctx.draw(cImg, in: CGRect(x: leftEdgeX + CGFloat(i) * cW, y: rowY, width: cW, height: cH))
            }
            // E
            draw(eImg, at: CGPoint(x: rightEdgeX, y: rowY), origin: eO)
        }

        // --- 底部行 ---
        // Pixi: y progresses from mid area bottom = y + rowCount * colHeight
        // = midTopY - rowCount * cH
        let botBaseY = midTopY - CGFloat(rowCount) * cH

        draw(swImg, at: CGPoint(x: 0, y: botBaseY), origin: swO)
        var bx = CGFloat(swImg.width) - swO.x
        for i in 0..<colCount {
            if i == 0, let arr = arrowImg {
                draw(arr, at: CGPoint(x: bx, y: botBaseY), origin: arrO)
                if CGFloat(sImg.width) > CGFloat(arr.width) {
                    bx -= CGFloat(sImg.width) - CGFloat(arr.width)
                }
            } else {
                draw(sImg, at: CGPoint(x: bx, y: botBaseY), origin: sO)
            }
            bx += cW
        }
        draw(seImg, at: CGPoint(x: bx, y: botBaseY), origin: seO)
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

    // 加载 origin（优先从缓存，否则从 API）
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
        let names = ["nw", "n", "head", "ne", "w", "c", "e", "sw", "s", "arrow", "se"]
        for name in names {
            let path = "\(dir)/\(name).png"
            if let data = try? Data(contentsOf: URL(fileURLWithPath: path)) {
                loaded[name] = data
            }
        }
        if loaded.count >= 9 {
            tileData = loaded
            tileImages = [:]
            // 同时加载 origin
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
        // 保存 origin
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

