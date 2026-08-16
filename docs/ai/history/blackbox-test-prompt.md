# MiniPet UI 验证 — 素材预览栏检查清单

## 环境

```bash
export DOTNET_ROOT=/Volumes/SSD/C#
PATH=$DOTNET_ROOT:$PATH
```

## 步骤

1. 编译：`cd /Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet && dotnet build --no-restore`

2. 启动：`cd /Users/a502/IdeaProjects/mapleStoryMiniPet && dotnet run --project MiniPet/MiniPet.csproj`

3. 等待 5 秒让窗口启动

4. 在 PetWindow 上右键 → 点击「设置中心」

5. 确保当前在「素材桌宠设置」标签页（默认就是）

## 检查清单

对每一项，截图并用 vision_analyze 检查，然后回答「是/否」+ 问题描述：

### 1. 垂直对齐
- 截图素材预览区域的整行
- 「路西德」文字和「8880150」输入框是否在同一水平高度？
- 所有按钮（收藏、播放、确定应用）是否和名称在同一水平高度？

### 2. 按钮完整显示
- 截图按钮行
- 「★ 收藏」文字是否完整显示，没有被截断？
- 「▶ 播放」文字是否完整显示？
- 「确定应用」文字是否完整显示？
- 按钮是否被右侧窗口边界折叠/遮挡？

### 3. 按钮顺序
- 按钮从左到右的顺序是什么？
- 「播放」按钮是否在「收藏」按钮前面？
- 「确定应用」是否在最右侧？

### 4. 整体布局
- 缩放滑块行是否正常显示？
- 背景设置色块是否完整显示？
- 整个预览区域是否有元素被截断或溢出？

## 输出

1. 每项检查的截图
2. 每项的回答（是/否 + 问题描述）
3. 如果有问题，说明具体是哪个元素、什么位置、建议怎么改