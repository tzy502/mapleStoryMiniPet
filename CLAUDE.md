# MapleStory MiniPet — Claude Code 上下文

Swift + AppKit 桌面悬浮宠物，显示冒险岛怪物动画。

## 技术栈

- Swift 5.9+ / AppKit / NSPanel
- macOS 13+
- 精灵图：PIL (Python) 生成，水平条带格式
- 冒险岛资源：WZ → XML（WzComparerR2）+ PNG 帧导出

## 核心文件

| 文件 | 用途 |
|------|------|
| `Sources/MiniPet/main.swift` | 全部代码（单文件） |
| `sprites/stand.png` | 待机 16f × 312px |
| `sprites/move.png` | 移动 16f × 312px |
| `sprites/attack1.png` | 攻击 16f × 679px |
| `sprites/skill1.png` | 技能 32f × 887px |

## 架构

```
MiniPet (AppKit)
├── PetPanel (NSPanel, 透明无边框悬浮)
├── ContainerView
│   ├── PetView (精灵图播放器)
│   │   ├── load(): 加载精灵图条 → 裁剪 CGImage 帧
│   │   ├── tick(): 240ms 定时器，逐帧播放
│   │   ├── senseHermes(): 每3s 检查 session 文件
│   │   └── 交互: mouseDown 拖拽, rightMouseDown 菜单
│   └── TerminalView (PTY 内嵌终端，可选)
└── PTYManager (伪终端，运行 hermes --continue)
```

## 编码约定

- 单文件架构，无模块拆分
- 精灵图：水平条带，`frameWidth × count = 总宽`
- 帧裁剪：`cg.cropping(to: CGRect(x: i*fw, y: 0, width: fw, height: cg.height))`
- 动画切换：`switchTo()` → `resize()` → 窗口自适应

## Origin 对齐

精灵图必须使用统一锚点对齐，避免抖动。

**正确方法**：
- WZ 数据可用 → WZ origin（`regenerate_sprites.py --method wz`）
- 无 WZ 数据 → 接地锚点（`regenerate_sprites.py --method ground`）

WZ origin 逻辑参考：`GifUtil.wzImgDetailToGif`
```java
finalOriginX = max(originX); finalOriginY = max(originY);
gc.drawImage(image, finalOriginX - originX, finalOriginY - originY, null);
```

## 精灵图生成

```bash
# 从 WZ XML + 原始帧
python3 regenerate_sprites.py --mob 9602538 --xml foo.img.xml --frames frames_dir/

# 从现有精灵图条重新对齐
python3 refix_strip.py --strip stand.png --frames 16 --frameW 312 --out stand_fixed.png
```

## 相关项目

- `maplestory-wiki-backend` — Java WZ 解析 + GIF 导出  接口文档 /Volumes/docker/hermes/download/api-docs/
- `mxd-spine` — PIXI v4 网页渲染
- `pet-custom-pet` — 另一套精灵图副本
