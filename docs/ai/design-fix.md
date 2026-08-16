# 设计文档验证修复清单

> 来源：架构评审 agent（26 个问题）
> 等待：可开发性 + 可测试性 验证结果

---

## 高优先级修复（阻塞开发）

### 1. BalloonData 模型缺失
**影响**：BalloonService.LoadBalloon 返回 BalloonData，但第 3 节完全没定义这个类型
**修复**：补充 BalloonData 模型：
```
BalloonData
├── Tiles: BalloonTiles (nw, n, ne, w, c, e, sw, s, arrow, se, head)
├── Clr: int (文字颜色 ARGB)
├── Name: string
└── Id: int
```

### 2. TrayService 零接口
**影响**：F11 的托盘功能没有对应的服务方法
**修复**：补充 TrayService 方法：
```
CreateTrayIcon() → void
ShowPet() → void
HidePet() → void
TogglePet() → void
CenterPet() → void
Dispose() → void
```

### 3. 缓存管理环节断点
**影响**：数据流 6.2 中 WzService → 磁盘缓存 → SpriteService，缓存管理职责不明确
**修复**：新增 CacheManager 模块，位于 Data Layer：
```
CacheManager
├── IsSpriteCached(mobId, action) → bool
├── SaveSpriteStrip(mobId, action, strip, config) → void
├── LoadSpriteStrip(mobId, action) → (spriteData, configData)?
├── GetCacheDir() → string
├── ClearOldCache(daysRetain) → void
```

### 4. 纸娃娃部位→WZ 路径映射逻辑缺失
**影响**：PaperdollService 合成时需要查 WZ 路径，但这个映射逻辑没有落脚点
**修复**：WzService 新增方法：
```
GetCharacterPartWzPath(type, id, action, frame) → string
```

### 5. Hermes 相关数据模型缺失
**影响**：P2 的 Hermes 集成没有对应的数据模型
**修复**：补充 Hermes 模型（留空，P2 阶段再细化）：
```
HermesConfig
├── Enabled: bool
├── WebSocketUrl: string
├── SessionId: string
└── AutoReconnect: bool

ConnectionStatus : enum (Connected, Disconnected, Reconnecting, Error)

StatusAnimationMap
├── Status: HermesStatus
└── Animation: string
```

## 中优先级修复

### 6. EventBus 未在架构分层中体现
**修复**：在 1.1 架构分层图中补充 EventBus，归属 App Layer

### 7. CacheManager 未在架构分层图中体现
**修复**：在 1.1 架构分层图中补充 CacheManager，归属 Data Layer

### 8. 模块依赖图不完整
**修复**：补充以下隐式依赖：
- PaperdollService → WzService
- MapService → WzService
- BalloonService → WzService

### 9. 收藏/最近使用数据模型不够灵活
**修复**：PetSettings 中 `Favorites` 从 `List<string>` 改为 `List<FavoriteItem>`：
```
FavoriteItem
├── Type: string (mob/npc/equip/hair/face/mount/chair)
├── Id: string
├── Name: string
└── AddedAt: datetime
```

### 10. 托盘原型缺失
**修复**：不新增原型文件，托盘为系统原生控件，仅需定义图标和菜单项

### 11. BalloonService 缺少显示时长控制
**修复**：BalloonService 新增方法：
```
ShowWithAnimation(text, balloonData) → void
Hide() → void
SetDisplayDuration(ms) → void
```

### 12. ConfigService 缺少变更通知
**修复**：ConfigService 新增事件：
```
event OnSettingsChanged
```

## 低优先级修复

| 问题 | 修复 |
|------|------|
| PetWindow 依赖图缺 BalloonControl/ContextMenu | 补充依赖线 |
| SpriteService ↔ WzService 循环依赖风险 | 明确 WzService 独立完成拼接 |
| AnimService ↔ SpriteService 依赖方向 | 明确 AnimService 通过 DI 获取 SpriteService |
| TrayService 缺视图组件 | 明确用 Avalonia TrayIcon 控件实现 |