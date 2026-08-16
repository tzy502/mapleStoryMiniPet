# MapleStory MiniPet 🐱

**冒险岛桌面悬浮宠物** —— 透明置顶的迷你伙伴，可以换装、说话、攻击展示，还能把冒险岛的地图挂在桌面上。

数据直接读取本地 WZ 客户端文件，**完全离线可用**。MIT 协议开源。

> **V0.01** — 首个可发布版本

## ✨ 功能简介

### 🧍 纸娃娃（角色换装）
- 透明无边框置顶悬浮窗，鼠标左键拖拽移动
- 空闲随机动画：**站姿 + 攻击（武器展示）动作**随机切换（swingO/T/P、stab、shoot），播完回归站姿——对齐冒险岛 mob 行为
- 分层拼装：身体 / 发型 / 脸型 / 耳朵 + 皮肤（中文名）/ 帽子 / 披风 / 上衣 / **套服** / 裤子 / 鞋子 / 手套 / 武器 / 盾牌 / **面饰(101) / 眼饰(102) / 耳环(103) 三槽可同穿** / 坐骑 / 椅子
- 装备特效：`Effect/ItemEff.img` 全局特效表（冰凌披风、神之子头冠等 18 帧循环动画）
- 发型 17337 个全量加载，中文名检索（性别不限）
- 染发调色（HSV 色相/饱和度/亮度）、预设管理（新建/保存/覆盖/加载/重命名/复制）

### 💬 聊天气泡
- 角色头顶随机冒泡（9-slice 样式），淡入淡出
- 纸娃娃 / 怪物 / NPC 三种锚定规则，长文本自适应

### 🗺️ 地图背景（两种模式）
- **静态地图**：整张地图作为桌面背景（裁紧透明边距、落地对齐、点击穿透）
- **摄像机模式**：拖动 / 缩放（0.25~4 倍）地图窗口，背景自动视差滚动
- 区域树选择器（世界 → 区域 → 地图）、图层开关、裁切设置

### 🧰 素材浏览
- 怪物 / NPC / 纸娃娃部件全量浏览（分类树：👾 素材组 + 🧍 纸娃娃组）
- 中文名检索、分页跳转、穿身上实时预览、应用到主窗口

### 🤖 Agent 观察（可选）
- 订阅 DSH / Hermes / Claude Code / Codex 实时状态，桌宠以气泡/动作反映 agent 干活过程
- **Windows 纯净版（NO_AI 编译）不含任何 AI 功能，纯桌宠即开即用**

## 🚀 快速开始

### 环境要求
- .NET 9 SDK（构建）/ 运行时可免装（Windows 纯净版为 self-contained 单文件）
- 冒险岛客户端 WZ 数据目录（`Character.wz` / `Mob.wz` / `Map.wz` 等拆包文件）

### 编译运行

```bash
# 编译（macOS 构建加 -p:UsedAvaloniaProducts= 跳过遥测分析）
dotnet build MiniPet/MiniPet.csproj -c Debug -p:UsedAvaloniaProducts=

# 运行（macOS 桌面需保持显示器唤醒：caffeinate -dimsu）
dotnet run --project MiniPet/MiniPet.csproj
```

### 首次配置

1. 启动后右键桌宠 → 设置中心（**未配置 WZ 时启动自动打开设置页引导**）
2. 配置 **WZ 库路径**（`WzComparerR2.WzLib.dll`）
3. 配置 **WZ 数据路径**（客户端 Base/Data 目录，含 `Base.wz` / `Character.wz` / `Map.wz` 等）
4. 保存后自动加载，开始使用

地图缓存到本地（macOS `~/Library/Caches/MiniPet`，Windows `%LOCALAPPDATA%\MiniPet\cache`），二次加载秒开。

### Windows 纯净版（绿色便携）

- `MiniPet.exe` 单文件 self-contained（免装 .NET），`NO_AI` 编译无任何 AI 功能
- **配置与日志与 exe 同目录**（`settings.json` / `wz-config.json` / `MiniPet.log`）——整个文件夹拷走即带走配置
- 桌宠不进任务栏，托盘图标常驻；设置中心正常显示
- 首次启动未配置 WZ 自动打开设置页

## 💡 操作提示

- **左键拖拽**：移动桌宠
- **右键**：打开菜单（切换动画 / 人物 / 背景模式 / 气泡 / DSH / 设置）
- 地图摄像机模式：左键拖相机、滚轮缩放、右键拖窗口位置

## 🖥 支持平台

- macOS（Dock 不显示桌宠，设置中心正常）
- Windows 10/11 x64

## 📚 参考与致谢

本项目参考/借鉴了以下开源项目（算法与数据结构对齐其实现，代码为独立重写）：

| 项目 | 用途 | 地址 |
|---|---|---|
| **MapleSalon2** | 纸娃娃渲染参考 | https://github.com/spd789562/MapleSalon2 |
| **WzComparerR2** | WZ 解析库与地图渲染参考 | https://github.com/MapleStoryMaple/WzComparerR2 |

## ⚠️ 版权声明

本项目（代码部分）以 MIT 协议开源，**仅用于学习与研究**。所有游戏素材（角色、地图、WZ 数据）版权归 **Nexon** 及冒险岛官方所有，请勿将游戏素材用于商业用途；发布/分发时请确保遵守当地法律与官方条款。
