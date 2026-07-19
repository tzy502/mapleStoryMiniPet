# MiniPet v1.0 — 开发阶段

## 目标
从零搭建 C# Avalonia 项目，实现 P0 功能：桌面悬浮显示、R2 wzlib 素材获取、精灵图动画、聊天气泡、动画行为逻辑、右键菜单。

## 环境
- 项目路径：`/Users/a502/IdeaProjects/mapleStoryMiniPet`
- SDK 在 SSD：`/Volumes/SSD/dotnet` 或系统 `/usr/local/share/dotnet`
- 使用 `dotnet new avalonia` 初始化

## 前提
- 需求文档：`docs/requirements-analysis.md`
- 设计文档：`docs/software-design.md`
- 原型：`prototypes/` 目录

## 开发顺序
1. 初始化 Avalonia 项目 + 项目结构
2. 数据模型 (Models/)
3. WzService + ConfigService + CacheManager
4. SpriteService + AnimService
5. PetWindow + SpriteCanvas + 右键菜单
6. BalloonService + BalloonControl
7. 集成测试