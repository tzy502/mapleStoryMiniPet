# 设置页面 UI 改造 —— Claude Code 任务

## 目标
根据 `/Users/a502/IdeaProjects/mapleStoryMiniPet/prototypes/index.html` 的 macOS 原生风格 UI 设计，改造 Swift AppKit 设置页面的 UI/布局，**不动逻辑层**。

## 改造原则

1. **只改 UI/布局** — 不修改数据模型、API 调用、持久化逻辑、通知机制
2. **数据先用 mock** — 不依赖后端 API，用本地假数据展示
3. **编译要过** — 改完后 `swift build` 无报错
4. **可运行可操作** — 所有按钮可点击，弹窗可打开，tab 可切换

## 原型参考

打开 `/Users/a502/IdeaProjects/mapleStoryMiniPet/prototypes/index.html` 在浏览器中查看效果。关键设计要素：

### 整体风格
- 深色/白天双主题（右上角 toggle）
- 窗口圆角 16px，毛玻璃效果
- 一级 Tab 栏（桌宠设置 / AI 设置 / 对话记录），下标指示器
- 桌宠设置下有 4 个子 Tab 栏（素材桌宠设置 / 纸娃娃桌宠设置 / 背景设置 / 聊天气泡设置）

### Tab 1: 素材桌宠设置
- 左侧 list（240px）：搜索框 + 怪物/NPC 分段切换 + "🔍 浏览素材库"按钮 + 收藏列表 + 最近使用列表
- 右侧：预览区（带背景切换卡片）+ 名称/ID/收藏/播放/复制 OBJ/缩放/确定应用
- 列表项：icon + 名称 + ID + 星标

### Tab 2: 纸娃娃桌宠设置
- 左侧：多套纸娃娃预设（多选 tag）+ 基础（性别/发型/脸型/皮肤）+ 装备（8个槽位：帽子/上衣/裤子/鞋/武器/披风/盾牌/手套）+ 坐骑/椅子 + 染色
- 右侧：角色预览 + 动画开关 + 缩放 + 背景切换 + 确定应用
- 每个装备槽显示 icon + 名称 + ID + "🔍 浏览"按钮

### Tab 3: 背景设置
- 左侧：地图树（三级展开：冒险岛世界→金银岛→射手村区域→射手村/东部/西部，神木村→峡谷入口，埃德尔斯坦→废城广场）+ 自定义地图
- 右侧上部：地图预览（带桌宠标记 + 地图名称标签）
- 右侧下部：OBJ 列表（可添加/删除/拖拽）+ 下拉选择 + 添加按钮

### Tab 4: 聊天气泡设置
- 左侧：搜索框 + 6 个预设卡片（经典蓝/暗黑玻璃/樱花粉/自然绿/紫罗兰/暖阳橙，每个有颜色预览+名称+ID）+ 自定义控件（ID/文字颜色/气泡颜色）
- 右侧：气泡预览（宠物名+消息+时间戳）+ 预览测试/隐藏/确定应用

### AI 设置/Pane 1
- 连接状态卡（绿色圆点 + 名称 + 状态 + 模型标签 + 活动指标条 + 断开按钮）
- 指令规则表（4条预设：关键词/动画/延迟/优先级/启用/操作）
- 最近对话（4条日志）
- 对话外观（主题/字体/消息对齐/透明度）
- 快捷键（呼出/隐藏）

### 对话记录/Pane 2
- 左侧 session 列表（4个 session，显示名称/日期/条数/摘要，蓝色选中指示器）
- 右侧 session 详情（4条对话气泡：你/AI 交替，头像+消息+时间戳）

## 现有代码结构

### 主要文件
- `Sources/MiniPet/SettingsCore.swift` — 主题色系统、窗口框架、标题栏、顶部 Tab 栏
- `Sources/MiniPet/SettingsMaterialPet.swift` — 素材桌宠设置子视图（拆分中）
- `Sources/MiniPet/SettingsPaperdoll.swift` — 纸娃娃桌宠设置子视图
- `Sources/MiniPet/SettingsBackground.swift` — 背景设置子视图
- `Sources/MiniPet/SettingsBalloon.swift` — 聊天气泡设置子视图
- `Sources/MiniPet/SettingsAI.swift` — AI 设置子视图
- `Sources/MiniPet/SettingsChatLog.swift` — 对话记录子视图
- `Sources/MiniPet/SettingsManager.swift` — 数据模型 + 持久化

### 现有数据模型（PetSettings）
- mobId, entityType, mapId, theme, balloonId, reactionRules, paperdollPresets, objList, favoriteMobIds, chatShortcutOpen, chatTheme, chatFontSize, chatOpacity, voiceEnabled 等

### 主题系统
- `ThemeColors` 结构体，dark/light 两套完整色值，通过 `Notification.Name.themeDidChange` 通知
- `SettingsTitleBar` 右上角有暗黑/白天 toggle

## 需要改动的具体文件

### SettingsMaterialPet.swift
- 重新布局左侧列表部分（搜索框 + 分段控件 + 浏览按钮 + 收藏/最近使用列表）
- 重新布局右侧预览部分（预览区 + 操作栏 + 缩放 + 背景切换卡片）
- 列表项改用 MaterialItemView (icon + name + code + star)
- 添加"🔍 浏览素材库"按钮（点击弹出简易素材选择弹窗，展示 mock 数据列表）
- 添加背景切换卡片（透明/网格/弓箭手村/市场/森林 5种）

### SettingsPaperdoll.swift
- 重新布局左侧：预设 tag 区 + 基础设置（性别/发型/脸型/皮肤）+ 装备网格（8槽）+ 坐骑/椅子 + 染色
- 重新布局右侧：预览区 + 动画开关 + 缩放控件 + 背景切换 + 确定应用
- 每个装备槽添加"🔍 浏览"按钮（点击弹出简易素材选择弹窗，只展示对应类型的 mock 数据）
- 发型/脸型/皮肤/坐骑/椅子也支持浏览按钮弹出对应类型的素材选择弹窗

### SettingsBackground.swift
- 重新布局左侧：地图树（三级展开/折叠 + 自定义地图项）
- 重新布局右侧上部：地图预览（带桌宠标记 + 地图名称标签）
- 重新布局右侧下部：OBJ 列表（可添加/删除/拖拽）+ 下拉选择 + 添加按钮

### SettingsBalloon.swift
- 重新布局左侧：搜索框 + 6 个预设卡片（颜色预览+名称+ID+选中标记）+ 自定义控件（ID/文字颜色/气泡颜色）
- 重新布局右侧：气泡预览（宠物名+消息+时间戳）+ 预览测试/隐藏/确定应用

### SettingsAI.swift
- 重新布局：连接状态卡 + 指令规则表 + 最近对话 + 对话外观 + 快捷键

### SettingsChatLog.swift
- 重新布局：左右分栏。左侧 4 个 session 列表（名称/日期/条数/摘要），右侧选中 session 的对话气泡列表

## 素材选择器弹窗组件

创建一个复用的 `MaterialPickerPopover` 或 `MaterialPickerWindowController`：
- 标题跟随场景（"选择怪物" / "选择发型" / "选择帽子" 等）
- 搜索框 + 类型筛选 chip + 分页列表 + 详情预览
- 选中后通过回调返回 (id, name)
- 使用 mock 数据（80 条随机生成）

## 配色规范（CSS 变量 → AppKit 主题色变量）

| 用途 | 深色 | 白天 |
|------|------|------|
| 窗口背景 | winBg: rgba(30,30,38,.88) | rgba(245,247,252,.95) |
| 窗口边框 | winBrd: rgba(80,80,90,.6) | rgba(200,205,220,.7) |
| 主文字 | txtPri: #f0f0f4 | #1a1e2e |
| 次要文字 | txtSec: #a0a0b0 | #5a5e70 |
| 暗淡文字 | txtDim: #707080 | #8a8e9e |
| 强调色 | ntAct: #5E9CFF | #4A7FD4 |
| 输入框背景 | inputBg: rgba(30,30,36,.7) | rgba(245,247,252,.85) |
| 列表选中 | liSel: rgba(94,156,255,.22) | rgba(74,127,212,.18) |
| 列表悬停 | liHov: rgba(94,156,255,.12) | rgba(74,127,212,.1) |
| 主要按钮 | btnPr: #5E9CFF | #4A7FD4 |
| 次要按钮 | btnScBg: rgba(60,60,70,.5) | rgba(220,225,240,.6) |

## 预览区背景（MaterialCanvasView 使用）

5 种背景模式：
- `transparent` — 棋盘格
- `grid` — 8px 网格线
- `henesy` — 弓箭手村：绿色渐变
- `market` — 市场：暖棕色渐变
- `forest` — 森林：暗绿色渐变

## 必须完成的工作

1. 把所有设置子视图的 UI 布局改为与原型一致
2. 所有数据先用 mock（静态数组、本地生成），不依赖 API
3. 所有按钮/开关/输入框可正常交互
4. 素材选择器弹窗组件可在各场景中正确打开
5. 背景设置的地图树可展开/折叠
6. 对话记录的 session 列表可点击切换
7. 设置页面可正常打开、关闭、切换 tab
8. 编译通过，无警告

## 不要做的事

- 不要改数据模型（PetSettings、PaperdollPreset 等）
- 不要改主题系统（ThemeColors、SettingsTitleBar 等）
- 不要改顶部 Tab 栏和窗口框架
- 不要改后端 API 调用
- 不要改持久化逻辑