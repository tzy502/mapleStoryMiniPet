根据原型改造 聊天气泡设置 页面 (SettingsBalloon.swift)

原型参考：/Users/a502/IdeaProjects/mapleStoryMiniPet/prototypes/index.html（在浏览器打开查看"聊天气泡设置"子 tab）

具体要求：
1. 左侧 panel：搜索框 + 6个预设卡片（经典蓝#560/暗黑玻璃#3/樱花粉#5/自然绿#50/紫罗兰#100/暖阳橙#200 每个卡片显示颜色缩略条+名称+ID+选中✓标记）+ 底部自定义控件（ID输入、文字颜色选择、气泡颜色选择、应用按钮）
2. 右侧 preview：气泡预览居中（显示宠物名+消息+时间戳）+ 预览测试/隐藏/确定应用按钮
3. 整个页面暗色毛玻璃 macOS 风格，使用 ThemeColors.current 变量
4. 编译通过且可运行

现有文件：/Users/a502/IdeaProjects/mapleStoryMiniPet/Sources/MiniPet/SettingsBalloon.swift
其他已设文件：SettingsCore.swift（主题系统）

不要修改非目标文件。