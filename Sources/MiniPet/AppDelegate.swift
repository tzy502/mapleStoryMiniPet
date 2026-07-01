import AppKit

// MARK: - App Delegate

class AppDelegate: NSObject, NSApplicationDelegate {
    var hermes: HermesClient?
    weak var petView: PetView?

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard let pv = petView else {
            hermes?.terminate()
            return .terminateNow
        }
        let dieAnim: String? = pv.strips["die1"] != nil ? "die1"
            : pv.strips.keys.sorted().first(where: { $0.hasPrefix("die") })
        guard let die = dieAnim else {
            hermes?.terminate()
            return .terminateNow
        }
        pv.switchTo(die)
        let dieFrames = pv.images[die]?.count ?? pv.strips[die]?.count ?? 1
        let dieDuration = TimeInterval(dieFrames) * 0.12 + 0.5
        DispatchQueue.main.asyncAfter(deadline: .now() + dieDuration) {
            sender.reply(toApplicationShouldTerminate: true)
        }
        return .terminateLater
    }

    func applicationWillTerminate(_ notification: Notification) {
        hermes?.terminate()
    }
}
