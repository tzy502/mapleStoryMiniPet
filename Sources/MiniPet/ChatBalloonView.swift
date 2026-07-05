import AppKit

// MARK: - Chat Balloon Tile Data

struct BalloonTileInfo {
    let data: Data
    let origin: CGPoint
    let url: String
}

// MARK: - Chat Balloon View (MapleSalon2 PixiJS Style)

/// Rewritten to match MapleSalon2 PixiJS renderBackground() exactly.
///
/// Key design:
/// - Origin-based pivot offsets per tile (PixiJS sprite.pivot = origin, sprite.position = (px, py))
/// - TilingSprite for center tile (single repeating texture covering entire center area)
/// - Arrow replaces bottom-row tile index 0 (not separate bottom element)
/// - head tile replaces n at center column of top row
/// - Text positioned at topLeftPadding
/// - Balloon anchor at bottom-center (pivot.y = height, pointing down at character)
/// - Color formula: 0xffffff + 1 + clr (not ARGB)
///
/// PieceFields = ['nw', 'n', 'ne', 'w', 'c', 'e', 'sw', 's', 'se', 'arrow', 'head']
class ChatBalloonView: NSView {
    private var tileInfos: [String: BalloonTileInfo] = [:] {
        didSet { tileCGImages.removeAll() }
    }
    private var balloonText: NSAttributedString?
    private var autoDismissWork: DispatchWorkItem?
    private var tileCGImages: [String: CGImage] = [:]

    var hasTiles: Bool { tileInfos.count >= 9 }
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

    // MARK: - Text Measurement

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

    // MARK: - CGImage Cache

    private func cgImage(_ name: String) -> CGImage? {
        guard let info = tileInfos[name] else { return nil }
        let key = "\(info.data.prefix(32).hashValue)"
        if let cached = tileCGImages[key] { return cached }
        guard let img = NSImage(data: info.data),
              let cg = img.cgImage(forProposedRect: nil, context: nil, hints: nil) else { return nil }
        tileCGImages[key] = cg
        return cg
    }

    // MARK: - Piece Helpers

    private func pieceOrigin(_ name: String) -> CGPoint {
        return tileInfos[name]?.origin ?? .zero
    }

    private func pieceSize(_ name: String) -> CGSize {
        guard let cg = cgImage(name) else { return .zero }
        return CGSize(width: cg.width, height: cg.height)
    }

    /// topOffset = max(pieces[nw]?.origin.y, pieces[n]?.origin.y)
    private var topOffset: CGFloat {
        max(pieceOrigin("nw").y, pieceOrigin("n").y)
    }

    /// leftOffset = pieces[nw]?.origin.x  (MapleSalon2: min of nw origin.x)
    private var leftOffset: CGFloat {
        pieceOrigin("nw").x
    }

    /// topLeftPadding = (nw.width - leftOffset, nw.height - topOffset)
    private var topLeftPadding: CGPoint {
        let nwSize = pieceSize("nw")
        return CGPoint(x: nwSize.width - leftOffset, y: nwSize.height - topOffset)
    }

    private var colWidth: CGFloat { pieceSize("c").width }
    private var colHeight: CGFloat { pieceSize("c").height }

    /// xOffsetByArrow: if arrow exists and s.width > arrow.width, offset = s.width - arrow.width
    /// MapleSalon2: this.pieces[9] && this.assets[7].width > this.assets[9].width ? this.assets[7].width - this.assets[9].width : 0
    private var xOffsetByArrow: CGFloat {
        guard tileInfos["arrow"] != nil else { return 0 }
        let sW = pieceSize("s").width
        let arrW = pieceSize("arrow").width
        return sW > arrW ? sW - arrW : 0
    }

    // MARK: - Grid Computation

    /// Compute the grid layout: colCount, rowCount, and the bounding box.
    /// Uses MapleSalon2's renderBackground() algorithm directly.
    /// Returns (colCount, rowCount, canvasSize).
    private func computeLayout(textContentSize: NSSize) -> (colCount: Int, rowCount: Int, canvasSize: NSSize) {
        guard colWidth > 0, colHeight > 0 else { return (3, 2, .zero) }

        let minWidth = max(textContentSize.width, CGFloat(80))
        let minHeight = max(textContentSize.height, CGFloat(40))

        let colCount = max(3, Int(ceil(minWidth / colWidth)))
        let rowCount = max(2, Int(ceil(minHeight / colHeight)))

        let canvasSize = computeBoundingBox(colCount: colCount, rowCount: rowCount)
        return (colCount, rowCount, canvasSize)
    }

    /// Compute the bounding box of all positioned pieces for a given grid size.
    /// This is the single source of truth for canvas size.
    /// Must exactly match MapleSalon2 renderBackground() positioning.
    private func computeBoundingBox(colCount: Int, rowCount: Int) -> NSSize {
        let nwSize = pieceSize("nw")
        let nwOrigin = pieceOrigin("nw")
        let nSize = pieceSize("n")
        let swSize = pieceSize("sw")
        let swOrigin = pieceOrigin("sw")
        let sSize = pieceSize("s")
        let wSize = pieceSize("w")
        let wOrigin = pieceOrigin("w")

        let ox = xOffsetByArrow
        let cW = colWidth
        let cH = colHeight

        // MapleSalon2 leftOffset in renderBackground:
        // leftOffset = this.assets[3].width - (this.pieces[3]?.origin.x || 0)
        let leftOffsetForCenter = wSize.width - wOrigin.x

        var positions: [(String, CGFloat, CGFloat)] = []

        // Top row (y=0) - matches renderBackground
        var tx: CGFloat = 0
        positions.append(("nw", 0, 0))
        tx += nwSize.width - nwOrigin.x

        let half = colCount / 2
        for i in 0..<colCount {
            let piece = (i == half && tileInfos["head"] != nil) ? "head" : "n"
            positions.append((piece, tx, 0))
            tx += (piece == "head" ? pieceSize("head").width : nSize.width)
        }
        tx -= ox
        positions.append(("ne", tx, 0))

        // Middle rows
        let topRowBottom = nwSize.height - topOffset
        for i in 0..<rowCount {
            let rowY = topRowBottom + CGFloat(i) * cH
            positions.append(("w", 0, rowY))
            // MapleSalon2: rightX = leftOffset + colWidth * colCount - xOffsetByArrow
            let rightX = leftOffsetForCenter + cW * CGFloat(colCount) - ox
            positions.append(("e", rightX, rowY))
        }

        // Bottom row
        let bottomY = topRowBottom + cH * CGFloat(rowCount)
        positions.append(("sw", 0, bottomY))
        var bx = swSize.width - swOrigin.x
        for i in 0..<colCount {
            if i == 0 && tileInfos["arrow"] != nil {
                positions.append(("arrow", bx, bottomY))
                let aSize = pieceSize("arrow")
                if sSize.width > aSize.width {
                    bx -= sSize.width - aSize.width
                }
            } else {
                positions.append(("s", bx, bottomY))
            }
            bx += cW
        }
        positions.append(("se", bx, bottomY))

        // Find bounding box
        var minX: CGFloat = 0, maxX: CGFloat = 0
        var minY: CGFloat = 0, maxY: CGFloat = 0
        var first = true

        for (name, px, py) in positions {
            guard let cg = cgImage(name) else { continue }
            let o = pieceOrigin(name)
            let x = px - o.x
            let y = py - o.y
            let r = x + CGFloat(cg.width)
            let b = y + CGFloat(cg.height)
            if first {
                minX = x; maxX = r; minY = y; maxY = b
                first = false
            } else {
                if x < minX { minX = x }
                if r > maxX { maxX = r }
                if y < minY { minY = y }
                if b > maxY { maxY = b }
            }
        }

        return NSSize(width: maxX - minX, height: maxY - minY)
    }

    // MARK: - Size Calculation

    func desiredSize(for text: NSAttributedString) -> NSSize {
        guard !tileInfos.isEmpty else { return .zero }
        let textSz = self.textSize(for: text)
        let (_, _, canvasSz) = computeLayout(textContentSize: textSz)
        return canvasSz
    }

    /// Arrow tip X position for balloon placement (bottom-center anchor).
    /// Computed from the positioned arrow sprite's center.
    func arrowTipX() -> CGFloat {
        guard tileInfos["arrow"] != nil else { return bounds.width / 2 }
        let swSize = pieceSize("sw")
        let swOrigin = pieceOrigin("sw")
        let arrowX = swSize.width - swOrigin.x
        let arrOrigin = pieceOrigin("arrow")
        let arrW = pieceSize("arrow").width
        return arrowX - arrOrigin.x + arrW / 2
    }

    // MARK: - Drawing

    override func draw(_ dirtyRect: NSRect) {
        guard let attr = balloonText, !tileInfos.isEmpty else { return }
        guard let ctx = NSGraphicsContext.current?.cgContext else { return }

        let textSz = self.textSize(for: attr)
        let (colCount, rowCount, canvasSz) = computeLayout(textContentSize: textSz)

        renderBackground(in: ctx, canvasSize: canvasSz, colCount: colCount, rowCount: rowCount)

        // Text at topLeftPadding (MapleSalon2 PixiJS position)
        let tlp = topLeftPadding

        // Account for PixiJS top-left origin vs CGContext bottom-left origin
        let textX = tlp.x
        let textY = canvasSz.height - tlp.y - textSz.height

        let tc = NSTextContainer(size: NSSize(width: textSz.width, height: textSz.height))
        let ts = NSTextStorage(attributedString: attr)
        let lm = NSLayoutManager()
        lm.addTextContainer(tc)
        ts.addLayoutManager(lm)
        tc.lineFragmentPadding = 0
        let glyphRange = lm.glyphRange(for: tc)
        lm.drawGlyphs(forGlyphRange: glyphRange, at: NSPoint(x: textX, y: textY))
    }

    /// MapleSalon2 renderBackground: origin-based positioning, exact replica.
    ///
    /// MapleSalon2 reference (chatBalloonBackground.ts):
    /// ```
    /// colCount = ceil(minWidth / colWidth)
    /// rowCount = ceil(minHeight / colHeight)
    /// x = 0, y = 0
    /// addSpriteWithPos(0, x, y)           // nw
    /// x += assets[0].width - pieces[0].origin.x
    /// half = floor(colCount / 2)
    /// for i in 0..colCount:
    ///   if i==half && pieces[10]: addSpriteWithPos(10, x, y)  // head
    ///   else: addSpriteWithPos(1, x, y)                        // n
    ///   x += assets[1].width
    /// x -= xOffsetByArrow
    /// addSpriteWithPos(2, x, y)           // ne
    /// x = 0
    /// y += assets[0].height - topOffset
    /// leftOffset = assets[3].width - pieces[3].origin.x
    /// addCenterTile(leftOffset, y, colWidth*colCount, colHeight*rowCount)
    /// for i in 0..rowCount:
    ///   addSpriteWithPos(3, x, y)         // w
    ///   rightX = leftOffset + colWidth*colCount - xOffsetByArrow
    ///   addSpriteWithPos(5, rightX, y)    // e
    ///   y += colHeight
    /// addSpriteWithPos(6, x, y)           // sw
    /// x += assets[6].width - pieces[6].origin.x
    /// for i in 0..colCount:
    ///   if i==0 && pieces[9]: addSpriteWithPos(9, x, y)  // arrow
    ///   else: addSpriteWithPos(7, x, y)                   // s
    ///   x += colWidth
    /// addSpriteWithPos(8, x, y)           // se
    /// ```
    private func renderBackground(in ctx: CGContext, canvasSize: NSSize, colCount: Int, rowCount: Int) {
        guard tileInfos["nw"] != nil, tileInfos["n"] != nil, tileInfos["ne"] != nil,
              tileInfos["w"] != nil, tileInfos["c"] != nil, tileInfos["e"] != nil,
              tileInfos["sw"] != nil, tileInfos["s"] != nil, tileInfos["se"] != nil else { return }

        let ox = xOffsetByArrow
        let cW = colWidth
        let cH = colHeight

        let nwSize = pieceSize("nw")
        let nwOrigin = pieceOrigin("nw")
        let nSize = pieceSize("n")
        let swSize = pieceSize("sw")
        let swOrigin = pieceOrigin("sw")
        let sSize = pieceSize("s")

        // --- Top Row (y=0) ---
        var tx: CGFloat = 0
        let ty: CGFloat = 0

        // addSpriteWithPos(0, x, y)  -- nw
        drawTile(ctx: ctx, name: "nw", atX: tx, atY: ty, canvasHeight: canvasSize.height)
        tx += nwSize.width - nwOrigin.x

        let half = colCount / 2
        for i in 0..<colCount {
            if i == half, let _ = tileInfos["head"] {
                // addSpriteWithPos(10, x, y)  -- head
                drawTile(ctx: ctx, name: "head", atX: tx, atY: ty, canvasHeight: canvasSize.height)
            } else {
                // addSpriteWithPos(1, x, y)  -- n
                drawTile(ctx: ctx, name: "n", atX: tx, atY: ty, canvasHeight: canvasSize.height)
            }
            tx += nSize.width
        }
        tx -= ox
        // addSpriteWithPos(2, x, y)  -- ne
        drawTile(ctx: ctx, name: "ne", atX: tx, atY: ty, canvasHeight: canvasSize.height)

        // --- Middle Rows ---
        // x = 0, y += assets[0].height - topOffset
        let my: CGFloat = nwSize.height - topOffset

        // leftOffset = assets[3].width - pieces[3].origin.x
        // In MapleSalon2: leftOffset = this.assets[3].width - (this.pieces[3]?.origin.x || 0)
        // But in our code, leftOffset = pieceOrigin("nw").x (same as MapleSalon2's this.leftOffset = min(this.pieces[0]?.origin.x || 0))
        // MapleSalon2 renderBackground() redefines leftOffset = assets[3].width - pieces[3].origin.x
        // which is the "w" piece's left offset. This is different from the "nw" leftOffset.
        // Let me re-examine: in MapleSalon2, addCenterTile uses leftOffset as the x position for the center tile.
        // leftOffset = this.assets[3].width - (this.pieces[3]?.origin.x || 0)
        // This is the position of the center tile's left edge, after the "w" piece.
        // In our code, we used leftOffset = pieceOrigin("nw").x but that's the nw origin.x.
        // The MapleSalon2 renderBackground leftOffset is specifically: w.width - w.origin.x
        // which is the offset from the w piece's left edge to its right edge after pivot.
        // This is then used as the x position for the center tile.
        let wSize = pieceSize("w")
        let wOrigin = pieceOrigin("w")
        let leftOffsetForCenter = wSize.width - wOrigin.x

        // addCenterTile(leftOffset, y, colWidth*colCount, colHeight*rowCount)
        drawCenterTile(ctx: ctx, atX: leftOffsetForCenter, atY: my,
                       width: cW * CGFloat(colCount), height: cH * CGFloat(rowCount),
                       canvasHeight: canvasSize.height)

        // Middle rows: w and e
        for i in 0..<rowCount {
            let rowY = my + CGFloat(i) * cH
            // addSpriteWithPos(3, x, y)  -- w (x is still 0 here)
            drawTile(ctx: ctx, name: "w", atX: 0, atY: rowY, canvasHeight: canvasSize.height)
            // rightX = leftOffset + colWidth * colCount - xOffsetByArrow
            let rightX = leftOffsetForCenter + cW * CGFloat(colCount) - ox
            // addSpriteWithPos(5, rightX, y)  -- e
            drawTile(ctx: ctx, name: "e", atX: rightX, atY: rowY, canvasHeight: canvasSize.height)
        }

        // --- Bottom Row ---
        let bottomY = my + cH * CGFloat(rowCount)

        // addSpriteWithPos(6, x, y)  -- sw (x is still 0)
        drawTile(ctx: ctx, name: "sw", atX: 0, atY: bottomY, canvasHeight: canvasSize.height)
        // x += assets[6].width - pieces[6].origin.x
        var bx = swSize.width - swOrigin.x

        // For i in 0..colCount:
        //   if i==0 && pieces[9]: addSpriteWithPos(9, x, y)  // arrow
        //   else: addSpriteWithPos(7, x, y)                   // s
        //   x += colWidth
        let aSize = tileInfos["arrow"].flatMap { _ in pieceSize("arrow") } ?? .zero
        for i in 0..<colCount {
            if i == 0, let _ = tileInfos["arrow"] {
                drawTile(ctx: ctx, name: "arrow", atX: bx, atY: bottomY, canvasHeight: canvasSize.height)
                if sSize.width > aSize.width {
                    bx -= sSize.width - aSize.width
                }
            } else {
                drawTile(ctx: ctx, name: "s", atX: bx, atY: bottomY, canvasHeight: canvasSize.height)
            }
            bx += cW
        }
        // addSpriteWithPos(8, x, y)  -- se
        drawTile(ctx: ctx, name: "se", atX: bx, atY: bottomY, canvasHeight: canvasSize.height)

        // --- Debug borders ---
        guard cli.debugAPI else { return }
        drawDebugBorders(ctx: ctx, canvasSize: canvasSize, colCount: colCount, rowCount: rowCount,
                         my: my, bottomY: bottomY, leftOffsetForCenter: leftOffsetForCenter)
    }

    /// Draw a single tile with origin-based pivot, Y-flipped for CGContext.
    ///
    /// PixiJS: sprite.pivot = origin, sprite.position = (px, py)
    /// Sprite top-left in PixiJS: (px - ox, py - oy)
    /// CGContext (bottom-left Y): (px - ox, canvasHeight - (py - oy) - h)
    /// = (px - ox, canvasHeight - py + oy - h)
    private func drawTile(ctx: CGContext, name: String, atX px: CGFloat, atY py: CGFloat, canvasHeight: CGFloat) {
        guard let info = tileInfos[name], let cg = cgImage(name) else { return }
        let origin = info.origin
        let h = CGFloat(cg.height)
        let cgX = px - origin.x
        let cgY = canvasHeight - (py - origin.y + h)
        ctx.draw(cg, in: CGRect(x: cgX, y: cgY, width: CGFloat(cg.width), height: h))
    }

    /// Draw the center area as a repeating tile pattern (TilingSprite equivalent).
    ///
    /// PixiJS: TilingSprite.from(this.assets[4]), sprite.setSize(width, height), sprite.position.set(x, y)
    /// The center tile has no origin/pivot offset (it tiles from its top-left).
    /// In CGContext (bottom-left Y): x = px, y = canvasHeight - py - height
    private func drawCenterTile(ctx: CGContext, atX px: CGFloat, atY py: CGFloat,
                                width: CGFloat, height: CGFloat, canvasHeight: CGFloat) {
        guard let cg = cgImage("c") else { return }
        let cW = CGFloat(cg.width), cH = CGFloat(cg.height)
        let cgX = px
        let cgY = canvasHeight - py - height

        ctx.saveGState()
        ctx.clip(to: CGRect(x: cgX, y: cgY, width: width, height: height))
        var drawY: CGFloat = 0
        while drawY < height {
            var drawX: CGFloat = 0
            while drawX < width {
                ctx.draw(cg, in: CGRect(x: cgX + drawX, y: cgY + drawY, width: cW, height: cH))
                drawX += cW
            }
            drawY += cH
        }
        ctx.restoreGState()
    }

    // MARK: - Debug Drawing

    private func drawDebugBorders(ctx: CGContext, canvasSize: NSSize, colCount: Int, rowCount: Int,
                                  my: CGFloat, bottomY: CGFloat, leftOffsetForCenter: CGFloat) {
        ctx.setLineWidth(1.0)

        // Top row
        ctx.setStrokeColor(NSColor.red.cgColor)
        let nwOrigin = pieceOrigin("nw")
        let nwSize = pieceSize("nw")
        let nwCgX: CGFloat = 0 - nwOrigin.x
        let nwCgY = canvasSize.height - (0 - nwOrigin.y + nwSize.height)
        ctx.stroke(CGRect(x: nwCgX, y: nwCgY, width: nwSize.width, height: nwSize.height))

        // Middle row separator
        ctx.setStrokeColor(NSColor.yellow.cgColor)
        ctx.beginPath()
        ctx.move(to: CGPoint(x: 0, y: canvasSize.height - my))
        ctx.addLine(to: CGPoint(x: canvasSize.width, y: canvasSize.height - my))
        ctx.move(to: CGPoint(x: 0, y: canvasSize.height - bottomY))
        ctx.addLine(to: CGPoint(x: canvasSize.width, y: canvasSize.height - bottomY))
        ctx.strokePath()

        // Center tile boundary
        ctx.setStrokeColor(NSColor.green.cgColor)
        let cgX = leftOffsetForCenter
        let cgY = canvasSize.height - my - colHeight * CGFloat(rowCount)
        ctx.stroke(CGRect(x: cgX, y: cgY,
                          width: colWidth * CGFloat(colCount),
                          height: colHeight * CGFloat(rowCount)))
    }

    // MARK: - Tile Loading

    func loadTileData(_ newTiles: [String: Data]) {
        var withOrigins: [String: BalloonTileInfo] = [:]
        for (name, data) in newTiles {
            let origin = tileInfos[name]?.origin ?? .zero
            let url = tileInfos[name]?.url ?? ""
            withOrigins[name] = BalloonTileInfo(data: data, origin: origin, url: url)
        }
        tileInfos = withOrigins
    }

    func loadTileDataWithInfo(_ newTiles: [String: BalloonTileInfo]) {
        tileInfos = newTiles
    }

    // MARK: - Origin Persistence

    func loadOriginData() -> [String: CGPoint]? {
        let path = "\(cacheRoot)/balloons/\(balloonId)/origins.json"
        guard let data = try? Data(contentsOf: URL(fileURLWithPath: path)),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: [String: CGFloat]] else {
            return nil
        }
        var origins: [String: CGPoint] = [:]
        for (key, val) in json {
            if let x = val["x"], let y = val["y"] {
                origins[key] = CGPoint(x: x, y: y)
            }
        }
        return origins
    }

    func saveOriginData(_ origins: [String: CGPoint]) {
        let dir = "\(cacheRoot)/balloons/\(balloonId)"
        try? FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        var json: [String: [String: CGFloat]] = [:]
        for (key, pt) in origins {
            json[key] = ["x": pt.x, "y": pt.y]
        }
        if let data = try? JSONSerialization.data(withJSONObject: json, options: .prettyPrinted) {
            try? data.write(to: URL(fileURLWithPath: "\(dir)/origins.json"))
        }
    }

    func loadTilesFromCache(balloonId: Int) -> Bool {
        let dir = "\(cacheRoot)/balloons/\(balloonId)"
        var loaded: [String: BalloonTileInfo] = [:]
        let names = ["nw", "n", "head", "ne", "w", "c", "e", "sw", "s", "arrow", "se"]
        let savedOrigins = loadOriginData()

        for name in names {
            let path = "\(dir)/\(name).png"
            if let data = try? Data(contentsOf: URL(fileURLWithPath: path)) {
                let origin = savedOrigins?[name] ?? .zero
                loaded[name] = BalloonTileInfo(data: data, origin: origin, url: "")
            }
        }
        if loaded.count >= 9 {
            tileInfos = loaded
            return true
        }
        return false
    }

    func saveTilesToCache(balloonId: Int) {
        let dir = "\(cacheRoot)/balloons/\(balloonId)"
        try? FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        for (name, info) in tileInfos {
            try? info.data.write(to: URL(fileURLWithPath: "\(dir)/\(name).png"))
        }
        var origins: [String: CGPoint] = [:]
        for (name, info) in tileInfos {
            origins[name] = info.origin
        }
        saveOriginData(origins)
    }
}