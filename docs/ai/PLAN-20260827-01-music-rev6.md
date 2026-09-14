# Plan: V0.2.0 音乐模块（F15-F17）
_Locked via grill-with-docs — by Claude + 用户. Terms per CONTEXT.md. Rev 6（折叠 Round 5 全部 5 条意见；达 MAX_ROUNDS 上限后未再经 Codex 复审）._

## Goal

桌宠获得从本地 WZ 客户端播放游戏 BGM 的能力（迷你点歌机）：设置中心第 4 个顶层 tab「🎵 音乐」提供分类浏览/搜索/播放控制；桌宠右键菜单快速进入；切换地图背景时可选自动改播该地图 BGM（场景联动，默认关）。数据一切从 WZ（SSD `/Volumes/SSD/mxd/mxd/Data/Sound/`，94 个 BgmXX.img 共 1174 首），禁数据库/硬编码路径。

## Approach

按依赖顺序 9 步，编译命令 `"/Volumes/SSD/C#/dotnet" build MiniPet/MiniPet.csproj -c Debug` 与 `dotnet test` 全程保持 0 错误：

1. **NuGet 引入 ManagedBass 3.1.1**（与 R2 同版本）。原生二进制按 OS 平台分发：macOS 用 osx-arm64 `libbass.dylib`、Windows 用 win-x64 `bass.dll`——csproj 用 **`$([MSBuild]::IsOSPlatform('OSX'))` 类平台条件**拷贝（项目无 RuntimeIdentifier，纯 RID 条件在普通 Debug 构建下永不命中），编译后断言输出目录存在对应动态库（原生加载参考 `WzLibRuntimeLoader` 先例）；libbass.dylib 与 bass.dll 的来源 URL、版本号、期望 SHA-256 在集成前记录进 ADR-0007 附录，csproj 校验目标对实际落盘二进制做哈希比对，不匹配即编译失败（闭源原生码防静默替换）。R2 自带的仅是 Windows PE 二进制，不能照搬给 mac。
2. **WzService.ExtractSound**（§11.7 照抄）：签名 `(string imgName, string trackName) → byte[]?`，内部 `_wzLoaded/_wzs` 判空 → `lock (_wzLock)` → `FindNodeByPath("Sound/" + imgName)`（⚠️ 必须 Sound/ 前缀）→ TryExtract → 读 **img.Node.Nodes**（⚠️ 外层恒 0）→ trackNode → `Wz_Sound.ExtractSound()`；四道 null 防线 + try-catch 打 stderr 返回 null。
3. **模型**：`Models/MusicData.cs`（MusicTrack record 含 Display/Key、MusicTrackRef、PlaybackState、PlaybackMode）；`Models/MusicConfig.cs`（Volume=70/PlaybackMode/LastTrackKey/SceneLinkEnabled=false/PlayHistory）；`AppConfig.cs` PetSettings 加 `Music` 字段。
4. **MusicCatalogService**（§11.2）：GetCatalogAsync 遍历 Sound/Bgm*.img 懒建缓存（首次约 2.2s）/ Search（OrdinalIgnoreCase 内存过滤）/ GetCategories / InvalidateCatalog / GetSceneTrackAsync（查 `Map/Map/Map{mapId首字符}/{mapId}.img/info/bgm`，bgm 值归一化 `"Bgm58/xxx"` → Img="Bgm58.img"，按需查不预遍历）。**并发纪律**：目录枚举是同步 WzLib 锁内工作，必须整体包在 `Task.Run` 的**单飞任务**里执行（`_catalogTask` 复用 + 版本号 guard）。失效挂钩：`WzService.LoadWz` 改为**先在局部变量完整构建新结构（含 Mob/Packs 追加加载），再于单个 `_wzLock` 临界区一次性发布 `_wzs` 与 `_wzLoaded=true`**——现状 109 行先行置 true 后仍继续加载至约 161 行，读者会在半成品结构上进入，目录任务可能正遍历旧结构被拦腰替换，音乐读者放大了这一既有缺陷，故做最小修补；同时暴露**重载代际事件**（WzReloaded/代际号自增），由 MusicCatalogService 订阅（WzService 不能反向依赖目录服务，避免 DI 成环）；**每次重载尝试开始即 bump 代际并失效缓存**（finally 兜底，失败收场的重载同样清除在途结果，不留活引用）；await 侧拿到结果后再比对当前代际，不匹配则按失效期错误处理而非返回脏数据。
5. **IMusicPlayer + BassMusicPlayer 单实现**（修订 §11.4，见 ADR-0007）：**接口在 §11.4 基础上扩展预备流能力**——`IPreparedStream Prepare(byte[])` 返回可校验的一次性预备句柄（`Commit()` 提升 / `Dispose()` 丢弃），原同步 `Play(byte[])` 降级为 Prepare+Commit 的便利方法；两阶段交换协议经此接口表达，不再有「接口表达不了预检」的矛盾；实现为长生命周期单例服务（≠R2 的一次性 Music 对象）。**集成闸门 = 扩展版 Darwin arm64 最小可播 demo**，必须逐一验证后才允许写正式类：Bass.Init + 原生库加载、pinned byte[] 建流播放停止、自然播完 END SYNC 回调触发、Position/Duration/Seek 取值、旧流释放后新流替换、BASS 调用失败码的暴露方式。**每次切歌 = 两阶段交换协议**（先建新后毁旧）：①准备阶段——pin 新 byte[] → 创建**第二个临时流**并校验成功（旧流此刻仍在播，完全不受影响）→②提交段（SemaphoreSlim 内原子完成）——移除旧流 END SYNC → stop/free 旧流 → Unpin 旧 pin → 把新流提升为活动流 → 应用音量 → 安装带**代际编号**的新 END SYNC → 起播。准备阶段失败时只丢弃临时流与临时 pin，活动流分毫未动；**提交段任一 BASS 调用失败 = 终态处理**：立即落 Stopped + PlaybackError 上抛，新旧流与 pin 一律确定性释放不留半开资源，决策函数配单测。所有过渡由单个 `SemaphoreSlim` 串行化；代际不匹配的迟到播完回调直接丢弃。**失败契约**：用户触发的提取/建流失败 → 保持原播放不变（原本空闲则落回 Stopped），`PlaybackError` 携带错误串沿服务层事件上抛，音乐 tab 状态栏显示、stderr 记录，绝不崩溃不弹模态。PlaybackEnded 从 BASS 回调线程 raise；服务层收到后只投递一个**轻量后台命令**（专用工作队列，入队即捕获操作代际，执行前比对——期间发生过任何用户播放操作则丢弃该命令防双跳/复活），自动 Next 的 WZ 提取与流切换编排运行于 BASS 线程与 UI 线程之外的第三线程，仅最终状态通知 marshal 到 UI——既不冻结宠物/设置窗口，也不阻塞音频回调。**自然播完后的失败语义**：此时旧流已终结，「保持当前播放」不再可行，规定为落 Stopped + PlaybackError 上抛 + tab 状态栏提示；RepeatOne 重备失败同样 Stopped 收场，无重试循环。
6. **MusicPlayerService**（§11.3）：PlayAsync/Pause/Resume/Stop/**SeekAsync(TimeSpan 进度条点击，clamp 至 [0,Duration)，播/停两态均可)**/Next/Prev/SetVolume/SetPlayMode + CurrentChanged/StateChanged/VolumeChanged + OnMapChanged(mapId) + RestoreLastTrackAsync。队列在 Play 时**深拷贝为不可变快照**并校验目标曲目在其中；Next 按 PlaybackMode 计算 index 越界回 0；PlaybackEnded → 自动 Next。**场景联动协议**：OnMapChanged 以**带日志的后台任务**方式启动（UI 同步代码不等待、异常必捕获记录），内部持地图代际 guard 防过期地图事件覆盖新选择，另持**服务操作代际**：预检期间任何用户播放操作都会 bump 操作代际；**提交前完整重验四元组快照**——地图代际、操作代际、WZ 代际、SceneLinkEnabled 开关值（预检后用户关掉联动或发生 WZ 重载同样放弃提交），任一不一致丢弃预备流保持现状；先完成 解析 bgm → ExtractSound → 利用两阶段交换的准备阶段打开并校验新流 三步预检，**三步全部成功才提交**队列替换与状态变更；任一步失败保持当前播放原样不动（对齐 CONTEXT.md「无可用 BGM 保持当前不断曲」）；若场景曲目 Key 与当前正在播的 Key 相同则完全 no-op（同曲跨图不重启）。恢复语义：启动只读配置做音量/模式还原 + 记住 LastTrackKey；**不主动构建索引**，精确 Key 查找推迟到目录就绪时（打开音乐 tab）执行，UI 选中态静默设置、**不触发 CurrentChanged**（不出启动气泡）。
7. **UI**：`Views/SettingsWindow.Music.cs` partial — 顶栏刷新/搜索/联动开关 + 左分类右曲目双列表 + 底部控制区（进度条/⏮⏯⏹⏭/模式循环按钮/音量滑块）。**接线事实**（与设计 §11.8 不同）：SettingsWindow 不是从 DI 容器解析的——`PetWindow.axaml.cs:1205` 手动 `new SettingsWindow(...)` 且构造函数已有依赖列表，因此音乐服务的传递链 = App 容器注册三服务 → PetWindow 构造获取后透传给 SettingsWindow 构造函数 → BuildBody/_mainPanes 追加第 4 面板；tabs 数组在 `axaml.cs:311`（实际现有 3 个顶层 tab，追加「🎵 音乐」为第 4 个）+ 新增 `OpenMusicTab()`。懒加载 tab 带 loading 提示；WZ 未加载灰占位「WZ 未加载，无法读取音乐」。**进度条机制**：设置中心窗口可见 **且** 音乐 tab 处于选中 **且** 存在活动流，三条件同时满足才跑 DispatcherTimer 轮询 Position/Duration；任一条件丢失（含窗口 Hide——该窗口隐藏不关闭）即停表，恢复可见即重启。PetWindow 四处挂钩：Menu.cs 右键菜单项 + 菜单头部动态曲名（订阅 CurrentChanged/StateChanged 于 UI 线程刷新「🎵 当前曲名」，§11.9 原始要求，Rev 4 遗漏此轮补回）+ Mode.cs ApplyMapSelection 成功路径尾部调 OnMapChanged(`sel.Id`)（LoadMap 失败早退不触发联动）+ axaml.cs 订阅 CurrentChanged 经 Dispatcher.UIThread.Post 弹气泡「🎵 曲名」。
8. **持久化策略**：音乐字段写入走新增的 **`ConfigService.Update(Action<PetSettings>)`**——在其现有序列化锁内完成「读→改 Music 子对象→写」整个事务，且该短临界段最终**派发到 UI 线程执行**：设置窗口缓存 _settings 引用后整对象回存（axaml.cs:581 等）是最高频的覆盖源，与其同线程串行化才能闭合「缓存读↔整存」区间内的插入提交。PlayHistory 每次切曲都写、写频率大幅上升，会显著放大与两个已证实的跨线程整对象写者的碰撞概率（PetWindow.axaml.cs:1054 ApplyPaperdoll 于 Task.Run 内整存整取；PetWindow.Mode.cs:185 SwitchToCameraMode 存 async gap 前的旧快照），因此这三个碰撞集写入点一并机械迁移到 Update helper 或 OK 处理器内重读（PetWindow.axaml.cs:1054 ApplyPaperdoll、PetWindow.Mode.cs:185 SwitchToCameraMode、MaterialBrowserWindow.Logic.cs:877/949 SaveDraftOutfit 对话框前取引用对话框后整存）（各约三行改动，属新写入压力引致的必要碰撞修复，非全局重构）；LastTrackKey 在每次开始播放新曲时**立即**落盘（不怕退出丢）；Volume 走 800ms 防抖尽力而为（接受极端快退丢最后一次滑动的窗口，已在风险中声明）；PlayHistory 写入时即做 move-to-front 去重 + 截断 20（与模型字段注释的 schema 契约一致，「P2 截断位置」待定项指的是管线位置，不是不做）。Windows 强退 Environment.Exit 前不引入新的 flush 机器：关键状态靠上述即时落盘保障，BASS 流随进程回收。
9. **测试与验证**：MiniPet.Tests 用绝对路径 `"/Volumes/SSD/C#/dotnet" test` 执行且全绿（dotnet 不在 PATH，§11.12 要求）；新增单元测试覆盖 bgm 归一化、MusicTrack.Key/Display（含 Ms=0 分支）、Next 三种模式数学含队列尾回卷、自然播完后备选失败→Stopped 决策函数、MusicConfig JSON 往返、场景联动预检失败不换歌的状态不变性；音频硬件路径不入自动测试（黑盒验收覆盖）。随后黑盒验收 8 条（原提示语清单）逐条过。

## Key decisions & tradeoffs

- **[ADR-0007] 音频后端 = ManagedBass 单实现双平台**，替代 §11.4 双实现。代价：闭源免费库授权约束、新增原生分发复杂度；收益：零 objc 桥接、单一代码路径、Seek/Position/Duration 内建。
- **恢复语义 = 只预选不出声**（修订 §11.3 字面）：常驻桌宠开机突然出声违反直觉；选中态静默设置避免假气泡。
- **场景曲目元数据缺口 = Ms 填 0**：Display 对 Ms≤0 省略时长；真实时长用 IMusicPlayer.Duration；不给 WzService 加第二个方法。
- **联动队列 = 单元素替换，但先预检后提交**：不可用曲目绝不打断当前播放；同曲跨图不重启。
- **Sequential 队列尾回卷到队首**（持续循环播放）为本版本显式口径，是对 CONTEXT.md「队列」条目耗尽行为的 v0.2.0 override，非遗漏。
- **并发统一口径**：服务层 SemaphoreSlim 串行 + 代际标签丢弃过期回调；目录层单飞任务 + 版本号失效；UI 层事件一律 UIThread.Post。
- **音乐 tab 为第 4 个顶层 tab**：纠正设计 §11.8 过时前提（代码实况 3 个 tab）。

## Risks / open questions

- BASS osx-arm64 原生库能否被 .NET 9 正确加载仍是第一未知，步骤 5 扩展 demo 是硬闸门；失败即停下报告选型危机（回退 AVFoundation 需另议）。
- Volume 防抖存在极小丢失窗口（快改快退 <800ms），属已声明的可接受损失。
- Service 层不保证事件线程，订阅方负责 UI 派发（XML 注释载明）。

## Out of scope

- §11 章以外的一切功能：播放列表管理、均衡器、歌词、频谱等禁止加码。
- P2 待定项本身（截断管线的最终形态、字段名统一）。
- Windows 实机听感验收（同源代码路径 + 编译通过即为交付标准，见 ADR-0007）。
- ConfigService 全局读写模式的改造（沿用现状，仅音乐子树加锁原子化）。
- F18+ 的 UI 维护类需求。

### V0.2.1 修订（2026-08-27 用户反馈：打开音乐明显卡死）
反向索引推倒重写：①打开音乐 tab 不再触发任何扫描（原全量预扫独占 WZ 锁 ~30s 是卡死根因）；
②改按需懒查——切分类/切歌时只 Kick 对应 BgmXX 节点，后台逐图小段拿锁（桌宠 WZ 操作随时插队）；
③结果落盘 `~/.minipet/music-track-maps.json`（Windows 走 exe 目录便携口径），第二次起秒出；
④每曲目仅收录 5 条**有名字**的关联地图（无名图视为隐藏级不占名额），超出以「…下略」计数展示；
⑤UI 去缓存字典直读服务切片，ReverseIndexChanged 事件驱动重建——修复「切 BGM 不刷新」。
