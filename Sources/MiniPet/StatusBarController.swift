import AppKit

// MARK: - Status Bar Controller

class StatusBarController: NSObject, NSMenuDelegate {
    private var statusItem: NSStatusItem!
    weak var petView: PetView?

    init(petView: PetView) {
        self.petView = petView
        super.init()
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        statusItem.button?.title = "MiniPet"
        statusItem.button?.font = NSFont.monospacedDigitSystemFont(ofSize: 12, weight: .medium)
        let menu = buildMenu(petView)
        menu.delegate = self
        statusItem.menu = menu
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        rebuildAnimSubmenu()
        rebuildMobSubmenu()
    }

    private func rebuildAnimSubmenu() {
        guard let pv = petView else { return }
        for item in statusItem.menu?.items ?? [] {
            if item.title == "切换动画", let sub = item.submenu {
                sub.removeAllItems()
                for name in pv.strips.keys.sorted() {
                    let mi = NSMenuItem(title: name, action: #selector(PetView.menuSwitchAnimation(_:)), keyEquivalent: "")
                    mi.target = pv
                    sub.addItem(mi)
                }
                sub.addItem(.separator())
                let sd = NSMenuItem(title: "设为默认动画", action: #selector(PetView.setDefaultAnim(_:)), keyEquivalent: "")
                sd.target = pv
                sub.addItem(sd)
                break
            }
        }
    }

    private func rebuildMobSubmenu() {
        guard let pv = petView else { return }
        for item in statusItem.menu?.items ?? [] {
            if item.title == "切换怪物", let sub = item.submenu {
                sub.removeAllItems()
                for mob in pv.mobList {
                    let mi = NSMenuItem(title: "\(mob.name) (\(mob.code))",
                                        action: #selector(PetView.menuSwitchMob(_:)), keyEquivalent: "")
                    mi.target = pv
                    mi.representedObject = mob.code
                    if mob.code == pv.mobId { mi.state = .on }
                    let rn = NSMenuItem(title: "重命名", action: #selector(PetView.renameMob(_:)), keyEquivalent: "")
                    rn.target = pv; rn.representedObject = mob.code
                    let dl = NSMenuItem(title: "删除", action: #selector(PetView.deleteMob(_:)), keyEquivalent: "")
                    dl.target = pv; dl.representedObject = mob.code
                    let s = NSMenu(); s.addItem(rn); s.addItem(dl)
                    mi.submenu = s
                    sub.addItem(mi)
                }
                sub.addItem(.separator())
                let add = NSMenuItem(title: "添加怪物…", action: #selector(PetView.addMob), keyEquivalent: "n")
                add.target = pv; sub.addItem(add)
                break
            }
        }
    }

    func refresh() {
        guard let pv = petView else { return }
        statusItem.button?.title = "🐾 \(pv.mobName)"
    }

    @objc func openSettings() {
        SettingsWindowController.show()
    }

    private func buildMenu(_ pv: PetView) -> NSMenu {
        let menu = NSMenu()

        let animMenu = NSMenu()
        animMenu.addItem(NSMenuItem(title: "加载中…", action: nil, keyEquivalent: ""))
        let animParent = NSMenuItem(title: "切换动画", action: nil, keyEquivalent: "")
        animParent.submenu = animMenu
        menu.addItem(animParent)

        let mobMenu = NSMenu()
        mobMenu.addItem(NSMenuItem(title: "加载中…", action: nil, keyEquivalent: ""))
        let mobParent = NSMenuItem(title: "切换怪物", action: nil, keyEquivalent: "")
        mobParent.submenu = mobMenu
        menu.addItem(mobParent)

        menu.addItem(.separator())
        let settingsItem = NSMenuItem(title: "设置中心…", action: #selector(openSettings), keyEquivalent: ",")
        settingsItem.target = self
        menu.addItem(settingsItem)
        menu.addItem(.separator())
        let centerItem = NSMenuItem(title: "居中", action: #selector(PetView.centerOriginOnScreen), keyEquivalent: "c")
        centerItem.target = pv
        menu.addItem(centerItem)
        menu.addItem(.separator())
        menu.addItem(NSMenuItem(title: "退出 MiniPet", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q"))

        return menu
    }
}