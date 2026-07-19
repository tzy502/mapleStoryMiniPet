# MiniPet v1.0 — 测试报告

> 日期：2026-07-19
> 版本：M1 开发阶段
> 测试框架：xUnit

---

## 测试结果

| 指标 | 数值 |
|------|------|
| 总用例 | 10 |
| 通过 | 10 |
| 失败 | 0 |
| 通过率 | 100% |

## 测试用例明细

### 模型测试（ModelTests）

| 用例 | 描述 | 结果 |
|------|------|------|
| SpriteStrip_TotalWidth | 精灵图条总宽 = FrameWidth × FrameCount | ✅ |
| PetConfig_Serialize | JSON 序列化往返 | ✅ |
| ConnectionStatus_Values | 枚举值映射 | ✅ |
| CharacterAppearance_BodyCode_Male | 男性 BodyCode 00002000 | ✅ |
| CharacterAppearance_BodyCode_Female | 女性 BodyCode 00012000 | ✅ |
| FavoriteItem_Defaults | 收藏项类型/ID/名称 | ✅ |
| WzConfig_Defaults | WZ 配置默认值 | ✅ |
| PetSettings_Defaults | 设置默认值 WindowScale=1.0 | ✅ |
| BalloonData_Defaults | 气泡数据默认值 | ✅ |
| UnitTest1.Test1 | xUnit 框架验证 | ✅ |

## 已开发模块

| 层 | 模块 | 文件 |
|----|------|------|
| Models | SpriteSheet | Models/SpriteSheet.cs |
| Models | SpriteStrip | Models/SpriteStrip.cs |
| Models | BalloonData | Models/BalloonData.cs |
| Models | MapData | Models/MapData.cs |
| Models | CharacterAppearance | Models/CharacterAppearance.cs |
| Models | AppConfig | Models/AppConfig.cs |
| Services | ConfigService | Services/ConfigService.cs |
| Services | CacheManager | Services/CacheManager.cs |
| Services | WzService | Services/WzService.cs |
| Services | SpriteService | Services/SpriteService.cs |
| Services | AnimService | Services/AnimService.cs |
| Views | PetWindow | Views/PetWindow.axaml |
| Views | SpriteCanvas | Views/Controls/SpriteCanvas.axaml |

## 待开发

- BalloonService + BalloonControl
- PaperdollService
- MapService + BackgroundControl
- SettingsWindow
- MaterialBrowser
- HermesService（P2）
