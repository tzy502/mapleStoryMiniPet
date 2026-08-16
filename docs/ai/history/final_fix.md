# 最终 UI 修复任务

## 问题
当前 SettingsPaperdoll.swift 和 SettingsMaterialPet.swift 的 UI 布局和原型完全不一致。

## 原型参考
浏览器打开 file:///Users/a502/IdeaProjects/mapleStoryMiniPet/prototypes/index.html
点击「纸娃娃桌宠设置」子 tab 查看完整布局。
点击「素材桌宠设置」子 tab 查看完整布局。

## 核心差距

### 纸娃娃桌宠设置 (SettingsPaperdoll.swift)
当前代码的左侧面板缺少：
- 预设 tag 区（4个多彩标签：冒险家/精灵族/剑客/法师 + 新建按钮）
- 装备是8槽网格，但浏览按钮样式不对
- 染色区域缺少开关和滑块

右侧预览区问题：
- 角色预览 Canvas 太小
- 动画开关布局不对
- 缩放控件位置不对
- 背景色卡和原型不一致

### 素材桌宠设置 (SettingsMaterialPet.swift)
左侧面板：
- 收藏列表和最近使用列表的样式和原型不一致
- 搜索框的交互不对

## 完成任务
严格按原型重写这两个文件，确保每个控件的布局、颜色、间距、交互都和原型一致。编译通过后完成。