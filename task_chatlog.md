根据原型改造 对话记录 页面 (SettingsChatLog.swift)

原型参考：/Users/a502/IdeaProjects/mapleStoryMiniPet/prototypes/index.html（在浏览器打开查看"对话记录" tab）

具体要求：
1. 页面内容分3个卡片区域：
   - 快捷键卡片：呼出(⌘+⇧+Space 录制按钮)、隐藏(Esc)
   - 外观卡片：主题(跟随系统/浅色/深色)、字体、消息对齐(左右/居中)、透明度滑块
   - 语音输入卡片：开关、引擎选择、语言选择
2. 每个卡片使用 .hc 类样式（圆角+半透明背景）
3. 所有控件使用 ThemeColors.current 变量
4. 编译通过且可运行

现有文件：/Users/a502/IdeaProjects/mapleStoryMiniPet/Sources/MiniPet/SettingsChatLog.swift
其他已设文件：SettingsCore.swift（主题系统）、SettingsManager.swift（数据模型）

不要修改非目标文件。