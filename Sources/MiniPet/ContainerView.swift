import AppKit

// MARK: - Container View

class ContainerView: NSView {
    let petView: PetView
    let terminalView: TerminalView
    let balloonView: ChatBalloonView
    private var termH: CGFloat = 0

    init(petView: PetView, terminalView: TerminalView) {
        self.petView = petView
        self.terminalView = terminalView
        self.balloonView = ChatBalloonView(frame: .zero)
        let petSize = petView.frame.size
        let termSize = terminalView.frame.size
        super.init(frame: NSRect(x: 0, y: 0, width: petSize.width, height: petSize.height + termSize.height))
        addSubview(petView)
        addSubview(terminalView)
        addSubview(balloonView)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) not implemented") }

    override func layout() {
        super.layout()
        let petH = petView.frame.height
        termH = terminalView.isHidden ? 0 : terminalView.bounds.height
        let ballH: CGFloat = balloonView.isHidden ? 0 : balloonView.frame.height

        // 如果存在背景视图，让背景视图填满容器
        if let bgView = backgroundView {
            bgView.frame = bounds
        }

        // Resize container to fit balloon above pet
        let totalH = petH + termH + ballH
        let totalW = max(petView.frame.width, balloonView.frame.width)
        frame.size = NSSize(width: totalW, height: totalH)
        window?.setContentSize(frame.size)

        // Layout from bottom: terminal → pet → balloon
        termH = terminalView.isHidden ? 0 : terminalView.bounds.height
        terminalView.frame = NSRect(x: 0, y: 0, width: bounds.width, height: termH)
        petView.frame.origin = NSPoint(x: 0, y: termH)

        // Balloon centered above pet, arrow tip at origin point (shift up for aesthetic)
        if !balloonView.isHidden {
            let pv = petView
            let cur = pv.cur
            let ox = pv.strips[cur]?.ox ?? pv.originX
            let oy = pv.strips[cur]?.oy ?? pv.originY
            let originX = CGFloat(ox)
            let arrowX = balloonView.arrowTipX()
            balloonView.frame.origin = NSPoint(x: originX - arrowX,
                                               y: termH + petH - CGFloat(oy) + 30)
        }

        // Debug: show origin line on balloon bottom
        debugOriginLine.isHidden = !cli.debugAPI
        if cli.debugAPI {
            needsToDraw(bounds)
        }
    }

    private lazy var debugOriginLine: NSView = {
        let v = NSView(frame: .zero)
        v.wantsLayer = true
        v.layer?.backgroundColor = NSColor.magenta.cgColor
        v.isHidden = true
        addSubview(v)
        return v
    }()

    override func draw(_ dirtyRect: NSRect) {
        super.draw(dirtyRect)
        guard cli.debugAPI else { return }

        let pv = petView
        let cur = pv.cur
        let ox = pv.strips[cur]?.ox ?? pv.originX
        let oy = pv.strips[cur]?.oy ?? pv.originY
        let originX = CGFloat(ox)
        let petFrame = petView.frame
        let originInContainer = CGPoint(x: originX, y: termH + petFrame.height - CGFloat(oy))

        guard let ctx = NSGraphicsContext.current?.cgContext else { return }

        // Magenta vertical line from balloon bottom to origin point
        if !balloonView.isHidden {
            let ballBottomX = balloonView.frame.minX + balloonView.arrowTipX()
            let ballBottomY = balloonView.frame.minY
            ctx.setStrokeColor(NSColor.magenta.cgColor)
            ctx.setLineWidth(1)
            ctx.beginPath()
            ctx.move(to: CGPoint(x: ballBottomX, y: ballBottomY))
            ctx.addLine(to: originInContainer)
            ctx.strokePath()
        }

        // Red dot at origin point (radius 5px)
        ctx.setFillColor(NSColor.red.cgColor)
        ctx.fillEllipse(in: CGRect(x: originInContainer.x - 5, y: originInContainer.y - 5, width: 10, height: 10))

        // Label
        let label = "origin=(\(ox),\(oy)) cur=\(cur)"
        (label as NSString).draw(at: NSPoint(x: originInContainer.x + 8, y: originInContainer.y),
                                 withAttributes: [.foregroundColor: NSColor.red, .font: NSFont.systemFont(ofSize: 10)])
    }

    func petDidResize() {
        needsLayout = true
    }
}
