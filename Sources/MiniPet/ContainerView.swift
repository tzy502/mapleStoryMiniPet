import AppKit

// MARK: - Container View

class ContainerView: NSView {
    let petView: PetView
    let terminalView: TerminalView
    let balloonView: ChatBalloonView

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
        let termH = terminalView.bounds.height
        petView.frame.origin = NSPoint(x: 0, y: bounds.height - petH)
        terminalView.frame = NSRect(x: 0, y: 0, width: bounds.width, height: termH)

        // Balloon: centered above pet
        if !balloonView.isHidden {
            let bSize = balloonView.frame.size
            balloonView.frame = NSRect(x: (bounds.width - bSize.width) / 2,
                                       y: bounds.height - petH + bSize.height,
                                       width: bSize.width, height: bSize.height)
        }
    }

    func petDidResize() {
        let petSize = petView.frame.size
        let termH = terminalView.isHidden ? 0 : terminalView.bounds.height
        let newSize = NSSize(width: petSize.width, height: petSize.height + termH)
        frame.size = newSize
        window?.setContentSize(newSize)
        needsLayout = true
    }
}
