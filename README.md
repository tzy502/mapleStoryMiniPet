# MapleStory MiniPet

冒险岛桌面悬浮宠物，基于 .NET 9 + Avalonia UI 实现。

## 功能

- 透明无边框悬浮窗，桌面置顶
- 精灵图动画播放：stand / move / fly / attack / skill / say / mouse / hand / laugh / eye / die
- 左键拖拽移动，右键菜单切换动画 / 怪物 / 背景 / 气泡
- 纸娃娃系统：装备渲染、z-order 排序
- 聊天气泡：9-slice 渲染、淡入淡出
- 地图背景：图层合成、视差偏移
- 设置中心：WZ 路径配置、主题切换、素材浏览
- Hermes 内嵌终端（可选）
- macOS / Windows 双平台

## 快速启动

```bash
# 编译
dotnet build MiniPet/MiniPet.csproj

# 运行
dotnet run --project MiniPet/MiniPet.csproj
```

## 项目结构

```
mapleStoryMiniPet/
├── MiniPet/                     # 主项目
│   ├── Models/                  # 数据模型
│   │   ├── AppConfig.cs         # 应用配置
│   │   ├── BalloonData.cs       # 聊天气泡数据
│   │   ├── CharacterAppearance.cs # 纸娃娃装备模型
│   │   ├── MapData.cs           # 地图数据
│   │   ├── SpriteSheet.cs       # 精灵图配置
│   │   └── SpriteStrip.cs       # 精灵图条 + 帧数据
│   ├── Services/                # 服务层
│   │   ├── AnimService.cs       # 动画状态机、帧循环
│   │   ├── BalloonService.cs    # 气泡 9-slice 渲染
│   │   ├── CacheManager.cs      # 缓存管理
│   │   ├── ConfigService.cs     # 设置持久化
│   │   ├── EventBus.cs          # 事件总线
│   │   ├── MapService.cs        # 地图加载、图层合成
│   │   ├── PaperdollService.cs  # 角色合成、z-order
│   │   ├── SpriteService.cs     # 精灵图管理、帧缓存
│   │   ├── TrayService.cs       # 托盘图标（macOS 静默降级）
│   │   ├── WzError.cs           # WZ 错误定义
│   │   └── WzService.cs         # WZ 加载、节点查询、PNG 提取
│   ├── Views/                   # 视图层
│   │   ├── PetWindow.axaml.cs   # 主窗口
│   │   ├── SettingsWindow.axaml.cs # 设置面板
│   │   ├── MaterialBrowserWindow.cs # 素材浏览
│   │   └── Controls/            # UI 控件
│   │       ├── SpriteCanvas.cs  # 精灵渲染画布
│   │       ├── BalloonControl.cs # 聊天气泡
│   │       └── BackgroundControl.cs # 地图背景
│   ├── App.axaml(.cs)           # 应用定义
│   ├── Program.cs               # 启动入口
│   └── MiniPet.csproj           # 项目文件
├── MiniPet.Tests/               # 测试项目
│   ├── ConfigServiceTests.cs
│   ├── EventBusTests.cs
│   ├── WzServiceTests.cs
│   ├── SpriteServiceTests.cs
│   ├── AnimServiceTests.cs
│   ├── BalloonServiceTests.cs
│   ├── MapServiceTests.cs
│   └── PaperdollServiceTests.cs
├── sprites/                     # 精灵图
├── fetch_and_generate.py        # API 帧下载 + 精灵图生成
├── regenerate_sprites.py        # WZ origin / 接地锚点精灵图生成
├── refix_strip.py               # 精灵图重新对齐
├── README.md
└── CLAUDE.md                    # Claude Code 上下文
```

## 技术栈

- .NET 9
- Avalonia UI（跨平台 UI 框架）
- SkiaSharp（2D 渲染）
- WzComparerR2.WzLib（WZ 文件解析）
- xUnit（单元测试）

## 编码约定

- Services 所有方法与事件处理使用 try-catch 包裹
- 禁止使用 `System.Drawing`（macOS 不支持）
- 禁止使用 `System.Windows.Forms`
- 窗口透明属性：`Background = Brushes.Transparent` / `TransparencyLevelHint = Transparent` / `SystemDecorations = None`
- 动画顺序：stand → say → mouse → move → hand → laugh → eye → fly → die，缺失 fallback 到 stand
