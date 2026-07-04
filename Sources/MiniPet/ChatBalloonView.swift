import AppKit

// MARK: - Chat Balloon View (冒险岛聊天戒指风格)

class ChatBalloonView: NSView {
    private(set) var tileData: [String: Data] = [:]
    private var balloonText: NSAttributedString?
    private var autoDismissWork: DispatchWorkItem?

    var hasTiles: Bool { tileData.count >= 9 }
    var textColor: NSColor = .white
    var balloonId: Int = 560

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

    // MARK: - 9-Slice Layout Calculation

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

    private func loadCGImage(from data: Data) -> CGImage? {
        guard let img = NSImage(data: data),
              let cg = img.cgImage(forProposedRect: nil, context: nil, hints: nil) else { return nil }
        return cg
    }

    func desiredSize(for text: NSAttributedString) -> NSSize {
        guard !tileData.isEmpty else { return .zero }
        let textSize = self.textSize(for: text)
        return canvasSize(textContentSize: textSize)
    }

    func arrowTipX() -> CGFloat {
        guard let cImg = tileData["c"].flatMap({ loadCGImage(from: $0) }),
              let wImg = tileData["w"].flatMap({ loadCGImage(from: $0) }),
              let nwImg = tileData["nw"].flatMap({ loadCGImage(from: $0) }),
              let swImg = tileData["sw"].flatMap({ loadCGImage(from: $0) }) else { return 0 }
        let leftColW = max(CGFloat(nwImg.width), CGFloat(wImg.width), CGFloat(swImg.width))
        let cW = CGFloat(cImg.width)
        guard let text = balloonText else { return leftColW + cW / 2 }
        let sz = canvasSize(textContentSize: textSize(for: text))
        let rightColW = max(
            CGFloat(tileData["ne"].flatMap { loadCGImage(from: $0)?.width } ?? 0),
            CGFloat(tileData["e"].flatMap { loadCGImage(from: $0)?.width } ?? 0),
            CGFloat(tileData["se"].flatMap { loadCGImage(from: $0)?.width } ?? 0))
        let midAreaW = sz.width - leftColW - rightColW
        let midCount = Int(round(midAreaW / cW))
        return leftColW + CGFloat(midCount / 2) * cW + cW / 2
    }

    private func canvasSize(textContentSize: NSSize) -> NSSize {
        guard let cData = tileData["c"],
              let cImg = loadCGImage(from: cData) else { return .zero }

        let padX: CGFloat = 10
        let padY: CGFloat = 6
        let contentW = textContentSize.width + padX * 2
        let contentH = textContentSize.height + padY * 2

        let cW = CGFloat(cImg.width)
        let cH = CGFloat(cImg.height)

        let nwW = CGFloat(tileData["nw"].flatMap { loadCGImage(from: $0)?.width } ?? 0)
        let nH = CGFloat(tileData["n"].flatMap { loadCGImage(from: $0)?.height } ?? 0)
        let nwH = CGFloat(tileData["nw"].flatMap { loadCGImage(from: $0)?.height } ?? 0)
        let neW = CGFloat(tileData["ne"].flatMap { loadCGImage(from: $0)?.width } ?? 0)
        let neH = CGFloat(tileData["ne"].flatMap { loadCGImage(from: $0)?.height } ?? 0)
        let wW = CGFloat(tileData["w"].flatMap { loadCGImage(from: $0)?.width } ?? 0)
        let eW = CGFloat(tileData["e"].flatMap { loadCGImage(from: $0)?.width } ?? 0)
        let swW = CGFloat(tileData["sw"].flatMap { loadCGImage(from: $0)?.width } ?? 0)
        let swH = CGFloat(tileData["sw"].flatMap { loadCGImage(from: $0)?.height } ?? 0)
        let sH = CGFloat(tileData["s"].flatMap { loadCGImage(from: $0)?.height } ?? 0)
        let seW = CGFloat(tileData["se"].flatMap { loadCGImage(from: $0)?.width } ?? 0)
        let seH = CGFloat(tileData["se"].flatMap { loadCGImage(from: $0)?.height } ?? 0)
        let arrH = CGFloat(tileData["arrow"].flatMap { loadCGImage(from: $0)?.height } ?? 0)

        let leftColW = max(nwW, wW, swW)
        let rightColW = max(neW, eW, seW)
        let minCols = 3
        let minRows = 2
        let cols = max(minCols, Int(ceil(contentW / cW)))
        let rows = max(minRows, Int(ceil(contentH / cH)))

        let totalW = leftColW + CGFloat(cols) * cW + rightColW
        let topH = max(nwH, nH, neH)
        let midH = cH * CGFloat(rows)
        let botH = max(swH, sH, seH, arrH)
        let totalH = topH + midH + botH

        return NSSize(width: totalW, height: totalH)
    }

    // MARK: - Drawing

    override func draw(_ dirtyRect: NSRect) {
        guard let attr = balloonText, !tileData.isEmpty else { return }

        guard let ctx = NSGraphicsContext.current?.cgContext else { return }

        let textSize = self.textSize(for: attr)
        let canvasSize = self.canvasSize(textContentSize: textSize)
        guard let cData = tileData["c"], let cImg = loadCGImage(from: cData) else { return }
        let cW = CGFloat(cImg.width), cH = CGFloat(cImg.height)

        let nwW = CGFloat(tileData["nw"].flatMap { loadCGImage(from: $0)?.width } ?? 0)
        let nwH = CGFloat(tileData["nw"].flatMap { loadCGImage(from: $0)?.height } ?? 0)
        let nH = CGFloat(tileData["n"].flatMap { loadCGImage(from: $0)?.height } ?? 0)
        let neW = CGFloat(tileData["ne"].flatMap { loadCGImage(from: $0)?.width } ?? 0)
        let neH = CGFloat(tileData["ne"].flatMap { loadCGImage(from: $0)?.height } ?? 0)
        let wW = CGFloat(tileData["w"].flatMap { loadCGImage(from: $0)?.width } ?? 0)
        let swW = CGFloat(tileData["sw"].flatMap { loadCGImage(from: $0)?.width } ?? 0)
        let swH = CGFloat(tileData["sw"].flatMap { loadCGImage(from: $0)?.height } ?? 0)
        let sH = CGFloat(tileData["s"].flatMap { loadCGImage(from: $0)?.height } ?? 0)
        let eW = CGFloat(tileData["e"].flatMap { loadCGImage(from: $0)?.width } ?? 0)
        let seW = CGFloat(tileData["se"].flatMap { loadCGImage(from: $0)?.width } ?? 0)
        let seH = CGFloat(tileData["se"].flatMap { loadCGImage(from: $0)?.height } ?? 0)
        let arrH = CGFloat(tileData["arrow"].flatMap { loadCGImage(from: $0)?.height } ?? 0)

        let leftEdge = max(nwW, wW, swW)
        let rightEdge = canvasSize.width - max(neW, eW, seW)
        let midCount = Int(round((rightEdge - leftEdge) / cW))
        let topH = max(nwH, nH, neH)
        let botMax = max(swH, sH, seH, arrH)
        let rowCount = max(2, Int(round((canvasSize.height - topH - botMax) / cH)))
        let botH = canvasSize.height - topH - cH * CGFloat(rowCount)

        drawNineSlice(in: ctx, canvasSize: canvasSize)

        let centerW = CGFloat(midCount) * cW
        let textX = leftEdge + (centerW - textSize.width) / 2
        let textY = botH + (cH * CGFloat(rowCount) - textSize.height) / 2

        let tc = NSTextContainer(size: NSSize(width: textSize.width, height: textSize.height))
        let ts = NSTextStorage(attributedString: attr)
        let lm = NSLayoutManager()
        lm.addTextContainer(tc)
        ts.addLayoutManager(lm)
        tc.lineFragmentPadding = 0
        let glyphRange = lm.glyphRange(for: tc)
        lm.drawGlyphs(forGlyphRange: glyphRange, at: NSPoint(x: textX, y: textY))
    }

    private func drawNineSlice(in ctx: CGContext, canvasSize: NSSize) {
        guard let nwData = tileData["nw"], let nData = tileData["n"],
              let neData = tileData["ne"], let wData = tileData["w"], let cData = tileData["c"],
              let eData = tileData["e"], let swData = tileData["sw"], let sData = tileData["s"],
              let arrowData = tileData["arrow"], let seData = tileData["se"] else { return }

        guard let nw = loadCGImage(from: nwData), let n = loadCGImage(from: nData),
              let ne = loadCGImage(from: neData),
              let w = loadCGImage(from: wData), let c = loadCGImage(from: cData),
              let e = loadCGImage(from: eData), let sw = loadCGImage(from: swData),
              let s = loadCGImage(from: sData), let arrow = loadCGImage(from: arrowData),
              let se = loadCGImage(from: seData) else { return }

        let cW = CGFloat(c.width), cH = CGFloat(c.height)
        let nW = CGFloat(n.width), nH = CGFloat(n.height)
        let sW = CGFloat(s.width), sH = CGFloat(s.height)

        // All tiles use the SAME grid: each mid-cell is cW wide.
        // Left column tiles (NW, W, SW) right-align at leftColW.
        // Right column tiles (NE, E, SE) left-align at rightStartX.
        let maxLeftW = max(CGFloat(nw.width), CGFloat(w.width), CGFloat(sw.width))
        let maxRightW = max(CGFloat(ne.width), CGFloat(e.width), CGFloat(se.width))
        let midAreaW = canvasSize.width - maxLeftW - maxRightW
        let midCount = Int(round(midAreaW / cW))
        let actualMidW = CGFloat(midCount) * cW

        let topRowH = max(CGFloat(nw.height), CGFloat(n.height), CGFloat(ne.height))
        let botRowH = max(CGFloat(sw.height), CGFloat(s.height), CGFloat(se.height), CGFloat(arrow.height))
        let rowCount = max(2, Int(round((canvasSize.height - topRowH - botRowH) / cH)))
        let actualMidH = CGFloat(rowCount) * cH

        let midStartX = maxLeftW
        let rightStartX = maxLeftW + actualMidW

        // --- Top row: bottom-aligned at ch ---
        let topY_bottom = canvasSize.height  // bottom edge of top row = canvas height
        ctx.draw(nw, in: CGRect(x: maxLeftW - CGFloat(nw.width), y: topY_bottom - CGFloat(nw.height),
                                width: CGFloat(nw.width), height: CGFloat(nw.height)))
        for i in 0..<midCount {
            let x = midStartX + CGFloat(i) * cW
            ctx.draw(n, in: CGRect(x: x, y: topY_bottom - nH, width: nW, height: nH))
        }
        ctx.draw(ne, in: CGRect(x: rightStartX, y: topY_bottom - CGFloat(ne.height),
                                width: CGFloat(ne.width), height: CGFloat(ne.height)))

        // head tile (optional) — replaces N at middle column
        if let headData = tileData["head"], let head = loadCGImage(from: headData) {
            let headCol = midCount / 2
            let hx = midStartX + CGFloat(headCol) * cW
            let hw = CGFloat(head.width), hh = CGFloat(head.height)
            ctx.draw(head, in: CGRect(x: hx, y: topY_bottom - hh, width: hw, height: hh))
        }

        // --- Middle rows ---
        for r in 0..<rowCount {
            let y = botRowH + CGFloat(r) * cH
            // Left column tile: right-aligned
            ctx.draw(w, in: CGRect(x: maxLeftW - CGFloat(w.width), y: y,
                                   width: CGFloat(w.width), height: CGFloat(w.height)))
            for i in 0..<midCount {
                ctx.draw(c, in: CGRect(x: midStartX + CGFloat(i) * cW, y: y, width: cW, height: cH))
            }
            ctx.draw(e, in: CGRect(x: rightStartX, y: y,
                                   width: CGFloat(e.width), height: CGFloat(e.height)))
        }

        // --- Bottom row: top-aligned at y=0 ---
        ctx.draw(sw, in: CGRect(x: maxLeftW - CGFloat(sw.width), y: 0,
                                width: CGFloat(sw.width), height: CGFloat(sw.height)))
        let midCol = midCount / 2
        for i in 0..<midCount {
            let x = midStartX + CGFloat(i) * cW
            if i == midCol {
                let aw = CGFloat(arrow.width), ah = CGFloat(arrow.height)
                let ax = x + (cW - aw) / 2
                ctx.draw(arrow, in: CGRect(x: ax, y: 0, width: aw, height: ah))
            } else {
                ctx.draw(s, in: CGRect(x: x, y: 0, width: sW, height: sH))
            }
        }
        ctx.draw(se, in: CGRect(x: rightStartX, y: 0,
                                width: CGFloat(se.width), height: CGFloat(se.height)))

        // --- Debug borders ---
        ctx.setLineWidth(1.0)

        ctx.setStrokeColor(NSColor.red.cgColor)
        ctx.stroke(CGRect(x: maxLeftW - CGFloat(nw.width), y: topY_bottom - CGFloat(nw.height),
                          width: CGFloat(nw.width), height: CGFloat(nw.height)))
        for i in 0..<midCount {
            ctx.stroke(CGRect(x: midStartX + CGFloat(i) * cW, y: topY_bottom - nH, width: nW, height: nH))
        }
        ctx.stroke(CGRect(x: rightStartX, y: topY_bottom - CGFloat(ne.height),
                          width: CGFloat(ne.width), height: CGFloat(ne.height)))

        ctx.setStrokeColor(NSColor.blue.cgColor)
        for r in 0..<rowCount {
            let y = botRowH + CGFloat(r) * cH
            ctx.stroke(CGRect(x: maxLeftW - CGFloat(w.width), y: y,
                              width: CGFloat(w.width), height: CGFloat(w.height)))
            for i in 0..<midCount {
                ctx.stroke(CGRect(x: midStartX + CGFloat(i) * cW, y: y, width: cW, height: cH))
            }
            ctx.stroke(CGRect(x: rightStartX, y: y,
                              width: CGFloat(e.width), height: CGFloat(e.height)))
        }

        ctx.setStrokeColor(NSColor.green.cgColor)
        ctx.stroke(CGRect(x: maxLeftW - CGFloat(sw.width), y: 0,
                          width: CGFloat(sw.width), height: CGFloat(sw.height)))
        for i in 0..<midCount {
            let x = midStartX + CGFloat(i) * cW
            if i == midCol {
                let aw = CGFloat(arrow.width), ah = CGFloat(arrow.height)
                let ax = x + (cW - aw) / 2
                ctx.stroke(CGRect(x: ax, y: 0, width: aw, height: ah))
            } else {
                ctx.stroke(CGRect(x: x, y: 0, width: sW, height: sH))
            }
        }
        ctx.stroke(CGRect(x: rightStartX, y: 0,
                          width: CGFloat(se.width), height: CGFloat(se.height)))

        ctx.setStrokeColor(NSColor.yellow.cgColor)
        ctx.beginPath()
        ctx.move(to: CGPoint(x: 0, y: botRowH))
        ctx.addLine(to: CGPoint(x: canvasSize.width, y: botRowH))
        ctx.move(to: CGPoint(x: 0, y: canvasSize.height - topRowH))
        ctx.addLine(to: CGPoint(x: canvasSize.width, y: canvasSize.height - topRowH))
        ctx.strokePath()
    }

    // MARK: - Tile Loading

    func loadTileData(_ newTiles: [String: Data]) {
        tileData = newTiles
    }

    func loadTilesFromCache(balloonId: Int) -> Bool {
        let dir = "\(cacheRoot)/balloons/\(balloonId)"
        let _ = FileManager.default
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
    }
}