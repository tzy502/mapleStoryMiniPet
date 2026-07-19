# 严格按原型重构设置页面 UI

## 任务
根据 `/Users/a502/IdeaProjects/mapleStoryMiniPet/prototypes/index.html` 的 macOS 原生风格设计，**完全重构**所有设置页面的 UI 布局。当前代码的 UI 和原型完全不一致，需要全部重做。

## 核心要求

1. **页面展示必须和原型完全一致** — 打开原型 HTML 对照，每个 tab 的布局、间距、控件位置、颜色都要一致
2. **操作逻辑要一致** — 左侧列表点击选中、右侧预览更新、按钮点击回调、弹窗选择
3. **mock 数据覆盖完整** — 每个列表/下拉/表格都有模拟数据可展示
4. **编译通过**

## 关键设计要点

### 整体风格（全文件通用）
- 暗色毛玻璃：`NSVisualEffectView` material = .dark, blendingMode = .withinWindow
- 窗口背景：`ThemeColors.current.winBg` (rgba(30,30,38,.88))
- 分割线：`ThemeColors.current.ntBrd` (rgba(120,120,140,.3))
- 主文字色：`ThemeColors.current.txtPri` (#f0f0f4)
- 次要文字色：`ThemeColors.current.txtSec` (#a0a0b0)
- 强调色：`ThemeColors.current.ntAct` (#5E9CFF)
- 选中背景：`ThemeColors.current.liSel` (rgba(94,156,255,.22))
- 悬停背景：`ThemeColors.current.liHov` (rgba(94,156,255,.12))
- 输入框背景：`ThemeColors.current.inputBg` (rgba(30,30,36,.7))
- 圆角 8px / 12px / 16px
- 所有 icon 使用 emoji 或 SF Symbol

### Tab 1: 素材桌宠设置 (SettingsMaterialPet.swift)
**左侧 panel 240px：**
- 搜索框（圆角8px，放大镜🔍图标在左，placeholder "过滤当前列表…"）
- 分段控件：怪物 / NPC 两个按钮（选中蓝色背景）
- "🔍 浏览素材库"按钮（蓝色强调色，宽度100%）
- ★ 收藏列表（3项mock：路西德/品克缤/狐狸）— 每项显示 icon + 名称 + ID + 星标
- 最近使用列表（3项mock：龙/狐狸/机器人）

**右侧预览区：**
- 预览 Canvas（灰色棋盘格背景，居中显示占位图标）
- 名称 label + ID 输入框
- 操作按钮行：★收藏 / ▶播放 / 📋复制为OBJ / 确定应用
- 缩放滑块（0.3~2.0，显示百分比）
- 背景设置（5个色卡：透明/网格/弓箭手村/市场/森林），点击高亮蓝色边框

### Tab 2: 纸娃娃桌宠设置 (SettingsPaperdoll.swift)
**左侧 panel：**
- 多套纸娃娃预设（4个tag标签：冒险家/精灵族/剑客/法师，多选可toggle，加号新建，✕删除）
- 基础设置：性别♂/♀分段控件、发型ID输入+浏览按钮、脸型ID输入+浏览按钮、皮肤ID输入+浏览按钮
- 装备网格（2列4行共8个槽位）：🎩暗夜帽/👕暗夜上衣/👖暗夜裤子/👟暗夜鞋/⚔暗夜剑/🧣暗夜披风/🛡暗夜盾/🧤暗夜手套，每项显示 icon + 名称 + ID + 🔍浏览按钮
- 坐骑/椅子：下拉选择（mock数据：飞天扫帚/魔法椅/无）
- 染色：开关toggle + 色相滑块 (0-360)

**右侧预览区：**
- 角色预览 Canvas（绘制纸娃娃身体轮廓）
- 动画开关 toggle + 缩放控件(−/⟲/+) + 5种背景色卡 + 确定应用按钮

### Tab 3: 背景设置 (SettingsBackground.swift)
**左侧：** 地图树（三级展开：冒险岛世界→金银岛→射手村区域→射手村/东部/西部，神木村→峡谷入口，埃德尔斯坦→废城广场）+ 自定义地图
**右侧上部：** 地图预览（带桌宠标记 + 地图名称标签）
**右侧下部：** OBJ 列表（可添加/删除/拖拽）+ 下拉选择 + 添加按钮

### Tab 4: 聊天气泡设置 (SettingsBalloon.swift)
**左侧：** 搜索框 + 6个预设卡片（经典蓝#560/暗黑玻璃#3/樱花粉#5/自然绿#50/紫罗兰#100/暖阳橙#200 每个卡片显示颜色缩略条+名称+ID+选中✓标记）+ 底部自定义控件（ID输入、文字颜色选择、气泡颜色选择、应用按钮）
**右侧：** 气泡预览居中（显示宠物名+消息+时间戳）+ 预览测试/隐藏/确定应用按钮

### 素材选择器弹窗 (MaterialPicker.swift)
- 标题跟随场景："选择怪物" / "选择发型" / "选择帽子" 等
- 搜索框 + 类型筛选 chip + 分页列表 + 详情预览
- 选中后通过回调返回 (id, name)
- 使用 mock 数据（80条随机生成）
- 取消按钮必须可关闭弹窗

## 颜色映射表
原型 CSS 变量 → AppKit 代码

| CSS 变量 | ThemeColors 属性 | 暗色值 |
|----------|-----------------|--------|
| --win-bg | winBg | rgba(30,30,38,.88) |
| --win-brd | winBrd | rgba(80,80,90,.6) |
| --txt-pri | txtPri | #f0f0f4 |
| --txt-sec | txtSec | #a0a0b0 |
| --txt-dim | txtDim | #707080 |
| --txt-mute | txtMute | rgba(255,255,255,.15) |
| --nt-bg | ntBg | rgba(28,28,36,.6) |
| --nt-brd | ntBrd | rgba(120,120,140,.3) |
| --nt-act | ntAct | #5E9CFF |
| --li-sel | liSel | rgba(94,156,255,.22) |
| --li-hov | liHov | rgba(94,156,255,.12) |
| --input-bg | inputBg | rgba(30,30,36,.7) |
| --input-brd | inputBrd | rgba(120,120,140,.3) |
| --input-brd-focus | inputBrdFocus | #5E9CFF |
| --btn-pr | btnPr | #5E9CFF |
| --btn-sc-bg | btnScBg | rgba(60,60,70,.5) |

## 不要修改的文件
- AppDelegate.swift（退出逻辑已正确）
- SettingsCore.swift（主题系统）
- SettingsManager.swift（数据模型）
- SettingsAI.swift（已有完善）
- SettingsChatLog.swift（已有完善）
- SettingsBackground.swift（已有完善）

## 完成后运行
swift build 编译通过

## 实现方式
每次只修改一个文件，完整写入全部代码，确保和原型一致后再编译验证。