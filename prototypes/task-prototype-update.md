# 原型补充任务

## 任务说明
基于当前 prototypes/index.html，补充以下缺失的原型页面。**不修改现有内容**，只新增缺失的。

## 需要补充的页面

### 1. PetWindow 原型（新建文件 prototypes/pet-window.html）
宠物主窗口的设计，包含：
- 透明无边框窗口示意
- 精灵图画布（SpriteCanvas）
- 聊天气泡（BalloonControl）在头顶上方
- 地图背景（BackgroundControl）在宠物下方
- 右键菜单内容（切换人物三级菜单、切换动画、切换背景、切换气泡、切换椅子/骑宠、Hermes 终端、设置中心、退出）

### 2. WZ 配置弹窗（在现有 index.html 中新增）
- 点击「WZ 配置」按钮弹出
- wzlib 路径: [输入框] [浏览] 按钮
- BaseWZ 路径: [输入框] [浏览] 按钮
- [保存] [取消] 按钮

### 3. Hermes 对话窗口原型（新建文件 prototypes/hermes-chat.html）
- 纯聊天界面
- 消息列表（左侧用户灰色、右侧 AI 蓝色）
- 输入框 + 发送按钮
- 连接状态指示器（绿点/红点）
- 窗口属于 Hermes 面板，不是设置面板

### 4. 素材浏览弹窗 三栏布局（新建文件 prototypes/material-browser.html）
- 左侧：分类树（怪物/NPC/装备/发型/脸型/坐骑/椅子）
- 中间：素材列表（名称 + ID + 缩略图，分页）
- 右侧：详情预览（预览图 + 名称 + ID + 类型 + [选择此素材] 按钮）
- 顶部：搜索栏 + 类型筛选

## 风格要求
- 保持现有 prototypes/index.html 的 macOS 暗色毛玻璃风格一致
- 标题栏红黄绿按钮 + 标题
- 单文件 HTML，所有 CSS 在 <style>，所有 JS 在 <script>