修复以下已知 bug，编译通过后完成：

## Bug 1: 设置页面的二级弹窗无法正常关闭
- 文件：MaterialPicker.swift
- 问题：素材选择器弹窗打开后，"取消"按钮可能无法关闭弹窗
- 修复要求：确保 cancel 按钮的 action 能正确调用 dismiss 关闭弹窗
- 如果 MaterialPicker.swift 不存在，从 git stash 恢复它，或者直接重建

## Bug 2: 菜单展示有问题
- 文件：StatusBarController.swift
- 问题：右键菜单和状态栏菜单的某些 menu item 的 target/action 可能未正确连接
- 修复要求：检查所有 NSMenuItem 的 target 和 action 是否正确设置，尤其 '设置中心…' 和 '退出 MiniPet'

## Bug 3: 设置窗口 close 逻辑
- 文件：SettingsCore.swift
- 确保 SettingsWindowController 的 close() 方法调用 destroyShared()
- 检查 deinit 是否正确移除通知观察者

## 不要改的文件
- AppDelegate.swift（退出逻辑已简化完毕）
- PetView.swift（右键菜单不需要动）
- SettingsMaterialPet.swift, SettingsBalloon.swift, SettingsPaperdoll.swift, SettingsChatLog.swift（UI 改造已完毕）

完成后运行 `swift build` 确认编译通过。