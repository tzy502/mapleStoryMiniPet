# MapleStory MiniPet — Claude Code 上下文

C# / Avalonia 桌面悬浮宠物，显示冒险岛角色（Paperdoll 拼装）+ WZ 地图背景。支持 WZ 本地解析、多角色/怪物切换、静态/摄像机双模式地图。

## 技术栈

- C# / .NET 9 / Avalonia 12.1 / SkiaSharp 3.119（桌面宠物，透明顶层窗口）
- WzComparerR2.WzLib（WZ 数据：`FindNodeByPath` / `ExtractPng` / `GetOrigin` / `GetIntProperty` / `GetStringProperty`，全在 `WzService.WithWzLock` 锁内）
- WZ 资源：本地 WZ 文件（Character / Mob / Npc / Map / MapHelper / Tile / Obj / Back）
- macOS：原生 `NSWindow.ignoresMouseEvents`（点击穿透）+ `objc_msgSend`

## 编译与启动

dotnet 是**可执行文件**（非目录），路径 `/Volumes/SSD/C#/dotnet`，必须引号调用（`#` 是 shell 特殊字符）。

```bash
# 编译（0 错误才交付）
"/Volumes/SSD/C#/dotnet" build MiniPet/MiniPet.csproj -c Debug

# 启动（GUI 需显示器唤醒，否则 Avalonia.Native 渲染定时器错误 -6661）
caffeinate -dimsu "/Volumes/SSD/C#/dotnet" run --project MiniPet/MiniPet.csproj --no-build
```

## 核心文件

| 文件 | 用途 |
|------|------|
| `MiniPet/Services/MapService.cs` | WZ 地图解析 + 渲染（`RenderViewport` 对齐 R2；精灵缓存；back 视差/平铺；portal；落地） |
| `MiniPet/Views/PetWindow.axaml.cs` | 桌宠主窗口 + 右键菜单（人物/动画/背景模式/气泡/Hermes/设置）+ 模式切换 |
| `MiniPet/Views/BackgroundWindow.cs` | 模式1 静态地图窗口（裁紧透明 padding + 落地对齐 + 点击穿透/交互） |
| `MiniPet/Views/MapViewportWindow.cs` | 模式2 摄像机窗口（800×600，左键拖相机 + 滚轮缩放 + 右键拖窗口 + WriteableBitmap 直拷） |
| `MiniPet/Views/SettingsWindow.axaml.cs` | 设置中心（背景 tab：选图器 / crop / 级联 / 层开关 / 预览） |
| `MiniPet/Services/WzService.cs` | WZ 读取（ExtractPng / GetOrigin / GetIntProperty / GetStringProperty + WithWzLock） |
| `MiniPet/Models/MapData.cs` | MapInfo / MapLayer / MapTile(+Z) / MapObj / MapBack / MapLife / MapPortal / MapFoothold |
| `MiniPet/Models/AppConfig.cs` | PetSettings（MapMode / MapShow* / MapHistory / Crop* / MapId） |

## 架构

```
MiniPet (Avalonia)
├── PetWindow         桌宠主窗口（Paperdoll 拼装角色 + 动画 + 聊天气泡 + 右键菜单）
├── BackgroundWindow  模式1：静态地图（裁紧透明 padding 的整图快照）
├── MapViewportWindow 模式2：摄像机（拖动相机 + 滚轮缩放 + back 视差实时）
├── SettingsWindow    设置中心（选图器 / crop / 层开关）
├── MapService        WZ 解析 + 渲染（RenderViewport 1:1 对齐 WzComparerR2.MapRender）
├── WzService         WZ 读取（锁串行化）
├── AnimService       角色动画
├── PaperdollService  角色拼装（body / head / hair / face / ear）
└── SpriteService     精灵加载/缓存
```

## 地图渲染（核心）

**模式1 静态 / 模式2 摄像机共用 `MapService.RenderViewport`**（算法 1:1 对齐 `WzComparerR2C#/WzComparerR2.MapRender`，MonoGame→SkiaSharp 移植）：
- 模式1 = RenderViewport 第 0 帧（相机=地图中心，zoom=1，viewTimeMs=0）的快照，`CropFull` 裁紧透明 padding。
- 模式2 = RenderViewport 动态（相机随左键拖动，滚轮缩放，viewTimeMs 累加驱动 back 自动滚动）。

**相机变换**：`screen = (world − camCenter) * zoom + (screenW/2, screenH/2)`；clamp 到地图边界（AdjustToWorldRect）。

**渲染顺序**（对齐 MapScene 场景树 + 容器内 (Z0,Z1) 排序）：
1. Back(非 IsFront) — 视差 `floor(camCenter*(100+rx)/100)`（带符号 rx，基于 camCenter），自动滚动 `(rx*5*viewTimeMs/1000)%cx`，tileMode 平铺
2. 8 层 obj+tile — **同层混合按 (Z,Id) 排序**（obj.Z / tile.Z），修正地面盖建筑
3. Life — 定位 (X, Cy)，flip
4. Portal — `game/{PortalTypes[pt]}/{image}`（type7→pv），资源不存在不画（无 editor 回退）
5. Front(IsFront back)
6. Foothold 调试覆盖（默认关）

**精灵缓存**：`_spriteCache`（wzPath→SKBitmap+origin），懒加载，避免每帧 ExtractPng。

## 模式默认分层

- 模式1 静态：MapShowBack / Life / Portal = false（只 obj/tile）
- 模式2 摄像机：MapShowLife / Portal = false（保留 back 视差 + obj/tile）

## 交互

- 摄像机窗口（MapViewportWindow）：左键=拖相机、右键=拖窗口位置、滚轮=缩放(0.25~4 以光标为中心)、拖边框=设大小(CanResize)
- 渲染：DispatcherTimer 30fps 推进 viewTimeMs + KickRender（拖动即时触发，跳过 PNG 用 WriteableBitmap 直拷 BGRA 像素）
- z-order：所有背景窗口 Show 后 `RaisePet()`，保证在桌宠之下

## 参考

- `maplestory-wiki-backend/WzComparerR2C#/`（C# 同 DLL 参考渲染器，MonoGame Windows-only，移植算法到 SkiaSharp）
- `maplestory-wiki-backend/mapRederWeb/frontend/src/index.ts`（PIXI 参考）
- `maplestory-wiki-backend/.../MapRenderHandler.java`（Java 静态参考）

## Claude Code 行为准则

1. **全中文输出** — 所有确认/选项/提示零英文
2. **只给结果不给过程** — 不展示代码 diff/测试/中间步骤
3. **给结果不给理由** — 不做无用推荐/不搞分化
4. **禁止发散思维** — 不准可能/或许/我觉得/有望/预计
5. **禁止编造欺骗** — 数据必须从 API 获取，不估算
6. **禁止期盼** — 不准预测/预估/预期
7. **禁止加码** — 用户说什么做什么，不扩大范围
8. **模型标签诚实** — 实际跑什么模型标什么名
9. **C# 项目必须编译通过才结束** — `"/Volumes/SSD/C#/dotnet" build MiniPet/MiniPet.csproj` 0 错误才交付
10. **涉及代码查询必须用 MCP（CodeGraph）** — 禁止 find/grep/rg/ag 搜源码
11. **if 后面必须加 {}** — 禁止省略花括号，即使只有一行代码


## AppKit/Avalonia UI 调试经验

1. **先确认进程是否崩溃** — `pgrep -f MiniPet` + `ls ~/Library/Logs/DiagnosticReports/ | grep -i minipet` 崩溃日志
2. **dotnet 是可执行文件** — `/Volumes/SSD/C#/dotnet`（引号调用，`#` 是特殊字符）
3. **GUI 需显示器唤醒** — `caffeinate -dimsu`，否则渲染定时器错误 -6661
4. **Auto Layout 异常** — `NSGenericException: no common ancestor` = addSubview 缺失
5. **避免存储属性初始化重量级视图** — `lazy var` 或 `commonInit()`
