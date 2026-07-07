import AppKit

// MARK: - Container View
// 气泡定位逻辑对照 MapleSalon2 重写:
// MapleSalon2 ChatBalloon.renderChatBalloon():
//   textNode.position.copyFrom(background.topLeftPadding)
//   this.pivot.y = this.height  (容器以底部为锚点)
//
// 定位方式: 气泡底部箭头尖端对齐宠物精灵 origin 点上方

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

        // Container 尺寸: 气泡 + 宠物 + 终端
        let totalH = petH + termH + ballH
        let totalW = max(petView.frame.width, balloonView.frame.width)
        frame.size = NSSize(width: totalW, height: totalH)
        window?.setContentSize(frame.size)

        // 从下往上布局: terminal → pet → balloon
        termH = terminalView.isHidden ? 0 : terminalView.bounds.height
        terminalView.frame = NSRect(x: 0, y: 0, width: bounds.width, height: termH)
        petView.frame.origin = NSPoint(x: 0, y: termH)

        // --- 气泡定位 (对照 MapleSalon2) ---
        // MapleSalon2 ChatBalloon:
        //   this.textNode.position.copyFrom(this.background.topLeftPadding)
        //   this.pivot.y = this.height   ← 关键: 容器 pivot 在底部！
        //
        // 所以气泡的 anchor 在底部，箭头尖端对齐 origin 点上方
        //
        // 在我们的布局中:
        //   arrowTipX() = 箭头尖端在气泡坐标系中的 X
        //   气泡底部 Y = termH + petH - (帧高度 - frameContentTopY)
        //   frameContentTopY = 内容顶部到帧底部的距离 (AppKit Y-up)
        //   所以 contentTopY (从顶部往下) = 帧高度 - frameContentTopY

        if !balloonView.isHidden {
            let pv = petView
            let cur = pv.cur
            let ox = pv.strips[cur]?.ox ?? pv.originX
            let originX = CGFloat(ox)

            // 从精灵 origin 点到气泡箭头尖端的 X 偏移
            let arrowX = balloonView.arrowTipX()

            // 计算气泡底部应该对齐的 Y
            // 气泡底部在 origin 点上方: 需要留出 origin 点到帧顶部的空间
            let contentTopY: CGFloat
            if let s = pv.strips[cur] {
                // frameContentTopY = s.fh - contentTopY (AppKit Y-up 转换)
                // 所以 contentTopY = s.fh - frameContentTopY
                contentTopY = CGFloat(s.fh) - CGFloat(pv.frameContentTopY)
            } else {
                contentTopY = 0
            }

            // 气泡底部 Y = 地面 + 帧高度 - contentTopY
            // contentTopY 是第一个非透明像素距帧顶部的距离
            // 气泡底部 = 帧底部 + 从 origin 到顶部的距离 = 精灵内容顶部
            let balloonBottomY = termH + petH - contentTopY
            let balloonH = balloonView.frame.height

            // 气泡 x: 使 arrowTip 对齐 originX
            // arrowTipX() = 箭头尖端在气泡坐标系中的 X
            // 所以气泡左边缘 = originX - arrowTipX()
            let balloonX = originX - arrowX

            balloonView.frame.origin = NSPoint(x: balloonX, y: balloonBottomY - balloonH)
        }
    }

    override func draw(_ dirtyRect: NSRect) { }

    func petDidResize() {
        needsLayout = true
    }
}