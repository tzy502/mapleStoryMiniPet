根据原型改造 纸娃娃桌宠设置 页面 (SettingsPaperdoll.swift)

原型参考：/Users/a502/IdeaProjects/mapleStoryMiniPet/prototypes/index.html（在浏览器打开查看"纸娃娃桌宠设置"子 tab）

具体要求：
1. 左侧 panel（240px）：多套纸娃娃预设 tag（多选可删可新建）+ 基础（性别♂/♀分段控件、发型/脸型/皮肤 ID 输入框+🔍浏览按钮）+ 装备网格（2列4行共8槽：🎩帽子/👕上衣/👖裤子/👟鞋/⚔武器/🧣披风/🛡盾牌/🧤手套 每项显示 icon + 名称 + ID + 🔍浏览按钮）+ 坐骑/椅子（可点击弹出选择器）+ 染色（开关+色相滑块）
2. 右侧 preview：角色预览区 + 动画开关 + 缩放(−/⟲/+) + 5种背景色卡（透明/网格/弓箭手村/市场/森林）+ 确定应用按钮
3. 整个页面暗色毛玻璃 macOS 风格，使用 ThemeColors.current 变量
4. 数据先用 mock：4个预设 + 每个装备槽 mock 数据
5. 点击"🔍浏览"弹出简易素材选择弹窗（使用 MaterialPickerViewController 展示对应类型 mock 数据）— 这个类在 MaterialPicker.swift 里，如果不存在先创建它
6. 编译通过且可运行

现有文件：/Users/a502/IdeaProjects/mapleStoryMiniPet/Sources/MiniPet/SettingsPaperdoll.swift
其他已设文件：SettingsCore.swift（主题系统）、SettingsManager.swift（数据模型）

不要修改非目标文件。