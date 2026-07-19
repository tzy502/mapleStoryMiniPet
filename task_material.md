根据原型改造 素材桌宠设置 页面 (SettingsMaterialPet.swift)

原型参考：/Users/a502/IdeaProjects/mapleStoryMiniPet/prototypes/index.html（在浏览器打开查看"素材桌宠设置"子 tab）

具体要求：
1. 左侧 panel（240px）：搜索框 + 怪物/NPC 分段切换 + "🔍浏览素材库"按钮 + ★收藏列表（3项）+ 最近使用列表（3项）
2. 右侧 preview：预览区（用 MaterialCanvasView 的 checkerboard/gradient 绘制5种背景） + 名称 label + ID 输入框 + ★收藏按钮 + ▶播放按钮 + 📋复制OBJ按钮 + 确定应用按钮 + 缩放滑块 + 5种背景色卡（透明/网格/弓箭手村/市场/森林）
3. 整个页面暗色毛玻璃 macOS 风格，使用 ThemeColors.current 变量
4. 数据先用 mock：收藏3项+最近3项
5. 点击"🔍浏览素材库"弹出素材选择器（使用 MaterialPickerViewController，展示怪物+NPC 类型 mock 数据）
6. 编译通过且可运行

素材选择器 MaterialPickerViewController 定义在 MaterialPicker.swift（如果不存在则先创建它）
现有文件：/Users/a502/IdeaProjects/mapleStoryMiniPet/Sources/MiniPet/SettingsMaterialPet.swift
其他已设文件：SettingsCore.swift（主题系统）、SettingsManager.swift（数据模型）

不要修改非目标文件。