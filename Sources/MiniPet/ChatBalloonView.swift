import AppKit

// MARK: - Chat Balloon View (冒险岛聊天戒指风格)

class ChatBalloonView: NSView {
    private var tileData: [String: Data] = [:]
    private var balloonText: NSAttributedString?
    private var autoDismissWork: DispatchWorkItem?

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

    private func canvasSize(textContentSize: NSSize) -> NSSize {
        guard let cData = tileData["c"],
              let cImg = loadCGImage(from: cData) else { return .zero }

        let padX: CGFloat = 10
        let padY: CGFloat = 6
        let contentW = textContentSize.width + padX * 2
        let contentH = textContentSize.height + padY * 2

        let cW = CGFloat(cImg.width)
        let cH = CGFloat(cImg.height)

        let minCols = 3
        let minRows = 2
        let cols = max(minCols, Int(ceil(contentW / cW)))
        let rows = max(minRows, Int(ceil(contentH / cH)))

        let nw = loadCGImage(from: tileData["nw"]!)!
        let nH = CGFloat(tileData["n"].flatMap { loadCGImage(from: $0)?.height } ?? 0)
        let ne = loadCGImage(from: tileData["ne"]!)!
        let sw = loadCGImage(from: tileData["sw"]!)!
        let sH = CGFloat(tileData["s"].flatMap { loadCGImage(from: $0)?.height } ?? 0)
        let se = loadCGImage(from: tileData["se"]!)!
        let arrH = CGFloat(tileData["arrow"].flatMap { loadCGImage(from: $0)?.height } ?? 0)

        let totalW = CGFloat(nw.width) + CGFloat(cols) * cW + CGFloat(ne.width)
        let topH = nH
        let midH = cH * CGFloat(rows)
        let botH = max(CGFloat(sw.height), sH, CGFloat(se.height), arrH)
        let totalH = topH + midH + botH

        return NSSize(width: totalW, height: totalH)
    }

    // MARK: - Drawing

    override func draw(_ dirtyRect: NSRect) {
        guard let attr = balloonText, !tileData.isEmpty else { return }

        guard let ctx = NSGraphicsContext.current?.cgContext else { return }

        // Measure text
        let textSize = self.textSize(for: attr)
        let canvasSize = self.canvasSize(textContentSize: textSize)
        let cImg = loadCGImage(from: tileData["c"]!)!
        let cW = CGFloat(cImg.width), cH = CGFloat(cImg.height)

        let nwW = CGFloat(loadCGImage(from: tileData["nw"]!)!.width)
        let nH = CGFloat(tileData["n"].flatMap { loadCGImage(from: $0)?.height } ?? 0)
        let neW = CGFloat(loadCGImage(from: tileData["ne"]!)!.width)
        let arrH = CGFloat(tileData["arrow"].flatMap { loadCGImage(from: $0)?.height } ?? 0)

        let cols = Int(round((canvasSize.width - nwW - neW) / cW))
        let topH = nH
        let midH = cH * CGFloat(Int(round((canvasSize.height - topH - (CGFloat(tileData["sw"].flatMap { loadCGImage(from: $0)?.height } ?? 0) + arrH) / 2) / cH)))

        // Draw nine-slice
        drawNineSlice(in: ctx, canvasSize: canvasSize)

        // Draw text
        let cRegionW = CGFloat(cols) * cW
        let textX = nwW + (cRegionW - textSize.width) / 2
        let textY = topH + (midH - textSize.height) / 2

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

        let nwW = CGFloat(nw.width), nwH = CGFloat(nw.height)
        let nW = CGFloat(n.width), nH = CGFloat(n.height)
        let neW = CGFloat(ne.width), neH = CGFloat(ne.height)
        let wW = CGFloat(w.width), wH = CGFloat(w.height)
        let cW = CGFloat(c.width), cH = CGFloat(c.height)
        let eW = CGFloat(e.width), eH = CGFloat(e.height)
        let swW = CGFloat(sw.width), swH = CGFloat(sw.height)
        let sW = CGFloat(s.width), sH = CGFloat(s.height)
        let arrowW = CGFloat(arrow.width), arrowH = CGFloat(arrow.height)
        let seW = CGFloat(se.width), seH = CGFloat(se.height)

        let cw = canvasSize.width
        let ch = canvasSize.height

        let midCount = Int(round((cw - nwW - neW) / cW))
        let rowCount = Int(round((ch - nH - max(swH, sH, seH, arrowH)) / cH))
        guard midCount > 0, rowCount > 0 else { return }

        let topH = nH
        let midH = cH * CGFloat(rowCount)
        let botH = ch - topH - midH

        // Top row: NW + N×midCount + NE
        ctx.draw(nw, in: CGRect(x: 0, y: ch - nwH, width: nwW, height: nwH))
        for i in 0..<midCount {
            ctx.draw(n, in: CGRect(x: nwW + CGFloat(i) * nW, y: ch - nH, width: nW, height: nH))
        }
        ctx.draw(ne, in: CGRect(x: cw - neW, y: ch - neH, width: neW, height: neH))

        // Middle rows: W + C×midCount + E (×rowCount)
        for r in 0..<rowCount {
            let y = topH + CGFloat(r) * cH
            ctx.draw(w, in: CGRect(x: 0, y: y, width: wW, height: wH))
            for i in 0..<midCount {
                ctx.draw(c, in: CGRect(x: nwW + CGFloat(i) * cW, y: y, width: cW, height: cH))
            }
            ctx.draw(e, in: CGRect(x: cw - eW, y: y, width: eW, height: eH))
        }

        // Bottom row: SW + S×midCount + SE, arrow replaces mid S
        let yBot = topH + midH
        ctx.draw(sw, in: CGRect(x: 0, y: yBot, width: swW, height: swH))
        let midCol = midCount / 2
        for i in 0..<midCount {
            let x = swW + CGFloat(i) * sW
            if i == midCol {
                let ax = x + (sW - arrowW) / 2
                let ay = yBot + botH - arrowH
                ctx.draw(arrow, in: CGRect(x: ax, y: ay, width: arrowW, height: arrowH))
            } else {
                ctx.draw(s, in: CGRect(x: x, y: yBot, width: sW, height: sH))
            }
        }
        ctx.draw(se, in: CGRect(x: cw - seW, y: yBot, width: seW, height: seH))
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
        if loaded.count >= 10 {
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