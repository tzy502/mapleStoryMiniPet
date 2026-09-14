# MiniPet — V0.2.0 音乐模块开发（F15-F17）

> 提示语版本：claude-prompt-20260827-01
> 日期：2026-08-27
> 分支：feature/0.2.0（音乐模块在此分支开发）
> 对应需求：`docs/ai/requirements-analysis.md` 第 8 章 F15-F17
> 对应设计：`docs/ai/software-design.md` 第 11 章（11.1-11.12，已过 3 agent 审查并修复 4 P0）
> 胶水约定：数据一切从 WZ，禁数据库/硬编码路径；避免 God Class；禁止加码

## 项目信息

- 项目根：`/Users/a502/IdeaProjects/mapleStoryMiniPet`
- 主项目：`MiniPet/MiniPet.csproj`
- .NET SDK（dotnet 是**可执行文件**，必须引号调用）：
  ```bash
  export DOTNET_ROOT="/Volumes/SSD/C#"
  export PATH="$DOTNET_ROOT:$PATH"

  "/Volumes/SSD/C#/dotnet" build MiniPet/MiniPet.csproj -c Debug
  ```
- 编译必须 0 错误才交付

## 架构总览（三个类，避免 God Class）

| 类 | 职责 |
|----|------|
| `MusicCatalogService` | 目录/分类/搜索/场景映射（纯数据，懒加载缓存） |
| `MusicPlayerService` | 播放控制/状态/联动调度 |
| `IMusicPlayer` + 实现 | 音频播放抽象（macOS AVFoundation / Windows NAudio，实现可换） |

## 探针实证的硬事实（照抄，禁止偏离）

1. **WZ 数据源**：SSD 客户端 `/Volumes/SSD/mxd/mxd/Data/Sound/`（Sound.wz + Sound_000~004.wz 分卷，94 个 BgmXX.img 共 1174 首 BGM）
2. **⚠️ WZ 路径必须带 `Sound/` 前缀**：`FindNodeByPath("Bgm00.img")` 返回 null；`FindNodeByPath("Sound/Bgm00.img")` 才命中（顶层节点是 Character/Map/Sound/String 等目录）
3. **⚠️ img 子节点必须读 `img.Node.Nodes`**（Wz_Image.Node 属性）：TryExtract() 后外层 node.Nodes 恒 0
4. **音频格式**：Wz_Sound.SoundType=Mp3，22050Hz 双声道；`Wz_Sound.ExtractSound()` 返回完整 MP3 byte[]；`Ms` 属性 = 时长毫秒
5. **场景联动地图路径**：`Map/Map/Map{mapId首字符}/{mapId}.img/info/bgm`（例：`Map/Map/Map0/000010000.img/info/bgm = "Bgm58/The Beginnig of The Adventure"`）
6. **⚠️ bgm 值无 `.img` 后缀**：`"Bgm58/xxx"` → 归一化为 `Img="Bgm58.img", Track="xxx"` 才能与目录匹配
7. **枚举命名**：播放模式叫 **PlaybackMode**（Sequential/RepeatOne/Random），不叫 PlayMode（与 AnimService 现有 PlayMode 冲突）

## 开发任务（按依赖顺序）

### 任务 1：WzService.ExtractSound（设计 11.7）

`MiniPet/Services/WzService.cs` 追加方法，不动现有逻辑。方法签名与实现照抄设计文档 11.7 节（含 `Sound/` 前缀 + WithWzLock + 四道 null 防线 + try-catch 静默返回 null）。

### 任务 2：模型 + 配置（设计 11.5/11.6）

- `MiniPet/Models/MusicData.cs`：MusicTrack（record，含 Display/Key 属性）/ MusicTrackRef / PlaybackState / **PlaybackMode**
- `MiniPet/Models/MusicConfig.cs`：Volume=70 / PlaybackMode / LastTrackKey / SceneLinkEnabled=false / PlayHistory
- `MiniPet/Models/AppConfig.cs`：PetSettings 加 `Music` 字段

### 任务 3：MusicCatalogService（设计 11.2）

`MiniPet/Services/MusicCatalogService.cs`：GetCatalogAsync（遍历 Sound/Bgm*.img → TryExtract → img.Node.Nodes → MusicTrack 列表，懒加载缓存）/ Search / GetCategories / InvalidateCatalog / GetSceneTrackAsync（查地图 bgm 字段，归一化，按需查不预遍历）

### 任务 4：IMusicPlayer + 实现（设计 11.4）

- `MiniPet/Services/IMusicPlayer.cs` 接口（Play(byte[])/Pause/Resume/Stop/Seek/Volume/Position/Duration/PlaybackEnded/PlaybackError）
- 实现：**先做最小可播 demo 验证选型**（一首 MP3 → 播 → 停），macOS 首选 AVFoundation（objc_msgSend，项目 Utils/MacNative.cs 有先例），确认可用再集成
- Play(byte[]) 实现必须包可定位 MemoryStream；Position/Duration 只在 Stopped 允许 null

### 任务 5：MusicPlayerService（设计 11.3）

`MiniPet/Services/MusicPlayerService.cs`：PlayAsync/PauseAsync/ResumeAsync/StopAsync/NextAsync/PrevAsync/SetVolume/SetPlayMode + CurrentChanged/StateChanged/VolumeChanged 事件 + OnMapChanged（场景联动调度）+ RestoreLastTrackAsync（恢复上次播放，失败静默）

### 任务 6：设置中心音乐大 tab（设计 11.8）

- `MiniPet/Views/SettingsWindow.Music.cs`：partial class（跟 Agent/Background/Balloon/Paperdoll 同模式）
- **独立大 tab**：设置中心顶层 TabStrip 第 3 个 tab「🎵 音乐」（不是宠物设置子 tab）
- 布局：顶栏（刷新目录/搜索/联动开关）+ 左分类列表 + 右曲目列表 + 底部播放控制区（进度条/⏮⏯⏹⏭/模式按钮/音量滑块）
- 首次加载约 2.2s 必须有 loading 提示；WZ 未加载显示灰色占位「WZ 未加载，无法读取音乐」（控件禁用，不崩溃不弹错）

### 任务 7：右键菜单 + 联动挂钩（设计 11.9/11.10）

- `MiniPet/Views/PetWindow.Menu.cs`：菜单加「🎵 音乐」→ 打开设置中心切到音乐 tab；播放中菜单 header 动态显示曲名
- `MiniPet/Views/PetWindow.Mode.cs`：**ApplyMapSelection 完成后**调用 `MusicPlayerService.OnMapChanged(mapId)`（注意：不是 SetMap，真实挂钩点在 ApplyMapSelection）
- `MiniPet/Views/PetWindow.axaml.cs`：订阅 CurrentChanged → 气泡提示「🎵 曲名」+ DI 注入

## 验收（黑盒，逐条过）

1. 编译 0 错误
2. 设置 → 音乐 tab → 分类/曲目列表加载（Bgm00 有 8 首）
3. 双击 SleepyWood → 听到音乐 + 进度条走动 + 桌宠气泡「🎵 SleepyWood」
4. 暂停/继续/停止/上一首/下一首/音量/模式切换全部生效
5. 搜索「Floral」→ 过滤出 FloralLife
6. 开场景联动 → 切地图 100000000（Henesys）→ 自动换歌 + 气泡提示；无 BGM 地图保持当前
7. WZ 未加载时打开音乐 tab → 灰色占位不崩溃
8. 播放后退出重进 → 音量/模式/上次曲目恢复

## 任务边界（禁止加码）

- 设计第 11 章就是全部范围，**不做**播放列表管理/均衡器/歌词/频谱等 11 章外功能
- P2 优化项（PlayHistory 截断位置、字段名统一）本轮不动
- 详细设计（类结构/接口签名/UI 布局/代码示例）以 `docs/ai/software-design.md` 第 11 章为准
