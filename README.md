# MapleStory MiniPet

冒险岛怪物桌面悬浮宠物，基于 Swift + AppKit 原生实现。

## 功能

- 透明无边框悬浮窗，置顶于桌面
- 4 种动画：stand / move / attack1 / skill1
- 左键拖拽移动，右键菜单切换动画
- 感知 Hermes Agent 活动，自动切换到 move 动画
- 内嵌终端（可选），可直接与 Hermes 对话

## 快速启动

```bash
./start.sh          # 启动（如已构建）
./start.sh --build  # 构建并启动
```

## 项目结构

```
mapleStoryMiniPet/
├── start.sh                  # 启动脚本
├── Package.swift             # Swift Package 配置
├── Sources/MiniPet/main.swift # 主程序
├── sprites/                  # 精灵图（固定 origin 对齐）
│   ├── stand.png             #   16帧 × 312px
│   ├── move.png              #   16帧 × 312px
│   ├── attack1.png           #   16帧 × 679px
│   └── skill1.png            #   32帧 × 887px
├── regenerate_sprites.py     # 精灵图生成工具（WZ origin / 接地锚点）
├── refix_strip.py            # 精灵图重新对齐工具
├── README.md
├── REQUIREMENTS.md           # 需求文档
├── DEBUG.md                  # 调试指南
└── CLAUDE.md                 # Claude Code 上下文
```

## 精灵图

精灵图使用**接地锚点**对齐，消除动画抖动。详见 `REQUIREMENTS.md` 和 `regenerate_sprites.py`。

当前精灵图来自冒险岛怪物 8880150（蝴蝶精）。

## 交互

| 操作 | 效果 |
|------|------|
| 左键拖拽 | 移动位置 |
| 右键 → 动画名 | 切换动画 |
| 右键 → 💬 内嵌终端 | 打开/关闭 Hermes 终端 |
| 右键 → ✕ 关闭 | 退出 |

## 依赖

- macOS 13+
- Swift 5.9+
- Python 3 + Pillow（精灵图生成工具）
